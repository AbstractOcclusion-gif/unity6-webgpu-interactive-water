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

        // Axis the hull MODEL was authored facing (its bow). The build yaws the VISUAL CHILD so
        // the boat root's +Z is the bow - BoatController is transform.forward by design (thrust,
        // keel drag, stern offset all ride the root frame), so the frame is corrected ONCE at
        // build time instead of threading a custom axis through the drive math (three places to
        // get a sign wrong). Irrelevant for the primitive hull (authored +Z already).
        internal enum BoatModelForward { PositiveZ, NegativeZ, PositiveX, NegativeX }

        // Yaw that maps the model's authored bow axis onto +Z. Applied to the child BEFORE the
        // collider / buoyancy-width / dry-interior fit reads the renderer bounds, so every
        // fitted size lives in the corrected frame - rotating after the build would orphan
        // them all (length/width swap on the collider and the exclusion box).
        static Quaternion ModelForwardRotation(BoatModelForward forward)
        {
            switch (forward)
            {
                case BoatModelForward.NegativeZ: return Quaternion.Euler(0f, 180f, 0f);
                case BoatModelForward.PositiveX: return Quaternion.Euler(0f, -90f, 0f);
                case BoatModelForward.NegativeX: return Quaternion.Euler(0f, 90f, 0f);
                default:                         return Quaternion.identity;
            }
        }

        // ---- custom-hull controller fit ---------------------------------------
        // The controller's water-contact geometry defaults are the 5 m primitive dinghy's numbers,
        // and a custom model's root origin is the MODEL'S PIVOT - so unfitted defaults put the
        // propeller anywhere, and (the real killer) hullDepth stayed 0.6 m while the fitted box's
        // centre of mass (superstructure included) rode higher above the waterline than that:
        // Wetness(COM) hit 0 and FixedUpdate returned BEFORE ApplySteering, so a custom boat
        // could not turn at all. Fractions of the fitted box, never absolutes:
        const float SternInsetFraction = 0.05f;      // stern point pulled inside the box by this much of the length
        const float SternDepthFraction = 0.25f;      // stern point this fraction of the height below box centre
        const float PropellerDepthFraction = 0.5f;   // thrust fade band scales with hull height
        const float BallastCenterOfMassDrop = 0.25f; // COM lowered by this fraction of the height (ballast):
                                                     // stabilises a top-heavy fitted box (Crest's ocean liner
                                                     // ships its COM far below deck for the same reason) AND
                                                     // drops the hull-wetness probe toward the waterline.

        // Fit BoatController's water-contact points to the FITTED hull box. Custom models only:
        // the primitive hull keeps its proven authored defaults, byte-identical. Feel constants
        // (turn/drag/authority) are deliberately untouched - geometry is derivable, feel is
        // authored. The Max floors reuse the component's own defaults so the primitive tuning
        // remains the minimum, never duplicated here as literals.
        static void FitControllerToHull(BoatController controller, Rigidbody rigidbody,
                                        Vector3 hullCenterLocal, Vector3 hullSize)
        {
            controller.sternOffset = hullCenterLocal + new Vector3(
                0f,
                -hullSize.y * SternDepthFraction,
                -(hullSize.z * (0.5f - SternInsetFraction)));
            controller.propellerDepth = Mathf.Max(controller.propellerDepth,
                                                  hullSize.y * PropellerDepthFraction);
            // Generous: the wetness probe reads the collider-derived COM, which floats up to about
            // half the box height above the waterline - the fade band must cover that.
            controller.hullDepth = Mathf.Max(controller.hullDepth, hullSize.y);
            rigidbody.centerOfMass = hullCenterLocal
                                   + Vector3.down * (hullSize.y * BallastCenterOfMassDrop);
        }

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
        // Mesh-carve dry interior (optional convex proxy): the carve-mesh contract is NORMALISED
        // vertices spanning -0.5..0.5 (WaterExclusionVolume.carveMesh tooltip) - assigning a raw
        // hull mesh by hand carves at the wrong scale/offset, so this path normalises the proxy
        // and saves the normalised copy under the project's WebGpuWater/Boats folder. CONVEX proxies only for a clean
        // carve: the mesh prepass keeps ONE front + ONE back face per pixel, so a concave
        // cavity biases the exit face (documented on the field itself).
        const float DryInteriorMeshShrink = 0.95f;   // slight inset keeps the cut edge behind the hull plating
        const float MinCarveMeshSpan = 1e-4f;        // degenerate-axis guard for the normalisation divide
        const string DryInteriorMeshSuffix = "_DryInterior";

        /// <summary>A drivable boat: probe buoyancy, BoatController drive, wake + membership,
        /// optional splash. The ROOT stays at scale (1,1,1) and carries all physics (Rigidbody,
        /// fitted BoxCollider, buoyancy - WaterBuoyancy reads the collider on its own object);
        /// the visuals are CHILDREN, so a custom hull model drops in without inheriting the
        /// primitive hull's (2, 0.6, 5) stretch - and can be swapped later by replacing the child.
        /// withDryInterior adds a "Dry Interior" WaterExclusionVolume child fitted to the same
        /// box the collider uses, so the water surface never renders inside the hull.
        /// interactionMesh names WHICH of the model's meshes drives the water interaction
        /// (submersion bounds, wake emission, refract-shadow silhouette) - on a multi-mesh
        /// model the component's auto-resolve takes the FIRST child renderer, which may be a
        /// cabin rather than the hull. Empty keeps the auto-resolve.
        /// Undo-registered; the caller owns the undo group.</summary>
        internal static GameObject CreateBoat(GameObject hullModel, bool withSplash, bool withDryInterior,
                                              BoatModelForward modelForward = BoatModelForward.PositiveZ,
                                              Mesh dryInteriorMesh = null,
                                              bool dryInteriorConvexAuto = false,
                                              Mesh interactionMesh = null)
        {
            var boat = NewUndoableGameObject(BoatName);
            boat.transform.position = PropSpawnPosition();

            Vector3 hullSize;
            Vector3 hullCenterLocal;
            GameObject visual = null;
            if (hullModel != null)
            {
                visual = InstantiateVisual(hullModel, boat.transform);
                // Bow onto +Z FIRST - the bounds fit below must read the corrected frame.
                visual.transform.localRotation = ModelForwardRotation(modelForward);
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

            var controller = boat.AddComponent<BoatController>();
            if (hullModel != null) FitControllerToHull(controller, rigidbody, hullCenterLocal, hullSize);
            boat.AddComponent<WaterMembership>();
            var interactable = boat.AddComponent<WaterInteractable>(); // wake ripples
            WireInteractionRenderer(interactable, interactionMesh, visual);
            if (withSplash) boat.AddComponent<WaterSplash>();
            if (withDryInterior)
            {
                // The dry-interior shape, in three steps: pick the SOURCE, optionally convexify it,
                // and fall back loudly if that fails.
                //
                // The source used to be all-or-nothing: assigning a mesh switched the convex
                // generator off entirely, and with no mesh the generator hulled EVERY MeshFilter
                // under the visual root. A model split across two meshes therefore produced the
                // convex envelope of BOTH fused together - a shape that fits neither. Naming the
                // hull mesh AND convexifying it is the case that was missing.
                Mesh dryMesh = dryInteriorMesh;
                Mesh generatedHull = null;
                if (dryInteriorConvexAuto && (dryInteriorMesh != null || visual != null))
                {
                    generatedHull = dryInteriorMesh != null
                        ? BuildConvexHullMesh(dryInteriorMesh, hullModel.name)   // just the named mesh
                        : BuildConvexHullMesh(visual.transform, hullModel.name); // the whole model
                    if (generatedHull == null)
                        Debug.LogWarning(LogPrefix + "Convex approximation failed (degenerate or " +
                                         "non-manifold hull geometry). The dry interior falls back to " +
                                         (dryInteriorMesh != null
                                            ? "the assigned mesh as authored - which the carve needs to be CONVEX."
                                            : "the fitted box."));
                    // Null keeps whatever the user named; null with no named mesh reaches the box.
                    if (generatedHull != null) dryMesh = generatedHull;
                }
                AddDryInterior(boat.transform, hullCenterLocal, hullSize, hullModel != null,
                               dryMesh, visual != null ? visual.transform : null);
                // AddDryInterior saved a NORMALISED copy as the asset; the raw hull is scratch.
                if (generatedHull != null) Object.DestroyImmediate(generatedHull);
            }
            return boat;
        }

        // Point the interactable at the renderer whose MeshFilter holds the NAMED mesh - the
        // wizard offers the same mesh-picking UX as the dry interior, but the component needs a
        // RENDERER (bounds + silhouette draws), so the mesh is translated here where the
        // instantiated visual exists to search. Falls back loudly to the component's own
        // auto-resolve when the mesh is not found under the visual (wrong model assigned).
        static void WireInteractionRenderer(WaterInteractable interactable, Mesh interactionMesh,
                                            GameObject visual)
        {
            if (interactionMesh == null || visual == null) return;
            foreach (MeshFilter filter in visual.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.sharedMesh != interactionMesh) continue;
                Renderer renderer = filter.GetComponent<Renderer>();
                if (renderer != null) { interactable.rendererOverride = renderer; return; }
            }
            Debug.LogWarning(LogPrefix + $"Interaction mesh '{interactionMesh.name}' was not found " +
                             "under the hull model; the interactable auto-resolves its renderer instead.");
        }

        // The "boat doesn't fill with water" step: a WaterExclusionVolume over the hull so the
        // surface sheet never renders inside it. Sized from the SAME fitted box physics uses -
        // inset (primitive hull) or shrunk (custom model) so the cut edge stays behind the hull
        // walls. Visual-only (buoyancy reads the collider, not this); resize or delete the child
        // freely to fit an open cockpit. Creation is undo-registered like every build step.
        static void AddDryInterior(Transform root, Vector3 hullCenterLocal, Vector3 hullSize, bool customHull,
                                   Mesh dryMesh = null, Transform visual = null)
        {
            if (dryMesh != null && visual == null)
            {
                Debug.LogWarning(LogPrefix + "Dry interior mesh is only used with a custom hull model; " +
                                 "falling back to the fitted box.");
                dryMesh = null;
            }

            var dry = NewUndoableGameObject(BoatDryInteriorName);
            if (dryMesh != null)
            {
                // Parent under the VISUAL child: the proxy is authored in the model's own frame,
                // and the visual already carries the model-forward yaw - the carve inherits both
                // for free instead of re-deriving them here.
                dry.transform.SetParent(visual, worldPositionStays: false);
                Bounds proxyBounds = dryMesh.bounds;
                dry.transform.localPosition = proxyBounds.center;

                EnsureFolder(BoatAssetsRoot);
                var meshVolume = dry.AddComponent<WaterExclusionVolume>();
                meshVolume.shape = WaterExclusionVolume.Shape.Mesh;
                meshVolume.carveMesh = SaveAsset(BuildNormalizedCarveMesh(dryMesh),
                                                 BoatAssetsRoot + "/" + dryMesh.name + DryInteriorMeshSuffix + ".asset");
                meshVolume.meshProxy = WaterExclusionVolume.Shape.Box; // sun shadow / particles / CPU point test
                meshVolume.size = Vector3.Max(proxyBounds.size * DryInteriorMeshShrink,
                                              DryInteriorMinEdge * Vector3.one);
                meshVolume.drawWaterWalls = false; // same content rule as the box path below
                return;
            }

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

        // Normalised copy of the proxy for the carve-mesh contract (-0.5..0.5 span): vertices
        // recentred and divided by the bounds; triangles/winding untouched (positive scale).
        // The ORIGINAL bounds become the volume's Size, so the carve lands exactly where the
        // proxy was authored. SaveAsset overwrites the Boats copy on rebuild, so an edited
        // proxy regenerates instead of serving a stale normalisation.
        static Mesh BuildNormalizedCarveMesh(Mesh source)
        {
            Bounds bounds = source.bounds;
            Vector3 inverseSize = new Vector3(
                1f / Mathf.Max(bounds.size.x, MinCarveMeshSpan),
                1f / Mathf.Max(bounds.size.y, MinCarveMeshSpan),
                1f / Mathf.Max(bounds.size.z, MinCarveMeshSpan));
            Vector3[] vertices = source.vertices;
            for (int i = 0; i < vertices.Length; i++)
                vertices[i] = Vector3.Scale(vertices[i] - bounds.center, inverseSize);

            var normalized = new Mesh { name = source.name + DryInteriorMeshSuffix };
            normalized.indexFormat = source.indexFormat;
            normalized.vertices = vertices;
            normalized.triangles = source.triangles;
            normalized.RecalculateBounds();
            normalized.RecalculateNormals();
            return normalized;
        }

        // Instantiate the hull visual under the boat root (prefab-linked when the source is a
        // prefab asset, plain clone otherwise) at local identity - the ROOT owns placement; the
        // caller may then yaw the child (model-forward correction) BEFORE anything reads bounds.
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
