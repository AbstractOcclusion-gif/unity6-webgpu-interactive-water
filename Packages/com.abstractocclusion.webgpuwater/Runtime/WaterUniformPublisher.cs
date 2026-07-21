// WebGpuWater - per-body shader uniform publishing.
// Extracted from WaterVolume: the single source of truth for the body's per-frame
// uniform derivations, written through a sink either into a MaterialPropertyBlock
// (this body's renderers, and WaterMembership'd objects) or into the global shader
// state (the primary body's fallback for objects without a membership) - so the
// values are derived once and the two paths can never drift.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUniformPublisher
    {
        // shader property / global ids, cached once
        static readonly int ID_Water = Shader.PropertyToID("_WaterTex");
        static readonly int ID_WaterTexel = Shader.PropertyToID("_WaterTexel");
        static readonly int ID_Caustic = Shader.PropertyToID("_CausticTex");
        // "Skybox/Cubemap" material texture slot - cached like every other ID: the lookup runs on
        // the per-frame body-uniform path, where an inline string was the one uncached exception.
        static readonly int ID_SkyboxCubemapTex = Shader.PropertyToID("_Tex");
        static readonly int ID_CausticOccluderActive = Shader.PropertyToID("_CausticOccluderActive");
        static readonly int ID_Tiles = Shader.PropertyToID("_Tiles");
        static readonly int ID_Sky = Shader.PropertyToID("_Sky");
        static readonly int ID_Light = Shader.PropertyToID("_LightDir");
        static readonly int ID_SunColor = Shader.PropertyToID("_SunColor");
        static readonly int ID_FogColor = Shader.PropertyToID("_WaterFogColor");
        static readonly int ID_FogExt = Shader.PropertyToID("_WaterExtinction");
        static readonly int ID_FogDensity = Shader.PropertyToID("_WaterFogDensity");
        static readonly int ID_FogEnabled = Shader.PropertyToID("_WaterFogEnabled");
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
        static readonly int ID_BedTex = Shader.PropertyToID("_BedTex");
        static readonly int ID_BedValid = Shader.PropertyToID("_BedValid");
        static readonly int ID_UseBedDepth = Shader.PropertyToID("_UseBedDepth");
        static readonly int ID_ShoreBodyGate = Shader.PropertyToID("_ShoreBodyGate");
        static readonly int ID_DeepWaterColor = Shader.PropertyToID("_DeepWaterColor");
        static readonly int ID_ShorelineScale = Shader.PropertyToID("_ShorelineDepthScale");
        static readonly int ID_ShorelineStrength = Shader.PropertyToID("_ShorelineStrength");
        static readonly int ID_DepthClarityRange = Shader.PropertyToID("_DepthClarityRange");
        static readonly int ID_DepthClarityStrength = Shader.PropertyToID("_DepthClarityStrength");
        static readonly int ID_FoamMask = Shader.PropertyToID("_FoamMask");
        static readonly int ID_FoamColor = Shader.PropertyToID("_FoamColor");
        static readonly int ID_FoamEnabled = Shader.PropertyToID("_FoamEnabled");
        static readonly int ID_FoamStrength = Shader.PropertyToID("_FoamStrength");
        static readonly int ID_FoamTileSize = Shader.PropertyToID("_FoamTileSize");
        // Body-owned surface texture inputs (Textures section): bound only when assigned on the body.
        static readonly int ID_FoamTex = Shader.PropertyToID("_FoamTex");
        static readonly int ID_FoamTexFrames = Shader.PropertyToID("_FoamTexFrames");
        static readonly int ID_FoamTexFPS = Shader.PropertyToID("_FoamTexFPS");
        static readonly int ID_FoamNormalStrength = Shader.PropertyToID("_FoamNormalStrength");
        static readonly int ID_OceanWhitecapTex = Shader.PropertyToID("_OceanWhitecapTex");
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
        static readonly int ID_VolumeCenter = Shader.PropertyToID("_VolumeCenter");
        static readonly int ID_VolumeExtent = Shader.PropertyToID("_VolumeExtent");
        static readonly int ID_VolumeRot = Shader.PropertyToID("_VolumeRot");
        static readonly int ID_GodRaySteps = Shader.PropertyToID("_GodRaySteps");
        static readonly int ID_SimWindowed = Shader.PropertyToID("_SimWindowed");
        static readonly int ID_SimCenter = Shader.PropertyToID("_SimCenter");
        static readonly int ID_SimExtent = Shader.PropertyToID("_SimExtent");
        static readonly int ID_SimEdgeFade = Shader.PropertyToID("_SimEdgeFadeTexels");
        static readonly int ID_LargeBody = Shader.PropertyToID("_LargeBody");
        static readonly int ID_OceanFftActive = Shader.PropertyToID("_OceanFftActive");
        static readonly int ID_OceanFoamColor = Shader.PropertyToID("_OceanFoamColor");
        static readonly int ID_OceanFoamTileSize = Shader.PropertyToID("_OceanFoamTileSize");
        static readonly int ID_OceanFoamFeather = Shader.PropertyToID("_OceanFoamFeather");
        static readonly int ID_LargeWaveAmp = Shader.PropertyToID("_LargeWaveAmplitude");
        static readonly int ID_LargeWaveWind = Shader.PropertyToID("_LargeWaveWindHeading");
        static readonly int ID_LargeWaveChop = Shader.PropertyToID("_LargeWaveChoppiness");
        static readonly int ID_RippleChoppiness = Shader.PropertyToID("_RippleChoppiness");
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
        static readonly int ID_CameraUnderwater = Shader.PropertyToID("_CameraUnderwater");
        static readonly int ID_UnderwaterSurfaceY = Shader.PropertyToID("_UnderwaterSurfaceY");
        static readonly int ID_UnderwaterUnbounded = Shader.PropertyToID("_UnderwaterUnbounded");
        static readonly int ID_UnderwaterFogSimple = Shader.PropertyToID("_UnderwaterFogSimple");
        static readonly int ID_UnderwaterFogArmed = Shader.PropertyToID("_UnderwaterFogArmed");
        static readonly int ID_PeakedRefine = Shader.PropertyToID("_PeakedRefineSteps");
        static readonly int ID_UsePlanar = Shader.PropertyToID("_UsePlanar");
        static readonly int ID_PlanarTex = Shader.PropertyToID("_PlanarReflectionTex");
        static readonly int ID_UseSSR = Shader.PropertyToID("_UseSSR");
        static readonly int ID_UseUrpProbe = Shader.PropertyToID("_UseUrpProbe");
        static readonly int ID_RealRefraction = Shader.PropertyToID("_RealRefraction");
        static readonly int ID_ProceduralPool = Shader.PropertyToID("_ProceduralPool");
        static readonly int ID_ReflectionStrength = Shader.PropertyToID("_ReflectionStrength");
        static readonly int ID_EnvReflectionIntensity = Shader.PropertyToID("_EnvReflectionIntensity");
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
        static readonly int ID_DetailNormalSpeed = Shader.PropertyToID("_DetailNormalSpeed");
        static readonly int ID_ReflectionDistortion = Shader.PropertyToID("_ReflectionDistortion");
        static readonly int ID_SSRStrength = Shader.PropertyToID("_SSRStrength");
        static readonly int ID_SSRStepSize = Shader.PropertyToID("_SSRStepSize");
        static readonly int ID_SSRMaxSteps = Shader.PropertyToID("_SSRMaxSteps");
        static readonly int ID_SSRThickness = Shader.PropertyToID("_SSRThickness");
        static readonly int ID_RefractionDistortion = Shader.PropertyToID("_RefractionDistortion");
        static readonly int ID_ExclusionCount = Shader.PropertyToID("_ExclusionCount");
        static readonly int ID_ExclusionWorldToBox = Shader.PropertyToID("_ExclusionWorldToBox");
        static readonly int ID_ExclusionEdgeColor = Shader.PropertyToID("_ExclusionEdgeColor");
        static readonly int ID_ExclusionEdgeParams = Shader.PropertyToID("_ExclusionEdgeParams");

        // Persistent FULL-SIZE buffers for the exclusion uniforms: Unity locks a global
        // array's size at its FIRST set, so every publish sends MaxVolumes entries and
        // _ExclusionCount clamps the shader loop. Static (the volumes are global state,
        // shared by every body's publisher) and reused every frame - no allocation.
        static readonly Matrix4x4[] _exclusionMatrices = new Matrix4x4[WaterExclusionVolume.MaxVolumes];
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
            Shader.SetGlobalVector(ID_Light, _body.EffectiveLightDir.normalized);
            Shader.SetGlobalColor(ID_SunColor, _body.sun != null ? _body.sun.color * _body.sun.intensity : Color.white);
            // Scene ambient feeds the volume-scatter in-scatter so shaded (away-from-sun) water isn't black.
            // Genuinely shared (scene lighting, not per body), so it rides with the sun here.
            Shader.SetGlobalColor(ID_ScatterAmbient, RenderSettings.ambientLight);
            if (_body.tiles != null) Shader.SetGlobalTexture(ID_Tiles, _body.tiles);
            // Exclusion volumes are GLOBAL, not per body (a dry room is dry in whichever
            // body intersects it), so they ride the shared-globals path, not the sink.
            PublishExclusionVolumes();
            // NOTE: the reflection cube (_Sky) is published PER BODY in WriteBodyUniforms, not here -
            // a global would be stomped by the last body each frame when bodies use different skies.
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
                _exclusionMatrices, _exclusionEdgeColors, _exclusionEdgeParams, reference);
            Shader.SetGlobalFloat(ID_ExclusionCount, count);
            // With count 0 the shader loop never reads the arrays, so skipping the sets is
            // safe and keeps the zero-volume frame free of the array uploads.
            if (count > 0)
            {
                Shader.SetGlobalMatrixArray(ID_ExclusionWorldToBox, _exclusionMatrices);
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

        static Cubemap SceneSkyboxCubemap()
        {
            Material skybox = RenderSettings.skybox;
            if (skybox == null || !skybox.HasProperty(ID_SkyboxCubemapTex)) return null;
            return skybox.GetTexture(ID_SkyboxCubemapTex) as Cubemap;
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

        /// <summary>Camera-submerged flag + flat surface Y for the underwater fog pass. Global only
        /// (it is camera state, not a per-object uniform), so it lives outside WriteBodyUniforms.
        /// fogSimple 1 = the tier's Simple mode: the fog shader takes the closed-form flat-waterline
        /// path (against surfaceY) instead of the per-pixel wavy-surface march.
        /// fogArmed 1 = the fullscreen fog pass runs this frame (WaterVolume.UnderwaterFogActive):
        /// the exclusion wall reads it to know whether the fog will paint behind its veil or the
        /// wall must reconstruct the fog result itself (above-water ocean views).</summary>
        internal void PublishUnderwater(float cameraUnderwater, float surfaceY, float unbounded,
                                        float fogSimple, float fogArmed)
        {
            Shader.SetGlobalFloat(ID_CameraUnderwater, cameraUnderwater);
            Shader.SetGlobalFloat(ID_UnderwaterSurfaceY, surfaceY);
            Shader.SetGlobalFloat(ID_UnderwaterUnbounded, unbounded);
            Shader.SetGlobalFloat(ID_UnderwaterFogSimple, fogSimple);
            Shader.SetGlobalFloat(ID_UnderwaterFogArmed, fogArmed);
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
            sink.SetFloat(ID_LargeWaveAmp, _body.LargeWaveAmplitudeEffective);
            sink.SetFloat(ID_LargeWaveWind, _body.LargeWaveHeadingRad);
            sink.SetFloat(ID_LargeWaveChop, _body.LargeWaveChoppiness);
            sink.SetFloat(ID_RippleChoppiness, _body.rippleChoppiness);
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

            sink.SetVectorArray(ID_WaveA, _body.WaveBank.PackedA);
            sink.SetVectorArray(ID_WaveB, _body.WaveBank.PackedB);
            sink.SetFloat(ID_WaveCount, _body.WindWaves ? _body.WaveBank.Count : 0f);
            sink.SetFloat(ID_WaveMeters, _body.WaveMetersPerUnit);
            sink.SetFloat(ID_WaveNormal, _body.waveNormalStrength);

            sink.SetColor(ID_FogColor, _body.fogColor);
            sink.SetColor(ID_FogExt, _body.fogExtinction);
            sink.SetFloat(ID_FogDensity, _body.fogDensity);
            sink.SetFloat(ID_FogEnabled, _body.WaterFog ? 1f : 0f);
            sink.SetFloat(ID_WaterOpacity, _body.waterOpacity);

            // Crest-style lit volume scattering: turns the flat fog colour into a sun-lit in-scatter.
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
            sink.SetFloat(ID_DetailNormalSpeed, _body.DetailNormalSpeed);

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

            if (_body.BedTexture != null) sink.SetTexture(ID_BedTex, _body.BedTexture);
            sink.SetFloat(ID_BedValid, _body.IsBedBaked ? 1f : 0f);
            sink.SetFloat(ID_UseBedDepth, _body.useBedDepth ? 1f : 0f);
            // Per-body gate on the SHARED _Shore*/_Surf* globals (WaterShoreDepthField publishes
            // one field at a time): only the body that opted into bed depth reads the shore
            // substrate, so a pond overlapping the terrain field can never catch its surf/shoal.
            sink.SetFloat(ID_ShoreBodyGate, _body.useBedDepth ? 1f : 0f);
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
                sink.SetTexture(ID_OceanWhitecapTex, _body.oceanWhitecapTexture);
            // Relief is shared by the foam pattern and the whitecap: push it when EITHER is body-owned.
            if (_body.foamPatternTexture != null || _body.oceanWhitecapTexture != null)
                sink.SetFloat(ID_FoamNormalStrength, _body.foamReliefStrength);
        }

        // A write target for the per-body uniforms: either a MaterialPropertyBlock or the
        // global shader state. Only the id-keyed setters WriteBodyUniforms needs are exposed.
        interface IUniformSink
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
