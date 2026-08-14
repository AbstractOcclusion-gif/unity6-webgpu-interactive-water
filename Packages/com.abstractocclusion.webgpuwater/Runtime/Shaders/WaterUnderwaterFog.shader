// WebGpuWater - real underwater fog (URP RenderGraph fullscreen; fallback budget reduced).
// Fogs only the part of each camera->scene ray that is actually IN the water, so it reads as a
// volume and a waterline falls out for free (a ray that never enters the water gets no fog):
//   * Ocean (unbounded): the below-surface half-space -> the fullscreen screen effect.
//   * Pond  (bounded):   the ray clipped to the pool box (pool space [-1,1] xz, [-1,0] y) via
//                        IntersectCube -> a finite fog volume you can circle around.
// Per-channel Beer-Lambert absorption + downwelling depth darkening, reusing the body's fog and
// depth globals. The per-pixel solve runs ONCE (C1, 2026-08-13): the "WaterFogSolve" MRT pass at
// the end of this file computes BOTH blend terms into two intermediate targets, and the two
// hardware-blend passes are now single pixel loads of those targets - the scene colour still
// never has to be copied and the blend states/order are unchanged:
//   0 Absorb:    scene *= pathTransmittance * depthAttenuation   (Blend Zero SrcColor, loads _WaterFogSolveAbsorb)
//   1 Inscatter: scene += fog * (1 - pathTransmittance) * depthAttenuation   (Blend One One, loads _WaterFogSolveInscatter)
// Driven by WaterUnderwaterFogFeature (gated on WaterVolume.UnderwaterFogActive: ocean = submerged
// only, pond = whenever Water Fog is on). U2: per-pixel wave-aware waterline - the surface crossing follows crests/troughs.
// U3: quality-tier Simple mode (_UnderwaterFogSimple, a uniform so every pixel takes the same branch):
// the closed-form flat waterline at _UnderwaterSurfaceY replaces the per-pixel crossing march - the
// budget path for WebGPU/mobile tiers. Same absorption/inscatter/darkening either way.
Shader "AbstractOcclusion/WebGpuWater/WaterUnderwaterFog"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        // Before ANY include that can pull WaterLargeWaves.hlsl. The fullscreen classify pass and
        // analytic fallback variants have sampler headroom, so they take the one-instruction
        // hardware bilinear for the ocean direction map instead of the four-Load fallback the
        // surface passes are stuck with (see OceanAperiodicDirectionMapBilinear). This is the
        // hottest classification read; beauty frames now pay it once in WaterFogClassify, while
        // debug/fallback frames retain the established direct solve.
        //
        // The APERIODIC SHAPE ITSELF STAYS. Compiling it out here (WATER_DISABLE_OCEAN_APERIODIC,
        // as LargeBodyCaustics.shader:38 does) was tried on 2026-08-12 and REVERTED: it desynced
        // the fog transition from the visible under/above-water boundary. The reasoning that it
        // would be invisible - that OceanRenderedCoverage multiplies the analytic term by
        // (1 - ownership.g), so the rendered prepass owns every pixel the sheet drew - was wrong in
        // practice. The analytic surface is consumed by more than that composite: the wet/dry ray
        // decision and the segment solve read it too, and the CPU's own crossing gates are derived
        // from the aperiodic field. Give the fog a differently-shaped ocean than the one on screen
        // and the transition happens at the wrong moment. DO NOT RE-TRY without also moving those
        // consumers onto the same field.
        #define WATER_APERIODIC_MAP_SAMPLER 1
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "WaterFog.hlsl"    // _WaterFogColor/_WaterExtinction/_WaterFogDensity, WaterPathLength, DownwellingAttenuation
        #include "WaterVolume.hlsl" // PoolToWorld / WorldToPool (+ the body's volume frame globals)
        #include "WaterShared.hlsl" // IntersectCube
        #include "WaterExclusion.hlsl"  // dry-interior volumes: ExclusionRayLength carves them out of the fog
        #include "WaterExclusionMeshSpan.hlsl" // MESH volumes: the prepass dry span (URP-core only)
        #include "WaterWaterline.hlsl"  // SurfaceHeightAtXZ / SurfaceSignedGap: the displaced wavy waterline (verbatim move)

        float _UnderwaterSurfaceY;
        float _UnderwaterUnbounded; // 1 = ocean half-space, 0 = clip to this body's box (pond)
        float _UnderwaterFogSimple; // 1 = tier Simple mode: flat waterline, skip the crossing march
        // Point/spot-light fog scattering (WATER_FOG_POINT_LIGHTS, inscatter pass only): the
        // published light list, the shared integral AND the _UnderwaterLightScatter knob all
        // live in WaterFog.hlsl (included above) - one home, shared with the surface's
        // from-above transmitted term so both views of the glow read identical lights.
        // 1 = the EYE sits inside a dry exclusion volume (PublishUnderwater, alongside
        // _CameraUnderwater - which now means "the eye is in WATER" and reads 0 in here). A uniform,
        // so the camera-height terms below stand down on a screen-coherent branch: in a sunken room
        // the eye's height against the outside waterline is not a measure of anything.
        float _CameraUnderwater;
        float _CameraDryVolume;
        // Ocean-surface eye-depth prepass (KWS-style rendered waterline): the DISPLACED surface's
        // linear eye depth per pixel, SIGNED by which side of the sheet is visible (+ = the ABOVE
        // sheet, seen from the air; - = the UNDER sheet, seen from below; 0 = no surface
        // rasterised there). Written by WaterSurface's "OceanSurfaceEyeDepth" pass via
        // WaterUnderwaterFogPass. When valid, the fog's crossing comes from this - the rendered
        // surface itself - instead of the bounded analytic march.
        TEXTURE2D(_OceanSurfaceEyeDepth);
        TEXTURE2D(_OceanSurfaceOwnership); SAMPLER(sampler_OceanSurfaceOwnership);
#ifdef WATER_FOG_CLASSIFY_RT
        // Full-resolution, point-loaded classification shared by the two fog composites and the
        // meniscus. RG32F keeps centimetre-scale precision across the full arming band without
        // filtering opposite signs together at the waterline.
        TEXTURE2D(_WaterFogClassifyRT);
#endif
        // C1 single-solve intermediates (2026-08-13): written by the "WaterFogSolve" MRT pass,
        // loaded by the absorb/inscatter blend passes. Alpha carries the debug-view flag (1 = a
        // fog debug view owns this pixel: absorb wipes, inscatter writes the false colour).
        TEXTURE2D(_WaterFogSolveAbsorb);
        TEXTURE2D(_WaterFogSolveInscatter);
        // Camera-local displaced height used only while producing the shared classification RT.
        // Unlike the 512 m / 2 m-texel march authority, this covers four metres at centimetre
        // texels and includes interactive ripples. G is raster coverage; an uncovered texel falls
        // back automatically to the established analytic chop inversion.
        TEXTURE2D(_WaterLensHeightRT); SAMPLER(sampler_WaterLensHeightRT);
        float4 _WaterLensHeightRTFrame; // xy centre, z half extent, w valid this frame
        float _OceanSurfaceDepthValid; // 1 = the prepass ran this frame (set by the fog pass)
        // Prepass resolution as a fraction of camera resolution (WaterUnderwaterFogPass publishes
        // it beside the validity flag). The RT is read with pixel LOADs, so every load coordinate
        // below multiplies through this - full-res was the Full tier's biggest constant GPU add.
        float _OceanSurfacePrepassScale;
        // Sun globals (published by WaterUniformPublisher) - not in this shader's include chain otherwise.
        // Needed so the underwater in-scatter can use the same lit WaterInscatterColor as the surface, for a
        // continuous colour crossing the waterline.
        float3 _LightDir;
        // _SunColor is declared by WaterFog.hlsl (included above) - the header that owns the in-scatter needing it.

        // Per-pixel wavy-waterline crossing search (U2). The camera->scene ray meets the DISPLACED surface
        // at a height that follows crests/troughs, so we bracket the FIRST sign change of
        // (rayY - SurfaceHeightAtXZ) with a constant-step coarse scan and refine by bisection. Constant
        // step/iteration counts keep this fullscreen pass cheap and allocation-free.
        // Eight bisections on the ONE 1.5 m march step resolve the fallback crossing to ~6 mm.
        // The rendered-surface prepass owns the normal ocean waterline exactly; this refinement is
        // paid only by pixels without a sheet sample (carves/off-mesh fallback), where the previous
        // 12-step sub-millimetre result added compiler/runtime pressure without visible benefit.
        #define UNDERWATER_CROSS_REFINE_ITERS 8
        // Crossing search: march the surface band with a FIXED WORLD STEP (constant, wave-scale resolution
        // so a crest is never skipped or aliased) up to a step cap; beyond the cap - the far horizon, where
        // waves are sub-pixel - fall back to the flat rest-plane waterline. Band = max(swell reach, surf
        // crest reach) + BAND_PAD metres (generous, to bracket crests + wind-wave chop). The step cap sets
        // how far the march reaches along the ray (STEP_METRES x MAX_STEPS): raised so the wider shore-surf
        // band is still bracketed on grazing up-looks, where the crossing sits many metres along the ray.
        #define UNDERWATER_CROSS_STEP_METRES 1.5
        #define UNDERWATER_CROSS_MAX_STEPS   16 // 24 m past band entry; then blend to the flat fallback
        // The rendered sheet now handles ordinary ocean pixels at every distance. This march is
        // the exceptional no-prepass path, so keeping the old 24/40-step reach made the compiler
        // and both fullscreen fog passes pay for distant precision that the fallback never owns.
        // The band half-width itself (swell reach vs surf-crest reach + chop pad) moved to
        // WaterWaterline.hlsl as SurfaceHeightBand(): the god-ray pass early-outs against the
        // SAME envelope before paying any surface fetches, so the number has exactly one home.
        // Fraction of the march reach where the wavy crossing starts fading to the flat fallback
        // (fully flat AT the reach), so the wavy->flat handover is a blend, not a seam.
        #define UNDERWATER_SEAM_BLEND_START  0.75
        // Below this sigma*L the transmittance-weighted mean-depth formula (see the downwelling
        // block in UnderwaterFog) is numerically degenerate (0/0) and its analytic limit L/2 is
        // used instead.
        #define DOWNWELL_MEAN_SIGMA_MIN 1e-3
        // The waterline coverage curve and its gradient floor are shared with the exclusion wall
        // (WaterWaterline.hlsl, WaterlineCoverage) so the two edges cannot land on different
        // pixels. Only the carve-specific over-cover lives here.
        // Floor for the eye -> near-plane direction (degenerate only if the near plane sat on the
        // eye), used when pushing a dry-carve pixel out to its exit face.
        #define CLASSIFY_DIR_EPSILON 1e-5
        // Vertical reach, in pixels, of the from-air corroboration test below. VERTICAL on purpose:
        // the artifact it rejects is a ONE-PIXEL-TALL, many-pixel-WIDE run along the horizon, so
        // horizontal neighbours are part of the same run and would corroborate it. Its vertical
        // neighbours are the only ones that can tell an above-water VIEW from a grazing SILHOUETTE.
        #define PREPASS_FROM_AIR_CORROBORATION_PIXELS 1
        // WATERLINE_CARVE_OVER_COVER_PIXELS moved to WaterWaterline.hlsl beside the curve it
        // shifts: the exclusion wall now mirrors this coverage to hand off against it, so the
        // number has to have exactly one home.

        int2 OceanSurfacePrepassPixel(float2 uv)
        {
            int2 pixelMax = max(int2(_ScaledScreenParams.xy * _OceanSurfacePrepassScale) - int2(1, 1),
                                int2(0, 0));
            return clamp(int2(uv * _ScaledScreenParams.xy * _OceanSurfacePrepassScale),
                         int2(0, 0), pixelMax);
        }

        float OceanSurfaceSignedAtUV(float2 uv)
        {
            return LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, OceanSurfacePrepassPixel(uv)).r;
        }

        float2 OceanOwnershipSample(float2 uv)
        {
            return SAMPLE_TEXTURE2D_LOD(_OceanSurfaceOwnership,
                                        sampler_OceanSurfaceOwnership, saturate(uv), 0).rg;
        }

        #include "WaterOceanRenderedCoverage.hlsl"

        // False-colour views for THIS pass (WaterFogDebug.hlsl), inert unless _WaterDebugMode
        // selects one. Included here rather than with the headers at the top on purpose: it reads
        // _CameraDryVolume and _UnderwaterFogSimple out of the uniform block directly above, so it
        // is a splinter of this pass, not a library - the same relationship WaterSurfaceFragStages
        // has with WaterSurface.shader.
        #include "WaterFogDebug.hlsl"

        struct Attributes { uint vertexID : SV_VertexID; };
        struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

        Varyings Vert(Attributes IN)
        {
            Varyings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
            o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
            return o;
        }

        float3 SceneWorldPos(float2 uv)
        {
            // Use the RESOLVED scene depth (_CameraDepthTexture) rather than the raw depth-stencil
            // attachment: on the WebGPU/Dawn backend a depth-stencil resource sampled as a colour
            // texture is stride-reinterpreted, which duplicated the depth image 2x/4x across the screen
            // and tiled the ocean fog. This is the same depth source the (correct) god-ray pass uses.
            // The wavy waterline no longer relies on post-transparent depth - it is computed analytically
            // in SurfaceHeightAtXZ below - so the pre-transparent opaque depth here is fine.
            float rawDepth = SampleSceneDepth(uv);
            return ComputeWorldSpacePosition(uv, rawDepth, UNITY_MATRIX_I_VP);
        }

        // SurfaceHeightAtXZ / SurfaceSignedGap moved VERBATIM to WaterWaterline.hlsl: the
        // exclusion wall clips at the same displaced waterline this pass integrates against.

        // ---- NOT COMPILED IN THE SIMPLE VARIANT (WATER_FOG_SIMPLE) --------------------------
        // Everything from here to OceanFlatPath is the per-pixel wavy-crossing machinery. It used to
        // be skipped by a UNIFORM BRANCH on _UnderwaterFogSimple, which is not the same thing: the
        // code stayed in the module, and a fragment shader's register allocation is sized to its
        // WORST path - so the whole marching module was setting the occupancy of every Simple-tier
        // pixel too, on a FULLSCREEN pass, twice per frame (absorb + inscatter). Fencing it with the
        // preprocessor is what actually removes it. Simple keeps exactly one path: OceanFlatPath.
        //
        // What the fence is worth has CHANGED, and the old note here ("a 40-step march whose every
        // step calls SurfaceHeightAtXZ, ~6 texture fetches") no longer describes this file. Since
        // F3 the march samples _WaterHeightRT (SurfaceSignedGapRT, one tap per step), so its 16
        // steps + 8 refine iterations are 26 cheap taps. On direct fallback variants the fence also
        // removes ArmWeight's three-evaluation analytic classification. Full-tier beauty variants
        // read that classification from WaterFogClassify instead. Register pressure remains the
        // reason Simple must be a preprocessor fence rather than a uniform branch. See
        // WaterWaterline.hlsl's header for the per-evaluation price list.
#ifndef WATER_FOG_SIMPLE
        // Refine a bracketed surface crossing [a(gapA), b(opposite sign)] to a world point on the surface.
        // 'gapA' is the signed gap at 'a' (passed in so it is not re-evaluated); bisection keeps the
        // sub-interval that still straddles the sign change. Constant iteration count -> constant cost.
        float3 RefineSurfaceCrossing(float3 a, float gapA, float3 b)
        {
            [loop]
            for (int r = 0; r < UNDERWATER_CROSS_REFINE_ITERS; r++)
            {
                float3 m = 0.5 * (a + b);
                float gapM = SurfaceSignedGap(m);
                if (gapA * gapM <= 0.0) { b = m; }
                else { a = m; gapA = gapM; }
            }
            return 0.5 * (a + b);
        }

        float3 RefineSurfaceCrossingRT(float3 a, float gapA, float3 b, float flatFallbackY)
        {
            [loop]
            for (int r = 0; r < UNDERWATER_CROSS_REFINE_ITERS; r++)
            {
                float3 m = 0.5 * (a + b);
                float gapM = SurfaceSignedGapRT(m, flatFallbackY);
                if (gapA * gapM <= 0.0) { b = m; }
                else { a = m; gapA = gapM; }
            }
            return 0.5 * (a + b);
        }

        // In-water length of the camera->scene ray against the WAVY ocean surface (per-pixel displaced
        // height), plus the deepest submerged Y and the surface height above that deepest point (the
        // depth-darkening reference). The crossing follows crests/troughs, so the fog waterline is a real
        // meniscus: no fog over a trough, fog under a crest.
        void OceanWavyPath(float3 sceneWorld, float3 cam, bool rayStartsWet,
                           out float pathLen, out float deepestY, out float surfaceRefY,
                           out float3 wetStart)
        {
            // All three returns below are this one path; the carve handoff in OceanPrepassPath
            // re-stamps the id AFTER its call, so a marched carve pixel still reads as the carve.
            WaterFogDebugBranch(WATER_FOG_BRANCH_WAVY_MARCH);
            // PERF (2026-08-03): the surface height at the CAMERA's xz is constant across the
            // frame, and the CPU already publishes it every frame (_UnderwaterSurfaceY, the same
            // value the Simple tier's whole waterline runs on). Evaluating the full displaced
            // field here (~6 texture fetches) per pixel, twice per frame (absorb + inscatter),
            // priced a per-frame constant. camSurf only feeds surfaceRefY / early-out references
            // (metres-scale, smooth), never the crossing itself - SurfaceSignedGap below still
            // marches the exact displaced surface.
            float camSurf = _UnderwaterSurfaceY;
            float sceneSurf = HeightRTSurfaceY(sceneWorld.xz, camSurf);
            bool sceneUnder = sceneWorld.y <= sceneSurf;
            wetStart = cam; // start of the in-water span ALONG the ray (exclusion subtraction origin)

            // Whole segment on one side of the surface: no crossing to search for.
            if (rayStartsWet && sceneUnder)
            {
                pathLen = length(sceneWorld - cam);
                deepestY = min(cam.y, sceneWorld.y);
                surfaceRefY = (cam.y <= sceneWorld.y) ? camSurf : sceneSurf;
                return;
            }
            if (!rayStartsWet && !sceneUnder)
            {
                pathLen = 0.0;
                deepestY = _VolumeCenter.y;
                surfaceRefY = camSurf;
                return;
            }

            // Mixed: the ray crosses the surface. March the SURFACE BAND (where the wavy surface can sit,
            // [restY +- band]) from the camera side with a FIXED WORLD STEP, so the coarse resolution is
            // constant and wave-scale regardless of ray length. A fractional whole-ray (or windowed) scan
            // made each step tens of metres on grazing/horizon rays, which SKIPPED near crests (fog drawn
            // ABOVE the waves) and aliased the crossing (dense-fog "lines"). Beyond the step cap - the far
            // horizon, where waves are sub-pixel - fall back to the flat rest-plane waterline.
            float3 ray = sceneWorld - cam;
            float rayLen = max(length(ray), 1e-4);
            float3 dir = ray / rayLen;
            float dySafe = ray.y + (ray.y >= 0.0 ? 1e-4 : -1e-4); // guard near-horizontal rays
            float restY = _VolumeCenter.y;
            // Every height the displaced surface can reach: swell reach vs surf-crest reach, plus
            // the chop pad. ONE definition, shared with the god-ray pass's above-surface early-out
            // - see SurfaceHeightBand in WaterWaterline.hlsl (moved from here, value unchanged).
            float band = SurfaceHeightBand();
            float tFlat = (restY - cam.y) / dySafe;              // flat rest-plane crossing (ray parameter)
            float tBand = band / max(abs(ray.y), 1e-4);          // half-band in ray-parameter units
            float startDist = saturate(tFlat - tBand) * rayLen;  // skip the deep water below the band
            float3 prev = cam + dir * startDist;
            float gapPrev = SurfaceSignedGapRT(prev, camSurf);
            // The FALLBACK line sits at the CAMERA-LOCAL live surface height (_UnderwaterSurfaceY,
            // the same level the whole Simple tier trusts), NOT the rest plane. The rest-plane
            // fallback re-created R2's collapse one tier deeper, INSIDE the marcher: an eye riding
            // a set crest above rest level saturates an up-ray's flat crossing to t = 0, so every
            // beyond-reach ray's span collapsed to zero while the mask demanded fog - the red
            // mask-vs-span dashes at the far junction (cyan WAVY_MARCH branch, 2026-08-10; the
            // stochastic group sets made eyes-above-rest far more common). camSurf >= a submerged
            // eye by construction, so the collapse cannot happen; at the true horizon the two
            // lines subtend under a pixel (waves are sub-pixel there), so the asymptote the cap
            // was written for is unchanged. tFlat above still centres the BAND search - the band
            // is defined around the rest plane, only the fallback line moves.
            float tFallback = (camSurf - cam.y) / dySafe;
            float3 hitFlat = cam + ray * saturate(tFallback); // fallback waterline
            float3 hit = hitFlat;
            // Where the march's reach ends: crossings found near it fade toward the flat fallback
            // (below), so the wavy->flat handover at the cap is a blend, not a visible seam line.
            float marchReach = startDist + UNDERWATER_CROSS_MAX_STEPS * UNDERWATER_CROSS_STEP_METRES;
            [loop]
            for (int s = 1; s <= UNDERWATER_CROSS_MAX_STEPS; s++)
            {
                float d = startDist + s * UNDERWATER_CROSS_STEP_METRES;
                if (d >= rayLen) break;                          // reached the scene end
                float3 p = cam + dir * d;
                float gap = SurfaceSignedGapRT(p, camSurf);
                if (gapPrev * gap <= 0.0)
                {
                    // Wavy crossing, faded toward the flat one over the march's last quarter: a hard
                    // switch at the step cap printed a seam where the fog waterline snapped from the
                    // waves to the rest plane at ~the march distance.
                    float seam = smoothstep(marchReach * UNDERWATER_SEAM_BLEND_START, marchReach, d);
                    hit = lerp(RefineSurfaceCrossingRT(prev, gapPrev, p, camSurf), hitFlat, seam);
                    break;
                }
                prev = p; gapPrev = gap;
            }

            float3 underEnd = sceneUnder ? sceneWorld : cam;
            pathLen = length(underEnd - hit);
            deepestY = min(hit.y, underEnd.y);
            surfaceRefY = sceneUnder ? sceneSurf : camSurf; // surface above the submerged endpoint
            wetStart = rayStartsWet ? cam : hit;            // wet span runs [start -> far end] along the ray
        }

        // Rendered-surface ocean path (the KWS trick): the crossing is the DISPLACED surface's own
        // eye depth at this pixel, so the fog waterline matches the drawn waves EXACTLY at any
        // distance - no march, no step cap, no flat-plane fallback mismatch at long range. Pixels
        // with no surface rasterised (looking straight down at the floor, or past the clipmap's
        // reach) fall back to the flat rest-plane crossing, exactly like the march's own far
        // fallback. Structure mirrors OceanWavyPath so the outputs stay drop-in compatible.
        void OceanPrepassPath(float2 uv, float3 sceneWorld, float3 cam, bool rayStartsWet,
                              out float pathLen, out float deepestY, out float surfaceRefY,
                              out float3 wetStart)
        {
            // Same PERF move as OceanWavyPath above: per-frame constant, published by the CPU.
            float camSurf = _UnderwaterSurfaceY;
            // DEFERRED (2026-08-03): sceneSurf / sceneUnder used to be computed HERE, before the
            // prepass load - ~6 texture fetches per pixel that every prepass-owned pixel (the
            // bulk of a submerged frame) then never read. Both now live at the analytic-authority
            // section below, the only consumer. No behaviour change: nothing between here and
            // there reads them.
            wetStart = cam;

            // RASTERIZED SURFACE FIRST (authority inversion - the Crest/KWS ranking). The
            // analytic early-outs used to run BEFORE this lookup, classifying the ray against the
            // OPAQUE scene point - and the drawn water is transparent, so at the distant waterline
            // that "scene" is the SKYBOX at the far plane. Any ray whose far-plane point dipped
            // below the analytic field took the both-under early-out and integrated fog over the
            // WHOLE ray to the skybox, painted OVER the drawn sheet: from underwater at grazing,
            // the visible waterline (the drawn crest silhouette) sits BELOW the analytic plane's
            // horizon on screen, so every pixel between the two got a straight fog edge overriding
            // the wavy line, and the underside read as sorted BEHIND the fog. Both references make
            // the rasterized surface depth BOUND the span (Crest: clamp(scene, backFace) -
            // frontFace) - nothing analytic can override it. Same here now: a prepass sample in
            // front of the scene IS the crossing; the analytic classification only speaks where
            // the sheet genuinely never rasterised.
            float3 ray = sceneWorld - cam;
            float rayLen = max(length(ray), 1e-4);
            float3 dir = ray / rayLen;
            // Prepass-space pixel: the RT is _OceanSurfacePrepassScale x camera resolution.
            // Clamped against the RT's own max coord (an out-of-range load is undefined, not 0,
            // and odd camera sizes floor-divide - uv ~1 could land one texel past the edge).
            int2 prepassPixelMax = max(int2(_ScaledScreenParams.xy * _OceanSurfacePrepassScale) - int2(1, 1),
                                      int2(0, 0));
            int2 prepassPixel = OceanSurfacePrepassPixel(uv);
            float surfaceSigned = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, prepassPixel).r;
            float surfaceEye = abs(surfaceSigned);
            // INSTRUMENT ONLY. Stamped here rather than re-loaded by the view: which of the two
            // coincident sheet twins won this pixel is exactly the thing under suspicion, and a
            // view that samples the RT again could disagree with the branch the pixel took.
            WaterFogDebugSheetSigned(surfaceSigned);

            // CORROBORATION, and why the raw sign is not enough on its own.
            //
            // The above and under sheets are COINCIDENT twins with OPPOSITE culling (see the
            // OceanSurfaceEyeDepth pass in WaterSurface.shader). At the horizon they are edge-on,
            // and there the two disagree about which triangles survive backface culling - so a
            // thin run of pixels along the sheet's grazing SILHOUETTE receives only the
            // ABOVE-facing twin. Those pixels then claimed the from-air ownership rule and had
            // their span forced to 0, i.e. no fog at all, while every neighbour around them was
            // priced analytically. That is the 1-px unfogged dashed line at the far waterline
            // (2026-07-28: confirmed by fog views 12, 13 and 10 agreeing - the run reads
            // PREPASS_AIR green against an ANALYTIC yellow field, with no sheet at all beside it).
            //
            // THE TEST. The rule's premise is that this pixel shows water SEEN FROM THE AIR, and
            // that the surface shader already absorbed its column. A genuine above-water view is a
            // large contiguous region - the straddling-frame band the rule was written for. A
            // grazing silhouette is one pixel tall with NO sheet above or below it. So require the
            // from-air reading to be corroborated vertically: uncorroborated, the pixel is a
            // silhouette and falls through to the submerged branch below, which prices it exactly
            // like its neighbours. The straddling band's INTERIOR cannot be affected - every pixel
            // in it has a from-air neighbour by construction.
            //
            // LOAD, not SAMPLE: no implicit derivatives, so this is valid before any branch, and
            // the coordinates are clamped because an out-of-range load is undefined, not zero.
            int prepassRowUp   = min(prepassPixel.y + PREPASS_FROM_AIR_CORROBORATION_PIXELS, prepassPixelMax.y);
            int prepassRowDown = max(prepassPixel.y - PREPASS_FROM_AIR_CORROBORATION_PIXELS, 0);
            float surfaceSignedUp   = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, int2(prepassPixel.x, prepassRowUp)).r;
            float surfaceSignedDown = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, int2(prepassPixel.x, prepassRowDown)).r;
            // BOTH neighbours, not either (2026-08-10). At the sheet's FAR raster silhouette the
            // above/under twins flip on depth precision per texel, and at the prepass' reduced
            // resolution a 2-texel run of above-twin wins CORROBORATES ITSELF under the old
            // either-neighbour test - span zeroed, no fog: the dark dashes along the distant
            // sheet edge (branch view green sprinkled over blue; beauty shows a dark line,
            // 2026-08-10). Requiring the from-air reading to be an INTERIOR pixel of a from-air
            // region (both vertical neighbours agree) rejects every silhouette run by
            // construction. A genuine above-water view keeps every pixel but its one edge row,
            // and that row falls through to the submerged pricing below, where the waterline
            // mask already owns the blend - a thick edge reads as water, a gap reads as a hole
            // (the same over-cover doctrine as the carve rim).
            bool fromAirCorroborated = surfaceSignedUp > 0.0 && surfaceSignedDown > 0.0;

            // Which face of the sheet this pixel shows - a RASTER fact, per pixel, from the same
            // draw the camera made. It replaces the eye's own waterline as the owner test below.
            //
            // THE RAW SIGN, deliberately. Corroboration is NOT folded in here: this condition also
            // guards the CARVE handoff below, and a carve is exactly where the prepass RT is full of
            // holes (its fragDepth discards inside every exclusion volume, mirroring WaterSurface's
            // carve discard). Gating the whole block therefore stopped carve pixels reaching
            // OceanWavyPath and broke the surface/exclusion stitch - a regression, 2026-07-28.
            // Corroboration belongs to the ONE decision it was introduced for: whether to zero the
            // span. It is applied at that return, below the carve check.
            bool sheetSeenFromAir = surfaceSigned > 0.0;
            // Eye depth is measured from the real camera, while `cam` is the visible near-plane
            // point for ocean fog. Convert to distance along the camera ray, then remove that
            // hidden camera-to-near segment so the integrated span and ArmWeight start together.
            float3 camForward = -UNITY_MATRIX_V[2].xyz;
            float cameraToStart = dot(cam - _WorldSpaceCameraPos, dir);
            float hitDist = surfaceEye / max(dot(dir, camForward), 1e-4) - cameraToStart;
            float3 hit;
            if (surfaceEye > 0.0 && hitDist < rayLen)
            {
                // The pixel's water STARTS at the rendered surface seen from the AIR side: the
                // sheet's own from-above shading (its transmittance + WaterDepthClarity) already
                // absorbed everything behind it, so fogging [surface -> scene] again here painted
                // a flat second fog over the drawn waves - the "plain band" at water level, and
                // the same band from inside a dry room above sea level. Crest and KWS never let
                // the volume pass touch a from-above water pixel - the surface shader owns that
                // view. Pixels with the sheet NEAR-CLIPPED (surfaceEye 0, the lens-in-water strip
                // at the bottom of a straddling frame) keep full fog below: that strip is exactly
                // what both references hand to the volume pass.
                //
                // The owner test is the PREPASS SIGN, not the eye's waterline. Those two disagree
                // exactly at the crossing: with the near plane dipped under a wave the mask reads
                // "wet" for a band of pixels that still show the ABOVE sheet, and this pass then
                // washed its scatter colour over a surface the sheet had already shaded from air -
                // fog "reflected onto" the water, and a span that looked like it had skipped the
                // nearest crossing. A per-pixel raster fact cannot make that mistake.
                if (sheetSeenFromAir)
                {
                    // SCOPED TO WHERE ITS PREMISE HOLDS. The rule above assumes the surface shader
                    // already absorbed THIS ray's water column when it shaded the sheet. That is
                    // false when part of the column BEYOND the sheet is a dry exclusion volume: the
                    // sheet's shading has no idea a room is carved back there, so suppressing the
                    // span here leaves it painted by nobody at all.
                    //
                    // The symptom, and it is a nasty one because the fog looks innocent: stand near
                    // a carve with a wave crest between you and it. The CREST is the nearest sheet,
                    // so it wins the depth prepass and this branch claims the pixel - for the WHOLE
                    // ray, including the hole behind it. Bert: "the system picks the closest water
                    // point to activate deactivate fog. In this case math are wrong."
                    //
                    // Such pixels go to the SAME carve path the no-prepass case below uses, rather
                    // than to a second span rule invented here: one validated behaviour, and mode 10
                    // then reads CARVE_MARCH over the hole instead of PREPASS_AIR.
                    float3 sheetHit = cam + dir * hitDist;
                    float beyondLen = rayLen - hitDist;
                    float carveBeyond = ExclusionRayLength(sheetHit, dir, beyondLen);
                    if (_ExclusionMeshCount > 0.5)
                        carveBeyond += ExclusionMeshRayLength(uv, sheetHit, dir, beyondLen);
                    if (carveBeyond > 0.0)
                    {
                        OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                                      wetStart);
                        // After the call, which stamps its own id on entry - this is a carve pixel.
                        WaterFogDebugBranch(WATER_FOG_BRANCH_CARVE_MARCH);
                        return;
                    }
                    // THE MIRROR CASE, and the one the rule above cannot see: the carve is not BEYOND
                    // the sheet, it is BETWEEN THE EYE AND IT. Looking out through a carve rim at water
                    // level, the sheet that wins this pixel is the one OUTSIDE the carve, seen nearly
                    // edge-on - so which of the two coincident twins survives backface culling is
                    // settled by depth precision, per pixel, and wherever the ABOVE twin wins, this rule
                    // zeroed a span the waterline mask demanded. That is the carve-rim seam: the same
                    // coin toss as the horizon line, but ~5 px thick instead of 1, which is exactly why
                    // the vertical corroboration below cannot reject it - the run corroborates itself.
                    //
                    // The premise is falsifiable per pixel, from RASTER: if the ray LEAVES the carve
                    // BELOW the displaced surface, it is already in water at that point, so whatever
                    // sheet it meets afterwards is not water seen from the air - whichever twin drew it.
                    //
                    // Note what this does NOT read: not the eye's near plane (rayStartsWet / armWeight),
                    // not the camera's state (_CameraDryVolume), not the neighbouring pixels. All three
                    // were proposed and refuted, because in every scalar the shader had, the failing
                    // case and the intended case were identical. This one is a fact about the CARVE
                    // BOUNDARY at this pixel, rasterised - which only became available for Box and
                    // Sphere volumes when WaterExclusionDepthPass was widened past the Mesh tier.
                    //
                    // Same destination as the carve-beyond case above (OceanWavyPath, ONE validated
                    // crossing search) rather than a second span rule invented here.
                    float2 carveRawSpan;
                    float carveExitDist;
                    if (ExclusionPrepassExitDistance(uv, cam, dir, carveRawSpan, carveExitDist)
                        && carveExitDist < hitDist
                        && SurfaceSignedGapRT(cam + dir * carveExitDist, camSurf) <= 0.0)
                    {
                        OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                                      wetStart);
                        // After the call, which stamps its own id on entry - this is a carve pixel.
                        WaterFogDebugBranch(WATER_FOG_BRANCH_CARVE_MARCH);
                        return;
                    }

                    // ONLY HERE. Suppressing the span outright needs the premise that this pixel is
                    // genuinely water seen FROM THE AIR - and, per the exception above, that the
                    // ray reached it through AIR. A real above-water view is a large contiguous
                    // region - the straddling-frame band this rule was written for, every pixel of
                    // which has a from-air neighbour. A grazing SILHOUETTE of the coincident sheet
                    // twins is one pixel tall with no sheet above or below it, and zeroing those left
                    // the unfogged dashed line at the far waterline. Uncorroborated, fall THROUGH to
                    // the submerged branch below and be priced like the neighbours.
                    if (fromAirCorroborated)
                    {
                        WaterFogDebugBranch(WATER_FOG_BRANCH_PREPASS_AIR);
                        pathLen = 0.0;
                        deepestY = _VolumeCenter.y;
                        surfaceRefY = camSurf;
                        return;
                    }
                }
                // Submerged eye, drawn surface in front: the visible water column ends AT
                // the sheet, so the span is [eye -> hit] no matter what the analytic field says
                // about the opaque scene point behind it. This intentionally also captures rays
                // the old both-under early-out claimed: what is drawn past the exit is the
                // sheet's own reflection/refraction imagery - the fog cannot see it and must
                // not price it.
                WaterFogDebugBranch(WATER_FOG_BRANCH_PREPASS_WET);
                float wetDist = min(surfaceEye / max(dot(dir, camForward), 1e-4) - cameraToStart,
                                    rayLen);
                hit = cam + dir * wetDist;
                pathLen = wetDist;
                deepestY = min(cam.y, hit.y);
                surfaceRefY = camSurf;
                return;
            }

            // NO rasterized surface at this pixel: the analytic classification is the right
            // authority (deep murk, floor views, past the clipmap - places the sheet never
            // drew, where the skybox cannot masquerade as a waterline). Ordered AFTER the
            // prepass on purpose - see the authority note above.
            WaterFogDebugBranch(WATER_FOG_BRANCH_ANALYTIC);
            float sceneSurf = HeightRTSurfaceY(sceneWorld.xz, camSurf); // deferred from the top - see note there
            bool sceneUnder = sceneWorld.y <= sceneSurf;
            if (rayStartsWet && sceneUnder)
            {
                pathLen = length(sceneWorld - cam);
                deepestY = min(cam.y, sceneWorld.y);
                surfaceRefY = (cam.y <= sceneWorld.y) ? camSurf : sceneSurf;
                return;
            }
            if (!rayStartsWet && !sceneUnder)
            {
                pathLen = 0.0;
                deepestY = _VolumeCenter.y;
                surfaceRefY = camSurf;
                return;
            }

            // Mixed ray with NO prepass sample -> the SAME validated crossing search the
            // no-prepass tier runs (OceanWavyPath: fixed 1.5 m march + refine, self-blending to
            // the flat rest plane past its reach - UNDERWATER_SEAM_BLEND_START).
            //
            // This block used to keep a closed-form FLAT rest-plane crossing for open water
            // (WATER_FOG_BRANCH_FLAT_FALLBACK - unreachable; its debug id is deleted), on the premise that "no sheet
            // rasterised" means the far horizon or a straight-down look. PARTIAL SUBMERSION
            // breaks that premise: the sheet is NEAR-CLIPPED around the lens, so the crossing
            // band itself has no prepass sample - and there the flat plane sits a whole swell
            // amplitude from the displaced surface (the "linear fog" edge). Worse than a
            // misplaced line: with the eye riding a crest ABOVE the rest plane, an up-ray's flat
            // crossing saturates to t = 0 and the span collapses to ZERO while the waterline
            // mask demands full fog - the unfogged band popping at the crossing (Bert's
            // mask-vs-span RED / branch-view yellow->red, 2026-07-31). The marcher prices
            // exactly this case, its far end still degrades to the flat line, and the cost is
            // confined to the no-prepass set: the near-clip strip, carve holes and sub-pixel
            // silhouettes - every other water pixel is owned by the prepass above.
            //
            // Deliberately NOT a bespoke refine here: an earlier attempt bisected a +-band
            // bracket five times, quantising the crossing to ~30 cm and printing steps.
            // OceanWavyPath's fixed 1.5 m march + refine is the validated resolution.
            {
                // Carve test kept for the DEBUG STAMP only (the pricing is the same marcher
                // either way now): a carve pixel must keep reading as the carve in the views.
                float dySafe = ray.y + (ray.y >= 0.0 ? 1e-4 : -1e-4);
                float tFlat = saturate((_VolumeCenter.y - cam.y) / dySafe);
                bool overCarve = _ExclusionCount > 0.5
                              && (_CameraDryVolume > 0.5 || InsideExclusion(cam + ray * tFlat));
                OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                              wetStart);
                // AFTER the call, which stamps WAVY_MARCH on entry.
                if (overCarve) WaterFogDebugBranch(WATER_FOG_BRANCH_CARVE_MARCH);
            }
        }
#endif // !WATER_FOG_SIMPLE

        // Simple-mode ocean path (tier budget path): the closed-form in-water span against the FLAT
        // waterline at _UnderwaterSurfaceY - the CPU-published, wave-aware surface height at the
        // CAMERA's xz, the same height that arms the submerge gate, so the fog and the gate can never
        // disagree at the eye (and the waterline still rides the local swell as the camera bobs).
        // No march, no per-pixel wave evaluation: a handful of ALU ops replaces up to
        // UNDERWATER_CROSS_MAX_STEPS surface evaluations per pixel.
        void OceanFlatPath(float3 sceneWorld, float3 cam,
                           out float pathLen, out float deepestY, out float surfaceRefY,
                           out float3 wetStart)
        {
            WaterFogDebugBranch(WATER_FOG_BRANCH_FLAT_SIMPLE);
            float level = _UnderwaterSurfaceY;
            pathLen = WaterPathLength(sceneWorld, cam, level);
            // min against 'level' makes an in-air endpoint contribute its crossing at the waterline,
            // so the deepest submerged point is exact in every camera-above/below combination.
            deepestY = min(level, min(cam.y, sceneWorld.y));
            surfaceRefY = level;
            // Wet-span start along the ray: the camera when submerged, else the flat-waterline
            // crossing (closed form, mirroring WaterPathLength's clip against 'level').
            wetStart = cam;
            if (cam.y > level)
            {
                float3 ray = sceneWorld - cam;
                float dySafe = ray.y + (ray.y >= 0.0 ? 1e-4 : -1e-4); // guard near-horizontal rays
                wetStart = cam + ray * saturate((level - cam.y) / dySafe);
            }
        }

#ifndef WATER_FOG_SIMPLE
        // Pull a pond segment's ENTRY down to the wavy surface when it starts in AIR: the pool box top is
        // the flat rest plane (pool y = 0), so a wave trough sitting below it would otherwise fog the air
        // in the trough. Returns the surface crossing when the entry is above water; else keeps the entry.
        float3 ClampEntryToSurface(float3 enterWorld, float3 exitWorld)
        {
            float gapEnter = SurfaceSignedGap(enterWorld);
            if (gapEnter <= 0.0) return enterWorld;                   // entry already underwater: keep it
            if (SurfaceSignedGap(exitWorld) > 0.0) return exitWorld;  // whole segment in air: no water (len 0)
            return RefineSurfaceCrossing(enterWorld, gapEnter, exitWorld);
        }

        // Mirror clamp for the raised lid (see the pond branch): pull a segment's EXIT down to the
        // wavy crossing when it ends in AIR - an up-look from a submerged eye exits through the
        // raised lid, which can sit above the true surface, and without this the span gained an
        // air tail the flat lid never had. Entry-side air is already resolved by
        // ClampEntryToSurface before this runs, so an air exit brackets a crossing against the wet
        // entry (degenerate all-air segments arrive with entry == exit and stay length 0).
        float3 ClampExitToSurface(float3 enterWorld, float3 exitWorld)
        {
            float gapExit = SurfaceSignedGap(exitWorld);
            if (gapExit <= 0.0) return exitWorld; // exit already underwater: keep it
            return RefineSurfaceCrossing(exitWorld, gapExit, enterWorld);
        }
#endif // !WATER_FOG_SIMPLE

        // World-space length of the in-water part of the camera->scene ray, the deepest submerged point's
        // world Y (for downwelling), and the displaced surface height above it (the depth reference).
        // pathLen 0 = this pixel's ray never enters the water.
        void UnderwaterSegment(float2 uv, float3 sceneWorld, bool rayStartsWet, out float pathLen,
                               out float deepestY, out float surfaceRefY, out float3 wetStart)
        {
            float3 cam = _WorldSpaceCameraPos;

            if (_UnderwaterUnbounded > 0.5)
            {
                // The image begins at the camera near plane, not at the hidden projection origin.
                // ArmWeight already classifies this exact point, including perspective/FOV and
                // near-clip distance. Starting the ocean integral there keeps its hard span choice
                // and soft reveal mask on the same geometric boundary during partial submersion.
                cam = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP);
                // Ocean: the below-surface span. Simple is a COMPILE-TIME fork, not a uniform
                // branch, so the variant has no call site into the march at all and the crossing
                // machinery above is absent from its module. The remaining runtime gate
                // (_OceanSurfaceDepthValid) is still a uniform, so it stays screen-coherent.
#ifdef WATER_FOG_SIMPLE
                OceanFlatPath(sceneWorld, cam, pathLen, deepestY, surfaceRefY, wetStart);
#else
                if (_OceanSurfaceDepthValid > 0.5)
                    OceanPrepassPath(uv, sceneWorld, cam, rayStartsWet, pathLen, deepestY,
                                     surfaceRefY, wetStart);
                else
                    OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                                  wetStart);
#endif
                return;
            }

            // Pond: clip the ray to the pool water box in pool space ([-1,1] xz, [-1,0] y). Working in
            // pool space lets one IntersectCube handle the surface top AND the walls/floor at once.
            WaterFogDebugBranch(WATER_FOG_BRANCH_POND);
            float3 originPool = WorldToPool(cam);
            float3 scenePool = WorldToPool(sceneWorld);
            float3 rayPool = scenePool - originPool;
            float sceneT = length(rayPool);
            rayPool /= max(sceneT, 1e-5);

#ifndef WATER_FOG_SIMPLE
            // Crest coverage (the "straight fog line at the rest level" fix): the water box top is
            // the FLAT rest plane and ClampEntryToSurface only ever pulls an air entry DOWN - so
            // water ABOVE rest (wind-wave crests) held no fog, and from a submerged or straddling
            // eye the fog's upper edge read as a straight line while the drawn waterline curved.
            // Raise the clip LID by the surface height band - the same one-home envelope the
            // crossing march and the god-ray ceiling already trust - converted through the volume
            // frame itself (the rest level maps to pool y = 0, so no new uniform). Correctness
            // stays the CLAMPS': entries that start in air bisect down to the true wavy surface,
            // all-air segments collapse to length 0, and ClampExitToSurface below closes the
            // up-look mirror case the raise opens.
            float3 poolBoxMax = POOL_WATER_BOX_MAX;
            poolBoxMax.y = WorldToPool(_VolumeCenter + float3(0.0, SurfaceHeightBand(), 0.0)).y;
            float2 hit = IntersectCube(originPool, rayPool, POOL_WATER_BOX_MIN, poolBoxMax);
#else
            // Simple tier: the flat rest lid IS its waterline by definition - nothing to raise.
            float2 hit = IntersectCube(originPool, rayPool, POOL_WATER_BOX_MIN, POOL_WATER_BOX_MAX);
#endif
            float tEnter = max(hit.x, 0.0);
            float tExit = min(hit.y, sceneT); // never fog past the scene surface
            if (tExit <= tEnter)
            {
                pathLen = 0.0;
                deepestY = _UnderwaterSurfaceY;
                surfaceRefY = _UnderwaterSurfaceY;
                wetStart = cam;
                return;
            }

            // Convert the entry/exit back to world for a correct length (pool axes are scaled by extent),
            // then pull the entry down to the wavy surface so a trough no longer fogs the air above it.
            // Simple mode keeps the box-top entry as-is: the pool top (pool y = 0) IS the flat
            // waterline, so the clamp (which evaluates the wavy surface) is skipped along with the
            // wavy downwelling reference - _VolumeCenter.y is the same rest plane the box top maps to.
            float3 enterWorld = PoolToWorld(originPool + rayPool * tEnter);
            float3 exitWorld = PoolToWorld(originPool + rayPool * tExit);
#ifdef WATER_FOG_SIMPLE
            // The pool top (pool y = 0) IS the flat waterline, so there is nothing to clamp to and
            // _VolumeCenter.y is the same rest plane the box top maps to.
            pathLen = length(exitWorld - enterWorld);
            deepestY = min(enterWorld.y, exitWorld.y);
            surfaceRefY = _VolumeCenter.y;
#else
            enterWorld = ClampEntryToSurface(enterWorld, exitWorld);
            exitWorld = ClampExitToSurface(enterWorld, exitWorld);

            pathLen = length(exitWorld - enterWorld);
            deepestY = min(enterWorld.y, exitWorld.y);
            surfaceRefY = SurfaceHeightAtXZ(enterWorld.xz); // wavy surface above the entry, for downwelling
#endif
            wetStart = enterWorld;
        }

        // The shadow-column terms (EXCLUSION_SHADOW_FLOOR, the analytic span sun visibility)
        // live in WaterExclusion.hlsl: the exclusion wall's above-water fog reconstruction
        // shares them, so both views of the carve shade identically.

        // Per-pixel waterline mask - the one thing BOTH references do and we did not.
        // Crest classifies every pixel by testing its NEAR-CLIP-PLANE world position against the
        // displaced surface (Volume/Mask.compute: `position.y <= height ? -1 : 1`), and its
        // fullscreen underwater pass then DISCARDS every above-surface pixel
        // (Volume/Underwater.hlsl: `if (mask > CREST_MASK_BELOW_SURFACE) discard;`). KWS is the
        // same shape (KWS_Underwater.shader: `alpha = ... waterMask > 0.5 ? 1 : 0; if (alpha == 0)
        // discard;`). NEITHER applies any camera-height ramp to the effect.
        //
        // WHY THAT MATTERS FOR ARMING: it is precisely why neither of them pops when its CPU gate
        // flips. The gate is a SUPERSET of this per-pixel coverage, so on the frame the pass first
        // runs, the set of pixels this mask lets through is still empty - submitting the pass
        // changes nothing on screen. A hard bool cannot produce a hard edge. Our arm band
        // (FogArmBandMeters) now has that same property for free.
        //
        // WHAT THIS REPLACES: a camera-height arm fade - a 0.25 m ramp on cam.y against the
        // surface at the EYE's xz, plus a "lens exemption" keyed on how near this ray's water
        // began. Two faults. It dimmed the WHOLE SCREEN together as the camera neared the surface
        // (one global number, so every pixel moved at once - the transition band reading as weird);
        // and the lens exemption held steeply-down-looking rays at FULL fog right up to the frame
        // the gate toggled off, because their crossing is only ~0.5 m away - the pop as the water
        // reached the camera.
        //
        // Above-water pixels lose nothing by being masked out: the surface shader already applies
        // the water column's absorption for a view from above (its own transmittance +
        // WaterDepthClarity), exactly as Crest's Fragment.hlsl and KWS's fragWater do. The
        // fullscreen pass painting them was double-counting that.
        //
        // FEATHERED over one pixel instead of a hard discard: both references hide their hard edge
        // under a far wider meniscus than ours (Crest ~11% of screen height on the air side, KWS a
        // 40-80 px blurred band; ours defaults to 5 px), so our boundary itself has to be clean.
        // The meniscus pass evaluates the IDENTICAL gap, so the line it draws and this edge are the
        // same curve by construction - there is no seam between them to hide.
        //
        // Derivative safety: every early-out below is on a UNIFORM global (_UnderwaterUnbounded,
        // _CameraDryVolume, _UnderwaterFogSimple), and this is called before any per-pixel
        // marching, so fwidth sits in uniform control flow.

        // The world point this pixel's coverage is decided at. Normally the pixel's own
        // NEAR-CLIP-PLANE position - Crest's mask, verbatim.
        //
        // Inside a dry carve that point is useless: the lens sits in AIR below sea level, so its
        // waterline says nothing about the water it is looking at through the pane. The previous
        // answer was to give up and return full coverage, citing Crest disabling its camera-height
        // heuristics under a portal. That was a misreading. Crest disables the height RAMPS; it
        // never disables the MASK - it MOVES it onto the portal geometry, classifying the portal
        // WALL's world position against the water line (Portals.hlsl Fragment: `positionWS.y <=
        // height ? -1 : 1`, fed by a height field fitted to the portal bounds).
        //
        // Same move here, analytically: push the ray to where it LEAVES the carve and classify
        // THAT point. It is the same boundary point WaterExclusionWall shades and classifies
        // against the same SurfaceHeightAtXZ, so the fog's waterline and the wall's waterline are
        // ONE curve by construction rather than two curves that have to agree.
        //
        // MESH volumes are skipped by the analytic push (their exact exit needs the back-face
        // prepass), and a near-plane point that no analytic volume contains pushes by 0 - both
        // fall back to the near-plane point, which is the pre-carve behaviour.
        // 'pushDist' reports how far the point was moved out to a carve exit, in world metres.
        // 0 means the push found nothing to push out of, so the classification stayed on the near
        // plane - which is the CORRECT answer in the open and a silent FAILURE inside a carve
        // (the near-plane point is then dry air below sea level, saying nothing about the water
        // being looked at). Returned rather than re-derived so the debug view reads the number
        // this function actually used.
        float3 WaterlineClassifyPoint(float2 uv, out float pushDist)
        {
            pushDist = 0.0;
            float3 nearWorld = ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE,
                                                         UNITY_MATRIX_I_VP);
            if (_CameraDryVolume < 0.5) return nearWorld; // uniform: the eye is not in a carve
            float3 toNear = nearWorld - _WorldSpaceCameraPos;
            float3 rayDir = toNear / max(length(toNear), CLASSIFY_DIR_EPSILON);
            pushDist = ExclusionPushToExit(nearWorld, rayDir, 0.0, _ProjectionParams.z);
            return nearWorld + rayDir * pushDist;
        }

        // Returns the coverage weight AND the signed gap it was derived from, so the caller can
        // take the hard "does this ray start in water" decision from the SAME number the soft
        // weight feathers - the two can then never disagree about where the line is.
        // Entry face of the top of the pool water box, in pool-space units: an entry with
        // pool y at or above -epsilon came in THROUGH THE RENDERED SHEET, not a wall/floor.
        #define POND_TOP_FACE_EPSILON 1e-3

#if !defined(WATER_FOG_SIMPLE) && !defined(WATER_FOG_CLASSIFY_RT)
        #define WATER_LENS_HEIGHT_VALID_MIN 0.999
        #define WATER_LENS_HEIGHT_EXTENT_MIN 1e-4

        bool TryLensHeightClassification(float3 classifyPoint, out float classifyGap)
        {
            float2 uv = (classifyPoint.xz - _WaterLensHeightRTFrame.xy)
                      / (max(_WaterLensHeightRTFrame.z, WATER_LENS_HEIGHT_EXTENT_MIN) * 2.0)
                      + 0.5;
            bool inside = all(uv >= 0.0) && all(uv <= 1.0);
            float2 heightCoverage = SAMPLE_TEXTURE2D_LOD(
                _WaterLensHeightRT, sampler_WaterLensHeightRT, saturate(uv), 0).rg;
            bool covered = inside && heightCoverage.y >= WATER_LENS_HEIGHT_VALID_MIN;
            classifyGap = classifyPoint.y - (_VolumeCenter.y + heightCoverage.x);
            return covered;
        }

        void EvaluateWaterlineClassificationGaps(float3 classifyPoint, out float classifyGap,
                                                 out float gapSmooth)
        {
            // One height-RT tap replaces the analytic solve whenever the camera is uniformly clear
            // of the surface. Otherwise the inversion's first iteration supplies the smooth
            // vertical read while the completed solve supplies the true chop-inverted position.
            float farGap;
            if (WaterlineFarFromSurface(classifyPoint, farGap))
            {
                classifyGap = farGap;
                gapSmooth = farGap;
                return;
            }

            // Crest-style near-lens authority: rasterise the real displaced surface once on a
            // dense local grid, then replace the three-evaluation chop inversion with one sample.
            // The smooth gap deliberately remains ONE analytic vertical evaluation for ddx/ddy:
            // the shipped invariant is position from the inverted surface, feather width from the
            // C1 vertical field. Only classifyGap may select per pixel; gapSmooth follows uniform
            // control flow across every quad. Dry-carve rays classify their pushed exit point and
            // therefore retain the exact analytic pair.
            if (_WaterLensHeightRTFrame.w > 0.5 && _CameraDryVolume < 0.5)
            {
                gapSmooth = SurfaceSignedGap(classifyPoint);
                if (!TryLensHeightClassification(classifyPoint, classifyGap))
                    classifyGap = SurfaceSignedGapChopInverted(classifyPoint);
                return;
            }

            classifyGap = SurfaceSignedGapChopInvertedPair(classifyPoint, gapSmooth);
        }
#endif

#ifdef WATER_FOG_CLASSIFY_RT
        float2 LoadWaterFogClassification(float2 uv)
        {
            int2 pixelMax = max(int2(_ScaledScreenParams.xy) - int2(1, 1), int2(0, 0));
            int2 pixel = clamp(int2(uv * _ScaledScreenParams.xy), int2(0, 0), pixelMax);
            return LOAD_TEXTURE2D(_WaterFogClassifyRT, pixel).rg;
        }
#endif

        float ArmWeight(float2 uv, out float classifyPushDist)
        {
            float3 classifyPoint = WaterlineClassifyPoint(uv, classifyPushDist);
            // classifyGap pruned from the signature (2026-08-13): the caller reads the WEIGHT,
            // never the raw gap, ever since the carve-waterline fix - and that fix is
            // play-confirmed and committed (2026-07-28), so the parked refactor was unblocked.
            // gapSmooth is declared with classifyGap because ONE solve now produces both - see
            // the note on the smooth-vs-inverted split below, and SurfaceSignedGapChopInvertedPair.
            float classifyGap;
            float gapSmooth;
#ifdef WATER_FOG_SIMPLE
            classifyGap = classifyPoint.y - _UnderwaterSurfaceY;
            gapSmooth = classifyGap; // flat plane: already smooth
#elif defined(WATER_FOG_CLASSIFY_RT)
            float2 classifyGaps = LoadWaterFogClassification(uv);
            classifyGap = classifyGaps.x;
            gapSmooth = classifyGaps.y;
#else
            EvaluateWaterlineClassificationGaps(classifyPoint, classifyGap, gapSmooth);
#endif
            float overCoverPixels = (_CameraDryVolume > 0.5) ? WATERLINE_CARVE_OVER_COVER_PIXELS
                                                             : 0.0;
            // Slopes from the SMOOTH vertical read, position from the accurate one. The
            // chop-inverted gap is a fixed-point search that can converge to DIFFERENT wave
            // sources on adjacent pixels near pinched crests, so its screen derivatives
            // spike pixel-to-pixel and the feather width / search direction fizz, reshuffling
            // every frame. The vertical field is C1 by construction and its slope is the
            // right magnitude for a pixel metric, so the feather stays calm while the line
            // itself stays on the inverted (true) waterline.
            //
            // Both gaps come out of ONE solve as of 2026-08-11: the inversion's first iteration
            // runs at the query xz, so it computes the vertical read on its way to the inverted
            // one. The second full evaluation this used to make was a quarter of the whole
            // classification cost and returned a number the first already had.
            //
            // Derivative taken BEFORE the per-pixel top-face select below: fwidth needs its
            // neighbours on the same code path, and the bounded/unbounded split alone is a
            // uniform global so the gap is now computed for BOTH body kinds unconditionally.
            float2 gapGradient = float2(ddx(gapSmooth), ddy(gapSmooth));
            float coverage = WaterlineCoverage(classifyGap,
                                               abs(gapGradient.x) + abs(gapGradient.y),
                                               overCoverPixels);
            // On a Full-tier ocean the visible displaced mesh owns the classification wherever it
            // rasterised. Horizontal FFT/Gerstner chop makes that surface non-single-valued, so an
            // independent height query at the final world XZ can legitimately select the opposite
            // medium for a frame. The signed prepass is the exact surface draw: positive is the
            // air-facing sheet (the surface shader owns the column), negative is the underside
            // (the volume owns it). Zero is a real hole/near-clip/exclusion and deliberately falls
            // back to the analytic coverage so dry-volume carving and off-mesh rays keep working.
            if (_UnderwaterUnbounded > 0.5 && _OceanSurfaceDepthValid > 0.5)
            {
                float gradientLength = length(gapGradient);
                float2 screenDirection = gradientLength > WATERLINE_GRADIENT_MIN
                                       ? gapGradient / gradientLength
                                       : float2(0.0, 1.0);
                return OceanRenderedCoverage(uv, coverage, screenDirection);
            }
            if (_UnderwaterUnbounded > 0.5) return coverage;
            // Bounded body. A finite fog VOLUME meant to be seen from OUTSIDE (stand at the
            // aquarium glass and look into the murk) - so a ray entering through a WALL or the
            // floor keeps full weight regardless of the eye's own waterline.
            //
            // THROUGH THE TOP is different (the pond ghost fix): from the air the rendered
            // sheet owns that column - its refracted sample already carries the water
            // absorption and the body opacity. This pass composites AFTER the sheet from the
            // OPAQUE unrefracted depth, so painting those pixels too re-exposed every
            // submerged object as an UNREFRACTED silhouette stamped over the sheet's
            // refracted image (worst on a floating hull: short fog column over the hull, long
            // beside it, opacity powerless because the fog draws last). Air-side top-entry
            // pixels therefore take the SAME per-pixel waterline coverage the ocean uses:
            // zero above the line, full fog the moment the near plane dips below it (the
            // crossing strip is near-clipped out of the sheet, so the fog must own it - the
            // transition fix stays intact). A submerged eye sits inside the box, its rays
            // have no entry face (tEnter 0), and the top-face test skips by construction.
            float3 originPool = WorldToPool(_WorldSpaceCameraPos);
            float3 rayPool = WorldToPool(classifyPoint) - originPool;
            rayPool /= max(length(rayPool), CLASSIFY_DIR_EPSILON);
            float2 boxHit = IntersectCube(originPool, rayPool,
                                          POOL_WATER_BOX_MIN, POOL_WATER_BOX_MAX);
            float tEnter = max(boxHit.x, 0.0);
            float entryPoolY = originPool.y + rayPool.y * tEnter;
            bool entersThroughTop = boxHit.y > tEnter && tEnter > 0.0
                                 && entryPoolY >= -POND_TOP_FACE_EPSILON;
            return entersThroughTop ? coverage : 1.0;
        }

#if !defined(WATER_FOG_SIMPLE) && !defined(WATER_FOG_CLASSIFY_RT)
        float2 FragClassify(Varyings input) : SV_Target
        {
            float classifyPushDist;
            float3 classifyPoint = WaterlineClassifyPoint(input.uv, classifyPushDist);
            float classifyGap;
            float gapSmooth;
            EvaluateWaterlineClassificationGaps(classifyPoint, classifyGap, gapSmooth);
            return float2(classifyGap, gapSmooth);
        }
#endif

        // Per-channel path transmittance for this pixel; also returns the depth-darkening term,
        // the sun visibility of the wet span past the exclusion volumes (1 = unshadowed), and the
        // per-pixel waterline mask (see ArmWeight).
        // wetStartOut / wetSpanOut report the PRE-CARVE wet segment (start point on the ray +
        // its world length) for the inscatter pass's additional-light loop. Pre-carve on
        // purpose: point-light scatter does not respect exclusion volumes in this increment
        // (documented on the knob), so handing it the carved span would fake half an awareness
        // the feature does not have. The absorb pass passes dummies.
        float3 UnderwaterFog(float2 uv, out float3 depthAttenuation, out float sunVisibility,
                             out float armWeight, out float4 debugColor,
                             out float3 wetStartOut, out float wetSpanOut)
        {
            // FIRST, ahead of every per-pixel march below: the waterline mask takes a screen
            // derivative and must be evaluated in uniform control flow.
            float classifyPushDist;
            armWeight = ArmWeight(uv, classifyPushDist);
            // Zero-coverage exit. The mask multiplies BOTH passes' output (absorb takes
            // lerp(1, ..., armWeight), inscatter takes inscatter *= armWeight), so a pixel the
            // waterline feather has already zeroed cannot change a single texel no matter what the
            // rest of this function computes - and it used to compute all of it: scene depth
            // reconstruction, the segment solve, the exclusion chain, the downwelling reference and
            // the clarity fetch, then multiply the lot by zero. With the eye near the surface in a
            // heavy sea that is routinely a third to a half of the frame (everything above the
            // line, sky included), twice over.
            //
            // Returning identity (transmittance 1, attenuation 1) reproduces the multiplied-by-zero
            // result exactly rather than approximating it. Derivatives are safe: ArmWeight took its
            // ddx/ddy above, in uniform flow, and nothing below this point takes another.
            //
            // A selected fog debug view is the one caller that reads the numbers we would skip, so
            // it keeps the long path - it is an instrument, and an instrument that measures a
            // shortcut is measuring the wrong thing.
            if (armWeight <= 0.0 && _WaterDebugMode < WATER_DEBUG_FOG_FIRST)
            {
                depthAttenuation = float3(1.0, 1.0, 1.0);
                sunVisibility = 1.0;
                debugColor = float4(0.0, 0.0, 0.0, 0.0);
                wetStartOut = _WorldSpaceCameraPos;
                wetSpanOut = 0.0;
                return float3(1.0, 1.0, 1.0);
            }
            // Does THIS PIXEL'S ray start in water? Per pixel, and from the SAME gap the mask
            // feathers over. It replaces `camUnder` - one camera-height boolean that held the
            // identical value for every pixel on screen while selecting between branches whose
            // path lengths differ by the whole ray (0 one frame, the full span the next, over the
            // entire frame at once, and worst on the horizontal/up looks where the span is long).
            // Sharing one number means the branch can only flip where the weight is already
            // crossing 0.5, so the step is multiplied by ~0 - which is exactly why neither
            // reference pops: the coverage test and the span test are the same test.
            //
            // TAKEN FROM THE WEIGHT, NOT FROM THE RAW GAP - and that difference was a shipped bug.
            // `classifyGap <= 0.0` flips at gapPixels == 0, but WaterlineCoverage crosses 0.5 at
            // gapPixels == overCoverPixels, and inside a dry carve ArmWeight hands it
            // WATERLINE_CARVE_OVER_COVER_PIXELS (3). So the two parted by three pixels at exactly
            // the place the invariant above claims they cannot, and the weight's 0.98 contour
            // landed at gapPixels = +0.12 - on the AIR side of zero. In that sliver the mask
            // demanded FULL fog while this bool said the ray started dry, so OceanWavyPath took
            // its `!rayStartsWet && !sceneUnder` early-out and returned pathLen 0. That is the thin
            // red line along the carve waterline in fog debug view 12, and it appeared ONLY with
            // the eye inside a carve because that is the only place the over-cover is non-zero.
            //
            // Reading the weight restores the stated invariant by construction, and is a NO-OP
            // wherever the over-cover is 0: WaterlineCoverage >= 0.5 is then algebraically
            // classifyGap <= 0, so open water, ponds, the straddling near plane and the horizon
            // are untouched.
            //
            // classifyGap was pruned from ArmWeight's signature on 2026-08-13: the play-test
            // this paragraph once waited on confirmed the fix (committed 2026-07-28). (The
            // out-param the debug views actually read is classifyPushDist, via WaterFogDebugColor.)
            bool rayStartsWet = armWeight >= WATERLINE_COVERAGE_WET_MIN;
            float3 sceneWorld = SceneWorldPos(uv);
            float pathLen;
            float deepestY;
            float surfaceRefY;
            float3 wetStart;
            UnderwaterSegment(uv, sceneWorld, rayStartsWet, pathLen, deepestY, surfaceRefY,
                              wetStart);
            // Dry-interior exclusion: the part of the wet span that crosses an exclusion volume is
            // AIR, so carve it out of the fog integral. Zero volumes = the loops never run. When
            // the whole span is dry (camera in a submerged room looking at its own wall), the
            // depth-darkening reference resets so the dry interior is not darkened as if it were
            // under water.
            float3 seg = sceneWorld - _WorldSpaceCameraPos;
            float3 segDir = seg / max(length(seg), 1e-5);
            float wetSpanLen = pathLen; // pre-carve span length (wetStart -> wet end, world metres)
            wetStartOut = wetStart;     // reported for the additional-light loop - see the header
            wetSpanOut = wetSpanLen;
            float dryLen = ExclusionRayLength(wetStart, segDir, pathLen);
            // MESH volumes carve by their real silhouette, taken from the depth prepass at this
            // pixel and returned in the SAME world metres as the analytic chord above (the analytic
            // loop skips them by design, so the two never double-count the same volume).
            if (_ExclusionMeshCount > 0.5)
                dryLen += ExclusionMeshRayLength(uv, wetStart, segDir, pathLen);
            pathLen = max(pathLen - dryLen, 0.0);
            if (pathLen <= 0.0)
            {
                deepestY = surfaceRefY;
            }
            else
            {
                // Depth darkening from the WET span only: y is linear along the ray, so the deepest
                // wet point sits at the span's deep end, PULLED OUT of any dry volume containing it
                // (down-rays) or PUSHED past it (up-rays, camera in a room). Without this, a dry
                // room at the deep end darkened the lit water wall seen through its window. Only
                // ever SHALLOWER than the raw endpoint min, hence the max().
                float tDeep = (segDir.y <= 0.0)
                            ? ExclusionPullToEntry(wetStart, segDir, wetSpanLen)
                            : ExclusionPushToExit(wetStart, segDir, 0.0, wetSpanLen);
                deepestY = max(deepestY, wetStart.y + segDir.y * tDeep);
            }
            // Carved presence: dry volumes block the DIRECT sun feeding this span's in-scatter
            // (Crest's carved-in-fog shadow, analytic). Averaged over three span points so the
            // shadow column steps softly. Zero volumes -> the visibility loops never run.
            sunVisibility = 1.0;
            if (_ExclusionCount > 0.5 && pathLen > 0.0)
            {
                sunVisibility = ExclusionSpanSunVisibility(wetStart, segDir, wetSpanLen, pathLen,
                                                           _LightDir);
            }
            // NO turbulence-foam exemption here any more: the sim foam floating on top of this
            // column is re-drawn AFTER this pass by WaterSurface's "PondFoamOverlay" pass (the
            // same after-fog reroute the particle sprites use - see WaterParticlesAfterFogPass).
            // Cancelling the fog by mask coverage - tried as a linear lerp, then inside the
            // exponent - could never match the DRAWN foam: the mask is low-frequency while the
            // visible foam is mask x pattern texture, so a full cancel punched clear un-fogged
            // holes through dense fog inside a foam patch, and a partial cancel still washed the
            // drawn foam toward the fog colour. The fog stays physical and uniform; the foam now
            // sorts by draw order instead.
            // EFFECTIVE downwelling depth (2026-07-31; Bert: "depth extinction is still very
            // strong, only the first meters are not too affected" - strength 0.1 crushed the
            // frame, and the extinction COLOUR could not show). deepestY is the span's DEEPEST
            // point, and on an unbounded ocean every below-horizontal ray ends at the far-plane
            // abyss - so the depth term saturated to black for half the screen the moment the
            // knob left zero, and with all three channels crushed there was no hue left to
            // tint. The light this ray actually delivers in-scatters about one mean free path
            // away, so the downwelling is evaluated at the transmittance-weighted MEAN depth of
            // the wet span instead (the god-ray reprojection anchor's logic, in closed form):
            //   tMean = 1/sigma - L * exp(-sigma*L) / (1 - exp(-sigma*L)),
            // sigma = mean extinction * density, with the sigma*L -> 0 limit (L/2) taken
            // explicitly below the threshold. The POSITION on the ray is geometry, so sigma is
            // a scalar mean; the COLOUR stays fully per-channel inside DownwellingAttenuation -
            // which is what finally lets the depth extinction colour read. Clamped no deeper
            // than the carve-adjusted deepestY so the dry-room correction above keeps its
            // meaning, and unchanged in shape for bounded ponds (their spans were never
            // abyssal, the mean just sits a little shallower than the floor).
            float downwellSigma = dot(_WaterExtinction.rgb, float3(1.0/3.0, 1.0/3.0, 1.0/3.0))
                                * _WaterFogDensity;
            float downwellSigmaL = downwellSigma * pathLen;
            // Denominators clamped BEFORE the select: an HLSL ternary evaluates both lanes, so
            // sigma = 0 (fog density slid to zero) would still compute 1/0 in the dead lane and
            // trip the compiler's division-by-zero diagnostics even though the L/2 lane wins.
            float downwellExp = exp(-downwellSigmaL);
            float downwellTMean = (downwellSigmaL > DOWNWELL_MEAN_SIGMA_MIN)
                ? (1.0 / max(downwellSigma, DOWNWELL_MEAN_SIGMA_MIN * 1e-3)
                   - pathLen * downwellExp / max(1.0 - downwellExp, DOWNWELL_MEAN_SIGMA_MIN * 1e-3))
                : (0.5 * pathLen);
            float downwellY = max(wetStart.y + segDir.y * downwellTMean, deepestY);
#ifndef WATER_FOG_SIMPLE
            // Downwelling reference LOCAL to the in-scatter point (the ocean stripe fix): the
            // per-path surfaceRefY samples the wave height above the ray's FAR ENDPOINT - and for
            // a level camera every pixel in a screen COLUMN lands on nearly the same far xz, so
            // the reference rode the wave phase out there column-coherently. A strong depth
            // extinction turns that +-amplitude swing into vertical bright/dark stripes across
            // the whole frame (exp of reference-minus-downwellY, multiplied onto the scene by the
            // absorb pass). The light this term prices in-scatters at the MEAN point computed
            // right above - so the height that belongs over it is the surface at THAT point's own
            // xz, which is also smooth across columns (neighbouring rays' mean points sit metres
            // apart, not hundreds). One extra wave sample per armed pixel; the wavy paths already
            // pay several in their march, and WaterDepthClarity's shore fetch below sits at this
            // same reconverged point in the control flow.
            float2 downwellXZ = wetStart.xz + segDir.xz * downwellTMean;
            float downwellRtWeight = HeightRTFeatherWeight(downwellXZ);
            // Three explicit branches, not one lerp. HLSL's lerp is an arithmetic blend, not a
            // select: written as lerp(analytic, rt, w) BOTH arms are evaluated, so every pixel paid
            // a full analytic field evaluation even where the RT owns the answer outright - and it
            // owns it almost everywhere, because the window is 512 m across and the feather only
            // 16 m of that. On an aperiodic FFT ocean that discarded arm is 4 cascades x 15 source
            // reads, per pixel, per fog pass. The weight-1 and weight-0 lanes are exact, and the
            // feather band in between still blends the same two numbers it always did.
            float downwellRefY;
            if (downwellRtWeight >= 1.0)
            {
                downwellRefY = SampleHeightRTWorldY(downwellXZ);
            }
            else if (downwellRtWeight > 0.0)
            {
                downwellRefY = lerp(SurfaceHeightAtXZ(downwellXZ),
                                    SampleHeightRTWorldY(downwellXZ), downwellRtWeight);
            }
            else
            {
                downwellRefY = SurfaceHeightAtXZ(downwellXZ);
            }
#else
            // Simple tier: the per-path reference is already the flat waterline - stripe-free by
            // construction, and this variant compiles no SurfaceHeightAtXZ to call.
            float downwellRefY = surfaceRefY;
#endif
            depthAttenuation = DownwellingAttenuation(downwellY, downwellRefY);
            // Carve-boundary pane: edge occlusion + sun facet of the box face this ray looks
            // through (Crest-style darkened zone edges, analytic). Folded into the term BOTH
            // hardware passes multiply by, so the scene absorption and the in-scatter darken
            // together - the walls cannot do this themselves, they draw before this pass.
            if (_ExclusionCount > 0.5 && wetSpanLen > 0.0)
            {
                depthAttenuation *= ExclusionBoundaryPaneShade(wetStart, segDir, wetSpanLen, _LightDir);
            }
            // Depth clarity: the SAME curve the surface shader uses above water (WaterDepthClarity).
            // Murkier water (shallower bed) shortens the fog reach, so below- and above-water clarity
            // stay consistent. Driven by the still-water column depth at the scene point; identity when
            // the feature is off (returns 1) or off the shore field (deep sentinel -> deep-clarity end).
            float clarity = WaterDepthClarity(ShoreShoalDepth(sceneWorld.xz));
            float density = _WaterFogDensity * lerp(CLARITY_FOG_DENSITY_MAX, 1.0, clarity);
            float3 transmittance = exp(-_WaterExtinction.rgb * (density * pathLen));
            // Instrument LAST, off the finished numbers rather than off a re-derivation: this
            // pixel's span BEFORE the carve (wetSpanLen), what survived it (pathLen), and what the
            // waterline mask let through (armWeight). debugColor.a stays 0 - and every caller
            // stays on its normal path - unless _WaterDebugMode selects a fog view.
            float3 debugRgb;
            debugColor = WaterFogDebugColor(armWeight, classifyPushDist, wetSpanLen, pathLen,
                                            debugRgb)
                       ? float4(debugRgb, 1.0)
                       : float4(0.0, 0.0, 0.0, 0.0);
            return transmittance;
        }

        // Interleaved-gradient dither (~+-0.5/255) added to the fog output to break the residual 8-bit
        // banding dense fog shows on smooth gradients (the target is usually LDR on the mobile/WebGPU URP
        // asset). Uses the screen pixel coordinate (SV_POSITION.xy).
        float3 FogDither(float2 pixel)
        {
            float n = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            return ((n - 0.5) / 255.0).xxx;
        }
        ENDHLSL

        // ---- Pass 0: absorption + depth darkening (dst *= pathTrans * depthAtten) ----
        Pass
        {
            Name "WaterUnderwaterFogAbsorb"
            Blend Zero SrcColor

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragAbsorb
            #pragma target 4.0
            // The heavy solve and ALL its variants (WATER_FOG_SIMPLE / WATER_FOG_CLASSIFY_RT /
            // WATER_STRIP_SHORE / WATER_FOG_POINT_LIGHTS) live in the "WaterFogSolve" MRT pass
            // since the C1 single-solve restructure (2026-08-13): this fragment is one pixel
            // load of the solved absorb term, identical in every variant, so the heavy module
            // compiles once per variant instead of twice across two fullscreen passes.

            half4 FragAbsorb(Varyings input) : SV_Target
            {
                float4 solved = LOAD_TEXTURE2D(_WaterFogSolveAbsorb, int2(input.positionCS.xy));
                // Debug view: WIPE the frame. This pass blends Zero SrcColor (dst *= src), so
                // returning 0 clears the target and the in-scatter pass immediately after - Blend
                // One One - writes the false colour into it. The two passes that already exist ARE
                // the replacement: no extra render pass, nothing left behind when off. The flag
                // rides the solve target's alpha - decided once, in the solve.
                if (solved.a > 0.5) return half4(0.0, 0.0, 0.0, 1.0);
                return half4(solved.rgb + FogDither(input.positionCS.xy), 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 1: inscattered fog colour, also dimmed by depth (dst += fog * (1-pathTrans) * depthAtten) ----
        Pass
        {
            Name "WaterUnderwaterFogInscatter"
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragInscatter
            #pragma target 4.0
            // Variant-free since C1 (2026-08-13) - see the absorb pass note. The point-light
            // loop moved to the "WaterFogSolve" pass with everything else.

            half4 FragInscatter(Varyings input) : SV_Target
            {
                float4 solved = LOAD_TEXTURE2D(_WaterFogSolveInscatter, int2(input.positionCS.xy));
                // Additive onto the target the absorb pass just cleared: this IS the view. No
                // dither on a debug false colour - the views are read by exact colour purity.
                if (solved.a > 0.5) return half4(solved.rgb, 1.0);
                return half4(solved.rgb + FogDither(input.positionCS.xy), 1.0);
            }
            ENDHLSL
        }

        // Keep the established material pass indices while compiling these focused passes
        // independently from the fog implementation above.
        UsePass "Hidden/AbstractOcclusion/WebGpuWater/WaterUnderwaterWaterline/WaterUnderwaterFogWaterline"
        UsePass "Hidden/AbstractOcclusion/WebGpuWater/WaterRestoreOpaqueDepth/WaterRestoreOpaqueDepth"

        Pass
        {
            Name "WaterFogClassify"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragClassify
            #pragma target 4.0
            #pragma multi_compile_fragment _ WATER_STRIP_SHORE
            ENDHLSL
        }

        // ---- Solve pass (C1, 2026-08-13): the ONE full per-pixel fog solve ------------------
        // Absorb and inscatter each used to run the entire UnderwaterFog() machinery - segment
        // solve, 16-step march + refine, exclusion loops, downwelling, clarity - so every armed
        // pixel paid it twice. This MRT pass runs it once and writes both finished blend terms;
        // passes 0/1 load them. Found by NAME in WaterUnderwaterFogPass (material pass indices
        // 0-4 are load-bearing), and carrying ALL the heavy variants so each expensive program
        // compiles once instead of twice across two fullscreen passes.
        Pass
        {
            Name "WaterFogSolve"

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragSolve
            #pragma target 4.0
            // multi_compile, NOT shader_feature: this material is created at runtime by
            // CoreUtils.CreateEngineMaterial, so build-time variant stripping would have no material
            // keyword state to inspect and could strip the variant we need.
            // WATER_FOG_SIMPLE : compile out the wavy-crossing machinery (see the fence above).
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT
            // Strips the shore/surf machinery (ShoreSample + EvaluateSurfWaves fences) out of the
            // module for bodies that never consume the shore substrate (useBedDepth off). Purely a
            // COMPILE-TIME twin of the runtime inert path - identical output, a fraction of the
            // ~600 KB Full-variant bytecode and its minutes-long d3d11 optimize (2026-08-10).
            // Keyword set beside WATER_FOG_SIMPLE in WaterUniformPublisher.PublishUnderwater.
            #pragma multi_compile_fragment _ WATER_STRIP_SHORE
            // Point/spot-light scattering in the fog. Solve-only (the blend passes carry no
            // light loop). A keyword, not a uniform: an 8-light loop behind a uniform branch
            // would still size every pixel's registers (the fps-cliff rule, 2026-07-29). Armed
            // by the publisher, and never together with WATER_FOG_SIMPLE.
            #pragma multi_compile_fragment _ WATER_FOG_POINT_LIGHTS

            struct SolveOutputs
            {
                // rgb = lerp(1, pathTransmittance * depthAttenuation, armWeight); a = debug flag.
                half4 absorb : SV_Target0;
                // rgb = armed inscatter total (or the debug false colour); a = debug flag.
                half4 inscatter : SV_Target1;
            };

            SolveOutputs FragSolve(Varyings input)
            {
                float3 depthAttenuation;
                float sunVisibility;
                float armWeight;
                float4 debugColor;
                float3 wetStart;
                float wetSpanLen;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibility,
                                                         armWeight, debugColor,
                                                         wetStart, wetSpanLen);
                SolveOutputs output;
                if (debugColor.a > 0.5)
                {
                    // A fog debug view owns the frame: the absorb pass wipes, the inscatter pass
                    // writes this false colour. Decided here, carried on the alpha flags.
                    output.absorb = half4(0.0, 0.0, 0.0, 1.0);
                    output.inscatter = half4(debugColor.rgb, 1.0);
                    return output;
                }
                // Per-pixel arm fade: below-line rays are full-strength instantly (weight 1); only
                // the through-surface murk eases in, so the gate can flip a frame early/late with
                // no visible change (at murk weight 0 the multiplier is 1 = scene untouched).
                float3 absorb = lerp(float3(1.0, 1.0, 1.0), pathTransmittance * depthAttenuation,
                                     armWeight);
                // F8 finite tattler (2026-08-11), absorb half: a non-finite absorb multiplies the
                // scene by NaN/Inf. Confess in the beauty as "no absorb" (1): if the dark marks
                // vanish while the magenta tattler below stays quiet, the NaN lives in the
                // transmittance / depth-attenuation / mask chain. Never fires on healthy pixels.
                if (any(isnan(absorb)) || any(isinf(absorb)))
                    absorb = float3(1.0, 1.0, 1.0);
                // Lit in-scatter target: the same WaterInscatterColor the surface uses, so the fog
                // colour seen from below matches the water colour seen from above (continuous
                // across the waterline). The view ray is surface->camera, from the scene depth.
                float3 sceneWorld = SceneWorldPos(input.uv);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - sceneWorld);
                // Sun colour attenuated by the exclusion-volume sun visibility: only the DIRECT
                // term darkens (WaterInscatterColor's ambient term ignores sunColor), so the
                // carve shadow reads as a lit fog losing its beam, never as black.
                float3 fogColor = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunVisibility, 0.0);
                // Overall floor multiplier on top: keeps a visible (never black) shadow column
                // whether Volume Scatter is on or off.
                fogColor *= lerp(EXCLUSION_SHADOW_FLOOR, 1.0, sunVisibility);
                float3 inscatter = fogColor * (1.0 - pathTransmittance);
                // Per-pixel arm fade: additive term scales straight to 0, mirroring the absorb term.
                inscatter *= armWeight;
                float3 total = inscatter * depthAttenuation;
#if defined(WATER_FOG_POINT_LIGHTS) && !defined(WATER_FOG_SIMPLE)
                // Scene-light glow, added AFTER the downwelling multiply above: local lights
                // never crossed the surface, so the sun's depth darkening does not apply to them
                // (their own extinction-to-light is inside the integral, measured from where the
                // WATER starts - tStart). Rides the SAME armWeight as the fog, so the glow can
                // never paint an above-waterline pixel.
                if (wetSpanLen > 0.0)
                {
                    float3 dir = normalize(sceneWorld - _WorldSpaceCameraPos);
                    float tStart = distance(_WorldSpaceCameraPos, wetStart);
                    total += WaterSceneLightsInscatter(_WorldSpaceCameraPos, dir, tStart,
                                                       tStart + wetSpanLen, tStart,
                                                       _VolumeCenter.y)
                           * (_UnderwaterLightScatter * armWeight);
                }
#endif
                // F8 finite tattler, inscatter half: a non-finite total adds NaN/Inf into the
                // scene. Confess as pure MAGENTA in the beauty - no debug view to select, no
                // frame-debugger jitter. Never fires on healthy pixels.
                if (any(isnan(total)) || any(isinf(total)))
                    total = float3(1.0, 0.0, 1.0);
                output.absorb = half4(absorb, 0.0);
                output.inscatter = half4(total, 0.0);
                return output;
            }
            ENDHLSL
        }
    }
}
