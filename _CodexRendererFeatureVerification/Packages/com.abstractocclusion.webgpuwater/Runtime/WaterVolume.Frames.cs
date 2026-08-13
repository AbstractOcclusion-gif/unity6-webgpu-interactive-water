// WebGpuWater - WaterVolume partial: the coordinate frames and the transforms between them.
//
// A body juggles three frames - world, pool space (x,z in [-1,1], surface at y=0) and, on large
// bodies, the camera-following sim window - and almost every bug in this area is a conversion
// done in the wrong one. All the conversions therefore sit together, next to the anisotropy
// correction that keeps ripples round on a rectangular footprint and the ray/surface picking
// that both the input router and the query facade go through.

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace AbstractOcclusion.WebGpuWater
{
    public partial class WaterVolume
    {
        // ---- volume placement frame (center + rotation + non-uniform extent) ----
        internal Vector3 VolumeExtentSafe => new Vector3(
            Mathf.Max(volumeExtent.x, MinVolumeExtent),
            Mathf.Max(volumeExtent.y, MinVolumeExtent),
            Mathf.Max(volumeExtent.z, MinVolumeExtent));
        // Position + rotation come from this GameObject's transform (move it to place water).
        internal Vector3 VolumeCenter => transform.position;
        internal Quaternion VolumeRotation => transform.rotation;
        internal Vector3 VolumeUp => VolumeRotation * Vector3.up;
        // Average horizontal extent, used to keep a click ripple round in world units.
        float VolumeHorizontalExtent => 0.5f * (VolumeExtentSafe.x + VolumeExtentSafe.z);

        // Tell the sim how to keep ripples ROUND in world on a rectangular (non-square) pool. The
        // heightfield runs on a square grid over pool space, so on a body with extent.x != extent.z
        // both the drop stamp and the wavefront would stretch to that ratio. We weight the wave
        // Laplacian per axis by ~1/extent^2 (equal WORLD propagation speed; normalised by the
        // smaller extent so the max weight stays at the stable 0.25) and squash the drop stamp by
        // extent/avg (matching the average-extent radius normalisation used by AddRipple). Windowed
        // bodies sim over a SQUARE world window already, so they use the identity values.
        void ApplySimAnisotropy()
        {
            if (_water == null) return;
            if (_windowed)
            {
                Vector3 windowExtent = SimHalfExtent;
                _water.SetAnisotropy(new Vector2(0.25f, 0.25f), Vector2.one);
                _water.SetHorizontalFlowGeometry(new Vector2(windowExtent.x, windowExtent.z), windowExtent.y);
                return;
            }

            float ex = VolumeExtentSafe.x;
            float ez = VolumeExtentSafe.z;
            float minExtent = Mathf.Min(ex, ez);
            float minSq = minExtent * minExtent;
            float avg = VolumeHorizontalExtent;
            var waveWeight = new Vector2(0.25f * minSq / (ex * ex), 0.25f * minSq / (ez * ez));
            var dropScale = new Vector2(ex / avg, ez / avg);
            _water.SetAnisotropy(waveWeight, dropScale);
            _water.SetHorizontalFlowGeometry(new Vector2(ex, ez), VolumeExtentSafe.y);
        }

#if UNITY_EDITOR
        // One-time editor notice: large bodies (big lakes / oceans) are experimental in this
        // proof-of-concept. The interactive ripple sim is a POOL solver on a fixed grid, so past
        // ~20 m of extent the ripples go coarse and the analytic wind waves aren't ocean-scale.
        // Editor-only so a shipped build never logs it. See the README "Scope" notes.
        const float LargeBodyWarnExtent = 20f; // world half-extent (metres) where the pool solver frays
        bool _largeBodyWarned;

        void WarnIfLargeBody()
        {
            if (_largeBodyWarned) return;
            Vector3 e = VolumeExtentSafe;
            float maxExtent = Mathf.Max(e.x, e.z);
            if (maxExtent <= LargeBodyWarnExtent) return;

            _largeBodyWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' is a large water body (extent ~{maxExtent:0} m). Large bodies " +
                "(big lakes / oceans) are experimental in this version: the interactive ripple sim is a " +
                "pool solver, so its ripples get coarse and the wind waves aren't ocean-scale. This asset " +
                "targets small-to-mid bodies - see the README \"Scope\" notes.", this);
        }

        // One-time editor notice: Unity Terrain integration (the bed-depth bake) is experimental in
        // this proof-of-concept - it approximates a shoreline depth gradient, not full terrain support.
        bool _terrainWarned;

        void WarnIfExperimentalTerrain()
        {
            if (_terrainWarned || !useBedDepth) return;
            _terrainWarned = true;
            Debug.LogWarning(
                $"[WebGpuWater] '{name}' uses terrain bed-depth (Use Bed Depth). Unity Terrain integration " +
                "is experimental in this version - the baked shoreline depth is a basic approximation, not " +
                "full terrain support. See the README \"Scope\" notes.", this);
        }
#endif

        internal Vector3 PoolToWorld(Vector3 pool) => VolumeCenter + VolumeRotation * Vector3.Scale(pool, VolumeExtentSafe);

        internal Vector3 WorldToPool(Vector3 world)
        {
            Vector3 e = VolumeExtentSafe;
            Vector3 local = Quaternion.Inverse(VolumeRotation) * (world - VolumeCenter);
            return new Vector3(local.x / e.x, local.y / e.y, local.z / e.z);
        }

        // CPU mirror of LbwEdgeWeight() in WaterLargeWaves.hlsl: the bounded-body edge guard that
        // feathers the whole open-water wave field to rest toward the footprint border. Every CPU
        // consumer of the wave field (buoyancy sample, fog gate, query velocity) multiplies by this
        // at the same composition points the shader does, so floaters and gates keep matching the
        // flattened border the surface actually renders.
        internal float LargeWaveEdgeWeight(float worldX, float worldZ)
        {
            float feather = LargeWaveEdgeFeatherEffective;
            if (feather <= 0f) return 1f;
            Vector3 pool = WorldToPool(new Vector3(worldX, VolumeCenter.y, worldZ));
            Vector3 extent = VolumeExtentSafe;
            float borderMeters = Mathf.Min((1f - Mathf.Abs(pool.x)) * extent.x,
                                           (1f - Mathf.Abs(pool.z)) * extent.z);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(borderMeters / feather));
        }

        // Underwater-fog gate + per-body planar mirror -> WaterVolume.Underwater.cs.

        // ---- large-water sim window frame ----------------------------------
        // Half-size (world) of the window: simWindowMeters horizontally, the body's depth
        // scale vertically (ripple height stays coupled to extent.y like the whole-body sim).
        internal Vector3 SimHalfExtent => new Vector3(
            Mathf.Max(simWindowMeters, MinWindowHalfExtent),
            VolumeExtentSafe.y,
            Mathf.Max(simWindowMeters, MinWindowHalfExtent));

        // Average horizontal window half-size, keeping an injected ripple round in world units.
        float SimHorizontalExtent => Mathf.Max(simWindowMeters, MinWindowHalfExtent);

        // POOL-space slope -> WORLD slope, per axis. Pool space normalises each axis by its own
        // extent, so a slope measured there is the world slope times horizontal/vertical - and on a
        // wide shallow body that factor is enormous (200 m wide, 5 m deep = 40x). Any consumer that
        // treats a pool slope as a real one, or builds a NORMAL from it, has to convert first:
        // sqrt(1 - dot(n,n)) cannot hold a slope of 2, and the flattening that follows is not
        // something a later divide-by-extent can undo. The sim's own gradient carries an extra
        // factor 2 on top (it is measured per texture unit, and [0,1] spans what pool space spans
        // with [-1,1]) - that half belongs to the sim's consumers, not here.
        // Windowed bodies span the sim WINDOW horizontally but still scale height by the body extent.
        internal Vector4 PoolSlopeToWorld
        {
            get
            {
                Vector3 e = VolumeExtentSafe;
                return new Vector4(e.y / e.x, e.y / e.z, 0f, 0f);
            }
        }

        // The SIM's slope -> WORLD, which is NOT the same conversion. A windowed body's heightfield
        // spans the sim WINDOW, not the volume footprint, so its gradient is normalised by the window
        // while the wind-wave layer is still sampled in POOL space and normalised by the extent.
        // Feeding both through one factor made the wind waves on a 500 m body with a 30 m window
        // 16.7x too steep - "very sharp and not realistic". They coincide on every non-windowed body,
        // which is why pools looked right.
        internal Vector4 SimSlopeToWorld
        {
            get
            {
                Vector3 horizontal = _windowed ? SimHalfExtent : VolumeExtentSafe;
                float vertical = VolumeExtentSafe.y;
                return new Vector4(vertical / horizontal.x, vertical / horizontal.z, 0f, 0f);
            }
        }

        // GPU consumer API (sim state texture, frame uniforms, window accessors) -> WaterVolume.Facade.cs.

        // World -> sim-window normalised coords (.xz in [-1,1] inside the window).
        internal Vector3 WorldToSim(Vector3 world) => _simWindow.WorldToSim(world);

        // Windowing turns on for bodies whose horizontal half-extent exceeds the threshold.
        bool ShouldWindow()
        {
            if (!enableLargeBodyWindow) return false;
            // An unbounded ocean is infinite by definition, so the footprint-size threshold does not
            // apply - it always needs the camera-following window for its near-field ripples.
            if (openWater && unboundedOcean) return true;
            Vector3 e = VolumeExtentSafe;
            return Mathf.Max(e.x, e.z) > largeBodyThreshold;
        }

        // World point -> pool. Returns false if outside the [-1,1] horizontal footprint.
        // Internal: WaterCausticsPass gates occluder draws on it (only in-footprint objects
        // may stamp the caustic green channel).
        internal bool WorldToPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f;
        }

        // World point -> pool for the surface QUERIES (height/submersion/flow). Same as WorldToPoolXZ, except
        // an unbounded ocean has no footprint edge - its surface spans everywhere (clipmap to the horizon) -
        // so points beyond the bounded extent are accepted. Without this a floater (or the boat's propulsion,
        // which gates on IsSubmerged) cuts out at the extent edge. BodyContaining still uses the strict
        // footprint so per-body membership stays bounded.
        bool QueryPoolXZ(Vector3 world, out float poolX, out float poolZ)
        {
            Vector3 p = WorldToPool(world);
            poolX = p.x; poolZ = p.z;
            return IsOceanClipmap || (poolX >= -1f && poolX <= 1f && poolZ >= -1f && poolZ <= 1f);
        }

        // Intersect a camera ray with the (possibly tilted) surface plane through the
        // volume centre. Returns the world hit and its pool x,z (which may fall outside
        // [-1,1]); false only if the ray is parallel to or points away from the plane.
        bool TryPickSurface(Vector3 eye, Vector3 dir, out Vector3 worldHit, out float poolX, out float poolZ)
        {
            worldHit = Vector3.zero; poolX = 0f; poolZ = 0f;
            Vector3 n = VolumeUp;
            float denom = Vector3.Dot(dir, n);
            if (Mathf.Abs(denom) < RayParallelEpsilon) return false;
            float t = Vector3.Dot(VolumeCenter - eye, n) / denom;
            if (t < 0f) return false;
            worldHit = eye + dir * t;
            Vector3 pool = WorldToPool(worldHit);
            poolX = pool.x; poolZ = pool.z;
            return true;
        }

        // ---- interaction (WaterInputRouter drives this) -----------------------

        /// <summary>Does this body's surface plane lie under the ray, within its footprint?
        /// Returns the world hit point. Lets the input router pick which lake was clicked.</summary>
        public bool TryRaycastSurface(Ray ray, out Vector3 worldHit)
        {
            worldHit = Vector3.zero;
            if (!TryPickSurface(ray.origin, ray.direction, out Vector3 hit, out float px, out float pz)) return false;
            if (Mathf.Abs(px) > 1f || Mathf.Abs(pz) > 1f) return false;
            worldHit = hit;
            return true;
        }
    }
}
