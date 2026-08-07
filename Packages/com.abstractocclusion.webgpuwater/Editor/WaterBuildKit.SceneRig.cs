// WebGpuWater build kit - the scene rig around the water: camera, sun and the splash FX hierarchy.
// Scene furniture, not water: a body works without any of it.
using System.IO;
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
        internal const string SplashCrownChildName = "Crown Ring";

        // Shared, fully editable splash particles (drift droplets + a flipbook crown).
        // Materials are create-once assets on the lit splash shader, so hand-tuning
        // survives rebuilds (same convention as the water/foam-particle materials).
        // One root so the hierarchy reads as a single feature: the emitter on the root
        // drives both children. "Droplet Spray" is the CPU fallback - bodies with an
        // active GPU WaterFoamParticles route droplets there instead, so it only bursts
        // on non-GPU bodies. "Crown Ring" always plays on both paths.
        internal static WaterSplashEmitter CreateSplashEmitter(Transform parent)
        {
            var rootGO = NewUndoableGameObject(SplashRootName);
            rootGO.transform.SetParent(parent);
            var splashEmitter = rootGO.AddComponent<WaterSplashEmitter>();

            var splashGO = NewUndoableGameObject(SplashDropletChildName);
            splashGO.transform.SetParent(rootGO.transform);
            var splashPS = splashGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureForDrift(splashPS);
            var splashPSR = splashGO.GetComponent<ParticleSystemRenderer>();
            splashPSR.sharedMaterial = LoadOrCreateSplashMaterial(
                SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            // Render mode is owned by ConfigureForDrift (stretched billboards: fast droplets
            // streak along their motion) - no override here.
            splashEmitter.particles = splashPS;

            EnsureJetLayer(splashEmitter, CreateOrUpgradeCrownMaterial());

            var crownGO = NewUndoableGameObject(SplashCrownChildName);
            crownGO.transform.SetParent(rootGO.transform);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, CrownSheetCols, CrownSheetRows);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            // Free-rotating billboards: each chunk sprite spawns at a random angle and
            // tumbles (KWS droplet cloud). The old vertical-billboard bottom pivot belonged
            // to the single crown card this system used to be.
            crownPSR.renderMode = ParticleSystemRenderMode.Billboard;
            crownPSR.pivot = Vector3.zero;
            crownPSR.sharedMaterial = CreateOrUpgradeCrownMaterial();
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;
        }

        // Upgrade the shared splash materials and retrofit the independent jet layer onto
        // existing emitters in the open scene. New emitters receive it in CreateSplashEmitter.
        internal static void UpgradeSplashMaterials()
        {
            EnsureGenFolder();
            LoadOrCreateSplashMaterial(SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            Material crownMaterial = CreateOrUpgradeCrownMaterial();
            foreach (WaterSplashEmitter emitter in Object.FindObjectsByType<WaterSplashEmitter>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
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
            ParticleSystem jetParticles = existingJet != null
                ? existingJet.GetComponent<ParticleSystem>()
                : null;
            if (jetParticles == null)
            {
                var jetGO = NewUndoableGameObject(SplashJetChildName);
                jetGO.transform.SetParent(emitter.transform);
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
        // procedural flipbook: the texture is swapped and six-way lighting is switched
        // OFF, because the baked light sheets match the old flipbook's frames, not the
        // chunk atlas - relighting chunks with them would shade garbage.
        static Material CreateOrUpgradeCrownMaterial()
        {
            var material = LoadOrCreateSplashMaterial(SplashCrownMaterialPath,
                LoadOrProvisionPackagedSheet(SplashCrownSheetPath, CrownSheetPackageRelativePath));
            if (material == null) return null;

            if (material.HasProperty(SixWayProperty))
                material.SetFloat(SixWayProperty, 0f);
            if (material.HasProperty(TransmissionStrengthProperty) &&
                Mathf.Approximately(material.GetFloat(TransmissionStrengthProperty), 0f))
            {
                material.SetFloat(TransmissionStrengthProperty, DefaultCrownTransmission);
            }
            EditorUtility.SetDirty(material);
            return material;
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
