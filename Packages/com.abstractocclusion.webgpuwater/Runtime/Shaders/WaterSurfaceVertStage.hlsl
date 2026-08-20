// WebGpuWater - WaterSurface vertex stage (SHADER-SPLIT-4, verbatim move - any behaviour
// change here is a bug). The pass-local uniforms, the windowed ripple sampler, the v2f
// contract and vert() itself, shared by BOTH passes of WaterSurface.shader: the visible
// surface pass AND the ocean-surface eye-depth prepass (the KWS-style rendered waterline
// the underwater fog samples). Include AFTER the WaterSurface*.hlsl chain (vert reads its
// helpers); WaterSurfaceFragStages.hlsl reads several of these uniforms, so in the visible
// pass this must sit above it (it already does - same spot the moved code occupied).
#ifndef WATER_SURFACE_VERT_STAGE_INCLUDED
#define WATER_SURFACE_VERT_STAGE_INCLUDED

            float _Underwater;
            // River ribbons use the same material and fragment stack, but their vertices are already
            // authored in full 3D instead of being a [-1,1] pool grid. Per-renderer and default-zero,
            // so every existing pool, lake, patch and clipmap stays on its original path.
            float _IsRiver;
            // Camera-following high-detail patch (windowed large bodies): a dense [-1,1] grid
            // remapped into just the sim window's sub-region of pool space, so near-field
            // ripple/wave geometry is sampled densely enough (target ~one vertex per sim texel)
            // to avoid the undersampling shimmer / false ripples a coarse whole-plane mesh shows
            // on big volumes. Inert at the defaults (_IsPatch = 0, _PatchDepthBias = 0).
            float  _IsPatch;          // 0 = normal full-plane surface, 1 = the window patch
            float2 _PatchPoolCenter;  // window centre in pool xz
            float2 _PatchPoolHalf;    // window half-size in pool units (per axis)
            float  _PatchDepthBias;   // view-space metres to pull the patch toward the camera so it wins over the coplanar far plane
            // Chunk fill level as the surface plane's POOL-Y (published per body by WaterVolume.Chunk.cs;
            // 0 = the rest plane, the default for every non-chunk body). Lowers / raises the disc so a
            // chunk can be partly full; the sphere clip below reads the fragment's DISPLACED pool
            // position, so the disc circle tracks the shape's cross-section at the chosen level for free.
            float  _ChunkSurfacePoolY;
            float  _ChunkBoundaryEnabled;
            float  _ChunkBoundaryWidth;
            float  _ChunkEdgeWaveHeight;
            float  _ChunkEdgeChoppiness;
            // Unbounded-ocean clipmap: 1 = a camera-following world-locked geometry-clipmap LOD level
            // (authored in INTEGER CELL UNITS, scaled to metres by the transform, reaching the horizon),
            // 0 = pool-grid surfaces. Inert at the default (_IsClipmap = 0).
            float  _IsClipmap;
            // Edge geomorph for a clipmap LOD level: in the outer band (Chebyshev cell distance from the
            // level centre >= _ClipmapMorphStart) the vertex slides onto the next-coarser lattice (nearest
            // EVEN cell) so it meets the coarser level vertex-for-vertex with no T-junction crack.
            // _ClipmapMorphScale = 1 / band width (cells). Inert on the outermost level (start >= M/2).
            float  _ClipmapMorphStart;
            float  _ClipmapMorphScale;
            // Distance (metres) at which the ocean surface has fully dissolved into the horizon sky, so
            // the far edge has no hard line. 0 = off (bounded bodies, and until the artist opts in). A
            // light stopgap - the real horizon softening is the (future) large-body fog pass.
            float  _HorizonFadeDistance;
            #define HORIZON_FADE_START 0.5   // fraction of the fade distance where the blend to sky begins
            // Exponential atmospheric horizon haze (supersedes the smoothstep stopgap above): the far
            // ocean dissolves toward the sky by distance with a physical 1 - exp(-density * dist) falloff.
            // _HorizonHazeColor.a tints the sky toward a fixed atmosphere colour (0 = pure sky, seamless).
            // Density 0 = off (bounded bodies, unchanged).
            float4 _HorizonHazeColor;
            float  _HorizonHazeDensity;
            float _WaveNormalStrength; // global; scales the wind-wave tilt on the normal
            float _RippleChoppiness;   // per-body; horizontal Gerstner pinch on the interactive ripple/wake (0 = off)
            float _PeakedRefineSteps;  // per-body (quality tier); see PEAKED_REFINE_MAX_STEPS

            float _RefractionDistortion;
            // Art-directed strength of the Snell bend on the analytic refraction path. 1 = physical.
            float _RefractionStrength;

            // Pool-space terrain bed height (R = bed height in pool units), baked by WaterVolume.
            sampler2D _BedTex;

            // Shore depth + SDF uniforms and helpers (Layer A/B) are declared in WaterShore.hlsl,
            // included via WaterLargeWaves.hlsl above; the debug branches below read them directly.

            // Interactive ripple sample (r = height, ba = normal.xz) for a surface point.
            // Whole-body bodies sample the pool UV as before. Windowed bodies sample the
            // camera-following window by WORLD position (sub-texel smooth, world-anchored)
            // and fade the ripple to flat over the last _SimEdgeFadeTexels, so there is no
            // seam where the window meets the analytic-only water. 'fade' is the ripple
            // weight: 1 inside the window, -> 0 at/beyond its border.
            float4 SampleRipple(float3 poolPos, float3 worldPos, out float fade)
            {
                fade = 1.0;
                float4 info = (float4)0.0;
                if (_SimWindowed < 0.5)
                {
                    // A single explicit exit keeps Unity's D3D compiler from losing the definite
                    // assignment of the out parameter across this uniform branch (Unity 6000.3.9f1
                    // otherwise terminates the shader worker instead of reporting the diagnostic).
                    info = SampleWaterBicubic(poolPos.xz * 0.5 + 0.5);
                }
                else
                {
                    float2 uv = WorldToSim(worldPos).xz * 0.5 + 0.5;
                    if (any(uv < 0.0) || any(uv > 1.0))
                    {
                        fade = 0.0;
                    }
                    else
                    {
                        float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
                        float2 d = min(uv, 1.0 - uv);
                        fade = saturate(min(d.x, d.y) / max(band, 1e-5));
                        info = SampleWaterBicubic(uv);
                        info.r  *= fade; // fade ripple height
                        info.ba *= fade; // fade normal tilt back to flat
                    }
                }
                return info;
            }

            // ---- RESTORED helpers (uncommitted work wiped by an errant whole-file revert;
            // verify against IDE Local History for a guaranteed-exact copy). ----
            float  _PatchCoverActive; // 1 = punch the base sheet where the near-field patch covers it
            float2 _PatchCoverMargin; // shrink of the cover test inside the window (pool units, per axis)

            // The UV SampleRipple WOULD read for this point - the raw texel address the headroom
            // debug view point-samples; one branch on _SimWindowed so the two never disagree.
            float2 RippleSimUV(float3 poolPos, float3 worldPos)
            {
                if (_SimWindowed < 0.5) return poolPos.xz * 0.5 + 0.5;
                return WorldToSim(worldPos).xz * 0.5 + 0.5;
            }

            // True where the dense near-field patch already draws this pixel, so the base sheet must
            // NOT (one surface per pixel). Patch/clipmap/underside renderers stay out.
            bool PatchCoversBaseSheet(float3 poolPos)
            {
                if (_PatchCoverActive < 0.5 || _IsPatch > 0.5 || _IsClipmap > 0.5 || _Underwater > 0.5)
                    return false;
                float2 inner = _PatchPoolHalf - _PatchCoverMargin;
                if (any(inner <= 0.0)) return false; // margin swallowed the window: keep the sheet whole
                return all(abs(poolPos.xz - _PatchPoolCenter) < inner);
            }

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 tangent : TANGENT;
                // River normalized lateral bake coordinate + longitudinal metres.
                float2 riverBakeUv : TEXCOORD0;
                // River-only metric surface coordinate + physical speed. Pool meshes leave UV1 at 0.
                float3 riverCurrentData : TEXCOORD1;
            };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0; // POOL space ([-1,1]); drives the analytic tracer
                float4 screenPos: TEXCOORD1;
                float3 worldPos : TEXCOORD2; // world space; drives depth/SSR/foam-contact
                float2 largeWaveSourceXZ : TEXCOORD3; // undisplaced world xz of the open-water wave,
                                                      // so the fragment normal reads the SOURCE point
                                                      // (not the chop-displaced worldPos)
                UNITY_FOG_COORDS(4)
                float3 worldNormal : TEXCOORD5; // base sheet normal; transported ribbon-up for rivers
                float4 worldTangent : TEXCOORD6; // x-slope axis; w reconstructs the z-slope axis
                float4 riverCurrentData : TEXCOORD7; // metric position xy, baked velocity zw
                float2 riverBakeUv : TEXCOORD8; // lateral 0..1, longitudinal world metres
            };

            #define RIVER_FRAME_MIN_LENGTH_SQ 1e-8
            #define GRID_TANGENT_HANDEDNESS -1.0

            float2 SampleRiverFluidVelocity(float2 bakeCoordinate, float fallbackSpeed)
            {
                if (_RiverFluidActive < 0.5) return float2(0.0, max(fallbackSpeed, 0.0));
                float2 uv = saturate(float2(
                    bakeCoordinate.x, bakeCoordinate.y * _RiverFluidInvLength));
                float2 encoded = tex2Dlod(_FoamMask, float4(uv, 0.0, 0.0)).rg;
                return (encoded * 2.0 - 1.0) * _RiverFluidMaxSpeed;
            }

            // WindWaveSampleXZ + _OceanWorldWaves moved to WaterWaves.hlsl (2026-08-10): the foam
            // glue and the waterline must pick the SAME wind-wave coordinate as this vertex path.

            float ChunkBoundaryInteriorWeight(float2 poolXZ)
            {
                if (_ChunkBoundaryEnabled < 0.5) return 1.0;
                float3 extent = VolumeExtentSafe();
                float edgeDistance = min((1.0 - abs(poolXZ.x)) * extent.x,
                                         (1.0 - abs(poolXZ.y)) * extent.z);
                return smoothstep(0.0, max(_ChunkBoundaryWidth, 1e-4), edgeDistance);
            }

            float3 StabilizeChunkBoundary(float3 worldFlat, float3 worldDisplaced, float2 poolXZ)
            {
                float interior = ChunkBoundaryInteriorWeight(poolXZ);
                float verticalWeight = lerp(_ChunkEdgeWaveHeight, 1.0, interior);
                float horizontalWeight = lerp(_ChunkEdgeChoppiness, 1.0, interior);
                float3 stabilized = worldDisplaced;
                stabilized.y = worldFlat.y + (worldDisplaced.y - worldFlat.y) * verticalWeight;
                stabilized.xz = worldFlat.xz + (worldDisplaced.xz - worldFlat.xz) * horizontalWeight;
                return stabilized;
            }

            float3 DisplaceSurfaceVertex(float3 poolFlat, float3 worldFlat, float4 info,
                                         float riverWeight, float4 riverCurrentData,
                                         out float3 poolDisplaced, out float2 largeWaveSourceXZ)
            {
                float2 poolXZ = poolFlat.xz;
                float3 position = poolFlat;
                position.y += info.r;                  // interactive ripple heightfield (windowed: faded)
                float2 gridWaveSample = WindWaveSampleXZ(poolXZ, worldFlat.xz);
                float2 riverWaveSample = RiverCurrentWaveSampleXZ(riverCurrentData);
                position.y += WaveHeight(lerp(gridWaveSample, riverWaveSample, riverWeight));
                                                       // small wind-wave detail; open water
                                                       // layers the big swell on top in world space below
                poolDisplaced = position;              // keep pool-space position for the tracer
                float3 worldPos = PoolToWorld(position);
                // Open water: add the wave in WORLD space (metres), so large bodies get real 3D waves
                // whose amplitude is NOT shrunk by the depth extent the way the pool-unit WaveHeight
                // above is. Height lifts Y; choppiness displaces xz (Gerstner) for sharp crests. The
                // SOURCE xz (before the xz displacement) is carried to the fragment so its normal reads
                // the wave at the same point the vertex did. No-op for pool/small bodies (_LargeBody = 0).
                largeWaveSourceXZ = worldPos.xz;
                // ONE shore + surf sample per vertex, shared by the wave height, the chop and the
                // swash film block below (the old path re-sampled the shore and re-evaluated the
                // surf fronts inside Height, again inside Displacement, and a third time for the
                // swash - ~2.5x the whole field per vertex). Inert defaults keep pools byte-identical.
                ShoreData shoreVert = ShoreDataInert();
                SurfWaveSample surfVert = SurfWaveSampleInert();
                if (_LargeBody > 0.5)
                {
                    float2 sourceXZ = worldPos.xz;
                    largeWaveSourceXZ = sourceXZ;
                    shoreVert = ShoreSample(sourceXZ);
                    surfVert = EvaluateSurfWaves(sourceXZ, shoreVert.depth, shoreVert.sdfDist,
                                                 shoreVert.toShore, shoreVert.slopeTan,
                                                 shoreVert.influence, _SurfBeatTime);
                    // Height + chop from one field evaluation. The far-field band-limit (dropping
                    // short waves the coarse mesh can't resolve, keeping the long swell) lives
                    // INSIDE, driven by camera distance - no-op for bounded bodies.
                    float lbwHeight;
                    float2 lbwDisp;
                    LargeBodyWaveHeightDispShore(sourceXZ, shoreVert, surfVert, lbwHeight, lbwDisp);
                    worldPos.y  += lbwHeight;
                    worldPos.xz += lbwDisp; // 0 when choppiness = 0
                }
                // Interactive-ripple horizontal choppiness (Crest-style _HorizontalDisplace, aimed at the
                // WAKE): the ripple sim only lifts HEIGHT, so the wake V and interactive ripples read soft
                // and round. Add a Gerstner pinch along the ripple slope so they sharpen. info.ba is the sim
                // normal.xz (= -grad h, already faded at the window edge), so displacing AGAINST it pulls
                // the surface toward crests. 0 = off (byte-identical). SIGN NOTE: if the wake BULGES instead
                // of sharpening, flip the '-' to '+' (cf. the sim-window Scroll sign).
                // The fragment re-samples the ripple at the SOURCE xz (largeWaveSourceXZ), i.e. the
                // same point this stage sampled info at, so the wake's normal/foam and its bump stay
                // one object. It used to re-sample at the displaced position, which was written off as
                // minor back when a whole-field multiplier kept lbwDisp at centimetres; at honest
                // metres that mismatch became a wake smearing across its own geometry.
                if (_RippleChoppiness > 0.0)
                    worldPos.xz -= _RippleChoppiness * info.ba;
                worldPos = StabilizeChunkBoundary(worldFlat, worldPos, poolXZ);
                // Surf swash film: over the beach the surface HUGS THE SAND (a thin film a few
                // centimetres proud of it) wherever the swash has recently reached - a flat plane
                // below the terrain would lose the depth test and the breathing waterline + wet
                // glaze would never render. Fragments past the drying wet line stay under the sand
                // (depth-occluded) and are clipped in the fragment anyway; the still-water region
                // is untouched (the lift only ever RAISES onto dry ground).
                // Gates match the fragment's clip/glaze block exactly (_BedValid included): if the
                // pool-frame bed bake failed, the fragment never clips the beach, so lifting film
                // geometry here would print a floating water sheet on dry sand. The shore sample +
                // swash are evaluated at the SOURCE xz - the same point the fragment uses - so the
                // lifted film and the wet-sand glaze breathe on the same swash phase even under
                // horizontal chop displacement (they used to sample different points).
                if (_SurfActive > 0.5 && _ShoreDepthValid > 0.5 && _UseBedDepth > 0.5
                    && _BedValid > 0.5 && _LargeBody > 0.5)
                {
                    float beachRise = -shoreVert.depth; // metres the sand sits above the still level
                    if (shoreVert.influence > 0.0 && beachRise > 0.0)
                    {
                        float2 swashVert = EvaluateSurfSwash(largeWaveSourceXZ, shoreVert.toShore,
                                                             shoreVert.slopeTan,
                                                             shoreVert.influence, _SurfBeatTime);
                        // FOAM-5: persistent swash deposits linger on the sand ABOVE the drying wet
                        // line. Lift the beach film right onto the sand wherever the foam buffer
                        // still holds a deposit, so the foam has geometry to DISSOLVE on instead of
                        // blinking out the instant the wet line recedes below it (the fragment clip
                        // extends by the same test, so the lifted vertex and surviving fragment
                        // agree). Same foam-coord the pond-foam layer uses. Gated: gain 0 keeps the
                        // old wet-line-only lift, byte-identical.
                        float geomReach = swashVert.y;
                        if (_ShoreSwashDepositGain > 0.0)
                        {
                            float2 depUV = (_SimWindowed < 0.5)
                                ? (position.xz * 0.5 + 0.5)
                                : (WorldToSim(float3(largeWaveSourceXZ.x, worldPos.y,
                                                     largeWaveSourceXZ.y)).xz * 0.5 + 0.5);
                            if (SampleFoamMaskWindowed(depUV) > FOAM_MASK_EPSILON)
                                geomReach = beachRise; // hold the film onto the sand under the deposit
                        }
                        if (geomReach > 1e-3)
                        {
                            // Both joins were hard clamps, and each printed its own crease running
                            // parallel to the beach. INNER: min(beachRise, geomReach) is where the
                            // sand-hugging film flattens into the wet-line plateau. OUTER: max()
                            // against the wave surface is where the sea's slope meets the beach's -
                            // the "angle" between the open water and the swash. Both are C0 but not
                            // C1, and a slope discontinuity is exactly what the eye reads as an
                            // edge. Smoothing them over SURF_FILM_BLEND joins sea, film and plateau
                            // into one continuous surface; a blend of 0 restores the hard clamps.
                            float filmTop = _ShoreWaterLevel
                                          + SmoothMin(beachRise, geomReach, SURF_FILM_BLEND)
                                          + SURF_FILM_THICKNESS;
                            worldPos.y = SmoothMax(worldPos.y, filmTop, SURF_FILM_BLEND);
                        }
                    }
                }
                return worldPos;
            }

            v2f vert(appdata v)
            {
                v2f o;
                // Three vertex sources feed the SAME ripple/wave path below:
                //  - full plane   : the grid vertex IS pool xz;
                //  - window patch : the SAME [-1,1] grid remapped into the window's pool sub-region,
                //                   so it tessellates only the near field (dense);
                //  - ocean clipmap: verts authored in WORLD metres (x,0,z) on a camera-following mesh,
                //                   mapped BACK into pool space so the ripple/pool sampling is unchanged
                //                   (ripples fade to flat past the sim window, leaving open-water swell).
                float3 poolFlat;
                float3 worldFlat;
                if (_IsClipmap > 0.5)
                {
                    // Edge geomorph: in the outer band, slide the vertex onto the next-coarser lattice
                    // (nearest EVEN cell) so this LOD level meets the coarser one crack-free. v.vertex.xz
                    // are this level's integer cell indices; the transform scales them to world metres.
                    float2 cell = v.vertex.xz;
                    float cheb = max(abs(cell.x), abs(cell.y));
                    float morph = saturate((cheb - _ClipmapMorphStart) * _ClipmapMorphScale);
                    float2 morphedCell = lerp(cell, round(cell * 0.5) * 2.0, morph);
                    float3 worldOnPlane = mul(unity_ObjectToWorld, float4(morphedCell.x, 0.0, morphedCell.y, 1.0)).xyz;
                    worldFlat = float3(worldOnPlane.x, _VolumeCenter.y, worldOnPlane.z); // resting plane
                    poolFlat = WorldToPool(worldFlat);
                    poolFlat.y = 0.0;
                }
                else
                {
                    float2 gridPoolXZ = (_IsPatch > 0.5) ? (_PatchPoolCenter + v.vertex.xy * _PatchPoolHalf)
                                                         : v.vertex.xy;
                    poolFlat = float3(gridPoolXZ.x, _ChunkSurfacePoolY, gridPoolXZ.y); // grid -> pool (x, level, z); level 0 for non-chunks
                    worldFlat = PoolToWorld(poolFlat);
                }
                // River meshes are already authored in full 3D. Select that source without a second
                // vertex control-flow fork: Unity's D3D compiler duplicated this already-large stage
                // around the uniform branch and terminated its worker process. Pools, patches and
                // clipmaps retain riverWeight = 0 and therefore their original path exactly.
                float riverWeight = saturate(_IsRiver);
                float3 riverWorldFlat = mul(unity_ObjectToWorld, v.vertex).xyz;
                worldFlat = lerp(worldFlat, riverWorldFlat, riverWeight);
                poolFlat = lerp(poolFlat, WorldToPool(riverWorldFlat), riverWeight);
                float3 gridWorldNormal = mul(VolumeRot(), float3(0.0, 1.0, 0.0));
                float3 riverWorldNormal = UnityObjectToWorldNormal(v.normal);
                o.worldNormal = normalize(lerp(gridWorldNormal, riverWorldNormal, riverWeight));
                float3 gridWorldTangent = mul(VolumeRot(), float3(1.0, 0.0, 0.0));
                float3 riverWorldTangent = mul((float3x3)unity_ObjectToWorld, v.tangent.xyz);
                // Non-uniform scale can make a transformed tangent lean into the normal. Removing
                // that component keeps the transported ribbon frame orthogonal on waterfall spans.
                riverWorldTangent = riverWorldTangent
                                  - riverWorldNormal
                                  * dot(riverWorldTangent, riverWorldNormal);
                float riverTangentLengthSq = max(dot(riverWorldTangent, riverWorldTangent),
                                                  RIVER_FRAME_MIN_LENGTH_SQ);
                riverWorldTangent *= rsqrt(riverTangentLengthSq);
                o.worldTangent.xyz = normalize(lerp(gridWorldTangent, riverWorldTangent,
                                                    riverWeight));
                o.worldTangent.w = lerp(GRID_TANGENT_HANDEDNESS, v.tangent.w, riverWeight);
                o.riverBakeUv = v.riverBakeUv * riverWeight;
                float2 riverVelocity = SampleRiverFluidVelocity(
                    v.riverBakeUv, v.riverCurrentData.z);
                o.riverCurrentData = float4(v.riverCurrentData.xy, riverVelocity) * riverWeight;
                // World position at the surface plane (height 0) picks the windowed UV; the
                // xz mapping doesn't depend on ripple height, so this is exact.
                float fade;
                float4 info = SampleRipple(poolFlat, worldFlat, fade);
                // The interactive solver is a rectangular WaterVolume heightfield. Until a river-
                // space simulation owns an explicit mapping, sampling it on a winding ribbon makes
                // an unrelated second wave layer that the river wind-wave controls cannot affect.
                // Keep the shared analytic wind waves below; those are world-unit authored and are
                // the technically valid reusable motion path for this mesh.
                info *= 1.0 - riverWeight;
                float3 worldPos = DisplaceSurfaceVertex(
                    poolFlat, worldFlat, info, riverWeight, o.riverCurrentData,
                    o.position, o.largeWaveSourceXZ);
                // The common shader expresses height along the WaterVolume up axis. A ribbon may
                // turn through a waterfall, so carry that same scalar displacement along its
                // transported surface normal instead of pulling every wave vertically upward.
                float riverHeight = dot(worldPos - worldFlat, gridWorldNormal);
                float3 riverWorldPos = worldFlat + o.worldNormal * riverHeight;
                worldPos = lerp(worldPos, riverWorldPos, riverWeight);
                o.worldPos = worldPos;
                // Nudge the patch a fixed few centimetres toward the camera IN VIEW SPACE so it wins the
                // depth test against the coplanar far plane at EVERY distance. The old bias was a constant
                // NDC offset (bias * pos.w) which, under the non-linear reversed-Z buffer, grew into a huge
                // world-depth offset far from the camera and let the patch draw OVER opaque geometry. A
                // fixed view-space (world-metre) offset can never beat opaque more than _PatchDepthBias
                // metres behind the patch. Inert when bias = 0 (every non-patch surface).
                float4 viewPos = mul(UNITY_MATRIX_V, float4(worldPos, 1.0));
                viewPos.z += _PatchDepthBias; // view forward is -Z, so +Z moves toward the camera (nearer)
                o.pos = mul(UNITY_MATRIX_P, viewPos);
                o.screenPos = ComputeScreenPos(o.pos);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }

#endif // WATER_SURFACE_VERT_STAGE_INCLUDED
