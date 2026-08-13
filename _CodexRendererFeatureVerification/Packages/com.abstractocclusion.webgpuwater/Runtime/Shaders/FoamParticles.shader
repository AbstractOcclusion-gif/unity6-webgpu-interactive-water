// WebGpuWater - GPU foam particle rendering (KWS-inspired)
//
// Draws the particle pool written by WaterFoamParticles.compute as procedural quads:
// the vertex shader pulls a FoamParticle from a StructuredBuffer by SV_VertexID
// (6 vertices per particle), so there is no mesh, no instancing path and no geometry
// shader - the one expansion technique that works everywhere WebGPU does.
//
// EVERY quad that can straddle the waterline must lie IN the surface plane: the surface
// writes depth (ZWrite On) and these sprites draw after it, so a quad that crosses it has
// the far half Z-killed - a sprite cut in half along a screen line. Surface foam and the
// seen-from-above bubble image both follow that rule; only fully-airborne spray and the
// fully-submerged bubble view are free to face the camera.
//
// Surface foam lies IN the water plane (tilted by the local ripple normal, glued to
// the ripple + wind-wave height like the surface mesh), so it never criss-crosses
// the waterline. Spray is a camera-facing billboard stretched along its velocity.
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
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl" // _WaterTex + SampleWaterBilinear, _LightDir
            #include "WaterWaves.hlsl"  // WaveHeight (ambient wind-wave layer)
            #include "WaterVolume.hlsl" // pool/window <-> world frame
            #include "WaterLargeWaves.hlsl" // FFT ocean surface: LargeBodyWaveHeight, OceanFftNormalTilt, _OceanFftActive
            #include "WaterFoamCommon.hlsl" // shared foam lighting + erosion (FOAM_LIGHT_WRAP, EROSION_SOFTNESS...)
            #include "WaterParticleCommon.hlsl" // billboard corner expansion + flipbook atlas cell
            #include "WaterExclusion.hlsl" // dry-interior volumes: per-fragment dissolve of intruding sprites
            #include "WaterParticleFog.hlsl" // after-fog reroute frames: per-sprite camera->particle fog

            // Atlas layout is a uniform now (_ParticleFlipbookGrid): (1,1) = a plain non-atlas texture,
            // (2,2) etc. = a flipbook. Optional, like the surface foam's _FoamTexFrames.

            // Life envelope (FoamParticleEnvelope) is shared via WaterFoamCommon.hlsl with the
            // density-splat compute, so screen-space foam weight always matches the quad look.
            // Erosion dissolve + foam lighting constants come from WaterFoamCommon.hlsl,
            // shared with the surface foam and the splash particles.

            // Below this speed a quad is not stretched (avoids jitter around zero).
            #define STRETCH_MIN_SPEED    0.02
            #define STRETCH_MAX          4.0
            #define CREST_FLECK_STRETCH_MIN 1.0
            #define CREST_FLECK_STRETCH_MAX 2.5
            // Slow/apex spray still gets this fixed elongation along a per-seed direction:
            // a camera-facing quad with radial alpha is a perfect circle by construction,
            // and spray hangs at ~zero velocity exactly when you look at it - the one case
            // the velocity stretch can never break up.
            #define SPRAY_IDLE_STRETCH   1.3

            // Lift surface-foam quads slightly off the water so they never z-fight it.
            #define SURFACE_LIFT         0.004
            // Particle quads follow the physical surface plane rather than the authored shading
            // strength. A value below one lets the water geometry cross and depth-cut the quad.
            #define SURFACE_NORMAL_STRENGTH 1.0

            static const float KIND_SPRAY  = 1.0;
            static const float KIND_BUBBLE = 2.0; // MUST match WaterFoamParticles.compute
            static const float KIND_RIPPLE_CREST = 3.0; // MUST match WaterFoamParticles.compute
            // Bubble look: analytic rim circle (no texture) - KWS ships bubbles as ONE static
            // sprite; generating the same look in-shader needs no asset at all.
            static const float BUBBLE_RIM_START = 0.45;      // uv radius where rim brightening begins
            static const float BUBBLE_EDGE_SOFT = 0.12;      // outer edge softness in uv radius
            static const float BUBBLE_INTERIOR_ALPHA = 0.28; // fill alpha inside the rim
            // KWS renders dynamic-wave foam flecks as an analytic, faint soft dot rather than an
            // atlas sprite. Crest flecks use that treatment only; splash spray remains textured.
            static const float CREST_FLECK_UV_CENTER = 0.5;
            static const float CREST_FLECK_RADIUS_TO_UV_SCALE = 2.0;
            static const float CREST_FLECK_FALLOFF_POWER = 2.0;
            static const float CREST_FLECK_ALPHA_GAIN = 10.0;
            static const float CREST_FLECK_ALPHA_MULTIPLIER = 0.1;
            // Depth is NOT dimmed by hand any more: the camera->bubble wet path priced below
            // carries the real per-channel extinction, which is what makes a bubble sink into the
            // water colour instead of sitting on it. A body with the fog feature OFF has no
            // extinction to carry it, so keep ONE explicit fade for that case - without it a 5 m
            // deep bubble reads as bright as one touching the surface.
            static const float BUBBLE_NO_FOG_DEPTH_FADE = 0.5; // 1/m alpha falloff, fog-off bodies only
            // Seen-from-above image: a genuinely submerged quad is Z-killed by the surface, so the
            // bubble draws its apparent image AT the waterline instead - laid IN the surface plane
            // (see the file header), which by construction cannot be cut in half and rides the
            // wave tilt for free. Its brightness is the transmitted share (Schlick,
            // FRESNEL_F0_WATER) rather than a hand-tuned ramp: grazing water really is a mirror.
            // The crossing is solved twice, because the first solve is against a HORIZONTAL plane;
            // a steep face can close over the camera at the refined xz, where the ray/plane solve
            // is meaningless, so the refine is only adopted with this much camera clearance.
            static const float WATERLINE_REFINE_CLEARANCE = 0.01; // world units above the surface
            // A disc lying in the surface plane projects to an ellipse squashed along the view
            // azimuth by exactly dot(view, normal) - it reads as a coin lying on the water, and a
            // bubble is a sphere. The image quad is pre-stretched by the inverse so the projection
            // undoes it. Capped because the inverse diverges at the horizon: past this the quad
            // would sweep long and thin across the wave and poke through it, and the Fresnel
            // transmission has already closed the sprite to a fraction by then.
            static const float BUBBLE_IMAGE_MAX_STRETCH = 6.0; // ~9.6 degrees above the local surface
            // Corner expansion + flipbook cell come from WaterParticleCommon.hlsl (shared
            // with the other particle draw shaders).

            // MUST match FoamParticle in WaterFoamParticles.compute (52 bytes).
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
                float  opacity;
            };
            StructuredBuffer<FoamParticle> _Particles;
            // A crest-only companion buffer retains a short motion history without changing the
            // shared particle record used by generic foam, spray and bubbles.
            StructuredBuffer<float3> _CrestFleckPreviousPositions;

            sampler2D _ParticleTex;
            // Which kinds this draw renders: 0 = foam + spray, 1 = floating foam only
            // (KIND_SURFACE), 2 = spray only (KIND_SPRAY), 3 = bubbles only (KIND_BUBBLE),
            // 4 = ripple crest flecks only (KIND_RIPPLE_CREST).
            // Lets the kinds draw in separate passes with their own materials/looks. Set per
            // draw by WaterFoamParticles.cs, never a material slider.
            float _DrawKind;
            // _LargeBody (1 = open water, picks the large-body glue below) comes from
            // WaterVolume.hlsl - already included; do not redeclare.
            // _SunColor comes from WaterFog.hlsl, reached TRANSITIVELY via WaterParticleFog.hlsl - declaring it here again is a redefinition.
            float _CameraUnderwater;
            float4 _Tint;
            float _ParticleOpacity;
            float _VelocityStretch;
            float _SoftFadeDistance;
            float2 _ParticleFlipbookGrid; // atlas (cols, rows); (1,1) = plain texture, no flipbook
            float _ParticleFlipbookFps;   // 0 = static per-seed variant; >0 animates the atlas over age
            sampler2D _CameraDepthTexture;

            // ---- Interactive-ripple glue (file-local BY DESIGN).
            // These live here, not in WaterVolume.hlsl, because that include is pulled in by every
            // water shader in the package - the fog marcher included. Putting foam helpers there
            // made a foam edit force a full rebuild of the heaviest variant set in the project for
            // no reason. Keep glue code next to the glue. The vertex stage carries its own copy of
            // the same fade for the same reason; the two are kept in step by the comment on each.

            // Largest ripple height (POOL units - multiples of the volume's vertical half-extent)
            // the glue will trust. Real ripples are orders of magnitude smaller; this only ever
            // fires on a corrupt texel.
            #define FOAM_RIPPLE_MAX_POOL_HEIGHT 1.0

            // Ripple height hardened for the glue. A fresh wake stamp can spike a texel, and ONE
            // bad height here turns every sprite in the draw into garbage geometry (the whole-ocean
            // "rainbow specks" regression) - a glue value feeds thousands of quads, where the
            // surface mesh would only lose a single vertex. min/max rather than isfinite: it
            // flushes NaN to the bound without adding an intrinsic to this translation unit.
            float FoamRippleHeightSafe(float rawPoolHeight)
            {
                return min(max(rawPoolHeight, -FOAM_RIPPLE_MAX_POOL_HEIGHT),
                           FOAM_RIPPLE_MAX_POOL_HEIGHT);
            }

            // Weight of the ripple at sim-window UV: 1 inside, ramping to 0 over the last
            // _SimEdgeFadeTexels, 0 outside the window. MUST match WaterSurfaceVertStage's
            // SampleRipple - the drawn surface fades its ripple to flat at the border, and past the
            // border the clamped sampler would serve the edge texel's wake across the open sea.
            // SINGLE EXIT on purpose: this package's plat-4 compile already reports "potentially
            // uninitialized variable" against multi-return helpers across a dozen files, and there
            // is no reason to add to that list for a four-line function.
            float FoamRippleWindowFade(float2 uv)
            {
                float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
                float2 edgeDist = min(uv, 1.0 - uv);
                float fade = saturate(min(edgeDist.x, edgeDist.y) / max(band, 1e-5));
                bool outsideWindow = any(uv < 0.0) || any(uv > 1.0);
                return outsideWindow ? 0.0 : fade;
            }

            // Interactive-ripple (wake) height in WORLD metres under a world xz. Open water needs
            // this because the RENDERED surface adds the same heightfield on top of the swell
            // (WaterSurfaceVertStage lifts the vertex by the faded ripple): without it the sprites
            // sat at plain swell height while the water under them rode the wake, so ZWrite cut the
            // foam out of the wake it belongs to. Sampled on the surface plane, like the vertex
            // stage - under a rotated volume a probe's own y would bleed into the window's xz.
            float RippleGlueWorldHeight(float2 worldXZ, out float2 rippleTilt)
            {
                float3 flatWorld = float3(worldXZ.x, _VolumeCenter.y, worldXZ.y);
                bool   windowed  = _SimWindowed >= 0.5;
                float2 uv   = windowed ? (WorldToSim(flatWorld).xz * 0.5 + 0.5)
                                       : (WorldToPool(flatWorld).xz * 0.5 + 0.5);
                float  fade = windowed ? FoamRippleWindowFade(uv) : 1.0;
                float4 info = SampleWaterBilinear(uv);
                // Match WaterSurfaceFragStages: info.ba is stored in SIM slope units and must be
                // converted before it can share a world-space plane with the large-body waves.
                rippleTilt = info.ba * SIM_SLOPE_TO_POOL * _SimSlopeToWorld.xy * fade;
                // fade == 0 outside the window: analytic-only water there, so no ripple. The sample
                // still runs (single exit, see FoamRippleWindowFade) and is multiplied away.
                return FoamRippleHeightSafe(info.r) * fade * VolumeExtentSafe().y;
            }

            // Short-wave height and matching normal tilt for the layers whose curvature can vary
            // across one foam quad. The long FFT/surf field stays centre-evaluated: its wavelengths
            // are large relative to a sprite, while repeating its shore/cascade work per vertex
            // would turn a small glue correction into the draw's dominant cost.
            float WindWaveWorldHeight(float2 worldXZ, float3 poolAtRest)
            {
                float2 sampleXZ = WindWaveSampleXZ(poolAtRest.xz, worldXZ);
                float windHeightPool = WaveHeight(sampleXZ);
                return
                    PoolToWorld(float3(poolAtRest.x, poolAtRest.y + windHeightPool,
                                       poolAtRest.z)).y
                    - PoolToWorld(poolAtRest).y;
            }

            float OpenWaterShortWaveHeight(float2 worldXZ)
            {
                float2 rippleTiltUnused;
                float rippleHeight = RippleGlueWorldHeight(worldXZ, rippleTiltUnused);
                float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
                return rippleHeight + WindWaveWorldHeight(worldXZ, poolAtRest);
            }

            float OpenWaterShortWaveSurface(float2 worldXZ, out float2 shortWaveTilt)
            {
                float2 rippleTilt;
                float rippleHeight = RippleGlueWorldHeight(worldXZ, rippleTilt);
                float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
                float2 sampleXZ = WindWaveSampleXZ(poolAtRest.xz, worldXZ);
                float2 windSlope = WaveSlope(sampleXZ);
                shortWaveTilt = rippleTilt - windSlope * _PoolSlopeToWorld.xy;
                return rippleHeight + WindWaveWorldHeight(worldXZ, poolAtRest);
            }

            // The animated water surface at a probe point's xz, in world space. Two bodies, one
            // contract: open water rides the FULL large-body surface (LargeBodyWaveHeight already
            // carries the swell/FFT, the near-shore shoal attenuation and the surf fronts, so foam
            // sits ON shoaling and breaking waves); a pond rides the ripple sim plus the ambient
            // wind wave. probeWorld.y only ever reaches the volume rotation - pool space is
            // re-heighted from the sim - so callers pass the stored height offset through it.
            void EvaluateWaterSurface(float3 probeWorld, out float3 surfaceWorld,
                                      out float3 surfaceNormal, out float shortWaveHeight,
                                      out float2 shortWaveTilt)
            {
                if (_LargeBody > 0.5)
                {
                    // ONE shore + surf sample feeds BOTH the height and the tilt. Calling the
                    // LargeBodyWaveHeight / OceanFftNormalTilt wrappers instead made each of them
                    // re-sample the shore, re-evaluate the surf fronts and re-run the cascade
                    // fetch - and this function is called twice per clamped bubble vertex.
                    float2 wxz = probeWorld.xz;
                    ShoreData shore = ShoreSample(wxz);
                    SurfWaveSample surf = EvaluateSurfWaves(wxz, shore.depth, shore.sdfDist,
                                                            shore.toShore, shore.slopeTan,
                                                            shore.influence, _SurfBeatTime);
                    shortWaveHeight = OpenWaterShortWaveSurface(wxz, shortWaveTilt);
                    // The WIND-WAVE layer was MISSING from this branch: the rendered surface adds
                    // it on open water too (WaterSurfaceVertStage vertex + the waterline's
                    // SurfaceHeightAtXZ), so foam quads rode ripple+swell alone and the wind chop
                    // cut straight through them. Invisible while the old periodic envelope stayed
                    // small and smooth; exposed by the stochastic sets (2026-08-10). Composed
                    // exactly like the waterline: wind height in pool units, lifted through the
                    // full volume transform, taken as a delta off the rest plane.
                    surfaceWorld = float3(wxz.x,
                                          _VolumeCenter.y + LargeBodyWaveHeightShore(wxz, shore, surf)
                                                          + shortWaveHeight,
                                          wxz.y);
                    // Match the surface fragment's base plane: the sim stores normal.xz while
                    // WaveSlope returns a height gradient, so the wind term is subtracted after
                    // both are converted to world slope. Height already included this same wind
                    // layer above; omitting its tilt lets steep chop edge-clip the in-plane quad.
                    // Start with ripple + wind, then use the canonical large-body composition.
                    // This includes FFT/analytic waves, shore attenuation and the surf-front
                    // slope; omitting any term lets the depth-writing water cross the quad.
                    float3 rippleNormal = normalize(float3(shortWaveTilt.x, 1.0,
                                                           shortWaveTilt.y));
                    surfaceNormal = ApplyLargeBodyWaveNormalShore(
                        rippleNormal, wxz, SURFACE_NORMAL_STRENGTH, shore, surf);
                }
                else
                {
                    shortWaveHeight = 0.0;
                    shortWaveTilt = float2(0.0, 0.0);
                    float3 poolPos = WorldToPool(probeWorld);
                    float2 fcoord = (_SimWindowed < 0.5) ? (poolPos.xz * 0.5 + 0.5)
                                                         : (WorldToSim(probeWorld).xz * 0.5 + 0.5);
                    float4 info = SampleWaterBilinear(fcoord);
                    // Same coordinate rule as the surface: on a world-anchored body the raw pool
                    // xz desyncs the wind-wave phase from the rendered surface.
                    poolPos.y = info.r + WaveHeight(WindWaveSampleXZ(poolPos.xz, probeWorld.xz));
                    surfaceWorld = PoolToWorld(poolPos);
                    surfaceNormal = PoolNormalToWorld(
                        float3(info.b, sqrt(max(1e-4, 1.0 - dot(info.ba, info.ba))), info.a));
                }
            }

            // Fraction of the camera->bubble ray at which it crosses a horizontal surface at
            // surfaceY: a cheap stand-in for the refracted exit point. The apparent image sits
            // there AND shrinks by the same fraction, so a sinking bubble reads as sinking from
            // above rather than merely dimming.
            float WaterlineCrossFraction(float3 cameraPos, float3 bubbleWorld, float surfaceY)
            {
                float cameraAbove = max(cameraPos.y - surfaceY, 1e-3);
                float bubbleBelow = max(surfaceY - bubbleWorld.y, 0.0);
                return cameraAbove / max(cameraAbove + bubbleBelow, 1e-3);
            }

            // Orthonormal basis lying IN the surface plane, rotated by yaw. NaN-guarded: the cross
            // degenerates when the surface normal reaches +/-Z (DEGENERATE_DIR_EPSILON, WaterShared.hlsl),
            // and either NaN would spread to the whole billboard.
            void SurfacePlaneAxes(float3 surfaceNormal, float yaw, out float3 axisX, out float3 axisY)
            {
                float3 rawFlat = cross(surfaceNormal, float3(0, 0, 1));
                if (dot(rawFlat, rawFlat) < DEGENERATE_DIR_EPSILON)
                    rawFlat = cross(surfaceNormal, float3(1, 0, 0));
                float3 flat0 = normalize(rawFlat);
                float3 flat1 = cross(surfaceNormal, flat0);
                axisX = flat0 * cos(yaw) + flat1 * sin(yaw);
                axisY = cross(surfaceNormal, axisX);
            }

            // Axes for a sprite that lies IN the surface plane but must still PROJECT as a circle.
            // axisX runs along the in-plane direction toward the camera - the one direction the
            // projection foreshortens - and the returned stretch is its inverse foreshortening,
            // clamped by BUBBLE_IMAGE_MAX_STRETCH (which also absorbs a normal tilted away from the
            // eye on a steep wave face, where the raw inverse would go negative and flip the quad).
            float SurfaceImageAxes(float3 surfaceNormal, float3 centerWorld, float yaw,
                                   out float3 axisX, out float3 axisY)
            {
                float3 toCamera = normalize(_WorldSpaceCameraPos.xyz - centerWorld);
                float steepness = dot(toCamera, surfaceNormal);
                float3 inPlane = toCamera - surfaceNormal * steepness;
                float stretch = 1.0;
                if (dot(inPlane, inPlane) < DEGENERATE_DIR_EPSILON)
                {
                    // Straight down the azimuth cancels - and there is nothing to foreshorten
                    // there either, so any in-plane yaw will do.
                    SurfacePlaneAxes(surfaceNormal, yaw, axisX, axisY);
                }
                else
                {
                    axisX = normalize(inPlane);
                    axisY = cross(surfaceNormal, axisX);
                    stretch = 1.0 / max(steepness, 1.0 / BUBBLE_IMAGE_MAX_STRETCH);
                }
                return stretch;
            }

            // Share of the light from below that gets THROUGH the interface. The surface resolves
            // its refraction from _CameraOpaqueTexture, captured before any transparent, so these
            // sprites can never appear in it - this is the only place that ratio can be applied.
            float SurfaceTransmission(float cosIncident)
            {
                float reflected = FRESNEL_F0_WATER
                                + (1.0 - FRESNEL_F0_WATER)
                                  * pow(1.0 - saturate(cosIncident), FRESNEL_SCHLICK_POWER);
                return 1.0 - reflected;
            }

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv        : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float3 litColor  : TEXCOORD2; // per-vertex foam lighting (soft blobs: no need per-pixel)
                float2 fade      : TEXCOORD3; // x = life envelope, y = fragment eye depth
                float3 worldPos  : TEXCOORD4; // for the per-fragment exclusion dissolve
                float3 fogMul    : TEXCOORD5; // camera->sprite fog transmittance (1 when fog is off)
                float3 fogAdd    : TEXCOORD6; // camera->sprite fog in-scatter (0 when fog is off)
                float sceneFogFactor : TEXCOORD7;
            };

            float SceneFogFactor(float eyeDepth)
            {
                #if defined(FOG_LINEAR)
                    return saturate(eyeDepth * unity_FogParams.z + unity_FogParams.w);
                #elif defined(FOG_EXP)
                    return saturate(exp2(-unity_FogParams.y * eyeDepth));
                #elif defined(FOG_EXP2)
                    float fogDepth = unity_FogParams.x * eyeDepth;
                    return saturate(exp2(-fogDepth * fogDepth));
                #else
                    return 1.0;
                #endif
            }

            // Degenerate output for dead slots: w = 0 collapses the triangle.
            v2f Dead()
            {
                v2f o;
                o.pos = float4(0, 0, 0, 0);
                o.uv = 0; o.screenPos = 0; o.litColor = 0; o.fade = 0; o.worldPos = 0;
                o.fogMul = 1; o.fogAdd = 0;
                o.sceneFogFactor = 1;
                return o;
            }

            v2f vert(uint vid : SV_VertexID)
            {
                FoamParticle particle = _Particles[vid / 6];
                if (particle.life <= 0.0 || particle.age >= particle.life) return Dead();
                // Kind filter (two-pass split): a foam-only pass drops spray, a spray-only pass
                // drops foam, so each can be drawn with its own material. 0 = draw both.
                bool isSpray = (particle.kind == KIND_SPRAY);
                bool isBubble = (particle.kind == KIND_BUBBLE);
                bool isRippleCrest = (particle.kind == KIND_RIPPLE_CREST);
                bool bubblePass = (_DrawKind > 2.5 && _DrawKind < 3.5);
                bool rippleCrestPass = (_DrawKind > 3.5);
                if (bubblePass != isBubble) return Dead(); // bubbles draw ONLY in their own pass
                if (rippleCrestPass != isRippleCrest) return Dead();
                if (!bubblePass && !rippleCrestPass)
                {
                    if (_DrawKind > 1.5 && !isSpray) return Dead();                  // spray-only pass
                    if (_DrawKind > 0.5 && _DrawKind < 1.5 && (isSpray || isRippleCrest)) return Dead();
                }

                float2 corner = ParticleQuadCorner(vid);

                // ---- glue the particle to the animated surface ----
                float3 surfaceWorld;
                float3 surfaceNormal;
                float shortWaveHeight;
                float2 shortWaveTilt;
                EvaluateWaterSurface(particle.worldPos, surfaceWorld, surfaceNormal,
                                     shortWaveHeight, shortWaveTilt);

                // Spray rides ABOVE the surface (offset clamped up); bubbles ride BELOW it (the
                // stored offset is negative and must pass through). EXCEPT from an above-water
                // camera, where a truly submerged quad is Z-killed and the bubble draws its
                // apparent image at the waterline instead. The ONLY thing that decides whether the
                // surface Z is in front is the camera being above the local surface - NOT the fog
                // arm state: the fog arms in a band while the camera is still in the air (gating
                // on it was v1's bug - clamp off, image Z-killed, "no bubbles").
                bool bubbleClampedToSurface = isBubble
                                              && _WorldSpaceCameraPos.y > surfaceWorld.y;
                float heightOffset = isBubble ? particle.worldPos.y : max(0.0, particle.worldPos.y);
                // The TRUE submerged position, kept whatever the image does: the wet path priced
                // for the fog is the water the bubble is ACTUALLY behind, not where it is drawn.
                float3 bubbleWorld = surfaceWorld + float3(0, particle.worldPos.y, 0);
                float3 center = surfaceWorld
                              + surfaceNormal * SURFACE_LIFT
                              + float3(0, 1, 0) * heightOffset;
                float bubbleSizeScale = 1.0;
                if (bubbleClampedToSurface)
                {
                    // Two-step crossing: the first solve is against a horizontal plane at the
                    // bubble's OWN xz, but the image lands somewhere else, where the wave height
                    // differs - on waves the flat answer floats off the surface and is then
                    // Z-killed wholesale. Re-glue at the crossing xz and solve again; the residual
                    // xz drift after one refine is below sprite size.
                    float3 cameraPos = _WorldSpaceCameraPos.xyz;
                    float3 guess = lerp(cameraPos, bubbleWorld,
                                        WaterlineCrossFraction(cameraPos, bubbleWorld, surfaceWorld.y));
                    float3 refinedSurface;
                    float3 refinedNormal;
                    float refinedShortWaveHeight;
                    float2 refinedShortWaveTilt;
                    EvaluateWaterSurface(float3(guess.x, particle.worldPos.y, guess.z),
                                         refinedSurface, refinedNormal, refinedShortWaveHeight,
                                         refinedShortWaveTilt);
                    if (cameraPos.y > refinedSurface.y + WATERLINE_REFINE_CLEARANCE)
                    {
                        surfaceWorld = refinedSurface;   // the bit of surface the image sits on:
                        surfaceNormal = refinedNormal;   // its tilt orients and lights the quad
                    }
                    float crossFraction = WaterlineCrossFraction(cameraPos, bubbleWorld, surfaceWorld.y);
                    center = lerp(cameraPos, bubbleWorld, crossFraction) + surfaceNormal * SURFACE_LIFT;
                    bubbleSizeScale = crossFraction; // keep the TRUE angular size at the nearer image
                }

                // ---- quad axes ----
                float3 axisX, axisY;
                float stretch = 1.0;
                float speed = length(particle.velocity);
                if (isBubble)
                {
                    if (bubbleClampedToSurface)
                    {
                        // The apparent image lies IN the surface plane, so it cannot straddle the
                        // waterline and cannot be cut in half (file header) - but a disc in that
                        // plane also projects as an ellipse, which reads as a coin lying flat on the
                        // water. The pre-stretch cancels that projection: a bubble is a sphere and
                        // must read round from every angle.
                        stretch = SurfaceImageAxes(surfaceNormal, center,
                                                   particle.seed * PARTICLE_TWO_PI, axisX, axisY);
                    }
                    else
                    {
                        // Submerged view: camera-facing, unstretched - a bubble is a sphere from
                        // anywhere, and nothing is between it and the eye to cut it.
                        axisX = UNITY_MATRIX_V[0].xyz;
                        axisY = UNITY_MATRIX_V[1].xyz;
                    }
                }
                else if (particle.kind == KIND_SPRAY)
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
                else if (isRippleCrest)
                {
                    // KWS renders dynamic-wave foam as a camera-facing motion ribbon. Position
                    // history is intentionally used instead of an instantaneous velocity axis so
                    // a few advected flecks connect into a trail at normal frame rates.
                    float3 motion = particle.worldPos - _CrestFleckPreviousPositions[vid / 6];
                    float3 camRight = UNITY_MATRIX_V[0].xyz;
                    float3 camUp = UNITY_MATRIX_V[1].xyz;
                    float2 motionScreen = float2(dot(motion, camRight), dot(motion, camUp));
                    float motionScreenLength = length(motionScreen);
                    if (motionScreenLength > DEGENERATE_DIR_EPSILON)
                    {
                        float2 motionDirection = motionScreen / motionScreenLength;
                        axisX = camRight * motionDirection.x + camUp * motionDirection.y;
                        axisY = camRight * (-motionDirection.y) + camUp * motionDirection.x;
                        float velocitySizeFactor = saturate(length(particle.velocity.xz) * 0.3);
                        stretch = lerp(CREST_FLECK_STRETCH_MIN, CREST_FLECK_STRETCH_MAX,
                                       velocitySizeFactor);
                    }
                    else
                    {
                        SurfacePlaneAxes(surfaceNormal, particle.seed * PARTICLE_TWO_PI, axisX, axisY);
                    }
                }
                else
                {
                    // in the surface plane: seed yaw, stretched along the drift direction.
                    SurfacePlaneAxes(surfaceNormal, particle.seed * PARTICLE_TWO_PI, axisX, axisY);
                    if (speed > STRETCH_MIN_SPEED)
                    {
                        // NaN-guarded like the basis itself: the projected velocity cancels when
                        // the drift is parallel to the normal (extreme wave tilt).
                        float3 planar = particle.velocity - surfaceNormal * dot(particle.velocity, surfaceNormal);
                        if (dot(planar, planar) >= DEGENERATE_DIR_EPSILON)
                        {
                            axisX = normalize(planar);
                            axisY = cross(surfaceNormal, axisX);
                            stretch = 1.0 + min(STRETCH_MAX, speed * _VelocityStretch);
                        }
                    }
                }

                float sizeWorld = particle.size * bubbleSizeScale; // 1 for foam/spray
                float3 worldVertex = center
                                   + axisX * (corner.x * sizeWorld * stretch)
                                   + axisY * (corner.y * sizeWorld);
                if (!isSpray && !isBubble && !isRippleCrest && _LargeBody > 0.5)
                {
                    // The centre tangent already predicts the linear part of ripple + wind tilt.
                    // Add only the nonlinear corner residual; adding the full height delta would
                    // count the slope twice and bend an otherwise planar wave into a false ridge.
                    float2 cornerOffset = worldVertex.xz - surfaceWorld.xz;
                    float predictedShortWaveDelta = -dot(shortWaveTilt, cornerOffset);
                    float cornerShortWaveHeight = OpenWaterShortWaveHeight(worldVertex.xz);
                    float actualShortWaveDelta = cornerShortWaveHeight - shortWaveHeight;
                    worldVertex.y += actualShortWaveDelta - predictedShortWaveDelta;
                }

                // ---- life envelope ----
                float envelope = FoamParticleEnvelope(particle.age, particle.life)
                               * particle.strength * particle.opacity;
                if (bubbleClampedToSurface)
                {
                    // Only the transmitted share reaches an above-water camera; at grazing angles
                    // the surface is a mirror and the image is simply gone.
                    float3 toCamera = normalize(_WorldSpaceCameraPos.xyz - center);
                    envelope *= SurfaceTransmission(dot(toCamera, surfaceNormal));
                }
                // Fog-off bodies only: see BUBBLE_NO_FOG_DEPTH_FADE.
                if (isBubble && _WaterFogEnabled < 0.5)
                    envelope *= exp(BUBBLE_NO_FOG_DEPTH_FADE * min(0.0, particle.worldPos.y));

                // ---- sprite cell from the atlas: a fixed per-seed variant, or an animated flipbook
                // (foam churn) when _ParticleFlipbookFps > 0 (shared math, WaterParticleCommon.hlsl) ----
                // Bubbles skip the atlas: the frag draws them analytically from the raw corner.
                float2 uv = isBubble
                    ? corner * 0.5 + 0.5
                    : ParticleFlipbookUv(corner, _ParticleFlipbookGrid.xy,
                                         particle.seed, particle.age, _ParticleFlipbookFps);

                // ---- lighting, matched to the surface foam ----
                float wrapped = FoamWrappedDiffuse(surfaceNormal, _LightDir);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldVertex, 1.0));
                o.uv = uv;
                o.screenPos = ComputeScreenPos(o.pos);
                o.litColor = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                float eyeDepth = -mul(UNITY_MATRIX_V, float4(worldVertex, 1.0)).z;
                o.fade = float2(envelope, eyeDepth);
                o.sceneFogFactor = SceneFogFactor(eyeDepth);
                o.worldPos = worldVertex;
                // After-fog reroute frames (WaterParticleFog.hlsl): the fullscreen fog no longer
                // paints this sprite, so price the camera->sprite wet path here. Identity
                // mul/add whenever the fog is off - the queue-time look is untouched.
                float3 fogLightDir = normalize(_LightDir + 1e-5);
                // Into LOCALS, then onto o: passing o.fogMul/o.fogAdd straight in as out-params made
                // the compiler treat the whole partially-written v2f as an aggregate copy-in/copy-out
                // and report "potentially uninitialized variable (o)" on plat 4.
                float3 fogMul;
                float3 fogAdd;
                if (isBubble)
                {
                    // A bubble is submerged BY DEFINITION and is drawn at its apparent image, so
                    // the armed gate and the drawn position both lied about it: the gate returned
                    // identity for every above-water camera (no water tint at all - "particles
                    // don't blend with the fog"), and the image sits exactly ON the waterline, so
                    // even an ungated call would have measured a zero wet path. Price the TRUE
                    // position against the bubble's OWN local waterline instead.
                    ParticleUnderwaterFogAtLevel(bubbleWorld, surfaceWorld.y, fogLightDir,
                                                 _SunColor, fogMul, fogAdd);
                }
                else
                {
                    // Against the sprite's OWN local waterline, not the camera-xz flat one: foam is
                    // glued to that surface and spray is thrown from it, so on waves the flat level
                    // is a different height entirely and dry spray over a trough came out
                    // fog-coloured. surfaceWorld.y is already the glue's answer - no extra sample.
                    ParticleUnderwaterFogArmedAtLevel(worldVertex, surfaceWorld.y, fogLightDir,
                                                      _SunColor, fogMul, fogAdd);
                }
                o.fogMul = fogMul;
                o.fogAdd = fogAdd;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float envelope = i.fade.x;
                float4 sprite;
                float alpha;
                if (_DrawKind > 2.5 && _DrawKind < 3.5)
                {
                    // Bubble pass: analytic rim circle - bright rim, dim interior, soft outer
                    // edge. No texture fetch; the erosion lace is a foam-texture concept and
                    // does not apply.
                    float radial = length(i.uv * 2.0 - 1.0);
                    float circle = 1.0 - smoothstep(1.0 - BUBBLE_EDGE_SOFT, 1.0, radial);
                    float rim = smoothstep(BUBBLE_RIM_START, 1.0 - BUBBLE_EDGE_SOFT, radial);
                    sprite = float4(1.0, 1.0, 1.0, 1.0);
                    alpha = circle * lerp(BUBBLE_INTERIOR_ALPHA, 1.0, rim)
                          * envelope * _ParticleOpacity;
                }
                else if (_DrawKind > 3.5)
                {
                    float radialAlpha = saturate(1.0 - length(i.uv - CREST_FLECK_UV_CENTER)
                                                 * CREST_FLECK_RADIUS_TO_UV_SCALE);
                    sprite = float4(1.0, 1.0, 1.0, 1.0);
                    alpha = saturate(pow(radialAlpha, CREST_FLECK_FALLOFF_POWER)
                                     * CREST_FLECK_ALPHA_GAIN)
                          * CREST_FLECK_ALPHA_MULTIPLIER * envelope * _ParticleOpacity;
                }
                else
                {
                    // Negative mip bias keeps the lace from averaging into a round blob at
                    // distance (FOAM_SPRITE_MIP_BIAS, shared foam-look constant).
                    sprite = tex2Dbias(_ParticleTex, float4(i.uv, 0.0, FOAM_SPRITE_MIP_BIAS));

                    // Texture-preserving erosion: fresh sprites show their own lace, dying ones
                    // crumble through it (the old gate-only form saturated the interior into a
                    // solid disc - the "round semi-transparent spheres").
                    alpha = FoamErosionLace(sprite.a, envelope);
                    alpha *= envelope * _ParticleOpacity;
                }

                // soft fade against the opaque scene (pool walls, floating objects)
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                alpha *= saturate((sceneEye - i.fade.y) / _SoftFadeDistance);

                // Dry-interior exclusion: the render-side guarantee on top of the compute's
                // age-boost dissolve - the parts of a sprite protruding into a dry volume
                // dissolve over the volume's fade band NOW, not over the particle's lifetime.
                if (_ExclusionCount > 0.5)
                    alpha *= ExclusionParticleAttenuation(i.worldPos);

                // Per-sprite underwater fog (identity on fog-off frames): applied after the
                // texture multiply, exact because the fog lerp is linear in the color.
                float3 rgb = i.litColor * sprite.rgb * i.fogMul + i.fogAdd;
                if (_CameraUnderwater < 0.5)
                    rgb = lerp(unity_FogColor.rgb, rgb, i.sceneFogFactor);

                return fixed4(rgb, alpha);
            }
            ENDCG
        }
    }
}
