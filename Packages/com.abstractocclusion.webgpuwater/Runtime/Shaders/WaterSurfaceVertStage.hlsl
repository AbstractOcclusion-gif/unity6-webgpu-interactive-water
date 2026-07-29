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
            // 1 = sample the small wind-wave layer in WORLD metres (oceans), so its scale is independent
            // of the volume extent; 0 = pool space (bounded bodies, unchanged). Inert at the default.
            float  _OceanWorldWaves;
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
                if (_SimWindowed < 0.5)
                    return SampleWaterBicubic(poolPos.xz * 0.5 + 0.5);

                float2 uv = WorldToSim(worldPos).xz * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) { fade = 0.0; return (float4)0.0; }

                float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
                float2 d = min(uv, 1.0 - uv);
                fade = saturate(min(d.x, d.y) / max(band, 1e-5));

                float4 info = SampleWaterBicubic(uv);
                info.r  *= fade; // fade ripple height
                info.ba *= fade; // fade normal tilt back to flat
                return info;
            }

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0; // POOL space ([-1,1]); drives the analytic tracer
                float4 screenPos: TEXCOORD1;
                float3 worldPos : TEXCOORD2; // world space; drives depth/SSR/foam-contact
                float2 largeWaveSourceXZ : TEXCOORD3; // undisplaced world xz of the open-water wave,
                                                      // so the fragment normal reads the SOURCE point
                                                      // (not the chop-displaced worldPos)
            };

            // Coordinate fed to the wind-wave layer (WaveHeight/WaveSlope). Bounded bodies sample in
            // pool xz, so the wave scale rides the volume extent (worldXZ / extent). Oceans sample in
            // WORLD metres instead, so tweaking the volume box no longer slides/rescales the wind-wave
            // pattern - its scale is set solely by Pool Half Extent Meters (_WaveMetersPerUnit). At a
            // matched extent the two are identical, so this only decouples; it doesn't change the look.
            float2 WindWaveSampleXZ(float2 poolXZ, float2 worldXZ)
            {
                if (_OceanWorldWaves > 0.5) return worldXZ / max(_WaveMetersPerUnit, 1e-3);
                return poolXZ;
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
                // World position at the surface plane (height 0) picks the windowed UV; the
                // xz mapping doesn't depend on ripple height, so this is exact.
                float2 poolXZ = poolFlat.xz;
                float fade;
                float4 info = SampleRipple(poolFlat, worldFlat, fade);
                float3 position = poolFlat;
                position.y += info.r;                  // interactive ripple heightfield (windowed: faded)
                position.y += WaveHeight(WindWaveSampleXZ(poolXZ, worldFlat.xz)); // small wind-wave detail; open water
                                                       // layers the big swell on top in world space below
                o.position = position;                 // keep pool-space position for the tracer
                float3 worldPos = PoolToWorld(position);
                // Open water: add the wave in WORLD space (metres), so large bodies get real 3D waves
                // whose amplitude is NOT shrunk by the depth extent the way the pool-unit WaveHeight
                // above is. Height lifts Y; choppiness displaces xz (Gerstner) for sharp crests. The
                // SOURCE xz (before the xz displacement) is carried to the fragment so its normal reads
                // the wave at the same point the vertex did. No-op for pool/small bodies (_LargeBody = 0).
                o.largeWaveSourceXZ = worldPos.xz;
                // ONE shore + surf sample per vertex, shared by the wave height, the chop and the
                // swash film block below (the old path re-sampled the shore and re-evaluated the
                // surf fronts inside Height, again inside Displacement, and a third time for the
                // swash - ~2.5x the whole field per vertex). Inert defaults keep pools byte-identical.
                ShoreData shoreVert = ShoreDataInert();
                SurfWaveSample surfVert = SurfWaveSampleInert();
                if (_LargeBody > 0.5)
                {
                    float2 sourceXZ = worldPos.xz;
                    o.largeWaveSourceXZ = sourceXZ;
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
                // of sharpening, flip the '-' to '+' (cf. the sim-window Scroll sign). The fragment
                // re-samples the ripple at the displaced xz (minor, as the large-wave path already does);
                // add a source-xz carry later if a strong pinch shows a sampling seam.
                if (_RippleChoppiness > 0.0)
                    worldPos.xz -= _RippleChoppiness * info.ba;
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
                        float2 swashVert = EvaluateSurfSwash(o.largeWaveSourceXZ, shoreVert.toShore,
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
                                : (WorldToSim(float3(o.largeWaveSourceXZ.x, worldPos.y,
                                                     o.largeWaveSourceXZ.y)).xz * 0.5 + 0.5);
                            if (SampleFoamMaskWindowed(depUV) > FOAM_MASK_EPSILON)
                                geomReach = beachRise; // hold the film onto the sand under the deposit
                        }
                        if (geomReach > 1e-3)
                            worldPos.y = max(worldPos.y, _ShoreWaterLevel
                                             + min(beachRise, geomReach) + SURF_FILM_THICKNESS);
                    }
                }
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
                return o;
            }

#endif // WATER_SURFACE_VERT_STAGE_INCLUDED
