namespace PSXEmu;

/// <summary>
/// PSX Sound Processing Unit (SPU) - 24 ADPCM voices at 44,100 Hz stereo.
/// </summary>
public class PsxSpu
{
	private readonly Psx _psx;

	// SPU RAM (512 KB)
	public byte[] Ram { get; } = new byte[PsxConstants.SpuRamSize];

	// CD audio capture buffer write index (0..511, wraps).
	// Real PSX SPU auto-writes incoming CD audio L/R samples to SPU RAM:
	//   0x000-0x3FF (1 KB = 512 samples * 2 bytes): CD audio LEFT
	//   0x400-0x7FF (1 KB = 512 samples * 2 bytes): CD audio RIGHT
	// Games can poll these to detect "is CD audio still playing", RE2 likely
	// uses this during FMV to know when to advance to the next phase. Without
	// this, our capture buffer stays at zero and games see "audio dead",
	// triggering early FMV termination.
	private int _captureBufferIdx;

	// Transfer
	private uint _transferAddr;
	private uint _transferCurrent;

	// Control
	private ushort _spuCtrl;
	private ushort _transferControl; // SPUDTC (0x1F801DAC): real R/W transfer-mode register (Fixing a very old workaround)
	private ushort _mainVolLeft;
	private ushort _mainVolRight;
	private ushort _cdVolLeft;
	private ushort _cdVolRight;
	private uint _irqAddr;

	// SPU RAM IRQ latch (SPUSTAT bit 6). Set when an SPU RAM access matches
	// `_irqAddr` (8-byte-quantum) AND `SPUCNT.irq9_enable` (bit 6) is set.
	// Cleared when the game writes SPUCNT with irq9_enable=0, OR when the
	// game writes the bit cleared via SPUSTAT-write-1-to-clear pattern, we
	// model the simpler "cleared on irq9_enable 1->0 transition" path.
	//
	// Many PSX FMV pipelines (RE2, Driver 2, FFVII, MGS, Tekken intros) arm
	// this IRQ on a CD-capture buffer offset and block their decoder on it
	// as the "audio frame ready" pulse. Without it, FMV stalls forever.
	private bool _spuIrqFlag;

	// ---- Reverb (SPU's hardware reverb effect, IIR/comb/APF network) ----
	// Reverb output volumes (vLOUT 0x184 / vROUT 0x186), signed Q15.
	// NOTE: previously the code mis-routed 0x184/0x186 to _cdVol* (which is
	// the legacy "CDA boost" CD-audio volume at 0x1B0/0x1B2, a separate
	// register). The vLOUT/vROUT pair scales the reverb-unit output added
	// to the main L/R mix.
	private ushort _reverbVolLeft;
	private ushort _reverbVolRight;

	// Reverb work area pointers in halfword (16-bit) units.
	// `_reverbBaseAddr` = mBASE register x 4 (mBASE is in 8-byte units, so x8
	// is byte address; halfword address is x4). `_reverbCurrentAddr` is the
	// running write/read pointer that wraps within `[mBASE..end-of-RAM]`,
	// advanced by 1 every time ProcessReverb runs (= every other audio sample).
	private uint _reverbBaseAddr;
	private uint _reverbCurrentAddr;

	// EON, Reverb On register (0x198 lo / 0x19A hi). Bit `v` set means
	// voice `v`'s output is also fed into the reverb input bus. Voices NOT
	// in EON go straight to the main mix only.
	private uint _reverbOnRegister;
	// ENDX (0x1F801D9C/9E): per-voice "reached ADPCM loop-end" status. Set when
	// a voice consumes a block carrying the loop-end flag; cleared on key-on.
	// Sound drivers poll it to know a one-shot finished before re-keying. Read-only.
	private uint _endxRegister;
	// NON (0x1F801D94/96): per-voice noise-mode enable. PMON (0x1F801D90/92):
	// per-voice pitch-modulation (FM) enable. Noise LFSR (Dr. Hell waveform).
	private uint _noiseModeReg;
	private uint _pitchModReg;
	private uint _noiseCount;
	private uint _noiseLevel = 1;

	// 32 reverb configuration registers (0x1C0..0x1FE). Each is 16-bit;
	// some are signed coefficients (Q15), some are unsigned halfword
	// addresses into the reverb work area, some are signed halfword offsets.
	// Indexed by RVB_* constants below.
	private readonly ushort[] _reverbRegs = new ushort[32];

	// Resample buffers for the 39-tap FIR that converts between the SPU's
	// 44.1 kHz mix bus and the reverb unit's internal 22.05 kHz processing
	// rate. Both are duplicated (lower half == upper half) so the SIMD
	// reads in ProcessReverb don't need to wrap.
	//   downBuf: 128 samples = 64 logical positions x 2 mirrors
	//   upBuf:    64 samples = 32 logical positions x 2 mirrors
	private readonly short[][] _reverbDownBuf = { new short[128], new short[128] };
	private readonly short[][] _reverbUpBuf = { new short[64], new short[64] };

	// Resample position (0..63), incremented every audio sample. The
	// reverb unit's heavy IIR/comb/APF math runs only on odd positions
	// (the 22.05 kHz half-rate). Even positions just upsample-read.
	private int _reverbResamplePos;

	// Last-sample reverb output (held for the inactive half-rate phase).
	private short _lastReverbInLeft;
	private short _lastReverbInRight;

	// Reverb register named indices into _reverbRegs (each register is at
	// offset 0x1C0 + i*2 in the SPU register space).
	private const int RVB_dAPF1   = 0;   // APF1 read offset (signed halfwords back)
	private const int RVB_dAPF2   = 1;   // APF2 read offset
	private const int RVB_vIIR    = 2;   // IIR_ALPHA : same/diff-side IIR coefficient
	private const int RVB_vCOMB1  = 3;   // ACC_COEF_A : comb tap 1 gain
	private const int RVB_vCOMB2  = 4;   // ACC_COEF_B : comb tap 2 gain
	private const int RVB_vCOMB3  = 5;   // ACC_COEF_C : comb tap 3 gain
	private const int RVB_vCOMB4  = 6;   // ACC_COEF_D : comb tap 4 gain
	private const int RVB_vWALL   = 7;   // IIR_COEF : IIR feedback (wall reflection)
	private const int RVB_vAPF1   = 8;   // FB_ALPHA : APF1 feedback gain
	private const int RVB_vAPF2   = 9;   // FB_X : APF2 feedback gain
	private const int RVB_mLSAME  = 10;  // IIR_DEST_A0 : same-side reflect L address
	private const int RVB_mRSAME  = 11;  // IIR_DEST_A1 : same-side reflect R address
	private const int RVB_mLCOMB1 = 12;  // ACC_SRC_A0 : comb tap 1 L address
	private const int RVB_mRCOMB1 = 13;  // ACC_SRC_A1 : comb tap 1 R address
	private const int RVB_mLCOMB2 = 14;  // ACC_SRC_B0 : comb tap 2 L address
	private const int RVB_mRCOMB2 = 15;  // ACC_SRC_B1 : comb tap 2 R address
	private const int RVB_dLSAME  = 16;  // IIR_SRC_A0 : same-side IIR source L
	private const int RVB_dRSAME  = 17;  // IIR_SRC_A1 : same-side IIR source R
	private const int RVB_mLDIFF  = 18;  // IIR_DEST_B0 : diff-side reflect L address
	private const int RVB_mRDIFF  = 19;  // IIR_DEST_B1 : diff-side reflect R address
	private const int RVB_mLCOMB3 = 20;  // ACC_SRC_C0 : comb tap 3 L address
	private const int RVB_mRCOMB3 = 21;  // ACC_SRC_C1 : comb tap 3 R address
	private const int RVB_mLCOMB4 = 22;  // ACC_SRC_D0 : comb tap 4 L address
	private const int RVB_mRCOMB4 = 23;  // ACC_SRC_D1 : comb tap 4 R address
	private const int RVB_dLDIFF  = 24;  // IIR_SRC_B0 : diff-side IIR source L
	private const int RVB_dRDIFF  = 25;  // IIR_SRC_B1 : diff-side IIR source R
	private const int RVB_mLAPF1  = 26;  // MIX_DEST_A0 : APF1 L delay-line address
	private const int RVB_mRAPF1  = 27;  // MIX_DEST_A1 : APF1 R delay-line address
	private const int RVB_mLAPF2  = 28;  // MIX_DEST_B0 : APF2 L delay-line address
	private const int RVB_mRAPF2  = 29;  // MIX_DEST_B1 : APF2 R delay-line address
	private const int RVB_vLIN    = 30;  // IN_COEF_L : input gain L
	private const int RVB_vRIN    = 31;  // IN_COEF_R : input gain R

	// 20-tap reverb FIR resample coefficients (per PSX-SPX). The full filter
	// is 39 taps but every other coefficient is zero, so they're omitted; the
	// center tap (0x4000 at FIR position 19) is also handled separately.
	private static readonly int[] ReverbResampleCoeffs = {
		-0x0001,  0x0002, -0x000A,  0x0023, -0x0067,  0x010A, -0x0268,  0x0534,
		-0x0B90,  0x2806,  0x2806, -0x0B90,  0x0534, -0x0268,  0x010A, -0x0067,
		 0x0023, -0x000A,  0x0002, -0x0001,
	};

	// Output
	public short[] OutputBuffer { get; } = new short[PsxConstants.MaxSpuSamplesPerFrame * 2];
	public int SamplesWritten { get; private set; }
	/// <summary>Number of XA-ADPCM shorts (L+R interleaved) currently buffered but not yet mixed.</summary>
	public int XaQueueLength => _xaAudioQueue.Count;
	public int LastFrameSamplesWritten { get; private set; }
	public int LastFrameActiveVoices { get; private set; }
	public int LastFramePeakAbs { get; private set; }
	public int LastFrameClippedSamples { get; private set; }
	public int LastFrameKeyOns { get; private set; }
	public int LastFrameKeyOffs { get; private set; }

	// ---- Voice state (parallel arrays, 24 voices) ----
	private const int NumVoices = 24;

	private readonly short[] _vVolLeft = new short[NumVoices];
	private readonly short[] _vVolRight = new short[NumVoices];
	// Per-voice L/R + main L/R volume sweeps (fixed level or running envelope).
	private readonly VolumeSweep[] _vVolSweepL = new VolumeSweep[NumVoices];
	private readonly VolumeSweep[] _vVolSweepR = new VolumeSweep[NumVoices];
	private VolumeSweep _mainVolSweepL;
	private VolumeSweep _mainVolSweepR;
	private readonly ushort[] _vPitch = new ushort[NumVoices];
	private readonly uint[] _vStartAddr = new uint[NumVoices];
	private readonly uint[] _vRepeatAddr = new uint[NumVoices];
	private readonly uint[] _vAdsrRaw = new uint[NumVoices];  // lo | hi<<16

	private readonly bool[] _vActive = new bool[NumVoices];
	private readonly uint[] _vCurAddr = new uint[NumVoices];
	private readonly int[] _vCounter = new int[NumVoices];   // sub-sample counter (0..0xFFF); bits[11:4] = Gauss interp index
	private readonly int[] _vOld = new int[NumVoices];
	private readonly int[] _vOlder = new int[NumVoices];

	// Decoded sample buffer: [0..2] = last 3 samples of previous block (for Gauss window), [3..30] = current 28 samples
	private readonly int[][] _vSamples = new int[NumVoices][];
	private readonly int[] _vSampleIdx = new int[NumVoices];    // 0..27 = index into current block
	private readonly int[] _vLoopFlag = new int[NumVoices];    // last block's loop flags

	// PSX Gaussian interpolation table (512 entries)
	private static readonly short[] GaussTable = {
		-0x001, -0x001, -0x001, -0x001, -0x001, -0x001, -0x001, -0x001,
		-0x001, -0x001, -0x001, -0x001, -0x001, -0x001, -0x001, -0x001,
		 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0000, 0x0001,
		 0x0001, 0x0001, 0x0001, 0x0002, 0x0002, 0x0002, 0x0003, 0x0003,
		 0x0003, 0x0004, 0x0004, 0x0005, 0x0005, 0x0006, 0x0007, 0x0007,
		 0x0008, 0x0009, 0x0009, 0x000A, 0x000B, 0x000C, 0x000D, 0x000E,
		 0x000F, 0x0010, 0x0011, 0x0012, 0x0013, 0x0015, 0x0016, 0x0018,
		 0x0019, 0x001B, 0x001C, 0x001E, 0x0020, 0x0021, 0x0023, 0x0025,
		 0x0027, 0x0029, 0x002C, 0x002E, 0x0030, 0x0033, 0x0035, 0x0038,
		 0x003A, 0x003D, 0x0040, 0x0043, 0x0046, 0x0049, 0x004D, 0x0050,
		 0x0054, 0x0057, 0x005B, 0x005F, 0x0063, 0x0067, 0x006B, 0x006F,
		 0x0074, 0x0078, 0x007D, 0x0082, 0x0087, 0x008C, 0x0091, 0x0096,
		 0x009C, 0x00A1, 0x00A7, 0x00AD, 0x00B3, 0x00BA, 0x00C0, 0x00C7,
		 0x00CD, 0x00D4, 0x00DB, 0x00E3, 0x00EA, 0x00F2, 0x00FA, 0x0101,
		 0x010A, 0x0112, 0x011B, 0x0123, 0x012C, 0x0135, 0x013F, 0x0148,
		 0x0152, 0x015C, 0x0166, 0x0171, 0x017B, 0x0186, 0x0191, 0x019C,
		 0x01A8, 0x01B4, 0x01C0, 0x01CC, 0x01D9, 0x01E5, 0x01F2, 0x0200,
		 0x020D, 0x021B, 0x0229, 0x0237, 0x0246, 0x0255, 0x0264, 0x0273,
		 0x0283, 0x0293, 0x02A3, 0x02B4, 0x02C4, 0x02D6, 0x02E7, 0x02F9,
		 0x030B, 0x031D, 0x0330, 0x0343, 0x0356, 0x036A, 0x037E, 0x0392,
		 0x03A7, 0x03BC, 0x03D1, 0x03E7, 0x03FC, 0x0413, 0x042A, 0x0441,
		 0x0458, 0x0470, 0x0488, 0x04A0, 0x04B9, 0x04D2, 0x04EC, 0x0506,
		 0x0520, 0x053B, 0x0556, 0x0572, 0x058E, 0x05AA, 0x05C7, 0x05E4,
		 0x0601, 0x061F, 0x063E, 0x065C, 0x067C, 0x069B, 0x06BB, 0x06DC,
		 0x06FD, 0x071E, 0x0740, 0x0762, 0x0784, 0x07A7, 0x07CB, 0x07EF,
		 0x0813, 0x0838, 0x085D, 0x0883, 0x08A9, 0x08D0, 0x08F7, 0x091E,
		 0x0946, 0x096F, 0x0998, 0x09C1, 0x09EB, 0x0A16, 0x0A40, 0x0A6C,
		 0x0A98, 0x0AC4, 0x0AF1, 0x0B1E, 0x0B4C, 0x0B7A, 0x0BA9, 0x0BD8,
		 0x0C07, 0x0C38, 0x0C68, 0x0C99, 0x0CCB, 0x0CFD, 0x0D30, 0x0D63,
		 0x0D97, 0x0DCB, 0x0E00, 0x0E35, 0x0E6B, 0x0EA1, 0x0ED7, 0x0F0F,
		 0x0F46, 0x0F7F, 0x0FB7, 0x0FF1, 0x102A, 0x1065, 0x109F, 0x10DB,
		 0x1116, 0x1153, 0x118F, 0x11CD, 0x120B, 0x1249, 0x1288, 0x12C7,
		 0x1307, 0x1347, 0x1388, 0x13C9, 0x140B, 0x144D, 0x1490, 0x14D4,
		 0x1517, 0x155C, 0x15A0, 0x15E6, 0x162C, 0x1672, 0x16B9, 0x1700,
		 0x1747, 0x1790, 0x17D8, 0x1821, 0x186B, 0x18B5, 0x1900, 0x194B,
		 0x1996, 0x19E2, 0x1A2E, 0x1A7B, 0x1AC8, 0x1B16, 0x1B64, 0x1BB3,
		 0x1C02, 0x1C51, 0x1CA1, 0x1CF1, 0x1D42, 0x1D93, 0x1DE5, 0x1E37,
		 0x1E89, 0x1EDC, 0x1F2F, 0x1F82, 0x1FD6, 0x202A, 0x207F, 0x20D4,
		 0x2129, 0x217F, 0x21D5, 0x222C, 0x2282, 0x22DA, 0x2331, 0x2389,
		 0x23E1, 0x2439, 0x2492, 0x24EB, 0x2545, 0x259E, 0x25F8, 0x2653,
		 0x26AD, 0x2708, 0x2763, 0x27BE, 0x281A, 0x2876, 0x28D2, 0x292E,
		 0x298B, 0x29E7, 0x2A44, 0x2AA1, 0x2AFF, 0x2B5C, 0x2BBA, 0x2C18,
		 0x2C76, 0x2CD4, 0x2D33, 0x2D91, 0x2DF0, 0x2E4F, 0x2EAE, 0x2F0D,
		 0x2F6C, 0x2FCC, 0x302B, 0x308B, 0x30EA, 0x314A, 0x31AA, 0x3209,
		 0x3269, 0x32C9, 0x3329, 0x3389, 0x33E9, 0x3449, 0x34A9, 0x3509,
		 0x3569, 0x35C9, 0x3629, 0x3689, 0x36E8, 0x3748, 0x37A8, 0x3807,
		 0x3867, 0x38C6, 0x3926, 0x3985, 0x39E4, 0x3A43, 0x3AA2, 0x3B00,
		 0x3B5F, 0x3BBD, 0x3C1B, 0x3C79, 0x3CD7, 0x3D35, 0x3D92, 0x3DEF,
		 0x3E4C, 0x3EA9, 0x3F05, 0x3F62, 0x3FBD, 0x4019, 0x4074, 0x40D0,
		 0x412A, 0x4185, 0x41DF, 0x4239, 0x4292, 0x42EB, 0x4344, 0x439C,
		 0x43F4, 0x444C, 0x44A3, 0x44FA, 0x4550, 0x45A6, 0x45FC, 0x4651,
		 0x46A6, 0x46FA, 0x474E, 0x47A1, 0x47F4, 0x4846, 0x4898, 0x48E9,
		 0x493A, 0x498A, 0x49D9, 0x4A29, 0x4A77, 0x4AC5, 0x4B13, 0x4B5F,
		 0x4BAC, 0x4BF7, 0x4C42, 0x4C8D, 0x4CD7, 0x4D20, 0x4D68, 0x4DB0,
		 0x4DF7, 0x4E3E, 0x4E84, 0x4EC9, 0x4F0E, 0x4F52, 0x4F95, 0x4FD7,
		 0x5019, 0x505A, 0x509A, 0x50DA, 0x5118, 0x5156, 0x5194, 0x51D0,
		 0x520C, 0x5247, 0x5281, 0x52BA, 0x52F3, 0x532A, 0x5361, 0x5397,
		 0x53CC, 0x5401, 0x5434, 0x5467, 0x5499, 0x54CA, 0x54FA, 0x5529,
		 0x5558, 0x5585, 0x55B2, 0x55DE, 0x5609, 0x5632, 0x565B, 0x5684,
		 0x56AB, 0x56D1, 0x56F6, 0x571B, 0x573E, 0x5761, 0x5782, 0x57A3,
		 0x57C3, 0x57E2, 0x57FF, 0x581C, 0x5838, 0x5853, 0x586D, 0x5886,
		 0x589E, 0x58B5, 0x58CB, 0x58E0, 0x58F4, 0x5907, 0x5919, 0x592A,
		 0x593A, 0x5949, 0x5958, 0x5965, 0x5971, 0x597C, 0x5986, 0x598F,
		 0x5997, 0x599E, 0x59A4, 0x59A9, 0x59AD, 0x59B0, 0x59B2, 0x59B3,
	};

	// ADSR
	private readonly int[] _vAdsrVol = new int[NumVoices];   // 0..0x7FFF
	private readonly int[] _vAdsrPhase = new int[NumVoices];   // 0=attack,1=decay,2=sustain,3=release

	// Per-voice "last volume", the sample value after ADSR envelope is applied.
	// Captured into SPU RAM at offsets 0x800 (voice 1) and 0xC00 (voice 3) every
	// output sample by the capture-buffer mechanism. Used by games to
	// observe per-voice activity without polling per-voice registers; also drives
	// pitch modulation for voices that use the previous voice as PMOD source.
	private readonly int[] _vLastVolume = new int[NumVoices];
	private readonly int[] _vAdsrCtr = new int[NumVoices];   // countdown until next step

	// ADPCM filter coefficients (positive, negative)
	private static readonly int[] CoeffF0 = { 0, 60, 115, 98, 122 };
	private static readonly int[] CoeffF1 = { 0, 0, -52, -55, -60 };

	public PsxSpu(Psx psx)
	{
		_psx = psx;
		for (int v = 0; v < NumVoices; v++)
			_vSamples[v] = new int[31]; // [0..2] = last 3 of prev block (Gauss window), [3..30] = current 28 samples

		// Sample event: fires every CpuCyclesPerSample cycles to generate
		// one audio sample. The Interval = Period means a regular cadence;
		// re-scheduling adds Interval to the previous deadline, so no drift
		// even when fires happen late. Activated in Reset().
		_sampleEvent = new TimingEvent(
			"SpuSample",
			PsxConstants.CpuCyclesPerSample,
			PsxConstants.CpuCyclesPerSample,
			(param, ticksToExecute, _) => ((PsxSpu)param).OnSampleEvent(ticksToExecute),
			this);
	}

	/// <summary>
	/// Sample-generation event callback. Replaces the LegacyTick path's <c>Spu.SyncPendingSamples()</c> call.
	/// Receives elapsed CPU cycles since the last fire (typically <c>CpuCyclesPerSample</c>;
	/// larger if the dispatcher ran late or InvokeEarly was triggered) and
	/// runs the sample-emission pipeline once per 768-cycle boundary.
	/// </summary>
	private void OnSampleEvent(int ticksToExecute)
	{
		if (ticksToExecute <= 0) return;
		AdvanceCycles(ticksToExecute);
	}

	public void Reset()
	{
		// Scan BIOS ROM for ADPCM-like data blocks (valid header byte patterns).
		// This helps locate where the boot sound data lives, even if the BIOS never uploads it.
		ScanBiosForAdpcm();

		Array.Clear(Ram);
		_transferAddr = 0;
		_transferCurrent = 0;
		_spuCtrl = 0;
		_transferControl = 0;
		_mainVolLeft = _mainVolRight = 0;
		_mainVolSweepL = default;
		_mainVolSweepR = default;
		for (int sv = 0; sv < NumVoices; sv++) { _vVolSweepL[sv] = default; _vVolSweepR[sv] = default; }
		_cdVolLeft = _cdVolRight = 0;
		_irqAddr = 0;
		_spuIrqFlag = false;
		// Reverb state, all coefficients/addresses, work area pointers, and
		// resample buffers reset to zero. Games re-program the reverb regs
		// before enabling reverb_master_enable, so initial state doesn't matter
		// for correctness, but we want a clean buffer to avoid clicks on
		// reset-then-enable.
		_reverbVolLeft = _reverbVolRight = 0;
		_reverbBaseAddr = 0;
		_reverbCurrentAddr = 0;
		_reverbOnRegister = 0;
		_endxRegister = 0;
		_noiseModeReg = 0;
		_pitchModReg = 0;
		_noiseCount = 0;
		_noiseLevel = 1;
		Array.Clear(_reverbRegs);
		Array.Clear(_reverbDownBuf[0]); Array.Clear(_reverbDownBuf[1]);
		Array.Clear(_reverbUpBuf[0]); Array.Clear(_reverbUpBuf[1]);
		_reverbResamplePos = 0;
		_lastReverbInLeft = _lastReverbInRight = 0;
		SamplesWritten = 0;
		_cycleAccum = 0;
		// Arm the sample event for the first 768-cycle deadline.
		// Schedule (not Activate) so it correctly re-inserts after Scheduler.Reset.
		_sampleEvent.Schedule(PsxConstants.CpuCyclesPerSample);
		_xaAudioQueue.Clear();
		Array.Clear(_xaRingL);
		Array.Clear(_xaRingR);
		_xaRingP = 0;
		_xaRingSixstep = 6;
		_spuRamBytesWritten = 0;
		_captureBufferIdx = 0;
		_fifoWriteCount = 0;
		_fifoPcIdx = 0;
		_keyOnDumped = false;
		_seenSpuWrites.Clear();
		for (int v = 0; v < NumVoices; v++)
		{
			_vActive[v] = false;
			_vAdsrVol[v] = 0;
			_vAdsrPhase[v] = 3; // release
			_vCounter[v] = 0;
			_vOld[v] = 0;
			_vOlder[v] = 0;
			_vSampleIdx[v] = 28; // force block decode on first use
			Array.Clear(_vSamples[v]); // clear Gauss prev-block window too
		}
	}

	// ---- Save-state ----
	// SPU RAM + all registers + 24-voice state + reverb + XA streaming queue +
	// the 768-cycle sample event. OutputBuffer is the per-frame output handoff
	// (regenerated); _diag*/_frame*/_fifoPc*/_seenSpuWrites are debug; ZigzagTables
	// and Gauss/FIR coefficients are constants, all excluded.
	public void SaveState(StateWriter w)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		w.Bytes(Ram);
		w.U16(_spuCtrl); w.U16(_transferControl);
		w.U16(_mainVolLeft); w.U16(_mainVolRight);
		w.U16(_cdVolLeft); w.U16(_cdVolRight);
		w.U32(_irqAddr); w.Bool(_spuIrqFlag);
		w.U32(_transferAddr); w.U32(_transferCurrent);
		w.U16(_reverbVolLeft); w.U16(_reverbVolRight);
		w.U32(_reverbBaseAddr); w.U32(_reverbCurrentAddr); w.U32(_reverbOnRegister);
		w.UShorts(_reverbRegs);
		w.Shorts(_reverbDownBuf[0]); w.Shorts(_reverbDownBuf[1]);
		w.Shorts(_reverbUpBuf[0]); w.Shorts(_reverbUpBuf[1]);
		w.S32(_reverbResamplePos); w.S16(_lastReverbInLeft); w.S16(_lastReverbInRight);
		w.U32(_endxRegister); w.U32(_noiseModeReg); w.U32(_pitchModReg);
		w.U32(_noiseCount); w.U32(_noiseLevel);
		w.S32(_captureBufferIdx);
		_mainVolSweepL.SaveState(w); _mainVolSweepR.SaveState(w);
		w.Shorts(_vVolLeft); w.Shorts(_vVolRight);
		w.UShorts(_vPitch);
		w.UInts(_vStartAddr); w.UInts(_vRepeatAddr); w.UInts(_vAdsrRaw); w.UInts(_vCurAddr);
		w.Ints(_vCounter); w.Ints(_vOld); w.Ints(_vOlder);
		w.Ints(_vSampleIdx); w.Ints(_vLoopFlag);
		w.Ints(_vAdsrVol); w.Ints(_vAdsrPhase); w.Ints(_vLastVolume); w.Ints(_vAdsrCtr);
		for (int v = 0; v < NumVoices; v++) w.Bool(_vActive[v]);
		for (int v = 0; v < NumVoices; v++) w.Ints(_vSamples[v]);
		for (int v = 0; v < NumVoices; v++) _vVolSweepL[v].SaveState(w);
		for (int v = 0; v < NumVoices; v++) _vVolSweepR[v].SaveState(w);
		w.S32(_cycleAccum);
		w.S32(_xaAudioQueue.Count); foreach (var x in _xaAudioQueue) w.S16(x);
		w.Shorts(_xaRingL); w.Shorts(_xaRingR);
		w.S32(_xaRingP); w.S32(_xaRingSixstep);
		_sampleEvent.SaveState(w, g);
	}

	public void LoadState(StateReader r)
	{
		long g = _psx.Scheduler.GlobalTickCounter;
		r.Bytes(Ram);
		_spuCtrl = r.U16(); _transferControl = r.U16();
		_mainVolLeft = r.U16(); _mainVolRight = r.U16();
		_cdVolLeft = r.U16(); _cdVolRight = r.U16();
		_irqAddr = r.U32(); _spuIrqFlag = r.Bool();
		_transferAddr = r.U32(); _transferCurrent = r.U32();
		_reverbVolLeft = r.U16(); _reverbVolRight = r.U16();
		_reverbBaseAddr = r.U32(); _reverbCurrentAddr = r.U32(); _reverbOnRegister = r.U32();
		r.UShorts(_reverbRegs);
		r.Shorts(_reverbDownBuf[0]); r.Shorts(_reverbDownBuf[1]);
		r.Shorts(_reverbUpBuf[0]); r.Shorts(_reverbUpBuf[1]);
		_reverbResamplePos = r.S32(); _lastReverbInLeft = r.S16(); _lastReverbInRight = r.S16();
		_endxRegister = r.U32(); _noiseModeReg = r.U32(); _pitchModReg = r.U32();
		_noiseCount = r.U32(); _noiseLevel = r.U32();
		_captureBufferIdx = r.S32();
		_mainVolSweepL.LoadState(r); _mainVolSweepR.LoadState(r);
		r.Shorts(_vVolLeft); r.Shorts(_vVolRight);
		r.UShorts(_vPitch);
		r.UInts(_vStartAddr); r.UInts(_vRepeatAddr); r.UInts(_vAdsrRaw); r.UInts(_vCurAddr);
		r.Ints(_vCounter); r.Ints(_vOld); r.Ints(_vOlder);
		r.Ints(_vSampleIdx); r.Ints(_vLoopFlag);
		r.Ints(_vAdsrVol); r.Ints(_vAdsrPhase); r.Ints(_vLastVolume); r.Ints(_vAdsrCtr);
		for (int v = 0; v < NumVoices; v++) _vActive[v] = r.Bool();
		for (int v = 0; v < NumVoices; v++) r.Ints(_vSamples[v]);
		for (int v = 0; v < NumVoices; v++) _vVolSweepL[v].LoadState(r);
		for (int v = 0; v < NumVoices; v++) _vVolSweepR[v].LoadState(r);
		_cycleAccum = r.S32();
		_xaAudioQueue.Clear(); int nx = r.S32(); for (int i = 0; i < nx; i++) _xaAudioQueue.Enqueue(r.S16());
		r.Shorts(_xaRingL); r.Shorts(_xaRingR);
		_xaRingP = r.S32(); _xaRingSixstep = r.S32();
		_sampleEvent.LoadState(r, g);
	}

	private int _diagFrameCount;
	private int _spuRamBytesWritten;
	private int _framePeakAbs;
	private int _frameClippedSamples;
	private int _frameKeyOns;
	private int _frameKeyOffs;

	// PC-trace ring buffer: record last 16 FIFO-write PCs
	private readonly uint[] _fifoPcRing = new uint[16];
	private int _fifoPcIdx;
	private int _fifoWriteCount;

	// Fractional cycle accumulator for sample generation
	private int _cycleAccum;

	// Per-SPU sample event. Fires every 768 CPU cycles (= 1 audio sample at 44.1 kHz, see PsxConstants.CpuCyclesPerSample).
	// Reads/writes to SPU MMIO call SyncPendingSamples -> event.InvokeEarly,
	// which generates any in-flight samples right before the access.
	private TimingEvent _sampleEvent;

	// ---- XA-ADPCM (CD-ROM audio) ----
	// Resampled PCM queue: interleaved L/R shorts at 44100 Hz
	private readonly System.Collections.Generic.Queue<short> _xaAudioQueue = new();
	// Ring buffers for the zigzag FIR resampler (37800 -> 44100 Hz)
	private readonly short[] _xaRingL = new short[32];
	private readonly short[] _xaRingR = new short[32];
	private int _xaRingP = 0;
	private int _xaRingSixstep = 6;

	// 7 * 29 FIR coefficient tables for the zigzag 37800->44100 Hz resampler.
	private static readonly short[][] ZigzagTables =
	{
		new short[] {     0,     0,     0,     0,     0,    -2,    10,   -34,    65,   -84,    52,     9,  -266,  1024, -2680,  9036, 26516, -6016,  3021, -1571,   848,  -365,   107,    10,   -16,    17,    -8,     3,    -1 },
		new short[] {     0,     0,     0,    -2,     0,     3,   -19,    60,   -75,   162,  -227,   306,   -67,  -615,  3229, 29883, -4532,  2488, -1471,   882,  -424,   166,   -27,     5,     6,    -8,     3,    -1,     0 },
		new short[] {     0,     0,    -1,     3,    -2,    -5,    31,   -74,   179,  -402,   689,  -926,  1272, -1446, 31033, -1446,  1272,  -926,   689,  -402,   179,   -74,    31,    -5,    -2,     3,    -1,     0,     0 },
		new short[] {     0,    -1,     3,    -8,     6,     5,   -27,   166,  -424,   882, -1471,  2488, -4532, 29883,  3229,  -615,   -67,   306,  -227,   162,   -75,    60,   -19,     3,     0,    -2,     0,     0,     0 },
		new short[] {    -1,     3,    -8,    17,   -16,    10,   107,  -365,   848, -1571,  3021, -6016, 26516,  9036, -2680,  1024,  -266,     9,    52,   -84,    65,   -34,    10,    -1,     0,     1,     0,     0,     0 },
		new short[] {     2,    -8,    16,   -35,    43,    26,  -235,   635, -1352,  2810, -5882, 21472, 15367, -4681,  2062,  -839,   347,   -68,   -23,    70,   -35,    17,    -5,     0,     0,     0,     0,     0,     0 },
		new short[] {    -5,    17,   -35,    70,   -23,   -68,   347,  -839,  2062, -4681, 15367, 21472, -5882,  2810, -1352,   635,  -235,    26,    43,   -35,    16,    -8,     2,     0,     0,     0,     0,     0,     0 },
	};

	public void BeginFrame()
	{
		int targetSamplesPerFrame = _psx.TargetSpuSamplesPerFrame;
		LastFrameSamplesWritten = SamplesWritten;
		LastFramePeakAbs = _framePeakAbs;
		LastFrameClippedSamples = _frameClippedSamples;
		LastFrameKeyOns = _frameKeyOns;
		LastFrameKeyOffs = _frameKeyOffs;
		LastFrameActiveVoices = 0;
		for (int v = 0; v < NumVoices; v++)
			if (_vActive[v]) LastFrameActiveVoices++;

		SamplesWritten = 0;
		_framePeakAbs = 0;
		_frameClippedSamples = 0;
		_frameKeyOns = 0;
		_frameKeyOffs = 0;

		_diagFrameCount++;
		if (_diagFrameCount == 1 || _diagFrameCount % 300 == 0)
		{
			int activeCount = 0;
			for (int v = 0; v < NumVoices; v++)
				if (_vActive[v]) activeCount++;
			PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
				$"[DIAG] frame={_diagFrameCount} active={activeCount} spuRamWritten={_spuRamBytesWritten}");
		}

		if (_diagFrameCount > 1 &&
			(LastFrameClippedSamples > 0 || LastFrameSamplesWritten < (targetSamplesPerFrame - 8) || _diagFrameCount % 60 == 0))
		{
			PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
				$"[SPU/AUDIO] frame={_diagFrameCount} samples={LastFrameSamplesWritten}/{targetSamplesPerFrame} voices={LastFrameActiveVoices} keyOn={LastFrameKeyOns} keyOff={LastFrameKeyOffs} peak={LastFramePeakAbs} clipped={LastFrameClippedSamples}");
		}
	}

	/// <summary>
	/// Per-access SPU RAM IRQ check.
	///
	/// Call this from EVERY SPU RAM access path: voice ADPCM block fetches,
	/// CD-capture buffer writes, manual FIFO writes, DMA writes, and as a
	/// late-trigger after IRQ_ADDR / TRANSFER_ADDR writes to catch the case
	/// where the new address already matches an in-flight transfer.
	/// </summary>
	private void CheckRamIrq(uint addr)
	{
		// Bit 6 of SPUCNT = irq9_enable. No re-fire while the flag is latched.
		if ((_spuCtrl & 0x0040) == 0) return;
		if (_spuIrqFlag) return;
		if (_irqAddr != addr) return;
		_spuIrqFlag = true;
		_psx.Interrupts.Raise(PsxConstants.IrqSpu);
	}

	public void SyncPendingSamples()
	{
		// Catch up the sample-generation event to "now". InvokeEarly fires
		// OnSampleEvent with ticks_since_last (= elapsed CPU cycles since
		// last fire), which feeds AdvanceCycles. The event then re-sorts
		// so the next scheduled fire stays on the original 768-cycle
		// cadence (no drift from on-demand syncs).
		_sampleEvent?.InvokeEarly();
	}

	private void AdvanceCycles(int cpuCycles)
	{
		_cycleAccum += cpuCycles;
		int samplesToGen = _cycleAccum / PsxConstants.CpuCyclesPerSample;
		if (samplesToGen <= 0) return;
		_cycleAccum %= PsxConstants.CpuCyclesPerSample;

		// Clamp to buffer capacity
		int remaining = _psx.TargetSpuSamplesPerFrame - SamplesWritten;
		if (samplesToGen > remaining) samplesToGen = remaining;
		if (samplesToGen <= 0) return;

		GenerateSamples(samplesToGen);
	}

	// ---------------------------------------------------------------
	// Sample generation
	// ---------------------------------------------------------------

	private void GenerateSamples(int count)
	{
		for (int s = 0; s < count; s++)
		{
			// Advance the noise LFSR once per output sample.
			UpdateNoise();

			int mixL = 0, mixR = 0;
			int reverbInL = 0, reverbInR = 0;

			for (int v = 0; v < NumVoices; v++)
			{
				bool wasActive = _vActive[v];

				// Voice-processing gate, only skip entirely when BOTH the
				// voice is off AND SPU IRQ isn't armed. When SPU IRQ is armed,
				// we run full voice processing for off voices too, so the
				// pitch counter keeps advancing, block fetches keep happening,
				// and CheckRamIrq fires when the playback head sweeps past IRQ_ADDR.
				// ADSR volume is 0 for inactive voices, so they contribute no
				// audio. This is the fix for FMV pipelines that arm SPU
				// IRQ at a voice's playback address and wait for the IRQ
				// pulse, without this, key-off would freeze the playback
				// head before it reached IRQ_ADDR and the IRQ never fires.
				if (!wasActive && (_spuCtrl & 0x0040) == 0)
				{
					// Inactive voice: last_volume must be zero.
					_vLastVolume[v] = 0;
					continue;
				}

				// Inactive voice path-through (irq9_enable is set): silent
				// last_volume the same way, but still process for IRQ.
				if (!wasActive) _vLastVolume[v] = 0;

				// Decode block if needed (first sample after KeyOn, or block boundary)
				if (_vSampleIdx[v] >= 28)
				{
					DecodeBlock(v);
					// Active -> inactive transition mid-iteration (loop-end no
					// repeat in DecodeBlock): fade-out, skip rest of sample.
					// For voices that were ALREADY inactive entering this
					// iteration, keep going so the pitch counter advances
					// and we keep firing block-fetch IRQs on subsequent
					// boundaries.
					if (!_vActive[v] && wasActive) continue;

					// Process any counter carry from the previous block advance:
					// the counter might already be >= 0x1000 from the break at the block boundary.
					while (_vCounter[v] >= 0x1000 && _vSampleIdx[v] < 27)
					{
						_vCounter[v] -= 0x1000;
						_vSampleIdx[v]++;
					}
				}

				// Sample source: per-voice noise level for noise-enabled voices,
				// else Gaussian-interpolated ADPCM.
				int sample;
				if (((_noiseModeReg >> v) & 1u) != 0)
				{
					sample = (short)(ushort)_noiseLevel;
				}
				else
				{
					int interpIdx = (_vCounter[v] >> 4) & 0xFF;
					int bufPos = _vSampleIdx[v] + 3; // 0..2=prev, 3..30=current
					sample = GaussInterpolate(_vSamples[v], bufPos, interpIdx);
				}

				// Pitch step: optionally frequency-modulated by the PREVIOUS voice's
				// last_volume (FM), then folded to u16 and clamped to 0x3FFF.
				// Voice 0 cannot be modulated.
				int step = _vPitch[v];
				if (v > 0 && ((_pitchModReg >> v) & 1u) != 0)
				{
					int factor = Math.Clamp(_vLastVolume[v - 1], -0x8000, 0x7FFF) + 0x8000;
					step = ((short)_vPitch[v] * factor) >> 15;
				}
				step = Math.Min(step & 0xFFFF, 0x3FFF);

				// Advance pitch counter for next sample
				_vCounter[v] += step;
				while (_vCounter[v] >= 0x1000)
				{
					_vCounter[v] -= 0x1000;
					_vSampleIdx[v]++;
					if (_vSampleIdx[v] >= 28)
						break; // will decode at top of next iteration
				}

				// ADSR envelope
				TickAdsr(v);
				sample = (int)((long)sample * _vAdsrVol[v] >> 15);

				// Capture last_volume (post-ADSR, pre-L/R volume) for the capture
				// buffer mechanism + pitch modulation source.
				_vLastVolume[v] = sample;

				// Volume
				int voiceL = (int)((long)sample * _vVolSweepL[v].Level >> 15);
				int voiceR = (int)((long)sample * _vVolSweepR[v].Level >> 15);
				_vVolSweepL[v].Tick();
				_vVolSweepR[v].Tick();
				mixL += voiceL;
				mixR += voiceR;

				// Reverb routing: voices with their EON bit set also feed the
				// reverb input bus, in addition to going to the main mix.
				if (((_reverbOnRegister >> v) & 1u) != 0)
				{
					reverbInL += voiceL;
					reverbInR += voiceR;
				}
			}

			// SPUCNT bit 14 = un-mute. When CLEAR, hardware zeroes the voice +
			// reverb-input sums BEFORE reverb/CD/master.
			if ((_spuCtrl & 0x4000) == 0)
				mixL = mixR = reverbInL = reverbInR = 0;

			// Apply master volume to the saturated voice sum, then advance the main sweep.
			int outL = (int)((long)Sat16(mixL) * _mainVolSweepL.Level >> 15);
			int outR = (int)((long)Sat16(mixR) * _mainVolSweepR.Level >> 15);
			_mainVolSweepL.Tick();
			_mainVolSweepR.Tick();

			// Mix XA-ADPCM audio (CD-ROM voices / FMV audio), at 44100 Hz, 1 pair per sample
			short cdSampleL = 0;
			short cdSampleR = 0;
			if (_xaAudioQueue.Count >= 2)
			{
				cdSampleL = _xaAudioQueue.Dequeue();
				cdSampleR = _xaAudioQueue.Dequeue();
				int cdL = ((int)cdSampleL * (int)(short)_cdVolLeft) >> 15;
				int cdR = ((int)cdSampleR * (int)(short)_cdVolRight) >> 15;
				outL += cdL;
				outR += cdR;
				// CD audio can be routed to reverb if SPUCNT.cd_audio_reverb (bit 2)
				// is set, used by some games for echo/cathedral CD-music presets.
				if ((_spuCtrl & 0x0004) != 0)
				{
					reverbInL += cdL;
					reverbInR += cdR;
				}
			}

			// Reverb: always run ProcessReverb (it advances the resample
			// position and work-area pointer regardless), but pass zero input
			// when reverb master is disabled so the IIR rings out cleanly.
			short rvIn_L = (short)Sat16(reverbInL);
			short rvIn_R = (short)Sat16(reverbInR);
			ProcessReverb(rvIn_L, rvIn_R, out int rvOutL, out int rvOutR);
			outL += rvOutL;
			outR += rvOutR;

			// SPU Capture Buffer, 4 channels, 1KB each (1024 bytes = 512 s16 samples).
			// Real PSX SPU writes EVERY output sample to four circular buffers in SPU RAM:
			//   - Buffer 0 (0x000-0x3FF): CD audio LEFT
			//   - Buffer 1 (0x400-0x7FF): CD audio RIGHT
			//   - Buffer 2 (0x800-0xBFF): Voice 1 last_volume (post-ADSR sample)
			//   - Buffer 3 (0xC00-0xFFF): Voice 3 last_volume (post-ADSR sample)
			// Games poll these buffers to detect activity without per-register reads.
			{
				int idx = _captureBufferIdx * 2;
				ushort uL  = (ushort)cdSampleL;
				ushort uR  = (ushort)cdSampleR;
				ushort uV1 = (ushort)Math.Clamp(_vLastVolume[1], -0x8000, 0x7FFF);
				ushort uV3 = (ushort)Math.Clamp(_vLastVolume[3], -0x8000, 0x7FFF);
				// Buffer 0: CD L
				Ram[idx]          = (byte)(uL & 0xFF);
				Ram[idx + 1]      = (byte)(uL >> 8);
				// Buffer 1: CD R
				Ram[0x400 + idx]  = (byte)(uR & 0xFF);
				Ram[0x401 + idx]  = (byte)(uR >> 8);
				// Buffer 2: Voice 1 last_volume
				Ram[0x800 + idx]  = (byte)(uV1 & 0xFF);
				Ram[0x801 + idx]  = (byte)(uV1 >> 8);
				// Buffer 3: Voice 3 last_volume
				Ram[0xC00 + idx]  = (byte)(uV3 & 0xFF);
				Ram[0xC01 + idx]  = (byte)(uV3 >> 8);
				// SPU RAM IRQ check on each capture-buffer write. Most FMV pipelines
				// arm IRQ_ADDR somewhere in 0x000-0xFFF specifically to fire here.
				CheckRamIrq((uint)idx);
				CheckRamIrq((uint)(0x400 + idx));
				CheckRamIrq((uint)(0x800 + idx));
				CheckRamIrq((uint)(0xC00 + idx));
				_captureBufferIdx = (_captureBufferIdx + 1) & 0x1FF; // wrap at 512
			}

			if (outL < short.MinValue || outL > short.MaxValue) _frameClippedSamples++;
			if (outR < short.MinValue || outR > short.MaxValue) _frameClippedSamples++;
			int absL = Math.Abs(Math.Clamp(outL, short.MinValue, short.MaxValue));
			int absR = Math.Abs(Math.Clamp(outR, short.MinValue, short.MaxValue));
			if (absL > _framePeakAbs) _framePeakAbs = absL;
			if (absR > _framePeakAbs) _framePeakAbs = absR;
			OutputBuffer[SamplesWritten * 2] = (short)Math.Clamp(outL, short.MinValue, short.MaxValue);
			OutputBuffer[SamplesWritten * 2 + 1] = (short)Math.Clamp(outR, short.MinValue, short.MaxValue);
			SamplesWritten++;
		}
	}

	// Gaussian interpolation using PSX hardware table.
	// buf[0..2] = last 3 samples of previous block; buf[3..30] = 28 current-block samples.
	// bufPos = 3 + sampleIdx (so bufPos-3..bufPos gives the 4-tap window).
	private static int GaussInterpolate(int[] buf, int bufPos, int i)
	{
		int out32 = (int)GaussTable[0x0FF - i] * buf[bufPos - 3];
		out32 += (int)GaussTable[0x1FF - i] * buf[bufPos - 2];
		out32 += (int)GaussTable[0x100 + i] * buf[bufPos - 1];
		out32 += (int)GaussTable[0x000 + i] * buf[bufPos - 0];
		return out32 >> 15;
	}

	// ---------------------------------------------------------------
	// Noise generator, advanced once per output sample, NON voices output
	// _noiseLevel instead of interpolated ADPCM.
	// ---------------------------------------------------------------

	private static readonly byte[] NoiseWaveAdd = {
		1,0,0,1,0,1,1,0, 1,0,0,1,0,1,1,0, 1,0,0,1,0,1,1,0, 1,0,0,1,0,1,1,0,
		0,1,1,0,1,0,0,1, 0,1,1,0,1,0,0,1, 0,1,1,0,1,0,0,1, 0,1,1,0,1,0,0,1,
	};
	private static readonly int[] NoiseFreqAdd = { 0, 84, 140, 180, 210 };

	private void UpdateNoise()
	{
		uint noiseClock = ((uint)_spuCtrl >> 8) & 0x3Fu;
		uint level = (0x8000u >> (int)(noiseClock >> 2)) << 16;
		_noiseCount += 0x10000u + (uint)NoiseFreqAdd[noiseClock & 3];
		if ((_noiseCount & 0xFFFFu) >= (uint)NoiseFreqAdd[4])
		{
			_noiseCount += 0x10000;
			_noiseCount -= (uint)NoiseFreqAdd[noiseClock & 3];
		}
		if (_noiseCount < level) return;
		_noiseCount %= level;
		_noiseLevel = (_noiseLevel << 1) | (uint)NoiseWaveAdd[(_noiseLevel >> 10) & 63u];
	}

	// ---------------------------------------------------------------
	// Volume sweep / envelope, a volume register with bit 15 clear is a fixed
	// level ((bits 0-14 signed 15-bit) x 2); bit 15 set runs an ADSR-style
	// envelope (rate bits 0-6, exp b14, decrease b13, phase b12).
	// ---------------------------------------------------------------

	private struct VolumeEnvelope
	{
		public byte Rate;
		public bool Decreasing;
		public bool Exponential;
		public bool PhaseInvert;
		public uint Counter;
		public uint CounterIncrement;
		public int Step;

		public void Reset(int rate, int rateMask, bool decreasing, bool exponential, bool phaseInvert)
		{
			Rate = (byte)rate;
			Decreasing = decreasing;
			Exponential = exponential;
			PhaseInvert = phaseInvert && !(decreasing && exponential);
			Counter = 0;
			CounterIncrement = 0x8000;
			int baseStep = 7 - (rate & 3);
			Step = ((decreasing ^ phaseInvert) || (decreasing && exponential)) ? ~baseStep : baseStep;
			if (rate < 44) Step <<= (11 - (rate >> 2));
			else if (rate >= 48)
			{
				CounterIncrement >>= ((rate >> 2) - 11);
				if ((rate & rateMask) != rateMask)
					CounterIncrement = System.Math.Max(CounterIncrement, 1u);
			}
		}

		// Advances `level`; returns false when the envelope has reached its end.
		public bool Tick(ref int level)
		{
			uint inc = CounterIncrement;
			int s = Step;
			if (Exponential)
			{
				if (Decreasing) s = (s * level) >> 15;
				else if (level >= 0x6000)
				{
					if (Rate < 40) s >>= 2;
					else if (Rate >= 44) inc >>= 2;
					else { s >>= 1; inc >>= 1; }
				}
			}
			Counter += inc;
			if ((Counter & 0x8000) == 0) return true;
			Counter = 0;
			int newLevel = level + s;
			if (!Decreasing)
			{
				level = System.Math.Clamp(newLevel, -32768, 32767);
				return newLevel != (s < 0 ? -32768 : 32767);
			}
			if (PhaseInvert) level = System.Math.Clamp(newLevel, -32768, 0);
			else level = System.Math.Max(newLevel, 0);
			return newLevel == 0;
		}

		public void SaveState(StateWriter w)
		{
			w.U8(Rate); w.Bool(Decreasing); w.Bool(Exponential); w.Bool(PhaseInvert);
			w.U32(Counter); w.U32(CounterIncrement); w.S32(Step);
		}
		public void LoadState(StateReader r)
		{
			Rate = r.U8(); Decreasing = r.Bool(); Exponential = r.Bool(); PhaseInvert = r.Bool();
			Counter = r.U32(); CounterIncrement = r.U32(); Step = r.S32();
		}
	}

	private struct VolumeSweep
	{
		public VolumeEnvelope Envelope;
		public int Level;       // current applied level (s16 range, x2 of the fixed reg)
		public bool Active;     // sweep envelope running

		public void Reset(ushort reg)
		{
			if ((reg & 0x8000) == 0)
			{
				// Fixed volume: bits 0-14 are a signed 15-bit "volume / 2".
				Level = (((short)(reg << 1)) >> 1) * 2;
				Active = false;
				return;
			}
			Envelope.Reset(reg & 0x7F, 0x7F, (reg & 0x2000) != 0, (reg & 0x4000) != 0, (reg & 0x1000) != 0);
			Active = Envelope.CounterIncrement > 0;
		}

		public void Tick()
		{
			if (Active) Active = Envelope.Tick(ref Level);
		}

		public void SaveState(StateWriter w)
		{
			Envelope.SaveState(w);
			w.S32(Level); w.Bool(Active);
		}
		public void LoadState(StateReader r)
		{
			Envelope.LoadState(r);
			Level = r.S32(); Active = r.Bool();
		}
	}

	// ---------------------------------------------------------------
	// ADPCM block decode
	// ---------------------------------------------------------------

	private void DecodeBlock(int v)
	{
		uint addr = _vCurAddr[v];

		// Loop flag handling from previous block
		if ((_vLoopFlag[v] & 1) != 0)
		{
			// Voice reached the ADPCM loop-end flag, latch ENDX.
			// Hardware parks current_address at the repeat address on EVERY loop-end,
			// then silences the voice only when loop-repeat is clear, and not
			// even then if the voice is noise-enabled (noise keeps running).
			_endxRegister |= (1u << v);
			_vCurAddr[v] = _vRepeatAddr[v];
			if ((_vLoopFlag[v] & 2) == 0 && ((_noiseModeReg >> v) & 1u) == 0)
			{
				// End with no loop: silence voice (preserve prev-block window for smooth fade)
				_vActive[v] = false;
				_vAdsrVol[v] = 0;
				_vSampleIdx[v] = 0;
				return;
			}
			addr = _vCurAddr[v];
		}

		// Read 16 bytes from SPU RAM
		if (addr + 16 > PsxConstants.SpuRamSize)
		{
			_vActive[v] = false;
			_vSampleIdx[v] = 0;
			return;
		}

		// SPU RAM IRQ check on voice ADPCM block fetch. 16-byte block spans two
		// 8-byte chunks, so check both halves to catch IRQ_ADDR pointing into either half.
		CheckRamIrq(addr);
		CheckRamIrq(addr + 8);

		// Save last 3 samples of the old block into the Gaussian window (buf[0..2])
		_vSamples[v][0] = _vSamples[v][28]; // sampleIdx 25 of old block
		_vSamples[v][1] = _vSamples[v][29]; // sampleIdx 26
		_vSamples[v][2] = _vSamples[v][30]; // sampleIdx 27

		byte header1 = Ram[addr];
		byte header2 = Ram[addr + 1];
		int shift = Math.Min(header1 & 0xF, 12);
		int filter = (header1 >> 4) & 0xF; // 4-bit; indices 5-15 map to zero coefficients
		if (filter > 4) filter = 4;
		int flags = header2 & 0x7;

		int f0 = CoeffF0[filter];
		int f1 = CoeffF1[filter];
		int old = _vOld[v];
		int older = _vOlder[v];

		int si = 3; // fill buf[3..30] (= decoded samples 0..27)
		for (int i = 0; i < 14; i++)
		{
			byte b = Ram[addr + 2 + i];
			for (int nibbleIdx = 0; nibbleIdx < 2; nibbleIdx++)
			{
				int nibble = nibbleIdx == 0 ? (b & 0xF) : (b >> 4);
				// Sign-extend 4-bit nibble
				if (nibble >= 8) nibble -= 16;
				int s = (nibble << 12) >> shift;
				s = s + ((old * f0) >> 6) + ((older * f1) >> 6);
				s = Math.Clamp(s, -32768, 32767);
				_vSamples[v][si++] = s;
				older = old;
				old = s;
			}
		}

		_vOld[v] = old;
		_vOlder[v] = older;
		_vLoopFlag[v] = flags;
		_vSampleIdx[v] = 0;

		// Set loop start address if loop-start flag
		if ((flags & 4) != 0)
			_vRepeatAddr[v] = addr;

		_vCurAddr[v] = addr + 16;
	}

	// ---------------------------------------------------------------
	// ADSR envelope
	// ---------------------------------------------------------------

	private void TickAdsr(int v)
	{
		uint raw = _vAdsrRaw[v];
		int phase = _vAdsrPhase[v];
		int vol = _vAdsrVol[v];
		int target = 0;
		int rate = 0;
		int rateMask = 0x7F;
		bool decreasing = false;
		bool exponential = false;

		switch (phase)
		{
			case 0: // Attack
				target = 0x7FFF;
				rate = (int)((raw >> 8) & 0x7F);
				exponential = (raw & 0x8000) != 0;
				break;

			case 1: // Decay
				target = Math.Min((((int)(raw & 0xF)) + 1) * 0x800, 0x7FFF);
				rate = (int)((raw >> 4) & 0xF) << 2;
				rateMask = 0x1F << 2;
				decreasing = true;
				exponential = true;
				break;

			case 2: // Sustain
				rate = (int)((raw >> 22) & 0x7F);
				decreasing = (raw & 0x40000000u) != 0;
				exponential = (raw & 0x80000000u) != 0;
				break;

			case 3: // Release
				rate = (int)((raw >> 16) & 0x1F) << 2;
				rateMask = 0x1F << 2;
				decreasing = true;
				exponential = (raw & 0x200000u) != 0;
				break;

			default:
				return;
		}

		int counterIncrement;
		int step;
		AdsrRateToEnvelope(rate, rateMask, decreasing, exponential, out counterIncrement, out step);

		int thisIncrement = counterIncrement;
		int thisStep = step;
		if (exponential)
		{
			if (decreasing)
			{
				thisStep = (thisStep * vol) >> 15;
			}
			else if (vol >= 0x6000)
			{
				if (rate < 40)
				{
					thisStep >>= 2;
				}
				else if (rate >= 44)
				{
					thisIncrement >>= 2;
				}
				else
				{
					thisStep >>= 1;
					thisIncrement >>= 1;
				}
			}
		}

		_vAdsrCtr[v] += thisIncrement;
		if ((_vAdsrCtr[v] & 0x8000) == 0)
			return;
		_vAdsrCtr[v] = 0;

		int newVol = vol + thisStep;
		if (!decreasing)
		{
			vol = Math.Clamp(newVol, 0, 0x7FFF);
		}
		else
		{
			vol = Math.Max(newVol, 0);
		}

		if (phase == 0 && vol >= target)
		{
			vol = target;
			phase = 1;
		}
		else if (phase == 1 && vol <= target)
		{
			vol = target;
			phase = 2;
		}
		else if (phase == 3 && vol <= 0)
		{
			vol = 0;
			_vActive[v] = false;
		}

		_vAdsrVol[v] = vol;
		_vAdsrPhase[v] = phase;
	}

	private static void AdsrRateToEnvelope(int rate, int rateMask, bool decreasing, bool exponential, out int counterIncrement, out int step)
	{
		rate = Math.Clamp(rate, 0, 0x7F);
		counterIncrement = 0x8000;
		int baseStep = 7 - (rate & 3);
		step = (decreasing || (decreasing && exponential)) ? ~baseStep : baseStep;

		if (rate < 44)
		{
			step <<= (11 - (rate >> 2));
		}
		else if (rate >= 48)
		{
			counterIncrement >>= ((rate >> 2) - 11);
			if ((rate & rateMask) != rateMask)
				counterIncrement = Math.Max(counterIncrement, 1);
		}
	}

	// ---------------------------------------------------------------
	// Key on / off
	// ---------------------------------------------------------------

	private bool _keyOnDumped;

	private void KeyOn(int v)
	{
		_frameKeyOns++;
		_vCurAddr[v] = _vStartAddr[v];
		_vCounter[v] = 0;
		_vOld[v] = 0;
		_vOlder[v] = 0;
		_vSampleIdx[v] = 28; // trigger decode on first sample
		_vLoopFlag[v] = 0;
		_endxRegister &= ~(1u << v); // key-on clears ENDX
		_vAdsrVol[v] = 0;
		_vAdsrPhase[v] = 0; // attack
		_vAdsrCtr[v] = 0;
		_vActive[v] = true;
		// Clear the Gaussian prev-block window: voice starts from silence
		_vSamples[v][0] = _vSamples[v][1] = _vSamples[v][2] = 0;
		// On the first keyon after the FIFO writes, dump the ring of last-seen FIFO PCs
		if (!_keyOnDumped && _fifoWriteCount > 0)
		{
			_keyOnDumped = true;
			var sb = new System.Text.StringBuilder();
			sb.Append($"[SPU] FIFO write PCs (last {Math.Min(_fifoWriteCount, 16)} of {_fifoWriteCount} writes): ");
			int start = _fifoWriteCount <= 16 ? 0 : _fifoPcIdx;
			for (int i = 0; i < Math.Min(_fifoWriteCount, 16); i++)
				sb.Append($"0x{_fifoPcRing[(start + i) & 15]:X8} ");
			PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info, sb.ToString());
		}
	}

	private void KeyOff(int v)
	{
		_frameKeyOffs++;
		_vAdsrPhase[v] = 3; // release
	}

	// ---------------------------------------------------------------
	// Reverb (SPU hardware reverb effect, Mednafen-PSX algorithm.
	// ---------------------------------------------------------------

	private static int Sat16(int x)
	{
		if (x > 32767) return 32767;
		if (x < -32768) return -32768;
		return x;
	}

	/// <summary>
	/// Computes the byte address into <see cref="Ram"/> for a reverb access at
	/// halfword offset `address` past the current reverb work-area pointer,
	/// wrapping within `[mBASE..end-of-RAM]`. Negative offsets (used for APF
	/// look-back) work via uint underflow + the 18-bit MASK.
	/// </summary>
	private uint ReverbMemoryAddress(uint address)
	{
		// Mask to halfword count - 1 (SPU RAM is 512 KB = 256 K halfwords).
		const uint MASK = 0x3FFFFu;
		uint offset = _reverbCurrentAddr + (address & MASK);
		// If the sum overflowed past 18 bits, slide it back
		// into the work area by adding the base address.
		offset += _reverbBaseAddr & (uint)(((int)(offset << 13)) >> 31);
		return (offset & MASK) * 2u;
	}

	/// <summary>Reads a signed halfword from the reverb work area at
	/// `_reverbRegs[reg]` plus an optional halfword `offset` (may be negative
	/// for delay-line look-back).</summary>
	private short ReverbRead(int reg, int halfwordOffset = 0)
	{
		uint addr = ReverbMemoryAddress((uint)((_reverbRegs[reg] << 2) + halfwordOffset));
		// SPU RAM IRQ check on reverb reads, some FMV pipelines arm the IRQ
		// inside the reverb work area to pace audio decoding.
		CheckRamIrq(addr);
		if (addr + 1 >= PsxConstants.SpuRamSize) return 0;
		return (short)(Ram[addr] | (Ram[addr + 1] << 8));
	}

	/// <summary>Reads the work area at a precomputed halfword address (used
	/// for the FB_SRC look-back: `MIX_DEST - FB_SRC_*` in halfwords).</summary>
	private short ReverbReadAt(int halfwordAddr)
	{
		uint addr = ReverbMemoryAddress((uint)halfwordAddr);
		CheckRamIrq(addr);
		if (addr + 1 >= PsxConstants.SpuRamSize) return 0;
		return (short)(Ram[addr] | (Ram[addr + 1] << 8));
	}

	/// <summary>Writes a signed halfword into the reverb work area at
	/// `_reverbRegs[reg]`. Silently no-ops if reverb master enable is off
	/// (so the running pointer can advance without corrupting RAM).</summary>
	private void ReverbWrite(int reg, short value)
	{
		if ((_spuCtrl & 0x0080) == 0) return;
		uint addr = ReverbMemoryAddress((uint)(_reverbRegs[reg] << 2));
		if (addr + 1 >= PsxConstants.SpuRamSize) return;
		Ram[addr] = (byte)(value & 0xFF);
		Ram[addr + 1] = (byte)((value >> 8) & 0xFF);
		CheckRamIrq(addr);
	}

	/// <summary>
	/// Applies the SPU reverb effect for one audio sample. Heavy IIR/comb/APF
	/// math runs only every other call (at the 22.05 kHz half-rate); the
	/// inactive call just samples the upsample buffer. Output is gained by
	/// vLOUT/vROUT and returned via the out-params for the caller to add to
	/// the main L/R mix.
	/// </summary>
	private void ProcessReverb(short leftIn, short rightIn, out int leftOut, out int rightOut)
	{
		_lastReverbInLeft = leftIn;
		_lastReverbInRight = rightIn;

		// Push input into duplicated downsample buffer (positions [0..63] and
		// the mirror at [64..127], so FIR reads never need to wrap).
		int pos = _reverbResamplePos;
		_reverbDownBuf[0][pos | 0x00] = leftIn;
		_reverbDownBuf[0][pos | 0x40] = leftIn;
		_reverbDownBuf[1][pos | 0x00] = rightIn;
		_reverbDownBuf[1][pos | 0x40] = rightIn;

		int outL, outR;

		if ((pos & 1) != 0)
		{
			// Active half-rate phase: downsample + process + write upsample.

			short downL = (short)Sat16(ReverbDownsample(0, pos));
			short downR = (short)Sat16(ReverbDownsample(1, pos));
			short[] downsampled = { downL, downR };
			int[] outs = new int[2];

			short IIR_ALPHA = (short)_reverbRegs[RVB_vIIR];
			short IIR_COEF  = (short)_reverbRegs[RVB_vWALL];
			short ACC_A     = (short)_reverbRegs[RVB_vCOMB1];
			short ACC_B     = (short)_reverbRegs[RVB_vCOMB2];
			short ACC_C     = (short)_reverbRegs[RVB_vCOMB3];
			short ACC_D     = (short)_reverbRegs[RVB_vCOMB4];
			short FB_ALPHA  = (short)_reverbRegs[RVB_vAPF1];
			short FB_X      = (short)_reverbRegs[RVB_vAPF2];
			// FB_SRC_A/B are unsigned halfword offsets (treated as u16 for the
			// MIX_DEST - FB_SRC subtraction; the result wraps in u16 space).
			ushort FB_SRC_A = _reverbRegs[RVB_dAPF1];
			ushort FB_SRC_B = _reverbRegs[RVB_dAPF2];

			// IIR_DEST/SRC/ACC/MIX are halfword addresses into the work area.
			// Per channel (0 = L, 1 = R):
			//   IIR_SRC_A[0]=dLSAME,    IIR_SRC_A[1]=dRSAME    (XOR with 0 -> same side)
			//   IIR_SRC_B[0]=dLDIFF,    IIR_SRC_B[1]=dRDIFF    (XOR with 1 -> diff side)
			//   IIR_DEST_A[0]=mLSAME,   IIR_DEST_A[1]=mRSAME
			//   IIR_DEST_B[0]=mLDIFF,   IIR_DEST_B[1]=mRDIFF
			//   ACC_SRC_A[0]=mLCOMB1,   ACC_SRC_A[1]=mRCOMB1
			//   ACC_SRC_B[0]=mLCOMB2,   ACC_SRC_B[1]=mRCOMB2
			//   ACC_SRC_C[0]=mLCOMB3,   ACC_SRC_C[1]=mRCOMB3
			//   ACC_SRC_D[0]=mLCOMB4,   ACC_SRC_D[1]=mRCOMB4
			//   MIX_DEST_A[0]=mLAPF1,   MIX_DEST_A[1]=mRAPF1
			//   MIX_DEST_B[0]=mLAPF2,   MIX_DEST_B[1]=mRAPF2
			//   IN_COEF[0]=vLIN,        IN_COEF[1]=vRIN

			for (int channel = 0; channel < 2; channel++)
			{
				int iirSrcA  = channel == 0 ? RVB_dLSAME  : RVB_dRSAME;
				int iirSrcB  = channel == 0 ? RVB_dRDIFF  : RVB_dLDIFF;
				int iirDestA = channel == 0 ? RVB_mLSAME  : RVB_mRSAME;
				int iirDestB = channel == 0 ? RVB_mLDIFF  : RVB_mRDIFF;
				int accSrcA  = channel == 0 ? RVB_mLCOMB1 : RVB_mRCOMB1;
				int accSrcB  = channel == 0 ? RVB_mLCOMB2 : RVB_mRCOMB2;
				int accSrcC  = channel == 0 ? RVB_mLCOMB3 : RVB_mRCOMB3;
				int accSrcD  = channel == 0 ? RVB_mLCOMB4 : RVB_mRCOMB4;
				int mixDestA = channel == 0 ? RVB_mLAPF1  : RVB_mRAPF1;
				int mixDestB = channel == 0 ? RVB_mLAPF2  : RVB_mRAPF2;
				short IN_COEF = (short)_reverbRegs[channel == 0 ? RVB_vLIN : RVB_vRIN];

				int IIR_INPUT_A = 0, IIR_INPUT_B = 0;
				int IIR_A = 0, IIR_B = 0;

				if ((_spuCtrl & 0x0080) != 0)
				{
					// IIR input = (read(IIR_SRC) * IIR_COEF + downsampled * IN_COEF) / 2
					// All shifts >> 14 then >> 1 to compose >> 15 final scaling.
					IIR_INPUT_A = Sat16((((ReverbRead(iirSrcA) * IIR_COEF) >> 14) +
					                     ((downsampled[channel] * IN_COEF) >> 14)) >> 1);
					IIR_INPUT_B = Sat16((((ReverbRead(iirSrcB) * IIR_COEF) >> 14) +
					                     ((downsampled[channel] * IN_COEF) >> 14)) >> 1);

					// IIR_A = (IIR_INPUT_A * IIR_ALPHA + read(IIR_DEST_A,-1) * (1-IIR_ALPHA)) / 2
					// "iiasm(x)" handles the IIR_ALPHA == -32768 corner case (1-(-32768) = 32769 overflows s16).
					IIR_A = Sat16((((IIR_INPUT_A * IIR_ALPHA) >> 14) +
					               (Iiasm(ReverbRead(iirDestA, -1)) >> 14)) >> 1);
					IIR_B = Sat16((((IIR_INPUT_B * IIR_ALPHA) >> 14) +
					               (Iiasm(ReverbRead(iirDestB, -1)) >> 14)) >> 1);

					ReverbWrite(iirDestA, (short)IIR_A);
					ReverbWrite(iirDestB, (short)IIR_B);
				}

				// Comb filter, 4 delayed taps mixed with their gains.
				int ACC =
					((ReverbRead(accSrcA) * ACC_A) >> 14) +
					((ReverbRead(accSrcB) * ACC_B) >> 14) +
					((ReverbRead(accSrcC) * ACC_C) >> 14) +
					((ReverbRead(accSrcD) * ACC_D) >> 14);

				// All-pass filters with feedback look-back at MIX_DEST - FB_SRC.
				// FB_SRC_A/B are u16 halfword offsets stored in dAPF1/dAPF2.
				// Both operands zero-extend to int, the subtraction can go
				// negative if FB_SRC > MIX_DEST (intentional wrap).
				int fbAddrA = (_reverbRegs[mixDestA] - FB_SRC_A) << 2;
				int fbAddrB = (_reverbRegs[mixDestB] - FB_SRC_B) << 2;
				short FB_A = ReverbReadAt(fbAddrA);
				short FB_B = ReverbReadAt(fbAddrB);

				// MDA = APF1 inner sample (gets written back to MIX_DEST_A).
				int MDA = Sat16((ACC + ((FB_A * NegSat(FB_ALPHA)) >> 14)) >> 1);
				// MDB = APF2 inner sample (gets written back to MIX_DEST_B).
				int MDB = Sat16(FB_A + ((((MDA * FB_ALPHA) >> 14) +
				                          ((FB_B * NegSat(FB_X)) >> 14)) >> 1));

				// Final upsample-buffer write at half-rate position; duplicate
				// to mirror at +0x20 so upsample reads don't need to wrap.
				int upPos = pos >> 1;
				short outSample = (short)Sat16(FB_B + ((MDB * FB_X) >> 15));
				_reverbUpBuf[channel][upPos | 0x00] = outSample;
				_reverbUpBuf[channel][upPos | 0x20] = outSample;

				if ((_spuCtrl & 0x0080) != 0)
				{
					ReverbWrite(mixDestA, (short)MDA);
					ReverbWrite(mixDestB, (short)MDB);
				}

				outs[channel] = ReverbUpsample(channel, pos);
			}

			// Advance reverb work-area pointer (wraps from 0x40000 -> 0 -> mBASE).
			_reverbCurrentAddr = (_reverbCurrentAddr + 1) & 0x3FFFFu;
			if (_reverbCurrentAddr == 0)
				_reverbCurrentAddr = _reverbBaseAddr;

			outL = outs[0];
			outR = outs[1];
		}
		else
		{
			// Inactive phase: read the held upsample value at the FIR center.
			int idx = (((pos >> 1) - 19) & 0x1F) + 9;
			outL = _reverbUpBuf[0][idx];
			outR = _reverbUpBuf[1][idx];
		}

		// Advance position (mod 64 = 6 bits).
		_reverbResamplePos = (_reverbResamplePos + 1) & 0x3F;

		// Apply final reverb output volume (vLOUT/vROUT, signed Q15).
		leftOut  = (outL * (short)_reverbVolLeft)  >> 15;
		rightOut = (outR * (short)_reverbVolRight) >> 15;
	}

	/// <summary>
	/// IIR alpha-complement scaling for the prev-sample feedback term:
	/// `iiasm(x) = x * (32768 - IIR_ALPHA)`, with a corner case for
	/// IIR_ALPHA == -32768 where (32768 - (-32768)) = 65536 overflows s16.
	/// Returns int32 (caller shifts >> 14 to bring it back into s17 range).
	/// </summary>
	private int Iiasm(short insamp)
	{
		short alpha = (short)_reverbRegs[RVB_vIIR];
		if (alpha == -32768)
			return insamp == -32768 ? 0 : insamp * -65536;
		return insamp * (32768 - alpha);
	}

	/// <summary>Saturating arithmetic negation: avoids -(-32768) overflow.</summary>
	private static int NegSat(short v) => v == -32768 ? 0x7FFF : -v;

	/// <summary>
	/// Downsample FIR (39-tap polyphase, zeros pre-removed -> 20 nonzero
	/// non-center coefficients + 1 center tap of 0x4000). Reads are at
	/// `(pos - 38)` and span 39 consecutive entries in the duplicated buffer.
	/// </summary>
	private int ReverbDownsample(int channel, int pos)
	{
		short[] buf = _reverbDownBuf[channel];
		int srcBase = (pos - 38) & 0x3F;  // start of 39-sample window in [0..63]
		int acc = 0;
		// The total 20 non-center coefficients map to source
		// offsets {0..3, 8..11, 16..19, 24..27, 32..35} from srcBase.
		for (int g = 0; g < 5; g++)
		{
			int coeffBase = g * 4;
			int srcOff = g * 8;
			for (int i = 0; i < 4; i++)
				acc += buf[srcBase + srcOff + i] * ReverbResampleCoeffs[coeffBase + i];
		}
		// Add the center tap (FIR position 19) separately, then scale by >>15.
		acc = (acc + 0x4000 * (int)buf[srcBase + 19]) >> 15;
		return acc;
	}

	/// <summary>
	/// Upsample FIR, reads 20 consecutive half-rate samples from the upsample
	/// buffer and applies the 20-tap symmetric coefficient set. Center handled
	/// implicitly because the upsample buffer is at half-rate (no zero gaps).
	/// </summary>
	private int ReverbUpsample(int channel, int pos)
	{
		short[] buf = _reverbUpBuf[channel];
		int srcBase = ((pos >> 1) - 19) & 0x1F;  // 32-position buffer
		int acc = 0;
		for (int i = 0; i < 20; i++)
			acc += buf[srcBase + i] * ReverbResampleCoeffs[i];
		return Sat16(acc >> 14);
	}

	// ---------------------------------------------------------------
	// Register I/O
	// ---------------------------------------------------------------

	public uint ReadWord(uint offset)
	{
		SyncPendingSamples();
		return offset switch
		{
			0x1AA => _spuCtrl,
			0x1AE => ReadStat(),
			_ => ReadHalf(offset) | ((uint)ReadHalf(offset + 2) << 16),
		};
	}

	public ushort ReadHalf(uint offset)
	{
		SyncPendingSamples();
		// Voice registers: offsets 0x000..0x17F = v*16 + reg
		if (offset < 0x180)
		{
			int v = (int)(offset / 16);
			int reg = (int)(offset % 16);
			return VoiceReadHalf(v, reg);
		}

		// Reverb configuration registers 0x1C0..0x1FE, return the last
		// written value so games that read-modify-write reverb presets work.
		if (offset >= 0x1C0 && offset < 0x200 && (offset & 1) == 0)
			return _reverbRegs[(offset - 0x1C0) >> 1];

		return offset switch
		{
			0x180 => _mainVolLeft,
			0x182 => _mainVolRight,
			0x184 => _reverbVolLeft,                   // vLOUT
			0x186 => _reverbVolRight,                  // vROUT
			0x190 => (ushort)(_pitchModReg & 0xFFFF),            // PMON lo
			0x192 => (ushort)((_pitchModReg >> 16) & 0xFF),      // PMON hi
			0x194 => (ushort)(_noiseModeReg & 0xFFFF),           // NON lo
			0x196 => (ushort)((_noiseModeReg >> 16) & 0xFF),     // NON hi
			0x198 => (ushort)(_reverbOnRegister & 0xFFFF),       // EON lo (voices 0-15)
			0x19A => (ushort)((_reverbOnRegister >> 16) & 0xFF), // EON hi (voices 16-23)
			0x19C => (ushort)(_endxRegister & 0xFFFF),           // ENDX lo (voices 0-15)
			0x19E => (ushort)((_endxRegister >> 16) & 0xFF),     // ENDX hi (voices 16-23)
			0x1A2 => (ushort)(_reverbBaseAddr >> 2),   // mBASE (halfword addr / 4 = mBASE value)
			0x1A4 => (ushort)(_irqAddr / 8),          // IRQ address register
			0x1A6 => (ushort)(_transferAddr >> 3),     // Transfer address register
			0x1A8 => 0xFFFF,                             // Transfer data register (read returns 0xFFFF)
			0x1AA => _spuCtrl,
			0x1AC => _transferControl,
			0x1AE => ReadStat(),
			0x1B0 => _cdVolLeft,
			0x1B2 => _cdVolRight,
			_ => 0,
		};
	}

	private ushort VoiceReadHalf(int v, int reg)
	{
		if ((uint)v >= NumVoices) return 0;
		return reg switch
		{
			0 => (ushort)_vVolLeft[v],
			2 => (ushort)_vVolRight[v],
			4 => _vPitch[v],
			6 => (ushort)(_vStartAddr[v] >> 3),
			8 => (ushort)_vAdsrRaw[v],
			10 => (ushort)(_vAdsrRaw[v] >> 16),
			12 => (ushort)_vAdsrVol[v],
			14 => (ushort)(_vRepeatAddr[v] >> 3),
			_ => 0,
		};
	}

	private ushort ReadStat()
	{
		// SPUSTAT bits[5:0] mirror SPUCNT bits[5:0], copied on every SPUCNT write on
		// real hardware. The transfer-mode bits 4-5 ARE included: the BIOS upload loop never polls
		// "SPUSTAT & 0x7FF == 0" for ready (if it did, the dma_request bit 7 set just
		// below, long present, would already have broken audio), so mirroring 4-5 is
		// safe. Fixes ps1-tests spu/memory-transfer testControlBitsAreCopiedToStatusRegister.
		ushort stat = (ushort)(_spuCtrl & 0x003F); // mirror SPUCNT bits 0-5
		uint dmaMode = (uint)((_spuCtrl >> 4) & 3);
		if (dmaMode == 2) stat |= 0x0080; // DMA write request
		if (dmaMode == 3) stat |= 0x0100; // DMA read request
		// Bit 6 (0x0040) = SPU RAM IRQ flag (irq9_flag). Set when an SPU RAM
		// access matched IRQ_ADDR while irq9_enable was on; cleared when the
		// game cycles SPUCNT.irq9_enable. Some games poll this directly to
		// ack the SPU IRQ instead of going through the system I_STAT.
		if (_spuIrqFlag) stat |= 0x0040;
		// Bit 10 (transfer busy): 0 = ready (our transfers are synchronous/instant)
		// Bit 11 (second_half_capture_buffer): toggles each time the capture-buffer
		// write index crosses the half-buffer boundary (256 samples).
		// Games can poll this bit to detect "is audio still being captured?"
		// without needing to track sample timing themselves.
		if (_captureBufferIdx >= 256) stat |= 0x0800;
		return stat;
	}

	public void WriteWord(uint offset, uint value)
	{
		WriteHalf(offset, (ushort)value);
		WriteHalf(offset + 2, (ushort)(value >> 16));
	}

	public void WriteHalf(uint offset, ushort value)
	{
		SyncPendingSamples();

		// Voice registers 0x000..0x17F
		if (offset < 0x180)
		{
			int v = (int)(offset / 16);
			int reg = (int)(offset % 16);
			VoiceWriteHalf(v, reg, value);
			return;
		}

		switch (offset)
		{
			case 0x180: _mainVolLeft = value; _mainVolSweepL.Reset(value); break;
			case 0x182: _mainVolRight = value; _mainVolSweepR.Reset(value); break;
			// 0x184/0x186 are vLOUT/vROUT, the reverb output volume that scales
			// the reverb-unit's L/R into the main mix. Previously these were
			// (incorrectly) routed to _cdVol*; CD audio volume lives at 0x1B0/0x1B2.
			case 0x184: _reverbVolLeft = value; break;
			case 0x186: _reverbVolRight = value; break;

			// Key On (voices 0-15, then 16-23)
			case 0x188:
				for (int i = 0; i < 16; i++)
					if ((value & (1 << i)) != 0) KeyOn(i);
				break;
			case 0x18A:
				for (int i = 0; i < 8; i++)
					if ((value & (1 << i)) != 0) KeyOn(16 + i);
				break;

			// Key Off
			case 0x18C:
				for (int i = 0; i < 16; i++)
					if ((value & (1 << i)) != 0) KeyOff(i);
				break;
			case 0x18E:
				for (int i = 0; i < 8; i++)
					if ((value & (1 << i)) != 0) KeyOff(16 + i);
				break;

			// PMON (pitch-modulation enable) / NON (noise-mode enable), 24 voices.
			case 0x190: _pitchModReg = (_pitchModReg & 0xFF0000u) | value; break;
			case 0x192: _pitchModReg = (_pitchModReg & 0x00FFFFu) | ((uint)(value & 0xFF) << 16); break;
			case 0x194: _noiseModeReg = (_noiseModeReg & 0xFF0000u) | value; break;
			case 0x196: _noiseModeReg = (_noiseModeReg & 0x00FFFFu) | ((uint)(value & 0xFF) << 16); break;

			// EON (Reverb On), bit `v` set means voice `v`'s output is
			// also fed into the reverb input bus (in addition to main mix).
			case 0x198: _reverbOnRegister = (_reverbOnRegister & 0xFF0000u) | value; break;
			case 0x19A: _reverbOnRegister = (_reverbOnRegister & 0x00FFFFu) | ((uint)(value & 0xFF) << 16); break;

			// mBASE, reverb work area start address (0x1F801DA2). Stored
			// value x 8 = byte address; we store x4 = halfword address since
			// reverb internally addresses RAM by halfword. Writing mBASE also
			// resets the running work-area pointer to the new base.
			case 0x1A2:
				_reverbBaseAddr = (uint)value << 2;
				_reverbCurrentAddr = _reverbBaseAddr;
				break;

			// IRQ address register (0x1F801DA4)
			case 0x1A4:
				{
					uint newIrqAddr = (uint)value * 8;
					_irqAddr = newIrqAddr;
					// Late-trigger scan. On IRQ_ADDR write, check the transfer
					// address AND every active voice's current playback position.
					// Fire immediately on any match.
					//
					// Without the per-voice scan, an IRQ_ADDR armed at a voice's
					// current block waits for the voice's NEXT 8-byte transition
					// to fire, for a voice parked at the address (because the
					// game just armed the IRQ to wake on the current playback
					// position), that wait could be a full pitch-period away,
					// or never if the voice was keyed off in the meantime.
					CheckRamIrq(_transferCurrent);
					if (!_spuIrqFlag)
					{
						for (int v = 0; v < NumVoices; v++)
						{
							// Only scan active voices. Inactive voices either
							// have stale addresses (haven't played) or are still
							// processed by GenerateSamples when irq9_enable is
							// set (the inactive-voice IRQ-skip fix), their
							// next block boundary will trigger CheckRamIrq
							// naturally.
							if (!_vActive[v]) continue;
							CheckRamIrq(_vCurAddr[v]);
							CheckRamIrq(_vCurAddr[v] + 8);
							if (_spuIrqFlag) break;
						}
					}
					break;
				}

			// Transfer address register (0x1F801DA6): value * 8 = byte offset into SPU RAM
			case 0x1A6:
				_transferAddr = (uint)value << 3;
				_transferCurrent = _transferAddr;
				PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info, $"[SPU] TransferAddr set: raw=0x{value:X4} -> byteAddr=0x{_transferAddr:X}");
				// Late-trigger: if the new transfer position already matches IRQ_ADDR, fire IRQ now.
				CheckRamIrq(_transferCurrent);
				break;

			// Transfer FIFO data port (0x1F801DA8), manual CPU writes to SPU RAM
			case 0x1A8:
				{
					uint pc = _psx.Cpu.Pc;
					if (_fifoWriteCount == 0)
					{
						PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
							$"[SPU] First FIFO write: transferCurrent=0x{_transferCurrent:X} transferAddr=0x{_transferAddr:X} value=0x{value:X4} cpuPC=0x{pc:X8}");
					}
					_fifoPcRing[_fifoPcIdx] = pc;
					_fifoPcIdx = (_fifoPcIdx + 1) & 15;
					_fifoWriteCount++;
					if (_transferCurrent + 1 < PsxConstants.SpuRamSize)
					{
						uint writeAddr = _transferCurrent;
						Ram[_transferCurrent++] = (byte)value;
						Ram[_transferCurrent++] = (byte)(value >> 8);
						_spuRamBytesWritten += 2;
						// SPU RAM IRQ check on manual FIFO write.
						CheckRamIrq(writeAddr);
					}
					break;
				}

			case 0x1AA:
				{
					int oldIrqEn = (_spuCtrl >> 6) & 1;
					int newIrqEn = (value >> 6) & 1;
					
					bool wasEnabled = (_spuCtrl & 0x8000) != 0;
					_spuCtrl = value;

					// SPUCNT bit 15 = SPU enable. On a 1->0 transition hardware
					// force-stops every voice immediately.
					if (wasEnabled && (value & 0x8000) == 0)
						for (int fv = 0; fv < NumVoices; fv++)
						{
							_vActive[fv] = false;
							_vAdsrVol[fv] = 0;
						}

					// When irq9_enable transitions 1->0, clear SPUSTAT.irq9_flag AND
					// deassert the IRQ line. This is the "ack" pattern most games use,
					// they cycle irq9_enable to re-arm SPU IRQ for the next checkpoint.
					if (oldIrqEn == 1 && newIrqEn == 0)
					{
						_spuIrqFlag = false;
						_psx.Interrupts.Clear(PsxConstants.IrqSpu);
					}

					if (_seenSpuWrites.Add(0xFFFF0000u | value)) // unique per value
						PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
							$"[SPU] SPUCNT=0x{value:X4} enable={(value >> 15) & 1} mute={(value >> 14) & 1} dmaMode={(value >> 4) & 3} irqEn={(value >> 6) & 1} fifoWritesSoFar={_fifoWriteCount} cpuPC=0x{_psx.Cpu.Pc:X8}");
					break;
				}
			case 0x1AC: _transferControl = value; break; // SPUDTC (0x1F801DAC): store transfer-mode so reads return it (BIOS write-then-verify)
			case 0x1B0: _cdVolLeft = value; break; // CD audio volume left
			case 0x1B2: _cdVolRight = value; break; // CD audio volume right

			default:
				// Reverb configuration registers 0x1C0..0x1FE (32 halfwords).
				// Each game programs these once at startup (or per scene change)
				// to choose room/hall/echo presets, then leaves them static.
				if (offset >= 0x1C0 && offset < 0x200 && (offset & 1) == 0)
				{
					_reverbRegs[(offset - 0x1C0) >> 1] = value;
					break;
				}
				// Log any unhandled global SPU register write in transfer-control zone once
				if (offset >= 0x1A0 && offset < 0x1C0 && _seenSpuWrites.Add(offset))
					PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
						$"[SPU] WriteHalf unknown offset=0x{offset:X3} value=0x{value:X4}");
				break;
		}
	}

	private readonly System.Collections.Generic.HashSet<uint> _seenSpuWrites = new();

	private void VoiceWriteHalf(int v, int reg, ushort value)
	{
		if ((uint)v >= NumVoices) return;
		switch (reg)
		{
			case 0: _vVolLeft[v] = (short)value; _vVolSweepL[v].Reset(value); break;
			case 2: _vVolRight[v] = (short)value; _vVolSweepR[v].Reset(value); break;
			case 4: _vPitch[v] = value; break;
			case 6: _vStartAddr[v] = (uint)value << 3; break;
			case 8: _vAdsrRaw[v] = (_vAdsrRaw[v] & 0xFFFF0000u) | value; break;
			case 10: _vAdsrRaw[v] = (_vAdsrRaw[v] & 0x0000FFFFu) | ((uint)value << 16); break;
			case 12: _vAdsrVol[v] = value; break;
			case 14: _vRepeatAddr[v] = (uint)value << 3; break;
		}
	}

	/// <summary>
	/// Scan BIOS ROM for consecutive valid ADPCM blocks (runs of >=8 valid 16-byte blocks).
	/// Log the first few candidate regions so we know where audio data lives in ROM.
	/// </summary>
	private void ScanBiosForAdpcm()
	{
		byte[] bios = _psx.Memory.Bios;
		if (bios == null || bios.Length < 32) return;

		int found = 0;
		for (int off = 0; off + 16 <= bios.Length && found < 6; off += 16)
		{
			int run = 0;
			for (int block = 0; off + (block + 1) * 16 <= bios.Length; block++)
			{
				int o = off + block * 16;
				int shift = bios[o] & 0xF;
				int filter = (bios[o] >> 4) & 0xF;
				int flags = bios[o + 1] & 0x7;
				bool validHdr = shift <= 12 && filter <= 4;
				if (validHdr) run++;
				else break;
			}
			if (run >= 8)
			{
				PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
					$"[SPU] BIOS ADPCM candidate: ROM offset=0x{off:X} ({run} consecutive valid blocks), first bytes: " +
					$"{bios[off]:X2} {bios[off + 1]:X2} {bios[off + 2]:X2} {bios[off + 3]:X2} " +
					$"{bios[off + 16]:X2} {bios[off + 17]:X2} {bios[off + 32]:X2} {bios[off + 33]:X2}");
				found++;
				off += (run - 1) * 16; // skip past this run
			}
		}
		if (found == 0)
			PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info, "[SPU] BIOS ADPCM scan: no candidate regions found (>=8 consecutive valid blocks)");
	}

	// ---------------------------------------------------------------
	// XA-ADPCM feed + zigzag resampler (37800 -> 44100 Hz)
	// ---------------------------------------------------------------

	/// <summary>
	/// Accepts a batch of decoded XA-ADPCM PCM samples from the CDROM controller,
	/// resamples from 37800 Hz (or 18900 Hz) to 44100 Hz using the hardware zigzag FIR,
	/// and pushes the result to the XA audio queue for mixing in <see cref="GenerateSamples"/>.
	/// </summary>
	/// <param name="samples">PCM data, interleaved L/R if <paramref name="stereo"/>, flat mono otherwise.</param>
	/// <param name="count">Total sample count (2 * frames for stereo, frames for mono).</param>
	/// <param name="stereo">True if the XA sector uses stereo encoding.</param>
	/// <param name="halfRate">True if the XA sector runs at 18900 Hz (each input frame is presented twice).</param>
	public void FeedXaAdpcm(short[] samples, int count, bool stereo, bool halfRate)
	{
		SyncPendingSamples();

		int numFrames = stereo ? count / 2 : count;
		int frameStride = stereo ? 2 : 1;
		int repeat = halfRate ? 2 : 1; // duplicate frames to reach 37800 Hz before resampling

		for (int frame = 0; frame < numFrames; frame++)
		{
			short sL = samples[frame * frameStride];
			short sR = stereo ? samples[frame * frameStride + 1] : sL;

			for (int r = 0; r < repeat; r++)
			{
				_xaRingL[_xaRingP] = sL;
				_xaRingR[_xaRingP] = sR;
				_xaRingP = (_xaRingP + 1) & 31;
				_xaRingSixstep--;

				if (_xaRingSixstep == 0)
				{
					// Every 6 input frames -> produce 7 output frames (6/7 * 44100 = 37800)
					_xaRingSixstep = 6;
					for (int j = 0; j < 7; j++)
					{
						short outL = ZigzagInterpolate(_xaRingL, j, _xaRingP);
						short outR = ZigzagInterpolate(_xaRingR, j, _xaRingP);
						_xaAudioQueue.Enqueue(outL);
						_xaAudioQueue.Enqueue(outR);
					}
				}
			}
		}
	}

	/// <summary>
	/// One tap of the zigzag FIR interpolator.
	/// Reads 29 coefficients from ZigzagTables[tableIndex] against the ring buffer
	/// rooted at position <paramref name="p"/> (next-write slot, so newest sample is at p-1).
	/// </summary>
	private static short ZigzagInterpolate(short[] ringbuf, int tableIndex, int p)
	{
		short[] table = ZigzagTables[tableIndex];
		int sum = 0;
		for (int i = 0; i < 29; i++)
			sum += ((int)ringbuf[(p - i) & 31] * (int)table[i]) >> 15;
		return (short)Math.Clamp(sum, -32768, 32767);
	}

	/// <summary>
	/// Feeds one raw CDDA sector (2352 bytes = 588 stereo 16-bit LE PCM samples at 44100 Hz)
	/// directly into the XA audio queue, bypassing the zigzag resampler.
	/// CDDA is already at 44100 Hz so no resampling is needed.
	/// Volume scaling is applied by <see cref="GenerateSamples"/> using the CD volume registers.
	/// </summary>
	/// <param name="disc">The disc image byte array.</param>
	/// <param name="byteOffset">Byte offset of the CDDA sector start in <paramref name="disc"/>.</param>
	public void FeedCdda(byte[] disc, int byteOffset)
	{
		SyncPendingSamples();
		// Raw CDDA sector layout: 2352 bytes of 16-bit signed LE stereo PCM, no sync/header.
		// Each 4-byte unit: [L_lo, L_hi, R_lo, R_hi]
		for (int i = 0; i < PsxCdrom.RawSectorSize; i += 4)
		{
			short l = (short)(disc[byteOffset + i]     | (disc[byteOffset + i + 1] << 8));
			short r = (short)(disc[byteOffset + i + 2] | (disc[byteOffset + i + 3] << 8));
			_xaAudioQueue.Enqueue(l);
			_xaAudioQueue.Enqueue(r);
		}
	}

	/// <summary>
	/// Resets the XA-ADPCM audio decoder state: clears the zigzag ring buffers,
	/// resets the sixstep interpolator counter, and flushes the audio queue.
	/// Called from CDROM whenever the active XA stream changes (SetFilter, Pause, ReadN/ReadS).
	/// </summary>
	public void ResetXaDecoder()
	{
		Array.Clear(_xaRingL);
		Array.Clear(_xaRingR);
		_xaRingP = 0;
		_xaRingSixstep = 6;
		_xaAudioQueue.Clear();
	}

	/// <summary>DMA write: copy words from main RAM to SPU RAM.</summary>
	public void DmaWrite(uint[] data, int count)
	{
		SyncPendingSamples();
		PsxLog.Write(PsxLogCategory.SPU, PsxLogLevel.Info,
			$"[SPU] DmaWrite: count={count} transferCurrent=0x{_transferCurrent:X} transferAddr=0x{_transferAddr:X}");
		for (int i = 0; i < count; i++)
		{
			if (_transferCurrent + 3 >= PsxConstants.SpuRamSize) break;
			uint word = data[i];
			uint writeAddr = _transferCurrent;
			Ram[_transferCurrent++] = (byte)word;
			Ram[_transferCurrent++] = (byte)(word >> 8);
			Ram[_transferCurrent++] = (byte)(word >> 16);
			Ram[_transferCurrent++] = (byte)(word >> 24);
			_spuRamBytesWritten += 4;
			// SPU RAM IRQ check on DMA write
			// One check per 4-byte word; the 8-byte IRQ granularity catches both
			// halves of any 8-byte aligned region this word lands in.
			CheckRamIrq(writeAddr);
		}
	}

	/// <summary>DMA read: copy words from SPU RAM into the caller's buffer (-> main RAM).</summary>
	public void DmaRead(uint[] dest, int count)
	{
		SyncPendingSamples();
		for (int i = 0; i < count; i++)
		{
			if (_transferCurrent + 3 >= PsxConstants.SpuRamSize) break;
			uint readAddr = _transferCurrent;
			dest[i] = (uint)(Ram[_transferCurrent]
				| (Ram[_transferCurrent + 1] << 8)
				| (Ram[_transferCurrent + 2] << 16)
				| (Ram[_transferCurrent + 3] << 24));
			_transferCurrent += 4;
			// SPU RAM IRQ check on DMA read, same granularity as DmaWrite.
			CheckRamIrq(readAddr);
		}
	}
}
