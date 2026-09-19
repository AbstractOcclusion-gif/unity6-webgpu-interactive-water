// WebGpuWater build kit - the GameObject create-menu command for a standalone water exclusion
// volume. Its own file because it is a menu entry point, not a step any builder calls.
using System.IO;
using UnityEditor;
using UnityEngine;
using AbstractOcclusion.WebGpuWater;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    internal static partial class WaterBuildKit
    {
        // ---------------------------------------------------------------- exclusion volume
        // Standalone dry rooms (underwater houses, caves): a SCENE-OBJECT creator, so it lives
        // on the GameObject menu like Unity's own primitives - the Window/ MenuRoot hosts tool
        // windows, not scene objects. Boats get theirs automatically via CreateBoat.
        const string ExclusionVolumeMenuPath = GameObjectMenuRoot + "Water Exclusion Volume";
        const string ExclusionVolumeObjectName = "Water Exclusion Volume";
        const int ExclusionVolumeMenuPriority = 10; // Unity's standard create-menu priority band
        static readonly Vector3 ExclusionVolumeDefaultSize = new Vector3(4f, 3f, 4f); // a small room

        [MenuItem(ExclusionVolumeMenuPath, false, ExclusionVolumeMenuPriority)]
        static void CreateExclusionVolumeFromMenu(MenuCommand command)
        {
            WaterExclusionVolume volume = CreateExclusionVolume(null, ExclusionVolumeObjectName,
                                                                WaterExclusionVolume.Shape.Box, Vector3.zero,
                                                                ExclusionVolumeDefaultSize);
            // Parent under the right-clicked object (context menu) like Unity's built-in creators.
            GameObjectUtility.SetParentAndAlign(volume.gameObject, command.context as GameObject);
            Selection.activeGameObject = volume.gameObject;
        }

        // The one exclusion-volume recipe, shared by the menu entry and the Water System plan
        // (which places carves at authored world positions under the system root or a body).
        internal static WaterExclusionVolume CreateExclusionVolume(Transform parent, string name,
                                                                   WaterExclusionVolume.Shape shape,
                                                                   Vector3 center, Vector3 size)
        {
            var go = NewUndoableGameObject(name);
            if (parent != null) go.transform.SetParent(parent);
            go.transform.position = center;
            var volume = go.AddComponent<WaterExclusionVolume>();
            volume.shape = shape;
            volume.size = size;
            return volume;
        }

    }
}
