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
        // C1 single-solve intermediates (2026-08-13): the "WaterFogSolve" MRT pass runs the full
        // per-pixel fog solve ONCE and writes both blend terms; the absorb/inscatter draws just
        // load them. Half floats: the terms already travelled through half4 fragment outputs, so
        // 16 bits per channel loses nothing. Alpha carries the debug-view flag.
        const string SolveShaderPassName = "WaterFogSolve";
        const string SolveAbsorbTextureName = "_WaterFogSolveAbsorb";
        const string SolveInscatterTextureName = "_WaterFogSolveInscatter";
        const GraphicsFormat SolveRtFormat = GraphicsFormat.R16G16B16A16_SFloat;
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
        static readonly int ID_WaterHeightRTIncludeRipple =
            Shader.PropertyToID("_WaterHeightRTIncludeRipple");
        static readonly int ID_WaterLensHeightRT = Shader.PropertyToID("_WaterLensHeightRT");
        static readonly int ID_WaterLensHeightRTFrame = Shader.PropertyToID("_WaterLensHeightRTFrame");
        internal const int HeightRtResolution = 256;
        internal const float HeightRtWindowSize = 512f;
        const float HeightRtHalfExtent = HeightRtWindowSize * 0.5f;
        const float HeightRtTexelSize = HeightRtWindowSize / HeightRtResolution;
        const float HeightRtChopApron = 16f;
        const float HeightRtCameraAltitude = 1024f;
        const float HeightRtDepthRange = 2048f;
        const string HeightRtTextureName = "_WaterHeightRT";
        const string HeightRtDepthName = "WaterHeightRT.Depth";
        // Crest's useful distinction is two scales: the wide field answers ray marching, while a
        // dense lens field answers the centimetre waterline. Four metres covers a normal camera's
        // near-plane footprint; points outside it retain the analytic fallback. The coarser vertex
        // lattice is safe because the raster target interpolates the displaced mesh between samples,
        // while the fixed chop apron admits source vertices displaced horizontally into the window.
        const int LensHeightRtResolution = 256;
        const float LensHeightRtWindowSize = 4f;
        const float LensHeightRtHalfExtent = LensHeightRtWindowSize * 0.5f;
        const float LensHeightRtTexelSize = LensHeightRtWindowSize / LensHeightRtResolution;
        const float LensHeightRtGridCellSize = 0.125f;
        // C3 (2026-08-13): the 16 m chop apron is the maximum horizontal chop reach and must NOT
        // shrink (storm chop would hole the centimetre waterline) - but at 0.125 m cells it was
        // 128 cells per side: ~98% of an 83k-vert grid whose only job is delivering geometry
        // displaced INTO the 4 m window, which the raster interpolates anyway. The apron ring
        // therefore samples at 1 m; the dense window keeps its centimetre cells; the 8:1 boundary
        // is stitched with triangle fans so independently displaced vertices cannot open
        // T-junction cracks in the height/coverage raster. ~83k verts -> ~2.4k.
        const float LensApronCellSize = 1f;
        const string LensHeightRtTextureName = "_WaterLensHeightRT";
        const string LensHeightRtDepthName = "WaterLensHeightRT.Depth";
        const string HeightRtGridName = "WaterHeightRT.Grid";
        const string LensHeightRtGridName = "WaterLensHeightRT.Grid";
        const string LensHeightRtPassName = "WaterUnderwaterFog.LensHeightRT";
        const GraphicsFormat LensHeightRtFormat = GraphicsFormat.R16G16_SFloat;

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
        static readonly int ID_WaterFogSolveAbsorb = Shader.PropertyToID(SolveAbsorbTextureName);
        static readonly int ID_WaterFogSolveInscatter =
            Shader.PropertyToID(SolveInscatterTextureName);

        readonly Material _material;
        readonly Material _heightRtMaterial;
        readonly ProfilingSampler _sampler = new ProfilingSampler("WaterUnderwaterFog");
        readonly ProfilingSampler _prepassSampler = new ProfilingSampler("WaterUnderwaterFog.SurfaceDepth");
        readonly ProfilingSampler _heightRtSampler = new ProfilingSampler("WaterUnderwaterFog.HeightRT");
        readonly ProfilingSampler _lensHeightRtSampler = new ProfilingSampler(LensHeightRtPassName);
        readonly ProfilingSampler _classifySampler = new ProfilingSampler("WaterUnderwaterFog.Classify");
        readonly ProfilingSampler _solveSampler = new ProfilingSampler("WaterUnderwaterFog.Solve");
        readonly int _classifyShaderPass;
        readonly bool _classifyRtSupported;
        readonly bool _lensHeightRtSupported;
        readonly int _solveShaderPass;
        readonly bool _solveRtSupported;
        // One complaint per session, not per frame: C1 makes the solve pass a hard requirement
        // of the fog chain (the blend passes have nothing correct to load without it), so a
        // missing pass/format skips the fog and says so once.
        static bool s_SolveUnsupportedLogged;
        // Reused each frame so the prepass allocates no garbage.
        readonly MaterialPropertyBlock _scratchBlock = new MaterialPropertyBlock();
        static readonly List<Renderer> s_SurfaceRenderers = new List<Renderer>();
        static Mesh s_HeightRtGrid;
        static Mesh s_LensHeightRtGrid;

        internal WaterUnderwaterFogPass(Material material, Material heightRtMaterial)
        {
            _material = material;
            _heightRtMaterial = heightRtMaterial;
            _classifyShaderPass = material != null
                ? material.FindPass(ClassifyShaderPassName)
                : InvalidShaderPass;
            _solveShaderPass = material != null
                ? material.FindPass(SolveShaderPassName)
                : InvalidShaderPass;
            _classifyRtSupported = SystemInfo.IsFormatSupported(ClassifyRtFormat,
                                                                GraphicsFormatUsage.Render);
            _solveRtSupported = SystemInfo.IsFormatSupported(SolveRtFormat,
                                                             GraphicsFormatUsage.Render);
            _lensHeightRtSupported = SystemInfo.IsFormatSupported(LensHeightRtFormat,
                                                                   GraphicsFormatUsage.Render)
                                  && SystemInfo.IsFormatSupported(LensHeightRtFormat,
                                                                  GraphicsFormatUsage.Sample);
            renderPassEvent = InjectionPoint;
        }

        sealed class PassData
        {
            public Material material;
        }

        sealed class SolvePassData
        {
            public Material material;
            public int shaderPass;
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
            public bool includeRipple;
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
            bool lensHeightRtRecorded = classifyRtRecorded
                                     && fogSource.IsOceanClipmap
                                     && _heightRtMaterial != null
                                     && _lensHeightRtSupported
                                     && s_SurfaceRenderers.Count > 0;
            if (lensHeightRtRecorded)
                RecordLensHeightRt(renderGraph, cameraData, fogSource.VolumeCenter.y);
            else
                Shader.SetGlobalVector(ID_WaterLensHeightRTFrame, Vector4.zero);
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

            using var builder = renderGraph.AddRasterRenderPass<HeightRtPassData>(
                _heightRtSampler.name, out HeightRtPassData data, _heightRtSampler);
            data.material = _heightRtMaterial;
            data.block = _scratchBlock;
            data.mesh = GetHeightRtGrid();
            data.model = Matrix4x4.Translate(center);
            data.viewProjection = CreateHeightRtViewProjection(center, HeightRtHalfExtent);
            data.includeRipple = false;
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
                d.block.SetFloat(ID_WaterHeightRTIncludeRipple, d.includeRipple ? 1f : 0f);
                ctx.cmd.DrawMesh(d.mesh, d.model, d.material, 0, 0, d.block);
            });
        }

        void RecordLensHeightRt(RenderGraph renderGraph, UniversalCameraData cameraData,
                                float restPlaneY)
        {
            Vector3 cameraPosition = cameraData.worldSpaceCameraPos;
            float centerX = Mathf.Floor(cameraPosition.x / LensHeightRtTexelSize)
                          * LensHeightRtTexelSize;
            float centerZ = Mathf.Floor(cameraPosition.z / LensHeightRtTexelSize)
                          * LensHeightRtTexelSize;
            Vector3 center = new Vector3(centerX, restPlaneY, centerZ);

            TextureDesc colorDesc = new TextureDesc(LensHeightRtResolution, LensHeightRtResolution)
            {
                name = LensHeightRtTextureName,
                colorFormat = LensHeightRtFormat,
                depthBufferBits = DepthBits.None,
                msaaSamples = MSAASamples.None,
                clearBuffer = true,
                clearColor = Color.clear,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            TextureHandle color = renderGraph.CreateTexture(colorDesc);
            TextureDesc depthDesc = new TextureDesc(LensHeightRtResolution, LensHeightRtResolution)
            {
                name = LensHeightRtDepthName,
                colorFormat = GraphicsFormat.None,
                depthBufferBits = DepthBits.Depth32,
                msaaSamples = MSAASamples.None,
                clearBuffer = true
            };
            TextureHandle depth = renderGraph.CreateTexture(depthDesc);

            Matrix4x4 viewProjection = CreateHeightRtViewProjection(center, LensHeightRtHalfExtent);
            using var builder = renderGraph.AddRasterRenderPass<HeightRtPassData>(
                _lensHeightRtSampler.name, out HeightRtPassData data, _lensHeightRtSampler);
            data.material = _heightRtMaterial;
            data.block = _scratchBlock;
            data.mesh = GetLensHeightRtGrid();
            data.model = Matrix4x4.Translate(center);
            data.viewProjection = viewProjection;
            data.includeRipple = true;
            builder.SetRenderAttachment(color, 0, AccessFlags.Write);
            builder.SetRenderAttachmentDepth(depth, AccessFlags.Write);
            builder.AllowPassCulling(false);
            builder.SetGlobalTextureAfterPass(color, ID_WaterLensHeightRT);
            Shader.SetGlobalVector(ID_WaterLensHeightRTFrame,
                new Vector4(centerX, centerZ, LensHeightRtHalfExtent, 1f));
            builder.SetRenderFunc((HeightRtPassData d, RasterGraphContext ctx) =>
            {
                Renderer source = s_SurfaceRenderers[0];
                source.GetPropertyBlock(d.block);
                d.block.SetMatrix(ID_WaterHeightRTViewProjection, d.viewProjection);
                d.block.SetFloat(ID_WaterHeightRTIncludeRipple, d.includeRipple ? 1f : 0f);
                ctx.cmd.DrawMesh(d.mesh, d.model, d.material, 0, 0, d.block);
            });
        }

        static Matrix4x4 CreateHeightRtViewProjection(Vector3 center, float halfExtent)
        {
            Vector3 eye = center + Vector3.up * HeightRtCameraAltitude;
            Quaternion rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            Matrix4x4 cameraToWorld = Matrix4x4.TRS(eye, rotation, Vector3.one);
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraToWorld.inverse;
            Matrix4x4 projection = GL.GetGPUProjectionMatrix(
                Matrix4x4.Ortho(-halfExtent, halfExtent, -halfExtent, halfExtent,
                                0f, HeightRtDepthRange), true);
            return projection * view;
        }

        static Mesh GetHeightRtGrid()
        {
            return GetOrCreateHeightRtGrid(ref s_HeightRtGrid, HeightRtGridName,
                                           HeightRtWindowSize, HeightRtTexelSize);
        }

        static Mesh GetLensHeightRtGrid()
        {
            if (s_LensHeightRtGrid != null) return s_LensHeightRtGrid;
            s_LensHeightRtGrid = CreateTwoDensityGrid(LensHeightRtGridName,
                                                      LensHeightRtWindowSize,
                                                      LensHeightRtGridCellSize,
                                                      HeightRtChopApron, LensApronCellSize);
            return s_LensHeightRtGrid;
        }

        static Mesh GetOrCreateHeightRtGrid(ref Mesh grid, string name, float windowSize,
                                            float gridCellSize)
        {
            if (grid != null) return grid;

            int apronCells = Mathf.CeilToInt(HeightRtChopApron / gridCellSize);
            int windowCells = Mathf.CeilToInt(windowSize / gridCellSize);
            int cellsPerAxis = windowCells + apronCells * 2;
            int verticesPerAxis = cellsPerAxis + 1;
            var vertices = new Vector3[verticesPerAxis * verticesPerAxis];
            var indices = new int[cellsPerAxis * cellsPerAxis * 6];
            float gridHalfExtent = windowSize * 0.5f + HeightRtChopApron;
            int vertexIndex = 0;
            for (int z = 0; z < verticesPerAxis; z++)
            {
                for (int x = 0; x < verticesPerAxis; x++)
                {
                    vertices[vertexIndex++] = new Vector3(-gridHalfExtent + x * gridCellSize,
                                                          0f,
                                                          -gridHalfExtent + z * gridCellSize);
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
            grid = new Mesh
            {
                name = name,
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.HideAndDontSave
            };
            grid.vertices = vertices;
            grid.SetIndices(indices, MeshTopology.Triangles, 0, calculateBounds: false);
            grid.bounds = new Bounds(Vector3.zero,
                new Vector3(gridHalfExtent * 2f, HeightRtDepthRange, gridHalfExtent * 2f));
            return grid;
        }

        // Two-density grid (C3): a dense inner window, a coarse apron ring, and a one-coarse-cell
        // stitching band of triangle fans between them, so the 8:1 vertex-density change shares
        // every boundary vertex. A T-junction would let independently displaced vertices open
        // cracks in the rasterised height/coverage exactly where storm chop hands off into the
        // window; the fans make the two lattices watertight by construction.
        static Mesh CreateTwoDensityGrid(string name, float windowSize, float innerCellSize,
                                         float apron, float coarseCellSize)
        {
            const float LatticeEpsilon = 1e-4f;
            int stitchRatio = Mathf.RoundToInt(coarseCellSize / innerCellSize);
            if (Mathf.Abs(stitchRatio * innerCellSize - coarseCellSize) > LatticeEpsilon)
                throw new System.InvalidOperationException(
                    "CreateTwoDensityGrid: the coarse cell must be an integer multiple of the inner cell.");
            float innerHalf = windowSize * 0.5f;
            if (Mathf.Abs(Mathf.Round(innerHalf / coarseCellSize) * coarseCellSize - innerHalf)
                > LatticeEpsilon)
                throw new System.InvalidOperationException(
                    "CreateTwoDensityGrid: the window half extent must sit on the coarse lattice.");
            float outerHalf = innerHalf + apron;
            // The pure-coarse region starts one coarse cell outside the dense window; the frame
            // between the two squares is the stitching band.
            float coarseInnerHalf = innerHalf + coarseCellSize;

            var vertices = new List<Vector3>();
            var indices = new List<int>();

            void AddQuad(int lowerLeft, int upperLeft, int lowerRight, int upperRight)
            {
                indices.Add(lowerLeft); indices.Add(upperLeft); indices.Add(lowerRight);
                indices.Add(lowerRight); indices.Add(upperLeft); indices.Add(upperRight);
            }

            // Winding of the plain quads above is negative in xz; the fans match it by
            // construction here, so the mesh stays orientation-consistent (the height RT
            // material culls off today, but a consistent mesh keeps that a free choice).
            void AddTriangleOriented(int a, int b, int c)
            {
                Vector3 pa = vertices[a];
                Vector3 pb = vertices[b];
                Vector3 pc = vertices[c];
                float cross = (pb.x - pa.x) * (pc.z - pa.z) - (pb.z - pa.z) * (pc.x - pa.x);
                if (cross > 0f) { int swap = b; b = c; c = swap; }
                indices.Add(a); indices.Add(b); indices.Add(c);
            }

            // 1) Dense inner grid, plain quads.
            int innerCells = Mathf.RoundToInt(windowSize / innerCellSize);
            int innerVertsPerAxis = innerCells + 1;
            for (int z = 0; z < innerVertsPerAxis; z++)
                for (int x = 0; x < innerVertsPerAxis; x++)
                    vertices.Add(new Vector3(-innerHalf + x * innerCellSize, 0f,
                                             -innerHalf + z * innerCellSize));
            int DenseIndex(int xi, int zi) => zi * innerVertsPerAxis + xi;
            for (int z = 0; z < innerCells; z++)
            {
                for (int x = 0; x < innerCells; x++)
                {
                    int lowerLeft = DenseIndex(x, z);
                    AddQuad(lowerLeft, lowerLeft + innerVertsPerAxis,
                            lowerLeft + 1, lowerLeft + innerVertsPerAxis + 1);
                }
            }

            // 2) Coarse lattice vertices: every coarse point of the full grid on or outside the
            //    coarse inner square (its strict interior belongs to the dense grid + the fans).
            int coarseCellsPerAxis = Mathf.RoundToInt(outerHalf * 2f / coarseCellSize);
            int coarseVertsPerAxis = coarseCellsPerAxis + 1;
            var coarseLookup = new int[coarseVertsPerAxis * coarseVertsPerAxis];
            for (int i = 0; i < coarseLookup.Length; i++) coarseLookup[i] = -1;
            for (int z = 0; z < coarseVertsPerAxis; z++)
            {
                for (int x = 0; x < coarseVertsPerAxis; x++)
                {
                    float worldX = -outerHalf + x * coarseCellSize;
                    float worldZ = -outerHalf + z * coarseCellSize;
                    if (Mathf.Max(Mathf.Abs(worldX), Mathf.Abs(worldZ))
                        < coarseInnerHalf - LatticeEpsilon) continue;
                    coarseLookup[z * coarseVertsPerAxis + x] = vertices.Count;
                    vertices.Add(new Vector3(worldX, 0f, worldZ));
                }
            }
            int CoarseAt(float worldX, float worldZ)
            {
                int xi = Mathf.RoundToInt((worldX + outerHalf) / coarseCellSize);
                int zi = Mathf.RoundToInt((worldZ + outerHalf) / coarseCellSize);
                int index = coarseLookup[zi * coarseVertsPerAxis + xi];
                if (index < 0)
                    throw new System.InvalidOperationException(
                        "CreateTwoDensityGrid: missing coarse vertex at (" + worldX + ", "
                        + worldZ + ").");
                return index;
            }

            // 3) Coarse quads: every coarse cell not fully inside the coarse inner square.
            for (int z = 0; z < coarseCellsPerAxis; z++)
            {
                for (int x = 0; x < coarseCellsPerAxis; x++)
                {
                    float x0 = -outerHalf + x * coarseCellSize;
                    float z0 = -outerHalf + z * coarseCellSize;
                    bool insideHole = x0 > -coarseInnerHalf - LatticeEpsilon
                                   && x0 + coarseCellSize < coarseInnerHalf + LatticeEpsilon
                                   && z0 > -coarseInnerHalf - LatticeEpsilon
                                   && z0 + coarseCellSize < coarseInnerHalf + LatticeEpsilon;
                    if (insideHole) continue;
                    AddQuad(CoarseAt(x0, z0),
                            CoarseAt(x0, z0 + coarseCellSize),
                            CoarseAt(x0 + coarseCellSize, z0),
                            CoarseAt(x0 + coarseCellSize, z0 + coarseCellSize));
                }
            }

            // 4) Stitching fans: each 1-coarse-cell boundary segment fans onto the stitchRatio+1
            //    dense edge vertices it spans, split at the midpoint so the fans stay shallow.
            void StitchSide(bool horizontal, float sign)
            {
                float outerEdge = coarseInnerHalf * sign;
                int denseEdgeIndex = sign > 0f ? innerCells : 0;
                int segments = Mathf.RoundToInt(windowSize / coarseCellSize);
                int mid = stitchRatio / 2;
                for (int segment = 0; segment < segments; segment++)
                {
                    float segmentStart = -innerHalf + segment * coarseCellSize;
                    int outer0 = horizontal ? CoarseAt(segmentStart, outerEdge)
                                            : CoarseAt(outerEdge, segmentStart);
                    int outer1 = horizontal
                        ? CoarseAt(segmentStart + coarseCellSize, outerEdge)
                        : CoarseAt(outerEdge, segmentStart + coarseCellSize);
                    int Dense(int step)
                    {
                        int along = segment * stitchRatio + step;
                        return horizontal ? DenseIndex(along, denseEdgeIndex)
                                          : DenseIndex(denseEdgeIndex, along);
                    }
                    for (int step = 0; step < mid; step++)
                        AddTriangleOriented(outer0, Dense(step), Dense(step + 1));
                    AddTriangleOriented(outer0, Dense(mid), outer1);
                    for (int step = mid; step < stitchRatio; step++)
                        AddTriangleOriented(outer1, Dense(step), Dense(step + 1));
                }
            }
            StitchSide(true, 1f);   // north (+z)
            StitchSide(true, -1f);  // south
            StitchSide(false, 1f);  // east (+x)
            StitchSide(false, -1f); // west

            // 5) The four corner cells of the stitching band: one dense corner + three coarse
            //    vertices each, every edge shared with a neighbouring fan or coarse quad.
            void CornerQuad(float signX, float signZ)
            {
                int denseCorner = DenseIndex(signX > 0f ? innerCells : 0,
                                             signZ > 0f ? innerCells : 0);
                int outerCorner = CoarseAt(coarseInnerHalf * signX, coarseInnerHalf * signZ);
                int edgeAlongX = CoarseAt(coarseInnerHalf * signX, innerHalf * signZ);
                int edgeAlongZ = CoarseAt(innerHalf * signX, coarseInnerHalf * signZ);
                AddTriangleOriented(denseCorner, edgeAlongX, outerCorner);
                AddTriangleOriented(denseCorner, outerCorner, edgeAlongZ);
            }
            CornerQuad(1f, 1f);
            CornerQuad(1f, -1f);
            CornerQuad(-1f, 1f);
            CornerQuad(-1f, -1f);

            var mesh = new Mesh
            {
                name = name,
                indexFormat = IndexFormat.UInt32,
                hideFlags = HideFlags.HideAndDontSave
            };
            mesh.SetVertices(vertices);
            mesh.SetIndices(indices, MeshTopology.Triangles, 0, calculateBounds: false);
            mesh.bounds = new Bounds(Vector3.zero,
                new Vector3(outerHalf * 2f, HeightRtDepthRange, outerHalf * 2f));
            return mesh;
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
            // C1 hard requirement: the blend draws below only load what the solve pass wrote, so
            // without the pass or a renderable half-float MRT format there is nothing correct to
            // composite. Fail fast and visibly (once) rather than blending garbage.
            if (_solveShaderPass == InvalidShaderPass || !_solveRtSupported)
            {
                if (!s_SolveUnsupportedLogged)
                {
                    s_SolveUnsupportedLogged = true;
                    Debug.LogError(
                        "WaterUnderwaterFogPass: 'WaterFogSolve' shader pass or R16G16B16A16_SFloat "
                        + "render support missing - underwater fog skipped.");
                }
                return;
            }

            TextureHandle solveAbsorb = CreateSolveTexture(renderGraph, cameraColor,
                                                           SolveAbsorbTextureName);
            TextureHandle solveInscatter = CreateSolveTexture(renderGraph, cameraColor,
                                                              SolveInscatterTextureName);
            RecordFogSolvePass(renderGraph, resources, solveAbsorb, solveInscatter, classifyRt);

            using var builder = renderGraph.AddRasterRenderPass<PassData>(passName, out PassData data, _sampler);

            data.material = _material;
            // ReadWrite loads the existing scene so the hardware blend composites onto it.
            builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
            builder.UseTexture(solveAbsorb, AccessFlags.Read);
            builder.UseTexture(solveInscatter, AccessFlags.Read);
            // The solve targets are read through their global names (the SetGlobalTextureAfterPass
            // handoff convention) - same UseTexture + globals pairing the classify RT ships with.
            builder.UseAllGlobalTextures(true);
            // Two draws, ONE raster pass. Absorb multiplies the destination (Blend Zero SrcColor) and
            // inscatter adds to it (Blend One One) - both composite through the fixed-function blender,
            // and NEITHER shader samples the colour target, so this is ordinary blend accumulation in
            // submission order, not a read-after-write on the attachment. (Where a self-read IS needed,
            // RecordWaterlinePass copies to a transient first - deliberately, for exactly that reason.)
            // Since C1 both draws are single loads of the solve targets: variant-free, so the keyword
            // juggling this pass used to do moved to RecordFogSolvePass with the heavy programs.
            builder.SetRenderFunc((PassData d, RasterGraphContext ctx) =>
            {
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, AbsorbShaderPass);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, InscatterShaderPass);
            });
        }

        static TextureHandle CreateSolveTexture(RenderGraph renderGraph, TextureHandle sizeSource,
                                                string name)
        {
            TextureDesc desc = renderGraph.GetTextureDesc(sizeSource);
            desc.name = name;
            desc.colorFormat = SolveRtFormat;
            desc.depthBufferBits = DepthBits.None;
            desc.msaaSamples = MSAASamples.None;
            desc.clearBuffer = false; // the fullscreen solve writes every pixel
            return renderGraph.CreateTexture(desc);
        }

        // The single full per-pixel fog solve (C1): runs the "WaterFogSolve" MRT pass once into
        // the two intermediates the blend pass loads. Carries the classify-RT keyword selection
        // that used to wrap the two heavy draws.
        void RecordFogSolvePass(RenderGraph renderGraph, UniversalResourceData resources,
                                TextureHandle solveAbsorb, TextureHandle solveInscatter,
                                TextureHandle classifyRt)
        {
            using var builder = renderGraph.AddRasterRenderPass<SolvePassData>(
                _solveSampler.name, out SolvePassData data, _solveSampler);
            data.material = _material;
            data.shaderPass = _solveShaderPass;
            data.useClassifyRt = classifyRt.IsValid();
            builder.SetRenderAttachment(solveAbsorb, 0, AccessFlags.Write);
            builder.SetRenderAttachment(solveInscatter, 1, AccessFlags.Write);
            if (resources.cameraDepthTexture.IsValid())
                builder.UseTexture(resources.cameraDepthTexture, AccessFlags.Read);
            if (classifyRt.IsValid()) builder.UseTexture(classifyRt, AccessFlags.Read);
            builder.UseAllGlobalTextures(true); // published fog globals (shore field, FFT displacement, ...)
            builder.AllowPassCulling(false);
            // The classified reader is a real shader variant, not a uniform branch. RenderGraph
            // requires an explicit declaration before the command buffer may select that keyword.
            builder.AllowGlobalStateModification(true);
            builder.SetGlobalTextureAfterPass(solveAbsorb, ID_WaterFogSolveAbsorb);
            builder.SetGlobalTextureAfterPass(solveInscatter, ID_WaterFogSolveInscatter);
            builder.SetRenderFunc((SolvePassData d, RasterGraphContext ctx) =>
            {
                if (d.useClassifyRt) ctx.cmd.EnableShaderKeyword(ClassifyRtKeyword);
                else ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
                CoreUtils.DrawFullScreen(ctx.cmd, d.material, null, d.shaderPass);
                if (d.useClassifyRt) ctx.cmd.DisableShaderKeyword(ClassifyRtKeyword);
            });
        }
    }
}
#endif
