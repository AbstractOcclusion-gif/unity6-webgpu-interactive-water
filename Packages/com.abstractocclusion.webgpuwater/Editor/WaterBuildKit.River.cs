// WebGpuWater build kit - one-click RIVER creation (river harmonization round, 2026-08-29).
//
// THE river authoring recipe lives here once (reuse-never-rewrite): the GameObject-menu creator,
// the Connected Waters test rig and the wizard's Water System section all build through
// CreateConnectedRiver (rig + connect-at-each-end over CreateRiverRig), so the demo documents
// the exact path a user's one-click river takes. The rig is the facade way: spline + current field +
// ribbon surface + the WaterRiver facade that owns wiring and connect-to-body.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string RiverMenuPath = GameObjectMenuRoot + "River";
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

            if (!TryResolveRiverMaterials(parentVolume, out Material above, out Material under))
            {
                Undo.RevertAllDownToGroup(undoGroup);
                return;
            }

            // THE connected-river recipe: a parented river arrives already ported into its body
            // at the mouth, at the connection's own default seam radius; a standalone river
            // (no parent) simply has no mouth target.
            Vector3 basePoint = parentVolume != null ? parentVolume.VolumeCenter : Vector3.zero;
            WaterRiver river = CreateConnectedRiver(null, RiverGameObjectName, DefaultKnots(basePoint),
                                                    parentVolume, above, under, sourceBody: null,
                                                    mouthBody: parentVolume, upstreamRiver: null,
                                                    transitionRadiusMeters: WaterConnection.DefaultTransitionRadiusMeters,
                                                    withProceduralFoam: false);

            Selection.activeGameObject = river.gameObject;
            Undo.CollapseUndoOperations(undoGroup);
        }

        // A river shares its PARENT body's surface materials (one look, one folder - the demo
        // rig has always passed the same two materials to bodies and rivers). Only a parentless
        // river, or a hand-built parent without materials, gets its own folder; that used to
        // happen for EVERY menu river, leaving a fresh 'Waters/Water N' per river.
        static bool TryResolveRiverMaterials(WaterVolume parentVolume, out Material above, out Material under)
        {
            if (TryResolveBodyMaterials(parentVolume, out above, out under)) return true;

            // Asset half only (materials/quality) - creating a river in an EXISTING scene must
            // not rig a camera or sun the way the demo builders do.
            if (!TryBuildSharedAssets(CreateUniqueWaterFolder(), buildPoolMaterial: false,
                                      out BuildContext ctx))
                return false;
            above = ctx.MatAbove;
            under = ctx.MatUnder;
            return true;
        }

        // One connected river: THE recipe (facade-owned wiring), then connect to a target at each
        // named end. The explicit parent body supplies animated uniforms and the underwater
        // medium; both halves of a river-to-river stitch intentionally share it. The source is
        // ported into a body OR sewn onto an upstream river's mouth (the facade rejects both);
        // the upstream river's mouth must already be generated - its row is what the source copies.
        internal static WaterRiver CreateConnectedRiver(Transform parent, string riverName,
                                                        List<WaterRiverKnot> knots, WaterVolume parentBody,
                                                        Material surfaceMaterial, Material underSurfaceMaterial,
                                                        WaterVolume sourceBody, WaterVolume mouthBody,
                                                        WaterRiver upstreamRiver, float transitionRadiusMeters,
                                                        bool withProceduralFoam)
        {
            WaterRiver river = CreateRiverRig(parent, parentBody, surfaceMaterial, underSurfaceMaterial, knots);
            river.gameObject.name = riverName;
            if (withProceduralFoam) AddProceduralRiverFoam(river);

            if (sourceBody != null || upstreamRiver != null)
            {
                river.sourceEnd.body = sourceBody;
                river.sourceEnd.upstreamRiver = upstreamRiver;
                river.sourceEnd.transitionRadiusMeters = transitionRadiusMeters;
                WaterRiverEditor.GenerateEnd(river, WaterRiverEndKind.Source);
            }
            if (mouthBody != null)
            {
                river.mouthEnd.body = mouthBody;
                river.mouthEnd.transitionRadiusMeters = transitionRadiusMeters;
                WaterRiverEditor.GenerateEnd(river, WaterRiverEndKind.Mouth);
            }
            return river;
        }

        // Procedural bank/whitewater foam: the fluid solver plus the foam pass that reads it.
        static void AddProceduralRiverFoam(WaterRiver river)
        {
            if (river == null) throw new System.ArgumentNullException(nameof(river));
            if (river.GetComponent<WaterRiverFluid>() == null)
                Undo.AddComponent<WaterRiverFluid>(river.gameObject);
            if (river.GetComponent<WaterRiverFoam>() == null)
                Undo.AddComponent<WaterRiverFoam>(river.gameObject);
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
            // Serialized so the link is visible in the saved scene; APPEND, never assign - the
            // parent may carry other authored current fields (the facade repeats this rule for
            // its play-mode attach). Must come BEFORE the facade: WaterRiver is [ExecuteAlways],
            // so AddComponent runs its OnEnable at once and that appends the field IN MEMORY
            // (no Undo) - writing the Undo-recorded link first makes the facade's append the no-op.
            AppendCurrentFieldSerialized(parentVolume, currentField);

            var surface = riverGO.AddComponent<WaterRiverSurface>();
            // The under material is the body's cull-front twin: a submerged camera looking up
            // must see the river surface, exactly as it sees a body's.
            surface.Configure(spline, parentVolume, surfaceMaterial, underSurfaceMaterial,
                              WaterRiverSurface.DefaultSamplesPerSegment);

            var facade = riverGO.AddComponent<WaterRiver>();
            facade.parentVolume = parentVolume;
            facade.ApplyWiring(); // redundant on an active rig (OnEnable wired it); needed for inactive parents
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
