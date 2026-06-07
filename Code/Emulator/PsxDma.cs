using System.Runtime.CompilerServices;

namespace PSXEmu;

/// <summary>
/// PSX DMA controller - 7 channels.
/// Channel 0: MDECin     Channel 1: MDECout
/// Channel 2: GPU        Channel 3: CDROM
/// Channel 4: SPU        Channel 5: PIO
/// Channel 6: OTC (ordering table clear - very commonly used)
/// </summary>
public class PsxDmaController
{
	private readonly Psx _psx;

	public readonly DmaChannel[] Channels = new DmaChannel[7];

	// DMA master registers
	private uint _dpcr; // DMA Priority Control Register (0x1F8010F0)
	private uint _dicr; // DMA Interrupt Control Register (0x1F8010F4)

	/// <summary>Expose DICR for diagnostics (IDLE DIAG log in Psx.cs).</summary>
	public uint DiagDicr => _dicr;

	// Limit DMA IRQ diagnostic log to first N firings so it doesn't spam.
	private int _dmaIrqLogCount;
	
	public int ActiveChannel { get; private set; } = -1;
	public bool HasPendingTransfer { get; private set; }

	// Pooled DMA word buffer. Re-used across all MDEC/SPU transfers so we don't
	// allocate a fresh uint[] (up to ~21 KB for a single MDEC frame) on every call.
	// Sized once on first use to the largest count seen, then never shrinks.
	private uint[] _dmaWordBuf = System.Array.Empty<uint>();

	private uint[] EnsureDmaBuf(uint count)
	{
		if (_dmaWordBuf.Length < count)
			_dmaWordBuf = new uint[count];
		return _dmaWordBuf;
	}

	public PsxDmaController(Psx psx)
	{
		_psx = psx;
		for (int i = 0; i < 7; i++)
			Channels[i] = new DmaChannel();

		// Single DMA event drives slice-paced block deliveries
		// (DMA0/1/3/4). Fires every ~256 cycles while any channel still has
		// pending blocks; the callback runs the legacy per-channel block-drip
		// (one block per active channel) and then re-arms for the next block
		// or deactivates if everything finished.
		_event = new TimingEvent(
			"Dma", int.MaxValue, int.MaxValue,
			(param, _, _) => ((PsxDmaController)param).OnDmaEvent(),
			this);

		_unhaltEvent = new TimingEvent(
			"DmaUnhalt", int.MaxValue, int.MaxValue,
			(param, ticks, _) => ((PsxDmaController)param).OnUnhaltEvent(ticks),
			this);

		_spuDmaEvent = new TimingEvent(
			"SpuDmaComplete", int.MaxValue, int.MaxValue,
			(param, ticks, _) => ((PsxDmaController)param).OnSpuDmaComplete(),
			this);
	}

	private TimingEvent _event;

	// Real PSX DMA drains many blocks in a single bus-arbitration window then
	// yields to the CPU. When blocks remain and the peripheral's request
	// line is still asserted, the channel HaltsTransfer for a short
	// period and an unhalt event fires later to retry in priority order.
	//
	// `remainingTicks` is a LOCAL arbitration counter for slice halt timing.
	// Per-block CPU cycle charging happens INSIDE each TransferOne*Block via
	// ChargeBlockCycles. All four slice-paced channels
	// (DMA0/1/3/4) charge `N + ceil(N/16)` cycles per block to `Cpu.Cycles`,
	// matching real PSX's "DMA halts CPU bus for that many ticks per block"
	// behavior (Bus::GetDMARAMTickCount). ChargeBlockCycles is context-aware,
	// in scheduler callback context it ALSO bumps PendingTicks so the
	// scheduler sees the slice ticks (otherwise they'd be silently dropped
	// from GlobalTickCounter, causing audio/event timing drift).
	private const int DmaMaxSliceTicks = 1000;
	private const int DmaHaltTicks = 100;
	private TimingEvent _unhaltEvent;
	private int _haltTicksRemaining;

	// Deferred SPU-DMA (channel 4) completion, see DoSpuTransfer / OnSpuDmaComplete.
	private TimingEvent _spuDmaEvent;
	private int _spuDmaDelayTicks;

	// Bitmask of channels whose Manual (burst) + chopping transfer was delayed at
	// CHCR-write time; the unhalt event dispatches each one after the delay
	// elapses. A bitmask (not a single index) so two channels deferring before
	// the same unhalt fire don't clobber each other.
	private int _deferredManualMask;

	// Approximate per-block interval, matches the legacy LegacyTick cadence
	// where Tick() fired every 256 cycles. Kept as a safety-net retry path
	// while the new slice-budget model bakes in; OnDmaEvent now respects
	// IsTransferHalted to avoid colliding with the unhalt event's retry.
	// Cleanup may remove this once SetRequest + unhalt are proven
	// sufficient for every channel.
	private const int DmaBlockIntervalCycles = 256;

	private void OnDmaEvent()
	{
		TickInternal();
		RescheduleEvent();
	}

	private void RescheduleEvent()
	{
		// Any active slice-paced channel?  If so, schedule for the next
		// block-delivery boundary. Otherwise leave the event deactivated.
		// ScheduleIfEarlier (not Schedule), every MMIO write at the end of
		// WriteWord calls RescheduleEvent. Plain Schedule(N) would push the
		// deadline forward by N cycles on every unrelated write, and the
		// event would never reach its fire time.
		//
		// If a slice halt is currently active (IsTransferHalted), the
		// unhalt event owns resumption, skip arming the periodic safety-net
		// to avoid a redundant pre-halt fire.
		if (IsTransferHalted()) return;
		for (int i = 0; i < 7; i++)
		{
			if (Channels[i].PendingBlocks > 0)
			{
				_event.ScheduleIfEarlier(DmaBlockIntervalCycles);
				return;
			}
		}
		_event.Deactivate();
	}

	/// <summary>
	/// All four gates must pass for the channel to be
	/// allowed to start/continue a transfer:
	///   1. DPCR per-channel master enable (bit ch*4+3)
	///   2. CHCR enable_busy (bit 24)
	///   3. NOT slice-halted (unless <paramref name="ignoreHalt"/>, OR the
	///      channel uses manual sync mode = burst, Manual transfers are
	///      single-shot and don't participate in slice halts)
	///   4. peripheral request line asserted
	/// Pure read-only predicate.
	/// </summary>
	private bool CanTransferChannel(int ch, bool ignoreHalt)
	{
		if (((_dpcr >> (ch * 4 + 3)) & 1) == 0) return false;
		var channel = Channels[ch];
		if ((channel.Chcr & 0x01000000u) == 0) return false;
		uint syncMode = (channel.Chcr >> 9) & 3;
		// syncMode 0 == Manual (burst); halts only apply to slice / linked-list.
		if (syncMode != 0 && IsTransferHalted() && !ignoreHalt) return false;
		return channel.Request;
	}

	// True while a slice halt is pending.
	private bool IsTransferHalted() => _unhaltEvent.IsActive;

	/// <summary>
	/// Pause slice-paced transfers for <paramref name="duration"/> ticks.
	/// Accumulates into a running total, repeated calls during one CPU window
	/// extend the halt without re-scheduling the event.
	/// The unhalt event fires once after the accumulated total elapses.
	/// </summary>
	private void HaltTransfer(int duration)
	{
		_haltTicksRemaining += duration;
		if (_unhaltEvent.IsActive) return;
		_unhaltEvent.SetIntervalAndSchedule(_haltTicksRemaining);
	}

	/// <summary>
	/// Walks channels in priority order and retries each one whose CanTransferChannel
	/// gate passes. If any channel halts again (returns false), stop iterating,
	/// the next halt event will pick up where we left off.
	/// </summary>
	private void OnUnhaltEvent(int ticks)
	{
		_haltTicksRemaining -= ticks;
		_unhaltEvent.Deactivate();

		// Run any Manual+chopping transfers that were deferred at CHCR-write time.
		// Re-check DPCR enable + CHCR busy, the game may have disabled or aborted
		// the channel during the delay window.
		if (_deferredManualMask != 0)
		{
			int mask = _deferredManualMask;
			_deferredManualMask = 0;
			for (int ch = 0; ch < 7; ch++)
			{
				if ((mask & (1 << ch)) == 0) continue;
				if (((_dpcr >> (ch * 4 + 3)) & 1) == 0) continue;
				if ((Channels[ch].Chcr & 0x01000000u) == 0) continue;
				DispatchTransfer(ch);
			}
		}

		// Priority order: real PSX uses DPCR.priority bits per channel; we use
		// channel index 0..6 as a simple stand-in.
		for (int i = 0; i < 7; i++)
		{
			if (!CanTransferChannel(i, ignoreHalt: false)) continue;
			if (!TryTransferChannel(i)) return;
		}
		_haltTicksRemaining = 0;
	}

	/// <summary>
	/// Drain blocks for channel <paramref name="ch"/> up to the slice budget.
	/// Returns <c>true</c> if the channel completed (or stalled on Request);
	/// <c>false</c> if it halted on budget exhaustion (more blocks pending,
	/// request still asserted).
	///
	///   - Inner loop while (PendingBlocks > 0 && Request && remaining > 0)
	///   - Dispatch to per-channel TransferOne*Block (which returns false to
	///     signal "no progress", currently only MDECout's scenario A path).
	///   - Subtract synthetic per-block cost from `remainingTicks` (used for
	///     halt arbitration only; does NOT bump Cpu.Cycles).
	///   - After the loop: complete OR stalled-on-Request -> return true;
	///     budget-exhausted with Request still asserted -> HaltTransfer + false.
	/// </summary>
	private bool TryTransferChannel(int ch)
	{
		if (!CanTransferChannel(ch, ignoreHalt: false)) return true;
		var channel = Channels[ch];

		// GPU linked-list has its own slice loop (per-entry tick
		// cost differs from per-block; tracks position in ch.Madr not
		// PendingBlocks). Burst/slice (manual) GPU modes are single-shot
		// via DoGpuTransfer and never enter the slice retry path.
		if (ch == 2)
		{
			uint syncMode = (channel.Chcr >> 9) & 3;
			if (syncMode == 2) return TryTransferGpuLinkedList(channel);
			return true;
		}

		int remainingTicks = DmaMaxSliceTicks;
		while (channel.PendingBlocks > 0 && channel.Request && remainingTicks > 0)
		{
			bool progress;
			switch (ch)
			{
				case 0: progress = TransferOneMdecInBlock(channel); break;
				case 1: progress = TransferOneMdecOutBlock(channel); break;
				case 3: progress = TransferOneCdromBlock(channel); break;
				case 4: progress = TransferOneSpuBlock(channel); break;
				default: return true; // non-slice-paced channels never enter here
			}
			if (!progress) break; // peripheral not ready (MDECout scenario A)
			// Synthetic block cost for slice arbitration only.
			// N + ceil(N/16)
			int n = (int)channel.BlockSize;
			remainingTicks -= n + ((n + 15) / 16);
		}
		if (channel.PendingBlocks == 0) return true; // transfer complete
		if (!channel.Request) return true; // stalled on peripheral; SetRequest will resume
		// Budget exhausted with more blocks pending + request still asserted ->
		// halt and let the unhalt event retry shortly.
		HaltTransfer(DmaHaltTicks);
		return false;
	}

	/// <summary>
	/// Slice-paced GPU linked-list transfer. Walks the
	/// linked list from <c>ch.Madr</c> until either (a) a terminator entry
	/// is reached (bit 23 of next-pointer set), completes the transfer,
	/// fires DMA2 IRQ if armed, OR (b) the slice budget exhausts, saves
	/// the current position in <c>ch.Madr</c>, calls HaltTransfer, returns
	/// false so UnhaltTransfer resumes from there shortly.
	///
	///   - 8 ticks header read (always)
	///   - +5 setup + N words + ceil(N/16) row overhead if word_count > 0
	/// Tick total is bumped to Cpu.Cycles via AdvanceClock at the end of the
	/// slice (whether completing or halting), mirroring the legacy
	/// "AdvanceClock(dmaTicks) at end of DoGpuTransfer" behaviour but split
	/// across slices instead of paying it all in one synchronous burst.
	///
	/// Returns <c>true</c> on completion; <c>false</c> on slice halt.
	/// </summary>
	private bool TryTransferGpuLinkedList(DmaChannel ch)
	{
		int remainingTicks = DmaMaxSliceTicks;
		int sliceTicks = 0;
		uint addr = ch.Madr & 0x001FFFFC;
		// Safety cap on entries-per-slice to prevent a pathological linked
		// list with zero word_counts from running away, slice budget gates
		// the normal case but a list of 1000 empty headers would still
		// drain 8000 ticks instantly, beyond budget intent.
		int maxEntries = 1024;

		while (remainingTicks > 0 && maxEntries-- > 0)
		{
			uint header = _psx.Memory.ReadWord(addr);
			int wordCount = (int)(header >> 24);
			uint dataAddr = (addr + 4) & 0x001FFFFC;
			for (int i = 0; i < wordCount; i++)
			{
				uint cmd = _psx.Memory.ReadWord(dataAddr);
				_psx.Gpu.WriteGp0(cmd);
				dataAddr = (dataAddr + 4) & 0x001FFFFC;
			}
			int entryTicks = 8;
			if (wordCount > 0)
				entryTicks += 5 + wordCount + (wordCount + 15) / 16;
			sliceTicks += entryTicks;
			remainingTicks -= entryTicks;

			// Terminator: bit 23 of next-pointer set (0xFFFFFF or any 0x8xxxxx).
			uint next = header & 0x00FFFFFF;
			if ((next & 0x800000) != 0)
			{
				ch.Madr = 0x00FFFFFF;
				ch.Chcr &= ~0x01000000u;
				ChargeSliceTicks(sliceTicks);
				// DMA2 (GPU) IRQ-on-completion stays DISABLED
				// for now. History: firing this IRQ broke the BIOS Sony logo
				// (StartTransfer comment ~ch != 2 skip). The theory was that
				// slice-pacing + HaltTransfer would fix the spin, but we
				// haven't verified that and the user wants this stable. Most
				// games poll CHCR.enable_busy for GPU DMA completion anyway,
				// so skipping the IRQ is harmless.
				//   uint irqEnable = (_dicr >> 18) & 1;
				//   if (irqEnable != 0) { _dicr |= (uint)(1 << 26); CheckDmaIrq(); }
				return true;
			}
			addr = next & 0x001FFFFC;
		}

		// Slice exhausted (or safety cap hit) before terminator, save
		// position so the next slice picks up here, and halt.
		ch.Madr = addr;
		ChargeSliceTicks(sliceTicks);
		HaltTransfer(DmaHaltTicks);
		return false;
	}

	/// <summary>
	/// DMA slice-tick charge that's visible to the scheduler in BOTH calling contexts:
	///
	///   - During a CPU step (CHCR write triggering the first slice):
	///     bump <c>Cpu.Cycles</c>. The Run() loop's
	///     <c>PendingTicks += Cycles - cyclesBefore</c> at end-of-step
	///     picks up the delta and commits it to <c>GlobalTickCounter</c>
	///     on next dispatch. Bumping <c>PendingTicks</c> here too would
	///     double-count.
	///
	///   - During a scheduler callback (unhalt event firing subsequent
	///     slices): bump BOTH <c>Cpu.Cycles</c> AND <c>PendingTicks</c>.
	///     There's no <c>cyclesBefore</c> diff to catch the Cycles bump
	///     in this path, so without bumping PendingTicks the slice ticks
	///     are silently dropped from <c>GlobalTickCounter</c>.
	///
	/// The dropped-ticks bug manifested as audio crackling.
	/// initial commit: GPU linked-lists that needed multiple slices lost
	/// 80%+ of their tick budget from GlobalTickCounter, so SPU sample
	/// events fired less often than wallclock demanded -> buffer underrun.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ChargeSliceTicks(int ticks)
	{
		_psx.Cpu.Cycles += ticks;
		if (_psx.Scheduler.CurrentEvent != null)
			_psx.Cpu.PendingTicks += ticks;
	}

	public void Reset()
	{
		foreach (var ch in Channels)
			ch.Reset();
		_dpcr = 0x07654321; // default priority
		_dicr = 0;
		ActiveChannel = -1;
		HasPendingTransfer = false;
		// Clear slice-halt state so a stale unhalt event from the
		// previous run doesn't fire spuriously after Reset.
		_haltTicksRemaining = 0;
		_deferredManualMask = 0;
		_unhaltEvent?.Deactivate();
		_spuDmaEvent?.Deactivate();
		// Nothing is pending at boot, so the event stays
		// deactivated until a channel starts a slice-paced transfer.
		RescheduleEvent();
	}

	// ---- Save-state ---- (_dmaWordBuf is scratch; _dmaIrqLogCount is diag.)
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		foreach (var ch in Channels)
		{
			w.U32(ch.Madr); w.U32(ch.Bcr); w.U32(ch.Chcr);
			w.U32(ch.PendingBlocks); w.U32(ch.BlockSize); w.Bool(ch.Request);
		}
		w.U32(_dpcr); w.U32(_dicr);
		w.S32(_haltTicksRemaining); w.S32(_deferredManualMask);
		w.Bool(_ch4ToRam);
		w.S32(ActiveChannel); w.Bool(HasPendingTransfer);
		_event.SaveState(w, g);
		_unhaltEvent.SaveState(w, g);
		_spuDmaEvent.SaveState(w, g);
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		foreach (var ch in Channels)
		{
			ch.Madr = r.U32(); ch.Bcr = r.U32(); ch.Chcr = r.U32();
			ch.PendingBlocks = r.U32(); ch.BlockSize = r.U32(); ch.Request = r.Bool();
		}
		_dpcr = r.U32(); _dicr = r.U32();
		_haltTicksRemaining = r.S32(); _deferredManualMask = r.S32();
		_ch4ToRam = r.Bool();
		ActiveChannel = r.S32(); HasPendingTransfer = r.Bool();
		_event.LoadState(r, g);
		_unhaltEvent.LoadState(r, g);
		_spuDmaEvent.LoadState(r, g);
	}

	/// <summary>
	/// Bump <see cref="MipsCore.Cycles"/> by the DMA RAM tick cost for a
	/// block of <paramref name="blockSize"/> words.
	///
	/// Now context-aware (mirrors <see cref="ChargeSliceTicks"/>).
	/// In CPU-step context (CHCR write triggering DMA), only <see cref="MipsCore.Cycles"/>
	/// is bumped, the Run() loop's cyclesBefore-diff picks up the delta for
	/// PendingTicks. In scheduler-callback context (peripheral SetRequest
	/// firing TryTransferChannel from inside an event callback), ALSO bump
	/// <see cref="MipsCore.PendingTicks"/> so the slice's ticks are visible
	/// to GlobalTickCounter and don't get silently dropped. Without this,
	/// per-block charges from callback-triggered DMA would vanish from the
	/// scheduler's time accounting, causing audio/event timing drift.
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void ChargeBlockCycles(uint blockSize)
	{
		int n = (int)blockSize;
		int ticks = n + ((n + 15) / 16);
		_psx.Cpu.Cycles += ticks;
		if (_psx.Scheduler.CurrentEvent != null)
			_psx.Cpu.PendingTicks += ticks;
	}

	/// <summary>
	/// Asserts or clears the bus-arbitration request signal for a DMA channel,
	/// mirroring real PSX bus behaviour.
	/// Peripherals call this from their own state transitions:
	///   - CDROM asserts ch3 when a fresh sector buffer is available, clears
	///     it when the buffer has been drained by DMA3.
	///   - MDEC asserts ch0 when its input FIFO has room, ch1 when its
	///     output FIFO has data. Both clear when the respective FIFO state
	///     reverses.
	///   - SPU asserts ch4 when its DMA FIFO is in the right state for the
	///     active direction (read vs write).
	/// Slice-paced TransferOne*Block invocations are gated on the channel
	/// request, false stalls the channel without decrementing PendingBlocks,
	/// matching real PSX bus behaviour where DMA physically waits for the
	/// peripheral to be ready.
	/// </summary>
	public void SetRequest(int ch, bool asserted)
	{
		if (ch < 0 || ch >= 7) return;
		bool wasAsserted = Channels[ch].Request;
		if (wasAsserted == asserted) return;
		Channels[ch].Request = asserted;
		// A freshly-asserted request fires the channel's transfer immediately
		// if it can transfer now (gates pass). No periodic-event rearm,
		// the slice loop drains the channel in this call and either completes,
		// stalls again on Request, or halts on slice exhaust (HaltTransfer
		// owns retry from there). Falling back to RescheduleEvent for the
		// "halted, will retry later" case where TryTransferChannel can't run.
		if (!asserted) return;
		if (Channels[ch].PendingBlocks == 0) return;
		if (CanTransferChannel(ch, ignoreHalt: false))
			TryTransferChannel(ch);
		else
			RescheduleEvent(); // safety net for halted/disabled-but-pending case
	}

	/// <summary>
	/// Slice-paced channel poll. Called from <see cref="OnDmaEvent"/> as a
	/// safety-net every <see cref="DmaBlockIntervalCycles"/>; the unhalt event
	/// + SetRequest path covers the primary resumption channels. Each
	/// active channel drains up to <see cref="DmaMaxSliceTicks"/> worth of
	/// blocks via <see cref="TryTransferChannel"/> before either completing,
	/// stalling on Request, or halting on budget exhaustion.
	/// </summary>
	private void TickInternal()
	{
		// If a slice halt is pending the unhalt event owns retrying, skip
		// the safety-net to avoid two retry paths racing.
		if (IsTransferHalted()) return;
		// Channels 0/1/3/4 are slice-paced; ch2 GPU linked-list is also
		// slice-paced and may need polling if it stalled without
		// halting (unusual but cheap to cover); ch6 OTC stays synchronous,
		// ch5 PIO unimplemented.
		if (Channels[0].PendingBlocks > 0) TryTransferChannel(0);
		if (Channels[1].PendingBlocks > 0) TryTransferChannel(1);
		if ((Channels[2].Chcr & 0x01000000u) != 0 && ((Channels[2].Chcr >> 9) & 3) == 2)
			TryTransferChannel(2);
		if (Channels[3].PendingBlocks > 0) TryTransferChannel(3);
		if (Channels[4].PendingBlocks > 0) TryTransferChannel(4);
	}

	/// <summary>
	/// Read a 32-bit DMA register.
	/// DMA channels occupy: 0x1F801080 + (ch*0x10) + offset (0,4,8)
	/// Master regs: DPCR=0x1F8010F0, DICR=0x1F8010F4
	/// </summary>
	public uint ReadWord(uint addr)
	{
		if (addr == 0x1F8010F0) return _dpcr;
		if (addr == 0x1F8010F4) return _dicr;

		int ch = (int)((addr - 0x1F801080) >> 4);
		if (ch < 0 || ch >= 7) return 0;
		uint reg = (addr >> 2) & 3;
		return reg switch
		{
			0 => Channels[ch].Madr,
			1 => Channels[ch].Bcr,
			2 => Channels[ch].Chcr,
			_ => 0,
		};
	}

	public void WriteWord(uint addr, uint value)
	{
		if (addr == 0x1F8010F0)
		{
			// DPCR write: per-channel master-enable bits live at (channel*4 + 3).
			// When a previously disabled channel becomes enabled and its CHCR.start
			// is already set, the pending transfer fires immediately.
			// Required because the game may set CHCR.start while the channel is DPCR-disabled
			// (e.g., during FMV setup the producer disables MDECin briefly while writing IQ
			// tables), without this re-check, the transfer would never run.
			uint oldDpcr = _dpcr;
			_dpcr = value;
			for (int ch = 0; ch < 7; ch++)
			{
				bool wasEnabled = ((oldDpcr >> (ch * 4 + 3)) & 1) != 0;
				bool nowEnabled = ((_dpcr  >> (ch * 4 + 3)) & 1) != 0;
				if (!wasEnabled && nowEnabled && (Channels[ch].Chcr & 0x01000000u) != 0)
					StartTransfer(ch);
			}
			RescheduleEvent();
			return;
		}
		if (addr == 0x1F8010F4) { WriteDicr(value); return; }

		int chIdx = (int)((addr - 0x1F801080) >> 4);
		if (chIdx < 0 || chIdx >= 7) return;
		uint reg = (addr >> 2) & 3;

		switch (reg)
		{
			case 0: Channels[chIdx].Madr = value & 0x00FFFFFF; break;
			case 1: Channels[chIdx].Bcr = value; break;
			case 2:
				Channels[chIdx].Chcr = value;
				if ((value & 0x01000000) != 0)
				{
					StartTransfer(chIdx);
				}
				else
				{
					// CHCR.enable_busy cleared mid-transfer: abort any pending
					// slice-paced blocks for this channel. Matches ProjectPSX's
					// `if (!enable) pendingBlocks = 0;` in DmaChannel.cs:85.
					Channels[chIdx].PendingBlocks = 0;
				}
				break;
		}
		// A CHCR start (StartTransfer queued new pending blocks) or a CHCR clear
		// (zeroed PendingBlocks) may have changed whether the slice-paced DMA event needs to be running.
		// Recompute.
		RescheduleEvent();
	}

	/// <summary>
	/// Halfword (16-bit) write to a DMA register. Composes the merged word
	/// value from this controller's *cached* state (no bus ReadWord) and
	/// dispatches to <see cref="WriteWord"/> with two corrections versus a
	/// naive RMW:
	///   1. The read side is taken from internal fields (`_dpcr` / `_dicr` /
	///      `Channels[ch].*`), so we don't issue a redundant bus read.
	///   2. For DICR, ack bits [24:30] that lie OUTSIDE the half the game
	///      wrote are masked to zero in the composed value. Otherwise the
	///      RMW would read pending-IRQ bits back as 1 and write them through
	///      to <see cref="WriteDicr"/>, which interprets `1` as
	///      "ack this IRQ", silently clearing pending IRQs the kernel hadn't
	///      serviced yet. The fix preserves the existing ack state for bits
	///      the game's SH didn't actually cover.
	/// SH on DMA registers is extremely rare in real games (essentially all
	/// drivers use SW), but the correctness matters for ports of homebrew /
	/// test ROMs.
	/// </summary>
	public void WriteHalf(uint addr, ushort value)
	{
		int shift = (int)((addr & 2u) * 8u);
		uint mask = 0xFFFFu << shift;
		uint shifted = (uint)value << shift;
		WriteSizedInternal(addr, mask, shifted);
	}

	/// <summary>Byte (8-bit) write to a DMA register. See <see cref="WriteHalf"/>
	/// for the rationale, same internal compose-and-dispatch path.</summary>
	public void WriteByte(uint addr, byte value)
	{
		int shift = (int)((addr & 3u) * 8u);
		uint mask = 0xFFu << shift;
		uint shifted = (uint)value << shift;
		WriteSizedInternal(addr, mask, shifted);
	}

	private void WriteSizedInternal(uint addr, uint mask, uint shifted)
	{
		// Pull the current value from our own cached state.
		uint cur;
		if (addr >= 0x1F8010F0 && addr < 0x1F8010F4) cur = _dpcr;
		else if (addr >= 0x1F8010F4 && addr < 0x1F8010F8) cur = _dicr;
		else
		{
			int ch = (int)((addr - 0x1F801080) >> 4);
			if (ch < 0 || ch >= 7) return;
			uint reg = (addr >> 2) & 3;
			cur = reg switch
			{
				0 => Channels[ch].Madr,
				1 => Channels[ch].Bcr,
				2 => Channels[ch].Chcr,
				_ => 0u,
			};
		}

		uint composed = (cur & ~mask) | shifted;

		// DICR ack-bit fix: bits [24:30] are write-1-to-clear. Force the ack
		// bits OUTSIDE our write-mask back to 0 in the composed value so
		// WriteDicr sees `0` there and doesn't clear those IRQ flags.
		if (addr >= 0x1F8010F4 && addr < 0x1F8010F8)
		{
			uint ackOutsideOurMask = 0x7F000000u & ~mask;
			composed &= ~ackOutsideOurMask;
		}

		WriteWord(addr & ~3u, composed);
	}

	private void WriteDicr(uint value)
	{
		// Bits [0:5]   = unknown / unused
		// Bit  15      = Force IRQ
		// Bits [16:22] = IRQ Enable per channel
		// Bit  23      = Master IRQ enable
		// Bits [24:30] = IRQ Flags per channel (write 1 to clear / acknowledge)
		// Bit  31      = Master IRQ flag (read-only, computed)
		//
		// The writable bits ([0:5], [15], [16:23]) are normal R/W and must be
		// REPLACED by the written value, NOT OR-merged with the old value. The
		// previous code only cleared the flag bits [24:30] (`_dicr & ~clearBits`)
		// and then OR-ed in `value & 0x00FF803F`, so the IRQ-ENABLE bits [16:23]
		// could only ever be SET, never cleared. That silently broke libcd's
		// STR/FMV streaming: it clears channel-3's IRQ-enable bit for every
		// non-final chunk and re-enables it only for the LAST chunk, using that as
		// the sole "frame ready" gate (only the last chunk's CD-DMA completion is
		// meant to raise the IRQ that flips the ring slot to state==2). With the
		// enable stuck on, EVERY CD-DMA completion fired the ready IRQ, so StGetNext
		// handed each frame to DecDCTvlc after only ~2 of its N chunks had loaded,
		// the garbled-first-FMV-frame bug.
		_dicr = (_dicr & ~0x00FF803Fu) | (value & 0x00FF803Fu); // replace writable bits
		_dicr &= ~(value & 0x7F000000u);                        // ack flags (write-1-to-clear)
		CheckDmaIrq();
	}

	private void CheckDmaIrq()
	{
		bool masterEnable = (_dicr & 0x00800000) != 0;
		bool forceIrq = (_dicr & 0x00008000) != 0; // bit 15: forces the master flag high
		// Correct formula: extract flags [30:24] and enables [22:16] separately.
		// The original "_dicr & (_dicr>>16) & 0x7F" checked bits [6:0] which are always 0 -> permanently false.
		uint flags = (_dicr >> 24) & 0x7F;
		uint enables = (_dicr >> 16) & 0x7F;
		bool anyPending = (flags & enables) != 0;
		// Master flag (bit 31) = force_irq OR (master_enable AND any channel flag&enable)
		// Previously the bit-15 force path never raised an IRQ.
		if (forceIrq || (masterEnable && anyPending))
		{
			_dicr |= 0x80000000u;
			if (_dmaIrqLogCount++ < 5)
				PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.GameError,
					$"[DMA IRQ #{_dmaIrqLogCount}] DICR=0x{_dicr:X8} flags=0x{flags:X2} enables=0x{enables:X2} IStat=0x{_psx.Interrupts.IStat:X} CPU={_psx.Cpu.Cycles}");
			_psx.Interrupts.Raise(PsxConstants.IrqDma);
		}
		else
		{
			_dicr &= ~0x80000000u;
			// Level-triggered deassert: the DMA IRQ line on real hardware follows DICR bit 31.
			// The moment the BIOS clears the channel flags (bit 31 goes low), I_STAT bit 3
			// must also go low, otherwise the CPU infinitely re-enters the DMA IRQ handler.
			_psx.Interrupts.Clear(PsxConstants.IrqDma);
		}
	}

	private void StartTransfer(int ch)
	{
		// DPCR per-channel master-enable check. Master enable bits live at (channel*4 + 3) of
		// DPCR. If the channel is disabled, the transfer is deferred, CHCR
		// and BCR/MADR remain set, and when DPCR is later updated to enable the
		// channel, the WriteWord(DPCR) handler re-fires StartTransfer for it.
		// Skipping this check causes channels to transfer when the game has
		// explicitly disabled them, scrambling MDEC pipelining during FMV setup.
		if (((_dpcr >> (ch * 4 + 3)) & 1) == 0)
		{
			PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info, $"[DMA] StartTransfer ch={ch} DEFERRED : DPCR master enable=0 (DPCR=0x{_dpcr:X8})");
			return;
		}

		var channel = Channels[ch];
		uint chcr = channel.Chcr;
		uint syncMode = (chcr >> 9) & 3;   // 0=burst, 1=slice, 2=linked list

		// Manual-chopping delay. When a Manual (burst) transfer has chopping enabled,
		// real PSX periodically yields the bus back to the CPU, so the transfer
		// and its completion IRQ lands LATER than an instant burst would.
		// Games like Lagnacure Legend rely on this: they enable the DICR IRQ AFTER the
		// CHCR write that kicks the transfer, and would miss the completion IRQ if we fired it
		// synchronously. Estimate the delay and defer the transfer to the
		// unhalt event. word_count<=4 is excluded so Dotchi Mecha's 3-word
		// CD-header transfer still completes immediately. NOTE: uses the RAW BCR
		// word-count field (NOT the 0->0x10000 expansion).
		//
		// GPU (ch 2) is deliberately EXCLUDED, we intentionally
		// don't fire the GPU DMA IRQ (it spins the BIOS Sony logo, see the IRQ
		// skip in DispatchTransfer). So deferring GPU buys nothing and only adds
		// timing risk on the most regression-prone channel. Kept for the
		// IRQ-firing channels (MDEC/CDROM/SPU).
		if (ch != 6 && ch != 2 && syncMode == 0 && (chcr & 0x00000100u) != 0)
		{
			uint wordCountRaw = channel.Bcr & 0xFFFF;
			int cpuCyclesPerBlock = 1 << (int)((chcr >> 20) & 7);
			uint blocks = wordCountRaw >> (int)((chcr >> 16) & 7);
			int delayCycles = (int)System.Math.Min((long)cpuCyclesPerBlock * blocks, 500);
			if (wordCountRaw > 4 && delayCycles > 1)
			{
				_deferredManualMask |= (1 << ch);
				HaltTransfer(delayCycles);
				return; // unhalt event runs DispatchTransfer(ch) after the delay
			}
		}

		DispatchTransfer(ch);
	}

	/// <summary>
	/// Run the per-channel transfer dispatch (the sync-mode switch + the
	/// completion IRQ for non-slice-paced channels). Split out from
	/// <see cref="StartTransfer"/> so the unhalt event can re-run a deferred
	/// Manual+chopping transfer WITHOUT re-triggering the deferral check,
	/// whose UnhaltTransfer calls the channel transfer function
	/// directly rather than re-entering the CHCR-write handler.
	/// </summary>
	private void DispatchTransfer(int ch)
	{
		var channel = Channels[ch];
		uint chcr = channel.Chcr;

		bool toRam = (chcr & 0x01) == 0; // 0 = device->RAM, 1 = RAM->device
		uint syncMode = (chcr >> 9) & 3;   // 0=burst, 1=slice, 2=linked list

		PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
			$"[DMA] StartTransfer ch={ch} madr=0x{channel.Madr:X} bcr=0x{channel.Bcr:X} chcr=0x{chcr:X} toRam={toRam} sync={syncMode}");

		switch (ch)
		{
			case 0: // MDECin: RAM -> MDEC (send compressed video data; discard, notify MDEC)
				DoMdecInTransfer(channel);
				break;
			case 1: // MDECout: MDEC -> RAM (decoded pixel data)
				DoMdecOutTransfer(channel);
				break;
			case 6: // OTC: clear ordering table in RAM
				DoOtcTransfer(channel);
				break;
			case 2: // GPU
				if (!toRam)
					DoGpuTransfer(channel, syncMode);
				else
					DoGpuReadTransfer(channel);
				break;
			case 3: // CDROM -> RAM
				DoCdromTransfer(channel);
				break;
			case 4: // SPU
				DoSpuTransfer(channel, toRam);
				break;
			default:
				// For unimplemented channels, just clear the start bit
				channel.Chcr &= ~0x01000000u;
				break;
		}

		// Signal DMA IRQ if enabled for this channel (bits 16-22 of DICR).
		// Skip ch2 (GPU) and ch6 (OTC): the BIOS spins at the Sony logo if we
		// fire these. Likely missing piece: slice-paced linked-list DMA
		// with HaltTransfer + GPU SetRequest signaling.
		//
		// Skip ch0 (MDECin), ch1 (MDECout), ch3 (CDROM), ch4 (SPU), they're all
		// slice-paced and fire their own IRQ from TransferOne*Block when PendingBlocks
		// reaches 0 (the *actual* end of the transfer). Firing here would fire
		// IMMEDIATELY at game's CHCR write (premature), defeating the slice-pace
		// timing that the game's state machine depends on for parallel CPU work.
		if (ch != 0 && ch != 1 && ch != 2 && ch != 3 && ch != 4 && ch != 6)
		{
			uint irqEnable = (_dicr >> (16 + ch)) & 1;
			if (irqEnable != 0)
			{
				_dicr |= (uint)(1 << (24 + ch)); // set channel IRQ flag
				CheckDmaIrq();
			}
		}
	}

	// MDECin (ch0): RAM -> MDEC. Slice-paced transfer (Theory M 2026-05-12):
	// transfer ONE block per call, with CPU running between blocks via TickPendingBlocks.
	// Previously all-at-once: DMA0 delivered all 1760 words instantly, MDEC decoded
	// "in parallel" via Execute() and BUSY extension. But the CPU never ran between
	// "kick off DMA0" and "MDEC ready for next cmd1", potentially skipping critical
	// FMV state-machine transitions that SH/RE2 rely on.
	//
	// NOTE: ProjectPSX BYPASSES MDECin (all-at-once) per their HACK comment, but
	// ProjectPSX has no MDEC BUSY extension, so they need the bypass to compensate.
	// Our model has BUSY extension, so slice-pacing DMA0 is more realistic.
	private void DoMdecInTransfer(DmaChannel ch)
	{
		uint bs = ch.Bcr & 0xFFFF;
		uint bc = ch.Bcr >> 16;
		ch.BlockSize = bs == 0 ? 0x10000u : bs;
		ch.PendingBlocks = bc == 0 ? 1u : bc;

		// Enter the slice loop immediately if gates pass; it'll drain
		// up to DmaMaxSliceTicks worth of blocks and either complete, stall on
		// Request, or HaltTransfer. Falls back to the periodic safety-net
		// event if CanTransferChannel currently denies (halted / no request).
		if (CanTransferChannel(0, ignoreHalt: false))
			TryTransferChannel(0);
	}

	/// <summary>
	/// Move one block (<c>ch.BlockSize</c> words) from RAM to MDEC input FIFO.
	/// On the last block, clears CHCR.enable_busy and fires the channel-0 DMA IRQ.
	/// Returns <c>true</c> when a block was transferred (PendingBlocks decremented);
	/// always <c>true</c> for MDECin since RAM-side reads can't stall.
	/// </summary>
	private bool TransferOneMdecInBlock(DmaChannel ch)
	{
		uint addr = ch.Madr & 0x001FFFFC;
		uint blockSize = ch.BlockSize;
		var buf = EnsureDmaBuf(blockSize);
		var ram = _psx.Memory.Ram;
		int ramLen = ram.Length;
		for (uint i = 0; i < blockSize; i++)
		{
			if ((int)addr + 3 < ramLen)
				buf[i] = ram[addr]
				       | ((uint)ram[addr + 1] << 8)
				       | ((uint)ram[addr + 2] << 16)
				       | ((uint)ram[addr + 3] << 24);
			else
				buf[i] = 0;
			addr = (addr + 4) & 0x001FFFFC;
		}
		ch.Madr = addr;
		ch.PendingBlocks--;
		_psx.Mdec.DmaWrite(buf, (int)blockSize);
		// Per-block CPU cycle charge re-enabled.
		ChargeBlockCycles(blockSize);

		if (ch.PendingBlocks == 0)
		{
			// Last block: finalise the channel and fire the IRQ if armed.
			ch.Chcr &= ~0x01000000u;
			// Channel-0 (MDECin) IRQ enable lives at DICR bit 16; flag at bit 24.
			uint irqEnable = (_dicr >> 16) & 1;
			if (irqEnable != 0)
			{
				_dicr |= (uint)(1 << 24);
				CheckDmaIrq();
			}
		}
		return true;
	}

	// MDECout (ch1): MDEC -> RAM. Slice-paced transfer (Theory C 2026-05-12):
	// transfer ONE block per call, with CPU running between blocks via TickPendingBlocks.
	// Matches real PSX DMA1 (REQUEST mode) and ProjectPSX DmaChannel.cs:103-112.
	// Previously all-at-once: IRQ fired synchronously with game's CHCR write, skipping
	// the parallel CPU work that real PSX gives between "kick off DMA1" and the
	// completion IRQ. Theory: SH/RE2 FMV state machines depend on this parallel time.
	private void DoMdecOutTransfer(DmaChannel ch)
	{
		uint bs = ch.Bcr & 0xFFFF;
		uint bc = ch.Bcr >> 16;
		ch.BlockSize = bs == 0 ? 0x10000u : bs;
		ch.PendingBlocks = bc == 0 ? 1u : bc;

		// See DoMdecInTransfer comment.
		if (CanTransferChannel(1, ignoreHalt: false))
			TryTransferChannel(1);
	}

	/// <summary>
	/// Move one block (<c>ch.BlockSize</c> words) from the MDEC output FIFO to RAM.
	/// On the last block, clears CHCR.enable_busy and fires the channel-1 DMA IRQ.
	/// Returns <c>true</c> on real or scenario-B progress (PendingBlocks decremented);
	/// <c>false</c> on scenario A (MDEC still decoding upstream, slice loop should
	/// break and let HaltTransfer reschedule a retry, mirroring real PSX bus stall).
	/// </summary>
	private bool TransferOneMdecOutBlock(DmaChannel ch)
	{
		uint addr = ch.Madr & 0x001FFFFC;
		uint blockSize = ch.BlockSize;
		var buf = EnsureDmaBuf(blockSize);
		int got = _psx.Mdec.DmaRead(buf, (int)blockSize);

		// FIX (2026-05-12): when DmaRead returns 0 words, we have two scenarios:
		//
		//   A) Race with DMA0: MDEC is still in DecodingMacroblock state but hasn't
		//      produced output yet (DMA0 hasn't fed enough bitstream). We MUST wait
		//      for next Tick, otherwise we'd decrement PendingBlocks without
		//      advancing addr, so the next block's data overwrites this block's slot.
		//      Observed in RE2 gameplay as "first column of background shifted up
		//      by 1 MB".
		//
		//   B) Game over-asked: MDEC is Idle (cmd1 fully decoded, all output already
		//      drained) but game's DMA1 has more blocks pending. Real PSX would
		//      deadlock here too, but we accept the empty block so the channel can
		//      complete (matches pre-fix lenient behaviour that RE2 FMV depends on
		//      e.g. cmd1 #1 with ~26K words of MDEC output but game asks for ~35K).
		//
		// Distinguish by checking MDEC state. Decoding -> wait. Idle -> accept.
		//
		// EXTENDED 2026-05-15 (THEORY.md option 3): DMA0-aware scenario B.
		// IsDecoding alone misses two cases where "more bitstream is on the way":
		//   - MDEC's input FIFO still has halfwords queued (Execute() hasn't run)
		//   - DMA0 (RAM->MDEC) has PendingBlocks > 0 (game is mid-feeding next cmd1)
		// In both cases, MDEC will resume decoding momentarily and produce output.
		// Falling through to scenario B here corrupts the frame because the game's
		// DMA1 batch swallows the empty blocks before the next cmd1's output lands.
		if (got == 0)
		{
			bool moreBitstreamComing =
				_psx.Mdec.IsDecoding
				|| _psx.Mdec.InFifoCount > 0
				|| Channels[0].PendingBlocks > 0;
			if (moreBitstreamComing)
				return false; // scenario A: wait for MDEC to produce more
			// scenario B: MDEC is done AND no input pending AND no DMA0 incoming.
			// Game really did over-ask, accept empty block so DMA1 can complete.
			// Don't advance addr (no data was written).
			ch.PendingBlocks--;
			if (ch.PendingBlocks == 0)
			{
				ch.Chcr &= ~0x01000000u;
				_psx.Mdec.OnDmaOutComplete();
				uint irqEn = (_dicr >> 17) & 1;
				if (irqEn != 0)
				{
					_dicr |= (uint)(1 << 25);
					CheckDmaIrq();
				}
			}
			return true;
		}

		var ram = _psx.Memory.Ram;
		int ramLen = ram.Length;
		for (int i = 0; i < got; i++)
		{
			if ((int)addr + 3 < ramLen)
			{
				uint w = buf[i];
				ram[addr]     = (byte)w;
				ram[addr + 1] = (byte)(w >> 8);
				ram[addr + 2] = (byte)(w >> 16);
				ram[addr + 3] = (byte)(w >> 24);
			}
			addr = (addr + 4) & 0x001FFFFC;
		}
		ch.Madr = addr;
		ch.PendingBlocks--;
		// Per-block charge re-enabled (see TransferOneMdecInBlock
		// for the rationale). Uses `got` not BlockSize, scenario A returned
		// earlier with no charge, scenario B accepted an empty block with `got=0`.
		ChargeBlockCycles((uint)got);

		if (ch.PendingBlocks == 0)
		{
			// Last block: finalise the channel and fire the IRQ if armed.
			ch.Chcr &= ~0x01000000u;
			_psx.Mdec.OnDmaOutComplete();
			// Channel-1 (MDECout) IRQ enable lives at DICR bit 17; flag at bit 25.
			uint irqEnable = (_dicr >> 17) & 1;
			if (irqEnable != 0)
			{
				_dicr |= (uint)(1 << 25);
				CheckDmaIrq();
			}
		}
		return true;
	}

	// OTC: Fill RAM region with a backwards-linked list (used for ordering tables)
	private void DoOtcTransfer(DmaChannel ch)
	{
		long perfStart = PsxPerfMonitor.Stamp();
		uint addr = ch.Madr & 0x1FFFFC;
		uint count = ch.Bcr & 0xFFFF;
		if (count == 0) count = 0x10000;

		for (uint i = 0; i < count - 1; i++)
		{
			uint link = (addr - 4) & 0x00FFFFFC;
			_psx.Memory.WriteWord(addr, link);
			addr = link;
		}
		// Terminate the list
		_psx.Memory.WriteWord(addr, 0x00FFFFFF);

		ch.Madr = addr;
		ch.Chcr &= ~0x01000000u; // clear start bit
		// Models the "DRAM Hyper Page Mode" rate, DRAM rows access at 1
		// clock/word, plus ~1 extra clock per 16-word row for row-address
		// loading and refresh.
		int ticks = (int)count + ((int)count + 15) / 16;
		_psx.AdvanceClock(ticks);
		_psx.Perf.AddTicks(PsxPerfSection.DmaOtc, PsxPerfMonitor.Stamp() - perfStart);
	}

	// GPU DMA: RAM -> GPU (linked list mode or slice mode)
	private void DoGpuTransfer(DmaChannel ch, uint syncMode)
	{
		long perfStart = PsxPerfMonitor.Stamp();
		int dmaTicks = 0;
		// New model:
		//   - Linked list: per entry, 8 ticks header read + (5 + N + ceil(N/16))
		//     if word_count > 0. Empty entries still cost 8 (just the header).
		//   - Manual/Request: N + ceil(N/16) for the word transfer (no header
		//     overhead, single contiguous block).
		//
		// Linked-list mode is now slice-paced via TryTransferGpuLinkedList,
		// DoGpuTransfer's linked-list branch just kicks off the first slice;
		// HaltTransfer + UnhaltTransfer own resumption.
		// Burst/slice (manual) modes stay all-at-once.

		if (syncMode == 2) // linked list
		{
			// Initial slice runs synchronously inside this CHCR
			// write step (matches the legacy "first transfer starts now"
			// pattern for the other slice-paced channels). Subsequent
			// slices fire from the unhalt event if the list didn't
			// complete in one slice. Per-slice tick accumulation happens
			// inside TryTransferGpuLinkedList via AdvanceClock; we skip
			// the local dmaTicks/AdvanceClock for the linked-list path
			// to avoid double-counting.
			TryTransferGpuLinkedList(ch);
			_psx.Perf.AddTicks(PsxPerfSection.DmaGpu, PsxPerfMonitor.Stamp() - perfStart);
			return;
		}
		else // burst/slice
		{
			uint addr = ch.Madr & 0x001FFFFC;
			uint bs = ch.Bcr & 0xFFFF;
			uint bc = ch.Bcr >> 16;
			uint count = (syncMode == 0) ? bs : bs * bc;
			for (uint i = 0; i < count; i++)
			{
				uint cmd = _psx.Memory.ReadWord(addr);
				_psx.Gpu.WriteGp0(cmd);
				addr = (addr + 4) & 0x001FFFFC;
			}
			ch.Madr = addr; // update MADR to point past last transferred word
			dmaTicks = (int)count + ((int)count + 15) / 16;
		}
		ch.Chcr &= ~0x01000000u;
		_psx.AdvanceClock(dmaTicks);
		_psx.Perf.AddTicks(PsxPerfSection.DmaGpu, PsxPerfMonitor.Stamp() - perfStart);
	}

	// GPU DMA: GPU -> RAM (VRAM readback)
	private void DoGpuReadTransfer(DmaChannel ch)
	{
		long perfStart = PsxPerfMonitor.Stamp();
		uint addr = ch.Madr & 0x001FFFFC;
		uint bs = ch.Bcr & 0xFFFF;
		uint bc = ch.Bcr >> 16;
		uint count = bs * bc;
		for (uint i = 0; i < count; i++)
		{
			uint data = _psx.Gpu.ReadGpuData();
			_psx.Memory.WriteWord(addr, data);
			addr = (addr + 4) & 0x001FFFFC;
		}
		ch.Madr = addr; // update MADR to point past last transferred word
		ch.Chcr &= ~0x01000000u;
		_psx.AdvanceClock((int)count);
		_psx.Perf.AddTicks(PsxPerfSection.DmaGpuRead, PsxPerfMonitor.Stamp() - perfStart);
	}

	// CDROM DMA: CDROM -> RAM. Slice-paced (Theory M-extended for SH):
	// transfer ONE block per call, with CPU running between blocks via TickPendingBlocks.
	// Same pattern as DMA1/DMA0. Real PSX DMA3 is paced by the CDROM bus and the CPU
	// runs in parallel; all-at-once would starve the game's CPU during sector reads.
	private void DoCdromTransfer(DmaChannel ch)
	{
		uint bs = ch.Bcr & 0xFFFF;
		uint bc = ch.Bcr >> 16;
		ch.BlockSize = bs == 0 ? 0x10000u : bs;
		ch.PendingBlocks = bc == 0 ? 1u : bc;

		uint totalCount = ch.BlockSize * ch.PendingBlocks;
		PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
			$"[DMA3/CDROM] CDROM->RAM start: total={totalCount}w blockSize={ch.BlockSize} blockCount={ch.PendingBlocks} dest=0x{(ch.Madr & 0x001FFFFC):X6}");

		// See DoMdecInTransfer comment.
		if (CanTransferChannel(3, ignoreHalt: false))
			TryTransferChannel(3);
	}

	/// <summary>
	/// Move one block (<c>ch.BlockSize</c> words) from CDROM data FIFO to RAM.
	/// On the last block, clears CHCR.enable_busy and fires the channel-3 DMA IRQ.
	/// Returns <c>true</c> always (CDROM ReadByte returns 0 on drained-buffer
	/// rather than stalling, so this method always decrements PendingBlocks).
	/// </summary>
	private bool TransferOneCdromBlock(DmaChannel ch)
	{
		long perfStart = PsxPerfMonitor.Stamp();
		uint addr = ch.Madr & 0x001FFFFC;
		uint blockSize = ch.BlockSize;
		for (uint i = 0; i < blockSize; i++)
		{
			byte b0 = _psx.Cdrom.ReadByte(2);
			byte b1 = _psx.Cdrom.ReadByte(2);
			byte b2 = _psx.Cdrom.ReadByte(2);
			byte b3 = _psx.Cdrom.ReadByte(2);
			uint word = b0 | ((uint)b1 << 8) | ((uint)b2 << 16) | ((uint)b3 << 24);
			_psx.Memory.WriteWord(addr, word);
			addr = (addr + 4) & 0x001FFFFC;
		}
		ch.Madr = addr;
		ch.PendingBlocks--;

		// Was an inline `Cpu.Cycles += N + ceil(N/16)`,
		// now routes through ChargeBlockCycles for consistency with the
		// other slice-paced DMA channels (MDECin/MDECout/SPU all use it).
		// Behaviour identical to the previous inline computation.
		ChargeBlockCycles(blockSize);

		if (ch.PendingBlocks == 0)
		{
			ch.Chcr &= ~0x01000000u;
			// Channel-3 (CDROM) IRQ enable lives at DICR bit 19; flag at bit 27.
			uint irqEnable = (_dicr >> 19) & 1;
			if (irqEnable != 0)
			{
				_dicr |= (uint)(1 << 27);
				CheckDmaIrq();
			}
		}
		_psx.Perf.AddTicks(PsxPerfSection.DmaCdrom, PsxPerfMonitor.Stamp() - perfStart);
		return true;
	}

	// SPU DMA: RAM <-> SPU RAM. Slice-paced (Theory M-extended for SH):
	// transfer ONE block per call, with CPU running between blocks. Real PSX SPU
	// DMA is paced by the SPU bus; all-at-once would starve the game's audio
	// state machine that depends on parallel CPU work between DMA setup and IRQ.
	//
	// Stores toRam direction in the high bit of BlockSize (sentinel; OK because
	// block sizes are 16-bit values). Per-channel state is otherwise small enough.
	private bool _ch4ToRam;
	private void DoSpuTransfer(DmaChannel ch, bool toRam)
	{
		uint bs = ch.Bcr & 0xFFFF;
		uint bc = ch.Bcr >> 16;
		ch.BlockSize = bs == 0 ? 0x10000u : bs;
		ch.PendingBlocks = bc == 0 ? 1u : bc;
		_ch4ToRam = toRam;

		uint totalCount = ch.BlockSize * ch.PendingBlocks;
		PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
			$"[DMA4/SPU] start: toRam={toRam} total={totalCount}w blockSize={ch.BlockSize} blockCount={ch.PendingBlocks} madr=0x{(ch.Madr & 0x001FFFFC):X6}");

		// Pace the SPU DMA like hardware: TRANSFER_TICKS_PER_HALFWORD (16) per
		// halfword = 32 per word. The data still moves in TransferOneSpuBlock, but
		// the completion (CHCR-busy clear + IRQ) is deferred by this long via
		// _spuDmaEvent, so the transfer isn't instant and code runs between blocks in
		// sync mode 1 (ps1-tests spu/memory-transfer testDMA*Timing). Channel 4 only,
		// FMV rides CD/MDEC DMA, so this can't perturb movie timing.
		_spuDmaDelayTicks = (int)System.Math.Min((long)totalCount * 32, 1_000_000L);

		// See DoMdecInTransfer comment.
		if (CanTransferChannel(4, ignoreHalt: false))
			TryTransferChannel(4);
	}

	/// <summary>
	/// Move one block (<c>ch.BlockSize</c> words) between RAM and SPU. Direction
	/// stored in <c>_ch4ToRam</c> from the initial DoSpuTransfer call.
	/// Returns <c>true</c> always (SPU side doesn't stall mid-block).
	/// </summary>
	private bool TransferOneSpuBlock(DmaChannel ch)
	{
		long perfStart = PsxPerfMonitor.Stamp();
		uint addr = ch.Madr & 0x001FFFFC;
		uint blockSize = ch.BlockSize;

		if (!_ch4ToRam)
		{
			// RAM -> SPU: read block from RAM, hand to SPU
			var buf = EnsureDmaBuf(blockSize);
			for (uint i = 0; i < blockSize; i++)
			{
				buf[i] = _psx.Memory.ReadWord(addr);
				addr = (addr + 4) & 0x001FFFFC;
			}
			_psx.Spu.DmaWrite(buf, (int)blockSize);
		}
		else
		{
			// SPU -> RAM: read block from SPU RAM, write to main RAM.
			var buf = EnsureDmaBuf(blockSize);
			_psx.Spu.DmaRead(buf, (int)blockSize);
			for (uint i = 0; i < blockSize; i++)
			{
				_psx.Memory.WriteWord(addr, buf[i]);
				addr = (addr + 4) & 0x001FFFFC;
			}
		}

		ch.Madr = addr;
		ch.PendingBlocks--;
		// Per-block charge re-enabled along with DMA0/1.
		// See TransferOneMdecInBlock for the rationale on why this is safe now.
		ChargeBlockCycles(blockSize);

		if (ch.PendingBlocks == 0)
		{
			// Defer completion (CHCR-busy clear + IRQ) by the transfer duration so the
			// DMA isn't instant, see DoSpuTransfer. CHCR busy stays set until
			// _spuDmaEvent fires OnSpuDmaComplete. Deactivate first so a rare
			// back-to-back re-kick cleanly re-arms instead of double-scheduling.
			_spuDmaEvent.Deactivate();
			_spuDmaEvent.SetIntervalAndSchedule(_spuDmaDelayTicks > 0 ? _spuDmaDelayTicks : 1);
		}
		_psx.Perf.AddTicks(PsxPerfSection.DmaSpu, PsxPerfMonitor.Stamp() - perfStart);
		return true;
	}

	/// <summary>
	/// Deferred SPU-DMA (channel 4) completion, fires _spuDmaDelayTicks after the
	/// data moved, so the transfer takes realistic time.
	/// Clears CHCR busy and raises the channel-4 DMA IRQ.
	/// </summary>
	private void OnSpuDmaComplete()
	{
		_spuDmaEvent.Deactivate();
		var ch = Channels[4];
		ch.Chcr &= ~0x01000000u;
		// Channel-4 (SPU) IRQ enable lives at DICR bit 20; flag at bit 28.
		uint irqEnable = (_dicr >> 20) & 1;
		if (irqEnable != 0)
		{
			_dicr |= (uint)(1 << 28);
			CheckDmaIrq();
		}
	}
}

public class DmaChannel
{
	public uint Madr; // Memory address
	public uint Bcr;  // Block count / size
	public uint Chcr; // Channel control

	// Slice-paced transfer state (DMA1 / MDECout only, for now).
	// Theory C: DMA1 IRQ should fire AFTER the slice transfer completes
	// asynchronously, not synchronously with the game's CHCR write. All-at-once
	// transfer fires IRQ "too soon", game's CPU work between CHCR write and ISR
	// (which would happen during the real PSX async DMA time) is skipped.
	public uint PendingBlocks; // remaining blocks for slice-paced transfers
	public uint BlockSize;     // words per block (snapshot of Bcr & 0xFFFF)

	// Bus-arbitration request signal.
	// Real PSX DMA channels physically stall when the peripheral hasn't asserted its
	// request line, DMA only transfers a word when BOTH the OS allows the
	// channel (DPCR master enable) AND the device says "I have data" /
	// "I can accept data" (request asserted). Peripherals (CDROM, MDEC,
	// SPU) toggle this via PsxDmaController.SetRequest as their internal
	// state changes. Default is `true` so channels we haven't wired up yet
	// behave exactly like before this refactor.
	public bool Request = true;

	public void Reset()
	{
		Madr = 0;
		Bcr = 0;
		Chcr = 0;
		PendingBlocks = 0;
		BlockSize = 0;
		Request = true;
	}
}
