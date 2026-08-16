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

        static WaterRiverAuthoringChangeRouter()
        {
            Undo.postprocessModifications += RouteTransformChanges;
        }

        // Transform edits do not call a sibling component's OnValidate. Routing the editor's existing
        // Undo event keeps scale-compensated geometry current without an Update-time transform poll.
        static UndoPropertyModification[] RouteTransformChanges(
            UndoPropertyModification[] modifications)
        {
            ChangedSplines.Clear();
            ChangedSurfaces.Clear();
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
            }

            foreach (WaterRiverSpline spline in ChangedSplines) spline.NotifyChanged();
            foreach (WaterRiverSurface surface in ChangedSurfaces)
                if (surface.spline == null || !ChangedSplines.Contains(surface.spline))
                    surface.RequestRebuild();
            return modifications;
        }
    }
}
#endif
