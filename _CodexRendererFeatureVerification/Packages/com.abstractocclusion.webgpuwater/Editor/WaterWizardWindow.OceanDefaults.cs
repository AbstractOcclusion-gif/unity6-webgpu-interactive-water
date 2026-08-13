// WebGpuWater - wizard: the default OPEN-WATER OCEAN look.
//
// Values lifted from the tuned beach-demo ocean (Bert's reference scene, 2026-08-08), rounded
// to tidy numbers, minus everything shore-bound (bed terrain, surf engine, clarity-from-depth)
// and minus scene wiring. ApplyLookDefaults (the generic baseline every kind gets) runs first;
// this pass then overrides it for the Ocean kind only, so the other kinds stay byte-identical.
// The Jerlov Ocean I water colour rides the shared WaterJerlovLookWriter - the same writer the
// inspector's "Apply water colour" button uses, so the two can never drift apart.
//
// Property paths written only here stay inline (the registry's own scope rule: single-use
// paths live at their use site); a stale path fails FAST and LOUD through RequireProperty at
// the Create click, naming the missing path instead of NRE-ing anonymously.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterWizardWindow
    {
        // ---- default ocean look (rounded beach-demo values) ------------------
        static readonly Vector3 OceanDefaultExtent = new Vector3(500f, 100f, 500f);

        // Spectrum: a 3 m organised sea with a long half-metre swell. Sea depth stays at the
        // class default 0 (deep water, no shoaling correction) by Bert's call.
        const float OceanSignificantWaveHeight = 3f;
        const float OceanPeakWavelengthMeters = 90f;
        const float OceanCascadeReachDefault = 8f;
        const float OceanSwellHeightMeters = 0.5f;
        const float OceanWindTurbulenceDefault = 0.15f;
        const float OceanHorizonHazeDensity = 0.85f;

        // God rays: calm cyan shafts. The depth fade deliberately stays at the class default
        // (0.5/m, beams die by ~10 m): the wizard used to force 0.05/m for deep-plunging beams,
        // retired with the density drop to 0.3 - trust the tuned reference scene instead.
        static readonly Color OceanGodRayColor = new Color(0f, 0.75f, 1f);
        const float OceanGodRayDensity = 0.3f;

        // Whitecaps: denser, softer, less streak-stretched than the class defaults.
        const float OceanFoamWindThreshold = 3f;
        const float OceanFoamCoverage = 0.9f;
        const float OceanFoamStrength = 1.5f;
        const float OceanFoamFeather = 0.7f;
        const float OceanFoamStreakStretch = 1.7f;
        const float OceanFoamTextureInfluence = 0.8f;
        const float OceanFoamFaceBias = 0.85f;
        const float OceanFoamDrift = 0.2f;

        // Volume: point/spot light scatter in the fog; refract shadows ON with a tight penumbra
        // (the generic baseline turns them off - the ocean reference look wants them).
        const float OceanLightScatter = 0.5f;
        const float OceanRefractShadowSoftness = 0.05f;

        // Surface reflections: a dim mirror with a raised fresnel floor.
        const float OceanReflectionStrength = 0.15f;
        const float OceanEnvReflectionIntensity = 0.3f;
        const float OceanFresnelFloor = 0.25f;
        const float OceanFresnelPower = 3.3f;
        const float OceanPlanarClipDepth = 0.007f;

        // Detail normals: finer near tile; far layer pushed out and sped up; hex tiling on.
        const float OceanDetailNormalStrength = 0.5f;
        const float OceanDetailTileMeters = 8f;
        const float OceanDetailFarTileMeters = 250f;
        const float OceanDetailFarTileDistance = 700f;
        const float OceanDetailFarScrollSpeed = 3f;

        // Underside look: dim mirror blended toward the water colour, visible shafts, strong
        // underside detail normals.
        const float OceanUnderReflectionStrength = 0.15f;
        const float OceanUnderMirrorWaterBlend = 0.8f;
        const float OceanUnderMirrorShafts = 0.25f;
        const float OceanUnderDetailNormalStrength = 1.3f;

        // Interactive sim foam (wake/turbulence): tuned generation thresholds and a long
        // wetness memory. Values are always written; they only take effect when the wizard's
        // Foam toggle rigs the feature.
        const float OceanSimFoamDecayRate = 1.25f;
        const float OceanSimFoamWetnessDryTime = 30f;
        const float OceanSimFoamGenThreshold = 0.35f;
        const float OceanSimFoamMinWaveHeight = 0.09f;
        const float OceanSimFoamDeposit = 0.45f;
        const float OceanSimFoamBreakStrength = 0.4f;
        const float OceanSimFoamCrestBias = 0.7f;
        const float OceanSimFoamHeadroom = 1f;
        const float OceanSimFoamPatternSize = 5f;
        const float OceanSimFoamStrength = 0.5f;
        const float OceanSimFoamFeather = 0.5f;
        const float OceanSimFoamCoreCut = 0.1f;

        // Selecting the Ocean kind prefills the size and reflection mode with the default-ocean
        // choices - only while they still hold the untouched defaults, so a hand-entered value
        // is never stomped. Leaving Ocean restores the small default size the same way.
        void ApplyKindPrefills(WaterKind previousKind)
        {
            bool becameOcean = _kind == WaterKind.OpenWaterOcean;
            bool leftOcean = previousKind == WaterKind.OpenWaterOcean && !becameOcean;

            if (becameOcean && _extent == DefaultExtent)
                _extent = OceanDefaultExtent;
            if (leftOcean && _extent == OceanDefaultExtent)
                _extent = DefaultExtent;
            if (becameOcean && _reflectionMode == WaterVolume.ReflectionMode.SSR)
                _reflectionMode = WaterVolume.ReflectionMode.Planar;
        }

        // The beach-derived ocean look. Runs AFTER ApplyLookDefaults, so the generic values it
        // overrides (fog density, detail strength, refract-shadows off) are overridden on purpose.
        void ApplyOceanLookDefaults(WaterVolume body, bool withGodRays)
        {
            // Internal fields (InternalsVisibleTo), same direct path as ApplyLookDefaults.
            body.refractShadows = true;
            body.refractShadowSoftness = OceanRefractShadowSoftness;

            var serialized = new SerializedObject(body);
            WriteOceanSpectrum(serialized);
            WriteOceanWhitecaps(serialized);
            WriteOceanWaterColourAndVolume(serialized);
            WriteOceanReflections(serialized);
            WriteOceanDetailNormals(serialized);
            WriteOceanUndersideSurface(serialized);
            WriteOceanSimFoam(serialized);
            if (withGodRays)
                WriteOceanGodRays(serialized);
            serialized.ApplyModifiedProperties(); // rides the Create Water undo group
        }

        static void WriteOceanSpectrum(SerializedObject serialized)
        {
            RequireProperty(serialized, WaterVolumePropertyPaths.AmbientWindReferenceSpeed).floatValue =
                RequireProperty(serialized, WaterVolumePropertyPaths.WindSpeed).floatValue;
            RequireProperty(serialized, WaterVolumePropertyPaths.SignificantWaveHeight).floatValue = OceanSignificantWaveHeight;
            RequireProperty(serialized, WaterVolumePropertyPaths.PeakWavelength).floatValue = OceanPeakWavelengthMeters;
            RequireProperty(serialized, "ocean.cascadeReach").floatValue = OceanCascadeReachDefault;
            RequireProperty(serialized, WaterVolumePropertyPaths.SwellHeight).floatValue = OceanSwellHeightMeters;
            RequireProperty(serialized, WaterVolumePropertyPaths.OceanWindTurbulence).floatValue = OceanWindTurbulenceDefault;
            RequireProperty(serialized, WaterVolumePropertyPaths.HorizonHazeDensity).floatValue = OceanHorizonHazeDensity;
            // The FFT owns the whole ocean surface; the small analytic wind-wave layer doubles it up.
            RequireProperty(serialized, "windWaveSettings.windWaves").boolValue = false;
        }

        static void WriteOceanWhitecaps(SerializedObject serialized)
        {
            RequireProperty(serialized, "ocean.oceanFoamWindThreshold").floatValue = OceanFoamWindThreshold;
            RequireProperty(serialized, "ocean.oceanFoamCoverage").floatValue = OceanFoamCoverage;
            RequireProperty(serialized, "ocean.oceanFoamStrength").floatValue = OceanFoamStrength;
            RequireProperty(serialized, "ocean.oceanFoamFeather").floatValue = OceanFoamFeather;
            RequireProperty(serialized, "ocean.oceanFoamStreakStretch").floatValue = OceanFoamStreakStretch;
            RequireProperty(serialized, "ocean.oceanFoamTextureInfluence").floatValue = OceanFoamTextureInfluence;
            RequireProperty(serialized, "ocean.oceanFoamFaceBias").floatValue = OceanFoamFaceBias;
            RequireProperty(serialized, "ocean.oceanFoamDrift").floatValue = OceanFoamDrift;
        }

        static void WriteOceanWaterColourAndVolume(SerializedObject serialized)
        {
            // The same writer as the inspector's "Apply water colour" button: fog + scatter
            // colour from the physical Ocean I coefficients, tuned density/opacity/intensity,
            // stored water-type reference, Water Fog enabled.
            WaterJerlovLookWriter.Write(serialized, JerlovWaterType.OceanI);
            RequireProperty(serialized, WaterVolumePropertyPaths.VolumeScatter).boolValue = true;
            RequireProperty(serialized, "waterFogSettings.lightScatter").floatValue = OceanLightScatter;
            RequireProperty(serialized, "depthAttenuation.depthDarken").boolValue = true;
            RequireProperty(serialized, "depthAttenuation.screenSpaceCaustics").boolValue = true;
        }

        static void WriteOceanReflections(SerializedObject serialized)
        {
            RequireProperty(serialized, "reflectionSettings.reflectionStrength").floatValue = OceanReflectionStrength;
            RequireProperty(serialized, "reflectionSettings.envReflectionIntensity").floatValue = OceanEnvReflectionIntensity;
            RequireProperty(serialized, "reflectionSettings.fresnelFloor").floatValue = OceanFresnelFloor;
            RequireProperty(serialized, "reflectionSettings.fresnelPower").floatValue = OceanFresnelPower;
            RequireProperty(serialized, "reflectionSettings.planarClipDepth").floatValue = OceanPlanarClipDepth;
        }

        static void WriteOceanDetailNormals(SerializedObject serialized)
        {
            RequireProperty(serialized, WaterVolumePropertyPaths.DetailNormalStrength).floatValue = OceanDetailNormalStrength;
            RequireProperty(serialized, "detailNormalSettings.tileMeters").floatValue = OceanDetailTileMeters;
            RequireProperty(serialized, "detailNormalSettings.farTileMeters").floatValue = OceanDetailFarTileMeters;
            RequireProperty(serialized, "detailNormalSettings.farTileDistance").floatValue = OceanDetailFarTileDistance;
            RequireProperty(serialized, "detailNormalSettings.farScrollSpeed").floatValue = OceanDetailFarScrollSpeed;
            RequireProperty(serialized, "detailNormalSettings.hexTiling").boolValue = true;
        }

        static void WriteOceanUndersideSurface(SerializedObject serialized)
        {
            RequireProperty(serialized, "underwaterSurfaceSettings.reflectionStrength").floatValue = OceanUnderReflectionStrength;
            RequireProperty(serialized, "underwaterSurfaceSettings.mirrorWaterBlend").floatValue = OceanUnderMirrorWaterBlend;
            RequireProperty(serialized, "underwaterSurfaceSettings.mirrorShafts").floatValue = OceanUnderMirrorShafts;
            RequireProperty(serialized, "underwaterSurfaceSettings.detailNormalStrength").floatValue = OceanUnderDetailNormalStrength;
        }

        static void WriteOceanSimFoam(SerializedObject serialized)
        {
            RequireProperty(serialized, "foamSettings.foamDecayRate").floatValue = OceanSimFoamDecayRate;
            RequireProperty(serialized, "foamSettings.wetnessMemory").boolValue = true;
            RequireProperty(serialized, "foamSettings.wetnessDryTime").floatValue = OceanSimFoamWetnessDryTime;
            RequireProperty(serialized, "foamSettings.foamGenThreshold").floatValue = OceanSimFoamGenThreshold;
            RequireProperty(serialized, "foamSettings.foamMinWaveHeight").floatValue = OceanSimFoamMinWaveHeight;
            RequireProperty(serialized, "foamSettings.foamDeposit").floatValue = OceanSimFoamDeposit;
            RequireProperty(serialized, "foamSettings.foamBreakStrength").floatValue = OceanSimFoamBreakStrength;
            RequireProperty(serialized, "foamSettings.foamCrestBias").floatValue = OceanSimFoamCrestBias;
            RequireProperty(serialized, "foamSettings.foamHeadroom").floatValue = OceanSimFoamHeadroom;
            RequireProperty(serialized, "foamSettings.foamPatternSize").floatValue = OceanSimFoamPatternSize;
            RequireProperty(serialized, "foamSettings.foamStrength").floatValue = OceanSimFoamStrength;
            RequireProperty(serialized, "foamSettings.foamFeather").floatValue = OceanSimFoamFeather;
            RequireProperty(serialized, "foamSettings.foamCoreCut").floatValue = OceanSimFoamCoreCut;
        }

        static void WriteOceanGodRays(SerializedObject serialized)
        {
            RequireProperty(serialized, "ocean.largeGodRayColor").colorValue = OceanGodRayColor;
            RequireProperty(serialized, WaterVolumePropertyPaths.LargeGodRayDensity).floatValue = OceanGodRayDensity;
        }

        // A stale path here must fail loudly at the Create click, naming the path - not NRE
        // anonymously. This guard is what keeps the inline single-use paths above safe from
        // silent drift.
        static SerializedProperty RequireProperty(SerializedObject serialized, string path)
        {
            SerializedProperty property = serialized.FindProperty(path);
            if (property == null)
                throw new System.InvalidOperationException(
                    "[WebGpuWater] Wizard ocean defaults: serialized path '" + path +
                    "' not found on WaterVolume (field renamed?).");
            return property;
        }
    }
}
