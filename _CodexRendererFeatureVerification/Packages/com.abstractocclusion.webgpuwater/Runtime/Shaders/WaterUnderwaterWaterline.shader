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
            #pragma multi_compile_fragment _ WATER_FOG_SIMPLE WATER_FOG_CLASSIFY_RT

            // Before the includes: a fullscreen pass with sampler headroom takes the hardware
            // bilinear for the direction map. See OceanAperiodicDirectionMapBilinear for why the
            // Load fallback exists and must stay the default. The aperiodic SHAPE stays - see the
            // reverted-experiment note in WaterUnderwaterFog.shader.
            #define WATER_APERIODIC_MAP_SAMPLER 1
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
#ifdef WATER_FOG_CLASSIFY_RT
            TEXTURE2D(_WaterFogClassifyRT);
#endif

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

            #include "WaterOceanRenderedCoverage.hlsl"

#ifdef WATER_FOG_CLASSIFY_RT
            float2 LoadWaterFogClassification(float2 uv)
            {
                int2 pixelMax = max(int2(_ScaledScreenParams.xy) - int2(1, 1), int2(0, 0));
                int2 pixel = clamp(int2(uv * _ScaledScreenParams.xy), int2(0, 0), pixelMax);
                return LOAD_TEXTURE2D(_WaterFogClassifyRT, pixel).rg;
            }
#endif

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
                float gap;
                float gapSmooth;
#ifdef WATER_FOG_SIMPLE
                gap = nearWorld.y - _UnderwaterSurfaceY;
                gapSmooth = gap;
#elif defined(WATER_FOG_CLASSIFY_RT)
                float2 classifyGaps = LoadWaterFogClassification(input.uv);
                gap = classifyGaps.x;
                gapSmooth = classifyGaps.y;
#else
                // ONE solve for both gaps (2026-08-11): the inversion's first iteration already
                // evaluates the vertical field at this xz, so the separate SurfaceSignedGap call
                // this replaces was re-deriving a number the inversion had. Same division of duties
                // as the fog's ArmWeight - position from the inverted read, feather width from the
                // smooth one - at three field evaluations instead of four.
                //
                // And NO solve at all when the camera is metres clear of its own surface: the whole
                // near plane is then on one side, this band is off screen, and every pixel is about
                // to be clipped below. One height-RT tap decides it, uniformly across the screen so
                // the derivative below stays defined. Same test and same margin as the fog pass -
                // see WaterlineFarFromSurface in WaterWaterline.hlsl for why it is sound.
                float farGap;
                if (WaterlineFarFromSurface(nearWorld, farGap))
                {
                    gap = farGap;
                    gapSmooth = farGap;
                }
                else
                {
                    gap = SurfaceSignedGapChopInvertedPair(nearWorld, gapSmooth);
                }
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
