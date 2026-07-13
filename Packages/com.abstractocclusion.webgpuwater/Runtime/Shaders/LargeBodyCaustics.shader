// WebGpuWater - large-body caustics (Unity 6 / URP port).
// The ocean version of our pool Caustics.shader: same refraction + area-shrink (Jacobian) focusing,
// but rebuilt in the moving sim-WINDOW's WORLD frame instead of the pool box - because an ocean has
// no fixed floor and the near-field sim covers a camera-following window, not the whole body.
//
// Each vertex of the dense window grid samples the window sim (_WaterTex, sampled in the window's
// normalised space), refracts the sun through the surface normal, and projects onto a REFERENCE
// PLANE a fixed depth below the surface (the ocean analog of the pool floor). The fragment writes
// how much the projected area shrank (light focusing) into the caustic RT, which the underwater god
// rays sample by the same window map. Gated/opt-in: only the windowed ocean renders this; pools and
// bounded bodies keep the pool Caustics.shader untouched.
//
// Drawn manually from C# via CommandBuffer.DrawMesh with an identity matrix (the vertex shader
// outputs clip space directly), exactly like the pool caustic pass.
Shader "AbstractOcclusion/WebGpuWater/LargeBodyCaustics"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"     // SampleWaterBilinear, _LightDir, _WaterTexel; WaterShared: IOR_*, SafeRefractedLightY
            #include "WaterVolume.hlsl"     // _SimCenter / _SimExtent (window frame) + LARGE_CAUSTIC_REFERENCE_DEPTH
            #include "WaterWaves.hlsl"      // _WaveTime (shared clock) for the analytic wave field
            #include "WaterLargeWaves.hlsl" // ApplyLargeBodyWaveNormal, LargeBodyWaveHeight - the open-water swell

            float _WaveNormalStrength; // global; the same wave-normal strength the surface uses

            // Reference-plane depth is shared with the god-ray sampler via WaterVolume.hlsl
            // (LARGE_CAUSTIC_REFERENCE_DEPTH), so generation and sampling can't drift apart.
            // Full surface slopes over-focus into hard sparkles; the pool caustic softens the same way.
            #define CAUSTIC_NORMAL_SOFTEN   0.5
            // Brightness of the focused caustic (matches the pool caustic's CAUSTIC_FOCUS_SCALE).
            #define CAUSTIC_FOCUS_SCALE     0.2
            // The interactive ripple sim is coarse over a large window, so weight it DOWN against the
            // analytic swell; it stays a soft splash/wake detail rather than the dominant (weird) focus.
            #define CAUSTIC_RIPPLE_WEIGHT   0.3

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 oldPos : TEXCOORD0; // undisturbed projection (flat surface)
                float3 newPos : TEXCOORD1; // refracted projection (displaced surface)
            };

            // March a ray from 'origin' along 'dir' down to the horizontal plane y = planeY.
            // SafeRefractedLightY guards a near-horizontal sun (dir.y ~ 0) from dividing by zero.
            float3 ProjectToPlane(float3 origin, float3 dir, float planeY)
            {
                float t = (planeY - origin.y) / SafeRefractedLightY(dir.y);
                return origin + dir * t;
            }

            v2f vert(appdata v)
            {
                v2f o;
                // The window grid is a normalised [-1,1] plane in xy; map it into the window's world frame.
                float2 windowNorm = v.vertex.xy;
                float2 worldXZ = _SimCenter.xz + windowNorm * _SimExtent.xz; // axis-aligned window (ocean is unrotated)
                float surfaceY = _SimCenter.y;
                float refPlaneY = surfaceY - LARGE_CAUSTIC_REFERENCE_DEPTH;

                // Base tilt from the interactive ripple sim, softened + weighted DOWN: it is coarse over a
                // large window, so it must not dominate. The analytic ocean swell is the primary focus.
                float4 info = SampleWaterBilinear(windowNorm * 0.5 + 0.5);
                float2 rippleTilt = info.ba * (CAUSTIC_NORMAL_SOFTEN * CAUSTIC_RIPPLE_WEIGHT);
                float3 normal = float3(rippleTilt.x, sqrt(max(0.0, 1.0 - dot(rippleTilt, rippleTilt))), rippleTilt.y);
                // Fold in the large-body swell so the caustic - and the volumetric beams that sample it -
                // focus light through the ACTUAL visible wave shape (crisp, resolution-independent), like
                // KWS. Same function + strength the surface uses, so the beams line up with the waves.
                normal = ApplyLargeBodyWaveNormal(normal, worldXZ, _WaveNormalStrength);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER); // undisturbed
                float3 ray           = refract(-_LightDir, normal,               IOR_AIR / IOR_WATER); // through the surface

                // Displaced surface point: analytic swell height + the (soft) interactive ripple height.
                float waveHeight = LargeBodyWaveHeight(worldXZ) + info.r * _SimExtent.y * CAUSTIC_RIPPLE_WEIGHT;
                float3 flatPos = float3(worldXZ.x, surfaceY, worldXZ.y);
                float3 dispPos = float3(worldXZ.x, surfaceY + waveHeight, worldXZ.y);

                o.oldPos = ProjectToPlane(flatPos, refractedLight, refPlaneY);
                o.newPos = ProjectToPlane(dispPos, ray,            refPlaneY);

                // Index the caustic RT in the window frame: the refracted hit's world xz, normalised
                // back into [-1,1] over the window, so the god-ray march samples it by the same map.
                float2 causticNorm = (o.newPos.xz - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                o.pos = float4(causticNorm.x, causticNorm.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Brighter where the projected triangle shrank (light converging), dimmer where it
                // spread. Guard newArea: a degenerate near-parallel projection would divide by ~0 and
                // write Inf/NaN into the RT that the god rays then sample.
                float oldArea = length(ddx(i.oldPos)) * length(ddy(i.oldPos));
                float newArea = length(ddx(i.newPos)) * length(ddy(i.newPos));
                // r = focusing; g = 1 (no occluder shadow term, matching the pool caustic RT layout).
                return float4(oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE, 1.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
