// WebGpuWater build kit - the WATER SYSTEM plan and the parameterised steps that execute it.
//
// A water system is several bodies, the rivers that join them, the dry rooms carved out of
// them and one quality asset - the exact set the Connected Waters test rig hard-coded as one
// long method. The plan is DATA (serializable, so the wizard remembers it across domain
// reloads like its other fields); the steps below are the recipe, and each one composes the
// kit's EXISTING primitive (CreateWaterBody, CreateConnectedRiver, CreateExclusionVolume,
// LoadOrCreate*) rather than being a fourth way to build a body. The demo rig and the wizard's
// "Water System" section both run BuildWaterSystem, so the rig documents the exact path a
// user's own system takes.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // The base water type (moved here from the wizard so a plan can carry it; the wizard's
        // Create Water section still uses it unchanged). Fog is a property of the type: only
        // SurfaceWithFog turns it on, so the pool and plain surface start fog-free.
        // OpenWaterOcean drives the experimental large-body/clipmap path.
        internal enum WaterKind { LegacyAnalyticPool, SurfaceOnly, SurfaceWithFog, OpenWaterOcean }

        // Where a system's WaterQuality comes from. PackageDefault is what every kit body already
        // receives (ctx.Quality), so it is the bit-identical choice; CopyIntoSystemFolder clones the
        // package default into the system folder once so the project owns an editable tier
        // (the LoadOrCreateFoamProfile idiom); Asset assigns a hand-picked asset.
        internal enum WaterQualitySource { PackageDefault, CopyIntoSystemFolder, Asset }

        // Sentinel for "no body" in the plan's index fields (a river end left open, an exclusion
        // parented to the system root). Indices are plan-local, not scene references, so a plan
        // survives a scene change and a domain reload as plain data.
        internal const int NoPlanIndex = -1;

        internal const string DefaultSystemName = "Water System";
        internal const string DefaultSystemBodyName = "Water Body";
        internal const string DefaultSystemRiverName = "River";
        internal const string DefaultSystemExclusionName = "Dry Room";
        // The system-owned quality copy (see WaterQualitySource.CopyIntoSystemFolder).
        internal const string SystemQualityFileName = "WaterQuality.asset";

        // Smallest body half-extent the builders accept: the wizard's Create Water limit, shared
        // so the system plan and the single-body path refuse the same degenerate sizes.
        internal const float MinBodyExtentComponent = 0.05f;

        // The authored underwater fog density every wizard-built body starts from, not the class
        // default: WaterFogSettings ships fogDensity = 2, which reads as a solid murk WALL right
        // at the surface of a bounded body (their fog volume deliberately renders from any angle).
        // 0.2 is the value the full wizard has authored since 2026-07-25; the Connected Waters rig
        // used to re-declare it as RigFogDensity "mirroring the wizard" - now there is one.
        internal const float DefaultAuthoredFogDensity = 0.2f;

        // Extent a freshly added body row starts with: the wizard's default 2x1x2 body.
        static readonly Vector3 DefaultSystemBodyExtent = new Vector3(2f, 1f, 2f);

        [Serializable]
        internal sealed class WaterSystemPlan
        {
            [Tooltip("Scene root name AND the asset folder under Assets/WebGpuWater/Waters/.")]
            [SerializeField] internal string systemName = DefaultSystemName;
            [Tooltip("Index into Bodies of the ONE primary body (mirrors globals; only one per scene).")]
            [SerializeField] internal int primaryBodyIndex;
            [Tooltip("Turn Water Fog on for EVERY body (the Connected Waters rig's setting, so " +
                     "underwater connections are visible). Off = only SurfaceWithFog bodies fog.")]
            [SerializeField] internal bool fogOnAllBodies;
            [Tooltip("Give every body a splash emitter so buoyant props splash on entry.")]
            [SerializeField] internal bool splash = true;
            [SerializeField] internal WaterQualitySource qualitySource = WaterQualitySource.PackageDefault;
            [Tooltip("Assigned to every body when Quality Source is Asset.")]
            [SerializeField] internal WaterQuality quality;
            [SerializeField] internal List<WaterSystemBodyPlan> bodies = new List<WaterSystemBodyPlan>();
            [SerializeField] internal List<WaterSystemRiverPlan> rivers = new List<WaterSystemRiverPlan>();
            [SerializeField] internal List<WaterSystemExclusionPlan> exclusions =
                new List<WaterSystemExclusionPlan>();

            internal string SystemFolder => WatersRoot + "/" + systemName;
        }

        [Serializable]
        internal sealed class WaterSystemBodyPlan
        {
            [SerializeField] internal string name = DefaultSystemBodyName;
            [SerializeField] internal WaterKind kind = WaterKind.SurfaceOnly;
            [Tooltip("World position of the body's frame (its rest plane sits at this height).")]
            [SerializeField] internal Vector3 center;
            [Tooltip("Half-extents: X/Z horizontal, Y depth.")]
            [SerializeField] internal Vector3 extent = DefaultSystemBodyExtent;
            [Tooltip("Optional look preset applied after creation (included domains only).")]
            [SerializeField] internal WaterLookPreset lookPreset;
            [Tooltip("Optional Terrain whose heightmap is this body's bed (an ocean shoals against it).")]
            [SerializeField] internal Terrain bedTerrain;
        }

        [Serializable]
        internal sealed class WaterSystemRiverPlan
        {
            [SerializeField] internal string name = DefaultSystemRiverName;
            [Tooltip("Body supplying the ribbon's animated uniforms and underwater medium. Required.")]
            [SerializeField] internal int parentBodyIndex;
            [Tooltip("Body the SOURCE end is ported into, or -1 for an open source.")]
            [SerializeField] internal int sourceBodyIndex = NoPlanIndex;
            [Tooltip("Earlier river in this list whose MOUTH is sewn into this river's SOURCE, " +
                     "or -1. Mutually exclusive with Source Body; both rivers must share a parent.")]
            [SerializeField] internal int upstreamRiverIndex = NoPlanIndex;
            [Tooltip("Body the MOUTH end is ported into, or -1 for an open mouth.")]
            [SerializeField] internal int mouthBodyIndex = NoPlanIndex;
            [Tooltip("Spline knots, source first. A terminal knot should sit ON its body's " +
                     "footprint border and rest plane.")]
            [SerializeField] internal List<Vector3> points = new List<Vector3>();
            [Tooltip("Tangent shared by every knot (the demo rivers author one per river).")]
            [SerializeField] internal Vector3 tangent = Vector3.forward;
            [SerializeField] internal float widthMeters = DefaultRiverWidthMeters;
            [SerializeField] internal float speedMetersPerSecond = DefaultRiverSpeedMetersPerSecond;
            [Tooltip("Half-width of the seam transition slab at each connected end, metres.")]
            [SerializeField] internal float transitionRadiusMeters =
                WaterConnection.DefaultTransitionRadiusMeters;
            [Tooltip("Procedural bank/whitewater foam (WaterRiverFluid + WaterRiverFoam).")]
            [SerializeField] internal bool proceduralFoam = true;
        }

        [Serializable]
        internal sealed class WaterSystemExclusionPlan
        {
            [SerializeField] internal string name = DefaultSystemExclusionName;
            [Tooltip("Body whose object the volume is parented under, or -1 for the system root. " +
                     "Organisational only - exclusion volumes carve every body they overlap.")]
            [SerializeField] internal int bodyIndex = NoPlanIndex;
            [Tooltip("Box or Sphere. Mesh volumes need a carve mesh - use the Dry Interior section.")]
            [SerializeField] internal WaterExclusionVolume.Shape shape = WaterExclusionVolume.Shape.Box;
            [SerializeField] internal Vector3 center;
            [SerializeField] internal Vector3 size = ExclusionVolumeDefaultSize;
        }

        // What one build produced, in plan order, so callers can select the primary body, post
        // a summary or (the demo rig) hang extra props off the same objects.
        internal sealed class WaterSystemBuildResult
        {
            internal readonly List<WaterVolume> Bodies = new List<WaterVolume>();
            internal readonly List<WaterRiver> Rivers = new List<WaterRiver>();
            internal readonly List<WaterExclusionVolume> Exclusions = new List<WaterExclusionVolume>();
            internal WaterVolume Primary;
        }

        // ---------------------------------------------------------------- validation
        // Every rule the steps would otherwise trip over at build time, reported as plain text so
        // the wizard can show them BEFORE the Build button is enabled. Returns true when clean.
        internal static bool ValidatePlan(WaterSystemPlan plan, List<string> problems)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (problems == null) throw new ArgumentNullException(nameof(problems));
            problems.Clear();

            if (string.IsNullOrWhiteSpace(plan.systemName))
                problems.Add("System name is required (it names the scene root and the asset folder).");
            else if (plan.systemName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                problems.Add("System name contains characters that cannot form an asset folder.");
            if (plan.bodies.Count == 0)
                problems.Add("A system needs at least one body.");
            if (!IsBodyIndex(plan, plan.primaryBodyIndex))
                problems.Add("Primary body index must point at a body.");
            if (plan.qualitySource == WaterQualitySource.Asset && plan.quality == null)
                problems.Add("Quality Source is Asset but no WaterQuality asset is assigned.");

            for (int i = 0; i < plan.bodies.Count; i++) ValidateBodyPlan(plan.bodies[i], i, problems);
            for (int i = 0; i < plan.rivers.Count; i++) ValidateRiverPlan(plan, plan.rivers[i], i, problems);
            for (int i = 0; i < plan.exclusions.Count; i++)
                ValidateExclusionPlan(plan, plan.exclusions[i], i, problems);
            return problems.Count == 0;
        }

        static bool IsBodyIndex(WaterSystemPlan plan, int index) => index >= 0 && index < plan.bodies.Count;

        static bool IsBodyIndexOrNone(WaterSystemPlan plan, int index)
            => index == NoPlanIndex || IsBodyIndex(plan, index);

        static void ValidateBodyPlan(WaterSystemBodyPlan body, int index, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(body.name))
                problems.Add($"Body {index}: name is required.");
            if (body.extent.x < MinBodyExtentComponent || body.extent.y < MinBodyExtentComponent ||
                body.extent.z < MinBodyExtentComponent)
                problems.Add($"Body {index}: every extent component must be at least {MinBodyExtentComponent}.");
        }

        static void ValidateRiverPlan(WaterSystemPlan plan, WaterSystemRiverPlan river, int index,
                                      List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(river.name))
                problems.Add($"River {index}: name is required.");
            if (!IsBodyIndex(plan, river.parentBodyIndex))
                problems.Add($"River {index}: parent must be a body of this system.");
            if (!IsBodyIndexOrNone(plan, river.sourceBodyIndex))
                problems.Add($"River {index}: source body index is out of range.");
            if (!IsBodyIndexOrNone(plan, river.mouthBodyIndex))
                problems.Add($"River {index}: mouth body index is out of range.");
            // Facade rules (WaterRiver.ValidateEnd): one source target, no self/forward stitch, and
            // a shared parent. "Earlier in the list" also guarantees the upstream mouth exists
            // before the downstream source copies its row.
            bool hasUpstream = river.upstreamRiverIndex != NoPlanIndex;
            if (hasUpstream && river.sourceBodyIndex != NoPlanIndex)
                problems.Add($"River {index}: Source Body and Upstream River are mutually exclusive.");
            if (hasUpstream && (river.upstreamRiverIndex < 0 || river.upstreamRiverIndex >= index))
                problems.Add($"River {index}: the upstream river must appear EARLIER in the list.");
            else if (hasUpstream && plan.rivers[river.upstreamRiverIndex].parentBodyIndex != river.parentBodyIndex)
                problems.Add($"River {index}: an upstream river must share this river's parent body.");
            if (river.points.Count < WaterRiverSpline.MinimumKnotCount)
                problems.Add($"River {index}: at least {WaterRiverSpline.MinimumKnotCount} points are required.");
            if (river.widthMeters < WaterRiverSpline.MinimumWidth)
                problems.Add($"River {index}: width must be at least {WaterRiverSpline.MinimumWidth} m.");
            if (river.speedMetersPerSecond < WaterRiverSpline.MinimumSpeed)
                problems.Add($"River {index}: speed must be at least {WaterRiverSpline.MinimumSpeed} m/s.");
            if (river.transitionRadiusMeters < WaterConnection.MinTransitionRadiusMeters)
                problems.Add($"River {index}: transition radius must be at least " +
                             $"{WaterConnection.MinTransitionRadiusMeters} m.");
        }

        static void ValidateExclusionPlan(WaterSystemPlan plan, WaterSystemExclusionPlan exclusion,
                                          int index, List<string> problems)
        {
            if (string.IsNullOrWhiteSpace(exclusion.name))
                problems.Add($"Exclusion {index}: name is required.");
            if (!IsBodyIndexOrNone(plan, exclusion.bodyIndex))
                problems.Add($"Exclusion {index}: body index is out of range (-1 = system root).");
            if (exclusion.shape == WaterExclusionVolume.Shape.Mesh)
                problems.Add($"Exclusion {index}: Mesh volumes need a carve mesh - use the Dry Interior section.");
            if (exclusion.size.x <= 0f || exclusion.size.y <= 0f || exclusion.size.z <= 0f)
                problems.Add($"Exclusion {index}: size must be positive on every axis.");
        }

        // ---------------------------------------------------------------- execution
        // Runs a validated plan under 'root' with a context the caller created (so the caller
        // decides between CreateContext - camera + sun rigged - and the asset-only half). Order is
        // the demo rig's: bodies, fog, rivers (an upstream river's mouth before the downstream
        // source that copies its row), exclusions, quality. Throws on an invalid plan: callers
        // validate first and show the problems; this is the last line, not the UI.
        internal static WaterSystemBuildResult BuildWaterSystem(WaterSystemPlan plan, BuildContext ctx,
                                                                Transform root)
        {
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            if (root == null) throw new ArgumentNullException(nameof(root));
            var problems = new List<string>();
            if (!ValidatePlan(plan, problems))
                throw new InvalidOperationException(LogPrefix + "water system plan is invalid: " +
                                                    string.Join(" ", problems));

            var result = new WaterSystemBuildResult();
            for (int i = 0; i < plan.bodies.Count; i++)
                result.Bodies.Add(CreateBodyStep(ctx, root, plan.bodies[i],
                                                 primary: i == plan.primaryBodyIndex,
                                                 withSplash: plan.splash));
            result.Primary = result.Bodies[plan.primaryBodyIndex];
            if (plan.fogOnAllBodies) EnsureUnderwaterFog(result.Bodies);

            for (int i = 0; i < plan.rivers.Count; i++)
                result.Rivers.Add(CreateRiverStep(root, plan.rivers[i], result));
            for (int i = 0; i < plan.exclusions.Count; i++)
                result.Exclusions.Add(CreateExclusionStep(root, plan.exclusions[i], result));
            ApplyQualityStep(result.Bodies, ResolveSystemQuality(plan, ctx));
            return result;
        }

        // One body of the plan through THE body recipe (CreateWaterBody), then the kind's extras.
        // Bodies stay lean (no pool walls beyond the analytic-pool kind, no god rays, no particle
        // foam) - the demo rig's choice, and every extra is a one-click retrofit in Utilities.
        internal static WaterVolume CreateBodyStep(BuildContext ctx, Transform parent, WaterSystemBodyPlan body,
                                                   bool primary, bool withSplash)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            bool withPool = body.kind == WaterKind.LegacyAnalyticPool;
            WaterVolume volume = CreateWaterBody(ctx, parent, body.name, body.center, body.extent,
                                                primary, withPool, withGodRays: false,
                                                withFoamParticles: false, withSplash: withSplash);
            if (body.kind == WaterKind.SurfaceWithFog) EnsureUnderwaterFog(volume);
            if (body.kind == WaterKind.OpenWaterOcean) ConfigureUnboundedOcean(volume, body.bedTerrain);
            else if (body.bedTerrain != null) ApplyBedTerrain(volume, body.bedTerrain);
            if (body.lookPreset != null) ApplyLookPreset(volume, body.lookPreset);
            return volume;
        }

        // One river of the plan through THE river recipe (CreateConnectedRiver), drawing its
        // surface materials from the parent body so the ribbon shares the body's look and no
        // second material folder appears (the GameObject-menu river follows the same rule).
        internal static WaterRiver CreateRiverStep(Transform parent, WaterSystemRiverPlan river,
                                                   WaterSystemBuildResult built)
        {
            if (river == null) throw new ArgumentNullException(nameof(river));
            if (built == null) throw new ArgumentNullException(nameof(built));
            WaterVolume parentBody = built.Bodies[river.parentBodyIndex];
            if (!TryResolveBodyMaterials(parentBody, out Material above, out Material under))
                throw new InvalidOperationException(
                    LogPrefix + $"river '{river.name}': parent body '{parentBody.name}' has no surface materials.");
            WaterVolume sourceBody = BodyAt(built, river.sourceBodyIndex);
            WaterVolume mouthBody = BodyAt(built, river.mouthBodyIndex);
            WaterRiver upstream = river.upstreamRiverIndex == NoPlanIndex
                ? null : built.Rivers[river.upstreamRiverIndex];
            return CreateConnectedRiver(parent, river.name, BuildKnots(river), parentBody, above, under,
                                        sourceBody, mouthBody, upstream, river.transitionRadiusMeters,
                                        river.proceduralFoam);
        }

        static WaterVolume BodyAt(WaterSystemBuildResult built, int index)
            => index == NoPlanIndex ? null : built.Bodies[index];

        // Uniform width/speed/tangent per river: the shape the demo rig authors, and all a first
        // system needs. Per-knot values are edited on the spline afterwards.
        static List<WaterRiverKnot> BuildKnots(WaterSystemRiverPlan river)
        {
            var knots = new List<WaterRiverKnot>(river.points.Count);
            for (int i = 0; i < river.points.Count; i++)
                knots.Add(new WaterRiverKnot(river.points[i], river.tangent, river.widthMeters,
                                             river.speedMetersPerSecond));
            return knots;
        }

        // The body's own surface materials, which the kit already shares between a body and its
        // rivers (the demo rig passes ctx.MatAbove/MatUnder to both). A body built by hand may
        // lack one - then the caller falls back to building its own.
        static bool TryResolveBodyMaterials(WaterVolume body, out Material above, out Material under)
        {
            above = body != null && body.surfaceAbove != null ? body.surfaceAbove.sharedMaterial : null;
            under = body != null && body.surfaceUnder != null ? body.surfaceUnder.sharedMaterial : null;
            return above != null && under != null;
        }

        // One exclusion volume of the plan through the GameObject-menu creator's body.
        internal static WaterExclusionVolume CreateExclusionStep(Transform root, WaterSystemExclusionPlan exclusion,
                                                                 WaterSystemBuildResult built)
        {
            if (exclusion == null) throw new ArgumentNullException(nameof(exclusion));
            if (built == null) throw new ArgumentNullException(nameof(built));
            WaterVolume body = BodyAt(built, exclusion.bodyIndex);
            // The body's ROOT object (its frame carries the volume; siblings hold the renderers).
            Transform parent = body != null ? body.transform.parent : root;
            return CreateExclusionVolume(parent, exclusion.name, exclusion.shape, exclusion.center,
                                         exclusion.size);
        }

        // ---------------------------------------------------------------- quality
        internal static WaterQuality ResolveSystemQuality(WaterSystemPlan plan, BuildContext ctx)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (ctx == null) throw new ArgumentNullException(nameof(ctx));
            switch (plan.qualitySource)
            {
                case WaterQualitySource.Asset:
                    return plan.quality != null ? plan.quality : ctx.Quality;
                case WaterQualitySource.CopyIntoSystemFolder:
                    WaterQuality copy = LoadOrCopySystemQuality(ctx.WaterFolder);
                    return copy != null ? copy : ctx.Quality;
                default:
                    return ctx.Quality;
            }
        }

        // Clone the packaged default tier once into the system folder (the LoadOrCreateFoamProfile
        // idiom), so the project owns an editable quality asset instead of editing the package's.
        internal static WaterQuality LoadOrCopySystemQuality(string waterFolder)
        {
            if (string.IsNullOrEmpty(waterFolder))
                throw new ArgumentException("A water asset folder is required.", nameof(waterFolder));
            EnsureFolder(waterFolder);
            string path = waterFolder + "/" + SystemQualityFileName;
            var existing = AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
            if (existing != null) return existing;

            var template = LoadRequiredDefault<WaterQuality>(WaterQualityAssetPath, "water quality");
            if (template == null) return null;
            if (!AssetDatabase.CopyAsset(WaterQualityAssetPath, path))
            {
                Debug.LogError(LogPrefix + $"could not copy the default water quality to '{path}'.");
                return null;
            }
            AssetDatabase.SaveAssets();
            return AssetDatabase.LoadAssetAtPath<WaterQuality>(path);
        }

        // Point every body at one quality asset. Bodies created by CreateWaterBody already carry
        // ctx.Quality, so the package-default source is a no-op here (bit-identical rigs).
        internal static void ApplyQualityStep(IReadOnlyList<WaterVolume> bodies, WaterQuality quality)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            if (quality == null) return;
            for (int i = 0; i < bodies.Count; i++)
            {
                WaterVolume body = bodies[i];
                if (body == null || body.Quality == quality) continue;
                body.Quality = quality;
                EditorUtility.SetDirty(body);
            }
        }

        // ---------------------------------------------------------------- body extras
        // Water Fog ON at the authored density. Freshly created bodies default fog OFF; fullscreen
        // volume fog is already on by default, so only the master toggle and density change.
        internal static void EnsureUnderwaterFog(WaterVolume body)
        {
            if (body == null) return;
            body.FogSettings.waterFog = true;
            body.FogSettings.fogDensity = DefaultAuthoredFogDensity;
        }

        internal static void EnsureUnderwaterFog(IReadOnlyList<WaterVolume> bodies)
        {
            if (bodies == null) throw new ArgumentNullException(nameof(bodies));
            for (int i = 0; i < bodies.Count; i++) EnsureUnderwaterFog(bodies[i]);
        }

        // The unbounded-ocean configuration through the shared property-path registry. The bed
        // Terrain is optional: with one, the ocean shoals against it and its infinite clipmap
        // stays out of the dry banks; without one the sheet is flat-bedded until a Terrain is
        // assigned in the inspector. Surf is left OFF: a fresh ocean (and the seam diagnostic
        // this was written for) reads better with calm shoaling than with a breaker train
        // obscuring its borders - the body inspector turns it on.
        internal static void ConfigureUnboundedOcean(WaterVolume ocean, Terrain bedTerrain)
        {
            if (ocean == null) throw new ArgumentNullException(nameof(ocean));
            var serialized = new SerializedObject(ocean);
            serialized.FindProperty(WaterVolumePropertyPaths.BodyType).enumValueIndex =
                (int)WaterVolume.WaterBodyType.Ocean;
            serialized.FindProperty(WaterVolumePropertyPaths.OpenWater).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.UnboundedOcean).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.EnableLargeBodyWindow).boolValue = true;
            serialized.FindProperty(WaterVolumePropertyPaths.UseBedDepth).boolValue = bedTerrain != null;
            serialized.FindProperty(WaterVolumePropertyPaths.BedTerrain).objectReferenceValue = bedTerrain;
            serialized.FindProperty(WaterVolumePropertyPaths.SurfEnabled).boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(ocean);
        }

        // A bounded body's terrain bed (the wizard's "Use terrain bed" option, same two paths).
        internal static void ApplyBedTerrain(WaterVolume body, Terrain bedTerrain)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            var serialized = new SerializedObject(body);
            serialized.FindProperty(WaterVolumePropertyPaths.UseBedDepth).boolValue = bedTerrain != null;
            serialized.FindProperty(WaterVolumePropertyPaths.BedTerrain).objectReferenceValue = bedTerrain;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(body);
        }

        // Preset -> body, included domains only (the inspector's Apply button, committed here
        // because the body is not an inspector's serializedObject).
        internal static void ApplyLookPreset(WaterVolume body, WaterLookPreset preset)
        {
            if (body == null) throw new ArgumentNullException(nameof(body));
            if (preset == null) throw new ArgumentNullException(nameof(preset));
            var presetSerialized = new SerializedObject(preset);
            var bodySerialized = new SerializedObject(body);
            WaterLookPresetSync.ApplyIncluded(presetSerialized, bodySerialized, preset);
            bodySerialized.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(body);
        }
    }
}
