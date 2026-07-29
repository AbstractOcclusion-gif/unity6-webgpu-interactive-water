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
// Four passes are DECLARED, TWO are dispatched: 0 = raymarch into a half-res persistent history
// target (reads scene depth + main-light shadows via URP globals; animated-jitter march + temporal
// reprojection accumulation); 3 = additive composite (global _LargeGodRayTex) over the camera
// colour. Jitter + temporal accumulation are what calms the shafts today - few march steps read as
// many, and fast flicker cannot survive the accumulation.
//
// 1+2 = separable Gaussian blur. COMPILED BUT NEVER DISPATCHED - LargeBodyAtmospherePass runs
// RaymarchShaderPass (0) then CompositeShaderPass (3); see its line 41. They are kept for the
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
// move when the camera does. Over open sea a viewer in air
// gets nothing, because the surface shader owns that view and shafts there would be painted onto
// water the viewer is not inside. Requires the URP asset's Depth Texture ON and main-light shadows
// enabled. All tuning comes from published globals.
Shader "AbstractOcclusion/WebGpuWater/LargeBodyGodRays"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // ---- Pass 0: raymarch the shafts into the half-res target --------------------
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

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterVolume.hlsl" // _SimCenter/_SimExtent (window frame) + LARGE_CAUSTIC_REFERENCE_DEPTH
            #include "WaterShared.hlsl" // IOR_*, SafeRefractedLightY (caustic light projection)
            #include "WaterExclusion.hlsl" // dry-interior volumes: marched samples inside are air
            #include "WaterExclusionMeshSpan.hlsl" // ExclusionPrepassExitDistance: the RASTERISED carve exit
            #include "WaterFog.hlsl"    // shared water fog + downwelling helpers/globals (view-fog tint, depth fade)
            // SurfaceHeightAtXZ: the displaced surface, READ-ONLY. Included to ASK the shared
            // waterline where the water is, never to change it - the last attempt at from-air
            // shafts moved a helper INTO this header to share it and coupled the trusted fog to an
            // experiment, which is what forced that revert. Nothing here writes to it.
            #include "WaterWaterline.hlsl"

            float3 _LightDir;   // global, normalized direction toward the sun
            // _SunColor is declared by WaterFog.hlsl (included above) - the header that owns the in-scatter needing it.

            // Published by the underwater fog path; reused here so the shafts share the exact submersion
            // state and surface height the fog uses (one source of truth, no separate god-ray copy).
            float _UnderwaterSurfaceY; // world Y of the water surface above the camera
            // 1 = the EYE sits inside a dry carve. Split off from _CameraUnderwater in round 2 of
            // the exclusion work because "below the water HEIGHT" and "IN water" are different
            // questions and a sunken room answers them differently. The fog and the exclusion wall
            // have consumed this for a while; this pass had never heard of it.
            float _CameraDryVolume;

            float4 _LargeGodRayColor;
            float  _LargeGodRayDensity;
            // Hard ceiling on the march. Matches the top of GodRays.shader's Range(8,64) so the two
            // god-ray paths cannot diverge in worst-case cost; see the clamp at the march below.
            #define LARGE_GOD_RAY_MAX_STEPS 64
            float  _LargeGodRaySteps;
            float  _LargeGodRayAnisotropy;
            float  _LargeGodRayCausticStrength; // near-field surface-caustic shimmer (0 = plain shadow shafts)
            // Depth softening of the caustic shimmer (mip levels per metre below the surface): real
            // caustic light decorrelates with depth, so deep samples should carry broad slow beams,
            // not the razor-sharp surface focus. Mip averaging converges toward the RT's mean, so one
            // depth-scaled LOD gives BOTH the blur and the contrast fade. 0 = legacy sharp-at-any-depth.
            // Needs the caustic RT's mips (WaterCausticsPass generates them for ocean-clipmap bodies);
            // without mips the LOD clamps to 0 and this degrades to the legacy look.
            float  _LargeGodRayCausticDepthSoften;
            // Strength of the from-air, through-a-pane view relative to the submerged one.
            // 0 (the default) makes every above-water pixel early-out exactly as it did before this
            // existed, so the shipped underwater look is byte-identical until an author opts in.
            float  _LargeGodRayFromAir;

            // Temporal reprojection (the KWS calm): the pass renders into a persistent history RT and
            // blends each pixel with last frame's value reprojected by scene world position. Combined
            // with the per-frame animated jitter below, the march noise averages out over a few frames
            // and fast flicker physically cannot survive the accumulation. Set by the C# pass:
            // blend = 0 on the first frames, after a resize, and for non-game cameras.
            float4x4 _GodRayPrevVP;        // previous frame's view-projection (GPU convention)
            float4x4 _GodRayCurrVP;        // CURRENT frame's, same construction - see reprojection
            float    _GodRayTemporalBlend; // history weight [0,1); 0 = no accumulation
            float    _GodRayFrame;         // frame counter for the animated jitter
            TEXTURE2D(_LargeGodRayHistory); SAMPLER(sampler_LargeGodRayHistory);

            // The body's near-field caustic RT (window frame), published as a global. Sampled by light-
            // projection so the shafts flicker with the surface focusing, like the pool god rays.
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            // CAUSTIC_WINDOW_FADE now lives in WaterVolume.hlsl beside LARGE_CAUSTIC_REFERENCE_DEPTH:
            // the screen-space caustic projection reads the same RT and must fade with it, so the
            // number has to have exactly one home. Same value, so the shafts are byte-identical.
            // Shafts are a near/mid-field underwater effect; cap the march to a bounded visible distance
            // rather than the camera far plane (now horizon-sized on an ocean, so averaging over it would
            // dilute the shafts into invisibility). The fog hides anything past this anyway.
            #define SHAFT_MAX_DISTANCE 100.0
            // Volumetric caustic reach: KWS-style distance fade (theirs dies by 200m) - far shafts
            // read as steady light, near ones dapple.
            //
            // IMPORTANT - no base-LOD mip floor here, and one smoothing stage per axis ONLY. Over
            // the open ocean nothing casts shadows, so the shadow term is 1 everywhere and the
            // ENTIRE beam structure comes from this caustic term: the surface-focus banding IS the
            // god ray. Stacking a flat mip floor on top of the source band-limit knob (wavelet
            // harshness) and the depth-soften knob (depth calm) flattened the term to near-DC and
            // left only the anisotropic glow - shafts gone. Wavelet filtering belongs to
            // _LargeGodRayCausticSmooth alone; the LARGE-wave banding must reach the march sharp.
            #define GODRAY_CAUSTIC_DISTANCE_FADE  0.005
            // Base calm (the "mix"): in the top few metres the surface focusing is sharpest and
            // most transient - physically correct rays BLINK there, because only some wave
            // configurations focus. Extra mip blur confined to that zone converts the blinking
            // into a steady broad glow, and the gain restores the energy the blur averages away -
            // so the base trades flicker for BREADTH, not for presence, while the beam BODY
            // (below the calm depth) keeps its sharp structure. All three fade out together.
            // KEEP THE ZONE SHALLOW: with the camera near the surface most of a ray's path sits in
            // the top metres, so a deep calm zone blurs the whole BEAM, not just its base (v7's 3-4m
            // zones read as "lost god rays"). One metre treats only the attachment point.
            #define GODRAY_BASE_CALM_DEPTH  1.0  // metres below the surface the calm zone spans
            #define GODRAY_BASE_CALM_LOD    1.0  // extra mip blur right at the surface
            #define GODRAY_BASE_CALM_GAIN   0.3  // energy restored to the blurred base (+30% at 0m)
            // NOTE: KWS also applies a sun-elevation kill (smoothstep(-0.25,1,sunDir.y)). Tried and
            // REMOVED here: it crushed the shafts and the anisotropy glow to ~20% in exactly the
            // low-sun sunset scenes this ocean is built to show off. Low-sun shafts stay.

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            // Interleaved gradient noise (Jimenez 2014): a stable per-pixel [0,1) dither that turns
            // step-count banding into high-frequency noise the eye averages out across the shafts.
            float InterleavedGradientNoise(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            // Henyey-Greenstein phase: forward-scattering lobe. g -> 1 sharpens the glow toward the
            // sun. Normalised so _LargeGodRayDensity stays the single intensity control.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;
                return (1.0 - g2) / (4.0 * PI * pow(max(denom, 1e-4), 1.5));
            }

            // Near-field caustic focus at a submerged sample: project it along the refracted sun to the
            // shared reference plane, map into the window frame, sample the caustic RT. Returns 0 beyond
            // the window (plain shafts there), matching how LargeBodyCaustics.shader wrote the RT.
            // 'lod' is the depth-softening mip (see _LargeGodRayCausticDepthSoften).
            float LargeBodyCausticAt(float3 p, float3 refractedSun, float refPlaneY, float lod)
            {
                float2 projXZ = p.xz + refractedSun.xz * ((refPlaneY - p.y) / SafeRefractedLightY(refractedSun.y));
                float2 windowNorm = (projXZ - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                float2 edge = 1.0 - abs(windowNorm);
                if (edge.x <= 0.0 || edge.y <= 0.0) return 0.0;
                float fade = saturate(min(edge.x, edge.y) / CAUSTIC_WINDOW_FADE);
                float focus = SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, windowNorm * 0.5 + 0.5, lod).r;
                return focus * fade;
            }

            // Iterations solving the from-air waterline crossing against the DISPLACED surface at
            // the crossing's OWN xz. Three lands well inside the surface's own slope error. NOT a
            // bisection over a band: an earlier attempt bracketed +-amplitude and bisected five
            // times, which quantised the crossing to ~30 cm and printed visible steps. A fixed
            // point on a height field has no bracket to quantise.
            #define GODRAY_PANE_CROSS_ITERS 3

            // Distance along the ray at which an ABOVE-WATER camera's view ray meets the water.
            //
            // WHY THIS EXISTS. The obvious answer is the ray's intersection with the plane at
            // _UnderwaterSurfaceY, and it is wrong in a way a still frame cannot show:
            // _UnderwaterSurfaceY is the surface height at the CAMERA's xz. For a submerged eye
            // that is right - the eye IS at that xz. For a viewer in air looking at a distant pane
            // it makes the plane bob with the camera's own travel through the swell, so the pane's
            // edge SLIDES WITH THE VIEWER. Camera-coupled world geometry; on screen it read as
            // "the god ray box moves when the cam moves".
            //
            // So the height is read at the CROSSING's own xz instead, by fixed point: solve against
            // the current guess, re-read the real surface where that lands, repeat. The answer then
            // depends only on the world and the ray - track the camera sideways without turning and
            // the crossing stays put. It also follows the wave form for free, since it is the
            // displaced surface being asked, not a plane fitted to it.
            //
            // COST: SurfaceHeightAtXZ is ~6 texture fetches on an ocean body, so ~24 per pixel here.
            // Paid only by ABOVE-WATER pixels, only on downward rays, only in a scene that has an
            // exclusion volume at all, and only at half res - the caller gates all three.
            //
            // GRAZING RAYS converge slowest: with rayDir.y near 0 a small height change slides the
            // crossing far horizontally, onto a different part of the wave field. The clamp keeps
            // every iterate inside the span so one overshoot cannot throw the result - such a ray
            // lands somewhere plausible on the surface rather than exactly on its crossing. Those
            // rays meet the water near the horizon, where the shafts have already faded out.
            float PaneWaterlineDistance(float3 camWorld, float3 rayDir, float maxDist)
            {
                float planeY = _UnderwaterSurfaceY; // first guess: the flat, camera-local height
                [unroll]
                for (int i = 0; i < GODRAY_PANE_CROSS_ITERS; i++)
                {
                    float t = clamp((planeY - camWorld.y) / rayDir.y, 0.0, maxDist);
                    planeY = SurfaceHeightAtXZ((camWorld + rayDir * t).xz);
                }
                return clamp((planeY - camWorld.y) / rayDir.y, 0.0, maxDist);
            }

            // The shafts' submersion fade: zero at the surface, full this many metres below. SPATIAL
            // and current-frame (same pattern as the fog's murk ramp): the binary _CameraUnderwater
            // flag carries the CPU gate's readback staleness and hysteresis, so gating the scatter
            // on it popped the shafts a frame early/late whenever WAVES drove the crossing.
            #define GODRAY_SUBMERGE_FADE_METERS 0.25
            // Submersion depth over which the TEMPORAL accumulation comes up - deliberately far
            // deeper than the shafts' own fade above, and that separation IS the fix.
            //
            // Sharing one fade put the accumulation's arming at 0-0.25 m, which is exactly where the
            // image it accumulates CHANGES SHAPE: above the line only pane pixels are drawn, and the
            // instant the eye is under, every pixel has a span. So it switched on precisely as its
            // own history became meaningless, and spent its first frames converging away from a
            // stale pane. Bert, seeing it: "at water level crossing we still have a stale, i think
            // system have hard time to decide if it should smooth the ray or no" - the system was
            // not deciding badly, it was being asked at the one moment the answer was changing.
            //
            // This is the fog's arming rule applied to a different gate: a gate is safe when it is a
            // SUPERSET of where its effect can alter the result, so toggling it changes nothing. At
            // two metres down the field has been full-screen and stable for many frames, so bringing
            // the history in there cannot resurrect anything from the crossing.
            //
            // (The strictly doctrinal form would be PER PIXEL - store the regime that produced each
            // history texel and drop it where it disagrees. Not free: the half-res target inherits
            // the camera colour format, which on a common URP HDR setup is B10G11R11 with no alpha
            // channel to put it in. That is the upgrade path if this proves not enough.)
            #define GODRAY_TEMPORAL_FADE_METERS 1.0

            half4 FragRaymarch(Varyings input) : SV_Target
            {
                // Submerged: fade in over the first centimetres of submersion rather than
                // switching on the binary flag, so the scatter rises with the water taking the lens
                // instead of popping. In AIR this is 0 and the pane weight below decides instead.
                // (The feature also gates on an active god-ray ocean.)
                // ...and 0 outright when the eye sits in a DRY CARVE below sea level. This term
                // asks "is the lens wet"; a sunken room is dry air metres under the surface, so the
                // height difference answers a question that was never being asked. Left unfixed it
                // made regime = max(1, _LargeGodRayFromAir) = 1 in the one view the From Air knob
                // exists for, so the knob did nothing there.
                float submergeFade = (_CameraDryVolume > 0.5)
                                   ? 0.0
                                   : saturate((_UnderwaterSurfaceY - _WorldSpaceCameraPos.y)
                                              / GODRAY_SUBMERGE_FADE_METERS);
                // The accumulation's own, much deeper fade - see GODRAY_TEMPORAL_FADE_METERS. It
                // drives BOTH the animated jitter and the history blend, because those two are one
                // mechanism and must arm together.
                //
                // MEASURED AGAINST THE REST PLANE, NOT _UnderwaterSurfaceY - and that is the fix.
                // _UnderwaterSurfaceY is the surface height at the CAMERA's xz and it RIDES THE
                // LOCAL SWELL (WaterVolume.Underwater.cs:253, "wave-aware at the camera's xz, so
                // the line still rides the local swell"). Driving the history weight off it meant an
                // eye parked near the surface had every passing wave swing this ratio - and with it
                // the blend, SCREEN-WIDE - between 0 and 1 at wave frequency. On screen that is the
                // shafts smoothing and sharpening in time with the swell: Bert, 2026-07-28, "at
                // exact point where water line cross camera, the fog ray blur / deblur". The rest
                // plane is the MEAN level, so a stationary eye gets a stationary weight.
                //
                // submergeFade above deliberately KEEPS the wavy surface: it answers "is the lens
                // actually wet", which is a fact about the real displaced water, not about a mean.
                float temporalFade = saturate((_VolumeCenter.y - _WorldSpaceCameraPos.y)
                                              / GODRAY_TEMPORAL_FADE_METERS);
                if (_LargeGodRayDensity <= 0.0) return half4(0.0, 0.0, 0.0, 1.0);
                // With the from-air knob at 0 this is the ORIGINAL early-out, unchanged: an
                // above-water pixel costs one compare and leaves.
                if (submergeFade <= 0.0 && _LargeGodRayFromAir <= 0.0) return half4(0.0, 0.0, 0.0, 1.0);

                float rawDepth = SampleSceneDepth(input.uv);
                float3 sceneWorld = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);

                float3 camWorld = _WorldSpaceCameraPos;
                float3 toScene = sceneWorld - camWorld;
                float sceneDist = length(toScene);
                float3 rayDir = toScene / max(sceneDist, 1e-5);

                // The BELOW-SURFACE span [tEnter, tExit] of the view ray against the flat waterline.
                // ONE formulation for both viewpoints, rather than a second regime bolted beside the
                // first: submerged, the span starts at the eye and (for an up-ray) ends where the ray
                // leaves the water, so a shaft stops where the water ends instead of streaking into
                // the air; from air, it STARTS where the ray dips under, and a ray that never dips has
                // no span at all. Never past the scene, and never past SHAFT_MAX_DISTANCE.
                // ONE rasterised carve query per pixel, used TWICE below - to find where a
                // dry-carve eye's ray enters the water, and to floor the regime across the surface
                // crossing. Hoisted so the two do not each pay the texel fetches. Gated on
                // _ExclusionCount so a scene with no volume issues no fetch at all.
                float2 carveSpan = float2(0.0, 0.0);
                float carveExit = 0.0;
                bool rayLeavesCarve = (_ExclusionCount > 0.5)
                                    && ExclusionPrepassExitDistance(input.uv, camWorld, rayDir,
                                                                    carveSpan, carveExit);

                float camGap = _UnderwaterSurfaceY - camWorld.y; // > 0 = the eye is below the surface
                // ...below the water HEIGHT, which is NOT "in water" - a dry carve is air down there.
                bool eyeInWater = camGap > 0.0 && _CameraDryVolume < 0.5;
                float tEnter = 0.0;
                float tExit = min(sceneDist, SHAFT_MAX_DISTANCE);
                if (eyeInWater)
                {
                    if (rayDir.y > 1e-4) tExit = min(tExit, camGap / rayDir.y);
                }
                else
                {
                    // In air, so this pixel can only ever be a PANE view - and a pane needs a
                    // volume. Leaving here first keeps a scene with no carve from paying for the
                    // surface field at all.
                    if (_ExclusionCount < 0.5) return half4(0.0, 0.0, 0.0, 1.0);

                    // TWO kinds of pane, entering the water in different places.
                    //
                    // Eye INSIDE a dry carve (a sunken room): the water starts where the ray LEAVES
                    // the carve, and that wall can be horizontal or even overhead - the window is
                    // not a ceiling, so the downward-only rule below would blank the whole room.
                    // Taken from the RASTERISED exclusion silhouette, per pixel and for every shape.
                    // Only honoured when the exit is BELOW the displaced surface: an exit into air
                    // is not a water entry, and that ray falls through to the waterline rule.
                    bool entered = false;
                    if (_CameraDryVolume > 0.5 && rayLeavesCarve
                        && carveExit < tExit
                        && SurfaceSignedGap(camWorld + rayDir * carveExit) <= 0.0)
                    {
                        tEnter = carveExit;
                        entered = true;
                    }
                    if (!entered)
                    {
                        // Eye above the open surface: only a DOWNWARD ray reaches the water. Also the
                        // fallback when the prepass did not run (no WaterExclusionDepthFeature on the
                        // renderer), so an un-migrated project keeps exactly its old behaviour.
                        if (rayDir.y > -1e-4) return half4(0.0, 0.0, 0.0, 1.0);
                        // Solved at the CROSSING's own xz, not the camera's - see PaneWaterlineDistance.
                        tEnter = PaneWaterlineDistance(camWorld, rayDir, tExit);
                    }
                }
                if (tExit <= tEnter) return half4(0.0, 0.0, 0.0, 1.0);

                // THE PANE CULL - the whole reason from-air shafts are safe to draw at all. Tested at
                // the point where this ray crosses the waterline: inside a carve that point is a
                // WINDOW into a lit water volume, which is exactly the view worth drawing; outside, it
                // is open sea, which the surface shader owns and which must get nothing.
                //
                // The two regimes are MAXed rather than blended through a camera-height ramp, and that
                // is safe for a reason worth stating: underwater the eye is IN WATER, so it is never
                // inside a dry carve, so this term is 0 there and the submerged look is bit-for-bit
                // what it was. An earlier attempt floored the submerged side at the knob instead and
                // popped the shafts on diving in.
                float3 waterEntry = camWorld + rayDir * tEnter;
                // An eye already INSIDE the carve is looking through the window by construction, so
                // it does not have to prove it: waterEntry is then the carve EXIT, which sits ON the
                // boundary and would test as outside, blanking the room's shafts entirely.
                float paneWeight = (_CameraDryVolume > 0.5 || InsideExclusion(waterEntry))
                                 ? _LargeGodRayFromAir : 0.0;
                // THE HANDOFF, and why max() was not one. The two regimes are MUTUALLY EXCLUSIVE
                // across the eyeInWater branch above - only ever one is non-zero - so max() never
                // blended anything. Crossing the surface while looking into a carve therefore
                // STEPPED: paneWeight (_LargeGodRayFromAir, a constant) above the line, and
                // submergeFade, which starts at 0, below it. The shafts read the knob, dropped to
                // black at the crossing, then climbed back over GODRAY_SUBMERGE_FADE_METERS.
                //
                // Floor the submerged side at the knob so the two meet, but do it PER PIXEL - only
                // where a carve actually stands along this ray. That is the difference from the
                // earlier attempt this file records ("floored the submerged side at the knob
                // instead and popped the shafts on diving in"): that one floored EVERYWHERE, so
                // open water inherited a pane weight it never had. The rasterised silhouette
                // answers "is there a carve on this ray" per pixel, for free, from the query
                // hoisted above.
                //
                // Bit-identical wherever it can be: no carve on the ray, or _ExclusionCount 0, or
                // _LargeGodRayFromAir 0 (every shipped scene but the Exclusion Demo) all give
                // paneFloor 0, and lerp(0, 1, submergeFade) IS submergeFade.
                //
                // TWO GATES, because "a carve stands somewhere on this ray" is not "this ray sees
                // water through a window". This term is a FLOOR UNDER submergeFade, not a regime of
                // its own, and each gate closes one way it was acting like one:
                //  - eyeInWater: IN AIR submergeFade is 0, so lerp(paneFloor, 1, 0) collapses to
                //    paneFloor outright and handed the knob to every pixel the rasterised silhouette
                //    covers - the carve's ABOVE-WATER body included, since that silhouette does not
                //    stop at the waterline. Reported 2026-07-28: "our godrays veil do not stop at
                //    water line, it propagate all along exclusion mesh above water". Above the line
                //    the pane cull below is the sole authority, which is what it was written to be.
                //    eyeInWater is true exactly where submergeFade > 0 for a non-dry eye, so the
                //    crossing stays continuous: paneWeight just above, lerp(knob, 1, ~0) just below.
                //  - the carve EXIT must lie BELOW the displaced surface: the increment-C rule,
                //    already applied to the dry-carve eye's water entry above. A ray that leaves the
                //    carve into AIR met no water inside it, so there is no pane weight to floor.
                bool carveExitInWater = rayLeavesCarve
                                      && SurfaceSignedGap(camWorld + rayDir * carveExit) <= 0.0;
                float paneFloor = (eyeInWater && carveExitInWater) ? _LargeGodRayFromAir : 0.0;
                float regime = max(lerp(paneFloor, 1.0, submergeFade), paneWeight);
                if (regime <= 0.0) return half4(0.0, 0.0, 0.0, 1.0);

                // PER-PIXEL arming for the temporal pair (jitter + history) - what the depth fade
                // above was standing in for and could never actually express. Both halves fail on
                // the SAME premise: a PANE pixel's value is a property of its whole marched span,
                // not of the point behind it, so accumulating it slides a stale pane along with the
                // camera. paneWeight already answers "is this a pane pixel" PER PIXEL and is free
                // here - it is 0 for every submerged-field pixel by construction, because
                // underwater the eye is in water and so never inside a dry carve. This is the
                // "strictly doctrinal form ... PER PIXEL" the header wished for, and it needs no
                // history channel at all: the regime is RECOMPUTED from this frame's geometry
                // instead of stored, which is what made the B10G11R11 alpha objection moot.
                //
                // The depth ramp is kept as well: the history RT is written every frame regardless
                // of this weight, so a pixel that has just flipped pane -> field must not be able to
                // pull last frame's pane value straight back out. The ramp holds arming off until
                // the pane is a metre of mean depth behind us.
                // Only the from-ABOVE pane refuses accumulation. Its span is what a moving camera
                // sees THROUGH a window in the surface, and translating the eye changes that span
                // completely - the premise the reprojection rests on. An eye inside the room is a
                // different case: its span is [wall -> scene] and it behaves like any submerged
                // pixel, so refusing there would strip the whole screen of smoothing for no reason.
                bool fromAbovePane = (_CameraDryVolume < 0.5) && (paneWeight > 0.0);
                float temporalArm = temporalFade * (fromAbovePane ? 0.0 : 1.0);

                float marchDist = tExit - tEnter;

                // Clamped in the SHADER, not just by the publisher: this pass has no Properties
                // block, so _LargeGodRaySteps is a plain global with no Range() to bound it (unlike
                // GodRays.shader's Range(8,64)). An unbounded value here is an unbounded dynamic
                // loop - a TDR / device-lost on WebGPU and mobile, not merely a slow frame.
                int steps = clamp((int)_LargeGodRaySteps, 1, LARGE_GOD_RAY_MAX_STEPS);
                float dt = marchDist / steps;
                // ANIMATED jitter (Jimenez): shifting the noise pattern every frame turns the static
                // dither into per-frame samples the temporal accumulation below averages - a few
                // marched steps behave like many.
                //
                // Faded out by the SAME submersion factor as that accumulation, because the two are
                // one mechanism and only make sense together: an animated pattern with nothing
                // averaging it does not smooth, it CRAWLS - a fresh dither every frame, shifting the
                // samples by up to a full step through the shadow and caustic field. Where the
                // accumulation is off (the from-air pane view - see the blend below) this leaves a
                // STATIC dither instead, which the separable blur can smooth. Fading the frame term
                // rather than switching it keeps the pattern morphing smoothly across the crossing.
                float jitter = InterleavedGradientNoise(input.positionCS.xy
                                                        + 5.588238 * _GodRayFrame * temporalArm);

                // Constant along a straight view ray -> hoisted: the sun glow (phase) and the per-step
                // view-fog factor (Beer-Lambert over one step, per channel so red dies first).
                float phase = HenyeyGreenstein(dot(rayDir, _LightDir), _LargeGodRayAnisotropy);
                float3 viewFogStep = (_WaterFogEnabled > 0.5)
                    ? exp(-_WaterExtinction.rgb * (_WaterFogDensity * dt)) : float3(1.0, 1.0, 1.0);

                // Near-field caustic shimmer: the refracted sun and its reference plane are constant along
                // the straight view ray, so hoist them; each sample then projects onto that plane to read
                // the surface focusing. Skipped entirely when the shimmer is off (strength 0).
                bool wantCaustic = _LargeGodRayCausticStrength > 0.0;
                float3 refractedSun = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                float causticRefPlaneY = _UnderwaterSurfaceY - LARGE_CAUSTIC_REFERENCE_DEPTH;

                float3 accum = float3(0.0, 0.0, 0.0);
                float3 viewFog = float3(1.0, 1.0, 1.0); // transmittance from the camera to the current sample
                // Sum of the per-sample transmittance weights (rgb mean, so the relative red-first
                // extinction along a ray survives the normalisation below). With fog off every
                // weight is 1 and this equals the step count - byte-identical to the old average.
                float viewFogWeightSum = 0.0;
                // First moment of those SAME weights, for the temporal anchor at the end of the pass.
                // The value this pixel carries is the zeroth moment of this distribution, so the depth
                // it belongs to is the first. Dry excluded samples add weight here exactly as they add
                // to the divisor below - keeping both moments on ONE distribution matters more than
                // trimming that tail, and it is what makes the anchor consistent with the brightness.
                float viewFogWeightedDist = 0.0;
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float t = tEnter + (s + jitter) * dt;
                    float3 p = camWorld + rayDir * t;
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    // Carved presence: a dry volume between this sample and the sun blocks the
                    // direct beam (analytic box shadow, refraction-aware, matching the fog's
                    // in-scatter shadowing).
                    shadow *= ExclusionSunVisibility(p, _LightDir, _UnderwaterSurfaceY);
                    // downwelling: less sun reaches deeper samples (shared depth-darken knob).
                    float depthFade = DepthFadeScalar(p.y, _UnderwaterSurfaceY, _GodRayDepthFade);
                    // surface-focused caustic brightens/flickers the shaft near the camera; neutral far
                    // out, and softened/calmed with the SAMPLE's depth (broad slow beams down deep).
                    // Near the surface the base-calm mix applies (see GODRAY_BASE_CALM_* above):
                    // blur + gain confined to the top metres, sharp beam body below.
                    float depthBelow = max(0.0, _UnderwaterSurfaceY - p.y);
                    float baseCalm = 1.0 - saturate(depthBelow / GODRAY_BASE_CALM_DEPTH);
                    float causticLod = GODRAY_BASE_CALM_LOD * baseCalm
                                     + depthBelow * _LargeGodRayCausticDepthSoften;
                    float caustic = wantCaustic ? LargeBodyCausticAt(p, refractedSun, causticRefPlaneY, causticLod) : 0.0;
                    caustic *= 1.0 + GODRAY_BASE_CALM_GAIN * baseCalm;
                    caustic *= 1.0 - saturate(t * GODRAY_CAUSTIC_DISTANCE_FADE);
                    // Dry-interior exclusion: samples inside an exclusion volume are air - skip their
                    // scatter; the view-fog transmittance still advances along the ray.
                    if (!InsideExclusion(p))
                        accum += shadow * depthFade * viewFog * (1.0 + caustic * _LargeGodRayCausticStrength);
                    float sampleWeight = (viewFog.r + viewFog.g + viewFog.b) / 3.0;
                    viewFogWeightSum += sampleWeight;
                    viewFogWeightedDist += sampleWeight * t;
                    viewFog *= viewFogStep;
                }
                // SELF-NORMALIZING average: divide by the summed transmittance weights, not the raw
                // step count. The old /steps made shaft brightness scale with the MEAN transmittance
                // over the whole march - with any fog density, most of a 100m march contributes
                // ~nothing yet still counts in the divisor, so the shafts collapsed toward invisible
                // ("we almost lose god rays when fog density > 0"). Weight-normalised, brightness
                // stays O(1) at any density; dense fog instead shifts the STRUCTURE toward the
                // near-camera dapple (the KWS look - their rays die by 200m but the near field stays
                // lively). Fog off: every weight is 1, the divisor equals the step count, and this
                // is byte-identical to the old average. The rgb-mean weight keeps the relative
                // red-first spectral loss along the ray; the floor guards a fully-extinct march.
                accum /= max(viewFogWeightSum, 1e-4);

                float3 col = _LargeGodRayColor.rgb * _SunColor * (accum * _LargeGodRayDensity * phase);
                // Submerged fade, or the from-air pane weight - whichever claims this pixel.
                col *= regime;

                // Temporal accumulation: blend with last frame's value at this scene point. The history
                // is the raw march RT (ping-ponged by the C# pass), which is also what the composite
                // reads - the blur passes are not dispatched (see the file header). Off-screen history
                // = fresh value.
                //
                // SUBMERGED-FIELD ONLY, hence the fade. Reprojecting by the SCENE world position is
                // sound while the shafts are a smooth volume in front of real geometry: the value at
                // a pixel really is a property of the point behind it. A from-air PANE view breaks
                // that premise outright - what a pane pixel shows is a property of the whole marched
                // span through the carve, so translating the camera changes the span completely while
                // the reprojection still hands back most of last frame's value from wherever that
                // scene point went. On screen that is a stale copy of the pane sliding along with the
                // camera and dissolving over the ~8 frames the 0.88 weight integrates.
                // Scaling by submergeFade removes it exactly where the premise fails and leaves the
                // underwater calm bit-for-bit as it was; the same factor is continuous across the
                // crossing, so the accumulation fades in rather than switching on.
                float temporalBlend = _GodRayTemporalBlend * temporalArm;
                if (temporalBlend > 0.0)
                {
                    // SELF-CALIBRATING reprojection: project this pixel's scene point through BOTH
                    // frames' matrices with identical math and apply only the DELTA to the raster
                    // uv. Any constant convention mismatch (y-flip, half-texel, GL-vs-D3D clip)
                    // cancels exactly - a static camera reprojects onto itself by construction.
                    // (The earlier absolute-uv form with a UNITY_UV_STARTS_AT_TOP flip guessed the
                    // convention wrong here, and with feedback the mismap DISSOLVED the shafts into
                    // a dim haze over a few frames. Never reproject absolutely; always delta.)
                    // ANCHOR - deliberately NOT sceneWorld. Drawn water is TRANSPARENT, so just below
                    // the waterline the depth buffer behind this pixel holds the SKYBOX at the far
                    // plane, while the value is an integral over a span the WATERLINE terminated a few
                    // metres away (tExit, above). A point at infinity barely moves under camera
                    // TRANSLATION, so the history came back from the wrong place and the shafts dragged
                    // whenever the camera moved underwater - the file's own premise, "the value at a
                    // pixel really is a property of the point behind it", is false for a waterline-
                    // clipped ray. The transmittance-weighted MEAN distance is the depth the value
                    // actually belongs to (the span end alone would overshoot: shaft contribution is
                    // front-loaded by transmittance), and it costs one accumulator because those
                    // weights already exist for the brightness normalisation above. Falls back to the
                    // span end if the march extinguished completely.
                    float anchorDist = (viewFogWeightSum > 1e-4)
                                     ? viewFogWeightedDist / viewFogWeightSum
                                     : tExit;
                    float3 anchorWorld = camWorld + rayDir * anchorDist;
                    float4 currClip = mul(_GodRayCurrVP, float4(anchorWorld, 1.0));
                    float4 prevClip = mul(_GodRayPrevVP, float4(anchorWorld, 1.0));
                    if (currClip.w > 1e-4 && prevClip.w > 1e-4)
                    {
                        // Both matrices are built with GetGPUProjectionMatrix(renderIntoTexture:
                        // true), which bakes the platform y-flip into clip space. That flip is
                        // uv.y = -ndc.y * 0.5 + 0.5 - an OFFSET AND A NEGATION. Differencing two
                        // projections cancels the offset, which is exactly what the delta form was
                        // added for, but the negation SURVIVES subtraction: "any constant convention
                        // mismatch cancels exactly" is true for offsets and false for signs, so the
                        // guard above is structurally blind to this one.
                        // Nothing here could have caught it either - a static camera has delta = 0
                        // and reprojects onto itself for ANY sign, and translation testing is mostly
                        // horizontal, where x was already right. PITCH is the first motion that puts
                        // real signal into delta.y: it read as the shafts sliding the wrong way at
                        // roughly double rate while yaw stayed clean, which is the signature of a
                        // y-only sign error and nothing else.
                        float2 delta = prevClip.xy / prevClip.w - currClip.xy / currClip.w;
                        // ndc delta -> uv delta. If a future backend ever needs the other convention
                        // this ONE constant is the whole switch - do not scatter flips downstream.
                        const float2 ndcDeltaToUv = float2(0.5, -0.5);
                        float2 prevUV = input.uv + delta * ndcDeltaToUv;
                        if (prevUV.x > 0.0 && prevUV.x < 1.0 && prevUV.y > 0.0 && prevUV.y < 1.0)
                        {
                            float3 history = SAMPLE_TEXTURE2D_LOD(_LargeGodRayHistory,
                                                 sampler_LargeGodRayHistory, prevUV, 0).rgb;
                            col = lerp(col, history, temporalBlend);
                        }
                    }
                }
                return half4(col, 1.0);
            }
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

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_LargeGodRayTex);
            SAMPLER(sampler_LargeGodRayTex);

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                // _LargeGodRayTex is the half-res shaft target, bound as a global by the raymarch pass.
                return SAMPLE_TEXTURE2D(_LargeGodRayTex, sampler_LargeGodRayTex, input.uv);
            }
            ENDHLSL
        }
    }
}
