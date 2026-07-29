// WebGpuWater - GPU foam particle rendering (KWS-inspired)
//
// Draws the particle pool written by WaterFoamParticles.compute as procedural quads:
// the vertex shader pulls a FoamParticle from a StructuredBuffer by SV_VertexID
// (6 vertices per particle), so there is no mesh, no instancing path and no geometry
// shader - the one expansion technique that works everywhere WebGPU does.
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
            // Slow/apex spray still gets this fixed elongation along a per-seed direction:
            // a camera-facing quad with radial alpha is a perfect circle by construction,
            // and spray hangs at ~zero velocity exactly when you look at it - the one case
            // the velocity stretch can never break up.
            #define SPRAY_IDLE_STRETCH   1.3

            // Lift surface-foam quads slightly off the water so they never z-fight it.
            #define SURFACE_LIFT         0.004

            static const float KIND_SPRAY = 1.0;
            // Corner expansion + flipbook cell come from WaterParticleCommon.hlsl (shared
            // with the other particle draw shaders).

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
            // _SunColor comes from WaterFog.hlsl, reached TRANSITIVELY via WaterParticleFog.hlsl - declaring it here again is a redefinition.
            float4 _Tint;
            float _ParticleOpacity;
            float _VelocityStretch;
            float _SoftFadeDistance;
            float2 _ParticleFlipbookGrid; // atlas (cols, rows); (1,1) = plain texture, no flipbook
            float _ParticleFlipbookFps;   // 0 = static per-seed variant; >0 animates the atlas over age
            sampler2D _CameraDepthTexture;

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
            };

            // Degenerate output for dead slots: w = 0 collapses the triangle.
            v2f Dead()
            {
                v2f o;
                o.pos = float4(0, 0, 0, 0);
                o.uv = 0; o.screenPos = 0; o.litColor = 0; o.fade = 0; o.worldPos = 0;
                o.fogMul = 1; o.fogAdd = 0;
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
                float3 surfaceWorld;
                float3 surfaceNormal;
                if (_LargeBody > 0.5)
                {
                    // Open water (FFT or analytic): ride the FULL large-body surface -
                    // LargeBodyWaveHeight internally carries the swell/FFT, the near-shore shoal
                    // attenuation, the ambient fade under the surf fronts AND the fronts
                    // themselves, so foam sits ON the shoaling/breaking waves. Gating this on
                    // _OceanFftActive only (the original) dropped every analytic ocean to the
                    // pond path: particles ignored the shoal and the shore waves entirely.
                    // (Interactive ripples aren't in this glue - the same trade the FFT path
                    // always made.) The pond path (else) is byte-for-byte unchanged.
                    float2 wxz = particle.worldPos.xz;
                    surfaceWorld = float3(wxz.x, _VolumeCenter.y + LargeBodyWaveHeight(wxz), wxz.y);
                    float2 tilt = OceanFftNormalTilt(wxz); // 0 tilt when FFT is off (flat lean)
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
                            stretch = 1.0 + min(STRETCH_MAX, speed * _VelocityStretch);
                        }
                    }
                    axisY = cross(surfaceNormal, axisX);
                }

                float3 worldVertex = center
                                   + axisX * (corner.x * particle.size * stretch)
                                   + axisY * (corner.y * particle.size);

                // ---- life envelope ----
                float envelope = FoamParticleEnvelope(particle.age, particle.life) * particle.strength;

                // ---- sprite cell from the atlas: a fixed per-seed variant, or an animated flipbook
                // (foam churn) when _ParticleFlipbookFps > 0 (shared math, WaterParticleCommon.hlsl) ----
                float2 uv = ParticleFlipbookUv(corner, _ParticleFlipbookGrid.xy,
                                               particle.seed, particle.age, _ParticleFlipbookFps);

                // ---- lighting, matched to the surface foam ----
                float wrapped = FoamWrappedDiffuse(surfaceNormal, _LightDir);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldVertex, 1.0));
                o.uv = uv;
                o.screenPos = ComputeScreenPos(o.pos);
                o.litColor = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                o.fade = float2(envelope, -mul(UNITY_MATRIX_V, float4(worldVertex, 1.0)).z);
                o.worldPos = worldVertex;
                // After-fog reroute frames (WaterParticleFog.hlsl): the fullscreen fog no longer
                // paints this sprite, so price the camera->sprite wet path here. Identity
                // mul/add whenever the fog is off - the queue-time look is untouched.
                ParticleUnderwaterFog(worldVertex, normalize(_LightDir + 1e-5), _SunColor,
                                      o.fogMul, o.fogAdd);
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

                // Dry-interior exclusion: the render-side guarantee on top of the compute's
                // age-boost dissolve - the parts of a sprite protruding into a dry volume
                // dissolve over the volume's fade band NOW, not over the particle's lifetime.
                if (_ExclusionCount > 0.5)
                    alpha *= ExclusionParticleAttenuation(i.worldPos);

                // Per-sprite underwater fog (identity on fog-off frames): applied after the
                // texture multiply, exact because the fog lerp is linear in the color.
                float3 rgb = i.litColor * sprite.rgb * i.fogMul + i.fogAdd;

                return fixed4(rgb, alpha);
            }
            ENDCG
        }
    }
}
