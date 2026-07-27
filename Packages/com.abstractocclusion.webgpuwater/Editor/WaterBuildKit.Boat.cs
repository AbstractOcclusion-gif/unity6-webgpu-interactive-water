// WebGpuWater build kit - the demo boat, hull to dry interior.
// The dry interior is a water exclusion volume fitted to the hull's own bounds, which is why it
// lives with the boat rather than with the standalone exclusion-volume command next door.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- boat
        // Resurrected from the retired WaterBoatDemoBuilder: the same probe-buoyancy + BoatController
        // rig, now a wizard one-click that works with or without water in the scene. Tuning values are
        // the demo's proven set (they made the primitive boat float level and carve properly).
        const string BoatName = "Boat";
        const string BoatCabinName = "Cabin";
        static readonly Vector3 BoatHullScale = new Vector3(2f, 0.6f, 5f);   // wide, low, long
        static readonly Vector3 BoatCabinScale = new Vector3(1.2f, 0.5f, 1.8f);
        static readonly Vector3 BoatCabinLocalPosition = new Vector3(0f, 0.55f, -0.4f);
        const float BoatMass = 200f;
        const float BoatBuoyancy = 2.6f;
        const int BoatSamplesPerAxis = 3;   // 27 probes -> good roll/pitch + length torque
        // (Ripple-LOD objectWidth is derived from the hull's real footprint in CreateBoat -
        // max(x, z) of the fitted collider - which reproduces the old hand-tuned 5 m for the
        // primitive hull and scales correctly for custom models.)

        const string BoatHullName = "Hull";

        // ---- dry interior (water exclusion) -----------------------------------
        const string BoatDryInteriorName = "Dry Interior";
        // Primitive hull: the dry box is the hull box inset by a wall thickness per face, so the
        // surface's cut edge stays hidden INSIDE the hull walls (the content rule both reference
        // implementations state: the walls must cover the cut).
        const float DryInteriorWallInset = 0.05f; // metres, per face
        // Custom hull model: renderer bounds shrunk by this factor - a hull mesh is wider than
        // its interior, and the fitted box is a starting point the user refines on the child.
        const float DryInteriorBoundsShrink = 0.9f;
        // Floor on a fitted dry-box edge so an extreme inset/shrink on a tiny hull can never
        // collapse (or invert) the box.
        const float DryInteriorMinEdge = 0.05f; // metres

        /// <summary>A drivable boat: probe buoyancy, BoatController drive, wake + membership,
        /// optional splash. The ROOT stays at scale (1,1,1) and carries all physics (Rigidbody,
        /// fitted BoxCollider, buoyancy - WaterBuoyancy reads the collider on its own object);
        /// the visuals are CHILDREN, so a custom hull model drops in without inheriting the
        /// primitive hull's (2, 0.6, 5) stretch - and can be swapped later by replacing the child.
        /// withDryInterior adds a "Dry Interior" WaterExclusionVolume child fitted to the same
        /// box the collider uses, so the water surface never renders inside the hull.
        /// Undo-registered; the caller owns the undo group.</summary>
        internal static GameObject CreateBoat(GameObject hullModel, bool withSplash, bool withDryInterior)
        {
            var boat = NewUndoableGameObject(BoatName);
            boat.transform.position = PropSpawnPosition();

            Vector3 hullSize;
            Vector3 hullCenterLocal;
            if (hullModel != null)
            {
                GameObject visual = InstantiateVisual(hullModel, boat.transform);
                if (!TryGetCombinedRendererBounds(visual, out Bounds worldBounds))
                {
                    // A model with no renderers can't size the collider; fall back to the
                    // primitive hull's box so the boat still floats and drives predictably.
                    Debug.LogWarning("[WebGpuWater] Hull model has no renderers; using the default hull-sized collider.");
                    worldBounds = new Bounds(boat.transform.position, BoatHullScale);
                }
                var box = boat.AddComponent<BoxCollider>();
                box.center = boat.transform.InverseTransformPoint(worldBounds.center);
                box.size = worldBounds.size; // root is unscaled + unrotated at creation, so world == local
                hullSize = worldBounds.size;
                hullCenterLocal = box.center;
            }
            else
            {
                AddPrimitiveHull(boat.transform);
                var box = boat.AddComponent<BoxCollider>();
                box.size = BoatHullScale;
                hullSize = BoatHullScale;
                hullCenterLocal = Vector3.zero;
            }

            var rigidbody = boat.AddComponent<Rigidbody>();
            rigidbody.mass = BoatMass;
            rigidbody.interpolation = RigidbodyInterpolation.Interpolate;

            var buoyancy = boat.AddComponent<WaterBuoyancy>();
            buoyancy.buoyancy = BoatBuoyancy;
            buoyancy.samplesPerAxis = BoatSamplesPerAxis;
            // Ripple LOD follows the hull's real footprint (a custom hull may be far from 5 m):
            // ignore ripples shorter than the hull so a big boat rides swell without buzzing.
            buoyancy.objectWidth = Mathf.Max(hullSize.x, hullSize.z);
            buoyancy.surfaceRelativeDrag = true;
            buoyancy.ignoreInteractiveRipples = true; // don't let the boat's own wake ripples propel it

            boat.AddComponent<BoatController>();
            boat.AddComponent<WaterMembership>();
            boat.AddComponent<WaterInteractable>(); // wake ripples
            if (withSplash) boat.AddComponent<WaterSplash>();
            if (withDryInterior) AddDryInterior(boat.transform, hullCenterLocal, hullSize, hullModel != null);
            return boat;
        }

        // The "boat doesn't fill with water" step: a WaterExclusionVolume over the hull so the
        // surface sheet never renders inside it. Sized from the SAME fitted box physics uses -
        // inset (primitive hull) or shrunk (custom model) so the cut edge stays behind the hull
        // walls. Visual-only (buoyancy reads the collider, not this); resize or delete the child
        // freely to fit an open cockpit. Creation is undo-registered like every build step.
        static void AddDryInterior(Transform root, Vector3 hullCenterLocal, Vector3 hullSize, bool customHull)
        {
            var dry = NewUndoableGameObject(BoatDryInteriorName);
            dry.transform.SetParent(root, worldPositionStays: false);
            dry.transform.localPosition = hullCenterLocal;

            Vector3 size = customHull
                ? hullSize * DryInteriorBoundsShrink
                : hullSize - 2f * DryInteriorWallInset * Vector3.one;
            var volume = dry.AddComponent<WaterExclusionVolume>();
            volume.size = Vector3.Max(size, DryInteriorMinEdge * Vector3.one);
            // The hull IS the boundary geometry (the content rule): water walls here would paint
            // fog colour over the cockpit interior. Bare standalone volumes keep them on.
            volume.drawWaterWalls = false;
        }

        // Instantiate the hull visual under the boat root (prefab-linked when the source is a
        // prefab asset, plain clone otherwise) at local identity - the ROOT owns placement.
        static GameObject InstantiateVisual(GameObject source, Transform parent)
        {
            var visual = PrefabUtility.InstantiatePrefab(source) as GameObject;
            if (visual == null) visual = Object.Instantiate(source);
            Undo.RegisterCreatedObjectUndo(visual, BoatName);
            visual.name = BoatHullName;
            visual.transform.SetParent(parent, worldPositionStays: false);
            visual.transform.localPosition = Vector3.zero;
            visual.transform.localRotation = Quaternion.identity;
            return visual;
        }

        // Combined world bounds of every renderer under the visual (a real boat model is usually
        // several meshes/materials). False when there is nothing to measure.
        static bool TryGetCombinedRendererBounds(GameObject visual, out Bounds bounds)
        {
            var renderers = visual.GetComponentsInChildren<Renderer>();
            bounds = default;
            if (renderers.Length == 0) return false;
            bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return true;
        }

        // The visual-only primitive hull + cabin, as CHILDREN of the unscaled root: the hull cube
        // carries the (2, 0.6, 5) stretch itself, and the cabin sits in plain root space (its old
        // divide-out-the-hull-stretch dance is gone with the scaled root). Both colliders are
        // removed - physics lives on the root's fitted BoxCollider.
        static void AddPrimitiveHull(Transform root)
        {
            var hull = GameObject.CreatePrimitive(PrimitiveType.Cube);
            hull.name = BoatHullName;
            Undo.RegisterCreatedObjectUndo(hull, BoatHullName);
            hull.transform.SetParent(root, worldPositionStays: false);
            hull.transform.localScale = BoatHullScale;
            Object.DestroyImmediate(hull.GetComponent<Collider>());

            var cabin = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cabin.name = BoatCabinName;
            Undo.RegisterCreatedObjectUndo(cabin, BoatCabinName);
            cabin.transform.SetParent(root, worldPositionStays: false);
            // Same world pose as the old scaled-root rig: the cabin offset was authored in the
            // stretched hull's local space, so scale it out once here (one source, no new literals).
            cabin.transform.localPosition = Vector3.Scale(BoatCabinLocalPosition, BoatHullScale);
            cabin.transform.localScale = BoatCabinScale;
            Object.DestroyImmediate(cabin.GetComponent<Collider>());
        }

        /// <summary>Point the scene at the boat: swap the camera's controller for a follow camera
        /// (orbit/fly disabled, not destroyed - bodies may reference them) and focus the primary
        /// open-water body's ripple window on the hull instead of the trailing camera.</summary>
        internal static void FocusSceneOnBoat(GameObject boat)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                var orbit = cam.GetComponent<OrbitCamera>();
                if (orbit != null) { Undo.RecordObject(orbit, "Focus On Boat"); orbit.enabled = false; }
                var fly = cam.GetComponent<FlyCamera>();
                if (fly != null) { Undo.RecordObject(fly, "Focus On Boat"); fly.enabled = false; }
                var follow = cam.GetComponent<SimpleFollowCamera>();
                if (follow == null) follow = Undo.AddComponent<SimpleFollowCamera>(cam.gameObject);
                else Undo.RecordObject(follow, "Focus On Boat");
                follow.target = boat.transform;
            }

            var bodies = Object.FindObjectsByType<WaterVolume>(FindObjectsSortMode.None);
            WaterVolume primary = System.Array.Find(bodies, b => b.IsPrimary);
            if (primary != null && primary.IsWindowed)
            {
                Undo.RecordObject(primary, "Focus On Boat");
                primary.simWindowFocus = boat.transform;
                EditorUtility.SetDirty(primary);
            }
        }

    }
}
