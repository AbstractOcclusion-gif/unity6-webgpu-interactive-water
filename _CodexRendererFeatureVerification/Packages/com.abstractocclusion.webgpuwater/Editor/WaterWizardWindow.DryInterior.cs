// WebGpuWater Water Wizard - standalone convex dry-interior section.
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal sealed partial class WaterWizardWindow
    {
        [SerializeField] GameObject _dryInteriorHullRoot;
        [SerializeField] Mesh _dryInteriorHullMesh;

        void DrawDryInteriorSection()
        {
            EditorGUILayout.HelpBox(
                "Create only a convex mesh water-exclusion child under an existing scene hull. " +
                "This does not add boat physics, buoyancy, controls, wake or splash components.",
                MessageType.None);

            _dryInteriorHullRoot = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Hull object", "Existing scene object that owns the hull render meshes. " +
                                              "The generated Dry Interior becomes its child."),
                _dryInteriorHullRoot, typeof(GameObject), allowSceneObjects: true);
            _dryInteriorHullMesh = (Mesh)EditorGUILayout.ObjectField(
                new GUIContent("Hull mesh (optional)", "Use only this MeshFilter mesh from the hull hierarchy. " +
                                                     "Leave empty to convex-hull every render mesh under the object."),
                _dryInteriorHullMesh, typeof(Mesh), allowSceneObjects: false);

            bool persistentHull = _dryInteriorHullRoot != null &&
                                  EditorUtility.IsPersistent(_dryInteriorHullRoot);
            bool meshMissing = _dryInteriorHullRoot != null && _dryInteriorHullMesh != null &&
                               !WaterBuildKit.HullContainsMesh(_dryInteriorHullRoot,
                                                               _dryInteriorHullMesh);
            if (persistentHull)
                EditorGUILayout.HelpBox("Choose an instance in the open scene, not a prefab asset.",
                                        MessageType.Warning);
            else if (meshMissing)
                EditorGUILayout.HelpBox("The selected hull mesh is not used by a MeshFilter under this object.",
                                        MessageType.Warning);

            bool canCreate = _dryInteriorHullRoot != null && !persistentHull && !meshMissing;
            using (new EditorGUI.DisabledScope(!canCreate))
            {
                if (!GUILayout.Button("Create Convex Dry Interior", GUILayout.Height(26f))) return;

                Undo.SetCurrentGroupName("Create Convex Dry Interior");
                int undoGroup = Undo.GetCurrentGroup();
                try
                {
                    WaterExclusionVolume volume = WaterBuildKit.CreateConvexDryInterior(
                        _dryInteriorHullRoot, _dryInteriorHullMesh);
                    Undo.CollapseUndoOperations(undoGroup);
                    Debug.Log($"{WaterBuildKit.LogPrefix}Created convex dry interior for " +
                              $"'{_dryInteriorHullRoot.name}' using '{volume.carveMesh.name}'.",
                              volume);
                }
                catch (System.Exception exception)
                {
                    Undo.RevertAllDownToGroup(undoGroup);
                    Debug.LogException(exception);
                }
            }
        }
    }
}
