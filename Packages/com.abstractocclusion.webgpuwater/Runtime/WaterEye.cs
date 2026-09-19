// WebGpuWater - WHICH camera the water treats as "the eye".
//
// THE PROBLEM THIS REPLACES. Every eye-role read in the package used to go through
// WaterVolume.targetCamera: a single [SerializeField] Camera, authored per body, resolved once at
// enable. That one field was doing three unrelated jobs -
//   1. the EYE: arbiter of the global underwater state (UnderwaterFogActive, CameraSubmerged,
//      FogSource are statics and PublishUnderwater writes Shader.SetGlobal*, so exactly one camera
//      per frame owns them and something has to elect it);
//   2. the SIM CENTRE: clipmap follow, sim scheduling, budget culling, scene-light gather;
//   3. the DEMO RIG: OrbitCamera lookup, WaterInputRouter screen picking, opt-in framing.
// - which is fine for a demo scene with one camera and wrong for a game that switches cameras.
//
// Worse, the underwater gate was reference equality (`cam != targetCamera`), so a camera that was
// merely INACTIVE failed it exactly like a wrong camera would: the eye silently stopped existing,
// the fog never armed, and WaterRuntimeValidation reported nothing because the reference was
// non-null. That is how a disabled player-rig prefab cost an afternoon.
//
// THE FIX - ownership inverted. The camera declares itself the eye by carrying this component;
// OnEnable/OnDisable register it, so switching cameras hands the eye over by construction and a
// disabled rig deregisters itself. Highest Priority among the LIVE eyes wins, ties by registration
// order. WaterVolume.targetCamera survives as a legacy fallback so scenes authored before this
// component keep working untouched - but it is now only consulted when it is actually live.
//
// Resolution order (Resolve):
//   1. highest-priority live WaterEye;
//   2. the primary body's targetCamera, then any body's - LIVE ONLY (the fix above);
//   3. Camera.main;
//   4. null - no eye this frame; every caller already handles a null camera.
// ResolveWired stops before step 3: diagnostics must not count an accidental MainCamera tag as
// wiring, which is also what the pre-existing MissingCamera flag meant.
//
// The result is cached per frame and invalidated on every registration change, so the per-body,
// per-camera calls in OnBeginCameraRender cost one int compare after the first.
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Marks a camera as the water's eye: the camera whose submersion drives the
    /// underwater fog and whose position drives simulation LOD. Put it on your gameplay camera.
    /// Several may exist - the enabled one with the highest <see cref="Priority"/> wins, so a
    /// scope/cutscene camera takes over simply by being enabled.</summary>
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Eye")]
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class WaterEye : MonoBehaviour
    {
        /// <summary>Live registered eyes (the WaterFogTransparent.Live pattern).</summary>
        internal static readonly List<WaterEye> Live = new List<WaterEye>();

        [Tooltip("Highest priority among the ENABLED eyes wins; ties go to whichever registered " +
                 "first. Leave every eye at 0 and simply enable the camera you want; raise this " +
                 "on an override camera (scope, cutscene) that must win while both are enabled.")]
        [SerializeField] int priority;

        Camera _camera;

        /// <summary>The camera this eye is attached to. Named EyeCamera, not Camera: a member
        /// whose name matches its type leans on C#'s "Color Color" rule every time the type is
        /// used in this file, and this file uses it in static methods on every line.</summary>
        public Camera EyeCamera => _camera != null ? _camera : _camera = GetComponent<Camera>();

        /// <summary>Election weight - see the tooltip. Setting it re-elects immediately.</summary>
        public int Priority
        {
            get => priority;
            set { if (priority == value) return; priority = value; Invalidate(); }
        }

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState()
        {
            Live.Clear();
            Invalidate();
        }

        void OnEnable()
        {
            _camera = GetComponent<Camera>();
            if (!Live.Contains(this)) Live.Add(this);
            Invalidate();
        }

        void OnDisable()
        {
            Live.Remove(this);
            Invalidate();
        }

        // Priority edited in the inspector must re-elect without a play-mode round trip.
        void OnValidate() => Invalidate();

        // ---- election ------------------------------------------------------------------------

        static Camera _resolved;
        static int _resolvedFrame = -1;

        /// <summary>Drop the cached election. Called on every registration change; also safe to
        /// call from game code after enabling/disabling cameras in the same frame.</summary>
        public static void Invalidate() => _resolvedFrame = -1;

        /// <summary>The camera the water treats as the eye this frame, or null when the scene has
        /// none. Cached per frame.</summary>
        public static Camera Resolve()
        {
            if (_resolvedFrame == Time.frameCount) return _resolved;
            _resolvedFrame = Time.frameCount;
            _resolved = ResolveWired();
            if (_resolved == null) _resolved = Camera.main; // step 3: last-resort convention
            return _resolved;
        }

        /// <summary>The eye as far as AUTHORING is concerned: a live WaterEye, else a live legacy
        /// targetCamera. Never falls back to Camera.main, so diagnostics report an unwired scene
        /// instead of silently riding whatever carries the MainCamera tag.</summary>
        internal static Camera ResolveWired()
        {
            Camera best = null;
            int bestPriority = int.MinValue;
            for (int i = 0; i < Live.Count; i++)
            {
                WaterEye eye = Live[i];
                if (eye == null || !eye.isActiveAndEnabled) continue;
                Camera camera = eye.EyeCamera;
                if (!IsLive(camera)) continue;
                if (best != null && eye.priority <= bestPriority) continue; // ties: first registered
                best = camera;
                bestPriority = eye.priority;
            }
            if (best != null) return best;

            // Legacy fallback. LIVE ONLY - an assigned but inactive camera is exactly the silent
            // failure this component exists to end, so it does not win the election; it is
            // reported by WaterRuntimeValidation.StaleLegacyCamera instead.
            WaterVolume primary = WaterVolume.Primary;
            if (primary != null && IsLive(primary.targetCamera)) return primary.targetCamera;
            List<WaterVolume> bodies = WaterVolume.Bodies;
            for (int i = 0; i < bodies.Count; i++)
                if (bodies[i] != null && IsLive(bodies[i].targetCamera)) return bodies[i].targetCamera;
            return null;
        }

        /// <summary>True when <paramref name="camera"/> is the eye this frame - the one gate the
        /// per-camera render callbacks use in place of the old reference equality.</summary>
        internal static bool IsEye(Camera camera) => camera != null && camera == Resolve();

        /// <summary>A camera that will actually render: assigned, on an active object, enabled.
        /// The whole point of the rewrite - `!= null` was never the right test.</summary>
        static bool IsLive(Camera camera) => camera != null && camera.isActiveAndEnabled;

        /// <summary>True when a legacy targetCamera is assigned but cannot render, and no WaterEye
        /// covers for it - the "assigned yet the fog is dead" case, for diagnostics.</summary>
        internal static bool HasStaleLegacyCamera(WaterVolume body)
            => body != null && body.targetCamera != null &&
               !IsLive(body.targetCamera) && ResolveWired() == null;
    }
}
