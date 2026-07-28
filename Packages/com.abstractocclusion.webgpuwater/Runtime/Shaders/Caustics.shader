// WebGpuWater - caustics pass (Unity 6 / URP port)
// Renders the water grid mesh into the caustic RenderTexture. The vertex shader
// projects each water vertex along the refracted light onto the pool floor and
// outputs clip-space position directly (no view/projection matrix). The fragment
// shader brightens where the projected area shrinks (light focusing). The green
// channel is left at 1.0 (no occluder shadow).
//
// Drawn manually from C# via CommandBuffer.DrawMesh with an identity matrix.
Shader "AbstractOcclusion/WebGpuWater/Caustics"
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
            // Brings WaterShared: CAUSTIC_PROJECTION_SCALE, CAUSTIC_FOCUS_SCALE,
            // CAUSTIC_NORMAL_SOFTEN (shared with LargeBodyCaustics), RIM_SHADOW_*, POOL_*.
            #include "WaterCommon.hlsl"
            // WaveSlope + _WaveTime: the SAME analytic wind-wave layer the surface folds into its
            // normal (EvaluateSurfaceGeometry, WaterSurfaceFragStages.hlsl), so the caustic focuses through the exact
            // waves the surface shows - correlated by construction. The params
            // (_WaveA/_WaveB/_WaveCount/_WaveMetersPerUnit/_WaveTime) are per-body, so they are set
            // on THIS material in WaterCausticsPass.Render (the body block isn't applied at caustic
            // time). Inert when Wind Waves is off: _WaveCount == 0 -> WaveSlope() returns 0.
            #include "WaterWaves.hlsl"
            float _WaveNormalStrength; // the same wave-normal strength the surface uses (mirrors LargeBodyCaustics)

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                float3 oldPos : TEXCOORD0;
                float3 newPos : TEXCOORD1;
            };

            // project the ray onto the pool floor plane
            float3 project(float3 origin, float3 ray, float3 refractedLight)
            {
                float2 tcube = IntersectCube(origin, ray, POOL_BOX_MIN, POOL_BOX_MAX);
                origin += ray * tcube.y;
                // SafeRefractedLightY: a near-horizontal sun otherwise divides by ~0.
                float tplane = (-origin.y - 1.0) / SafeRefractedLightY(refractedLight.y);
                return origin + refractedLight * tplane;
            }

            v2f vert(appdata v)
            {
                v2f o;
                // Manual bilinear (not tex2Dlod): WebGPU point-samples float32 textures, so a
                // plain sample makes the projected heights/normals - and therefore the whole
                // caustic focusing - blocky in builds whenever mesh res != sim res.
                float4 info = SampleWaterBilinear(v.vertex.xy * 0.5 + 0.5);
                // Softens the ripple normal (CAUSTIC_NORMAL_SOFTEN, WaterShared - shared with the
                // large-body caustic): full-strength slopes over-focus into hard sparkles.
                info.ba *= CAUSTIC_NORMAL_SOFTEN;
                // Fold in the wind-wave slope exactly as the surface does (same MINUS sign and raw
                // * _WaveNormalStrength, in EvaluateSurfaceGeometry) so the caustic - and the
                // chunk god-ray shafts that sample it - inherit the wave structure the surface shows.
                // Ripple keeps its soften; the wave term is raw (mirrors the surface). With Wind Waves
                // off WaveSlope() is 0, so nxz == the softened ripple normal -> byte-identical RT.
                float2 nxz = info.ba - WaveSlope(v.vertex.xy) * _WaveNormalStrength;
                float3 normal = float3(nxz.x, sqrt(max(0.0, 1.0 - dot(nxz, nxz))), nxz.y);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                float3 ray = refract(-_LightDir, normal, IOR_AIR / IOR_WATER);

                o.oldPos = project(v.vertex.xzy, refractedLight, refractedLight);
                o.newPos = project(v.vertex.xzy + float3(0.0, info.r, 0.0), ray, refractedLight);

                // Raw clip-space output (no MVP), so compensate the platform/context render-target
                // Y-flip ourselves: _ProjectionParams.x is -1 when Unity renders flipped (e.g. via an
                // intermediate target under the Mobile URP asset / WebGPU), which otherwise mirrors the
                // caustic RT vs the desktop editor and shifts everything that samples _CausticTex.
                float2 cpos = CAUSTIC_PROJECTION_SCALE * (o.newPos.xz + refractedLight.xz / SafeRefractedLightY(refractedLight.y));
                o.pos = float4(cpos.x, cpos.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // if the projected triangle gets smaller it gets brighter, and vice versa
                float oldArea = length(ddx(i.oldPos)) * length(ddy(i.oldPos));
                float newArea = length(ddx(i.newPos)) * length(ddy(i.newPos));
                // green channel = occluder shadow term; 1.0 means unshadowed.
                // Guard newArea: a degenerate (near-parallel) projected triangle would divide
                // by ~0 and write Inf/NaN into the caustic RT that every other pass samples.
                float4 col = float4(oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE, 1.0, 0.0, 0.0);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

                // Rim shadow. NEGATED on purpose: this shader's 'refractedLight' is the DOWNWARD
                // propagation ray, while PoolRimShadow wants the toward-light direction (see its
                // header in WaterShared.hlsl).
                col.r *= PoolRimShadow(i.newPos, -refractedLight);

                return col;
            }
            ENDCG
        }
    }
}
