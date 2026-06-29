// WebGL Water - water surface (Unity 6 / URP port)
// Raytraced reflection + refraction of the pool, sphere and sky cubemap.
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
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }
        Pass
        {
            Cull [_Cull]
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"

            float _Underwater;
            float _ReflectionStrength;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos      : SV_POSITION;
                float3 position : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float4 info = tex2Dlod(_WaterTex, float4(v.vertex.xy * 0.5 + 0.5, 0, 0));
                float3 position = v.vertex.xzy;   // grid XY plane -> world (x, 0, z)
                position.y += info.r;
                o.position = position;
                o.pos = mul(UNITY_MATRIX_VP, float4(position, 1.0));
                return o;
            }

            float3 getSurfaceRayColor(float3 origin, float3 ray, float3 waterColor)
            {
                float3 color;
                float q = IntersectSphere(origin, ray, _SphereCenter, _SphereRadius);
                if (q < 1.0e6)
                {
                    color = GetSphereColor(origin + ray * q);
                }
                else if (ray.y < 0.0)
                {
                    float2 t = IntersectCube(origin, ray, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                    color = GetWallColor(origin + ray * t.y);
                }
                else
                {
                    float2 t = IntersectCube(origin, ray, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
                    float3 hit = origin + ray * t.y;
                    if (hit.y < 2.0 / 12.0)
                    {
                        color = GetWallColor(hit);
                    }
                    else
                    {
                        color = texCUBE(_Sky, ray).rgb;
                        color += float3(10.0, 8.0, 6.0) * pow(max(0.0, dot(_LightDir, ray)), 5000.0);
                    }
                }
                if (ray.y < 0.0) color *= waterColor;
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

                float3 normal = float3(info.b, sqrt(1.0 - dot(info.ba, info.ba)), info.a);
                float3 incomingRay = normalize(i.position - _Eye);

                if (_Underwater > 0.5)
                {
                    normal = -normal;
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_WATER / IOR_AIR);
                    float fresnel = lerp(0.5, 1.0, pow(1.0 - dot(normal, -incomingRay), 3.0));

                    float3 reflectedColor = getSurfaceRayColor(i.position, reflectedRay, UNDERWATER_COLOR);
                    float3 refractedColor = getSurfaceRayColor(i.position, refractedRay, float3(1.0, 1.0, 1.0)) * float3(0.8, 1.0, 1.1);

                    float tUnder = (1.0 - fresnel) * length(refractedRay);
                    tUnder = lerp(1.0, tUnder, _ReflectionStrength); // strength 0 = fully refracted
                    return float4(lerp(reflectedColor, refractedColor, tUnder), 1.0);
                }
                else
                {
                    float3 reflectedRay = reflect(incomingRay, normal);
                    float3 refractedRay = refract(incomingRay, normal, IOR_AIR / IOR_WATER);
                    float fresnel = lerp(0.25, 1.0, pow(1.0 - dot(normal, -incomingRay), 3.0));

                    float3 reflectedColor = getSurfaceRayColor(i.position, reflectedRay, ABOVEWATER_COLOR);
                    float3 refractedColor = getSurfaceRayColor(i.position, refractedRay, ABOVEWATER_COLOR);

                    return float4(lerp(refractedColor, reflectedColor, fresnel * _ReflectionStrength), 1.0);
                }
            }
            ENDCG
        }
    }
}
