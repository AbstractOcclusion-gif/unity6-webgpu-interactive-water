// WebGpuWater - underwater god rays (URP RenderGraph fullscreen).
// The MILESTONE shafts: sunlight beams seen from BELOW the surface, broken by shadows and (next
// increment) the surface caustics, living inside the underwater fog volume. This SUPERSEDES the
// earlier above-water atmosphere use of this shader (that look was a misdirection). It reuses the
// pool GodRays technique (shadow-shaft raymarch) but fullscreen and bounded to the water volume -
// exactly as the underwater fog generalised the pool-box fog to the ocean half-space.
//
// Increment 1: shadow + Henyey-Greenstein phase shafts, marched only along the IN-WATER part of the
// view ray (stops at the scene, the far plane, or the surface for an up-ray), tinted + thinned by the
// shared water fog and downwelling depth. Caustic shimmer arrives next (near-field sim caustic).
//
// Five passes are DECLARED, TWO are dispatched per frame: 0 (Full fog tiers) OR 4 (Simple fog
// tiers) = raymarch into a half-res persistent history target (reads scene depth + main-light
// shadows via URP globals; animated-jitter march + temporal reprojection accumulation), one
// shared body in LargeBodyGodRaysRaymarch.hlsl; 3 = additive composite (global _LargeGodRayTex)
// over the camera colour. Jitter + temporal accumulation are what calms the shafts today - few
// march steps read as many, and fast flicker cannot survive the accumulation.
//
// 1+2 = separable Gaussian blur. COMPILED BUT NEVER DISPATCHED - LargeBodyAtmospherePass runs
// RaymarchShaderPass (0) or RaymarchSimpleShaderPass (4), then CompositeShaderPass (3); see its
// pass-index consts. They are kept for the
// UNDERWATER view, where softening the shafts is pure gain. They are NOT simply switched on because
// of the from-air case below: a fullscreen separable blur bleeds across depth discontinuities, and
// an above-water camera looking through an exclusion volume's window (_LargeGodRayFromAir > 0)
// depends on the carve boundary staying a HARD edge - blurred shafts would smear across the wall's
// silhouette. Wiring them therefore means gating on submersion, or making the blur depth-aware.
// Unfinished work, not an oversight: do not delete, and do not assume they run.
// Runs when the camera is submerged (fading in over the first centimetres below the surface -
// spatial, so wave-driven crossings never pop) AND, at _LargeGodRayFromAir > 0, when an above-water
// camera looks into the water THROUGH AN EXCLUSION VOLUME'S WINDOW. The from-air case is culled to
// exactly that: a ray whose waterline crossing lands inside a carve - and that crossing is solved
// against the DISPLACED surface at its own xz, so the pane's edge follows the waves and does not
// move when the camera does. "Inside a carve" is answered per tier: analytic volumes by the point
// test, MESH volumes by the rasterised prepass span along the pixel's own ray. Over open sea a
// viewer in air gets nothing, because the surface shader owns that view and shafts there would be
// painted onto water the viewer is not inside. Requires the URP asset's Depth Texture ON and main-light shadows
// enabled. All tuning comes from published globals.
//
// SURFACE-SYNC CONTRACT (the waterline-transition rule, from the KWS/Crest post-mortem): every
// VISUAL term in this shader that needs "where is the surface" reads the CURRENT-FRAME GPU field
// (SurfaceHeightAtXZ - the same FFT texture + wave clock the rendered surface and the fog's
// per-pixel waterline use this frame). The CPU-published _UnderwaterSurfaceY is an async readback
// ~1-2 frames stale; keying visual terms on it desynced the shafts from the drawn surface and the
// fog in a heavy sea - worst with a STATIC camera, where the temporal accumulation hardened the
// lag into a visible seam instead of motion masking it. The scalar remains the authority only on
// WATER_FOG_SIMPLE tiers, whose fog waterline is the same flat scalar - each tier stays
// internally consistent. The composite is additionally masked by the fog's own per-pixel
// waterline coverage curve (WaterlineCoverage), so a stale history texel can never glow over a
// pixel the fog classifies as air.
Shader "AbstractOcclusion/WebGpuWater/LargeBodyGodRays"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // ---- Pass 0: raymarch the shafts into the half-res target (FULL fog tiers) -------
        // The march body lives in LargeBodyGodRaysRaymarch.hlsl, shared with pass 4 (the Simple-
        // tier twin). Two passes because the WebGPU translator guard below cannot be scoped to a
        // keyword variant: this pass compiles WITHOUT WATER_FOG_SIMPLE and WITHOUT the guard, so
        // the Full march ships optimised on web; LargeBodyAtmospherePass picks the pass index by
        // the same fact that used to select the keyword variant (WaterVolume.UnderwaterFogSimplePublished).
        Pass
        {
            Name "LargeBodyGodRaysRaymarch"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma target 4.0
            // Sample the main light's shadow MAP (cascades), matching the pool GodRays pass. The
            // screen-space variant is intentionally omitted: it is keyed to opaque-surface depth
            // and would be wrong for arbitrary volumetric samples. Without a shadowmap the pass
            // degrades gracefully to unshadowed shafts.
            // _fragment: the only consumer is in frag, so the unscoped form compiled one identical
            // vertex program per shadow keyword for nothing.
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            // The Simple-tier fork is NO LONGER a keyword on this pass (2026-09-02): it is pass 4,
            // which #defines WATER_FOG_SIMPLE itself before the shared include. Full derives every
            // surface reference from the live GPU field (see the surface-sync contract in the
            // file header); Simple keeps the CPU scalar its flat fog waterline is built on.
            // Match the fog's compile fork: bodies without a shore field do not carry the shore
            // SDF and surf-front machinery through this already-large translation unit.
            #pragma multi_compile_fragment _ WATER_STRIP_SHORE
            // A2: scene-lamp in-scatter inside the march. This deliberately has its OWN keyword,
            // separate from analytic fog scatter: the per-sample lamp loop would otherwise size
            // every march pixel's register allocation when only the cheap fog glow was requested.
            // PublishUnderwater arms it only when the god-ray knob is non-zero, Full fog is active,
            // and the published list contains an eligible point/spot light.
            #pragma multi_compile_fragment _ WATER_GODRAY_POINT_LIGHTS

            #include "LargeBodyGodRaysRaymarch.hlsl"
            ENDHLSL
        }

        // ---- Passes 1+2: separable Gaussian blur of the half-res shafts (the KWS pyramid-blur
        // equivalent), intended as a third calm pillar after jitter + temporal accumulation.
        // Linear-sampled 9-tap Gaussian in two directions.
        // NOT DISPATCHED: the composite reads the RAW march target, not this. Parked for the
        // underwater view; see the file header for why it cannot just be turned on globally. -------
        Pass
        {
            Name "LargeBodyGodRaysBlurH"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 4.0
            #define BLUR_DIR float2(1.0, 0.0)
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_GodRayBlurSrc); SAMPLER(sampler_GodRayBlurSrc);
            float4 _GodRayBlurSrc_TexelSize;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragBlur(Varyings input) : SV_Target
            {
                // Linear-sampled 9-tap Gaussian: 5 fetches, bilinear does the pairing.
                float2 step = BLUR_DIR * _GodRayBlurSrc_TexelSize.xy;
                half3 c = SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv, 0).rgb * 0.227027;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 3.230769, 0).rgb * 0.070270;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 3.230769, 0).rgb * 0.070270;
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "LargeBodyGodRaysBlurV"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 4.0
            #define BLUR_DIR float2(0.0, 1.0)
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_GodRayBlurSrc); SAMPLER(sampler_GodRayBlurSrc);
            float4 _GodRayBlurSrc_TexelSize;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragBlur(Varyings input) : SV_Target
            {
                float2 step = BLUR_DIR * _GodRayBlurSrc_TexelSize.xy;
                half3 c = SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv, 0).rgb * 0.227027;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 3.230769, 0).rgb * 0.070270;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 3.230769, 0).rgb * 0.070270;
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 3: additive composite of the half-res shafts over the camera colour --
        Pass
        {
            Name "LargeBodyGodRaysComposite"
            Blend One One   // additive glow

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.0
            // Same fork as the fog's solve and meniscus (WaterUnderwaterFog.shader): Full masks
            // against the live displaced surface, Simple against the flat scalar its fog
            // waterline uses - each tier's mask is the same curve as its fog's, by construction.
            // WATER_FOG_CLASSIFY_RT (B3, 2026-09-02): the fog chain classified this exact
            // near-plane pair into _WaterFogClassifyRT earlier in the same graph; LargeBodyAtmospherePass
            // selects this variant on the command buffer (the fog pass's own mechanism) on the
            // frames it was recorded at full res, and the four analytic field evaluations per
            // pixel below become one LOAD. Analytic stays the fallback when the keyword is off.
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT
            #pragma multi_compile_fragment _ WATER_STRIP_SHORE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WaterExclusionMeshSpan.hlsl" // current-frame per-pixel carve silhouette
            // SurfaceSignedGap + WaterlineCoverage: the fog's per-pixel waterline curve,
            // READ-ONLY (the same contract the raymarch pass states for its include).
            #include "WaterWaterline.hlsl"

            TEXTURE2D(_LargeGodRayTex);
            SAMPLER(sampler_LargeGodRayTex);
            TEXTURE2D(_OceanSurfaceOwnership);
            SAMPLER(sampler_OceanSurfaceOwnership);
#ifdef WATER_FOG_CLASSIFY_RT
            // The fog chain's classification pair (x = chop-inverted gap, y = smooth gap) and
            // the fraction of camera resolution it was allocated at - read exactly as
            // WaterUnderwaterFog.shader's LoadWaterFogClassification does.
            TEXTURE2D(_WaterFogClassifyRT);
            float _WaterFogClassifyScale;
#endif

            // Declared locally rather than via their owning headers (WaterExclusion.hlsl is a
            // heavy include for three floats; precedent: WaterExclusionWall / WaterParticleFog
            // local declarations). Globals, so the values are the ones the fog read this frame.
            float _ExclusionCount;
            float _CameraDryVolume;
            float _UnderwaterSurfaceY;
            float _OceanSurfaceDepthValid;
            float _OceanSurfacePrepassScale;
            // The From Air knob, read here for the same reason the raymarch pass reads it: with it
            // at 0 no pane pixel can exist, and the mask below can therefore stay armed.
            float _LargeGodRayFromAir;

            #define GODRAY_COMPOSITE_DIR_EPSILON 1e-5

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            float2 OceanOwnershipSample(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_OceanSurfaceOwnership,
                                            sampler_OceanSurfaceOwnership, saturate(uv), 0).rg;
            }

            // The SHARED rendered-coverage composite (with the F7/F9 flank corroboration the
            // fog and meniscus already apply). This pass carried a private pre-F7 copy until
            // 2026-09-02, so an isolated half-res ownership texel could pull the shaft mask
            // below/above analytic exactly the way F7/F9 fixed for the fog.
            #include "WaterOceanRenderedCoverage.hlsl"

#ifdef WATER_FOG_CLASSIFY_RT
            float2 LoadWaterFogClassification(float2 uv)
            {
                float2 rtSize = _ScaledScreenParams.xy * _WaterFogClassifyScale;
                int2 pixelMax = max(int2(rtSize) - int2(1, 1), int2(0, 0));
                int2 pixel = clamp(int2(uv * rtSize), int2(0, 0), pixelMax);
                return LOAD_TEXTURE2D(_WaterFogClassifyRT, pixel).rg;
            }
#endif

            float PaneAwareCompositeMask(float2 screenUV, float waterCoverage, float3 nearWorld)
            {
                // A submerged pixel is already fully admitted and needs no carve query. This also
                // keeps the ordinary underwater field on the established zero-extra-work path.
                if (waterCoverage >= 1.0) return waterCoverage;

                bool paneViewPossible = _LargeGodRayFromAir > 0.0
                                      && (_ExclusionCount > 0.5 || _CameraDryVolume > 0.5);
                if (!paneViewPossible) return waterCoverage;

                // An older renderer without the exclusion prepass cannot identify the pane at
                // composite time. Preserve its former from-air behavior instead of deleting the
                // feature; migrated renderers take the exact per-pixel path below.
                if (_ExclusionPrepassValid < 0.5) return 1.0;

                float3 toNear = nearWorld - _WorldSpaceCameraPos;
                float3 rayDir = toNear / max(length(toNear), GODRAY_COMPOSITE_DIR_EPSILON);
                float2 carveSpan;
                float carveExit;
                if (!ExclusionPrepassExitDistance(screenUV, _WorldSpaceCameraPos, rayDir,
                                                  carveSpan, carveExit))
                    return waterCoverage;

                // Match the raymarch's pane rule: only an exclusion exit that is actually under
                // the current displaced surface may bypass the normal waterline mask. Open ocean
                // beside the pane therefore keeps current-frame coverage and clips half-res bleed.
                float3 carveExitWorld = _WorldSpaceCameraPos + rayDir * carveExit;
#ifdef WATER_FOG_SIMPLE
                bool carveExitInWater = carveExitWorld.y <= _UnderwaterSurfaceY;
#else
                bool carveExitInWater = SurfaceSignedGapChopInverted(carveExitWorld) <= 0.0;
#endif
                return carveExitInWater ? 1.0 : waterCoverage;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                // _LargeGodRayTex is the half-res shaft target, bound as a global by the raymarch pass.
                half4 shafts = SAMPLE_TEXTURE2D(_LargeGodRayTex, sampler_LargeGodRayTex, input.uv);

                // PER-PIXEL WATERLINE MASK (the KWS composite rule: the volumetric texture may be
                // half-res and temporally accumulated, but it is COMPOSITED behind a current-frame
                // per-pixel mask, so staleness can never cross the waterline). The half-res target
                // carries up to ~8 frames of history (0.88 blend); on a straddling frame in a
                // heavy sea, a trough exposing the lens zeroed this frame's march but the history
                // kept the shafts glowing over the AIR half of the screen for those frames - the
                // "god rays out of sync with the surface" seam, worst on a static camera where
                // nothing else moved. Masking here with the SAME coverage curve the fog's
                // ArmWeight feathers (WaterlineCoverage of the near-plane point's gap, over-cover
                // 0) pins the shafts to the fog's own waterline: where the fog says air, the
                // shafts are gone the SAME frame, whatever the history holds.
                //
                // Coverage FIRST, unconditionally, so fwidth sits in uniform control flow - the
                // fog's ArmWeight discipline, not a bet on the WGSL uniformity analysis accepting
                // a derivative behind a branch. SurfaceSignedGap is all explicit-LOD fetches, so
                // the unconditional call is derivative-safe by itself.
                float3 nearWorld = ComputeWorldSpacePosition(input.uv, UNITY_NEAR_CLIP_VALUE,
                                                             UNITY_MATRIX_I_VP);
                float gap;
                float gapSmooth;
#ifdef WATER_FOG_SIMPLE
                gap = nearWorld.y - _UnderwaterSurfaceY;
                gapSmooth = gap; // flat plane: already smooth
#elif defined(WATER_FOG_CLASSIFY_RT)
                // The fog chain's pair for this very pixel (B3): the near-plane point it
                // classified is this nearWorld - EXCEPT with the eye inside a dry carve, where
                // the fog classifies the ray's carve EXIT instead (WaterlineClassifyPoint's
                // portal move) while this mask must keep classifying the lens itself so
                // PaneAwareCompositeMask sees the same coverage it always did. A uniform
                // branch (camera state), so the derivative below stays in uniform flow.
                if (_CameraDryVolume < 0.5)
                {
                    float2 classifyGaps = LoadWaterFogClassification(input.uv);
                    gap = classifyGaps.x;
                    gapSmooth = classifyGaps.y;
                }
                else
                {
                    gap = SurfaceSignedGapChopInvertedPair(nearWorld, gapSmooth);
                }
#else
                // Slopes from the smooth vertical field, position from the inverted one -
                // same split as the fog's ArmWeight (see its note). ONE solve for both gaps
                // (SurfaceSignedGapChopInvertedPair, three evaluations instead of four), and
                // no solve at all when the camera is metres clear of its own surface - the
                // fog's and meniscus's WaterlineFarFromSurface skip, uniform across the screen
                // (it reads the height RT at the camera xz), so the derivative below stays
                // defined. Same test, same margin - see WaterWaterline.hlsl for why it is sound.
                float farGap;
                if (WaterlineFarFromSurface(nearWorld, _ExclusionCount > 0.5, farGap))
                {
                    gap = farGap;
                    gapSmooth = farGap;
                }
                else
                {
                    gap = SurfaceSignedGapChopInvertedPair(nearWorld, gapSmooth);
                }
#endif
                float2 gapGradient = float2(ddx(gapSmooth), ddy(gapSmooth));
                float coverage = WaterlineCoverage(gap,
                                                   abs(gapGradient.x) + abs(gapGradient.y), 0.0);
#ifndef WATER_FOG_SIMPLE
                if (_OceanSurfaceDepthValid > 0.5)
                {
                    float gradientLength = length(gapGradient);
                    float2 screenDirection = gradientLength > WATERLINE_GRADIENT_MIN
                                           ? gapGradient / gradientLength
                                           : float2(0.0, 1.0);
                    coverage = OceanRenderedCoverage(input.uv, coverage, screenDirection);
                }
#endif

                // The from-air pane is the one legal exception to the waterline mask. It used to
                // disable that mask SCREEN-WIDE whenever any exclusion existed, so bilinear
                // sampling of the half-resolution shaft target leaked pane light onto the open
                // surface. Re-identify the exception from the full-resolution exclusion prepass:
                // only this pixel's submerged carve exit may pass unmasked.
                float mask = PaneAwareCompositeMask(input.uv, coverage, nearWorld);
                shafts.rgb *= mask;
                return shafts;
            }
            ENDHLSL
        }
        // ---- Pass 4: the SIMPLE-tier raymarch (flat CPU-scalar waterline) --------------
        // Appended AFTER the composite so passes 0-3 keep their load-bearing indices
        // (LargeBodyAtmospherePass). Same march body as pass 0 with WATER_FOG_SIMPLE defined
        // here, exactly as the keyword used to define it - so the Simple march is byte-identical
        // to its former variant and the lamp fork (`&& !defined(WATER_FOG_SIMPLE)`) stays
        // compiled out without declaring WATER_GODRAY_POINT_LIGHTS at all.
        Pass
        {
            Name "LargeBodyGodRaysRaymarchSimple"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma target 4.0
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ WATER_STRIP_SHORE
            // WEBGPU TRANSLATOR GUARD (2026-08-15): with optimizations on, Unity's HLSL->GLSL
            // translator emits an undeclared u_xlat temp somewhere in this pass's Simple-tier
            // variants (glslang: undeclared identifier; all 8 WATER_FOG_SIMPLE variants failed
            // the web build while every Full variant compiled clean). The bug follows the
            // OPTIMIZER, not one construct - removing the aperiodic graph only moved the error
            // (line 1985 -> 854) - so optimization is disabled for webgpu ONLY. Every other API
            // keeps the optimized codegen, and the browser's own WGSL compiler still optimizes
            // downstream, so the runtime cost is bounded to this pass on web. Scoped to THIS
            // pass since 2026-09-02: the Full march (pass 0) was carrying the guard for a bug it
            // never had, and it is the pass that runs on the tiers where the march is heavy.
            #pragma skip_optimizations webgpu

            // The Simple fork, set the way the keyword used to set it - before every include, so
            // the shared headers see the same macro they always did.
            #define WATER_FOG_SIMPLE 1
            #include "LargeBodyGodRaysRaymarch.hlsl"
            ENDHLSL
        }
    }
}
