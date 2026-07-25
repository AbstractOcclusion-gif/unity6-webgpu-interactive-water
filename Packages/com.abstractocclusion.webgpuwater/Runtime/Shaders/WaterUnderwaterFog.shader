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
        #include "WaterWaterline.hlsl"  // SurfaceHeightAtXZ / SurfaceSignedGap: the displaced wavy waterline (verbatim move)

        float _UnderwaterSurfaceY;
        float _UnderwaterUnbounded; // 1 = ocean half-space, 0 = clip to this body's box (pond)
        float _UnderwaterFogSimple; // 1 = tier Simple mode: flat waterline, skip the crossing march
        // Ocean-surface eye-depth prepass (KWS-style rendered waterline): the DISPLACED surface's
        // linear eye depth per pixel (0 = no surface rasterised there), written by WaterSurface's
        // "OceanSurfaceEyeDepth" pass via WaterUnderwaterFogPass. When valid, the fog's crossing
        // comes from this - the rendered surface itself - instead of the bounded analytic march.
        TEXTURE2D(_OceanSurfaceEyeDepth);
        float _OceanSurfaceDepthValid; // 1 = the prepass ran this frame (set by the fog pass)
        // Sun globals (published by WaterUniformPublisher) - not in this shader's include chain otherwise.
        // Needed so the underwater in-scatter can use the same lit WaterInscatterColor as the surface, for a
        // continuous colour crossing the waterline.
        float3 _LightDir;
        float3 _SunColor;

        // Per-pixel wavy-waterline crossing search (U2). The camera->scene ray meets the DISPLACED surface
        // at a height that follows crests/troughs, so we bracket the FIRST sign change of
        // (rayY - SurfaceHeightAtXZ) with a constant-step coarse scan and refine by bisection. Constant
        // step/iteration counts keep this fullscreen pass cheap and allocation-free.
        #define UNDERWATER_CROSS_REFINE_ITERS 5
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
        // Arm-fade split distances (ArmWeightFor): a wet span starting within NEAR metres of the
        // camera is the LENS region (submerged lens pixels - including half-submersion, where the
        // crossing sits centimetres in front of the eye) and gets full fog instantly; a span
        // starting past FAR is genuine from-above murk on water ahead and takes the approach
        // ramp. Blended between.
        #define ARM_LENS_NEAR 0.5
        #define ARM_LENS_FAR  2.0
        // The murk's approach ramp: full when the camera reaches the surface, zero this many
        // metres ABOVE it. SPATIAL, current-frame (the KWS lesson taken fully - no temporal
        // state anywhere): the transition lasts exactly as long as the physical crossing, at any
        // wave speed, so it can never read slow or drag behind the water. KEEP smaller than
        // WaterVolume's FogArmBandMeters, so the pass only toggles where this fade is already 0.
        #define MURK_FADE_ABOVE_METERS 0.25
        // camSurfaceY sentinel for bounded bodies: far above any camera, so the murk ramp
        // saturates to 1 (a pond's box fog is a volume seen from ANY side, never gated).
        #define POND_CAM_SURF_SENTINEL 1e8

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
        void OceanWavyPath(float3 sceneWorld, float3 cam,
                           out float pathLen, out float deepestY, out float surfaceRefY,
                           out float3 wetStart, out float camSurfaceY)
        {
            float camSurf = SurfaceHeightAtXZ(cam.xz);
            camSurfaceY = camSurf; // the murk arm-fade's reference (ArmWeightFor)
            float sceneSurf = SurfaceHeightAtXZ(sceneWorld.xz);
            bool camUnder = cam.y <= camSurf;
            bool sceneUnder = sceneWorld.y <= sceneSurf;
            wetStart = cam; // start of the in-water span ALONG the ray (exclusion subtraction origin)

            // Whole segment on one side of the surface: no crossing to search for.
            if (camUnder && sceneUnder)
            {
                pathLen = length(sceneWorld - cam);
                deepestY = min(cam.y, sceneWorld.y);
                surfaceRefY = (cam.y <= sceneWorld.y) ? camSurf : sceneSurf;
                return;
            }
            if (!camUnder && !sceneUnder)
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
            wetStart = camUnder ? cam : hit;                // wet span runs [start -> far end] along the ray
        }

        // Rendered-surface ocean path (the KWS trick): the crossing is the DISPLACED surface's own
        // eye depth at this pixel, so the fog waterline matches the drawn waves EXACTLY at any
        // distance - no march, no step cap, no flat-plane fallback mismatch at long range. Pixels
        // with no surface rasterised (looking straight down at the floor, or past the clipmap's
        // reach) fall back to the flat rest-plane crossing, exactly like the march's own far
        // fallback. Structure mirrors OceanWavyPath so the outputs stay drop-in compatible.
        void OceanPrepassPath(float2 uv, float3 sceneWorld, float3 cam,
                              out float pathLen, out float deepestY, out float surfaceRefY,
                              out float3 wetStart, out float camSurfaceY)
        {
            float camSurf = SurfaceHeightAtXZ(cam.xz);
            camSurfaceY = camSurf; // the murk arm-fade's reference (ArmWeightFor)
            float sceneSurf = SurfaceHeightAtXZ(sceneWorld.xz);
            bool camUnder = cam.y <= camSurf;
            bool sceneUnder = sceneWorld.y <= sceneSurf;
            wetStart = cam;

            // Whole segment on one side of the surface: no crossing to look up.
            if (camUnder && sceneUnder)
            {
                pathLen = length(sceneWorld - cam);
                deepestY = min(cam.y, sceneWorld.y);
                surfaceRefY = (cam.y <= sceneWorld.y) ? camSurf : sceneSurf;
                return;
            }
            if (!camUnder && !sceneUnder)
            {
                pathLen = 0.0;
                deepestY = _VolumeCenter.y;
                surfaceRefY = camSurf;
                return;
            }

            // Mixed: take the crossing from the rendered surface's eye depth at this pixel.
            float3 ray = sceneWorld - cam;
            float rayLen = max(length(ray), 1e-4);
            float3 dir = ray / rayLen;
            float surfaceEye = LOAD_TEXTURE2D(_OceanSurfaceEyeDepth,
                                              int2(uv * _ScaledScreenParams.xy)).r;
            // Eye depth is view-space Z; divide by the ray/forward cosine for distance along the ray.
            float3 camForward = -UNITY_MATRIX_V[2].xyz;
            float hitDist = surfaceEye / max(dot(dir, camForward), 1e-4);
            float3 hit;
            if (surfaceEye > 0.0 && hitDist < rayLen)
            {
                hit = cam + dir * hitDist;
            }
            else
            {
                // No surface at this pixel: flat rest-plane crossing (the march's far fallback).
                float dySafe = ray.y + (ray.y >= 0.0 ? 1e-4 : -1e-4);
                hit = cam + ray * saturate((_VolumeCenter.y - cam.y) / dySafe);
            }

            float3 underEnd = sceneUnder ? sceneWorld : cam;
            pathLen = length(underEnd - hit);
            deepestY = min(hit.y, underEnd.y);
            surfaceRefY = sceneUnder ? sceneSurf : camSurf; // surface above the submerged endpoint
            wetStart = camUnder ? cam : hit;                // wet span runs [start -> far end] along the ray
        }

        // Simple-mode ocean path (tier budget path): the closed-form in-water span against the FLAT
        // waterline at _UnderwaterSurfaceY - the CPU-published, wave-aware surface height at the
        // CAMERA's xz, the same height that arms the submerge gate, so the fog and the gate can never
        // disagree at the eye (and the waterline still rides the local swell as the camera bobs).
        // No march, no per-pixel wave evaluation: a handful of ALU ops replaces up to
        // UNDERWATER_CROSS_MAX_STEPS surface evaluations per pixel.
        void OceanFlatPath(float3 sceneWorld, float3 cam,
                           out float pathLen, out float deepestY, out float surfaceRefY,
                           out float3 wetStart, out float camSurfaceY)
        {
            float level = _UnderwaterSurfaceY;
            camSurfaceY = level; // the murk arm-fade's reference (flat, matching this tier's waterline)
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

        // World-space length of the in-water part of the camera->scene ray, the deepest submerged point's
        // world Y (for downwelling), and the displaced surface height above it (the depth reference).
        // pathLen 0 = this pixel's ray never enters the water.
        void UnderwaterSegment(float2 uv, float3 sceneWorld, out float pathLen, out float deepestY,
                               out float surfaceRefY, out float3 wetStart, out float camSurfaceY)
        {
            float3 cam = _WorldSpaceCameraPos;
            // Bounded bodies: sentinel far above any camera, so the murk arm-fade saturates to 1
            // (their box fog is a volume seen from any side, never a gated fullscreen state).
            camSurfaceY = POND_CAM_SURF_SENTINEL;

            if (_UnderwaterUnbounded > 0.5)
            {
                // Ocean: the below-surface span. All three gates are uniforms, so the branch is
                // coherent across the screen - Simple tiers never pay for the wavy march, and when
                // the rendered-surface prepass ran, nobody does (the crossing is a texture load).
                if (_UnderwaterFogSimple > 0.5)
                    OceanFlatPath(sceneWorld, cam, pathLen, deepestY, surfaceRefY, wetStart, camSurfaceY);
                else if (_OceanSurfaceDepthValid > 0.5)
                    OceanPrepassPath(uv, sceneWorld, cam, pathLen, deepestY, surfaceRefY, wetStart, camSurfaceY);
                else
                    OceanWavyPath(sceneWorld, cam, pathLen, deepestY, surfaceRefY, wetStart, camSurfaceY);
                return;
            }

            // Pond: clip the ray to the pool water box in pool space ([-1,1] xz, [-1,0] y). Working in
            // pool space lets one IntersectCube handle the surface top AND the walls/floor at once.
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
            if (_UnderwaterFogSimple < 0.5)
                enterWorld = ClampEntryToSurface(enterWorld, exitWorld);

            pathLen = length(exitWorld - enterWorld);
            deepestY = min(enterWorld.y, exitWorld.y);
            surfaceRefY = (_UnderwaterFogSimple > 0.5)
                        ? _VolumeCenter.y                    // flat rest plane (matches the box top)
                        : SurfaceHeightAtXZ(enterWorld.xz);  // wavy surface above the entry, for downwelling
            wetStart = enterWorld;
        }

        // The shadow-column terms (EXCLUSION_SHADOW_FLOOR, the analytic span sun visibility)
        // live in WaterExclusion.hlsl: the exclusion wall's above-water fog reconstruction
        // shares them, so both views of the carve shade identically.

        // Per-pixel arm fade (the KWS lesson): KWS/Crest have NO temporal fade - their underwater
        // effect is binary per pixel against the RASTERIZED surface, frame-exact by construction,
        // with the meniscus hiding the edge. Match that where it matters: a ray that STARTS in
        // water gets FULL fog instantly (the below-line lens region - its edge is the per-pixel
        // waterline, current-frame GPU math, no readback staleness). Only the through-surface murk
        // (camera-above rays crossing into the water ahead) fades - and even that fade is SPATIAL,
        // driven by the camera's live height above the surface, never by time: the gate can flip
        // a frame early/late for free because the murk is already zero that far from the water.
        float ArmWeightFor(float3 wetStart, float pathLen, float camSurfaceY)
        {
            if (pathLen <= 0.0) return 1.0; // no fog on this ray; weight is moot
            float heightAbove = _WorldSpaceCameraPos.y - camSurfaceY;
            float murkWeight = 1.0 - saturate(heightAbove / MURK_FADE_ABOVE_METERS);
            // Distance to where this ray's water STARTS: ~0..near-plane for the submerged part
            // of the lens, metres for genuine from-above murk. Smooth blend so no ring appears
            // where the two regimes meet during a half-submerged frame.
            float startDist = distance(wetStart, _WorldSpaceCameraPos);
            float murkiness = smoothstep(ARM_LENS_NEAR, ARM_LENS_FAR, startDist);
            return lerp(1.0, murkWeight, murkiness);
        }

        // Per-channel path transmittance for this pixel; also returns the depth-darkening term,
        // the sun visibility of the wet span past the exclusion volumes (1 = unshadowed), and the
        // per-pixel arm-fade weight (see ArmWeightFor).
        float3 UnderwaterFog(float2 uv, out float3 depthAttenuation, out float sunVisibility,
                             out float armWeight)
        {
            float3 sceneWorld = SceneWorldPos(uv);
            float pathLen;
            float deepestY;
            float surfaceRefY;
            float3 wetStart;
            float camSurfaceY;
            UnderwaterSegment(uv, sceneWorld, pathLen, deepestY, surfaceRefY, wetStart, camSurfaceY);
            armWeight = ArmWeightFor(wetStart, pathLen, camSurfaceY);
            // Dry-interior exclusion: the part of the wet span that crosses an exclusion volume is
            // AIR, so carve it out of the fog integral. Zero volumes = the loops never run. When
            // the whole span is dry (camera in a submerged room looking at its own wall), the
            // depth-darkening reference resets so the dry interior is not darkened as if it were
            // under water.
            float3 seg = sceneWorld - _WorldSpaceCameraPos;
            float3 segDir = seg / max(length(seg), 1e-5);
            float wetSpanLen = pathLen; // pre-carve span length (wetStart -> wet end, world metres)
            pathLen = max(pathLen - ExclusionRayLength(wetStart, segDir, pathLen), 0.0);
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
            return exp(-_WaterExtinction.rgb * (density * pathLen));
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

            half4 FragAbsorb(Varyings input) : SV_Target
            {
                float3 depthAttenuation;
                float sunVisibilityUnused; // absorption is sun-independent; only the in-scatter shadows
                float armWeight;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibilityUnused,
                                                         armWeight);
                // Per-pixel arm fade: below-line rays are full-strength instantly (weight 1); only
                // the through-surface murk eases in, so the gate can flip a frame early/late with
                // no visible change (at murk weight 0 the multiplier is 1 = scene untouched).
                float3 absorb = lerp(float3(1.0, 1.0, 1.0), pathTransmittance * depthAttenuation,
                                     armWeight);
                return half4(absorb + FogDither(input.positionCS.xy), 1.0);
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

            half4 FragInscatter(Varyings input) : SV_Target
            {
                float3 depthAttenuation;
                float sunVisibility;
                float armWeight;
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibility,
                                                         armWeight);
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
                float gap = (_UnderwaterFogSimple > 0.5)
                          ? nearWorld.y - _UnderwaterSurfaceY
                          : SurfaceSignedGap(nearWorld);
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
