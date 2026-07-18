// WebGL Water - water surface (Unity 6 / URP port)
// Hybrid reflection (analytic sky/pool -> planar -> SSR) and refraction (analytic
// pool, or real screen-space refraction of the live scene). All extras are
// keyword-gated and default off, so the base look matches the original.
// One material is instanced twice by the scene builder: an "above water" object
// (_Underwater = 0, Cull Front) and an "under water" object (_Underwater = 1,
// Cull Back), sharing the same displaced grid mesh.
Shader "AbstractOcclusion/WebGpuWater/WaterSurface"
{
    Properties
    {
        _Underwater ("Underwater (0/1)", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1 // Front
        // Reflection + refraction are driven by the WaterVolume component (Reflections foldout) -
        // the single place to configure them. Kept as [HideInInspector] so the shader keeps their
        // defaults + variants and the component can seed from / publish to them, without cluttering
        // the material inspector.
        [HideInInspector] _ReflectionStrength ("Reflection Strength", Range(0,1)) = 1.0
        [HideInInspector] _EnvReflectionIntensity ("Env Reflection Intensity", Range(0,4)) = 1.0
        [HideInInspector] _UsePlanar ("Use Planar Reflection", Float) = 0
        [HideInInspector] _UseSSR ("Use Screen Space Reflection", Float) = 0
        // Read by C# ONLY (WaterUniformPublisher seeds the volume's reflection mode from it);
        // the pass itself never reads it, so it has no shader uniform.
        [HideInInspector] _UseUrpProbe ("Reflect URP Environment Probe (else procedural sky)", Float) = 0
        [HideInInspector] _ReflectionDistortion ("Reflection Distortion", Range(0,0.2)) = 0.05
        [HideInInspector] _SSRStrength ("SSR Strength", Range(0,1)) = 1.0
        [HideInInspector] _SSRStepSize ("SSR Step Size (world units)", Range(0.005,0.2)) = 0.03
        [HideInInspector] _SSRMaxSteps ("SSR Max Steps", Range(8,64)) = 24
        [HideInInspector] _SSRThickness ("SSR Thickness", Range(0.01,1.0)) = 0.2
        [HideInInspector] _RealRefraction ("Real (Screen-Space) Refraction", Float) = 0
        [HideInInspector] _RefractionDistortion ("Refraction Distortion", Range(0,0.2)) = 0.05
        // Above-water look (WOW pass): physical Schlick Fresnel + GGX sun specular.
        // _FresnelFloor = artistic minimum reflectance (0 = pure physics; the legacy
        // curve behaved like a 0.25 floor, which mirrored the sky even straight down).
        [HideInInspector] _FresnelFloor ("Fresnel Floor (artistic min reflectance)", Range(0,1)) = 0.0
        // Overall shininess: the Schlick grazing exponent (Crest's _Crest_Fresnel knob). 5 =
        // physical; LOWER makes reflectivity rise faster on tilted wave faces, so the whole
        // surface reads glossier with contrast (unlike the floor, which mirrors uniformly).
        [HideInInspector] _FresnelPower ("Fresnel Power (5 = physical, lower = shinier)", Range(1,5)) = 5.0
        // Shared surface roughness (sun lobe width + sky-reflection blur): near value, far value,
        // and the distance ramp between them (Crest's smoothness-far pattern). All published per
        // body by the WaterVolume Reflections foldout.
        [HideInInspector] _SunRoughness ("Roughness (near)", Range(0.01,1)) = 0.08
        [HideInInspector] _RoughnessFar ("Roughness (far)", Range(0.01,1)) = 0.2
        [HideInInspector] _RoughnessFarDistance ("Far Roughness Distance (m)", Range(50,5000)) = 1000
        [HideInInspector] _RoughnessFalloff ("Far Roughness Falloff", Range(0.25,4)) = 1
        // Vertical stretch of the blurred sky reflection (KWS anisotropic look): 0 = off.
        [HideInInspector] _ReflectionAnisoStretch ("Reflection Vertical Stretch", Range(0,1)) = 0.5
        // Dual-lobe sun specular: a second, much broader lobe puts a soft sheen on wave faces
        // far outside the mirror direction (a single lobe leaves them dead). 0 = off.
        [HideInInspector] _SunSheen ("Sun Sheen (broad lobe weight)", Range(0,1)) = 0
        [HideInInspector] _SunSheenRoughness ("Sun Sheen Roughness", Range(0.2,1)) = 0.6
        // Wrapped NoL for the sun lobes: at a grazing (horizon) sun, plain NoL kills the
        // specular exactly when a real sea glitters hardest. 0 = physical NoL (unchanged).
        [HideInInspector] _SunGrazeBoost ("Sun Graze Boost (wrapped NoL)", Range(0,1)) = 0

        [Header(Detail Normals)]
        // Crest-style micro-detail: two CROSSING, SCROLLING samples of a tiling normal map at two
        // world scales, crossfaded by camera distance. The default "bump" map unpacks to a flat
        // normal, so the feature is INERT until a real tiling water-normal texture is assigned -
        // every existing scene is unchanged.
        _DetailNormalTex ("Detail Normal (tiling water normals)", 2D) = "bump" {}
        _DetailNormalStrength ("Detail Normal Strength", Range(0, 2)) = 0.6
        _DetailNormalScale ("Detail Normal Tile (world metres)", Range(1, 100)) = 18
        _DetailNormalSpeed ("Detail Normal Scroll (metres per second)", Range(0, 2)) = 0.25
        // Water fog is global now (driven by WaterController), shared with the
        // object/pool shaders so it's consistent however you view the water.

        [Header(Foam)]
        // Grid (1,1) = a single seamless TILING texture (hardware Repeat, like the ocean
        // whitecap - assign any soft foam tile); a real grid = an animated flipbook whose
        // cells are inset-sampled. Pattern world size comes from the WaterVolume's
        // Foam Pattern Size (published as _FoamTileSize), not this texture's ST.
        _FoamTex ("Foam Pattern (single tile or flipbook)", 2D) = "white" {}
        _FoamTexFrames ("Foam Flipbook Grid (cols, rows)", Vector) = (1, 1, 0, 0)
        _FoamTexFPS ("Foam Flipbook Frame Rate", Range(0, 30)) = 10
        // Relief is derived procedurally from the pattern (Crest-style finite differences,
        // same as the ocean whitecap since its rework) - no foam normal map anymore;
        // materials that still serialize _FoamNormalTex keep it as inert data.
        _FoamNormalStrength ("Foam Relief Strength (procedural)", Range(0, 3)) = 1

        [Header(Ocean Wave Foam)]
        _OceanWhitecapTex ("Ocean Whitecap (single tiling texture)", 2D) = "white" {}
        // Whitecap relief is now derived procedurally from the albedo (Crest-style finite
        // differences), so no whitecap normal-map slot; materials that still serialize
        // _OceanWhitecapNormalTex keep it as inert data.
    }
    SubShader
    {
        // Transparent queue so _CameraOpaqueTexture / _CameraDepthTexture hold the
        // scene WITHOUT the water (required for SSR and screen-space refraction).
        // Still ZWrite On + Blend Off: we compute the final opaque-looking colour
        // ourselves (incl. refraction), we just need to draw after the opaque copy.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            // Main-light shadow keywords: this pass samples the shadow map BY HAND (it is CGPROGRAM, so
            // it can't include URP's Shadows.hlsl) to gate the analytic floor caustic. Needs "Transparent
            // Receive Shadows" ON in the active Renderer asset, else the keyword is never set (caustic
            // stays lit, i.e. the old behaviour).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            // Reflection mode (planar / SSR / URP-probe base / real refraction) is UNIFORM-driven,
            // published per body every frame via the MaterialPropertyBlock (WaterUniformPublisher),
            // so it updates live in the editor and needs no shader variants.
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl" // brings WaterShared (via WaterCommon): POOL_RIM_HEIGHT etc.
            #include "WaterLargeWaves.hlsl" // open-water world-space wave normal (large-body path)
            #include "WaterFoamCommon.hlsl" // shared foam lighting constants/helpers (FOAM_LIGHT_WRAP etc.)
            // ---- Pass-local code split into includes (SHADER-SPLIT-2, verbatim moves).
            // The order is a dependency chain - Screen (depth/UV helpers) -> Shadow (needs
            // the point sampler) -> Specular (SSR needs Screen) -> PoolTrace (needs
            // Specular + Shadow) -> FoamSampling -> DetailNormal - so keep it. ----
            #include "WaterSurfaceScreen.hlsl"
            #include "WaterSurfaceShadow.hlsl"
            #include "WaterSurfaceSpecular.hlsl"
            #include "WaterSurfacePoolTrace.hlsl"
            #include "WaterSurfaceFoamSampling.hlsl"
            #include "WaterSurfaceDetailNormal.hlsl"

            #define SSS_AMPLITUDE_EPSILON   1e-3   // guards the crest/amplitude ratio when the swell is flat
            // Shallow-water clarity (surf run-out): under this column depth the shore band
            // blends toward the refracted ground, so centimetres-deep water reads clear
            // instead of flat opaque blue between the last bore and the beach.
            #define SHALLOW_CLARITY_DEPTH 0.6   // metres; blend fully faded out at this depth
            #define SHALLOW_CLARITY_BLEND 0.5   // max blend toward the refracted colour at depth 0
            // Wet-sand glaze weights (swash zone): the thin film is centimetres of water ON
            // the sand, so it pulls HARD toward the refracted ground (never blue ocean on the
            // beach), and the drying glaze behind it mixes darkened ground + a sky sheen.
            #define WET_FILM_MIN_TRANSPARENCY 0.6    // film pull toward the ground at the waterline
            #define WET_FILM_DEPTH_GAIN       0.3    // extra pull as the film thins up-beach
            #define WET_GLAZE_EDGE            0.25   // smoothstep width of the drying wet edge
            #define WET_GLAZE_REFRACT         0.7    // refracted-ground weight in the wet look
            #define WET_GLAZE_REFLECT         0.12   // reflected-sky weight in the wet look
            #define WET_GLAZE_STRENGTH        0.85   // max glaze opacity over the base shading

            // Peaked-look refine: short steps along the ripple normal sharpen wave crests.
            // The step COUNT is tier-driven (_PeakedRefineSteps via the body's property
            // block): each step is a dependent texture fetch per pixel, the single biggest
            // fragment cost on mobile. The cap bounds the loop for the compiler.
            #define PEAKED_REFINE_MAX_STEPS 8
            #define PEAKED_REFINE_STEP  0.005

            float _Underwater;
            // Camera-following high-detail patch (windowed large bodies): a dense [-1,1] grid
            // remapped into just the sim window's sub-region of pool space, so near-field
            // ripple/wave geometry is sampled densely enough (target ~one vertex per sim texel)
            // to avoid the undersampling shimmer / false ripples a coarse whole-plane mesh shows
            // on big volumes. Inert at the defaults (_IsPatch = 0, _PatchDepthBias = 0).
            float  _IsPatch;          // 0 = normal full-plane surface, 1 = the window patch
            float2 _PatchPoolCenter;  // window centre in pool xz
            float2 _PatchPoolHalf;    // window half-size in pool units (per axis)
            float  _PatchDepthBias;   // view-space metres to pull the patch toward the camera so it wins over the coplanar far plane
            // Unbounded-ocean clipmap: 1 = a camera-following world-locked geometry-clipmap LOD level
            // (authored in INTEGER CELL UNITS, scaled to metres by the transform, reaching the horizon),
            // 0 = pool-grid surfaces. Inert at the default (_IsClipmap = 0).
            float  _IsClipmap;
            // Edge geomorph for a clipmap LOD level: in the outer band (Chebyshev cell distance from the
            // level centre >= _ClipmapMorphStart) the vertex slides onto the next-coarser lattice (nearest
            // EVEN cell) so it meets the coarser level vertex-for-vertex with no T-junction crack.
            // _ClipmapMorphScale = 1 / band width (cells). Inert on the outermost level (start >= M/2).
            float  _ClipmapMorphStart;
            float  _ClipmapMorphScale;
            // 1 = sample the small wind-wave layer in WORLD metres (oceans), so its scale is independent
            // of the volume extent; 0 = pool space (bounded bodies, unchanged). Inert at the default.
            float  _OceanWorldWaves;
            // Distance (metres) at which the ocean surface has fully dissolved into the horizon sky, so
            // the far edge has no hard line. 0 = off (bounded bodies, and until the artist opts in). A
            // light stopgap - the real horizon softening is the (future) large-body fog pass.
            float  _HorizonFadeDistance;
            #define HORIZON_FADE_START 0.5   // fraction of the fade distance where the blend to sky begins
            // Exponential atmospheric horizon haze (supersedes the smoothstep stopgap above): the far
            // ocean dissolves toward the sky by distance with a physical 1 - exp(-density * dist) falloff.
            // _HorizonHazeColor.a tints the sky toward a fixed atmosphere colour (0 = pure sky, seamless).
            // Density 0 = off (bounded bodies, unchanged).
            float4 _HorizonHazeColor;
            float  _HorizonHazeDensity;
            float _WaveNormalStrength; // global; scales the wind-wave tilt on the normal
            float _PeakedRefineSteps;  // per-body (quality tier); see PEAKED_REFINE_MAX_STEPS

            float _RefractionDistortion;

            // Pool-space terrain bed height (R = bed height in pool units), baked by WaterVolume.
            sampler2D _BedTex;

            // Shore depth + SDF uniforms and helpers (Layer A/B) are declared in WaterShore.hlsl,
            // included via WaterLargeWaves.hlsl above; the debug branches below read them directly.

            // Interactive ripple sample (r = height, ba = normal.xz) for a surface point.
            // Whole-body bodies sample the pool UV as before. Windowed bodies sample the
            // camera-following window by WORLD position (sub-texel smooth, world-anchored)
            // and fade the ripple to flat over the last _SimEdgeFadeTexels, so there is no
            // seam where the window meets the analytic-only water. 'fade' is the ripple
            // weight: 1 inside the window, -> 0 at/beyond its border.
            float4 SampleRipple(float3 poolPos, float3 worldPos, out float fade)
            {
                fade = 1.0;
                if (_SimWindowed < 0.5)
                    return SampleWaterBicubic(poolPos.xz * 0.5 + 0.5);

                float2 uv = WorldToSim(worldPos).xz * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) { fade = 0.0; return (float4)0.0; }

                float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
                float2 d = min(uv, 1.0 - uv);
                fade = saturate(min(d.x, d.y) / max(band, 1e-5));

                float4 info = SampleWaterBicubic(uv);
                info.r  *= fade; // fade ripple height
                info.ba *= fade; // fade normal tilt back to flat
                return info;
            }

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0; // POOL space ([-1,1]); drives the analytic tracer
                float4 screenPos: TEXCOORD1;
                float3 worldPos : TEXCOORD2; // world space; drives depth/SSR/foam-contact
                float2 largeWaveSourceXZ : TEXCOORD3; // undisplaced world xz of the open-water wave,
                                                      // so the fragment normal reads the SOURCE point
                                                      // (not the chop-displaced worldPos)
            };

            // Coordinate fed to the wind-wave layer (WaveHeight/WaveSlope). Bounded bodies sample in
            // pool xz, so the wave scale rides the volume extent (worldXZ / extent). Oceans sample in
            // WORLD metres instead, so tweaking the volume box no longer slides/rescales the wind-wave
            // pattern - its scale is set solely by Pool Half Extent Meters (_WaveMetersPerUnit). At a
            // matched extent the two are identical, so this only decouples; it doesn't change the look.
            float2 WindWaveSampleXZ(float2 poolXZ, float2 worldXZ)
            {
                if (_OceanWorldWaves > 0.5) return worldXZ / max(_WaveMetersPerUnit, 1e-3);
                return poolXZ;
            }

            v2f vert(appdata v)
            {
                v2f o;
                // Three vertex sources feed the SAME ripple/wave path below:
                //  - full plane   : the grid vertex IS pool xz;
                //  - window patch : the SAME [-1,1] grid remapped into the window's pool sub-region,
                //                   so it tessellates only the near field (dense);
                //  - ocean clipmap: verts authored in WORLD metres (x,0,z) on a camera-following mesh,
                //                   mapped BACK into pool space so the ripple/pool sampling is unchanged
                //                   (ripples fade to flat past the sim window, leaving open-water swell).
                float3 poolFlat;
                float3 worldFlat;
                if (_IsClipmap > 0.5)
                {
                    // Edge geomorph: in the outer band, slide the vertex onto the next-coarser lattice
                    // (nearest EVEN cell) so this LOD level meets the coarser one crack-free. v.vertex.xz
                    // are this level's integer cell indices; the transform scales them to world metres.
                    float2 cell = v.vertex.xz;
                    float cheb = max(abs(cell.x), abs(cell.y));
                    float morph = saturate((cheb - _ClipmapMorphStart) * _ClipmapMorphScale);
                    float2 morphedCell = lerp(cell, round(cell * 0.5) * 2.0, morph);
                    float3 worldOnPlane = mul(unity_ObjectToWorld, float4(morphedCell.x, 0.0, morphedCell.y, 1.0)).xyz;
                    worldFlat = float3(worldOnPlane.x, _VolumeCenter.y, worldOnPlane.z); // resting plane
                    poolFlat = WorldToPool(worldFlat);
                    poolFlat.y = 0.0;
                }
                else
                {
                    float2 gridPoolXZ = (_IsPatch > 0.5) ? (_PatchPoolCenter + v.vertex.xy * _PatchPoolHalf)
                                                         : v.vertex.xy;
                    poolFlat = float3(gridPoolXZ.x, 0.0, gridPoolXZ.y); // grid -> pool (x, 0, z)
                    worldFlat = PoolToWorld(poolFlat);
                }
                // World position at the surface plane (height 0) picks the windowed UV; the
                // xz mapping doesn't depend on ripple height, so this is exact.
                float2 poolXZ = poolFlat.xz;
                float fade;
                float4 info = SampleRipple(poolFlat, worldFlat, fade);
                float3 position = poolFlat;
                position.y += info.r;                  // interactive ripple heightfield (windowed: faded)
                position.y += WaveHeight(WindWaveSampleXZ(poolXZ, worldFlat.xz)); // small wind-wave detail; open water
                                                       // layers the big swell on top in world space below
                o.position = position;                 // keep pool-space position for the tracer
                float3 worldPos = PoolToWorld(position);
                // Open water: add the wave in WORLD space (metres), so large bodies get real 3D waves
                // whose amplitude is NOT shrunk by the depth extent the way the pool-unit WaveHeight
                // above is. Height lifts Y; choppiness displaces xz (Gerstner) for sharp crests. The
                // SOURCE xz (before the xz displacement) is carried to the fragment so its normal reads
                // the wave at the same point the vertex did. No-op for pool/small bodies (_LargeBody = 0).
                o.largeWaveSourceXZ = worldPos.xz;
                // ONE shore + surf sample per vertex, shared by the wave height, the chop and the
                // swash film block below (the old path re-sampled the shore and re-evaluated the
                // surf fronts inside Height, again inside Displacement, and a third time for the
                // swash - ~2.5x the whole field per vertex). Inert defaults keep pools byte-identical.
                ShoreData shoreVert = ShoreDataInert();
                SurfWaveSample surfVert = SurfWaveSampleInert();
                if (_LargeBody > 0.5)
                {
                    float2 sourceXZ = worldPos.xz;
                    o.largeWaveSourceXZ = sourceXZ;
                    shoreVert = ShoreSample(sourceXZ);
                    surfVert = EvaluateSurfWaves(sourceXZ, shoreVert.depth, shoreVert.sdfDist,
                                                 shoreVert.toShore, shoreVert.slopeTan,
                                                 shoreVert.influence, _SurfBeatTime);
                    // Height + chop from one field evaluation. The far-field band-limit (dropping
                    // short waves the coarse mesh can't resolve, keeping the long swell) lives
                    // INSIDE, driven by camera distance - no-op for bounded bodies.
                    float lbwHeight;
                    float2 lbwDisp;
                    LargeBodyWaveHeightDispShore(sourceXZ, shoreVert, surfVert, lbwHeight, lbwDisp);
                    worldPos.y  += lbwHeight;
                    worldPos.xz += lbwDisp; // 0 when choppiness = 0
                }
                // Surf swash film: over the beach the surface HUGS THE SAND (a thin film a few
                // centimetres proud of it) wherever the swash has recently reached - a flat plane
                // below the terrain would lose the depth test and the breathing waterline + wet
                // glaze would never render. Fragments past the drying wet line stay under the sand
                // (depth-occluded) and are clipped in the fragment anyway; the still-water region
                // is untouched (the lift only ever RAISES onto dry ground).
                // Gates match the fragment's clip/glaze block exactly (_BedValid included): if the
                // pool-frame bed bake failed, the fragment never clips the beach, so lifting film
                // geometry here would print a floating water sheet on dry sand. The shore sample +
                // swash are evaluated at the SOURCE xz - the same point the fragment uses - so the
                // lifted film and the wet-sand glaze breathe on the same swash phase even under
                // horizontal chop displacement (they used to sample different points).
                if (_SurfActive > 0.5 && _ShoreDepthValid > 0.5 && _UseBedDepth > 0.5
                    && _BedValid > 0.5 && _LargeBody > 0.5)
                {
                    float beachRise = -shoreVert.depth; // metres the sand sits above the still level
                    if (shoreVert.influence > 0.0 && beachRise > 0.0)
                    {
                        float2 swashVert = EvaluateSurfSwash(o.largeWaveSourceXZ, shoreVert.toShore,
                                                             shoreVert.slopeTan,
                                                             shoreVert.influence, _SurfBeatTime);
                        if (swashVert.y > 1e-3)
                            worldPos.y = max(worldPos.y, _ShoreWaterLevel
                                             + min(beachRise, swashVert.y) + SURF_FILM_THICKNESS);
                    }
                }
                o.worldPos = worldPos;
                // Nudge the patch a fixed few centimetres toward the camera IN VIEW SPACE so it wins the
                // depth test against the coplanar far plane at EVERY distance. The old bias was a constant
                // NDC offset (bias * pos.w) which, under the non-linear reversed-Z buffer, grew into a huge
                // world-depth offset far from the camera and let the patch draw OVER opaque geometry. A
                // fixed view-space (world-metre) offset can never beat opaque more than _PatchDepthBias
                // metres behind the patch. Inert when bias = 0 (every non-patch surface).
                float4 viewPos = mul(UNITY_MATRIX_V, float4(worldPos, 1.0));
                viewPos.z += _PatchDepthBias; // view forward is -Z, so +Z moves toward the camera (nearer)
                o.pos = mul(UNITY_MATRIX_P, viewPos);
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // ================== frag stages (SHADER-SPLIT-3) ==================
            // frag() is decomposed into single-responsibility stages that read in render
            // order. Stage bodies are VERBATIM moves of the old frag blocks: each stage
            // re-binds the shared-geometry fields to the original local names, so the
            // moved code is unchanged - any behavior change here is a bug.

            // Per-fragment surface geometry, evaluated ONCE and shared by every stage.
            struct WaterGeomStage
            {
                float3 normal;       // world-space shading normal (detail folded in; NOT flipped for underwater)
                float2 nxz;          // pool-space ripple+wind slope (foam flow/relief input)
                float3 incomingRay;  // camera -> surface, normalized
                float viewDist;      // metres from the camera to the surface
                float roughness;     // shared specular roughness (EffectiveWaterRoughness at viewDist)
                ShoreData shore;     // hoisted shore-substrate sample (inert off surf bodies)
                SurfWaveSample surf; // hoisted surf-front sample (inert off surf bodies)
                float surfGeomFoam;  // geometry foam from the surface's own Jacobian/slope
            };

            WaterGeomStage EvaluateSurfaceGeometry(v2f i)
            {
                float fade;
                float4 info = SampleRipple(i.position, i.worldPos, fade);

                // make the water look more "peaked": walk a few steps along the ripple normal
                // in the active UV domain (pool for whole-body, sim window for windowed).
                float2 coord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                                    : (WorldToSim(i.worldPos).xz * 0.5 + 0.5);
                int refineSteps = clamp((int)_PeakedRefineSteps, 0, PEAKED_REFINE_MAX_STEPS);
                [loop] // uniform trip count (tier knob); explicit-LOD samples are loop-safe
                for (int k = 0; k < refineSteps; k++)
                {
                    coord += info.ba * PEAKED_REFINE_STEP;
                    info = SampleWaterBilinear(coord);
                }
                info.ba *= fade; // keep the windowed ripple faded to flat at the border (no-op when fade = 1)

                // Combine the ripple normal (info.ba = normal.xz) with the wind-wave
                // tilt. A height gradient g contributes normal.xz = -g, so the two
                // slopes simply add in the xz components before re-deriving y.
                float2 nxz = info.ba - WaveSlope(WindWaveSampleXZ(i.position.xz, i.largeWaveSourceXZ)) * _WaveNormalStrength;
                float3 normalPool = float3(nxz.x, sqrt(max(1e-4, 1.0 - dot(nxz, nxz))), nxz.y);
                // World-space surface normal + view ray, so reflection/refraction angles
                // are correct even when the volume is rotated or has a rectangular footprint.
                float3 normal = PoolNormalToWorld(normalPool);
                // ---- Coastline: ONE shore-substrate + surf-front sample at the SOURCE xz, hoisted
                // here and shared by the wave normal, the whitewash foam, the crest glow and the
                // swash below - both cheaper and far less inlining pressure on the shader compiler
                // than re-evaluating per consumer. Inert (zeros / deep water) unless this body runs
                // the surf layer over a baked Layer A field. ----
                ShoreData shoreFrag = ShoreDataInert();
                SurfWaveSample surfFrag = SurfWaveSampleInert();
                if (_SurfActive > 0.5 && _ShoreDepthValid > 0.5)
                {
                    shoreFrag = ShoreSample(i.largeWaveSourceXZ);
                    surfFrag = EvaluateSurfWaves(i.largeWaveSourceXZ, shoreFrag.depth,
                                                 shoreFrag.sdfDist, shoreFrag.toShore,
                                                 shoreFrag.slopeTan,
                                                 shoreFrag.influence, _SurfBeatTime);
                }
                // Open water: PoolNormalToWorld divides normal.xz by the (large) footprint extent,
                // flattening the surface so screen-space refraction collapses on big bodies. Add a
                // WORLD-space wave slope here (after that division) so open water keeps real normals
                // and refraction holds at any size. No-op for pool/small bodies (_LargeBody = 0).
                // .w = GEOMETRY foam: breaking whiteness derived from the composite surface's own
                // Jacobian pinch + slope (Crest/KWS style) - glued to the rendered waves by
                // construction, so foam can never detach from what the eye tracks.
                float surfGeomFoam = 0.0;
                if (_LargeBody > 0.5)
                {
                    float4 normalFoam = ApplyLargeBodyWaveNormalFoamShore(normal, i.largeWaveSourceXZ,
                                                                          _WaveNormalStrength,
                                                                          shoreFrag, surfFrag);
                    normal = normalFoam.xyz;
                    surfGeomFoam = normalFoam.w;
                }
                // View ray + distance from one subtraction (the distance also drives the detail
                // normal fade and the shared specular roughness below).
                float3 toSurface = i.worldPos - _WorldSpaceCameraPos;
                float viewDistWorld = length(toSurface);
                float3 incomingRay = toSurface / max(viewDistWorld, 1e-5);

                // ---- Crest-style crossing scrolling detail normals: micro-ripple detail finer
                // than the FFT cascades resolve, sampled in WORLD metres at the undisplaced source
                // xz (like the foam) so it rides the waves and is body-size independent. Added as
                // an xz tilt exactly like the FFT cascade tilt. Inert with the default "bump"
                // texture or strength 0; above-water only, so the underwater ceiling and every
                // legacy path keep their look. Both gates are uniforms (WGSL-safe branch). ----
                if (_DetailNormalStrength > 0.0 && _Underwater < 0.5)
                {
                    float2 detailTilt = DetailNormalTilt(i.largeWaveSourceXZ, viewDistWorld);
                    normal = normalize(normal + float3(detailTilt.x, 0.0, detailTilt.y)
                                                * _DetailNormalStrength);
                }
                WaterGeomStage g;
                g.normal = normal;
                g.nxz = nxz;
                g.incomingRay = incomingRay;
                g.viewDist = viewDistWorld;
                // Shared by the whole specular family. Pure ALU, so evaluating it for BOTH
                // sides costs nothing - the underwater path never reads it and the compiler
                // strips it there.
                g.roughness = EffectiveWaterRoughness(viewDistWorld);
                g.shore = shoreFrag;
                g.surf = surfFrag;
                g.surfGeomFoam = surfGeomFoam;
                return g;
            }

            float EvaluateWaterClarity(v2f i, ShoreData shoreFrag)
            {
                // Depth clarity (auto transparency): ONE curve from the baked bed depth drives the
                // turbidity + underwater-fog reach below (and the deep-water tint in the shoreline
                // block). Identity (1) when the feature is off or no bed is baked, so every existing
                // body is unchanged. Blended toward the surf field's depth where it is live, so the
                // clarity waterline agrees with the rendered shore.
                float waterClarity = 1.0;
                if (_UseBedDepth > 0.5 && _BedValid > 0.5)
                {
                    float bedPoolYClarity = tex2Dlod(_BedTex, float4(i.position.xz * 0.5 + 0.5, 0, 0)).r;
                    float colDepthClarity = BedColumnDepthWorld(bedPoolYClarity, i.position.y, VolumeExtentSafe().y);
                    if (_SurfActive > 0.5 && shoreFrag.influence > 0.0)
                        colDepthClarity = lerp(colDepthClarity, shoreFrag.depth, saturate(shoreFrag.influence));
                    waterClarity = WaterDepthClarity(colDepthClarity);
                }
                return waterClarity;
            }

            // The whole seen-from-below path; returns the final pixel colour.
            float4 UnderwaterStage(v2f i, WaterGeomStage g, float waterClarity)
            {
                // Original frag locals, re-bound: this side of the surface faces DOWN,
                // so the shading normal is the geometry normal flipped.
                float3 normal = -g.normal;
                float3 incomingRay = g.incomingRay;
                float2 nxz = g.nxz;
                float3 reflectedRay = reflect(incomingRay, normal);
                float3 refractedRay = refract(incomingRay, normal, IOR_WATER / IOR_AIR);
                // Total internal reflection (common at grazing angles from below, eta > 1)
                // returns a ZERO vector; tracing it divides by zero in IntersectCube and
                // poisons the pixel with NaN. Fall back to the reflected ray.
                if (dot(refractedRay, refractedRay) < 1e-6) refractedRay = reflectedRay;
                // saturate: float error can push the dot above 1, making the pow base
                // negative -> NaN sparkle.
                float fresnel = lerp(FRESNEL_MIN_BELOW, 1.0, pow(saturate(1.0 - dot(normal, -incomingRay)), FRESNEL_POWER));

                // TIR reflection reflects the ENVIRONMENT, tinted underwater - never the pool
                // tiles. The reflected ray points back DOWN into the pool, so routing it through
                // GetSurfaceRayColor used to sample the analytic wall (a stale baked-in tile
                // reflection on the underside of the surface).
                float3 reflectedColor = SampleEnvironment(reflectedRay) * UNDERWATER_COLOR;
                float3 refractedColor = GetSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0)) * UNDERWATER_REFRACT_TINT;

                // Real transparency from below: sample the live scene above the surface.
                if (_RealRefraction > 0.5)
                {
                    float2 ruvU = ScreenUV(i.screenPos) + normal.xz * _RefractionDistortion;
                    refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruvU)).rgb * UNDERWATER_REFRACT_TINT;
                }

                float3 bodyInscatterUnder = WaterInscatterColor(-incomingRay, _LightDir, _SunColor, 0.0);
                refractedColor = ApplyWaterOpacityTintedClarity(refractedColor, bodyInscatterUnder, waterClarity); // turbidity from below too

                float tUnder = (1.0 - fresnel) * length(refractedRay);
                tUnder = lerp(1.0, tUnder, _ReflectionStrength); // strength 0 = fully refracted
                float3 underColor = lerp(reflectedColor, refractedColor, tUnder);

                // ---- Foam seen from below: the same advected mask, but instead of lit
                // white it reads as a SILHOUETTE - dense foam blocks the sky coming
                // through the surface, thin lace scatters a faint sun glow through.
                // No contact foam here: the depth texture holds the scene ABOVE the
                // surface from this side, so the contact heuristic is meaningless. ----
                if (_FoamEnabled > 0.5)
                {
                    float2 fcoord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                                         : (WorldToSim(i.worldPos).xz * 0.5 + 0.5);
                    float advected = SampleFoamMaskBilinear(fcoord);
                    float edge = min(1.0 - abs(i.position.x), 1.0 - abs(i.position.z));
                    float border = (_SimWindowed < 0.5) ? (1.0 - smoothstep(0.0, _FoamBorderWidth, edge)) : 0.0;
                    float mask = saturate((advected + border) * _FoamStrength);

                    // Same world-space pattern UV as the above-water side. Computed (with its
                    // screen derivatives) BEFORE the mask branch: WGSL requires derivatives in
                    // uniform control flow, and the branch below is per-fragment.
                    float2 fuv = i.worldPos.xz / max(_FoamTileSize, 1e-3)
                               + normal.xz * FOAM_NORMAL_NUDGE;
                    float2 fuvDdx = ddx(fuv);
                    float2 fuvDdy = ddy(fuv);

                    if (mask > FOAM_MASK_EPSILON)
                    {
                        float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
                        float3 pattern; float core, lace, foamAlpha; float2 tilt;
                        EvaluateFoam(fuv, fuvDdx, fuvDdy, nxz, mask, foamDist, pattern, core, lace, foamAlpha, tilt);

                        // Applied BEFORE the downwelling dim below, so the silhouette
                        // and its glow fade with eye depth like the rest of the scene.
                        float sunThrough = saturate(_LightDir.y);
                        underColor *= 1.0 - FOAM_UNDERSIDE_DARKEN * foamAlpha;
                        underColor += _FoamColor.rgb * pattern * (FOAM_UNDERSIDE_GLOW * sunThrough * lace * mask);
                    }
                }

                // Dim the underwater view by the CAMERA's depth: the deeper the eye, the less
                // downwelling light reaches it, so the whole submerged scene reads darker.
                // Measured against the analytic surface (rest + waves) directly above the eye,
                // not the flat centre plane, so depth stays consistent with the rest of the
                // shading when the surface is wind-driven.
                float3 camPool = WorldToPool(_WorldSpaceCameraPos);
                float camSurfaceY = PoolToWorld(float3(camPool.x,
                    WaveHeight(WindWaveSampleXZ(camPool.xz, _WorldSpaceCameraPos.xz)), camPool.z)).y;
                underColor *= DownwellingAttenuation(_WorldSpaceCameraPos.y, camSurfaceY);
                return float4(underColor, 1.0);
            }

            fixed4 frag(v2f i) : SV_Target
            {
                WaterGeomStage geom = EvaluateSurfaceGeometry(i);
                float waterClarity = EvaluateWaterClarity(i, geom.shore);

                // Both paths gate on the SAME uniform, so control flow stays uniform
                // (the WGSL derivative contract) exactly like the old if/else did.
                if (_Underwater > 0.5)
                    return UnderwaterStage(i, geom, waterClarity);

                // Above-water path (extracted into stages in the next commit). Original
                // frag locals, re-bound to the shared geometry stage.
                float3 normal = geom.normal;
                float2 nxz = geom.nxz;
                float3 incomingRay = geom.incomingRay;
                float viewDistWorld = geom.viewDist;
                ShoreData shoreFrag = geom.shore;
                SurfWaveSample surfFrag = geom.surf;
                float surfGeomFoam = geom.surfGeomFoam;
                {
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_AIR / IOR_WATER);
                    // Schlick Fresnel from the air/water IOR: ~2% mirror straight down (deep
                    // clear water at your feet), full mirror at grazing (the horizon). The
                    // exponent is the OVERALL SHININESS dial (Crest exposes the same): 5 is
                    // physical; lower lifts reflectivity on tilted wave faces so the whole
                    // surface reads glossier while keeping the down/grazing contrast.
                    // saturate: float error can push the dot above 1 -> negative pow base -> NaN.
                    float fresnelGrazing = pow(saturate(1.0 - dot(normal, -incomingRay)), _FresnelPower);
                    float fresnel = max(FRESNEL_F0_WATER + (1.0 - FRESNEL_F0_WATER) * fresnelGrazing,
                                        _FresnelFloor);

                    // Reflection samples the environment (sky / URP probe) for ANY reflected direction.
                    // GetSurfaceRayColor would route a below-horizon ray - common at grazing angles and on
                    // wave slopes, exactly where Fresnel makes the reflection strongest - into the pool
                    // floor and return the TILES, which showed up as tile "highlights" and hid the probe.
                    // The underwater branch already samples the environment directly; match it here.
                    // SKY only: the sun is added as the GGX lobe after the composite, so the legacy
                    // glint must not ride along inside the mirror term (it would double the sun).
                    // Sampled at the SHARED roughness mip: the mirror blurs with the same roughness
                    // that widens the sun lobe - near-sharp at your feet, hazier toward the horizon.
                    // The horizon clamp applies to a COPY of the ray: SSR below must march the true
                    // reflection (below-horizon rays legitimately hit scene geometry there), only
                    // the sky lookup needs the lift.
                    float surfaceRoughness = EffectiveWaterRoughness(viewDistWorld);
                    float3 skyRay = reflectedRay;
                    skyRay.y = max(skyRay.y, REFLECTION_MIN_UP_Y);
                    skyRay = normalize(skyRay);
                    float3 reflectedColor = SampleSkyEnvironmentAniso(skyRay, surfaceRoughness);

                    // ---- Wave-crest subsurface glow: steep crests scatter sunlight toward the viewer,
                    // brightest looking INTO the sun. Crest steepness is the TRUE displacement-Jacobian fold
                    // exported by the FFT compute (saturate(1 - J), the same fold that seeds whitecaps), so
                    // the glow tracks the actual breaking crests. Remapped through [min,max] and raised to a
                    // power so it concentrates on the sharp folds. Added emissively after compositing (see
                    // below) so it reads regardless of what is behind the crest. Ocean-FFT only + gated. ----
                    float sssBoost = 0.0;
                    if (_SssEnabled > 0.5 && _OceanFftActive > 0.5)
                    {
                        // Shore-attenuated fold: no crest glow from waves the depth field has
                        // flattened (shoreFrag is inert off surf bodies - deep ocean unchanged).
                        float fold = OceanFftJacobianShore(i.largeWaveSourceXZ, shoreFrag);
                        float ramp = saturate((fold - _SssPinchMin)
                                              / max(_SssPinchMax - _SssPinchMin, SSS_AMPLITUDE_EPSILON));
                        float pinch = pow(ramp, _SssPinchFalloff);
                        float sunFacing = pow(saturate(dot(-incomingRay, _LightDir)), _SssSunFalloff);
                        sssBoost = pinch * sunFacing * _SssIntensity;
                    }

                    // ---- Surf breaker crest glow: cresting lips scatter sunlight exactly like
                    // FFT-pinched crests, so reuse the subsurface glow path (same gate/knobs). The
                    // shore/front sample itself is hoisted next to the normal above. ----
                    if (_SssEnabled > 0.5 && surfFrag.breaker > 0.0)
                    {
                        float surfSun = pow(saturate(dot(-incomingRay, _LightDir)), _SssSunFalloff);
                        sssBoost += surfFrag.breaker * surfSun * _SssIntensity;
                    }

                    // The water's lit body colour (picked scatter colour + sun/ambient), or the flat fog
                    // colour when scattering is off. Used as the in-scatter target for EVERY path below (deep
                    // water, scene refraction, pool, turbidity) so the scatter actually shows. The crest glow
                    // is NOT folded in here - as a volume target it only shows where the water behind the
                    // crest is deep (sky/far behind), so it is added emissively after compositing instead.
                    float3 bodyInscatter = WaterInscatterColor(-incomingRay, _LightDir, _SunColor, 0.0);

                    float3 refractedColor = GetSurfaceRayColor(i.worldPos, refractedRay, ABOVEWATER_COLOR);

                    // ---- Reflection: analytic -> planar -> SSR (SSR wins where it hits). The toggles
                    // are uniform-driven (published per body via the property block), so they are live. ----
                    if (_UsePlanar > 0.5)
                        reflectedColor = SamplePlanarReflection(i.screenPos, normal, surfaceRoughness);
                    if (_UseSSR > 0.5)
                    {
                        float ssrHit;
                        float3 ssr = MarchSSR(i.worldPos, reflectedRay, surfaceRoughness, ssrHit); // SSR marches in world space
                        reflectedColor = lerp(reflectedColor, ssr, ssrHit * _SSRStrength);
                    }

                    // ---- Real transparency: sample the actual scene behind the surface, instead of
                    // the analytic pool; else fog the ANALYTIC pool by the refracted chord. Only one
                    // path runs, so the real-refraction view is never double-fogged. ----
                    if (_RealRefraction > 0.5)
                    {
                        float2 ruv = ScreenUV(i.screenPos);
                        ruv += normal.xz * _RefractionDistortion;
                        refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruv)).rgb * ABOVEWATER_COLOR;

                        // Fog the transmitted view by the water thickness behind the surface
                        // (scene eye-depth - surface eye-depth), so heavy fog reads through too.
                        float sceneEyeR = LinearEyeDepth(RawSceneDepth(saturate(ruv)));
                        float surfEyeR  = EyeDepthOf(i.worldPos);
                        refractedColor = ApplyWaterVolumeClarity(refractedColor, max(0.0, sceneEyeR - surfEyeR), bodyInscatter, waterClarity);
                    }
                    else if (_LargeBody < 0.5)
                    {
                        // Analytic pool fog: WORLD length of the refracted segment through the pool,
                        // by intersecting the unit box in pool space then measuring the world chord
                        // (correct under non-uniform extent / rotation). Open water has no pool box
                        // and its refracted colour is already the deep-water colour, so it is skipped.
                        float3 pdFog = WorldDirToPool(refractedRay);
                        float2 tfog = IntersectCube(i.position, pdFog, POOL_BOX_MIN, POOL_BOX_MAX);
                        float3 exitWorld = PoolToWorld(i.position + pdFog * max(0.0, tfog.y));
                        refractedColor = ApplyWaterVolumeClarity(refractedColor, length(exitWorld - i.worldPos), bodyInscatter, waterClarity);
                    }

                    refractedColor = ApplyWaterOpacityTintedClarity(refractedColor, bodyInscatter, waterClarity); // turbidity toward the body colour

                    // ---- Ocean FFT whitecap foam: coverage sampled per pixel from the cascade (.w), on the
                    // same crests as the normal tilt, then broken into moving lace by the foam flipbook -
                    // the coverage is a black-point threshold that dissolves the pattern in (Crest's
                    // WhiteFoamTexture). Whitecaps are matte, so the resulting alpha knocks the specular
                    // reflection down before compositing (this surface expresses gloss as the reflection
                    // term). Ocean-only; the analytic/pool path leaves this at 0. ----
                    float oceanFoam = 0.0;                       // textured coverage: drives matte + blend
                    float3 oceanFoamPattern = float3(1.0, 1.0, 1.0);
                    float2 oceanFoamSampleXZ = i.largeWaveSourceXZ; // parallax-lifted pattern-sample point
                    // WGSL derivative uniformity: whitecap/whitewash pattern gradients, hoisted HERE
                    // (uniform control flow) for every non-uniform coverage branch below - the ocean
                    // whitecap, its tilt, and the surf whitewash all sample from this base world XZ
                    // (their parallax lift is additive, so these gradients stay exact).
                    float2 foamWorldDdx = ddx(i.largeWaveSourceXZ);
                    float2 foamWorldDdy = ddy(i.largeWaveSourceXZ);
                    if (_OceanFftActive > 0.5)
                    {
                        // The surf band is the surf system's territory: the FFT foam ACCUMULATOR
                        // is depth-blind (its small cascades still whitecap at 2 m of water), so
                        // accumulated ocean whitecaps fade out where the fronts/whitewash own the
                        // shallows. Inert off surf bodies (the gate is 0 there).
                        float coverage = OceanFftFoam(i.largeWaveSourceXZ)
                                       * (1.0 - LbwGeometryFoamGate(shoreFrag));
                        if (coverage > FOAM_MASK_EPSILON)
                        {
                            // Parallax: sample the PATTERN where a layer floating just above the surface
                            // meets the view ray (coverage stays at the true surface point - foam is still
                            // WHERE the sim says, it just reads as sitting on top of the water).
                            float3 viewToCam = -incomingRay;
                            oceanFoamSampleXZ = i.largeWaveSourceXZ + viewToCam.xz
                                * (OCEAN_FOAM_PARALLAX_HEIGHT / max(viewToCam.y, OCEAN_FOAM_PARALLAX_MIN_VIEW_Y));

                            // Stock white _FoamTex -> pattern ~= 1 -> solid coverage (no regression); a real
                            // foam texture dissolves in as lace. Distance anti-tiling (second rotated octave)
                            // hides the repeat toward the horizon; the contrast sharpen breaks round blobs.
                            float foamCamDist = distance(i.largeWaveSourceXZ, _WorldSpaceCameraPos.xz);
                            oceanFoamPattern = SampleOceanWhitecapPattern(oceanFoamSampleXZ, foamCamDist,
                                                                          foamWorldDdx, foamWorldDdy);
                            // Shared KWS contrast/dissolve law (FoamDissolve above); no erosion term.
                            oceanFoam = FoamDissolve(oceanFoamPattern.r, coverage, _OceanFoamFeather, 0.0);
                        }
                    }

                    // ---- Foam layers, evaluated BEFORE the reflection composite so the combined foam
                    // can matte the specular (foam breaks the mirror sheet - previously only the ocean
                    // layer did; pond/wake foam stayed glossy, which read as painted-on). Evaluated
                    // separately (different sources + art direction), composited exclusively after the
                    // shoreline gradient below. ----
                    float oceanFoamAlpha = 0.0;
                    float3 oceanFoamLook = float3(0.0, 0.0, 0.0);
                    float pondFoamAlpha = 0.0;
                    float3 pondFoamLook = float3(0.0, 0.0, 0.0);

                    // ---- Ocean whitecap look: lit with the same wrapped-sun + ambient model as the pond
                    // foam so crests shade with the waves instead of reading as flat paint. Gated on the
                    // FFT ocean, so pools stay unchanged. ----
                    if (oceanFoam > FOAM_MASK_EPSILON)
                    {
                        // ---- Foam relief: emboss the lighting normal by the foam normal map (same flipbook,
                        // frame-synced to the pattern) so the lace shades three-dimensionally and its specular
                        // breakup matches the texture. Built as a LOCAL normal - the base wave normal that the
                        // pond foam / haze below rely on is left untouched. Default "bump" map = zero tilt.
                        // Tilt is sampled at the SAME parallax-lifted point as the pattern so they stay glued. ----
                        float2 oceanFoamTilt = SampleOceanWhitecapTilt(oceanFoamSampleXZ,
                                                                       foamWorldDdx, foamWorldDdy)
                                             * (_FoamNormalStrength * oceanFoam);
                        float3 oceanFoamNormal = ApplyFoamTiltToNormal(normal, oceanFoamTilt);

                        // Modulate the tint by the pattern so the foam carries internal light/dark detail
                        // instead of reading as a flat wash; whiten toward the peaks so dense foam stays bright.
                        float oceanWrap = FoamWrappedDiffuse(oceanFoamNormal, _LightDir);
                        float3 oceanTint = _OceanFoamColor.rgb * lerp(oceanFoamPattern, float3(1.0, 1.0, 1.0), oceanFoam);
                        oceanFoamLook = FoamLitColor(oceanTint, _SunColor, oceanWrap);
                        oceanFoamAlpha = oceanFoam * _OceanFoamColor.a;
                    }

                    // ---- Interactive/pond foam look: advected buffer + shoreline border + contact ----
                    if (_FoamEnabled > 0.5)
                    {
                        // Windowed bodies read the foam buffer in the window frame too - at the
                        // SOURCE xz (undisplaced), like the whitecap path. Sampling at the displaced
                        // worldPos misses foam under horizontally-displaced geometry: the hero wave's
                        // crest is thrown metres forward by lean + curl, so its fragments were reading
                        // the buffer ahead of where the lip foam was injected (empty crest head). FFT
                        // chop caused the same error at a smaller, invisible scale.
                        float3 foamSourcePos = float3(i.largeWaveSourceXZ.x, i.worldPos.y, i.largeWaveSourceXZ.y);
                        float2 fcoord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                                             : (WorldToSim(foamSourcePos).xz * 0.5 + 0.5);
                        float advected = SampleFoamMaskBilinear(fcoord);

                        // shoreline foam against the pool walls (whole-body only; a window has no walls)
                        float edge = min(1.0 - abs(i.position.x), 1.0 - abs(i.position.z));
                        float border = (_SimWindowed < 0.5) ? (1.0 - smoothstep(0.0, _FoamBorderWidth, edge)) : 0.0;

                        // contact foam where geometry pierces the waterline. BOUNDED bodies only (same
                        // gate as the border above): on a windowed ocean/large body the screen-depth
                        // contact test is unreliable (it fought the shore/SWE work) and there are no walls,
                        // so it is skipped entirely. Needs the depth texture; the behind-guard only adds
                        // foam where the scene is genuinely just BEHIND the surface (fixes "all water
                        // foamed" builds).
                        float contact = 0.0;
                        if (_SimWindowed < 0.5)
                        {
                            float2 suv = ScreenUV(i.screenPos);
                            float sceneEye = LinearEyeDepth(RawSceneDepth(suv));
                            float surfEye  = EyeDepthOf(i.worldPos);
                            float behind   = sceneEye - surfEye; // > 0 when scene sits below the surface
                            contact = behind > 0.0 ? (1.0 - saturate(behind / max(_FoamContactDepth, 1e-4))) : 0.0;
                        }

                        float mask = saturate((advected + border + contact) * _FoamStrength);

                        // WORLD-space pattern UV (like the ocean whitecap): scale set by the
                        // body's Foam Pattern Size, independent of extent, anchored under a
                        // scrolling window; nudged by the surface tilt so foam rides ripples.
                        // Computed (with its screen derivatives) BEFORE the mask branch: WGSL
                        // requires derivatives in uniform control flow, and the branch below
                        // is per-fragment.
                        float2 fuv = i.worldPos.xz / max(_FoamTileSize, 1e-3)
                                   + normal.xz * FOAM_NORMAL_NUDGE;
                        float2 fuvDdx = ddx(fuv);
                        float2 fuvDdy = ddy(fuv);

                        if (mask > FOAM_MASK_EPSILON)
                        {
                            float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
                            float3 pattern; float core, lace, foamAlpha; float2 tilt;
                            EvaluateFoam(fuv, fuvDdx, fuvDdy, nxz, mask, foamDist, pattern, core, lace, foamAlpha, tilt);

                            // ---- Foam relief: tilt the lighting normal by the foam's own
                            // normal map so the lace shades three-dimensionally. ----
                            float3 foamNormal = ApplyFoamTiltToNormal(normal, tilt);

                            // ---- Lit foam: wrapped diffuse from the sun over an ambient
                            // floor, so foam shades with the waves instead of flat white. ----
                            float wrapped = FoamWrappedDiffuse(foamNormal, _LightDir);
                            float3 albedo = _FoamColor.rgb * lerp(pattern, float3(1.0, 1.0, 1.0), core * FOAM_CORE_WHITEN);
                            pondFoamLook = FoamLitColor(albedo, _SunColor, wrapped);
                            pondFoamAlpha = foamAlpha;
                        }
                    }

                    // ---- Surf whitewash look: ANALYTIC coverage from the breaker-front layer (broken
                    // bores + trailing churn) + GEOMETRY foam (the surface's own Jacobian/slope,
                    // computed beside the normal above - white glued to whatever the rendered waves
                    // actually do). Rendered through the OCEAN WHITECAP pipeline, not the pond
                    // flipbook: whitewash IS seawater whitecap foam, so the surf shares the deep
                    // caps' texture + KWS contrast law (one material language from open ocean to
                    // the beach) - but through its own DEDICATED _SurfFoam* knobs, fully decoupled
                    // from both the ripple-foam and the ocean-whitecap sliders. ----
                    float surfFoamAlpha = 0.0;
                    float3 surfFoamLook = float3(0.0, 0.0, 0.0);
                    // FOAM-1: artist pop curve. The LUT maps the front's lifecycle clock (overCap,
                    // 0..SURF_CREST_LUT_OVERCAP_MAX) to crest-foam intensity, times the timing-free
                    // lip footprint - the curve alone decides WHEN crest foam pops and how it holds/
                    // releases. Inactive = 0 added; the legacy breaker window still feeds the sim
                    // injection + SSS, so nothing is lost. tex2Dlod: no derivatives, WGSL-uniform.
                    float surfCrestFoam = 0.0;
                    if (_SurfCrestFoamLutActive > 0.5 && surfFrag.lipShape > 0.0)
                    {
                        float crestLutU = saturate(surfFrag.overCap / SURF_CREST_LUT_OVERCAP_MAX);
                        float crestCurve = tex2Dlod(_SurfCrestFoamLut,
                                                    float4(crestLutU, 0.5, 0.0, 0.0)).r;
                        surfCrestFoam = crestCurve * surfFrag.lipShape * _SurfCrestFoamGain;
                    }
                    float surfCoverage = saturate((surfFrag.whitewash + surfCrestFoam + surfGeomFoam)
                                                  * _SurfFoamStrength);
                    if (surfCoverage > FOAM_MASK_EPSILON)
                    {
                        // Same parallax lift as the ocean caps: foam reads as sitting ON the water.
                        float3 surfViewToCam = -incomingRay;
                        float2 surfSampleXZ = i.largeWaveSourceXZ + surfViewToCam.xz
                            * (OCEAN_FOAM_PARALLAX_HEIGHT / max(surfViewToCam.y, OCEAN_FOAM_PARALLAX_MIN_VIEW_Y));
                        float surfDist = distance(i.largeWaveSourceXZ, _WorldSpaceCameraPos.xz);
                        float surfTile = max(_SurfFoamTileSize, 1e-3);
                        // Gradients hoisted with the whitecap's (foamWorldDdx/Ddy above): same base
                        // world XZ, additive parallax - exact for this tap too (WGSL uniformity).
                        float3 surfPattern = SampleOceanWhitecapPatternTiled(surfSampleXZ, surfDist, surfTile,
                                                                             foamWorldDdx, foamWorldDdy);
                        // FOAM-2: aged deposit rots into HOLES, not a uniform fade - age raises the
                        // pattern-dissolve threshold, so old foam breaks into lace patches, then
                        // filaments, then nothing (real sea foam dies by holes opening). trailAge
                        // is bore-gated, so the bore head (age ~0) stays solid. 0 seconds = off.
                        float surfTrailErode = 0.0;
                        if (_SurfFoamTrailDissolve > 0.0)
                            surfTrailErode = saturate(surfFrag.trailAge / _SurfFoamTrailDissolve)
                                           * SURF_TRAIL_ERODE_MAX;
                        // Shared KWS contrast/dissolve law (FoamDissolve above) + the trail erosion.
                        float surfFoam = FoamDissolve(surfPattern.r, surfCoverage, _SurfFoamFeather,
                                                      surfTrailErode);
                        if (surfFoam > FOAM_MASK_EPSILON)
                        {
                            float2 surfTiltXY = SampleOceanWhitecapTiltTiled(surfSampleXZ, surfTile,
                                                                             foamWorldDdx, foamWorldDdy)
                                              * (_FoamNormalStrength * surfFoam);
                            float3 surfFoamNormal = ApplyFoamTiltToNormal(normal, surfTiltXY);
                            float surfWrapped = FoamWrappedDiffuse(surfFoamNormal, _LightDir);
                            float3 surfTint = _SurfFoamColor.rgb
                                * lerp(surfPattern, float3(1.0, 1.0, 1.0), surfFoam);
                            surfFoamLook = FoamLitColor(surfTint, _SunColor, surfWrapped);
                            surfFoamAlpha = surfFoam * _SurfFoamColor.a;
                        }
                    }

                    // Foam is matte: the combined coverage knocks the specular reflection down before
                    // compositing (this surface expresses gloss as the reflection term, so this IS the
                    // "foam roughens the surface" cue - Crest lerps smoothness down the same way).
                    float foamMatte = max(max(oceanFoam, pondFoamAlpha), surfFoamAlpha);

                    float3 outColor = lerp(refractedColor, reflectedColor,
                                           fresnel * _ReflectionStrength * (1.0 - foamMatte));

                    // ---- GGX sun specular, added AFTER the fresnel composite: the lobe carries its
                    // own Schlick term at the half-vector, so folding it into the reflection lerp
                    // (which is weighted by the surface fresnel) would double-count Fresnel. Scaled
                    // by the reflection dial and matted by foam exactly like the mirror term, which
                    // also keeps reflection-off bodies sun-free like the legacy glint did. Shares
                    // surfaceRoughness with the sky mip above (computed with reflectedColor). ----
                    outColor += SunSpecular(normal, -incomingRay, surfaceRoughness)
                              * (_ReflectionStrength * (1.0 - foamMatte));

                    // ---- Wave-crest subsurface glow, added emissively so it reads on EVERY sun-facing
                    // crest regardless of what is behind it (the earlier in-scatter form only showed where
                    // the volume behind the crest was deep, i.e. sky/far behind). Tinted by the scatter
                    // body colour and lit by the sun; sssBoost already carries the crest pinch, sun-facing
                    // and intensity. Knocked down by foam so whitecaps stay matte over the glow. ----
                    if (sssBoost > 0.0)
                        outColor += _ScatterColor.rgb * _SunColor * (sssBoost * (1.0 - foamMatte));

                    // ---- Shallow-water clarity (surf bodies): centimetres-deep run-out shows the
                    // ground through it instead of reading as flat opaque blue between the last
                    // bore and the beach. Keyed off the WORLD-FRAME shore field so it works on the
                    // windowed ocean too (the pool-bed block below is bounded-only). ----
                    if (_SurfActive > 0.5 && shoreFrag.influence > 0.0
                        && shoreFrag.depth > 0.0 && shoreFrag.depth < SHALLOW_CLARITY_DEPTH)
                    {
                        float shallowClarity = 1.0 - saturate(shoreFrag.depth / SHALLOW_CLARITY_DEPTH);
                        outColor = lerp(outColor, refractedColor,
                                        shallowClarity * SHALLOW_CLARITY_BLEND * shoreFrag.influence);
                    }

                    // ---- Shoreline gradient from the real terrain depth (baked bed map).
                    // Tint toward the deep-water colour by the water-column depth, so the surface
                    // reads clear over shallows and dark over the drop-off. No-op until a bed is
                    // baked and the toggle is on.
                    // Surf swash (P4): the clip line breathes with the arriving fronts - the film runs
                    // up the beach and drains back - and the zone the film has recently covered renders
                    // as a dark wet-sand glaze instead of clipping away. Fully analytic (the swash and
                    // the drying wet line are closed-form functions of the wave clock); zero when the
                    // surf layer is off, so the classic hard waterline is byte-identical. ----
                    // FOAM-3 swash foam accumulators - filled inside the bed-depth block below,
                    // composited with the other foam layers after it (declared here for scope).
                    float swashFoamAlpha = 0.0;
                    float3 swashFoamLook = float3(0.0, 0.0, 0.0);
                    if (_UseBedDepth > 0.5 && _BedValid > 0.5)
                    {
                        float2 bedUV = i.position.xz * 0.5 + 0.5;
                        float bedPoolY = tex2Dlod(_BedTex, float4(bedUV, 0, 0)).r;
                        float colDepth = BedColumnDepthWorld(bedPoolY, i.position.y, VolumeExtentSafe().y);
                        // ONE WATERLINE: on surf bodies the fronts/lace/swash/debug all read the
                        // world-frame shore field, but the clip/tint here read the pool-frame
                        // _BedTex - two bakes on different texel grids whose zero crossings
                        // disagree by up to a texel. That strip is the "continuous dry line" the
                        // SDF debug shows at the shore: water still renders there while the shore
                        // field already says land, so it gets no waves, no lace and a confused
                        // swash. Use the SAME depth for the clip/tint/swash so every waterline
                        // consumer agrees (feather-blended so leaving the field stays seamless).
                        if (_SurfActive > 0.5 && shoreFrag.influence > 0.0)
                            colDepth = lerp(colDepth, shoreFrag.depth, saturate(shoreFrag.influence));
                        float2 swash = (_SurfActive > 0.5)
                            ? EvaluateSurfSwash(i.largeWaveSourceXZ, shoreFrag.toShore,
                                                shoreFrag.slopeTan,
                                                shoreFrag.influence, _SurfBeatTime)
                            : float2(0.0, 0.0);
                        float swashLevel = swash.x;
                        float wetLevel = swash.y;
                        // Terrain mask: cut the water where the bed rises above the surface (dry beach)
                        // so the plane doesn't draw over the sand. clip() discards the fragment; the small
                        // positive bias keeps a hair of water right at the waterline (no shimmer gap).
                        // The swash keeps fragments alive up to the wet line (current film OR still-drying
                        // sand), so the film and the glaze have geometry to render on.
                        const float SHORE_CLIP_BIAS = 0.02; // metres of water kept past the waterline
                        clip(colDepth + SHORE_CLIP_BIAS + max(swashLevel, wetLevel));
                        // Depth clarity ties the deep tint to the SAME curve as turbidity/fog: murkier
                        // (lower clarity) = more deep tint. Falls back to the plain depth gradient when
                        // clarity is off (WaterDepthClarity = 1 -> tint = shore), so bodies not using it
                        // are byte-identical.
                        float shore = 1.0 - exp(-_ShorelineDepthScale * colDepth);
                        float tint = (_DepthClarityStrength > 0.0) ? (1.0 - WaterDepthClarity(colDepth)) : shore;
                        outColor = lerp(outColor, _DeepWaterColor.rgb, saturate(tint * _ShorelineStrength));
                        // Wet-sand glaze: fragments above the CURRENT film but under the drying wet line
                        // show the darkened scene through a thin glossy sheet - wet sand with zero state.
                        float beachRise = -colDepth;                    // metres above the still level
                        if (beachRise > 0.0 && wetLevel > 0.0)
                        {
                            // Thin-film transparency: the swash sheet is centimetres of water ON the
                            // sand, not ocean - pull HARD toward the refracted ground so the film
                            // reads wet-and-clear ("swash amplitude causes the blue water line" -
                            // the band must never look like blue ocean sitting on the beach).
                            float filmT = saturate(beachRise / max(wetLevel, 1e-3));
                            outColor = lerp(outColor, refractedColor,
                                            WET_FILM_MIN_TRANSPARENCY + WET_FILM_DEPTH_GAIN * filmT);
                            float aboveFilm = saturate((beachRise - swashLevel)
                                                       / max(wetLevel - swashLevel, 1e-3));
                            float glaze = aboveFilm * smoothstep(0.0, WET_GLAZE_EDGE,
                                                                 (wetLevel - beachRise)
                                                                 / max(wetLevel, 1e-3));
                            float3 wetLook = refractedColor * WET_GLAZE_REFRACT
                                           + reflectedColor * WET_GLAZE_REFLECT;
                            outColor = lerp(outColor, wetLook, glaze * WET_GLAZE_STRENGTH);

                            // ---- FOAM-3: swash foam. A foamy line rides the film's leading edge
                            // up the beach, is STRANDED at the wash border (the wet line) at the
                            // apex, then dissolves into holes and stretches into downslope drain
                            // streaks through the reflux. Fully analytic: phase + levels are the
                            // same closed forms as the film itself, so the foam can never desync
                            // from the water it rides. Strength 0 = the block is skipped and the
                            // beach is byte-identical. ----
                            if (_SurfSwashFoam > 0.0 && _SurfActive > 0.5)
                            {
                                float swashT = max(_SurfPeriod, 0.5);
                                // Same phase convention as EvaluateSurfSwash: 0 = crest arrival.
                                float swashPhase = frac(_SurfBeatTime / swashT - 0.5);
                                // Backwash progress: 0 through the uprush, 1 at full reflux.
                                float refluxAge = smoothstep(SURF_SWASH_UPRUSH, 1.0, swashPhase);
                                float swashBand = max(_SurfSwashFoamWidth, 0.01);
                                // Bore edge: foam hugging the film's leading edge (rides up with
                                // the uprush, retreats with the film - a thin working line).
                                float edgeFoamW = saturate(1.0 - abs(beachRise - swashLevel) / swashBand);
                                // Deposit: the line stranded at the wash border once the film has
                                // turned - it appears AT the apex and ages through the backwash.
                                float depositW = saturate(1.0 - abs(beachRise - wetLevel) / swashBand)
                                               * refluxAge;
                                float swashCoverage = saturate(max(edgeFoamW, depositW) * _SurfSwashFoam);
                                if (swashCoverage > FOAM_MASK_EPSILON)
                                {
                                    // Downslope drain streaks: a LINEAR xz warp stretching the
                                    // pattern along the local downslope axis (toward the water-
                                    // line), growing with reflux age. Linear, so the hoisted
                                    // gradients transform exactly (WGSL uniformity intact).
                                    float2 streakAxis = shoreFrag.toShore;
                                    float streakAlong = 1.0 / (1.0 + _SurfSwashStreak * refluxAge
                                                                     * SURF_SWASH_STREAK_GAIN);
                                    float2 swashXZ = i.largeWaveSourceXZ + streakAxis
                                        * (dot(i.largeWaveSourceXZ, streakAxis) * (streakAlong - 1.0));
                                    float2 swashDdx = foamWorldDdx + streakAxis
                                        * (dot(foamWorldDdx, streakAxis) * (streakAlong - 1.0));
                                    float2 swashDdy = foamWorldDdy + streakAxis
                                        * (dot(foamWorldDdy, streakAxis) * (streakAlong - 1.0));
                                    float swashDist = distance(i.largeWaveSourceXZ,
                                                               _WorldSpaceCameraPos.xz);
                                    float3 swashPattern = SampleOceanWhitecapPatternTiled(
                                        swashXZ, swashDist, max(_SurfFoamTileSize, 1e-3),
                                        swashDdx, swashDdy);
                                    // Same shared law as the whitewash (FoamDissolve), plus the
                                    // reflux hole-erosion: age raises the dissolve threshold, so
                                    // the stranded line rots into lace patches, then filaments.
                                    float swashFoam = FoamDissolve(swashPattern.r, swashCoverage,
                                                                   _SurfFoamFeather,
                                                                   refluxAge * _SurfSwashFoamDissolve
                                                                   * SURF_SWASH_ERODE_MAX);
                                    if (swashFoam > FOAM_MASK_EPSILON)
                                    {
                                        // Lit like the whitewash (wrapped sun over the surface
                                        // normal); tinted by the shared surf foam colour so the
                                        // line matches the bores that fed it. NOTE: the specular
                                        // matte skips this layer (the beach zone is already pulled
                                        // hard toward the refracted ground above).
                                        float swashWrapped = FoamWrappedDiffuse(normal, _LightDir);
                                        float3 swashTint = _SurfFoamColor.rgb
                                            * lerp(swashPattern, float3(1.0, 1.0, 1.0), swashFoam);
                                        swashFoamLook = FoamLitColor(swashTint, _SunColor, swashWrapped);
                                        swashFoamAlpha = swashFoam * _SurfFoamColor.a;
                                    }
                                }
                            }
                        }
                    }
                    // ---- Exclusive foam composite (looks evaluated above, before the reflection
                    // composite, so the combined coverage could matte the specular): ONE write into
                    // outColor, after the shoreline gradient so foam sits over it. Coverage is the max of
                    // the layers (never their stack) and the colour is their alpha-weighted blend, so a
                    // lone layer is bit-identical to the old per-layer lerp while overlap can no longer
                    // double-lay foam. ----
                    float foamCombinedAlpha = max(max(max(oceanFoamAlpha, pondFoamAlpha),
                                                      surfFoamAlpha), swashFoamAlpha);
                    if (foamCombinedAlpha > 0.0)
                    {
                        float3 foamCombinedLook = (oceanFoamLook * oceanFoamAlpha
                                                   + pondFoamLook * pondFoamAlpha
                                                   + surfFoamLook * surfFoamAlpha
                                                   + swashFoamLook * swashFoamAlpha)
                                                / max(oceanFoamAlpha + pondFoamAlpha
                                                      + surfFoamAlpha + swashFoamAlpha, 1e-5);
                        outColor = lerp(outColor, foamCombinedLook, foamCombinedAlpha);
                    }

                    // ---- Horizon haze: dissolve the far ocean surface into the sky so the outer mesh
                    // edge / water-sky boundary has no hard line. The sky along the near-horizontal view
                    // ray IS the horizon, so the surface fades toward SampleEnvironment(incomingRay),
                    // optionally tinted toward a fixed atmosphere colour by _HorizonHazeColor.a. The
                    // exponential 1 - exp(-density * dist) falloff reads like real distance haze instead
                    // of a hard band. Off when density is 0 (bounded bodies, unchanged). ----
                    if (_HorizonHazeDensity > 0.0)
                    {
                        float horizD = distance(i.worldPos, _WorldSpaceCameraPos);
                        float haze = 1.0 - exp(-_HorizonHazeDensity * horizD);
                        float3 hazeTarget = lerp(SampleEnvironment(incomingRay), _HorizonHazeColor.rgb, _HorizonHazeColor.a);
                        outColor = lerp(outColor, hazeTarget, haze);
                    }
                    // Legacy smoothstep stopgap (retired in a later increment): only when the new haze is
                    // off, so a scene still tuned with Horizon Fade Distance keeps its look meanwhile.
                    else if (_HorizonFadeDistance > 0.0)
                    {
                        float horizD = distance(i.worldPos, _WorldSpaceCameraPos);
                        float horizonFade = smoothstep(_HorizonFadeDistance * HORIZON_FADE_START, _HorizonFadeDistance, horizD);
                        outColor = lerp(outColor, SampleEnvironment(incomingRay), horizonFade);
                    }

                    // ---- Layer A debug: visualize the world-frame seabed-depth field on the surface
                    // (red = dry / seabed above surface, green shallow -> blue deep). Debug only;
                    // _ShoreDepthDebug is off unless toggled from the WaterVolume context menu. ----
                    if (_ShoreDepthDebug > 0.5 && _ShoreDepthValid > 0.5)
                    {
                        float2 shoreUV = (i.worldPos.xz - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
                        // P0: the field stores the still-water column depth directly (see WaterShore.hlsl).
                        float shoreColDepth = tex2Dlod(_ShoreDepthTex, float4(shoreUV, 0, 0)).r;
                        const float SHORE_DEBUG_RANGE = 10.0;           // depth (m) mapped shallow -> deep
                        float3 shoreDbg = (shoreColDepth < 0.0)
                            ? float3(1.0, 0.0, 0.0)
                            : lerp(float3(0.1, 0.9, 0.4), float3(0.0, 0.2, 0.9), saturate(shoreColDepth / SHORE_DEBUG_RANGE));
                        float shoreInField = all(shoreUV == saturate(shoreUV)) ? 1.0 : 0.0;
                        outColor = lerp(outColor, shoreDbg, shoreInField);
                    }

                    // ---- Layer A debug: visualize the shoreline SDF (signed distance to shore). Water
                    // side cyan, land side orange, banded every few metres so distance reads as contours.
                    // Debug only; _ShoreSDFDebug is off unless toggled from the context menu. ----
                    if (_ShoreSDFDebug > 0.5 && _ShoreSDFValid > 0.5)
                    {
                        float2 sdfUV = (i.worldPos.xz - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
                        float4 sdfSample = tex2Dlod(_ShoreSDFTex, float4(sdfUV, 0, 0));
                        float signedDist = sdfSample.b;
                        const float SHORE_SDF_DEBUG_BAND = 5.0; // metres between distance contours
                        float band = frac(abs(signedDist) / SHORE_SDF_DEBUG_BAND);
                        float3 sdfDbg = (signedDist >= 0.0) ? float3(0.1, 0.7, 1.0) : float3(1.0, 0.5, 0.1);
                        sdfDbg *= 0.55 + 0.45 * band;
                        // A now stores the beach slope (SURF-PHYS), not a mask - in-field validity
                        // comes from the UV test + _ShoreSDFValid gate above.
                        float sdfInField = all(sdfUV == saturate(sdfUV)) ? 1.0 : 0.0;
                        outColor = lerp(outColor, sdfDbg, sdfInField);
                    }
                    return float4(outColor, 1.0);
                }
            }
            ENDCG
        }
    }
}
