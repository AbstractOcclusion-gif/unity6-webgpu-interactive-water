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

            // Bed shoaling (globals; this shader doesn't include WaterFog). Lets foam spray sit on the
            // shoaled surface near shore instead of the full-height swell.
            sampler2D _BedTex;
            float _BedValid;
            float _UseBedDepth;
            float _SwellShoalDepth;
            float _SwellShoalStrength;

            // Swell shoaling at a WORLD xz. KEEP IN SYNC with WaterSurface / WaterUnderwaterFog /
            // LargeBodyCaustics and LargeWaveField (CPU).
            float SwellShoalFactor(float2 worldXZ)
            {
                if (_UseBedDepth < 0.5 || _BedValid < 0.5) return 1.0;
                float2 bedUV = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y)).xz * 0.5 + 0.5;
                float bedPoolY = tex2Dlod(_BedTex, float4(bedUV, 0, 0)).r;
                float stillDepth = max(0.0, -bedPoolY * VolumeExtentSafe().y);
                float t = saturate(stillDepth / max(_SwellShoalDepth, 1e-3));
                return lerp(1.0 - _SwellShoalStrength, 1.0, t);
            }

            // Atlas layout is a uniform now (_ParticleFlipbookGrid): (1,1) = a plain non-atlas texture,
            // (2,2) etc. = a flipbook. Optional, like the surface foam's _FoamTexFrames.

            // Life envelope: quick fade-in, erosion-driven fade-out beginning at this
            // fraction of the particle's life.
            #define FADE_IN_SECONDS      0.25
            #define FADE_OUT_START       0.55
            // As the envelope decays the sprite ERODES (thin regions drop out first)
            // instead of uniformly ghosting; same trick as the surface lace.
            #define EROSION_SOFTNESS     0.35

            // Foam lighting, matched to the surface foam so particles sit in the same light.
            #define FOAM_LIGHT_WRAP      0.4
            #define FOAM_AMBIENT         0.35

            // Below this speed a quad is not stretched (avoids jitter around zero).
            #define STRETCH_MIN_SPEED    0.02
            #define STRETCH_MAX          4.0

            // Lift surface-foam quads slightly off the water so they never z-fight it.
            #define SURFACE_LIFT         0.004

            static const float KIND_SPRAY = 1.0;
            static const float2 QUAD_CORNERS[4] =
                { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1) };
            static const uint QUAD_INDICES[6] = { 0, 1, 2, 2, 1, 3 };

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

                float2 corner = QUAD_CORNERS[QUAD_INDICES[vid % 6]];

                // ---- glue the particle to the animated surface ----
                float3 surfaceWorld;
                float3 surfaceNormal;
                if (_OceanFftActive > 0.5)
                {
                    // Ocean: ride the FFT swell/chop crest (sea level + FFT height, leaned by the cascade
                    // tilt) so foam sits ON the breaking wave, not on the flat rest plane. The pond path
                    // (else) is byte-for-byte unchanged.
                    float2 wxz = particle.worldPos.xz;
                    surfaceWorld = float3(wxz.x, _VolumeCenter.y + LargeBodyWaveHeight(wxz) * SwellShoalFactor(wxz), wxz.y);
                    float2 tilt = OceanFftNormalTilt(wxz);
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
                    axisX = camRight; axisY = camUp;
                    float2 vScreen = float2(dot(particle.velocity, camRight),
                                            dot(particle.velocity, camUp));
                    float vLen = length(vScreen);
                    if (speed > STRETCH_MIN_SPEED && vLen > 1e-4)
                    {
                        float2 d = vScreen / vLen;
                        axisX = camRight * d.x + camUp * d.y;
                        axisY = camRight * (-d.y) + camUp * d.x;
                        stretch = 1.0 + min(STRETCH_MAX, speed * _VelocityStretch);
                    }
                }
                else
                {
                    // in the surface plane: seed yaw, stretched along the drift direction
                    float yaw = particle.seed * 6.2831853;
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
                float fadeIn = saturate(particle.age / FADE_IN_SECONDS);
                float fadeOut = 1.0 - smoothstep(particle.life * FADE_OUT_START, particle.life, particle.age);
                float envelope = fadeIn * fadeOut * particle.strength;

                // ---- sprite cell from the atlas: a fixed per-seed variant, or an animated flipbook (foam
                // churn) when _ParticleFlipbookFps > 0. The seed offsets each particle's phase so they never
                // flip in lockstep. Grid (1,1) = a plain texture (no cells, no animation); fps = 0 = the
                // original static variant. ----
                float2 grid = max(float2(1.0, 1.0), _ParticleFlipbookGrid.xy);
                float cellCount = grid.x * grid.y;
                float framePos = particle.seed * cellCount + particle.age * _ParticleFlipbookFps;
                float variant = fmod(floor(framePos), cellCount);
                float2 cell = float2(fmod(variant, grid.x), floor(variant / grid.x));
                float2 uv = (corner * 0.5 + 0.5 + cell) / grid;

                // ---- lighting, matched to the surface foam ----
                float wrapped = saturate(dot(surfaceNormal, _LightDir) * (1.0 - FOAM_LIGHT_WRAP) + FOAM_LIGHT_WRAP);

                v2f o;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldVertex, 1.0));
                o.uv = uv;
                o.screenPos = ComputeScreenPos(o.pos);
                o.litColor = _Tint.rgb * (FOAM_AMBIENT + _SunColor * wrapped);
                o.fade = float2(envelope, -mul(UNITY_MATRIX_V, float4(worldVertex, 1.0)).z);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 sprite = tex2D(_ParticleTex, i.uv);
                float envelope = i.fade.x;

                // erosion fade: the sprite's thin regions dissolve first as the envelope decays
                float alpha = saturate((sprite.a - (1.0 - envelope)) / EROSION_SOFTNESS);
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
