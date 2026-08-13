// WaterVolume settings - FOAM-1: bakes the authored crest-foam pop curve into a 1D LUT texture.
// Separated from the foam SETTINGS next door because this is a bake step, not authored data.
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {

        // ---- FOAM-1: crest-foam pop curve -> 1D LUT bake -----------------------------------
        // The AnimationCurve is baked to a tiny R8 LUT the surface (tex2Dlod) and the foam sim
        // (SampleLevel) both read. Rebaked whenever the curve's key signature changes, so play-
        // mode curve tuning is live without any editor-side hook. Render-only foam - the max
        // below is a LOCKSTEP comment contract with the shader, not a validator height pair.
        internal const float SurfCrestLutOverCapMax = 2f; // LOCKSTEP: SURF_CREST_LUT_OVERCAP_MAX (WaterSurfWaves.hlsl)
        const int SurfCrestLutResolution = 128;
        [System.NonSerialized] Texture2D _surfCrestFoamLut;
        [System.NonSerialized] float _surfCrestFoamLutSignature = float.NaN;

        internal bool SurfCrestFoamLutActive
            => bedDepthSettings.surfCrestFoamCurveEnabled
               && bedDepthSettings.surfCrestFoamCurve != null
               && bedDepthSettings.surfCrestFoamCurve.length > 0;

        /// <summary>The baked pop-curve LUT (null when the curve is disabled/empty). Lazily
        /// (re)baked on access - callers must gate on SurfCrestFoamLutActive.</summary>
        internal Texture2D SurfCrestFoamLutTexture
        {
            get
            {
                if (!SurfCrestFoamLutActive) return null;
                EnsureSurfCrestFoamLutBaked();
                return _surfCrestFoamLut;
            }
        }

        // Cheap per-frame change detection: fold every key's shape into one float. The indexer
        // (curve[i]) does not allocate, unlike the .keys array property.
        static float SurfCrestFoamCurveSignature(AnimationCurve curve)
        {
            float signature = curve.length;
            for (int i = 0; i < curve.length; i++)
            {
                Keyframe key = curve[i];
                signature = signature * 31f + key.time;
                signature = signature * 31f + key.value;
                signature = signature * 31f + key.inTangent;
                signature = signature * 31f + key.outTangent;
            }
            return signature;
        }

        void EnsureSurfCrestFoamLutBaked()
        {
            AnimationCurve curve = bedDepthSettings.surfCrestFoamCurve;
            float signature = SurfCrestFoamCurveSignature(curve);
            if (_surfCrestFoamLut != null && signature == _surfCrestFoamLutSignature) return;

            if (_surfCrestFoamLut == null)
            {
                _surfCrestFoamLut = new Texture2D(SurfCrestLutResolution, 1, TextureFormat.R8,
                                                  mipChain: false, linear: true)
                {
                    name = "SurfCrestFoamLut",
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            var texels = new byte[SurfCrestLutResolution];
            for (int i = 0; i < SurfCrestLutResolution; i++)
            {
                float overCap = (i / (float)(SurfCrestLutResolution - 1)) * SurfCrestLutOverCapMax;
                texels[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(curve.Evaluate(overCap)) * 255f);
            }
            _surfCrestFoamLut.SetPixelData(texels, 0);
            _surfCrestFoamLut.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            _surfCrestFoamLutSignature = signature;
        }

        void DestroySurfCrestFoamLut()
        {
            if (_surfCrestFoamLut == null) return;
            if (Application.isPlaying) Destroy(_surfCrestFoamLut);
            else DestroyImmediate(_surfCrestFoamLut);
            _surfCrestFoamLut = null;
            _surfCrestFoamLutSignature = float.NaN;
        }
    }
}
