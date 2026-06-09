namespace PSXEmu;

/// <summary>
/// Main PSX system orchestrator.
/// Owns all hardware subsystems and drives the emulation loop.
/// (Save-state serialization lives in the partial in PsxSaveState.cs.)
/// </summary>
public partial class Psx
{
	public PsxPerfMonitor Perf { get; } = new();
	public MipsCore Cpu { get; private set; }
	public PsxMemory Memory { get; private set; }
	public PsxGpu Gpu { get; private set; }
	public PsxSpu Spu { get; private set; }
	public PsxGte Gte { get; private set; }
	public PsxMdec Mdec { get; private set; }
	public PsxCdrom Cdrom { get; private set; }
	public PsxDmaController Dma { get; private set; }
	public PsxTimerController Timers { get; private set; }
	public PsxInterruptController Interrupts { get; private set; }
	public PsxController Controller { get; private set; }
	public PsxMemoryCard MemCard => Controller.MemCard;

	// Event-scheduler: drives all peripheral timing. Each peripheral owns
	// one or more TimingEvent instances scheduled on the sorted linked
	// list; the CPU's Run loop exits when an event becomes due, the
	// scheduler dispatches all due events, then the CPU resumes. See
	// REFACTOR_PLAN.md for the full lineage.
	public EventScheduler Scheduler { get; private set; }

	// Deterministic state-dump facility for cross-run /
	// cross-emulator comparison. Disabled by default; call
	// Trace.Enable(path) to start a trace. See EmulatorTrace.cs.
	public EmulatorTrace Trace { get; private set; }

	public bool IsRunning { get; set; }
	public long TotalCycles { get; private set; }
	public long FrameCount { get; private set; }
	// Fixed frame-clock accumulator: the CPU-cycle position where each frame's budget
	// BEGINS, advanced by exactly CpuClocksPerFrame/frame, NOT by Cpu.Cycles, which
	// drifts ahead when async DMA (e.g. GPU render in VBlank) charges its cost on top of
	// the CPU run. Running the CPU from its (possibly-ahead) Cycles up to this fixed
	// target turns that DMA over-charge into skipped already-elapsed instructions at the
	// START of the next frame, so the VBlank/DMA code at the END of each frame still
	// runs, and GlobalTick advances exactly CpuClocksPerFrame/frame on average (DMA
	// stalls the CPU as on real HW, instead of running ~8% fast during CD streaming,
	// which over-fed XA -> dialogue cuts).
	private long _frameClock;
	public PsxConstants.VideoStandard VideoStandard => Gpu.VideoStandard;
	public int CpuClocksPerFrame => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.CpuClocksPerFramePAL : PsxConstants.CpuClocksPerFrameNTSC;
	public int LinesPerFrame => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.LinesPerFramePAL : PsxConstants.LinesPerFrameNTSC;
	public int VisibleLines => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.VisibleLinesPAL : PsxConstants.VisibleLinesNTSC;
	public int CpuClocksPerLine => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.CpuClocksPerLinePAL : PsxConstants.CpuClocksPerLineNTSC;
	public double FrameTime => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.FrameTimePAL : PsxConstants.FrameTimeNTSC;
	public int TargetSpuSamplesPerFrame => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.SpuSamplesPerFramePAL : PsxConstants.SpuSamplesPerFrameNTSC;

	// Real-time emulated fps measurement
	private readonly System.Diagnostics.Stopwatch _fpsSw = System.Diagnostics.Stopwatch.StartNew();
	private long _fpsFrameAccum;
	public double EmulatedFps { get; private set; }
	public double TargetFps => VideoStandard == PsxConstants.VideoStandard.PAL ? PsxConstants.FrameRatePAL : PsxConstants.FrameRateNTSC;

	public Psx()
	{
		// Scheduler first, peripherals will schedule events into it.
		Scheduler = new EventScheduler();
		EventScheduler.Default = Scheduler;
		// Wire CPU hooks: the scheduler needs to read CPU's pending ticks and
		// adjust its downcount. These closures capture `this.Cpu` so they
		// follow the (re-assigned) MipsCore instance after Reset.
		Scheduler.CpuPendingTicksGetter = () => Cpu?.PendingTicks ?? 0;
		Scheduler.CpuPendingTicksResetter = () => { if (Cpu != null) Cpu.PendingTicks = 0; };
		Scheduler.CpuDowncountSetter = (n) => { if (Cpu != null) Cpu.Downcount = n; };
		// Reported as "interrupt-pending" only when the CPU will actually
		// dispatch it: a raw IStat/IMask hit while SR.IEc=0 (critical
		// section) would otherwise leave Downcount permanently forced to 0,
		// hanging the dispatch loop because CheckIrq inside Cpu.Run also
		// gates on SR.IEc. See MipsCore.CheckIrq.
		Scheduler.CpuHasPendingInterruptGetter = () =>
			Cpu != null && Interrupts != null &&
			Interrupts.IrqPending &&
			(Cpu.Sr & PsxConstants.SrIec) != 0;

		Memory = new PsxMemory(this);
		Cpu = new MipsCore(this);
		Gpu = new PsxGpu(this);
		Spu = new PsxSpu(this);
		Gte = new PsxGte();
		Mdec = new PsxMdec(this);
		Cdrom = new PsxCdrom(this);
		Dma = new PsxDmaController(this);
		Timers = new PsxTimerController(this);
		Interrupts = new PsxInterruptController(this);
		Trace = new EmulatorTrace(this);
		Controller = new PsxController(this);
	}

	/// <summary>
	/// Called before the Psx instance is discarded.
	/// Flushes any unsaved memory card data to disk.
	/// </summary>
	public void Shutdown()
	{
		MemCard.Flush();
		Trace?.Disable();
	}

	public void LoadBios(byte[] data)
	{
		Memory.LoadBios(data);
		PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"BIOS loaded ({data.Length} bytes)");
	}

	public void LoadDisc(byte[] binData, PsxCdrom.DiscTrack[] tracks = null)
	{
		Cdrom.LoadDisc(binData, tracks);
	}

	// PS-X EXE side-loading: stashed on launch; the CPU injects it when the BIOS
	// reaches the shell entry (0x80030000), fast-booting past the disc check.
	// Used to run raw PS-EXE programs (the ps1-tests suite) with no disc.
	public byte[] PendingExe { get; set; }
	public void LoadExe(byte[] exe) => PendingExe = exe;

	public void Reset()
	{
		// Reset scheduler FIRST so peripherals can schedule events into a
		// clean state. Detaches any leftover events from a previous run.
		Scheduler.Reset();

		Memory.Reset();
		Cpu.Reset();
		Gpu.Reset();
		Spu.Reset();
		Gte.Reset();
		// IMPORTANT: Dma.Reset() must run BEFORE Mdec.Reset() and Cdrom.Reset().
		// DmaChannel.Reset() defaults Request=true on every channel; the peripheral
		// resets that follow then call SetRequest(false) to establish the correct
		// boot state (CDROM has no sector ready, MDEC enable bits are off).
		// Reversing the order would leave DMA0/DMA1/DMA3 in the wrong state until
		// the first peripheral state change.
		Dma.Reset();
		Mdec.Reset();
		Cdrom.Reset();
		Timers.Reset();
		Interrupts.Reset();
		Controller.Reset();

		// Each peripheral's Reset() above schedules its own
		// events (or leaves them deactivated when nothing is pending). No
		// global "tick everything" bridge is needed any more.

		IsRunning = true;
		TotalCycles = 0;
		FrameCount = 0;
		_frameClock = Cpu.Cycles;
		_fpsFrameAccum = 0;
		EmulatedFps = 0;
		_fpsSw.Restart();

		PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, "PSX reset");
	}

	/// <summary>
	/// Run exactly one video frame using the active video standard.
	/// Drives CPU, timers, CDROM, SPU, and GPU VBlank in a scanline loop.
	/// </summary>
	public void RunFrame()
	{
		if (!IsRunning) return;

		// Update real-time emulated fps counter (once per second)
		_fpsFrameAccum++;
		long fpsElapsed = _fpsSw.ElapsedMilliseconds;
		if (fpsElapsed >= 1000)
		{
			EmulatedFps = _fpsFrameAccum * 1000.0 / fpsElapsed;
			_fpsFrameAccum = 0;
			_fpsSw.Restart();
		}

		long frameStart = PsxPerfMonitor.Stamp();
		Spu.BeginFrame();

		// Resync the fixed frame clock to the CPU if they've drifted more than a frame
		// apart in either direction, happens after a save-state load (_frameClock isn't
		// serialized) or a single DMA larger than a whole frame. Normal play keeps them
		// within one frame (the DMA overshoot), so this is otherwise a no-op.
		if (Cpu.Cycles > _frameClock + CpuClocksPerFrame || Cpu.Cycles < _frameClock - CpuClocksPerFrame)
			_frameClock = Cpu.Cycles;
		// Frame budget runs from the FIXED frame-clock accumulator (see _frameClock), not
		// from Cpu.Cycles. Cpu.Cycles may be ahead because last frame's async DMA charged
		// its cost past the budget; running the CPU from there up to this fixed target
		// makes that over-charge repay as skipped already-elapsed instructions at the
		// start of THIS frame, while the VBlank/DMA code at the end still runs.
		long frameBase = _frameClock;
		long frameEnd = frameBase + CpuClocksPerFrame;
		_frameClock = frameEnd;
		long cpuRunTicks = 0;
		long peripheralTicks = 0;
		long vblankTicks = 0;

		// VBlank ends as the new frame's first visible line begins. Drop the
		// VBlank gate on Timer 1 so games using `sync_enable=1, sync_mode=0`
		// (PauseWhileGateActive) resume counting HBlanks; or, for sync_mode=1
		// (ResetOnGateEnd), the timer counter latches to 0 here so the
		// FMV-pacing pattern "lines drawn this frame" reads correctly.
		Timers.SetGate(1, false);

		for (int line = 0; line < LinesPerFrame; line++)
		{
			long lineBase = frameBase + (long)line * CpuClocksPerLine;
			long lineEnd = lineBase + CpuClocksPerLine;

			// VBlank starts at the first non-visible line. Raise the Timer 1
			// gate BEFORE running this line's CPU + HBlank so the HBlank tick
			// at the end of this scanline is gated off (we're now in VBlank).
			if (line == VisibleLines)
				Timers.SetGate(1, true);

			// Run CPU for the visible portion of the line
			RunCpuTo(Math.Min(lineEnd, frameEnd), ref cpuRunTicks, ref peripheralTicks);

			// HBlank: tick Timer 1 by 1 if it uses HBlank as clock source
			// (gated by Timers.CountingEnabled[1] internally, no tick during VBlank).
			Timers.OnHBlank();

			// HBlank / VBlank events
			if (line == VisibleLines) // End of visible area -> VBlank
			{
				long vblankStart = PsxPerfMonitor.Stamp();
				Gpu.OnVBlank();
				vblankTicks += PsxPerfMonitor.Stamp() - vblankStart;
			}
		}

		// If the CPU finished early (idle/halt with nothing scheduled), snap up to
		// frameEnd so the fixed _frameClock target stays aligned. If async DMA overshot
		// frameEnd, leave Cpu.Cycles ahead, next frame's fixed budget turns that into
		// skipped early instructions (the DMA "stall"), keeping the clock rate exact.
		if (Cpu.Cycles < frameEnd)
			Cpu.Cycles = frameEnd;

		TotalCycles += CpuClocksPerFrame;
		FrameCount++;

		// Dump emulator state to the trace file (no-op when trace is disabled, which is the default).
		Trace.OnFrameEnd();

		Perf.AddTicks(PsxPerfSection.PsxFrameCpuRun, cpuRunTicks);
		Perf.AddTicks(PsxPerfSection.PsxFramePeripherals, peripheralTicks);
		Perf.AddTicks(PsxPerfSection.PsxFrameVBlank, vblankTicks);
		Perf.AddTicks(PsxPerfSection.EmuRunFrame, PsxPerfMonitor.Stamp() - frameStart);
	}

	// Per-peripheral CyclesUntilNextEvent() helpers have been removed where
	// they had no internal callers; CDROM/Controller keep them PRIVATE for
	// their own RescheduleEvent. The scheduler's UpdateCpuDowncount
	// derives the CPU's next-exit deadline from the head of its sorted
	// event list, no per-peripheral polling needed.

	/// <summary>
	/// Event-scheduler-driven dispatch loop.
	///
	/// Cpu.Run exits when EITHER:
	///   - <c>Cycles</c> reaches <paramref name="target"/> (frame boundary), OR
	///   - <c>PendingTicks &gt;= Downcount</c> (an event came due)
	///
	/// After Cpu.Run returns we call Scheduler.RunEvents() which advances
	/// the global tick counter, fires any due peripheral events (per-Timer,
	/// SPU sample, CDROM countdowns, MDEC block copy-out, DMA block drip,
	/// Controller transfer/ACK), and updates Downcount for the next CPU pass.
	/// </summary>
	private void RunCpuTo(long target, ref long cpuRunTicks, ref long peripheralTicks)
	{
		while (Cpu.Cycles < target && !Cpu.CrashDetected)
		{
			if (Cpu.Halted)
			{
				// Halted: skip CPU work, but events still fire on schedule.
				// Snap Cpu.Cycles forward to target and feed the scheduler the
				// skipped ticks so any pending peripheral deadlines that fall
				// inside this window get dispatched on the next RunEvents.
				int skipped = (int)Math.Min(target - Cpu.Cycles, int.MaxValue);
				Cpu.Cycles = target;
				Cpu.PendingTicks += skipped;  // feed scheduler so events fire
				long periphStart = PsxPerfMonitor.Stamp();
				Scheduler.RunEvents();
				peripheralTicks += PsxPerfMonitor.Stamp() - periphStart;
				break;
			}
			
			// MipsCore.Run uses Cpu.PendingTicks vs Cpu.Downcount as its exit
			// condition; we set Downcount once via Scheduler.UpdateCpuDowncount.
			// The frame-target check still applies via Cycles < target in Run().
			long cpuStart = PsxPerfMonitor.Stamp();
			Cpu.Run(target);
			cpuRunTicks += PsxPerfMonitor.Stamp() - cpuStart;

			// Run any events that became due during the CPU batch. This
			// commits PendingTicks to GlobalTickCounter and dispatches.
			long periphStart2 = PsxPerfMonitor.Stamp();
			Scheduler.RunEvents();
			peripheralTicks += PsxPerfMonitor.Stamp() - periphStart2;
		}

		// Commit any leftover pending ticks at frame boundary so events
		// don't carry over to the next frame with stale time.
		Scheduler.CommitLeftoverTicks();
	}

	public void AdvanceClock(int cycles)
	{
		Cpu.Cycles += cycles;
	}
}
