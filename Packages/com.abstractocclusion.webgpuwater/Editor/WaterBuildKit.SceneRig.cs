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

            var crownGO = NewUndoableGameObject(SplashCrownChildName);
            crownGO.transform.SetParent(rootGO.transform);
            var crownPS = crownGO.AddComponent<ParticleSystem>();
            WaterSplashEmitter.ConfigureCrown(crownPS, CrownSheetCols, CrownSheetRows);
            var crownPSR = crownGO.GetComponent<ParticleSystemRenderer>();
            crownPSR.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            crownPSR.pivot = new Vector3(0f, 0.5f, 0f);
            crownPSR.sharedMaterial = CreateOrUpgradeCrownMaterial();
            splashEmitter.crownParticles = crownPS;
            return splashEmitter;
        }

        // Upgrade (or create) both shared splash materials on the lit shader. They are
        // referenced by every demo scene, so this fixes all of them at once.
        internal static void UpgradeSplashMaterials()
        {
            EnsureGenFolder();
            LoadOrCreateSplashMaterial(SplashDropletMaterialPath, LoadOrBuildDroplet(DropletTexturePath));
            CreateOrUpgradeCrownMaterial();
            AssetDatabase.SaveAssets();
        }

        // The crown material: packed flipbook + the six-way light sheets and backlit
        // transmission (both baked by gen_splash_flipbook.py alongside the main sheet).
        // Doubles as the one-click upgrade for crown materials created before six-way
        // lighting existed. Missing light sheets (older package payloads) degrade
        // gracefully: the material stays on the scalar foam lighting.
        static Material CreateOrUpgradeCrownMaterial()
        {
            var material = LoadOrCreateSplashMaterial(SplashCrownMaterialPath,
                LoadOrProvisionPackagedSheet(SplashCrownSheetPath, CrownSheetPackageRelativePath));
            if (material == null) return null;

            var lightSheetA = LoadOrProvisionPackagedSheet(
                SplashCrownLightSheetAPath, CrownLightSheetAPackageRelativePath);
            var lightSheetB = LoadOrProvisionPackagedSheet(
                SplashCrownLightSheetBPath, CrownLightSheetBPackageRelativePath);
            bool sixWayReady = lightSheetA != null && lightSheetB != null
                && material.HasProperty(SixWayProperty);
            if (!sixWayReady) return material;

            material.SetTexture(LightSheetAProperty, lightSheetA);
            material.SetTexture(LightSheetBProperty, lightSheetB);
            material.SetFloat(SixWayProperty, 1f);
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
