// WebGpuWater - WaterVolume inspector: the INTERACTION tab.
// What the WORLD does to the water: floating/moving obstacles, and the splash + spray + particle
// components that fire off impacts. The wave tuning those inputs excite is in the Motion tab.
//
// The FX components are separate MonoBehaviours, so this tab cannot draw their fields; it draws a
// row per component that says whether the body has one and selects it. Read-only discovery over
// the body's own hierarchy - it adds no serialized state and creates nothing.
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater.Editor
{
    public partial class WaterVolumeEditor
    {
        void DrawPointerInteractionSection()
        {
            _showPointerInteraction = WaterEditorUI.Section(
                "Pointer Interaction", _showPointerInteraction, () =>
                    DrawFields("rippleSettings.pointerWaterInteraction"));
        }

        void DrawObjectInteractionSection()
        {
            _showObjectInteraction = WaterEditorUI.Section("Object Interaction", _showObjectInteraction, () =>
            {
                DrawFields(
                    "objectInteractionSettings.objectInteraction",
                    "objectInteractionSettings.obstacleStrength");
                // Numerical conditioning of the obstacle readback, not feel.
                _showObjectInteractionAdvanced = WaterEditorUI.SubSection("Advanced",
                    _showObjectInteractionAdvanced, () =>
                    DrawFields(
                        "objectInteractionSettings.obstacleDeadband",
                        "objectInteractionSettings.obstacleSmoothing",
                        "objectInteractionSettings.obstacleFlipY"));
            });
        }

        void DrawSplashSection()
        {
            _showSplash = WaterEditorUI.Section("Splash & FX Components", _showSplash, () =>
            {
                DrawFields("splashEmitter");
                WaterEditorUI.SubHeading("On this body");
                EditorGUILayout.HelpBox(FxComponentsHelp, MessageType.None);
                DrawComponentRow<WaterSplashEmitter>("Splash Emitter");
                DrawComponentRow<WaterSprayPump>("Spray Pump");
                DrawComponentRow<WaterFoamParticles>("Foam Particles");
            });
        }

        // One row: the component's presence on this body's hierarchy + a button that selects it so
        // its own inspector opens. Never adds or removes a component - the user stays in control.
        void DrawComponentRow<T>(string label) where T : Component
        {
            var volume = target as WaterVolume;
            T component = volume == null ? null : volume.GetComponentInChildren<T>(includeInactive: true);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField(label, component == null ? NoneLabel : component.gameObject.name);
                using (new EditorGUI.DisabledScope(component == null))
                {
                    if (GUILayout.Button(SelectLabel, EditorStyles.miniButton, SelectButtonWidth))
                        Selection.activeGameObject = component.gameObject;
                }
            }
        }

        static readonly GUILayoutOption SelectButtonWidth = GUILayout.Width(60f);
        const string NoneLabel = "none";
        const string SelectLabel = "Select";
        const string FxComponentsHelp =
            "These are separate components with their own inspectors. Select one to tune it; add one " +
            "from the GameObject / Component menu if it is missing.";
    }
}
#endif
