// WebGpuWater - world-frame terrain seabed-height + shoreline SDF field (Layer A shoreline substrate).
//
// Bakes the terrain seabed height into a WORLD-frame map, then derives a jump-flood signed-distance
// field (distance + direction to shore) from it, so shoreline features (shoaling, shore foam, the
// SWE zone) share one depth-and-shore signal that also exists on ocean/windowed bodies - unlike
// WaterBedBaker, which is pool-frame and bounded-only. The seabed is static geometry, so both the
// depth bake and the SDF are one-time CPU computations (the same proven Terrain.SampleHeight the bed
// baker uses), stored in half-float textures (WebGPU-filterable) and published as globals.
//
// WHY reuse the useBedDepth opt-in as the gate: the bake costs resolution^2 main-thread SampleHeight
// calls plus a jump flood, so - exactly like WaterBedBaker - a terrain scene must not pay it at
// startup for a feature that is off by default. A dedicated toggle can replace this gate later.
using UnityEngine;

namespace AbstractOcclusion.WebGpuWater
{
    internal sealed class WaterShoreDepthField
    {
        static readonly int ID_Tex = Shader.PropertyToID("_ShoreDepthTex");
        static readonly int ID_Center = Shader.PropertyToID("_ShoreDepthCenter");
        static readonly int ID_Size = Shader.PropertyToID("_ShoreDepthSize");
        static readonly int ID_Valid = Shader.PropertyToID("_ShoreDepthValid");
        static readonly int ID_Debug = Shader.PropertyToID("_ShoreDepthDebug");
        static readonly int ID_SdfTex = Shader.PropertyToID("_ShoreSDFTex");
        static readonly int ID_SdfValid = Shader.PropertyToID("_ShoreSDFValid");
        static readonly int ID_SdfDebug = Shader.PropertyToID("_ShoreSDFDebug");
        static readonly int ID_WaterLevel = Shader.PropertyToID("_ShoreWaterLevel");
        static readonly int ID_ShoalDepth = Shader.PropertyToID("_ShoreShoalDepth");

        // Debug visualizations are globals (one field is published at a time), toggled from the
        // WaterVolume context menu; static so the flags survive the per-body republish each frame.
        static bool _depthDebugEnabled;
        static bool _sdfDebugEnabled;

        readonly WaterVolume _body;

        Texture2D _depthTex;         // R = seabed WORLD height (metres), half-float
        Texture2D _sdfTex;           // RG = toward-shore dir (0..1), B = signed distance (m), A = mask
        Vector2 _center, _halfSize;  // world XZ centre / half-extent of the baked field
        float _waterLevel;           // still-water plane world Y at bake time (for shoaling depth)
        bool _depthBaked;
        bool _sdfBaked;
        bool _bakeAttempted;         // lazy gate: bake once per enable, only when useBedDepth is on

        internal WaterShoreDepthField(WaterVolume body)
            => _body = body ?? throw new System.ArgumentNullException(nameof(body));

        internal static void ToggleDepthDebug() => _depthDebugEnabled = !_depthDebugEnabled;
        internal static void ToggleSdfDebug() => _sdfDebugEnabled = !_sdfDebugEnabled;

        // Lazily bake (once, when opted in) then (re)publish the globals every frame so the samplers are
        // always bound - even unbaked they publish a black fallback + valid=0, because WebGPU never
        // tolerates an unbound sampler.
        internal void EnsureBakedAndPublish()
        {
            if (_body.useBedDepth && !_bakeAttempted) Rebake();
            Publish();
        }

        internal void Rebake()
        {
            _bakeAttempted = true;
            _depthBaked = false;
            _sdfBaked = false;

            Terrain terrain = _body.bedTerrain != null ? _body.bedTerrain : Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null) return;

            Vector3 origin = terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            _center = new Vector2(origin.x + size.x * 0.5f, origin.z + size.z * 0.5f);
            _halfSize = new Vector2(size.x * 0.5f, size.z * 0.5f);

            int res = Mathf.Clamp(_body.bedResolution, WaterBedBaker.MinResolution, WaterBedBaker.MaxResolution);
            EnsureTexture(ref _depthTex, res, TextureFormat.RHalf, "ShoreDepthWorld");

            var seabed = new float[res * res];
            var depthPixels = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                float worldZ = TexelToWorld(z, res, _center.y, _halfSize.y);
                for (int x = 0; x < res; x++)
                {
                    float worldX = TexelToWorld(x, res, _center.x, _halfSize.x);
                    float seabedY = origin.y + terrain.SampleHeight(new Vector3(worldX, 0f, worldZ));
                    seabed[z * res + x] = seabedY;
                    depthPixels[z * res + x] = new Color(seabedY, 0f, 0f, 0f);
                }
            }
            _depthTex.SetPixels(depthPixels);
            _depthTex.Apply(false, false);
            _depthBaked = true;

            // The still-water plane is the body's surface (transform Y); the waterline is where the
            // seabed crosses it. Stored + published so the shoaling shader reads depth = level - seabed.
            _waterLevel = _body.VolumeCenter.y;
            BuildSdf(seabed, res, _waterLevel);
        }

        // CPU jump-flood signed distance + direction to shore, derived from the baked seabed heights.
        void BuildSdf(float[] seabed, int res, float waterLevel)
        {
            int n = res * res;
            var worldX = new float[res];
            var worldZ = new float[res];
            for (int i = 0; i < res; i++) worldX[i] = TexelToWorld(i, res, _center.x, _halfSize.x);
            for (int i = 0; i < res; i++) worldZ[i] = TexelToWorld(i, res, _center.y, _halfSize.y);

            // Seed the waterline: a texel whose submerged state differs from a 4-neighbour is on the
            // shore boundary - a crisp 1-texel seed regardless of beach slope. -1 = not a seed.
            var src = new int[n];
            int seedCount = 0;
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    src[i] = -1;
                    bool submerged = seabed[i] < waterLevel;
                    bool boundary =
                        (x > 0 && (seabed[i - 1] < waterLevel) != submerged) ||
                        (x < res - 1 && (seabed[i + 1] < waterLevel) != submerged) ||
                        (z > 0 && (seabed[i - res] < waterLevel) != submerged) ||
                        (z < res - 1 && (seabed[i + res] < waterLevel) != submerged);
                    if (boundary) { src[i] = i; seedCount++; }
                }
            }

            // No waterline in the field (all water or all land): nothing to flood.
            if (seedCount == 0) return;

            var dst = new int[n];
            for (int step = res / 2; step >= 1; step >>= 1)
            {
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        int i = z * res + x;
                        int best = src[i];
                        float bestSq = SeedDistanceSq(best, x, z, res, worldX, worldZ);
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                if (ox == 0 && oz == 0) continue;
                                int nx = x + ox * step, nz = z + oz * step;
                                if (nx < 0 || nx >= res || nz < 0 || nz >= res) continue;
                                int candidate = src[nz * res + nx];
                                if (candidate < 0) continue;
                                float sq = SeedDistanceSq(candidate, x, z, res, worldX, worldZ);
                                if (sq < bestSq) { bestSq = sq; best = candidate; }
                            }
                        }
                        dst[i] = best;
                    }
                }
                (src, dst) = (dst, src);
            }

            // Pack: RG = toward-shore unit direction (0..1), B = signed distance (m, + water / - land), A = 1.
            var sdfPixels = new Color[n];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    int seed = src[i];
                    if (seed < 0) { sdfPixels[i] = new Color(0.5f, 0.5f, 0f, 0f); continue; }
                    float dx = worldX[seed % res] - worldX[x];
                    float dz = worldZ[seed / res] - worldZ[z];
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    float sign = seabed[i] < waterLevel ? 1f : -1f; // + offshore water, - dry land
                    float inv = dist > 1e-4f ? 1f / dist : 0f;
                    sdfPixels[i] = new Color(dx * inv * 0.5f + 0.5f, dz * inv * 0.5f + 0.5f, sign * dist, 1f);
                }
            }

            EnsureTexture(ref _sdfTex, res, TextureFormat.RGBAHalf, "ShoreSdfWorld");
            _sdfTex.SetPixels(sdfPixels);
            _sdfTex.Apply(false, false);
            _sdfBaked = true;
        }

        static float SeedDistanceSq(int seed, int x, int z, int res, float[] worldX, float[] worldZ)
        {
            if (seed < 0) return float.MaxValue;
            float dx = worldX[seed % res] - worldX[x];
            float dz = worldZ[seed / res] - worldZ[z];
            return dx * dx + dz * dz;
        }

        // Texel index -> world coordinate along one axis (texel centre, field spans centre +/- half).
        static float TexelToWorld(int index, int res, float center, float half)
            => center + (((index + 0.5f) / res) * 2f - 1f) * half;

        void Publish()
        {
            Shader.SetGlobalTexture(ID_Tex, _depthBaked ? (Texture)_depthTex : Texture2D.blackTexture);
            Shader.SetGlobalVector(ID_Center, new Vector4(_center.x, _center.y, 0f, 0f));
            Shader.SetGlobalVector(ID_Size, new Vector4(_halfSize.x, _halfSize.y, 0f, 0f));
            Shader.SetGlobalFloat(ID_Valid, _depthBaked ? 1f : 0f);
            Shader.SetGlobalFloat(ID_Debug, _depthDebugEnabled ? 1f : 0f);
            Shader.SetGlobalFloat(ID_WaterLevel, _waterLevel);
            Shader.SetGlobalFloat(ID_ShoalDepth, _body.shoreShoalDepth); // live-tunable; no rebake needed

            Shader.SetGlobalTexture(ID_SdfTex, _sdfBaked ? (Texture)_sdfTex : Texture2D.blackTexture);
            Shader.SetGlobalFloat(ID_SdfValid, _sdfBaked ? 1f : 0f);
            Shader.SetGlobalFloat(ID_SdfDebug, _sdfDebugEnabled ? 1f : 0f);
        }

        void EnsureTexture(ref Texture2D tex, int res, TextureFormat format, string texName)
        {
            if (tex != null && tex.width == res && tex.format == format) return;
            if (tex != null) DestroyTexture(ref tex);
            // Half-float: heights/distances need sub-metre precision but not float32 - and float32 is
            // not hardware-filterable on WebGPU, whereas half is. Linear (not sRGB) data.
            tex = new Texture2D(res, res, format, false, true)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = texName,
                hideFlags = HideFlags.HideAndDontSave
            };
        }

        internal void Dispose()
        {
            DestroyTexture(ref _depthTex);
            DestroyTexture(ref _sdfTex);
            _depthBaked = false;
            _sdfBaked = false;
            _bakeAttempted = false;   // re-arm the lazy bake gate for the next enable
        }

        static void DestroyTexture(ref Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Object.Destroy(tex); else Object.DestroyImmediate(tex);
            tex = null;
        }
    }
}
