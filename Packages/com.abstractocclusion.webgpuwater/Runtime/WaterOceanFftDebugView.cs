// WebGpuWater - opt-in on-screen preview of the FFT-cascade ocean textures (increment 1).
//
// Drop this on any GameObject and assign the ocean WaterVolume to eyeball a cascade slice while the
// FFT pipeline is built out. Development aid only: it draws nothing unless 'show' is on and the volume
// exposes an FFT texture, so a shipped build that never adds this component is entirely unaffected.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    /// <summary>Draws one slice of the ocean FFT cascade array to a screen corner for verification.</summary>
    [AddComponentMenu("WebGpuWater/Debug/Ocean FFT Debug View")]
    internal sealed class WaterOceanFftDebugView : MonoBehaviour
    {
        [SerializeField] WaterVolume oceanBody;
        [SerializeField] bool show = true;
        [SerializeField, Min(0)] int cascadeSlice;
        [SerializeField, Range(64, 512)] int screenSize = 256;

        // A plain 2D copy target: OnGUI can draw a Texture2D but not a single slice of an array, so we
        // blit the chosen slice across each frame it is shown.
        RenderTexture _preview;

        void OnDisable()
        {
            if (_preview == null) return;
            _preview.Release();
            Destroy(_preview);
            _preview = null;
        }

        void OnGUI()
        {
            if (!show || oceanBody == null) return;
            RenderTexture src = oceanBody.OceanFftTexture;
            if (src == null) return;

            int slice = Mathf.Clamp(cascadeSlice, 0, Mathf.Max(0, src.volumeDepth - 1));
            EnsurePreview(src);
            Graphics.CopyTexture(src, slice, 0, _preview, 0, 0);
            GUI.DrawTexture(new Rect(10, 10, screenSize, screenSize), _preview, ScaleMode.ScaleToFit, false);
        }

        void EnsurePreview(RenderTexture src)
        {
            if (_preview != null && _preview.width == src.width && _preview.height == src.height) return;
            if (_preview != null) { _preview.Release(); Destroy(_preview); }
            _preview = new RenderTexture(src.width, src.height, 0, src.format)
            {
                dimension = TextureDimension.Tex2D,
                name = "OceanFftPreview",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _preview.Create();
        }
    }
}
