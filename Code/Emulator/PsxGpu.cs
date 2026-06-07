namespace PSXEmu;

/// <summary>
/// PSX GPU emulation.
/// Maintains 1MB VRAM (1024*512, 16bpp RGB555) and processes GP0/GP1 commands.
/// A software rasterizer renders polygons/rects/lines into VRAM.
/// </summary>
public partial class PsxGpu
{
	private readonly Psx _psx;

	// VRAM: 1024*512 pixels, 16bpp (RGB555), stored as ushort[]
	public ushort[] Vram { get; } = new ushort[PsxConstants.VramWidth * PsxConstants.VramHeight];

	// True-color overlay: stores full 24-bit RGB (as 0xAARRGGBB) for shaded pixels.
	// The display converter reads from here instead of the 15-bit VRAM for smooth gradients.
	// Non-shaded pixels (flat color, textured, VRAM fill) write the expanded RGB555 here.
	private readonly uint[] _trueColorVram = new uint[PsxConstants.VramWidth * PsxConstants.VramHeight];

	// Thread-safe VRAM snapshot for display. The worker thread copies VRAM here
	// after each RunFrame; the main thread reads from here during display upload.
	// This prevents tearing when the worker is mid-draw on the next frame.
	private readonly ushort[] _vramSnapshot = new ushort[PsxConstants.VramWidth * PsxConstants.VramHeight];
	private readonly uint[] _trueColorSnapshot = new uint[PsxConstants.VramWidth * PsxConstants.VramHeight];
	private readonly object _snapshotLock = new();

	/// <summary>
	/// Copy current VRAM into the display snapshot. Called from the worker
	/// thread after RunFrame completes, before posting to the frame channel.
	/// </summary>
	public void SnapshotVram()
	{
		lock (_snapshotLock)
		{
			Array.Copy(Vram, 0, _vramSnapshot, 0, Vram.Length);
			Array.Copy(_trueColorVram, 0, _trueColorSnapshot, 0, _trueColorVram.Length);
			_snapshotDisplayStartX = DisplayStartX;
			_snapshotDisplayStartY = DisplayStartY;
			_snapshotDisplayW = _display24Bit ? Active24BitDisplayWidth() : DisplayW;
			_snapshotDisplayH = DisplayH;
			_snapshotDisplay24Bit = _display24Bit;

			// Hand the GPU rasterizer's per-frame vertex batch off to the main
			// thread. Cheap reference swap, see PsxGpu.Rendering.cs.
			SnapshotVertexBatch();
		}
	}

	// GP0 command FIFO
	private readonly uint[] _gp0Fifo = new uint[16];
	private int _gp0FifoLen;
	private int _gp0WordsNeeded;
	private byte _gp0Cmd;

	// GP0 VRAM transfer state
	private bool _gp0VramWrite;
	private bool _gp0VramRead;
	private int _vramTransferPixels; // diagnostic: pixels written in current transfer
	public bool GpuVramWritePending => _gp0VramWrite;

	// GP0 variable-length polyline state
	private bool _polylineMode;
	private bool _polylineGouraud;
	private int _polylineVertIdx; // vertex count received so far (not counting cmd word)
	private uint _polylinePrevXY;
	private uint _polylinePrevColor;
	private int _vramTransferX, _vramTransferY;
	private int _vramTransferW, _vramTransferH;
	private int _vramTransferCX, _vramTransferCY; // current position

	// GPU registers / state
	private uint _gpuStat;

	// Drawing area
	public int DrawAreaX1 { get; private set; }
	public int DrawAreaY1 { get; private set; }
	public int DrawAreaX2 { get; private set; }
	public int DrawAreaY2 { get; private set; }
	public int DrawOffsetX { get; private set; }
	public int DrawOffsetY { get; private set; }

	// Display area (what part of VRAM is shown on screen)
	public int DisplayStartX { get; private set; }
	public int DisplayStartY { get; private set; }
	public int DisplayW { get; private set; } = PsxConstants.ScreenWidth;

	// Horizontal display range (GP1 06h), in GPU dot-clock ticks (X1=start, X2=end).
	// Default to the standard NTSC full-width range so games that never
	// write GP1(06h) behave exactly like the old hRes-based width.
	public int DisplayX1 { get; private set; } = 608;
	public int DisplayX2 { get; private set; } = 3168;

	// Vertical display range (GP1 07h), in scanlines (Y1=start, Y2=end).
	// Visible height = (Y2-Y1), doubled when interlaced.
	// Defaults to the standard NTSC range (256 - 16 = 240 lines) so
	// a game that never writes GP1(07h) keeps the historical height.
	public int DisplayY1 { get; private set; } = 16;
	public int DisplayY2 { get; private set; } = 256;

	// GPU-clock divider for the dot clock, derived from the horizontal resolution.
	// dotclock = GPU clock / divider, and GPU clock = 11/7 x CPU (system) clock.
	// Used by Timer0's dot-clock source (PsxTimer).
	public int DotClockDivider => DisplayW switch
	{
		256 => 10,
		320 => 8,
		368 => 7,
		512 => 5,
		640 => 4,
		_ => 8,
	};

	/// <summary>
	/// Display width in pixels derived from the actual horizontal display range
	/// (GP1 06h, X1..X2) and the dot-clock divider.
	/// Used ONLY for the 24-bit (FMV) display read. The standard NTSC range
	/// (0x260..0xC60) yields exactly the fixed hRes at every divider, so a game that
	/// uses the normal range is unaffected; a game that sets a NARROW range to frame
	/// the video (which 24-bit FMVs do) is correctly clipped instead of overshooting
	/// into adjacent VRAM (the 3x-repeat + gray-block corruption).
	/// </summary>
	private int Active24BitDisplayWidth()
	{
		int div = DotClockDivider;
		if (div <= 0) return DisplayW;
		// Dot-clock pixels spanned by X1..X2.
		int pixels = (DisplayX2 / div) - (DisplayX1 / div);
		if (pixels <= 0) return DisplayW;        // unset/garbage range -> fall back to hRes
		int width = (pixels + 2) & ~3;
		return Math.Clamp(width, 1, MaxDisplayW);
	}

	// Visible scanline count derived from the GP1(07h) vertical range (Y2-Y1),
	// doubled in interlaced (480i) mode
	private void RecomputeDisplayHeight()
	{
		int lines = DisplayY2 - DisplayY1;
		
		if (lines < 1)
			lines = PsxConstants.ScreenHeight;
		
		DisplayH = _interlaced ? lines * 2 : lines;
	}

	public int DisplayH { get; private set; } = PsxConstants.ScreenHeight;
	public PsxConstants.VideoStandard VideoStandard { get; private set; } = PsxConstants.VideoStandard.NTSC;

	// Display enable
	private bool _displayEnabled;

	// Interlaced display flag (set by GP1 0x08)
	private bool _interlaced;

	// 24-bit color depth flag (set by GP1 0x08 bit 4). When true, the display area holds
	// packed 24bpp RGB (3 bytes/pixel) instead of 15bpp RGB555. Used during FMV playback.
	private bool _display24Bit;
	public bool IsDisplay24Bit => _display24Bit;

	// Texture settings (from E1 draw mode)
	private int _texPageX;   // texture page X (64px units)
	private int _texPageY;   // texture page Y (256px units)
	private int _texDepth;   // 0=4bpp, 1=8bpp, 2=15bpp
	private int _clutX;
	private int _clutY;
	private bool _texDisabled;
	private byte _semiTransMode; // 0-3

	// Texture window
	private int _texWinMaskX, _texWinMaskY;
	private int _texWinOffX, _texWinOffY;

	// Mask bit
	private bool _setMaskBit;
	private bool _checkMaskBit;

	// Frame tracking
	public int FrameCount { get; private set; }
	public int DrawCmdCount { get; private set; } // GP0 draw commands this frame

	// VRAM read state (for VRAM->CPU transfers)
	private int _vramReadX, _vramReadY, _vramReadW, _vramReadH;
	private int _vramReadCX, _vramReadCY;

	// GPUREAD latch: set by GP1(0x10) GPU-info queries, returned by CPU reads of 0x1F801810
	private uint _gpuReadLatch;

	// Diagnostic: log each unique GPU state message once
	private readonly System.Collections.Generic.HashSet<string> _seenGpuLog = new();
	private void LogGpuState(string msg)
	{
		if (_seenGpuLog.Add(msg))
			PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info, $"[GPU] {msg}");
	}

	public PsxGpu(Psx psx)
	{
		_psx = psx;
		// Draw area state held in upscaled VRAM coords.
		DrawAreaX2 = PsxConstants.VramWidthMask;
		DrawAreaY2 = PsxConstants.VramHeightMask;
		_gpuStat = 0x14802000; // default GPUSTAT
	}

	// ---- Save-state ----
	// Live VRAM (15-bit + true-colour shadow) + all register/command state. The
	// display SNAPSHOT buffers (_vramSnapshot/_trueColorSnapshot/_snapshotDisplay*)
	// and the render-thread vertex batch are skipped, both re-derive at the next
	// vblank, so at most one stale display frame after a load. Diagnostics
	// (FrameCount/DrawCmdCount/_vramTransferPixels/_seenGpuLog) excluded. GPU has
	// no scheduler event (driven by the scanline loop), so nothing to re-arm.
	public void SaveState(StateWriter w)
	{
		w.UShorts(Vram);
		w.UInts(_trueColorVram);
		w.UInts(_gp0Fifo); w.S32(_gp0FifoLen); w.S32(_gp0WordsNeeded); w.U8(_gp0Cmd);
		w.Bool(_gp0VramWrite); w.Bool(_gp0VramRead);
		w.Bool(_polylineMode); w.Bool(_polylineGouraud); w.S32(_polylineVertIdx);
		w.U32(_polylinePrevXY); w.U32(_polylinePrevColor);
		w.S32(_vramTransferX); w.S32(_vramTransferY); w.S32(_vramTransferW); w.S32(_vramTransferH);
		w.S32(_vramTransferCX); w.S32(_vramTransferCY);
		w.S32(_vramReadX); w.S32(_vramReadY); w.S32(_vramReadW); w.S32(_vramReadH);
		w.S32(_vramReadCX); w.S32(_vramReadCY);
		w.U32(_gpuReadLatch);
		w.U32(_gpuStat);
		w.S32(DrawAreaX1); w.S32(DrawAreaY1); w.S32(DrawAreaX2); w.S32(DrawAreaY2);
		w.S32(DrawOffsetX); w.S32(DrawOffsetY);
		w.S32(DisplayStartX); w.S32(DisplayStartY); w.S32(DisplayW); w.S32(DisplayH);
		w.S32((int)VideoStandard);
		w.Bool(_displayEnabled); w.Bool(_interlaced); w.Bool(_display24Bit);
		w.S32(_texPageX); w.S32(_texPageY); w.S32(_texDepth); w.S32(_clutX); w.S32(_clutY);
		w.Bool(_texDisabled); w.U8(_semiTransMode);
		w.S32(_texWinMaskX); w.S32(_texWinMaskY); w.S32(_texWinOffX); w.S32(_texWinOffY);
		w.Bool(_setMaskBit); w.Bool(_checkMaskBit);
	}

	public void LoadState(StateReader r)
	{
		r.UShorts(Vram);
		r.UInts(_trueColorVram);
		r.UInts(_gp0Fifo); _gp0FifoLen = r.S32(); _gp0WordsNeeded = r.S32(); _gp0Cmd = r.U8();
		_gp0VramWrite = r.Bool(); _gp0VramRead = r.Bool();
		_polylineMode = r.Bool(); _polylineGouraud = r.Bool(); _polylineVertIdx = r.S32();
		_polylinePrevXY = r.U32(); _polylinePrevColor = r.U32();
		_vramTransferX = r.S32(); _vramTransferY = r.S32(); _vramTransferW = r.S32(); _vramTransferH = r.S32();
		_vramTransferCX = r.S32(); _vramTransferCY = r.S32();
		_vramReadX = r.S32(); _vramReadY = r.S32(); _vramReadW = r.S32(); _vramReadH = r.S32();
		_vramReadCX = r.S32(); _vramReadCY = r.S32();
		_gpuReadLatch = r.U32();
		_gpuStat = r.U32();
		DrawAreaX1 = r.S32(); DrawAreaY1 = r.S32(); DrawAreaX2 = r.S32(); DrawAreaY2 = r.S32();
		DrawOffsetX = r.S32(); DrawOffsetY = r.S32();
		DisplayStartX = r.S32(); DisplayStartY = r.S32(); DisplayW = r.S32(); DisplayH = r.S32();
		VideoStandard = (PsxConstants.VideoStandard)r.S32();
		_displayEnabled = r.Bool(); _interlaced = r.Bool(); _display24Bit = r.Bool();

		// Y1/Y2 aren't persisted separately, rebuild them from the restored DisplayH
		// so a post-load GP1(08h) re-derives the same height (no snap back to 240).
		DisplayY1 = 0x10;
		DisplayY2 = DisplayY1 + (_interlaced ? DisplayH / 2 : DisplayH);
		_texPageX = r.S32(); _texPageY = r.S32(); _texDepth = r.S32(); _clutX = r.S32(); _clutY = r.S32();
		_texDisabled = r.Bool(); _semiTransMode = r.U8();
		_texWinMaskX = r.S32(); _texWinMaskY = r.S32(); _texWinOffX = r.S32(); _texWinOffY = r.S32();
		_setMaskBit = r.Bool(); _checkMaskBit = r.Bool();
	}

	/// <summary>
	/// Full reset: zeros VRAM AND register state. Called from Psx.Reset()
	/// when the system boots or the user resets the emulator.
	/// </summary>
	public void Reset()
	{
		Array.Clear(Vram);
		Array.Clear(_trueColorVram);
		ResetState();
	}
	
	private void ResetState()
	{
		_gp0FifoLen = 0;
		_gp0WordsNeeded = 0;
		_gp0VramWrite = false;
		_gp0VramRead = false;
		_polylineMode = false;
		_polylineVertIdx = 0;

		DrawAreaX1 = DrawAreaY1 = 0;
		DrawAreaX2 = PsxConstants.VramWidthMask;
		DrawAreaY2 = PsxConstants.VramHeightMask;
		DrawOffsetX = DrawOffsetY = 0;

		DisplayStartX = DisplayStartY = 0;
		DisplayW = PsxConstants.ScreenWidth;
		DisplayH = PsxConstants.ScreenHeight;
		DisplayY1 = 0x10;
		DisplayY2 = 0x100;
		VideoStandard = PsxConstants.VideoStandard.NTSC;
		_displayEnabled = false;

		_texPageX = _texPageY = 0;
		_texDepth = 0;
		_texDisabled = false;
		_semiTransMode = 0;
		_texWinMaskX = _texWinMaskY = _texWinOffX = _texWinOffY = 0;
		_setMaskBit = _checkMaskBit = false;
		_interlaced = false;
		_display24Bit = false;

		_gpuStat = 0x14802000;
		_gpuReadLatch = 0;
		FrameCount = 0;
	}

	// --- GP1 (GPU Control) ---

	public void WriteGp1(uint value)
	{
		uint cmd = value >> 24;
		uint arg = value & 0x00FFFFFF;

		switch (cmd)
		{
			case 0x00: // Reset GPU
				ResetState();
				break;

			case 0x01: // Reset command buffer
				_gp0FifoLen = 0;
				_gp0VramWrite = false;
				_gp0VramRead = false;
				break;

			case 0x02: // Acknowledge GPU interrupt (clear bit 24, do NOT re-raise)
				_gpuStat &= ~(1u << 24);
				break;

			case 0x03: // Display enable
				_displayEnabled = (arg & 1) == 0;
				if (_displayEnabled) _gpuStat &= ~(1u << 23);
				else _gpuStat |= (1u << 23);
				break;

			case 0x04: // DMA direction
				_gpuStat = (_gpuStat & ~(3u << 29)) | ((arg & 3) << 29);
				break;

			case 0x05: // Display start
				// X is masked 0x3FE (bottom bit forced to 0), the framebuffer
				// is 16-bit aligned so odd X starts are illegal.
				DisplayStartX = (int)(arg & 0x3FE);
				DisplayStartY = (int)((arg >> 10) & 0x1FF);
				PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info, $"[GPU] DisplayStart=({DisplayStartX},{DisplayStartY})");
				break;

			case 0x06: // Horizontal display range (X1..X2 in GPU dot-clock ticks)
				DisplayX1 = (int)(arg & 0xFFF);
				DisplayX2 = (int)((arg >> 12) & 0xFFF);
				break;

			case 0x07: // Vertical display range (Y1..Y2 in scanlines)
				DisplayY1 = (int)(arg & 0x3FF);
				DisplayY2 = (int)((arg >> 10) & 0x3FF);
				RecomputeDisplayHeight();
				break;

			case 0x08: // Display mode
				{
					// bits 0-1 = horizontal resolution 1 (0=256, 1=320, 2=512, 3=640)
					// bit 2   = vertical resolution (0=240, 1=480 when interlaced)
					// bit 3   = video mode (0=NTSC, 1=PAL)
					// bit 4   = color depth (0=15bpp, 1=24bpp)
					// bit 5   = vertical interlace
					// bit 6   = horizontal resolution 2 (1=368 pixels)
					// bit 7   = reverse flag (distorted, unused)
					int hRes;
					if ((arg & 0x40) != 0)
						hRes = 368;
					else
						hRes = (arg & 3) switch { 0 => 256, 1 => 320, 2 => 512, 3 => 640, _ => 320 };
					bool isPal = (arg & 0x08) != 0;
					bool interlaced = (arg & 0x24) == 0x24; // bit 5 (vertical interlace) AND bit 2 (interlace enable) both set
					DisplayW = hRes;
					VideoStandard = isPal ? PsxConstants.VideoStandard.PAL : PsxConstants.VideoStandard.NTSC;
					_interlaced = interlaced;
					_display24Bit = (arg & 0x10) != 0; // bit 4 = 24bpp (FMV playback mode)

					// Vertical height comes from the GP1(07h) range (Y2-Y1), doubled when
					// interlaced, no longer hardcoded to 240/480.
					RecomputeDisplayHeight();

					// Update GPUSTAT bits
					_gpuStat = (_gpuStat & ~0x7F4000u) | ((arg & 0x3F) << 17) | ((arg & 0x40) << 10);
					LogGpuState($"DisplayMode={hRes}x{DisplayH} depth={(_display24Bit ? 24 : 15)}bpp video={VideoStandard} interlaced={interlaced}");
					break;
				}

			case 0x10:
			case 0x11:
			case 0x12:
			case 0x13:
			case 0x14:
			case 0x15:
			case 0x16:
			case 0x17:
			case 0x18:
			case 0x19:
			case 0x1A:
			case 0x1B:
			case 0x1C:
			case 0x1D:
			case 0x1E:
			case 0x1F: // Get GPU info
				HandleGetGpuInfo(arg);
				break;
		}
	}

	// --- GP0 (GPU Command/Data) ---

	public void WriteGp0(uint value)
	{
		if (_gp0VramWrite)
		{
			// Receiving VRAM write data (packed as 2 pixels per word)
			WriteVramPixels(value);
			return;
		}

		// Variable-length polyline: keep reading vertex words until terminator
		if (_polylineMode)
		{
			uint top = value >> 16;
			if (top == 0x5000 || top == 0x5555)
			{
				_polylineMode = false;
				_polylineVertIdx = 0;
				return;
			}
			if (_polylineGouraud)
			{
				// Gouraud polyline: alternating color / XY pairs after the initial cmd+col+xy
				if ((_polylineVertIdx & 1) == 0)
				{
					_polylinePrevColor = value & 0xFFFFFF; // color word
				}
				else
				{
					DrawLineGouraud(_polylinePrevColor, _polylinePrevXY, _polylinePrevColor, value);
					_polylinePrevXY = value;
				}
			}
			else
			{
				// Flat polyline: each word is an XY vertex
				if (_polylineVertIdx > 0)
					DrawLineFlat(_polylinePrevColor, _polylinePrevXY, value);
				_polylinePrevXY = value;
			}
			_polylineVertIdx++;
			return;
		}

		_gp0Fifo[_gp0FifoLen++] = value;

		if (_gp0WordsNeeded == 0)
		{
			// Start of a new command
			_gp0Cmd = (byte)(value >> 24);
			_gp0WordsNeeded = GetGp0WordCount(_gp0Cmd);
		}

		if (_gp0FifoLen >= _gp0WordsNeeded)
		{
			ExecuteGp0();
			_gp0FifoLen = 0;
			_gp0WordsNeeded = 0;
		}
	}

	/// <summary>GPU -> CPU: return next word from VRAM read buffer, or the GPUREAD latch.</summary>
	public uint ReadGpuData()
	{
		if (_gp0VramRead)
		{
			uint lo = VramRead();
			uint hi = VramRead();
			_gpuReadLatch = lo | (hi << 16);
		}
		return _gpuReadLatch;
	}

	private void HandleGetGpuInfo(uint subCmd)
	{
		switch (subCmd & 0x07)
		{
			case 0x02: // Texture window
				_gpuReadLatch = (uint)((_texWinOffY / 8) << 15 | (_texWinOffX / 8) << 10 |
										(_texWinMaskY / 8) << 5 | (_texWinMaskX / 8));
				break;
			case 0x03: // Draw area top-left, return native coords to game
				_gpuReadLatch = (uint)(DrawAreaX1 |
				                       (DrawAreaY1 << 10));
				break;
			case 0x04: // Draw area bottom-right
				_gpuReadLatch = (uint)(DrawAreaX2 |
				                       (DrawAreaY2 << 10));
				break;
			case 0x05: // Draw offset, also native
				_gpuReadLatch = (uint)((DrawOffsetX & 0x7FF) | ((DrawOffsetY & 0x7FF) << 11));
				break;
				// 0x00, 0x01, 0x06, 0x07: leave latch unchanged
		}
	}

	private ushort VramRead()
	{
		if (_vramReadCY >= _vramReadH) return 0;
		// VRAM is at native resolution, one source pixel per game-visible pixel.
		int nx = (_vramReadX + _vramReadCX) & 0x3FF;
		int ny = (_vramReadY + _vramReadCY) & 0x1FF;
		ushort px = Vram[ny * PsxConstants.VramWidth + nx];
		_vramReadCX++;
		if (_vramReadCX >= _vramReadW)
		{
			_vramReadCX = 0;
			_vramReadCY++;
			if (_vramReadCY >= _vramReadH)
				_gp0VramRead = false;
		}
		return px;
	}

	public uint ReadGpuStat()
	{
		// Bit 26 = ready to receive command word
		// Bit 27 = ready to send VRAM to CPU
		// Bit 28 = ready to receive DMA block
		uint stat = _gpuStat;
		stat |= (1u << 26); // Ready for cmd
		stat |= (1u << 28); // Ready for DMA block
		if (_gp0VramRead) stat |= (1u << 27); // Ready to send VRAM

		// Bit 25 = DMA / Data Request (depends on GP1(04h) DMA direction)
		uint dmaDir = (stat >> 29) & 3;
		switch (dmaDir)
		{
			case 0: stat &= ~(1u << 25); break;                                 // Off
			case 1: stat |= (1u << 25); break;                                  // FIFO not full
			case 2: stat |= ((stat >> 3) & (1u << 25)); break;              // Same as bit 28
			case 3: stat |= ((stat >> 2) & (1u << 25)); break;              // Same as bit 27
		}

		return stat;
	}

	// Returns total words needed for a GP0 command
	private int GetGp0WordCount(byte cmd)
	{
		// Mask off semi-transparency and raw-texture bits (bits 0,1 of command nibble) for word count
		// All 4 variants (cmd, cmd|1, cmd|2, cmd|3) of each primitive have the same word count.
		return cmd switch
		{
			0x00 => 1, // NOP
			0x01 => 1, // Clear cache
			0x02 => 3, // Fill VRAM

			// Monochrome triangles (0x20-0x23): 4 words
			>= 0x20 and <= 0x23 => 4,
			// Textured triangles (0x24-0x27): 7 words
			>= 0x24 and <= 0x27 => 7,
			// Monochrome quads (0x28-0x2B): 5 words
			>= 0x28 and <= 0x2B => 5,
			// Textured quads (0x2C-0x2F): 9 words
			>= 0x2C and <= 0x2F => 9,
			// Gouraud triangles (0x30-0x33): 6 words
			>= 0x30 and <= 0x33 => 6,
			// Gouraud textured triangles (0x34-0x37): 9 words
			>= 0x34 and <= 0x37 => 9,
			// Gouraud quads (0x38-0x3B): 8 words
			>= 0x38 and <= 0x3B => 8,
			// Gouraud textured quads (0x3C-0x3F): 12 words
			>= 0x3C and <= 0x3F => 12,

			// Mono lines (0x40-0x47): 3 words (cmd+col+xy0+xy1)
			>= 0x40 and <= 0x47 => 3,
			// Mono polylines (0x48-0x4F): 3 words initial (cmd+col+xy0); stream xy until terminator
			>= 0x48 and <= 0x4F => 3,
			// Gouraud lines (0x50-0x57): 4 words
			>= 0x50 and <= 0x57 => 4,
			// Gouraud polylines (0x58-0x5F): 4 words initial; stream col/xy pairs until terminator
			>= 0x58 and <= 0x5F => 4,

			// Mono rectangles (0x60-0x63): 3 words
			>= 0x60 and <= 0x63 => 3,
			// Textured rectangles (0x64-0x67): 4 words
			>= 0x64 and <= 0x67 => 4,
			// 1*1 dots (0x68-0x6B): 2 words
			>= 0x68 and <= 0x6B => 2,
			// 8*8 mono rects (0x70-0x73): 2 words
			>= 0x70 and <= 0x73 => 2,
			// 8*8 textured rects (0x74-0x77): 3 words
			>= 0x74 and <= 0x77 => 3,
			// 16*16 mono rects (0x78-0x7B): 2 words
			>= 0x78 and <= 0x7B => 2,
			// 16*16 textured rects (0x7C-0x7F): 3 words
			>= 0x7C and <= 0x7F => 3,

			0x80 => 4, // VRAM->VRAM copy
			0xA0 => 3, // CPU->VRAM copy (followed by data words)
			0xC0 => 3, // VRAM->CPU copy
			0xE1 => 1, // Draw mode setting
			0xE2 => 1, // Texture window setting
			0xE3 => 1, // Set drawing area top-left
			0xE4 => 1, // Set drawing area bottom-right
			0xE5 => 1, // Set drawing offset
			0xE6 => 1, // Mask bit setting
			_ => 1, // Unknown: consume 1 word
		};
	}

	private void ExecuteGp0()
	{
		uint cmd0 = _gp0Fifo[0];
		byte cmd = (byte)(cmd0 >> 24);
		DrawCmdCount++;

		switch (cmd)
		{
			case 0x00: break; // NOP
			case 0x01: break; // Clear cache (no effect in software)
			case 0x02:
				{
					var xy = _gp0Fifo[1]; var wh = _gp0Fifo[2];
					CmdFillRect();
					break;
				}

			// Monochrome polygons (3-point)
			case 0x20:
			case 0x21:
			case 0x22:
			case 0x23:
				DrawTriMono(_gp0Fifo[0] & 0xFFFFFF, false, _gp0Fifo[1], _gp0Fifo[2], _gp0Fifo[3], 0, 0, false);
				break;

			// Textured polygons (3-point)
			case 0x24:
			case 0x25:
			case 0x26:
			case 0x27:
				DrawTriTextured3();
				break;

			// Monochrome polygons (4-point = 2 triangles)
			case 0x28:
			case 0x29:
			case 0x2A:
			case 0x2B:
				DrawQuadMono(_gp0Fifo[0] & 0xFFFFFF);
				break;

			// Textured polygons (4-point)
			case 0x2C:
			case 0x2D:
			case 0x2E:
			case 0x2F:
				DrawQuadTextured();
				break;

			// Gouraud polygons (3-point)
			case 0x30:
			case 0x31:
			case 0x32:
			case 0x33:
				DrawTriGouraud3();
				break;

			// Gouraud polygons (4-point)
			case 0x38:
			case 0x39:
			case 0x3A:
			case 0x3B:
				DrawQuadGouraud();
				break;

			// Gouraud textured (3-point)
			case 0x34:
			case 0x35:
			case 0x36:
			case 0x37:
				DrawTriGouraudTextured3();
				break;

			// Gouraud textured (4-point)
			case 0x3C:
			case 0x3D:
			case 0x3E:
			case 0x3F:
				DrawQuadGouraudTextured();
				break;

			// Lines (monochrome, fixed 2-vertex, all 8 variants)
			case 0x40:
			case 0x41:
			case 0x42:
			case 0x43:
			case 0x44:
			case 0x45:
			case 0x46:
			case 0x47:
				DrawLineFlat(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], _gp0Fifo[2]);
				break;

			// Polylines (monochrome, variable vertices: [cmd+color][xy0][xy1-or-term][xy2]...)
			// Collected 3 words; draw first segment (if not immediately terminated), enter stream mode.
			case 0x48:
			case 0x49:
			case 0x4A:
			case 0x4B:
			case 0x4C:
			case 0x4D:
			case 0x4E:
			case 0x4F:
				{
					uint plColor = _gp0Fifo[0] & 0xFFFFFF;
					uint plXy0 = _gp0Fifo[1];
					uint plXy1 = _gp0Fifo[2];
					if ((plXy1 >> 16) is not 0x5000 and not 0x5555)
					{
						DrawLineFlat(plColor, plXy0, plXy1);
						_polylineMode = true;
						_polylineGouraud = false;
						_polylinePrevColor = plColor;
						_polylinePrevXY = plXy1;
						_polylineVertIdx = 1;
					}
					break;
				}

			// Lines (gouraud, fixed 2-vertex)
			case 0x50:
			case 0x51:
			case 0x52:
			case 0x53:
				DrawLineGouraud(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], _gp0Fifo[2] & 0xFFFFFF, _gp0Fifo[3]);
				break;

			// Polylines (gouraud): [cmd+col0][xy0][col1][xy1-or-term] then stream: [col2][xy2-or-term]...
			// We collected 4 words. Draw first segment if not terminated, then enter stream mode.
			case 0x58:
			case 0x59:
			case 0x5A:
			case 0x5B:
			case 0x5C:
			case 0x5D:
			case 0x5E:
			case 0x5F:
				{
					uint plCol0 = _gp0Fifo[0] & 0xFFFFFF;
					uint plXy0 = _gp0Fifo[1];
					uint plCol1 = _gp0Fifo[2] & 0xFFFFFF;
					uint plXy1 = _gp0Fifo[3];
					if ((plXy1 >> 16) is not 0x5000 and not 0x5555)
					{
						DrawLineGouraud(plCol0, plXy0, plCol1, plXy1);
						_polylineMode = true;
						_polylineGouraud = true;
						_polylinePrevColor = plCol1;
						_polylinePrevXY = plXy1;
						_polylineVertIdx = 0; // next incoming = col2 (even index = color)
					}
					break;
				}

			// Monochrome rectangles
			case 0x60:
			case 0x61:
			case 0x62:
			case 0x63:
				DrawMonoRect(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], _gp0Fifo[2], -1, -1);
				break;

			// Textured rectangles
			case 0x64:
			case 0x65:
			case 0x66:
			case 0x67:
				DrawTexRect(true);
				break;

			// 1*1 dots
			case 0x68:
			case 0x69:
			case 0x6A:
			case 0x6B:
				DrawMonoRect(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], 0, 1, 1);
				break;

			// 8*8 rectangles
			case 0x70:
			case 0x71:
			case 0x72:
			case 0x73:
				DrawMonoRect(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], 0, 8, 8);
				break;

			// 8*8 textured
			case 0x74:
			case 0x75:
			case 0x76:
			case 0x77:
				DrawTexRect(true, 8, 8);
				break;

			// 16*16 rectangles
			case 0x78:
			case 0x79:
			case 0x7A:
			case 0x7B:
				DrawMonoRect(_gp0Fifo[0] & 0xFFFFFF, _gp0Fifo[1], 0, 16, 16);
				break;

			// 16*16 textured
			case 0x7C:
			case 0x7D:
			case 0x7E:
			case 0x7F:
				DrawTexRect(true, 16, 16);
				break;

			case 0x80: CmdVramVramCopy(); break;
			case 0xA0: CmdCpuToVram(); break;
			case 0xC0: CmdVramToCpu(); break;

			case 0xE2: CmdTexWindow(); break;
			case 0xE3:
				// Draw area is at native PSX coords (the rasterizer + GPU shader
				// both work in 1024x512 space).
				DrawAreaX1 = (int)(_gp0Fifo[0] & 0x3FF);
				DrawAreaY1 = (int)((_gp0Fifo[0] >> 10) & 0x1FF);
				break;
			case 0xE4:
				DrawAreaX2 = (int)(_gp0Fifo[0] & 0x3FF);
				DrawAreaY2 = (int)((_gp0Fifo[0] >> 10) & 0x1FF);
				break;
			case 0xE5:
				CmdDrawOffset();
				break;
			case 0xE1:
				CmdDrawMode();
				break;
			case 0xE6: _setMaskBit = (_gp0Fifo[0] & 1) != 0; _checkMaskBit = (_gp0Fifo[0] & 2) != 0; break;
		}
	}

	private void CmdDrawMode()
	{
		uint v = _gp0Fifo[0];
		// Texpage origin stored as upscaled VRAM coord, sampling adds the
		// scaled per-texel offset on top.
		_texPageX = (int)((v & 0xF) * 64);
		_texPageY = (int)(((v >> 4) & 1) * 256);
		_semiTransMode = (byte)((v >> 5) & 3);
		_texDepth = (int)((v >> 7) & 3);
		_texDisabled = (v & 0x800) != 0;
	}

	/// <summary>
	/// Apply the per-primitive texpage word from a textured polygon command.
	/// The texpage attribute appears in the upper 16 bits of UV1 (or UV0 for
	/// some quad variants). Per NoCash, this overrides bits
	/// 0-8 and bit 11 of the global GP0(0xE1) draw-mode register, i.e. it
	/// updates ALL of: TX, TY, ABR (semi-transparency mode), TP (color depth),
	/// and texture-disable. Previously we only applied TX/TY/TP, Driver's
	/// smoke (and any other game using per-primitive ABR overrides) would get
	/// the stale global semi-transparency mode and render as opaque/black.
	/// </summary>
	private void ApplyPolygonTexpage(uint tp)
	{
		_texPageX = (int)(tp & 0xF) * 64;
		_texPageY = (int)((tp >> 4) & 1) * 256;
		_semiTransMode = (byte)((tp >> 5) & 3);
		_texDepth = (int)((tp >> 7) & 3);
		_texDisabled = (tp & 0x800) != 0;
	}

	/// <summary>
	/// Decodes the CLUT word from a textured-polygon's UV0 register and stores
	/// the CLUT origin in upscaled VRAM coordinates. Sampling later adds the
	/// scaled per-entry index on top.
	/// </summary>
	private void ApplyPolygonClut(uint uv0)
	{
		_clutX = (int)((uv0 >> 16) & 0x3F) * 16;
		_clutY = (int)((uv0 >> 22) & 0x1FF);
	}

	private void CmdTexWindow()
	{
		uint v = _gp0Fifo[0];
		_texWinMaskX = (int)(v & 0x1F) * 8;
		_texWinMaskY = (int)((v >> 5) & 0x1F) * 8;
		_texWinOffX = (int)((v >> 10) & 0x1F) * 8;
		_texWinOffY = (int)((v >> 15) & 0x1F) * 8;
	}

	private void CmdDrawOffset()
	{
		uint v = _gp0Fifo[0];
		// 11-bit signed values
		int x = (int)(v & 0x7FF);
		int y = (int)((v >> 11) & 0x7FF);
		if ((x & 0x400) != 0) x |= unchecked((int)0xFFFFF800);
		if ((y & 0x400) != 0) y |= unchecked((int)0xFFFFF800);
		DrawOffsetX = x;
		DrawOffsetY = y;
	}

	private void CmdFillRect()
	{
		uint color = _gp0Fifo[0] & 0xFFFFFF;
		uint xy = _gp0Fifo[1];
		uint wh = _gp0Fifo[2];

		// Hardware coordinate alignment:
		//   X     = X & 0x3F0          (force 16-pixel alignment, low 4 bits cleared)
		//   Y     = Y & 0x1FF          (9-bit max within VRAM height)
		//   width = ((W & 0x3FF) + 0xF) & ~0xF   (round UP to multiple of 16)
		//   height= H & 0x1FF
		// Without these masks, a non-aligned X or non-multiple-of-16 W produces
		// stripes/gaps at the wrong locations. BIOS uses GP0(0x02) to clear the
		// framebuffer between FMV frames; mis-aligned fills leave garbage at the
		// edges that the next frame doesn't overwrite.
		// FillRect operates directly on VRAM (no DrawOffset, no clipping by
		// DrawArea, this is the framebuffer-clear primitive). All coords
		// scale to upscaled VRAM space.
		int x = (int)(xy & 0x3F0);
		int y = (int)((xy >> 16) & 0x1FF);
		int w = (int)((((wh & 0x3FF) + 0xF) & ~0xFu));
		int h = (int)((wh >> 16) & 0x1FF);

		// GPU rasterizer: also fill the corresponding region of
		// VramTex so accumulated polygons from previous frames are overwritten.
		// Without this, the GPU path's "no per-frame VramTex clear" (needed for
		// double-buffering) leads to stale polygons piling up, visible as the
		// PS-logo / Crash intro layering on top of each other.
		// Unclipped: PSX Fill VRAM bypasses DrawArea by design.
		GpuPushQuadUnclipped(x, y, w, h, color);

		ushort c = ColorToVram(color);
		FillRect(x, y, w, h, c);
	}

	private void CmdVramVramCopy()
	{
		uint src = _gp0Fifo[1];
		uint dst = _gp0Fifo[2];
		uint wh = _gp0Fifo[3];
		// All VRAM-to-VRAM copy coords scale to upscaled space.
		int sx = (int)(src & 0x3FF), sy = (int)((src >> 16) & 0x1FF);
		int dx = (int)(dst & 0x3FF), dy = (int)((dst >> 16) & 0x1FF);
		int w = (int)(wh & 0x3FF), h = (int)((wh >> 16) & 0x1FF);
		if (w == 0) w = 1024;
		if (h == 0) h = 512;

		// GPU rasterizer: mark the destination region as
		// "GPU didn't write here" so the display compute falls back to
		// VramSourceTex. Driver 2's menu restores button regions via this
		// command, without the hook, last frame's GPU output stays in VramTex
		// and the previously-highlighted button never gets cleared visually.
		GpuPushClearQuad(dx, dy, w, h);

		VramCopy(sx, sy, dx, dy, w, h);
	}

	private void CmdCpuToVram()
	{
		uint xy = _gp0Fifo[1];
		uint wh = _gp0Fifo[2];
		_vramTransferX = (int)(xy & 0x3FF);
		_vramTransferY = (int)((xy >> 16) & 0x1FF);
		int rawW = (int)(wh & 0xFFFF);
		int rawH = (int)(wh >> 16);
		_vramTransferW = ((rawW - 1) & 0x3FF) + 1;
		_vramTransferH = ((rawH - 1) & 0x1FF) + 1;
		_vramTransferCX = 0;
		_vramTransferCY = 0;
		_vramTransferPixels = 0;
		_gp0VramWrite = true;

		// GPU rasterizer: mark this region of VramTex as
		// "GPU didn't write here" via a transparent quad. The display compute
		// shader falls back to VramSourceTex (native CPU mirror) for alpha=0
		// pixels, VramSourceTex will contain the uploaded data after the
		// per-frame snapshot. Without this, accumulated alpha=1 from previous
		// frames in VramTex blocks the fallback and the upload stays invisible.
		GpuPushClearQuad(_vramTransferX, _vramTransferY, _vramTransferW, _vramTransferH);
	}

	private void CmdVramToCpu()
	{
		uint xy = _gp0Fifo[1];
		uint wh = _gp0Fifo[2];
		_vramReadX = (int)(xy & 0x3FF);
		_vramReadY = (int)((xy >> 16) & 0x1FF);
		int rawW = (int)(wh & 0xFFFF);
		int rawH = (int)(wh >> 16);
		_vramReadW = ((rawW - 1) & 0x3FF) + 1;
		_vramReadH = ((rawH - 1) & 0x1FF) + 1;
		_vramReadCX = _vramReadCY = 0;
		_gp0VramRead = true;
	}

	private void WriteVramPixels(uint word)
	{
		WriteOneVramPixel((ushort)word);
		WriteOneVramPixel((ushort)(word >> 16));
	}

	private void WriteOneVramPixel(ushort pixel)
	{
		if (_vramTransferCY >= _vramTransferH)
		{
			_gp0VramWrite = false;
			return;
		}
		// VRAM is at native PSX resolution, one source pixel writes one VRAM
		// pixel. Upscaling happens on the GPU rasterizer into its own render
		// target; the CPU side stays at 1024x512.
		int nx = (_vramTransferX + _vramTransferCX) & 0x3FF;
		int ny = (_vramTransferY + _vramTransferCY) & 0x1FF;
		int idx = ny * PsxConstants.VramWidth + nx;
		// Honor GP0(E6) mask settings on CPU->VRAM upload: skip the write
		// when check-mask is set and the destination pixel is already masked;
		// OR in bit 15 when set-mask is set. Both default off, so for the
		// common case this is identical to a plain write.
		if (!(_checkMaskBit && (Vram[idx] & 0x8000) != 0))
		{
			if (_setMaskBit) pixel |= 0x8000;
			Vram[idx] = pixel;
			_trueColorVram[idx] = Rgb555ToRgba(pixel);
		}
		_vramTransferPixels++;

		_vramTransferCX++;
		if (_vramTransferCX >= _vramTransferW)
		{
			_vramTransferCX = 0;
			_vramTransferCY++;
			if (_vramTransferCY >= _vramTransferH)
			{
				_gp0VramWrite = false;
				PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info,
					$"[GPU] CPU->VRAM complete: {_vramTransferPixels} px written (expected {_vramTransferW * _vramTransferH})");
			}
		}
	}

	// Called when VBlank starts
	public void OnVBlank()
	{
		FrameCount++;
		DrawCmdCount = 0;
		_gpuStat ^= (1u << 31); // Toggle even/odd interlace field, BIOS uses this as frame-sync
		_psx.Interrupts.Raise(PsxConstants.IrqVblank);
	}

	// --- Helper: convert 24-bit BGR color to 15-bit RGB555 (PSX format) ---
	public static ushort ColorToVram(uint rgb24)
	{
		uint r = (rgb24 & 0xFF) >> 3;
		uint g = ((rgb24 >> 8) & 0xFF) >> 3;
		uint b = ((rgb24 >> 16) & 0xFF) >> 3;
		return (ushort)(r | (g << 5) | (b << 10));
	}

	// PSX 4*4 ordered dither matrix
	// Applied to 8-bit R/G/B before truncating to 5-bit.
	private static readonly int[] DitherMatrix = {
		-4, +0, -3, +1,
		+2, -2, +3, -1,
		-3, +1, -4, +0,
		+3, -1, +2, -2,
	};

	// Convert 24-bit color to RGB555 with PSX ordered dithering.
	private static ushort ColorToVramDithered(uint rgb24, int x, int y)
	{
		int d = DitherMatrix[(y & 3) * 4 + (x & 3)];
		int r = Math.Clamp((int)(rgb24 & 0xFF) + d, 0, 255) >> 3;
		int g = Math.Clamp((int)((rgb24 >> 8) & 0xFF) + d, 0, 255) >> 3;
		int b = Math.Clamp((int)((rgb24 >> 16) & 0xFF) + d, 0, 255) >> 3;
		return (ushort)(r | (g << 5) | (b << 10));
	}

	// --- VRAM helpers ---

	private void FillRect(int x, int y, int w, int h, ushort color)
	{
		uint tc = Rgb555ToRgba(color);
		for (int row = 0; row < h; row++)
		{
			int vy = (y + row) & PsxConstants.VramHeightMask;
			for (int col = 0; col < w; col++)
			{
				int vx = (x + col) & PsxConstants.VramWidthMask;
				int idx = vy * PsxConstants.VramWidth + vx;
				Vram[idx] = color;
				_trueColorVram[idx] = tc;
			}
		}
	}

	private void VramCopy(int sx, int sy, int dx, int dy, int w, int h)
	{
		for (int row = 0; row < h; row++)
		{
			for (int col = 0; col < w; col++)
			{
				int si = ((sy + row) & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + ((sx + col) & PsxConstants.VramWidthMask);
				int di = ((dy + row) & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + ((dx + col) & PsxConstants.VramWidthMask);
				// Honor mask settings on VRAM->VRAM copy: skip masked destinations, OR bit 15 on
				// the copied pixel when set-mask is active. Both default off.
				if (_checkMaskBit && (Vram[di] & 0x8000) != 0) continue;
				ushort src = Vram[si];
				if (_setMaskBit) src |= 0x8000;
				Vram[di] = src;
				_trueColorVram[di] = _trueColorVram[si];
			}
		}
	}

	// --- Inline pixel writing with clip and mask checks ---
	private void PlotPixel(int x, int y, ushort color)
	{
		if (x < DrawAreaX1 || x > DrawAreaX2 || y < DrawAreaY1 || y > DrawAreaY2)
			return;
		int idx = (y & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (x & PsxConstants.VramWidthMask);
		if (_checkMaskBit && (Vram[idx] & 0x8000) != 0) return;
		if (_setMaskBit) color |= 0x8000;
		Vram[idx] = color;
		_trueColorVram[idx] = Rgb555ToRgba(color);
	}

	// PlotPixel with true-color: writes dithered RGB555 to VRAM but keeps
	// the full 24-bit color in the true-color overlay for smooth display.
	private void PlotPixelTrueColor(int x, int y, ushort color, uint rgb24)
	{
		if (x < DrawAreaX1 || x > DrawAreaX2 || y < DrawAreaY1 || y > DrawAreaY2)
			return;
		int idx = (y & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (x & PsxConstants.VramWidthMask);
		if (_checkMaskBit && (Vram[idx] & 0x8000) != 0) return;
		if (_setMaskBit) color |= 0x8000;
		Vram[idx] = color;
		uint r = rgb24 & 0xFF, g = (rgb24 >> 8) & 0xFF, b = (rgb24 >> 16) & 0xFF;
		_trueColorVram[idx] = r | (g << 8) | (b << 16) | 0xFF000000u;
	}

	// Expand RGB555 to RGBA8888 (for non-shaded PlotPixel writes to the true-color buffer)
	private static uint Rgb555ToRgba(ushort px)
	{
		uint r = (uint)(px & 0x1F);
		uint g = (uint)((px >> 5) & 0x1F);
		uint b = (uint)((px >> 10) & 0x1F);
		r = (r << 3) | (r >> 2);
		g = (g << 3) | (g >> 2);
		b = (b << 3) | (b >> 2);
		return r | (g << 8) | (b << 16) | 0xFF000000u;
	}

	private static ushort ModulateTextureColor(ushort texel, uint color24)
	{
		int tr = texel & 0x1F;
		int tg = (texel >> 5) & 0x1F;
		int tb = (texel >> 10) & 0x1F;
		int vr = (int)(color24 & 0xFF);
		int vg = (int)((color24 >> 8) & 0xFF);
		int vb = (int)((color24 >> 16) & 0xFF);

		int r = Math.Min(31, (tr * vr) >> 7);
		int g = Math.Min(31, (tg * vg) >> 7);
		int b = Math.Min(31, (tb * vb) >> 7);
		return (ushort)(r | (g << 5) | (b << 10) | (texel & 0x8000));
	}

	private ushort ApplySemiTransparency(ushort bgColor, ushort fgColor)
	{
		uint bgBits = bgColor;
		uint fgBits = fgColor;

		// Modes 0 and 1 use a packed-arithmetic blend (blargg's 15bpp pixel math)
		// that REQUIRES `fg & 0x8000` to be set, otherwise bit 15 of bg leaks into
		// bit 14 of the result, adding +16 to the B channel. Symptom: non-textured
		// semi-transparent black overlays (RE2 / Driver 2 pause menus) tint the
		// background BLUE instead of darkening it neutrally.
		//
		// Modes 2 and 3 explicitly mask/set bit 15 inside the formula, so they
		// don't need this and are left unchanged.

		switch (_semiTransMode)
		{
			case 0:
				bgBits |= 0x8000u;
				fgBits |= 0x8000u;
				return (ushort)(((fgBits + bgBits) - ((fgBits ^ bgBits) & 0x0421u)) >> 1);

			case 1:
				{
					bgBits &= ~0x8000u;
					fgBits |= 0x8000u;
					uint sum = fgBits + bgBits;
					uint carry = (sum - ((fgBits ^ bgBits) & 0x8421u)) & 0x8420u;
					return (ushort)((sum - carry) | (carry - (carry >> 5)));
				}

			case 2:
				{
					bgBits |= 0x8000u;
					fgBits &= ~0x8000u;
					uint diff = bgBits - fgBits + 0x108420u;
					uint borrow = (diff - ((bgBits ^ fgBits) & 0x108420u)) & 0x108420u;
					return (ushort)((diff - borrow) & (borrow - (borrow >> 5)));
				}

			case 3:
				{
					bgBits &= ~0x8000u;
					fgBits = ((fgBits >> 2) & 0x1CE7u) | 0x8000u;
					uint sum = fgBits + bgBits;
					uint carry = (sum - ((fgBits ^ bgBits) & 0x8421u)) & 0x8420u;
					return (ushort)((sum - carry) | (carry - (carry >> 5)));
				}

			default:
				return fgColor;
		}
	}

	private bool IsPrimitiveSemiTransparent() => ((_gp0Fifo[0] >> 25) & 1) != 0;
	private bool IsPrimitiveRawTexture() => ((_gp0Fifo[0] >> 24) & 1) != 0;

	private void PlotPixelGpu(int x, int y, ushort color, bool textureMapped, bool semiTransparent)
	{
		if (x < DrawAreaX1 || x > DrawAreaX2 || y < DrawAreaY1 || y > DrawAreaY2)
			return;

		int idx = (y & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (x & PsxConstants.VramWidthMask);
		ushort bg = Vram[idx];
		if (_checkMaskBit && (bg & 0x8000) != 0)
			return;

		ushort final = color;
		if (semiTransparent && ((color & 0x8000) != 0 || !textureMapped))
		{
			final = ApplySemiTransparency(bg, color);
			if (!textureMapped)
				final &= 0x7FFF;
		}

		if (_setMaskBit)
			final |= 0x8000;

		Vram[idx] = final;
		_trueColorVram[idx] = Rgb555ToRgba(final);
	}

	// --- Primitive drawing ---

	private void DrawMonoRect(uint color, uint xy, uint wh, int forceW, int forceH)
	{
		int x = SignExtend11((int)(xy & 0x7FF)) + DrawOffsetX;
		int y = SignExtend11((int)((xy >> 16) & 0x7FF)) + DrawOffsetY;
		// Sprite W/H are 10-bit / 9-bit on hardware. Without these masks, junk values in the
		// upper bits (from overflowed GTE results or other primitive corruption)
		// produce VRAM-spanning rectangles. Combined with the missing `IsLargePrimitive`
		// check on rectangles, a single bad sprite can blow away the entire VRAM.
		// Scaled to upscaled pixel space.
		int w = (forceW > 0 ? forceW : (int)(wh & 0x3FF));
		int h = (forceH > 0 ? forceH : (int)((wh >> 16) & 0x1FF));

		// GPU rasterizer: opaque or semi-transparent rect. Semi-trans goes
		// through the same path now, the fragment shader handles
		// the PSX blend modes by sampling VramCopyTex for the background.
		bool semiTransparent = IsPrimitiveSemiTransparent();
		if (!semiTransparent)
			GpuPushQuad(x, y, w, h, color);
		else
			GpuPushQuadSemiTrans(x, y, w, h, color, _semiTransMode);

		ushort c = ColorToVram(color);
		for (int row = 0; row < h; row++)
			for (int col = 0; col < w; col++)
				PlotPixelGpu(x + col, y + row, c, false, semiTransparent);
	}

	private void DrawTexRect(bool textured, int forceW = -1, int forceH = -1)
	{
		uint color = _gp0Fifo[0] & 0xFFFFFF;
		uint xy = _gp0Fifo[1];
		uint uv = _gp0Fifo[2];
		uint wh = _gp0FifoLen >= 4 ? _gp0Fifo[3] : 0;

		int x = SignExtend11((int)(xy & 0x7FF)) + DrawOffsetX;
		int y = SignExtend11((int)((xy >> 16) & 0x7FF)) + DrawOffsetY;
		int u = (int)(uv & 0xFF);
		int v = (int)((uv >> 8) & 0xFF);

		if (_gp0FifoLen >= 3) ApplyPolygonClut(uv);

		// Sprite W/H are 10-bit / 9-bit on hardware.
		// See DrawMonoRect for the rationale. Scaled to upscaled pixel space.
		int w = (forceW > 0 ? forceW : (int)(wh & 0x3FF));
		int h = (forceH > 0 ? forceH : (int)((wh >> 16) & 0x1FF));
		bool rawTexture = IsPrimitiveRawTexture();
		bool semiTransparent = IsPrimitiveSemiTransparent();

		// GPU rasterizer: textured sprite rectangle as two
		// triangles. PSX sprites apply UV at 1 native texel per native pixel,
		// corner UVs are (u, v), (u+nativeW, v), (u, v+nativeH), (u+nativeW, v+nativeH).
		// Semi-trans now goes through the same path, the PS samples
		// VramCopyTex for the bg and applies the PSX blend.
		if (textured && !_texDisabled)
		{
			int u0 = u, v0 = v;
			int u1 = u + w, v1 = v;
			int u2 = u, v2 = v + h;
			int u3 = u + w, v3 = v + h;
			int x1 = x + w;
			int y1 = y + h;
			GpuPushTexTri(
				x, y, u0, v0, color,
				x1, y, u1, v1, color,
				x1, y1, u3, v3, color,
				_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
			GpuPushTexTri(
				x, y, u0, v0, color,
				x1, y1, u3, v3, color,
				x, y1, u2, v2, color,
				_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
		}

		// Sprite UV advances 1:1 with rasterized pixels (native resolution).
		for (int row = 0; row < h; row++)
		{
			int texV = v + row;
			for (int col = 0; col < w; col++)
			{
				ushort c;
				if (textured && !_texDisabled)
				{
					int texU = u + col;
					c = SampleTexture(texU, texV);
					if (c == 0) continue; // transparent texel
					if (!rawTexture)
						c = ModulateTextureColor(c, color);
				}
				else
					c = ColorToVram(color);
				PlotPixelGpu(x + col, y + row, c, textured && !_texDisabled, semiTransparent);
			}
		}
	}

	// --- Triangle drawing (edge-walking rasterizer) ---

	private void DrawTriMono(uint color, bool textured,
		uint v0xy, uint v1xy, uint v2xy,
		int u0v0, int u1v1, bool gouraud)
	{
		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX;
		int y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX;
		int y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2xy & 0x7FF)) + DrawOffsetX;
		int y2 = SignExtend11((int)((v2xy >> 16) & 0x7FF)) + DrawOffsetY;

		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: opaque or semi-trans flat tri. Semi-trans
		// flag flows through to the fragment shader which samples VramCopyTex
		// for the background and applies the PSX blend mode.
		GpuPushTri(x0, y0, x1, y1, x2, y2, color, semiTransparent, _semiTransMode);

		ushort c = ColorToVram(color);
		DrawTriangle(x0, y0, x1, y1, x2, y2, c, semiTransparent);
	}

	private void DrawTriTextured3()
	{
		uint color = _gp0Fifo[0] & 0xFFFFFF;
		uint v0xy = _gp0Fifo[1], uv0 = _gp0Fifo[2];
		uint v1xy = _gp0Fifo[3], uv1 = _gp0Fifo[4];
		uint v2xy = _gp0Fifo[5], uv2 = _gp0Fifo[6];

		// uv0 upper 16 bits = CLUT attribute
		ApplyPolygonClut(uv0);
		// uv1 upper 16 bits = texture page (same layout as GP0(E1))
		ApplyPolygonTexpage(uv1 >> 16);

		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX;
		int y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX;
		int y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2xy & 0x7FF)) + DrawOffsetX;
		int y2 = SignExtend11((int)((v2xy >> 16) & 0x7FF)) + DrawOffsetY;

		int u0 = (int)(uv0 & 0xFF), v0 = (int)((uv0 >> 8) & 0xFF);
		int u1 = (int)(uv1 & 0xFF), v1 = (int)((uv1 >> 8) & 0xFF);
		int u2 = (int)(uv2 & 0xFF), v2 = (int)((uv2 >> 8) & 0xFF);

		bool rawTexture = IsPrimitiveRawTexture();
		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: textured tri. Semi-trans flag flows
		// through to the PS which samples VramCopyTex for the background and
		// applies the PSX blend mode, upscaled blending on GPU.
		GpuPushTexTri(x0, y0, u0, v0, color, x1, y1, u1, v1, color, x2, y2, u2, v2, color,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);

		DrawTriangleTextured(x0, y0, u0, v0, color, x1, y1, u1, v1, color, x2, y2, u2, v2, color, rawTexture, semiTransparent);
	}

	private void DrawTriGouraud3()
	{
		uint c0 = _gp0Fifo[0] & 0xFFFFFF;
		uint v0 = _gp0Fifo[1];
		uint c1 = _gp0Fifo[2] & 0xFFFFFF;
		uint v1 = _gp0Fifo[3];
		uint c2 = _gp0Fifo[4] & 0xFFFFFF;
		uint v2 = _gp0Fifo[5];

		int x0 = SignExtend11((int)(v0 & 0x7FF)) + DrawOffsetX, y0 = SignExtend11((int)((v0 >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1 & 0x7FF)) + DrawOffsetX, y1 = SignExtend11((int)((v1 >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2 & 0x7FF)) + DrawOffsetX, y2 = SignExtend11((int)((v2 >> 16) & 0x7FF)) + DrawOffsetY;

		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: Gouraud tri with optional semi-trans blend.
		GpuPushTriGouraud(x0, y0, c0, x1, y1, c1, x2, y2, c2, semiTransparent, _semiTransMode);

		DrawTriangleGouraud(x0, y0, c0, x1, y1, c1, x2, y2, c2, semiTransparent);
	}

	private void DrawTriGouraudTextured3()
	{
		uint c0 = _gp0Fifo[0] & 0xFFFFFF;
		uint v0xy = _gp0Fifo[1], uv0 = _gp0Fifo[2];
		uint c1 = _gp0Fifo[3] & 0xFFFFFF;
		uint v1xy = _gp0Fifo[4], uv1 = _gp0Fifo[5];
		uint c2 = _gp0Fifo[6] & 0xFFFFFF;
		uint v2xy = _gp0Fifo[7], uv2 = _gp0Fifo[8];

		ApplyPolygonClut(uv0);
		ApplyPolygonTexpage(uv1 >> 16);

		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX;
		int y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX;
		int y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2xy & 0x7FF)) + DrawOffsetX;
		int y2 = SignExtend11((int)((v2xy >> 16) & 0x7FF)) + DrawOffsetY;

		int u0 = (int)(uv0 & 0xFF), v0 = (int)((uv0 >> 8) & 0xFF);
		int u1 = (int)(uv1 & 0xFF), v1 = (int)((uv1 >> 8) & 0xFF);
		int u2 = (int)(uv2 & 0xFF), v2 = (int)((uv2 >> 8) & 0xFF);

		bool rawTexture = IsPrimitiveRawTexture();
		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: Gouraud-textured tri with optional semi-trans.
		GpuPushTexTri(x0, y0, u0, v0, c0, x1, y1, u1, v1, c1, x2, y2, u2, v2, c2,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);

		DrawTriangleTextured(x0, y0, u0, v0, c0, x1, y1, u1, v1, c1, x2, y2, u2, v2, c2, rawTexture, semiTransparent);
	}

	private void DrawQuadMono(uint color)
	{
		DrawTriMono(color, false, _gp0Fifo[1], _gp0Fifo[2], _gp0Fifo[3], 0, 0, false);
		DrawTriMono(color, false, _gp0Fifo[2], _gp0Fifo[3], _gp0Fifo[4], 0, 0, false);
	}

	private void DrawQuadTextured()
	{
		// Words: [cmd+col][v0xy][uv0/clut][v1xy][uv1/tpage][v2xy][uv2][v3xy][uv3]
		uint color = _gp0Fifo[0] & 0xFFFFFF;
		uint v0xy = _gp0Fifo[1], uv0 = _gp0Fifo[2];
		uint v1xy = _gp0Fifo[3], uv1 = _gp0Fifo[4];
		uint v2xy = _gp0Fifo[5], uv2 = _gp0Fifo[6];
		uint v3xy = _gp0Fifo[7], uv3 = _gp0Fifo[8];

		ApplyPolygonClut(uv0);
		ApplyPolygonTexpage(uv1 >> 16);

		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX, y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX, y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2xy & 0x7FF)) + DrawOffsetX, y2 = SignExtend11((int)((v2xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x3 = SignExtend11((int)(v3xy & 0x7FF)) + DrawOffsetX, y3 = SignExtend11((int)((v3xy >> 16) & 0x7FF)) + DrawOffsetY;

		int tu0 = (int)(uv0 & 0xFF), tv0 = (int)((uv0 >> 8) & 0xFF);
		int tu1 = (int)(uv1 & 0xFF), tv1 = (int)((uv1 >> 8) & 0xFF);
		int tu2 = (int)(uv2 & 0xFF), tv2 = (int)((uv2 >> 8) & 0xFF);
		int tu3 = (int)(uv3 & 0xFF), tv3 = (int)((uv3 >> 8) & 0xFF);

		bool rawTexture = IsPrimitiveRawTexture();
		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: textured quad = two textured triangles.
		// Semi-trans flows through to the PS for GPU blending.
		GpuPushTexTri(x0, y0, tu0, tv0, color, x1, y1, tu1, tv1, color, x2, y2, tu2, tv2, color,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
		GpuPushTexTri(x1, y1, tu1, tv1, color, x2, y2, tu2, tv2, color, x3, y3, tu3, tv3, color,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
		DrawTriangleTextured(x0, y0, tu0, tv0, color, x1, y1, tu1, tv1, color, x2, y2, tu2, tv2, color, rawTexture, semiTransparent);
		DrawTriangleTextured(x1, y1, tu1, tv1, color, x2, y2, tu2, tv2, color, x3, y3, tu3, tv3, color, rawTexture, semiTransparent);
	}

	private void DrawQuadGouraud()
	{
		DrawTriGouraud3();
		// Second triangle (v1, v2, v3)
		// Preserve the original command byte (including the semi-transparency flag in bit 25)
		// but replace the color24 with c1's color so DrawTriGouraud3 reads the right base color.
		uint[] s = _gp0Fifo.ToArray();
		_gp0Fifo[0] = (s[0] & 0xFF000000u) | (s[2] & 0x00FFFFFFu); // cmd byte from original, color from c1
		_gp0Fifo[1] = s[3];
		_gp0Fifo[2] = s[4]; _gp0Fifo[3] = s[5];
		_gp0Fifo[4] = s[6]; _gp0Fifo[5] = s[7];
		DrawTriGouraud3();
	}

	private void DrawQuadGouraudTextured()
	{
		// Gouraud textured quad layout (12 words):
		// [0]=cmd+col0 [1]=v0xy [2]=uv0+clut [3]=col1 [4]=v1xy [5]=uv1+tpage
		// [6]=col2 [7]=v2xy [8]=uv2 [9]=col3 [10]=v3xy [11]=uv3
		uint c0 = _gp0Fifo[0] & 0xFFFFFF;
		uint v0xy = _gp0Fifo[1], uv0 = _gp0Fifo[2];
		uint c1 = _gp0Fifo[3] & 0xFFFFFF;
		uint v1xy = _gp0Fifo[4], uv1 = _gp0Fifo[5];
		uint c2 = _gp0Fifo[6] & 0xFFFFFF;
		uint v2xy = _gp0Fifo[7], uv2 = _gp0Fifo[8];
		uint c3 = _gp0Fifo[9] & 0xFFFFFF;
		uint v3xy = _gp0Fifo[10], uv3 = _gp0Fifo[11];

		ApplyPolygonClut(uv0);
		ApplyPolygonTexpage(uv1 >> 16);

		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX, y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX, y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x2 = SignExtend11((int)(v2xy & 0x7FF)) + DrawOffsetX, y2 = SignExtend11((int)((v2xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x3 = SignExtend11((int)(v3xy & 0x7FF)) + DrawOffsetX, y3 = SignExtend11((int)((v3xy >> 16) & 0x7FF)) + DrawOffsetY;

		int tu0 = (int)(uv0 & 0xFF), tv0 = (int)((uv0 >> 8) & 0xFF);
		int tu1 = (int)(uv1 & 0xFF), tv1 = (int)((uv1 >> 8) & 0xFF);
		int tu2 = (int)(uv2 & 0xFF), tv2 = (int)((uv2 >> 8) & 0xFF);
		int tu3 = (int)(uv3 & 0xFF), tv3 = (int)((uv3 >> 8) & 0xFF);

		bool rawTexture = IsPrimitiveRawTexture();
		bool semiTransparent = IsPrimitiveSemiTransparent();
		// GPU rasterizer: Gouraud-textured quad = two tris with
		// optional GPU semi-trans blending.
		GpuPushTexTri(x0, y0, tu0, tv0, c0, x1, y1, tu1, tv1, c1, x2, y2, tu2, tv2, c2,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
		GpuPushTexTri(x1, y1, tu1, tv1, c1, x2, y2, tu2, tv2, c2, x3, y3, tu3, tv3, c3,
			_texPageX, _texPageY, _clutX, _clutY, _texDepth, rawTexture, semiTransparent, _semiTransMode);
		DrawTriangleTextured(x0, y0, tu0, tv0, c0, x1, y1, tu1, tv1, c1, x2, y2, tu2, tv2, c2, rawTexture, semiTransparent);
		DrawTriangleTextured(x1, y1, tu1, tv1, c1, x2, y2, tu2, tv2, c2, x3, y3, tu3, tv3, c3, rawTexture, semiTransparent);
	}

	// --- Line drawing (Bresenham) ---

	private void DrawLineFlat(uint color, uint v0xy, uint v1xy)
	{
		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX;
		int y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX;
		int y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;

		DrawLine(x0, y0, x1, y1, color, color, false, IsPrimitiveSemiTransparent());
	}

	private void DrawLineGouraud(uint c0, uint v0xy, uint c1, uint v1xy)
	{
		int x0 = SignExtend11((int)(v0xy & 0x7FF)) + DrawOffsetX;
		int y0 = SignExtend11((int)((v0xy >> 16) & 0x7FF)) + DrawOffsetY;
		int x1 = SignExtend11((int)(v1xy & 0x7FF)) + DrawOffsetX;
		int y1 = SignExtend11((int)((v1xy >> 16) & 0x7FF)) + DrawOffsetY;

		DrawLine(x0, y0, x1, y1, c0, c1, true, IsPrimitiveSemiTransparent());
	}

	private void DrawLine(int x0, int y0, int x1, int y1, uint c0, uint c1, bool dither, bool semiTransparent)
	{
		int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
		int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
		int err = dx + dy;
		int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0)) + 1;
		int step = 0;
		while (true)
		{
			float t = steps > 1 ? (float)step / (steps - 1) : 0;
			uint col = LerpColor24(c0, c1, t);
			if (dither)
			{
				ushort dithered = ColorToVramDithered(col, x0, y0);
				if (semiTransparent)
					PlotPixelGpu(x0, y0, dithered, false, true);
				else
					PlotPixelTrueColor(x0, y0, dithered, col);
			}
			else
				PlotPixelGpu(x0, y0, ColorToVram(col), false, semiTransparent);
			if (x0 == x1 && y0 == y1) break;
			int e2 = 2 * err;
			if (e2 >= dy) { err += dy; x0 += sx; }
			if (e2 <= dx) { err += dx; y0 += sy; }
			step++;
		}
	}

	// --- Software rasterizer helpers ---

	/// <summary>
	/// PSX hardware rejects any primitive whose bounding box exceeds 1023*511 pixels.
	/// Without this check, GTE perspective projections near/behind the camera produce
	/// extreme vertex coordinates that the rasterizer turns into screen-filling polygons.
	/// Vertex coords arrive scaled, so the threshold scales too (1024*scale x 512*scale).
	/// </summary>
	private static bool IsLargePrimitive(int x0, int y0, int x1, int y1, int x2, int y2)
	{
		int minX = Math.Min(x0, Math.Min(x1, x2));
		int maxX = Math.Max(x0, Math.Max(x1, x2));
		int minY = Math.Min(y0, Math.Min(y1, y2));
		int maxY = Math.Max(y0, Math.Max(y1, y2));
		return (maxX - minX) >= PsxConstants.VramWidth || (maxY - minY) >= PsxConstants.VramHeight;
	}

	private void DrawTriangle(int x0, int y0, int x1, int y1, int x2, int y2, ushort color, bool semiTransparent)
	{
		if (IsLargePrimitive(x0, y0, x1, y1, x2, y2)) return;

		// Sort vertices by Y
		if (y0 > y1) { Swap(ref x0, ref x1); Swap(ref y0, ref y1); }
		if (y0 > y2) { Swap(ref x0, ref x2); Swap(ref y0, ref y2); }
		if (y1 > y2) { Swap(ref x1, ref x2); Swap(ref y1, ref y2); }

		int totalH = y2 - y0;
		if (totalH == 0) return;

		for (int y = y0; y <= y2; y++)
		{
			bool second = y > y1 || y1 == y0;
			int segH = second ? y2 - y1 : y1 - y0;
			if (segH == 0) continue;

			float alpha = (float)(y - y0) / totalH;
			float beta = second ? (float)(y - y1) / segH : (float)(y - y0) / segH;

			int ax = x0 + (int)((x2 - x0) * alpha);
			int bx = second ? x1 + (int)((x2 - x1) * beta) : x0 + (int)((x1 - x0) * beta);

			if (ax > bx) Swap(ref ax, ref bx);
			// Half-open right edge: when two triangles share a diagonal edge
			// (e.g. the two halves of a quad), the rightmost pixel of one
			// triangle is the leftmost pixel of the other. Drawing both
			// double-blends semi-transparent pixels, producing the visible
			// diagonal seam users see in shadows / pause-menu backgrounds /
			// any quad with semi-transparency. Single-pixel scanlines
			// (apex of a triangle) still draw their one pixel.
			int xEnd = (ax == bx) ? bx : bx - 1;
			for (int x = ax; x <= xEnd; x++)
				PlotPixelGpu(x, y, color, false, semiTransparent);
		}
	}

	private void DrawTriangleGouraud(int x0, int y0, uint c0, int x1, int y1, uint c1, int x2, int y2, uint c2, bool semiTransparent)
	{
		if (IsLargePrimitive(x0, y0, x1, y1, x2, y2)) return;

		if (y0 > y1) { Swap(ref x0, ref x1); Swap(ref y0, ref y1); SwapU(ref c0, ref c1); }
		if (y0 > y2) { Swap(ref x0, ref x2); Swap(ref y0, ref y2); SwapU(ref c0, ref c2); }
		if (y1 > y2) { Swap(ref x1, ref x2); Swap(ref y1, ref y2); SwapU(ref c1, ref c2); }

		int totalH = y2 - y0;
		if (totalH == 0) return;

		for (int y = y0; y <= y2; y++)
		{
			bool second = y > y1 || y1 == y0;
			int segH = second ? y2 - y1 : y1 - y0;
			if (segH == 0) continue;

			float alpha = (float)(y - y0) / totalH;
			float beta = second ? (float)(y - y1) / segH : (float)(y - y0) / segH;

			int ax = x0 + (int)((x2 - x0) * alpha);
			int bx = second ? x1 + (int)((x2 - x1) * beta) : x0 + (int)((x1 - x0) * beta);

			uint ac = LerpColor24(c0, c2, alpha);
			uint bc = second ? LerpColor24(c1, c2, beta) : LerpColor24(c0, c1, beta);

			if (ax > bx) { Swap(ref ax, ref bx); SwapU(ref ac, ref bc); }
			if (ax == bx)
			{
				ushort dithered = ColorToVramDithered(ac, ax, y);
				if (semiTransparent)
					PlotPixelGpu(ax, y, dithered, false, true);
				else
					PlotPixelTrueColor(ax, y, dithered, ac);
			}
			else
			{
				int span = bx - ax;
				// Half-open right edge, see DrawTriangle for rationale. ax == bx
				// is already handled by the if-branch above.
				for (int x = ax; x < bx; x++)
				{
					float t = (float)(x - ax) / span;
					uint col = LerpColor24(ac, bc, t);
					ushort dithered = ColorToVramDithered(col, x, y);
					if (semiTransparent)
						PlotPixelGpu(x, y, dithered, false, true);
					else
						PlotPixelTrueColor(x, y, dithered, col);
				}
			}
		}
	}

	private void DrawTriangleTextured(
		int x0, int y0, int u0, int v0, uint c0,
		int x1, int y1, int u1, int v1, uint c1,
		int x2, int y2, int u2, int v2, uint c2,
		bool rawTexture, bool semiTransparent)
	{
		if (IsLargePrimitive(x0, y0, x1, y1, x2, y2)) return;

		if (y0 > y1) { Swap(ref x0, ref x1); Swap(ref y0, ref y1); Swap(ref u0, ref u1); Swap(ref v0, ref v1); SwapU(ref c0, ref c1); }
		if (y0 > y2) { Swap(ref x0, ref x2); Swap(ref y0, ref y2); Swap(ref u0, ref u2); Swap(ref v0, ref v2); SwapU(ref c0, ref c2); }
		if (y1 > y2) { Swap(ref x1, ref x2); Swap(ref y1, ref y2); Swap(ref u1, ref u2); Swap(ref v1, ref v2); SwapU(ref c1, ref c2); }

		int totalH = y2 - y0;
		if (totalH == 0) return;

		// We had three iterations to get this right:
		//
		// (1) Original: per-scanline edge interp + per-pixel scanline interp,
		//     two stacked `(int)` truncations. Produced visible 1-2 texel jumps
		//     at the top-right -> bottom-left diagonal of every textured quad
		//     (most visible on the BIOS SONY logo's slow zoom).
		//
		// (2) Float affine: single-pass `(int)` truncation per pixel. Fixed
		//     the SONY logo seam, but introduced "axis-aligned speckle", bright
		//     green dots on Crash's floor, pink dots in RE2 shadow, black dots
		//     in Driver 2 shadow. Root cause: `1.0f/det` is not exactly
		//     representable for most det values (e.g. `1/100 ~= 0.00999999978`),
		//     so dudx becomes 25.49999943 instead of 25.5. At integer-aligned
		//     pixel positions where uLine should land exactly on integer
		//     boundaries, accumulated error of ~5e-7 per step pushes it just
		//     below, `(int)25.99999943 = 25` instead of the intended 26.
		//     Sampling lands on the wrong texel.
		//
		// (3) Float + 0.5 pixel-center offset: shifted sampling to pixel centers
		//     (matches real PSX hardware), reduced visible artifact significantly
		//     but didn't eliminate it entirely, float precision still produced
		//     off-by-one at certain rotations.
		//
		// (4) FIXED-POINT (this version): Q24 (12 + 12 fractional bits) integer
		//     arithmetic. Half-integer gradients like 25.5 are
		//     EXACTLY representable as `25 * 2^24 + 2^23 = 427819008`. Adding
		//     `+0.5` (= `1 << (12 - 1)` = 2048 in Q12 = `2^23` in Q24)
		//     to the initial UV is also exact. Per-pixel `uLine_fixed += dudx`
		//     uses unsigned integer arithmetic, no precision loss, deterministic.
		//     Final texel index extracted as `(uLine_fixed >> 24) & 0xFF`, combines
		//     the integer-part shift with the texture-page wrap.

		long detL = (long)(x1 - x0) * (y2 - y1) - (long)(x2 - x1) * (y1 - y0);
		if (detL == 0) return;

		long duNumX = (long)(u1 - u0) * (y2 - y1) - (long)(u2 - u1) * (y1 - y0);
		long dvNumX = (long)(v1 - v0) * (y2 - y1) - (long)(v2 - v1) * (y1 - y0);
		long duNumY = (long)(x1 - x0) * (u2 - u1) - (long)(x2 - x1) * (u1 - u0);
		long dvNumY = (long)(x1 - x0) * (v2 - v1) - (long)(x2 - x1) * (v1 - v0);

		// dudx_fixed = (numerator * 2^12 / det) << 12  (Q24).
		// Using long for the intermediate to avoid s32 overflow when numerator
		// is large (up to ~255 * 1024 = 260K) and the shift pushes it past 31 bits.
		uint dudxF = (uint)((duNumX * (1L << 12) / detL) << 12);
		uint dvdxF = (uint)((dvNumX * (1L << 12) / detL) << 12);
		uint dudyF = (uint)((duNumY * (1L << 12) / detL) << 12);
		uint dvdyF = (uint)((dvNumY * (1L << 12) / detL) << 12);

		// COLOR GRADIENTS, same Q24 affine treatment as UV. Per-pixel R/G/B
		// computed via the affine determinant formula instead of per-scanline
		// endpoint lerp, eliminates the visible triangle seams that appear
		// when adjacent triangles within a Gouraud-shaded quad have different
		// per-edge color interpolation orientations. Without this, the per-
		// scanline lerp's "left/right edge color" depends on the rightFacing
		// orientation, and adjacent triangles within a quad can disagree on
		// the boundary pixel's color.
		int r0 = (int)(c0 & 0xFF), g0 = (int)((c0 >> 8) & 0xFF), b0 = (int)((c0 >> 16) & 0xFF);
		int r1 = (int)(c1 & 0xFF), g1 = (int)((c1 >> 8) & 0xFF), b1 = (int)((c1 >> 16) & 0xFF);
		int r2 = (int)(c2 & 0xFF), g2 = (int)((c2 >> 8) & 0xFF), b2 = (int)((c2 >> 16) & 0xFF);

		long drNumX = (long)(r1 - r0) * (y2 - y1) - (long)(r2 - r1) * (y1 - y0);
		long dgNumX = (long)(g1 - g0) * (y2 - y1) - (long)(g2 - g1) * (y1 - y0);
		long dbNumX = (long)(b1 - b0) * (y2 - y1) - (long)(b2 - b1) * (y1 - y0);
		long drNumY = (long)(x1 - x0) * (r2 - r1) - (long)(x2 - x1) * (r1 - r0);
		long dgNumY = (long)(x1 - x0) * (g2 - g1) - (long)(x2 - x1) * (g1 - g0);
		long dbNumY = (long)(x1 - x0) * (b2 - b1) - (long)(x2 - x1) * (b1 - b0);

		uint drdxF = (uint)((drNumX * (1L << 12) / detL) << 12);
		uint dgdxF = (uint)((dgNumX * (1L << 12) / detL) << 12);
		uint dbdxF = (uint)((dbNumX * (1L << 12) / detL) << 12);
		uint drdyF = (uint)((drNumY * (1L << 12) / detL) << 12);
		uint dgdyF = (uint)((dgNumY * (1L << 12) / detL) << 12);
		uint dbdyF = (uint)((dbNumY * (1L << 12) / detL) << 12);

		// Initial UV at sorted-v0 anchor, with +0.5 pixel-center offset baked in.
		// `(u0 << 12) + 2048` puts u0 + 0.5 into Q12, then `<< 12` more lifts to Q24.
		// Equivalent to `u0 << 24 + (1 << 23)`.
		uint uLine0 = (uint)(((u0 << 12) + (1 << (12 - 1))) << 12);
		uint vLine0 = (uint)(((v0 << 12) + (1 << (12 - 1))) << 12);

		// Initial R/G/B at sorted-v0 anchor, also with +0.5 offset.
		uint rLine0 = (uint)(((r0 << 12) + (1 << (12 - 1))) << 12);
		uint gLine0 = (uint)(((g0 << 12) + (1 << (12 - 1))) << 12);
		uint bLine0 = (uint)(((b0 << 12) + (1 << (12 - 1))) << 12);

		// FIXED-POINT Q32 EDGE WALKER
		// The Q32 formulation differs from our previous float-based ax/bx in:
		//   - Vertex X stored as `(x << 32) + (2^32 - 2^11)`, places vertex at
		//     "just below x+1" in Q32 so `>> 32` extracts pixel x via implicit floor
		//     of the right edge of the pixel column.
		//   - Edge step uses biased rounding (`+(dy-1)` for positive dx, `-(dy-1)`
		//     for negative). Implements the top-left fill rule by always rounding
		//     edges AWAY from the fill direction, guarantees adjacent quads'
		//     shared edges produce no gaps OR double-draws.
		//   - Scanline iteration is `< y2` (EXCLUSIVE on bottom). The bottom
		//     row is picked up by the adjacent quad below. This eliminates
		//     horizontal-seam double-draws between vertically-adjacent quads.
		long baseCoord = MakeFpXy(x0);
		long baseStep = MakeStepXy(x2 - x0, y2 - y0);
		long upperStep = (y1 == y0) ? 0L : MakeStepXy(x1 - x0, y1 - y0);
		long lowerStep = (y2 == y1) ? 0L : MakeStepXy(x2 - x1, y2 - y1);
		bool rightFacing = (y1 == y0) ? (x1 > x0) : (upperStep > baseStep);

		// Pre-stage edges per half. The "long" edge (v0->v2) spans the full
		// triangle. The "short" edges are v0->v1 (top half) and v1->v2 (bottom).
		// rightFacing tells us which side the long edge is on.
		long leftX_top, rightX_top, leftStep_top, rightStep_top;
		long leftX_bot, rightX_bot, leftStep_bot, rightStep_bot;
		long longCoordAtY1 = baseCoord + (long)(y1 - y0) * baseStep;

		if (rightFacing)
		{
			leftX_top  = baseCoord;       leftStep_top  = baseStep;
			rightX_top = MakeFpXy(x0);    rightStep_top = upperStep;
			leftX_bot  = longCoordAtY1;   leftStep_bot  = baseStep;
			rightX_bot = MakeFpXy(x1);    rightStep_bot = lowerStep;
		}
		else
		{
			leftX_top  = MakeFpXy(x0);    leftStep_top  = upperStep;
			rightX_top = baseCoord;       rightStep_top = baseStep;
			leftX_bot  = MakeFpXy(x1);    leftStep_bot  = lowerStep;
			rightX_bot = longCoordAtY1;   rightStep_bot = baseStep;
		}

		long lx, rx;

		// TOP HALF, y0 to y1 exclusive
		lx = leftX_top;
		rx = rightX_top;
		for (int y = y0; y < y1; y++)
		{
			int xStart = UnFpXy(lx);
			int xEnd = UnFpXy(rx);
			if (xEnd > xStart)
				DrawTexturedSpan(y, xStart, xEnd, x0, y0,
					dudxF, dvdxF, dudyF, dvdyF, uLine0, vLine0,
					drdxF, dgdxF, dbdxF, drdyF, dgdyF, dbdyF, rLine0, gLine0, bLine0,
					rawTexture, semiTransparent);
			lx += leftStep_top;
			rx += rightStep_top;
		}

		// BOTTOM HALF, y1 to y2 exclusive
		lx = leftX_bot;
		rx = rightX_bot;
		for (int y = y1; y < y2; y++)
		{
			int xStart = UnFpXy(lx);
			int xEnd = UnFpXy(rx);
			if (xEnd > xStart)
				DrawTexturedSpan(y, xStart, xEnd, x0, y0,
					dudxF, dvdxF, dudyF, dvdyF, uLine0, vLine0,
					drdxF, dgdxF, dbdxF, drdyF, dgdyF, dbdyF, rLine0, gLine0, bLine0,
					rawTexture, semiTransparent);
			lx += leftStep_bot;
			rx += rightStep_bot;
		}
	}

	// ---- Q32 fixed-point helpers for edge walking ----
	// (gpu_sw_rasterizer.inl:1463-1466)
	private const long FP_XY_OFFSET = (1L << 32) - (1L << 11);  // 4,294,965,248

	private static long MakeFpXy(int x) => ((long)x << 32) + FP_XY_OFFSET;
	private static int UnFpXy(long fp) => (int)((ulong)fp >> 32);
	private static long MakeStepXy(int dx, int dy)
	{
		if (dy == 0) return 0L;
		long bias = dx > 0 ? (long)(dy - 1) : (dx < 0 ? -(long)(dy - 1) : 0L);
		return (((long)dx << 32) + bias) / dy;
	}

	// Per-scanline draw helper for the Q32 edge walker. Both UV and RGB are
	// walked per-pixel in Q24 fixed-point, eliminates the per-scanline endpoint
	// lerp that depended on rightFacing-aware edge color ordering. Adjacent
	// triangles within a Gouraud-shaded quad now produce identical colors at
	// the shared diagonal pixel, no visible triangle seams.
	private void DrawTexturedSpan(int y, int xStart, int xEnd,
		int x0, int y0,
		uint dudxF, uint dvdxF, uint dudyF, uint dvdyF, uint uLine0, uint vLine0,
		uint drdxF, uint dgdxF, uint dbdxF, uint drdyF, uint dgdyF, uint dbdyF,
		uint rLine0, uint gLine0, uint bLine0,
		bool rawTexture, bool semiTransparent)
	{
		int span = xEnd - xStart;
		if (span <= 0) return;

		// UV at (xStart, y) in Q24.
		uint uLineF = uLine0 + dudxF * (uint)(xStart - x0) + dudyF * (uint)(y - y0);
		uint vLineF = vLine0 + dvdxF * (uint)(xStart - x0) + dvdyF * (uint)(y - y0);

		// R/G/B at (xStart, y) in Q24.
		uint rLineF = rLine0 + drdxF * (uint)(xStart - x0) + drdyF * (uint)(y - y0);
		uint gLineF = gLine0 + dgdxF * (uint)(xStart - x0) + dgdyF * (uint)(y - y0);
		uint bLineF = bLine0 + dbdxF * (uint)(xStart - x0) + dbdyF * (uint)(y - y0);

		for (int x = xStart; x < xEnd; x++)
		{
			int tu = (int)((uLineF >> 24) & 0xFF);
			int tv = (int)((vLineF >> 24) & 0xFF);
			uLineF += dudxF;
			vLineF += dvdxF;

			ushort texel = SampleTexture(tu, tv);
			if (texel != 0)
			{
				ushort color;
				if (rawTexture)
				{
					color = texel;
				}
				else
				{
					// Extract per-pixel R/G/B from Q24 walkers, repack to color24.
					uint rPix = (rLineF >> 24) & 0xFF;
					uint gPix = (gLineF >> 24) & 0xFF;
					uint bPix = (bLineF >> 24) & 0xFF;
					uint modColor = rPix | (gPix << 8) | (bPix << 16);
					color = ModulateTextureColor(texel, modColor);
				}
				PlotPixelGpu(x, y, color, true, semiTransparent);
			}

			rLineF += drdxF;
			gLineF += dgdxF;
			bLineF += dbdxF;
		}
	}

	// --- Texture sampling ---

	private ushort SampleTexture(int u, int v)
	{
		// Apply texture window
		if (_texWinMaskX != 0)
			u = (u & ~_texWinMaskX) | (_texWinOffX & _texWinMaskX);
		if (_texWinMaskY != 0)
			v = (v & ~_texWinMaskY) | (_texWinOffY & _texWinMaskY);

		// HW QUIRK, per real PSX (and nocash spec): U and V are 8-bit registers,
		// so values past 255 wrap to within the current 256x256 texture page.
		// Without this mask, sprites whose computed U exceeds 255 (e.g. RE2's
		// menu row, which submits a single 256-wide sprite at X=168 sampling
		// U=128..383) leak past the texpage boundary and read whatever is in
		// adjacent VRAM. In RE2 the "what's adjacent" happens to be the menu
		// background strip image at VRAM X=640, which then gets recoloured by
		// the hovered button's "highlighted" CLUT (0x7F8C), producing the
		// phantom inverted-U shape on the right of the screen that appears
		// only on EASY hover.
		u &= 0xFF;
		v &= 0xFF;

		switch (_texDepth)
		{
			case 0: return Sample4bpp(u, v);
			case 1: return Sample8bpp(u, v);
			default: return Sample15bpp(u, v);
		}
	}

	// VRAM SAMPLING at native PSX resolution (1024x512). `u`/`v` are texel
	// indices within the current texture page (0..255). `_texPageX/Y` and
	// `_clutX/Y` are already stored in native VRAM coords (from CmdDrawMode /
	// ApplyPolygonClut).
	private ushort Sample4bpp(int u, int v)
	{
		// 4bpp: 4 texels per 16-bit VRAM word, packed low-nibble-first
		int vramX = _texPageX + (u >> 2);
		int vramY = _texPageY + v;
		ushort data = Vram[(vramY & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (vramX & PsxConstants.VramWidthMask)];
		int nibble = (data >> ((u & 3) * 4)) & 0xF;
		return ReadClut(nibble);
	}

	private ushort Sample8bpp(int u, int v)
	{
		int vramX = _texPageX + (u >> 1);
		int vramY = _texPageY + v;
		ushort data = Vram[(vramY & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (vramX & PsxConstants.VramWidthMask)];
		int index = (u & 1) == 0 ? data & 0xFF : (data >> 8) & 0xFF;
		return ReadClut(index);
	}

	private ushort Sample15bpp(int u, int v)
	{
		int vramX = _texPageX + u;
		int vramY = _texPageY + v;
		return Vram[(vramY & PsxConstants.VramHeightMask) * PsxConstants.VramWidth + (vramX & PsxConstants.VramWidthMask)];
	}

	private ushort ReadClut(int index)
	{
		return Vram[_clutY * PsxConstants.VramWidth + ((_clutX + index) & PsxConstants.VramWidthMask)];
	}

	// --- Color utilities ---

	private static ushort LerpColor(ushort a, ushort b, float t)
	{
		int r = (int)((a & 0x1F) + t * ((b & 0x1F) - (a & 0x1F)));
		int g = (int)(((a >> 5) & 0x1F) + t * (((b >> 5) & 0x1F) - ((a >> 5) & 0x1F)));
		int bl = (int)(((a >> 10) & 0x1F) + t * (((b >> 10) & 0x1F) - ((a >> 10) & 0x1F)));
		return (ushort)(r | (g << 5) | (bl << 10));
	}

	private static uint LerpColor24(uint a, uint b, float t)
	{
		int r = (int)((a & 0xFF) + t * (((int)(b & 0xFF)) - (int)(a & 0xFF)));
		int g = (int)(((a >> 8) & 0xFF) + t * (((int)((b >> 8) & 0xFF)) - (int)((a >> 8) & 0xFF)));
		int bl = (int)(((a >> 16) & 0xFF) + t * (((int)((b >> 16) & 0xFF)) - (int)((a >> 16) & 0xFF)));
		return (uint)(Math.Clamp(r, 0, 255) | (Math.Clamp(g, 0, 255) << 8) | (Math.Clamp(bl, 0, 255) << 16));
	}

	private static int SignExtend11(int v)
	{
		if ((v & 0x400) != 0) v |= unchecked((int)0xFFFFF800);
		return v;
	}

	// Diagnostic helpers: format XY/WH words for logging
	private static string XY(uint word)
	{
		int x = (int)(word & 0xFFFF); if (x > 0x7FFF) x -= 0x10000;
		int y = (int)(word >> 16); if (y > 0x7FFF) y -= 0x10000;
		return $"({x},{y})";
	}
	private static string SZ(uint word) =>
		$"{(int)(word & 0xFFFF)}x{(int)(word >> 16)}";

	private static void Swap<T>(ref T a, ref T b) { T tmp = a; a = b; b = tmp; }
	private static void SwapU(ref uint a, ref uint b) { uint tmp = a; a = b; b = tmp; }
}
