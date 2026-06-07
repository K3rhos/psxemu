using System.Runtime.CompilerServices;

namespace PSXEmu;

/// <summary>
/// MIPS-I R3000A CPU core for the PlayStation 1.
/// 32 GPRs, HI/LO, COP0 (SR, CAUSE, EPC, BADVADDR).
/// Branch delay slots are handled via _branchTarget/_branchDelay.
/// Load delay slots are emulated via _loadDelayReg/_nextLoadDelayReg, a load
/// to $rt does not become visible in $rt until the instruction *after* the load completes.
/// </summary>
public partial class MipsCore
{
	private readonly Psx _psx;
	public PsxMemory Memory { get; }

	// --- General Purpose Registers ---
	// r0 is always 0 and enforced after every instruction.
	public uint[] Gpr = new uint[32];

	// Special registers
	public uint Hi;
	public uint Lo;
	public uint Pc;       // Program Counter
	public long Cycles;   // Cycle counter (kept for backward-compat; legacy
	                      // peripheral Tick(cycles) consumers still read it.
	                      // Going forward, EventScheduler.GlobalTickCounter
	                      // is the canonical "current time" and PendingTicks
	                      // is the not-yet-committed CPU progress).

	// Ticks executed by CPU but not yet committed to the scheduler's
	// global tick counter. Incremented per instruction in Step(). When
	// PendingTicks >= Downcount, Cpu.Run exits to let the scheduler fire
	// any due events (which advance the global counter and reset PendingTicks).
	public int PendingTicks;

	// Set by EventScheduler.UpdateCpuDowncount based on the next-due event.
	// Cpu.Run exits when PendingTicks >= Downcount. Forced to 0 when an
	// IRQ becomes pending so the CPU exits immediately to dispatch.
	public int Downcount = int.MaxValue;

	// --- GTE deferred-stall completion cycle ---
	// Set by ExecuteCop2 when a GTE command issues; consumed by MFC2/CFC2/SWC2
	// via StallUntilGteComplete(). Models the GTE coprocessor running IN
	// PARALLEL with the main pipeline: the cop2 issue instruction itself
	// retires in 1 cycle (paid by Step's +1), the GTE then takes (GteCycles)
	// total wall-clock cycles from issue to result-available, and any
	// subsequent read of a GTE register stalls Cycles up to that completion
	// stamp. Replaces the old eager `Cycles += GteCycles - 1` model which
	// over-charged by N cycles whenever there were N filler instructions
	// between the cop2 issue and the first MFC2/CFC2/SWC2.
	private long _gteCompletionCycle;

	// --- MULT/DIV deferred-stall completion cycle ---
	// Same pattern as GTE: MULT/DIV operations run IN PARALLEL with the main
	// pipeline on real R3000A. MULT takes 6/9/13 cycles (operand-magnitude
	// dependent); DIV takes 36. MFHI/MFLO/MTHI/MTLO and the next MULT/DIV
	// stall until the previous one completes. Reads/writes of HI/LO that
	// happen AFTER the completion deadline are free.
	private long _mulDivCompletionCycle;

	// COP0 registers
	public uint Sr;       // Status Register    (cop0 r12)
	public uint Cause;    // Cause Register     (cop0 r13)
	public uint Epc;      // Exception PC       (cop0 r14)
	public uint BadVAddr; // Bad Virtual Addr   (cop0 r8)

	// COP0 hardware-breakpoint registers (r3 BPC, r5 BDA, r6 JUMPDEST,
	// r7 DCIC, r9 BDAM, r11 BPCM). Normally used by the debug HW that
	// retail consoles don't expose, but several PAL anti-piracy schemes
	// (Capcom's Dino Crisis / RE3 LibCrypt obfuscation, MediEvil 2, etc.)
	// abuse them as scratch storage for protected pointers, relying on
	// the fact that emulators often silently ignore writes to these
	// registers. Storing the values verbatim is enough; we don't model
	// real breakpoint behaviour.
	public uint Cop0Bpc;     // r3
	public uint Cop0Bda;     // r5
	public uint Cop0Jumpdest;// r6
	public uint Cop0Dcic;    // r7
	public uint Cop0Bdam;    // r9
	public uint Cop0Bpcm;    // r11

	// --- Branch delay slot state ---
	private bool _branchDelay;    // True: next instruction is in a delay slot
	private uint _branchTarget;   // Jump target to take after delay slot
	private bool _branchTaken;    // Whether the branch is actually taken

	// --- Exception-in-delay-slot guard ---
	// Set by TriggerException, consumed by Step's post-Execute jump apply.
	// Without this, a fault inside a delay-slot instruction would be silently
	// swallowed: TriggerException correctly redirects PC to the exception
	// vector and clears _branchDelay, but the local `jumpAfter` snapshot
	// captured before Execute() would still fire and overwrite PC with the
	// branch target. EPC is preserved (handler returns to the BRANCH, not
	// the delay slot), so the exception is invisible from the handler's POV
	// but the delay-slot fault never gets serviced.
	private bool _exceptionRaised;

	// --- Halted flag (for WAIT/halt states) ---
	public bool Halted;

	// --- Debug / error info ---
	public bool CrashDetected;
	public uint CrashPc;

	// PSX R3000A i-cache emulation.
	// Real hardware:
	// 4 KB direct-mapped, 256 lines x 16 bytes (4 instructions/line).
	// Only KSEG0 (cached mirror, address bits 29..31 in [000..100b]) plus
	// KUSEG (0x00000000-0x7FFFFFFF) use the cache; KSEG1 (uncached mirror
	// 0xA0000000-0xBFFFFFFF) bypasses straight to bus.
	//   high 28 bits  = address  & 0xFFFFFFF0 (line base address)
	//   low  4 bits   = "invalid" mask, one bit per word in the line
	//                   bit N set  = word N hasn't been loaded yet
	//                   bit N clear = word N is valid in `_icacheData`
	// 0x0F invalid-bits => whole line invalid (initial / post-flush state).
	private const int IcacheLines = 256;
	private const uint IcacheTagAddressMask = 0xFFFFFFF0u;
	private const uint IcacheInvalidBits = 0x0Fu;
	private readonly uint[] _icacheTags = new uint[IcacheLines];

	// Per-word offset in line -> bit mask for the cache-hit check.
	// Word 0 bit 0, word 1 bit 1, word 2 bit 2, word 3 bit 3.
	private static readonly uint[] IcacheTagMaskForWord = {
		IcacheTagAddressMask | 1,
		IcacheTagAddressMask | 2,
		IcacheTagAddressMask | 4,
		IcacheTagAddressMask | 8,
	};

	// Per-word offset -> invalid bits set on the tag for the words BEFORE
	// the fetched offset (a partial-line fill from offset N only loads
	// words N..3; words 0..N-1 stay marked invalid).
	private static readonly uint[] IcacheFillInvalidBits = { 0, 1, 3, 7 };

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int IcacheLineIndex(uint addr) => (int)((addr >> 4) & 0xFFu);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int IcacheWordOffset(uint addr) => (int)((addr >> 2) & 0x03u);

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static bool IsCachedSegment(uint addr) => (addr >> 29) <= 4;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private bool IcacheHit(uint addr)
	{
		int line = IcacheLineIndex(addr);
		uint mask = IcacheTagMaskForWord[IcacheWordOffset(addr)];
		uint expected = addr & IcacheTagAddressMask;
		return (_icacheTags[line] & mask) == expected;
	}

	/// <summary>
	/// Mark the cache line as containing valid data starting at the fetched
	/// word offset, and return the number of words actually fetched (= words
	/// remaining in the line from the offset). Caller charges cycles based
	/// on the source region (RAM = 1 cycle/word burst, BIOS = BusCyclesBios per word).
	/// </summary>
	private int IcacheUpdateTagForFill(uint addr)
	{
		int line = IcacheLineIndex(addr);
		int wordOffset = IcacheWordOffset(addr);
		_icacheTags[line] = (addr & IcacheTagAddressMask) | IcacheFillInvalidBits[wordOffset];
		return 4 - wordOffset;
	}

	/// <summary>
	/// Public hook called from <see cref="PsxMemory"/> when the CPU writes
	/// to a cached address with SR.IsC set. BIOS uses this on cold boot
	/// (and after some kernel calls) to flush the i-cache before re-loading
	/// code into it. Set all 4 invalid bits so any read of any word in the
	/// line will miss and re-fill from memory.
	/// </summary>
	public void InvalidateICacheLine(uint addr)
	{
		_icacheTags[IcacheLineIndex(addr)] |= IcacheInvalidBits;
	}

	/// <summary>Wipe the entire i-cache. Called from <see cref="Reset"/>.</summary>
	public void ClearICache()
	{
		for (int i = 0; i < IcacheLines; i++)
			_icacheTags[i] = IcacheInvalidBits;
	}

	// ---- Save-state ----
	// Full architectural + timing state. Diagnostics (_lastPc/_stuckCount/
	// _lastLogCycle/_biosTraceEnabled/_ttyLine) and the constant i-cache helper
	// tables are intentionally excluded. PendingExe (sideload trigger) is null
	// during normal play, so it's excluded too.
	public void SaveState(StateWriter w)
	{
		w.UInts(Gpr);
		w.U32(Hi); w.U32(Lo); w.U32(Pc);
		w.S64(Cycles); w.S32(PendingTicks); w.S32(Downcount);
		w.S64(_gteCompletionCycle); w.S64(_mulDivCompletionCycle);
		w.U32(Sr); w.U32(Cause); w.U32(Epc); w.U32(BadVAddr);
		w.U32(Cop0Bpc); w.U32(Cop0Bda); w.U32(Cop0Jumpdest);
		w.U32(Cop0Dcic); w.U32(Cop0Bdam); w.U32(Cop0Bpcm);
		w.Bool(_branchDelay); w.U32(_branchTarget); w.Bool(_branchTaken);
		w.Bool(_exceptionRaised); w.Bool(Halted);
		w.Bool(CrashDetected); w.U32(CrashPc);
		w.U32(_loadDelayReg); w.U32(_loadDelayValue);
		w.U32(_nextLoadDelayReg); w.U32(_nextLoadDelayValue);
		w.UInts(_icacheTags);
	}

	public void LoadState(StateReader r)
	{
		r.UInts(Gpr);
		Hi = r.U32(); Lo = r.U32(); Pc = r.U32();
		Cycles = r.S64(); PendingTicks = r.S32(); Downcount = r.S32();
		_gteCompletionCycle = r.S64(); _mulDivCompletionCycle = r.S64();
		Sr = r.U32(); Cause = r.U32(); Epc = r.U32(); BadVAddr = r.U32();
		Cop0Bpc = r.U32(); Cop0Bda = r.U32(); Cop0Jumpdest = r.U32();
		Cop0Dcic = r.U32(); Cop0Bdam = r.U32(); Cop0Bpcm = r.U32();
		_branchDelay = r.Bool(); _branchTarget = r.U32(); _branchTaken = r.Bool();
		_exceptionRaised = r.Bool(); Halted = r.Bool();
		CrashDetected = r.Bool(); CrashPc = r.U32();
		_loadDelayReg = r.U32(); _loadDelayValue = r.U32();
		_nextLoadDelayReg = r.U32(); _nextLoadDelayValue = r.U32();
		r.UInts(_icacheTags);
	}

	/// <summary>
	/// Instruction fetch with i-cache modeling. Replaces a bare
	/// <c>Memory.ReadWord(Pc)</c> in Step(): on a cache hit, no tick penalty;
	/// on a miss, charge the line-fill cost (1 cycle/word from RAM, or
	/// <see cref="PsxConstants.BusCyclesBios"/> per word from BIOS) and
	/// update the tag. Uncached fetches (KSEG1) bypass the cache and charge
	/// the per-access cost directly via <see cref="PsxMemory.GetReadCycles"/>.
	///
	/// Read itself is always done via Memory.ReadWord, we don't model the
	/// 4-word cache-data buffer, just the timing (tags are sufficient for
	/// cycle accuracy; the actual instruction bits are re-fetched fresh).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint FetchInstruction(uint pc)
	{
		uint instr = Memory.ReadWord(pc);
		if (IsCachedSegment(pc))
		{
			if (!IcacheHit(pc))
			{
				int wordsFilled = IcacheUpdateTagForFill(pc);
				uint phys = pc & 0x1FFFFFFFu;
				int perWord;
				if (phys < 0x00800000u)
					perWord = 1; // RAM burst fill (DRAM Hyper Page Mode)
				else if (phys >= 0x1FC00000u && phys < 0x1FC80000u)
					perWord = PsxConstants.BusCyclesBiosWord;
				else
					perWord = 0; // unmapped / expansion / scratchpad-as-code: no penalty
				Cycles += wordsFilled * perWord;
			}
			// Cache hit: no penalty, fall through with instr loaded.
		}
		else
		{
			// Uncached segment (KSEG1, etc.): charge per-access bus cycles
			// directly. This is what data loads pay via GetReadCycles.
			Cycles += Memory.GetReadCycles(pc);
		}
		return instr;
	}

	public MipsCore(Psx psx)
	{
		_psx = psx;
		Memory = psx.Memory;
	}

	public void Reset()
	{
		Array.Clear(Gpr);
		Hi = Lo = 0;
		// CPU starts executing from BIOS at 0xBFC00000
		Pc = 0xBFC00000;
		Sr = 0x00000000;
		Cause = 0;
		Epc = 0;
		BadVAddr = 0;
		Cycles = 0;
		PendingTicks = 0;
		Downcount = int.MaxValue;
		_gteCompletionCycle = 0;
		_mulDivCompletionCycle = 0;
		Halted = false;
		CrashDetected = false;
		_branchDelay = false;
		_branchTarget = 0;
		_branchTaken = false;
		_exceptionRaised = false;
		_loadDelayReg = 32;
		_loadDelayValue = 0;
		_nextLoadDelayReg = 32;
		_nextLoadDelayValue = 0;
		// Wipe i-cache so a hot-reload doesn't leave stale tags.
		ClearICache();
	}

	// BIOS syscall tracing, always on but filters noisy calls
	private bool _biosTraceEnabled = true;
	public void EnableBiosTrace() { _biosTraceEnabled = true; }

	// --- Load delay slot ---
	// Real R3000A: the value written by `lw $rt, ...` (or any load / MFCx) is NOT
	// visible in $rt until the instruction *after* the load completes. An instruction
	// in the "load delay slot" reads the OLD $rt value. Hand-tuned assembly (incl. PSX
	// FMV decoders) relies on this behaviour.
	//
	//   _loadDelayReg / _loadDelayValue        - load that will commit AFTER the
	//                                            current instruction's Execute runs.
	//   _nextLoadDelayReg / _nextLoadDelayValue - load queued by the current Execute,
	//                                            becomes the pending load for next cycle.
	//
	// 32 is the sentinel for "no pending load" (Gpr indices are 0-31).
	private uint _loadDelayReg = 32;
	private uint _loadDelayValue;
	private uint _nextLoadDelayReg = 32;
	private uint _nextLoadDelayValue;

	/// <summary>Direct write to a GPR. Cancels any pending load to the same register
	/// so that a non-load writeback later in the pipeline correctly overrides the
	/// about-to-commit load (matches real R3000A pipeline ordering).</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteReg(uint reg, uint value)
	{
		if (reg == 0) return; // r0 is always 0
		Gpr[reg] = value;
		if (_loadDelayReg == reg) _loadDelayReg = 32;
	}

	/// <summary>Queue a load result for delayed commit. The write becomes visible
	/// after the next instruction executes. Handles the "double load to same
	/// register" case by cancelling the previous pending load (the second wins).</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void WriteRegDelayed(uint reg, uint value)
	{
		if (reg == 0) return;
		// Double-load: drop the previous pending so the current load wins.
		if (_loadDelayReg == reg) _loadDelayReg = 32;
		_nextLoadDelayReg = reg;
		_nextLoadDelayValue = value;
	}

	/// <summary>
	/// Returns $rt's value as seen by an LWL/LWR merge, these instructions
	/// bypass the load delay (read the just-loaded value if it targets the same register).
	/// </summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private uint ReadRegLwxBypass(uint reg)
	{
		if (_loadDelayReg == reg) return _loadDelayValue;
		return Gpr[reg];
	}

	/// <summary>Apply pending load to GPR and shift next->current. Called once per
	/// instruction AFTER Execute returns.</summary>
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private void UpdateLoadDelay()
	{
		if (_loadDelayReg < 32)
			Gpr[_loadDelayReg] = _loadDelayValue;
		_loadDelayReg = _nextLoadDelayReg;
		_loadDelayValue = _nextLoadDelayValue;
		_nextLoadDelayReg = 32;
	}

	/// <summary>Commit any in-flight loads to GPR before an exception entry, the
	/// saved register state should reflect them.</summary>
	private void FlushLoadDelay()
	{
		if (_loadDelayReg < 32)
			Gpr[_loadDelayReg] = _loadDelayValue;
		if (_nextLoadDelayReg < 32)
			Gpr[_nextLoadDelayReg] = _nextLoadDelayValue;
		_loadDelayReg = 32;
		_nextLoadDelayReg = 32;
	}

	// Stuck-loop detection
	private uint _lastPc;
	private int _stuckCount;
	private long _lastLogCycle;
	
	/// <summary>
	/// Run until <paramref name="target"/> cycles OR until <see cref="PendingTicks"/>
	/// reaches <see cref="Downcount"/> (whichever comes first).
	///
	/// The Downcount path is the event-scheduler exit: when an event becomes
	/// due, the scheduler sets Downcount = (next_event_time - global_tick)
	/// and we exit to RunEvents() to dispatch.
	///
	/// The target path is the legacy frame/scanline boundary check. Both
	/// gates apply during the transition to a fully event-driven model.
	///
	/// PendingTicks is updated as a Cycles-delta after each Step rather
	/// than mirrored at every `Cycles += N` site (load delays, GTE,
	/// MUL/DIV, etc.), single update point, fewer hot-path edits.
	/// </summary>
	public void Run(long target)
	{
		while (Cycles < target && !Halted && !CrashDetected)
		{
			// Snapshot Cycles BEFORE both CheckIrq AND Step so any bumps
			// (e.g. GTE-during-IRQ path in CheckIrq adding GteCycles, or
			// load-delay/MULT/DIV penalties in Step's handlers) are
			// included in PendingTicks.
			long cyclesBefore = Cycles;

			// IRQ check FIRST, unconditionally, must not be gated by the
			// Downcount check. The scheduler signals "IRQ pending, please
			// exit and dispatch" by setting Downcount=0; if we gated on
			// `PendingTicks < Downcount` before CheckIrq, the loop would
			// exit immediately, RunEvents would re-force Downcount=0 (IRQ
			// still pending), and the outer RunCpuTo would infinite-spin
			// because the CPU never actually dispatches the interrupt.
			if (CheckIrq())
			{
				PendingTicks += (int)(Cycles - cyclesBefore);
				continue;
			}

			// Now apply the Downcount exit (event-due signal from scheduler).
			// Uses stored PendingTicks (not fresh Cycles-GlobalTickCounter)
			// deliberately: callback Cycles-bumps from prior RunEvents would
			// otherwise make this check immediately true on Run re-entry,
			// causing Run to exit without ANY CPU step. Re-RunEvents would
			// see no new pending_ticks delta, no new events firing, callbacks
			// bump again, infinite no-progress loop. Stored PendingTicks
			// guarantees CPU makes forward progress between event dispatches.
			if (PendingTicks >= Downcount)
				break;

			Step();
			PendingTicks += (int)(Cycles - cyclesBefore);

			// Detect tight infinite loops: same PC for > 500K cycles, log every 1M cycles
			if (Pc == _lastPc)
			{
				_stuckCount++;
				if (_stuckCount > 500_000 && (Cycles - _lastLogCycle) > 1_000_000)
				{
					_lastLogCycle = Cycles;
					uint instr = Memory.ReadWord(Pc);
					PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Debug,
						$"[DIAG] Tight loop at PC=0x{Pc:X8} instr=0x{instr:X8} SR=0x{Sr:X8} Cause=0x{Cause:X8} cycles={Cycles}");
				}
			}
			else
			{
				_stuckCount = 0;
				_lastPc = Pc;
			}
		}
	}

	// TTY capture: BIOS std_out_putchar output accumulated a line at a time, so
	// test programs' printf is surfaced to the log (diffable vs reference psx.log).
	private readonly System.Text.StringBuilder _ttyLine = new System.Text.StringBuilder(256);

	private void TtyPutchar(byte ch)
	{
		if (ch == (byte)'\n' || ch == (byte)'\r')
		{
			if (_ttyLine.Length > 0)
			{
				PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"[TTY] {_ttyLine}");
				_ttyLine.Clear();
			}
			return;
		}
		if (ch == 0) return;
		_ttyLine.Append((char)ch);
		if (_ttyLine.Length >= 200) // defensively flush pathologically long lines
		{
			PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info, $"[TTY] {_ttyLine}");
			_ttyLine.Clear();
		}
	}

	/// <summary>
	/// Side-load a PS-X EXE: copy its body into RAM at the header's load address
	/// and set PC/GP/SP from the header. Invoked from Step() the moment the BIOS
	/// reaches the shell entry (0x80030000), so the kernel is already initialised.
	/// </summary>
	private void ApplyExe(byte[] exe)
	{
		_psx.PendingExe = null;
		if (exe == null || exe.Length < 0x800) return;
		uint Rd(int o) => (uint)(exe[o] | (exe[o + 1] << 8) | (exe[o + 2] << 16) | (exe[o + 3] << 24));
		uint pc0 = Rd(0x10), gp0 = Rd(0x14), tAddr = Rd(0x18), tSize = Rd(0x1C);
		uint sAddr = Rd(0x30), sSize = Rd(0x34);

		// Copy text/data to RAM at the load address (masked to the 2 MB region).
		uint off = tAddr & (PsxConstants.RamSize - 1);
		int n = (int)System.Math.Min(tSize, (uint)(exe.Length - 0x800));
		n = System.Math.Min(n, (int)(PsxConstants.RamSize - off));
		if (n > 0) System.Array.Copy(exe, 0x800, Memory.Ram, (int)off, n);

		// Enter at the EXE's entry point with a clean pipeline.
		Pc = pc0;
		Gpr[28] = gp0;
		uint sp = sAddr != 0 ? sAddr + sSize : 0;
		if (sp != 0) { Gpr[29] = sp; Gpr[30] = sp; }
		Gpr[31] = 0;
		_branchDelay = false;
		_branchTaken = false;
		_exceptionRaised = false;
		_loadDelayReg = 32;
		_nextLoadDelayReg = 32;
		ClearICache();
		PsxLog.Write(PsxLogCategory.PSX, PsxLogLevel.Info,
			$"[EXE] side-loaded: entry=0x{pc0:X8} load=0x{tAddr:X8} size={tSize} gp=0x{gp0:X8} sp=0x{sp:X8}");
	}

	/// <summary>Execute one instruction.</summary>
	public void Step()
	{
		// PS-X EXE side-load: the moment the BIOS finishes init and is about to
		// run the shell at 0x80030000, replace it with a pending side-loaded EXE
		// (fast-boot for the ps1-tests suite, no disc required).
		if (_psx.PendingExe != null && Pc == 0x80030000)
			ApplyExe(_psx.PendingExe);

		// PC alignment check: MIPS requires word-aligned instruction fetch
		if ((Pc & 3) != 0)
		{
			BadVAddr = Pc;
			TriggerException(PsxConstants.ExcAdEL, Pc, _branchDelay);
			return;
		}

		// Instruction-fetch bus error (IBE, exc 6). Real hardware can only
		// fetch instructions from RAM, BIOS and EXP1; fetching from the
		// scratchpad (the D-cache, not on the instruction bus) or unmapped
		// memory bus-errors. We conservatively raise IBE for ANYTHING outside
		// RAM/BIOS/EXP1, tested on the PHYSICAL address so the cached
		// (KUSEG/KSEG0) and uncached (KSEG1) mirrors are all covered.
		//
		// NOTE: this is intentionally broader than hardware. On real HW a
		// *mapped* I/O fetch returns the register's read value and executes it
		// (SPU/DMA echo the written word and return cleanly; INT/MDEC don't
		// echo, so execution runs off the short register block into an unmapped
		// gap and IBEs there). Modelling that faithfully isn't worth the
		// complexity: no game ever executes from I/O, so the over-broad IBE is
		// harmless. Consequence: ps1-tests cpu/code-in-io passes Ram /
		// Scratchpad / MDEC / Interrupts but (deliberately) not SPU / DMA0 /
		// DMAControl. R3000 bus errors do not latch BadVAddr, so we leave it.
		uint fetchPhys = Pc & 0x1FFFFFFFu;
		if (!(fetchPhys < 0x00800000u                                  // RAM (+ mirrors)
			  || (fetchPhys >= 0x1FC00000u && fetchPhys < 0x1FC80000u) // BIOS
			  || (fetchPhys >= 0x1F000000u && fetchPhys < 0x1F800000u)))// EXP1
		{
			TriggerException(PsxConstants.ExcIBE, Pc, _branchDelay);
			return;
		}

		uint instr;
		try
		{
			// I-cache modeling. Hit -> no extra cycles; miss ->
			// line-fill cost. KSEG1 (uncached) fetches charge full bus cost.
			instr = FetchInstruction(Pc);
		}
		catch
		{
			TriggerException(PsxConstants.ExcAdEL, Pc, _branchDelay);
			return;
		}

		uint currentPc = Pc;

		// TTY capture: A(0x3C)/B(0x3D) = std_out_putchar(char in a0). Surfaces
		// the ps1-tests suite's printf output (diffable vs the reference psx.log).
		{
			uint ttyPc = currentPc & 0x1FFFFFFF;
			if ((ttyPc == 0xA0 && Gpr[9] == 0x3C) || (ttyPc == 0xB0 && Gpr[9] == 0x3D))
				TtyPutchar((byte)(Gpr[4] & 0xFF));
		}

		// BIOS syscall tracing: always active, filters noisy calls
		if (_biosTraceEnabled)
		{
			uint physPc = currentPc & 0x1FFFFFFF;
			if (physPc == 0xA0 || physPc == 0xB0 || physPc == 0xC0)
			{
				char table = physPc == 0xA0 ? 'A' : physPc == 0xB0 ? 'B' : 'C';
				uint fn = Gpr[9];
				// Skip noisy calls: B(0x0B)=TestEvent,
				// B(0x3D)=putchar, B(0x17)=ReturnFromException,
				// A(0x13)=setjmp, C(0x0A)=ChangeClearRCnt
				// Note: B(0x07)=DeliverEvent NOT skipped, needed to debug CDROM event delivery
				bool skip = (table == 'B' && (fn == 0x0B || fn == 0x3D || fn == 0x17)) ||
							(table == 'C' && fn == 0x0A) ||
							(table == 'A' && fn == 0x13);
				if (!skip)
				{
					// Extra detail for DeliverEvent with CDROM class
					if (table == 'B' && fn == 0x07 && Gpr[4] == 0xF0000009)
						PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn,
							$"[DIAG] DeliverEvent CDROM class=0x{Gpr[4]:X8} spec=0x{Gpr[5]:X8} from RA=0x{Gpr[31]:X8}");
				}
			}
		}

		bool inDelaySlot = _branchDelay;

		// Advance PC before execution (so relative branches compute correctly)
		Pc += 4;

		// Reset the exception guard for this instruction. If Execute() raises
		// an exception (AdEL/AdES/Ovf/RI/CpU/etc.) the handler will set this
		// flag, telling the post-Execute jump-apply below to stand down so the
		// exception vector PC isn't clobbered by a stale `jumpAfter` snapshot.
		_exceptionRaised = false;

		// If we were in a delay slot, the branch fires now
		bool jumpAfter = false;
		uint jumpTarget = 0;
		if (inDelaySlot)
		{
			jumpAfter = _branchTaken;
			jumpTarget = _branchTarget;
			_branchDelay = false;
			_branchTaken = false;
		}

		Execute(instr, currentPc, inDelaySlot);

		// Apply pending load AFTER Execute completes, instructions in the load
		// delay slot read the OLD register value during their Execute phase, so
		// the commit must happen here, not before.
		UpdateLoadDelay();

		// Enforce r0 = 0
		Gpr[0] = 0;

		// Apply branch/jump after the delay slot, UNLESS Execute raised an
		// exception, which has already redirected PC to the exception vector
		// (and saved EPC = branch instruction). Without the _exceptionRaised
		// guard, a fault in the delay slot would have its vector PC
		// overwritten by the branch target and the exception would never be
		// serviced.
		//
		// We deliberately DO apply the jump even when _branchDelay is true
		// here, that's the "branch in a branch delay slot" case. Snapshot
		// trace for B1 at PC=0x100 (target 0x200) with B2 at PC=0x104
		// (target 0x300) as its delay slot:
		//   1. Outer jump fires -> PC = 0x200 (B1's target)
		//   2. _branchDelay stays true from B2, so the next Step treats the
		//      instruction at 0x200 as B2's delay slot
		//   3. After that delay slot, PC = 0x300 (B2's target)
		// The previous `!_branchDelay` guard collapsed this into "drop the outer jump",
		// effectively skipping 0x200 entirely and breaking exception-table
		// dispatch tricks and a handful of hand-tuned BIOS routines.
		if (jumpAfter && !_exceptionRaised)
			Pc = jumpTarget;

		Cycles++;
		// PendingTicks is updated in Run() via Cycles-delta after each Step
		// so we don't have to mirror every `Cycles += N` site (load delays,
		// GTE cycles, MUL/DIV penalties). Step contains the only +1 bump;
		// other increments happen inside Execute() handlers via Cycles += N.
	}

	// --- Branch helpers ---

	/// <summary>
	/// If <paramref name="target"/> is word-unaligned, raises AdEL immediately
	/// (BadVAddr/EPC = target, BD=false) and returns true. In that case the
	/// caller must NOT set up the pending branch state, the exception has
	/// already redirected PC to the vector and the delay-slot instruction
	/// must be skipped entirely.
	/// </summary>
	private bool CheckBranchAlignment(uint target)
	{
		if ((target & 3) == 0) return false;

		PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn,
			$"[CPU] AdEL on branch to unaligned 0x{target:X8} from PC=0x{Pc - 4:X8}");

		BadVAddr = target;
		// faultPc=target, inDelaySlot=false -> EPC = target.
		TriggerException(PsxConstants.ExcAdEL, target, false);
		return true;
	}

	/// <summary>Schedule an unconditional branch (always taken).</summary>
	private void Branch(uint target)
	{
		if (CheckBranchAlignment(target)) return;

		_branchDelay = true;
		_branchTarget = target;
		_branchTaken = true;
	}

	/// <summary>Schedule a conditional branch.</summary>
	private void BranchIf(bool condition, uint target)
	{
		// Only a *taken* branch with an unaligned target faults, a not-taken
		// branch never fetches from the bad address.
		if (condition && CheckBranchAlignment(target)) return;

		_branchDelay = true;
		_branchTarget = target;
		_branchTaken = condition;
	}

	/// <summary>Compute a PC-relative branch target from a signed 16-bit offset.</summary>
	private uint RelBranchTarget(uint pc, uint instr)
	{
		// offset is in words, shifted by 2
		int imm16 = (short)(instr & 0xFFFF);
		return (uint)((int)pc + 4 + (imm16 << 2));
	}

	/// <summary>Compute a J-type jump target.</summary>
	private uint JumpTarget(uint pc, uint instr)
	{
		// [31:28] of (PC+4) | [27:2] from instr[25:0] | 00
		return (pc & 0xF0000000u) | ((instr & 0x03FFFFFFu) << 2);
	}

	// --- COP0 register access ---

	public uint ReadCop0(int reg)
	{
		return reg switch
		{
			PsxConstants.Cop0Bpc       => Cop0Bpc,
			PsxConstants.Cop0Bda       => Cop0Bda,
			PsxConstants.Cop0Jumpdest  => Cop0Jumpdest,
			PsxConstants.Cop0Dcic      => Cop0Dcic,
			PsxConstants.Cop0BadVaddr  => BadVAddr,
			PsxConstants.Cop0Bdam      => Cop0Bdam,
			PsxConstants.Cop0Bpcm      => Cop0Bpcm,
			PsxConstants.Cop0Sr        => Sr,
			PsxConstants.Cop0Cause     => Cause,
			PsxConstants.Cop0Epc       => Epc,
			PsxConstants.Cop0Prid      => 0x00000002, // R3000A
			_ => 0,
		};
	}

	public void WriteCop0(int reg, uint value)
	{
		switch (reg)
		{
			// HW breakpoint registers, abused as scratch storage by Capcom's
			// PAL LibCrypt obfuscation (Dino Crisis, RE3) which stashes a
			// protected-table pointer in BDAM (r9) via mtc0 and reads it back
			// via mfc0. Storing the value verbatim is enough; we don't model
			// the actual debug-break behaviour.
			case PsxConstants.Cop0Bpc: Cop0Bpc      = value; break;
			case PsxConstants.Cop0Bda: Cop0Bda      = value; break;
			case PsxConstants.Cop0Jumpdest: Cop0Jumpdest = value; break;
			case PsxConstants.Cop0Dcic: Cop0Dcic     = value; break;
			case PsxConstants.Cop0Bdam: Cop0Bdam     = value; break;
			case PsxConstants.Cop0Bpcm: Cop0Bpcm     = value; break;
			case PsxConstants.Cop0Sr:
				Sr = value;
				// Tell memory bus if cache is isolated
				Memory.SetCacheIsolated((value & PsxConstants.SrIsc) != 0);
				break;
			case PsxConstants.Cop0Cause:
				// Only bits 8-9 (software interrupts) are writable
				Cause = (Cause & ~0x0300u) | (value & 0x0300u);
				break;
			case PsxConstants.Cop0Epc: Epc = value; break;
			case PsxConstants.Cop0BadVaddr: break; // read-only
		}
	}

	// --- Interrupt check ---

	private bool CheckIrq()
	{
		// IRQ fires if: IEc=1, (SR.IM & CAUSE.IP) != 0
		if ((Sr & PsxConstants.SrIec) == 0) return false;

		// Update CAUSE.IP bit 10 from the actual interrupt line
		bool irqLine = _psx.Interrupts.IrqPending;
		if (irqLine)
			Cause |= (1u << 10);
		else
			Cause &= ~(1u << 10);

		// Check if any unmasked interrupt is pending
		if ((Sr & Cause & PsxConstants.SrIm) == 0) return false;

		// HW QUIRK, GTE runs in parallel during exception entry.
		// Without this, every IRQ that lands on a GTE op silently drops that op.
		// Crash Bandicoot's tightly-pipelined renderer (Naughty Dog hand-assembly)
		// loses one transform per affected frame. Letters/buttons jumping by tens of pixels
		// for a single frame because the game submitted vertices computed from a
		// stale GTE output. Other games tend to mask interrupts during GTE-heavy
		// runs and so don't hit this case as often.
		if ((Pc & 3) == 0)
		{
			uint nextInstr = 0;
			bool readOk = true;
			try { nextInstr = Memory.ReadWord(Pc); }
			catch { readOk = false; }
			if (readOk && (nextInstr & 0xFE000000u) == 0x4A000000u)
			{
				// COP2 GTE command: execute it before raising the exception.
				_psx.Gte.Execute(nextInstr);
				Cycles += GteCycles(nextInstr);
			}
		}

		TriggerException(PsxConstants.ExcInt, Pc, _branchDelay);
		return true;
	}

	// --- Exception handler ---

	public void TriggerException(uint excCode, uint faultPc, bool inDelaySlot)
	{
		// Commit any in-flight loads before saving the register file, exception
		// handlers expect to see the post-load state.
		FlushLoadDelay();

		// Save EPC (if in delay slot, EPC = the branch instruction)
		Epc = inDelaySlot ? (faultPc - 4) : faultPc;

		// Diagnostic: log CPU fault exceptions (address errors, illegal instructions, overflow).
		// Syscall/Break are normal BIOS calls and are excluded to avoid log spam.
		// These faults fire regardless of IEc and can land in a corrupted exception vector.
		bool _isFault = excCode is PsxConstants.ExcAdEL or PsxConstants.ExcAdES or PsxConstants.ExcIBE or PsxConstants.ExcDBE or PsxConstants.ExcRI or PsxConstants.ExcOvf or PsxConstants.ExcCpU;
		
		if (_isFault)
		{
			bool bev = (Sr & PsxConstants.SrBev) != 0;
			
			string excName = excCode switch
			{
				PsxConstants.ExcAdEL => "AdEL",
				PsxConstants.ExcAdES => "AdES",
				PsxConstants.ExcIBE => "IBE",
				PsxConstants.ExcDBE => "DBE",
				PsxConstants.ExcRI => "RI",
				PsxConstants.ExcOvf => "Ovf",
				PsxConstants.ExcCpU => "CpU",
				_ => $"0x{excCode:X}",
			};
			
			PsxLog.Write(PsxLogCategory.CPU, PsxLogLevel.Warn, $"[EXC] {excName} faultPC=0x{faultPc:X8} EPC=0x{(inDelaySlot ? (faultPc - 4) : faultPc):X8} BadVAddr=0x{BadVAddr:X8} SR=0x{Sr:X8} vector=0x{(bev ? 0xBFC00180u : 0x80000080u):X8} ra=0x{Gpr[31]:X8}");
		}

		// Set CAUSE. Beyond ExcCode (bits [6:2]) and BD (bit 31), we also
		// clear CE (bits [29:28]) and BT (bit 30), those carry exception
		// metadata that becomes stale once a new exception fires. CE in
		// particular records which coprocessor caused a CpU exception; if
		// stale, a kernel handler that branches on CE could mis-dispatch.
		const uint CauseExceptionWriteMask = 0xF000007Cu; // BD | BT | CE | ExcCode
		Cause = (Cause & ~CauseExceptionWriteMask) | (excCode << 2);
		if (inDelaySlot) Cause |= PsxConstants.CauseBd;

		// Shift SR exception bits: [3:0] -> [5:2], clear [1:0]
		// i.e. IEc->IEp, KUc->KUp, IEp->IEo, KUp->KUo; IEc/KUc cleared
		uint shifted = (Sr & 0x0Fu) << 2;
		Sr = (Sr & ~0x3Fu) | shifted;

		// Jump to exception vector
		if ((Sr & PsxConstants.SrBev) != 0)
			Pc = 0xBFC00180; // BIOS exception vector
		else
			Pc = 0x80000080; // RAM exception vector

		// Cancel pending branch
		_branchDelay = false;
		_branchTaken = false;
		// Tell Step()'s post-Execute jump-apply to stand down, PC now points
		// at the exception vector and must not be overwritten by the branch
		// target captured before Execute() ran.
		_exceptionRaised = true;
		Halted = false;
	}

	// --- RFE: Return From Exception ---
	// Shifts SR bits right by 2: KUo,IEo -> KUp,IEp; KUp,IEp -> KUc,IEc
	private void ReturnFromException()
	{
		uint bits = (Sr >> 2) & 0x0Fu;
		Sr = (Sr & ~0x0Fu) | bits;
	}

	// --- Sign-extend helpers ---
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint SignExtend8(uint v) => (uint)(sbyte)v;
	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static uint SignExtend16(uint v) => (uint)(short)v;
}
