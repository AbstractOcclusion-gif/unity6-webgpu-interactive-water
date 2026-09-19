// WebGpuWater - event-driven Transform changes for river authoring.
#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    [InitializeOnLoad]
    internal static class WaterRiverAuthoringChangeRouter
    {
        static readonly HashSet<WaterRiverSpline> ChangedSplines = new HashSet<WaterRiverSpline>();
        static readonly HashSet<WaterRiverSurface> ChangedSurfaces = new HashSet<WaterRiverSurface>();
        static readonly HashSet<WaterVolume> ChangedBodies = new HashSet<WaterVolume>();

        static WaterRiverAuthoringChangeRouter()
        {
            Undo.postprocessModifications += RouteTransformChanges;
        }

        // Transform edits do not call a sibling component's OnValidate. Routing the editor's existing
        // Undo event keeps scale-compensated geometry current without an Update-time transform poll.
        // A moved river root reaches its facade through the spline's Changed event (the facade
        // subscribes); a moved BODY is not under any river, so it is forwarded here to every
        // river whose end targets it.
        static UndoPropertyModification[] RouteTransformChanges(
            UndoPropertyModification[] modifications)
        {
            ChangedSplines.Clear();
            ChangedSurfaces.Clear();
            ChangedBodies.Clear();
            for (int i = 0; i < modifications.Length; i++)
            {
                Object target = modifications[i].currentValue.target;
                if (target is not Transform changedTransform) continue;

                WaterRiverSpline[] splines =
                    changedTransform.GetComponentsInChildren<WaterRiverSpline>(includeInactive: true);
                for (int splineIndex = 0; splineIndex < splines.Length; splineIndex++)
                    ChangedSplines.Add(splines[splineIndex]);

                WaterRiverSurface[] surfaces =
                    changedTransform.GetComponentsInChildren<WaterRiverSurface>(includeInactive: true);
                for (int surfaceIndex = 0; surfaceIndex < surfaces.Length; surfaceIndex++)
                    ChangedSurfaces.Add(surfaces[surfaceIndex]);

                WaterVolume[] bodies =
                    changedTransform.GetComponentsInChildren<WaterVolume>(includeInactive: true);
                for (int bodyIndex = 0; bodyIndex < bodies.Length; bodyIndex++)
                    ChangedBodies.Add(bodies[bodyIndex]);
            }

            foreach (WaterRiverSpline spline in ChangedSplines) spline.NotifyChanged();
            foreach (WaterRiverSurface surface in ChangedSurfaces)
                if (surface.spline == null || !ChangedSplines.Contains(surface.spline))
                    surface.RequestRebuild();
            ReaimRiversTargeting(ChangedBodies);
            return modifications;
        }

        // The scene scan runs only when a body actually moved (rare compared with arbitrary
        // transform edits), so idle editing pays nothing for it. Disabled facades are skipped:
        // their OnEnable re-syncs everything when they come back.
        static void ReaimRiversTargeting(HashSet<WaterVolume> movedBodies)
        {
            if (movedBodies.Count == 0) return;
            WaterRiver[] rivers = Object.FindObjectsByType<WaterRiver>(FindObjectsSortMode.None);
            for (int riverIndex = 0; riverIndex < rivers.Length; riverIndex++)
            {
                WaterRiver river = rivers[riverIndex];
                if (!river.isActiveAndEnabled) continue;
                foreach (WaterVolume body in movedBodies)
                {
                    if (!river.HasEndBody(body)) continue;
                    river.SyncGeneratedConnections();
                    break;
                }
            }
        }
    }
}
#endif
