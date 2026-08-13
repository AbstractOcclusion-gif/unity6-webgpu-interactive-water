// WebGpuWater - per-body caustics render pass.
// Extracted from WaterVolume: owns the caustic material, render target and command
// buffer, and renders the body's own sim into its own caustic RT - so caustics never
// come from whatever body last wrote the _WaterTex global. The RT reaches the body's
// renderers via the property block; the primary also mirrors it to the _CausticTex
// global for objects without a WaterMembership.
using UnityEngine;
using UnityEngine.Rendering;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterCausticsPass
    {
        static readonly int ID_Water = WaterShaderProps.WaterTex;
        static readonly int ID_SimCenter = WaterShaderProps.SimCenter;
        static readonly int ID_SimExtent = WaterShaderProps.SimExtent;
        static readonly int ID_LightDir = WaterShaderProps.LightDir;
        static readonly int ID_VolumeCenter = WaterShaderProps.VolumeCenter;
        static readonly int ID_VolumeExtent = WaterShaderProps.VolumeExtent;
        static readonly int ID_VolumeRot = WaterShaderProps.VolumeRot;
        static readonly int ID_CausticSmooth = Shader.PropertyToID("_LargeGodRayCausticSmooth");
        static readonly int ID_CausticTime = Shader.PropertyToID("_LargeCausticTime");
        static readonly int ID_CausticRippleScale = Shader.PropertyToID("_LargeCausticRippleScale");
        // Normalised step between adjacent caustic-MESH vertices. Both generators measure their
        // focusing Jacobian over exactly this span, so it has to describe the mesh this pass draws -
        // not the sim texture, which merely happens to share its resolution today.
        static readonly int ID_CausticGridStep = Shader.PropertyToID("_CausticGridStepNorm");
        static readonly int ID_CausticRippleStrength = Shader.PropertyToID("_LargeCausticRippleStrength");
        static readonly int ID_PoolSlopeToWorld = Shader.PropertyToID("_PoolSlopeToWorld");
        static readonly int ID_SimSlopeToWorld = Shader.PropertyToID("_SimSlopeToWorld");
        static readonly int ID_WaveNormalStrength = Shader.PropertyToID("_WaveNormalStrength");
        static readonly int ID_OceanFftActive = Shader.PropertyToID("_OceanFftActive");
        static readonly int ID_LargeWaveAmplitude = Shader.PropertyToID("_LargeWaveAmplitude");

        // Green channel of the caustic RT starts at 1 (unshadowed) so floor fragments that sample
        // outside the drawn caustic footprint read "lit", not black, now that green drives the
        // underwater object shadow. The occluder pass writes 0 under a submerged object.
        static readonly Color CausticClear = new Color(0f, 1f, 0f, 0f);

        readonly Material _material;
        readonly Material _largeBodyMaterial; // null when the large-body caustics shader isn't assigned (oceans only)
        readonly Material _occluderMaterial;  // null when the occluder shader isn't assigned -> object shadows stay on the shadow map
        readonly RenderTexture _target;
        // The caustic pass's OWN sampling lattice, decoupled from the surface/patch mesh the body
        // draws. Null when WaterVolume.CausticGridResolution asked for the caller's mesh instead
        // (detail 1x, or a disc pool) - which is the default, so this is inert until opted into.
        readonly Mesh _causticGrid;
        readonly int _causticGridRes;
        readonly CommandBuffer _cb;
        // The body this pass belongs to: DrawOccluders draws ONLY interactables contained in this
        // body. The old unfiltered loop stamped EVERY submerged interactable in the scene into
        // every body's caustic RT through that body's frame - close-together pools got each
        // other's silhouettes as phantom duplicate shadows (the Multi-Lake "2 shadows, 1 object").
        readonly WaterVolume _owner;

        internal RenderTexture Texture => _target;

        // True when this body's last POOL caustic pass ran with the occluder material wired, i.e. the
        // RT's green channel is the valid refracted object-shadow term (cleared to 1 = lit, then this
        // body's submerged interactables drawn in). Published PER BODY by WaterUniformPublisher.
        // Deliberately NOT "drew at least one occluder this frame": with that meaning, a body with no
        // submerged objects fell back to the RAW UN-REFRACTED shadow map underwater, which projected
        // other pools' caster shadows across body boundaries (Multi-Lake) and multiplied deep floors'
        // caustics by an out-of-range shadow sample (Deep Lake, caustics gone). An empty green channel
        // reads 1 everywhere = "no object shadow", which is the correct answer for those bodies.
        internal bool OccluderChannelValid { get; private set; }

        internal WaterCausticsPass(WaterVolume owner, Shader causticsShader, Shader largeBodyCausticsShader,
                                   Shader occluderShader, int resolution, int causticGridResolution)
        {
            _owner = owner ?? throw new System.ArgumentNullException(nameof(owner));
            if (causticsShader == null) throw new System.ArgumentNullException(nameof(causticsShader));
            if (resolution <= 0)
                throw new System.ArgumentException($"Caustic resolution must be positive, got {resolution}.",
                                                   nameof(resolution));

            // Dedicated caustic lattice. 0 = draw the mesh the caller passes, exactly as before.
            // BuildGrid already uses IndexFormat.UInt32, so a 512 or 1024 lattice is legal - the cost
            // is vertices, not indices, which is why the detail knob is capped rather than free.
            if (causticGridResolution > 0)
            {
                _causticGrid = WaterMeshBuilder.BuildGrid(causticGridResolution);
                _causticGrid.hideFlags = HideFlags.HideAndDontSave;
                _causticGridRes = causticGridResolution;
            }

            // HideAndDontSave: an edit-mode preview must never serialize these into the scene.
            _material = new Material(causticsShader) { hideFlags = HideFlags.HideAndDontSave };
            // Optional: only the windowed ocean uses it, so a project without the shader assigned simply
            // gets no large-body caustics (the shafts still read as plain shadow shafts).
            if (largeBodyCausticsShader != null)
                _largeBodyMaterial = new Material(largeBodyCausticsShader) { hideFlags = HideFlags.HideAndDontSave };
            // Optional: submerged objects project their silhouette along the refracted light into the
            // caustic RT green channel, so their underwater shadow lines up with the caustics.
            if (occluderShader != null)
                _occluderMaterial = new Material(occluderShader) { hideFlags = HideFlags.HideAndDontSave };
            // Ocean-clipmap bodies get a mip chain: the god-ray march samples the caustic at a
            // depth-scaled LOD so deep beams read broad and slow (_LargeGodRayCausticDepthSoften).
            // Mips are generated EXPLICITLY after each caustic draw (see the Render methods) - never
            // auto - so a mip level can never hold stale/undefined data. Pools keep the flat RT
            // (their samplers were tuned against it); a body whose archetype changes at runtime
            // simply degrades to LOD 0 until its modules are rebuilt.
            bool withMips = owner.IsOceanClipmap;
            _target = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGB32)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "CausticTex",
                hideFlags = HideFlags.HideAndDontSave,
                useMipMap = withMips,
                autoGenerateMips = false
            };
            _target.Create();
            _cb = new CommandBuffer { name = "WebGpuWater.Caustics" };
        }

        // Project the body's own sim state into its caustic RT (vertex shader outputs
        // clip space directly, so the mesh draws with an identity matrix).
        internal void Render(Mesh waterMesh, RenderTexture simTexture, float waterRestY,
                             Vector3 volumeCenter, Vector3 volumeExtent, Quaternion volumeRotation,
                             Vector3 lightDir)
        {
            if (simTexture != null) _material.SetTexture(ID_Water, simTexture);
            // Fold the surface's wind-wave slope into the caustic. Set here on our own material because
            // this pass runs before the owner applies its per-body block, so the wave params aren't on
            // the material otherwise. Inert when Wind Waves is off (_WaveCount == 0 -> no change).
            _owner.ApplyCausticWaveUniforms(_material);
            // Caustic-only wind-wave weight, OVERRIDING the mirror-of-the-surface value the call
            // above just set: the generator's use of the wind-wave layer becomes art-directable
            // without touching the visible ripples (1 = mirror the surface exactly, byte-identical;
            // 0 = wind waves stop generating caustics). God rays follow for free - they sample the
            // caustic RT this pass writes. The ocean path has its own knob (LargeCausticRippleStrength).
            _material.SetFloat(ID_WaveNormalStrength,
                               _owner.waveNormalStrength * _owner.causticWindWaveStrength);
            _material.SetFloat(ID_CausticGridStep, CausticGridStepNorm());

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, CausticClear);
            _cb.DrawMesh(_causticGrid != null ? _causticGrid : waterMesh, Matrix4x4.identity, _material, 0, 0);
            DrawOccluders(waterRestY, volumeCenter, volumeExtent, volumeRotation, lightDir);
            if (_target.useMipMap) _cb.GenerateMips(_target); // keep every level valid (see ctor)
            Graphics.ExecuteCommandBuffer(_cb);
        }

        // Project THIS BODY's submerged interactables along the refracted light into the caustic RT
        // green channel (0 = occluded), using the same ProjectCausticUV mapping the floor samples with -
        // so the object shadow is registered with the caustics, not the un-refracted shadow map. The
        // volume frame is set on the material explicitly because the body publishes those globals only
        // after this pass runs. _CausticOccluderActive (see OccluderChannelValid) tells the pool/receiver
        // shaders to source the underwater object shadow from green; the shadow-map path remains only
        // for setups without the occluder shader wired.
        void DrawOccluders(float waterRestY, Vector3 volumeCenter, Vector3 volumeExtent,
                           Quaternion volumeRotation, Vector3 lightDir)
        {
            // No occluder shader wired, OR the body opts out of refracted shadows: skip the occluder so
            // _CausticOccluderActive publishes 0 and every shader (ours AND Standard Lit) falls back to
            // URP's straight shadow map - one consistent shadow across any material, at the cost of the
            // shadow/caustic registration on deep pools (the refractShadows gate).
            if (_occluderMaterial == null || !_owner.refractShadows) { OccluderChannelValid = false; return; }
            // Green was just cleared to 1 (lit) and only this body's silhouettes go in, so the
            // channel is valid even when nothing is submerged - "no object shadow" is the answer.
            OccluderChannelValid = true;

            _occluderMaterial.SetVector(ID_LightDir, lightDir);
            _occluderMaterial.SetVector(ID_VolumeCenter, volumeCenter);
            _occluderMaterial.SetVector(ID_VolumeExtent, volumeExtent);
            _occluderMaterial.SetMatrix(ID_VolumeRot, Matrix4x4.Rotate(volumeRotation));

            var list = WaterInteractable.Active;
            for (int i = 0; i < list.Count; i++)
            {
                WaterInteractable it = list[i];
                if (it == null || it.Renderer == null) continue;
                // Author-side filter (refractShadowLayers): an excluded layer casts no refracted
                // shadow at all. Tested first - it is a bitfield test, while every check below
                // fetches renderer bounds.
                if (!LayerInMask(it.Renderer.gameObject.layer, _owner.refractShadowLayers)) continue;
                // Same containment rule the interactable itself uses for drops/waterline, so an
                // object is stamped into exactly ONE body's RT - its own.
                if (WaterVolume.BodyContaining(it.Renderer.bounds.center) != _owner) continue;
                // AND inside the footprint: containment falls back to the PRIMARY body for points
                // outside every footprint, and IsSubmerged tests against the flat surface plane -
                // so a prop on ground beside (below) a raised pool passed both checks and stamped
                // a wildly-projected silhouette across the whole green channel, blacking out the
                // caustics and god rays that multiply by it (the Deep Lake play-mode blackout).
                if (!_owner.WorldToPoolXZ(it.Renderer.bounds.center, out _, out _)) continue;
                if (!it.IsSubmerged(it.WaterlineY(waterRestY))) continue;
                _cb.DrawRenderer(it.Renderer, _occluderMaterial, 0, 0);
            }
        }

        // Normalised spacing between adjacent vertices of the mesh the caustic pass draws. Both
        // meshes span [-1,1], so the step is 2/resolution. The pool draws EffectiveWaterMesh and the
        // ocean draws the sim-window patch grid, and both are BuildGrid(_simRes) today - but the
        // authored pool mesh is an exception whose resolution nothing can report, and the sim grid is
        // the caustic content's own band-limit either way, so SimResolution is both the correct answer
        // and the safe one. When the caustic mesh is decoupled from the sim, THIS is what re-points.
        float CausticGridStepNorm()
            => 2f / Mathf.Max(1, _causticGrid != null ? _causticGridRes : _owner.SimResolution);

        // A LayerMask is a bitfield indexed by layer number, so membership is a shift and a test.
        static bool LayerInMask(int layer, LayerMask mask) => (mask.value & (1 << layer)) != 0;

        // Ocean version: project the near-field WINDOW sim into the caustic RT via the large-body
        // (world-frame) caustic. The window centre/extent are set on the material explicitly so the
        // projection frame is correct even on the first frame, before the body publishes those globals.
        // No-op when the large-body shader isn't assigned, so oceans just fall back to plain shafts.
        internal void RenderLargeBody(Mesh windowMesh, RenderTexture simTexture,
                                      Vector3 windowCenter, Vector3 windowHalfExtent)
        {
            Mesh mesh = _causticGrid != null ? _causticGrid : windowMesh;
            if (_largeBodyMaterial == null || mesh == null) return;
            OccluderChannelValid = false; // the large-body path clears green to 0 and draws no silhouettes
            if (simTexture != null) _largeBodyMaterial.SetTexture(ID_Water, simTexture);
            _largeBodyMaterial.SetVector(ID_SimCenter, windowCenter);
            _largeBodyMaterial.SetVector(ID_SimExtent, windowHalfExtent);
            // God-ray caustic smoothing radius (Ocean God Rays block): set here like the window frame,
            // because this pass renders before the owner publishes its per-body block.
            _largeBodyMaterial.SetFloat(ID_CausticSmooth, _owner.LargeGodRayCausticSmooth);
            // Dedicated caustic ripple field (the KWS arrangement - see the shader): the caustic's
            // small-wave trigger is its OWN analytic ripple layer on its own clock, because the
            // surface's small content is FFT-texture driven (ignores analytic time scaling and
            // sweeps too fast to read). Scale/strength/speed are direct artist knobs.
            _largeBodyMaterial.SetFloat(ID_CausticTime, _owner.WaveTime * _owner.LargeCausticTimeScale);
            _largeBodyMaterial.SetFloat(ID_CausticRippleScale, _owner.LargeCausticRippleScale);
            _largeBodyMaterial.SetFloat(ID_CausticRippleStrength, _owner.LargeCausticRippleStrength);
            // The large-body material is drawn directly, before the owner's renderer property block.
            // Publish the two per-body FFT gates it needs explicitly; the cascade textures/layout are
            // global because only one ocean owns the FFT pass.
            _largeBodyMaterial.SetFloat(ID_OceanFftActive, _owner.OceanFftActive ? 1f : 0f);
            _largeBodyMaterial.SetFloat(ID_LargeWaveAmplitude, _owner.LargeWaveAmplitudeEffective);
            // Pool -> world slope conversion for the ripple tilt (see LargeBodyCaustics.shader). This
            // material takes a hand-written subset of the body uniforms, so it must be set explicitly.
            _largeBodyMaterial.SetVector(ID_PoolSlopeToWorld, _owner.PoolSlopeToWorld);
            _largeBodyMaterial.SetVector(ID_SimSlopeToWorld, _owner.SimSlopeToWorld);
            _largeBodyMaterial.SetFloat(ID_CausticGridStep, CausticGridStepNorm());

            _cb.Clear();
            _cb.SetRenderTarget(_target);
            _cb.ClearRenderTarget(true, true, Color.clear);
            _cb.DrawMesh(mesh, Matrix4x4.identity, _largeBodyMaterial, 0, 0);
            if (_target.useMipMap) _cb.GenerateMips(_target); // the god rays sample depth-scaled LODs
            Graphics.ExecuteCommandBuffer(_cb);
        }

        internal void Dispose()
        {
            _cb?.Release();
            // Release frees the GPU surface immediately; Destroy frees the wrapper objects,
            // which otherwise accumulate across enable/disable cycles until scene unload.
            if (_target != null)
            {
                _target.Release();
                WaterObjects.DestroyRuntime(_target);
            }
            WaterObjects.DestroyRuntime(_causticGrid);
            WaterObjects.DestroyRuntime(_material);
            WaterObjects.DestroyRuntime(_largeBodyMaterial);
            WaterObjects.DestroyRuntime(_occluderMaterial);
        }
    }
}
