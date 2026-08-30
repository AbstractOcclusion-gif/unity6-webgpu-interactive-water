// WebGpuWater build kit - one-click RIVER creation (river harmonization round, 2026-08-29).
//
// THE river authoring recipe lives here once (reuse-never-rewrite): the GameObject-menu creator
// and the Connected Waters test rig both build through CreateRiverRig, so the demo documents the
// exact path a user's one-click river takes. The rig is the facade way: spline + current field +
// ribbon surface + the WaterRiver facade that owns wiring and connect-to-body.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string RiverMenuPath = "GameObject/AbstractOcclusion/River";
        const int RiverMenuPriority = 12; // right under the Connected Waters rig
        const string RiverGameObjectName = "River (spline ribbon)";

        // Default authoring shape: three knots descending onto the parent body's -Z edge -
        // the same silhouette the Connected Waters rig ships, so a fresh river reads as a river
        // immediately and the user only drags knots.
        static readonly Vector3 DefaultKnotHighOffset = new Vector3(-3f, 4.2f, -24f);
        static readonly Vector3 DefaultKnotMidOffset = new Vector3(-1f, 2.2f, -16f);
        static readonly Vector3 DefaultKnotMouthOffset = new Vector3(0f, 0.1f, -9f);
        static readonly Vector3 DefaultKnotTangent = new Vector3(0.6f, -0.6f, 2.6f);
        const float DefaultRiverWidthMeters = 3f;
        const float DefaultRiverSpeedMetersPerSecond = 1.5f;

        [MenuItem(RiverMenuPath, false, RiverMenuPriority)]
        static void CreateRiverFromMenu()
        {
            int undoGroup = Undo.GetCurrentGroup();

            // Parent: the selected body when one is picked, else the scene's primary. Null is a
            // legal standalone river - the facade warns about what that means.
            WaterVolume parentVolume = Selection.activeGameObject != null
                ? Selection.activeGameObject.GetComponentInParent<WaterVolume>() : null;
            if (parentVolume == null) parentVolume = WaterVolume.Resolve();

            // Asset half only (materials/quality) - creating a river in an EXISTING scene must
            // not rig a camera or sun the way the demo builders do.
            if (!TryBuildSharedAssets(CreateUniqueWaterFolder(), buildPoolMaterial: false,
                                      out BuildContext ctx))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            Vector3 basePoint = parentVolume != null ? parentVolume.VolumeCenter : Vector3.zero;
            WaterRiver river = CreateRiverRig(null, parentVolume, ctx.MatAbove, ctx.MatUnder,
                                              DefaultKnots(basePoint));

            // The "connect to body" half of one-click: a parented river arrives already ported
            // into its body at the mouth.
            if (parentVolume != null)
            {
                river.mouthEnd.body = parentVolume;
                WaterRiverEditor.GenerateEnd(river, WaterRiverEndKind.Mouth);
            }

            Selection.activeGameObject = river.gameObject;
            Undo.CollapseUndoOperations(undoGroup);
        }

        static List<WaterRiverKnot> DefaultKnots(Vector3 basePoint) => new List<WaterRiverKnot>
        {
            new WaterRiverKnot(basePoint + DefaultKnotHighOffset, DefaultKnotTangent,
                               DefaultRiverWidthMeters, DefaultRiverSpeedMetersPerSecond),
            new WaterRiverKnot(basePoint + DefaultKnotMidOffset, DefaultKnotTangent,
                               DefaultRiverWidthMeters, DefaultRiverSpeedMetersPerSecond),
            new WaterRiverKnot(basePoint + DefaultKnotMouthOffset, DefaultKnotTangent,
                               DefaultRiverWidthMeters, DefaultRiverSpeedMetersPerSecond),
        };

        // The one river recipe. Components in dependency order, facade LAST so RequireComponent
        // finds the trio already present instead of auto-adding unconfigured duplicates.
        internal static WaterRiver CreateRiverRig(Transform parent, WaterVolume parentVolume,
                                                  Material surfaceMaterial,
                                                  Material underSurfaceMaterial,
                                                  List<WaterRiverKnot> knots)
        {
            var riverGO = NewUndoableGameObject(RiverGameObjectName);
            if (parent != null) riverGO.transform.SetParent(parent);

            var spline = riverGO.AddComponent<WaterRiverSpline>();
            spline.knots = knots;

            var currentField = riverGO.AddComponent<WaterRiverCurrentField>();
            currentField.Configure(spline);

            var surface = riverGO.AddComponent<WaterRiverSurface>();
            // The under material is the body's cull-front twin: a submerged camera looking up
            // must see the river surface, exactly as it sees a body's.
            surface.Configure(spline, parentVolume, surfaceMaterial, underSurfaceMaterial,
                              WaterRiverSurface.DefaultSamplesPerSegment);

            var facade = riverGO.AddComponent<WaterRiver>();
            facade.parentVolume = parentVolume;
            facade.ApplyWiring();

            // Serialized so the link is visible in the saved scene; APPEND, never assign - the
            // parent may carry other authored current fields (the facade repeats this rule for
            // its play-mode attach).
            AppendCurrentFieldSerialized(parentVolume, currentField);
            return facade;
        }

        static void AppendCurrentFieldSerialized(WaterVolume body, WaterCurrentField field)
        {
            if (body == null || field == null) return;
            WaterCurrentField[] fields = body.currentFields ?? System.Array.Empty<WaterCurrentField>();
            if (System.Array.IndexOf(fields, field) >= 0) return;

            Undo.RecordObject(body, "Link River Current Field");
            var grown = new WaterCurrentField[fields.Length + 1];
            System.Array.Copy(fields, grown, fields.Length);
            grown[fields.Length] = field;
            body.currentFields = grown;
            EditorUtility.SetDirty(body);
        }
    }
}
