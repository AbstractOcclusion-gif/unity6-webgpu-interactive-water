// WebGpuWater - screen-space density-foam composite (KWS-inspired flagship).
//
// The floating foam particles are NOT drawn as quads: WaterFoamParticles.compute splats
// them into a low-res screen-space density buffer (InterlockedAdd) plus a min-depth
// buffer. This fullscreen triangle then:
//   1. bilinearly reconstructs the half-resolution atlas coverage written by landing foam,
//   2. maps density -> foam with a two-term curve (KWS: a soft "low" film plus a
//      quadratic "high" core, so overlapping particles read as dense white patches),
//   3. occludes: the fragment WRITES the splatted min depth to SV_Depth and z-tests
//      LEqual against the live depth buffer - the ZWrite-On water surface (Transparent+0)
//      has already rasterized every wave crest by the time this pass (+5) runs, so foam
//      behind a nearer crest is rejected per pixel, exactly (KWS parity: their splat
//      rejects against the water depth). The opaque soft fade below stays for rocks, and
//   4. lights the result with the shared foam model, cool-tinted like sea foam.
// Drawn per body via Graphics.RenderPrimitives (3 vertices) after the water surface -
// no render feature, no scene-colour copy, WebGPU-safe (read-only structured buffers
// in the fragment stage; the driver checks device support and falls back to quads).
Shader "AbstractOcclusion/WebGpuWater/FoamDensityComposite"
{
    Properties
    {
        _Tint ("Tint", Color) = (0.75, 0.85, 1.0, 1.0) // KWS's cool sea-foam tint as the default
        _ParticleOpacity ("Opacity", Range(0, 1)) = 1.0
        _DensityLowGain ("Density Low Gain (thin film response)", Range(0, 4)) = 0.6
        _DensityHighGain ("Density High Gain (dense core response)", Range(0, 1)) = 0.15
        // World-anchored lace detail: the density veil erodes through a tileable pattern
        // (sampled in world XZ, so it does NOT swim with the camera) while dense cores stay
        // solid white. Strength 0 = the exact legacy featureless look.
        _BreakupTex ("Breakup Pattern (tileable, R = pattern)", 2D) = "white" {}
        _BreakupTiling ("Breakup Tile Size (m)", Range(0.5, 20)) = 4.0
        _BreakupStrength ("Breakup Strength", Range(0, 1)) = 0.0
    }
    SubShader
    {
        // Transparent+5: after the water surface (+0), BEFORE the spray quads (+10) and the
        // splash particles (+10) so airborne droplets draw over the surface-hugging foam layer.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+5" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            // Premultiplied-alpha over the scene: dense foam can go solid white (additive
            // alone could only brighten, washing out over bright sky reflections).
            Blend One OneMinusSrcAlpha
            ZWrite Off
            // LEqual against the live depth buffer, using the per-fragment SV_Depth written
            // from the splatted min particle depth: waves (ZWrite-On water surface), terrain
            // and props all occlude the veil in one hardware test. ZWrite stays Off - the
            // veil only TESTS, it never blocks anything drawn after it.
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5 // structured buffers in the fragment stage
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "WaterFoamCommon.hlsl" // FoamLitColor / FoamWrappedDiffuseNdotL

            // KWS curve weights: thin film contributes up to LOW_WEIGHT, the quadratic
            // core up to HIGH_WEIGHT (their 0.2 / 0.5 split).
            #define DENSITY_LOW_WEIGHT   0.2
            #define DENSITY_HIGH_WEIGHT  0.5
            // Soft occlusion band (metres) against the opaque scene depth.
            #define OCCLUSION_SOFTNESS   0.15
            // DEPTH_MM_TO_METERS comes from WaterFoamCommon.hlsl, defined as the exact
            // reciprocal of the compute's DEPTH_TO_MM - no keep-in-sync pair anymore.
            // The foam layer sits ON the water surface, so its splatted depth lands within
            // quantization noise of the wave depth at the same pixel - bias the z-test depth
            // this much toward the camera so on-surface foam never self-occludes against the
            // surface it decorates. Behind-crest foam is metres deeper along the ray; a few
            // centimetres of bias cannot leak it through.
            #define VEIL_ZTEST_BIAS_METERS 0.05

            StructuredBuffer<uint> _FoamDensity;      // fixed-point accumulated weight per texel
            StructuredBuffer<uint> _FoamDensityDepth; // min eye depth per texel (millimetres)
            // KWS LOD tiers (crest flecks): half- and quarter-resolution splat buffers. A
            // coarser texel covers more screen, so summing the tiers into the density gives
            // three dot sizes for free (KWS's f0 + f1 + f2).
            StructuredBuffer<uint> _FoamDensityTier1;
            StructuredBuffer<uint> _FoamDensityTier2;
            float2 _DensitySize;
            float  _DensityWeightScale;
            float4 _Tint;
            float  _ParticleOpacity;
            float  _DensityLowGain;
            float  _DensityHighGain;
            sampler2D _BreakupTex;
            float  _BreakupTiling;
            float  _BreakupStrength;
            // World-position reconstruction for the breakup pattern: the SAME view-projection
            // family the splat compute projected with (set per frame by WaterFoamParticles.cs),
            // so pattern lookups land exactly under the splatted foam.
            float4x4 _DensityInvViewProj;
            float3 _DensityCamPos;
            float3 _DensityCamForward;
            float3 _LightDir; // globals published by the primary WaterVolume
            float3 _SunColor;
            float _CameraUnderwater;
            sampler2D _CameraDepthTexture;

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv01      : TEXCOORD0; // OUR uv convention: ndc*0.5+0.5, no platform flip -
                                              // matches ProjectToScreen in the splat compute exactly
                float4 screenPos : TEXCOORD1; // platform-correct, for the scene depth tap only
            };

            // Fullscreen triangle from SV_VertexID: (-1,-1) (3,-1) (-1,3) covers the viewport.
            v2f vert(uint vid : SV_VertexID)
            {
                float2 ndc = float2(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0);
                v2f o;
                o.pos = float4(ndc, 0.0, 1.0);
                o.uv01 = ndc * 0.5 + 0.5;
                // The splat compute lays the density buffer out in UNFLIPPED projection space
                // (GL.GetGPUProjectionMatrix(..., false)). When this pass rasterizes into a
                // y-flipped target (_ProjectionParams.x < 0: D3D-style render-into-texture),
                // the fragment at emitted ndc y lands on the MIRRORED row - without this
                // unflip the veil renders vertically mirrored around screen centre: foam
                // "clouds" appear in the sky above the horizon, and the whole layer moves
                // contrary to the camera (reads as "foam drags with the camera") in every
                // view direction, including straight down. Standard fullscreen-triangle
                // idiom; a no-op (x > 0) on backends that don't flip.
                if (_ProjectionParams.x < 0.0) o.uv01.y = 1.0 - o.uv01.y;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // One density texel, bounds-clamped.
            uint LoadDensity(int2 p)
            {
                p = clamp(p, int2(0, 0), (int2)_DensitySize - 1);
                return _FoamDensity[(uint)p.y * (uint)_DensitySize.x + (uint)p.x];
            }

            uint LoadDepth(int2 p)
            {
                p = clamp(p, int2(0, 0), (int2)_DensitySize - 1);
                return _FoamDensityDepth[(uint)p.y * (uint)_DensitySize.x + (uint)p.x];
            }

            // Tier sizes derived exactly as the splat compute derives them (integer halving
            // per tier), so the index math of the pair can never disagree.
            int2 DensityTierSize(int tier)
            {
                return max(int2(1, 1), (int2)_DensitySize >> tier);
            }

            uint LoadDensityTier1(int2 p, int2 size)
            {
                p = clamp(p, int2(0, 0), size - 1);
                return _FoamDensityTier1[(uint)p.y * (uint)size.x + (uint)p.x];
            }

            uint LoadDensityTier2(int2 p, int2 size)
            {
                p = clamp(p, int2(0, 0), size - 1);
                return _FoamDensityTier2[(uint)p.y * (uint)size.x + (uint)p.x];
            }

            // Structured buffers have no filtered sampler. Manual bilinear reconstruction keeps
            // the half-resolution atlas coverage smooth without growing it into a round shape.
            float SampleBaseDensity(float2 uv)
            {
                float2 coordinate = uv * (float2)_DensitySize - 0.5;
                int2 lower = (int2)floor(coordinate);
                float2 blend = frac(coordinate);
                float bottom = lerp((float)LoadDensity(lower),
                                    (float)LoadDensity(lower + int2(1, 0)), blend.x);
                float top = lerp((float)LoadDensity(lower + int2(0, 1)),
                                 (float)LoadDensity(lower + int2(1, 1)), blend.x);
                return lerp(bottom, top, blend.y);
            }

            // Perspective eye depth (metres) -> raw device depth, the exact inverse of
            // LinearEyeDepth: raw = (1/eye - _ZBufferParams.w) / _ZBufferParams.z. Handles
            // reversed-Z via the params themselves; saturate clamps the empty-texel far case.
            float EyeDepthToRawDepth(float eyeDepth)
            {
                return saturate((1.0 / max(eyeDepth, 1e-4) - _ZBufferParams.w) / _ZBufferParams.z);
            }

            float SceneFogFactor(float eyeDepth)
            {
                #if defined(FOG_LINEAR)
                    return saturate(eyeDepth * unity_FogParams.z + unity_FogParams.w);
                #elif defined(FOG_EXP)
                    return saturate(exp2(-unity_FogParams.y * eyeDepth));
                #elif defined(FOG_EXP2)
                    float fogDepth = unity_FogParams.x * eyeDepth;
                    return saturate(exp2(-fogDepth * fogDepth));
                #else
                    return 1.0;
                #endif
            }

            fixed4 frag(v2f i, out float outDepth : SV_Depth) : SV_Target
            {
                // Empty/rejected texels z-test at the far plane (alpha 0 anyway; the far
                // depth lets the hardware reject them before blending). Overwritten with
                // the real foam depth once a splat is found below.
                outDepth = EyeDepthToRawDepth(1e8);

                int2 px = (int2)(i.uv01 * _DensitySize);

                float tier0Raw = SampleBaseDensity(i.uv01);

                // LOD tiers (KWS f0 + f1 + f2): tier 1/2 texels are already 2x/4x screen size,
                // so they read UNDILATED - a point splat fills its whole coarse texel, there
                // are no pinholes to close - and SUM with the dilated base tier, so piles of
                // different-size dots still accumulate toward solid white.
                int2 tier1Size = DensityTierSize(1);
                uint t1 = LoadDensityTier1((int2)(i.uv01 * (float2)tier1Size), tier1Size);
                int2 tier2Size = DensityTierSize(2);
                uint t2 = LoadDensityTier2((int2)(i.uv01 * (float2)tier2Size), tier2Size);

                float densityRaw = tier0Raw + t1 + t2;
                if (densityRaw <= 0.0) return fixed4(0, 0, 0, 0);

                uint depthMm = min(LoadDepth(px),
                              min(min(LoadDepth(px + int2(-1, 0)), LoadDepth(px + int2(1, 0))),
                                  min(LoadDepth(px + int2(0, -1)), LoadDepth(px + int2(0, 1)))));

                // Two-term density curve (KWS): a soft film that saturates early plus a
                // quadratic core that needs real overlap - sparse foam reads as a veil,
                // piles of particles read as solid white.
                float density = densityRaw / max(_DensityWeightScale, 1.0);
                float foamLow  = saturate(density * _DensityLowGain) * DENSITY_LOW_WEIGHT;
                float foamHigh = saturate(density * density * _DensityHighGain) * DENSITY_HIGH_WEIGHT;
                float alpha = saturate(foamLow + foamHigh) * _ParticleOpacity;
                float foamEye = depthMm * DEPTH_MM_TO_METERS;

                // Per-pixel wave/scene occlusion: hand the foam layer's own depth to the
                // hardware z-test (see the ZTest note above). Biased slightly camera-ward so
                // the veil never fights the very surface it sits on.
                outDepth = EyeDepthToRawDepth(max(foamEye - VEIL_ZTEST_BIAS_METERS, 1e-4));

                // World-anchored breakup lace: reconstruct the foam layer's world position
                // from the splatted min depth along this pixel's camera ray, then erode the
                // thin veil through a tileable pattern. Dense cores stay solid white (the
                // quadratic band overrides the pattern), and the world-XZ lookup means the
                // lace belongs to the water, never to the screen. Uniform branch + explicit
                // LOD: safe after the non-uniform early-out above (WGSL gradient rule).
                if (_BreakupStrength > 0.001)
                {
                    float4 rayClip = float4(i.uv01 * 2.0 - 1.0, 0.5, 1.0);
                    float4 rayPoint4 = mul(_DensityInvViewProj, rayClip);
                    float3 rayDir = rayPoint4.xyz / max(abs(rayPoint4.w), 1e-5) - _DensityCamPos;
                    float viewZ = max(dot(rayDir, _DensityCamForward), 1e-4);
                    float3 foamWorld = _DensityCamPos + rayDir * (foamEye / viewZ);
                    float2 patternUv = foamWorld.xz / max(_BreakupTiling, 0.01);
                    float pattern = tex2Dlod(_BreakupTex, float4(patternUv, 0.0, 0.0)).r;
                    float core = saturate(foamHigh / DENSITY_HIGH_WEIGHT);
                    alpha *= lerp(1.0, lerp(pattern, 1.0, core), _BreakupStrength);
                }

                // Soft occlusion against the opaque scene: foam behind geometry fades out
                // over OCCLUSION_SOFTNESS instead of clipping.
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(saturate(suv), 0, 0)));
                alpha *= saturate(1.0 + (sceneEye - foamEye) / OCCLUSION_SOFTNESS);
                if (alpha <= 0.0) return fixed4(0, 0, 0, 0);

                // Shared foam lighting, as upward-facing foam (a screen-space layer has no
                // normal); the default _Tint carries KWS's cool sea-foam cast.
                float wrapped = FoamWrappedDiffuseNdotL(_LightDir.y);
                float3 lit = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                float3 premultipliedColor = lit * alpha;
                if (_CameraUnderwater < 0.5)
                    premultipliedColor = lerp(unity_FogColor.rgb * alpha, premultipliedColor,
                                              SceneFogFactor(foamEye));
                return fixed4(premultipliedColor, alpha); // premultiplied
            }
            ENDCG
        }
    }
}
