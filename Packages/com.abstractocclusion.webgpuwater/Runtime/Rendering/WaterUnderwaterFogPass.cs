// WebGpuWater - real underwater fog pass (RenderGraph).
// When the camera is submerged, fogs the whole camera colour by water-path length using two
// hardware-blend fullscreen passes (per-channel absorb, then inscatter). No scene-colour copy:
// both passes read the destination through the blender, which is why the colour attachment is
// bound ReadWrite (load the scene) rather than Write (which would discard it).
//
    // The shader reconstructs the scene from the resolved _CameraDepthTexture. Full-tier beauty
    // frames classify the analytic wavy waterline once into _WaterFogClassifyRT and share it across
    // both composites plus the meniscus; Simple and automatic fallback paths stay direct/flat.
// The former DepthHandoff sub-pass that published one (_WaterFogSceneDepth) was dead weight: the
// shader declared the texture but never sampled it, so the handoff was removed (U3).
//
// Runs before post so bloom/tonemapping treat the fogged scene as the final image.
#if WEBGPUWATER_URP
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterUnderwaterFogPass : ScriptableRenderPass
    {
        internal const RenderPassEvent InjectionPoint = RenderPassEvent.BeforeRenderingPostProcessing;

        const int AbsorbShaderPass = 0;
        const int InscatterShaderPass = 1;
        const int WaterlineShaderPass = 2;
        const int InvalidShaderPass = -1;
        const string ClassifyShaderPassName = "WaterFogClassify";
        const string ClassifyRtTextureName = "_WaterFogClassifyRT";
        const string ClassifyRtKeyword = "WATER_FOG_CLASSIFY_RT";
        const GraphicsFormat ClassifyRtFormat = GraphicsFormat.R32G32_SFloat;
        // "WaterRestoreOpaqueDepth": rewrites the depth attachment from the opaque-only
        // _CameraDepthTexture so user transparents drawn after the water stack stop
        // z-failing behind the sheet's ZWrite On depth (the cross-side transparent fix).
        // Dispatched by WaterParticlesAfterFogPass (the feature hands it this material),
        // never by the fog chain in this file - internal so that pass can reach the index.
        internal const int RestoreDepthShaderPass = 3;
        // WaterSurface.shader's "OceanSurfaceEyeDepth" pass, drawn per surface renderer below.
        const int SurfaceDepthShaderPass = 1;

        static readonly int ID_OceanSurfaceEyeDepth = Shader.PropertyToID("_OceanSurfaceEyeDepth");
        static readonly int ID_OceanSurfaceOwnership = Shader.PropertyToID("_OceanSurfaceOwnership");
        static readonly int ID_OceanSurfaceDepthValid = Shader.PropertyToID("_OceanSurfaceDepthValid");
        static readonly int ID_OceanSurfacePrepassScale = Shader.PropertyToID("_OceanSurfacePrepassScale");
        static readonly int ID_WaterHeightRT = Shader.PropertyToID("_WaterHeightRT");
        static readonly int ID_WaterHeightRTFrame = Shader.PropertyToID("_WaterHeightRTFrame");
        static readonly int ID_WaterHeightRTViewProjection =
            Shader.PropertyToID("_WaterHeightRTViewProjection");
        internal const int HeightRtResolution = 256;
        internal const float HeightRtWindowSize = 512f;
        const float HeightRtHalfExtent = HeightRtWindowSize * 0.5f;
        const float HeightRtTexelSize = HeightRtWindowSize / HeightRtResolution;
        const float HeightRtChopApron = 16f;
        const float HeightRtCameraAltitude = 1024f;
        const float HeightRtDepthRange = 2048f;
        const string HeightRtTextureName = "_WaterHeightRT";
        const string HeightRtDepthName = "WaterHeightRT.Depth";

        // The eye-depth prepass renders at this fraction of camera resolution (both axes). The fog
        // only needs the SIGN of the sheet and its eye depth at wave scale - not per-pixel exact
        // silhouettes - and the full-res R32F + Depth32 pair was the single biggest constant GPU
        // add of the Full tier (~20 displaced-mesh draws into two camera-sized targets, plus the
        // mid-frame RT switch that costs far more on the WebGPU backend than native). At 0.5 the
        // fill + bandwidth drop 4x and the corroboration test's +-1 texel becomes +-2 screen
        // pixels, which still rejects the 1-px silhouette runs it exists for. The shader reads the
        // RT with pixel LOADs, so it must know the scale: published as _OceanSurfacePrepassScale.
        const float PrepassResolutionScale = 0.5f;
        static readonly int ID_WaterlineSceneTex = Shader.PropertyToID("_WaterlineSceneTex");
        static readonly int ID_WaterFogClassifyRT = Shader.PropertyToID(ClassifyRtTextureName);

        readonly Material _material;
        readonly Material _heightRtMaterial;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterUnderwaterFog");
        readonly ProfilingSampler _prepassSampler = new ProfilingSampler("WaterUnderwaterFog.SurfaceDepth");
        readonly ProfilingSampler _heightRtSampler = new ProfilingSampler("WaterUnderwaterFog.HeightRT");
        readonly ProfilingSampler _classifySampler = new ProfilingSampler("WaterUnderwaterFog.Classify");
        readonly int _classifyShaderPass;
        readonly bool _classifyRtSupported;
        // Reused each frame so the prepass allocates no garbage.
        readonly MaterialPropertyBlock _scratchBlock = new MaterialPropertyBlock();
        static readonly List<Renderer> s_SurfaceRenderers = new List<Renderer>();
        static Mesh s_HeightRtGrid;

        internal WaterUnderwaterFogPass(Material material, Material heightRtMaterial)
        {
            _material = material;
            _heightRtMaterial = heightRtMaterial;
            _classifyShaderPass = material != null
                ? material.FindPass(ClassifyShaderPassName)
                : InvalidShaderPass;
            _classifyRtSupported = SystemInfo.IsFormatSupported(ClassifyRtFormat,
                                                                GraphicsFormatUsage.Render);
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData
        {
            public Material material;
            public bool useClassifyRt;
        }

        sealed class ClassifyPassData
        {
            public Material material;
            public int shaderPass;
        }

        sealed class PrepassData
        {
            public List<Renderer> renderers;
            public MaterialPropertyBlock block;
        }

        sealed class HeightRtPassData
        {
            public Material material;
            public MaterialPropertyBlock block;
            public Mesh mesh;
            public Matrix4x4 model;
            public Matrix4x4 viewProjection;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_material == null) return;

            UniversalResourceData resources = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            TextureHandle cameraColor = resources.activeColorTexture;
            if (!cameraColor.IsValid()) return;
            // (The point-light scatter reads the package's OWN published light list - see
            // WaterUniformPublisher.PublishSceneLights - not URP's per-camera light data, so
            // this pass carries no light plumbing.)

            // Rendered-surface waterline prepass (KWS trick): draw the fog source ocean's DISPLACED
            // surface into an eye-depth target the fog samples per pixel, so its waterline is the
            // rendered surface itself - exact at any distance, replacing the bounded crossing
            // march. The validity global is refreshed EVERY record (globals persist across frames,
            // so a stale 1 after the ocean disappears would leave the fog reading a dead RT).
            bool prepassRecorded = false;
            WaterVolume fogSource = WaterVolume.FogSource;
            // NOT ON THE SIMPLE TIER - it has no reader there. UnderwaterSegment tests
            // _UnderwaterFogSimple BEFORE _OceanSurfaceDepthValid (WaterUnderwaterFog.shader), so a
            // Simple frame takes OceanFlatPath and _OceanSurfaceEyeDepth is sampled NOWHERE: its
            // only consumer in the package is OceanPrepassPath. Recording it anyway re-drew every
            // ocean surface renderer a second time - base + under + near-field patch + patch under +
            // two per clipmap level, each through the full displacement vertex stage - into a
            // camera-sized R32F plus its own Depth32, and threw the result away. It also forced a
            // mid-frame render-target switch, which costs far more on the WebGPU backend than
            // native. Leaving the validity global at 0 is the state a pond or a non-ocean fog source
            // already ships every frame, so this adds no new case for the shader to handle.
            // The waterline transition now consumes the same rendered ownership as the fog, so
            // straddle-only frames are readers too. Recording before either consumer prevents the
            // analytic meniscus from leading a moving crest by a frame while the camera is static.
            if ((WaterVolume.UnderwaterFogActive || WaterVolume.WaterlineActive)
                && fogSource != null && fogSource.IsOceanClipmap && !fogSource.UnderwaterFogSimple)
            {
                s_SurfaceRenderers.Clear();
                // One canonical mesh per clipmap level/patch/base sheet. The prepass renders it
                // two-sided and classifies SV_IsFrontFace in the fragment shader, following the
                // KWS mask pattern. Drawing the coincident above/under renderer twins made their
                // depth-equal fragments fight wherever strong chop reversed a triangle or two LOD
                // rings overlapped, producing long wrong-side bands in the ownership texture.
                fogSource.CollectAboveSurfaceRenderers(s_SurfaceRenderers);
                if (s_SurfaceRenderers.Count > 0)
                {
                    RecordSurfaceDepthPrepass(renderGraph, cameraColor);
                    prepassRecorded = true;
                }
            }
            Shader.SetGlobalFloat(ID_OceanSurfaceDepthValid, prepassRecorded ? 1f : 0f);
            // _OceanSurfacePrepassScale is published inside RecordSurfaceDepthPrepass, from the
            // scale actually applied to the RT - the only frames the shader reads it (validity 1).
            bool heightRtRecorded = WaterVolume.UnderwaterFogActive
                                    && fogSource != null
                                    && fogSource.IsOceanClipmap
                                    && !fogSource.UnderwaterFogSimple
                                    && _heightRtMaterial != null
                                    && s_SurfaceRenderers.Count > 0;
            if (heightRtRecorded)
                RecordHeightRt(renderGraph, cameraData, fogSource.VolumeCenter.y);
            else
                Shader.SetGlobalVector(ID_WaterHeightRTFrame, Vector4.zero);

            // Full-tier beauty frames share the expensive analytic waterline classification through
            // one full-resolution RG32F target. Debug views deliberately retain the direct path: they
            // stamp branch-local state that this two-channel first increment does not carry. A missing
            // shader pass or unsupported render format automatically leaves every consumer on the
            // established analytic variant; no manual fallback switch can be forgotten in a build.
            bool classifyRtRecorded = (WaterVolume.UnderwaterFogActive || WaterVolume.WaterlineActive)
                                   && fogSource != null
                                   && !fogSource.UnderwaterFogSimple
                                   && !WaterDebugView.FogViewActive
                                   && _classifyShaderPass != InvalidShaderPass
                                   && _classifyRtSupported;
            TextureHandle classifyRt = classifyRtRecorded
                ? RecordClassifyPass(renderGraph, cameraColor)
                : default;

            // Order matters: absorb (scene *= transmittance) then inscatter (scene += fog),
            // then the waterline meniscus ON TOP of the fogged scene (it darkens the final
            // crossing band, whichever side of it is fogged). The same per-frame gates the
            // feature enqueued on decide which sub-passes record - fog and waterline arm
            // independently (a straddling near plane arms the line before the eye submerges).
            if (WaterVolume.UnderwaterFogActive)
            {
                RecordFogPass(renderGraph, resources, cameraColor, "WaterUnderwaterFog",
                              classifyRt);
            }
            // The meniscus darkens the finished frame along the crossing - the exact band a fog
            // debug view exists to show - so it stands down while one is selected. The absorb and
            // inscatter passes above are NOT gated: they ARE the view (absorb wipes, inscatter
            // writes), which is also why a view only appears while the fog is armed.
            if (WaterVolume.WaterlineActive && !WaterDebugView.FogViewActive)
                RecordWaterlinePass(renderGraph, resources, cameraColor, classifyRt);
        }

        TextureHandle RecordClassifyPass(RenderGraph renderGraph, TextureHandle sizeSource)
        {
            TextureDesc classifyDesc = renderGraph.GetTextureDesc(sizeSource);
            classifyDesc.name = ClassifyRtTextureName;
            classifyDesc.colorFormat = ClassifyRtFormat;
            classifyDesc.depthBufferBits = DepthBits.None;
            classifyDesc.msaaSamples = MSAASamples.None;
            classifyDesc.clearBuffer = false;
            TextureHandle classifyRt = renderGraph.CreateTexture(classifyDesc);

            using var builder = renderGraph.AddRasterRenderPass<ClassifyPassData>(
                _classifySampler.name, out ClassifyPassData data, _classifySampler);
            data.material = _material;
            data.shaderPass = _classifyShaderPass;
            builder.SetRenderAttachment(classifyRt, 0, AccessFlags.Write);
            builder.UseAllGlobalTextures(true);
            builder.AllowPassCulling(false);
            builder.SetGlobalTextureAfterPass(classifyRt, ID_WaterFogClassifyRT);
            builder.SetRenderFunc((ClassifyPassData d, RasterGraphContext ctx) =>
            {
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, d.shaderPass);
            });
            return classifyRt;
        }

        void RecordHeightRt(RenderGraph renderGraph, UniversalCameraData cameraData, float restPlaneY)
        {
            Vector3 cameraPosition = cameraData.worldSpaceCameraPos;
            float centerX = Mathf.Floor(cameraPosition.x / HeightRtTexelSize) * HeightRtTexelSize;
            float centerZ = Mathf.Floor(cameraPosition.z / HeightRtTexelSize) * HeightRtTexelSize;
            Vector3 center = new Vector3(centerX, restPlaneY, centerZ);

            TextureDesc colorDesc = new TextureDesc(HeightRtResolution, HeightRtResolution)
            {
                name = HeightRtTextureName,
                colorFormat = GraphicsFormat.R16_SFloat,
                depthBufferBits = DepthBits.None,
                msaaSamples = MSAASamples.None,
                clearBuffer = true,
                clearColor = Color.clear,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            TextureHandle color = renderGraph.CreateTexture(colorDesc);
            TextureDesc depthDesc = new TextureDesc(HeightRtResolution, HeightRtResolution)
            {
                name = HeightRtDepthName,
                colorFormat = GraphicsFormat.None,
                depthBufferBits = DepthBits.Depth32,
                msaaSamples = MSAASamples.None,
                clearBuffer = true
            };
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            Vector3 eye = center + Vector3.up * HeightRtCameraAltitude;
            Quaternion rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Matrix4x4 cameraToWorld = Matrix4x4.TRS(eye, rotation, Vector3.one);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraToWorld.inverse;
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-HeightRtHalfExtent, HeightRtHalfExtent,
                                -HeightRtHalfExtent, HeightRtHalfExtent,
                                0f, HeightRtDepthRange), true);

            using var builder = renderGraph.AddRasterRenderPass<HeightRtPassData>(
                _heightRtSampler.name, out HeightRtPassData data, _heightRtSampler);
            data.material = _heightRtMaterial;
            data.block = _scratchBlock;
            data.mesh = GetHeightRtGrid();
            data.model = Matrix4x4.Translate(center);
            data.viewProjection = projection * view;
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetGlobalTextureAfterPass(color, ID_WaterHeightRT);
            Shader.SetGlobalVector(ID_WaterHeightRTFrame,
                new Vector4(centerX, centerZ, HeightRtHalfExtent, 1f));
            builder.SetRenderFunc((HeightRtPassData d, RasterGraphContext ctx) =>
            {
                Renderer source = s_SurfaceRenderers[0];
                source.GetPropertyBlock(d.block);
                d.block.SetMatrix(ID_WaterHeightRTViewProjection, d.viewProjection);
                ctx.cmd.DrawMesh(d.mesh, d.model, d.material, 0, 0, d.block);
            });
        }

        static Mesh GetHeightRtGrid()
        {
            if (s_HeightRtGrid != null) return s_HeightRtGrid;

            int apronCells = Mathf.CeilToInt(HeightRtChopApron / HeightRtTexelSize);
            int cellsPerAxis = HeightRtResolution + apronCells * 2;
            int verticesPerAxis = cellsPerAxis + 1;
            var vertices = new Vector3[verticesPerAxis * verticesPerAxis];
            var indices = new int[cellsPerAxis * cellsPerAxis * 6];
            float gridHalfExtent = HeightRtHalfExtent + HeightRtChopApron;
            int vertexIndex = 0;
            for (int z = 0; z < verticesPerAxis; z++)
            {
                for (int x = 0; x < verticesPerAxis; x++)
                {
                    vertices[vertexIndex++] = new Vector3(-gridHalfExtent + x * HeightRtTexelSize,
                                                          0f,
                                                          -gridHalfExtent + z * HeightRtTexelSize);
                }
            }
            int index = 0;
            for (int z = 0; z < cellsPerAxis; z++)
            {
                for (int x = 0; x < cellsPerAxis; x++)
                {
                    int lowerLeft = z * verticesPerAxis + x;
                    int upperLeft = lowerLeft + verticesPerAxis;
                    indices[index++] = lowerLeft;
                    indices[index++] = upperLeft;
                    indices[index++] = lowerLeft + 1;
                    indices[index++] = lowerLeft + 1;
                    indices[index++] = upperLeft;
                    indices[index++] = upperLeft + 1;
                }
            }
            s_HeightRtGrid = new Mesh
            {
                name = "WaterHeightRT.Grid",
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.HideAndDontSave
            };
            s_HeightRtGrid.vertices = vertices;
            s_HeightRtGrid.SetIndices(indices, MeshTopology.Triangles, 0, calculateBounds: false);
            s_HeightRtGrid.bounds = new Bounds(Vector3.zero,
                new Vector3(gridHalfExtent * 2f, HeightRtDepthRange, gridHalfExtent * 2f));
            return s_HeightRtGrid;
        }

        // The waterline meniscus draws over the fogged scene AND (for the KWS-style lens tension)
        // re-samples it at a warped UV - a raster pass cannot read its own colour target, so the
        // scene is copied to a transient first and handed to the material. The copy costs one
        // camera-sized blit only during the few straddle frames the waterline is armed.
        void RecordWaterlinePass(RenderGraph renderGraph, UniversalResourceData resources,
                                 TextureHandle cameraColor, TextureHandle classifyRt)
        {
            // The scene copy feeds ONLY the lens-tension warp: the shader samples
            // _WaterlineSceneTex exclusively inside its `_WaterlineWarp > 0` branch, so at
            // warp 0 the camera-sized copy was dead work on every straddle frame. Gated on the
            // SAME knob that uniform is published from (the fog source's MeniscusWarp,
            // PublishWaterline); black is bound in its place so no backend ever sees a stale
            // transient on the sampler.
            WaterVolume warpSource = WaterVolume.FogSource;
            bool warpActive = warpSource != null && warpSource.MeniscusWarp > 0f;
            TextureHandle sceneCopy = default;
            if (warpActive)
            {
                TextureDesc copyDesc = renderGraph.GetTextureDesc(cameraColor);
                copyDesc.name = "_WaterlineSceneTex";
                copyDesc.clearBuffer = false;
                sceneCopy = renderGraph.CreateTexture(copyDesc);
                renderGraph.AddCopyPass(cameraColor, sceneCopy, passName: "WaterUnderwaterFog.WaterlineCopy");
            }

            using var builder = renderGraph.AddRasterRenderPass<WaterlinePassData>(
                "WaterUnderwaterFog.Waterline", out WaterlinePassData data, _sampler);
            data.material = _material;
            data.sceneCopy = sceneCopy;
            data.warpActive = warpActive;
            data.useClassifyRt = classifyRt.IsValid();
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            if (warpActive) builder.UseTexture(sceneCopy, AccessFlags.Read);
            if (classifyRt.IsValid()) builder.UseTexture(classifyRt, AccessFlags.Read);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            builder.UseAllGlobalTextures(true);
            // The classified reader is a real shader variant, not a uniform branch. RenderGraph
            // requires an explicit declaration before the command buffer may select that keyword.
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc((WaterlinePassData d, RasterGraphContext ctx) =>
            {
                if (d.useClassifyRt) ctx.cmd.EnableShaderKeyword(ClassifyRtKeyword);
                else ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
                if (d.warpActive) d.material.SetTexture(ID_WaterlineSceneTex, d.sceneCopy);
                else d.material.SetTexture(ID_WaterlineSceneTex, Texture2D.blackTexture);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, WaterlineShaderPass);
                if (d.useClassifyRt) ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
            });
        }

        sealed class WaterlinePassData
        {
            public Material material;
            public TextureHandle sceneCopy;
            public bool warpActive;
            public bool useClassifyRt;
        }

        // Draw every canonical above-surface mesh with its OWN matrix, material and property block
        // through WaterSurface.shader's two-sided depth pass, so displacement matches the visible
        // surface without submitting the coincident under-surface twin.
        void RecordSurfaceDepthPrepass(RenderGraph renderGraph, TextureHandle sizeSource)
        {
            // Camera-sized R32F colour (linear eye depth; clear 0 = "no surface") + its own depth
            // buffer so the nearest sheet wins where above/under overlap on screen.
            TextureDesc colorDesc = renderGraph.GetTextureDesc(sizeSource);
            colorDesc.name = "_OceanSurfaceEyeDepth";
            colorDesc.colorFormat = GraphicsFormat.R32_SFloat;
            colorDesc.depthBufferBits = DepthBits.None;
            colorDesc.msaaSamples = MSAASamples.None;
            colorDesc.clearBuffer = true;
            colorDesc.clearColor = Color.clear;
            float appliedScale = ApplyPrepassScale(ref colorDesc);
            Shader.SetGlobalFloat(ID_OceanSurfacePrepassScale, appliedScale);
            TextureHandle color = renderGraph.CreateTexture(colorDesc);

            // R = rendered wet ownership (0 above/front, 1 under/back), G = validity. Clear
            // validity is 0, so near-clipped pixels and exclusion holes can blend back to the
            // analytic classification instead of absence being mistaken for air. Bilinear reads
            // of this low-resolution target provide the stable transition KWS gets from its mask.
            TextureDesc ownershipDesc = renderGraph.GetTextureDesc(sizeSource);
            ownershipDesc.name = "_OceanSurfaceOwnership";
            ownershipDesc.colorFormat = GraphicsFormat.R8G8_UNorm;
            ownershipDesc.depthBufferBits = DepthBits.None;
            ownershipDesc.msaaSamples = MSAASamples.None;
            ownershipDesc.clearBuffer = true;
            ownershipDesc.clearColor = Color.clear;
            ApplyPrepassScale(ref ownershipDesc);
            TextureHandle ownership = renderGraph.CreateTexture(ownershipDesc);

            TextureDesc depthDesc = renderGraph.GetTextureDesc(sizeSource);
            depthDesc.name = "OceanSurfaceDepthBuffer";
            depthDesc.colorFormat = GraphicsFormat.None;
            depthDesc.depthBufferBits = DepthBits.Depth32;
            depthDesc.msaaSamples = MSAASamples.None;
            depthDesc.clearBuffer = true;
            ApplyPrepassScale(ref depthDesc);
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            using var builder = renderGraph.AddRasterRenderPass<PrepassData>(_prepassSampler.name,
                out PrepassData data, _prepassSampler);
            data.renderers = s_SurfaceRenderers;
            data.block = _scratchBlock;
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.SetRenderAttachment(ownership, 1, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);                          // driven by our own list
            builder.SetGlobalTextureAfterPass(color, ID_OceanSurfaceEyeDepth); // fog reads it later this frame
            builder.SetGlobalTextureAfterPass(ownership, ID_OceanSurfaceOwnership);
            builder.SetRenderFunc((PrepassData d, RasterGraphContext ctx) =>
            {
                for (int i = 0; i < d.renderers.Count; i++)
                {
                    Renderer renderer = d.renderers[i];
                    if (renderer == null || renderer.sharedMaterial == null) continue;
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null) continue;
                    renderer.GetPropertyBlock(d.block); // the renderer's live per-body/per-level uniforms
                    ctx.cmd.DrawMesh(filter.sharedMesh, renderer.localToWorldMatrix,
                                     renderer.sharedMaterial, 0, SurfaceDepthShaderPass, d.block);
                }
            });
        }

        // Shrink a camera-sized desc to the prepass resolution, whatever size mode the source desc
        // carries (URP's camera color is usually Explicit; Scale covers dynamic-resolution setups).
        // Returns the scale ACTUALLY applied, so the published uniform can never disagree with
        // the RT that was allocated (Functor mode cannot be composed and stays full res).
        static float ApplyPrepassScale(ref TextureDesc desc)
        {
            if (desc.sizeMode == TextureSizeMode.Explicit)
            {
                desc.width = Mathf.Max(1, (int)(desc.width * PrepassResolutionScale));
                desc.height = Mathf.Max(1, (int)(desc.height * PrepassResolutionScale));
                return PrepassResolutionScale;
            }
            if (desc.sizeMode == TextureSizeMode.Scale)
            {
                desc.scale *= PrepassResolutionScale;
                return PrepassResolutionScale;
            }
            return 1f; // Functor: full res, and the uniform must say so
        }

        void RecordFogPass(RenderGraph renderGraph, UniversalResourceData resources,
                           TextureHandle cameraColor, string passName, TextureHandle classifyRt)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            data.useClassifyRt = classifyRt.IsValid();
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            if (classifyRt.IsValid()) builder.UseTexture(classifyRt, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // published fog globals (shore field, FFT displacement, ...)
            // See the waterline pass above: variant selection mutates command-buffer global state.
            builder.AllowGlobalStateModification(true);
            // Two draws, ONE raster pass. Absorb multiplies the destination (Blend Zero SrcColor) and
            // inscatter adds to it (Blend One One) - both composite through the fixed-function blender,
            // and NEITHER shader samples the colour target, so this is ordinary blend accumulation in
            // submission order, not a read-after-write on the attachment. (Where a self-read IS needed,
            // RecordWaterlinePass copies to a transient first - deliberately, for exactly that reason.)
            // Same shape as WaterCausticProjectionPass, which already accumulates N fullscreen draws
            // with this very pair of blend modes into one ReadWrite colour attachment.
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                if (d.useClassifyRt) ctx.cmd.EnableShaderKeyword(ClassifyRtKeyword);
                else ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, AbsorbShaderPass);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, InscatterShaderPass);
                if (d.useClassifyRt) ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
            });
        }
    }
}
#endif
