// WaterVolume settings - versioned migration of legacy flat fields into the nested per-feature
// blocks. Its own file because it is a one-way upgrade path, not configuration: nothing here is
// authored, and it exists solely so scenes saved before the god-class split keep their values.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        // Legacy capture: scenes/prefabs authored before this migration serialized these under the old
        // top-level names. [FormerlySerializedAs] IS valid here - the fields are still top-level on
        // WaterVolume (only a C# rename), so the old values land here and are copied into the block above
        // exactly once by MigrateDepthAttenuationV1 (see OnAfterDeserialize). Hidden; do not edit.
        [SerializeField, HideInInspector, FormerlySerializedAs("depthDarken")] bool _legacyDepthDarken = false;
        [SerializeField, HideInInspector, FormerlySerializedAs("depthExtinction")] Color _legacyDepthExtinction = new Color(0.45f, 0.15f, 0.08f);
        [SerializeField, HideInInspector, FormerlySerializedAs("depthDarkenStrength")] float _legacyDepthDarkenStrength = 1f;
        [SerializeField, HideInInspector, FormerlySerializedAs("causticDepthFade")] float _legacyCausticDepthFade = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("godRayDepthFade")] float _legacyGodRayDepthFade = 0.5f;
        [SerializeField, HideInInspector, FormerlySerializedAs("linkDepthToFog")] bool _legacyLinkDepthToFog = false;

        // ---- settings migration (god-class -> per-feature nested Settings blocks) ------------------
        // Bumped by one for each feature whose flat fields move into a nested Settings block. A scene
        // serialized before a given version has its old (FormerlySerializedAs) legacy fields copied into
        // the new block once, on load, so tuned values are never lost. The copies are idempotent.
        const int CurrentSettingsVersion = 12;
        [SerializeField, HideInInspector] int _settingsVersion = 0;

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_settingsVersion >= CurrentSettingsVersion) return; // new or already-migrated asset
            if (_settingsVersion < 1) MigrateDepthAttenuationV1();
            if (_settingsVersion < 2) MigrateOceanV2();
            if (_settingsVersion < 3) MigrateWaterFogV3();
            if (_settingsVersion < 4) MigrateWindWavesV4();
            if (_settingsVersion < 5) MigrateFoamV5();
            if (_settingsVersion < 6) MigrateInteractionAndRippleV6();
            if (_settingsVersion < 7) MigrateReflectionsV7();
            if (_settingsVersion < 8) MigrateBedDepthV8();
            if (_settingsVersion < 9) MigrateBodyTypeV9();
            if (_settingsVersion < 10) MigrateSeaStateV10();
            if (_settingsVersion < 11) MigrateWindWaveRigV11();
            if (_settingsVersion < 12) MigrateOceanAmplitudeIntoMetresV12();
            _settingsVersion = CurrentSettingsVersion;
        }

        // v1: the "Depth attenuation (downwelling)" fields moved into DepthAttenuationSettings.
        void MigrateDepthAttenuationV1()
        {
            depthAttenuation.depthDarken = _legacyDepthDarken;
            depthAttenuation.depthExtinction = _legacyDepthExtinction;
            depthAttenuation.depthDarkenStrength = _legacyDepthDarkenStrength;
            depthAttenuation.causticDepthFade = _legacyCausticDepthFade;
            depthAttenuation.godRayDepthFade = _legacyGodRayDepthFade;
            depthAttenuation.linkDepthToFog = _legacyLinkDepthToFog;
        }

        // v2: the four "Ocean ..." headers (open water, clipmap, god rays, whitecaps) moved into OceanSettings.
        void MigrateOceanV2()
        {
            ocean.openWater = _legacyOpenWater;
            ocean.largeWaveAmplitude = _legacyLargeWaveAmplitude;
            ocean.largeWaveChoppiness = _legacyLargeWaveChoppiness;
            ocean.swellHeight = _legacySwellHeight;
            ocean.swellWavelength = _legacySwellWavelength;
            ocean.unboundedOcean = _legacyUnboundedOcean;
            ocean.clipmapOuterRadius = _legacyClipmapOuterRadius;
            ocean.oceanDetailFalloff = _legacyOceanDetailFalloff;
            ocean.horizonFadeDistance = _legacyHorizonFadeDistance;
            ocean.horizonHazeColor = _legacyHorizonHazeColor;
            ocean.horizonHazeDensity = _legacyHorizonHazeDensity;
            ocean.largeGodRayColor = _legacyLargeGodRayColor;
            ocean.largeGodRayDensity = _legacyLargeGodRayDensity;
            ocean.largeGodRaySteps = _legacyLargeGodRaySteps;
            ocean.largeGodRayAnisotropy = _legacyLargeGodRayAnisotropy;
            ocean.largeGodRayExtinction = _legacyLargeGodRayExtinction;
            ocean.largeGodRayCausticStrength = _legacyLargeGodRayCausticStrength;
            ocean.oceanFoamWindThreshold = _legacyOceanFoamWindThreshold;
            ocean.oceanFoamCoverage = _legacyOceanFoamCoverage;
            ocean.oceanFoamStrength = _legacyOceanFoamStrength;
            ocean.oceanFoamFadeRate = _legacyOceanFoamFadeRate;
            ocean.oceanFoamColor = _legacyOceanFoamColor;
            ocean.oceanFoamTileSize = _legacyOceanFoamTileSize;
            ocean.oceanFoamFeather = _legacyOceanFoamFeather;
        }

        // v10: the FFT ocean moved from a wind-only Phillips spectrum to JONSWAP/TMA authored as
        // (Significant Height, Peak Wavelength, Peak Sharpness). There is nothing to copy - the old
        // parameterisation had no height and no wavelength to copy FROM - so this migrates the one field
        // whose MEANING changed: choppiness.
        //
        // Chop used to reach the analytic generator only; the FFT path ran a hardwired 1.0 whatever the
        // slider said. Now that the slider is live on both, a scene that left it at the old 0 default
        // would suddenly render round sine humps where it used to have crests. Lifting a stored 0 to 1
        // preserves what the ocean actually looked like. A deliberately authored non-zero value is left
        // exactly as it is.
        void MigrateSeaStateV10()
        {
            if (ocean.largeWaveChoppiness <= 0f) ocean.largeWaveChoppiness = DefaultLargeWaveChoppiness;
        }

        // v12: `largeWaveAmplitude` stopped being drawn when the FFT sea state moved to honest metres
        // (Significant Height / Swell Height, normalised by WaterOceanSpectrum), but it was left LIVE in
        // the runtime - a multiplier on the whole FFT field that no inspector showed. A scene carrying a
        // non-1 value therefore renders every authored metre scaled by an invisible factor, and the
        // author compensates by inflating the heights until it looks right. Worse, the factor does NOT
        // reach every consumer: the analytic swell band and the steepness readout skip it, so one knob
        // produced several different numbers (see docs/PLAN_buoyancy_swell_v1.md).
        //
        // Folding it into the two authored heights is EXACT for an FFT ocean - the spectrum scales
        // linearly in both - so the sea renders identically while the numbers finally read as metres.
        //
        // BOUNDED open water is deliberately left alone: it has no spectrum, so `largeWaveAmplitude` is
        // the ONLY height control its analytic chop band has (and it carries the wind coupling there).
        // Folding it into Significant Height on those bodies would move a value the analytic path never
        // reads, silently flattening them.
        void MigrateOceanAmplitudeIntoMetresV12()
        {
            if (!ocean.openWater || !ocean.unboundedOcean) return;
            float amplitude = ocean.largeWaveAmplitude;
            // 0 is "deliberately flat water" and is NOT foldable: multiplying the heights by it would
            // destroy them, and neutralising the multiplier afterwards would then un-flatten the sea.
            // Such a body keeps its 0 and is caught by the inspector's retired-multiplier warning.
            if (amplitude <= 0f || Mathf.Approximately(amplitude, NeutralOceanAmplitude)) return;
            ocean.significantWaveHeight *= amplitude;
            ocean.swellHeight *= amplitude;
            ocean.largeWaveAmplitude = NeutralOceanAmplitude;
        }

        // The value at which the retired multiplier is a no-op, i.e. "the authored metres are the
        // rendered metres". Named so the migration above cannot be read as an arbitrary reset to 1.
        const float NeutralOceanAmplitude = 1f;

        // v11: the small wind-wave layer moved from (wind speed, fetch, amplitude scale) to an
        // authored (length, height) rig - see WaterWaveBank's header for why the old three could not
        // express what they claimed. The legacy fields are still serialized at this point, so the
        // scene's ACTUAL rendered look is recoverable rather than guessed: WaterWaveBank exposes the
        // closed forms of what the old path really produced, and they are copied straight across.
        //
        void MigrateWindWaveRigV11()
        {
            windWaveSettings.waveHeightMeters = WaterWaveBank.LegacySignificantHeight(
                windWaveSettings.windSpeed, windWaveSettings.legacyAmplitudeScale);
            windWaveSettings.waveLengthMeters = WaterWaveBank.LegacyWavelength(
                windWaveSettings.windSpeed, windWaveSettings.legacyFetchMeters);
            // The old layer had neither, and both are shape-only, so starting them at zero keeps a
            // migrated scene byte-identical until the sliders are touched.
            windWaveSettings.waveGrouping = 0f;
            windWaveSettings.waveCrestSharpness = 0f;
        }

        // v3: the "Water fog (Beer-Lambert)" fields moved into WaterFogSettings.
        void MigrateWaterFogV3()
        {
            waterFogSettings.waterFog = _legacyWaterFog;
            waterFogSettings.fogColor = _legacyFogColor;
            waterFogSettings.fogExtinction = _legacyFogExtinction;
            waterFogSettings.fogDensity = _legacyFogDensity;
            waterFogSettings.waterOpacity = _legacyWaterOpacity;
        }

        // v4: the "Wind waves (spectral)" fields moved into WindWaveSettings.
        void MigrateWindWavesV4()
        {
            windWaveSettings.windWaves = _legacyWindWaves;
            windWaveSettings.windSpeed = _legacyWindSpeed;
            windWaveSettings.windFromDegrees = _legacyWindFromDegrees;
            windWaveSettings.legacyFetchMeters = _legacyPoolHalfExtentMeters;
            windWaveSettings.waveCount = _legacyWaveCount;
            windWaveSettings.legacyAmplitudeScale = _legacyWaveAmplitudeScale;
            windWaveSettings.waveDirectionSpread = _legacyWaveDirectionSpread;
            windWaveSettings.waveNormalStrength = _legacyWaveNormalStrength;
        }

        // v5: the "Foam" fields (pool/interactive surface foam) moved into FoamSettings.
        void MigrateFoamV5()
        {
            foamSettings.foam = _legacyFoam;
            foamSettings.foamGenRate = _legacyFoamGenRate;
            foamSettings.foamDecay = _legacyFoamDecay;
            foamSettings.foamDecayRate = _legacyFoamDecayRate;
            foamSettings.foamSpread = _legacyFoamSpread;
            foamSettings.foamAdvect = _legacyFoamAdvect;
            foamSettings.foamFromSpeed = _legacyFoamFromSpeed;
            foamSettings.foamFromCurvature = _legacyFoamFromCurvature;
            foamSettings.foamColor = _legacyFoamColor;
            foamSettings.foamStrength = _legacyFoamStrength;
            foamSettings.foamFeather = _legacyFoamFeather;
            foamSettings.foamCoreCut = _legacyFoamCoreCut;
            foamSettings.foamBorderWidth = _legacyFoamBorderWidth;
            foamSettings.foamContactDepth = _legacyFoamContactDepth;
        }

        // v6: the "Object interaction" and "Ripple tuning" fields moved into their nested Settings blocks.
        void MigrateInteractionAndRippleV6()
        {
            objectInteractionSettings.objectInteraction = _legacyObjectInteraction;
            objectInteractionSettings.obstacleStrength = _legacyObstacleStrength;
            objectInteractionSettings.obstacleDeadband = _legacyObstacleDeadband;
            objectInteractionSettings.obstacleSmoothing = _legacyObstacleSmoothing;
            objectInteractionSettings.obstacleFlipY = _legacyObstacleFlipY;

            rippleSettings.waveSpeed = _legacyWaveSpeed;
            rippleSettings.damping = _legacyDamping;
            rippleSettings.stepsPerFrame = _legacyStepsPerFrame;
            rippleSettings.rippleStrength = _legacyRippleStrength;
            rippleSettings.rippleRadius = _legacyRippleRadius;
            rippleSettings.seedRipplesOnStart = _legacySeedRipplesOnStart;
            rippleSettings.conserveVolume = _legacyConserveVolume;
            rippleSettings.conserveMaxCorrection = _legacyConserveMaxCorrection;
        }

        // v7: the "Reflections" fields (reflection mode + base environment) moved into ReflectionSettings.
        void MigrateReflectionsV7()
        {
            // Map the retired SkyOnly/SSR/Planar enum onto the independent toggles.
            reflectionSettings.useScreenSpaceReflection = _legacyReflectionMode == ReflectionMode.SSR;
            reflectionSettings.usePlanarReflection = _legacyReflectionMode == ReflectionMode.Planar;
            reflectionSettings.reflectUrpProbe = _legacyEnvironmentSource == EnvironmentSource.UrpProbe;
        }

        // v8: the "Bed depth (real terrain depth)" fields moved into BedDepthSettings.
        void MigrateBedDepthV8()
        {
            bedDepthSettings.useBedDepth = _legacyUseBedDepth;
            bedDepthSettings.bedTerrain = _legacyBedTerrain;
            bedDepthSettings.bedResolution = _legacyBedResolution;
            bedDepthSettings.deepWaterColor = _legacyDeepWaterColor;
            bedDepthSettings.bedFadeDepth = _legacyShorelineFadeDepth;
            bedDepthSettings.bedTintStrength = _legacyShorelineStrength;
        }

        // v9: infer the advisory body archetype for bodies authored before the WaterBodyType field
        // existed, so their inspector opens on the right type. Unbounded = Ocean, open water = Lake,
        // else Pond. Advisory only; the user can re-pick.
        void MigrateBodyTypeV9()
        {
            bodyType = ocean.unboundedOcean ? WaterBodyType.Ocean
                     : ocean.openWater      ? WaterBodyType.Lake
                     :                         WaterBodyType.Pond;
        }

        // Editor-only: a freshly added component starts already-migrated, so the one-time copy never runs
        // on new bodies. Only assets serialized before a feature existed (no _settingsVersion -> 0) migrate.
        // (Distinguishing new from pre-migration data is exactly what a field initializer cannot do.)
        void Reset() => _settingsVersion = CurrentSettingsVersion;
    }
}
