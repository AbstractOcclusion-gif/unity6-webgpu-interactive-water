// WebGpuWater - WaterVolume: the PUBLIC gameplay / GPU-consumer facade.
// Split out of WaterVolume.cs (final-clean E, verbatim move - any behavior change here is a bug):
// ripple/sphere injection, height/surface/submersion queries and their static *At variants
// (BodyContaining resolution), the analytic waterline, and the GPU-consumer surface
// (sim state texture + frame uniforms) that WaterFoamParticles and similar effects read.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // ---- Injection boundary guard -------------------------------------------------------
        // A NaN slips PAST every window/footprint test below: comparisons with NaN are all
        // false, so "outside [-1,1] -> return" never rejects it, and ONE non-finite texel
        // ping-pongs into the whole heightfield - a physics blow-up upstream kills the water
        // with no message. Non-finite input is a caller bug: warn ONCE per session so the
        // console names it, then drop every such call (fail fast without 60-per-second spam).
        static bool s_warnedNonFiniteInjection;
        static void WarnNonFiniteInjection()
        {
            if (s_warnedNonFiniteInjection) return;
            s_warnedNonFiniteInjection = true;
            Debug.LogWarning("WaterVolume: rejected a ripple/wake injection carrying NaN or " +
                             "Infinity (position, step, radius or strength). Fix the caller; " +
                             "further occurrences are dropped silently this session.");
        }
        static bool AllFinite(float a, float b, float c, float d)
            => float.IsFinite(a) && float.IsFinite(b) && float.IsFinite(c) && float.IsFinite(d);
        static bool AllFinite(Vector3 v)
            => WaterSurfaceKinematics.IsFinite(v);

        /// <summary>Inject a ripple at a WORLD position (x,z). Converted into the pool
        /// footprint via the volume frame; out-of-footprint calls are ignored. Radius is
        /// in world units (kept round via the average horizontal extent).</summary>
        public void AddRipple(float worldX, float worldZ, float radius, float strength)
        {
            if (_water == null) return;
            if (!AllFinite(worldX, worldZ, radius, strength)) { WarnNonFiniteInjection(); return; }

            // Chop inversion, same reason as AddSphereInteraction below: the sim is sampled at
            // undisplaced lattice xz, so a drop stamped at the query xz appears chop-metres away.
            Vector2 dropAt = InvertLargeWaveChopXZ(worldX, worldZ);

            // Windowed bodies inject into the sim WINDOW frame; ripples outside it are dropped.
            if (_windowed)
            {
                Vector3 sim = WorldToSim(new Vector3(dropAt.x, SimWindowCenter.y, dropAt.y));
                if (sim.x < -1f || sim.x > 1f || sim.z < -1f || sim.z > 1f) return;
                _water.AddDrop(sim.x, sim.z, radius / SimHorizontalExtent, strength / VolumeExtentSafe.y);
                return;
            }

            Vector3 probe = new Vector3(dropAt.x, VolumeCenter.y, dropAt.y);
            if (!WorldToPoolXZ(probe, out float px, out float pz)) return;
            _water.AddDrop(px, pz, radius / VolumeHorizontalExtent, strength / VolumeExtentSafe.y);
        }

        /// <summary>Inject a moving sphere's wake into THIS body (Crest-style velocity dipole). Unlike
        /// <see cref="AddRipple"/>, which stamps an isotropic HEIGHT drop, this accelerates the water's
        /// velocity field with a directional dipole - pushed ahead of travel, pulled behind - so a
        /// travelling object lays a V-wake. <paramref name="worldStep"/> is the sphere's world-space
        /// displacement THIS physics step (position delta, not a rate), so the wake is frame-rate
        /// independent; <paramref name="radius"/> and <paramref name="strength"/> are world radius and a
        /// master gain. Out-of-footprint or fully-clear-of-water calls are ignored. Coordinate mapping
        /// mirrors <see cref="AddRipple"/> (affine, so velocity maps exactly under rotation).</summary>
        public void AddSphereInteraction(Vector3 worldPos, Vector3 worldStep, float radius, float strength)
            => AddSphereInteraction(worldPos, worldStep, radius, strength, 0f);

        /// <summary>Inject a moving sphere wake with an optional cap on its vertical plunge/heave
        /// contribution. A cap of 0 preserves the uncapped legacy behaviour.</summary>
        public void AddSphereInteraction(Vector3 worldPos, Vector3 worldStep, float radius, float strength,
                                         float verticalForceCap)
        {
            if (_water == null) return;
            if (!AllFinite(worldPos) || !AllFinite(worldStep) ||
                !float.IsFinite(radius) || !float.IsFinite(strength) || !float.IsFinite(verticalForceCap))
            { WarnNonFiniteInjection(); return; }

            // Submersion weight from the ANALYTIC waterline (rest + wind + swell, never the live ripples),
            // so the object's own wake can't feed back into how hard it pushes.
            float weight = SphereSubmersionWeight(worldPos, radius);
            if (weight <= 0f) return;

            float velY = worldStep.y / VolumeExtentSafe.y; // world vertical motion -> pool-height units
            Vector3 bodyStep = Quaternion.Inverse(VolumeRotation) * worldStep;
            float wakeFoamDose = WaterSimulation.CalculateWakeFoamDose(
                new Vector2(bodyStep.x, bodyStep.z), Time.deltaTime);

            // CHOP INVERSION (2026-08-03): stamp at the SOURCE point the horizontal wave
            // displacement carries onto the object, not at the object itself - the sim is sampled
            // at undisplaced lattice xz, so a raw stamp APPEARS at (object + chop) and the wake
            // slid around the hull with the swell phase in a heavy sea. Submersion weight above
            // deliberately used the TRUE position (it is about the object, not the lattice).
            Vector2 inject = InvertLargeWaveChopXZ(worldPos.x, worldPos.z);

            // Windowed bodies inject into the scrolling sim WINDOW frame; wakes outside it are dropped.
            if (_windowed)
            {
                Vector3 c = WorldToSim(new Vector3(inject.x, SimWindowCenter.y, inject.y));
                if (c.x < -1f || c.x > 1f || c.z < -1f || c.z > 1f) return;
                // The step maps from the SAME inverted origin: the dipole's direction/magnitude is
                // the object's own motion, not the chop's (which is near-equal at both endpoints).
                _water.AddSphereInteraction(new Vector2(c.x, c.z), radius / SimHorizontalExtent,
                                            HorizontalStepInHeightUnits(worldStep), velY, weight, strength,
                                            wakeFoamDose, verticalForceCap);
                return;
            }

            Vector3 pool = WorldToPool(new Vector3(inject.x, VolumeCenter.y, inject.y));
            if (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f) return;
            _water.AddSphereInteraction(new Vector2(pool.x, pool.z), radius / VolumeHorizontalExtent,
                                        HorizontalStepInHeightUnits(worldStep), velY, weight, strength,
                                        wakeFoamDose, verticalForceCap);
        }

        /// <summary>A sphere interactor's horizontal step in POOL-HEIGHT units, in the body frame.
        /// BOTH dipole channels land in the sim's VELOCITY channel and are integrated into height, so
        /// the horizontal term has to be normalised by the same vertical extent velY is: taking it from
        /// the pool/sim delta divided it by the HORIZONTAL extent instead, and a wake's world amplitude
        /// then scaled with the body's depth. Only the body rotation is applied - the sim's normalised
        /// space is already squashed world-round for the dipole (_SphereAxisScale), so re-applying the
        /// horizontal extents here would squash the direction twice.
        /// SCALE NOTE: this changes the effective gain by horizontal/vertical - 3.3x weaker on a 500 m
        /// body with a 30 m window - so authored interactor strengths need scaling by the same ratio.
        /// The wake foam stamp reads |velXZ| as its speed gate, so it moves with them.</summary>
        Vector2 HorizontalStepInHeightUnits(Vector3 worldStep)
        {
            Vector3 bodyStep = Quaternion.Inverse(VolumeRotation) * worldStep;
            return new Vector2(bodyStep.x, bodyStep.z) / VolumeExtentSafe.y;
        }

        // Submersion weight for the sphere interactor: 1 at the waterline, a Gaussian fade as the sphere
        // sinks (a deep sphere barely dents the surface), and a sqrt fade to 0 as it lifts a radius clear.
        // Mirrors Crest's SphereWaterInteraction weighting. Uses the analytic waterline (valid from frame 0).
        float SphereSubmersionWeight(Vector3 worldPos, float radius)
        {
            if (!TryGetAnalyticWaterline(worldPos.x, worldPos.z, out float surfaceY)) return 0f;
            float r = Mathf.Max(radius, 1e-3f);
            float below = surfaceY - worldPos.y; // > 0 submerged, < 0 above the surface
            if (below >= 0f)
            {
                float t = 0.5f * below / r;
                return Mathf.Exp(-t * t);
            }
            return Mathf.Sqrt(Mathf.Clamp01(1f + below / r));
        }

        /// <summary>Inject a moving sphere's wake at a world position on whichever body contains it
        /// (Crest-style velocity dipole). <paramref name="worldStep"/> is the displacement this physics
        /// step. Returns false if no water body contains the point.</summary>
        public static bool TrySphereInteractionAt(Vector3 worldPos, Vector3 worldStep, float radius, float strength)
            => TrySphereInteractionAt(worldPos, worldStep, radius, strength, 0f);

        /// <summary>Inject a moving sphere wake at a world position with an optional cap on the
        /// vertical plunge/heave contribution. Returns false if no water body contains it.</summary>
        public static bool TrySphereInteractionAt(Vector3 worldPos, Vector3 worldStep, float radius, float strength,
                                                  float verticalForceCap)
        {
            WaterVolume body = BodyContaining(worldPos);
            if (body == null) return false;
            body.AddSphereInteraction(worldPos, worldStep, radius, strength, verticalForceCap);
            return true;
        }

        /// <summary>World-space height (Y) of the water surface above WORLD (x,z).
        /// Returns false until the first readback lands or if outside the footprint.</summary>
        public bool TryGetWaterHeight(float worldX, float worldZ, out float height)
        {
            height = 0f;
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!QueryPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight, out _)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y; // pool -> world Y
            // Open water layers the big world-space swell on top of the wind waves (the pool wavebank is
            // suppressed for these bodies), same as TryGetSurface / TrySampleSubmersion - without this an
            // ocean's height query under-reports by the whole swell.
            if (openWater)
                height += SampleLargeWaveField(worldX, worldZ).x;
            return true;
        }

        /// <summary>World surface height (Y) plus the legacy horizontal wave-drift vector (world x,z)
        /// above WORLD (x,z). For surface effects that ride the waterline (splash drift). The output
        /// parameter keeps its original name for source compatibility; authored current is separate.
        /// Approximate under steep tilt; exact for rotation/rectangular/depth.</summary>
        public bool TryGetSurface(float worldX, float worldZ, out float height, out Vector2 flow)
        {
            height = 0f;
            flow = Vector2.zero;
            if (_sampler == null) return false; // not initialized yet
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!QueryPoolXZ(probe, out float px, out float pz)) return false;
            if (!_sampler.TrySamplePoolSurface(probe, px, pz, out float poolHeight,
                                               out Vector2 poolSurfaceTilt)) return false;

            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            Vector3 worldSurfaceTilt =
                WaterSurfaceKinematics.TiltToWorld(VolumeRotation, poolSurfaceTilt);
            if (openWater)
            {
                Vector3 wave = SampleLargeWaveField(worldX, worldZ);
                height += wave.x;
                worldSurfaceTilt += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
            Vector3 waveDriftVelocity =
                WaterSurfaceKinematics.WaveDriftVelocityFromTilt(worldSurfaceTilt);
            flow = new Vector2(waveDriftVelocity.x, waveDriftVelocity.z);
            return true;
        }

        /// <summary>Sample submersion for a buoyancy point at an arbitrary WORLD point.
        /// Works under rotation/tilt/non-uniform extent because it is evaluated in pool
        /// space. Returns the world-space depth below the surface (negative = above),
        /// the volume's up direction, and the legacy world-space wave-drift push. The output
        /// parameter keeps its original name for source compatibility; authored current is separate.</summary>
        public bool TrySampleSubmersion(Vector3 worldPoint, out float depthWorld, out Vector3 up, out Vector3 worldFlow)
        {
            depthWorld = 0f;
            up = VolumeUp;
            worldFlow = Vector3.zero;
            if (_sampler == null) return false; // not initialized yet

            Vector3 pool = WorldToPool(worldPoint);
            // An unbounded ocean spans everywhere; bounded bodies still reject out-of-footprint points.
            if (!IsOceanClipmap && (pool.x < -1f || pool.x > 1f || pool.z < -1f || pool.z > 1f)) return false;
            if (!_sampler.TrySamplePoolSurface(worldPoint, pool.x, pool.z, out float surfaceH,
                                               out Vector2 poolSurfaceTilt)) return false;

            depthWorld = (surfaceH - pool.y) * VolumeExtentSafe.y; // pool depth -> world depth along up
            Vector3 worldSurfaceTilt =
                WaterSurfaceKinematics.TiltToWorld(VolumeRotation, poolSurfaceTilt);
            // Open water: the world-space swell is the wind-wave source (the pool wavebank is
            // suppressed for these bodies). Raise the surface by the wave height so the point sits
            // deeper on a crest, and push along the wave slope so the swell carries the object.
            if (openWater)
            {
                Vector3 wave = SampleLargeWaveField(worldPoint.x, worldPoint.z);
                depthWorld += wave.x;
                worldSurfaceTilt += new Vector3(-wave.y, 0f, -wave.z) * waveNormalStrength;
            }
            worldFlow = WaterSurfaceKinematics.WaveDriftVelocityFromTilt(worldSurfaceTilt);
            return true;
        }

        // ---- gameplay façade -----------------------------------------------
        // World-position-first wrappers over the sim primitives, so gameplay code (swimming,
        // audio, VFX, projectiles) queries the water without touching x/z or internals. The
        // static *At variants resolve the body that contains the point via BodyContaining.

        /// <summary>World-space surface height (Y) at a world position's x,z on THIS body.
        /// False until the first readback lands or if the point is outside the footprint.</summary>
        public bool TrySampleHeight(Vector3 worldPos, out float worldY)
            => TryGetWaterHeight(worldPos.x, worldPos.z, out worldY);

        /// <summary>True if the world point is below THIS body's surface.</summary>
        public bool IsSubmerged(Vector3 worldPos)
            => TrySampleSubmersion(worldPos, out float depth, out _, out _) && depth > 0f;

        /// <summary>Inject a ripple at a world position on THIS body (footsteps, projectiles,
        /// boats). Radius/strength are world units; out-of-footprint calls are ignored.</summary>
        public void SpawnRipple(Vector3 worldPos, float radius, float strength)
            => AddRipple(worldPos.x, worldPos.z, radius, strength);

        /// <summary>Surface height (Y) at a world position, resolving the body that contains it.
        /// False if there is no water or the readback isn't ready / point is out of footprint.</summary>
        public static bool TrySampleHeightAt(Vector3 worldPos, out float worldY)
        {
            worldY = 0f;
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.TrySampleHeight(worldPos, out worldY);
        }

        /// <summary>True if the world point is below the surface of whichever body contains it.</summary>
        public static bool IsSubmergedAt(Vector3 worldPos)
        {
            WaterVolume body = BodyContaining(worldPos);
            return body != null && body.IsSubmerged(worldPos);
        }

        /// <summary>Spawn a ripple at a world position on whichever body contains it. Returns
        /// false if there is no water body to receive it.</summary>
        public static bool TrySpawnRippleAt(Vector3 worldPos, float radius, float strength)
        {
            WaterVolume body = BodyContaining(worldPos);
            if (body == null) return false;
            body.SpawnRipple(worldPos, radius, strength);
            return true;
        }

        /// <summary>Waterline for the obstacle footprint: the ANALYTIC surface only (rest
        /// plane + wind waves), deliberately EXCLUDING the interactive ripples. Including
        /// them fed an object's own displacement back into its footprint through the stale
        /// async readback - a delayed feedback loop that kept re-exciting micro-ripples
        /// around every floater. Wind waves stay in, so a wave-riding float keeps a constant
        /// submerged depth against its waterline and injects nothing; scattering off passing
        /// ripples becomes a small, damped, open-loop effect (like the mouse, which injects
        /// without ever being influenced by the water). No readback needed: valid from frame 0.</summary>
        public bool TryGetAnalyticWaterline(float worldX, float worldZ, out float height)
        {
            height = 0f;
            Vector3 probe = new Vector3(worldX, VolumeCenter.y, worldZ);
            if (!QueryPoolXZ(probe, out float px, out float pz)) return false;

            // Oceans sample the wind-wave layer in WORLD metres (extent-independent) to match the shader.
            float mpu = WaveMetersPerUnit;
            float waveX = IsOceanClipmap ? worldX / mpu : px;
            float waveZ = IsOceanClipmap ? worldZ / mpu : pz;
            float poolHeight = windWaves ? _waveBank.SampleHeight(waveX, waveZ, _waveTime, mpu) : 0f;
            height = PoolToWorld(new Vector3(px, poolHeight, pz)).y;
            // Open water layers the big world-space swell on top of the small wind waves, mirroring
            // the shader (CPU copy of WaterLargeWaves.hlsl) so floaters ride the rendered surface.
            if (openWater)
                height += SampleLargeWaveField(worldX, worldZ).x;
            return true;
        }

        // ---- GPU consumer API (foam particles and similar per-body effects) ----

        /// <summary>Sim state texture (height, velocity, normal.xz) for GPU consumers.</summary>
        public RenderTexture SimStateTexture => _water?.Texture;
        /// <summary>Local horizontal ripple-flow texture for isolated crest-fleck transport.</summary>
        public RenderTexture SimHorizontalFlowTexture => _water?.HorizontalFlowTexture;
        /// <summary>Current foam-amount texture (R channel) for GPU consumers.</summary>
        public RenderTexture FoamMaskTexture => _water?.FoamTexture;
        /// <summary>Grid resolution of the active sim (per side), fixed at startup.</summary>
        public int SimResolution => _simRes;
        /// <summary>True when this body runs its GPU sim this frame (visible, in range,
        /// within the sim budget, not paused). GPU consumers should idle when false.</summary>
        public bool IsSimulating => _simulate && !_paused;
        /// <summary>True when this body's renderers draw this frame (frustum cull).</summary>
        public bool IsVisibleToCamera => _visible;

        /// <summary>Push this body's placement-frame uniforms (volume + sim window) onto a
        /// compute shader so GPU consumers can include WaterVolume.hlsl and share the exact
        /// same pool/window/world transforms as the render side.</summary>
        public void WriteSimFrameUniforms(ComputeShader cs)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            Publisher.WriteSimFrameUniforms(cs);
        }

        /// <summary>Push this body's wind-wave bank onto a compute shader. Compute state is
        /// explicitly body-bound because global shader state is not reliable for compute.</summary>
        internal void WriteWaveUniforms(ComputeShader cs)
        {
            if (cs == null) throw new System.ArgumentNullException(nameof(cs));
            Publisher.ApplyWaveUniforms(cs);
        }

        /// <summary>World-space area covered by one sim texel (m^2), for density-normalised
        /// GPU spawning. Uses the window frame when windowed, else the whole volume.</summary>
        public float SimTexelWorldArea
        {
            get
            {
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                float texelX = 2f * half.x / _simRes;
                float texelZ = 2f * half.z / _simRes;
                return texelX * texelZ;
            }
        }

        /// <summary>Loose world bounds of the active sim frame (surface plane plus wave
        /// headroom), for culling GPU-driven draws that follow this body.</summary>
        public Bounds SimWorldBounds
        {
            get
            {
                Vector3 center = _windowed ? SimWindowCenter : VolumeCenter;
                Vector3 half = _windowed ? SimHalfExtent : VolumeExtentSafe;
                // Rotation-safe: expand horizontally by the diagonal, vertically by the
                // depth plus wave headroom.
                float horizontal = Mathf.Sqrt(half.x * half.x + half.z * half.z);
                float vertical = half.y * (1f + WaveHeightMargin);
                return new Bounds(center, 2f * new Vector3(horizontal, vertical, horizontal));
            }
        }

        /// <summary>True if this body runs the camera-following windowed sim (decided at
        /// startup from its size and the threshold).</summary>
        public bool IsWindowed => _windowed;
        /// <summary>World centre of the active sim window (follows the camera at runtime).
        /// The volume centre until the window exists.</summary>
        public Vector3 SimWindowCenter => _simWindow != null ? _simWindow.Center : VolumeCenter;
        /// <summary>World half-size (x,z) and depth scale (y) of the sim window.</summary>
        public Vector3 SimWindowHalfExtent => SimHalfExtent;
    }
}
