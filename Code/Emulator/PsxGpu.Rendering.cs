using System.Runtime.InteropServices;
using Sandbox.Rendering;

namespace PSXEmu;

public partial class PsxGpu
{
	public PsxDisplayFilter DisplayFilter { get; private set; } = PsxDisplayFilter.Nearest;
	public float ScanlineStrength { get; private set; }
	public float ScanlineSharpness { get; private set; } = 3f;
	// Scanlines per NATIVE line (the shader locks the beam to native rows, not the
	// upscaled output): 1 = one scanline per native line (authentic), higher = denser.
	public float ScanlineFrequency { get; private set; } = 1f;
	public float PhosphorMaskStrength { get; private set; }
	public float CrtColorBoost { get; private set; } = 1.08f;
	public Texture OutputTexture { get; private set; }
	public CommandList RenderCommandList { get; private set; }
	public bool GpuReady { get; private set; }

	// --- GPU rasterizer ---
	// When true, polygon rasterization happens on the GPU via psx_raster.shader
	// instead of the CPU software rasterizer. Required for internal-resolution
	// upscaling at decent perf. See GPURASTERIZER_PROOF_OF_CONCEPT.md.
	public bool GpuRasterizer { get; private set; }

	// Internal render-resolution multiplier for the GPU rasterizer.
	// 1 = native (1024x512 VRAM), 2 = 2x (2048x1024), 4 = 4x, 8 = 8x, 16 = 16x.
	// CPU rasterizer always runs at native res, only VramTex scales with this.
	public int GpuRasterScale { get; private set; } = 1;

	// The "live" VRAM as a GPU render target. Polygon draws read CopyTex and
	// write here. At scale=S, the texture is (1024*S)x(512*S) RGBA8888.
	// Allocated lazily in InitGpu only when GpuRasterizer is enabled, when
	// disabled, the CPU path's ushort[] Vram remains the source of truth.
	public Texture VramTex { get; private set; }

	// Snapshot of VramTex taken before each batch flush, sampled by the
	// fragment shader (texturing + semi-transparency background reads).
	// Most GPU APIs disallow sampling from a texture currently bound as a
	// render target; this ping-pong avoids that constraint.
	public Texture VramCopyTex { get; private set; }

	// Native-resolution mirror of the CPU rasterizer's Vram[] array, used
	// by the fragment shader as the source for texture sampling (textures
	// are stored in VRAM by the game and must be readable by the rasterizer).
	// Sized 1024x512 RGBA8888_LINEAR, independent of GpuRasterScale because
	// PSX textures are always native res. We pack each ushort VRAM word as
	// (R=loByte, G=hiByte, B=0, A=0xFF) so the shader can reconstruct the
	// raw 16-bit value for indexed (4bpp/8bpp) formats.
	public Texture VramSourceTex { get; private set; }
	private Color32[] _vramSourceBuf;

	// Compiled psx_raster.shader, loaded once at InitGpu time.
	private Material _rasterMaterial;

	// Cached wrapper that pairs VramTex (color) with no depth target.
	// SetRenderTarget(RenderTarget) accepts this directly; recreating it per
	// frame is harmless but it's cheap to cache.
	private RenderTarget _vramRenderTarget;

	// --- Per-frame vertex batch ---
	// Worker thread appends to _vertexBatch in DrawMonoRect / DrawTriMono /
	// DrawTriGouraud3 when GpuRasterizer is on. SnapshotVertexBatch() swaps
	// it with _vertexBatchSnapshot under _snapshotLock; the main thread then
	// uploads & draws the snapshot via FlushGpuRasterBatch().
	private List<Vertex> _vertexBatch = new(8192);
	private List<Vertex> _vertexBatchSnapshot = new(8192);

	// --- Draw groups for hardware-blended semi-transparency ---
	//
	// PSX rendering is strictly order-dependent (no depth buffer; later draws
	// paint over earlier ones), and the GPU's RenderState, including blend
	// mode, is pipeline-state-level, not per-primitive. To preserve order
	// while switching blend modes, we split the vertex batch into a series of
	// "draw groups", each a contiguous run of verts that share the same blend
	// mode. Each group becomes one Draw call with its matching D_PSX_BLEND
	// combo set on the CommandList.
	//
	// D_PSX_BLEND values:
	//   0 = opaque (blend disabled)
	//   1 = PSX mode 0: B/2 + F/2
	//   2 = PSX mode 1: B + F
	//   3 = PSX mode 2: B - F
	//   4 = PSX mode 3: B + F/4
	private readonly struct DrawGroup
	{
		public readonly int VertStart;
		public readonly int VertCount;
		public readonly int BlendCombo;  // D_PSX_BLEND value (0..4)
		public DrawGroup( int vertStart, int vertCount, int blendCombo )
		{
			VertStart = vertStart;
			VertCount = vertCount;
			BlendCombo = blendCombo;
		}
	}

	private List<DrawGroup> _drawGroups = new( 16 );
	private List<DrawGroup> _drawGroupsSnapshot = new( 16 );
	private int _currentGroupStart;
	private int _currentGroupCombo = -1;  // -1 = no group open yet (next push opens one)

	/// <summary>
	/// Map PSX semi-trans state to the D_PSX_BLEND combo value.
	/// </summary>
	private static int PsxBlendCombo( bool semiTrans, int blendMode )
	{
		if ( !semiTrans ) return 0;
		// PSX blend mode 0..3 -> D_PSX_BLEND 1..4
		return ( blendMode & 3 ) + 1;
	}

	/// <summary>
	/// Open or extend a draw group for verts about to be pushed. Must be
	/// called BEFORE adding verts to _vertexBatch. If the blend mode differs
	/// from the currently-open group, the open group is closed and a new one
	/// is started at the current vert position, preserving PSX submission
	/// order across the GPU draw splits.
	/// </summary>
	private void EnsureDrawGroup( int blendCombo )
	{
		if ( _currentGroupCombo == blendCombo ) return;
		if ( _currentGroupCombo >= 0 )
		{
			int groupVerts = _vertexBatch.Count - _currentGroupStart;
			if ( groupVerts > 0 )
				_drawGroups.Add( new DrawGroup( _currentGroupStart, groupVerts, _currentGroupCombo ) );
		}
		_currentGroupStart = _vertexBatch.Count;
		_currentGroupCombo = blendCombo;
	}

	// Persistent GPU vertex buffer. Resized (re-allocated) when a frame
	// produces more verts than capacity. Keeps allocation noise out of the
	// per-frame path once warmed up.
	private GpuBuffer<Vertex> _vertexGpuBuf;
	private int _vertexGpuBufCapacity;
	private const int InitialVertexGpuBufCapacity = 8192;

	// Diagnostic counters (worker increments -> snapshot under lock -> main reads).
	private int _diagGpuTrisThisFrame;
	private int _diagGpuRectsThisFrame;
	private int _diagGpuTrisLastFrame;
	private int _diagGpuRectsLastFrame;
	private int _diagGpuVertsLastFrame;
	private long _diagGpuLastLogTick;
	private int _diagGpuTrisLogAccum;
	private int _diagGpuRectsLogAccum;
	private int _diagGpuFramesSinceLog;

	// Max possible PSX display (640x480 interlaced) at NATIVE resolution.
	// GPU-path output dims are scaled by GpuRasterScale inside InitGpu so the
	// upscaled rasterization actually survives to the screen (not downsampled
	// away by a 640x480 OutputTexture).
	private const int MaxDisplayW = 640;
	private const int MaxDisplayH = 480;
	private const int MaxDisplayPixels = MaxDisplayW * MaxDisplayH;

	private GpuBuffer<uint> _gpuVramBuf;
	private ComputeShader _csDisplay;
	private int _scaledW, _scaledH;

	// CPU-side display area buffer (RGBA8888), sized for the maximum display.
	private uint[] _displayBuf;
	private int _snapshotDisplayStartX;
	private int _snapshotDisplayStartY;
	private int _snapshotDisplayW;
	private int _snapshotDisplayH;
	private bool _snapshotDisplay24Bit;

	public void InitGpu(
		PsxDisplayFilter displayFilter = PsxDisplayFilter.Nearest,
		float scanlineStrength = 0f,
		float scanlineSharpness = 3f,
		float scanlineFrequency = 2f,
		float phosphorMaskStrength = 0f,
		float crtColorBoost = 1.08f,
		bool gpuRasterizer = false,
		int gpuRasterScale = 1)
	{
		ApplyDisplaySettings(displayFilter, scanlineStrength, scanlineSharpness, scanlineFrequency, phosphorMaskStrength, crtColorBoost);
		
		GpuRasterizer = gpuRasterizer;
		GpuRasterScale = int.Clamp(gpuRasterScale, 1, 9); // 1 = native, 9 = 2160p (4K)

		// OutputTexture sizing. CPU path keeps the historical MaxDisplayWxMaxDisplayH.
		// GPU path grows OutputTexture with GpuRasterScale so the high-resolution
		// rasterization in VramTex actually reaches the screen, otherwise the
		// display compute would point-sample at native pixel stride and 7 of every
		// 8 upscaled pixels would be discarded at scale=8. At scale=8 this is
		// 5120x3840 RGBA8888 = ~75 MB.
		int outputScale = GpuRasterizer ? GpuRasterScale : 1;
		_scaledW = MaxDisplayW * outputScale;
		_scaledH = MaxDisplayH * outputScale;

		// CPU display buffer & GPU upload buffer. These are only used on the
		// CPU path (GpuRasterizer == false, or 24-bit FMV); sized for the
		// maximum display region (640x480 native).
		_displayBuf = new uint[MaxDisplayPixels];
		_gpuVramBuf = new GpuBuffer<uint>(MaxDisplayPixels);

		OutputTexture = Texture.CreateRenderTarget()
			.WithSize(_scaledW, _scaledH)
			.WithFormat(ImageFormat.RGBA8888)
			.WithUAVBinding()
			.WithDynamicUsage()
			.Create();

		_csDisplay = new ComputeShader("psx_display.shader");
		RenderCommandList = new CommandList("PSX Emu");

		// GPU rasterizer infrastructure, only allocated when enabled so the
		// disabled path costs nothing in VRAM. At scale=16 these textures are
		// 16384x8192 RGBA8888 = 512 MB each (1 GB for the pair).
		if (GpuRasterizer)
			InitGpuRasterizer();

		GpuReady = true;
	}

	private void InitGpuRasterizer()
	{
		int vramW = PsxConstants.VramWidth * GpuRasterScale;
		int vramH = PsxConstants.VramHeight * GpuRasterScale;

		// Live VRAM render target. RGBA8888_LINEAR, NOT sRGB. The plain
		// `RGBA8888` format applies linear->sRGB encoding on render-target writes
		// (and sRGB->linear on sample reads), which washes out PSX colors because
		// the engine treats our raw vertex bytes as already-linear. The CPU
		// rasterizer path bypasses this entirely by going through a structured
		// buffer of raw uints, we mirror that here with the linear format.
		VramTex = Texture.CreateRenderTarget()
			.WithSize(vramW, vramH)
			.WithFormat(ImageFormat.RGBA8888_LINEAR)
			.WithDynamicUsage()
			.Create();

		// Sample-able snapshot. Same size and format so Graphics.CopyTexture
		// works without an intermediate blit.
		VramCopyTex = Texture.CreateRenderTarget()
			.WithSize(vramW, vramH)
			.WithFormat(ImageFormat.RGBA8888_LINEAR)
			.WithDynamicUsage()
			.Create();

		// CPU-uploaded VRAM mirror at native size (1024x512). Used by the
		// fragment shader to sample textures. Doesn't need to be a render
		// target but using CreateRenderTarget+WithDynamicUsage is the path
		// that supports per-frame Texture.Update() calls in s&box.
		VramSourceTex = Texture.CreateRenderTarget()
			.WithSize(PsxConstants.VramWidth, PsxConstants.VramHeight)
			.WithFormat(ImageFormat.RGBA8888_LINEAR)
			.WithDynamicUsage()
			.Create();
		_vramSourceBuf = new Color32[PsxConstants.VramWidth * PsxConstants.VramHeight];

		_rasterMaterial = Material.FromShader("psx_raster");
		if (_rasterMaterial == null)
			PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Error, "[GPU] Material.FromShader returned NULL for 'psx_raster', no draws will produce output!");
		else
			PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info, $"[GPU] Raster material loaded: {_rasterMaterial}");

		// Cache the RenderTarget wrappers. RenderTarget.From throws if the
		// texture wasn't created as a render target, guarded by InitGpuRasterizer's
		// CreateRenderTarget() chain above.
		_vramRenderTarget = RenderTarget.From(VramTex);

		// Persistent vertex GPU buffer for the rasterizer batches. Grows on
		// demand inside FlushGpuRasterBatch() if a frame submits more verts.
		_vertexGpuBufCapacity = InitialVertexGpuBufCapacity;
		_vertexGpuBuf = new GpuBuffer<Vertex>(_vertexGpuBufCapacity, GpuBuffer.UsageFlags.Vertex, "PSX.RasterVerts");

		_vertexBatch.Clear();
		_vertexBatchSnapshot.Clear();
		_drawGroups.Clear();
		_drawGroupsSnapshot.Clear();
		_currentGroupStart = 0;
		_currentGroupCombo = -1;
		_diagGpuTrisThisFrame = _diagGpuRectsThisFrame = 0;
		_diagGpuTrisLastFrame = _diagGpuRectsLastFrame = _diagGpuVertsLastFrame = 0;
		_diagGpuLastLogTick = PsxPerfMonitor.Stamp();
		_diagGpuTrisLogAccum = _diagGpuRectsLogAccum = _diagGpuFramesSinceLog = 0;

		PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info,
			$"[GPU] Rasterizer enabled: scale={GpuRasterScale} vram={vramW}x{vramH}");
	}

	public void ApplyDisplaySettings(
		PsxDisplayFilter displayFilter,
		float scanlineStrength,
		float scanlineSharpness,
		float scanlineFrequency,
		float phosphorMaskStrength,
		float crtColorBoost)
	{
		DisplayFilter = displayFilter;
		ScanlineStrength = Math.Clamp(scanlineStrength, 0f, 1f);
		ScanlineSharpness = Math.Clamp(scanlineSharpness, 0.5f, 8f);
		ScanlineFrequency = Math.Clamp(scanlineFrequency, 0f, 8f);
		PhosphorMaskStrength = Math.Clamp(phosphorMaskStrength, 0f, 1f);
		CrtColorBoost = Math.Clamp(crtColorBoost, 1f, 2f);
	}

	public void DisposeGpu()
	{
		GpuReady = false;
		_gpuVramBuf?.Dispose();
		_gpuVramBuf = null;
		OutputTexture?.Dispose();
		OutputTexture = null;
		RenderCommandList = null;

		// GPU rasterizer resources (only allocated when GpuRasterizer == true).
		VramTex?.Dispose();
		VramTex = null;
		VramCopyTex?.Dispose();
		VramCopyTex = null;
		VramSourceTex?.Dispose();
		VramSourceTex = null;
		_vramSourceBuf = null;
		_vertexGpuBuf?.Dispose();
		_vertexGpuBuf = null;
		_vertexGpuBufCapacity = 0;
		_vertexBatch.Clear();
		_vertexBatchSnapshot.Clear();
		_drawGroups.Clear();
		_drawGroupsSnapshot.Clear();
		_currentGroupCombo = -1;
		_rasterMaterial = null;
		_vramRenderTarget = null;
		GpuRasterizer = false;
	}

	/// <summary>
	/// Called from the main thread: convert the VRAM display area to RGBA8888,
	/// upload to GPU buffer, build the command list that runs the display shader.
	/// </summary>
	public void UploadAndBuildCommandList()
	{
		if (!GpuReady) return;
		long uploadStart = PsxPerfMonitor.Stamp();

		int dw, dh, sx, sy;
		int nativeSx = 0, nativeSy = 0;  // Native (pre-scale) display origin, for VramSourceTex fallback in display shader.
		int displayScale = 1;            // Same value used as `s` below, captured for shader bind.
		bool display24Bit;

		// Build the render command list.
		RenderCommandList.Reset();

		long convertStart = PsxPerfMonitor.Stamp();
		// Convert VRAM snapshot to RGBA8888 on the CPU side.
		// Uses the true-color overlay for smooth gradients on shaded primitives.
		//
		// SCALE NOTE: GP1 display registers are at NATIVE PSX coords (game thinks
		// in 320x240 etc.) and the CPU's Vram[] is also native. Only the GPU
		// rasterizer's VramTex is upscaled, when it's active we multiply the
		// display window by GpuRasterScale to sample the right region.
		lock (_snapshotLock)
		{
			dw = Math.Clamp(_snapshotDisplayW > 0 ? _snapshotDisplayW : PsxConstants.ScreenWidth, 1, MaxDisplayW);
			dh = Math.Clamp(_snapshotDisplayH > 0 ? _snapshotDisplayH : PsxConstants.ScreenHeight, 1, MaxDisplayH);
			sx = Math.Clamp(_snapshotDisplayStartX, 0, PsxConstants.VramWidth - 1);
			sy = Math.Clamp(_snapshotDisplayStartY, 0, PsxConstants.VramHeight - 1);
			display24Bit = _snapshotDisplay24Bit;

			// Pick the scale for the display window coords. The CPU path stays
			// at native resolution; the GPU path stores VramTex at GpuRasterScale
			// (1, 2, 4, 8, or 16). DisplayStartX/Y and DisplayW/H must match
			// the target texture's pixel coords so the shader samples the right
			// region of VramTex / VramBuf.
			//
			// 24-bit display mode (FMV) always uses the CPU path, its packed
			// pixel layout isn't representable in VramTex.
			bool useCpuDisplayPath = !GpuRasterizer || display24Bit;
			int s = useCpuDisplayPath ? 1 : GpuRasterScale;
			int dwS = dw * s;
			int dhS = dh * s;
			int sxS = sx * s;
			int syS = sy * s;

			// Capture native origin & scale for the display shader's
			// VramSourceTex-fallback path (the rest of the function uses the
			// upscaled values via the dw/dh/sx/sy variables).
			nativeSx = sx;
			nativeSy = sy;
			displayScale = s;

			// When the GPU rasterizer is on, the display shader samples VramTex
			// directly, no need to spend CPU cycles converting _vramSnapshot ->
			// _displayBuf. EXCEPTION: 24-bit display (FMV) uses a packed pixel
			// layout that VramTex / VramSourceTex don't handle, fall through
			// to the CPU 24-bit unpacker in this branch so FMV doesn't garble.
			if (!GpuRasterizer || display24Bit)
			{
				for (int row = 0; row < dhS; row++)
				{
					int vy = (syS + row) & PsxConstants.VramHeightMask;
					int rowOffset = row * dwS;
					int vramRowOffset = vy * PsxConstants.VramWidth;
					if (display24Bit)
					{
						// 24-bit display: packed RGB across VRAM cells. Sampled at
						// native resolution (one source pixel per native column),
						// then replicated as scalexscale blocks to fill the upscaled
						// display buffer. See Read24BitDisplayPixel for the layout.
						int nativeRow = row / s;
						int nativeRowBaseInBuf = nativeRow;
						for (int col = 0; col < dwS; col++)
						{
							int nativeCol = col / s;
							_displayBuf[rowOffset + col] = Read24BitDisplayPixel(sx, sy, nativeCol, nativeRow);
						}
					}
					else
					{
						for (int col = 0; col < dwS; col++)
						{
							int vx = (sxS + col) & PsxConstants.VramWidthMask;
							_displayBuf[rowOffset + col] = _trueColorSnapshot[vramRowOffset + vx];
						}
					}
				}
			}

			// Upload the CPU-side VRAM snapshot to VramSourceTex so the fragment
			// shader can sample textures from it. Done inside the lock to keep
			// _vramSnapshot stable for the read. Packs each ushort RGB555+mask
			// word as (R=loByte, G=hiByte, B=0, A=0xFF) so the shader can
			// reconstruct the raw 16-bit value for indexed (4bpp/8bpp) formats.
			if (GpuRasterizer && _vramSourceBuf != null)
			{
				int vramPixels = PsxConstants.VramWidth * PsxConstants.VramHeight;
				for (int i = 0; i < vramPixels; i++)
				{
					ushort v = _vramSnapshot[i];
					_vramSourceBuf[i] = new Color32((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), 0, 255);
				}
			}

			// Update dw/dh to upscaled values for the shader pipeline below.
			dw = dwS;
			dh = dhS;
			sx = sxS;
			sy = syS;
		}
		_psx.Perf.AddTicks(PsxPerfSection.GpuUploadConvert, PsxPerfMonitor.Stamp() - convertStart);

		// Push the VRAM mirror to VramSourceTex (outside the snapshot lock,
		// Texture.Update may stall on GPU sync, but no shared state is touched).
		if (GpuRasterizer && _vramSourceBuf != null && VramSourceTex != null)
		{
			VramSourceTex.Update(_vramSourceBuf, 0, 0, PsxConstants.VramWidth, PsxConstants.VramHeight);
		}

		// GPU rasterizer pass: queue the polygon draws into the command list BEFORE
		// the display dispatch so the compute shader (which samples VramTex when
		// UseVramTex == 1) sees the freshly-rasterized pixels. No-op when off.
		FlushGpuRasterBatch();

		// PS1 display modes use non-square pixels. For example, 640x240 is still
		// intended to fill a 4:3 TV image, not a 1:1 640x240 strip.
		const float targetAspect = 4f / 3f;
		int drawW = _scaledW;
		int drawH = Math.Max(1, (int)MathF.Round(drawW / targetAspect));
		if (drawH > _scaledH)
		{
			drawH = _scaledH;
			drawW = Math.Max(1, (int)MathF.Round(drawH * targetAspect));
		}

		int offsetX = Math.Max(0, (_scaledW - drawW) / 2);
		int offsetY = Math.Max(0, (_scaledH - drawH) / 2);
		float scaleX = drawW / (float)dw;
		float scaleY = drawH / (float)dh;

		// Upload display pixels to the GPU buffer. Required when the display
		// compute shader will read VramBuf, i.e., either CPU rasterizer is
		// active, OR we're in 24-bit display mode (FMV) where VramTex sampling
		// doesn't work because the data is packed across cells.
		int pixelCount = dw * dh;
		if (!GpuRasterizer || display24Bit)
		{
			long cpuToGpuStart = PsxPerfMonitor.Stamp();
			_gpuVramBuf.SetData(_displayBuf.AsSpan(0, pixelCount));
			_psx.Perf.AddTicks(PsxPerfSection.GpuUploadCpuToGpu, PsxPerfMonitor.Stamp() - cpuToGpuStart);
		}

		RenderCommandList.Attributes.Set("VramBuf", _gpuVramBuf);
		RenderCommandList.Attributes.Set("OutputTex", OutputTexture);
		RenderCommandList.Attributes.Set("DisplayW", dw);
		RenderCommandList.Attributes.Set("DisplayH", dh);
		RenderCommandList.Attributes.Set("DisplayStartX", sx);
		RenderCommandList.Attributes.Set("DisplayStartY", sy);
		RenderCommandList.Attributes.Set("DrawW", drawW);
		RenderCommandList.Attributes.Set("DrawH", drawH);
		RenderCommandList.Attributes.Set("ScaleX", scaleX);
		RenderCommandList.Attributes.Set("ScaleY", scaleY);
		RenderCommandList.Attributes.Set("OffsetX", offsetX);
		RenderCommandList.Attributes.Set("OffsetY", offsetY);
		RenderCommandList.Attributes.Set("DisplayFilter", (int)DisplayFilter);
		RenderCommandList.Attributes.Set("ScanlineStrength", ScanlineStrength);
		RenderCommandList.Attributes.Set("ScanlineSharpness", ScanlineSharpness);
		RenderCommandList.Attributes.Set("ScanlineFrequency", ScanlineFrequency);
		RenderCommandList.Attributes.Set("PhosphorMaskStrength", PhosphorMaskStrength);
		RenderCommandList.Attributes.Set("CrtColorBoost", CrtColorBoost);
		// Render-scale of the display window, needed by the CRT shader to size the
		// scanlines / phosphor mask in NATIVE pixels (so they look identical at any
		// render scale). Set on BOTH paths here (CPU path uses displayScale = 1); the
		// GPU branch below also re-sets it for its VramSourceTex fallback.
		RenderCommandList.Attributes.Set("VramRasterScale", displayScale);

		// GPU rasterizer hand-off to the display compute shader. When on AND not
		// in 24-bit display mode, the shader reads pixels straight from VramTex
		// at (DisplayStartX + x, DisplayStartY + y), sx/sy are already in
		// (upscaled) VRAM coords. VramSourceTex + NativeDisplayStartX/Y +
		// VramRasterScale support the fallback path the shader uses where
		// VramTex.a == 0 (CPU-only content).
		//
		// 24-bit display mode (FMV) ALWAYS uses the CPU path, VramTex stores
		// RGBA pixels but 24-bit FMV data is packed 3-bytes-per-pixel across
		// VRAM cells, which neither VramTex nor VramSourceTex can decode. The
		// existing Read24BitDisplayPixel helper handles it on CPU.
		if (GpuRasterizer && !display24Bit)
		{
			RenderCommandList.Attributes.Set("VramTex", VramTex);
			RenderCommandList.Attributes.Set("VramSourceTex", VramSourceTex);
			RenderCommandList.Attributes.Set("UseVramTex", 1);
			RenderCommandList.Attributes.Set("NativeDisplayStartX", nativeSx);
			RenderCommandList.Attributes.Set("NativeDisplayStartY", nativeSy);
			RenderCommandList.Attributes.Set("VramRasterScale", displayScale);
		}
		else
		{
			RenderCommandList.Attributes.Set("UseVramTex", 0);
		}

		long displayDispatchStart = PsxPerfMonitor.Stamp();
		RenderCommandList.DispatchCompute(_csDisplay, _scaledW, _scaledH, 1);
		_psx.Perf.AddTicks(PsxPerfSection.GpuUploadDisplayDispatch, PsxPerfMonitor.Stamp() - displayDispatchStart);
		_psx.Perf.AddTicks(PsxPerfSection.GpuUploadTotal, PsxPerfMonitor.Stamp() - uploadStart);

		MaybeLogGpuRasterizerStats();
	}

	private void MaybeLogGpuRasterizerStats()
	{
		if (!GpuRasterizer) return;

		_diagGpuTrisLogAccum += _diagGpuTrisLastFrame;
		_diagGpuRectsLogAccum += _diagGpuRectsLastFrame;
		_diagGpuFramesSinceLog++;

		long now = PsxPerfMonitor.Stamp();
		double elapsed = (now - _diagGpuLastLogTick) / (double)System.Diagnostics.Stopwatch.Frequency;
		if (elapsed < 1.0) return;

		PsxLog.Write(PsxLogCategory.IO, PsxLogLevel.Info,
			$"[GPU] Raster: {_diagGpuTrisLogAccum} tris + {_diagGpuRectsLogAccum} rects across {_diagGpuFramesSinceLog} frames in {elapsed:0.00}s (verts/frame last={_diagGpuVertsLastFrame})");
		_diagGpuLastLogTick = now;
		_diagGpuTrisLogAccum = 0;
		_diagGpuRectsLogAccum = 0;
		_diagGpuFramesSinceLog = 0;
	}

	// --- Vertex-batch builders (called on worker thread from CPU rasterizer entry points) ---
	//
	// Coordinates arrive in NATIVE PSX VRAM space (post-DrawOffset, 0..1024 / 0..512).
	// The vertex shader maps that range straight to clip space; the GPU's viewport
	// (set by SetRenderTarget on VramTex) is what scales up at GpuRasterScale > 1.
	// Flags bits [0:1] = 3 means "untextured", so the fragment shader uses the
	// interpolated vertex color directly.

	/// <summary>Append an opaque solid-color quad as two triangles (6 verts).</summary>
	internal void GpuPushQuad(int x, int y, int w, int h, uint color24) =>
		GpuPushQuadCore(x, y, w, h, color24, GpuClip.DrawArea, false, 0);

	/// <summary>Same as GpuPushQuad but bypasses DrawArea clipping, for Fill VRAM (GP0 0x02).</summary>
	internal void GpuPushQuadUnclipped(int x, int y, int w, int h, uint color24) =>
		GpuPushQuadCore(x, y, w, h, color24, GpuClip.Unclipped, false, 0);

	/// <summary>Semi-transparent solid-color quad.</summary>
	internal void GpuPushQuadSemiTrans(int x, int y, int w, int h, uint color24, int blendMode) =>
		GpuPushQuadCore(x, y, w, h, color24, GpuClip.DrawArea, true, blendMode);

	private void GpuPushQuadCore(int x, int y, int w, int h, uint color24, GpuClip clip, bool semiTrans, int blendMode)
	{
		if (!GpuRasterizer) return;
		EnsureDrawGroup( PsxBlendCombo( semiTrans, blendMode ) );
		int x1 = x + w;
		int y1 = y + h;
		Vertex v00 = MakeRasterVertex(x, y, color24, clip, semiTrans, blendMode);
		Vertex v10 = MakeRasterVertex(x1, y, color24, clip, semiTrans, blendMode);
		Vertex v01 = MakeRasterVertex(x, y1, color24, clip, semiTrans, blendMode);
		Vertex v11 = MakeRasterVertex(x1, y1, color24, clip, semiTrans, blendMode);
		// Tri 1: (00, 10, 11)
		_vertexBatch.Add(v00);
		_vertexBatch.Add(v10);
		_vertexBatch.Add(v11);
		// Tri 2: (00, 11, 01)
		_vertexBatch.Add(v00);
		_vertexBatch.Add(v11);
		_vertexBatch.Add(v01);
		_diagGpuRectsThisFrame++;
	}

	/// <summary>
	/// Append a transparent (alpha=0) untextured quad covering (x..x+w, y..y+h).
	/// The fragment shader writes alpha=0 for these pixels, causing the display
	/// compute shader's UseVramTex path to fall back to the native VramSourceTex
	/// at the same pixel, which is where CPU-only writes (VRAM uploads / DMA /
	/// semi-trans polys we don't yet handle) end up. Without this, accumulated
	/// alpha=1 from previous frames in VramTex would block the fallback.
	/// Unclipped, used by Fill VRAM / CPU->VRAM upload which bypass DrawArea.
	/// </summary>
	internal void GpuPushClearQuad(int x, int y, int w, int h)
	{
		if (!GpuRasterizer) return;
		// Clear quads always go through the opaque path (D_PSX_BLEND=0) so the
		// PS writes (0,0,0,0) verbatim to VramTex, semi-trans modes would
		// blend the (0,0,0,0) into the existing pixel, which is wrong for an
		// "invalidate this region" signal.
		EnsureDrawGroup( 0 );
		int x1 = x + w;
		int y1 = y + h;
		// Untextured (flags=3), alpha=0 so the PS writes (0,0,0,0).
		var clear = new Color32(0, 0, 0, 0);
		var tc0 = Vector4.Zero;
		var tc1 = new Vector4(0, 0, 3, 0);
		var da = MakeDrawAreaTangent(GpuClip.Unclipped);
		Vertex v00 = new Vertex { Position = new Vector3(x, y, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da };
		Vertex v10 = new Vertex { Position = new Vector3(x1, y, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da };
		Vertex v01 = new Vertex { Position = new Vector3(x, y1, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da };
		Vertex v11 = new Vertex { Position = new Vector3(x1, y1, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da };
		_vertexBatch.Add(v00); _vertexBatch.Add(v10); _vertexBatch.Add(v11);
		_vertexBatch.Add(v00); _vertexBatch.Add(v11); _vertexBatch.Add(v01);
		_diagGpuRectsThisFrame++;
	}

	/// <summary>
	/// Append a transparent (alpha=0) untextured triangle. Used to mark the
	/// pixel coverage of semi-transparent polygons we don't yet blend on GPU,
	/// the display compute then falls back to VramSourceTex at those pixels,
	/// showing the CPU rasterizer's (native-res) blended output.
	/// </summary>
	internal void GpuPushClearTri(int x0, int y0, int x1, int y1, int x2, int y2)
	{
		if (!GpuRasterizer) return;
		// See GpuPushClearQuad: clear tris go through the opaque path.
		EnsureDrawGroup( 0 );
		var clear = new Color32(0, 0, 0, 0);
		var tc0 = Vector4.Zero;
		var tc1 = new Vector4(0, 0, 3, 0);
		var da = MakeDrawAreaTangent(GpuClip.DrawArea);
		_vertexBatch.Add(new Vertex { Position = new Vector3(x0, y0, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da });
		_vertexBatch.Add(new Vertex { Position = new Vector3(x1, y1, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da });
		_vertexBatch.Add(new Vertex { Position = new Vector3(x2, y2, 0), Color = clear, TexCoord0 = tc0, TexCoord1 = tc1, Tangent = da });
		_diagGpuTrisThisFrame++;
	}

	/// <summary>Append a flat-color triangle (3 verts, all same color) to the current batch.</summary>
	internal void GpuPushTri(int x0, int y0, int x1, int y1, int x2, int y2, uint color24, bool semiTrans = false, int blendMode = 0)
	{
		if (!GpuRasterizer) return;
		EnsureDrawGroup( PsxBlendCombo( semiTrans, blendMode ) );
		_vertexBatch.Add(MakeRasterVertex(x0, y0, color24, GpuClip.DrawArea, semiTrans, blendMode));
		_vertexBatch.Add(MakeRasterVertex(x1, y1, color24, GpuClip.DrawArea, semiTrans, blendMode));
		_vertexBatch.Add(MakeRasterVertex(x2, y2, color24, GpuClip.DrawArea, semiTrans, blendMode));
		_diagGpuTrisThisFrame++;
	}

	/// <summary>Append a Gouraud triangle (3 verts, per-vertex colors) to the current batch.</summary>
	internal void GpuPushTriGouraud(int x0, int y0, uint c0, int x1, int y1, uint c1, int x2, int y2, uint c2, bool semiTrans = false, int blendMode = 0)
	{
		if (!GpuRasterizer) return;
		EnsureDrawGroup( PsxBlendCombo( semiTrans, blendMode ) );
		_vertexBatch.Add(MakeRasterVertex(x0, y0, c0, GpuClip.DrawArea, semiTrans, blendMode));
		_vertexBatch.Add(MakeRasterVertex(x1, y1, c1, GpuClip.DrawArea, semiTrans, blendMode));
		_vertexBatch.Add(MakeRasterVertex(x2, y2, c2, GpuClip.DrawArea, semiTrans, blendMode));
		_diagGpuTrisThisFrame++;
	}

	/// <summary>
	/// Append a textured triangle (3 verts). Texpage/CLUT/format are baked
	/// into per-vertex attributes via the flags field. See psx_raster.shader
	/// for the unpacking layout.
	/// </summary>
	internal void GpuPushTexTri(
		int x0, int y0, int u0, int v0, uint c0,
		int x1, int y1, int u1, int v1, uint c1,
		int x2, int y2, int u2, int v2, uint c2,
		int texPageX, int texPageY, int clutX, int clutY,
		int texMode, bool rawTexture, bool semiTransparent, int blendMode)
	{
		if (!GpuRasterizer) return;
		int flags = (texMode & 3)
			| ((blendMode & 3) << 2)
			| ((rawTexture ? 1 : 0) << 4)
			| ((semiTransparent ? 1 : 0) << 5);

		Vertex v0v = MakeTexVertex( x0, y0, u0, v0, c0, texPageX, texPageY, clutX, clutY, flags );
		Vertex v1v = MakeTexVertex( x1, y1, u1, v1, c1, texPageX, texPageY, clutX, clutY, flags );
		Vertex v2v = MakeTexVertex( x2, y2, u2, v2, c2, texPageX, texPageY, clutX, clutY, flags );

		if ( semiTransparent )
		{
			// PSX per-texel semi-trans: each texel's bit 15 controls its own
			// blend behavior. Bit15==0 = force opaque, bit15==1 = blend.
			// Fixed-function HW blending is a pipeline state, we can't toggle
			// it per pixel, so we draw the same triangle TWICE: once as opaque
			// (the shader discards bit15==1 texels) and once with HW blend on
			// (the shader discards bit15==0 texels). Submission order through
			// the draw-group system is: this poly's opaque pass first, then
			// its blend pass, preserving the PSX "draw force-opaque texels,
			// then blend the rest" semantics.
			EnsureDrawGroup( 0 );
			_vertexBatch.Add( v0v );
			_vertexBatch.Add( v1v );
			_vertexBatch.Add( v2v );

			EnsureDrawGroup( PsxBlendCombo( true, blendMode ) );
			_vertexBatch.Add( v0v );
			_vertexBatch.Add( v1v );
			_vertexBatch.Add( v2v );

			_diagGpuTrisThisFrame += 2;
		}
		else
		{
			// Genuinely opaque textured poly, single opaque draw. The shader's
			// bit-15 check is gated on the flag bit 5 (semi-trans), which is
			// clear here, so every non-zero texel writes through.
			EnsureDrawGroup( 0 );
			_vertexBatch.Add( v0v );
			_vertexBatch.Add( v1v );
			_vertexBatch.Add( v2v );
			_diagGpuTrisThisFrame++;
		}
	}

	private Vertex MakeTexVertex(int x, int y, int u, int v, uint color24,
		int texPageX, int texPageY, int clutX, int clutY, int flags)
	{
		var color = new Color32((byte)(color24 & 0xFF), (byte)((color24 >> 8) & 0xFF), (byte)((color24 >> 16) & 0xFF), 255);
		return new Vertex
		{
			Position = new Vector3(x, y, 0),
			Color = color,
			TexCoord0 = new Vector4(u, v, texPageX, texPageY),
			TexCoord1 = new Vector4(clutX, clutY, flags, 0),
			Tangent = MakeDrawAreaTangent(GpuClip.DrawArea)
		};
	}

	// PSX clipping mode for a vertex builder call.
	private enum GpuClip { DrawArea, Unclipped }

	private Vertex MakeRasterVertex(int x, int y, uint color24, GpuClip clip = GpuClip.DrawArea, bool semiTrans = false, int blendMode = 0)
	{
		// PSX color packs RGB into the low 24 bits: byte 0 = R, byte 1 = G, byte 2 = B.
		// flags.z layout: [0:1]=texMode (3=untextured), [2:3]=blendMode (PSX 0..3),
		//                 [4]=raw (n/a for untextured), [5]=semi-transparent.
		int flags = 3 | ((blendMode & 3) << 2) | ((semiTrans ? 1 : 0) << 5);
		var color = new Color32((byte)(color24 & 0xFF), (byte)((color24 >> 8) & 0xFF), (byte)((color24 >> 16) & 0xFF), 255);
		return new Vertex
		{
			Position = new Vector3(x, y, 0),
			Color = color,
			TexCoord0 = Vector4.Zero,
			TexCoord1 = new Vector4(0, 0, flags, 0),
			Tangent = MakeDrawAreaTangent(clip)
		};
	}

	// Pack PSX DrawArea bounds into the Tangent slot of the vertex. The
	// fragment shader discards pixels outside these bounds, required because
	// PSX polygons are intended to be clipped to the back-buffer rectangle and
	// CPU's PlotPixelGpu already enforces this. Native PSX coords; the shader
	// divides SV_Position by the GpuRasterScale uniform to map upscaled fragment
	// coords back to native space for the comparison.
	private Vector4 MakeDrawAreaTangent(GpuClip clip)
	{
		if (clip == GpuClip.Unclipped)
			return new Vector4(0, 0, PsxConstants.VramWidth - 1, PsxConstants.VramHeight - 1);
		return new Vector4(DrawAreaX1, DrawAreaY1, DrawAreaX2, DrawAreaY2);
	}

	/// <summary>
	/// Called from the worker thread inside SnapshotVram (under _snapshotLock) to publish
	/// this frame's vertex batch for the main thread to consume.
	/// </summary>
	internal void SnapshotVertexBatch()
	{
		if (!GpuRasterizer) return;

		// Close the in-progress draw group (if any) BEFORE swapping, so the
		// snapshot's group list is complete. Indices into _vertexBatch are
		// still valid here because we haven't swapped yet.
		if ( _currentGroupCombo >= 0 )
		{
			int groupVerts = _vertexBatch.Count - _currentGroupStart;
			if ( groupVerts > 0 )
				_drawGroups.Add( new DrawGroup( _currentGroupStart, groupVerts, _currentGroupCombo ) );
		}

		// Swap the two lists (cheap reference swap). Main thread reads from
		// _vertexBatchSnapshot; worker writes to _vertexBatch.
		(_vertexBatch, _vertexBatchSnapshot) = (_vertexBatchSnapshot, _vertexBatch);
		_vertexBatch.Clear();

		// Same swap for draw groups.
		(_drawGroups, _drawGroupsSnapshot) = (_drawGroupsSnapshot, _drawGroups);
		_drawGroups.Clear();

		// Reset the next-frame group tracker. -1 means "no group open yet"
		// so the first Push next frame opens one starting at vert index 0.
		_currentGroupStart = 0;
		_currentGroupCombo = -1;

		_diagGpuTrisLastFrame = _diagGpuTrisThisFrame;
		_diagGpuRectsLastFrame = _diagGpuRectsThisFrame;
		_diagGpuVertsLastFrame = _vertexBatchSnapshot.Count;
		_diagGpuTrisThisFrame = 0;
		_diagGpuRectsThisFrame = 0;
	}

	/// <summary>
	/// Main thread: upload the snapshotted vertex batch to the GPU and queue
	/// per-blend-mode draw calls against VramTex via the CommandList. Runs
	/// BEFORE the display compute dispatch so the display shader sees the
	/// freshly-rasterized output.
	///
	/// Instead of one Draw call for the whole batch, we iterate the
	/// snapshotted draw groups (each a contiguous run of verts sharing one
	/// PSX blend mode) and issue one Draw per group with the corresponding
	/// D_PSX_BLEND combo set. Submission order is preserved because groups
	/// were emitted in vertex-append order on the worker thread.
	/// </summary>
	private void FlushGpuRasterBatch()
	{
		if (!GpuRasterizer) return;

		// Read and upload the vertex snapshot under the lock so the worker
		// thread cannot swap _vertexBatchSnapshot's contents mid-flush. The
		// SetData blocks on a GPU upload but it's microseconds in practice.
		int vertCount;
		int groupCount;
		lock (_snapshotLock)
		{
			vertCount = _vertexBatchSnapshot.Count;
			groupCount = _drawGroupsSnapshot.Count;
			if (vertCount == 0 || groupCount == 0)
				return;

			// Grow the persistent GPU buffer if this frame needs more capacity.
			if (vertCount > _vertexGpuBufCapacity)
			{
				_vertexGpuBuf?.Dispose();
				_vertexGpuBufCapacity = Math.Max(vertCount, _vertexGpuBufCapacity * 2);
				_vertexGpuBuf = new GpuBuffer<Vertex>(_vertexGpuBufCapacity, GpuBuffer.UsageFlags.Vertex, "PSX.RasterVerts");
			}

			// Upload the batch. CollectionsMarshal.AsSpan avoids a full copy.
			_vertexGpuBuf.SetData(CollectionsMarshal.AsSpan(_vertexBatchSnapshot)[..vertCount]);
		}

		// Shared per-draw attributes. GpuRasterScale and VramSourceTex don't
		// change between groups, only the D_PSX_BLEND combo does, so we set
		// them once outside the loop.
		RenderCommandList.Attributes.Set("GpuRasterScale", GpuRasterScale);
		RenderCommandList.Attributes.Set("VramSourceTex", VramSourceTex);

		RenderCommandList.ResourceBarrierTransition(VramTex, ResourceState.RenderTarget);
		RenderCommandList.SetRenderTarget(_vramRenderTarget);

		// Per-group draw calls. Each group gets its own D_PSX_BLEND combo
		// which selects the matching RenderState (blend on/off, factors, op).
		// Groups are in submission order so PSX rendering order is preserved
		// across the inevitable pipeline-state changes that hardware blending
		// requires.
		var groups = CollectionsMarshal.AsSpan(_drawGroupsSnapshot);
		for (int i = 0; i < groups.Length; i++)
		{
			var g = groups[i];
			RenderCommandList.Attributes.SetCombo("D_PSX_BLEND", g.BlendCombo);
			RenderCommandList.Draw(_vertexGpuBuf, _rasterMaterial, g.VertStart, g.VertCount);
		}

		RenderCommandList.ClearRenderTarget();

		// Transition VramTex back to a shader-readable state so the display
		// compute shader (queued later in this same command list) can sample
		// from it as a Texture2D.
		RenderCommandList.ResourceBarrierTransition(VramTex, ResourceState.NonPixelShaderResource);
	}

	/// <summary>
	/// Reads one display pixel in 24-bit packed mode (used during FMV playback).
	/// VRAM holds tightly-packed 24bpp RGB: each pair of 16-bit VRAM words encodes
	/// 2/3 pixels. col is the NATIVE pixel column index (0..displayW-1); each
	/// pixel = 3 bytes starting at native byte offset col*3.
	///
	/// SCALE NOTE: 24-bit FMV is always read at native res, FMV frames are
	/// uploaded as raw bitmap data and aren't higher-res to begin with. Upscaling
	/// happens in the display compute shader (nearest / bilinear filter) when
	/// the result reaches OutputTexture.
	/// </summary>
	private uint Read24BitDisplayPixel(int sx, int sy, int col, int row)
	{
		// Each pixel is 3 bytes. Two consecutive 16-bit VRAM words = 4 bytes
		// = cover 1/3 pixels depending on byte alignment.
		int byteOffset = col * 3;
		int natVx = (sx + (byteOffset >> 1)) & 0x3FF;
		int natVy = (sy + row) & 0x1FF;
		int natVxNext = (natVx + 1) & 0x3FF;

		// Read two consecutive VRAM words (raw 15-bit values, bit 15 ignored)
		uint w0 = _vramSnapshot[natVy * PsxConstants.VramWidth + natVx];
		uint w1 = _vramSnapshot[natVy * PsxConstants.VramWidth + natVxNext];

		// Pack into a 32-bit stream and extract 3 bytes at the sub-word byte position
		uint stream = w0 | (w1 << 16);
		uint rgb = stream >> ((byteOffset & 1) * 8); // shift by 0 or 8 bits

		return (rgb & 0x00FFFFFFu) | 0xFF000000u;
	}
}
