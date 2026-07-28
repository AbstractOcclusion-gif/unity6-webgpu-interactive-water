// WebGpuWater - WaterVolume partial: required-wiring checks, scene-reference resolution and
// the shared teardown helpers.
//
// The setup/teardown edges of the lifecycle in WaterVolume.cs: what must be wired before a body
// can initialize, what it may resolve for itself so a bare prefab drop "just works", and the
// restore-then-destroy helpers OnDisable leans on to never leave a renderer pointing at a
// destroyed material or mesh.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        bool HasRequiredWiring() => simCompute != null && causticsShader != null && waterMesh != null;

        // Fail fast on the required wiring (play mode); a missing piece would otherwise surface
        // later as a confusing downstream error (broken caustic material, per-frame DrawMesh errors).
        void FailMissingWiring()
        {
            if (simCompute == null) Debug.LogError("WaterVolume: simCompute not assigned.", this);
            else if (causticsShader == null) Debug.LogError("WaterVolume: causticsShader not assigned.", this);
            else Debug.LogError("WaterVolume: waterMesh not assigned.", this);
            enabled = false;
        }

        // Hand the primary role to another live body flagged isPrimary, so disabling one of two
        // (misconfigured) primaries doesn't strand Primary at null while a candidate is alive -
        // that would send every Resolve() into a per-call whole-scene search.
        static WaterVolume FindNextPrimary(WaterVolume leaving)
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i] != leaving && Bodies[i].isPrimary) return Bodies[i];
            return null;
        }

        // Restore the renderer's authored material before destroying the per-body instance, so
        // a disable/enable cycle never leaves the renderer pointing at a destroyed material.
        static void RestoreSurfaceMaterial(Renderer r, ref Material instance, ref Material original)
        {
            if (instance == null) { original = null; return; }
            if (r != null && original != null) r.sharedMaterial = original;
            WaterObjects.DestroyRuntime(instance);
            instance = null;
            original = null;
        }

        // Drop the per-body property block from every renderer this body drives. The block holds this
        // body's sim/caustic RTs, destroyed moments later; clearing it lets each renderer fall back to
        // its material's own values instead of sampling a dead target. Property blocks are runtime-only
        // state, never serialized, so this is safe in edit mode.
        void ClearBodyRendererBlocks()
        {
            ClearRendererBlock(surfaceAbove);
            ClearRendererBlock(surfaceUnder);
            ClearRendererBlock(poolRenderer);
            ClearRendererBlock(godRayRenderer);
        }

        static void ClearRendererBlock(Renderer r)
        {
            if (r != null) r.SetPropertyBlock(null);
        }

        // Fill in the scene-level references a prefab can't carry, so dropping the WaterVolume
        // prefab into a fresh scene "just works". Only unset fields are touched, so an explicitly
        // wired scene (e.g. the demo builder) is left exactly as authored.
        //
        // PLAY MODE ONLY. These are [SerializeField]s and TryInitialize runs under [ExecuteAlways],
        // so resolving in edit mode wrote scene objects into AUTHORED data: merely opening a scene
        // filled them in and the next save baked them, and in prefab-isolation mode the write landed
        // on the prefab asset. Same rule as ApplyQuality (tier values live in '_' runtime fields) and
        // EffectiveLightDir (derived, never written back). A wizard-built scene is unaffected - the
        // build kit assigns camera/sun/orbit explicitly at author time.
        void ResolveSceneRefs()
        {
            if (!Application.isPlaying) return;

            if (targetCamera == null) targetCamera = Camera.main;
            if (sun == null) sun = ResolveSun();
            if (orbit == null && targetCamera != null) orbit = targetCamera.GetComponent<OrbitCamera>();
            // splashEmitter is resolved lazily on first impact (ResolveSplashEmitter), not eagerly here,
            // so a body that never splashes never searches the scene or creates an emitter.
        }

        // Name of the emitter auto-created when a body must supply splashes but none is authored.
        const string AutoSplashEmitterName = "Splash Emitter (auto)";

        /// <summary>The splash emitter this body routes impacts through - resolved lazily and cached:
        /// an assigned emitter, one already under the body, any emitter in the scene (back-compat with
        /// a single rigged emitter), or a droplet-only emitter created on the body on demand. Returns
        /// null when the body opts out of splashes (<see cref="provideSplashEmitter"/> off), so triggers
        /// over it stay silent.</summary>
        internal WaterSplashEmitter ResolveSplashEmitter()
        {
            if (!provideSplashEmitter) return null;
            if (splashEmitter != null) return splashEmitter;

            splashEmitter = GetComponentInChildren<WaterSplashEmitter>();
            if (splashEmitter != null) return splashEmitter;

            splashEmitter = FindFirstObjectByType<WaterSplashEmitter>();
            if (splashEmitter != null) return splashEmitter;

            if (!Application.isPlaying) return null; // never spawn content into a scene being edited
            return splashEmitter = CreateOwnedSplashEmitter();
        }

        // A droplet-only emitter parented to this body. WaterSplashEmitter.Awake builds a drift
        // ParticleSystem with no editor assets; the crown flipbook is an editor-only asset, so an
        // auto-created emitter has no crown - droplets still fire (GPU-routed when the body has a
        // WaterFoamParticles). The authored wizard emitter is the path that carries the crown.
        WaterSplashEmitter CreateOwnedSplashEmitter()
        {
            var host = new GameObject(AutoSplashEmitterName);
            host.transform.SetParent(transform, worldPositionStays: false);
            return host.AddComponent<WaterSplashEmitter>();
        }

        // The scene's key light: the lighting-settings sun if set, else the first directional light.
        static Light ResolveSun()
        {
            if (RenderSettings.sun != null) return RenderSettings.sun;
            Light[] lights = FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i].type == LightType.Directional) return lights[i];
            return null;
        }
    }
}
