// WebGL Water - editor-only obstacle-footprint inspector.
// Dumps the FootprintDelta pass's smoothed footprints (Prev, Curr) and their signed delta
// (Prev - Curr, the value the sim actually forces the surface with) to PNG, so the footprint
// can be SEEN instead of guessed at. This exists because the previous GUI.DrawTexture overlay
// on a single-channel RHalf RT did not render reliably and produced misleading debug artifacts.
//
// Self-verifying by design: it writes NORMALISED and RAW PNGs, logs spatial stats (min/max/mean +
// centre/corner samples), and reads the footprint back a SECOND independent way (AsyncGPUReadback)
// so a suspicious uniform result can be proven real rather than a readback artifact - the exact trap
// the earlier attempt fell into. Trust a conclusion only when both readback paths agree.
//
// Delete this whole file to remove the instrumentation - the runtime body does not depend on it.
#if UNITY_EDITOR
using System.IO;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        const string ObstacleDebugFolderName = "WaterDebug";  // sibling of the project's Assets folder
        // A coverage/delta range below this is treated as empty (avoids a divide-by-zero when
        // normalising an all-black footprint to full-scale grayscale).
        const float ObstacleDebugRangeEpsilon = 1e-6f;

        [ContextMenu("Dump Obstacle Footprint (PNG)")]
        void DumpObstacleFootprint()
        {
            // Context first: the footprint pass runs ONLY in FootprintDelta mode, so a flat texture
            // usually just means it was never rendered. Logging the mode and interactable count here
            // turns that guess into a fact.
            Debug.Log($"[WaterVolume] obstacle dump context: objectInteraction={objectInteraction}, " +
                      $"activeInteractables={WaterInteractable.Active.Count}, " +
                      $"obstacleShaderWired={obstacleShader != null}.", this);

            // A uniform full-frame footprint usually means ONE interactable spans the whole frame
            // (e.g. a large plane accidentally carrying a WaterInteractable). Log each one's world
            // footprint against this body's XZ extent so an oversized offender stands out.
            Vector3 bodyExtent = VolumeExtentSafe;
            var interactables = WaterInteractable.Active;
            for (int i = 0; i < interactables.Count; i++)
            {
                WaterInteractable it = interactables[i];
                if (it == null || it.Renderer == null) continue;
                Vector3 size = it.Renderer.bounds.size;
                Debug.Log($"[WaterVolume]   interactable[{i}] '{it.name}' worldXZ={size.x:0.###}x{size.z:0.###} " +
                          $"vs body extent {2f * bodyExtent.x:0.###}x{2f * bodyExtent.z:0.###}, " +
                          $"displaceScale={it.displaceScale:0.###}.", it);
            }

            WaterObstacle obstacle = _obstacle;
            if (obstacle == null)
            {
                Debug.LogWarning("[WaterVolume] No obstacle footprint to dump. Requires FootprintDelta " +
                                 "mode and at least one submerged WaterInteractable in this body.", this);
                return;
            }

            Color[] prev = ReadFootprint(obstacle.Prev, out int width, out int height);
            Color[] curr = ReadFootprint(obstacle.Curr, out _, out _);
            if (prev == null || curr == null)
            {
                Debug.LogWarning("[WaterVolume] Obstacle footprint readback failed.", this);
                return;
            }

            string folder = Path.Combine(Directory.GetParent(Application.dataPath).FullName, ObstacleDebugFolderName);
            Directory.CreateDirectory(folder);
            string stamp = System.DateTime.Now.ToString("HHmmss");

            float coverageMax = Mathf.Max(MaxRed(prev), MaxRed(curr));
            // NORMALISED (peak = white) and RAW (absolute 0..1) side by side: a uniform field looks
            // identical (all white) after normalising, so the raw PNG is what reveals a flat footprint.
            WriteCoveragePng(prev, width, height, coverageMax, Path.Combine(folder, $"obstacle_prev_norm_{stamp}.png"));
            WriteCoveragePng(curr, width, height, coverageMax, Path.Combine(folder, $"obstacle_curr_norm_{stamp}.png"));
            WriteCoveragePng(prev, width, height, 1f, Path.Combine(folder, $"obstacle_prev_raw_{stamp}.png"));
            WriteCoveragePng(curr, width, height, 1f, Path.Combine(folder, $"obstacle_curr_raw_{stamp}.png"));

            float deltaAbsMax = WriteDeltaPng(prev, curr, width, height,
                                              Path.Combine(folder, $"obstacle_delta_{stamp}.png"));

            Debug.Log($"[WaterVolume] Dumped obstacle footprint to '{folder}'. " +
                      $"coverage max = {coverageMax:0.####}, |delta| max = {deltaAbsMax:0.####}.", this);

            // Self-verification: spatial stats decide uniform-vs-blob, and a SECOND independent
            // readback (AsyncGPUReadback, a different path than Blit+ReadPixels) decides whether any
            // uniformity is real or a readback artifact - the trap the last debug attempt fell into.
            // A real 0.3m obstacle in a 1x1 pool must show corners ~0 and a high centre; equal corner
            // and centre on BOTH readback paths means the footprint texture is genuinely uniform.
            Debug.Log($"[WaterVolume] blit-readback prev  {FormatStats(ToRed(prev), width, height)}", this);
            Debug.Log($"[WaterVolume] blit-readback curr  {FormatStats(ToRed(curr), width, height)}", this);
            if (TryAsyncReadRed(obstacle.Curr, out float[] currAsync))
                Debug.Log($"[WaterVolume] async-readback curr {FormatStats(currAsync, width, height)}", this);
            else
                Debug.LogWarning("[WaterVolume] Async cross-check readback of Curr failed (backend "
                                 + "may not support it); rely on the blit-readback stats above.", this);

            // Isolate the smearing stage: hiRes is the raw draw target (before any blit), raw is the
            // box-filtered footprint (before temporal smoothing). min==max on hiRes = the DRAW/projection
            // already fills the frame; a blob there (min~0, max>0) with uniform curr = a downsample fault.
            DumpStageIfPresent("draw-stage hiRes", obstacle.DebugHiRes, folder, $"obstacle_hires_raw_{stamp}.png");
            DumpStageIfPresent("box-filter raw ", obstacle.DebugRaw, folder, $"obstacle_rawstage_raw_{stamp}.png");
            // Passive-reflection solid mask (reflector-flagged objects only). A blob here with black
            // background = a clean wall; empty (all zero) = no interactable has reflectsWaves enabled.
            DumpStageIfPresent("solid mask     ", obstacle.DebugSolid, folder, $"obstacle_solid_raw_{stamp}.png");
        }

        void DumpStageIfPresent(string label, RenderTexture source, string folder, string fileName)
        {
            if (source == null)
            {
                Debug.LogWarning($"[WaterVolume] {label}: texture unavailable.", this);
                return;
            }
            Color[] pixels = ReadFootprint(source, out int width, out int height);
            if (pixels == null)
            {
                Debug.LogWarning($"[WaterVolume] {label}: readback failed.", this);
                return;
            }
            Debug.Log($"[WaterVolume] {label} {FormatStats(ToRed(pixels), width, height)}", this);
            WriteCoveragePng(pixels, width, height, 1f, Path.Combine(folder, fileName));
        }

        static float[] ToRed(Color[] pixels)
        {
            var reds = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++) reds[i] = pixels[i].r;
            return reds;
        }

        /// <summary>Independent readback path (AsyncGPUReadback -> RFloat) so a uniform result can be
        /// cross-checked against the Blit+ReadPixels path. Synchronous: fine for an editor context menu.</summary>
        static bool TryAsyncReadRed(RenderTexture source, out float[] reds)
        {
            reds = null;
            if (source == null) return false;
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(source, 0, TextureFormat.RFloat);
            request.WaitForCompletion();
            if (request.hasError) return false;
            NativeArray<float> data = request.GetData<float>();
            reds = data.ToArray();
            return true;
        }

        /// <summary>min/max/mean plus the centre and four corner samples - the numbers that separate a
        /// real silhouette (corners ~0, centre high) from a genuinely uniform footprint (all equal).</summary>
        static string FormatStats(float[] values, int width, int height)
        {
            if (values == null || values.Length < width * height)
                return "(no data)";

            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                float v = values[i];
                min = Mathf.Min(min, v);
                max = Mathf.Max(max, v);
                sum += v;
            }
            float mean = sum / values.Length;

            float center = values[(height / 2) * width + (width / 2)];
            float bottomLeft = values[0];
            float bottomRight = values[width - 1];
            float topLeft = values[(height - 1) * width];
            float topRight = values[(height - 1) * width + (width - 1)];
            return $"{width}x{height} min={min:0.####} max={max:0.####} mean={mean:0.####} " +
                   $"center={center:0.####} corners=[{bottomLeft:0.####},{bottomRight:0.####}," +
                   $"{topLeft:0.####},{topRight:0.####}]";
        }

        /// <summary>Read a single-channel RHalf footprint RT into a CPU pixel array. Blits through a
        /// temp RGBAFloat target first so ReadPixels is well-defined on every backend (WebGPU
        /// included), rather than reading the half-float RT directly.</summary>
        Color[] ReadFootprint(RenderTexture source, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (source == null) return null;

            width = source.width;
            height = source.height;
            RenderTexture temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGBFloat);
            Texture2D readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(source, temp);
                RenderTexture.active = temp;
                readback.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                readback.Apply(false);
                return readback.GetPixels();
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(temp);
                DestroyImmediate(readback);
            }
        }

        static float MaxRed(Color[] pixels)
        {
            float max = 0f;
            for (int i = 0; i < pixels.Length; i++) max = Mathf.Max(max, pixels[i].r);
            return max;
        }

        /// <summary>Coverage (footprint thickness) as grayscale, normalised so the frame's peak reads
        /// white - the absolute scale is reported in the accompanying log line.</summary>
        static void WriteCoveragePng(Color[] pixels, int width, int height, float coverageMax, string path)
        {
            float inverseScale = coverageMax > ObstacleDebugRangeEpsilon ? 1f / coverageMax : 0f;
            var output = new Texture2D(width, height, TextureFormat.RGB24, false);
            var mapped = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float value = Mathf.Clamp01(pixels[i].r * inverseScale);
                mapped[i] = new Color(value, value, value, 1f);
            }
            output.SetPixels(mapped);
            output.Apply(false);
            File.WriteAllBytes(path, output.EncodeToPNG());
            DestroyImmediate(output);
        }

        /// <summary>Signed delta (Prev - Curr, what the sim forces the surface with) as a diverging map:
        /// red = negative (surface pushed down), blue = positive (pushed up), black = no forcing.
        /// Returns the peak absolute delta so the log can report the colour scale.</summary>
        static float WriteDeltaPng(Color[] prev, Color[] curr, int width, int height, string path)
        {
            float deltaAbsMax = 0f;
            for (int i = 0; i < prev.Length; i++)
                deltaAbsMax = Mathf.Max(deltaAbsMax, Mathf.Abs(prev[i].r - curr[i].r));

            float inverseScale = deltaAbsMax > ObstacleDebugRangeEpsilon ? 1f / deltaAbsMax : 0f;
            var output = new Texture2D(width, height, TextureFormat.RGB24, false);
            var mapped = new Color[prev.Length];
            for (int i = 0; i < prev.Length; i++)
            {
                float signed = (prev[i].r - curr[i].r) * inverseScale; // -1..1
                float positive = Mathf.Clamp01(signed);
                float negative = Mathf.Clamp01(-signed);
                mapped[i] = new Color(negative, 0f, positive, 1f);
            }
            output.SetPixels(mapped);
            output.Apply(false);
            File.WriteAllBytes(path, output.EncodeToPNG());
            DestroyImmediate(output);
            return deltaAbsMax;
        }
    }
}
#endif
