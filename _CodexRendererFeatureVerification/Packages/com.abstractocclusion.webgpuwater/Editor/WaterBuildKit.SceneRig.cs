// WebGpuWater build kit - the scene rig around the water: camera, sun and the splash FX hierarchy.
// Scene furniture, not water: a body works without any of it.
using System.IO;
using InvalidOperationException = System.InvalidOperationException;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- scene rig
        // Reuse the scene's main camera if there is one (avoids two cameras rendering on top of each
        // other), then attach the orbit helper.
        internal static Camera SetUpCamera(out OrbitCamera orbit)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var camGO = NewUndoableGameObject("Water Camera");
                cam = camGO.AddComponent<Camera>();
                camGO.tag = MainCameraTag;
            }
            // Leave the camera's clear flags / background (skybox) and far clip alone: forcing a solid
            // black clear and a 100 m far plane clipped the user's scene. Only the framing (fov/near) is
            // set - recorded, because the camera may be the USER'S pre-existing one.
            Undo.RecordObject(cam, "Frame Water Camera");
            cam.fieldOfView = WaterVolume.CameraFieldOfView;
            cam.nearClipPlane = WaterVolume.CameraNearClip;

            orbit = cam.GetComponent<OrbitCamera>();
            if (orbit == null) orbit = Undo.AddComponent<OrbitCamera>(cam.gameObject);
            else Undo.RecordObject(orbit, "Frame Water Camera");
            orbit.pivot = DemoOrbitPivot;
            orbit.pitch = DemoOrbitPitch;
            orbit.yaw = DemoOrbitYaw;
            orbit.distance = DemoOrbitDistance;
            // No PlanarReflection component here: per-body planar mirrors (WaterVolume.RenderPlanarMirror)
            // supersede the global camera-attached reflection, so attaching it (disabled) was dead weight.
            return cam;
        }

        // Single directional light: drives the analytic water + caustics (via the _LightDir global
        // the controller publishes) AND casts real URP shadows.
        internal static Light CreateSun(Transform parent)
        {
            var sunGO = NewUndoableGameObject("Sun");
            sunGO.transform.SetParent(parent);
            var sun = sunGO.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.shadows = LightShadows.Soft;
            sun.intensity = DefaultSunIntensity;
            sun.transform.rotation = Quaternion.LookRotation(-DefaultSunTowardLight.normalized);
            return sun;
        }

        // Hierarchy names for the splash feature: ONE root GO holding the emitter, with
        // both particle systems as clearly-labelled children (the old flat siblings
        // "Splash Particles"/"Splash Crown" read as two unrelated features).
        internal const string SplashRootName = "Water Splash FX";
        internal const string SplashDropletChildName = "Droplet Spray (CPU Fallback)";
        internal const string SplashJetChildName = "Vertical Entry Jets";
        const string LegacySplashJetChildName = "Streak Jets";
        internal const string SplashCrownChildName = "Crown Ring";

        // Instantiate the packaged hierarchy instead of authoring loose Shuriken systems in every
        // scene. The Asset Store validator requires ParticleSystems to belong to a Prefab; renderer
        // materials remain per-water overrides so artists keep the existing editable workflow.
        internal static WaterSplashEmitter CreateSplashEmitter(Transform parent, string materialFolder)
        {
            GameObject prefab = LoadRequiredDefault<GameObject>(SplashEmitterPrefabPath,
                "splash particle prefab");
            if (prefab == null) return null;

            var rootGO = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
            if (rootGO == null)
            {
                Debug.LogError(LogPrefix + $"could not instantiate splash prefab '{SplashEmitterPrefabPath}'.");
                return null;
            }

            Undo.RegisterCreatedObjectUndo(rootGO, SplashRootName);
            rootGO.transform.SetParent(parent, false);
            var splashEmitter = rootGO.GetComponent<WaterSplashEmitter>();
            if (splashEmitter == null)
            {
                Debug.LogError(LogPrefix + $"splash prefab '{SplashEmitterPrefabPath}' has no " +
                    $"{nameof(WaterSplashEmitter)} component.");
                Undo.DestroyObjectImmediate(rootGO);
                return null;
            }

            ApplySplashMaterials(splashEmitter, materialFolder);
            return splashEmitter;
        }

        internal static GameObject CreateOrReplaceSplashEmitterPrefab()
        {
            EnsureFolder(DefaultMaterialsRoot);
            EnsureFolder(DefaultPrefabsRoot);

            Material dropletMaterial = LoadOrCreateSplashMaterial(DefaultSplashDropletMaterialPath,
                LoadRequiredDefault<Texture2D>(DropletTexturePath, "packed splash droplet texture"));
            Material crownMaterial = CreateOrUpgradeCrownMaterial(DefaultMaterialsRoot);
            WaterSplashEmitter splashEmitter = BuildSplashEmitterHierarchy();

            try
            {
                AssignSplashMaterials(splashEmitter, dropletMaterial, crownMaterial);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(
                    splashEmitter.gameObject, SplashEmitterPrefabPath, out bool savedSuccessfully);
                if (!savedSuccessfully || prefab == null)
                    throw new InvalidOperationException(
                        $"Could not save splash prefab at '{SplashEmitterPrefabPath}'.");
                return prefab;
            }
            finally
            {
                Object.DestroyImmediate(splashEmitter.gameObject);
            }
        }

        static WaterSplashEmitter BuildSplashEmitterHierarchy()
        {
            var rootGO = new GameObject(SplashRootName);
            var splashEmitter = rootGO.AddComponent<WaterSplashEmitter>();

            var splashGO = new GameObject(SplashDropletChildName);
            splashGO.transform.SetParent(rootGO.transform, false);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            splashEmitter.particles = splashPS;

            var jetGO = new GameObject(SplashJetChildName);
            jetGO.transform.SetParent(rootGO.transform, false);
            var jetPS = jetGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureJets(jetPS, CrownSheetCols, CrownSheetRows);
            splashEmitter.jetParticles = jetPS;

            var crownGO = new GameObject(SplashCrownChildName);
            crownGO.transform.SetParent(rootGO.transform, false);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, CrownSheetCols, CrownSheetRows);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            // Free-rotating billboards: each chunk sprite spawns at a random angle and
            // tumbles (KWS droplet cloud). The old vertical-billboard bottom pivot belonged
            // to the single crown card this system used to be.
            crownPSR.renderMode = ParticleSystemRenderMode.Billboard;
            crownPSR.pivot = Vector3.zero;
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;
        }

        static void ApplySplashMaterials(WaterSplashEmitter splashEmitter, string materialFolder)
        {
            Material dropletMaterial = LoadOrCreateSplashMaterial(
                materialFolder + "/SplashDroplet.mat",
                LoadRequiredDefault<Texture2D>(DropletTexturePath, "packed splash droplet texture"));
            Material crownMaterial = CreateOrUpgradeCrownMaterial(materialFolder);
            AssignSplashMaterials(splashEmitter, dropletMaterial, crownMaterial);
        }

        static void AssignSplashMaterials(WaterSplashEmitter splashEmitter, Material dropletMaterial,
                                          Material crownMaterial)
        {
            if (splashEmitter == null) return;

            if (splashEmitter.particles != null)
            {
                var dropletRenderer = splashEmitter.particles.GetComponent<ParticleSystemRenderer>();
                if (dropletRenderer != null) dropletRenderer.sharedMaterial = dropletMaterial;
            }
            if (splashEmitter.jetParticles != null)
            {
                var jetRenderer = splashEmitter.jetParticles.GetComponent<ParticleSystemRenderer>();
                if (jetRenderer != null) jetRenderer.sharedMaterial = crownMaterial;
            }
            if (splashEmitter.crownParticles != null)
            {
                var crownRenderer = splashEmitter.crownParticles.GetComponent<ParticleSystemRenderer>();
                if (crownRenderer != null) crownRenderer.sharedMaterial = crownMaterial;
            }
        }

        internal static void EnsureSplashPrefabHierarchy(WaterSplashEmitter splashEmitter)
        {
            if (splashEmitter == null) return;

            Material crownMaterial = null;
            if (splashEmitter.crownParticles != null)
            {
                var crownRenderer = splashEmitter.crownParticles.GetComponent<ParticleSystemRenderer>();
                if (crownRenderer != null) crownMaterial = crownRenderer.sharedMaterial;
            }
            if (crownMaterial == null)
                crownMaterial = AssetDatabase.LoadAssetAtPath<Material>(DefaultSplashCrownMaterialPath);

            EnsureJetLayer(splashEmitter, crownMaterial);
        }

        // Upgrade the shared splash materials and retrofit the independent jet layer onto
        // existing emitters in the open scene. New emitters receive it in CreateSplashEmitter.
        internal static void UpgradeSplashMaterials()
        {
            foreach (WaterSplashEmitter emitter in Object.FindObjectsByType<WaterSplashEmitter>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                string materialFolder = ResolveMaterialFolder(emitter);
                Material dropletMaterial = LoadOrCreateSplashMaterial(materialFolder + "/SplashDroplet.mat",
                    LoadRequiredDefault<Texture2D>(DropletTexturePath, "packed splash droplet texture"));
                Material crownMaterial = CreateOrUpgradeCrownMaterial(materialFolder);
                if (emitter.particles != null)
                {
                    var dropletRenderer = emitter.particles.GetComponent<ParticleSystemRenderer>();
                    if (dropletRenderer != null) dropletRenderer.sharedMaterial = dropletMaterial;
                }
                EnsureJetLayer(emitter, crownMaterial);
                UpgradeCrownLayer(emitter, crownMaterial);
            }
            AssetDatabase.SaveAssets();
        }

        // The editor-only retrofit is explicit: it never adds objects at play time, and does
        // not overwrite an artist-assigned jet system.
        static ParticleSystem EnsureJetLayer(WaterSplashEmitter emitter, Material crownMaterial)
        {
            if (emitter == null) return null;
            if (emitter.jetParticles != null) return emitter.jetParticles;

            Transform existingJet = emitter.transform.Find(SplashJetChildName);
            if (existingJet == null)
            {
                existingJet = emitter.transform.Find(LegacySplashJetChildName);
                if (existingJet != null)
                    existingJet.name = SplashJetChildName;
            }
            ParticleSystem jetParticles = existingJet != null
                ? existingJet.GetComponent<ParticleSystem>()
                : null;
            if (jetParticles == null)
            {
                var jetGO = NewUndoableGameObject(SplashJetChildName);
                jetGO.transform.SetParent(emitter.transform, false);
                jetParticles = jetGO.AddComponent<ParticleSystem>();
                WaterSplashEmitter.ConfigureJets(jetParticles, CrownSheetCols, CrownSheetRows);
            }

            var jetRenderer = jetParticles.GetComponent<ParticleSystemRenderer>();
            if (jetRenderer != null && crownMaterial != null)
                jetRenderer.sharedMaterial = crownMaterial;
            Undo.RecordObject(emitter, "Add Splash Entry Jets");
            emitter.jetParticles = jetParticles;
            EditorUtility.SetDirty(emitter);
            return jetParticles;
        }

        // The old crown used an 8x8 procedural sheet. Switching only its material would
        // sample invalid cells, so this explicit upgrade changes the sheet layout with it.
        static void UpgradeCrownLayer(WaterSplashEmitter emitter, Material crownMaterial)
        {
            if (emitter == null || emitter.crownParticles == null || crownMaterial == null) return;

            WaterSplashEmitter.ConfigureCrown(emitter.crownParticles, CrownSheetCols, CrownSheetRows);
            var crownRenderer = emitter.crownParticles.GetComponent<ParticleSystemRenderer>();
            if (crownRenderer != null) crownRenderer.sharedMaterial = crownMaterial;
            EditorUtility.SetDirty(emitter.crownParticles);
        }

        // The crown material: the packed photographic chunk atlas (KWS WaterSplash
        // construction) + backlit transmission, which reads the atlas' thickness channel.
        // Doubles as the one-click upgrade for crown materials created on the old 8x8
        // procedural flipbook: the texture is swapped to the canonical packed atlas.
        static Material CreateOrUpgradeCrownMaterial(string materialFolder)
        {
            var material = LoadOrCreateSplashMaterial(materialFolder + "/SplashCrown.mat",
                LoadRequiredDefault<Texture2D>(SplashCrownSheetPath, "splash crown sheet"));
            if (material == null) return null;

            if (material.HasProperty(TransmissionStrengthProperty) &&
                Mathf.Approximately(material.GetFloat(TransmissionStrengthProperty), 0f))
            {
                material.SetFloat(TransmissionStrengthProperty, DefaultCrownTransmission);
            }
            EditorUtility.SetDirty(material);
            return material;
        }

        static string ResolveMaterialFolder(WaterSplashEmitter emitter)
        {
            if (emitter != null && emitter.crownParticles != null)
            {
                var renderer = emitter.crownParticles.GetComponent<ParticleSystemRenderer>();
                string materialPath = renderer != null
                    ? AssetDatabase.GetAssetPath(renderer.sharedMaterial)
                    : null;
                if (!string.IsNullOrEmpty(materialPath) && materialPath.StartsWith(ProjectAssetsPrefix))
                    return Path.GetDirectoryName(materialPath).Replace('\\', '/');
            }

            string waterFolder = CreateUniqueWaterFolder();
            string materialsFolder = MaterialsFolder(waterFolder);
            EnsureFolder(materialsFolder);
            return materialsFolder;
        }

        // A splash material on the lit shader (create-once). Also the one-click upgrade
        // path for materials created before the lit shader existed: an existing material
        // still on another shader is switched in place, keeping its texture.
        static Material LoadOrCreateSplashMaterial(string path, Texture2D sprite)
        {
            var shader = Shader.Find(ShaderSplashParticles);
            if (shader == null)
            {
                Debug.LogWarning($"WebGpuWater: shader '{ShaderSplashParticles}' missing; splash material not created.");
                return null;
            }

            var material = LoadOrCreateMaterial(path, shader, m =>
            {
                if (sprite != null) m.mainTexture = sprite;
            });
            if (material.shader != shader)
            {
                material.shader = shader; // upgrade in place; _MainTex carries over by name
                EditorUtility.SetDirty(material);
            }
            // This creator is only ever handed the KWS-packed textures now, so force both the
            // texture and the packed-channel flag every call: it doubles as the one-click
            // upgrade for materials created before the packed format existed.
            if (sprite != null && material.mainTexture != sprite)
            {
                material.mainTexture = sprite;
                EditorUtility.SetDirty(material);
            }
            const string PackedChannelsProperty = "_PackedChannels";
            if (material.HasProperty(PackedChannelsProperty) &&
                !Mathf.Approximately(material.GetFloat(PackedChannelsProperty), 1f))
            {
                material.SetFloat(PackedChannelsProperty, 1f);
                EditorUtility.SetDirty(material);
            }
            return material;
        }

    }
}
