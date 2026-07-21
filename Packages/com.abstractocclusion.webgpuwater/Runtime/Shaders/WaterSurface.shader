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

        // Surface texture inputs - detail normals, the foam pattern + its flipbook controls, and the
        // ocean whitecap - are authored on the WaterVolume "Textures" section (the single place) and
        // published to these slots per body. Kept as [HideInInspector] (same convention as the
        // reflection/refraction block above) so the shader keeps their defaults + variants while the
        // material inspector stays clean. A body that leaves a slot empty keeps whatever the material
        // already had, so existing scenes are unchanged. Foam pattern world size comes from the volume's
        // Foam Pattern Size (published as _FoamTileSize), not the texture's ST. Relief for the foam
        // pattern and the ocean whitecap is derived procedurally (Crest-style finite differences).
        [HideInInspector] _DetailNormalTex ("Detail Normal (tiling water normals)", 2D) = "bump" {}
        [HideInInspector] _DetailNormalStrength ("Detail Normal Strength", Range(0, 2)) = 0.6
        [HideInInspector] _DetailNormalScale ("Detail Normal Tile (world metres)", Range(1, 100)) = 18
        [HideInInspector] _DetailNormalSpeed ("Detail Normal Scroll (metres per second)", Range(0, 2)) = 0.25
        [HideInInspector] _FoamTex ("Foam Pattern (single tile or flipbook)", 2D) = "white" {}
        [HideInInspector] _FoamTexFrames ("Foam Flipbook Grid (cols, rows)", Vector) = (1, 1, 0, 0)
        [HideInInspector] _FoamTexFPS ("Foam Flipbook Frame Rate", Range(0, 30)) = 10
        [HideInInspector] _FoamNormalStrength ("Foam Relief Strength (procedural)", Range(0, 3)) = 1
        [HideInInspector] _OceanWhitecapTex ("Ocean Whitecap (single tiling texture)", 2D) = "white" {}
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
            // stays lit, i.e. the old behaviour). _MAIN_LIGHT_SHADOWS_SCREEN is deliberately absent:
            // WaterSurfaceShadow.hlsl only handles the shadow-map keywords, so the SCREEN variant would
            // compile byte-identical to the no-keyword one (unknown keywords are ignored at set time).
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            // Reflection mode (planar / SSR / URP-probe base / real refraction) is UNIFORM-driven,
            // published per body every frame via the MaterialPropertyBlock (WaterUniformPublisher),
            // so it updates live in the editor and needs no shader variants.
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl" // brings WaterShared (via WaterCommon): POOL_RIM_HEIGHT etc.
            #include "WaterExclusion.hlsl" // dry-interior exclusion volumes (global OBBs)
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
            // Chunk fill level as the surface plane's POOL-Y (published per body by WaterVolume.Chunk.cs;
            // 0 = the rest plane, the default for every non-chunk body). Lowers / raises the disc so a
            // chunk can be partly full; the sphere clip below reads the fragment's DISPLACED pool
            // position, so the disc circle tracks the shape's cross-section at the chosen level for free.
            float  _ChunkSurfacePoolY;
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
            float _RippleChoppiness;   // per-body; horizontal Gerstner pinch on the interactive ripple/wake (0 = off)
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
                    poolFlat = float3(gridPoolXZ.x, _ChunkSurfacePoolY, gridPoolXZ.y); // grid -> pool (x, level, z); level 0 for non-chunks
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
                // Interactive-ripple horizontal choppiness (Crest-style _HorizontalDisplace, aimed at the
                // WAKE): the ripple sim only lifts HEIGHT, so the wake V and interactive ripples read soft
                // and round. Add a Gerstner pinch along the ripple slope so they sharpen. info.ba is the sim
                // normal.xz (= -grad h, already faded at the window edge), so displacing AGAINST it pulls
                // the surface toward crests. 0 = off (byte-identical). SIGN NOTE: if the wake BULGES instead
                // of sharpening, flip the '-' to '+' (cf. the sim-window Scroll sign). The fragment
                // re-samples the ripple at the displaced xz (minor, as the large-wave path already does);
                // add a source-xz carry later if a strong pinch shows a sampling seam.
                if (_RippleChoppiness > 0.0)
                    worldPos.xz -= _RippleChoppiness * info.ba;
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

            // frag() stages (SHADER-SPLIT-3): a splinter of THIS pass, not a library -
            // it reads the uniforms/v2f/SampleRipple above, so it must stay HERE, the
            // last include directly above frag().
            #include "WaterSurfaceFragStages.hlsl"

            // Chunk sphere footprint clip (published per body by WaterVolume.Chunk.cs); 0 = ordinary body.
            float _ChunkSphereClip;
            // Slight overdraw past the unit sphere (squared-radius units, ~1% radius) so the disc rim
            // and the shell wall share a COVERED seam: an exact clip left 1-px holes where the
            // rasterized rim undershot the analytic sphere the shell resolves. The shell renders
            // after the disc and its wall pixels replace the overhang, so the overlap never shows.
            #define CHUNK_SPHERE_CLIP_MARGIN 0.02

            // Chunk MESH footprint: clip the disc to the mesh's cross-section at the water line using the
            // depth prepass (WaterChunkDepthFeature). Read by texel .Load - no sampler. This is a UnityCG
            // shader, so plain Texture2D + single-arg LinearEyeDepth (not the URP-core macros the wall uses).
            float _ChunkUseMesh;
            Texture2D _ChunkFogFrontDepth;
            Texture2D _ChunkFogBackDepth;
            // Span-relative overdraw past the mesh's [front,back] so the disc rim meets the wall with a
            // covered seam (the shell renders after and hides the overhang), like the sphere-clip margin.
            #define CHUNK_MESH_CLIP_MARGIN 0.05

            fixed4 frag(v2f i) : SV_Target
            {
                // Dry-interior exclusion (boat hull, sub room): kill the surface fragment
                // BEFORE any shading work. Runs on both sides (_Underwater 0 and 1), so a
                // dry room seen from below loses its ceiling sheet too. WGSL-safe: discard
                // demotes the invocation (helpers keep feeding neighbour derivatives, the
                // same contract ShorelineStage's clip() already relies on), and with zero
                // volumes the uniform count skips the loop entirely.
                if (InsideExclusion(i.worldPos)) discard;

                // Chunk sphere footprint: clip the flat surface disc to the body's SPHERE so the circle
                // tracks the sphere's cross-section as waves move the water level. A fixed-radius disc is
                // exact only at the rest level; a raised/lowered level meets the sphere at a SMALLER
                // circle, so an unclipped disc over/under-shoots the shell's edge. i.worldPos is fully
                // displaced (ripple + wind + swell), so the pool point is exact at any level.
                if (_ChunkSphereClip > 0.5)
                {
                    float3 chunkPool = WorldToPool(i.worldPos);
                    // Keep fragments up to the margin PAST the unit sphere (covered-seam overdraw).
                    clip(1.0 + CHUNK_SPHERE_CLIP_MARGIN - dot(chunkPool, chunkPool));
                }

                // Chunk MESH footprint: carve the flat disc down to the mesh's cross-section at the water
                // line. Keep the fragment only where its OWN depth lies inside the mesh's [front, back]
                // span at this pixel (the Crest volume test) - the same two depth RTs the wall reads.
                if (_ChunkUseMesh > 0.5)
                {
                    int2 chunkPixel = int2(i.pos.xy);
                    // Linear eye depths of the mesh's front/back faces and of this disc fragment. A face
                    // at the FAR plane means "not rasterised here" - a cleared texel (no mesh at this
                    // pixel), or, for the front face only, the camera being INSIDE the mesh. Detected via
                    // the far plane (_ProjectionParams.z) so no reversed-Z / SRP far-value macro is needed.
                    float farPlane = _ProjectionParams.z;
                    float linFrontRaw = LinearEyeDepth(_ChunkFogFrontDepth.Load(int3(chunkPixel, 0)).r);
                    float linBackRaw  = LinearEyeDepth(_ChunkFogBackDepth.Load(int3(chunkPixel, 0)).r);
                    bool frontEmpty = linFrontRaw >= farPlane * 0.99;
                    bool backEmpty  = linBackRaw  >= farPlane * 0.99;
                    clip((frontEmpty && backEmpty) ? -1.0 : 1.0); // no mesh at this pixel

                    float linDisc  = LinearEyeDepth(i.pos.z);
                    float linFront = frontEmpty ? 0.0 : linFrontRaw;      // camera inside: drop the near bound
                    float linBack  = backEmpty  ? farPlane : linBackRaw;
                    float margin   = max(linBack - linFront, 1e-4) * CHUNK_MESH_CLIP_MARGIN;
                    clip((linDisc >= linFront - margin && linDisc <= linBack + margin) ? 1.0 : -1.0);
                }

                WaterGeomStage geom = EvaluateSurfaceGeometry(i);
                float waterClarity = EvaluateWaterClarity(i, geom.shore);

                // Both paths gate on the SAME uniform, so control flow stays uniform
                // (the WGSL derivative contract) exactly like the old if/else did.
                if (_Underwater > 0.5)
                    return UnderwaterStage(i, geom, waterClarity);

                float fresnel;
                float3 reflectedColor = ReflectionStage(i, geom, fresnel);
                float3 refractedColor = RefractionStage(i, geom, waterClarity);
                float sssBoost = EvaluateCrestGlow(i, geom);

                // WGSL derivative uniformity: whitecap/whitewash/swash pattern gradients,
                // hoisted HERE (uniform control flow) for every non-uniform coverage branch
                // inside the foam and shoreline stages - they all sample from this base
                // world XZ (their parallax lift is additive, so these gradients stay exact).
                float2 foamWorldDdx = ddx(i.largeWaveSourceXZ);
                float2 foamWorldDdy = ddy(i.largeWaveSourceXZ);

                FoamLayer oceanFoamLayer, pondFoamLayer, surfFoamLayer;
                float oceanCoverage;
                FoamLayersStage(i, geom, foamWorldDdx, foamWorldDdy,
                                oceanFoamLayer, pondFoamLayer, surfFoamLayer, oceanCoverage);

                float3 outColor = CompositeSurfaceColor(geom, fresnel, reflectedColor, refractedColor,
                                                        oceanCoverage, pondFoamLayer, surfFoamLayer, sssBoost);
                outColor = ApplyShallowClarity(outColor, refractedColor, geom.shore);

                FoamLayer swashFoamLayer;
                outColor = ShorelineStage(i, geom, outColor, refractedColor, reflectedColor,
                                          foamWorldDdx, foamWorldDdy, swashFoamLayer);
                outColor = FinalCompositeStage(i, geom, outColor, oceanFoamLayer, pondFoamLayer,
                                               surfFoamLayer, swashFoamLayer);
                return float4(outColor, 1.0);
            }
            ENDCG
        }
    }
}
