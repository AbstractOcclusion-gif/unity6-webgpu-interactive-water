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
        // Water fog is global now (driven by WaterController), shared with the
        // object/pool shaders so it's consistent however you view the water.

        [Header(Foam)]
        _FoamTex ("Foam Pattern (optional, may be a flipbook)", 2D) = "white" {}
        _FoamTexFrames ("Foam Flipbook Grid (cols, rows)", Vector) = (1, 1, 0, 0)
        _FoamTexFPS ("Foam Flipbook Frame Rate", Range(0, 30)) = 10
        _FoamNormalTex ("Foam Normal Map (same flipbook grid)", 2D) = "bump" {}
        _FoamNormalStrength ("Foam Normal Strength", Range(0, 3)) = 1
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
            // Reflection mode (planar / SSR / URP-probe base / real refraction) is UNIFORM-driven,
            // published per body every frame via the MaterialPropertyBlock (WaterUniformPublisher),
            // so it updates live in the editor and needs no shader variants.
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl" // brings WaterShared (via WaterCommon): POOL_RIM_HEIGHT etc.
            #include "WaterLargeWaves.hlsl" // open-water world-space wave normal (large-body path)

            // Look constants local to this surface pass (single-use here).
            #define SUN_GLINT_TINT          float3(10.0, 8.0, 6.0)
            #define SUN_GLINT_SHARPNESS     5000.0
            #define UNDERWATER_REFRACT_TINT float3(0.8, 1.0, 1.1)
            #define FRESNEL_POWER           3.0
            #define FRESNEL_MIN_ABOVE       0.25
            #define FRESNEL_MIN_BELOW       0.5

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
            #define FOAM_CORE_START     0.55
            #define FOAM_CORE_FULL      0.95
            #define FOAM_LACE_SOFTNESS  0.25
            #define FOAM_CORE_WHITEN    0.7
            // Pattern-erosion band for the core cut: wider than the lace band so the
            // core rim breaks into chunkier pieces than the thin filaments.
            #define FOAM_CORE_CUT_SOFTNESS 0.35
            // Foam lighting: wrapped diffuse keeps the unlit side from going black
            // (foam scatters light), plus a flat ambient floor from the sky.
            #define FOAM_LIGHT_WRAP     0.4
            #define FOAM_AMBIENT        0.35
            // Seen from BELOW, dense foam blocks the sky transmitted through the surface,
            // while thin lace scatters a faint sunlit glow through.
            #define FOAM_UNDERSIDE_DARKEN 0.6
            #define FOAM_UNDERSIDE_GLOW   0.4

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
            // Unbounded-ocean clipmap: 1 = the camera-following radial mesh authored in WORLD metres
            // (reaches the horizon), 0 = pool-grid surfaces. Inert at the default (_IsClipmap = 0).
            float  _IsClipmap;
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

            // Pool-space terrain bed height (R = bed height in pool units), baked by WaterVolume.
            sampler2D _BedTex;

            // Foam: _FoamMask (sim buffer) + globals from the controller; _FoamTex
            // is an optional per-material pattern (defaults white = flat foam).
            sampler2D _FoamMask;
            sampler2D _FoamTex;
            sampler2D _FoamNormalTex; // relief matching _FoamTex frame-for-frame (raw RGB encode)
            float4 _FoamTex_ST;
            float4 _FoamTexFrames; // (cols, rows) of the flipbook grid; (1,1) = plain texture
            float  _FoamTexFPS;
            float  _FoamNormalStrength;
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

            // Foam pattern with frame advance + crossfade: the foam churns internally
            // even where the mask is static.
            float3 SampleFoamPattern(float2 uv)
            {
                float2 cellA, cellB, grid; float blend;
                FoamFlipbookFrames(cellA, cellB, grid, blend);
                float2 tiled = frac(uv);
                float3 a = tex2D(_FoamTex, (tiled + cellA) / grid).rgb;
                float3 b = tex2D(_FoamTex, (tiled + cellB) / grid).rgb;
                return lerp(a, b, blend);
            }

            // Tangent-plane tilt (xy) of the foam relief. The map is raw-RGB encoded
            // (n * 0.5 + 0.5) and imported LINEAR - deliberately NOT a Unity "Normal map"
            // import, so the decode is identical on every backend. Default "bump" decodes
            // to zero tilt, so materials without the map are unaffected.
            float2 SampleFoamNormalTilt(float2 uv)
            {
                float2 cellA, cellB, grid; float blend;
                FoamFlipbookFrames(cellA, cellB, grid, blend);
                float2 tiled = frac(uv);
                float2 a = tex2D(_FoamNormalTex, (tiled + cellA) / grid).rg * 2.0 - 1.0;
                float2 b = tex2D(_FoamNormalTex, (tiled + cellB) / grid).rg * 2.0 - 1.0;
                return lerp(a, b, blend);
            }

            // Shared foam evaluation for BOTH sides of the surface. Pattern: tiled/flipbook
            // texture dragged along the local flow; two half-offset phases cross-faded by a
            // seesaw weight give endless drift with no visible reset. Layers: dense white
            // core where the mask is thick; as it thins the pattern's dark regions erode
            // away first, so decaying foam breaks into filaments instead of ghosting out.
            // Tilt: the relief normal, sampled at the SAME phases/frames, scaled by the
            // mask so sparse foam doesn't dent the shading.
            void EvaluateFoam(float2 fuv, float2 flowXZ, float mask,
                              out float3 pattern, out float core, out float lace,
                              out float alpha, out float2 tilt)
            {
                float2 flowDir = flowXZ * FOAM_FLOW_DISTANCE;
                float phaseA = frac(_Time.y * FOAM_FLOW_RATE);
                float phaseB = frac(phaseA + 0.5);
                float seesaw = abs(phaseA * 2.0 - 1.0);
                pattern = lerp(SampleFoamPattern(fuv - flowDir * phaseA),
                               SampleFoamPattern(fuv - flowDir * phaseB), seesaw);

                core = smoothstep(FOAM_CORE_START, FOAM_CORE_FULL, mask);
                lace = saturate((pattern.r - (1.0 - mask)) / FOAM_LACE_SOFTNESS);

                // Core cut (user-tunable): erode the dense core's alpha by the pattern -
                // same trick as the lace, wider band - so the core rim breaks into
                // texture detail instead of ending in a smooth mask blob. 0 = solid core
                // (original look). Even at full cut the lace term below keeps the
                // saturated centre near-solid; only the darkest pattern texels open up.
                float coreCut = saturate((pattern.r - (1.0 - mask)) / FOAM_CORE_CUT_SOFTNESS);
                float coreAlpha = core * lerp(1.0, coreCut, _FoamCoreCut);

                // Edge feathering (user-tunable): fade the layer out smoothly as the
                // mask thins instead of clipping at the mask epsilon. 0 = off (hard
                // edge, the original look). Core is untouched by construction: it only
                // exists above FOAM_CORE_START, well over any sensible feather band.
                float feather = (_FoamFeather > 0.0) ? smoothstep(0.0, _FoamFeather, mask) : 1.0;
                alpha = max(coreAlpha, lace * mask) * feather;

                tilt = lerp(SampleFoamNormalTilt(fuv - flowDir * phaseA),
                            SampleFoamNormalTilt(fuv - flowDir * phaseB), seesaw)
                     * (_FoamNormalStrength * mask);
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
                    float3 worldOnPlane = mul(unity_ObjectToWorld, float4(v.vertex.xyz, 1.0)).xyz;
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
                if (_LargeBody > 0.5)
                {
                    float2 sourceXZ = worldPos.xz;
                    o.largeWaveSourceXZ = sourceXZ;
                    // Height + chop. The far-field band-limit (dropping short waves the coarse mesh can't
                    // resolve, keeping the long swell) lives INSIDE these functions now, driven by
                    // camera distance - no-op for bounded bodies (_LargeWaveDetailSlope = 0).
                    worldPos.y  += LargeBodyWaveHeight(sourceXZ);
                    worldPos.xz += LargeBodyWaveDisplacement(sourceXZ); // 0 when choppiness = 0
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

            // Sample the environment (reflection probe / procedural sky) for a WORLD-space ray,
            // plus the sun glint. This is what the water REFLECTS - never the analytic pool tiles.
            float3 SampleEnvironment(float3 worldRay)
            {
                float3 color;
                if (_UseUrpProbe > 0.5)
                {
                    // The scene's active reflection probe / skybox (URP binds it to unity_SpecCube0),
                    // so the water matches the user's lit environment. Mip 0 keeps it mirror-sharp.
                    half4 encodedProbe = UNITY_SAMPLE_TEXCUBE_LOD(unity_SpecCube0, worldRay, 0);
                    color = DecodeHDR(encodedProbe, unity_SpecCube0_HDR).rgb;
                }
                else
                {
                    color = texCUBE(_Sky, worldRay).rgb;
                }
                // sun glint - direction from _LightDir, tint/brightness from the Unity sun
                color += SUN_GLINT_TINT * _SunColor * pow(max(0.0, dot(_LightDir, worldRay)), SUN_GLINT_SHARPNESS);
                return color;
            }

            // Shade a WORLD-space ray: a DOWN ray refracts into the pool and samples the analytic
            // floor/walls (the tiles seen THROUGH the water); an UP ray is a reflection and samples
            // the environment only. Reflections never return the pool tiles - the floor is seen via
            // refraction alone. The pool box is intersected in POOL space so rotation / non-uniform
            // extent is handled exactly, while the environment uses the WORLD ray.
            float3 getSurfaceRayColor(float3 worldOrigin, float3 worldRay, float3 waterColor)
            {
                if (worldRay.y < 0.0)
                {
                    // Open water has no pool floor to sample: return the deep-water inscattering
                    // colour so the analytic refraction reads as "can't see the bottom" rather than
                    // pool tiles. The _REAL_REFRACTION path (in frag) samples the actual scene where
                    // geometry exists and overrides this; this is the no-geometry fallback.
                    if (_LargeBody > 0.5)
                        return _WaterFogColor.rgb * waterColor;

                    float3 po = WorldToPool(worldOrigin);
                    float3 pd = WorldDirToPool(worldRay);
                    float2 t = IntersectCube(po, pd, POOL_BOX_MIN, POOL_BOX_MAX);
                    return GetWallColor(po + pd * t.y) * waterColor;
                }
                return SampleEnvironment(worldRay);
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
                // Open water: PoolNormalToWorld divides normal.xz by the (large) footprint extent,
                // flattening the surface so screen-space refraction collapses on big bodies. Add a
                // WORLD-space wave slope here (after that division) so open water keeps real normals
                // and refraction holds at any size. No-op for pool/small bodies (_LargeBody = 0).
                if (_LargeBody > 0.5)
                    normal = ApplyLargeBodyWaveNormal(normal, i.largeWaveSourceXZ, _WaveNormalStrength);
                float3 incomingRay = normalize(i.worldPos - _WorldSpaceCameraPos);

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

                    refractedColor = ApplyWaterOpacity(refractedColor); // turbidity from below too

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

                        if (mask > FOAM_MASK_EPSILON)
                        {
                            float2 fuv = fcoord * _FoamTex_ST.xy + _FoamTex_ST.zw + normal.xz * FOAM_NORMAL_NUDGE;
                            float3 pattern; float core, lace, foamAlpha; float2 tilt;
                            EvaluateFoam(fuv, nxz, mask, pattern, core, lace, foamAlpha, tilt);

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
                    float fresnel = lerp(FRESNEL_MIN_ABOVE, 1.0, pow(saturate(1.0 - dot(normal, -incomingRay)), FRESNEL_POWER));

                    float3 reflectedColor = getSurfaceRayColor(i.worldPos, reflectedRay, ABOVEWATER_COLOR);
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
                        refractedColor = ApplyWaterFog(refractedColor, max(0.0, sceneEyeR - surfEyeR));
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
                        refractedColor = ApplyWaterFog(refractedColor, length(exitWorld - i.worldPos));
                    }

                    refractedColor = ApplyWaterOpacity(refractedColor); // art-directed turbidity floor

                    // ---- Ocean FFT whitecap foam: coverage sampled per pixel from the cascade (.w), on the
                    // same crests as the normal tilt, then broken into moving lace by the foam flipbook -
                    // the coverage is a black-point threshold that dissolves the pattern in (Crest's
                    // WhiteFoamTexture). Whitecaps are matte, so the resulting alpha knocks the specular
                    // reflection down before compositing (this surface expresses gloss as the reflection
                    // term). Ocean-only; the analytic/pool path leaves this at 0. ----
                    float oceanFoam = 0.0;                       // textured coverage: drives matte + blend
                    float3 oceanFoamPattern = float3(1.0, 1.0, 1.0);
                    if (_OceanFftActive > 0.5)
                    {
                        float coverage = OceanFftFoam(i.largeWaveSourceXZ);
                        if (coverage > FOAM_MASK_EPSILON)
                        {
                            // Stock white _FoamTex -> pattern ~= 1 -> solid coverage (no regression); a real
                            // foam texture dissolves in as lace. frac() inside SampleFoamPattern does the tile.
                            oceanFoamPattern = SampleFoamPattern(i.largeWaveSourceXZ / max(_OceanFoamTileSize, 1e-3));
                            float threshold = 1.0 - coverage;
                            oceanFoam = smoothstep(threshold, threshold + max(_OceanFoamFeather, 1e-3), oceanFoamPattern.r);
                        }
                    }
                    float3 outColor = lerp(refractedColor, reflectedColor,
                                           fresnel * _ReflectionStrength * (1.0 - oceanFoam));

                    // ---- Shoreline gradient from the real terrain depth (baked bed map).
                    // Tint toward the deep-water colour by the water-column depth, so the surface
                    // reads clear over shallows and dark over the drop-off. No-op until a bed is
                    // baked and the toggle is on. ----
                    if (_UseBedDepth > 0.5 && _BedValid > 0.5)
                    {
                        float2 bedUV = i.position.xz * 0.5 + 0.5;
                        float bedPoolY = tex2Dlod(_BedTex, float4(bedUV, 0, 0)).r;
                        float colDepth = BedColumnDepthWorld(bedPoolY, i.position.y, VolumeExtentSafe().y);
                        float shore = 1.0 - exp(-_ShorelineDepthScale * colDepth);
                        outColor = lerp(outColor, _DeepWaterColor.rgb, saturate(shore * _ShorelineStrength));
                    }

                    // ---- Ocean whitecap blend: lay the lit foam colour over the water by the sampled
                    // coverage. Lit with the same wrapped-sun + ambient model as the pond foam so crests
                    // shade with the waves instead of reading as flat paint. Separate from the pond
                    // _FoamMask path below and gated on the FFT ocean, so pools stay unchanged. ----
                    if (oceanFoam > FOAM_MASK_EPSILON)
                    {
                        // ---- Foam relief: emboss the lighting normal by the foam normal map (same flipbook,
                        // frame-synced to the pattern) so the lace shades three-dimensionally and its specular
                        // breakup matches the texture. Built as a LOCAL normal - the base wave normal that the
                        // pond foam / haze below rely on is left untouched. Default "bump" map = zero tilt. ----
                        float2 oceanFoamTilt = SampleFoamNormalTilt(i.largeWaveSourceXZ / max(_OceanFoamTileSize, 1e-3))
                                             * (_FoamNormalStrength * oceanFoam);
                        float3 oceanFoamTangent = normalize(cross(normal, float3(0.0, 0.0, 1.0)));
                        float3 oceanFoamBitangent = cross(normal, oceanFoamTangent);
                        float3 oceanFoamNormal = normalize(normal + oceanFoamTangent * oceanFoamTilt.x
                                                                  + oceanFoamBitangent * oceanFoamTilt.y);

                        // Modulate the tint by the pattern so the foam carries internal light/dark detail
                        // instead of reading as a flat wash; whiten toward the peaks so dense foam stays bright.
                        float oceanWrap = saturate(dot(oceanFoamNormal, _LightDir) * (1.0 - FOAM_LIGHT_WRAP) + FOAM_LIGHT_WRAP);
                        float3 oceanTint = _OceanFoamColor.rgb * lerp(oceanFoamPattern, float3(1.0, 1.0, 1.0), oceanFoam);
                        float3 oceanFoamLook = oceanTint * (FOAM_AMBIENT + _SunColor * oceanWrap);
                        outColor = lerp(outColor, oceanFoamLook, oceanFoam * _OceanFoamColor.a);
                    }

                    // ---- Foam: advected buffer + shoreline border + waterline contact ----
                    if (_FoamEnabled > 0.5)
                    {
                        // Windowed bodies read the foam buffer in the window frame too.
                        float2 fcoord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                                             : (WorldToSim(i.worldPos).xz * 0.5 + 0.5);
                        float advected = SampleFoamMaskBilinear(fcoord);

                        // shoreline foam against the pool walls (whole-body only; a window has no walls)
                        float edge = min(1.0 - abs(i.position.x), 1.0 - abs(i.position.z));
                        float border = (_SimWindowed < 0.5) ? (1.0 - smoothstep(0.0, _FoamBorderWidth, edge)) : 0.0;

                        // contact foam where geometry pierces the waterline. Needs the
                        // depth texture; when it's unavailable (or uses a different Z
                        // convention in a build) the sample can resolve in FRONT of the
                        // surface, which the old formula turned into full-surface foam.
                        // Guard: only add contact foam where the scene is genuinely just
                        // BEHIND the surface, else none. Fixes "all water foamed" builds.
                        float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                        float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                        float surfEye  = -mul(UNITY_MATRIX_V, float4(i.worldPos, 1.0)).z;
                        float behind   = sceneEye - surfEye; // > 0 when scene sits below the surface
                        float contact  = behind > 0.0 ? (1.0 - saturate(behind / max(_FoamContactDepth, 1e-4))) : 0.0;

                        float mask = saturate((advected + border + contact) * _FoamStrength);

                        if (mask > FOAM_MASK_EPSILON)
                        {
                            float2 fuv = fcoord * _FoamTex_ST.xy + _FoamTex_ST.zw + normal.xz * FOAM_NORMAL_NUDGE;
                            float3 pattern; float core, lace, foamAlpha; float2 tilt;
                            EvaluateFoam(fuv, nxz, mask, pattern, core, lace, foamAlpha, tilt);

                            // ---- Foam relief: tilt the lighting normal by the foam's own
                            // normal map so the lace shades three-dimensionally. ----
                            float3 foamTangent = normalize(cross(normal, float3(0.0, 0.0, 1.0)));
                            float3 foamBitangent = cross(normal, foamTangent);
                            float3 foamNormal = normalize(normal + foamTangent * tilt.x + foamBitangent * tilt.y);

                            // ---- Lit foam: wrapped diffuse from the sun over an ambient
                            // floor, so foam shades with the waves instead of flat white. ----
                            float wrapped = saturate(dot(foamNormal, _LightDir) * (1.0 - FOAM_LIGHT_WRAP) + FOAM_LIGHT_WRAP);
                            float3 albedo = _FoamColor.rgb * lerp(pattern, float3(1.0, 1.0, 1.0), core * FOAM_CORE_WHITEN);
                            float3 foamLook = albedo * (FOAM_AMBIENT + _SunColor * wrapped);

                            outColor = lerp(outColor, foamLook, foamAlpha);
                        }
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

                    return float4(outColor, 1.0);
                }
            }
            ENDCG
        }
    }
}
