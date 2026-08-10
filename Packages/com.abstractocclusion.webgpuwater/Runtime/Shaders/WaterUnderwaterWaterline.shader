// WebGpuWater - screen-space waterline meniscus used during partial submersion.
Shader "Hidden/AbstractOcclusion/WebGpuWater/WaterUnderwaterWaterline"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "WaterUnderwaterFogWaterline"
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragWaterline
            #pragma target 4.0
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterExclusion.hlsl"
            #include "WaterWaterline.hlsl"

            #define WATERLINE_METERS_PER_PIXEL_MIN 1e-5
            #define WATERLINE_MIN_ALPHA 0.004
            #define WATERLINE_WARP_BAND_SCALE 6.0
            #define WATERLINE_WARP_MAX 0.06
            #define WATERLINE_WARP_COVER_EDGE 0.15

            float _UnderwaterSurfaceY;
            float _UnderwaterUnbounded;
            float _OceanSurfaceDepthValid;
            float _OceanSurfacePrepassScale;
            float _WaterlineWidthPx;
            float _WaterlineStrength;
            float _WaterlineWarp;

            TEXTURE2D(_OceanSurfaceOwnership); SAMPLER(sampler_OceanSurfaceOwnership);
            TEXTURE2D(_WaterlineSceneTex); SAMPLER(sampler_WaterlineSceneTex);

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                output.uv = GetFullScreenTriangleTexCoord(input.vertexID);
                return output;
            }

            float2 OceanOwnershipSample(float2 uv)
            {
                return SAMPLE_TEXTURE2D_LOD(_OceanSurfaceOwnership,
                                            sampler_OceanSurfaceOwnership, saturate(uv), 0).rg;
            }

            // F7: fraction of an ownership sample's VALID share below which it is read as an
            // AIR claim (r is premultiplied by g; the compare is kept multiplied through to
            // avoid the division). MIRROR of the fog's constant - this function is a known
            // duplicate of WaterUnderwaterFog.shader's copy, and 2026-08-11 proved the drift
            // this duplication invites: F7 landed in the fog's copy first, and the meniscus
            // kept drawing its darkening band on the uncorroborated coin-toss dips - the
            // surviving dashed line, convicted by meniscus-off. Keep the two copies in
            // lockstep until they are deduped into a shared header.
            #define OWNERSHIP_AIR_CORROBORATION_WET_MAX 0.5

            float OceanRenderedCoverage(float2 uv, float analyticCoverage, float2 screenDirection)
            {
                float2 prepassTexel = 1.0 / max(_ScaledScreenParams.xy * _OceanSurfacePrepassScale, 1.0);
                float2 offset = screenDirection * prepassTexel;
                float2 center = OceanOwnershipSample(uv);
                float2 flankA = OceanOwnershipSample(uv + offset);
                float2 flankB = OceanOwnershipSample(uv - offset);
                float2 ownership = center * 0.5 + flankA * 0.25 + flankB * 0.25;
                float coverage = saturate(ownership.r + analyticCoverage * (1.0 - ownership.g));
                // F7 (2026-08-11): an AIR claim may only pull coverage below the analytic
                // value when BOTH flanking samples corroborate it - the interior of a genuine
                // from-air region. An isolated silhouette coin-toss run fails the test and
                // falls back to the analytic coverage (over-cover doctrine: erring wet).
                // Full rationale on the fog's copy of this function.
                bool flankAAir = flankA.g > 0.5
                              && flankA.r < OWNERSHIP_AIR_CORROBORATION_WET_MAX * flankA.g;
                bool flankBAir = flankB.g > 0.5
                              && flankB.r < OWNERSHIP_AIR_CORROBORATION_WET_MAX * flankB.g;
                if (!(flankAAir && flankBAir))
                    coverage = max(coverage, analyticCoverage);
                return coverage;
            }

            half4 FragWaterline(Varyings input) : SV_Target
            {
                float3 nearWorld = ComputeWorldSpacePosition(input.uv, UNITY_NEAR_CLIP_VALUE,
                                                             UNITY_MATRIX_I_VP);
                if (InsideExclusion(nearWorld)) discard;
                if (_UnderwaterUnbounded < 0.5)
                {
                    float3 nearPool = WorldToPool(nearWorld);
                    if (max(abs(nearPool.x), abs(nearPool.z)) > 1.0) discard;
                }
#ifdef WATER_FOG_SIMPLE
                float gap = nearWorld.y - _UnderwaterSurfaceY;
                float gapSmooth = gap;
#else
                float gap = SurfaceSignedGapChopInverted(nearWorld);
                float gapSmooth = SurfaceSignedGap(nearWorld);
#endif
                float2 gapGradient = float2(ddx(gapSmooth), ddy(gapSmooth));
                float metersPerPixel = max(abs(gapGradient.x) + abs(gapGradient.y),
                                           WATERLINE_METERS_PER_PIXEL_MIN);
                float pixelsFromLine = abs(gap) / metersPerPixel;
                float waterlineWidth = max(_WaterlineWidthPx, 1.0);
                float band = 1.0 - smoothstep(0.0, waterlineWidth, pixelsFromLine);
                float tensionMask = 1.0 - saturate(pixelsFromLine /
                                                   (waterlineWidth * WATERLINE_WARP_BAND_SCALE));

                if (_UnderwaterUnbounded > 0.5 && _OceanSurfaceDepthValid > 0.5)
                {
                    float gradientLength = length(gapGradient);
                    float2 searchDirection = gradientLength > WATERLINE_METERS_PER_PIXEL_MIN
                                           ? gapGradient / gradientLength
                                           : float2(0.0, 1.0);
                    float analyticCoverage = WaterlineCoverage(gap, metersPerPixel, 0.0);
                    float renderedCoverage = OceanRenderedCoverage(input.uv, analyticCoverage,
                                                                    searchDirection);
                    float renderedEdge = saturate(4.0 * renderedCoverage * (1.0 - renderedCoverage));
                    band = renderedEdge;
                    tensionMask = renderedEdge;
                }

                float lineAlpha = band * _WaterlineStrength;
                float gapPerUvY = ddy(gapSmooth);
                if (_WaterlineWarp > 0.0)
                {
                    float offset = _WaterlineWarp * WATERLINE_WARP_MAX * 4.0
                                 * tensionMask * (1.0 - tensionMask);
                    float upSign = gapPerUvY >= 0.0 ? 1.0 : -1.0;
                    float2 warpedUV = saturate(input.uv + float2(0.0, upSign * offset));
                    float3 scene = SAMPLE_TEXTURE2D_LOD(_WaterlineSceneTex,
                                                        sampler_WaterlineSceneTex, warpedUV, 0).rgb;
                    scene *= 1.0 - lineAlpha;
                    float coverage = smoothstep(0.0, WATERLINE_WARP_COVER_EDGE, tensionMask);
                    clip(coverage - WATERLINE_MIN_ALPHA);
                    return half4(scene, coverage);
                }

                clip(lineAlpha - WATERLINE_MIN_ALPHA);
                return half4(0.0, 0.0, 0.0, lineAlpha);
            }
            ENDHLSL
        }
    }
}
