// WebGpuWater - screen-space additive caustic projection + refracted object shadow (URP RenderGraph fullscreen).
// Paints the projected caustic pattern AND the refracted object shadow onto ANY underwater surface by reading
// the depth buffer, reusing the EXACT SAME pool-space projection the water surfaces use (WaterReceiver /
// AnalyticPool), so a Standard-Lit prop, terrain, or a bare ocean floor with no receiver shows caustics and
// object shadows perfectly registered with the water. Because it works off depth it is independent of each
// surface's own shader.
//
// TWO passes, both fullscreen, both stencil-excluding the surfaces that already do this in-shader:
//   Pass 0  Caustic  - ADD (Blend One One)      : refracted caustic pattern, faded with depth, killed under occluders.
//   Pass 1  Shadow   - MULTIPLY (Blend Zero SrcColor): darkens underwater pixels that sit under a submerged
//                                                  occluder, along the SAME refracted ray as the caustics. This is
//                                                  the refracted object shadow for FOREIGN shaders (terrain, Standard
//                                                  Lit), which otherwise only get URP's un-refracted shadow map.
// The shadow reuses the caustic RT's GREEN channel (the CausticOccluder silhouette + depth, projected down the
// refracted sun); it is a no-op wherever no occluder covers the pixel or _CausticOccluderActive is 0.
//
// Driven by WaterCausticProjectionFeature, gated on WaterVolume.CausticProjectionActive (primary body active +
// valid caustic RT + the per-body Screen-Space Caustics opt-in). Above water / outside the footprint each pass
// contributes its identity (add 0 / multiply 1), so an armed pass over dry pixels is a no-op.
//
// Double avoidance (Approach A): WaterReceiver / AnalyticPool ALREADY add caustics and apply the refracted
// occluder shadow in-shader, so both passes must SKIP them. Those shaders write stencil bit 3 (URP
// StencilUsage.UserMask is bits [0,3]) during the opaque ForwardLit pass; both passes run a NotEqual test on it.
Shader "AbstractOcclusion/WebGpuWater/WaterCausticProjection"
{
    Properties
    {
        // Mirror WaterReceiver's caustic controls (NOT published globals - they are per-material on the
        // receiver/pool), driven from the render feature's serialized fields. Depth-fade rate is the body's
        // published _CausticDepthFade global so it stays consistent with the surfaces automatically.
        _CausticStrength ("Caustic Strength", Range(0,8)) = 4
        _CausticTint ("Caustic Tint", Color) = (1,1,1,1)
        // Per-BODY multiplier on Caustic Strength, set per draw by WaterCausticProjectionPass from
        // the body's Screen Caustic Intensity slider (each projecting body can tune its own brightness).
        _ScreenCausticIntensity ("Per-Body Intensity", Range(0,2)) = 1
        // Darkening applied by the refracted object shadow (0 = none, 1 = fully black under an occluder).
        // Matches AnalyticPool's Object Shadow Strength default.
        _RefractedShadowStrength ("Refracted Shadow Strength", Range(0,1)) = 0.6
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // Skip pixels the receiver/pool already caustic+shadow-shaded (they wrote bit 3). NotEqual: draw only
        // where (Ref & ReadMask) != (buffer & ReadMask), i.e. bit 3 is CLEAR. Read-only test (no write).
        Stencil
        {
            Ref 8
            ReadMask 8
            WriteMask 0
            Comp NotEqual
        }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "WaterVolume.hlsl" // WorldToPool / WorldDirToPool / PoolToWorld / FootprintMaskPool (+ volume frame)
        #include "WaterShared.hlsl" // ProjectCausticUV, OccluderLitFromGreen, IOR_*
        #include "WaterFog.hlsl"    // DepthFadeScalar + _CausticDepthFade (published global)

        // Caustic map + green occluder-shadow channel (published globals; UseAllGlobalTextures binds them).
        TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
        // Sim state, for the wavy surface height that decides above/below water (same source the surfaces use).
        TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
        float4 _WaterTexel;           // (1/w, 1/h, w, h) of _WaterTex
        float3 _LightDir;             // global "toward the light", driven from the Unity sun
        float _CausticOccluderActive; // 1 when caustic.g is this body's valid refracted occluder-shadow channel

        CBUFFER_START(UnityPerMaterial)
            float _CausticStrength;
            float4 _CausticTint;
            float _RefractedShadowStrength;
            float _ScreenCausticIntensity; // per-body multiplier (set per draw via the pass's MPB)
        CBUFFER_END

        // Manual bilinear height sample (COPY of WaterReceiver's local helper): WebGPU cannot hardware-filter
        // the float32 sim texture, so a filtered SAMPLE_TEXTURE2D silently point-samples there and the
        // underwater cut goes blocky in builds. SAMPLE_TEXTURE2D_LOD => no implicit derivatives, valid anywhere.
        float SampleWaterHeightBilinear(float2 uv)
        {
            float2 texel = _WaterTexel.xy;
            float2 st = uv * _WaterTexel.zw - 0.5;
            float2 f = frac(st);
            float2 baseUV = (floor(st) + 0.5) * texel;
            float c00 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV, 0).r;
            float c10 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(texel.x, 0.0), 0).r;
            float c01 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(0.0, texel.y), 0).r;
            float c11 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + texel, 0).r;
            return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
        }

        struct Attributes { uint vertexID : SV_VertexID; };
        struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes IN)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
            o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
            return o;
        }

        // Shared reconstruction + gate + refracted caustic-RT sample, in UNIFORM control flow (the GRAD sample
        // must run before any branch - an implicit-derivative sample inside a per-fragment branch is undefined on
        // WebGPU/WGSL). Both passes call this so they project identically and stay registered.
        void SampleProjection(float2 uv, out float3 poolPos, out float3 worldPos,
                              out float underwaterMask, out float4 causticSample, out float surfaceY)
        {
            float rawDepth = SampleSceneDepth(uv);
            worldPos = ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
            poolPos = WorldToPool(worldPos);

            float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
            // Pool-space refracted ray: ProjectCausticUV's xz/y ratio is only valid in pool space, so a WORLD
            // direction mis-projects on non-uniform (deep) bodies. Uniform extents are byte-identical.
            float2 cuv = ProjectCausticUV(poolPos, WorldDirToPool(refractedLight));
            causticSample = SAMPLE_TEXTURE2D_GRAD(_CausticTex, sampler_CausticTex, cuv, ddx(cuv), ddy(cuv));

            float inside = FootprintMaskPool(poolPos);
            float2 wuv = poolPos.xz * 0.5 + 0.5;
            float simH = SampleWaterHeightBilinear(wuv);
            bool isSky = (rawDepth == UNITY_RAW_FAR_CLIP_VALUE);
            underwaterMask = (!isSky && inside > 0.5 && poolPos.y < simH) ? 1.0 : 0.0;
            surfaceY = PoolToWorld(float3(poolPos.x, simH, poolPos.z)).y;
        }

        // Pass 0: additive refracted caustics. Faded with depth, killed under an occluder (green). Masked to 0
        // above water / outside the footprint, so the additive blend adds exactly 0 there.
        half4 FragCaustic(Varyings IN) : SV_Target
        {
            float3 poolPos, worldPos; float underwaterMask; float4 causticSample; float surfaceY;
            SampleProjection(IN.uv, poolPos, worldPos, underwaterMask, causticSample, surfaceY);

            float causticFade = DepthFadeScalar(worldPos.y, surfaceY, _CausticDepthFade);
            float lit = OccluderLitFromGreen(poolPos.y, causticSample.g);
            float3 caustic = _CausticTint.rgb
                           * (causticSample.r * _CausticStrength * _ScreenCausticIntensity
                              * causticFade * lit * underwaterMask);
            return half4(caustic, 0.0); // additive (Blend One One); alpha unused
        }

        // Pass 1: refracted object shadow. MULTIPLIES the scene colour down where an underwater pixel sits below
        // a submerged occluder along the refracted ray - the shadow FOREIGN shaders can't get themselves. Returns
        // 1 (identity) wherever there is no occluder, above water, or when this body wrote no valid occluder
        // channel (green invalid), so it is a true no-op outside real shadow columns.
        half4 FragShadow(Varyings IN) : SV_Target
        {
            float3 poolPos, worldPos; float underwaterMask; float4 causticSample; float surfaceY;
            SampleProjection(IN.uv, poolPos, worldPos, underwaterMask, causticSample, surfaceY);

            float lit = OccluderLitFromGreen(poolPos.y, causticSample.g);
            float shadowAmount = _RefractedShadowStrength * underwaterMask * step(0.5, _CausticOccluderActive);
            float factor = lerp(1.0, lit, shadowAmount); // 1 = unshadowed; < 1 under an occluder
            return half4(factor, factor, factor, 1.0);    // Blend Zero SrcColor => dst *= factor
        }
        ENDHLSL

        // ---- Pass 0: additive caustics ----
        Pass
        {
            Name "WaterCausticProjection"
            Blend One One
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCaustic
            #pragma target 4.0
            ENDHLSL
        }

        // ---- Pass 1: refracted object shadow (multiply) ----
        Pass
        {
            Name "WaterRefractedShadow"
            Blend Zero SrcColor
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragShadow
            #pragma target 4.0
            ENDHLSL
        }
    }
}
