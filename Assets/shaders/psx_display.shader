HEADER
{
    Description = "PSX Display: copies a packed RGBA8888 display window to the output texture with per-axis scaling";
}

MODES
{
    Default();
}

FEATURES
{
}

COMMON
{
	#include "system.fxc"
}

CS
{
    // Packed display window pixels, pre-converted to RGBA8888 on the CPU.
    // Each uint = 0xAABBGGRR (little-endian RGBA).
    StructuredBuffer<uint> VramBuf < Attribute("VramBuf"); > ;

    // GPU rasterizer path: when UseVramTex == 1, sample directly from the GPU
    // rasterizer's VramTex render target instead of the CPU-uploaded VramBuf.
    // This avoids the per-frame Vram[] → _displayBuf → _gpuVramBuf round-trip
    // when the GPU rasterizer (Phase 2+) is enabled.
    Texture2D<float4> VramTex < Attribute("VramTex"); > ;

    // Native-resolution VRAM mirror (CPU-uploaded each frame).  Used as the
    // fallback when VramTex's alpha is 0 — i.e. where the GPU rasterizer hasn't
    // drawn this frame.  This is how CPU-only paths (VRAM uploads / DMA / FMV
    // pre-renders / semi-transparent polys we skip on GPU) still appear.
    Texture2D<float4> VramSourceTex < Attribute("VramSourceTex"); > ;

    int UseVramTex < Attribute("UseVramTex"); > ;
    int DisplayStartX < Attribute("DisplayStartX"); > ;
    int DisplayStartY < Attribute("DisplayStartY"); > ;

    // Display coords in VramTex are upscaled; VramSourceTex is always native.
    // Pass the original (pre-scaled) DisplayStart and the scale factor so the
    // fallback path can compute its sampling coordinate.
    int NativeDisplayStartX < Attribute("NativeDisplayStartX"); > ;
    int NativeDisplayStartY < Attribute("NativeDisplayStartY"); > ;
    int VramRasterScale < Attribute("VramRasterScale"); > ;

    RWTexture2D<float4> OutputTex < Attribute("OutputTex"); > ;

    int DisplayW < Attribute("DisplayW"); > ;
    int DisplayH < Attribute("DisplayH"); > ;
    int DrawW < Attribute("DrawW"); > ;
    int DrawH < Attribute("DrawH"); > ;
    float ScaleX < Attribute("ScaleX"); > ;
    float ScaleY < Attribute("ScaleY"); > ;
    int OffsetX < Attribute("OffsetX"); > ;
    int OffsetY < Attribute("OffsetY"); > ;
    int DisplayFilter < Attribute("DisplayFilter"); > ;
    float ScanlineStrength < Attribute("ScanlineStrength"); > ;
    float ScanlineSharpness < Attribute("ScanlineSharpness"); > ;
    float ScanlineFrequency < Attribute("ScanlineFrequency"); > ;
    float PhosphorMaskStrength < Attribute("PhosphorMaskStrength"); > ;
    float CrtColorBoost < Attribute("CrtColorBoost"); > ;

    float4 DecodePixel(int x, int y)
    {
        x = clamp(x, 0, DisplayW - 1);
        y = clamp(y, 0, DisplayH - 1);

        // GPU rasterizer path: read directly from the rendered VRAM texture.
        // DisplayStartX/Y are the top-left of the display window in (upscaled)
        // VRAM coords; the rasterizer writes there during the polygon pass.
        // Where VramTex alpha == 0 (nothing drawn by GPU this frame), fall back
        // to VramSourceTex (native VRAM mirror) so CPU-only paths still appear:
        //   - VRAM uploads (texture data, pre-renders, FMV-style blits)
        //   - Semi-transparent polys we skip on GPU until Phase 7
        //   - Anything the game wrote to VRAM that isn't a polygon
        if (UseVramTex == 1)
        {
            float4 vc = VramTex.Load(int3(DisplayStartX + x, DisplayStartY + y, 0));
            // Threshold > 0.01 (not > 0.5) because Phase 7b's hardware-blended
            // mode 0 (B/2 + F/2) leaves alpha = 0.5 on first write to a clear
            // region.  Anything > 0 means "GPU drew here"; only literal clears
            // (Fill VRAM / CPU→VRAM upload / VRAM→VRAM copy quads that emit
            // alpha=0) fall through to VramSourceTex.
            if (vc.a > 0.01)
                return float4(vc.rgb, 1.0);

            // Fallback: native-resolution VramSourceTex.  Unpack the packed
            // (R = loByte, G = hiByte) format back into RGB555 → normalized float.
            int natX = NativeDisplayStartX + x / max(VramRasterScale, 1);
            int natY = NativeDisplayStartY + y / max(VramRasterScale, 1);
            float4 sc = VramSourceTex.Load(int3(natX & 0x3FF, natY & 0x1FF, 0));
            int word = (int)(sc.r * 255.0 + 0.5) | ((int)(sc.g * 255.0 + 0.5) << 8);
            return float4((word & 0x1F) / 31.0, ((word >> 5) & 0x1F) / 31.0, ((word >> 10) & 0x1F) / 31.0, 1.0);
        }

        uint pixel = VramBuf[y * DisplayW + x];

        float4 color;
        color.r = (float)(pixel & 0xFF) / 255.0;
        color.g = (float)((pixel >> 8) & 0xFF) / 255.0;
        color.b = (float)((pixel >> 16) & 0xFF) / 255.0;
        color.a = 1.0;

        return color;
    }

    float4 SampleNearest(int localX, int localY)
    {
        int nativeX = (int)floor((float)localX / max(ScaleX, 0.0001));
        int nativeY = (int)floor((float)localY / max(ScaleY, 0.0001));

        return DecodePixel(nativeX, nativeY);
    }

    float4 SampleBilinear(int localX, int localY)
    {
        float srcX = ((float)localX + 0.5) / max(ScaleX, 0.0001) - 0.5;
        float srcY = ((float)localY + 0.5) / max(ScaleY, 0.0001) - 0.5;

        int x0 = (int)floor(srcX);
        int y0 = (int)floor(srcY);
        int x1 = x0 + 1;
        int y1 = y0 + 1;
        float tx = frac(srcX);
        float ty = frac(srcY);

        float4 c00 = DecodePixel(x0, y0);
        float4 c10 = DecodePixel(x1, y0);
        float4 c01 = DecodePixel(x0, y1);
        float4 c11 = DecodePixel(x1, y1);

        float4 cx0 = lerp(c00, c10, tx);
        float4 cx1 = lerp(c01, c11, tx);

        return lerp(cx0, cx1, ty);
    }

    float3 ApplyCrtMask(float3 color, int localX)
    {
        float maskStrength = clamp(PhosphorMaskStrength, 0.0, 1.0);

        if (maskStrength <= 0.0)
            return color;

        // Output columns per NATIVE pixel (ScaleX × render-scale = drawW/nativeW),
        // constant in native terms at any render scale.  Three RGB stripes per native
        // pixel, each kept >= 1 output column wide so it doesn't alias / drop a colour
        // at low render scale.
        float colsPerNative = max(ScaleX * (float)max(VramRasterScale, 1), 1.0);
        float stripeWidth = max(1.0, colsPerNative / 3.0);
        int phase = (int)floor((float)localX / stripeWidth) % 3;
        float3 mask = phase == 0
                          ? float3(1.0, 0.78, 0.78)
                          : (phase == 1
                                 ? float3(0.78, 1.0, 0.78)
                                 : float3(0.78, 0.78, 1.0));

        return color * lerp(float3(1.0, 1.0, 1.0), mask, maskStrength);
    }

    float3 ApplyCrtScanline(float3 color, int localY)
    {
        float strength = clamp(ScanlineStrength, 0.0, 1.0);

        if (strength <= 0.0)
            return color;

        // Output rows ONE NATIVE PSX scanline occupies.  ScaleY maps output→the
        // (already-upscaled) display window and VramRasterScale is the render-scale
        // upscale, so their product is output-rows-per-NATIVE-row = drawH/nativeH —
        // CONSTANT in native terms at any render scale.  Dividing localY by it gives
        // the true native row, so the scanline COUNT is identical at 1x or 16x (only
        // the underlying game gets sharper).
        float rowsPerNative = max(ScaleY * (float)max(VramRasterScale, 1), 1.0);
        float nativeY = (float)localY / rowsPerNative;
        // One scanline per native line × ScanlineFrequency.  No +0.5 offset (it shifts
        // the samples off the beam peak at low scale → flat dim screen).  Nyquist-cap
        // to <= 1 scanline per 2 output rows so it never aliases to a dark frame;
        // denser scanlines than that simply need a higher render scale.
        float density = min(clamp(ScanlineFrequency, 0.0, 8.0), rowsPerNative * 0.5);
        float sourceY = nativeY * max(density, 0.0001);

        float distanceToBeam = abs(frac(sourceY) - 0.5) * 2.0;
        float sharpness = max(ScanlineSharpness, 0.5);
        float beam = exp(-pow(distanceToBeam * sharpness, 2.0));

        float luma = dot(color, float3(0.2126, 0.7152, 0.0722));
        float brightPixelBeam = lerp(beam, 1.0, saturate(luma * 0.65));
        float lineWeight = lerp(1.0 - strength, 1.0, brightPixelBeam);

        return color * lineWeight;
    }

    [numthreads(8, 8, 1)]
    void MainCs(uint3 id: SV_DispatchThreadID)
    {
        int localX = (int)id.x - OffsetX;
        int localY = (int)id.y - OffsetY;

        if (localX < 0 || localY < 0 || localX >= DrawW || localY >= DrawH)
        {
            OutputTex[id.xy] = float4(0, 0, 0, 1);

            return;
        }

        float4 color = DisplayFilter == 1 ? SampleBilinear(localX, localY) : SampleNearest(localX, localY);

        color.rgb = ApplyCrtScanline(color.rgb, localY);
        color.rgb = ApplyCrtMask(color.rgb, localX);
        color.rgb = saturate(color.rgb * clamp(CrtColorBoost, 1.0, 2.0));

        OutputTex[id.xy] = color;
    }
}
