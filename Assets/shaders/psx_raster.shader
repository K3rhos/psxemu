HEADER
{
	DevShader = true;
	Description = "PSX hardware rasterizer - GPU-side polygon rasterization for upscaled VRAM";
}

MODES
{
	Default();
	Forward();
}

FEATURES
{
}

COMMON
{
	#include "system.fxc"
}

// Vertex layout — packed PSX state per-vertex into the standard s&box Vertex struct.
// CPU side builds these via PsxGpu.Rendering.MakeVertex(...).
struct VS_INPUT
{
	// .xy = NATIVE PSX VRAM coords (0..1024, 0..512), post DrawOffset.
	// .z  = unused (PSX has no depth buffer).
	float3 vPositionOs : POSITION < Semantic( PosXyz ); >;

	// PSX vertex color (Gouraud RGB). Alpha = 1 — PSX has no per-vertex alpha.
	float4 vColor : COLOR0 < Semantic( Color ); >;

	// .xy = PSX UV (native, 0..255 within texpage).
	// .zw = texture-page top-left in native VRAM coords (e.g. (640, 0)).
	float4 vTexCoord0 : TEXCOORD0 < Semantic( LowPrecisionUv ); >;

	// .xy = CLUT top-left in native VRAM coords.
	// .z  = packed flags  bits: [0:1]=texMode (0=4bpp, 1=8bpp, 2=15bpp, 3=untextured)
	//                           [4]  =raw texture (skip vertex-color modulation)
	//                           NOTE: bits [2:3] (blendMode) and [5] (semi-trans)
	//                           are no longer read in the PS — Phase 7b uses HW
	//                           blending driven by D_PSX_BLEND combo and per-group
	//                           draw calls.  The CPU side decides which group a
	//                           vert belongs to using those flag bits at push time.
	// .w  = reserved (mask bit, dither, etc.)
	float4 vTexCoord1 : TEXCOORD1 < Semantic( LowPrecisionUv1 ); >;

	// PSX drawing-area clip at NATIVE PSX coords.
	// (X1, Y1, X2, Y2) — both inclusive endpoints on PSX hardware.
	// Per-vertex so polys submitted across DrawArea changes still clip correctly.
	// Hijacks the unused Tangent slot in the standard s&box Vertex struct.
	float4 vTangent : TANGENT < Semantic( TangentU_SignV ); >;
};

struct PS_INPUT
{
	float4 vColor : COLOR0;
	float2 vUv : TEXCOORD0;
	// nointerpolation: PSX texpage / CLUT / flags are per-primitive constants,
	// must not interpolate across the triangle.
	nointerpolation float4 vTexpage : TEXCOORD1;  // texpageX, texpageY, clutX, clutY
	nointerpolation float4 vFlags : TEXCOORD2;    // (clutX, clutY, packedFlags, reserved) — passthrough of vTexCoord1
	nointerpolation float4 vDrawArea : TEXCOORD3; // (X1, Y1, X2, Y2) at NATIVE PSX coords — passthrough of vTangent
	float4 vPositionPs : SV_Position;
};

VS
{
	// VRAM dims used for orthographic projection.  Always NATIVE (1024×512) —
	// the viewport (auto-set from VramTex's size when SetRenderTarget binds it)
	// is what scales rasterization up at GpuRasterScale > 1.  Multiplying by
	// GpuRasterScale here would shrink polygons into the top-left corner.
	PS_INPUT MainVs( VS_INPUT i )
	{
		PS_INPUT o;

		// Map native VRAM coord (0..1024, 0..512) to clip space (-1..+1).
		// Y is inverted (top of VRAM = +Y in clip space).
		const float vramW = 1024.0;
		const float vramH = 512.0;
		o.vPositionPs.x = (i.vPositionOs.x / vramW) * 2.0 - 1.0;
		o.vPositionPs.y = 1.0 - (i.vPositionOs.y / vramH) * 2.0;
		o.vPositionPs.z = 0.5;
		o.vPositionPs.w = 1.0;

		o.vColor = i.vColor;
		o.vUv = i.vTexCoord0.xy;
		// Pack (texpageX, texpageY, clutX, clutY) into one float4 for the PS.
		o.vTexpage = float4( i.vTexCoord0.zw, i.vTexCoord1.xy );
		o.vFlags = i.vTexCoord1;
		o.vDrawArea = i.vTangent;
		return o;
	}
}

PS
{
	// ─── PSX semi-transparency via hardware blending (Phase 7b) ──────────────
	//
	// D_PSX_BLEND selects one of five RenderState configurations:
	//   0 = opaque (blend disabled — defaults to SRC=ONE/DST=ZERO)
	//   1 = PSX mode 0: B/2 + F/2     (SRC_ALPHA / INV_SRC_ALPHA, alpha=0.5)
	//   2 = PSX mode 1: B + F          (ONE / ONE, ADD)
	//   3 = PSX mode 2: B - F clamped  (ONE / ONE, REV_SUBTRACT — dst - src)
	//   4 = PSX mode 3: B + F/4        (ONE / ONE, ADD — shader pre-multiplies by 0.25)
	//
	// CPU side (PsxGpu.Rendering.cs) batches verts into contiguous runs of the
	// same blend mode and issues one Draw per run with the matching combo set.
	// Order is preserved because the vertex batch is append-only and groups
	// split it into contiguous ranges — PSX rendering is order-dependent and
	// this is the only correct way to express that on the GPU.
	DynamicCombo( D_PSX_BLEND, 0..4, Sys( ALL ) );

	#if D_PSX_BLEND == 1
		// Mode 0: B/2 + F/2.  Shader outputs alpha = 0.5 to drive the SRC_ALPHA
		// blend factor; RGB blends as fg*0.5 + bg*0.5.  Alpha is summed so the
		// display shader's "alpha > 0" mask correctly treats blended pixels as
		// "GPU drew here" (won't fall through to VramSourceTex).
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, SRC_ALPHA );
		RenderState( DstBlend, INV_SRC_ALPHA );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ONE );
		RenderState( DstBlendAlpha, ONE );
		RenderState( BlendOpAlpha, ADD );
	#elif D_PSX_BLEND == 2
		// Mode 1: B + F.  Additive blend.
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ONE );
        RenderState(DstBlendAlpha, ONE);
        RenderState(BlendOpAlpha, ADD );
	#elif D_PSX_BLEND == 3
    	// Mode 2: B - F (clamped to 0).  REV_SUBTRACT computes dst - src;
		// the unsigned render-target format clamps negative results to 0.
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, ONE );
        RenderState(DstBlend, ONE);
        RenderState(BlendOp, REV_SUBTRACT );
		RenderState( SrcBlendAlpha, ONE );
		RenderState( DstBlendAlpha, ONE );
		RenderState( BlendOpAlpha, ADD );
	#elif D_PSX_BLEND == 4
		// Mode 3: B + F/4.  Shader pre-multiplies fg by 0.25; blend is plain ADD.
		RenderState( BlendEnable, true );
		RenderState( SrcBlend, ONE );
		RenderState( DstBlend, ONE );
		RenderState( BlendOp, ADD );
		RenderState( SrcBlendAlpha, ONE );
		RenderState( DstBlendAlpha, ONE );
		RenderState( BlendOpAlpha, ADD );
	#endif

	// CPU-uploaded native-resolution mirror of VRAM, used for texture sampling.
	// Each pixel packs an ushort VRAM word as (R = loByte, G = hiByte, B = 0,
	// A = 0xFF) so we can reconstruct raw 16-bit data for CLUT-indexed formats.
	Texture2D<float4> VramSourceTex < Attribute( "VramSourceTex" ); >;

	int GpuRasterScale < Attribute( "GpuRasterScale" ); Default( 1 ); >;

	// PSX rasterizer renders flat 2D into VRAM — no depth, no culling.
	RenderState( DepthEnable, false );
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );

	// Read a raw 16-bit VRAM word at (vramX, vramY).  VRAM coords wrap at the
	// native 1024×512 boundary (PSX hardware behavior).  The +0.5 in the
	// float→int conversion is essentially redundant because Load returns
	// exact 8-bit float multiples — included for safety.
	int LoadVramWord( int vramX, int vramY )
	{
		int4 px = (int4)( VramSourceTex.Load( int3( vramX & 0x3FF, vramY & 0x1FF, 0 ) ) * 255.0 + 0.5 );
		return px.r | ( px.g << 8 );
	}

	// Unpack a packed 16-bit RGB555+mask value into normalized float RGB.
	// Mask bit (bit 15) is discarded here — Phase 10 will use it for per-texel
	// blend opt-out on textured semi-trans polys.
	float3 UnpackRgb555( int word )
	{
		int r5 = word & 0x1F;
		int g5 = ( word >> 5 ) & 0x1F;
		int b5 = ( word >> 10 ) & 0x1F;
		return float3( r5, g5, b5 ) / 31.0;
	}

	// Return the post-CLUT 16-bit texel value, or 0 for transparent.
	// texMode: 0 = 4bpp CLUT, 1 = 8bpp CLUT, 2 = 15bpp direct.
	int SampleTexel( int texMode, int u, int v, int texPageX, int texPageY, int clutX, int clutY )
	{
		// PSX UV wraps at 256 within a texture page.
		u &= 0xFF;
		v &= 0xFF;

		if ( texMode == 0 )  // 4bpp: 4 texels per VRAM word, low nibble first
		{
			int word = LoadVramWord( texPageX + ( u >> 2 ), texPageY + v );
			int nibble = ( word >> ( ( u & 3 ) * 4 ) ) & 0xF;
			return LoadVramWord( clutX + nibble, clutY );
		}
		if ( texMode == 1 )  // 8bpp: 2 texels per VRAM word
		{
			int word = LoadVramWord( texPageX + ( u >> 1 ), texPageY + v );
			int idx = ( ( u & 1 ) == 0 ) ? ( word & 0xFF ) : ( ( word >> 8 ) & 0xFF );
			return LoadVramWord( clutX + idx, clutY );
		}
		// 15bpp: direct read, no CLUT
		return LoadVramWord( texPageX + u, texPageY + v );
	}

	// Pack the shader output for the current blend mode.  The C# side already
	// grouped this draw by blend mode and set the matching D_PSX_BLEND combo,
	// so the RenderState above is in effect; we just need to emit the right
	// alpha (for mode 0) or pre-multiplied RGB (for mode 3).
	float4 PsxOutput( float3 rgb )
	{
	#if D_PSX_BLEND == 1
		// Mode 0: alpha drives SRC_ALPHA blend factor — must be 0.5.
		return float4( rgb, 0.5 );
	#elif D_PSX_BLEND == 4
		// Mode 3: B + F/4.  Pre-multiply fg by 0.25 since blend is plain ADD.
		return float4( rgb * 0.25, 1.0 );
	#else
		// Opaque (0), mode 1 (2), mode 2 (3): plain RGB with alpha=1.
		return float4( rgb, 1.0 );
	#endif
	}

	float4 MainPs( PS_INPUT i ) : SV_Target0
	{
		// Drawing-area clip (PSX state: DrawAreaX1/Y1/X2/Y2 — both inclusive).
		// CPU rasterizer enforces this per-pixel in PlotPixelGpu; we mirror it
		// here so polys can't spill outside the back buffer into adjacent VRAM
		// (which was the front-buffer-garbage / corner-flickering bug at scale).
		// vPositionPs.xy is in viewport-pixel coords (upscaled); divide by the
		// scale factor to get the native PSX pixel that DrawArea is expressed in.
		int2 nativePx = (int2)( i.vPositionPs.xy / max( (float)GpuRasterScale, 1.0 ) );
		if ( nativePx.x < (int)i.vDrawArea.x || nativePx.x > (int)i.vDrawArea.z ||
		     nativePx.y < (int)i.vDrawArea.y || nativePx.y > (int)i.vDrawArea.w )
			discard;

		int packedFlags = (int)i.vFlags.z;
		int texMode = packedFlags & 3;            // 0..2 = textured, 3 = untextured
		bool isRaw = ( packedFlags & 0x10 ) != 0;

		// Untextured: solid / Gouraud vertex color — with one special case.
		//
		// Clear quads pushed by CPU-only VRAM writes (CmdFillRect / CmdCpuToVram /
		// CmdVramVramCopy) emit vColor.a == 0 as the "this region of VRAM was
		// touched outside the polygon rasterizer" signal.  Phase 7a let those
		// pixels fall through to VramSourceTex via the display shader's alpha-0
		// fallback — but Phase 7b needs the actual background content INSIDE
		// VramTex so HW-blended semi-trans polys drawn over CPU-only content
		// (e.g. RE2 HUD over pre-rendered backgrounds) see the right bg.
		//
		// So on the opaque pass we sample VramSourceTex at the corresponding
		// native PSX coord and write that into VramTex.  Subsequent semi-trans
		// poly draws this frame then blend against the correct bg.  Display
		// shader's alpha-0 fallback still exists as a safety net for regions
		// the CPU hasn't touched at all.
		if ( texMode == 3 )
		{
		#if D_PSX_BLEND == 0
			if ( i.vColor.a < 0.5 )
			{
				int2 nativePx = (int2)( i.vPositionPs.xy / max( (float)GpuRasterScale, 1.0 ) );
				int srcWord = LoadVramWord( nativePx.x, nativePx.y );
				float3 srcRgb = UnpackRgb555( srcWord );
				return float4( srcRgb, 1.0 );
			}
			return i.vColor;
		#else
			return PsxOutput( i.vColor.rgb );
		#endif
		}

		// Textured path (Phase 5 + 6 + 7b).
		int texPageX = (int)i.vTexpage.x;
		int texPageY = (int)i.vTexpage.y;
		int clutX = (int)i.vTexpage.z;
		int clutY = (int)i.vTexpage.w;
		int u = (int)i.vUv.x;
		int v = (int)i.vUv.y;

		int texel = SampleTexel( texMode, u, v, texPageX, texPageY, clutX, clutY );
		if ( texel == 0 ) discard;

		// ─── PSX per-texel semi-transparency selection ─────────────────────────
		//
		// PSX hardware checks texel bit 15 (the "mask" or "STP" bit) per pixel
		// on textured semi-transparent polys:
		//   bit15 == 1 → blend this texel with the framebuffer (semi-trans)
		//   bit15 == 0 → write this texel opaque (force-opaque texel)
		//
		// To express that on fixed-function HW blending — which is a pipeline
		// state, not per-pixel — the CPU side pushes textured semi-trans polys
		// TWICE: once into the opaque draw group (D_PSX_BLEND == 0) and once
		// into the matching blend group (D_PSX_BLEND >= 1).  Each pass discards
		// the texels that belong to the other pass:
		//   - opaque pass on a semi-trans poly: discard bit15==1 texels
		//   - blend pass: discard bit15==0 texels (already drawn opaque)
		//
		// Without this, Crash/Driver/etc menus render with their button text
		// half-transparent because the surrounding-pixel texels (bit15==1) AND
		// the letter texels (bit15==0) both get blended.
		bool texelMask = ( texel & 0x8000 ) != 0;
	#if D_PSX_BLEND == 0
		// Opaque pass.  Flag bit 5 = "this poly was originally semi-trans".
		// Genuinely opaque polys (flag clear) draw every texel unconditionally.
		bool isSemiTrans = ( packedFlags & 0x20 ) != 0;
		if ( isSemiTrans && texelMask ) discard;
	#else
		// Blend pass.  Only textured semi-trans polys reach the textured path
		// in D_PSX_BLEND >= 1; their bit15==0 texels were drawn in the opaque
		// pass and must be skipped here to avoid double-rendering.
		if ( !texelMask ) discard;
	#endif

		float3 rgb = UnpackRgb555( texel );
		if ( !isRaw )
			rgb = saturate( rgb * i.vColor.rgb * 1.9921875 );  // PSX modulation

		return PsxOutput( rgb );
	}
}
