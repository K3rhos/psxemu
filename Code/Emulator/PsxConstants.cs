namespace PSXEmu;

public static class PsxConstants
{
	public enum VideoStandard
	{
		NTSC,
		PAL
	}

	// CPU clock frequency
	public const int CpuHz = 33_868_800;

	// Memory sizes
	public const int RamSize = 2 * 1024 * 1024;  // 2 MB main RAM
	public const int BiosSize = 512 * 1024;        // 512 KB BIOS ROM
	public const int ScratchpadSize = 1024;              // 1 KB scratchpad (data cache)
	public const int SpuRamSize = 512 * 1024;        // 512 KB SPU RAM

	// VRAM: 1024 x 512 pixels @ 16bpp = 1 MB. The CPU rasterizer always writes
	// at native resolution; upscaling is done by the GPU rasterizer into a
	// separate render target (see PsxGpu.Rendering.cs / GpuRasterScale).
	public const int VramWidth = 1024;
	public const int VramHeight = 512;
	public const int VramSize = VramWidth * VramHeight * 2; // bytes
	// Bit-mask equivalents of `% VramWidth` / `% VramHeight`, relies on both
	// dims being powers of two.
	public const int VramWidthMask = VramWidth - 1;
	public const int VramHeightMask = VramHeight - 1;

	// Default display dimensions (320x240 NTSC)
	public const int ScreenWidth = 320;
	public const int ScreenHeight = 240;

	// --- NTSC timing ---
	// The PSX runs NTSC at ~59.82 Hz, NOT the textbook 59.94 Hz, confirmed
	// against NoCash spec and Mednafen. Previous values (564,480 / 59.94)
	// emulated 33,832,915 cycles per real-time second (0.10 % slow), which
	// underfed CDROM XA streaming relative to SPU consumption, drift of
	// ~1 audio sector per ~30 s of FMV, the canonical "FMV runs out of audio"
	// failure pattern.
	public const int LinesPerFrameNTSC = 263;
	public const int VisibleLinesNTSC = 240;
	public const int CpuClocksPerLineNTSC = 2152;   // 566,204 / 263 (rest rolls up at frame end)
	public const int CpuClocksPerFrameNTSC = 566_204;
	public const double FrameRateNTSC = 59.823;     // matches CpuHz / CpuClocksPerFrameNTSC
	public const double FrameTimeNTSC = 1.0 / FrameRateNTSC;

	// --- PAL timing ---
	// Same CRTC derivation:
	//   PAL CRTC clock = 53,203,425 Hz
	//   CPU/CRTC ratio = 451,584 / 709,379 (exact integer)
	//   314 lines x 3406 CRTC ticks/line = 1,069,484 CRTC ticks
	//   1,069,484 x 451,584 / 709,379    = 680,823 CPU cycles/frame
	//   Implied frame rate               = 33,868,800 / 680,823 = 49.747 Hz
	// PAL drift was much smaller than NTSC (~247 cycles/frame vs ~1,724) but
	// fixed in the same pass for consistency.
	public const int LinesPerFramePAL = 314;
	public const int VisibleLinesPAL = 288;
	public const int CpuClocksPerLinePAL = 2168;   // 680,823 / 314 (rest rolls up at frame end)
	public const int CpuClocksPerFramePAL = 680_823;
	public const double FrameRatePAL = 49.747;     // matches CpuHz / CpuClocksPerFramePAL
	public const double FrameTimePAL = 1.0 / FrameRatePAL;

	// SPU output: 44,100 Hz stereo.
	// Per-frame budget = ceil(SpuSampleRate / FrameRate*) + a small safety
	// margin, must use the REAL frame rate, not "60" / "50", or the budget
	// undershoots and we drop ~1-2 samples per frame at the boundary
	// (audible micro-clicks during long FMVs).
	public const int SpuSampleRate = 44_100;
	public const int SpuChannels = 2;
	public const int SpuSamplesPerFrameNTSC = (int)(SpuSampleRate / FrameRateNTSC) + 1;  // ~738
	public const int SpuSamplesPerFramePAL = (int)(SpuSampleRate / FrameRatePAL) + 1;    // ~887
	public const int MaxSpuSamplesPerFrame = SpuSamplesPerFramePAL;
	public const int CpuCyclesPerSample = CpuHz / SpuSampleRate; // ~768 CPU cycles per audio sample

	// --- Per-region bus cycle costs ---
	//
	// CPU bus access cost (cycles) charged to the CPU per memory READ.
	// Writes are pipelined and don't stall.
	//
	// SIZE-AWARE regions (BIOS/SPU/CDROM/EXP1):
	// Different access sizes (byte/halfword/word) hit
	// real hardware at different rates, a 16-bit data bus needs two cycles
	// to fetch a 32-bit word, while a byte read fits in one bus cycle.
	// Computed from default MEMCTRL config 0x0013243F / 0x00031125 etc.
	// BIOS may write MEMCTRL later to retune; we hardcode the defaults
	// since most games never modify them after boot.
	//
	// FIXED regions (RAM/Scratchpad/IO): single-cost regardless of size.

	// Fixed regions
	public const int BusCyclesRam = 6;
	public const int BusCyclesScratchpad = 0;
	public const int BusCyclesIo = 2;
	public const int BusCyclesUnmapped = 0;

	// BIOS
	public const int BusCyclesBiosByte = 6;
	public const int BusCyclesBiosHalf = 12;
	public const int BusCyclesBiosWord = 24;

	// SPU
	public const int BusCyclesSpuByte = 20;
	public const int BusCyclesSpuHalf = 20;
	public const int BusCyclesSpuWord = 40;

	// CDROM
	public const int BusCyclesCdromByte = 6;
	public const int BusCyclesCdromHalf = 12;
	public const int BusCyclesCdromWord = 24;

	// EXPANSION1
	public const int BusCyclesExp1Byte = 6;
	public const int BusCyclesExp1Half = 12;
	public const int BusCyclesExp1Word = 24;

	// Access size for the size-aware GetReadCycles lookup.
	public enum BusAccessSize { Byte = 0, Half = 1, Word = 2 }

	// --- Physical address regions ---
	public const uint RamBase = 0x00000000;
	public const uint RamMirrorEnd = 0x00800000; // RAM mirrors in 0-7FFFFF
	public const uint Expansion1Base = 0x1F000000;
	public const uint ScratchpadBase = 0x1F800000;
	public const uint IoBase = 0x1F801000;
	public const uint Expansion2Base = 0x1F802000;
	public const uint BiosBase = 0x1FC00000;

	// I/O register offsets (from IoBase)
	public const uint MemCtrlOffset = 0x000; // 0x1F801000
	public const uint PadSioOffset = 0x040; // 0x1F801040  controller/memory card SIO
	public const uint SioOffset = 0x050; // 0x1F801050  serial port
	public const uint MemCtrl2Offset = 0x060; // 0x1F801060
	public const uint IrqStatOffset = 0x070; // 0x1F801070  I_STAT
	public const uint IrqMaskOffset = 0x074; // 0x1F801074  I_MASK
	public const uint DmaBaseOffset = 0x080; // 0x1F801080  DMA channels
	public const uint DmaPcr = 0x1F801F0; // actually 0x1F8010F0
	public const uint DmaIcr = 0x1F801F4; // actually 0x1F8010F4
	public const uint TimerBaseOffset = 0x100; // 0x1F801100  timers 0-2
	public const uint CdromOffset = 0x800; // 0x1F801800  CD-ROM
	public const uint GpuGp0Offset = 0x810; // 0x1F801810
	public const uint GpuGp1Offset = 0x814; // 0x1F801814
	public const uint MdecOffset = 0x820; // 0x1F801820
	public const uint SpuBaseOffset = 0xC00; // 0x1F801C00

	// Full I/O addresses
	public const uint AddrIrqStat = IoBase + IrqStatOffset;   // 0x1F801070
	public const uint AddrIrqMask = IoBase + IrqMaskOffset;   // 0x1F801074
	public const uint AddrDmaPcr = 0x1F8010F0;
	public const uint AddrDmaIcr = 0x1F8010F4;
	public const uint AddrGpuGp0 = 0x1F801810;
	public const uint AddrGpuGp1 = 0x1F801814;
	public const uint AddrGpuStat = 0x1F801814; // read = GPUSTAT
	public const uint AddrCdrom = 0x1F801800;

	// Interrupt bits (I_STAT / I_MASK)
	public const uint IrqVblank = 1u << 0;
	public const uint IrqGpu = 1u << 1;
	public const uint IrqCdrom = 1u << 2;
	public const uint IrqDma = 1u << 3;
	public const uint IrqTimer0 = 1u << 4;
	public const uint IrqTimer1 = 1u << 5;
	public const uint IrqTimer2 = 1u << 6;
	public const uint IrqController = 1u << 7;
	public const uint IrqSio = 1u << 8;
	public const uint IrqSpu = 1u << 9;
	public const uint IrqLightpen = 1u << 10;

	// COP0 register indices
	public const int Cop0Bpc = 3;
	public const int Cop0Bda = 5;
	public const int Cop0Jumpdest = 6;
	public const int Cop0Dcic = 7;
	public const int Cop0BadVaddr = 8;
	public const int Cop0Bdam = 9;
	public const int Cop0Bpcm = 11;
	public const int Cop0Sr = 12; // Status Register
	public const int Cop0Cause = 13;
	public const int Cop0Epc = 14;
	public const int Cop0Prid = 15; // Processor ID

	// SR bits
	public const uint SrIec = 1u << 0;  // Current interrupt enable
	public const uint SrKuc = 1u << 1;  // Current kernel/user (0=kernel)
	public const uint SrIep = 1u << 2;  // Previous interrupt enable
	public const uint SrKup = 1u << 3;
	public const uint SrIeo = 1u << 4;  // Old interrupt enable
	public const uint SrKuo = 1u << 5;
	public const uint SrIm = 0xFF00u;  // Interrupt mask (bits 8-15)
	public const uint SrIsc = 1u << 16; // Isolate cache
	public const uint SrSwc = 1u << 17; // Swap cache
	public const uint SrBev = 1u << 22; // Boot exception vectors

	// CAUSE bits
	public const uint CauseExcCode = 0x7Cu; // bits 2-6 = exception code
	public const uint CauseIp = 0xFF00u; // bits 8-15 = interrupt pending
	public const uint CauseBd = 1u << 31; // branch delay flag

	// Exception codes (ExcCode field in CAUSE)
	public const uint ExcInt = 0;  // External interrupt
	public const uint ExcAdEL = 4;  // Address error (load)
	public const uint ExcAdES = 5;  // Address error (store)
	public const uint ExcIBE = 6;  // Bus error (instruction fetch)
	public const uint ExcDBE = 7;  // Bus error (data load/store)
	public const uint ExcSyscall = 8;
	public const uint ExcBreak = 9;
	public const uint ExcRI = 10; // Reserved instruction
	public const uint ExcCpU = 11; // Coprocessor unusable
	public const uint ExcOvf = 12; // Arithmetic overflow

	// MIPS register names
	public const int R0 = 0;
	public const int AT = 1;
	public const int V0 = 2;
	public const int V1 = 3;
	public const int A0 = 4;
	public const int A1 = 5;
	public const int A2 = 6;
	public const int A3 = 7;
	public const int SP = 29;
	public const int FP = 30;
	public const int RA = 31;
}
