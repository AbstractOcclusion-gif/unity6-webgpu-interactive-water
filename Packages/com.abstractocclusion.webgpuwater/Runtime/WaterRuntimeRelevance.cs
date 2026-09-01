// WebGpuWater - shared per-camera body relevance, grants and read-only diagnostics.
// One ranked snapshot feeds simulation, planar reflections, screen-space caustics and the
// after-fog foam overlay so those systems cannot silently disagree about which body matters.
using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal readonly struct WaterRuntimeBudgets
    {
        internal readonly int SimulationBodies;
        internal readonly int PlanarReflectionBodies;
        internal readonly int CausticProjectionBodies;

        internal WaterRuntimeBudgets(int simulationBodies, int planarReflectionBodies,
                                     int causticProjectionBodies)
        {
            SimulationBodies = simulationBodies;
            PlanarReflectionBodies = planarReflectionBodies;
            CausticProjectionBodies = causticProjectionBodies;
        }
    }

    internal readonly struct WaterRuntimeCounters
    {
        internal readonly int Registered;
        internal readonly int Visible;
        internal readonly int SimulationEligible;
        internal readonly int SimulationGranted;
        internal readonly int PlanarWanted;
        internal readonly int PlanarGranted;
        internal readonly int CausticWanted;
        internal readonly int CausticGranted;
        internal readonly int FoamWanted;
        internal readonly int FoamDrawn;
        internal readonly int FoamCulled;
        internal readonly int FogSources;

        internal WaterRuntimeCounters(int registered, int visible, int simulationEligible,
                                      int simulationGranted, int planarWanted, int planarGranted,
                                      int causticWanted, int causticGranted, int foamWanted,
                                      int foamDrawn, int foamCulled, int fogSources)
        {
            Registered = registered;
            Visible = visible;
            SimulationEligible = simulationEligible;
            SimulationGranted = simulationGranted;
            PlanarWanted = planarWanted;
            PlanarGranted = planarGranted;
            CausticWanted = causticWanted;
            CausticGranted = causticGranted;
            FoamWanted = foamWanted;
            FoamDrawn = foamDrawn;
            FoamCulled = foamCulled;
            FogSources = fogSources;
        }
    }

    internal struct WaterRuntimeBodySnapshot
    {
        internal WaterVolume Body;
        internal int RegistrationOrder;
        internal int RelevanceRank;
        internal bool Registered;
        internal bool FrustumVisible;
        internal bool RenderVisible;
        internal bool ImportancePinned;
        internal bool CameraSubmerged;
        internal bool CausticInfluenceVisible;
        internal float NearestBoundsDistance;
        internal float ScreenCoverage;
        internal bool SimulationEligible;
        internal bool SimulationGranted;
        internal bool PlanarWanted;
        internal bool PlanarGranted;
        internal bool CausticWanted;
        internal bool CausticGranted;
        internal bool FoamWanted;
        internal bool FoamDrawn;
        internal bool FoamCulled;
        internal bool SelectedFogSource;
    }

    internal static class WaterRuntimeRelevance
    {
        internal const int PlanarReflectionBudget = 3;
        const int InvalidFrame = -1;
        const int InvalidRegistrationVersion = -1;
        const int FirstRelevanceRank = 1;
        const int FrustumPlaneCount = 6;
        const int BoundsCornerCount = 8;
        const int CornerXMask = 1;
        const int CornerYMask = 2;
        const int CornerZMask = 4;
        const float FullScreenCoverage = 1f;
        const float MinimumViewportCoordinate = 0f;
        const float MaximumViewportCoordinate = 1f;
        const float MinimumForwardDepth = 0f;

        static readonly ProfilerMarker BuildMarker =
            new ProfilerMarker("WebGpuWater.RuntimeRelevance.Build");
        static readonly ProfilerMarker CausticCollectionMarker =
            new ProfilerMarker("WebGpuWater.CausticProjection.CollectBodies");
        static readonly ProfilerMarker FoamCollectionMarker =
            new ProfilerMarker("WebGpuWater.FoamOverlay.CollectRenderers");
        static readonly List<CameraSnapshot> CameraSnapshots = new List<CameraSnapshot>();
        static readonly BodyComparer RelevanceComparer = new BodyComparer();

        static int _registrationVersion;

        internal static IReadOnlyList<WaterRuntimeBodySnapshot> GetSnapshot(Camera camera)
            => EnsureSnapshot(camera).Bodies;

        internal static WaterRuntimeBudgets GetBudgets(Camera camera)
            => EnsureSnapshot(camera).Budgets;

        internal static WaterRuntimeCounters GetCounters(Camera camera)
        {
            List<WaterRuntimeBodySnapshot> bodies = EnsureSnapshot(camera).Bodies;
            int visible = 0;
            int simulationEligible = 0;
            int simulationGranted = 0;
            int planarWanted = 0;
            int planarGranted = 0;
            int causticWanted = 0;
            int causticGranted = 0;
            int foamWanted = 0;
            int foamDrawn = 0;
            int foamCulled = 0;
            int fogSources = 0;
            for (int i = 0; i < bodies.Count; i++)
            {
                WaterRuntimeBodySnapshot body = bodies[i];
                if (body.RenderVisible) visible++;
                if (body.SimulationEligible) simulationEligible++;
                if (body.SimulationGranted) simulationGranted++;
                if (body.PlanarWanted) planarWanted++;
                if (body.PlanarGranted) planarGranted++;
                if (body.CausticWanted) causticWanted++;
                if (body.CausticGranted) causticGranted++;
                if (body.FoamWanted) foamWanted++;
                if (body.FoamDrawn) foamDrawn++;
                if (body.FoamCulled) foamCulled++;
                if (body.SelectedFogSource) fogSources++;
            }
            return new WaterRuntimeCounters(
                bodies.Count, visible, simulationEligible, simulationGranted,
                planarWanted, planarGranted, causticWanted, causticGranted,
                foamWanted, foamDrawn, foamCulled, fogSources);
        }

        internal static void ApplySimulationSchedule(Camera camera)
        {
            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot = GetSnapshot(camera);
            for (int i = 0; i < snapshot.Count; i++)
            {
                WaterRuntimeBodySnapshot state = snapshot[i];
                if (state.Body == null) continue;
                state.Body._visible = state.RenderVisible;
                state.Body._simulate = state.SimulationGranted;
            }
        }

        internal static bool IsPlanarGranted(WaterVolume body, Camera camera)
            => TryGetBodyState(EnsureSnapshot(camera), body, out WaterRuntimeBodySnapshot state)
               && state.PlanarGranted;

        internal static bool AnyCausticProjectionWork(Camera camera, bool includeCaustics,
                                                       bool includeRefractedShadows)
        {
            using (CausticCollectionMarker.Auto())
            {
                IReadOnlyList<WaterRuntimeBodySnapshot> snapshot = GetSnapshot(camera);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    WaterRuntimeBodySnapshot state = snapshot[i];
                    if (!state.CausticGranted || state.Body == null) continue;
                    if (includeCaustics && state.Body.WantsCausticLightProjection) return true;
                    if (includeRefractedShadows && state.Body.WantsRefractedShadowProjection)
                        return true;
                }
                return false;
            }
        }

        internal static void CollectCausticProjectionBodies(
            Camera camera, List<WaterVolume> causticBodies,
            List<WaterVolume> refractedShadowBodies, bool includeCaustics,
            bool includeRefractedShadows)
        {
            if (causticBodies == null) throw new ArgumentNullException(nameof(causticBodies));
            if (refractedShadowBodies == null)
                throw new ArgumentNullException(nameof(refractedShadowBodies));

            using (CausticCollectionMarker.Auto())
            {
                causticBodies.Clear();
                refractedShadowBodies.Clear();
                IReadOnlyList<WaterRuntimeBodySnapshot> snapshot = GetSnapshot(camera);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    WaterRuntimeBodySnapshot state = snapshot[i];
                    WaterVolume body = state.Body;
                    if (!state.CausticGranted || body == null) continue;
                    if (includeCaustics && body.WantsCausticLightProjection)
                        causticBodies.Add(body);
                    if (includeRefractedShadows && body.WantsRefractedShadowProjection)
                        refractedShadowBodies.Add(body);
                }
            }
        }

        internal static bool AnyFoamOverlayBody(Camera camera)
        {
            IReadOnlyList<WaterRuntimeBodySnapshot> snapshot = GetSnapshot(camera);
            for (int i = 0; i < snapshot.Count; i++)
                if (snapshot[i].FoamWanted && snapshot[i].FrustumVisible) return true;
            return false;
        }

        internal static void CollectFoamOverlayRenderers(Camera camera, List<Renderer> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));

            using (FoamCollectionMarker.Auto())
            {
                into.Clear();
                CameraSnapshot snapshot = EnsureSnapshot(camera);
                for (int i = 0; i < snapshot.Bodies.Count; i++)
                {
                    WaterRuntimeBodySnapshot state = snapshot.Bodies[i];
                    WaterVolume body = state.Body;
                    if (!state.FoamWanted || !state.FrustumVisible || body == null) continue;
                    int rendererCountBefore = into.Count;
                    if (body.Foam || body.RiverMouthOutflowCount > 0)
                        body.CollectAboveSurfaceRenderers(into);
                    body.CollectExternalFoamRenderers(into);
                    state.FoamDrawn = into.Count > rendererCountBefore;
                    state.FoamCulled = !state.FoamDrawn;
                    snapshot.Bodies[i] = state;
                }
            }
        }

        internal static float NearestBoundsDistanceSquared(Bounds bounds, Vector3 point)
            => bounds.SqrDistance(point);

        internal static void InvalidateRegistrations() => _registrationVersion++;

        internal static void ResetStaticState()
        {
            CameraSnapshots.Clear();
            _registrationVersion = 0;
        }

        internal static IReadOnlyList<WaterRuntimeBodySnapshot> RebuildForTests(
            Camera camera, int simulationBudget, int causticBudget)
        {
            CameraSnapshot snapshot = FindOrCreateSnapshot(camera);
            BuildSnapshot(snapshot, camera, simulationBudget, causticBudget);
            return snapshot.Bodies;
        }

        internal static void ApplyGrantsForTests(List<WaterRuntimeBodySnapshot> bodies,
                                                 int simulationBudget, int causticBudget)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            var snapshot = new CameraSnapshot(null)
            {
                Budgets = new WaterRuntimeBudgets(
                    simulationBudget, PlanarReflectionBudget, causticBudget),
            };
            snapshot.Bodies.AddRange(bodies);
            snapshot.Bodies.Sort(RelevanceComparer);
            ApplyGrants(snapshot);
            bodies.Clear();
            bodies.AddRange(snapshot.Bodies);
        }

        static CameraSnapshot EnsureSnapshot(Camera camera)
        {
            CameraSnapshot snapshot = FindOrCreateSnapshot(camera);
            WaterRuntimeBudgets budgets = ResolveBudgets();
            bool fogChanged = snapshot.FogSource != WaterVolume.FogSource
                           || snapshot.CameraSubmerged != WaterVolume.CameraSubmerged;
            bool cameraChanged = CameraChanged(snapshot, camera);
            bool budgetChanged = snapshot.Budgets.SimulationBodies != budgets.SimulationBodies
                              || snapshot.Budgets.CausticProjectionBodies !=
                                 budgets.CausticProjectionBodies;
            if (snapshot.Frame != Time.frameCount || snapshot.RegistrationVersion != _registrationVersion
                || fogChanged || cameraChanged || budgetChanged)
                BuildSnapshot(snapshot, camera, budgets.SimulationBodies,
                              budgets.CausticProjectionBodies);
            return snapshot;
        }

        static CameraSnapshot FindOrCreateSnapshot(Camera camera)
        {
            for (int i = 0; i < CameraSnapshots.Count; i++)
            {
                CameraSnapshot snapshot = CameraSnapshots[i];
                if (snapshot.Camera == camera) return snapshot;
                if (snapshot.Camera == null && camera != null)
                {
                    snapshot.Camera = camera;
                    snapshot.Frame = InvalidFrame;
                    return snapshot;
                }
            }
            var created = new CameraSnapshot(camera);
            CameraSnapshots.Add(created);
            return created;
        }

        static void BuildSnapshot(CameraSnapshot snapshot, Camera camera, int simulationBudget,
                                  int causticBudget)
        {
            using (BuildMarker.Auto())
            {
                snapshot.Bodies.Clear();
                snapshot.Camera = camera;
                snapshot.Frame = Time.frameCount;
                snapshot.RegistrationVersion = _registrationVersion;
                snapshot.FogSource = WaterVolume.FogSource;
                snapshot.CameraSubmerged = WaterVolume.CameraSubmerged;
                snapshot.Budgets = new WaterRuntimeBudgets(
                    Mathf.Max(0, simulationBudget), PlanarReflectionBudget,
                    Mathf.Max(0, causticBudget));
                CaptureCameraState(snapshot, camera);

                bool hasCamera = camera != null;
                if (hasCamera)
                    GeometryUtility.CalculateFrustumPlanes(camera, snapshot.FrustumPlanes);

                IReadOnlyList<WaterVolume> registeredBodies = WaterVolume.Bodies;
                for (int i = 0; i < registeredBodies.Count; i++)
                {
                    WaterVolume body = registeredBodies[i];
                    if (body == null) continue;
                    snapshot.Bodies.Add(BuildBodyState(body, i, camera, snapshot.FrustumPlanes));
                }

                snapshot.Bodies.Sort(RelevanceComparer);
                ApplyGrants(snapshot);
            }
        }

        static WaterRuntimeBodySnapshot BuildBodyState(WaterVolume body, int registrationOrder,
                                                        Camera camera, Plane[] frustumPlanes)
        {
            Bounds bounds = body.CullBounds();
            bool hasCamera = camera != null;
            bool frustumVisible = !hasCamera ||
                                  GeometryUtility.TestPlanesAABB(frustumPlanes, bounds);
            float nearestDistanceSquared = hasCamera
                ? NearestBoundsDistanceSquared(bounds, camera.transform.position)
                : float.PositiveInfinity;
            float nearestDistance = Mathf.Sqrt(nearestDistanceSquared);
            bool renderVisible = !body.EnableCulling || frustumVisible;
            bool simulationEligible = !body.EnableCulling ||
                                      (frustumVisible && (body.IsOceanClipmap ||
                                       nearestDistance <= Mathf.Max(0f, body.activationDistance)));
            bool foamWanted = body.WantsFoamOverlay;
            bool fogSource = body == WaterVolume.FogSource;

            return new WaterRuntimeBodySnapshot
            {
                Body = body,
                RegistrationOrder = registrationOrder,
                Registered = true,
                FrustumVisible = frustumVisible,
                RenderVisible = renderVisible,
                ImportancePinned = body.RuntimeImportancePin,
                CameraSubmerged = fogSource && WaterVolume.CameraSubmerged,
                // CullBounds is the complete water column, not just the surface sheet. A failed
                // frustum test therefore proves that no reconstructed scene pixel can lie in the
                // projection footprint; a missing camera remains conservatively relevant.
                CausticInfluenceVisible = frustumVisible,
                NearestBoundsDistance = nearestDistance,
                ScreenCoverage = hasCamera && frustumVisible
                    ? EstimateScreenCoverage(camera, bounds) : 0f,
                SimulationEligible = simulationEligible,
                PlanarWanted = body.WantsPlanar,
                CausticWanted = body.WantsAnyCausticProjection,
                FoamWanted = foamWanted,
                FoamCulled = foamWanted && !frustumVisible,
                SelectedFogSource = fogSource,
            };
        }

        static void ApplyGrants(CameraSnapshot snapshot)
        {
            int simulationGrants = 0;
            int planarGrants = 0;
            int causticGrants = 0;
            for (int i = 0; i < snapshot.Bodies.Count; i++)
            {
                WaterRuntimeBodySnapshot body = snapshot.Bodies[i];
                body.RelevanceRank = i + FirstRelevanceRank;
                if (body.SimulationEligible)
                {
                    // Disabling culling is the existing explicit force-on escape hatch. It remains
                    // outside the finite budget so this production slice does not change its meaning.
                    bool forced = body.Body != null && !body.Body.EnableCulling;
                    body.SimulationGranted = forced ||
                        simulationGrants < snapshot.Budgets.SimulationBodies;
                    if (!forced && body.SimulationGranted) simulationGrants++;
                }
                if (body.PlanarWanted && body.RenderVisible &&
                    planarGrants < snapshot.Budgets.PlanarReflectionBodies)
                {
                    body.PlanarGranted = true;
                    planarGrants++;
                }
                if (body.CausticWanted && body.CausticInfluenceVisible &&
                    causticGrants < snapshot.Budgets.CausticProjectionBodies)
                {
                    body.CausticGranted = true;
                    causticGrants++;
                }
                snapshot.Bodies[i] = body;
            }
        }

        static WaterRuntimeBudgets ResolveBudgets()
        {
            WaterVolume owner = WaterVolume.Primary;
            if (owner == null && WaterVolume.Bodies.Count > 0) owner = WaterVolume.Bodies[0];
            int simulationBudget = owner != null
                ? owner.MaxSimulatedBodies : WaterQuality.Default.MaxSimulatedBodies;
            int causticBudget = owner != null
                ? owner.MaxCausticProjectionBodies : WaterQuality.Default.MaxCausticProjectionBodies;
            return new WaterRuntimeBudgets(
                simulationBudget, PlanarReflectionBudget, causticBudget);
        }

        static bool TryGetBodyState(CameraSnapshot snapshot, WaterVolume body,
                                    out WaterRuntimeBodySnapshot state)
        {
            for (int i = 0; i < snapshot.Bodies.Count; i++)
            {
                state = snapshot.Bodies[i];
                if (state.Body == body) return true;
            }
            state = default;
            return false;
        }

        static bool CameraChanged(CameraSnapshot snapshot, Camera camera)
        {
            if (camera == null) return snapshot.CameraPosition != default;
            return snapshot.CameraPosition != camera.transform.position
                || snapshot.CameraRotation != camera.transform.rotation
                || !Mathf.Approximately(snapshot.FieldOfView, camera.fieldOfView)
                || !Mathf.Approximately(snapshot.Aspect, camera.aspect)
                || snapshot.Orthographic != camera.orthographic
                || !Mathf.Approximately(snapshot.OrthographicSize, camera.orthographicSize);
        }

        static void CaptureCameraState(CameraSnapshot snapshot, Camera camera)
        {
            if (camera == null)
            {
                snapshot.CameraPosition = default;
                snapshot.CameraRotation = default;
                snapshot.FieldOfView = 0f;
                snapshot.Aspect = 0f;
                snapshot.Orthographic = false;
                snapshot.OrthographicSize = 0f;
                return;
            }
            snapshot.CameraPosition = camera.transform.position;
            snapshot.CameraRotation = camera.transform.rotation;
            snapshot.FieldOfView = camera.fieldOfView;
            snapshot.Aspect = camera.aspect;
            snapshot.Orthographic = camera.orthographic;
            snapshot.OrthographicSize = camera.orthographicSize;
        }

        static float EstimateScreenCoverage(Camera camera, Bounds bounds)
        {
            if (bounds.Contains(camera.transform.position)) return FullScreenCoverage;

            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            float minX = MaximumViewportCoordinate;
            float minY = MaximumViewportCoordinate;
            float maxX = MinimumViewportCoordinate;
            float maxY = MinimumViewportCoordinate;
            bool anyInFront = false;
            bool anyBehind = false;
            for (int corner = 0; corner < BoundsCornerCount; corner++)
            {
                Vector3 offset = new Vector3(
                    (corner & CornerXMask) == 0 ? -extents.x : extents.x,
                    (corner & CornerYMask) == 0 ? -extents.y : extents.y,
                    (corner & CornerZMask) == 0 ? -extents.z : extents.z);
                Vector3 viewport = camera.WorldToViewportPoint(center + offset);
                if (viewport.z <= MinimumForwardDepth)
                {
                    anyBehind = true;
                    continue;
                }
                anyInFront = true;
                minX = Mathf.Min(minX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxX = Mathf.Max(maxX, viewport.x);
                maxY = Mathf.Max(maxY, viewport.y);
            }
            if (!anyInFront) return 0f;
            // A box crossing the near plane can cover the screen between corners; treating it as
            // full coverage is conservative and avoids unstable priority while the camera enters it.
            if (anyBehind) return FullScreenCoverage;
            float width = Mathf.Clamp01(maxX) - Mathf.Clamp01(minX);
            float height = Mathf.Clamp01(maxY) - Mathf.Clamp01(minY);
            return Mathf.Clamp01(Mathf.Max(0f, width) * Mathf.Max(0f, height));
        }

        sealed class BodyComparer : IComparer<WaterRuntimeBodySnapshot>
        {
            public int Compare(WaterRuntimeBodySnapshot left, WaterRuntimeBodySnapshot right)
            {
                int result = CompareDescending(left.ImportancePinned, right.ImportancePinned);
                if (result != 0) return result;
                result = CompareDescending(left.CameraSubmerged, right.CameraSubmerged);
                if (result != 0) return result;
                result = CompareDescending(left.FrustumVisible, right.FrustumVisible);
                if (result != 0) return result;
                result = right.ScreenCoverage.CompareTo(left.ScreenCoverage);
                if (result != 0) return result;
                result = left.NearestBoundsDistance.CompareTo(right.NearestBoundsDistance);
                return result != 0 ? result : left.RegistrationOrder.CompareTo(right.RegistrationOrder);
            }

            static int CompareDescending(bool left, bool right) => right.CompareTo(left);
        }

        sealed class CameraSnapshot
        {
            internal Camera Camera;
            internal readonly Plane[] FrustumPlanes = new Plane[FrustumPlaneCount];
            internal readonly List<WaterRuntimeBodySnapshot> Bodies =
                new List<WaterRuntimeBodySnapshot>();
            internal int Frame = InvalidFrame;
            internal int RegistrationVersion = InvalidRegistrationVersion;
            internal WaterVolume FogSource;
            internal bool CameraSubmerged;
            internal WaterRuntimeBudgets Budgets;
            internal Vector3 CameraPosition;
            internal Quaternion CameraRotation;
            internal float FieldOfView;
            internal float Aspect;
            internal bool Orthographic;
            internal float OrthographicSize;

            internal CameraSnapshot(Camera camera) => Camera = camera;
        }
    }
}
