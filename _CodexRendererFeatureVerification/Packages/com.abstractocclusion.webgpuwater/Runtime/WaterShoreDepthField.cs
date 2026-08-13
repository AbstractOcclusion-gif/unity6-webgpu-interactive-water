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
        static readonly int ID_ShoalDepth = WaterShaderProps.ShoreShoalDepth;
        static readonly int ID_GreenBandDepth = WaterShaderProps.ShoreGreenBandDepth;
        // P1 shoal-transform + P2 surf-front knobs (all live-tunable; no rebake needed).
        static readonly int ID_Refraction = Shader.PropertyToID("_ShoreRefraction");
        static readonly int ID_Compression = Shader.PropertyToID("_ShoreCompression");
        static readonly int ID_Greens = Shader.PropertyToID("_ShoreGreens");
        static readonly int ID_WarpReach = Shader.PropertyToID("_ShoreWarpReach");
        static readonly int ID_SurfBeatTime = Shader.PropertyToID("_SurfBeatTime");
        static readonly int ID_SurfActive = WaterShaderProps.SurfActive;
        static readonly int ID_SurfAmplitude = WaterShaderProps.SurfAmplitude;
        static readonly int ID_SurfWavelength = WaterShaderProps.SurfWavelength;
        static readonly int ID_SurfPeriod = WaterShaderProps.SurfPeriod;
        static readonly int ID_SurfBandDepth = WaterShaderProps.SurfBandDepth;
        static readonly int ID_SurfSetStrength = WaterShaderProps.SurfSetStrength;
        static readonly int ID_SurfLean = WaterShaderProps.SurfLean;
        static readonly int ID_SurfCompression = WaterShaderProps.SurfCompression;
        static readonly int ID_SurfGreens = WaterShaderProps.SurfGreens;
        static readonly int ID_SurfAmbientFade = WaterShaderProps.SurfAmbientFade;
        static readonly int ID_SurfSwashAmplitude = WaterShaderProps.SurfSwashAmplitude;
        static readonly int ID_SurfSwashMaxSlopeTan = WaterShaderProps.SurfSwashMaxSlopeTan;
        static readonly int ID_SurfWaterlineFoam = WaterShaderProps.SurfWaterlineFoam;
        static readonly int ID_SurfSmallWaveFoam = Shader.PropertyToID("_SurfSmallWaveFoam");
        static readonly int ID_SurfCrestLength = WaterShaderProps.SurfCrestLength;
        static readonly int ID_SurfCrestVariation = WaterShaderProps.SurfCrestVariation;
        static readonly int ID_SurfCrestPersistence = WaterShaderProps.SurfCrestPersistence;
        static readonly int ID_SurfDirectionality = WaterShaderProps.SurfDirectionality;
        static readonly int ID_SurfWindDirXZ = WaterShaderProps.SurfWindDirXZ;
        static readonly int ID_SurfFoamStrength = Shader.PropertyToID("_SurfFoamStrength");
        static readonly int ID_SurfFoamFeather = Shader.PropertyToID("_SurfFoamFeather");
        static readonly int ID_SurfFoamTileSize = Shader.PropertyToID("_SurfFoamTileSize");
        static readonly int ID_SurfFoamColor = Shader.PropertyToID("_SurfFoamColor");
        // FOAM-1/2/3 (render-only foam enhancement set - see WaterSurfWaves.hlsl / WaterSurface.shader)
        static readonly int ID_SurfCrestFoamLut = Shader.PropertyToID("_SurfCrestFoamLut");
        static readonly int ID_SurfCrestFoamLutActive = Shader.PropertyToID("_SurfCrestFoamLutActive");
        static readonly int ID_SurfCrestFoamGain = Shader.PropertyToID("_SurfCrestFoamGain");
        static readonly int ID_SurfFoamCrestCap = Shader.PropertyToID("_SurfFoamCrestCap");
        static readonly int ID_SurfFoamRepartActive = WaterShaderProps.SurfFoamRepartActive;
        static readonly int ID_SurfFoamBoreGain = WaterShaderProps.SurfFoamBoreGain;
        static readonly int ID_SurfFoamTrailGain = WaterShaderProps.SurfFoamTrailGain;
        static readonly int ID_SurfFoamTrailLength = WaterShaderProps.SurfFoamTrailLength;
        static readonly int ID_SurfFoamTrailDissolve = Shader.PropertyToID("_SurfFoamTrailDissolve");
        static readonly int ID_SurfSwashFoam = Shader.PropertyToID("_SurfSwashFoam");
        static readonly int ID_SurfSwashFoamWidth = Shader.PropertyToID("_SurfSwashFoamWidth");
        static readonly int ID_SurfSwashFoamDissolve = Shader.PropertyToID("_SurfSwashFoamDissolve");
        static readonly int ID_ShoreSwashDepositGain = WaterShaderProps.ShoreSwashDepositGain;

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
        int _bakeVersion;

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
        internal int FieldResolution => _res;
        internal int BakeVersion => _bakeVersion;
        internal float FieldWaterLevel => _waterLevel;

        // Lazily bake once when opted in. Publishing happens through WaterUniformPublisher's per-body
        // material-property-block/global-fallback sinks, so two shore-enabled bodies cannot race over
        // a single graphics global field.
        internal void EnsureBaked()
        {
            if (_body.useBedDepth && !_bakeAttempted) Rebake();
        }

        internal void Rebake()
        {
            _bakeAttempted = true;
            _depthBaked = false;
            _sdfBaked = false;

            Terrain terrain = _body.bedTerrain != null ? _body.bedTerrain : Terrain.activeTerrain;
            if (terrain == null || terrain.terrainData == null)
            {
                // Loud once, not silent: Use Bed Depth is ON but there is no terrain to bake the
                // shore field from, and _bakeAttempted would otherwise hide the no-op (no shoal,
                // no surf, no swash) until a disable/enable.
                Debug.LogWarning($"WaterVolume '{_body.name}': Use Bed Depth is on but no Terrain " +
                                 "(with TerrainData) is available - shore depth field disabled.", _body);
                return;
            }

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
            _bakeVersion++;
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
            // One channel at a time: the kernel never reads the other component, so two independent
            // passes are identical to the interleaved loop this replaced (and the shared helper is now
            // the only place the kernel is written).
            dirX = BoxBlur3x3(dirX, new float[n], res, DirectionSmoothPasses);
            dirZ = BoxBlur3x3(dirZ, new float[n], res, DirectionSmoothPasses);
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

            return BoxBlur3x3(slope, new float[n], res, DirectionSmoothPasses);
        }

        // Kernel extent for the bake-time smoothing below. ONE knob: the tap count is derived, so the
        // kernel cannot be widened in the loop bounds and left un-normalised in the divide.
        const int BoxBlurRadius = 1;                                                    // 3x3
        const float BoxBlurTapCount = (2 * BoxBlurRadius + 1) * (2 * BoxBlurRadius + 1); // 9

        // Clamp-edge box blur, run `passes` times, ping-ponging between src and scratch.
        // RETURNS whichever array ended up holding the result - after an odd number of passes that is
        // the array handed in as `scratch` - so callers must ASSIGN the return value, never keep using
        // the array they passed as `src`. Bake-time only (shore field rebuild), so the extra pass over
        // a second channel costs nothing that matters.
        static float[] BoxBlur3x3(float[] src, float[] scratch, int res, int passes)
        {
            for (int pass = 0; pass < passes; pass++)
            {
                for (int z = 0; z < res; z++)
                {
                    for (int x = 0; x < res; x++)
                    {
                        float sum = 0f;
                        for (int oz = -BoxBlurRadius; oz <= BoxBlurRadius; oz++)
                        {
                            int zz = Mathf.Clamp(z + oz, 0, res - 1);
                            for (int ox = -BoxBlurRadius; ox <= BoxBlurRadius; ox++)
                                sum += src[zz * res + Mathf.Clamp(x + ox, 0, res - 1)];
                        }
                        scratch[z * res + x] = sum / BoxBlurTapCount;
                    }
                }
                (src, scratch) = (scratch, src);
            }
            return src;
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

        internal bool TrySampleDepth(float worldX, float worldZ, out float depth)
        {
            depth = float.MaxValue;
            if (!_depthBaked || _cpuDepth == null) return false;
            float u = (worldX - _center.x) / (2f * _halfSize.x) + 0.5f;
            float v = (worldZ - _center.y) / (2f * _halfSize.y) + 0.5f;
            if (u < 0f || u > 1f || v < 0f || v > 1f) return false;
            depth = BilinearCpu(_cpuDepth, u, v);
            return true;
        }

        // Shared filter (WaterFieldSampling). The old local form clamped the UV first (Clamp01)
        // and then the floor/fraction separately; that is output-identical to the shared
        // clamp-the-texel-coordinate form for every input - both collapse out-of-range
        // coordinates to the edge texel - so it was unified rather than kept as a variant.
        float BilinearCpu(float[] field, float u, float v)
            => WaterFieldSampling.SampleBilinear(field, _res, u, v);

        /// <summary>Write this body's shore field through the same sink as its other surface uniforms.</summary>
        /// <remarks>Every texture receives a black fallback when unavailable: WebGPU rejects unbound samplers.</remarks>
        internal void WriteUniforms(WaterUniformPublisher.IUniformSink sink)
        {
            if (sink == null) throw new System.ArgumentNullException(nameof(sink));
            // Runtime toggle-off must actually TURN THE GPU SIDE OFF (the CPU mirror already gates
            // on useBedDepth): a stale bake keeps its textures but writes invalid, so the shaders
            // and the buoyancy mirror always agree about whether the shore is live.
            bool depthLive = _depthBaked && _body.useBedDepth;
            bool sdfLive = _sdfBaked && _body.useBedDepth;
            sink.SetTexture(ID_Tex, depthLive ? (Texture)_depthTex : Texture2D.blackTexture);
            sink.SetVector(ID_Center, new Vector4(_center.x, _center.y, 0f, 0f));
            sink.SetVector(ID_Size, new Vector4(_halfSize.x, _halfSize.y, 0f, 0f));
            sink.SetFloat(ID_Valid, depthLive ? 1f : 0f);
            sink.SetFloat(ID_Debug, _depthDebugEnabled ? 1f : 0f);
            sink.SetFloat(ID_WaterLevel, _waterLevel);
            // Two bands, one authored: the ATTENUATION band follows the sea state (so a big sea starts
            // flattening further out), while Green's-law amplification stays on the authored coastal
            // profile. Both live-tunable; no rebake needed.
            sink.SetFloat(ID_ShoalDepth, _body.ShoreShoalDepthEffective);
            sink.SetFloat(ID_GreenBandDepth, _body.shoreShoalDepth);

            sink.SetTexture(ID_SdfTex, sdfLive ? (Texture)_sdfTex : Texture2D.blackTexture);
            sink.SetFloat(ID_SdfValid, sdfLive ? 1f : 0f);
            sink.SetFloat(ID_SdfDebug, _sdfDebugEnabled ? 1f : 0f);

            // P1 shoal-transform knobs (inert when the field is unbaked - the shaders gate on the
            // valid flags above - but published every frame so they stay live-tunable).
            sink.SetFloat(ID_Refraction, _body.shoreRefraction);
            sink.SetFloat(ID_Compression, _body.shoreCompression);
            sink.SetFloat(ID_Greens, _body.shoreGreens);
            // ONE compression curve: the ambient swell's warp reach is the same front-spacing
            // multiple the surf fronts use (SurfWarpDistance), so both wave families bunch in
            // lockstep - via the validator-guarded shared constants, not a hand copy.
            sink.SetFloat(ID_WarpReach,
                LargeWaveField.SurfWarpReachSpacings
                * Mathf.Max(_body.SurfWavelengthEffective, LargeWaveField.SurfMinWavelength));

            // P2 surf breaker fronts: active only with BOTH fields baked (they steer by the SDF)
            // and the body opted in. The same values feed the ripple-sim foam injection through
            // WaterSimulation.BindShoreFoam - one source, two consumers.
            sink.SetFloat(ID_SurfActive, SurfLayerActive ? 1f : 0f);
            // THE MASTER SURF BEAT (see WaterVolume.SurfBeatTime): every surf consumer evaluates
            // the front field on this wrapped clock, never raw _WaveTime.
            sink.SetFloat(ID_SurfBeatTime, _body.SurfBeatTime);
            sink.SetFloat(ID_SurfAmplitude, _body.SurfAmplitudeEffective);
            sink.SetFloat(ID_SurfWavelength, _body.SurfWavelengthEffective);
            sink.SetFloat(ID_SurfPeriod, _body.surfPeriod);
            sink.SetFloat(ID_SurfBandDepth, _body.surfBandDepth);
            sink.SetFloat(ID_SurfSetStrength, _body.surfSetStrength);
            sink.SetFloat(ID_SurfLean, _body.surfLean);
            sink.SetFloat(ID_SurfCompression, _body.shoreCompression);
            sink.SetFloat(ID_SurfGreens, _body.shoreGreens);
            sink.SetFloat(ID_SurfAmbientFade, _body.surfAmbientFade);
            sink.SetFloat(ID_SurfSwashAmplitude, _body.surfSwashAmplitude);
            // MUST be published on BOTH paths: EvaluateSurfSwash also runs in WaterSim.compute for
            // the persistent swash deposit, and a cap the render honoured but the sim did not would
            // strand foam lines up a cliff the water no longer washes. See WaterSimulation.cs.
            sink.SetFloat(ID_SurfSwashMaxSlopeTan, _body.surfSwashMaxSlopeTan);
            sink.SetFloat(ID_SurfWaterlineFoam, _body.surfWaterlineFoam);
            // FOAM-7: small-wave crest+tail foam (surface render; 0 = byte-identical).
            sink.SetFloat(ID_SurfSmallWaveFoam, _body.surfSmallWaveFoam);
            sink.SetFloat(ID_SurfCrestLength, _body.surfCrestLength);
            sink.SetFloat(ID_SurfCrestVariation, _body.surfCrestVariation);
            sink.SetFloat(ID_SurfCrestPersistence, _body.surfCrestPersistence);
            sink.SetFloat(ID_SurfDirectionality, _body.surfDirectionality);
            sink.SetVector(ID_SurfWindDirXZ,
                new Vector4(Mathf.Cos(_body.LargeWaveHeadingRad), Mathf.Sin(_body.LargeWaveHeadingRad), 0f, 0f));
            sink.SetFloat(ID_SurfFoamStrength, _body.surfFoamStrength);
            sink.SetFloat(ID_SurfFoamFeather, _body.surfFoamFeather);
            sink.SetFloat(ID_SurfFoamTileSize, _body.surfFoamTileSize);
            sink.SetColor(ID_SurfFoamColor, _body.surfFoamColor);
            // FOAM-1: crest-foam pop curve LUT. Texture ALWAYS bound (black fallback) so no
            // backend ever sees an unbound sampler; the active flag gates all reads.
            bool crestLutActive = _body.SurfCrestFoamLutActive;
            Texture2D crestLut = crestLutActive ? _body.SurfCrestFoamLutTexture : null;
            sink.SetTexture(ID_SurfCrestFoamLut,
                                    crestLut != null ? crestLut : (Texture)Texture2D.blackTexture);
            sink.SetFloat(ID_SurfCrestFoamLutActive,
                                  crestLutActive && crestLut != null ? 1f : 0f);
            sink.SetFloat(ID_SurfCrestFoamGain, _body.surfCrestFoamGain);
            // FOAM-4: crest-cap gain (surface-only; 0 = byte-identical). Live-tunable.
            sink.SetFloat(ID_SurfFoamCrestCap, _body.surfFoamCrestCap);
            // FOAM-2: whitewash repartition (the gate lerps the weights in from the legacy
            // constants, so bodies publishing here get the knobs, everything else stays legacy).
            sink.SetFloat(ID_SurfFoamRepartActive, 1f);
            sink.SetFloat(ID_SurfFoamBoreGain, _body.surfFoamBoreGain);
            sink.SetFloat(ID_SurfFoamTrailGain, _body.surfFoamTrailGain);
            sink.SetFloat(ID_SurfFoamTrailLength, _body.surfFoamTrailLength);
            sink.SetFloat(ID_SurfFoamTrailDissolve, _body.surfFoamTrailDissolve);
            // FOAM-3: swash foam knobs (surface-only consumers).
            sink.SetFloat(ID_SurfSwashFoam, _body.surfSwashFoam);
            sink.SetFloat(ID_SurfSwashFoamWidth, _body.surfSwashFoamWidth);
            sink.SetFloat(ID_SurfSwashFoamDissolve, _body.surfSwashFoamDissolve);
            // FOAM-5: the SAME gain the sim uses to inject persistent deposits, published to the
            // SURFACE too so the vertex lift + fragment clip keep the beach alive under them.
            sink.SetFloat(ID_ShoreSwashDepositGain, _body.surfSwashDepositGain);
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
            WaterObjects.DestroyRuntime(tex);
            tex = null;
        }
    }
}
