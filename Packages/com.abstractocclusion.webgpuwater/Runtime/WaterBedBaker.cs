// WebGpuWater - terrain bed-height bake.
// Extracted from WaterVolume: samples the terrain heightmap into a pool-space map so
// shaders can read the real water-column depth (surface - bed). One-time CPU bake
// aligned to the body's volume frame; re-run via WaterVolume.RebakeBed() (context menu)
// if the terrain or placement changes. Lazy: the bake costs bedResolution^2 main-thread
// SampleHeight calls, so a scene with a Terrain must not pay it at startup for a
// feature that is off by default.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterBedBaker
    {
        // Baked bed-height map resolution bounds (pool-space). Internal consts so the
        // volume's inspector Range attribute and the bake share one source of truth.
        internal const int MinResolution = 64;
        internal const int MaxResolution = 1024;

        readonly WaterVolume _body;

        Texture2D _bedTex;   // pool-space terrain bed height (R), baked from the Terrain
        bool _baked;
        bool _bakeAttempted; // lazy gate: bake once per enable, only when useBedDepth is on

        internal Texture2D Texture => _bedTex;
        internal bool IsBaked => _baked;

        internal WaterBedBaker(WaterVolume body)
        {
            _body = body ?? throw new System.ArgumentNullException(nameof(body));
        }

        internal void EnsureBaked()
        {
            if (!_body.useBedDepth || _bakeAttempted) return;
            Rebake();
        }

        internal void Rebake()
        {
            _bakeAttempted = true;
            Terrain terrain = _body.bedTerrain != null ? _body.bedTerrain : Terrain.activeTerrain;
            if (terrain == null)
            {
                // Loud once, not silent: Use Bed Depth is ON but there is nothing to bake from, and
                // the _bakeAttempted gate would otherwise hide the no-op until a disable/enable.
                Debug.LogWarning($"WaterVolume '{_body.name}': Use Bed Depth is on but no Terrain is " +
                                 "assigned (and there is no active terrain) - bed-depth shading disabled.", _body);
                _baked = false;
                return;
            }

            if (terrain.terrainData == null)
            {
                // A Terrain component can deserialize before its TerrainData asset is available.
                // Sampling it throws every enable and can lock the editor while opening a scene.
                Debug.LogWarning($"WaterVolume '{_body.name}': Use Bed Depth is on but Terrain " +
                                 $"'{terrain.name}' has no valid TerrainData - bed-depth shading disabled.",
                                 _body);
                _baked = false;
                return;
            }

            int res = Mathf.Clamp(_body.bedResolution, MinResolution, MaxResolution);
            EnsureBedTexture(res);

            float terrainBaseY = terrain.transform.position.y;
            var pixels = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                float poolZ = ((z + 0.5f) / res) * 2f - 1f;
                for (int x = 0; x < res; x++)
                {
                    float poolX = ((x + 0.5f) / res) * 2f - 1f;
                    Vector3 world = _body.PoolToWorld(new Vector3(poolX, 0f, poolZ));
                    float bedWorldY = terrainBaseY + terrain.SampleHeight(world);
                    // Only the Y differs from the surface probe, so this yields the bed's pool-space
                    // height under the same volume frame (correct under rotation / non-uniform extent).
                    float bedPoolY = _body.WorldToPool(new Vector3(world.x, bedWorldY, world.z)).y;
                    pixels[z * res + x] = new Color(bedPoolY, 0f, 0f, 0f);
                }
            }
            _bedTex.SetPixels(pixels);
            _bedTex.Apply(false, false);
            _baked = true;
        }

        void EnsureBedTexture(int res)
        {
            if (_bedTex != null && _bedTex.width == res) return;
            if (_bedTex != null) DestroyBedTexture();
            // Bilinear is load-bearing and must not be "fixed" to Point: the SURFACE reads this
            // texture per pixel for depth-clarity (WaterSurfaceFragStages.hlsl:175), where point
            // sampling quantises transparency to the bed texel grid. The sim's point-sampling note
            // describes that one consumer's intent, not this resource's contract.
            _bedTex = new Texture2D(res, res, TextureFormat.RFloat, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = "BedHeightPool",
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        // Teardown; also re-arms the lazy gate for the next enable.
        internal void Dispose()
        {
            DestroyBedTexture();
            _baked = false;
            _bakeAttempted = false;
        }

        // Destroy the bed texture safely from either play mode or the editor context menu.
        void DestroyBedTexture()
        {
            if (_bedTex == null) return;
            WaterObjects.DestroyRuntime(_bedTex);
            _bedTex = null;
        }
    }
}
