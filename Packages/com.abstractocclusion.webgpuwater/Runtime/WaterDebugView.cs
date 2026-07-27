// WebGpuWater - false-colour debug views for the water surface.
// Drop this on ANY object in the scene and pick a mode; it publishes _WaterDebugMode and the
// surface shader replaces its final colour with the view (WaterSurfaceDebug.hlsl). Remove the
// component (or set Off) and the shader is back to one uniform compare per pixel.
//
// WHY THIS EXISTS: reading the C# gates is not the same as reading what the GPU received. A
// renderer that never gets a MaterialPropertyBlock silently falls back to the MATERIAL ASSET's
// values, and coincident sheets (the base surface, the near-field patch and every clipmap ring)
// are indistinguishable in a beauty shot. Both reference assets ship an equivalent - Crest's
// _DEBUG_VISUALIZE_MASK, KWS's debug modes - for exactly this reason.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    [ExecuteAlways]
    [AddComponentMenu("AbstractOcclusion/WebGpuWater/Water Debug View")]
    public sealed class WaterDebugView : MonoBehaviour
    {
        /// <summary>Which false-colour view the water surface draws. Values MUST match the
        /// WATER_DEBUG_* defines in Runtime/Shaders/WaterSurfaceDebug.hlsl.</summary>
        public enum Mode
        {
            /// <summary>Normal shading.</summary>
            Off = 0,
            /// <summary>R = SSR, G = planar, B = real refraction, as the SHADER reads them. Water
            /// that stays red after unticking SSR is a renderer missing its property block.</summary>
            ReflectionGate = 1,
            /// <summary>A distinct colour per renderer: base sheet, near-field patch, and each
            /// clipmap ring. Two colours interleaved over the same water = coincident sheets both
            /// shading, which reads as doubled reflections and distance-banded artifacts.</summary>
            RendererId = 2,
            /// <summary>The UV the planar mirror is sampled at, tiled x8. A band or a
            /// discontinuity here IS the artifact; smooth means the sampler is not the cause.</summary>
            PlanarUV = 3,
            /// <summary>View-space surface normal, the source of the planar nudge.</summary>
            ViewNormal = 4,
            /// <summary>The planar mirror RT itself, undecorated - no wave nudge, no roughness mip,
            /// no aniso smear. The scene should read upside-down, filling the frame, horizon at the
            /// same screen height as the real one. Anything wrong HERE was rendered wrong by
            /// PlanarMirror and no sampler change can repair it.</summary>
            RawMirror = 5,
            /// <summary>The mirror again, with MAGENTA wherever it holds nothing. Large magenta
            /// areas mean the reflection camera's frustum, oblique clip plane or culling is
            /// dropping the scene.</summary>
            MirrorEmpty = 6,
        }

        [Tooltip("Which false-colour view the water surface draws. Off restores normal shading.")]
        [SerializeField] Mode mode = Mode.Off;

        static readonly int ID_WaterDebugMode = Shader.PropertyToID("_WaterDebugMode");

        /// <summary>The active view. Setting it publishes immediately, so tooling can drive it.</summary>
        public Mode View
        {
            get => mode;
            set { mode = value; Publish(); }
        }

        // Published every frame rather than on change: the global is shared state that a domain
        // reload, a scene load or another component can clear underneath us, and a stale value
        // would leave the water stuck in a debug view with no visible cause.
        void Update() => Publish();

        void OnEnable() => Publish();

        // Leaving the mode set after the component goes away would be a trap - the water would
        // stay false-coloured with nothing in the scene explaining it.
        void OnDisable() => Shader.SetGlobalFloat(ID_WaterDebugMode, 0f);

        void Publish() => Shader.SetGlobalFloat(ID_WaterDebugMode, (float)mode);
    }
}
