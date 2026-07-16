// WebGL Water - GPU foam particle rendering (KWS-inspired)
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
            // 0 when the screen-space density pass renders the floating foam (this draw then only
            // shows KIND_SPRAY droplets); 1 = classic quads for everything. Set per draw by
            // WaterFoamParticles.cs, never a material slider.
            float _SurfaceQuadsEnabled;
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
                // Density mode: floating foam is rendered by the screen-space density composite;
                // this draw keeps only the ballistic spray as textured billboards.
                if (particle.kind != KIND_SPRAY && _SurfaceQuadsEnabled < 0.5) return Dead();

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
                    // in the surface plane: seed yaw, stretched along the drift direction
                    float yaw = particle.seed * PARTICLE_TWO_PI;
                    float3 flat0 = normalize(cross(surfaceNormal, float3(0, 0, 1)));
                    float3 flat1 = cross(surfaceNormal, flat0);
                    axisX = flat0 * cos(yaw) + flat1 * sin(yaw);
                    if (speed > STRETCH_MIN_SPEED)
                    {
                        axisX = normalize(particle.velocity - surfaceNormal * dot(particle.velocity, surfaceNormal));
                        stretch = 1.0 + min(STRETCH_MAX, speed * _VelocityStretch);
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
