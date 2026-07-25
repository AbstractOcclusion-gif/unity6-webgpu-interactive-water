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
        // Underside (seen-from-below) look - driven by the WaterVolume "Underwater Surface"
        // block, same [HideInInspector] convention as the reflection family above. Physical
        // fresnel = the Snell window (clear overhead, true TIR mirror past ~48.6 deg); the
        // legacy curve's 0.5 floor mirrored half the environment even straight up.
        [HideInInspector] _UnderFresnelPhysical ("Underside Physical Fresnel (0/1)", Float) = 1
        [HideInInspector] _UnderTirSoftness ("Underside TIR Edge Softness", Range(0,0.5)) = 0.08
        [HideInInspector] _UnderFresnelFloor ("Underside Fresnel Floor", Range(0,1)) = 0.0
        [HideInInspector] _UnderReflectionStrength ("Underside Reflection Strength", Range(0,1)) = 1.0
        [HideInInspector] _UnderMirrorWaterBlend ("Underside Mirror Water Blend", Range(0,1)) = 0.5
        [HideInInspector] _FoamUndersideDarken ("Underside Foam Silhouette Darken", Range(0,1)) = 0.6
        [HideInInspector] _FoamUndersideGlow ("Underside Foam Sun Glow", Range(0,1)) = 0.4
        [HideInInspector] _UnderDetailNormalStrength ("Underside Detail Normal Strength", Range(0,2)) = 0.0

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
        [HideInInspector] _OceanWhitecapTex ("Ocean Whitecap (single tile or flipbook)", 2D) = "white" {}
        [HideInInspector] _OceanWhitecapFrames ("Whitecap Flipbook Grid (cols, rows)", Vector) = (1, 1, 0, 0)
        [HideInInspector] _OceanWhitecapFPS ("Whitecap Flipbook Frame Rate", Range(0, 30)) = 10
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

            // ---- Vertex stage (SHADER-SPLIT-4, verbatim move): the pass-local uniforms,
            // SampleRipple, the v2f contract and vert() live in WaterSurfaceVertStage.hlsl,
            // shared with the ocean-surface eye-depth prepass (Pass 1 below) so that pass
            // displaces EXACTLY like this visible one. ----
            #include "WaterSurfaceVertStage.hlsl"

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

        // ---- Pass 1: ocean-surface eye-depth prepass (the KWS-style rendered waterline). ----
        // Re-draws the SAME displaced surface (the shared WaterSurfaceVertStage.hlsl guarantees
        // wave-exact agreement with Pass 0) into _OceanSurfaceEyeDepth, so the underwater fog
        // takes its per-pixel waterline from the RENDERED surface instead of a bounded analytic
        // march - the march fell back to the flat rest plane past its ~60 m step cap, which
        // mismatched the far waves and read as sorting errors along the distant waterline.
        // Drawn EXPLICITLY by WaterUnderwaterFogPass with each surface renderer's own mesh,
        // matrix, material and property block - never by the camera (no LightMode tag, and the
        // camera only ever renders Pass 0 of a surface material). Cull Off: both the above and
        // under sheets contribute; the depth test keeps the nearest crossing.
        Pass
        {
            Name "OceanSurfaceEyeDepth"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragDepth
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterExclusion.hlsl"
            #include "WaterLargeWaves.hlsl"
            #include "WaterFoamCommon.hlsl"
            // Same helper chain as Pass 0 (include guards make this cheap): vert reads the foam
            // mask sampler and the shore/surf field helpers through these.
            #include "WaterSurfaceScreen.hlsl"
            #include "WaterSurfaceShadow.hlsl"
            #include "WaterSurfaceSpecular.hlsl"
            #include "WaterSurfacePoolTrace.hlsl"
            #include "WaterSurfaceFoamSampling.hlsl"
            #include "WaterSurfaceDetailNormal.hlsl"
            #include "WaterSurfaceVertStage.hlsl"

            float4 fragDepth(v2f i) : SV_Target
            {
                // Dry-interior exclusion: no surface there, so no waterline either (matches the
                // visible pass's discard).
                if (InsideExclusion(i.worldPos)) discard;
                // Linear EYE depth of the displaced surface; the RT clears to 0 = "no surface".
                return float4(LinearEyeDepth(i.pos.z), 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
