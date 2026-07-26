// WebGL Water - GPU foam particle rendering (KWS-inspired)
//
// Draws the particle pool written by WaterFoamParticles.compute as procedural quads:
// the vertex shader pulls a FoamParticle from a StructuredBuffer by SV_VertexID
// (6 vertices per particle), so there is no mesh, no instancing path and no geometry
// shader - the one expansion technique that works everywhere WebGPU does.
//
// Surface foam lies IN the water plane (tilted by the local ripple normal, glued to
// the ripple + wind-wave height like the surface mesh), so it never criss-crosses
// the waterline. On open water each quad CORNER rides the composed swell (wind-wave
// layer + chop-inverted FFT/analytic field), so quads bend with the wave instead of
// being depth-sliced by it. Spray is a camera-facing billboard stretched along its
// velocity.
Shader "AbstractOcclusion/WebGpuWater/FoamParticles"
{
    Properties
    {
        _ParticleTex ("Particle Sprite Atlas (2x2 variants)", 2D) = "white" {}
        _Tint ("Tint", Color) = (0.95, 0.98, 1.0, 1.0)
        _ParticleOpacity ("Opacity", Range(0, 1)) = 0.85
        _VelocityStretch ("Velocity Stretch (per unit speed)", Range(0, 10)) = 3.0
        _SoftFadeDistance ("Soft Fade vs Scene Depth (world)", Range(0.001, 0.5)) = 0.05
        // Flipbook grid + FPS are NOT material sliders: they are driven from the WaterFoamParticles
        // component (one place to tweak) via its MaterialPropertyBlock. Declared as uniforms below.
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl" // _WaterTex + SampleWaterBilinear, _LightDir
            #include "WaterWaves.hlsl"  // WaveHeight (ambient wind-wave layer)
            #include "WaterVolume.hlsl" // pool/window <-> world frame
            #include "WaterExclusion.hlsl" // dry-interior volumes: InsideExclusion for the vertex filter
            #include "WaterLargeWaves.hlsl" // FFT ocean surface: LargeBodyWaveHeight, OceanFftNormalTilt, _OceanFftActive
            #include "WaterFoamCommon.hlsl" // shared foam lighting + erosion (FOAM_LIGHT_WRAP, EROSION_SOFTNESS...)
            #include "WaterParticleCommon.hlsl" // billboard corner expansion + flipbook atlas cell

            // Atlas layout is a uniform now (_ParticleFlipbookGrid): (1,1) = a plain non-atlas texture,
            // (2,2) etc. = a flipbook. Optional, like the surface foam's _FoamTexFrames.

            // Life envelope (FoamParticleEnvelope) is shared via WaterFoamCommon.hlsl with the
            // density-splat compute, so screen-space foam weight always matches the quad look.
            // Erosion dissolve + foam lighting constants come from WaterFoamCommon.hlsl,
            // shared with the surface foam and the splash particles.

            // Below this speed a quad is not stretched (avoids jitter around zero).
            #define STRETCH_MIN_SPEED    0.02
            #define STRETCH_MAX          4.0
            // Slow/apex spray still gets this fixed elongation along a per-seed direction:
            // a camera-facing quad with radial alpha is a perfect circle by construction,
            // and spray hangs at ~zero velocity exactly when you look at it - the one case
            // the velocity stretch can never break up.
            #define SPRAY_IDLE_STRETCH   1.3

            // Lift surface-foam quads slightly off the water so they never z-fight it.
            #define SURFACE_LIFT         0.004

            // Floating foam (KIND_SURFACE) never stretches past this. A landed droplet keeps a
            // fraction of its splash speed (SPRAY_LANDING_KEEP, compute side) and the crest roll
            // adds more, so the uncapped 1 + speed * _VelocityStretch smeared resting deposits up
            // to 5x along a direction unrelated to the drift. Airborne spray keeps the full
            // STRETCH_MAX - motion stretch is an in-flight look.
            #define SURFACE_STRETCH_MAX  1.5
            // Fixed-point steps inverting the Gerstner chop for the open-water glue: the rendered
            // surface ABOVE a world xz is the wave field evaluated at a chop-DISPLACED source
            // point, so sampling the field at the raw xz mis-places foam exactly on steep crests.
            // CPU buoyancy inverts with LBW_INVERSION_ITERATIONS (4, physics-grade); one step
            // removes the first-order error and every extra step costs a full wave-field
            // evaluation per vertex, so the visual glue stops at 1.
            #define FOAM_CHOP_INVERSION_STEPS 1
            // Wind-wave world-metre divide guard - same value WaterSurfaceVertStage's
            // WindWaveSampleXZ uses, so the two samplers can never disagree at the floor.
            #define WIND_WAVE_METERS_MIN 1e-3

            // Open-water camera-ward depth bias for surface-foam quads (the sim-window patch's
            // _PatchDepthBias idiom, view-space metres): the rendered water is a triangle mesh
            // whose linear chords lie ABOVE the analytic curve in every concave-up region
            // (troughs), and the distant clipmap undersamples the wind-wave layer - so a quad
            // glued to the analytic surface loses the depth test to the very water it sits on
            // (foam swallowed by drifting wavelets, fresh deposits hidden until the wave phase
            // frees them). The bias grows with distance to track the mesh's coarsening chord
            // error, capped so a genuinely occluding crest between camera and foam still wins.
            #define FOAM_DEPTH_BIAS_BASE   0.02
            #define FOAM_DEPTH_BIAS_SLOPE  0.002
            #define FOAM_DEPTH_BIAS_MAX    0.5

            static const float KIND_SPRAY = 1.0;
            // Corner expansion + flipbook cell come from WaterParticleCommon.hlsl (shared
            // with SurfRollerParticles.shader).

            // MUST match FoamParticle in WaterFoamParticles.compute (48 bytes).
            struct FoamParticle
            {
                float3 worldPos;
                float3 velocity;
                float  age;
                float  life;
                float  size;
                float  seed;
                float  kind;
                float  strength;
            };
            StructuredBuffer<FoamParticle> _Particles;

            sampler2D _ParticleTex;
            // Which kinds this draw renders: 0 = both, 1 = floating foam only (KIND_SURFACE),
            // 2 = spray only (KIND_SPRAY). Lets foam and spray draw in separate passes with their
            // own materials. Set per draw by WaterFoamParticles.cs, never a material slider.
            float _DrawKind;
            // _LargeBody (1 = open water, picks the large-body glue below) comes from
            // WaterVolume.hlsl - already included; do not redeclare.
            float3 _SunColor; // Unity directional light color * intensity (global, from WaterVolume)
            float4 _Tint;
            float _ParticleOpacity;
            float _VelocityStretch;
            float _SoftFadeDistance;
            float2 _ParticleFlipbookGrid; // atlas (cols, rows); (1,1) = plain texture, no flipbook
            float _ParticleFlipbookFps;   // 0 = static per-seed variant; >0 animates the atlas over age
            sampler2D _CameraDepthTexture;
            // 1 = ocean clipmap: the small wind-wave layer samples in WORLD metres; 0 = pool xz
            // (bounded bodies). Same per-body value WaterSurfaceVertStage reads - published through
            // WriteBodyProps into this draw's MaterialPropertyBlock; declared pass-locally there,
            // so it must be re-declared here.
            float _OceanWorldWaves;

            // Rendered open-water surface at a SOURCE xz (the undisplaced field point), matching
            // WaterSurfaceVertStage term for term: swell/FFT height (shore shoaling, ambient fade
            // and the surf fronts all live inside LargeBodyWaveHeightDispShore) + the small
            // wind-wave layer, with the Gerstner chop displacing xz exactly like the surface mesh.
            // Interactive ripples stay outside this glue - the trade the open-water path always
            // made. Returns the DISPLACED world position the surface actually renders.
            float3 LargeBodySurfaceAt(float2 sourceXZ, ShoreData shore, SurfWaveSample surf)
            {
                float height;
                float2 disp;
                LargeBodyWaveHeightDispShore(sourceXZ, shore, surf, height, disp);
                // Wind-wave layer: oceans sample in world metres, bounded bodies in pool xz (the
                // surface's WindWaveSampleXZ contract). Its pool-unit amplitude scales by the
                // volume's vertical extent, exactly as PoolToWorld scales the surface vertex.
                float2 windXZ = (_OceanWorldWaves > 0.5)
                    ? sourceXZ / max(_WaveMetersPerUnit, WIND_WAVE_METERS_MIN)
                    : WorldToPool(float3(sourceXZ.x, 0.0, sourceXZ.y)).xz;
                float surfaceY = _VolumeCenter.y + height
                               + WaveHeight(windXZ) * VolumeExtentSafe().y;
                float2 displacedXZ = sourceXZ + disp;
                return float3(displacedXZ.x, surfaceY, displacedXZ.y);
            }

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv        : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float3 litColor  : TEXCOORD2; // per-vertex foam lighting (soft blobs: no need per-pixel)
                float2 fade      : TEXCOORD3; // x = life envelope, y = fragment eye depth
            };

            // Degenerate output for dead slots: w = 0 collapses the triangle.
            v2f Dead()
            {
                v2f o;
                o.pos = float4(0, 0, 0, 0);
                o.uv = 0; o.screenPos = 0; o.litColor = 0; o.fade = 0;
                return o;
            }

            v2f vert(uint vid : SV_VertexID)
            {
                FoamParticle particle = _Particles[vid / 6];
                if (particle.life <= 0.0 || particle.age >= particle.life) return Dead();
                // Kind filter (two-pass split): a foam-only pass drops spray, a spray-only pass
                // drops foam, so each can be drawn with its own material. 0 = draw both.
                bool isSpray = (particle.kind == KIND_SPRAY);
                if (_DrawKind > 1.5 && !isSpray) return Dead();                  // spray-only pass
                if (_DrawKind > 0.5 && _DrawKind < 1.5 && isSpray) return Dead(); // foam-only pass

                float2 corner = ParticleQuadCorner(vid);

                // ---- glue the particle to the animated surface ----
                // Open water hoists ONE shore + surf sample (the surface vertex's own idiom) and
                // reuses it for every field evaluation this vertex makes: the shore varies over
                // metres, the quad spans centimetres. Inert defaults keep the pond path untouched.
                ShoreData glueShore = ShoreDataInert();
                SurfWaveSample glueSurf = SurfWaveSampleInert();
                float2 glueSrcXZ = particle.worldPos.xz; // chop-inverted SOURCE point (open water)
                float3 surfaceWorld;
                float3 surfaceNormal;
                if (_LargeBody > 0.5)
                {
                    // Open water (FFT or analytic): ride the FULL rendered surface. The previous
                    // glue sampled LargeBodyWaveHeight at the particle's raw xz and stopped there,
                    // which missed THREE terms the surface vertex renders: (1) the small wind-wave
                    // layer (the pond path always had it - its cm-scale waves depth-sliced the
                    // cm-scale deposits into "bands cut by water"), (2) the Gerstner chop (the
                    // surface above a world xz is the field at a DISPLACED source point - worst
                    // exactly on steep crests, where foam concentrates), and (3) wave curvature
                    // (handled per-corner at the quad expansion below). Interactive ripples remain
                    // outside this glue - the trade the open-water path always made. The pond path
                    // (else) is byte-for-byte unchanged.
                    float2 wxz = particle.worldPos.xz;
                    glueShore = ShoreSample(wxz);
                    glueSurf = EvaluateSurfWaves(wxz, glueShore.depth, glueShore.sdfDist,
                                                 glueShore.toShore, glueShore.slopeTan,
                                                 glueShore.influence, _SurfBeatTime);
                    float invHeight;
                    float2 invDisp;
                    [unroll]
                    for (int it = 0; it < FOAM_CHOP_INVERSION_STEPS; it++)
                    {
                        LargeBodyWaveHeightDispShore(glueSrcXZ, glueShore, glueSurf,
                                                     invHeight, invDisp);
                        glueSrcXZ = wxz - invDisp;
                    }
                    surfaceWorld = LargeBodySurfaceAt(glueSrcXZ, glueShore, glueSurf);
                    // Normal lean composed like the surface's own (ApplyLargeBodyWaveNormal...):
                    // FFT tilt (shore-shoaled) faded under the surf fronts, plus the fronts' own
                    // slope, edge-feathered - evaluated at the SOURCE xz, the same point the
                    // surface fragment reads. Still 0 tilt when FFT is off in open analytic water.
                    float2 tilt = (OceanFftNormalTiltShore(glueSrcXZ, glueShore)
                                       * SurfAmbientWeight(glueSurf.mask)
                                   - glueSurf.slopeXZ) * LbwEdgeWeight(glueSrcXZ);
                    surfaceNormal = normalize(float3(tilt.x, 1.0, tilt.y));
                }
                else
                {
                    float3 poolPos = WorldToPool(particle.worldPos);
                    float2 fcoord = (_SimWindowed < 0.5) ? (poolPos.xz * 0.5 + 0.5)
                                                         : (WorldToSim(particle.worldPos).xz * 0.5 + 0.5);
                    float4 info = SampleWaterBilinear(fcoord);
                    poolPos.y = info.r + WaveHeight(poolPos.xz);
                    surfaceWorld = PoolToWorld(poolPos);
                    surfaceNormal = PoolNormalToWorld(
                        float3(info.b, sqrt(max(1e-4, 1.0 - dot(info.ba, info.ba))), info.a));
                }
                float3 center = surfaceWorld
                              + surfaceNormal * SURFACE_LIFT
                              + float3(0, 1, 0) * max(0.0, particle.worldPos.y); // spray height offset

                // Conditional dry-volume filter (render side): a sprite whose CURRENT point sits
                // inside an exclusion volume is dropped outright this frame. The compute's
                // depth-ramped fade still recycles the particle over a few frames, but the
                // visual removal must not wait on it - drifting foam entering a hull's carve
                // showed sprites floating in the dry interior. `center` is the glued surface
                // point for foam and the true airborne point for spray, so droplets arcing
                // OVER a hull keep drawing. Zero volumes: the loop never runs (free).
                if (_ExclusionCount > 0.5 && InsideExclusion(center)) return Dead();

                // ---- quad axes ----
                float3 axisX, axisY;
                float stretch = 1.0;
                float speed = length(particle.velocity);
                if (particle.kind == KIND_SPRAY)
                {
                    // camera-facing, stretched along the screen-projected velocity
                    float3 camRight = UNITY_MATRIX_V[0].xyz;
                    float3 camUp = UNITY_MATRIX_V[1].xyz;
                    float2 vScreen = float2(dot(particle.velocity, camRight),
                                            dot(particle.velocity, camUp));
                    float vLen = length(vScreen);
                    if (speed > STRETCH_MIN_SPEED && vLen > 1e-4)
                    {
                        float2 d = vScreen / vLen;
                        axisX = camRight * d.x + camUp * d.y;
                        axisY = camRight * (-d.y) + camUp * d.x;
                        stretch = max(1.0 + min(STRETCH_MAX, speed * _VelocityStretch),
                                      SPRAY_IDLE_STRETCH);
                    }
                    else
                    {
                        // Apex/slow droplet: fixed per-seed elongation so it never renders
                        // as a perfect circle (see SPRAY_IDLE_STRETCH).
                        float idleYaw = particle.seed * PARTICLE_TWO_PI;
                        float2 d = float2(cos(idleYaw), sin(idleYaw));
                        axisX = camRight * d.x + camUp * d.y;
                        axisY = camRight * (-d.y) + camUp * d.x;
                        stretch = SPRAY_IDLE_STRETCH;
                    }
                }
                else
                {
                    // in the surface plane: seed yaw, stretched along the drift direction.
                    // Both normalizes are NaN-guarded (DEGENERATE_DIR_EPSILON, WaterShared.hlsl):
                    // cross degenerates when the surface normal reaches +/-Z, and the projected
                    // velocity cancels when the drift is parallel to the normal (extreme wave
                    // tilt) - either NaN would spread to the whole billboard.
                    float yaw = particle.seed * PARTICLE_TWO_PI;
                    float3 rawFlat = cross(surfaceNormal, float3(0, 0, 1));
                    if (dot(rawFlat, rawFlat) < DEGENERATE_DIR_EPSILON)
                        rawFlat = cross(surfaceNormal, float3(1, 0, 0));
                    float3 flat0 = normalize(rawFlat);
                    float3 flat1 = cross(surfaceNormal, flat0);
                    axisX = flat0 * cos(yaw) + flat1 * sin(yaw);
                    if (speed > STRETCH_MIN_SPEED)
                    {
                        float3 planar = particle.velocity - surfaceNormal * dot(particle.velocity, surfaceNormal);
                        if (dot(planar, planar) >= DEGENERATE_DIR_EPSILON)
                        {
                            axisX = normalize(planar);
                            // Capped well below the spray range: resting foam that still carries
                            // its landing/crest-roll speed must not smear (SURFACE_STRETCH_MAX).
                            stretch = min(1.0 + speed * _VelocityStretch, SURFACE_STRETCH_MAX);
                        }
                    }
                    axisY = cross(surfaceNormal, axisX);
                }

                float3 worldVertex;
                if (!isSpray && _LargeBody > 0.5)
                {
                    // ---- per-corner glue (open water): one flat tilted plane cannot follow
                    // metre-scale wave curvature, and whatever dipped below the ZWrite-On surface
                    // was depth-sliced to a band. Each vertex instead rides the surface at its OWN
                    // corner: the offset is laid out in SOURCE space and the field re-evaluated
                    // there, so the quad bends (and chop-pinches) exactly like the water mesh under
                    // it. The two triangles' shared corners get identical positions by
                    // construction, so the quad stays watertight.
                    float3 cornerOffset = axisX * (corner.x * particle.size * stretch)
                                        + axisY * (corner.y * particle.size);
                    float2 cornerSrcXZ = glueSrcXZ + cornerOffset.xz;
                    worldVertex = LargeBodySurfaceAt(cornerSrcXZ, glueShore, glueSurf)
                                + surfaceNormal * SURFACE_LIFT;
                }
                else
                {
                    worldVertex = center
                                + axisX * (corner.x * particle.size * stretch)
                                + axisY * (corner.y * particle.size);
                }

                // ---- life envelope ----
                float envelope = FoamParticleEnvelope(particle.age, particle.life) * particle.strength;

                // ---- sprite cell from the atlas: a fixed per-seed variant, or an animated flipbook
                // (foam churn) when _ParticleFlipbookFps > 0 (shared math, WaterParticleCommon.hlsl) ----
                float2 uv = ParticleFlipbookUv(corner, _ParticleFlipbookGrid.xy,
                                               particle.seed, particle.age, _ParticleFlipbookFps);

                // ---- lighting, matched to the surface foam ----
                float wrapped = FoamWrappedDiffuse(surfaceNormal, _LightDir);

                // ---- projection: open-water surface foam is pulled a few centimetres toward
                // the camera IN VIEW SPACE (see FOAM_DEPTH_BIAS_*) so the surface mesh's chord
                // error can't swallow the glued quads. The soft-fade eye depth stays UNBIASED -
                // it measures true distance against the opaque scene, not the z-test.
                float4 viewPos = mul(UNITY_MATRIX_V, float4(worldVertex, 1.0));
                float eyeDepth = -viewPos.z;
                if (!isSpray && _LargeBody > 0.5)
                    viewPos.z += min(FOAM_DEPTH_BIAS_BASE + eyeDepth * FOAM_DEPTH_BIAS_SLOPE,
                                     FOAM_DEPTH_BIAS_MAX); // view forward is -Z: +Z = nearer

                v2f o;
                o.pos = mul(UNITY_MATRIX_P, viewPos);
                o.uv = uv;
                o.screenPos = ComputeScreenPos(o.pos);
                o.litColor = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                o.fade = float2(envelope, eyeDepth);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Negative mip bias keeps the lace from averaging into a round blob at
                // distance (FOAM_SPRITE_MIP_BIAS, shared foam-look constant).
                float4 sprite = tex2Dbias(_ParticleTex, float4(i.uv, 0.0, FOAM_SPRITE_MIP_BIAS));
                float envelope = i.fade.x;

                // Texture-preserving erosion: fresh sprites show their own lace, dying ones
                // crumble through it (the old gate-only form saturated the interior into a
                // solid disc - the "round semi-transparent spheres").
                float alpha = FoamErosionLace(sprite.a, envelope);
                alpha *= envelope * _ParticleOpacity;

                // soft fade against the opaque scene (pool walls, floating objects)
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                alpha *= saturate((sceneEye - i.fade.y) / _SoftFadeDistance);

                return fixed4(i.litColor * sprite.rgb, alpha);
            }
            ENDCG
        }
    }
}
