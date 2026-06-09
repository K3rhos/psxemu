namespace PSXEmu;

/// <summary>
/// MDEC (Motion Decoder): PSX hardware DCT video decoder used for FMV playback.
///
/// Data flow:
///   1. CPU/DMA ch0 (MDECin) sends compressed BS-format macroblock data -> WriteWord(0) / DmaWrite
///   2. MDEC decodes each 16*16 macroblock (6 DCT blocks: Cr,Cb,Y1-Y4)
///   3. DMA ch1 (MDECout) reads decoded pixels -> DmaRead
///
/// Commands (written to offset 0):
///   Cmd 1  : Decode Macroblock (BS bitstream: RLE->IDCT->YCbCr->RGB)
///   Cmd 2  : Set Quantization Table (luma 64B, optionally chroma 64B)
///   Cmd 3  : Set IDCT Scale Table (64 s16 values, column-major)
///
/// </summary>
public class PsxMdec
{
	// Status register bit masks (offset 4, read-only), matches No$PSX.
	// Bit 31: Data-Out FIFO Empty  (1 = empty, 0 = has data)
	// Bit 30: Data-In  FIFO Full   (1 = full)
	// Bit 29: Command Busy         (1 = executing)
	// Bit 28: Data-In  DMA Request (1 = DMA enabled AND input FIFO has space)
	// Bit 27: Data-Out DMA Request (1 = DMA enabled AND output FIFO has data)
	private const uint STAT_DATA_OUT_FIFO_EMPTY = 1u << 31;
	private const uint STAT_DATA_IN_FIFO_FULL   = 1u << 30;
	private const uint STAT_BUSY                = 1u << 29;
	private const uint STAT_DATA_IN_REQUEST     = 1u << 28;
	private const uint STAT_DATA_OUT_REQUEST    = 1u << 27;

	private enum MdecState { Idle, DecodingMacroblock, WritingMacroblock, SetIqTable, SetScaleTable }

	// Output depth codes (bits [26:25] of command word)
	private const int DEPTH_4BIT = 0;
	private const int DEPTH_8BIT = 1;
	private const int DEPTH_24BIT = 2;
	private const int DEPTH_15BIT = 3;

	// Inverse zigzag table: maps RLE scan position -> 8*8 DCT block linear index.
	// zagzig[i] gives the block[x + y*8] index for the i-th zigzag position.
	private static readonly byte[] ZagZig =
	{
		 0,  1,  8, 16,  9,  2,  3, 10,
		17, 24, 32, 25, 18, 11,  4,  5,
		12, 19, 26, 33, 40, 48, 41, 34,
		27, 20, 13,  6,  7, 14, 21, 28,
		35, 42, 49, 56, 57, 50, 43, 36,
		29, 22, 15, 23, 30, 37, 44, 51,
		58, 59, 52, 45, 38, 31, 39, 46,
		53, 60, 61, 54, 47, 55, 62, 63
	};

	// State

	private MdecState _state = MdecState.Idle;
	private int _outputDepth = DEPTH_15BIT;
	private bool _outputSigned;
	private bool _outputBit15;
	private bool _enableDmaIn;
	private bool _enableDmaOut;

	/// <summary>
	/// True if MDEC is currently mid-decode for a cmd1 (actively decoding bits OR
	/// holding a decoded block in staging waiting for its 2688-cycle copy-out
	/// event to fire). Used by DMA1 to decide whether to wait for more output
	/// (decoding) or accept an empty block (idle = game over-asked, no more
	/// data coming).
	/// </summary>
	public bool IsDecoding =>
		_state == MdecState.DecodingMacroblock || _state == MdecState.WritingMacroblock;

	/// <summary>
	/// Number of unconsumed halfwords sitting in MDEC's input FIFO. DMA1 uses
	/// this together with DMA0.PendingBlocks to detect "more bitstream is on
	/// its way", when either is non-zero, DMA1 should wait rather than fall
	/// through to scenario B (accept empty block). See PsxDma.cs.
	/// </summary>
	public int InFifoCount => _inFifo.Count;

	// Diagnostics: count commands and decoded macroblocks to understand data flow
	private int _diagCmd1Count;
	public int DiagCmd1Count => _diagCmd1Count;
	private int _diagMacroblockCount;
	// Per-cmd1 stats: halfwords declared at start, MB count at start, so we can compute
	// how many halfwords each cmd1 actually consumed and at what halfwords-per-MB rate.
	private int _diagPrevCmd1Halfwords;
	private int _diagPrevCmd1MbCount;

	// Quantization tables (64 bytes: luma / chroma)
	private readonly byte[] _iqY = new byte[64];
	private readonly byte[] _iqUv = new byte[64];

	// IDCT scale matrix (row-major after transpose at load)
	private readonly short[] _scaleTable = new short[64];

	// Per-macroblock decode cursor
	private int _remainingHalfwords;
	private int _currentBlock = 0;  // 0=Cr,1=Cb,2..5=Y1..Y4
	private int _currentCoefficient = 64; // 64 = start-of-block sentinel
	private ushort _currentQScale;

	// MDEC processing-time emulation, per-macroblock copy-out event model.
	//
	// Real PSX MDEC takes 448 cycles PER 8x8 BLOCK to decode; a macroblock is 6
	// blocks (Cr, Cb, Y0..Y3) so total = 2688 cycles/MB. Each macroblock is decoded into
	// a staging buffer, output stays out of the data-out FIFO until the event
	// fires 2688 cycles later. DMA1 reads from MDEC stall during this window,
	// matching the natural rate at which a real PSX MDEC produces output.
	//
	// We replicate that here. Decode flow per cmd1:
	//   1. Game writes cmd1 -> state = DecodingMacroblock, _remainingHalfwords set.
	//   2. Bits arrive via DMA0 / Write. Execute() decodes ONE macroblock into the
	//      `_blocks` / `_blockRgb` staging.
	//   3. State transitions to WritingMacroblock, _blockReadyCycles = 2688,
	//      `_event.Schedule(2688)` arms the block-ready event.
	//   4. CPU runs for 2688 cycles in parallel until the scheduler fires
	//      `OnMdecEvent`, which calls CopyOutBlock() to push the staged RGB
	//      to _outFifo, then either goes back to DecodingMacroblock for the
	//      next MB (input still has bits) or to Idle (cmd1 fully drained).
	//      DMA1 can now drain one MB worth.
	//
	// While in DecodingMacroblock or WritingMacroblock, MDEC1.STAT bit 29 (BUSY)
	// reads as 1, IsDecoding covers both states. This makes the legacy
	// `_busyExtensionCycles` accumulation redundant for normal cmd1 flow; we keep
	// the field as a safety net for any code path that still relies on it but
	// expect it to stay 0 once the new pacing model is in effect.
	//
	// Previous models (kept for context):
	//   - Pure synchronous decode + bulk OutFIFO fill: too fast. DMA1 IRQ fires
	//     ~8x sooner than real PSX, racing the game's main-thread halt check.
	//     This was the cause of the RE2 new-game intro halt at 0x80031D20.
	//   - Per-cmd1 bulk BUSY-extension charge after synchronous decode: matched
	//     the BUSY-bit poll window but DMA1 still drained immediately, so the
	//     IRQ delivery timing was wrong (handler ran AFTER halt check).
	private const int CyclesPerBlock = 448;
	private const int BlocksPerMB = 6;
	private const int CyclesPerMB = CyclesPerBlock * BlocksPerMB; // = 2688
	private int _busyExtensionCycles;

	// Ticks until the staged macroblock's RGB is released into _outFifo.
	// Only meaningful while _state == WritingMacroblock; set to CyclesPerMB
	// after each successful decode, decremented by Tick().
	private int _blockReadyCycles;

	// 6 DCT blocks per macroblock (each 8*8 s16)
	private readonly short[][] _blocks;

	// 16*16 decoded RGBA8888 output (stored as 0x00BBGGRR)
	private readonly uint[] _blockRgb = new uint[256];

	// IDCT pass-1 scratch buffer. Reused across every IDCT call to avoid per-block
	// allocations. An MDEC frame is ~1800 IDCTs, and a fresh long[64] each time was
	// producing several MB of garbage per FMV frame, which manifested as visible stalls
	// in the FMV pipeline (the only path that calls MDEC on the hot loop).
	// Pass 1 writes every cell before pass 2 reads, so no inter-call reset is needed.
	// Theory #3: short[] (was long[]) so it can hold the post-pass IDCT output
	// in the same format ProjectPSX uses. The new IDCT formula stores
	// `(sum + 0xFFF) / 0x2000` as a short, then swaps src/dst between passes.
	private readonly short[] _idctTmp = new short[64];

	// Input/output FIFOs
	private readonly Queue<ushort> _inFifo = new(2048);
	private readonly Queue<uint> _outFifo = new(512);

	// Bus-arbitration plumbing: MDEC asserts DMA0 (MDECin) request when the input
	// FIFO has room AND _enableDmaIn is set, and DMA1 (MDECout) request when the
	// output FIFO has data AND _enableDmaOut is set. Without this signaling,
	// DMA1 could prematurely drain the OutFIFO (or read empty) while MDEC is
	// still decoding upstream bits, See PsxDma.cs SetRequest / Channels[].Request
	// for the DMA-side gating.
	private Psx _psx;

	public PsxMdec(Psx psx = null)
	{
		_psx = psx;
		_blocks = new short[6][];
		for (int i = 0; i < 6; i++)
			_blocks[i] = new short[64];

		// MDEC block-copy-out event. Replaces the LegacyTick path's per-tick
		// decrement of _blockReadyCycles. Scheduled for CyclesPerMB cycles when
		// Execute transitions to WritingMacroblock; callback runs FireBlockCopyOut + chains the next
		// MB if more input bits are available. Deactivated otherwise.
		_event = new TimingEvent(
			"Mdec", int.MaxValue, int.MaxValue,
			(param, _, _) => ((PsxMdec)param).OnMdecEvent(),
			this);
	}

	private TimingEvent _event;

	// ---- Save-state ---- (ZagZig LUT + _diag* counters excluded.)
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		w.S32((int)_state);
		w.S32(_outputDepth);
		w.Bool(_outputSigned); w.Bool(_outputBit15);
		w.Bool(_enableDmaIn); w.Bool(_enableDmaOut);
		w.Bytes(_iqY); w.Bytes(_iqUv);
		w.Shorts(_scaleTable);
		w.S32(_remainingHalfwords); w.S32(_currentBlock);
		w.S32(_currentCoefficient); w.U16(_currentQScale);
		w.S32(_busyExtensionCycles); w.S32(_blockReadyCycles);
		foreach (var blk in _blocks) w.Shorts(blk);
		w.UInts(_blockRgb);
		w.Shorts(_idctTmp);
		w.S32(_inFifo.Count); foreach (var x in _inFifo) w.U16(x);
		w.S32(_outFifo.Count); foreach (var x in _outFifo) w.U32(x);
		_event.SaveState(w, g);
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		_state = (MdecState)r.S32();
		_outputDepth = r.S32();
		_outputSigned = r.Bool(); _outputBit15 = r.Bool();
		_enableDmaIn = r.Bool(); _enableDmaOut = r.Bool();
		r.Bytes(_iqY); r.Bytes(_iqUv);
		r.Shorts(_scaleTable);
		_remainingHalfwords = r.S32(); _currentBlock = r.S32();
		_currentCoefficient = r.S32(); _currentQScale = r.U16();
		_busyExtensionCycles = r.S32(); _blockReadyCycles = r.S32();
		foreach (var blk in _blocks) r.Shorts(blk);
		r.UInts(_blockRgb);
		r.Shorts(_idctTmp);
		_inFifo.Clear(); int ni = r.S32(); for (int i = 0; i < ni; i++) _inFifo.Enqueue(r.U16());
		_outFifo.Clear(); int no = r.S32(); for (int i = 0; i < no; i++) _outFifo.Enqueue(r.U32());
		_event.LoadState(r, g);
	}

	private void OnMdecEvent()
	{
		// State may have advanced between scheduling and firing (e.g. CMD3
		// reset cleared everything). Re-check before doing work.
		if (_state == MdecState.WritingMacroblock && _blockReadyCycles > 0)
		{
			_blockReadyCycles = 0;
			FireBlockCopyOut();
		}
		// Execute may decode the next MB (which sets _blockReadyCycles again
		// and lands us back in WritingMacroblock) or fall through to Idle if
		// the input bitstream is exhausted.
		Execute();
		UpdateDmaRequests();
		// If decoder is waiting on another copy-out, schedule the next fire.
		if (_blockReadyCycles > 0)
			_event.Schedule(_blockReadyCycles);
	}

	/// <summary>
	/// Recompute DMA0/DMA1 request lines based on current FIFO state and the
	/// MDEC1.STAT enable bits. Cheap (two int comparisons + two field writes
	/// when nothing changed), but called frequently, every WriteWord, every
	/// Execute step that drains the input FIFO, every CopyOutBlock, every
	/// DmaRead drain, every FireBlockCopyOut. See class-level rationale.
	/// </summary>
	private void UpdateDmaRequests()
	{
		if (_psx?.Dma == null) return;
		// DMA0 (MDECin): MDEC wants more bitstream when its input FIFO isn't
		// full and the DMA-in enable bit is set. Threshold 512 halfwords
		// matches the same value used by BuildStatus's STAT_DATA_IN_FIFO_FULL.
		bool inReq = _enableDmaIn && _inFifo.Count < 512;
		// DMA1 (MDECout): assert request whenever the DMA-out enable bit is set.
		// Do NOT gate on `_outFifo.Count > 0` here, the scenario A/B logic in
		// PsxDma.TransferOneMdecOutBlock already correctly handles the empty
		// case (wait if more bitstream is coming, drain-empty if game over-
		// asked at end of cmd1). Gating Tick on `_outFifo.Count > 0` would
		// deadlock the over-ask case (e.g. RE2 cmd1 #1 game asks 14x1920 +
		// one extra batch of 1920 words against ~26K of real output): the
		// 15th transfer's `PendingBlocks > 0` but `Request == false` would
		// keep TransferOneMdecOutBlock from ever running, and scenario B
		// would never fire to complete the channel.
		bool outReq = _enableDmaOut;
		_psx.Dma.SetRequest(0, inReq);
		_psx.Dma.SetRequest(1, outReq);
	}

	// Lifecycle

	public void Reset()
	{
		_state = MdecState.Idle;
		_enableDmaIn = false;
		_enableDmaOut = false;
		// Reset clears the output-depth status bits (25-26) to 0 (4-bit) like
		// real hardware, MDECSTAT reads 0x800400xx at idle, not 0x860400xx.
		// The first Decode-Macroblock command (cmd1) sets the real
		// depth, so this only affects the pre-decode idle status read.
		_outputDepth = DEPTH_4BIT;
		_outputSigned = false;
		_outputBit15 = false;
		_remainingHalfwords = 0;
		_currentBlock = 0;
		_currentCoefficient = 64;
		_currentQScale = 0;
		_inFifo.Clear();
		_outFifo.Clear();
		_diagCmd1Count = 0;
		_diagMacroblockCount = 0;
		_diagPrevCmd1Halfwords = _diagPrevCmd1MbCount = 0;
		_busyExtensionCycles = 0;
		_blockReadyCycles = 0;
		// Tear down any pending block-copy-out event. Reset can be triggered
		// mid-decode by an MMIO write to MDEC1 with bit 31 set, in which case Scheduler.Reset()
		// did NOT run and the event may still be active with a stale NextRunTime.
		// Deactivate explicitly.
		_event?.Deactivate();
		// Both DMA channels start de-asserted: enable bits off and FIFOs empty.
		UpdateDmaRequests();
	}

	/// <summary>
	/// Per-macroblock copy-out: move the staged RGB block from _blocks/_blockRgb
	/// into _outFifo so DMA1 can drain it. Fires after 2688 cycles of "MDEC busy"
	/// simulated wall time.
	/// Updates state to either DecodingMacroblock (if more input bits remain to
	/// decode another MB) or Idle (cmd1 finished).
	/// </summary>
	private void FireBlockCopyOut()
	{
		// Push the staged RGB into the output FIFO via the existing CopyOutBlock
		// path (which handles packing for depth 4/8/15/24).
		CopyOutBlock();
		_diagMacroblockCount++;
		// OutFIFO just gained 64..192 words; assert DMA1 request if enabled.
		// The actual SetRequest call happens in Tick (the only caller of
		// FireBlockCopyOut) after Execute returns, but we set it here too so
		// any code path that ends up here outside Tick still gets the signal.
		UpdateDmaRequests();

		if (_remainingHalfwords == 0)
		{
			// cmd1 fully drained. State returns to Idle; BUSY clears naturally
			// once the OutFIFO is drained by DMA1.
			_state = MdecState.Idle;
		}
		else
		{
			// More halfwords pending, set up for the next macroblock decode.
			// Execute() will run on the next tick / FIFO write and produce the
			// next staged MB.
			ResetDecoder();
			_state = MdecState.DecodingMacroblock;
		}
	}

	// Register I/O

	/// <summary>
	/// offset 0 = MDEC0: data register (output FIFO on read, command on write).
	/// offset 4 = MDEC1: status register (read), control register (write).
	/// </summary>
	public uint ReadWord(uint offset)
	{
		if (offset == 0)
		{
			if (_outFifo.TryDequeue(out uint val))
			{
				Execute();
				// OutFIFO just shrank by one word; if it's now empty we need to
				// drop the DMA1 request line so DMA1 stops asking.
				UpdateDmaRequests();
				return val;
			}
			return 0xFFFFFFFFu;
		}
		return BuildStatus();
	}

	public void WriteWord(uint offset, uint value)
	{
		if (offset == 0)
		{
			_inFifo.Enqueue((ushort)(value & 0xFFFF));
			_inFifo.Enqueue((ushort)(value >> 16));
			Execute();
			// Input FIFO just grew, possibly reaching full; re-evaluate DMA0.
			// (Also captures any Execute-triggered FIFO drains that asserted
			// DMA0 again, Execute itself doesn't update the lines.)
			UpdateDmaRequests();
		}
		else
		{
			if ((value & 0x80000000u) != 0) Reset();
			_enableDmaIn = (value & 0x40000000u) != 0;
			_enableDmaOut = (value & 0x20000000u) != 0;
			Execute();
			// The DMA enable bits just changed, even if FIFO state is stable,
			// the asserted-or-not decision depends on them. Must update.
			UpdateDmaRequests();
		}
	}

	// DMA interface

	// DMA ch0 (MDECin): feed compressed macroblock data from RAM.
	public void DmaWrite(uint[] words, int count)
	{
		for (int i = 0; i < count; i++)
		{
			_inFifo.Enqueue((ushort)(words[i] & 0xFFFF));
			_inFifo.Enqueue((ushort)(words[i] >> 16));
			Execute();
		}
		// Input FIFO levels changed; re-evaluate both lines (a long DmaWrite may
		// have triggered a state transition that drained OutFIFO).
		UpdateDmaRequests();
	}

	// DMA ch1 (MDECout): drain decoded pixels to RAM.
	// With back-pressure limiting the FIFO to one macroblock at a time (128 words for
	// 15-bit depth), a single DmaRead request can span many macroblocks. We loop:
	// decode one MB -> drain -> decode next MB -> drain ... until count words are delivered
	// or the input stream is exhausted.
	public int DmaRead(uint[] buf, int count)
	{
		int total = 0;
		while (total < count)
		{
			// If output FIFO is empty, try to decode the next macroblock.
			if (_outFifo.Count == 0) Execute();
			// Still empty -> input exhausted, stop.
			if (_outFifo.Count == 0) break;

			int n = Math.Min(count - total, _outFifo.Count);
			for (int i = 0; i < n; i++)
				buf[total + i] = _outFifo.Dequeue();
			total += n;
		}
		// OutFIFO drained; may now be empty -> drop DMA1 request.
		UpdateDmaRequests();
		return total;
	}

	// Main state machine
	private void Execute()
	{
		for (; ; )
		{
			switch (_state)
			{
				case MdecState.Idle:
					{
						if (_inFifo.Count < 2) return;
						// Don't start a new command while the previous cmd1's per-MB
						// processing-time extension is still counting down, real PSX
						// MDEC reports BUSY during this window and won't accept new
						// commands. Tick() will clear _busyExtensionCycles in due
						// course; Execute() will be re-invoked from ReadWord/WriteWord
						// when the next FIFO access happens.
						if (_busyExtensionCycles > 0) return;

						uint cw = _inFifo.Dequeue() | ((uint)_inFifo.Dequeue() << 16);

						// Clear OutFIFO on every new command entry. Stale words can
						// linger from a previous cmd1 in two cases:
						//   1. DMA1 over-ask scenario B (PsxDma.TransferOneMdecOutBlock)
						//      accepts empty blocks to complete the channel, but it
						//      doesn't drain residual data already in _outFifo.
						//   2. Any cmd2/cmd3 between cmd1s, these don't touch OutFIFO
						//      and leftover words from the previous cmd1 stay there.
						// If we don't clear, the next cmd1's MDEC output is APPENDED to
						// the stale words; DMA1 then drains [stale][new], shifting every
						// game-side VRAM upload by N words and producing visible tile/
						// quadrant duplication on FMV replay (RE2 Capcom intro restart).
						_outFifo.Clear();

						_outputDepth = (int)(cw >> 27) & 3;
						_outputSigned = (cw & (1u << 26)) != 0;
						_outputBit15 = (cw & (1u << 25)) != 0;

						int cmd = (int)(cw >> 29) & 7;
						switch (cmd)
						{
							case 1: // Decode Macroblock
								// Finalise the previous cmd1 stats (for the first 8 cmd1s) so we can
								// compare halfwords-fed vs MBs-decoded per cmd. If our DecodeRLE is
								// consuming more halfwords per MB than real PSX, the ratio will be
								// noticeably higher than the typical ~50 halfwords/MB.
								if (_diagCmd1Count > 0 && _diagCmd1Count <= 8)
								{
									int consumed = _diagPrevCmd1Halfwords - _remainingHalfwords;
									int mbs = _diagMacroblockCount - _diagPrevCmd1MbCount;
									int hwPerMb = mbs > 0 ? consumed / mbs : 0;
									PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Warn,
										$"[MDEC] cmd1 #{_diagCmd1Count} stats: declared={_diagPrevCmd1Halfwords}hw consumed={consumed}hw mbs={mbs} avgHwPerMb={hwPerMb} remaining={_remainingHalfwords}hw outFifo={_outFifo.Count}w");
								}

								_diagCmd1Count++;
								_remainingHalfwords = (int)(cw & 0xFFFF) * 2;
								_diagPrevCmd1Halfwords = _remainingHalfwords;
								_diagPrevCmd1MbCount = _diagMacroblockCount;
								_currentBlock = 0;
								_currentCoefficient = 64;
								_currentQScale = 0;
								_state = MdecState.DecodingMacroblock;
								break;

							case 2: // Set Quantization Table
								_remainingHalfwords = (16 + (((cw & 1) != 0) ? 16 : 0)) * 2;
								_state = MdecState.SetIqTable;
								PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
									$"[MDEC] cmd2 SetIQ: halfwords={_remainingHalfwords} chroma={(cw & 1) != 0}");
								break;

							case 3: // Set IDCT Scale Table
								_remainingHalfwords = 64; // 32 words = 64 halfwords
								_state = MdecState.SetScaleTable;
								PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info, "[MDEC] cmd3 SetScaleTable");
								break;

							default:
								// Unknown command: drain declared word count
								int drain = (int)(cw & 0xFFFF) * 2;
								while (drain > 0 && _inFifo.Count > 0)
								{ _inFifo.Dequeue(); drain--; }
								break;
						}
						break;
					}

				case MdecState.DecodingMacroblock:
					{
						bool done = (_outputDepth <= DEPTH_8BIT)
							? DecodeMonoMacroblock()
							: DecodeColorMacroblock();

						if (!done)
						{
							// Out of input mid-macroblock and none coming
							if (_remainingHalfwords == 0 && _currentBlock < 6)
							{
								ResetDecoder();
								_state = MdecState.Idle;
								break; // continue outer loop
							}
							return;
						}

						// One macroblock decoded into _blocks / _blockRgb staging.
						// We do NOT call CopyOutBlock here, that happens in Tick() / FireBlockCopyOut()
						// when the countdown fires. Returning out of the loop instead of
						// continuing to decode the next MB is what gives DMA1 the
						// real-PSX-rate stall it expects: while in WritingMacroblock,
						// IsDecoding is true and the OutFIFO is empty (only the
						// previously copied-out blocks are there, which DMA1 has
						// already drained).
						_state = MdecState.WritingMacroblock;
						_blockReadyCycles = CyclesPerMB;
						// Schedule the block-ready event so the copy-out fires
						// at CyclesPerMB cycles from now, previously the LegacyTick
						// path decremented _blockReadyCycles per 256-cycle batch.
						_event.Schedule(CyclesPerMB);
						return;
					}

				case MdecState.WritingMacroblock:
					{
						// Block decoded but the 2688-cycle copy-out event hasn't
						// fired yet. Tick() owns this, Execute can't make
						// forward progress here.
						return;
					}

				case MdecState.SetIqTable:
					{
						if (_inFifo.Count < _remainingHalfwords) return;
						HandleSetQuantTable();
						_state = MdecState.Idle;
						break;
					}

				case MdecState.SetScaleTable:
					{
						if (_inFifo.Count < _remainingHalfwords) return;
						HandleSetScaleTable();
						_state = MdecState.Idle;
						break;
					}

				default:
					return;
			}
		}
	}

	// Macroblock decoders

	private bool DecodeMonoMacroblock()
	{
		if (!DecodeRLE(_blocks[0], _iqY)) return false;
		IDCT(_blocks[0]);
		YUVToMono(_blocks[0]);
		ResetDecoder();
		return true;
	}

	private bool DecodeColorMacroblock()
	{
		for (; _currentBlock < 6; _currentBlock++)
		{
			byte[] qt = (_currentBlock >= 2) ? _iqY : _iqUv;
			if (!DecodeRLE(_blocks[_currentBlock], qt)) return false;
			IDCT(_blocks[_currentBlock]);
		}

		// Assemble 16*16 RGB from Cr/Cb (blocks[0/1]) and 4 Y blocks (blocks[2-5])
		YUVToRGB(0, 0, _blocks[0], _blocks[1], _blocks[2]);
		YUVToRGB(8, 0, _blocks[0], _blocks[1], _blocks[3]);
		YUVToRGB(0, 8, _blocks[0], _blocks[1], _blocks[4]);
		YUVToRGB(8, 8, _blocks[0], _blocks[1], _blocks[5]);

		ResetDecoder();
		return true;
	}

	private void ResetDecoder()
	{
		_currentBlock = 0;
		_currentCoefficient = 64;
		_currentQScale = 0;
	}

	// RLE bitstream decoder
	// Reads RLE-coded DCT coefficients from the input FIFO into one 8*8 block.
	// Returns true when the block is complete (end-of-block code encountered).
	// Can resume after a partial read if the FIFO ran dry.

	private bool DecodeRLE(short[] blk, byte[] qt)
	{
		if (_currentCoefficient == 64)
		{
			// Start of block: clear, skip padding words (0xFE00), read DC coefficient
			Array.Clear(blk, 0, 64);

			ushort n;
			for (; ; )
			{
				if (_inFifo.Count == 0 || _remainingHalfwords == 0) return false;
				n = _inFifo.Dequeue();
				_remainingHalfwords--;
				if (n != 0xFE00) break;
			}

			_currentQScale = (ushort)((n >> 10) & 0x3F);
			_currentCoefficient = 0;
			// DC coefficient. When qscale > 0, multiply by qt[0] then write to zigzag-mapped position.
			// When qscale == 0, the quantisation table is BYPASSED (val * 2 instead of val * qt[0]) AND
			// the result is written to the RAW coefficient position (no zigzag).
			// Previously we always multiplied by qt[0] and always zigzagged, which
			// produced subtly-wrong DCT inputs whenever a frame used qscale=0
			// blocks, corrupting the IDCT output for the rest of the stream.
			int dcRaw = SignExt10(n & 0x3FF);
			int dc = (_currentQScale == 0) ? dcRaw * 2 : dcRaw * qt[0];
			dc = Clamp(dc, -0x400, 0x3FF);
			if (_currentQScale > 0)
				blk[ZagZig[0]] = (short)dc;
			else
				blk[0] = (short)dc;
		}

		// Read AC coefficients until end-of-block or FIFO runs dry
		while (_inFifo.Count > 0 && _remainingHalfwords > 0)
		{
			ushort n = _inFifo.Dequeue();
			_remainingHalfwords--;

			int run = (n >> 10) & 0x3F;
			_currentCoefficient += run + 1;

			if (_currentCoefficient < 64)
			{
				int val = SignExt10(n & 0x3FF);
				int ac = (_currentQScale == 0)
					? val * 2
					: (val * qt[_currentCoefficient] * _currentQScale + 4) / 8;
				ac = Clamp(ac, -0x400, 0x3FF);
				// Same zigzag bypass as DC when qscale == 0.
				if (_currentQScale > 0)
					blk[ZagZig[_currentCoefficient]] = (short)ac;
				else
					blk[_currentCoefficient] = (short)ac;
			}

			if (_currentCoefficient >= 63)
			{
				_currentCoefficient = 64; // signal: block done
				return true;
			}
		}

		return false;
	}

	// IDCT
	// Two-pass 8*8 IDCT using the hardware-provided scale matrix.

	private void IDCT(short[] blk)
	{
		short[] src = blk;
		short[] dst = _idctTmp;

		for (int pass = 0; pass < 2; pass++)
		{
			for (int x = 0; x < 8; x++)
			{
				for (int y = 0; y < 8; y++)
				{
					int sum = 0;
					for (int z = 0; z < 8; z++)
					{
						sum += src[y + z * 8] * (_scaleTable[x + z * 8] / 8);
					}
					dst[x + y * 8] = (short)((sum + 0xFFF) / 0x2000);
				}
			}
			(src, dst) = (dst, src);
		}
	}

	// YCbCr -> RGB conversion
	// Fills an 8*8 sub-region of the 16*16 _blockRgb output buffer.
	// Cr and Cb are shared 8*8 chroma blocks, sub-sampled 2:1 in each axis.

	private void YUVToRGB(int xx, int yy,
							short[] crBlk, short[] cbBlk, short[] yBlk)
	{
		int addval = _outputSigned ? 0 : 0x80;
		for (int y = 0; y < 8; y++)
			for (int x = 0; x < 8; x++)
			{
				// Clamp/sign-extend Y/Cr/Cb to s9 [-128,127] BEFORE the colour
				// math, matching the mono path (YUVToMono). Hardware clamps
				// each IDCT element, so a Y of 200 must become 127 before R/G/B are added,
				// not after (otherwise overshooting blocks get the wrong luma + unclamped
				// chroma scaling). Near-no-op for in-range content.
				int cr = Clamp(SignExt9(crBlk[((x + xx) / 2) + ((y + yy) / 2) * 8]), -128, 127);
				int cb = Clamp(SignExt9(cbBlk[((x + xx) / 2) + ((y + yy) / 2) * 8]), -128, 127);
				int lum = Clamp(SignExt9(yBlk[x + y * 8]), -128, 127);

				int R = (int)(1.402f * cr);
				int G = (int)(-0.3437f * cb + -0.7143f * cr);
				int B = (int)(1.772f * cb);

				int ro = Clamp(lum + R, -128, 127) + addval;
				int go = Clamp(lum + G, -128, 127) + addval;
				int bo = Clamp(lum + B, -128, 127) + addval;

				_blockRgb[(x + xx) + (y + yy) * 16] =
					((uint)ro & 0xFF) | (((uint)go & 0xFF) << 8) | (((uint)bo & 0xFF) << 16);
			}
	}

	private void YUVToMono(short[] yBlk)
	{
		int addval = _outputSigned ? 0 : 0x80;
		for (int i = 0; i < 64; i++)
			_blockRgb[i] = (uint)(Clamp(SignExt9(yBlk[i]), -128, 127) + addval);
	}

	// Pack decoded pixels to output FIFO

	private void CopyOutBlock()
	{
		// Diagnostic: log a few pixel values from the first decoded macroblock
		// so we can verify the IDCT/YCbCr chain is producing real data.
		if (_diagMacroblockCount == 0)
		{
			uint p0 = _blockRgb[0];
			uint p7 = _blockRgb[7 + 7 * 16]; // bottom-right of top-left Y block
			uint p15 = _blockRgb[15 + 15 * 16]; // bottom-right of macroblock
												// _blockRgb is stored as 0x00BBGGRR (R=byte0, G=byte1, B=byte2)
			PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
				$"[MDEC] MB#0 pixels: p[0,0]=RGB({p0 & 0xFF},{(p0 >> 8) & 0xFF},{(p0 >> 16) & 0xFF})" +
				$" p[7,7]=RGB({p7 & 0xFF},{(p7 >> 8) & 0xFF},{(p7 >> 16) & 0xFF})" +
				$" p[15,15]=RGB({p15 & 0xFF},{(p15 >> 8) & 0xFF},{(p15 >> 16) & 0xFF})");
		}

		// Per-MB pixel statistics for FMV diagnostic.
		// Track max(R) and a flag for "any nonzero pixel" within the first ~10 cmd1's
		// so we can verify whether the MDEC IDCT is actually producing image data
		// vs returning zeros (which would explain "all FMV is black").
		if (_diagCmd1Count <= 10)
		{
			int maxR = 0, maxG = 0, maxB = 0, nonZero = 0;
			for (int i = 0; i < 256; i++)
			{
				uint p = _blockRgb[i];
				int r = (int)(p & 0xFF);
				int g = (int)((p >> 8) & 0xFF);
				int b = (int)((p >> 16) & 0xFF);
				if (r > maxR) maxR = r;
				if (g > maxG) maxG = g;
				if (b > maxB) maxB = b;
				if ((p & 0xFFFFFF) != 0) nonZero++;
			}
			// Log every 50th MB to keep volume reasonable; first MB always logged.
			if (_diagMacroblockCount == 0 || _diagMacroblockCount % 50 == 0)
			{
				PsxLog.Write(PsxLogCategory.DMA, PsxLogLevel.Info,
					$"[MDEC/STATS] cmd1#{_diagCmd1Count} MB#{_diagMacroblockCount} maxRGB=({maxR},{maxG},{maxB}) nonZero={nonZero}/256");
			}
		}

		switch (_outputDepth)
		{
			case DEPTH_4BIT:
				{
					// Mono MB is 8x8 = 64 pixels. 4 bits/pixel x 64 pixels = 256 bits = 8 words.
					// Previously looped i<256 (256 pixels), outputting 32 words, but
					// DecodeMonoMacroblock only fills _blockRgb[0..63], so words 9-32 were
					// stale data from a previous MB. Matches the "blocky orange/brown mess"
					// RE2 displayed at cmd1 #81 (its second FMV scene, in 4-bit mono mode).
					for (int i = 0; i < 64; i += 8)
					{
						uint v = 0;
						for (int j = 0; j < 8; j++)
							v |= ((_blockRgb[i + j] >> 4) & 0xFu) << (j * 4);
						_outFifo.Enqueue(v);
					}
					break;
				}

			case DEPTH_8BIT:
				{
					// Mono MB is 8x8 = 64 pixels. 8 bits/pixel x 64 pixels = 512 bits = 16 words.
					// Same fix as DEPTH_4BIT: previously looped i<256 producing 64 words
					// of partially-stale data.
					for (int i = 0; i < 64; i += 4)
						_outFifo.Enqueue(
							(_blockRgb[i] & 0xFF) |
							((_blockRgb[i + 1] & 0xFF) << 8) |
							((_blockRgb[i + 2] & 0xFF) << 16) |
							((_blockRgb[i + 3] & 0xFF) << 24));
					break;
				}

			case DEPTH_24BIT:
				{
					// Tightly pack 3 bytes per pixel (no padding) into 32-bit words
					uint rgb = 0;
					int phase = 0;
					for (int i = 0; i < 256; i++)
					{
						uint px = _blockRgb[i] & 0x00FFFFFFu;
						switch (phase)
						{
							case 0: rgb = px; phase = 1; break;
							case 1:
								rgb |= (px & 0xFF) << 24; _outFifo.Enqueue(rgb);
								rgb = px >> 8; phase = 2; break;
							case 2:
								rgb |= (px & 0xFFFF) << 16; _outFifo.Enqueue(rgb);
								rgb = px >> 16; phase = 3; break;
							case 3: rgb |= px << 8; _outFifo.Enqueue(rgb); phase = 0; break;
						}
					}
					break;
				}

			case DEPTH_15BIT:
			default:
				{
					// RGB555: 5 bits per channel, two pixels packed per 32-bit word.
					// Round-to-nearest 8->5, E8TO5 = min((c+4)>>3, 0x1F) instead of
					// truncating, truncation made every 15-bit FMV ~half a level
					// dark per channel. Channels are packed R=byte0, G=byte1, B=byte2.
					uint a = _outputBit15 ? 0x8000u : 0u;
					for (int i = 0; i < 256; i += 2)
					{
						uint c0 = _blockRgb[i];
						uint c1 = _blockRgb[i + 1];
						uint p0 = E8To5(c0) | (E8To5(c0 >> 8) << 5) | (E8To5(c0 >> 16) << 10) | a;
						uint p1 = E8To5(c1) | (E8To5(c1 >> 8) << 5) | (E8To5(c1 >> 16) << 10) | a;
						_outFifo.Enqueue(p0 | (p1 << 16));
					}
					break;
				}
		}
	}

	// Round-to-nearest 8-bit channel -> 5-bit.
	private static uint E8To5(uint channel) => System.Math.Min(((channel & 0xFF) + 4) >> 3, 0x1Fu);

	// Table setup

	private void HandleSetQuantTable()
	{
		// Luma table: 32 halfwords -> 64 bytes -> _iqY
		for (int i = 0; i < 32 && _inFifo.Count > 0 && _remainingHalfwords > 0; i++)
		{
			ushort hw = _inFifo.Dequeue();
			_remainingHalfwords--;
			_iqY[i * 2] = (byte)(hw & 0xFF);
			_iqY[i * 2 + 1] = (byte)(hw >> 8);
		}
		bool hasChroma = _remainingHalfwords >= 32;
		// Chroma table (present only when bit 0 of SetIqTab command = 1)
		if (hasChroma)
		{
			for (int i = 0; i < 32 && _inFifo.Count > 0; i++)
			{
				ushort hw = _inFifo.Dequeue();
				_remainingHalfwords--;
				_iqUv[i * 2] = (byte)(hw & 0xFF);
				_iqUv[i * 2 + 1] = (byte)(hw >> 8);
			}
		}
	}

	private void HandleSetScaleTable()
	{
		var raw = new ushort[64];
		for (int i = 0; i < 64 && _inFifo.Count > 0 && _remainingHalfwords > 0; i++)
		{
			raw[i] = _inFifo.Dequeue();
			_remainingHalfwords--;
		}

		for (int i = 0; i < 64; i++)
			_scaleTable[i] = (short)raw[i];
	}

	// Status register

	private uint BuildStatus()
	{
		uint s = 0;
		// Bit 31: Data-Out FIFO Empty (1 = empty), matches No$PSX
		if (_outFifo.Count == 0) s |= STAT_DATA_OUT_FIFO_EMPTY;
		// Bit 30: Data-In FIFO Full (1 = full)
		if (_inFifo.Count >= 512) s |= STAT_DATA_IN_FIFO_FULL;
		// Bit 29: Command Busy (1 = executing)
		// Set if either the state machine is non-idle OR the per-MB processing-time
		// extension is still counting down. See _busyExtensionCycles for rationale.
		if (_state != MdecState.Idle || _busyExtensionCycles > 0) s |= STAT_BUSY;
		// Bit 28: Data-In DMA Request (1 = DMA enabled AND input FIFO has space)
		if (_enableDmaIn && _inFifo.Count < 512) s |= STAT_DATA_IN_REQUEST;
		// Bit 27: Data-Out DMA Request (1 = DMA enabled AND output FIFO has data)
		if (_enableDmaOut && _outFifo.Count > 0) s |= STAT_DATA_OUT_REQUEST;
		// Bits 26-25: output depth
		s |= (uint)(_outputDepth & 3) << 25;
		// Bit 24: signed output
		if (_outputSigned) s |= (1u << 24);
		// Bit 23: bit15 set
		if (_outputBit15) s |= (1u << 23);
		// Bits 18-16: current block index (in range 0..5)
		s |= (uint)((_currentBlock + 4) % 6) << 16;
		// Bits 15-0: parameter-words-remaining minus 1. Per Nocash spec, this
		// underflows to 0xFFFF when no command is active (0 words remaining -> -1
		// -> 0xFFFF in u16). Games may poll for `(status & 0xFFFF) == 0xFFFF` to
		// detect "command finished"; the previous guard reported 0 in that case
		// which is indistinguishable from "1 word remaining".
		s |= (uint)(((_remainingHalfwords / 2) - 1) & 0xFFFF);
		return s;
	}

	// Bit manipulation helpers

	// Sign-extend a 10-bit value to s32.
	private static int SignExt10(int v) => (int)((uint)(v & 0x3FF) << 22) >> 22;

	// Sign-extend a 9-bit value to s32.
	private static int SignExt9(int v) => (int)((uint)(v & 0x1FF) << 23) >> 23;

	private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

	// Legacy no-op stubs kept so existing call sites compile
	public void OnDmaInComplete() { }
	public void OnDmaOutComplete() { }
}
