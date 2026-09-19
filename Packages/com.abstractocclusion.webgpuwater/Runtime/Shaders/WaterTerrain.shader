// WebGpuWater - dedicated lit TERRAIN shader: four procedural substrates that know where the water is.
//
// WHY A SEPARATE SHADER AND NOT WaterReceiver: a Unity Terrain is not a Renderer, so the receiver
// converter cannot reach it (WaterReceiverConverter iterates GetComponentsInChildren<Renderer>()).
// Until now the only water shading terrain could get was the fullscreen caustic projection - which is
// also the path that leaves URP's own un-refracted shadow underneath, because that pass cannot
// intercept the lighting of a shader the package does not own. This shader owns it, so that doubled
// shadow simply does not arise here.
//
// SUBSTRATES ARE PROCEDURAL, from height above the waterline and slope - not a painted splatmap. The
// beach therefore lands wherever the WATER is, with nothing to keep in sync by hand, and an unpainted
// terrain (which is what the demo terrains are - zero TerrainLayers between them) already reads as a
// coastline. WaterTerrainSubstrate.hlsl owns that selection; this file owns what each one looks like.
//
// SAMPLER BUDGET - the thing that decides this shader's shape. Eight substrate maps (4 albedo +
// 4 normal) would be eight of the sixteen d3d11 units before URP's own shadow samplers are counted.
// They all go through the shared inline sampler_TrilinearRepeat instead (see the project's
// shared-sampler rule), which URP core declares - declaring it here again is a redefinition error.
// That collapses eight units into one and leaves the pass with real headroom.
//
// TEXTURE BUDGET (WebGPU): 16 sampled textures per stage is the default WebGPU limit, and this pass
// already binds 14 (8 substrate maps, caustic, sim, foam, two shore fields, the main shadow map). The
// ground-marks field makes 15. That is why MUD and SNOW below own NO maps of their own: mud is the
// beach maps re-tinted, smoothed and flattened; snow is the beach maps at their own tiling under a
// white tint with the normal flattened hard. Giving either its own albedo+normal would cross the limit
// and silently break WebGPU builds only.
//
// GROUND MARKS (footprints, keel grooves): read from WaterGroundMarks.hlsl, applied FRAGMENT-ONLY -
// a one-step parallax offset on the substrate UVs, a normal from the mark's gradient, darkening in the
// pit and a wet/crumbled rim while the print is fresh. Silhouettes stay flat on purpose: WebGPU has no
// tessellation stage, and vertex displacement would have to be repeated in the shadow/depth passes.
Shader "AbstractOcclusion/WebGpuWater/WaterTerrain"
{
    Properties
    {
        [Header(Substrate blending)]
        _SeabedTop ("Seabed Top (m rel. waterline)", Range(-8,0)) = -0.15
        _BeachTop ("Beach Top (m above waterline)", Range(0,20)) = 1.5
        _HeightFeather ("Height Feather (m)", Range(0.01,5)) = 0.35
        _RockSlope ("Rock Slope Threshold", Range(0,1)) = 0.45
        _SlopeFeather ("Rock Slope Feather", Range(0.01,0.5)) = 0.12
        _TriplanarSharpness ("Rock Triplanar Sharpness", Range(1,16)) = 4
        _NormalScale ("Normal Strength", Range(0,2)) = 1

        [Header(Seabed)]
        _SeabedMap ("Seabed Albedo", 2D) = "white" {}
        [Normal] _SeabedNormal ("Seabed Normal", 2D) = "bump" {}
        _SeabedTint ("Seabed Tint", Color) = (0.34, 0.33, 0.28, 1)
        _SeabedTiling ("Seabed Tiles / metre", Range(0.01,4)) = 0.35
        _SeabedSmoothness ("Seabed Smoothness", Range(0,1)) = 0.30
        _SeabedWetDarken ("Seabed Wet Darkening", Range(0,1)) = 0.55
        _SeabedWetSmoothness ("Seabed Wet Smoothness", Range(0,1)) = 0.60

        [Header(Beach)]
        _BeachMap ("Beach Albedo", 2D) = "white" {}
        [Normal] _BeachNormal ("Beach Normal", 2D) = "bump" {}
        _BeachTint ("Beach Tint", Color) = (0.80, 0.72, 0.55, 1)
        _BeachTiling ("Beach Tiles / metre", Range(0.01,4)) = 0.5
        _BeachSmoothness ("Beach Smoothness", Range(0,1)) = 0.12
        // Sand is the most porous thing on a coastline: it darkens hardest and glosses hardest.
        _BeachWetDarken ("Beach Wet Darkening", Range(0,1)) = 0.85
        _BeachWetSmoothness ("Beach Wet Smoothness", Range(0,1)) = 0.85

        [Header(Rock)]
        _RockMap ("Rock Albedo", 2D) = "white" {}
        [Normal] _RockNormal ("Rock Normal", 2D) = "bump" {}
        _RockTint ("Rock Tint", Color) = (0.42, 0.40, 0.38, 1)
        _RockTiling ("Rock Tiles / metre", Range(0.01,4)) = 0.25
        _RockSmoothness ("Rock Smoothness", Range(0,1)) = 0.25
        _RockWetDarken ("Rock Wet Darkening", Range(0,1)) = 0.65
        _RockWetSmoothness ("Rock Wet Smoothness", Range(0,1)) = 0.80

        [Header(Grass)]
        _GrassMap ("Grass Albedo", 2D) = "white" {}
        [Normal] _GrassNormal ("Grass Normal", 2D) = "bump" {}
        _GrassTint ("Grass Tint", Color) = (0.28, 0.38, 0.18, 1)
        _GrassTiling ("Grass Tiles / metre", Range(0.01,4)) = 0.6
        _GrassSmoothness ("Grass Smoothness", Range(0,1)) = 0.15
        // Grass barely darkens - the water sits on the blades, it does not soak into them.
        _GrassWetDarken ("Grass Wet Darkening", Range(0,1)) = 0.25
        _GrassWetSmoothness ("Grass Wet Smoothness", Range(0,1)) = 0.45

        [Header(Mud (beach variant, no extra maps))]
        // Mud replaces the beach where the shore is FLAT and LOW: the same maps, re-tinted darker,
        // glossier, with the normal flattened - the look of saturated silt, not a separate splat.
        _MudAmount ("Mud Amount", Range(0,1)) = 0
        _MudMaxHeight ("Mud Max Height Above Waterline (m)", Range(0,5)) = 0.4
        _MudMaxSlope ("Mud Max Slope", Range(0.01,1)) = 0.15
        _MudTint ("Mud Tint", Color) = (0.30, 0.25, 0.19, 1)
        _MudTiling ("Mud Tiles / metre", Range(0.01,4)) = 0.4
        _MudSmoothness ("Mud Smoothness", Range(0,1)) = 0.45
        _MudNormalFlatten ("Mud Normal Flatten", Range(0,1)) = 0.5
        _MudWetDarken ("Mud Wet Darkening", Range(0,1)) = 0.5
        _MudWetSmoothness ("Mud Wet Smoothness", Range(0,1)) = 0.9

        [Header(Snow (global overlay, no extra maps))]
        // Weather-driven cover over every substrate, gated by height above the waterline (the swash
        // zone stays bare) and slope (steep rock sheds it). Reuses the beach maps - see the header.
        _SnowAmount ("Snow Amount", Range(0,1)) = 0
        _SnowMinHeight ("Snow Min Height Above Waterline (m)", Range(0,5)) = 0.3
        _SnowHeightFeather ("Snow Height Feather (m)", Range(0.01,3)) = 0.3
        _SnowMaxSlope ("Snow Max Slope", Range(0.05,1)) = 0.6
        _SnowSlopeFeather ("Snow Slope Feather", Range(0.01,0.5)) = 0.15
        _SnowTint ("Snow Tint", Color) = (0.92, 0.94, 0.98, 1)
        _SnowTiling ("Snow Tiles / metre", Range(0.01,4)) = 0.8
        _SnowTextureInfluence ("Snow Texture Influence", Range(0,1)) = 0.15
        _SnowSmoothness ("Snow Smoothness", Range(0,1)) = 0.25
        _SnowNormalFlatten ("Snow Normal Flatten", Range(0,1)) = 0.8

        [Header(Ground marks (footprints and keel grooves))]
        // Depth in METRES a full-strength mark presses into each ground. 0 = that ground takes none.
        // The WaterGroundMarks component must be in the scene; without it this block costs nothing.
        _MarkStrength ("Marks", Range(0,1)) = 1
        _MarkDepthSeabed ("Print Depth Seabed (m)", Range(0,0.3)) = 0.02
        _MarkDepthBeach ("Print Depth Sand (m)", Range(0,0.3)) = 0.03
        _MarkDepthMud ("Print Depth Mud (m)", Range(0,0.3)) = 0.06
        _MarkDepthGrass ("Print Depth Grass (m)", Range(0,0.3)) = 0.01
        _MarkDepthSnow ("Print Depth Snow (m)", Range(0,0.3)) = 0.12
        _MarkParallax ("Print Parallax", Range(0,2)) = 1
        _MarkNormalStrength ("Print Normal Strength", Range(0,4)) = 1.5
        _MarkDarken ("Print Darkening", Range(0,1)) = 0.35
        _MarkFreshWet ("Fresh Print Wetness", Range(0,1)) = 0.6
        _MarkSnowRim ("Snow Rim Brightening", Range(0,1)) = 0.35

        [Header(Wetness)]
        // Master, exactly as on WaterReceiver: 0 = the whole wetness block is skipped.
        _WetStrength ("Wetness", Range(0,1)) = 1
        _WetBandHeight ("Wet Band Above Waterline (m)", Range(0,3)) = 0.25
        _WetNormalFlatten ("Wet Normal Flatten", Range(0,1)) = 0.6
        _WetSwashStrength ("Wet From Beach Swash", Range(0,1)) = 1
        _WetFoamStrength ("Wet From Foam", Range(0,1)) = 0.5

        [Header(Water column)]
        _CausticStrength ("Caustic Strength", Range(0,8)) = 3
        _CausticTint ("Caustic Tint", Color) = (1,1,1,1)
        _UnderwaterTint ("Underwater Tint", Color) = (0.55, 0.85, 0.95, 1)
        // Metres of WATER COLUMN over which the tint above fades in from the waterline. 0 = the
        // legacy flat tint (full strength the instant ground is submerged - the same colour at
        // every depth, and a hard colour step at the shoreline). Exponential, so ~63% of the tint
        // is reached at this depth and ~95% at three times it.
        _UnderwaterTintFadeDepth ("Underwater Tint Fade Depth (m)", Range(0,20)) = 0
        _SpecColor ("Specular Color", Color) = (0.2, 0.2, 0.2, 1)
    }

    SubShader
    {
        // TerrainCompatible tells Unity this may be assigned to a Terrain's material template.
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" "TerrainCompatible"="True" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On ZTest LEqual Cull Back

            // Same contract as WaterReceiver: mark bit 3 so the screen-space caustic pass SKIPS these
            // pixels. This shader adds caustics itself below; without the mark they land twice.
            Stencil { Ref 8 WriteMask 8 Comp Always Pass Replace }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            // ONE 4-way set, exactly as URP's own Lit.shader declares it. URP sets these keywords
            // mutually exclusively, so two independent pragmas were compiling a 3x2 cross product in
            // which both *_SCREEN combinations are unreachable states.
            // _fragment on all of them: every consumer of these keywords lives in frag, so the
            // unscoped forms were also compiling a separate, byte-identical vertex program per combination.
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl"          // IOR_*, ProjectCausticUV, occluder PCF
            #include "WaterCausticMap.hlsl"      // ResolveCausticMap - frame-aware caustic uv/footprint
            #include "WaterFoamMask.hlsl"        // SimFoamCoverage, SampleWetMarkWindowed, _WetMarkActive
            #include "WaterShore.hlsl"           // ShoreSample / ShoreData / _ShoreWaterLevel
            #include "WaterSurfWaves.hlsl"       // EvaluateSurfSwash + _SurfBeatTime
            #include "WaterWaves.hlsl"           // WaveHeight / WindWaveSampleXZ - small wind-wave layer
            #include "WaterWetness.hlsl"         // THE wetness model, shared with WaterReceiver
            #include "WaterTerrainSubstrate.hlsl" // THE substrate model
            #include "WaterGroundMarks.hlsl"      // SampleGroundMark / GroundMarkGradient (Load-based)

            // All eight share ONE sampler unit (see the header note). sampler_TrilinearRepeat is
            // declared by URP core - re-declaring it here is a redefinition error, not a safeguard.
            TEXTURE2D(_SeabedMap); TEXTURE2D(_SeabedNormal);
            TEXTURE2D(_BeachMap);  TEXTURE2D(_BeachNormal);
            TEXTURE2D(_RockMap);   TEXTURE2D(_RockNormal);
            TEXTURE2D(_GrassMap);  TEXTURE2D(_GrassNormal);

            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
            float3 _LightDir;
            float _CausticOccluderActive;

            CBUFFER_START(UnityPerMaterial)
                float4 _SeabedTint, _BeachTint, _RockTint, _GrassTint;
                float4 _CausticTint, _UnderwaterTint, _SpecColor;
                float _UnderwaterTintFadeDepth;
                float _SeabedTop, _BeachTop, _HeightFeather, _RockSlope, _SlopeFeather;
                float _TriplanarSharpness, _NormalScale;
                float _SeabedTiling, _BeachTiling, _RockTiling, _GrassTiling;
                float _SeabedSmoothness, _BeachSmoothness, _RockSmoothness, _GrassSmoothness;
                float _SeabedWetDarken, _BeachWetDarken, _RockWetDarken, _GrassWetDarken;
                float _SeabedWetSmoothness, _BeachWetSmoothness, _RockWetSmoothness, _GrassWetSmoothness;
                float _WetStrength, _WetBandHeight, _WetNormalFlatten;
                float _WetSwashStrength, _WetFoamStrength;
                float _CausticStrength;
                float4 _MudTint, _SnowTint;
                float _MudAmount, _MudMaxHeight, _MudMaxSlope, _MudTiling, _MudSmoothness, _MudNormalFlatten;
                float _MudWetDarken, _MudWetSmoothness;
                float _SnowAmount, _SnowMinHeight, _SnowHeightFeather, _SnowMaxSlope, _SnowSlopeFeather;
                float _SnowTiling, _SnowTextureInfluence, _SnowSmoothness, _SnowNormalFlatten;
                float _MarkStrength, _MarkDepthSeabed, _MarkDepthBeach, _MarkDepthMud, _MarkDepthGrass, _MarkDepthSnow;
                float _MarkParallax, _MarkNormalStrength, _MarkDarken, _MarkFreshWet, _MarkSnowRim;
            CBUFFER_END

            // Sim height, manually filtered: WebGPU cannot hardware-filter the float32 sim texture, so
            // a filtered sample silently point-samples there and the waterline goes blocky in builds.
            float SampleTerrainWaterHeight(float2 uv)
            {
                float2 texel = _WaterTexel.xy;
                float2 st = uv * _WaterTexel.zw - 0.5;
                float2 f = frac(st);
                float2 baseUV = (floor(st) + 0.5) * texel;
                float c00 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV, 0).r;
                float c10 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(texel.x, 0.0), 0).r;
                float c01 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(0.0, texel.y), 0).r;
                float c11 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + texel, 0).r;
                return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
            }

            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct Varyings   { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                return o;
            }

            // Substrate UVs are WORLD XZ, so the correct tangent frame is world X / world Z projected
            // onto the surface. Deriving it here rather than taking a vertex tangent is not a shortcut:
            // Unity terrain meshes carry no reliable tangent stream, and a frame that did not match the
            // UV parameterisation would light every normal map in the wrong direction.
            void TerrainTangentFrame(float3 normalWS, out float3 tangentWS, out float3 bitangentWS)
            {
                tangentWS = normalize(float3(1.0, 0.0, 0.0) - normalWS * normalWS.x);
                bitangentWS = cross(normalWS, tangentWS);
            }

            float3 TangentToWorld(float3 normalTS, float3 T, float3 B, float3 N)
            {
                return normalize(normalTS.x * T + normalTS.y * B + normalTS.z * N);
            }

            // Triplanar world normal, "whiteout" blend: each projection's tangent-space normal is
            // swizzled onto its own plane and combined with the geometric normal, then the three are
            // mixed. Whiteout rather than a plain lerp because a lerp lets the dominant axis wash the
            // other two flat exactly on the 45-degree faces where all three matter.
            float3 TriplanarRockNormal(float3 posWS, float3 N, float3 blend, float tiling)
            {
                float3 nx = UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormal, sampler_TrilinearRepeat, posWS.zy * tiling), _NormalScale);
                float3 ny = UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormal, sampler_TrilinearRepeat, posWS.xz * tiling), _NormalScale);
                float3 nz = UnpackNormalScale(SAMPLE_TEXTURE2D(_RockNormal, sampler_TrilinearRepeat, posWS.xy * tiling), _NormalScale);
                nx = float3(nx.xy + N.zy, abs(nx.z) * N.x);
                ny = float3(ny.xy + N.xz, abs(ny.z) * N.y);
                nz = float3(nz.xy + N.xy, abs(nz.z) * N.z);
                return normalize(nx.zyx * blend.x + ny.xzy * blend.y + nz.xyz * blend.z);
            }

            float3 TriplanarRockAlbedo(float3 posWS, float3 blend, float tiling)
            {
                float3 ax = SAMPLE_TEXTURE2D(_RockMap, sampler_TrilinearRepeat, posWS.zy * tiling).rgb;
                float3 ay = SAMPLE_TEXTURE2D(_RockMap, sampler_TrilinearRepeat, posWS.xz * tiling).rgb;
                float3 az = SAMPLE_TEXTURE2D(_RockMap, sampler_TrilinearRepeat, posWS.xy * tiling).rgb;
                return ax * blend.x + ay * blend.y + az * blend.z;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 geometricNormal = normalize(IN.normalWS);
                float3 tangentWS, bitangentWS;
                TerrainTangentFrame(geometricNormal, tangentWS, bitangentWS);
                float2 planarUV = IN.positionWS.xz;

                // ---- Where is the water? -------------------------------------------------------
                // The shore field is world-frame and covers the terrain; the volume frame only covers
                // the body's own footprint. Terrain is mostly OUTSIDE that footprint, so the shore
                // field is the primary source and the still plane is the fallback - gating this on the
                // footprint (as WaterReceiver does) would leave every shoreline dry.
                float3 poolPos = WorldToPool(IN.positionWS);
                float insideBody = FootprintMaskPool(poolPos);
                bool shoreField = (_ShoreDepthValid > 0.5 && _ShoreBodyGate > 0.5);
                float stillY = shoreField
                    ? _ShoreWaterLevel
                    : PoolToWorld(float3(poolPos.x, 0.0, poolPos.z)).y;

                // THE SIM'S OWN FRAME. On a windowed (large) body the ripple sim covers a
                // camera-following WINDOW, not the whole body, so pool xz addresses the wrong texels
                // entirely - and because the window follows the camera, the error travels with you.
                // Every other consumer already indexes it this way (WaterSurfaceVertStage:74, the
                // chunk wall, the foam particles); these two shaders were the last reading pool xz.
                //
                // THE HEIGHT UNIT IS UNAFFECTED, which is what keeps this a three-line change:
                // SimHalfExtent.y IS VolumeExtentSafe.y (WaterVolume.Frames.cs:120-123), so a sampled
                // height still converts through the volume frame in both cases.
                float3 simPos = (_SimWindowed > 0.5) ? WorldToSim(IN.positionWS) : poolPos;
                float2 wuv = simPos.xz * 0.5 + 0.5;
                float simCovered = (max(abs(simPos.x), abs(simPos.z)) <= 1.0) ? 1.0 : 0.0;
                float simH = SampleTerrainWaterHeight(wuv) * simCovered;

                // Ripples are applied as an OFFSET from the still plane rather than as an absolute
                // height. stillY may come from the shore field (_ShoreWaterLevel) while PoolToWorld is
                // relative to the volume centre, so adding an absolute pool height to a shore-field
                // level would mix two different origins and step the waterline at the field boundary.
                // Small wind-wave layer: the SAME pool-unit WaveHeight the surface vertex adds
                // (WaterSurfaceVertStage), so the terrain waterline finally moves with small
                // waves instead of only with the ripple sim. Gated on the footprint for bounded
                // bodies (outside it there is no wind-wave surface); world-wave oceans keep it
                // everywhere, exactly like their own vertices do.
                float2 windSampleXZ = WindWaveSampleXZ(poolPos.xz, IN.positionWS.xz);
                float windGate = (_OceanWorldWaves > 0.5) ? 1.0 : insideBody;
                float windH = WaveHeight(windSampleXZ) * windGate;
                float poolStillY = PoolToWorld(float3(poolPos.x, 0.0, poolPos.z)).y;
                float rippleOffset = PoolToWorld(float3(poolPos.x, simH + windH, poolPos.z)).y - poolStillY;
                float surfaceY = stillY + rippleOffset;
                float heightAboveWater = IN.positionWS.y - stillY;

                // ---- Which substrate? ----------------------------------------------------------
                float slope01 = WaterTerrainSlope01(geometricNormal);
                float4 w = WaterTerrainSubstrateWeights(heightAboveWater, slope01,
                                                        _SeabedTop, _BeachTop, _HeightFeather,
                                                        _RockSlope, _SlopeFeather);

                // MUD is a share of the BEACH: flat and low enough to stay saturated. Taken out of
                // w.y so the five weights still sum to 1 and every blend below stays convex.
                float mud = w.y * WaterTerrainMudShare(heightAboveWater, slope01, _MudAmount,
                                                       _MudMaxHeight, _MudMaxSlope);
                w.y -= mud;

                // SNOW is an overlay on everything (applied after the substrate blend), not a
                // weight: it must be able to cover grass, sand and flat rock alike.
                float snow = WaterTerrainSnowCover(heightAboveWater, slope01, _SnowAmount,
                                                   _SnowMinHeight, _SnowHeightFeather,
                                                   _SnowMaxSlope, _SnowSlopeFeather);

                // ---- Ground marks: how deep is THIS ground pressed here? ------------------------
                // Depth in metres that a full mark reaches, per ground; snow overrides because the
                // print is in the snow, not in what lies under it.
                float printDepthM = _MarkDepthSeabed * w.x + _MarkDepthBeach * w.y + _MarkDepthMud * mud
                                  + _MarkDepthGrass * w.w;                              // rock: none
                printDepthM = lerp(printDepthM, _MarkDepthSnow, snow) * _MarkStrength;

                float3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float2 mark = float2(0.0, 0.0);      // (depth 0..1, fresh 0..1)
                float2 markGradient = float2(0.0, 0.0);
                float2 substrateUV = planarUV;       // parallax-shifted for the planar maps
                if (_GroundMarkActive > 0.5 && printDepthM > 0.0)
                {
                    float2 markUV = GroundMarkUV(planarUV);
                    mark = SampleGroundMark(markUV);
                    if (mark.x > 0.001)
                    {
                        // ONE-STEP PARALLAX: a pit reads as displaced away from the eye. Tangent
                        // frame: T = world X, B = -world Z (see TerrainTangentFrame), so a
                        // tangent-space shift (du, dv) is (dx, -dz) in world XZ.
                        float3 viewTS = float3(dot(viewDirWS, tangentWS), dot(viewDirWS, bitangentWS),
                                               dot(viewDirWS, geometricNormal));
                        float pitHeight = -mark.x * printDepthM * _MarkParallax;
                        float2 shift = float2(viewTS.x, -viewTS.y) / max(viewTS.z, 0.25) * pitHeight;
                        substrateUV = planarUV + shift;
                        markUV = GroundMarkUV(substrateUV);
                        mark = SampleGroundMark(markUV);
                        markGradient = GroundMarkGradient(markUV);
                    }
                }

                float3 triBlend = WaterTerrainTriplanarWeights(geometricNormal, _TriplanarSharpness);

                float3 seabedA = _SeabedTint.rgb * SAMPLE_TEXTURE2D(_SeabedMap, sampler_TrilinearRepeat, substrateUV * _SeabedTiling).rgb;
                float3 beachA  = _BeachTint.rgb  * SAMPLE_TEXTURE2D(_BeachMap,  sampler_TrilinearRepeat, substrateUV * _BeachTiling).rgb;
                float3 grassA  = _GrassTint.rgb  * SAMPLE_TEXTURE2D(_GrassMap,  sampler_TrilinearRepeat, substrateUV * _GrassTiling).rgb;
                float3 rockA   = _RockTint.rgb   * TriplanarRockAlbedo(IN.positionWS, triBlend, _RockTiling);
                // Mud and snow: the beach maps again at their own tiling (texture budget, see header).
                float3 mudA    = _MudTint.rgb    * SAMPLE_TEXTURE2D(_BeachMap,  sampler_TrilinearRepeat, substrateUV * _MudTiling).rgb;
                float3 snowTex = SAMPLE_TEXTURE2D(_BeachMap, sampler_TrilinearRepeat, substrateUV * _SnowTiling).rgb;
                float snowLum  = dot(snowTex, float3(0.2126, 0.7152, 0.0722));
                float3 snowA   = _SnowTint.rgb * lerp(1.0, snowLum * 2.0, _SnowTextureInfluence);

                float3 albedo = seabedA * w.x + beachA * w.y + rockA * w.z + grassA * w.w + mudA * mud;

                float3 seabedN = TangentToWorld(UnpackNormalScale(SAMPLE_TEXTURE2D(_SeabedNormal, sampler_TrilinearRepeat, substrateUV * _SeabedTiling), _NormalScale), tangentWS, bitangentWS, geometricNormal);
                float3 beachN  = TangentToWorld(UnpackNormalScale(SAMPLE_TEXTURE2D(_BeachNormal,  sampler_TrilinearRepeat, substrateUV * _BeachTiling),  _NormalScale), tangentWS, bitangentWS, geometricNormal);
                float3 grassN  = TangentToWorld(UnpackNormalScale(SAMPLE_TEXTURE2D(_GrassNormal,  sampler_TrilinearRepeat, substrateUV * _GrassTiling),  _NormalScale), tangentWS, bitangentWS, geometricNormal);
                float3 rockN   = TriplanarRockNormal(IN.positionWS, geometricNormal, triBlend, _RockTiling);
                float3 mudN    = TangentToWorld(UnpackNormalScale(SAMPLE_TEXTURE2D(_BeachNormal, sampler_TrilinearRepeat, substrateUV * _MudTiling), _NormalScale * (1.0 - _MudNormalFlatten)), tangentWS, bitangentWS, geometricNormal);
                float3 snowN   = TangentToWorld(UnpackNormalScale(SAMPLE_TEXTURE2D(_BeachNormal, sampler_TrilinearRepeat, substrateUV * _SnowTiling), _NormalScale * (1.0 - _SnowNormalFlatten)), tangentWS, bitangentWS, geometricNormal);

                // Blend the world-space normals and renormalise: the weights sum to 1, so this is a
                // convex combination and cannot invert the normal however the substrates overlap.
                float3 N = normalize(seabedN * w.x + beachN * w.y + rockN * w.z + grassN * w.w + mudN * mud);

                float smoothness = _SeabedSmoothness * w.x + _BeachSmoothness * w.y
                                 + _RockSmoothness * w.z + _GrassSmoothness * w.w + _MudSmoothness * mud;

                // ---- Snow overlay ----------------------------------------------------------------
                albedo = lerp(albedo, snowA, snow);
                N = normalize(lerp(N, snowN, snow));
                smoothness = lerp(smoothness, _SnowSmoothness, snow);

                // ---- Ground marks: the look -----------------------------------------------------
                // Pit normal from the gradient: the surface is h = -D * m, so its normal is
                // (D dm/dx, 1, D dm/dz) in world XZ - T carries X, -B carries Z.
                float markPit = mark.x * _MarkDarken;
                if (mark.x > 0.001)
                {
                    float2 g = markGradient * printDepthM * _MarkNormalStrength;
                    N = normalize(N + tangentWS * g.x - bitangentWS * g.y);
                    // Rim: the soft edge of the print, where fresh (the unscaled stamp mask) is
                    // between 0 and 1. Peaks at 0.5, zero in the core and outside.
                    float rim = 4.0 * mark.y * (1.0 - mark.y);
                    // Snow crumbles bright at the rim; bare ground just darkens in the pit.
                    albedo *= 1.0 - markPit * (1.0 - snow);
                    albedo *= 1.0 + rim * _MarkSnowRim * snow;
                }

                // ---- How wet? ------------------------------------------------------------------
                // Same three sources and the same shared model as WaterReceiver, so the ground and any
                // prop standing on it cannot dry at different rates or with different contours.
                float wet = 0.0;
                if (_WetStrength > 0.0)
                {
                    // Gated on the SIM's coverage, not the body footprint: terrain is mostly outside
                    // the footprint, and gating there is exactly what left inland ground with no
                    // drying memory at all. markH = 0 makes the offset 0, so ground beyond the sim
                    // falls back to the still plane with no branch.
                    float markH = (_WetMarkActive > 0.5 && simCovered > 0.5) ? SampleWetMarkWindowed(wuv) : 0.0;
                    float wetFloorY = stillY
                        + (PoolToWorld(float3(poolPos.x, markH, poolPos.z)).y - poolStillY);
                    float bandWet = WaterWetBand(IN.positionWS.y, surfaceY, wetFloorY, _WetBandHeight);

                    ShoreData shore = ShoreSample(planarUV);
                    float2 swash = (_SurfActive > 0.5)
                        ? EvaluateSurfSwash(planarUV, shore.toShore, shore.slopeTan,
                                            shore.influence, _SurfBeatTime)
                        : float2(0.0, 0.0);
                    float swashWet = WaterWetSwash(-shore.depth, swash.y) * _WetSwashStrength;

                    float foamWet = (_FoamEnabled > 0.5 && simCovered > 0.5)
                        ? WaterWetFoam(SimFoamCoverage(poolPos.xz, wuv, 0.0)) * _WetFoamStrength
                        : 0.0;

                    wet = _WetStrength * WaterWetCombine(bandWet, swashWet, foamWet);
                }
                // A FRESH print in sand or mud is wetter than its surroundings: the boot squeezed
                // the water table up. Not in snow (it is not wet, it is crushed) and not on rock.
                wet = max(wet, _WetStrength * mark.y * _MarkFreshWet * (1.0 - snow) * (1.0 - w.z));

                WaterWetLook wetLook;
                wetLook.darken = _SeabedWetDarken * w.x + _BeachWetDarken * w.y
                               + _RockWetDarken * w.z + _GrassWetDarken * w.w + _MudWetDarken * mud;
                wetLook.smoothness = _SeabedWetSmoothness * w.x + _BeachWetSmoothness * w.y
                                   + _RockWetSmoothness * w.z + _GrassWetSmoothness * w.w + _MudWetSmoothness * mud;
                // Snow barely darkens when wet and never glosses like silt.
                wetLook.darken = lerp(wetLook.darken, 0.15, snow);
                wetLook.smoothness = lerp(wetLook.smoothness, _SnowSmoothness + 0.1, snow);
                wetLook.normalFlatten = _WetNormalFlatten;
                WaterWetSurface wetSurface = WaterApplyWetness(wetLook, wet, albedo, smoothness,
                                                               N, geometricNormal);
                albedo = wetSurface.albedo;
                N = wetSurface.normal;
                smoothness = wetSurface.smoothness;

                // ---- Lighting ------------------------------------------------------------------
                // Against the SAME rippled surfaceY every depth term below uses (sim ripples + wakes
                // + the small wind-wave layer, on the shore-field level). This used to test
                // poolPos.y < simH: no wind waves at all, and a pool-frame height against a surface
                // that may sit on _ShoreWaterLevel - so the edge where caustics, the underwater tint
                // and the refracted shadow switch on stood still while the waterline drawn by the
                // sheet moved over it. WaterReceiver never showed it because it folds the wind wave
                // into simH before its own test.
                bool underwater = (insideBody > 0.5 && IN.positionWS.y < surfaceY);

                // FRAME-AWARE caustic map. A Unity Terrain under an OCEAN belongs to a body whose
                // caustic RT is written in the sim WINDOW's frame, not the pool box: reading it
                // through ProjectCausticUV stretched a ~40 m pattern across the whole footprint and
                // made it churn as the window followed the camera. On a POOL body the resolver
                // returns the identical uv, gradScale 1 and footprint 1 wherever 'underwater' can be
                // true (it requires insideBody > 0.5, i.e. FootprintMaskPool == 1) - so the pool look
                // is unchanged by construction.
                WaterCausticMap causticMap = ResolveCausticMap(IN.positionWS, poolPos, _LightDir);
                float2 cuv = causticMap.uv;
                float4 causticSample = SAMPLE_TEXTURE2D_GRAD(_CausticTex, sampler_CausticTex, cuv,
                                                             ddx(cuv) * causticMap.gradScale,
                                                             ddy(cuv) * causticMap.gradScale);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndl = saturate(dot(N, mainLight.direction));
                float3 ambient = SampleSH(N);

                // Submerged ground takes the REFRACTED occluder shadow, like the pool floor and the
                // receiver - not URP's un-refracted shadow map, which lands offset at depth.
                float lightShadow = mainLight.shadowAttenuation;
                if (underwater && _CausticOccluderActive > 0.5)
                {
                    float occRadius = OccluderPenumbraRadiusUV(poolPos.y);
                    float4 occGreens = float4(
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP0 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP1 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP2 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP3 * occRadius, 0).g);
                    lightShadow = OccluderLitFromGreenPCF(poolPos.y, causticSample.g, occGreens);
                }

                float3 color = albedo * (ambient + mainLight.color * (ndl * lightShadow));

                float3 halfDirWS = normalize(mainLight.direction + viewDirWS);
                float dryExponent = WaterSpecularExponent(
                    lerp(_SeabedSmoothness * w.x + _BeachSmoothness * w.y + _RockSmoothness * w.z
                         + _GrassSmoothness * w.w + _MudSmoothness * mud, _SnowSmoothness, snow));
                float specExponent = WaterSpecularExponent(smoothness);
                float specTerm = pow(saturate(dot(N, halfDirWS)), specExponent) * ndl * lightShadow;
                // Wet ground is shinier only because the narrowing lobe keeps its energy - see
                // WaterWetSpecularGain. Exactly 1.0 while dry.
                color += mainLight.color * _SpecColor.rgb
                       * (specTerm * WaterWetSpecularGain(dryExponent, specExponent));

                if (insideBody > 0.5) color *= DownwellingAttenuation(IN.positionWS.y, surfaceY);

                if (underwater)
                {
                    float causticFade = DepthFadeScalar(IN.positionWS.y, surfaceY, _CausticDepthFade);
                    color += albedo * _CausticTint.rgb
                           * (causticSample.r * _CausticStrength * causticFade * lightShadow
                              * causticMap.footprint);
                    // Tint BY WATER COLUMN (2026-09-19): the flat multiply made submerged ground the
                    // same colour at every depth and stepped it at the waterline. The column is
                    // measured against the same rippled surfaceY every other depth term here uses.
                    float tintColumn = max(0.0, surfaceY - IN.positionWS.y);
                    float tintWeight = (_UnderwaterTintFadeDepth > 1e-4)
                                     ? 1.0 - exp(-tintColumn / _UnderwaterTintFadeDepth)
                                     : 1.0; // 0 = legacy flat tint
                    color *= lerp(float3(1.0, 1.0, 1.0), _UnderwaterTint.rgb, tintWeight);
                }

                if (insideBody > 0.5)
                    color = ApplyWaterFog(color, WaterPathLength(IN.positionWS, _WorldSpaceCameraPos, surfaceY),
                                          WaterInscatterColor(normalize(_WorldSpaceCameraPos - IN.positionWS),
                                                              _LightDir, _WaterSunColor, 0.0));

                return half4(color, 1);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct V { float4 positionCS:SV_POSITION; };

            float4 GetShadowPositionHClip(A IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            V vert(A IN) { V o; o.positionCS = GetShadowPositionHClip(IN); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };

            V vert(A IN) { V o; o.positionCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // Without this the terrain vanishes from _CameraDepthTexture whenever a depth-NORMALS (SSAO)
        // prepass is active, and the volumetric god rays draw straight over the ground.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct V { float4 positionCS:SV_POSITION; float3 normalWS:TEXCOORD0; };

            V vert(A IN)
            {
                V o;
                o.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return o;
            }
            half4 frag(V IN) : SV_Target { return half4(normalize(IN.normalWS), 0.0); }
            ENDHLSL
        }
    }
}
