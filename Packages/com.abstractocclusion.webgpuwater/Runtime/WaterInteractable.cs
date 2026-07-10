// WebGL Water - marker for objects that interact with the water (Unity 6 / URP).
// Add this to any Renderer that should disturb the surface. It self-registers in a
// static list, so detection is automatic: no manual wiring, no per-frame Find.
//
// Two interaction modes, chosen on the WaterVolume (objectInteraction):
// - MouseLikeDrops (default): this component emits analytic cosine drops CLONED from
//   the mouse rules - one drop per fixed step of vertical plunge/rise, one wake drop
//   per fixed step of horizontal travel (the mouse drag's spacing rule). Smooth by
//   construction: no rasterization, and slow rotation emits nothing at all.
// - FootprintDelta: WaterObstacle rasterizes the submerged footprint top-down and the
//   sim injects the frame-to-frame delta (shaped wakes for large hulls).
using System.Collections.Generic;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [RequireComponent(typeof(Renderer))]
    public class WaterInteractable : MonoBehaviour
    {
        static readonly List<WaterInteractable> _active = new List<WaterInteractable>();

        /// <summary>All currently enabled interactables, for the obstacle pass. Read-only to
        /// callers; membership is managed by OnEnable/OnDisable.</summary>
        public static IReadOnlyList<WaterInteractable> Active => _active;

        // Cleared by WaterVolume.ResetStaticState for Fast Enter Play Mode (no domain reload).
        internal static void ResetStaticState() => _active.Clear();

        // Safety cap on drops emitted in one frame (a teleporting object would otherwise
        // burst-fire its whole accumulated travel as a stack of drops).
        const int MaxDropsPerFrame = 4;

        [Tooltip("Per-object RELATIVE weight on how strongly it disturbs the water. Leave " +
                 "at 1 unless one object should push more or less than the others; the " +
                 "master strengths live on the WaterVolume (Ripple Strength in drop mode, " +
                 "Obstacle Strength in footprint mode).")]
        public float displaceScale = 1f;

        [Header("Mouse-like drop emission")]
        [Tooltip("Vertical plunge/rise (world units) that emits one drop. Smaller = more " +
                 "frequent bobbing ripples.")]
        [Range(0.002f, 0.1f)] [SerializeField] internal float verticalEmitSpacing = 0.012f;
        [Tooltip("Horizontal travel (world units) that emits one wake drop - the same " +
                 "spacing rule the mouse drag uses.")]
        [Range(0.005f, 0.2f)] [SerializeField] internal float horizontalEmitSpacing = 0.02f;
        [Tooltip("Multiplier on the mouse's Ripple Radius for THIS object's drops. 1 = " +
                 "identical to a mouse drop; raise it so a bigger object throws a wider " +
                 "ripple. Scales radius only - amplitude is displaceScale.")]
        [Range(1f, 20f)] [SerializeField] internal float rippleRadiusScale = 1f;

        [Header("Passive reflection")]
        [Tooltip("When on, this object's submerged footprint acts as a SOLID WALL: ripples that " +
                 "reach it bounce off instead of passing through (KWS ObstacleObject behaviour). " +
                 "Independent of the emission above - a static reflector can sit still and simply " +
                 "reflect. Off (default) leaves the sim byte-identical.")]
        public bool reflectsWaves = false;

        Vector3 _lastDropPosition;
        float _prevRelDepth;
        bool _tracking;

        public Renderer Renderer { get; private set; }

        void Awake()
        {
            Renderer = GetComponent<Renderer>();
        }
        void OnEnable()
        {
            if (Renderer == null) Renderer = GetComponent<Renderer>();
            if (!_active.Contains(this)) _active.Add(this);
        }
        void OnDisable() { _active.Remove(this); }

        // Mouse-like drop emission. LateUpdate so physics (FixedUpdate) and the volume's
        // sim step have settled; AddRipple dispatches immediately and is safe any time.
        void LateUpdate()
        {
            if (Renderer == null) return;
            Bounds bounds = Renderer.bounds;
            WaterVolume body = WaterVolume.BodyContaining(bounds.center);
            if (body == null || body.objectInteraction != WaterVolume.ObjectInteraction.MouseLikeDrops)
            {
                _tracking = false;
                return;
            }
            if (!body.TryGetAnalyticWaterline(bounds.center.x, bounds.center.z, out float waterlineY)
                || !IsSubmerged(waterlineY))
            {
                _tracking = false;
                return;
            }

            // Plunge depth tracked from the TRANSFORM, not the AABB: a rotating object's
            // bounds breathe with its orientation, which would fake vertical motion.
            float relDepth = waterlineY - transform.position.y;
            Vector3 position = bounds.center;

            if (!_tracking)
            {
                // (Re)entered the water: prime the trackers; the physical entry splash is
                // WaterSplash's job, steady-state disturbance starts next frame.
                _tracking = true;
                _prevRelDepth = relDepth;
                _lastDropPosition = position;
                return;
            }

            // Drop look CLONED from the mouse: the mouse's amplitude and radius, so at
            // rippleRadiusScale 1 a moving float ripples identically to a mouse drag. Size
            // is an EXPLICIT per-object multiplier on the radius, never the object's raw
            // bounds - inflating radius by bounds spread the fixed strength over a wide
            // dome that read as a soft swell, nothing like the mouse's crisp ring.
            float radius = body.RippleRadius * rippleRadiusScale;
            float strength = body.RippleStrength * displaceScale;
            int budget = MaxDropsPerFrame;

            // Vertical bobbing: one drop per spacing step of plunge/rise. Signed like the
            // footprint delta was: sinking dips the surface, rising lifts it. The
            // un-emitted remainder keeps accumulating across frames.
            float depthDelta = relDepth - _prevRelDepth;
            while (Mathf.Abs(depthDelta) >= verticalEmitSpacing && budget-- > 0)
            {
                float direction = Mathf.Sign(depthDelta);
                body.AddRipple(position.x, position.z, radius, -direction * strength);
                depthDelta -= direction * verticalEmitSpacing;
            }
            _prevRelDepth = relDepth - depthDelta;

            // Horizontal drift: the mouse drag rule - one wake drop per spacing step.
            Vector3 travel = position - _lastDropPosition;
            travel.y = 0f;
            if (travel.magnitude >= horizontalEmitSpacing && budget > 0)
            {
                body.AddRipple(position.x, position.z, radius, strength);
                _lastDropPosition = position;
            }
        }

        /// <summary>Local ANALYTIC waterline (world Y) under the object - rest plane plus
        /// wind waves, deliberately NOT the live rippled surface. A float riding a wind
        /// wave keeps a constant submerged depth against this moving line, so it injects
        /// nothing; only genuine motion through the surface (plunge, drift, bob) changes
        /// it. Using the live readback height here fed the object's own ripples back into
        /// its footprint (stale by the async readback) - a delayed feedback loop that kept
        /// re-exciting micro-ripples around every floater. Falls back to the flat rest
        /// plane (restY) outside every body's footprint.</summary>
        public float WaterlineY(float restY)
        {
            if (Renderer == null) return restY;
            Bounds b = Renderer.bounds;
            // Resolve the body under the object each call so the waterline follows the lake
            // it is actually in, not a single body cached at startup.
            WaterVolume body = WaterVolume.BodyContaining(b.center);
            if (body != null && body.TryGetAnalyticWaterline(b.center.x, b.center.z, out float surfaceY))
                return surfaceY;
            return restY;
        }

        /// <summary>True if any part of the object sits below the given waterline.</summary>
        public bool IsSubmerged(float waterlineY)
        {
            return Renderer != null && Renderer.bounds.min.y < waterlineY;
        }
    }
}
