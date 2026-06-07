namespace PSXEmu;

/// <summary>
/// PSX digital pad controller emulation.
/// Communicates with the CPU via SIO0 (the pad/memory-card serial interface).
/// The PSX uses a simple SPI-like protocol: the CPU clocks out bytes and
/// the controller clocks back its response.
///
/// Register map (offsets from 0x1F801040):
///   0x00  JOY_DATA  (R/W)  : TX/RX data FIFO
///   0x04  JOY_STAT  (R)    : Status register (32-bit)
///   0x08  JOY_MODE  (R/W)  : Mode register (16-bit)
///   0x0A  JOY_CTRL  (R/W)  : Control register (16-bit)
///   0x0E  JOY_BAUD  (R/W)  : Baud rate (16-bit)
///
/// Transfer timing is critical: the BIOS clears the interrupt flags shortly
/// after writing to JOY_DATA, then waits for a NEW interrupt from the device
/// response. If the transfer completes immediately (synchronously), the
/// interrupt is raised and then cleared before the BIOS checks, so the BIOS
/// thinks no controller is connected. To match real hardware, the response
/// is delayed by (JOY_BAUD * 8) CPU cycles, and the ACK pulse arrives ~450
/// cycles after that.
/// </summary>
public class PsxController
{
	private readonly Psx _psx;

	// --- STAT register bits ---
	private const uint StatTxRdy1 = 1u << 0;  // TX ready flag 1
	private const uint StatRxFifo = 1u << 1;  // RX FIFO not empty
	private const uint StatTxRdy2 = 1u << 2;  // TX ready flag 2
	private const uint StatAckInput = 1u << 7;  // ACK input level
	private const uint StatIntr = 1u << 9;  // Interrupt request (IRQ7 pending)

	// --- CTRL register bits ---
	private const ushort CtrlTxEn = 1 << 0;   // TX enable
	private const ushort CtrlSelect = 1 << 1;   // Device select (/CS assert)
	private const ushort CtrlRxEn = 1 << 2;   // RX enable
	private const ushort CtrlAck = 1 << 4;   // Acknowledge (write 1 to clear INTR)
	private const ushort CtrlReset = 1 << 6;   // Soft reset
	private const ushort CtrlTxIntEn = 1 << 10;  // TX interrupt enable
	private const ushort CtrlRxIntEn = 1 << 11;  // RX interrupt enable
	private const ushort CtrlAckIntEn = 1 << 12;  // ACK interrupt enable
	private const ushort CtrlSlot = 1 << 13;  // 0=port 1, 1=port 2

	// ACK delay in CPU cycles (~450 ticks for controller, ~170 for memory card)
	private const int AckDelayCycles = 450;
	private const int AckDelayMemCard = 170;

	private ushort _mode;
	private ushort _ctrl;
	private ushort _baud;

	// Transfer state machine
	private int _txPhase;    // which byte of response we're at
	// Cached `JOY_CTRL.SELECT` bit (CPU's chip-select state). This field
	// is ONLY mutated by `WriteCtrl`, devices ending their session must NOT
	// touch it.
	// Previously this bool was conflated with the device-session state and
	// got cleared by the controller / memcard on transfer end, breaking the
	// common "select once, do controller poll + memcard poll, then deselect"
	// pattern, every save-prompt FMV pipeline relies on it.
	private bool _selected;   // /CS is asserted (mirror of CTRL.SELECT)
	private uint _rxData;     // last received byte from controller
	private bool _rxValid;    // RX FIFO has data
	private bool _ackPending; // ACK pulse pending (STAT bit 7)
	private bool _intr;       // Interrupt flag (STAT bit 9)

	// Deferred transfer: byte written to JOY_DATA is held until the transfer
	// delay elapses, then the exchange happens and ACK/IRQ fires.
	private bool _transferPending;
	private byte _txPendingByte;
	private int _transferCountdown; // cycles until transfer completes
	private int _ackCountdown;      // cycles until ACK fires after transfer
	private bool _ackScheduled;      // ACK timer is running

	// SIO active device, which device currently owns the session. Set by
	// the phase-0 selection byte (0x01 -> Controller, 0x81 -> MemoryCard) and
	// cleared when the active device stops ACKing OR when CPU clears
	// CTRL.SELECT. Independent of `_selected` so the CPU can leave SELECT
	// asserted across multiple device sessions in one polling pass.
	private enum SioActiveDevice { None, Controller, MemoryCard }
	private SioActiveDevice _activeDevice = SioActiveDevice.None;

	// Pad buttons: active-low bitmask (0=pressed)
	public ushort ButtonMask { get; set; } = 0xFFFF; // all released

	public PsxMemoryCard MemCard { get; }

	public PsxController(Psx psx)
	{
		_psx = psx;
		MemCard = new PsxMemoryCard();

		// Single controller event drives transfer-complete
		// and ACK-arrival countdowns. Both fire at known cycle deadlines
		// (baud-rate * 8 for transfer, device-specific for ACK). Replaces
		// the LegacyTick path's 256-cycle per-tick decrement.
		_event = new TimingEvent(
			"Controller", int.MaxValue, int.MaxValue,
			(param, ticks, _) => ((PsxController)param).OnControllerEvent(ticks),
			this);
	}

	private TimingEvent _event;

	// ---- Save-state ---- (ButtonMask reflects host input; saved for completeness
	// but refreshed by the next input poll anyway.)
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		w.U16(_mode); w.U16(_ctrl); w.U16(_baud);
		w.S32(_txPhase);
		w.Bool(_selected); w.U32(_rxData);
		w.Bool(_rxValid); w.Bool(_ackPending); w.Bool(_intr);
		w.Bool(_transferPending); w.U8(_txPendingByte);
		w.S32(_transferCountdown); w.S32(_ackCountdown); w.Bool(_ackScheduled);
		w.S32((int)_activeDevice);
		w.U16(ButtonMask);
		_event.SaveState(w, g);
		MemCard.SaveState(w);
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		_mode = r.U16(); _ctrl = r.U16(); _baud = r.U16();
		_txPhase = r.S32();
		_selected = r.Bool(); _rxData = r.U32();
		_rxValid = r.Bool(); _ackPending = r.Bool(); _intr = r.Bool();
		_transferPending = r.Bool(); _txPendingByte = r.U8();
		_transferCountdown = r.S32(); _ackCountdown = r.S32(); _ackScheduled = r.Bool();
		_activeDevice = (SioActiveDevice)r.S32();
		ButtonMask = r.U16();
		_event.LoadState(r, g);
		MemCard.LoadState(r);
	}

	private void OnControllerEvent(int ticks)
	{
		if (ticks > 0) TickInternal(ticks);
		RescheduleEvent();
	}

	private void RescheduleEvent()
	{
		int min = CyclesUntilNextEvent();
		if (min == int.MaxValue)
		{
			_event.Deactivate();
			return;
		}
		if (min < 1) min = 1;
		// ScheduleIfEarlier (not Schedule), same reasoning as PsxCdrom:
		// WriteWord calls this at end-of-switch regardless of whether the
		// write actually changed a countdown. Schedule(min) would push the
		// deadline forward on every unrelated MMIO touch and the event would
		// never reach its fire time.
		_event.ScheduleIfEarlier(min);
	}

	public void Reset()
	{
		_mode = 0;
		_ctrl = 0;
		_baud = 0x0088;
		_txPhase = 0;
		_selected = false;
		_rxData = 0xFF;
		_rxValid = false;
		_ackPending = false;
		_intr = false;
		_transferPending = false;
		_ackScheduled = false;
		_activeDevice = SioActiveDevice.None;
		ButtonMask = 0xFFFF;
		MemCard.Reset();
		// Nothing pending after reset -> event stays deactivated.
		// Subsequent MMIO writes will arm it via RescheduleEvent.
		RescheduleEvent();
	}

	/// <summary>
	/// Drive the transfer-complete and ACK countdowns by <paramref name="cpuCycles"/>.
	/// Called from <see cref="OnControllerEvent"/> with the elapsed cycles since
	/// the last event fire.
	///
	/// Subtle accounting: the ACK countdown gets ARMED inside DoTransfer
	/// (when the transfer fires), so it didn't actually exist for the FULL
	/// cpuCycles, only for the OVERSHOOT cycles past the transfer deadline.
	/// Apply the overshoot rather than the full cpuCycles to the freshly-
	/// armed ACK countdown, otherwise the ACK collapses onto the same
	/// instant as transfer-complete and games that watch both edges
	/// separately (RE2 input polling) break.
	/// </summary>
	private void TickInternal(int cpuCycles)
	{
		bool transferFiredThisTick = false;
		int overshootAfterTransfer = 0;

		// Transfer delay (byte exchange)
		if (_transferPending)
		{
			_transferCountdown -= cpuCycles;
			if (_transferCountdown <= 0)
			{
				// How many cycles ran AFTER the transfer deadline within this tick.
				overshootAfterTransfer = -_transferCountdown;
				_transferPending = false;
				DoTransfer(_txPendingByte);  // arms _ackScheduled / _ackCountdown
				transferFiredThisTick = true;
			}
		}

		// ACK delay (fires after byte exchange). Only apply the
		// cycles that ELAPSED after DoTransfer ran (= overshoot if transfer
		// fired this tick; otherwise the full cpuCycles).
		if (_ackScheduled)
		{
			int ackCharge = transferFiredThisTick ? overshootAfterTransfer : cpuCycles;
			_ackCountdown -= ackCharge;
			if (_ackCountdown <= 0)
			{
				_ackScheduled = false;
				_ackPending = true;
				if ((_ctrl & CtrlAckIntEn) != 0)
					TriggerIrq();
			}
		}
	}

	/// <summary>
	/// Smallest in-flight countdown across the transfer and ACK timers.
	/// Used by <see cref="RescheduleEvent"/> to schedule the next event
	/// fire at the earliest deadline. Floor each at 1 cycle so blocked
	/// retries don't divide-by-zero in the scheduler.
	/// </summary>
	private int CyclesUntilNextEvent()
	{
		int min = int.MaxValue;
		if (_transferPending) min = Math.Min(min, Math.Max(_transferCountdown, 1));
		if (_ackScheduled) min = Math.Min(min, Math.Max(_ackCountdown, 1));
		return min;
	}

	public uint ReadWord(uint offset)
	{
		return offset switch
		{
			0x00 => ReadData(),
			0x04 => ReadStat(),
			0x08 => _mode,
			0x0A => _ctrl,
			0x0E => _baud,
			_ => 0,
		};
	}

	public void WriteWord(uint offset, uint value)
	{
		switch (offset)
		{
			case 0x00: BeginTransfer((byte)value); break;
			case 0x08: _mode = (ushort)value; break;
			case 0x0A: WriteCtrl((ushort)value); break;
			case 0x0E: _baud = (ushort)value; break;
		}
		// WriteCtrl falling-edge SELECT and RESET bit may have cleared _transferPending/_ackScheduled.
		// Recompute event arm.
		RescheduleEvent();
	}

	public ushort ReadHalf(uint offset)
	{
		return offset switch
		{
			0x04 => (ushort)ReadStat(),
			0x06 => (ushort)(ReadStat() >> 16),
			0x08 => _mode,
			0x0A => _ctrl,
			0x0E => _baud,
			_ => (ushort)ReadWord(offset),
		};
	}

	public void WriteHalf(uint offset, ushort value)
	{
		WriteWord(offset, value);
	}

	/// <summary>
	/// Byte write dispatch. Routes each byte to the correct register without
	/// reading anything that has side effects, in particular, JOY_DATA reads
	/// pop the RX FIFO, so a bus-level RMW path (ReadWord -> modify byte ->
	/// WriteWord) would silently drain pad data on every SB. This method
	/// merges the byte against the *cached* register fields (`_mode`,
	/// `_ctrl`, `_baud`) which are pure state, no read side effects.
	/// SB to STAT (0x04-0x07) is ignored since STAT is read-only on real HW.
	/// SB to JOY_DATA non-zero offsets (0x01-0x03) is ignored since JOY_DATA
	/// is byte-wide (offset 0), the upper bytes simply don't exist.
	/// </summary>
	public void WriteByte(uint offset, byte value)
	{
		switch (offset)
		{
			// JOY_DATA: byte-wide write that initiates a transfer. Pass the
			// byte straight to BeginTransfer; no RMW needed.
			case 0x00: BeginTransfer(value); break;

			// 0x01-0x03: JOY_DATA upper bytes don't exist. Ignore.
			// 0x04-0x07: JOY_STAT is read-only. Ignore.

			// JOY_MODE (16-bit): merge byte into cached `_mode`, no side effect.
			// `u` suffix on the mask literals keeps everything as uint so the
			// compiler doesn't promote to long on `int | uint` and warn about
			// the sign-extension (CS0675).
			case 0x08: _mode = (ushort)((_mode & 0xFF00u) | value); break;
			case 0x09: _mode = (ushort)((_mode & 0x00FFu) | ((uint)value << 8)); break;

			// JOY_CTRL (16-bit): go through WriteCtrl because it has side
			// effects (ACK clear, reset, etc.), but RMW against the cached
			// `_ctrl` field, NOT a fresh read (which is harmless here but
			// keeps the pattern uniform).
			case 0x0A: WriteCtrl((ushort)((_ctrl & 0xFF00u) | value)); break;
			case 0x0B: WriteCtrl((ushort)((_ctrl & 0x00FFu) | ((uint)value << 8))); break;

			// JOY_BAUD (16-bit): merge byte into cached `_baud`.
			case 0x0E: _baud = (ushort)((_baud & 0xFF00u) | value); break;
			case 0x0F: _baud = (ushort)((_baud & 0x00FFu) | ((uint)value << 8)); break;
		}
	}

	private uint ReadData()
	{
		uint val = _rxData;
		_rxValid = false;
		return val;
	}

	private uint ReadStat()
	{
		// NOTE: previous versions called `RunPendingTransferEvents()` here to
		// "early-fire" any pending transfer/ACK events whenever the game polled
		// JOY_STAT. That was wrong on two counts:
		//   1. It fired events with NO countdown gate, the very first poll
		//      after a JOY_TX_DATA write would immediately complete the
		//      transfer AND fire the ACK, collapsing ~620 cycles of real SIO
		//      timing into 0. Tight `sw + lw + lw` poll loops in
		//      memcard/IRQ-handler code broke their timing assumption.
		//   2. The IRQ raise happened INSIDE the LW instruction's Execute call,
		//      meaning the next Step's CheckIrq saw the IRQ pending and saved
		//      EPC pointing at the instruction AFTER the LW (the ANDI of the
		//      poll loop). Combined with the load-delay-slot writeback timing,
		//      this could land the IRQ handler in the middle of a 3-instruction
		//      BIOS critical section where a nested controller poll would
		//      clobber the RX FIFO of the original poll.
		// Now: events fire via _event (see OnControllerEvent), which the
		// scheduler dispatches at the exact transfer/ACK deadline cycle.
		uint stat = StatTxRdy1 | StatTxRdy2;
		if (_rxValid)
			stat |= StatRxFifo;
		if (_ackPending)
		{
			stat |= StatAckInput;
			_ackPending = false; // ACKINPUT is cleared on every JOY_STAT read
		}
		if (_intr)
			stat |= StatIntr;
		return stat;
	}

	private void WriteCtrl(ushort value)
	{
		_ctrl = value;

		// Bit 4: ACK: writing 1 clears the INTR flag and acknowledges the IRQ
		if ((value & CtrlAck) != 0)
		{
			_intr = false;
			_ackPending = false;
		}

		// Bit 1: SELECT (assert /CS). This is the ONLY path that mutates
		// `_selected`, devices ending their session do not touch it.
		bool select = (value & CtrlSelect) != 0;
		if (!_selected && select)
		{
			// Rising edge, start a fresh device-selection round.
			_txPhase = 0;
			_selected = true;
			_activeDevice = SioActiveDevice.None;
			MemCard.ResetTransferState();
		}
		else if (_selected && !select)
		{
			// Falling edge, full deassert: kill the in-flight transfer,
			// clear pending ACK, drop the active device.
			_selected = false;
			_txPhase = 0;
			_activeDevice = SioActiveDevice.None;
			_ackScheduled = false;
			_ackPending = false;
			_transferPending = false;
			MemCard.ResetTransferState();
		}

		// Bit 6: RESET
		if ((value & CtrlReset) != 0)
		{
			_txPhase = 0;
			_selected = false;
			_rxValid = false;
			_ackPending = false;
			_intr = false;
			_transferPending = false;
			_ackScheduled = false;
			_activeDevice = SioActiveDevice.None;
			MemCard.ResetTransferState();
		}
	}

	private void TriggerIrq()
	{
		_intr = true;
		_psx.Interrupts.Raise(PsxConstants.IrqController);
	}

	/// <summary>
	/// CPU writes a byte to JOY_DATA. The actual exchange is deferred by
	/// the baud-rate transfer delay so the BIOS's interrupt-clear window
	/// passes before the new IRQ fires.
	/// </summary>
	private void BeginTransfer(byte txByte)
	{
		if (!_selected)
		{
			_rxData = 0xFF;
			_rxValid = true;
			return;
		}

		_txPendingByte = txByte;
		_transferPending = true;
		// Transfer takes JOY_BAUD * 8 CPU cycles (default baud 0x88 -> 1088 cycles)
		int baudRate = _baud > 0 ? _baud : 0x0088;
		_transferCountdown = baudRate * 8;
		// Arm the controller event for the transfer deadline.
		RescheduleEvent();
	}

	/// <summary>
	/// Execute the actual byte exchange with the controller or memory card
	/// (called after the transfer delay elapses).
	/// </summary>
	private void DoTransfer(byte txByte)
	{
		// Clear ACK from the previous byte exchange
		// ACKINPUT stays high from when ACK fires until the next DoTransfer.
		_ackPending = false;

		byte rx;
		bool ack;

		// IMPORTANT: device-side end-of-session paths must NOT clear `_selected`
		// (CTRL.SELECT mirror, CPU-only).
		// CTRL.SELECT high across two device sessions in one polling pass,
		// e.g. controller poll then memcard poll back-to-back without redrop.
		if ((_ctrl & CtrlSlot) != 0)
		{
			// Port 2: no devices connected in this emulator. Drop the
			// device-session state but leave CTRL.SELECT to the CPU.
			rx = 0xFF;
			_txPhase = 0;
			_activeDevice = SioActiveDevice.None;
			ack = false;
		}
		else if (_activeDevice == SioActiveDevice.MemoryCard)
		{
			// Memory card owns this session, route all bytes to it
			(rx, ack) = MemCard.Transfer(txByte);
			if (!ack)
			{
				// Memory card ended the session, drop the active device but
				// keep `_selected` so the next CPU byte can start a fresh
				// selection round (e.g. memcard poll -> controller poll).
				_activeDevice = SioActiveDevice.None;
				_txPhase = 0;
			}
		}
		else switch (_txPhase)
		{
			case 0: // Device selection byte
				rx = 0xFF;
				if (txByte == 0x01)
				{
					// 0x01 -> controller selected
					_activeDevice = SioActiveDevice.Controller;
					_txPhase = 1;
					ack = true;
				}
				else
				{
					// Try memory card (it handles 0x81; anything else it won't ACK)
					(rx, ack) = MemCard.Transfer(txByte);
					if (ack)
					{
						_activeDevice = SioActiveDevice.MemoryCard;
					}
					// no-ACK fall-through: leave _selected alone, txPhase
					// stays 0 so the next CPU byte gets a fresh shot at
					// device selection
				}
				break;

			case 1: // CPU sends command (0x42 = read buttons)
				if (txByte == 0x42)
				{
					rx = 0x41; // Digital pad ID low byte
					_txPhase = 2;
					ack = true;
				}
				else
				{
					// Bad command for digital pad -> end this device session
					// (active_device -> None), but DO NOT clear _selected.
					// CPU may retry with a new selection byte in this same
					// /CS-asserted window.
					rx = 0xFF;
					_txPhase = 0;
					_activeDevice = SioActiveDevice.None;
					ack = false;
				}
				break;

			case 2: // TAP/multitap address byte
				rx = 0x5A; // Digital pad ID high byte
				_txPhase = 3;
				ack = true;
				break;

			case 3: // Return low byte of button state
				rx = (byte)(ButtonMask & 0xFF);
				_txPhase = 4;
				ack = true;
				break;

			case 4: // Return high byte of button state, LAST byte, no ACK
				// Controller's response complete. Drop active_device so
				// the NEXT CPU byte (in the same /CS-asserted window)
				// triggers a fresh phase-0 selection, typically the
				// game continues into a memcard poll here.
				rx = (byte)(ButtonMask >> 8);
				_txPhase = 0;
				_activeDevice = SioActiveDevice.None;
				ack = false;
				break;

			default:
				rx = 0xFF;
				_txPhase = 0;
				_activeDevice = SioActiveDevice.None;
				ack = false;
				break;
		}

		_rxData = rx;
		_rxValid = true;

		// Schedule ACK pulse after a short delay.
		// Real hardware: ~450 cycles for controller, ~170 cycles for memory card.
		if (ack)
		{
			_ackScheduled = true;
			_ackCountdown = _activeDevice == SioActiveDevice.MemoryCard
				? AckDelayMemCard
				: AckDelayCycles;
			// ACK arms a fresh countdown, make sure the controller event fires when it elapses.
			// This path runs inside DoTransfer which is inside OnControllerEvent ->
			// callback's own RescheduleEvent at the end picks it up, but call explicitly
			// here too for callers that may invoke DoTransfer outside the
			// event (e.g. fast-path MMIO writes).
			RescheduleEvent();
		}
	}
}

[Flags]
public enum PsxButton : ushort
{
	Select = 1 << 0,
	L3 = 1 << 1,
	R3 = 1 << 2,
	Start = 1 << 3,
	Up = 1 << 4,
	Right = 1 << 5,
	Down = 1 << 6,
	Left = 1 << 7,
	L2 = 1 << 8,
	R2 = 1 << 9,
	L1 = 1 << 10,
	R1 = 1 << 11,
	Triangle = 1 << 12,
	Circle = 1 << 13,
	Cross = 1 << 14,
	Square = 1 << 15,
}
