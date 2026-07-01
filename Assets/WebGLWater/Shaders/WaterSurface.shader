// WebGL Water - water surface (Unity 6 / URP port)
// Hybrid reflection (analytic sky/pool -> planar -> SSR) and refraction (analytic
// pool, or real screen-space refraction of the live scene). All extras are
// keyword-gated and default off, so the base look matches the original.
// One material is instanced twice by the scene builder: an "above water" object
// (_Underwater = 0, Cull Front) and an "under water" object (_Underwater = 1,
// Cull Back), sharing the same displaced grid mesh.
Shader "WebGLWater/WaterSurface"
{
    Properties
    {
        _Underwater ("Underwater (0/1)", Float) = 0
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 1 // Front
        _ReflectionStrength ("Reflection Strength", Range(0,1)) = 1.0

        [Header(Hybrid Reflections)]
        [Toggle(_USE_PLANAR)] _UsePlanar ("Use Planar Reflection", Float) = 0
        [Toggle(_USE_SSR)]    _UseSSR    ("Use Screen Space Reflection", Float) = 0
        _ReflectionDistortion ("Reflection Distortion", Range(0,0.2)) = 0.05
        _SSRStrength  ("SSR Strength", Range(0,1)) = 1.0
        _SSRStepSize  ("SSR Step Size (world units)", Range(0.005,0.2)) = 0.03
        _SSRMaxSteps  ("SSR Max Steps", Range(8,64)) = 24
        _SSRThickness ("SSR Thickness", Range(0.01,1.0)) = 0.2

        [Header(Real Transparency)]
        [Toggle(_REAL_REFRACTION)] _RealRefraction ("Real (Screen-Space) Refraction", Float) = 0
        _RefractionDistortion ("Refraction Distortion", Range(0,0.2)) = 0.05
        // Water fog is global now (driven by WaterController), shared with the
        // object/pool shaders so it's consistent however you view the water.

        [Header(Foam)]
        _FoamTex ("Foam Pattern (optional)", 2D) = "white" {}
    }
    SubShader
    {
        // Transparent queue so _CameraOpaqueTexture / _CameraDepthTexture hold the
        // scene WITHOUT the water (required for SSR and screen-space refraction).
        // Still ZWrite On + Blend Off: we compute the final opaque-looking colour
        // ourselves (incl. refraction), we just need to draw after the opaque copy.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma shader_feature_local _USE_PLANAR
            #pragma shader_feature_local _USE_SSR
            #pragma shader_feature_local _REAL_REFRACTION
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl" // brings WaterShared (via WaterCommon): POOL_RIM_HEIGHT etc.

            // Look constants local to this surface pass (single-use here).
            #define SUN_GLINT_TINT          float3(10.0, 8.0, 6.0)
            #define SUN_GLINT_SHARPNESS     5000.0
            #define UNDERWATER_REFRACT_TINT float3(0.8, 1.0, 1.1)
            #define FRESNEL_POWER           3.0
            #define FRESNEL_MIN_ABOVE       0.25
            #define FRESNEL_MIN_BELOW       0.5

            float _Underwater;
            float _ReflectionStrength;
            float _WaveNormalStrength; // global; scales the wind-wave tilt on the normal
            float3 _SunColor; // Unity directional light color * intensity (global)

            sampler2D _PlanarReflectionTex;
            float     _ReflectionDistortion;

            // URP scene textures (enable Opaque Texture + Depth Texture in the URP asset)
            sampler2D _CameraOpaqueTexture;
            sampler2D _CameraDepthTexture;

            float _SSRStrength, _SSRStepSize, _SSRMaxSteps, _SSRThickness;
            float _RefractionDistortion;

            // Foam: _FoamMask (sim buffer) + globals from the controller; _FoamTex
            // is an optional per-material pattern (defaults white = flat foam).
            sampler2D _FoamMask;
            sampler2D _FoamTex;
            float4 _FoamTex_ST;
            float4 _FoamColor;
            float _FoamEnabled, _FoamStrength, _FoamBorderWidth, _FoamContactDepth;

            // Screen-space ray march along 'dir' from world 'p0'. On a depth hit it
            // returns the scene colour and sets hit=1; otherwise hit=0 (caller falls
            // back to planar / analytic). Kept deliberately simple + linear; tune the
            // step size / thickness in the material.
            float3 MarchSSR(float3 p0, float3 dir, out float hit)
            {
                hit = 0.0;
                float3 p = p0;
                int maxSteps = (int)_SSRMaxSteps;
                [loop]
                for (int s = 0; s < maxSteps; s++)
                {
                    p += dir * _SSRStepSize;
                    float4 clip = mul(UNITY_MATRIX_VP, float4(p, 1.0));
                    if (clip.w <= 0.0) break;
                    // Platform-correct screen UV (handles the WebGPU/GL vs D3D V-flip),
                    // matching the refraction / planar paths that use ComputeScreenPos.
                    // A hand-rolled clip.xy/clip.w*0.5+0.5 samples the mirrored row in a
                    // build and makes SSR reflections look screen-locked.
                    float4 sp = ComputeScreenPos(clip);
                    float2 uv = sp.xy / max(sp.w, 1e-5);
                    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

                    // explicit-LOD samples: safe inside a divergent loop (WebGPU)
                    float sceneDepth = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0, 0)));
                    float rayDepth   = -mul(UNITY_MATRIX_V, float4(p, 1.0)).z; // positive eye depth
                    if (rayDepth > sceneDepth && (rayDepth - sceneDepth) < _SSRThickness)
                    {
                        hit = 1.0;
                        return tex2Dlod(_CameraOpaqueTexture, float4(uv, 0, 0)).rgb;
                    }
                }
                return 0.0;
            }

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0; // POOL space ([-1,1]); drives the analytic tracer
                float4 screenPos: TEXCOORD1;
                float3 worldPos : TEXCOORD2; // world space; drives depth/SSR/foam-contact
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 info = tex2Dlod(_WaterTex, float4(v.vertex.xy * 0.5 + 0.5, 0, 0));
                float3 position = v.vertex.xzy;   // grid XY plane -> pool (x, 0, z)
                position.y += info.r;                  // interactive ripple heightfield
                position.y += WaveHeight(v.vertex.xy); // ambient wind-wave layer (pool xz = vertex.xy)
                o.position = position;                 // keep pool-space position for the tracer
                float3 worldPos = PoolToWorld(position);
                o.worldPos = worldPos;
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos, 1.0));
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // Sample the planar reflection RT at the fragment's screen UV, nudged
            // by the surface normal so ripples wobble the mirror image.
            float3 SamplePlanarReflection(float4 screenPos, float3 normal)
            {
                float2 uv = screenPos.xy / max(screenPos.w, 1e-5);
                uv += normal.xz * _ReflectionDistortion;
                return tex2D(_PlanarReflectionTex, saturate(uv)).rgb;
            }

            // Shade a WORLD-space ray (origin + direction) against the analytic pool/sky.
            // The pool box is intersected in POOL space (where it is the unit box), so any
            // volume rotation or non-uniform extent is handled exactly, while the sky
            // lookup and sun glint use the WORLD ray - which keeps reflections/refraction
            // angle-correct for rotated or rectangular volumes.
            float3 getSurfaceRayColor(float3 worldOrigin, float3 worldRay, float3 waterColor)
            {
                float3 po = WorldToPool(worldOrigin);
                float3 pd = WorldDirToPool(worldRay);
                float3 color;
                if (worldRay.y < 0.0)
                {
                    float2 t = IntersectCube(po, pd, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                    color = GetWallColor(po + pd * t.y);
                }
                else
                {
                    float2 t = IntersectCube(po, pd, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                    float3 hit = po + pd * t.y;
                    if (hit.y < POOL_RIM_HEIGHT)
                    {
                        color = GetWallColor(hit);
                    }
                    else
                    {
                        color = texCUBE(_Sky, worldRay).rgb;
                        // sun glint - direction from _LightDir, tint/brightness from the Unity sun
                        color += SUN_GLINT_TINT * _SunColor * pow(max(0.0, dot(_LightDir, worldRay)), SUN_GLINT_SHARPNESS);
                    }
                }
                if (worldRay.y < 0.0) color *= waterColor;
                return color;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 coord = i.position.xz * 0.5 + 0.5;
                float4 info = tex2D(_WaterTex, coord);

                // make the water look more "peaked"
                [unroll]
                for (int k = 0; k < 5; k++)
                {
                    coord += info.ba * 0.005;
                    info = tex2D(_WaterTex, coord);
                }

                // Combine the ripple normal (info.ba = normal.xz) with the wind-wave
                // tilt. A height gradient g contributes normal.xz = -g, so the two
                // slopes simply add in the xz components before re-deriving y.
                float2 nxz = info.ba - WaveSlope(i.position.xz) * _WaveNormalStrength;
                float3 normalPool = float3(nxz.x, sqrt(max(1e-4, 1.0 - dot(nxz, nxz))), nxz.y);
                // World-space surface normal + view ray, so reflection/refraction angles
                // are correct even when the volume is rotated or has a rectangular footprint.
                float3 normal = PoolNormalToWorld(normalPool);
                float3 incomingRay = normalize(i.worldPos - _WorldSpaceCameraPos);

                if (_Underwater > 0.5)
                {
                    normal = -normal;
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_WATER / IOR_AIR);
                    float fresnel = lerp(FRESNEL_MIN_BELOW, 1.0, pow(1.0 - dot(normal, -incomingRay), FRESNEL_POWER));

                    float3 reflectedColor = getSurfaceRayColor(i.worldPos, reflectedRay, UNDERWATER_COLOR);
                    float3 refractedColor = getSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0)) * UNDERWATER_REFRACT_TINT;

                    // Real transparency from below: sample the live scene above the surface.
                #if defined(_REAL_REFRACTION)
                    float2 ruvU = i.screenPos.xy / max(i.screenPos.w, 1e-5) + normal.xz * _RefractionDistortion;
                    refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruvU)).rgb * UNDERWATER_REFRACT_TINT;
                #endif

                    float tUnder = (1.0 - fresnel) * length(refractedRay);
                    tUnder = lerp(1.0, tUnder, _ReflectionStrength); // strength 0 = fully refracted
                    return float4(lerp(reflectedColor, refractedColor, tUnder), 1.0);
                }
                else
                {
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_AIR / IOR_WATER);
                    float fresnel = lerp(FRESNEL_MIN_ABOVE, 1.0, pow(1.0 - dot(normal, -incomingRay), FRESNEL_POWER));

                    float3 reflectedColor = getSurfaceRayColor(i.worldPos, reflectedRay, ABOVEWATER_COLOR);
                    float3 refractedColor = getSurfaceRayColor(i.worldPos, refractedRay, ABOVEWATER_COLOR);

                    // ---- Reflection: analytic -> planar -> SSR (SSR wins where it hits) ----
                #if defined(_USE_PLANAR)
                    reflectedColor = SamplePlanarReflection(i.screenPos, normal);
                #endif
                #if defined(_USE_SSR)
                    float ssrHit;
                    float3 ssr = MarchSSR(i.worldPos, reflectedRay, ssrHit); // SSR marches in world space
                    reflectedColor = lerp(reflectedColor, ssr, ssrHit * _SSRStrength);
                #endif

                    // ---- Real transparency: sample the actual scene behind the surface,
                    // instead of the analytic pool. Depth fog is applied just below. ----
                #if defined(_REAL_REFRACTION)
                    float2 ruv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                    ruv += normal.xz * _RefractionDistortion;
                    refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruv)).rgb * ABOVEWATER_COLOR;
                #endif

                    // ---- Water fog. With REAL refraction the sampled scene is already
                    // fogged by the geometry shaders, so we only fog the ANALYTIC pool
                    // here - avoids double-fogging what's seen through the surface. ----
                #if !defined(_REAL_REFRACTION)
                    // Fog distance is the WORLD length of the refracted segment through the
                    // pool, found by intersecting the unit box in pool space then measuring
                    // the world chord (correct under non-uniform extent / rotation).
                    float3 pdFog = WorldDirToPool(refractedRay);
                    float2 tfog = IntersectCube(i.position, pdFog, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                    float3 exitWorld = PoolToWorld(i.position + pdFog * max(0.0, tfog.y));
                    refractedColor = ApplyWaterFog(refractedColor, length(exitWorld - i.worldPos));
                #endif

                    float3 outColor = lerp(refractedColor, reflectedColor, fresnel * _ReflectionStrength);

                    // ---- Foam: advected buffer + shoreline border + waterline contact ----
                    if (_FoamEnabled > 0.5)
                    {
                        float2 fcoord = i.position.xz * 0.5 + 0.5;
                        float advected = tex2D(_FoamMask, fcoord).r;

                        // shoreline foam against the pool walls
                        float edge = min(1.0 - abs(i.position.x), 1.0 - abs(i.position.z));
                        float border = 1.0 - smoothstep(0.0, _FoamBorderWidth, edge);

                        // contact foam where geometry pierces the waterline. Needs the
                        // depth texture; when it's unavailable (or uses a different Z
                        // convention in a build) the sample can resolve in FRONT of the
                        // surface, which the old formula turned into full-surface foam.
                        // Guard: only add contact foam where the scene is genuinely just
                        // BEHIND the surface, else none. Fixes "all water foamed" builds.
                        float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                        float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                        float surfEye  = -mul(UNITY_MATRIX_V, float4(i.worldPos, 1.0)).z;
                        float behind   = sceneEye - surfEye; // > 0 when scene sits below the surface
                        float contact  = behind > 0.0 ? (1.0 - saturate(behind / max(_FoamContactDepth, 1e-4))) : 0.0;

                        float mask = saturate((advected + border + contact) * _FoamStrength);

                        // appearance: optional tiling pattern, nudged by the ripple normal
                        float2 fuv = fcoord * _FoamTex_ST.xy + _FoamTex_ST.zw + normal.xz * 0.1;
                        float3 foamLook = _FoamColor.rgb * tex2D(_FoamTex, fuv).rgb;
                        outColor = lerp(outColor, foamLook, mask);
                    }

                    return float4(outColor, 1.0);
                }
            }
            ENDCG
        }
    }
}
