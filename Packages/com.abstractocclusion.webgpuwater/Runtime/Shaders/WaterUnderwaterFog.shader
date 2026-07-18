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
        #include "WaterWaves.hlsl"      // WaveHeight: wind-wave layer for the per-pixel waterline
        #include "WaterLargeWaves.hlsl" // LargeBodyWaveHeight: open-water swell/FFT; needs _WaveTime (above)

        float _UnderwaterSurfaceY;
        float _UnderwaterUnbounded; // 1 = ocean half-space, 0 = clip to this body's box (pond)
        float _UnderwaterFogSimple; // 1 = tier Simple mode: flat waterline, skip the crossing march
        // Sun globals (published by WaterUniformPublisher) - not in this shader's include chain otherwise.
        // Needed so the underwater in-scatter can use the same lit WaterInscatterColor as the surface, for a
        // continuous colour crossing the waterline.
        float3 _LightDir;
        float3 _SunColor;
        float _OceanWorldWaves;     // 1 = sample wind waves in WORLD metres (ocean); 0 = pool xz (pond)

        // Per-pixel wavy-waterline crossing search (U2). The camera->scene ray meets the DISPLACED surface
        // at a height that follows crests/troughs, so we bracket the FIRST sign change of
        // (rayY - SurfaceHeightAtXZ) with a constant-step coarse scan and refine by bisection. Constant
        // step/iteration counts keep this fullscreen pass cheap and allocation-free.
        #define UNDERWATER_CROSS_REFINE_ITERS 5
        #define WAVE_METERS_MIN 1e-3 // matches WindWaveSampleXZ's guard in WaterSurface.shader
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
        #define UNDERWATER_SURF_SETAMP_MAX   1.1 // max SurfSetAmp jitter (see WaterSurfWaves.SurfSetAmp)

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

        // Displaced world-space surface height at a WORLD xz: the single source of truth for the wavy
        // waterline. Rest plane (via the volume transform, so extent.y + rotation are exact, matching
        // TryGetAnalyticWaterline) + wind-wave layer + open-water swell/FFT. Pools: the swell is a no-op
        // (_LargeBody = 0), so this reduces to the wind-wave surface over the flat pool top.
        float SurfaceHeightAtXZ(float2 worldXZ)
        {
            // Map to pool xz at the rest plane; the surface shader samples the wind waves off this xz.
            float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
            float2 poolXZ = poolAtRest.xz;

            // Oceans sample the wind waves in WORLD metres (extent-independent) to match WindWaveSampleXZ.
            float2 windSampleXZ = (_OceanWorldWaves > 0.5) ? (worldXZ / max(_WaveMetersPerUnit, WAVE_METERS_MIN))
                                                           : poolXZ;
            // Wind-wave height is authored in pool units; lift it to world through the full transform,
            // exactly as the vertex path does (PoolToWorld of the displaced pool point).
            float surfaceY = PoolToWorld(float3(poolXZ.x, WaveHeight(windSampleXZ), poolXZ.y)).y;

            // Open-water swell/FFT is authored in WORLD metres and layered on top (no-op for pools).
            if (_LargeBody > 0.5) surfaceY += LargeBodyWaveHeight(worldXZ);
            return surfaceY;
        }

        // Signed height of a world point above its local displaced surface (>0 in air, <=0 underwater).
        float SurfaceSignedGap(float3 world)
        {
            return world.y - SurfaceHeightAtXZ(world.xz);
        }

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
                           out float pathLen, out float deepestY, out float surfaceRefY)
        {
            float camSurf = SurfaceHeightAtXZ(cam.xz);
            float sceneSurf = SurfaceHeightAtXZ(sceneWorld.xz);
            bool camUnder = cam.y <= camSurf;
            bool sceneUnder = sceneWorld.y <= sceneSurf;

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
                            ? _SurfAmplitude * UNDERWATER_SURF_SETAMP_MAX * max(_SurfGreens, 1.0)
                            : 0.0;
            float band = max(abs(_LargeWaveAmplitude) * UNDERWATER_SURFACE_BAND_AMPS, surfReach)
                       + UNDERWATER_SURFACE_BAND_PAD;
            float tFlat = (restY - cam.y) / dySafe;              // flat rest-plane crossing (ray parameter)
            float tBand = band / max(abs(ray.y), 1e-4);          // half-band in ray-parameter units
            float startDist = saturate(tFlat - tBand) * rayLen;  // skip the deep water below the band
            float3 prev = cam + dir * startDist;
            float gapPrev = SurfaceSignedGap(prev);
            float3 hit = cam + ray * saturate(tFlat);            // fallback: flat waterline (far horizon)
            [loop]
            for (int s = 1; s <= UNDERWATER_CROSS_MAX_STEPS; s++)
            {
                float d = startDist + s * UNDERWATER_CROSS_STEP_METRES;
                if (d >= rayLen) break;                          // reached the scene end
                float3 p = cam + dir * d;
                float gap = SurfaceSignedGap(p);
                if (gapPrev * gap <= 0.0) { hit = RefineSurfaceCrossing(prev, gapPrev, p); break; }
                prev = p; gapPrev = gap;
            }

            float3 underEnd = sceneUnder ? sceneWorld : cam;
            pathLen = length(underEnd - hit);
            deepestY = min(hit.y, underEnd.y);
            surfaceRefY = sceneUnder ? sceneSurf : camSurf; // surface above the submerged endpoint
        }

        // Simple-mode ocean path (tier budget path): the closed-form in-water span against the FLAT
        // waterline at _UnderwaterSurfaceY - the CPU-published, wave-aware surface height at the
        // CAMERA's xz, the same height that arms the submerge gate, so the fog and the gate can never
        // disagree at the eye (and the waterline still rides the local swell as the camera bobs).
        // No march, no per-pixel wave evaluation: a handful of ALU ops replaces up to
        // UNDERWATER_CROSS_MAX_STEPS surface evaluations per pixel.
        void OceanFlatPath(float3 sceneWorld, float3 cam,
                           out float pathLen, out float deepestY, out float surfaceRefY)
        {
            float level = _UnderwaterSurfaceY;
            pathLen = WaterPathLength(sceneWorld, cam, level);
            // min against 'level' makes an in-air endpoint contribute its crossing at the waterline,
            // so the deepest submerged point is exact in every camera-above/below combination.
            deepestY = min(level, min(cam.y, sceneWorld.y));
            surfaceRefY = level;
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
        void UnderwaterSegment(float3 sceneWorld, out float pathLen, out float deepestY, out float surfaceRefY)
        {
            float3 cam = _WorldSpaceCameraPos;

            if (_UnderwaterUnbounded > 0.5)
            {
                // Ocean: the below-surface span. _UnderwaterFogSimple is a uniform, so this branch is
                // coherent across the screen - Simple tiers never pay for the wavy march.
                if (_UnderwaterFogSimple > 0.5)
                    OceanFlatPath(sceneWorld, cam, pathLen, deepestY, surfaceRefY);
                else
                    OceanWavyPath(sceneWorld, cam, pathLen, deepestY, surfaceRefY);
                return;
            }

            // Pond: clip the ray to the pool water box in pool space ([-1,1] xz, [-1,0] y). Working in
            // pool space lets one IntersectCube handle the surface top AND the walls/floor at once.
            float3 originPool = WorldToPool(cam);
            float3 scenePool = WorldToPool(sceneWorld);
            float3 rayPool = scenePool - originPool;
            float sceneT = length(rayPool);
            rayPool /= max(sceneT, 1e-5);

            float2 hit = IntersectCube(originPool, rayPool, float3(-1.0, -1.0, -1.0), float3(1.0, 0.0, 1.0));
            float tEnter = max(hit.x, 0.0);
            float tExit = min(hit.y, sceneT); // never fog past the scene surface
            if (tExit <= tEnter)
            {
                pathLen = 0.0;
                deepestY = _UnderwaterSurfaceY;
                surfaceRefY = _UnderwaterSurfaceY;
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
        }

        // Per-channel path transmittance for this pixel; also returns the depth-darkening term.
        float3 UnderwaterFog(float2 uv, out float3 depthAttenuation)
        {
            float3 sceneWorld = SceneWorldPos(uv);
            float pathLen;
            float deepestY;
            float surfaceRefY;
            UnderwaterSegment(sceneWorld, pathLen, deepestY, surfaceRefY);
            depthAttenuation = DownwellingAttenuation(deepestY, surfaceRefY);
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
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation);
                return half4(pathTransmittance * depthAttenuation + FogDither(input.positionCS.xy), 1.0);
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
                float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation);
                // Lit in-scatter target: the same WaterInscatterColor the surface uses, so the fog colour
                // seen from below matches the water colour seen from above (continuous across the waterline).
                // The view ray is surface->camera, reconstructed from the scene depth. WaterInscatterColor
                // returns the flat _WaterFogColor when scattering is off, so this is a no-op until enabled.
                float3 sceneWorld = SceneWorldPos(input.uv);
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - sceneWorld);
                float3 fogColor = WaterInscatterColor(viewDirWS, _LightDir, _SunColor, 0.0);
                float3 inscatter = fogColor * (1.0 - pathTransmittance);
                return half4(inscatter * depthAttenuation + FogDither(input.positionCS.xy), 1.0);
            }
            ENDHLSL
        }
    }
}
