// WebGL Water - shared ray-tracing helpers (Unity 6 / URP port)
// Faithful translation of helperFunctions from Evan Wallace's renderer.js (MIT).
#ifndef WEBGL_WATER_COMMON_INCLUDED
#define WEBGL_WATER_COMMON_INCLUDED

#define IOR_AIR   1.0
#define IOR_WATER 1.333
#define POOL_HEIGHT 1.0

static const float3 ABOVEWATER_COLOR = float3(0.25, 1.0, 1.25);
static const float3 UNDERWATER_COLOR = float3(0.4, 0.9, 1.0);

// Global uniforms (set from C# via Shader.SetGlobalX)
sampler2D   _WaterTex;     // (height, velocity, normal.x, normal.z)
sampler2D   _CausticTex;   // (caustic intensity, sphere/rim shadow, -, -)
sampler2D   _Tiles;        // pool wall/floor albedo (REPEAT)
samplerCUBE _Sky;          // sky cubemap

float3 _LightDir;          // normalized direction toward the light
float3 _SphereCenter;
float  _SphereRadius;
float3 _Eye;               // camera world position

float2 IntersectCube(float3 origin, float3 ray, float3 cubeMin, float3 cubeMax)
{
    float3 tMin = (cubeMin - origin) / ray;
    float3 tMax = (cubeMax - origin) / ray;
    float3 t1 = min(tMin, tMax);
    float3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar  = min(min(t2.x, t2.y), t2.z);
    return float2(tNear, tFar);
}

float IntersectSphere(float3 origin, float3 ray, float3 sphereCenter, float sphereRadius)
{
    float3 toSphere = origin - sphereCenter;
    float a = dot(ray, ray);
    float b = 2.0 * dot(toSphere, ray);
    float c = dot(toSphere, toSphere) - sphereRadius * sphereRadius;
    float discriminant = b * b - 4.0 * a * c;
    if (discriminant > 0.0)
    {
        float t = (-b - sqrt(discriminant)) / (2.0 * a);
        if (t > 0.0) return t;
    }
    return 1.0e6;
}

float3 GetSphereColor(float3 p)
{
    float3 color = float3(0.5, 0.5, 0.5);

    // ambient occlusion against the walls / floor
    color *= 1.0 - 0.9 / pow((1.0 + _SphereRadius - abs(p.x)) / _SphereRadius, 3.0);
    color *= 1.0 - 0.9 / pow((1.0 + _SphereRadius - abs(p.z)) / _SphereRadius, 3.0);
    color *= 1.0 - 0.9 / pow((p.y + 1.0 + _SphereRadius) / _SphereRadius, 3.0);

    // caustics
    float3 sphereNormal = (p - _SphereCenter) / _SphereRadius;
    float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float diffuse = max(0.0, dot(-refractedLight, sphereNormal)) * 0.5;
    float4 info = tex2D(_WaterTex, p.xz * 0.5 + 0.5);
    if (p.y < info.r)
    {
        float4 caustic = tex2D(_CausticTex, 0.75 * (p.xz - p.y * refractedLight.xz / refractedLight.y) * 0.5 + 0.5);
        diffuse *= caustic.r * 4.0;
    }
    color += diffuse;
    return color;
}

float3 GetWallColor(float3 p)
{
    float scale = 0.5;

    float3 wallColor;
    float3 normal;
    if (abs(p.x) > 0.999)
    {
        wallColor = tex2D(_Tiles, p.yz * 0.5 + float2(1.0, 0.5)).rgb;
        normal = float3(-p.x, 0.0, 0.0);
    }
    else if (abs(p.z) > 0.999)
    {
        wallColor = tex2D(_Tiles, p.yx * 0.5 + float2(1.0, 0.5)).rgb;
        normal = float3(0.0, 0.0, -p.z);
    }
    else
    {
        wallColor = tex2D(_Tiles, p.xz * 0.5 + 0.5).rgb;
        normal = float3(0.0, 1.0, 0.0);
    }

    scale /= length(p);                                                        // pool ambient occlusion
    scale *= 1.0 - 0.9 / pow(length(p - _SphereCenter) / _SphereRadius, 4.0);  // sphere ambient occlusion

    float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
    float diffuse = max(0.0, dot(refractedLight, normal));
    float4 info = tex2D(_WaterTex, p.xz * 0.5 + 0.5);
    if (p.y < info.r)
    {
        float4 caustic = tex2D(_CausticTex, 0.75 * (p.xz - p.y * refractedLight.xz / refractedLight.y) * 0.5 + 0.5);
        scale += diffuse * caustic.r * 2.0 * caustic.g;
    }
    else
    {
        // shadow for the rim of the pool
        float2 t = IntersectCube(p, refractedLight, float3(-1.0, -POOL_HEIGHT, -1.0), float3(1.0, 2.0, 1.0));
        diffuse *= 1.0 / (1.0 + exp(-200.0 / (1.0 + 10.0 * (t.y - t.x)) * (p.y + refractedLight.y * t.y - 2.0 / 12.0)));
        scale += diffuse * 0.5;
    }

    return wallColor * scale;
}

#endif // WEBGL_WATER_COMMON_INCLUDED
