namespace PSXEmu;

/// <summary>
/// PSX Timer system - 3 independent 16-bit timers.
/// Timer 0: dot clock or system clock
/// Timer 1: hblank or system clock
/// Timer 2: system clock / 8 or system clock
/// </summary>
public class PsxTimer
{
	public ushort Counter;
	public ushort Target;
	public ushort Mode;

	// Derived from mode register
	// Mode bit 0: sync_enable. When 1, the timer's gate (HBlank for T0,
	// VBlank for T1; T2 has no gate) interacts with the counter according
	// to `SyncMode`. When 0, the timer free-runs as before.
	public bool SyncEnable   => (Mode & 0x0001) != 0;
	// Mode bits 1-2: sync_mode.
	//   0 = PauseWhileGateActive   : counter pauses while gate is true
	//   1 = ResetOnGateEnd         : counter resets to 0 at gate falling edge
	//   2 = ResetAndRunOnGateStart : counter resets to 0 at gate rising edge,
	//                                 only counts while gate is true
	//   3 = FreeRunOnGateEnd       : counts only while gate is true; on the
	//                                 first rising gate edge, sync_enable
	//                                 auto-clears so the timer free-runs
	public int SyncMode      => (Mode >> 1) & 0x3;
	public bool UseTarget    => (Mode & 0x0008) != 0;
	public bool IrqAtTarget  => (Mode & 0x0010) != 0;
	public bool IrqAtMax     => (Mode & 0x0020) != 0;
	// Mode bit 6 (irq_repeat): 0 = one-shot (fire IRQ once, then suppress until
	// mode register is rewritten); 1 = repeat (fire IRQ on every match).
	// We previously ignored bit 6 entirely and always fired on every match;
	// one-shot timers used by the BIOS scheduler for delayed callbacks were thus getting flooded with spurious repeats.
	public bool IrqRepeat    => (Mode & 0x0040) != 0;
	// Mode bit 7 (irq_pulse_n): 0 = pulse (fire IRQ once briefly), 1 = toggle
	// (toggle bit 10 on each match, used for some game-specific IRQ patterns).
	public bool IrqToggleMode => (Mode & 0x0080) != 0;
	public bool ReachedTarget;
	public bool ReachedMax;
	// Sticky one-shot flag for pulse-mode IRQs. Set when the IRQ fires; only
	// cleared when the mode register is rewritten (= timer reset). In repeat
	// mode this flag is set but ignored.
	public bool IrqDone;

	// Gate state from the CRTC. T0 gate = HBlank, T1 gate = VBlank, T2 = N/A.
	// Updated by `PsxTimerController.SetGate(idx, state)` from the GPU's
	// scanline loop. Combined with `SyncEnable`/`SyncMode` to derive
	// `CountingEnabled`, which Tick/OnHBlank consult before incrementing.
	public bool Gate;
	// Computed gate-aware "is this timer currently allowed to count?" flag.
	// True for free-running (sync disabled) timers; otherwise depends on the
	// gate + sync mode combination. Recomputed in `UpdateCountingEnabled`
	// any time gate / sync_enable / sync_mode changes.
	public bool CountingEnabled = true;

	// Fractional accumulator for clock division
	public int Frac;

	// Per-timer scheduler event. Fires at the exact CPU
	// cycle the next IRQ deadline (target match or 0xFFFF wrap) would hit.
	// Deactivated when no IRQ is armed or when the timer is gate-paused.
	// Counter reads InvokeEarly this event to sync the counter to "now"
	// without needing the LegacyTick 256-cycle bridge.
	public TimingEvent Event;
}

public class PsxTimerController
{
	private readonly Psx _psx;
	public readonly PsxTimer[] Timers = new PsxTimer[3];

	public PsxTimerController(Psx psx)
	{
		_psx = psx;
		for (int i = 0; i < 3; i++)
		{
			Timers[i] = new PsxTimer();
			// Capture i by value into the callback param so all three events
			// don't share the same closure idx (classic foreach-capture trap).
			int idx = i;
			Timers[i].Event = new TimingEvent(
				$"Timer{i}", int.MaxValue, int.MaxValue,
				(param, ticksToExecute, ticksLate) => OnTimerEvent((int)param, ticksToExecute, ticksLate),
				idx);
		}
	}

	public void Reset()
	{
		foreach (var t in Timers)
		{
			t.Counter = 0;
			t.Target = 0;
			t.Mode = 0;
			t.Frac = 0;
			t.ReachedTarget = false;
			t.ReachedMax = false;
			t.IrqDone = false;
			t.Gate = false;
			// Sync disabled by default -> counting_enabled = true (free-run).
			t.CountingEnabled = true;
		}

		// All timers boot with Mode=0 -> no IRQ enabled -> events stay
		// deactivated. They'll be Schedule()d when the BIOS / game writes a
		// mode register that enables IrqAtTarget or IrqAtMax.
		for (int i = 0; i < 3; i++)
			RescheduleTimer(i);
	}

	// ---- Save-state ---- (Sync*/UseTarget/etc. are computed from Mode, not stored.)
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		foreach (var t in Timers)
		{
			w.U16(t.Counter); w.U16(t.Target); w.U16(t.Mode);
			w.Bool(t.ReachedTarget); w.Bool(t.ReachedMax); w.Bool(t.IrqDone);
			w.Bool(t.Gate); w.Bool(t.CountingEnabled);
			w.S32(t.Frac);
			t.Event.SaveState(w, g);
		}
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		foreach (var t in Timers)
		{
			t.Counter = r.U16(); t.Target = r.U16(); t.Mode = r.U16();
			t.ReachedTarget = r.Bool(); t.ReachedMax = r.Bool(); t.IrqDone = r.Bool();
			t.Gate = r.Bool(); t.CountingEnabled = r.Bool();
			t.Frac = r.S32();
			t.Event.LoadState(r, g);
		}
	}

	/// <summary>
	/// Per-timer event callback. Called by the scheduler
	/// either when the next-IRQ deadline hits, or via <see cref="TimingEvent.InvokeEarly"/>
	/// from a Counter read that needs the fresh value. Advances the timer by
	/// <paramref name="ticksToExecute"/> CPU cycles, fires its IRQ on match,
	/// and re-schedules for the next deadline.
	/// </summary>
	private void OnTimerEvent(int idx, int ticksToExecute, int ticksLate)
	{
		if ((uint)idx >= 3) return;
		var t = Timers[idx];
		if (ticksToExecute <= 0) { RescheduleTimer(idx); return; }

		// Apply elapsed cycles using the per-timer clock source. Timer 1 in
		// HBlank-source mode does NOT advance from CPU cycles, its counter
		// ticks once per OnHBlank() call. In that case we still re-schedule
		// after the early-return so the event stays deactivated.
		if (idx == 1)
		{
			bool t1External = (t.Mode & 0x0100) != 0;
			if (t1External)
			{
				RescheduleTimer(idx);
				return;
			}
		}

		if (t.CountingEnabled)
		{
			GetClockRatio(idx, out int num, out int den);
			TickTimer(idx, ticksToExecute, num, den);
		}

		RescheduleTimer(idx);
	}

	/// <summary>
	/// Compute the next CPU-cycle deadline for a timer and (re-)schedule its
	/// event. Deactivates the event when nothing needs to fire (no IRQ
	/// armed, gate-paused, or HBlank-source Timer 1).
	/// </summary>
	private void RescheduleTimer(int idx)
	{
		var t = Timers[idx];
		// Gate-paused -> counter can't reach any deadline. Deactivate.
		if (!t.CountingEnabled)
		{
			t.Event.Deactivate();
			return;
		}
		// Timer 1 in HBlank-source mode: counter advances per scanline via
		// OnHBlank(), not per CPU cycle. No event needed.
		if (idx == 1 && (t.Mode & 0x0100) != 0)
		{
			t.Event.Deactivate();
			return;
		}
		// No IRQ deadline configured -> nothing to schedule. Counter still
		// advances correctly on read via InvokeEarly + OnTimerEvent.
		if (!t.IrqAtTarget && !t.IrqAtMax)
		{
			t.Event.Deactivate();
			return;
		}

		GetClockRatio(idx, out int num, out int den);
		int cycles = TimerCyclesUntilEvent(t, num, den);
		if (cycles == int.MaxValue)
		{
			t.Event.Deactivate();
			return;
		}
		if (cycles < 1) cycles = 1;
		t.Event.Schedule(cycles);
	}

	/// <summary>
	/// Updates the timer's gate state from the CRTC. Call from the GPU
	/// scanline loop:
	///   - `SetGate(0, in_hblank)` whenever the HBlank flag toggles
	///   - `SetGate(1, in_vblank)` whenever the VBlank flag toggles
	/// On gate edges, runs the per-`SyncMode` reset/disable logic and
	/// then recomputes `CountingEnabled` so Tick/OnHBlank gate properly.
	/// </summary>
	public void SetGate(int idx, bool state)
	{
		if ((uint)idx >= 3) return;
		var t = Timers[idx];
		if (t.Gate == state) return;
		// Sync the counter to "now" BEFORE the gate edge takes effect, so
		// the pre-edge ticks are credited to the pre-edge counting policy.
		// Without this, a gate transition that disables counting would lose
		// up to (CPU pending ticks)/divisor ticks of counter progress.
		t.Event?.InvokeEarly();
		t.Gate = state;

		// Sync disabled -> gate edges have no observable effect on this timer.
		// (CountingEnabled stays true, t.Counter is unchanged.)
		if (!t.SyncEnable) { RescheduleTimer(idx); return; }

		switch (t.SyncMode)
		{
			case 0: // PauseWhileGateActive : no counter manipulation; counting_enabled handles it
				break;

			case 1: // ResetOnGateEnd : counter resets at gate falling edge (entering "outside-gate" phase)
				if (!state) t.Counter = 0;
				break;

			case 2: // ResetAndRunOnGateStart : counter resets at gate rising edge; pause outside gate
				if (state) t.Counter = 0;
				break;

			case 3: // FreeRunOnGateEnd : once gate goes high, sync_enable auto-clears (timer free-runs forever after)
				if (state) t.Mode &= unchecked((ushort)~0x0001);
				break;
		}

		UpdateCountingEnabled(t);
		RescheduleTimer(idx);
	}

	/// <summary>
	/// Recomputes `CountingEnabled` from the current `SyncEnable`/`SyncMode`/`Gate` state.
	/// Must be called from <see cref="SetGate"/> AND from any path that mutates
	/// `Mode` (write to mode register, sync_enable auto-clear in mode 3).
	/// </summary>
	private static void UpdateCountingEnabled(PsxTimer t)
	{
		if (t.SyncEnable)
		{
			switch (t.SyncMode)
			{
				case 0: // PauseWhileGateActive
					t.CountingEnabled = !t.Gate;
					break;
				case 1: // ResetOnGateEnd
					t.CountingEnabled = true;
					break;
				case 2: // ResetAndRunOnGateStart
				case 3: // FreeRunOnGateEnd
					t.CountingEnabled = t.Gate;
					break;
				default:
					t.CountingEnabled = true;
					break;
			}
		}
		else
		{
			t.CountingEnabled = true;
		}
	}

	/// <summary>
	/// Called once per scanline (HBlank). Ticks Timer 1 by 1 when it uses HBlank as clock source.
	/// Gated by `CountingEnabled` so VBlank-paused timers don't accumulate scanline ticks
	/// during VBlank (the canonical "lines drawn this frame" FMV-pacing pattern).
	/// </summary>
	public void OnHBlank()
	{
		bool t1External = (Timers[1].Mode & 0x0100) != 0;
		if (t1External && Timers[1].CountingEnabled)
			TickTimer(1, 1, 1, 1);
	}

	/// <summary>
	/// Per-timer clock ratio: the counter advances <paramref name="num"/> ticks per
	/// <paramref name="den"/> CPU cycles. System clock = 1/1; System clock/8 = 1/8
	/// (Timer2, mode bit 9); Dot clock = 11/(7*hdiv) (Timer0, mode bit 8), the GPU
	/// clock is 11/7 x CPU, divided by the current H-resolution divider.
	/// </summary>
	private void GetClockRatio(int idx, out int num, out int den)
	{
		var t = Timers[idx];
		if (idx == 0 && (t.Mode & 0x0100) != 0)
		{
			num = 11;
			den = 7 * _psx.Gpu.DotClockDivider;
		}
		else if (idx == 2 && (t.Mode & 0x0200) != 0)
		{
			num = 1;
			den = 8;
		}
		else
		{
			num = 1;
			den = 1;
		}
	}

	private static int TimerCyclesUntilEvent(PsxTimer t, int num, int den)
	{
		// No IRQ enabled -> counter doesn't drive any event we care about scheduling for.
		bool irqAtTarget = t.IrqAtTarget;
		bool irqAtMax = t.IrqAtMax;
		if (!irqAtTarget && !irqAtMax) return int.MaxValue;

		int ticksRemaining = int.MaxValue;
		// Arm the target deadline whenever irq_at_target is set, INDEPENDENT of
		// reset_at_target (UseTarget). An irq_at_target timer without reset must
		// still be scheduled to fire at the target crossing each overflow cycle.
		if (irqAtTarget)
		{
			int delta = t.Target - t.Counter;
			if (delta > 0)
				ticksRemaining = Math.Min(ticksRemaining, delta);
			else if (delta == 0)
				ticksRemaining = Math.Min(ticksRemaining, 0xFFFF); // wrap full cycle
			else
				// Counter has overshot Target (e.g. game wrote a smaller Target
				// after Counter passed it). Counter must wrap at 0xFFFF, reset
				// to 0, then climb back to Target. Total ticks =
				// (0xFFFF - Counter) + Target. In the legacy polling model
				// this case was masked by the LegacyTick bridge still
				// advancing TickTimer every 256 cycles; with per-timer events
				// a return of int.MaxValue here would deactivate the event
				// permanently and the target IRQ would never fire.
				ticksRemaining = Math.Min(ticksRemaining, (0xFFFF - t.Counter) + t.Target);
		}
		if (irqAtMax)
		{
			int delta = 0xFFFF - t.Counter;
			if (delta > 0) ticksRemaining = Math.Min(ticksRemaining, delta);
			else ticksRemaining = Math.Min(ticksRemaining, 0xFFFF); // already at max -> full wrap to next
		}

		// Convert timer ticks to CPU cycles via the num/den clock ratio (the timer
		// advances `num` ticks per `den` CPU cycles). Subtract the partial Frac
		// accumulator (in numxcpu units) and round up.
		long cpuCycles = ((long)ticksRemaining * den - t.Frac + num - 1) / num;
		if (cpuCycles < 1) cpuCycles = 1;
		if (cpuCycles > int.MaxValue) cpuCycles = int.MaxValue;
		return (int)cpuCycles;
	}

	private void TickTimer(int idx, int cpuCycles, int num, int den)
	{
		var t = Timers[idx];

		t.Frac += cpuCycles * num;
		int ticks = t.Frac / den;
		t.Frac %= den;

		if (ticks == 0) return;

		uint irqBit = idx switch { 0 => PsxConstants.IrqTimer0, 1 => PsxConstants.IrqTimer1, _ => PsxConstants.IrqTimer2 };

		while (ticks > 0)
		{
			// The target is a step boundary whenever it drives an IRQ OR a
			// reset. So we must land on the target even when reset_at_target is off,
			// otherwise an irq_at_target-only timer would never fire its target IRQ.
			bool targetRelevant = t.UseTarget || t.IrqAtTarget;
			int step = ticks;
			int toTarget = t.Target - t.Counter;
			if (targetRelevant && toTarget > 0)
				step = Math.Min(step, toTarget);
			int toMax = 0xFFFF - t.Counter;
			if (toMax > 0)
				step = Math.Min(step, toMax);

			ushort oldCounter = t.Counter;
			t.Counter = (ushort)(t.Counter + step);
			ticks -= step;

			// Target crossing, EDGE-triggered (old < target <= new, or
			// target==0 which fires each step). Latch reached_target and fire
			// irq_at_target INDEPENDENT of reset_at_target; only RESET the
			// counter when reset_at_target (UseTarget) is set and target > 0.
			// ReachedTarget / ReachedMax are sticky, latched here, cleared only on a Mode-register READ,
			// so games polling Mode bit 11/12 for frame boundaries can observe them.
			if (targetRelevant && t.Counter >= t.Target &&
			    (oldCounter < t.Target || t.Target == 0))
			{
				t.ReachedTarget = true;
				if (t.IrqAtTarget)
					RequestIrq(t, irqBit);
				if (t.UseTarget && t.Target != 0)
					t.Counter = 0;
			}

			// Overflow, independent of the target branch: a non-resetting
			// target timer keeps climbing to 0xFFFF and wraps there
			if (t.Counter >= 0xFFFF)
			{
				t.ReachedMax = true;
				if (t.IrqAtMax)
					RequestIrq(t, irqBit);
				t.Counter = 0;
			}
		}
	}

	public uint ReadWord(uint offset)
	{
		int idx = (int)(offset >> 4);
		if (idx > 2) return 0;
		uint reg = (offset >> 2) & 3;
		var t = Timers[idx];
		// Sync the counter to "now" before any read. Without this, a counter
		// read returns the value AT THE LAST EVENT FIRE, up to (max
		// divisor) * (cycles since fire) stale.
		// Mode/Target reads also benefit because the sticky reached_target /
		// reached_max flags may have just been latched mid-window.
		t.Event?.InvokeEarly();
		return reg switch
		{
			0 => t.Counter,
			1 => ReadMode(idx),
			2 => t.Target,
			_ => 0,
		};
	}

	private void RequestIrq(PsxTimer t, uint irqBit)
	{
		// Pulse mode (bit 7 = 0): IRQ fires once unless bit 6 (irq_repeat) is
		// set. In one-shot mode, after the first fire `IrqDone` blocks further
		// raises until the game resets the timer by writing the mode register.
		// Toggle mode (bit 7 = 1): IRQ line follows the toggling of bit 10
		if (!t.IrqToggleMode)
		{
			// Pulse mode
			if (!t.IrqDone || t.IrqRepeat)
			{
				_psx.Interrupts.Raise(irqBit);
				t.Mode &= unchecked((ushort)~0x0400); // bit 10 = 0 (IRQ active)
			}
			t.IrqDone = true;
		}
		else
		{
			// Toggle mode, flip bit 10 every time the IRQ would fire.
			_psx.Interrupts.Raise(irqBit);
			t.Mode ^= 0x0400;
		}
	}

	private uint ReadMode(int idx)
	{
		var t = Timers[idx];
		uint val = t.Mode;
		if (t.ReachedTarget) val |= 0x0800;
		if (t.ReachedMax) val |= 0x1000;
		// Bit 10 (IRQ not yet requested) is tracked in t.Mode directly
		// Sticky flags 11/12 auto-clear on read (matches nocash spec).
		t.ReachedTarget = false;
		t.ReachedMax = false;
		return val;
	}

	public void WriteWord(uint offset, uint value)
	{
		int idx = (int)(offset >> 4);
		if (idx > 2) return;
		uint reg = (offset >> 2) & 3;
		var t = Timers[idx];
		// Sync to "now" so any in-flight counter progress is committed BEFORE
		// the write replaces the register. Without this, a write to Counter
		// could clobber ticks that should still have produced an IRQ.
		t.Event?.InvokeEarly();
		PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info,
			$"[TMR] Timer{idx} write reg={reg} val=0x{value:X4} (before: counter=0x{t.Counter:X4} mode=0x{t.Mode:X4} target=0x{t.Target:X4})");
		switch (reg)
		{
			case 0: t.Counter = (ushort)value; break;
			case 1:
				// Mode-register write mask:
				//   - Bits 11 (reached_target) and 12 (reached_overflow) are
				//     HARDWARE-ONLY sticky flags. The game can read them
				//     (which auto-clears as a side effect) but must not be
				//     able to set them via the RMW pattern `mode |= flag`,
				//     which carries the previously-read sticky bits back
				//     into the write value. Without this mask, any RMW
				//     that crosses a target/overflow event permanently
				//     stamps the bit into our stored `t.Mode`, leaking
				//     phantom "event happened" reads forever after.
				//   - Bit 10 (interrupt_request_n) is forced to 1 on mode
				//     write per nocash spec (= "no IRQ pending after arm").
				const uint MODE_WRITE_MASK = ~0x1800u;
				t.Mode = (ushort)((value & MODE_WRITE_MASK) | 0x0400);
				t.Counter = 0;
				t.ReachedTarget = false;
				t.ReachedMax = false;
				// Reset the one-shot lockout so the timer can fire its IRQ
				// again after the game arms it.
				t.IrqDone = false;
				// Recompute counting_enabled, sync_enable / sync_mode may
				// have changed. Without this a game arming sync_enable=1
				// while gate is currently set wouldn't take effect until the
				// next gate edge.
				UpdateCountingEnabled(t);
				break;
			case 2: t.Target = (ushort)value; break;
		}
		// Any write may have changed the next-IRQ deadline (counter / mode /
		// target / clock source / sync flags), re-arm the event.
		RescheduleTimer(idx);
	}

	public ushort ReadHalf(uint offset) => (ushort)ReadWord(offset);
	public void WriteHalf(uint offset, ushort value) => WriteWord(offset, (uint)value);
}
