// WebGpuWater build kit - standalone convex dry-interior creation.
// Decouples the verified convex-hull generator from CreateBoat so an existing scene hull can
// receive only the visual water carve: no Rigidbody, buoyancy, controller, wake or splash.
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        const string StandaloneDryInteriorAssetSuffix = "_ConvexHull_DryInterior.asset";
        const string StandaloneDryInteriorUndoName = "Create Convex Dry Interior";
        const string FallbackDryInteriorAssetName = "Hull";

        /// <summary>Create or rebuild a convex mesh exclusion under an existing scene hull.</summary>
        internal static WaterExclusionVolume CreateConvexDryInterior(GameObject hullRoot,
                                                                      Mesh sourceMesh = null)
        {
            ValidateDryInteriorHull(hullRoot);

            Mesh convexHull = sourceMesh != null
                ? BuildConvexHullMesh(hullRoot.transform, sourceMesh, hullRoot.name)
                : BuildConvexHullMesh(hullRoot.transform, hullRoot.name);
            if (convexHull == null)
            {
                string selection = sourceMesh != null
                    ? $"selected mesh '{sourceMesh.name}'"
                    : "render meshes under the hull root";
                throw new System.InvalidOperationException(
                    $"{LogPrefix}Could not build a verified convex hull from the {selection}. " +
                    "The mesh may be absent, unreadable or geometrically degenerate.");
            }

            try
            {
                Bounds hullBounds = convexHull.bounds;
                WaterExclusionVolume volume = GetOrCreateDryInteriorVolume(hullRoot.transform);
                Undo.RecordObject(volume.transform, StandaloneDryInteriorUndoName);
                Undo.RecordObject(volume, StandaloneDryInteriorUndoName);
                Mesh normalizedHull = BuildNormalizedCarveMesh(convexHull);
                volume.carveMesh = SaveStandaloneDryInteriorAsset(normalizedHull, volume, hullRoot.name);

                volume.transform.SetLocalPositionAndRotation(hullBounds.center, Quaternion.identity);
                volume.transform.localScale = Vector3.one;
                volume.shape = WaterExclusionVolume.Shape.Mesh;
                volume.meshProxy = WaterExclusionVolume.Shape.Box;
                volume.size = Vector3.Max(hullBounds.size * DryInteriorMeshShrink,
                                          DryInteriorMinEdge * Vector3.one);
                volume.drawWaterWalls = false;
                EditorUtility.SetDirty(volume);
                EditorSceneManager.MarkSceneDirty(hullRoot.scene);
                Selection.activeGameObject = volume.gameObject;
                return volume;
            }
            finally
            {
                Object.DestroyImmediate(convexHull);
            }
        }

        internal static bool HullContainsMesh(GameObject hullRoot, Mesh sourceMesh)
        {
            if (hullRoot == null || sourceMesh == null) return false;
            foreach (MeshFilter filter in hullRoot.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh == sourceMesh) return true;
            return false;
        }

        static void ValidateDryInteriorHull(GameObject hullRoot)
        {
            if (hullRoot == null)
                throw new System.ArgumentNullException(nameof(hullRoot));
            if (EditorUtility.IsPersistent(hullRoot) || !hullRoot.scene.IsValid())
                throw new System.ArgumentException(
                    "Hull root must be an existing GameObject in an open scene.", nameof(hullRoot));
            if (hullRoot.GetComponentInChildren<MeshFilter>(true) == null)
                throw new System.ArgumentException(
                    "Hull root has no MeshFilter in its hierarchy.", nameof(hullRoot));
        }

        static WaterExclusionVolume GetOrCreateDryInteriorVolume(Transform hullRoot)
        {
            Transform existing = hullRoot.Find(BoatDryInteriorName);
            if (existing != null)
            {
                WaterExclusionVolume existingVolume = existing.GetComponent<WaterExclusionVolume>();
                return existingVolume != null
                    ? existingVolume
                    : Undo.AddComponent<WaterExclusionVolume>(existing.gameObject);
            }

            var dryInterior = NewUndoableGameObject(BoatDryInteriorName);
            dryInterior.transform.SetParent(hullRoot, worldPositionStays: false);
            return Undo.AddComponent<WaterExclusionVolume>(dryInterior);
        }

        static Mesh SaveStandaloneDryInteriorAsset(Mesh normalizedHull,
                                                   WaterExclusionVolume volume,
                                                   string hullName)
        {
            EnsureFolder(DryInteriorAssetsRoot);
            string existingPath = volume.carveMesh != null
                ? AssetDatabase.GetAssetPath(volume.carveMesh)
                : string.Empty;
            string assetPath = existingPath.StartsWith(DryInteriorAssetsRoot + "/",
                                                        System.StringComparison.Ordinal)
                ? existingPath
                : AssetDatabase.GenerateUniqueAssetPath(
                    DryInteriorAssetsRoot + "/" + SafeAssetName(hullName) +
                    StandaloneDryInteriorAssetSuffix);
            return SaveAsset(normalizedHull, assetPath);
        }

        static string SafeAssetName(string objectName)
        {
            string safeName = string.IsNullOrWhiteSpace(objectName)
                ? FallbackDryInteriorAssetName
                : objectName;
            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                safeName = safeName.Replace(invalidCharacter, '_');
            return safeName;
        }
    }
}
