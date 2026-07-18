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
        // Perceptual roughness of the sun lobe at the camera; distance widening is added
        // on top in the shader (see SUN_SPEC_DISTANCE_* constants).
        [HideInInspector] _SunRoughness ("Sun Specular Roughness", Range(0.01,1)) = 0.08
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

            // ---- Surf foam enhancement uniforms (FOAM-1/2/3, ALL RENDER-ONLY). Published as
            // globals by WaterShoreDepthField beside the _Surf* set; unpublished = every feature
            // off and the pass byte-identical. The scalar repartition weights live in
            // WaterSurfWaves.hlsl (shared with the computes); these are surface-only. ----
            sampler2D _SurfCrestFoamLut;   // R: crest-foam intensity over the lifecycle clock
            float _SurfCrestFoamLutActive; // 1 = the artist pop curve replaces the built-in window
            float _SurfCrestFoamGain;      // master gain on the curve-driven crest foam
            float _SurfFoamTrailDissolve;  // seconds an aged deposit takes to rot into holes (0 = off)
            float _SurfSwashFoam;          // swash foam strength (0 = feature off)
            float _SurfSwashFoamWidth;     // metres of run-up height covered by the foam band
            float _SurfSwashFoamDissolve;  // 0..1 how hard reflux age erodes the stranded line
            float _SurfSwashStreak;        // 0..1 downslope streak stretch during the backwash
            // How far age can push the pattern-dissolve threshold (a full push leaves only the
            // brightest pattern peaks alive - lace filaments, then nothing).
            #define SURF_TRAIL_ERODE_MAX   0.6
            #define SURF_SWASH_ERODE_MAX   0.7
            // Full-streak elongation factor of the backwash drain marks at _SurfSwashStreak = 1.
            #define SURF_SWASH_STREAK_GAIN 3.0

            // Look constants local to this surface pass (single-use here).
            // Legacy hard sun glint: kept ONLY for the underwater branch (TIR ceiling and
            // rays through the Snell window). The above-water sun is the GGX lobe in
            // SunSpecular below.
            #define SUN_GLINT_TINT          float3(10.0, 8.0, 6.0)
            #define SUN_GLINT_SHARPNESS     5000.0
            #define UNDERWATER_REFRACT_TINT float3(0.8, 1.0, 1.1)
            // Above-water Fresnel is physical Schlick (1994): F0 derived from the air/water
            // IOR (~0.02), grazing exponent 5. The old artistic curve (pow 3 lerped from a
            // 0.25 floor) reflected a quarter of the sky looking straight DOWN, which
            // flattened the surface; _FresnelFloor (default 0) keeps that override
            // available as an opt-in. The underwater branch keeps the legacy curve.
            #define FRESNEL_F0_WATER        (((IOR_WATER - IOR_AIR) * (IOR_WATER - IOR_AIR)) \
                                           / ((IOR_WATER + IOR_AIR) * (IOR_WATER + IOR_AIR)))
            #define FRESNEL_SCHLICK_POWER   5.0
            #define FRESNEL_POWER           3.0    // legacy curve - underwater branch only
            #define FRESNEL_MIN_BELOW       0.5
            // GGX sun specular (above water): the lobe's roughness grows with view distance
            // (KWS trick) - far pixels average many unresolved wave facets, so widening the
            // lobe is both the physically-motivated filter and the specular anti-aliasing;
            // the sun's glitter path broadens toward the horizon instead of aliasing into
            // sparkle dust. The clamp stops a mirror-calm lobe from blowing out HDR.
            #define SUN_SPEC_DISTANCE_METERS    1000.0 // widening saturates at this camera distance
            #define SUN_SPEC_DISTANCE_ROUGHNESS 0.12   // extra perceptual roughness added when saturated
            #define SUN_SPEC_CLAMP              50.0   // max lobe value (multiplies the sun colour)
            #define GGX_EPSILON                 1e-5
            // Roughness -> sky-cube mip: Unity's perceptual remap (the UNITY_SPECCUBE_LOD_STEPS
            // convention, mip = r * (1.7 - 0.7r) * steps) so the blur ramp behaves like probe
            // reflections artists already know. Needs the bound cube to HAVE mips: the wizard's
            // BuildSky now bakes them (regenerate the sky asset!) and imported skybox cubemaps
            // have them by default; without mips the lod sample just clamps to the sharp top.
            #define SKY_MIP_CURVE_SCALE 1.7
            #define SKY_MIP_CURVE_BIAS  0.7
            #define SKY_MIP_STEPS       6.0
            #define SSS_AMPLITUDE_EPSILON   1e-3   // guards the crest/amplitude ratio when the swell is flat

            // Peaked-look refine: short steps along the ripple normal sharpen wave crests.
            // The step COUNT is tier-driven (_PeakedRefineSteps via the body's property
            // block): each step is a dependent texture fetch per pixel, the single biggest
            // fragment cost on mobile. The cap bounds the loop for the compiler.
            #define PEAKED_REFINE_MAX_STEPS 8
            #define PEAKED_REFINE_STEP  0.005
            // Perturb the foam texture UV by the surface tilt so foam rides the ripples.
            #define FOAM_NORMAL_NUDGE   0.1
            // Skip all foam texture work below this mask level (nothing would be visible).
            #define FOAM_MASK_EPSILON   0.005
            // Flow-phased pattern drift: how far the foam pattern is dragged along the
            // local surface flow (UV units per phase) and how fast the two phases cycle.
            // Two half-offset phases cross-faded by a seesaw weight hide the reset jump
            // (classic flowmap trick), so the pattern drifts forever without stretching.
            #define FOAM_FLOW_DISTANCE  0.35
            #define FOAM_FLOW_RATE      0.5
            // Two-layer look: mask level where the dense core starts/saturates, softness
            // of the lace erosion edge, and how far the core is pushed toward plain white.
            // CORE_START sits high: the solid-white core is reserved for genuinely thick
            // foam, so everyday ripple foam stays textured lace/flecks instead of big
            // white patches (the sqrt-reach dissolve below carries the mid range).
            #define FOAM_CORE_START     0.8
            #define FOAM_CORE_FULL      0.95
            #define FOAM_LACE_SOFTNESS  0.25
            #define FOAM_CORE_WHITEN    0.7
            // Pattern-erosion band for the core cut: wider than the lace band so the
            // core rim breaks into chunkier pieces than the thin filaments.
            #define FOAM_CORE_CUT_SOFTNESS 0.35
            // Procedural foam relief (replaces the normal-map flipbook, like the whitecap):
            // finite-difference tap offset in TILE-UV units (~4 texels of a 128px cell) and
            // the gain mapping brightness gradient -> normal tilt.
            #define FOAM_PROC_NORMAL_DELTA 0.03
            #define FOAM_PROC_NORMAL_GAIN  2.0
            // (Residual foam is controlled in the SIM: the Residual Foam slider blends the thin-
            // foam survival rate toward the fresh rate, so leftovers decay away uniformly. A
            // render-side slope gate was tried and rejected - modulating foam by live wave phase
            // makes it pulse in rings, which reads as visually wrong.)
            // Foam lighting (FOAM_LIGHT_WRAP / FOAM_AMBIENT) lives in WaterFoamCommon.hlsl,
            // shared with FoamParticles/SplashParticles so every foam element shades alike.
            // Seen from BELOW, dense foam blocks the sky transmitted through the surface,
            // while thin lace scatters a faint sunlit glow through.
            #define FOAM_UNDERSIDE_DARKEN 0.6
            #define FOAM_UNDERSIDE_GLOW   0.4
            // Ocean whitecap anti-tiling: a second, rotated, differently-scaled octave of the foam pattern
            // is combined with the first so no single texture tile is resolvable toward the horizon. This is
            // continuous (unlike a hashed triangle grid it has no cell seams), so it is safe on every
            // backend. Contrast then sharpens the dissolve so crests read as crisp whitecaps, not round blobs.
            #define OCEAN_WHITECAP_OCTAVE2_SCALE     2.37       // 2nd octave world scale vs the 1st (non-integer so the grids rarely realign)
            #define OCEAN_WHITECAP_OCTAVE2_ROT_COS   0.8660254  // cos(30 deg): rotate the 2nd octave so its axes don't line up with the 1st
            #define OCEAN_WHITECAP_OCTAVE2_ROT_SIN   0.5        // sin(30 deg)
            #define OCEAN_WHITECAP_OCTAVE_BLEND_DIST 60.0       // metres over which the 2nd octave fades in (near water keeps one crisp tile)
            #define OCEAN_WHITECAP_CONTRAST          1.6        // >1 sharpens the pattern so foam breaks into crisper shapes, less round
            #define OCEAN_WHITECAP_CONTRAST_DENSE    1.0        // contrast relaxes toward this as coverage saturates (KWS), so dense foam goes SOLID instead of staying lacy
            // Whitecap parallax (SW3-style fake height): the foam pattern is sampled where a layer floating
            // PARALLAX_HEIGHT metres above the surface would intersect the view ray, so foam visually sits
            // on top of the water instead of being painted into it. The view-ray Y is floored so grazing
            // angles can't stretch the offset to infinity.
            #define OCEAN_FOAM_PARALLAX_HEIGHT 0.04
            #define OCEAN_FOAM_PARALLAX_MIN_VIEW_Y 0.25
            // Procedural whitecap relief (Crest MultiScaleFoamNormal): finite-difference the albedo
            // tile instead of shipping a normal map. DELTA = tap offset as a fraction of the tile
            // (4 texels of the 1024px source); GAIN calibrated so the default tilt is comparable to
            // the retired normal map at strength 1.
            #define OCEAN_FOAM_NORMAL_DELTA (4.0 / 1024.0)
            #define OCEAN_FOAM_NORMAL_GAIN  2.5

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
            float _ReflectionStrength;
            float _FresnelFloor;  // artistic minimum reflectance on the Schlick curve (0 = physical)
            float _SunRoughness;  // perceptual roughness of the GGX sun lobe at the camera
            float _WaveNormalStrength; // global; scales the wind-wave tilt on the normal
            float _PeakedRefineSteps;  // per-body (quality tier); see PEAKED_REFINE_MAX_STEPS
            float3 _SunColor; // Unity directional light color * intensity (global)

            sampler2D _PlanarReflectionTex;
            float     _ReflectionDistortion;

            // URP scene textures (enable Opaque Texture + Depth Texture in the URP asset)
            sampler2D _CameraOpaqueTexture;
            sampler2D _CameraDepthTexture;

            float _SSRStrength, _SSRStepSize, _SSRMaxSteps, _SSRThickness;
            float _RefractionDistortion;
            // Reflection mode flags (0/1), driven per body via the property block.
            float _UsePlanar, _UseSSR, _UseUrpProbe, _RealRefraction;
            float _ProceduralPool; // 1 = this body draws the analytic/procedural pool (tiles); 0 = surface only
            float _EnvReflectionIntensity; // brightness of the reflected sky / URP probe (not the sun glint)

            // Pool-space terrain bed height (R = bed height in pool units), baked by WaterVolume.
            sampler2D _BedTex;

            // Shore depth + SDF uniforms and helpers (Layer A/B) are declared in WaterShore.hlsl,
            // included via WaterLargeWaves.hlsl above; the debug branches below read them directly.

            // Foam: _FoamMask (sim buffer) + globals from the controller; _FoamTex
            // is an optional per-material pattern (defaults white = flat foam).
            sampler2D _FoamMask;
            sampler2D _FoamTex;
            // Dedicated ocean wave-foam (whitecap) slots: a single seamless TILING texture (not a flipbook
            // atlas) + its raw-RGB relief normal, sampled only by the FFT-ocean whitecap path. Defaults
            // (white / bump) keep the look unchanged when unassigned. Decoupled from _FoamTex so the ocean
            // whitecap and the interactive/shoreline foam can be art-directed independently.
            sampler2D _OceanWhitecapTex;
            // Auto-populated by Unity as (1/w, 1/h, w, h). Drives the flipbook half-texel inset that
            // stops bilinear filtering bleeding across cell/tile edges.
            float4 _FoamTex_TexelSize;
            float4 _FoamTexFrames; // (cols, rows) of the flipbook grid; (1,1) = plain tiling texture
            float  _FoamTexFPS;
            float  _FoamNormalStrength;
            // WORLD metres per foam-pattern tile (published per body: Foam Pattern Size). The pattern
            // is sampled in world space, so its scale is independent of the body extent (no more
            // "pattern rides the pool size") and world-anchored on windowed bodies (no more pattern
            // swimming with the camera window).
            float  _FoamTileSize;
            float4 _FoamColor;
            float _FoamEnabled, _FoamStrength, _FoamBorderWidth, _FoamContactDepth;
            // Mask level over which the foam layer fades in from nothing (edge
            // feathering). 0 disables: foam clips hard at the mask epsilon.
            float _FoamFeather;
            // How much the pattern erodes the dense core's alpha (0 = solid core,
            // 1 = fully pattern-cut like the lace).
            float _FoamCoreCut;

            // Manual bilinear sample of the float foam mask - same fix as SampleWaterBilinear:
            // WebGPU cannot hardware-filter float32, so a plain tex2D point-samples there and
            // the foam edges go blocky in builds only. The foam RT matches the sim resolution,
            // so _WaterTexel applies. tex2Dlod keeps it valid in any control flow.
            float SampleFoamMaskBilinear(float2 uv)
            {
                float2 texel = _WaterTexel.xy;
                float2 st = uv * _WaterTexel.zw - 0.5;
                float2 f = frac(st);
                float2 baseUV = (floor(st) + 0.5) * texel;
                float c00 = tex2Dlod(_FoamMask, float4(baseUV, 0, 0)).r;
                float c10 = tex2Dlod(_FoamMask, float4(baseUV + float2(texel.x, 0.0), 0, 0)).r;
                float c01 = tex2Dlod(_FoamMask, float4(baseUV + float2(0.0, texel.y), 0, 0)).r;
                float c11 = tex2Dlod(_FoamMask, float4(baseUV + texel, 0, 0)).r;
                return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
            }

            // Screen-space ray march along 'dir' from world 'p0'. On a depth hit it
            // returns the scene colour and sets hit=1; otherwise hit=0 (caller falls
            // back to planar / analytic). Kept deliberately simple + linear; tune the
            // step size / thickness in the material.
            float3 MarchSSR(float3 p0, float3 dir, out float hit)
            {
                hit = 0.0;
                float3 p = p0;
                int maxSteps = (int)_SSRMaxSteps;
                [loop]
                for (int s = 0; s < maxSteps; s++)
                {
                    p += dir * _SSRStepSize;
                    float4 clip = mul(UNITY_MATRIX_VP, float4(p, 1.0));
                    if (clip.w <= 0.0) break;
                    // Platform-correct screen UV (handles the WebGPU/GL vs D3D V-flip),
                    // matching the refraction / planar paths that use ComputeScreenPos.
                    // A hand-rolled clip.xy/clip.w*0.5+0.5 samples the mirrored row in a
                    // build and makes SSR reflections look screen-locked.
                    float4 sp = ComputeScreenPos(clip);
                    float2 uv = sp.xy / max(sp.w, 1e-5);
                    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

                    // explicit-LOD samples: safe inside a divergent loop (WebGPU)
                    float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)));
                    float rayDepth   = -mul(UNITY_MATRIX_V, float4(p, 1.0)).z; // positive eye depth
                    if (rayDepth > sceneDepth && (rayDepth - sceneDepth) < _SSRThickness)
                    {
                        hit = 1.0;
                        return tex2Dlod(_CameraOpaqueTexture, float4(uv, 0, 0)).rgb;
                    }
                }
                return 0.0;
            }

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

            // Flipbook frame pair + crossfade weight for the current time. Both the foam
            // pattern and its normal map use this, so their frames can never drift apart.
            // A (1,1) grid reduces to a plain tiled lookup (existing materials unaffected).
            void FoamFlipbookFrames(out float2 cellA, out float2 cellB, out float2 grid, out float blend)
            {
                grid = max(float2(1.0, 1.0), _FoamTexFrames.xy);
                float frameCount = grid.x * grid.y;
                float framePos = _Time.y * _FoamTexFPS;
                blend = frac(framePos);

                float frameA = fmod(floor(framePos), frameCount);
                float frameB = fmod(frameA + 1.0, frameCount);
                // Flipbooks read left-to-right, top-to-bottom; texture V runs bottom-up.
                cellA = float2(fmod(frameA, grid.x), grid.y - 1.0 - floor(frameA / grid.x));
                cellB = float2(fmod(frameB, grid.x), grid.y - 1.0 - floor(frameB / grid.x));
            }

            // Seamless flipbook-cell sample. frac(uv) tiles the pattern but spikes ddx/ddy at every tile
            // boundary, which snaps the GPU to a coarse mip there - a visible stitch line on the seam - and
            // lets bilinear filtering bleed into the neighbouring frame. Fix both: choose the mip from the
            // CONTINUOUS pre-frac gradients via tex2Dgrad, and inset the tile by half a texel so a filtered
            // tap can't leave the cell. WGSL derivative uniformity: the pre-frac uv gradients (uvDdx/uvDdy)
            // are HOISTED by the caller from uniform control flow - computing ddx/ddy here would be undefined,
            // since this helper runs inside the non-uniform foam-mask branches.
            float4 SampleFlipbookCell(sampler2D tex, float2 uv, float2 uvDdx, float2 uvDdy, float2 cell, float2 grid, float2 invSize)
            {
                float2 gradX = uvDdx / grid;
                float2 gradY = uvDdy / grid;
                // Half a texel in tile space, capped so the 1x1 white-fallback texture (no foam assigned,
                // invSize = 1) can't invert the clamp below; a white tap stays white either way.
                float2 inset = min(invSize * 0.5 * grid, 0.49);
                float2 tiled = clamp(frac(uv), inset, 1.0 - inset);
                return tex2Dgrad(tex, (tiled + cell) / grid, gradX, gradY);
            }

            // Foam pattern with frame advance + crossfade: the foam churns internally
            // even where the mask is static. Grid (1,1) = a single seamless TILING texture:
            // plain hardware-wrap sample (like the ocean whitecap) - the flipbook cell inset
            // would break a seamless tile's edges, and there are no frames to crossfade.
            // WGSL derivative uniformity: gradients are passed in (hoisted by the caller in
            // uniform control flow), never derived here - this runs inside non-uniform
            // foam-mask branches where ddx/ddy would be undefined.
            float3 SampleFoamPattern(float2 uv, float2 uvDdx, float2 uvDdy)
            {
                float2 cellA, cellB, grid; float blend;
                FoamFlipbookFrames(cellA, cellB, grid, blend);
                if (grid.x * grid.y <= 1.0)
                    return tex2Dgrad(_FoamTex, uv, uvDdx, uvDdy).rgb;
                float3 a = SampleFlipbookCell(_FoamTex, uv, uvDdx, uvDdy, cellA, grid, _FoamTex_TexelSize.xy).rgb;
                float3 b = SampleFlipbookCell(_FoamTex, uv, uvDdx, uvDdy, cellB, grid, _FoamTex_TexelSize.xy).rgb;
                return lerp(a, b, blend);
            }

            // Shared foam evaluation for BOTH sides of the surface. Pattern: tiled/flipbook
            // texture dragged along the local flow; two half-offset phases cross-faded by a
            // seesaw weight give endless drift with no visible reset. A rotated, rescaled
            // second octave fades in with camera distance (the ocean whitecap's anti-tiling)
            // so the pattern's repeat stops reading as a grid. Layers: dense white core
            // where the mask is thick; as it thins the pattern's dark regions erode away
            // first, so decaying foam breaks into filaments instead of ghosting out.
            // Tilt: PROCEDURAL relief from finite differences of the pattern (Crest-style,
            // matching the ocean whitecap - no normal map), scaled by the mask so sparse
            // foam doesn't dent the shading.
            // WGSL derivative uniformity: fuvDdx/fuvDdy are the SCREEN derivatives of fuv, hoisted
            // by the caller BEFORE its non-uniform mask branch - every sample below runs in
            // non-uniform control flow, where implicit-derivative tex2D/ddx/ddy are undefined.
            // The flow/phase/relief offsets are ADDITIVE, so the base gradients stay exact; the
            // rotated octave is a linear transform, so its gradients get the same rotation/scale.
            void EvaluateFoam(float2 fuv, float2 fuvDdx, float2 fuvDdy,
                              float2 flowXZ, float mask, float camDist,
                              out float3 pattern, out float core, out float lace,
                              out float alpha, out float2 tilt)
            {
                float2 flowDir = flowXZ * FOAM_FLOW_DISTANCE;
                float phaseA = frac(_Time.y * FOAM_FLOW_RATE);
                float phaseB = frac(phaseA + 0.5);
                float seesaw = abs(phaseA * 2.0 - 1.0);
                float2 uvA = fuv - flowDir * phaseA;
                float3 baseA = SampleFoamPattern(uvA, fuvDdx, fuvDdy);
                pattern = lerp(baseA, SampleFoamPattern(fuv - flowDir * phaseB, fuvDdx, fuvDdy), seesaw);

                // Distance anti-tiling, same recipe as SampleOceanWhitecapPattern: min() of a
                // rotated second octave keeps foam only where BOTH octaves agree, breaking the
                // repeat into irregular shapes toward the distance.
                float octaveBlend = saturate(camDist / OCEAN_WHITECAP_OCTAVE_BLEND_DIST);
                if (octaveBlend > 0.0)
                {
                    float2 rotated = float2(
                        fuv.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuv.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                        fuv.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuv.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
                        / OCEAN_WHITECAP_OCTAVE2_SCALE;
                    // Same linear transform applied to the hoisted gradients (exact, no new ddx).
                    float2 rotDdx = float2(
                        fuvDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuvDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                        fuvDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuvDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
                        / OCEAN_WHITECAP_OCTAVE2_SCALE;
                    float2 rotDdy = float2(
                        fuvDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - fuvDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                        fuvDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + fuvDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS)
                        / OCEAN_WHITECAP_OCTAVE2_SCALE;
                    float3 octave1 = SampleFoamPattern(rotated - flowDir * phaseA, rotDdx, rotDdy);
                    pattern = lerp(pattern, min(pattern, octave1), octaveBlend);
                }

                core = smoothstep(FOAM_CORE_START, FOAM_CORE_FULL, mask);
                // Dissolve threshold with sqrt REACH (the KWS law the whitecap path already
                // uses): a THIN mask reaches high into the pattern, so light foam shows as a
                // few bright FLECKS tracking the ripple crests instead of nothing-then-blob.
                // (The old linear 1-mask threshold could exceed a midtone texture's maximum,
                // so thin foam vanished entirely and moderate foam jumped to solid patches.)
                float reach = sqrt(saturate(mask));
                float laceThreshold = 1.0 - reach;
                lace = saturate((pattern.r - laceThreshold) / FOAM_LACE_SOFTNESS);

                // Core cut (user-tunable): erode the dense core's alpha by the pattern -
                // same trick as the lace, wider band - so the core rim breaks into
                // texture detail instead of ending in a smooth mask blob. 0 = solid core
                // (original look). Even at full cut the lace term below keeps the
                // saturated centre near-solid; only the darkest pattern texels open up.
                float coreCut = saturate((pattern.r - laceThreshold) / FOAM_CORE_CUT_SOFTNESS);
                float coreAlpha = core * lerp(1.0, coreCut, _FoamCoreCut);

                // Edge feathering (user-tunable): fade the layer out smoothly as the
                // mask thins instead of clipping at the mask epsilon. 0 = off (hard
                // edge, the original look). Core is untouched by construction: it only
                // exists above FOAM_CORE_START, well over any sensible feather band.
                float feather = (_FoamFeather > 0.0) ? smoothstep(0.0, _FoamFeather, mask) : 1.0;
                // The reach term doubles as the fleck weight: thin-mask flecks stay readable
                // without linear dimming forcing the strength slider up into blob territory.
                alpha = max(coreAlpha, lace * reach) * feather;

                // Procedural relief (Crest MultiScaleFoamNormal): brightness reads as bubble
                // height, so the negated finite-difference gradient tilts the shading normal
                // away from raised foam. Taken at phase A of the base octave (relief slightly
                // lagging the crossfade is imperceptible; the offsets stay consistent).
                float rx = SampleFoamPattern(uvA + float2(FOAM_PROC_NORMAL_DELTA, 0.0), fuvDdx, fuvDdy).r;
                float rz = SampleFoamPattern(uvA + float2(0.0, FOAM_PROC_NORMAL_DELTA), fuvDdx, fuvDdy).r;
                tilt = -FOAM_PROC_NORMAL_GAIN * float2(rx - baseA.r, rz - baseA.r)
                     * (_FoamNormalStrength * mask);
            }

            // Ocean whitecap pattern with distance anti-tiling. Combines the base foam tile with a rotated,
            // differently-scaled second octave that fades in with distance, so the texture's repeat stops
            // reading as a grid toward the horizon. min() of the two octaves as they blend keeps foam only
            // where BOTH agree, which also breaks the round patches into more whitecap-like shapes. Returns
            // the pattern rgb; .r drives the coverage dissolve.
            // tileSize is a PARAMETER so the surf whitewash can reuse this exact pipeline with its
            // own dedicated tiling (decoupled from the ocean whitecap knob); the no-arg wrappers
            // below keep the ocean call sites unchanged.
            // WGSL derivative uniformity: worldDdx/worldDdy are the screen derivatives of the BASE
            // (pre-parallax) world XZ, hoisted by the caller in uniform control flow - these taps run
            // inside non-uniform coverage branches where implicit-derivative tex2D is undefined. The
            // parallax lift is ADDITIVE so the base gradients are exact; the tile divide and the
            // rotated octave are linear, so the gradients get the same scale/rotation.
            float3 SampleOceanWhitecapPatternTiled(float2 worldXZ, float camDist, float tileSize,
                                                   float2 worldDdx, float2 worldDdy)
            {
                // Dedicated whitecap: a single seamless tiling texture sampled with hardware Repeat wrap -
                // no frac/flipbook cell, so no atlas mip-bleed and no tile-edge seam. The rotated second
                // octave still hides the texture's own repeat toward the horizon.
                float tile0 = max(tileSize, 1e-3);
                float2 uv0 = worldXZ / tile0;
                float3 octave0 = tex2Dgrad(_OceanWhitecapTex, uv0, worldDdx / tile0, worldDdy / tile0).rgb;

                float2 rotated = float2(
                    worldXZ.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldXZ.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                    worldXZ.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldXZ.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS);
                float tile1 = max(tileSize * OCEAN_WHITECAP_OCTAVE2_SCALE, 1e-3);
                float2 rotDdx = float2(
                    worldDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                    worldDdx.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldDdx.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS) / tile1;
                float2 rotDdy = float2(
                    worldDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_COS - worldDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_SIN,
                    worldDdy.x * OCEAN_WHITECAP_OCTAVE2_ROT_SIN + worldDdy.y * OCEAN_WHITECAP_OCTAVE2_ROT_COS) / tile1;
                float3 octave1 = tex2Dgrad(_OceanWhitecapTex, rotated / tile1, rotDdx, rotDdy).rgb;

                float blend = saturate(camDist / OCEAN_WHITECAP_OCTAVE_BLEND_DIST);
                return lerp(octave0, min(octave0, octave1), blend);
            }

            float3 SampleOceanWhitecapPattern(float2 worldXZ, float camDist,
                                              float2 worldDdx, float2 worldDdy)
            {
                return SampleOceanWhitecapPatternTiled(worldXZ, camDist, _OceanFoamTileSize, worldDdx, worldDdy);
            }

            // Relief tilt (xy) of the whitecap, derived PROCEDURALLY from the albedo tile by finite
            // differences (Crest's MultiScaleFoamNormal): brightness reads as bubble height, so the
            // negated gradient tilts the shading normal away from raised foam. Self-flattening - where
            // there is no foam the gradient is ~0 - and it retires the separate normal-map texture
            // (_OceanWhitecapNormalTex kept only as an unused asset on disk).
            // WGSL derivative uniformity: same hoisted-gradient contract as the pattern sampler above -
            // called inside non-uniform foam branches, so the finite-difference taps use explicit
            // gradients (the tap offsets are additive, so all three share the base uv gradients).
            float2 SampleOceanWhitecapTiltTiled(float2 worldXZ, float tileSize,
                                                float2 worldDdx, float2 worldDdy)
            {
                float tile = max(tileSize, 1e-3);
                float dd = tile * OCEAN_FOAM_NORMAL_DELTA;
                float2 uvDdx = worldDdx / tile;
                float2 uvDdy = worldDdy / tile;
                float c  = tex2Dgrad(_OceanWhitecapTex, worldXZ / tile, uvDdx, uvDdy).r;
                float cx = tex2Dgrad(_OceanWhitecapTex, (worldXZ + float2(dd, 0.0)) / tile, uvDdx, uvDdy).r;
                float cz = tex2Dgrad(_OceanWhitecapTex, (worldXZ + float2(0.0, dd)) / tile, uvDdx, uvDdy).r;
                return -OCEAN_FOAM_NORMAL_GAIN * float2(cx - c, cz - c);
            }

            float2 SampleOceanWhitecapTilt(float2 worldXZ, float2 worldDdx, float2 worldDdy)
            {
                return SampleOceanWhitecapTiltTiled(worldXZ, _OceanFoamTileSize, worldDdx, worldDdy);
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

            // Sample the planar reflection RT at the fragment's screen UV, nudged
            // by the surface normal so ripples wobble the mirror image.
            float3 SamplePlanarReflection(float4 screenPos, float3 normal)
            {
                float2 uv = screenPos.xy / max(screenPos.w, 1e-5);
                uv += normal.xz * _ReflectionDistortion;
                return tex2D(_PlanarReflectionTex, saturate(uv)).rgb;
            }

            // Legacy hard sun glint (pow-5000 disc). Underwater paths only - the above-water
            // sun is the roughness-driven GGX lobe in SunSpecular, so folding this fixed disc
            // into the above-water mirror would double the sun.
            float3 LegacySunGlint(float3 worldRay)
            {
                return SUN_GLINT_TINT * _SunColor * pow(max(0.0, dot(_LightDir, worldRay)), SUN_GLINT_SHARPNESS);
            }

            // Sample the SKY environment (reflection probe / procedural sky) for a WORLD-space
            // ray - no sun term. This is what the water REFLECTS - never the analytic pool tiles.
            // WGSL derivative uniformity: the grad variant exists for call sites inside NON-UNIFORM
            // control flow (getSurfaceRayColor's per-fragment up/down ray split), where texCUBE's
            // implicit derivatives are undefined - the caller hoists ddx/ddy of the ray beforehand.
            float3 SampleSkyEnvironmentGrad(float3 worldRay, float3 rayDdx, float3 rayDdy)
            {
                // Reflection base is ALWAYS a plain cubemap in _Sky: the assigned Sky slot for procedural
                // sky, or the scene's skybox cubemap when Reflect URP Probe is on (WaterUniformPublisher
                // picks which). Sampling a cubemap works in EVERY render path - unlike unity_SpecCube0,
                // which URP Forward+ (used on WebGPU) does not bind per-object, so the old probe path read
                // the default/skybox and the plane showed no reflection.
                float3 color = texCUBEgrad(_Sky, worldRay, rayDdx, rayDdy).rgb;
                // Art-directed brightness of the reflected environment (sky OR probe). Applied before any
                // sun term so the sun stays a fixed specular regardless of the mirror intensity.
                color *= _EnvReflectionIntensity;
                return color;
            }

            // Environment WITH the legacy sun glint - the underwater branch and the shared
            // ray tracer (getSurfaceRayColor) keep this so their look is unchanged by the
            // above-water GGX rework.
            float3 SampleEnvironmentGrad(float3 worldRay, float3 rayDdx, float3 rayDdy)
            {
                return SampleSkyEnvironmentGrad(worldRay, rayDdx, rayDdy) + LegacySunGlint(worldRay);
            }

            // Implicit-derivative conveniences for UNIFORM control flow (the reflection paths and
            // the horizon haze, all gated on uniforms) - identical results, no hoisting needed there.
            float3 SampleEnvironment(float3 worldRay)
            {
                return SampleEnvironmentGrad(worldRay, ddx(worldRay), ddy(worldRay));
            }

            // Sky environment at a roughness-selected mip: a rough surface reflects a BLURRED
            // sky. Explicit LOD, so it is WGSL-safe in any control flow; if the bound cube has
            // no mips the lod clamps to 0 and this degrades to the old sharp mirror.
            float3 SampleSkyEnvironmentRough(float3 worldRay, float roughness)
            {
                float mip = roughness * (SKY_MIP_CURVE_SCALE - SKY_MIP_CURVE_BIAS * roughness)
                          * SKY_MIP_STEPS;
                return texCUBElod(_Sky, float4(worldRay, mip)).rgb * _EnvReflectionIntensity;
            }

            // Shared surface roughness for the whole specular family: the artist knob plus the
            // view-distance widening (see SUN_SPEC_DISTANCE_*). Drives BOTH the GGX sun lobe
            // and the sky-reflection mip, so the sun and the mirror around it roughen together.
            float EffectiveWaterRoughness(float viewDist)
            {
                return saturate(_SunRoughness
                    + SUN_SPEC_DISTANCE_ROUGHNESS * saturate(viewDist / SUN_SPEC_DISTANCE_METERS));
            }

            // ---- GGX sun specular (above water). Replaces the fixed pow-5000 glint with a
            // Trowbridge-Reitz lobe: NDF * Smith-joint visibility (Karis approximation) *
            // Schlick Fresnel at the half-vector, times the sun colour. Roughness comes from
            // EffectiveWaterRoughness, which is what widens the glitter path toward the
            // horizon and suppresses far sparkle. ----
            float3 SunSpecular(float3 normal, float3 viewDir, float roughness)
            {
                float alpha  = roughness * roughness;
                float alpha2 = alpha * alpha;

                float3 halfDir = normalize(viewDir + _LightDir);
                float noh = saturate(dot(normal, halfDir));
                float nol = saturate(dot(normal, _LightDir));
                float nov = saturate(dot(normal, viewDir));
                float loh = saturate(dot(_LightDir, halfDir));

                float ndfDenom = noh * noh * (alpha2 - 1.0) + 1.0;
                float ndf = alpha2 / max(UNITY_PI * ndfDenom * ndfDenom, GGX_EPSILON);
                float visibility = 0.5 / max(lerp(2.0 * nol * nov, nol + nov, alpha), GGX_EPSILON);
                float fresnelSpec = FRESNEL_F0_WATER
                    + (1.0 - FRESNEL_F0_WATER) * pow(1.0 - loh, FRESNEL_SCHLICK_POWER);

                return min(ndf * visibility * fresnelSpec * nol, SUN_SPEC_CLAMP) * _SunColor;
            }

            // ---- Manual URP main-light shadow tap (this pass is CGPROGRAM and cannot include URP's
            // Shadows.hlsl). Mirrors URP's cascade select + a single hard depth compare - enough to GATE
            // the analytic floor caustic (a soft multiply), NOT to draw crisp shadows. Returns 1 (lit)
            // when shadows are off/unsupported, so the caustic falls back to its legacy look. ----
#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)
            // The shadow map is a DEPTH texture; URP binds a COMPARISON sampler for it. This pass does its
            // own hard depth compare (below), so it needs the raw depth, not hardware comparison. Declaring
            // it as a plain sampler2D made WebGPU bind URP's comparison sampler to a non-comparison slot
            // (validation error -> the whole WaterSurface bind group is invalid -> black screen in builds).
            // Read it as a Texture2D with an explicit NON-comparison point sampler instead.
            Texture2D _MainLightShadowmapTexture;
            SamplerState sampler_PointClamp; // Unity inline sampler: point filter, clamp wrap (non-comparison)
            float4x4  _MainLightWorldToShadow[5];
            float4    _CascadeShadowSplitSpheres0;
            float4    _CascadeShadowSplitSpheres1;
            float4    _CascadeShadowSplitSpheres2;
            float4    _CascadeShadowSplitSpheres3;
            float4    _CascadeShadowSplitSphereRadii;
            float4    _MainLightShadowParams; // x = shadow strength

            float WaterMainLightShadow(float3 worldPos)
            {
                // Cascade index from distance to the four split spheres (URP ComputeCascadeIndex).
                float3 f0 = worldPos - _CascadeShadowSplitSpheres0.xyz;
                float3 f1 = worldPos - _CascadeShadowSplitSpheres1.xyz;
                float3 f2 = worldPos - _CascadeShadowSplitSpheres2.xyz;
                float3 f3 = worldPos - _CascadeShadowSplitSpheres3.xyz;
                float4 d2 = float4(dot(f0, f0), dot(f1, f1), dot(f2, f2), dot(f3, f3));
                float4 w  = float4(d2 < _CascadeShadowSplitSphereRadii);
                w.yzw = saturate(w.yzw - w.xyz);
                int cascade = min(3, (int)(4.0 - dot(w, float4(4.0, 3.0, 2.0, 1.0))));

                float4 c = mul(_MainLightWorldToShadow[cascade], float4(worldPos, 1.0));
                c.xyz /= c.w;
                if (c.z <= 0.0 || c.z >= 1.0) return 1.0; // outside the atlas -> treat as lit

                float occluder = _MainLightShadowmapTexture.SampleLevel(sampler_PointClamp, c.xy, 0.0).r;
                // In shadow when the fragment is FARTHER from the light than the stored occluder.
            #if defined(UNITY_REVERSED_Z)
                float lit = c.z < occluder ? 0.0 : 1.0;
            #else
                float lit = c.z > occluder ? 0.0 : 1.0;
            #endif
                return lerp(1.0, lit, _MainLightShadowParams.x); // fold in shadow strength
            }
#else
            float WaterMainLightShadow(float3 worldPos) { return 1.0; }
#endif

            // Shade a WORLD-space ray: a DOWN ray refracts into the pool and samples the analytic
            // floor/walls (the tiles seen THROUGH the water); an UP ray is a reflection and samples
            // the environment only. Reflections never return the pool tiles - the floor is seen via
            // refraction alone. The pool box is intersected in POOL space so rotation / non-uniform
            // extent is handled exactly, while the environment uses the WORLD ray.
            // Deep-water in-scatter for the refracted ray: the lit body colour (the crest SSS is added
            // emissively after compositing, not here). The view direction is reconstructed from the camera
            // to this fragment so the scatter phase tracks the real view.
            float3 DeepWaterColor(float3 worldOrigin, float3 waterColor)
            {
                float3 viewDirWS = normalize(_WorldSpaceCameraPos - worldOrigin);
                return WaterInscatterColor(viewDirWS, _LightDir, _SunColor, 0.0) * waterColor;
            }

            // ---- WGSL derivative uniformity: gradient-fed clones of WaterCommon.hlsl's
            // GetWallShadeSplit / GetWallColorShadowed. getSurfaceRayColor reaches the wall colour
            // inside a PER-FRAGMENT (non-uniform) ray branch, where the include's implicit-derivative
            // tex2D taps of _CausticTex / _Tiles are undefined in WGSL - and the include can't take
            // gradients without changing every other caller. The maths below is byte-identical to
            // the include; only the two taps become tex2Dgrad fed by the caller's hoisted
            // floor-point derivatives. ----
            float GetWallShadeSplitGrad(float3 p, float3 normal, float3 pDdx, float3 pDdy,
                                        out float causticTerm)
            {
                causticTerm = 0.0;
                float scale = 0.5;
                scale /= max(length(p), POOL_AO_MIN_DIST);                                 // pool ambient occlusion

                float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                float diffuse = max(0.0, dot(refractedLight, normal));
                // Manual bilinear (not tex2D): WebGPU point-samples float32 textures, which turned
                // the above/below-waterline cut into a blocky stair-step in builds.
                float4 info = SampleWaterBilinear(p.xz * 0.5 + 0.5);
                if (p.y < info.r)
                {
                    // ProjectCausticUV is linear in p (refractedLight is uniform), so differencing it
                    // along the hoisted position derivatives yields the exact caustic-UV gradients.
                    float2 cuv = ProjectCausticUV(p, refractedLight);
                    float2 cuvDdx = ProjectCausticUV(p + pDdx, refractedLight) - cuv;
                    float2 cuvDdy = ProjectCausticUV(p + pDdy, refractedLight) - cuv;
                    float4 caustic = tex2Dgrad(_CausticTex, cuv, cuvDdx, cuvDdy);
                    causticTerm = diffuse * caustic.r * caustic.g;
                }
                else
                {
                    // shadow for the rim of the pool
                    float2 t = IntersectCube(p, refractedLight, POOL_BOX_MIN, POOL_BOX_MAX);
                    diffuse *= 1.0 / (1.0 + exp(-RIM_SHADOW_SHARPNESS / (1.0 + RIM_SHADOW_SPREAD * (t.y - t.x)) * (p.y + refractedLight.y * t.y - POOL_RIM_HEIGHT)));
                    scale += diffuse * 0.5;
                }
                return scale;
            }

            float3 GetWallColorShadowedGrad(float3 p, float causticShadow, float3 pDdx, float3 pDdy)
            {
                float2 uv; float3 normal, tangent, bitangent;
                WallSurface(p, uv, normal, tangent, bitangent);
                // The wall UV is a per-face linear pick of two position components (WallSurface):
                // mirror the same face selection on the hoisted position derivatives.
                float2 uvDdx, uvDdy;
                if (abs(p.x) > 0.999)      { uvDdx = pDdx.yz * 0.5; uvDdy = pDdy.yz * 0.5; }
                else if (abs(p.z) > 0.999) { uvDdx = pDdx.yx * 0.5; uvDdy = pDdy.yx * 0.5; }
                else                       { uvDdx = pDdx.xz * 0.5; uvDdy = pDdy.xz * 0.5; }
                float causticTerm;
                float scale = GetWallShadeSplitGrad(p, normal, pDdx, pDdy, causticTerm);
                float shade = scale + causticTerm * WALL_CAUSTIC_LEGACY_STRENGTH * causticShadow;
                return tex2Dgrad(_Tiles, uv, uvDdx, uvDdy).rgb * shade;
            }

            float3 getSurfaceRayColor(float3 worldOrigin, float3 worldRay, float3 waterColor)
            {
                // WGSL derivative uniformity: the down/up ray split below is per-fragment
                // (non-uniform) and BOTH sides sample textures (pool tiles/caustic, sky cube).
                // Hoist the screen derivatives of every sampling coordinate here, in uniform
                // control flow (both call sites branch only on uniforms), so the in-branch
                // samples can use explicit gradients. The floor point is computed for every
                // fragment purely so its derivative is well-defined; up rays never read it.
                float3 rayDdx = ddx(worldRay);
                float3 rayDdy = ddy(worldRay);
                float3 po = WorldToPool(worldOrigin);
                float3 pd = WorldDirToPool(worldRay);
                float2 t = IntersectCube(po, pd, POOL_BOX_MIN, POOL_BOX_MAX);
                float3 floorPool = po + pd * t.y;
                float3 floorDdx = ddx(floorPool);
                float3 floorDdy = ddy(floorPool);
                if (worldRay.y < 0.0)
                {
                    // Open water has no pool floor to sample: return the deep-water inscattering
                    // colour so the analytic refraction reads as "can't see the bottom" rather than
                    // pool tiles. The _REAL_REFRACTION path (in frag) samples the actual scene where
                    // geometry exists and overrides this; this is the no-geometry fallback.
                    if (_LargeBody > 0.5)
                        return DeepWaterColor(worldOrigin, waterColor);

                    // Pool tiles only when this body draws the PROCEDURAL (analytic) pool AND real
                    // refraction isn't already sampling the actual scene. Surface-only bodies (no pool)
                    // and the real-refraction path fall back to the deep-water/fog colour, never tiles.
                    if (_ProceduralPool < 0.5 || _RealRefraction > 0.5)
                        return DeepWaterColor(worldOrigin, waterColor);

                    // Gate the floor caustic by the main-light shadow at the FLOOR's world position, so
                    // a caster's shadow on the pool bottom kills the caustic there (like the geometry paths).
                    // When the occluder pass is active the refracted object shadow is already baked into the
                    // caustic green channel (caustic.r * caustic.g in GetWallShadeSplitGrad), so don't also
                    // apply the un-refracted shadow map on top - that would double-shadow the reflected floor.
                    float causticShadow = (_CausticOccluderActive > 0.5) ? 1.0 : WaterMainLightShadow(PoolToWorld(floorPool));
                    return GetWallColorShadowedGrad(floorPool, causticShadow, floorDdx, floorDdy) * waterColor;
                }
                return SampleEnvironmentGrad(worldRay, rayDdx, rayDdy);
            }

            fixed4 frag(v2f i) : SV_Target
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
                float3 incomingRay = normalize(i.worldPos - _WorldSpaceCameraPos);

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

                if (_Underwater > 0.5)
                {
                    normal = -normal;
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
                    // getSurfaceRayColor used to sample the analytic wall (a stale baked-in tile
                    // reflection on the underside of the surface).
                    float3 reflectedColor = SampleEnvironment(reflectedRay) * UNDERWATER_COLOR;
                    float3 refractedColor = getSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0)) * UNDERWATER_REFRACT_TINT;

                    // Real transparency from below: sample the live scene above the surface.
                    if (_RealRefraction > 0.5)
                    {
                        float2 ruvU = i.screenPos.xy / max(i.screenPos.w, 1e-5) + normal.xz * _RefractionDistortion;
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
                else
                {
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_AIR / IOR_WATER);
                    // Physical Schlick Fresnel from the air/water IOR: ~2% mirror straight down
                    // (deep clear water at your feet), full mirror at grazing (the horizon).
                    // saturate: float error can push the dot above 1 -> negative pow base -> NaN.
                    float fresnelGrazing = pow(saturate(1.0 - dot(normal, -incomingRay)), FRESNEL_SCHLICK_POWER);
                    float fresnel = max(FRESNEL_F0_WATER + (1.0 - FRESNEL_F0_WATER) * fresnelGrazing,
                                        _FresnelFloor);

                    // Reflection samples the environment (sky / URP probe) for ANY reflected direction.
                    // getSurfaceRayColor would route a below-horizon ray - common at grazing angles and on
                    // wave slopes, exactly where Fresnel makes the reflection strongest - into the pool
                    // floor and return the TILES, which showed up as tile "highlights" and hid the probe.
                    // The underwater branch already samples the environment directly; match it here.
                    // SKY only: the sun is added as the GGX lobe after the composite, so the legacy
                    // glint must not ride along inside the mirror term (it would double the sun).
                    // Sampled at the SHARED roughness mip: the mirror blurs with the same roughness
                    // that widens the sun lobe - near-sharp at your feet, hazier toward the horizon.
                    float surfaceViewDist = length(i.worldPos - _WorldSpaceCameraPos);
                    float surfaceRoughness = EffectiveWaterRoughness(surfaceViewDist);
                    float3 reflectedColor = SampleSkyEnvironmentRough(reflectedRay, surfaceRoughness);

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

                    float3 refractedColor = getSurfaceRayColor(i.worldPos, refractedRay, ABOVEWATER_COLOR);

                    // ---- Reflection: analytic -> planar -> SSR (SSR wins where it hits). The toggles
                    // are uniform-driven (published per body via the property block), so they are live. ----
                    if (_UsePlanar > 0.5)
                        reflectedColor = SamplePlanarReflection(i.screenPos, normal);
                    if (_UseSSR > 0.5)
                    {
                        float ssrHit;
                        float3 ssr = MarchSSR(i.worldPos, reflectedRay, ssrHit); // SSR marches in world space
                        reflectedColor = lerp(reflectedColor, ssr, ssrHit * _SSRStrength);
                    }

                    // ---- Real transparency: sample the actual scene behind the surface, instead of
                    // the analytic pool; else fog the ANALYTIC pool by the refracted chord. Only one
                    // path runs, so the real-refraction view is never double-fogged. ----
                    if (_RealRefraction > 0.5)
                    {
                        float2 ruv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                        ruv += normal.xz * _RefractionDistortion;
                        refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruv)).rgb * ABOVEWATER_COLOR;

                        // Fog the transmitted view by the water thickness behind the surface
                        // (scene eye-depth - surface eye-depth), so heavy fog reads through too.
                        float sceneEyeR = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(saturate(ruv), 0, 0)));
                        float surfEyeR  = -mul(UNITY_MATRIX_V, float4(i.worldPos, 1.0)).z;
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
                            // KWS contrast law: dense coverage RELAXES the contrast (heavy foam stops
                            // being eroded into lace and goes solid) and the dissolve threshold falls
                            // with sqrt(coverage) so mid coverage reaches further into the pattern.
                            float coverageSat = saturate(coverage);
                            float contrast = lerp(OCEAN_WHITECAP_CONTRAST, OCEAN_WHITECAP_CONTRAST_DENSE, coverageSat);
                            float sharpened = pow(saturate(oceanFoamPattern.r), contrast);
                            float threshold = 1.0 - sqrt(coverageSat);
                            oceanFoam = smoothstep(threshold, threshold + max(_OceanFoamFeather, 1e-3), sharpened);
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
                        float3 oceanFoamTangent = normalize(cross(normal, float3(0.0, 0.0, 1.0)));
                        float3 oceanFoamBitangent = cross(normal, oceanFoamTangent);
                        float3 oceanFoamNormal = normalize(normal + oceanFoamTangent * oceanFoamTilt.x
                                                                  + oceanFoamBitangent * oceanFoamTilt.y);

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
                            float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                            float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                            float surfEye  = -mul(UNITY_MATRIX_V, float4(i.worldPos, 1.0)).z;
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
                            float3 foamTangent = normalize(cross(normal, float3(0.0, 0.0, 1.0)));
                            float3 foamBitangent = cross(normal, foamTangent);
                            float3 foamNormal = normalize(normal + foamTangent * tilt.x + foamBitangent * tilt.y);

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
                        // KWS contrast law: dense coverage relaxes the contrast (heavy whitewash
                        // goes solid) and the dissolve threshold falls with sqrt(coverage).
                        float surfContrast = lerp(OCEAN_WHITECAP_CONTRAST, OCEAN_WHITECAP_CONTRAST_DENSE, surfCoverage);
                        float surfSharpened = pow(saturate(surfPattern.r), surfContrast);
                        float surfThreshold = 1.0 - sqrt(surfCoverage);
                        // FOAM-2: aged deposit rots into HOLES, not a uniform fade - age raises the
                        // pattern-dissolve threshold, so old foam breaks into lace patches, then
                        // filaments, then nothing (real sea foam dies by holes opening). trailAge
                        // is bore-gated, so the bore head (age ~0) stays solid. 0 seconds = off.
                        if (_SurfFoamTrailDissolve > 0.0)
                            surfThreshold += saturate(surfFrag.trailAge / _SurfFoamTrailDissolve)
                                           * SURF_TRAIL_ERODE_MAX;
                        float surfFoam = smoothstep(surfThreshold,
                                                    surfThreshold + max(_SurfFoamFeather, 1e-3),
                                                    surfSharpened);
                        if (surfFoam > FOAM_MASK_EPSILON)
                        {
                            float2 surfTiltXY = SampleOceanWhitecapTiltTiled(surfSampleXZ, surfTile,
                                                                             foamWorldDdx, foamWorldDdy)
                                              * (_FoamNormalStrength * surfFoam);
                            float3 surfFoamTangent = normalize(cross(normal, float3(0.0, 0.0, 1.0)));
                            float3 surfFoamBitangent = cross(normal, surfFoamTangent);
                            float3 surfFoamNormal = normalize(normal + surfFoamTangent * surfTiltXY.x
                                                                     + surfFoamBitangent * surfTiltXY.y);
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
                        && shoreFrag.depth > 0.0 && shoreFrag.depth < 0.6)
                    {
                        float shallowClarity = 1.0 - saturate(shoreFrag.depth / 0.6);
                        outColor = lerp(outColor, refractedColor,
                                        shallowClarity * 0.5 * shoreFrag.influence);
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
                            outColor = lerp(outColor, refractedColor, 0.6 + 0.3 * filmT);
                            float aboveFilm = saturate((beachRise - swashLevel)
                                                       / max(wetLevel - swashLevel, 1e-3));
                            float glaze = aboveFilm * smoothstep(0.0, 0.25, (wetLevel - beachRise)
                                                                 / max(wetLevel, 1e-3));
                            float3 wetLook = refractedColor * 0.7 + reflectedColor * 0.12;
                            outColor = lerp(outColor, wetLook, glaze * 0.85);

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
                                    // Same contrast/threshold law as the surf whitewash, plus the
                                    // reflux hole-erosion: age raises the dissolve threshold, so
                                    // the stranded line rots into lace patches, then filaments.
                                    float swashContrast = lerp(OCEAN_WHITECAP_CONTRAST,
                                                               OCEAN_WHITECAP_CONTRAST_DENSE,
                                                               swashCoverage);
                                    float swashSharpened = pow(saturate(swashPattern.r), swashContrast);
                                    float swashThreshold = 1.0 - sqrt(swashCoverage)
                                        + refluxAge * _SurfSwashFoamDissolve * SURF_SWASH_ERODE_MAX;
                                    float swashFoam = smoothstep(swashThreshold,
                                                                 swashThreshold
                                                                 + max(_SurfFoamFeather, 1e-3),
                                                                 swashSharpened);
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
