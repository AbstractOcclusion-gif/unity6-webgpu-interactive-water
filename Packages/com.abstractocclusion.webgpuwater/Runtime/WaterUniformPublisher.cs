// WebGpuWater - per-body shader uniform publishing.
// Extracted from WaterVolume: the single source of truth for the body's per-frame
// uniform derivations, written through a sink either into a MaterialPropertyBlock
// (this body's renderers, and WaterMembership'd objects) or into the global shader
// state (the primary body's fallback for objects without a membership) - so the
// values are derived once and the two paths can never drift.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUniformPublisher
    {
        // shader property / global ids, cached once
        static readonly int ID_Water = WaterShaderProps.WaterTex;
        static readonly int ID_WaterTexel = Shader.PropertyToID("_WaterTexel");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        // Stand-down flag for ClearBodyGlobals; 0 (the unpublished default) means business as usual.
        static readonly int ID_NoWaterBodies = Shader.PropertyToID("_NoWaterBodies");
        // "Skybox/Cubemap" material texture slot - cached like every other ID: the lookup runs on
        // the per-frame body-uniform path, where an inline string was the one uncached exception.
        static readonly int ID_SkyboxCubemapTex = Shader.PropertyToID("_Tex");
        static readonly int ID_CausticOccluderActive = Shader.PropertyToID("_CausticOccluderActive");
        static readonly int ID_CausticFrameMode = Shader.PropertyToID("_CausticFrameMode");
        static readonly int ID_OccluderShadowSoftness = Shader.PropertyToID("_OccluderShadowSoftness");
        static readonly int ID_SunShadowStrength = Shader.PropertyToID("_SunShadowStrength");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = WaterShaderProps.LightDir;
        static readonly int ID_SunColor = Shader.PropertyToID("_SunColor");
        static readonly int ID_FogColor = Shader.PropertyToID("_WaterFogColor");
        static readonly int ID_FogExt = Shader.PropertyToID("_WaterExtinction");
        static readonly int ID_UnderwaterLightScatter = Shader.PropertyToID("_UnderwaterLightScatter");
        // Compile-time variant for the scene-light scatter loops (the fps-cliff rule: an 8-light
        // loop behind a uniform branch would still size every Simple/legacy pixel's registers).
        const string KW_UnderwaterFogPointLights = "WATER_FOG_POINT_LIGHTS";

        // ---- The package's OWN scene-light list (WaterSceneLightsInscatter, WaterFog.hlsl) ----
        // Published rather than read from URP's additional-light arrays, for four verified
        // reasons: the CGPROGRAM surface pass cannot include URP's Lighting.hlsl; URP's
        // _AdditionalLightsCount global is the PER-OBJECT cap, not the visible count; its UBO
        // arrays keep STALE entries past the visible count; and GetAdditionalLight(i) maps
        // through per-object indices no fullscreen draw has. One list, one integral, and the
        // submerged fog + the from-above surface glow can never read different lights.
        const int MaxSceneLights = 8; // KEEP IN SYNC with WATER_SCENE_LIGHT_MAX (WaterFog.hlsl)
        // Light creation/destruction is rare compared with rendering. Cache the scene lookup so
        // armed fog does not allocate a Light[] every frame; the cached entries themselves are
        // still evaluated each frame, keeping transforms, intensity and enabled state current.
        const float SceneLightCacheRefreshSeconds = 0.5f;
        // Points publish this as cos(outerCone): the cone term saturates to 1 for any direction.
        const float PointLightConeSentinel = -2f;
        const float SpotConeRangeEpsilon = 1e-4f; // guards 1/(cosInner - cosOuter) on degenerate spots
        static readonly int ID_SceneLightPosRange = Shader.PropertyToID("_WaterSceneLightPosRange");
        static readonly int ID_SceneLightColorCone = Shader.PropertyToID("_WaterSceneLightColorCone");
        static readonly int ID_SceneLightSpotDir = Shader.PropertyToID("_WaterSceneLightSpotDir");
        static readonly int ID_SceneLightCount = Shader.PropertyToID("_WaterSceneLightCount");
        static readonly Vector4[] s_SceneLightPosRange = new Vector4[MaxSceneLights];
        static readonly Vector4[] s_SceneLightColorCone = new Vector4[MaxSceneLights];
        static readonly Vector4[] s_SceneLightSpotDir = new Vector4[MaxSceneLights];
        static readonly float[] s_SceneLightDistSq = new float[MaxSceneLights];
        static readonly List<Light> s_SceneLights = new List<Light>();
        static float s_SceneLightCacheRefreshAt;
        static readonly int ID_FogDensity = WaterShaderProps.WaterFogDensity;
        static readonly int ID_FogEnabled = WaterShaderProps.WaterFogEnabled;
        static readonly int ID_WaterOpacity = Shader.PropertyToID("_WaterOpacity");
        static readonly int ID_ScatterEnabled = Shader.PropertyToID("_ScatterEnabled");
        static readonly int ID_ScatterColor = Shader.PropertyToID("_ScatterColor");
        static readonly int ID_ScatterIntensity = Shader.PropertyToID("_ScatterIntensity");
        static readonly int ID_ScatterAmbient = Shader.PropertyToID("_ScatterAmbient");
        static readonly int ID_ScatterAmbientTerm = Shader.PropertyToID("_ScatterAmbientTerm");
        static readonly int ID_ScatterSunTerm = Shader.PropertyToID("_ScatterSunTerm");
        static readonly int ID_ScatterAnisotropy = Shader.PropertyToID("_ScatterAnisotropy");
        static readonly int ID_SssEnabled = Shader.PropertyToID("_SssEnabled");
        static readonly int ID_SssIntensity = Shader.PropertyToID("_SssIntensity");
        static readonly int ID_SssSunFalloff = Shader.PropertyToID("_SssSunFalloff");
        static readonly int ID_SssPinchMin = Shader.PropertyToID("_SssPinchMin");
        static readonly int ID_SssPinchMax = Shader.PropertyToID("_SssPinchMax");
        static readonly int ID_SssPinchFalloff = Shader.PropertyToID("_SssPinchFalloff");
        static readonly int ID_DepthExt = Shader.PropertyToID("_DepthExtinction");
        static readonly int ID_DepthStrength = Shader.PropertyToID("_DepthDarkenStrength");
        static readonly int ID_DepthEnabled = Shader.PropertyToID("_DepthDarkenEnabled");
        static readonly int ID_CausticDepthFade = Shader.PropertyToID("_CausticDepthFade");
        static readonly int ID_GodRayDepthFade = Shader.PropertyToID("_GodRayDepthFade");
        static readonly int ID_BedTex = WaterShaderProps.BedTex;
        static readonly int ID_BedValid = Shader.PropertyToID("_BedValid");
        static readonly int ID_UseBedDepth = WaterShaderProps.UseBedDepth;
        static readonly int ID_ShoreBodyGate = Shader.PropertyToID("_ShoreBodyGate");
        static readonly int ID_DeepWaterColor = Shader.PropertyToID("_DeepWaterColor");
        static readonly int ID_ShorelineScale = Shader.PropertyToID("_ShorelineDepthScale");
        static readonly int ID_ShorelineStrength = Shader.PropertyToID("_ShorelineStrength");
        static readonly int ID_DepthClarityRange = Shader.PropertyToID("_DepthClarityRange");
        static readonly int ID_DepthClarityStrength = Shader.PropertyToID("_DepthClarityStrength");
        static readonly int ID_FoamMask = Shader.PropertyToID("_FoamMask");
        static readonly int ID_FoamColor = Shader.PropertyToID("_FoamColor");
        static readonly int ID_FoamEnabled = Shader.PropertyToID("_FoamEnabled");
        static readonly int ID_WetMarkActive = Shader.PropertyToID("_WetMarkActive");
        static readonly int ID_WetDryTimeSeconds = Shader.PropertyToID("_WetDryTimeSeconds");
        static readonly int ID_FoamStrength = Shader.PropertyToID("_FoamStrength");
        static readonly int ID_FoamTileSize = Shader.PropertyToID("_FoamTileSize");
        // Body-owned surface texture inputs (Textures section): bound only when assigned on the body.
        static readonly int ID_FoamTex = WaterShaderProps.FoamTex;
        static readonly int ID_FoamTexFrames = WaterShaderProps.FoamTexFrames;
        static readonly int ID_FoamTexFPS = Shader.PropertyToID("_FoamTexFPS");
        static readonly int ID_FoamNormalStrength = Shader.PropertyToID("_FoamNormalStrength");
        static readonly int ID_OceanWhitecapTex = Shader.PropertyToID("_OceanWhitecapTex");
        static readonly int ID_OceanWhitecapFrames = Shader.PropertyToID("_OceanWhitecapFrames");
        static readonly int ID_OceanWhitecapFPS = Shader.PropertyToID("_OceanWhitecapFPS");
        static readonly int ID_FoamBorder = Shader.PropertyToID("_FoamBorderWidth");
        static readonly int ID_FoamContact = Shader.PropertyToID("_FoamContactDepth");
        static readonly int ID_FoamFeather = Shader.PropertyToID("_FoamFeather");
        static readonly int ID_FoamCoreCut = Shader.PropertyToID("_FoamCoreCut");
        static readonly int ID_WaveA = Shader.PropertyToID("_WaveA");
        static readonly int ID_WaveB = Shader.PropertyToID("_WaveB");
        static readonly int ID_WaveCount = Shader.PropertyToID("_WaveCount");
        static readonly int ID_WaveTime = Shader.PropertyToID("_WaveTime");
        static readonly int ID_WaveMeters = Shader.PropertyToID("_WaveMetersPerUnit");
        static readonly int ID_WaveNormal = Shader.PropertyToID("_WaveNormalStrength");
        // Wind-wave SHAPING (group envelopes + Stokes crest term). Precomputed by the bank, so these
        // are plain uploads - see WaterWaves.hlsl for what each lane means.
        static readonly int ID_WaveGroupA = Shader.PropertyToID("_WaveGroupA");
        static readonly int ID_WaveGroupB = Shader.PropertyToID("_WaveGroupB");
        static readonly int ID_WaveShape = Shader.PropertyToID("_WaveShape");
        static readonly int ID_WaveStokesNorm = Shader.PropertyToID("_WaveStokesNorm");
        static readonly int ID_VolumeCenter = WaterShaderProps.VolumeCenter;
        static readonly int ID_VolumeExtent = WaterShaderProps.VolumeExtent;
        static readonly int ID_VolumeRot = WaterShaderProps.VolumeRot;
        static readonly int ID_GodRaySteps = Shader.PropertyToID("_GodRaySteps");
        static readonly int ID_SimWindowed = Shader.PropertyToID("_SimWindowed");
        static readonly int ID_SimCenter = WaterShaderProps.SimCenter;
        static readonly int ID_SimExtent = WaterShaderProps.SimExtent;
        static readonly int ID_SimEdgeFade = Shader.PropertyToID("_SimEdgeFadeTexels");
        static readonly int ID_LargeBody = Shader.PropertyToID("_LargeBody");
        static readonly int ID_OceanFftActive = Shader.PropertyToID("_OceanFftActive");
        static readonly int ID_OceanFoamColor = Shader.PropertyToID("_OceanFoamColor");
        static readonly int ID_OceanFoamTileSize = Shader.PropertyToID("_OceanFoamTileSize");
        static readonly int ID_OceanFoamFeather = Shader.PropertyToID("_OceanFoamFeather");
        static readonly int ID_OceanFoamStreakStretch = Shader.PropertyToID("_OceanFoamStreakStretch");
        static readonly int ID_OceanFoamTextureInfluence = Shader.PropertyToID("_OceanFoamTextureInfluence");
        static readonly int ID_OceanFoamDepthTint = Shader.PropertyToID("_OceanFoamDepthTint");
        static readonly int ID_LbwGeomFoamFloor = Shader.PropertyToID("_LbwGeomFoamFloor");
        static readonly int ID_LargeWaveAmp = Shader.PropertyToID("_LargeWaveAmplitude");
        static readonly int ID_OffshoreSigHeight = Shader.PropertyToID("_OffshoreSignificantHeight");
        static readonly int ID_LargeWaveWind = Shader.PropertyToID("_LargeWaveWindHeading");
        static readonly int ID_LargeWaveChop = Shader.PropertyToID("_LargeWaveChoppiness");
        static readonly int ID_RippleChoppiness = Shader.PropertyToID("_RippleChoppiness");
        static readonly int ID_PoolSlopeToWorld = Shader.PropertyToID("_PoolSlopeToWorld");
        static readonly int ID_SimSlopeToWorld = Shader.PropertyToID("_SimSlopeToWorld");
        // The near-field patch's footprint, published to EVERY renderer of the body: the patch reads it
        // to place its vertices, the base sheet reads it to cut its hole.
        static readonly int ID_PatchCoverActive = Shader.PropertyToID("_PatchCoverActive");
        static readonly int ID_PatchCoverMargin = Shader.PropertyToID("_PatchCoverMargin");
        static readonly int ID_PatchCoverCenter = Shader.PropertyToID("_PatchPoolCenter");
        static readonly int ID_PatchCoverHalf = Shader.PropertyToID("_PatchPoolHalf");
        static readonly int ID_LargeWaveDetail = Shader.PropertyToID("_LargeWaveDetailSlope");
        static readonly int ID_LargeWaveEdgeFeather = Shader.PropertyToID("_LargeWaveEdgeFeather");
        static readonly int ID_OceanWorldWaves = Shader.PropertyToID("_OceanWorldWaves");
        static readonly int ID_SwellWavelength = Shader.PropertyToID("_LargeSwellWavelength");
        static readonly int ID_SwellHeight = Shader.PropertyToID("_LargeSwellHeight");
        static readonly int ID_HorizonFade = Shader.PropertyToID("_HorizonFadeDistance");
        static readonly int ID_HorizonHazeColor = Shader.PropertyToID("_HorizonHazeColor");
        static readonly int ID_HorizonHazeDensity = Shader.PropertyToID("_HorizonHazeDensity");
        static readonly int ID_LargeGodRayColor = Shader.PropertyToID("_LargeGodRayColor");
        static readonly int ID_LargeGodRayDensity = Shader.PropertyToID("_LargeGodRayDensity");
        static readonly int ID_LargeGodRaySteps = Shader.PropertyToID("_LargeGodRaySteps");
        static readonly int ID_LargeGodRayAnisotropy = Shader.PropertyToID("_LargeGodRayAnisotropy");
        static readonly int ID_LargeGodRayExtinction = Shader.PropertyToID("_LargeGodRayExtinction");
        static readonly int ID_LargeGodRayCausticStrength = Shader.PropertyToID("_LargeGodRayCausticStrength");
        static readonly int ID_LargeGodRayCausticDepthSoften = Shader.PropertyToID("_LargeGodRayCausticDepthSoften");
        static readonly int ID_LargeGodRayFromAir = Shader.PropertyToID("_LargeGodRayFromAir");
        static readonly int ID_LargeGodRayLightScatter = Shader.PropertyToID("_LargeGodRayLightScatter");
        static readonly int ID_LargeCausticProjectionLod = Shader.PropertyToID("_LargeCausticProjectionLod");
        static readonly int ID_CameraUnderwater = Shader.PropertyToID("_CameraUnderwater");
        static readonly int ID_CameraDryVolume = Shader.PropertyToID("_CameraDryVolume");
        static readonly int ID_UnderwaterSurfaceY = Shader.PropertyToID("_UnderwaterSurfaceY");
        static readonly int ID_UnderwaterUnbounded = Shader.PropertyToID("_UnderwaterUnbounded");
        static readonly int ID_UnderwaterFogSimple = Shader.PropertyToID("_UnderwaterFogSimple");
        // The SAME fact as ID_UnderwaterFogSimple, as a shader keyword. Both are set from one place
        // below so they cannot drift: the float stays because other shaders read it
        // (WaterExclusionWall, fog debug view 13), the keyword exists so the fullscreen fog's Simple
        // variant is COMPILED without the wavy-crossing machinery instead of merely branching past
        // it at runtime.
        const string KW_UnderwaterFogSimple = "WATER_FOG_SIMPLE";
        // Compiles the underside sea-foam silhouette (ocean whitecaps + surf whitewash) into
        // WaterSurface's fragment program. Same reasoning as the keyword above and NOT a uniform for
        // the same reason: the guarded code is two whitecap pattern taps, and register allocation is
        // sized to the worst path through the module whether or not the branch is taken. Armed off
        // the fog's arming flag - the broader of the two underwater facts, true whenever the eye is
        // below the surface plane even inside a dry exclusion volume - because that is exactly when
        // the underside sheet can be looked at.
        const string KW_UndersideFoam = "WATER_UNDERSIDE_FOAM";
        static readonly int ID_UnderwaterFogArmed = Shader.PropertyToID("_UnderwaterFogArmed");
        static readonly int ID_PeakedRefine = Shader.PropertyToID("_PeakedRefineSteps");
        static readonly int ID_UsePlanar = Shader.PropertyToID("_UsePlanar");
        static readonly int ID_PlanarTex = WaterShaderProps.PlanarReflectionTex;
        static readonly int ID_UseSSR = Shader.PropertyToID("_UseSSR");
        static readonly int ID_UseUrpProbe = Shader.PropertyToID("_UseUrpProbe");
        static readonly int ID_RealRefraction = WaterShaderProps.RealRefraction;
        static readonly int ID_ProceduralPool = Shader.PropertyToID("_ProceduralPool");
        static readonly int ID_ReflectionStrength = Shader.PropertyToID("_ReflectionStrength");
        static readonly int ID_EnvReflectionIntensity = Shader.PropertyToID("_EnvReflectionIntensity");
        static readonly int ID_SunReflectionIntensity = Shader.PropertyToID("_SunReflectionIntensity");
        static readonly int ID_FresnelFloor = Shader.PropertyToID("_FresnelFloor");
        static readonly int ID_FresnelPower = Shader.PropertyToID("_FresnelPower");
        static readonly int ID_SunRoughness = Shader.PropertyToID("_SunRoughness");
        static readonly int ID_RoughnessFar = Shader.PropertyToID("_RoughnessFar");
        static readonly int ID_RoughnessFarDistance = Shader.PropertyToID("_RoughnessFarDistance");
        static readonly int ID_RoughnessFalloff = Shader.PropertyToID("_RoughnessFalloff");
        static readonly int ID_ReflectionAnisoStretch = Shader.PropertyToID("_ReflectionAnisoStretch");
        static readonly int ID_SunSheen = Shader.PropertyToID("_SunSheen");
        static readonly int ID_SunSheenRoughness = Shader.PropertyToID("_SunSheenRoughness");
        static readonly int ID_SunGrazeBoost = Shader.PropertyToID("_SunGrazeBoost");
        static readonly int ID_DetailNormalTex = Shader.PropertyToID("_DetailNormalTex");
        static readonly int ID_DetailNormalStrength = Shader.PropertyToID("_DetailNormalStrength");
        static readonly int ID_DetailNormalScale = Shader.PropertyToID("_DetailNormalScale");
        static readonly int ID_DetailNormalFarScale = Shader.PropertyToID("_DetailNormalFarScale");
        static readonly int ID_DetailNormalFarDistance = Shader.PropertyToID("_DetailNormalFarDistance");
        static readonly int ID_DetailNormalFarSpeed = Shader.PropertyToID("_DetailNormalFarSpeed");
        static readonly int ID_DetailNormalHexTiling = Shader.PropertyToID("_DetailNormalHexTiling");
        static readonly int ID_DetailNormalDistanceBoost = Shader.PropertyToID("_DetailNormalDistanceBoost");
        static readonly int ID_DetailNormalSpeed = Shader.PropertyToID("_DetailNormalSpeed");
        static readonly int ID_DetailNormalCrestBoost = Shader.PropertyToID("_DetailNormalCrestBoost");
        static readonly int ID_WindDirection = Shader.PropertyToID("_WindDirection");
        static readonly int ID_UnderFresnelPhysical = Shader.PropertyToID("_UnderFresnelPhysical");
        static readonly int ID_UnderTirSoftness = Shader.PropertyToID("_UnderTirSoftness");
        static readonly int ID_UnderFresnelFloor = Shader.PropertyToID("_UnderFresnelFloor");
        static readonly int ID_UnderReflectionStrength = Shader.PropertyToID("_UnderReflectionStrength");
        static readonly int ID_UnderMirrorWaterBlend = Shader.PropertyToID("_UnderMirrorWaterBlend");
        static readonly int ID_UnderMirrorShafts = Shader.PropertyToID("_UnderMirrorShafts");
        static readonly int ID_FoamUndersideDarken = Shader.PropertyToID("_FoamUndersideDarken");
        static readonly int ID_FoamUndersideGlow = Shader.PropertyToID("_FoamUndersideGlow");
        static readonly int ID_UnderDetailNormalStrength = Shader.PropertyToID("_UnderDetailNormalStrength");
        static readonly int ID_WaterlineWidthPx = Shader.PropertyToID("_WaterlineWidthPx");
        static readonly int ID_WaterlineStrength = Shader.PropertyToID("_WaterlineStrength");
        static readonly int ID_WaterlineWarp = Shader.PropertyToID("_WaterlineWarp");
        static readonly int ID_ReflectionDistortion = Shader.PropertyToID("_ReflectionDistortion");
        static readonly int ID_SSRStrength = Shader.PropertyToID("_SSRStrength");
        static readonly int ID_SSRStepSize = Shader.PropertyToID("_SSRStepSize");
        static readonly int ID_SSRMaxSteps = Shader.PropertyToID("_SSRMaxSteps");
        static readonly int ID_SSRThickness = Shader.PropertyToID("_SSRThickness");
        static readonly int ID_RefractionDistortion = Shader.PropertyToID("_RefractionDistortion");
        static readonly int ID_RefractionStrength = Shader.PropertyToID("_RefractionStrength");
        static readonly int ID_ExclusionCount = WaterShaderProps.ExclusionCount;
        static readonly int ID_ExclusionWorldToLocal = WaterShaderProps.ExclusionWorldToLocal;
        static readonly int ID_ExclusionShape = WaterShaderProps.ExclusionShape;
        static readonly int ID_ExclusionMeshCount = Shader.PropertyToID("_ExclusionMeshCount");
        static readonly int ID_ExclusionPrepassValid = Shader.PropertyToID("_ExclusionPrepassValid");
        static readonly int ID_ExclusionEdgeColor = Shader.PropertyToID("_ExclusionEdgeColor");
        static readonly int ID_ExclusionEdgeParams = WaterShaderProps.ExclusionEdgeParams;

        // Persistent FULL-SIZE buffers for the exclusion uniforms: Unity locks a global
        // array's size at its FIRST set, so every publish sends MaxVolumes entries and
        // _ExclusionCount clamps the shader loop. Static (the volumes are global state,
        // shared by every body's publisher) and reused every frame - no allocation.
        static readonly Matrix4x4[] _exclusionMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
        static readonly Vector4[] _exclusionShapes = new Vector4[WaterExclusionVolume.MaxVolumes];
        static readonly Vector4[] _exclusionEdgeColors = new Vector4[WaterExclusionVolume.MaxVolumes];
        static readonly Vector4[] _exclusionEdgeParams = new Vector4[WaterExclusionVolume.MaxVolumes];

        readonly WaterVolume _body;
        // Two sinks over the SAME derivations; cached to avoid per-frame allocation.
        readonly MpbUniformSink _mpbSink = new MpbUniformSink();
        readonly GlobalUniformSink _globalSink = new GlobalUniformSink();

        internal WaterUniformPublisher(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
        }

        // Genuinely shared across all bodies: the sun and the environment.
        internal void PublishSharedGlobals()
        {
            // A live body is publishing, so lift any stand-down left by a previous scene's teardown.
            Shader.SetGlobalFloat(ID_NoWaterBodies, 0f);
            Shader.SetGlobalVector(ID_Light, _body.EffectiveLightDir.normalized);
            Shader.SetGlobalColor(ID_SunColor, _body.sun != null ? _body.sun.color * _body.sun.intensity : Color.white);
            // Scene ambient feeds the volume-scatter in-scatter so shaded (away-from-sun) water isn't black.
            // Genuinely shared (scene lighting, not per body), so it rides with the sun here.
            Shader.SetGlobalColor(ID_ScatterAmbient, RenderSettings.ambientLight);
            // Exclusion volumes are GLOBAL, not per body (a dry room is dry in whichever
            // body intersects it), so they ride the shared-globals path, not the sink.
            PublishExclusionVolumes();
            // NOTE: tiles (_Tiles) and reflection cubes (_Sky) are published PER BODY in
            // WriteBodyUniforms, not here - a global would be stomped by the last body each frame
            // when bodies use different pool interiors or skies.
            // The wave clock (_WaveTime) moved to WriteBodyUniforms for the same reason: a shared global
            // was last-writer-wins across bodies, so with 2+ bodies at different TimeScale (or one
            // paused) every surface animated on whichever body updated last while CPU buoyancy used its
            // own clock. Per-renderer blocks now carry each body's clock; the primary's global mirror
            // (PublishBodyGlobals) remains the fallback for camera passes and membership-less objects.
        }

        // Dry-interior exclusion volumes -> shader globals. The over-limit tie-break
        // (nearest win) is anchored on the target camera - the volumes the viewer can
        // actually see into matter most; the body centre is the camera-less fallback
        // (edit-mode previews, headless).
        void PublishExclusionVolumes()
        {
            Vector3 reference = _body.targetCamera != null
                ? _body.targetCamera.transform.position
                : _body.VolumeCenter;
            int count = WaterExclusionVolume.WriteVolumeUniforms(
                _exclusionMatrices, _exclusionShapes, _exclusionEdgeColors, _exclusionEdgeParams,
                reference);
            Shader.SetGlobalFloat(ID_ExclusionCount, count);
            // Mesh-shape volumes carve from the depth prepass, not from the analytic loop: this is
            // the gate that keeps every consumer's prepass read out of a scene that has no mesh
            // volume. Published unconditionally (unlike the arrays) because dropping to zero has to
            // REACH the shaders - a stale 1 would leave them reading last frame's depth targets.
            Shader.SetGlobalFloat(ID_ExclusionMeshCount, WaterExclusionVolume.MeshVolumeCount);
            // LOWERED here, RAISED by WaterExclusionDepthPass when it actually records. This runs
            // every frame from PublishSharedGlobals - ahead of rendering - so a renderer with no
            // WaterExclusionDepthFeature installed leaves it at 0 all frame and every consumer keeps
            // its analytic path. A flag the pass alone owned would latch at 1 and then go stale the
            // moment the feature was removed: the same failure the underwater fog avoids by
            // refreshing _OceanSurfaceDepthValid on EVERY record.
            Shader.SetGlobalFloat(ID_ExclusionPrepassValid, 0f);
            // With count 0 the shader loop never reads the arrays, so skipping the sets is
            // safe and keeps the zero-volume frame free of the array uploads.
            if (count > 0)
            {
                Shader.SetGlobalMatrixArray(ID_ExclusionWorldToLocal, _exclusionMatrices);
                Shader.SetGlobalVectorArray(ID_ExclusionShape, _exclusionShapes);
                Shader.SetGlobalVectorArray(ID_ExclusionEdgeColor, _exclusionEdgeColors);
                Shader.SetGlobalVectorArray(ID_ExclusionEdgeParams, _exclusionEdgeParams);
            }
        }

        // Reflection base cube for Reflect URP Probe: the scene skybox's cubemap if it exposes one, else
        // the body's Sky slot. NOTE: Camera.RenderToCubemap is NOT used - it forces URP to rebuild its
        // pipeline (Blitter double-init exception). A "Skybox/Cubemap" material exposes _Tex; other skybox
        // types (panoramic HDRI, 6-sided, procedural) have no directly-samplable cubemap, so assign a
        // cubemap to the water's Sky slot for those.
        Cubemap ResolveReflectionCube()
        {
            if (_body.ReflectUrpProbe)
            {
                Cubemap scene = SceneSkyboxCubemap();
                if (scene != null) return scene;
            }
            return _body.sky;
        }

        // Resolved ONCE PER FRAME. The scene skybox is scene-global and cannot change mid-frame, but
        // WriteBodyUniforms runs ~22x per frame on a default ocean (body + both patches + every clipmap
        // level x2), so this was ~66 native material queries per frame all returning the same object.
        static Cubemap _skyboxCube;
        static int _skyboxCubeFrame = -1;

        static Cubemap SceneSkyboxCubemap()
        {
            if (_skyboxCubeFrame == Time.frameCount) return _skyboxCube;
            _skyboxCubeFrame = Time.frameCount;
            Material skybox = RenderSettings.skybox;
            _skyboxCube = (skybox == null || !skybox.HasProperty(ID_SkyboxCubemapTex))
                        ? null
                        : skybox.GetTexture(ID_SkyboxCubemapTex) as Cubemap;
            return _skyboxCube;
        }

        /// <summary>Overwrite the block with the body's per-renderer uniforms.</summary>
        internal void WriteBodyProps(MaterialPropertyBlock mpb)
        {
            mpb.Clear();
            _mpbSink.Target = mpb;
            WriteBodyUniforms(_mpbSink);
        }

        // The primary body mirrors its per-body uniforms to shader globals, the fallback that
        // object shaders without a WaterMembership read. Same derivations as the property block.
        internal void PublishBodyGlobals() => WriteBodyUniforms(_globalSink);

        /// <summary>Stand the water globals down after the LAST body leaves. Shader globals survive
        /// scene loads, so without this the dead body's volume frame keeps describing a real box and a
        /// WaterReceiver floor in the NEXT scene renders wet inside its footprint. The gate is the
        /// lever: ONE flag, read by FootprintMaskPool, which every consumer already routes through.
        /// The two textures are blacked out as well because they are sampled UNGATED
        /// (WaterReceiver.shader), where a destroyed RT otherwise resolves to Unity's substitute.
        /// Static: the caller is the body on its way out, and there is nothing left to derive from.</summary>
        internal static void ClearBodyGlobals()
        {
            Shader.SetGlobalFloat(ID_NoWaterBodies, 1f);
            Shader.SetGlobalTexture(ID_Water, Texture2D.blackTexture);
            Shader.SetGlobalTexture(ID_Caustic, Texture2D.blackTexture);
        }

        // Set ONLY the wind-wave uniforms on a material the caustic pass draws with directly. That pass
        // runs BEFORE ApplyBodyBlock populates the per-body block, so the caustic material can't see the
        // per-body wave params any other way. Same sources and the same WindWaves-off gate as
        // WriteBodyUniforms, so Caustics.shader's WaveSlope matches the surface's exactly.
        internal void ApplyWaveUniforms(Material material)
        {
            if (material == null) throw new System.ArgumentNullException(nameof(material));
            material.SetFloat(ID_WaveTime, _body.WaveTime);
            material.SetVectorArray(ID_WaveA, _body.WaveBank.PackedA);
            material.SetVectorArray(ID_WaveB, _body.WaveBank.PackedB);
            material.SetFloat(ID_WaveCount, _body.WindWaves ? _body.WaveBank.Count : 0f);
            material.SetFloat(ID_WaveMeters, _body.WaveMetersPerUnit);
            material.SetFloat(ID_WaveNormal, _body.waveNormalStrength);
            // The caustic normal is built from the same two slopes the surface uses, so it needs the
            // same pool -> world conversion; without it the material reads 0 and the surface goes flat.
            material.SetVector(ID_PoolSlopeToWorld, _body.PoolSlopeToWorld);
            material.SetVector(ID_SimSlopeToWorld, _body.SimSlopeToWorld);
            material.SetVector(ID_WaveGroupA, _body.WaveBank.GroupA);
            material.SetVector(ID_WaveGroupB, _body.WaveBank.GroupB);
            material.SetVector(ID_WaveShape, _body.WaveBank.Shape);
            material.SetFloat(ID_WaveStokesNorm, _body.WaveBank.StokesNorm);
        }

        /// <summary>Camera-submerged flag + flat surface Y for the underwater fog pass. Global only
        /// (it is camera state, not a per-object uniform), so it lives outside WriteBodyUniforms.
        /// fogSimple 1 = the tier's Simple mode: the fog shader takes the closed-form flat-waterline
        /// path (against surfaceY) instead of the per-pixel wavy-surface march.
        /// fogArmed 1 = the fullscreen fog pass runs this frame (WaterVolume.UnderwaterFogActive):
        /// the exclusion wall reads it to know whether the fog will paint behind its veil or the
        /// wall must reconstruct the fog result itself (above-water ocean views).
        /// cameraDryVolume 1 = the eye sits INSIDE a dry exclusion volume. Deliberately a separate
        /// flag from cameraUnderwater rather than a special case of it: "the fog pass must run" and
        /// "the eye is in water" are different questions, and in a sunken room below sea level they
        /// have opposite answers. Every camera-height term downstream keys on this to stand down.
        /// </summary>
        internal void PublishUnderwater(float cameraUnderwater, float surfaceY, float unbounded,
                                        float fogSimple, float fogArmed, float cameraDryVolume)
        {
            Shader.SetGlobalFloat(ID_CameraUnderwater, cameraUnderwater);
            Shader.SetGlobalFloat(ID_CameraDryVolume, cameraDryVolume);
            Shader.SetGlobalFloat(ID_UnderwaterSurfaceY, surfaceY);
            Shader.SetGlobalFloat(ID_UnderwaterUnbounded, unbounded);
            Shader.SetGlobalFloat(ID_UnderwaterFogSimple, fogSimple);
            // A uniform branch skips the march at runtime, but the code is still in the module and a
            // fragment shader's register allocation is sized to its worst path - so the 40-step
            // crossing march (~6 texture fetches per step) was setting the occupancy of every
            // Simple-tier pixel on a fullscreen pass, twice a frame. The keyword removes it.
            if (fogSimple > 0.5f) Shader.EnableKeyword(KW_UnderwaterFogSimple);
            else Shader.DisableKeyword(KW_UnderwaterFogSimple);
            // Scene-light fog scattering: armed only when this body wants it AND the tier is
            // not Simple (the budget path stays sun-only). Same keyword-beside-float split as the
            // Simple pair above, so the CPU gate and the compiled variant cannot disagree. The
            // light list itself is published in the same breath - list, keyword and knob move
            // together or not at all. TWO knobs can want the list since A2: the fog's Light
            // Scatter and the god-ray march's Light Scatter (LargeGodRayLightScatter, already
            // gated to an active god-ray ocean) - either arms the shared keyword, so a lamp can
            // glow in the shafts even while the analytic fog glow is authored to 0.
            bool fogPointLights = (_body.UnderwaterLightScatter > 0f
                                   || _body.LargeGodRayLightScatter > 0f)
                                  && fogSimple < 0.5f;
            if (fogPointLights) Shader.EnableKeyword(KW_UnderwaterFogPointLights);
            else Shader.DisableKeyword(KW_UnderwaterFogPointLights);
            PublishSceneLights(fogPointLights);
            Shader.SetGlobalFloat(ID_UnderwaterFogArmed, fogArmed);
            // See KW_UndersideFoam: the underside sheet is only ever looked at from below, so above
            // the surface the whitecap/whitewash taps are compiled out of the surface pass entirely.
            if (fogArmed > 0.5f) Shader.EnableKeyword(KW_UndersideFoam);
            else Shader.DisableKeyword(KW_UndersideFoam);
        }

        // Gather the nearest point/spot lights and publish the package's own capped list (see
        // the field block above for why URP's arrays are deliberately not read). Runs once per
        // frame from the primary body's PublishUnderwater, and does real work only while a body
        // has Light Scatter authored above 0 - disarmed frames publish count 0 so a stale list
        // can never glow. The scene lookup is refreshed at a low cadence; the cached lights are
        // still evaluated every frame, so dynamic lights remain fully live.
        void PublishSceneLights(bool armed)
        {
            if (!armed)
            {
                Shader.SetGlobalFloat(ID_SceneLightCount, 0f);
                return;
            }
            Camera eye = _body.targetCamera;
            Vector3 eyePos = eye != null ? eye.transform.position : _body.VolumeCenter;
            RefreshSceneLightCache();
            int count = 0;
            for (int i = 0; i < s_SceneLights.Count; i++)
            {
                Light light = s_SceneLights[i];
                if (light == null || !light.isActiveAndEnabled || light.intensity <= 0f) continue;
                if (light.type != LightType.Point && light.type != LightType.Spot) continue;
                Vector3 pos = light.transform.position;
                float distSq = (pos - eyePos).sqrMagnitude;
                // Insertion into the capped, nearest-first arrays (N is tiny; no allocations).
                int slot = count < MaxSceneLights ? count : MaxSceneLights - 1;
                if (count >= MaxSceneLights && distSq >= s_SceneLightDistSq[slot]) continue;
                while (slot > 0 && s_SceneLightDistSq[slot - 1] > distSq)
                {
                    s_SceneLightDistSq[slot] = s_SceneLightDistSq[slot - 1];
                    s_SceneLightPosRange[slot] = s_SceneLightPosRange[slot - 1];
                    s_SceneLightColorCone[slot] = s_SceneLightColorCone[slot - 1];
                    s_SceneLightSpotDir[slot] = s_SceneLightSpotDir[slot - 1];
                    slot--;
                }
                s_SceneLightDistSq[slot] = distSq;
                s_SceneLightPosRange[slot] = new Vector4(pos.x, pos.y, pos.z, light.range);
                Color tint = light.color * light.intensity;
                if (light.type == LightType.Spot)
                {
                    float cosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
                    float cosInner = Mathf.Cos(light.innerSpotAngle * 0.5f * Mathf.Deg2Rad);
                    float invConeRange = 1f / Mathf.Max(cosInner - cosOuter, SpotConeRangeEpsilon);
                    Vector3 dir = light.transform.forward;
                    s_SceneLightColorCone[slot] = new Vector4(tint.r, tint.g, tint.b, cosOuter);
                    s_SceneLightSpotDir[slot] = new Vector4(dir.x, dir.y, dir.z, invConeRange);
                }
                else
                {
                    // Point: the sentinel makes the shader's cone factor saturate to 1 for any
                    // direction, so one code path serves both light types.
                    s_SceneLightColorCone[slot] = new Vector4(tint.r, tint.g, tint.b,
                                                              PointLightConeSentinel);
                    s_SceneLightSpotDir[slot] = new Vector4(0f, 1f, 0f, 1f);
                }
                if (count < MaxSceneLights) count++;
            }
            // SetGlobalVectorArray pins the array SIZE on first use, so the full fixed-size
            // arrays are always sent; the count bounds the shader loop over live entries.
            Shader.SetGlobalVectorArray(ID_SceneLightPosRange, s_SceneLightPosRange);
            Shader.SetGlobalVectorArray(ID_SceneLightColorCone, s_SceneLightColorCone);
            Shader.SetGlobalVectorArray(ID_SceneLightSpotDir, s_SceneLightSpotDir);
            Shader.SetGlobalFloat(ID_SceneLightCount, count);
        }

        static void RefreshSceneLightCache()
        {
            if (Time.unscaledTime < s_SceneLightCacheRefreshAt) return;

            s_SceneLightCacheRefreshAt = Time.unscaledTime + SceneLightCacheRefreshSeconds;
            Light[] discoveredLights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            s_SceneLights.Clear();
            s_SceneLights.AddRange(discoveredLights);
        }

        /// <summary>Screen-space waterline (meniscus) tunables for the fog material's waterline
        /// pass. Global like PublishUnderwater (camera/screen state, primary-driven); the pass
        /// itself is gated by WaterVolume.WaterlineActive, so stale values never draw.</summary>
        internal void PublishWaterline(float widthPixels, float strength, float warp)
        {
            Shader.SetGlobalFloat(ID_WaterlineWidthPx, widthPixels);
            Shader.SetGlobalFloat(ID_WaterlineStrength, strength);
            Shader.SetGlobalFloat(ID_WaterlineWarp, warp);
        }

        /// <summary>Push the body's placement-frame uniforms (volume + sim window) onto a
        /// compute shader so GPU consumers share the exact same transforms as the render side.</summary>
        internal void WriteSimFrameUniforms(ComputeShader cs)
        {
            cs.SetVector(ID_VolumeCenter, _body.VolumeCenter);
            cs.SetVector(ID_VolumeExtent, _body.VolumeExtentSafe);
            cs.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(_body.VolumeRotation));
            cs.SetFloat(ID_SimWindowed, _body.IsWindowed ? 1f : 0f);
            cs.SetVector(ID_SimCenter, _body.SimWindowCenter);
            cs.SetVector(ID_SimExtent, _body.SimHalfExtent);
        }

        // Single source of truth for the per-frame uniform derivations. Texture guards match
        // both former paths (a null texture is skipped rather than unbound).
        void WriteBodyUniforms(IUniformSink sink)
        {
            // Per-body wave clock: each body's renderers/objects animate on THEIR OWN clock (TimeScale,
            // pause), matching CPU buoyancy/waterline. The primary's global mirror is the fallback for
            // the fullscreen fog pass, the large-body caustic grid, and membership-less object shaders.
            sink.SetFloat(ID_WaveTime, _body.WaveTime);

            WaterSimulation water = _body.Simulation;
            if (water != null)
            {
                sink.SetTexture(ID_Water, water.Texture);
                sink.SetVector(ID_WaterTexel, _body.WaterTexel);
                if (water.FoamTexture != null) sink.SetTexture(ID_FoamMask, water.FoamTexture);
            }
            if (_body.CausticTexture != null) sink.SetTexture(ID_Caustic, _body.CausticTexture);
            // Per body (a global was last-writer-wins with 2+ caustic bodies): 1 when THIS body's pass
            // wrote submerged-object silhouettes into caustic.g this frame.
            sink.SetFloat(ID_CausticOccluderActive, _body.CausticOccluderActive ? 1f : 0f);
            // Which frame that RT was written in - published beside the texture itself so the two can
            // never be separated. WaterCausticProjection.shader undoes the matching projection.
            sink.SetFloat(ID_CausticFrameMode, (float)_body.CausticProjectionFrame);
            // Refract-shadow look: the per-body softness knob, plus the sun's OWN Shadow Strength -
            // so the refracted occluder path dims its shadows exactly like URP's shadow map does on
            // the fallback path (shadowAttenuation folds the same value in). No sun wired = full 1.
            sink.SetFloat(ID_OccluderShadowSoftness, _body.refractShadowSoftness);
            sink.SetFloat(ID_SunShadowStrength, _body.sun != null ? _body.sun.shadowStrength : 1f);

            sink.SetVector(ID_VolumeCenter, _body.VolumeCenter);
            sink.SetVector(ID_VolumeExtent, _body.VolumeExtentSafe);
            sink.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(_body.VolumeRotation));

            sink.SetFloat(ID_SimWindowed, _body.IsWindowed ? 1f : 0f);
            sink.SetVector(ID_SimCenter, _body.SimWindowCenter);
            sink.SetVector(ID_SimExtent, _body.SimHalfExtent);
            sink.SetFloat(ID_SimEdgeFade, _body.simWindowEdgeFadeTexels);
            sink.SetFloat(ID_LargeBody, _body.openWater ? 1f : 0f);
            // Per-body: only the FFT-driven ocean samples the cascade textures; every other body (pools,
            // bounded open water) publishes 0 and keeps the analytic large-wave path unchanged.
            sink.SetFloat(ID_OceanFftActive, _body.OceanFftActive ? 1f : 0f);
            sink.SetColor(ID_OceanFoamColor, _body.OceanFoamColor);
            sink.SetFloat(ID_OceanFoamTileSize, _body.OceanFoamTileSize);
            sink.SetFloat(ID_OceanFoamFeather, _body.OceanFoamFeather);
            sink.SetFloat(ID_OceanFoamStreakStretch, _body.OceanFoamStreakStretch);
            sink.SetFloat(ID_OceanFoamTextureInfluence, _body.OceanFoamTextureInfluence);
            sink.SetFloat(ID_OceanFoamDepthTint, _body.OceanFoamDepthTint);
            // Ambient geometry-foam floor: an ocean-surface CHUNK has no FFT accumulator and no
            // surf band, so the analytic Jacobian/steepness foam is its ONLY whitecap source -
            // enabled there and nowhere else (FFT oceans + every existing scene publish 0 and
            // stay byte-identical). The value is the body's Whitecap Foam knob: 1 = physical
            // pinch/steepness, >1 whitens milder crests, 0 = off. See LbwGeometryFoamGate.
            sink.SetFloat(ID_LbwGeomFoamFloor,
                          _body.IsChunk && _body.openWater ? _body.chunkFoamStrength : 0f);
            sink.SetFloat(ID_LargeWaveAmp, _body.LargeWaveAmplitudeEffective);
            // The sea's only metre scale, for SurfaceHeightBand (WaterWaterline.hlsl). The
            // amplitude above is a multiplier on this, not a height - the band needs both.
            sink.SetFloat(ID_OffshoreSigHeight, _body.OffshoreSignificantHeight);
            sink.SetFloat(ID_LargeWaveWind, _body.LargeWaveHeadingRad);
            sink.SetFloat(ID_LargeWaveChop, _body.LargeWaveChoppiness);
            sink.SetFloat(ID_RippleChoppiness, _body.rippleChoppiness);
            sink.SetVector(ID_PoolSlopeToWorld, _body.PoolSlopeToWorld);
            sink.SetVector(ID_SimSlopeToWorld, _body.SimSlopeToWorld);
            // 0 whenever no patch is drawn (every bounded body, and windowed bodies in edit mode),
            // which leaves the base sheet whole exactly as before.
            sink.SetFloat(ID_PatchCoverActive, _body.PatchCoverActive ? 1f : 0f);
            sink.SetFloat(ID_PatchCoverMargin, _body.PatchCoverMargin);
            sink.SetVector(ID_PatchCoverCenter, _body.PatchPoolCenter);
            sink.SetVector(ID_PatchCoverHalf, _body.PatchPoolHalf);
            sink.SetFloat(ID_LargeWaveDetail, _body.OceanDetailSlope);
            // 0 for pools AND unbounded oceans (the Effective accessor gates); only a BOUNDED
            // open-water body feathers its wave field toward the footprint border.
            sink.SetFloat(ID_LargeWaveEdgeFeather, _body.LargeWaveEdgeFeatherEffective);
            sink.SetFloat(ID_OceanWorldWaves, _body.IsOceanClipmap ? 1f : 0f);
            sink.SetFloat(ID_SwellWavelength, _body.SwellWavelength);
            sink.SetFloat(ID_SwellHeight, _body.SwellHeight);
            sink.SetFloat(ID_HorizonFade, _body.HorizonFadeDistance);
            sink.SetColor(ID_HorizonHazeColor, _body.HorizonHazeColor);
            sink.SetFloat(ID_HorizonHazeDensity, _body.HorizonHazeDensity);
            sink.SetColor(ID_LargeGodRayColor, _body.LargeGodRayColor);
            sink.SetFloat(ID_LargeGodRayDensity, _body.LargeGodRayDensity);
            sink.SetFloat(ID_LargeGodRaySteps, _body.LargeGodRaySteps);
            sink.SetFloat(ID_LargeGodRayAnisotropy, _body.LargeGodRayAnisotropy);
            sink.SetFloat(ID_LargeGodRayExtinction, _body.LargeGodRayExtinction);
            sink.SetFloat(ID_LargeGodRayCausticStrength, _body.LargeGodRayCausticStrength);
            sink.SetFloat(ID_LargeGodRayCausticDepthSoften, _body.LargeGodRayCausticDepthSoften);
            sink.SetFloat(ID_LargeGodRayFromAir, _body.LargeGodRayFromAir);
            sink.SetFloat(ID_LargeGodRayLightScatter, _body.LargeGodRayLightScatter);
            sink.SetFloat(ID_LargeCausticProjectionLod, _body.LargeCausticProjectionLod);

            sink.SetVectorArray(ID_WaveA, _body.WaveBank.PackedA);
            sink.SetVectorArray(ID_WaveB, _body.WaveBank.PackedB);
            sink.SetFloat(ID_WaveCount, _body.WindWaves ? _body.WaveBank.Count : 0f);
            sink.SetFloat(ID_WaveMeters, _body.WaveMetersPerUnit);
            sink.SetFloat(ID_WaveNormal, _body.waveNormalStrength);
            sink.SetVector(ID_WaveGroupA, _body.WaveBank.GroupA);
            sink.SetVector(ID_WaveGroupB, _body.WaveBank.GroupB);
            sink.SetVector(ID_WaveShape, _body.WaveBank.Shape);
            sink.SetFloat(ID_WaveStokesNorm, _body.WaveBank.StokesNorm);

            sink.SetColor(ID_FogColor, _body.fogColor);
            sink.SetColor(ID_FogExt, _body.fogExtinction);
            sink.SetFloat(ID_FogDensity, _body.fogDensity);
            sink.SetFloat(ID_FogEnabled, _body.WaterFog ? 1f : 0f);
            sink.SetFloat(ID_WaterOpacity, _body.waterOpacity);
            // Point/spot-light scattering strength in the underwater fog (the WATER_FOG_POINT_LIGHTS
            // variant, armed by PublishUnderwater from the SAME field so gate and shader agree).
            sink.SetFloat(ID_UnderwaterLightScatter, _body.UnderwaterLightScatter);

            // Lit volume scattering: turns the flat fog colour into a sun-lit in-scatter.
            sink.SetFloat(ID_ScatterEnabled, _body.volumeScatter ? 1f : 0f);
            sink.SetColor(ID_ScatterColor, _body.scatterColor);
            sink.SetFloat(ID_ScatterIntensity, _body.scatterIntensity);
            sink.SetFloat(ID_ScatterAmbientTerm, _body.scatterAmbientTerm);
            sink.SetFloat(ID_ScatterSunTerm, _body.scatterSunTerm);
            sink.SetFloat(ID_ScatterAnisotropy, _body.scatterAnisotropy);
            sink.SetFloat(ID_SssEnabled, _body.crestScatter ? 1f : 0f);
            sink.SetFloat(ID_SssIntensity, _body.sssIntensity);
            sink.SetFloat(ID_SssSunFalloff, _body.sssSunFalloff);
            sink.SetFloat(ID_SssPinchMin, _body.sssPinchMin);
            sink.SetFloat(ID_SssPinchMax, _body.sssPinchMax);
            sink.SetFloat(ID_SssPinchFalloff, _body.sssPinchFalloff);

            sink.SetColor(ID_DepthExt, _body.EffectiveDepthExtinction);
            sink.SetFloat(ID_DepthStrength, _body.depthDarkenStrength);
            sink.SetFloat(ID_DepthEnabled, _body.depthDarken ? 1f : 0f);
            sink.SetFloat(ID_CausticDepthFade, _body.causticDepthFade);
            sink.SetFloat(ID_GodRayDepthFade, _body.godRayDepthFade);
            // Tier cost knobs ride the same per-body path so bodies on different tiers never
            // fight over a shared material (and the editor asset is never dirtied).
            sink.SetFloat(ID_GodRaySteps, _body.GodRaySteps);
            sink.SetFloat(ID_PeakedRefine, _body.PeakedRefineSteps);

            // Pool interiors are body data: a second procedural pool may use different tiles.
            // The primary body's global mirror remains the fallback for unbound renderers.
            if (_body.tiles != null) sink.SetTexture(ID_Tiles, _body.tiles);

            // Reflection: uniform-driven and live. Tier-capped toggles + the look, per body per frame.
            sink.SetFloat(ID_UsePlanar, _body.EffectiveUsePlanar ? 1f : 0f);
            // This body's OWN planar mirror, bound per body (was a single shared global that only one plane
            // could be correct for). Null until the first mirror render / when planar is off - the shader
            // only samples it when _UsePlanar is set, which tracks the same EffectiveUsePlanar gate.
            Texture planarTex = _body.PlanarReflectionTexture;
            if (planarTex != null) sink.SetTexture(ID_PlanarTex, planarTex);
            sink.SetFloat(ID_UseSSR, _body.EffectiveUseSSR ? 1f : 0f);
            sink.SetFloat(ID_UseUrpProbe, _body.ReflectUrpProbe ? 1f : 0f);
            sink.SetFloat(ID_RealRefraction, _body.EffectiveRealRefraction ? 1f : 0f);
            sink.SetFloat(ID_ProceduralPool, _body.HasProceduralPool ? 1f : 0f);
            sink.SetFloat(ID_ReflectionStrength, _body.ReflectionStrength);
            sink.SetFloat(ID_EnvReflectionIntensity, _body.EnvReflectionIntensity);
            sink.SetFloat(ID_SunReflectionIntensity, _body.SunReflectionIntensity);
            // Fresnel + shared-roughness ramp + reflection stretch (the WOW look pass), live per body.
            sink.SetFloat(ID_FresnelFloor, _body.FresnelFloor);
            sink.SetFloat(ID_FresnelPower, _body.FresnelPower);
            sink.SetFloat(ID_SunRoughness, _body.SunRoughness);
            sink.SetFloat(ID_RoughnessFar, _body.RoughnessFar);
            sink.SetFloat(ID_RoughnessFarDistance, _body.RoughnessFarDistance);
            sink.SetFloat(ID_RoughnessFalloff, _body.RoughnessFalloff);
            sink.SetFloat(ID_ReflectionAnisoStretch, _body.ReflectionAnisoStretch);
            sink.SetFloat(ID_SunSheen, _body.SunSheen);
            sink.SetFloat(ID_SunSheenRoughness, _body.SunSheenRoughness);
            sink.SetFloat(ID_SunGrazeBoost, _body.SunGrazeBoost);
            // Detail normals: texture bound only when assigned (a null SetTexture is an error);
            // DetailNormalStrength is already forced to 0 with no texture, which gates the shader.
            Texture detailNormalTex = _body.DetailNormalTexture;
            if (detailNormalTex != null) sink.SetTexture(ID_DetailNormalTex, detailNormalTex);
            sink.SetFloat(ID_DetailNormalStrength, _body.DetailNormalStrength);
            sink.SetFloat(ID_DetailNormalScale, _body.DetailNormalScale);
            sink.SetFloat(ID_DetailNormalFarScale, _body.DetailNormalFarScale);
            sink.SetFloat(ID_DetailNormalFarDistance, _body.DetailNormalFarDistance);
            sink.SetFloat(ID_DetailNormalFarSpeed, _body.DetailNormalFarSpeed);
            sink.SetFloat(ID_DetailNormalHexTiling, _body.DetailNormalHexTiling ? 1f : 0f);
            sink.SetFloat(ID_DetailNormalDistanceBoost, _body.DetailNormalDistanceBoost);
            sink.SetFloat(ID_DetailNormalSpeed, _body.DetailNormalSpeed);
            sink.SetFloat(ID_DetailNormalCrestBoost, _body.DetailNormalCrestBoost);
            // One wind for the surface. Published unconditionally (like the fog coefficients) so the
            // detail layer never reads a stale or zero heading on a body with Wind Waves switched off.
            sink.SetVector(ID_WindDirection, _body.WindDirectionXZ);

            // Underside (seen-from-below) look: its own fresnel/mirror family (Underwater Surface
            // block), so the below-water view no longer rides the above-water constants.
            sink.SetFloat(ID_UnderFresnelPhysical, _body.UnderwaterPhysicalFresnel ? 1f : 0f);
            sink.SetFloat(ID_UnderTirSoftness, _body.UnderwaterTirEdgeSoftness);
            sink.SetFloat(ID_UnderFresnelFloor, _body.UnderwaterFresnelFloor);
            sink.SetFloat(ID_UnderReflectionStrength, _body.UnderwaterReflectionStrength);
            sink.SetFloat(ID_UnderMirrorWaterBlend, _body.UnderwaterMirrorWaterBlend);
            // Volumetric coupling of the TIR mirror (KWS increment, phase 1). The accessor gates
            // to 0 without an active god-ray ocean, so the shader term (which samples the shaft
            // HISTORY global LargeBodyAtmospherePass binds) adds black on every legacy scene.
            sink.SetFloat(ID_UnderMirrorShafts, _body.UnderwaterMirrorShafts);
            sink.SetFloat(ID_FoamUndersideDarken, _body.FoamUndersideDarken);
            sink.SetFloat(ID_FoamUndersideGlow, _body.FoamUndersideGlow);
            sink.SetFloat(ID_UnderDetailNormalStrength, _body.UnderwaterDetailNormalStrength);

            // Reflection base cubemap, PER BODY (via the property block) so multiple bodies with
            // different Sky slots / reflection modes never stomp a shared global. Procedural sky = the
            // body's Sky slot; Reflect URP Probe = the scene skybox cube (SceneSkyboxCubemap).
            Cubemap reflectionCube = ResolveReflectionCube();
            if (reflectionCube != null) sink.SetTexture(ID_Sky, reflectionCube);
            sink.SetFloat(ID_ReflectionDistortion, _body.ReflectionDistortion);
            sink.SetFloat(ID_SSRStrength, _body.SSRStrength);
            sink.SetFloat(ID_SSRStepSize, _body.SSRStepSize);
            sink.SetFloat(ID_SSRMaxSteps, _body.SSRMaxSteps);
            sink.SetFloat(ID_SSRThickness, _body.SSRThickness);
            sink.SetFloat(ID_RefractionDistortion, _body.RefractionDistortion);
            sink.SetFloat(ID_RefractionStrength, _body.RefractionStrength);

            if (_body.BedTexture != null) sink.SetTexture(ID_BedTex, _body.BedTexture);
            sink.SetFloat(ID_BedValid, _body.IsBedBaked ? 1f : 0f);
            sink.SetFloat(ID_UseBedDepth, _body.useBedDepth ? 1f : 0f);
            // The whole shore field rides this same per-body sink: depth/SDF textures, their world
            // frame and every shoal/surf knob must stay together or two shore-enabled bodies would
            // sample whichever one last wrote the old graphics globals.
            sink.SetFloat(ID_ShoreBodyGate, _body.useBedDepth ? 1f : 0f);
            _body.ShoreDepth.WriteUniforms(sink);
            sink.SetColor(ID_DeepWaterColor, _body.deepWaterColor);
            sink.SetFloat(ID_ShorelineScale, 1f / Mathf.Max(WaterVolume.MinBedFadeDepth, _body.bedFadeDepth));
            sink.SetFloat(ID_ShorelineStrength, _body.bedTintStrength);
            // Depth clarity: one curve (shallow/deep depth + clarity at each end) drives turbidity,
            // fog reach and the deep tint together. Strength 0 (feature off) = inert (flat per-body look).
            sink.SetVector(ID_DepthClarityRange, new Vector4(
                _body.clarityShallowDepth, _body.clarityDeepDepth, _body.clarityShallow, _body.clarityDeep));
            sink.SetFloat(ID_DepthClarityStrength, _body.clarityFromDepth ? _body.clarityStrength : 0f);

            sink.SetColor(ID_FoamColor, _body.foamColor);
            sink.SetFloat(ID_FoamEnabled, _body.Foam ? 1f : 0f);
            // The wet mark is only meaningful while the foam pass is actually stepping. With both off
            // the buffer keeps its LAST values, and a consumer reading them would hold ground
            // permanently wet at a waterline that has not existed for minutes.
            sink.SetFloat(ID_WetMarkActive, (_body.Foam || _body.wetnessMemory) ? 1f : 0f);
            // Published unconditionally, NOT gated on wetnessMemory: the surf swash dries on this
            // clock whether or not the sim is keeping a wet mark, and a body with the memory off must
            // still hand the beach a sane duration.
            sink.SetFloat(ID_WetDryTimeSeconds, _body.wetnessDryTime);
            sink.SetFloat(ID_FoamStrength, _body.foamStrength);
            sink.SetFloat(ID_FoamTileSize, _body.foamPatternSize);
            sink.SetFloat(ID_FoamBorder, _body.foamBorderWidth);
            sink.SetFloat(ID_FoamContact, _body.foamContactDepth);
            sink.SetFloat(ID_FoamFeather, _body.foamFeather);
            sink.SetFloat(ID_FoamCoreCut, _body.foamCoreCut);

            // Body-owned surface textures (Textures section). Each is bound ONLY when assigned on the body,
            // so a body that leaves it empty keeps whatever the water material authored - existing scenes are
            // unchanged. The flipbook grid/rate ride along only when the foam pattern itself is body-owned.
            if (_body.foamPatternTexture != null)
            {
                sink.SetTexture(ID_FoamTex, _body.foamPatternTexture);
                sink.SetVector(ID_FoamTexFrames,
                    new Vector4(_body.foamPatternGrid.x, _body.foamPatternGrid.y, 0f, 0f));
                sink.SetFloat(ID_FoamTexFPS, _body.foamPatternFps);
            }
            if (_body.oceanWhitecapTexture != null)
            {
                sink.SetTexture(ID_OceanWhitecapTex, _body.oceanWhitecapTexture);
                // Optional flipbook - drives the deep whitecaps AND the surf whitewash (shared texture).
                sink.SetVector(ID_OceanWhitecapFrames,
                    new Vector4(_body.oceanWhitecapGrid.x, _body.oceanWhitecapGrid.y, 0f, 0f));
                sink.SetFloat(ID_OceanWhitecapFPS, _body.oceanWhitecapFps);
            }
            // Relief is shared by the foam pattern and the whitecap: push it when EITHER is body-owned.
            if (_body.foamPatternTexture != null || _body.oceanWhitecapTexture != null)
                sink.SetFloat(ID_FoamNormalStrength, _body.foamReliefStrength);
        }

        // A write target for the per-body uniforms: either a MaterialPropertyBlock or the
        // global shader state. Only the id-keyed setters WriteBodyUniforms needs are exposed.
        internal interface IUniformSink
        {
            void SetFloat(int id, float value);
            void SetColor(int id, Color value);
            void SetVector(int id, Vector4 value);
            void SetMatrix(int id, Matrix4x4 value);
            void SetVectorArray(int id, Vector4[] value);
            void SetTexture(int id, Texture value);
        }

        sealed class MpbUniformSink : IUniformSink
        {
            public MaterialPropertyBlock Target;
            public void SetFloat(int id, float value) => Target.SetFloat(id, value);
            public void SetColor(int id, Color value) => Target.SetColor(id, value);
            public void SetVector(int id, Vector4 value) => Target.SetVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => Target.SetMatrix(id, value);
            public void SetVectorArray(int id, Vector4[] value) => Target.SetVectorArray(id, value);
            public void SetTexture(int id, Texture value) => Target.SetTexture(id, value);
        }

        sealed class GlobalUniformSink : IUniformSink
        {
            public void SetFloat(int id, float value) => Shader.SetGlobalFloat(id, value);
            public void SetColor(int id, Color value) => Shader.SetGlobalColor(id, value);
            public void SetVector(int id, Vector4 value) => Shader.SetGlobalVector(id, value);
            public void SetMatrix(int id, Matrix4x4 value) => Shader.SetGlobalMatrix(id, value);
            public void SetVectorArray(int id, Vector4[] value) => Shader.SetGlobalVectorArray(id, value);
            public void SetTexture(int id, Texture value) => Shader.SetGlobalTexture(id, value);
        }
    }
}
