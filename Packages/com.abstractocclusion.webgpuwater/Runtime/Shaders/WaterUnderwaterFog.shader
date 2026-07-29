// WebGpuWater - real underwater fog (URP RenderGraph fullscreen).
// Fogs only the part of each camera->scene ray that is actually IN the water, so it reads as a
// volume and a waterline falls out for free (a ray that never enters the water gets no fog):
//   * Ocean (unbounded): the below-surface half-space -> the fullscreen screen effect.
//   * Pond  (bounded):   the ray clipped to the pool box (pool space [-1,1] xz, [-1,0] y) via
//                        IntersectCube -> a finite fog volume you can circle around.
// Per-channel Beer-Lambert absorption + downwelling depth darkening, reusing the body's fog and
// depth globals. Two hardware-blend passes so the scene colour never has to be copied:
//   0 Absorb:    scene *= pathTransmittance * depthAttenuation   (Blend Zero SrcColor)
//   1 Inscatter: scene += fog * (1 - pathTransmittance) * depthAttenuation   (Blend One One)
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
        // 1 = the EYE sits inside a dry exclusion volume (PublishUnderwater, alongside
        // _CameraUnderwater - which now means "the eye is in WATER" and reads 0 in here). A uniform,
        // so the camera-height terms below stand down on a screen-coherent branch: in a sunken room
        // the eye's height against the outside waterline is not a measure of anything.
        float _CameraDryVolume;
        // Ocean-surface eye-depth prepass (KWS-style rendered waterline): the DISPLACED surface's
        // linear eye depth per pixel, SIGNED by which side of the sheet is visible (+ = the ABOVE
        // sheet, seen from the air; - = the UNDER sheet, seen from below; 0 = no surface
        // rasterised there). Written by WaterSurface's "OceanSurfaceEyeDepth" pass via
        // WaterUnderwaterFogPass. When valid, the fog's crossing comes from this - the rendered
        // surface itself - instead of the bounded analytic march.
        TEXTURE2D(_OceanSurfaceEyeDepth);
        float _OceanSurfaceDepthValid; // 1 = the prepass ran this frame (set by the fog pass)
        // Sun globals (published by WaterUniformPublisher) - not in this shader's include chain otherwise.
        // Needed so the underwater in-scatter can use the same lit WaterInscatterColor as the surface, for a
        // continuous colour crossing the waterline.
        float3 _LightDir;
        // _SunColor is declared by WaterFog.hlsl (included above) - the header that owns the in-scatter needing it.

        // Per-pixel wavy-waterline crossing search (U2). The camera->scene ray meets the DISPLACED surface
        // at a height that follows crests/troughs, so we bracket the FIRST sign change of
        // (rayY - SurfaceHeightAtXZ) with a constant-step coarse scan and refine by bisection. Constant
        // step/iteration counts keep this fullscreen pass cheap and allocation-free.
        // 12 bisections on the ONE 1.5 m march step that brackets the crossing -> ~0.4 mm, so
        // the fog waterline agrees with the exclusion wall's EXACT per-pixel classification even
        // under grazing magnification (5 iterations left ~5 cm of error, which a horizontal look
        // at water level stretched into a visible empty band between the wall line and the fog
        // line). RULE (round-1 post-mortem): the iteration count must be sized to THIS bracket -
        // never reuse it on a wider one.
        #define UNDERWATER_CROSS_REFINE_ITERS 12
        // Crossing search: march the surface band with a FIXED WORLD STEP (constant, wave-scale resolution
        // so a crest is never skipped or aliased) up to a step cap; beyond the cap - the far horizon, where
        // waves are sub-pixel - fall back to the flat rest-plane waterline. Band = max(swell reach, surf
        // crest reach) + BAND_PAD metres (generous, to bracket crests + wind-wave chop). The step cap sets
        // how far the march reaches along the ray (STEP_METRES x MAX_STEPS): raised so the wider shore-surf
        // band is still bracketed on grazing up-looks, where the crossing sits many metres along the ray.
        #define UNDERWATER_CROSS_STEP_METRES 1.5
        #define UNDERWATER_CROSS_MAX_STEPS   40
        #define UNDERWATER_SURFACE_BAND_AMPS 3.0
        #define UNDERWATER_SURFACE_BAND_PAD  2.0
        // Max SurfSetAmp jitter: SURF_SETAMP_JITTER_MAX from WaterSurfWaves.hlsl (via
        // WaterLargeWaves above) - the crossing-search band brackets the highest surf crest the
        // set jitter can produce, so it must be the SAME constant, not a hand copy.
        #define UNDERWATER_SURF_SETAMP_MAX   SURF_SETAMP_JITTER_MAX
        // Fraction of the march reach where the wavy crossing starts fading to the flat fallback
        // (fully flat AT the reach), so the wavy->flat handover is a blend, not a seam.
        #define UNDERWATER_SEAM_BLEND_START  0.75
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
        // WORST path. A 40-step march whose every step calls SurfaceHeightAtXZ (~6 texture fetches:
        // 2x ShoreSample + 4x OceanFftDisplacementShore) plus a 12-iteration bisection was therefore
        // setting the occupancy of every Simple-tier pixel too, on a FULLSCREEN pass, twice per frame
        // (absorb + inscatter). Fencing it with the preprocessor is what actually removes it.
        // Simple keeps exactly one path: OceanFlatPath, below.
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
            float camSurf = SurfaceHeightAtXZ(cam.xz);
            float sceneSurf = SurfaceHeightAtXZ(sceneWorld.xz);
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
            // Surf fronts shoal + break to crests well above the swell (H <= _SurfAmplitude * setAmp_max *
            // _SurfGreens; see WaterSurfWaves EvaluateSurfWaves), so a swell-only band would start the march
            // ABOVE a tall shore crest and miss the crossing, flattening the fog waterline onto the rest
            // plane. Include that reach so the search brackets the shore crest. Inert (0) when surf is off.
            float surfReach = (_SurfActive > 0.5)
                            ? _SurfAmplitude * UNDERWATER_SURF_SETAMP_MAX * max(_SurfGreens, SURF_MIN_GREENS)
                            : 0.0;
            float band = max(abs(_LargeWaveAmplitude) * UNDERWATER_SURFACE_BAND_AMPS, surfReach)
                       + UNDERWATER_SURFACE_BAND_PAD;
            float tFlat = (restY - cam.y) / dySafe;              // flat rest-plane crossing (ray parameter)
            float tBand = band / max(abs(ray.y), 1e-4);          // half-band in ray-parameter units
            float startDist = saturate(tFlat - tBand) * rayLen;  // skip the deep water below the band
            float3 prev = cam + dir * startDist;
            float gapPrev = SurfaceSignedGap(prev);
            float3 hitFlat = cam + ray * saturate(tFlat);        // flat waterline (far horizon)
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
                float gap = SurfaceSignedGap(p);
                if (gapPrev * gap <= 0.0)
                {
                    // Wavy crossing, faded toward the flat one over the march's last quarter: a hard
                    // switch at the step cap printed a seam where the fog waterline snapped from the
                    // waves to the rest plane at ~the march distance.
                    float seam = smoothstep(marchReach * UNDERWATER_SEAM_BLEND_START, marchReach, d);
                    hit = lerp(RefineSurfaceCrossing(prev, gapPrev, p), hitFlat, seam);
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
            float camSurf = SurfaceHeightAtXZ(cam.xz);
            float sceneSurf = SurfaceHeightAtXZ(sceneWorld.xz);
            bool sceneUnder = sceneWorld.y <= sceneSurf;
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
            int2 prepassPixel = int2(uv * _ScaledScreenParams.xy);
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
            int2 prepassPixelMax = int2(_ScaledScreenParams.xy) - int2(1, 1);
            int prepassRowUp   = min(prepassPixel.y + PREPASS_FROM_AIR_CORROBORATION_PIXELS, prepassPixelMax.y);
            int prepassRowDown = max(prepassPixel.y - PREPASS_FROM_AIR_CORROBORATION_PIXELS, 0);
            float surfaceSignedUp   = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, int2(prepassPixel.x, prepassRowUp)).r;
            float surfaceSignedDown = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth, int2(prepassPixel.x, prepassRowDown)).r;
            bool fromAirCorroborated = surfaceSignedUp > 0.0 || surfaceSignedDown > 0.0;

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
            // Eye depth is view-space Z; divide by the ray/forward cosine for distance along the ray.
            float3 camForward = -UNITY_MATRIX_V[2].xyz;
            float hitDist = surfaceEye / max(dot(dir, camForward), 1e-4);
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
                        && SurfaceSignedGap(cam + dir * carveExitDist) <= 0.0)
                    {
                        OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                                      wetStart);
                        // After the call, which stamps its own id on entry - this is a carve pixel.
                        WaterFogDebugBranch(WATER_FOG_BRANCH_CARVE_MARCH);
                        return;
                    }

                    // ONLY HERE. Suppressing the span outright needs the premise that this pixel is
                    // genuinely water seen FROM THE AIR. A real above-water view is a large contiguous
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
                hit = cam + dir * hitDist;
                pathLen = hitDist;
                deepestY = min(cam.y, hit.y);
                surfaceRefY = camSurf;
                return;
            }

            // NO rasterized surface at this pixel: the analytic classification is the right
            // authority (deep murk, floor views, past the clipmap - places the sheet never
            // drew, where the skybox cannot masquerade as a waterline). Ordered AFTER the
            // prepass on purpose - see the authority note above.
            WaterFogDebugBranch(WATER_FOG_BRANCH_ANALYTIC);
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

            // Mixed ray with NO prepass sample: the analytic fallback (flat crossing + the
            // carve handoff to the validated marcher) prices the crossing.
            {
                // No surface rasterised at this pixel. TWO causes land here and they must not
                // share an answer invented on the spot:
                //  * the far horizon past the clipmap, or a straight-down look. Open water: the flat
                //    rest-plane crossing has always been right for it, and stays untouched.
                //  * an exclusion volume DISCARDED the sheet (WaterSurface's carve discard). There
                //    the flat rest plane is simply the WRONG waterline: the exclusion wall
                //    classifies against the DISPLACED surface (SurfaceHeightAtXZ), so a flat fog
                //    line sat a full wave amplitude away from it - the hole between the waterline
                //    and the fog. Hand those pixels to OceanWavyPath: the SAME crossing search the
                //    non-prepass tier runs, against the SAME displaced surface the wall uses, so the
                //    two waterlines are ONE curve by construction instead of by agreement.
                //    It also removes the old "band above water" on a sealed room's walls without any
                //    camera-height guard: the wavy crossing lands INSIDE the room, so
                //    ExclusionRayLength carves that whole span away and the air is never fogged.
                //    (The flat crossing landed OUTSIDE the box, uncarved - which is why that band
                //    existed and why it needed a guard that flipped with the eye's height.)
                // Deliberately NOT a bespoke refine here: an earlier attempt bisected a +-band
                // bracket five times, quantising the crossing to ~30 cm and printing steps.
                // OceanWavyPath's fixed 1.5 m march + refine is the validated resolution.
                float dySafe = ray.y + (ray.y >= 0.0 ? 1e-4 : -1e-4);
                float tFlat = saturate((_VolumeCenter.y - cam.y) / dySafe);
                hit = cam + ray * tFlat;
                bool overCarve = _ExclusionCount > 0.5
                              && (_CameraDryVolume > 0.5 || InsideExclusion(hit));
                if (overCarve)
                {
                    OceanWavyPath(sceneWorld, cam, rayStartsWet, pathLen, deepestY, surfaceRefY,
                                  wetStart);
                    // AFTER the call, which stamps WAVY_MARCH on entry: this pixel is a carve
                    // pixel that happens to be priced by the marcher, and the view must say so.
                    WaterFogDebugBranch(WATER_FOG_BRANCH_CARVE_MARCH);
                    return;
                }
            }

            WaterFogDebugBranch(WATER_FOG_BRANCH_FLAT_FALLBACK);
            float3 underEnd = sceneUnder ? sceneWorld : cam;
            pathLen = length(underEnd - hit);
            deepestY = min(hit.y, underEnd.y);
            surfaceRefY = sceneUnder ? sceneSurf : camSurf; // surface above the submerged endpoint
            wetStart = rayStartsWet ? cam : hit;            // wet span runs [start -> far end] along the ray
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

            float2 hit = IntersectCube(originPool, rayPool, POOL_WATER_BOX_MIN, POOL_WATER_BOX_MAX);
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
        float ArmWeight(float2 uv, out float classifyGap, out float classifyPushDist)
        {
            // Bounded bodies are a finite fog VOLUME meant to be seen from OUTSIDE (circle a pond
            // and look into the murk), so they are never masked by the eye's own waterline; their
            // rays always start inside the box the pond path clips to.
            classifyGap = -1.0;
            classifyPushDist = 0.0;
            if (_UnderwaterUnbounded < 0.5) return 1.0;
            float3 classifyPoint = WaterlineClassifyPoint(uv, classifyPushDist);
#ifdef WATER_FOG_SIMPLE
            classifyGap = classifyPoint.y - _UnderwaterSurfaceY;
#else
            classifyGap = SurfaceSignedGap(classifyPoint);
#endif
            float overCoverPixels = (_CameraDryVolume > 0.5) ? WATERLINE_CARVE_OVER_COVER_PIXELS
                                                             : 0.0;
            return WaterlineCoverage(classifyGap, fwidth(classifyGap), overCoverPixels);
        }

        // Per-channel path transmittance for this pixel; also returns the depth-darkening term,
        // the sun visibility of the wet span past the exclusion volumes (1 = unshadowed), and the
        // per-pixel waterline mask (see ArmWeight).
        float3 UnderwaterFog(float2 uv, out float3 depthAttenuation, out float sunVisibility,
                             out float armWeight, out float4 debugColor)
        {
            // FIRST, ahead of every per-pixel march below: the waterline mask takes a screen
            // derivative and must be evaluated in uniform control flow.
            float classifyGap;
            float classifyPushDist;
            armWeight = ArmWeight(uv, classifyGap, classifyPushDist);
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
            // classifyGap is now written and never read here. Left in place deliberately rather
            // than pruned from ArmWeight's signature: that is a refactor and this is an experiment
            // awaiting a play-test, and the two must not travel together. (The out-param the debug
            // views actually read is classifyPushDist, via WaterFogDebugColor.)
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
            depthAttenuation = DownwellingAttenuation(deepestY, surfaceRefY);
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
            // multi_compile, NOT shader_feature: this material is created at runtime by
            // CoreUtils.CreateEngineMaterial, so build-time variant stripping would have no material
            // keyword state to inspect and could strip the variant we need. Two keywords x three
            // passes is a trivial variant count.
            // WATER_FOG_SIMPLE : compile out the wavy-crossing machinery (see the fence above).
            // WATER_FOG_NULL : DIAGNOSTIC. Keeps the pass, the two fullscreen draws, the
            // attachments and every RenderGraph dependency byte-identical while making both
            // fragments return their blend identity. It is the A/B that separates "the fog SHADER is
            // expensive" from "the fog PASS is expensive" - a distinction no amount of source
            // reading can settle, and one that decides whether the fix is this keyword split or a
            // structural change (merging the pass, or marching at reduced resolution).
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE
            #pragma multi_compile_fragment _ WATER_FOG_NULL

            half4 FragAbsorb(Varyings input) : SV_Target
            {
#ifdef WATER_FOG_NULL
                return half4(1.0, 1.0, 1.0, 1.0); // Blend Zero SrcColor identity: dst *= 1
#else
                float3 depthAttenuation;
                float sunVisibilityUnused; // absorption is sun-independent; only the in-scatter shadows
                float armWeight;
                float4 debugColor;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibilityUnused,
                                                         armWeight, debugColor);
                // Debug view: WIPE the frame. This pass blends Zero SrcColor (dst *= src), so
                // returning 0 clears the target and the in-scatter pass immediately after - Blend
                // One One - writes the false colour into it. The two passes that already exist ARE
                // the replacement: no extra render pass, no C# change, nothing left behind when off.
                if (debugColor.a > 0.5) return half4(0.0, 0.0, 0.0, 1.0);
                // Per-pixel arm fade: below-line rays are full-strength instantly (weight 1); only
                // the through-surface murk eases in, so the gate can flip a frame early/late with
                // no visible change (at murk weight 0 the multiplier is 1 = scene untouched).
                float3 absorb = lerp(float3(1.0, 1.0, 1.0), pathTransmittance * depthAttenuation,
                                     armWeight);
                return half4(absorb + FogDither(input.positionCS.xy), 1.0);
#endif
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
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE
            #pragma multi_compile_fragment _ WATER_FOG_NULL

            half4 FragInscatter(Varyings input) : SV_Target
            {
#ifdef WATER_FOG_NULL
                return half4(0.0, 0.0, 0.0, 1.0); // Blend One One identity: dst += 0
#else
                float3 depthAttenuation;
                float sunVisibility;
                float armWeight;
                float4 debugColor;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibility,
                                                         armWeight, debugColor);
                // Additive onto the target the absorb pass just cleared: this IS the view.
                if (debugColor.a > 0.5) return half4(debugColor.rgb, 1.0);
                // Lit in-scatter target: the same WaterInscatterColor the surface uses, so the fog colour
                // seen from below matches the water colour seen from above (continuous across the waterline).
                // The view ray is surface->camera, reconstructed from the scene depth. WaterInscatterColor
                // returns the flat _WaterFogColor when scattering is off, so this is a no-op until enabled.
                float3 sceneWorld = SceneWorldPos(input.uv);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - sceneWorld);
                // Sun colour attenuated by the exclusion-volume sun visibility: only the DIRECT
                // term darkens (WaterInscatterColor's ambient term ignores sunColor), so the
                // carve shadow reads as a lit fog losing its beam, never as black.
                float3 fogColor = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunVisibility, 0.0);
                // Overall floor multiplier on top: with Volume Scatter OFF the flat fog colour
                // ignores sunColor entirely, which made the carve shadow invisible on flat-fog
                // bodies; this keeps a visible (never black) shadow column in both modes.
                fogColor *= lerp(EXCLUSION_SHADOW_FLOOR, 1.0, sunVisibility);
                float3 inscatter = fogColor * (1.0 - pathTransmittance);
                // Per-pixel arm fade: additive term scales straight to 0, mirroring the absorb pass.
                inscatter *= armWeight;
                return half4(inscatter * depthAttenuation + FogDither(input.positionCS.xy), 1.0);
#endif
            }
            ENDHLSL
        }

        // ---- Pass 2: screen-space waterline meniscus (partial submersion) ----
        // A thin surface-tension darkening along the ON-SCREEN waterline when the camera sits at
        // the surface. Each pixel evaluates the signed gap of its NEAR-PLANE point against the
        // displaced surface (SurfaceSignedGap; Simple tiers: the flat waterline at
        // _UnderwaterSurfaceY, matching the fog), and the gap is converted to SCREEN PIXELS
        // through its own screen derivative, so the band holds a constant pixel thickness at any
        // FOV / aspect / camera roll / resolution. Enqueued only while the near plane straddles
        // the surface (WaterVolume.WaterlineActive) - including the frames where the binary
        // submerge gate still reads 'above', which used to show a raw hard cut at the crossing.
        Pass
        {
            Name "WaterUnderwaterFogWaterline"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragWaterline
            #pragma target 4.0
            // No WATER_FOG_NULL here: the meniscus is a straddle-frame effect, not part of the
            // sustained underwater cost the diagnostic is aimed at.
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE

            float _WaterlineWidthPx;  // meniscus band thickness, screen pixels
            float _WaterlineStrength; // meniscus opacity at the crossing
            float _WaterlineWarp;     // lens-tension warp weight (0 = plain darkened line)
            // The scene as fogged so far, copied by the pass (a raster pass cannot read its own
            // target); the tension warp re-samples it at a shifted UV.
            TEXTURE2D(_WaterlineSceneTex); SAMPLER(sampler_WaterlineSceneTex);

            // Guard for the metres-per-pixel derivative (degenerate at a perfectly surface-
            // parallel view), and the alpha below which the fragment discards instead of
            // paying the blend.
            #define WATERLINE_METERS_PER_PIXEL_MIN 1e-5
            #define WATERLINE_MIN_ALPHA            0.004
            // Lens tension (KWS half-line): warp band width as a multiple of the line width, the
            // maximum UV pull at full knob (screen fractions), and the coverage fade-in edge.
            #define WATERLINE_WARP_BAND_SCALE      6.0
            #define WATERLINE_WARP_MAX             0.06
            #define WATERLINE_WARP_COVER_EDGE      0.15

            half4 FragWaterline(Varyings input) : SV_Target
            {
                // World position of this pixel ON the near plane (not the scene depth): the
                // meniscus sits on the camera's 'lens' - exactly the near-plane cut through
                // the water surface.
                float3 nearWorld = ComputeWorldSpacePosition(input.uv, UNITY_NEAR_CLIP_VALUE,
                                                             UNITY_MATRIX_I_VP);
                // Dry-interior exclusion (boat cockpit at eye level): no water at the near
                // plane means no meniscus. WGSL-safe: discard demotes the invocation while
                // helpers keep feeding the fwidth below, the same contract the surface
                // shader's exclusion discard relies on.
                if (InsideExclusion(nearWorld)) discard;
                // Bounded body: the surface (and so its waterline) ends at the pool footprint.
                if (_UnderwaterUnbounded < 0.5)
                {
                    float3 nearPool = WorldToPool(nearWorld);
                    if (max(abs(nearPool.x), abs(nearPool.z)) > 1.0) discard;
                }
#ifdef WATER_FOG_SIMPLE
                float gap = nearWorld.y - _UnderwaterSurfaceY;
#else
                float gap = SurfaceSignedGap(nearWorld);
#endif
                // Metres of gap per screen pixel at this pixel (derivatives in uniform control
                // flow, WGSL-safe): dividing by it turns the world gap into a pixel distance
                // from the line, making the band thickness a true pixel count.
                float metersPerPixel = max(fwidth(gap), WATERLINE_METERS_PER_PIXEL_MIN);
                float pixelsFromLine = abs(gap) / metersPerPixel;
                float band = 1.0 - smoothstep(0.0, max(_WaterlineWidthPx, 1.0), pixelsFromLine);
                float lineAlpha = band * _WaterlineStrength;

                // Lens tension (KWS half-line): in a wider band around the line, re-sample the
                // scene pulled toward the AIR side, so the water appears to grip and climb the
                // lens while crossing. The m*(1-m) curve is zero AT the line and at the band
                // edge, peaking between - the image bulges beside the line, not on it. Uniform
                // branch (_WaterlineWarp is a global), and all derivatives sit above it.
                float gapPerUvY = ddy(gap);
                if (_WaterlineWarp > 0.0)
                {
                    float warpBandPx = max(_WaterlineWidthPx, 1.0) * WATERLINE_WARP_BAND_SCALE;
                    float m = 1.0 - saturate(pixelsFromLine / warpBandPx);
                    float offset = _WaterlineWarp * WATERLINE_WARP_MAX * 4.0 * m * (1.0 - m);
                    // ddy(gap)'s sign says which way screen-y runs relative to the surface, so
                    // the pull points toward the air side on every platform orientation and
                    // under camera roll. If the grip visibly pulls the WRONG way, flip upSign.
                    float upSign = (gapPerUvY >= 0.0) ? 1.0 : -1.0;
                    float2 warpedUV = saturate(input.uv + float2(0.0, upSign * offset));
                    float3 scene = SAMPLE_TEXTURE2D_LOD(_WaterlineSceneTex,
                                                        sampler_WaterlineSceneTex, warpedUV, 0).rgb;
                    scene *= 1.0 - lineAlpha; // the meniscus darken rides the warped image
                    float coverage = smoothstep(0.0, WATERLINE_WARP_COVER_EDGE, m);
                    clip(coverage - WATERLINE_MIN_ALPHA);
                    return half4(scene, coverage);
                }

                clip(lineAlpha - WATERLINE_MIN_ALPHA); // off-band pixels: no blend cost
                return half4(0.0, 0.0, 0.0, lineAlpha); // pure darkening, like the chunk wall meniscus
            }
            ENDHLSL
        }
    }
}
