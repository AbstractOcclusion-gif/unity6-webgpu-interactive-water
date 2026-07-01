// WebGL Water - marker for objects that interact with the water (Unity 6 / URP).
// Add this to any Renderer that should displace the surface. It self-registers in
// a static list that WaterObstacle iterates each step, so detection is automatic:
// no manual wiring, no per-frame FindObjectsOfType.
using System.Collections.Generic;
using UnityEngine;

namespace WebGLWater
{
    [RequireComponent(typeof(Renderer))]
    public class WaterInteractable : MonoBehaviour
    {
        /// <summary>All currently enabled interactables, for the obstacle pass.</summary>
        public static readonly List<WaterInteractable> Active = new List<WaterInteractable>();

        [Tooltip("Per-object RELATIVE weight on how strongly it displaces the water. Leave " +
                 "at 1 unless one object should push more or less than the others; the " +
                 "master strength is 'Obstacle Strength' on the WaterController.")]
        public float displaceScale = 1f;

        public Renderer Renderer { get; private set; }

        // Reads the local water surface height under this object so the footprint is
        // referenced to the moving surface, not the flat rest plane.
        WaterController _ctrl;

        void Awake()
        {
            Renderer = GetComponent<Renderer>();
            _ctrl = WaterController.Resolve(); // TODO(Phase 2): the body containing this object
        }
        void OnEnable()
        {
            if (Renderer == null) Renderer = GetComponent<Renderer>();
            if (!Active.Contains(this)) Active.Add(this);
        }
        void OnDisable() { Active.Remove(this); }

        /// <summary>Local water surface height (world Y) under the object, used as the
        /// per-object waterline for the submerged-thickness footprint. A float riding a
        /// wave keeps a constant submerged depth against this moving line, so it injects
        /// nothing; only genuine plunging through the surface changes it. Falls back to
        /// the flat rest plane (restY) until the height readback is available.</summary>
        public float WaterlineY(float restY)
        {
            if (Renderer == null) return restY;
            Bounds b = Renderer.bounds;
            if (_ctrl != null && _ctrl.TryGetWaterHeight(b.center.x, b.center.z, out float surfaceY))
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
