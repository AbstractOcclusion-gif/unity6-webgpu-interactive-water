// WebGpuWater - world-frame terrain seabed-depth + shoreline SDF field (Layer A shoreline substrate).
//
// Bakes the terrain into a WORLD-frame map, then derives a jump-flood signed-distance field
// (distance + direction to shore) from it, so shoreline features (shoaling, surf fronts, shore
// foam, swash) share one depth-and-shore signal that also exists on ocean/windowed bodies - unlike
// WaterBedBaker, which is pool-frame and bounded-only. The seabed is static geometry, so both the
// depth bake and the SDF are one-time CPU computations (the same proven Terrain.SampleHeight the bed
// baker uses), stored in half-float textures (WebGPU-filterable) and published as globals.
//
// P0 precision fix (audit B4): the depth texture now stores the STILL-WATER COLUMN DEPTH
// (waterLevel - seabedY, metres) instead of the seabed's absolute world height. Half-float spends
// its precision on the small values near the waterline - exactly where every consumer needs it -
// instead of on a large absolute Y, which banded the shallows into visible terraces.
//
// P0 direction fix (audit B11): the raw jump-flood direction is piecewise-constant per nearest-seed
// cell and flips hard on the medial axis; a couple of box-blur passes over the (unnormalized)
// direction vectors makes it smooth enough to steer refraction and the surf fronts.
//
// The CPU-side arrays are KEPT after the bake (a few MB at default resolution) so the buoyancy
// mirror (LargeWaveField) can sample the same field the shaders see - no GPU readback anywhere.
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
        // P1 shoal-transform + P2 surf-front knobs (all live-tunable; no rebake needed).
        static readonly int ID_Refraction = Shader.PropertyToID("_ShoreRefraction");
        static readonly int ID_Compression = Shader.PropertyToID("_ShoreCompression");
        static readonly int ID_Greens = Shader.PropertyToID("_ShoreGreens");
        static readonly int ID_WarpReach = Shader.PropertyToID("_ShoreWarpReach");
        static readonly int ID_SurfBeatTime = Shader.PropertyToID("_SurfBeatTime");
        static readonly int ID_SurfActive = Shader.PropertyToID("_SurfActive");
        static readonly int ID_SurfAmplitude = Shader.PropertyToID("_SurfAmplitude");
        static readonly int ID_SurfWavelength = Shader.PropertyToID("_SurfWavelength");
        static readonly int ID_SurfPeriod = Shader.PropertyToID("_SurfPeriod");
        static readonly int ID_SurfBandDepth = Shader.PropertyToID("_SurfBandDepth");
        static readonly int ID_SurfSetStrength = Shader.PropertyToID("_SurfSetStrength");
        static readonly int ID_SurfLean = Shader.PropertyToID("_SurfLean");
        static readonly int ID_SurfCompression = Shader.PropertyToID("_SurfCompression");
        static readonly int ID_SurfGreens = Shader.PropertyToID("_SurfGreens");
        static readonly int ID_SurfAmbientFade = Shader.PropertyToID("_SurfAmbientFade");
        static readonly int ID_SurfSwashAmplitude = Shader.PropertyToID("_SurfSwashAmplitude");
        static readonly int ID_SurfWaterlineFoam = Shader.PropertyToID("_SurfWaterlineFoam");
        static readonly int ID_SurfCrestLength = Shader.PropertyToID("_SurfCrestLength");
        static readonly int ID_SurfCrestVariation = Shader.PropertyToID("_SurfCrestVariation");
        static readonly int ID_SurfCrestPersistence = Shader.PropertyToID("_SurfCrestPersistence");
        static readonly int ID_SurfDirectionality = Shader.PropertyToID("_SurfDirectionality");
        static readonly int ID_SurfWindDirXZ = Shader.PropertyToID("_SurfWindDirXZ");
        static readonly int ID_SurfFoamStrength = Shader.PropertyToID("_SurfFoamStrength");
        static readonly int ID_SurfFoamFeather = Shader.PropertyToID("_SurfFoamFeather");
        static readonly int ID_SurfFoamTileSize = Shader.PropertyToID("_SurfFoamTileSize");
        static readonly int ID_SurfFoamColor = Shader.PropertyToID("_SurfFoamColor");
        // FOAM-1/2/3 (render-only foam enhancement set - see WaterSurfWaves.hlsl / WaterSurface.shader)
        static readonly int ID_SurfCrestFoamLut = Shader.PropertyToID("_SurfCrestFoamLut");
        static readonly int ID_SurfCrestFoamLutActive = Shader.PropertyToID("_SurfCrestFoamLutActive");
        static readonly int ID_SurfCrestFoamGain = Shader.PropertyToID("_SurfCrestFoamGain");
        static readonly int ID_SurfFoamRepartActive = Shader.PropertyToID("_SurfFoamRepartActive");
        static readonly int ID_SurfFoamBoreGain = Shader.PropertyToID("_SurfFoamBoreGain");
        static readonly int ID_SurfFoamTrailGain = Shader.PropertyToID("_SurfFoamTrailGain");
        static readonly int ID_SurfFoamTrailLength = Shader.PropertyToID("_SurfFoamTrailLength");
        static readonly int ID_SurfFoamTrailDissolve = Shader.PropertyToID("_SurfFoamTrailDissolve");
        static readonly int ID_SurfSwashFoam = Shader.PropertyToID("_SurfSwashFoam");
        static readonly int ID_SurfSwashFoamWidth = Shader.PropertyToID("_SurfSwashFoamWidth");
        static readonly int ID_SurfSwashFoamDissolve = Shader.PropertyToID("_SurfSwashFoamDissolve");
        static readonly int ID_SurfSwashStreak = Shader.PropertyToID("_SurfSwashStreak");

        // How many box-blur passes smooth the SDF direction field (see the header note).
        const int DirectionSmoothPasses = 2;

        // Debug visualizations are globals (one field is published at a time), toggled from the
        // WaterVolume context menu; static so the flags survive the per-body republish each frame.
        static bool _depthDebugEnabled;
        static bool _sdfDebugEnabled;

        readonly WaterVolume _body;

        Texture2D _depthTex;         // R = still-water column depth (m, + water / - land), half-float
        Texture2D _sdfTex;           // RG = toward-shore dir (0..1), B = signed distance (m), A = beach slope tan(beta)
        Vector2 _center, _halfSize;  // world XZ centre / half-extent of the baked field
        float _waterLevel;           // still-water plane world Y at bake time (for shoaling depth)
        int _res;                    // baked resolution (texels per side)
        bool _depthBaked;
        bool _sdfBaked;
        bool _bakeAttempted;         // lazy gate: bake once per enable, only when useBedDepth is on

        // CPU copies kept for the buoyancy mirror (LargeWaveField samples the SAME field as the
        // shaders, bilinearly, with no readback). Null until baked.
        float[] _cpuDepth;           // column depth per texel
        float[] _cpuSdfDist;         // signed distance per texel
        float[] _cpuSdfDirX;         // toward-shore direction per texel (unit, world xz)
        float[] _cpuSdfDirZ;
        float[] _cpuSlope;           // local beach slope tan(beta) per texel (SURF-PHYS)

        internal WaterShoreDepthField(WaterVolume body)
            => _body = body ?? throw new System.ArgumentNullException(nameof(body));

        internal static void ToggleDepthDebug() => _depthDebugEnabled = !_depthDebugEnabled;
        internal static void ToggleSdfDebug() => _sdfDebugEnabled = !_sdfDebugEnabled;

        // Read-only surface for downstream consumers that must bind the fields explicitly onto a
        // compute (the SWE zone, the ripple-sim foam injection) rather than rely on the published
        // graphics globals.
        internal bool DepthBaked => _depthBaked;
        internal bool SdfBaked => _sdfBaked;
        internal Texture DepthTexture => _depthTex;
        internal Texture SdfTexture => _sdfTex;
        internal Vector2 FieldCenter => _center;
        internal Vector2 FieldHalfSize => _halfSize;
        internal float FieldWaterLevel => _waterLevel;

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
            // The still-water plane is the body's surface (transform Y); the waterline is where the
            // seabed crosses it. Baked into the stored depth, and published for absolute consumers.
            _waterLevel = _body.VolumeCenter.y;

            int res = Mathf.Clamp(_body.bedResolution, WaterBedBaker.MinResolution, WaterBedBaker.MaxResolution);
            _res = res;
            EnsureTexture(ref _depthTex, res, TextureFormat.RHalf, "ShoreDepthWorld");

            var depth = new float[res * res];
            var depthPixels = new Color[res * res];
            for (int z = 0; z < res; z++)
            {
                float worldZ = TexelToWorld(z, res, _center.y, _halfSize.y);
                for (int x = 0; x < res; x++)
                {
                    float worldX = TexelToWorld(x, res, _center.x, _halfSize.x);
                    float seabedY = origin.y + terrain.SampleHeight(new Vector3(worldX, 0f, worldZ));
                    float columnDepth = _waterLevel - seabedY; // + in water, - on dry land
                    depth[z * res + x] = columnDepth;
                    depthPixels[z * res + x] = new Color(columnDepth, 0f, 0f, 0f);
                }
            }
            _depthTex.SetPixels(depthPixels);
            _depthTex.Apply(false, false);
            _depthBaked = true;
            _cpuDepth = depth;

            BuildSdf(depth, res);
        }

        // CPU jump-flood signed distance + direction to shore, derived from the baked column depths.
        void BuildSdf(float[] depth, int res)
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
                    bool submerged = depth[i] > 0f;
                    bool boundary =
                        (x > 0 && (depth[i - 1] > 0f) != submerged) ||
                        (x < res - 1 && (depth[i + 1] > 0f) != submerged) ||
                        (z > 0 && (depth[i - res] > 0f) != submerged) ||
                        (z < res - 1 && (depth[i + res] > 0f) != submerged);
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

            // Raw per-texel results: signed distance + toward-shore vector (unnormalized for the blur).
            var dist = new float[n];
            var dirX = new float[n];
            var dirZ = new float[n];
            for (int z = 0; z < res; z++)
            {
                for (int x = 0; x < res; x++)
                {
                    int i = z * res + x;
                    int seed = src[i];
                    if (seed < 0) { dist[i] = 0f; dirX[i] = 0f; dirZ[i] = 0f; continue; }
                    float dx = worldX[seed % res] - worldX[x];
                    float dz = worldZ[seed / res] - worldZ[z];
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    float sign = depth[i] > 0f ? 1f : -1f; // + offshore water, - dry land
                    dist[i] = sign * d;
                    float inv = d > 1e-4f ? 1f / d : 0f;
                    dirX[i] = dx * inv;
                    dirZ[i] = dz * inv;
                }
            }

            // Direction smoothing (audit B11): box-blur the direction VECTORS (not the angles) a
            // couple of passes, then renormalize per texel. Cheap at bake time; kills the medial-axis
            // flips and the per-Voronoi-cell facets that would otherwise steer the surf fronts.
            var blurX = new float[n];
            var blurZ = new float[n];
            for (int pass = 0; pass < DirectionSmoothPasses; pass++)
            {
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float sumX = 0f, sumZ = 0f;
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            int zz = Mathf.Clamp(z + oz, 0, res - 1);
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int xx = Mathf.Clamp(x + ox, 0, res - 1);
                                int j = zz * res + xx;
                                sumX += dirX[j];
                                sumZ += dirZ[j];
                            }
                        }
                        int i = z * res + x;
                        blurX[i] = sumX / 9f;
                        blurZ[i] = sumZ / 9f;
                    }
                }
                (dirX, blurX) = (blurX, dirX);
                (dirZ, blurZ) = (blurZ, dirZ);
            }
            for (int i = 0; i < n; i++)
            {
                float len = Mathf.Sqrt(dirX[i] * dirX[i] + dirZ[i] * dirZ[i]);
                if (len > 1e-4f) { dirX[i] /= len; dirZ[i] /= len; }
                else { dirX[i] = 0f; dirZ[i] = 0f; }
            }

            float[] slope = BuildSlope(depth, res);

            // Pack: RG = toward-shore unit direction (0..1), B = signed distance (m), A = local
            // beach slope tan(beta) (SURF-PHYS; validity stays implicit in _ShoreSDFValid - no
            // reader ever used A as a mask).
            var sdfPixels = new Color[n];
            for (int i = 0; i < n; i++)
                sdfPixels[i] = new Color(dirX[i] * 0.5f + 0.5f, dirZ[i] * 0.5f + 0.5f, dist[i], slope[i]);

            EnsureTexture(ref _sdfTex, res, TextureFormat.RGBAHalf, "ShoreSdfWorld");
            _sdfTex.SetPixels(sdfPixels);
            _sdfTex.Apply(false, false);
            _sdfBaked = true;
            _cpuSdfDist = dist;
            _cpuSdfDirX = dirX;
            _cpuSdfDirZ = dirZ;
            _cpuSlope = slope;
        }

        // SURF-PHYS 1a: local beach slope tan(beta) = |grad(depth)| per texel, central differences
        // over the world texel size (grad(depth) = -grad(seabed), so the magnitude IS the beach
        // slope), then the same 3x3 box-smooth (and pass count) the direction field gets - raw
        // terrain gradients are noisy and the breaker physics wants the beach's TREND, not every
        // heightmap step. The slope that matters is the one under the surf zone; consumers sample
        // it at the same uv as depth, which is exactly this field.
        float[] BuildSlope(float[] depth, int res)
        {
            int n = res * res;
            float texelSizeX = (2f * _halfSize.x) / res;
            float texelSizeZ = (2f * _halfSize.y) / res;
            var slope = new float[n];
            for (int z = 0; z < res; z++)
            {
                int zm = Mathf.Max(z - 1, 0);
                int zp = Mathf.Min(z + 1, res - 1);
                for (int x = 0; x < res; x++)
                {
                    int xm = Mathf.Max(x - 1, 0);
                    int xp = Mathf.Min(x + 1, res - 1);
                    float dDepthDx = (depth[z * res + xp] - depth[z * res + xm])
                                   / ((xp - xm) * texelSizeX);
                    float dDepthDz = (depth[zp * res + x] - depth[zm * res + x])
                                   / ((zp - zm) * texelSizeZ);
                    slope[z * res + x] = Mathf.Sqrt(dDepthDx * dDepthDx + dDepthDz * dDepthDz);
                }
            }

            var blur = new float[n];
            for (int pass = 0; pass < DirectionSmoothPasses; pass++)
            {
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float sum = 0f;
                        for (int oz = -1; oz <= 1; oz++)
                        {
                            int zz = Mathf.Clamp(z + oz, 0, res - 1);
                            for (int ox = -1; ox <= 1; ox++)
                            {
                                int xx = Mathf.Clamp(x + ox, 0, res - 1);
                                sum += slope[zz * res + xx];
                            }
                        }
                        blur[z * res + x] = sum / 9f;
                    }
                }
                (slope, blur) = (blur, slope);
            }
            return slope;
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

        // --- CPU sampling for the buoyancy mirror (matches the shader's bilinear reads + border
        // feather, so LargeWaveField sees the same field the vertex shader does) -------------------

        // Matches SHORE_BORDER_FEATHER in WaterShore.hlsl.
        const float BorderFeather = 0.08f;

        /// <summary>Sample the shore field at a world xz for the CPU wave mirror. Returns false
        /// (deep-water behaviour) when unbaked or outside the feathered field. <paramref name="slopeTan"/>
        /// is the local beach slope tan(beta) (0 when the SDF is unbaked).</summary>
        internal bool TrySampleShore(float worldX, float worldZ, out float depth, out float sdfDist,
                                     out float dirX, out float dirZ, out float slopeTan,
                                     out float influence)
        {
            depth = float.MaxValue;
            sdfDist = 0f;
            dirX = 0f;
            dirZ = 0f;
            slopeTan = 0f;
            influence = 0f;
            if (!_depthBaked || _cpuDepth == null) return false;

            float u = (worldX - _center.x) / (2f * _halfSize.x) + 0.5f;
            float v = (worldZ - _center.y) / (2f * _halfSize.y) + 0.5f;
            float edgeU = Mathf.Min(u, 1f - u);
            float edgeV = Mathf.Min(v, 1f - v);
            influence = Mathf.Clamp01(edgeU / BorderFeather) * Mathf.Clamp01(edgeV / BorderFeather);
            if (influence <= 0f) { influence = 0f; return false; }

            depth = BilinearCpu(_cpuDepth, u, v);
            if (_sdfBaked && _cpuSdfDist != null)
            {
                sdfDist = BilinearCpu(_cpuSdfDist, u, v);
                dirX = BilinearCpu(_cpuSdfDirX, u, v);
                dirZ = BilinearCpu(_cpuSdfDirZ, u, v);
                slopeTan = BilinearCpu(_cpuSlope, u, v);
                float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                if (len > 1e-4f) { dirX /= len; dirZ /= len; }
                else { dirX = 0f; dirZ = 0f; }
            }
            return true;
        }

        float BilinearCpu(float[] field, float u, float v)
        {
            int res = _res;
            float fx = Mathf.Clamp01(u) * res - 0.5f;
            float fz = Mathf.Clamp01(v) * res - 0.5f;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(fx), 0, res - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt(fz), 0, res - 1);
            int x1 = Mathf.Min(x0 + 1, res - 1);
            int z1 = Mathf.Min(z0 + 1, res - 1);
            float tx = Mathf.Clamp01(fx - x0);
            float tz = Mathf.Clamp01(fz - z0);
            float a = Mathf.Lerp(field[z0 * res + x0], field[z0 * res + x1], tx);
            float b = Mathf.Lerp(field[z1 * res + x0], field[z1 * res + x1], tx);
            return Mathf.Lerp(a, b, tz);
        }

        void Publish()
        {
            // Runtime toggle-off must actually TURN THE GPU SIDE OFF (the CPU mirror already gates
            // on useBedDepth): a stale bake keeps its textures but publishes invalid, so the
            // shaders and the buoyancy mirror always agree about whether the shore is live.
            bool depthLive = _depthBaked && _body.useBedDepth;
            bool sdfLive = _sdfBaked && _body.useBedDepth;
            Shader.SetGlobalTexture(ID_Tex, depthLive ? (Texture)_depthTex : Texture2D.blackTexture);
            Shader.SetGlobalVector(ID_Center, new Vector4(_center.x, _center.y, 0f, 0f));
            Shader.SetGlobalVector(ID_Size, new Vector4(_halfSize.x, _halfSize.y, 0f, 0f));
            Shader.SetGlobalFloat(ID_Valid, depthLive ? 1f : 0f);
            Shader.SetGlobalFloat(ID_Debug, _depthDebugEnabled ? 1f : 0f);
            Shader.SetGlobalFloat(ID_WaterLevel, _waterLevel);
            Shader.SetGlobalFloat(ID_ShoalDepth, _body.shoreShoalDepth); // live-tunable; no rebake needed

            Shader.SetGlobalTexture(ID_SdfTex, sdfLive ? (Texture)_sdfTex : Texture2D.blackTexture);
            Shader.SetGlobalFloat(ID_SdfValid, sdfLive ? 1f : 0f);
            Shader.SetGlobalFloat(ID_SdfDebug, _sdfDebugEnabled ? 1f : 0f);

            // P1 shoal-transform knobs (inert when the field is unbaked - the shaders gate on the
            // valid flags above - but published every frame so they stay live-tunable).
            Shader.SetGlobalFloat(ID_Refraction, _body.shoreRefraction);
            Shader.SetGlobalFloat(ID_Compression, _body.shoreCompression);
            Shader.SetGlobalFloat(ID_Greens, _body.shoreGreens);
            // ONE compression curve: the ambient swell's warp reach is the same 2 x front spacing
            // the surf fronts use (SurfWarpDistance), so both wave families bunch in lockstep.
            Shader.SetGlobalFloat(ID_WarpReach, 2f * Mathf.Max(_body.SurfWavelengthEffective, 1f));

            // P2 surf breaker fronts: active only with BOTH fields baked (they steer by the SDF)
            // and the body opted in. The same values feed the ripple-sim foam injection through
            // WaterSimulation.BindShoreFoam - one source, two consumers.
            Shader.SetGlobalFloat(ID_SurfActive, SurfLayerActive ? 1f : 0f);
            // THE MASTER SURF BEAT (see WaterVolume.SurfBeatTime): every surf consumer evaluates
            // the front field on this wrapped clock, never raw _WaveTime.
            Shader.SetGlobalFloat(ID_SurfBeatTime, _body.SurfBeatTime);
            Shader.SetGlobalFloat(ID_SurfAmplitude, _body.SurfAmplitudeEffective);
            Shader.SetGlobalFloat(ID_SurfWavelength, _body.SurfWavelengthEffective);
            Shader.SetGlobalFloat(ID_SurfPeriod, _body.surfPeriod);
            Shader.SetGlobalFloat(ID_SurfBandDepth, _body.surfBandDepth);
            Shader.SetGlobalFloat(ID_SurfSetStrength, _body.surfSetStrength);
            Shader.SetGlobalFloat(ID_SurfLean, _body.surfLean);
            Shader.SetGlobalFloat(ID_SurfCompression, _body.shoreCompression);
            Shader.SetGlobalFloat(ID_SurfGreens, _body.shoreGreens);
            Shader.SetGlobalFloat(ID_SurfAmbientFade, _body.surfAmbientFade);
            Shader.SetGlobalFloat(ID_SurfSwashAmplitude, _body.surfSwashAmplitude);
            Shader.SetGlobalFloat(ID_SurfWaterlineFoam, _body.surfWaterlineFoam);
            Shader.SetGlobalFloat(ID_SurfCrestLength, _body.surfCrestLength);
            Shader.SetGlobalFloat(ID_SurfCrestVariation, _body.surfCrestVariation);
            Shader.SetGlobalFloat(ID_SurfCrestPersistence, _body.surfCrestPersistence);
            Shader.SetGlobalFloat(ID_SurfDirectionality, _body.surfDirectionality);
            Shader.SetGlobalVector(ID_SurfWindDirXZ,
                new Vector4(Mathf.Cos(_body.LargeWaveHeadingRad), Mathf.Sin(_body.LargeWaveHeadingRad), 0f, 0f));
            Shader.SetGlobalFloat(ID_SurfFoamStrength, _body.surfFoamStrength);
            Shader.SetGlobalFloat(ID_SurfFoamFeather, _body.surfFoamFeather);
            Shader.SetGlobalFloat(ID_SurfFoamTileSize, _body.surfFoamTileSize);
            Shader.SetGlobalColor(ID_SurfFoamColor, _body.surfFoamColor);
            // FOAM-1: crest-foam pop curve LUT. Texture ALWAYS bound (black fallback) so no
            // backend ever sees an unbound sampler; the active flag gates all reads.
            bool crestLutActive = _body.SurfCrestFoamLutActive;
            Texture2D crestLut = crestLutActive ? _body.SurfCrestFoamLutTexture : null;
            Shader.SetGlobalTexture(ID_SurfCrestFoamLut,
                                    crestLut != null ? crestLut : (Texture)Texture2D.blackTexture);
            Shader.SetGlobalFloat(ID_SurfCrestFoamLutActive,
                                  crestLutActive && crestLut != null ? 1f : 0f);
            Shader.SetGlobalFloat(ID_SurfCrestFoamGain, _body.surfCrestFoamGain);
            // FOAM-2: whitewash repartition (the gate lerps the weights in from the legacy
            // constants, so bodies publishing here get the knobs, everything else stays legacy).
            Shader.SetGlobalFloat(ID_SurfFoamRepartActive, 1f);
            Shader.SetGlobalFloat(ID_SurfFoamBoreGain, _body.surfFoamBoreGain);
            Shader.SetGlobalFloat(ID_SurfFoamTrailGain, _body.surfFoamTrailGain);
            Shader.SetGlobalFloat(ID_SurfFoamTrailLength, _body.surfFoamTrailLength);
            Shader.SetGlobalFloat(ID_SurfFoamTrailDissolve, _body.surfFoamTrailDissolve);
            // FOAM-3: swash foam knobs (surface-only consumers).
            Shader.SetGlobalFloat(ID_SurfSwashFoam, _body.surfSwashFoam);
            Shader.SetGlobalFloat(ID_SurfSwashFoamWidth, _body.surfSwashFoamWidth);
            Shader.SetGlobalFloat(ID_SurfSwashFoamDissolve, _body.surfSwashFoamDissolve);
            Shader.SetGlobalFloat(ID_SurfSwashStreak, _body.surfSwashStreak);
        }

        /// <summary>True when the surf breaker-front layer runs on this body: bed depth on, surf
        /// opted in, and both substrate fields baked (the fronts steer by the SDF). One definition,
        /// consumed by the publisher, the foam injection and the CPU mirror alike.</summary>
        internal bool SurfLayerActive
            => _body.useBedDepth && _body.surfEnabled && _depthBaked && _sdfBaked;

        void EnsureTexture(ref Texture2D tex, int res, TextureFormat format, string texName)
        {
            if (tex != null && tex.width == res && tex.format == format) return;
            if (tex != null) DestroyTexture(ref tex);
            // Half-float: depths/distances need sub-metre precision but not float32 - and float32 is
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
            _cpuDepth = null;
            _cpuSdfDist = null;
            _cpuSdfDirX = null;
            _cpuSdfDirZ = null;
            _cpuSlope = null;
        }

        static void DestroyTexture(ref Texture2D tex)
        {
            if (tex == null) return;
            if (Application.isPlaying) Object.Destroy(tex); else Object.DestroyImmediate(tex);
            tex = null;
        }
    }
}
