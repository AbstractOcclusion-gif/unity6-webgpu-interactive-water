// WebGpuWater - F3 top-down displaced-height raster: the ONE sampled waterline authority.
// Ortho top-down draw of a dedicated flat grid displaced by the SAME vertex core as the
// visible surface (DisplaceSurfaceVertex, WaterSurfaceVertStage.hlsl - reuse-never-rewrite
// applied to the authority itself). The far R16 target holds "surface world Y minus the rest
// plane" for fog marching; a second near-lens RG16 target stores the same height plus coverage
// for centimetre-scale waterline classification.
// Chop is handled BY RASTERIZATION: the horizontal Gerstner/FFT displacement lands where it
// lands, which IS the chop-inverted answer - with zero math drift from the render, the
// property no analytic inversion could guarantee (docs/PLAN_F3_height_rt_2026-08-10.md).
//
// Drawn EXPLICITLY by WaterUnderwaterFogPass beside the eye-depth prepass (no LightMode tag,
// so a camera never draws it), with the fog source's live per-body property block so the
// per-body uniforms (_BedTex, shore gates, sim window...) match the visible surface draw.
//
// Deliberately NOT compiled with WATER_STRIP_SHORE (the keyword is simply not declared
// here): the height authority needs the real shore/surf transforms where they are present.
//
// Depth buffer + ZTest LEqual with the ortho eye ABOVE the sea: where strong chop folds the
// surface over itself, the HIGHEST sheet wins the texel - the crest actually seen.
//
// NO exclusion discards, ON PURPOSE (V1): a carve hole would read back as "rest plane",
// which is worse than the true surface for every consumer; the fog's carve handoffs are a
// separate, untouched mechanism (V1 scope in the plan).
Shader "Hidden/AbstractOcclusion/WebGpuWater/WaterHeightRT"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Name "WaterHeightRT"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma vertex vertHeight
            #pragma fragment fragHeight
            #pragma target 4.0
            // Same helper chain as WaterSurface's passes (include guards make this cheap):
            // the shared vertex core reads the foam-mask sampler and the shore/surf field
            // helpers through these, exactly like the eye-depth prepass pass does.
            #include "UnityCG.cginc"
            #include "WaterCommon.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterWaves.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterExclusion.hlsl"
            #include "WaterExclusionMesh.hlsl"
            #include "WaterLargeWaves.hlsl"
            #include "WaterFoamCommon.hlsl"
            #include "WaterSurfaceScreen.hlsl"
            #include "WaterSurfaceShadow.hlsl"
            #include "WaterSurfaceSpecular.hlsl"
            #include "WaterSurfacePoolTrace.hlsl"
            #include "WaterSurfaceFoamSampling.hlsl"
            #include "WaterSurfaceDetailNormal.hlsl"
            #include "WaterSurfaceVertStage.hlsl"

            struct HeightVaryings
            {
                float4 pos : SV_POSITION;
                // Displaced surface world Y, interpolated. The output derives from THIS
                // rather than from the depth buffer so the stored value is the physical
                // surface, free of any raster-only bias (the eye-depth prepass precedent).
                float surfaceWorldY : TEXCOORD0;
            };

            float4x4 _WaterHeightRTViewProjection;
            float _WaterHeightRTIncludeRipple;

            // Grid vertices arrive as a flat lattice in WORLD metres around the origin; the
            // object matrix (WaterUnderwaterFogPass owns that frame) translates the lattice
            // onto the texel-snapped height window. The rest-plane mapping mirrors the
            // CLIPMAP branch of vert(): world xz on the rest plane through _VolumeCenter,
            // pool frame for the pool-authored terms, pool y 0. The clipmap's edge geomorph
            // is deliberately absent - it stitches adjacent LOD lattices, and this uniform
            // grid has no LODs to stitch.
            HeightVaryings vertHeight(appdata v)
            {
                float3 worldOnPlane = mul(unity_ObjectToWorld,
                                          float4(v.vertex.x, 0.0, v.vertex.z, 1.0)).xyz;
                float3 worldFlat = float3(worldOnPlane.x, _VolumeCenter.y, worldOnPlane.z);
                float3 poolFlat = WorldToPool(worldFlat);
                poolFlat.y = 0.0;
                // The 512 m march authority deliberately omits centimetre ripples. The separate
                // four-metre lens authority opts in because those ripples can visibly cross the
                // near plane; this is a uniform pass setting, so it does not diverge per vertex.
                float rippleFade = 0.0;
                float4 ripple = (float4)0.0;
                [branch]
                if (_WaterHeightRTIncludeRipple > 0.5)
                    ripple = SampleRipple(poolFlat, worldFlat, rippleFade);
                float3 poolDisplaced;
                float2 largeWaveSourceXZ;
                float3 worldPos = DisplaceSurfaceVertex(
                    poolFlat, worldFlat, ripple, 0.0, (float4)0.0,
                    poolDisplaced, largeWaveSourceXZ);
                HeightVaryings o;
                // Use a pass-owned matrix instead of replacing the command buffer's camera
                // matrices. The fog, foam overlay and splash redraw execute immediately after
                // this offscreen raster and must inherit URP's untouched camera state.
                o.pos = mul(_WaterHeightRTViewProjection, float4(worldPos, 1.0));
                o.surfaceWorldY = worldPos.y;
                return o;
            }

            float4 fragHeight(HeightVaryings i) : SV_Target
            {
                // Height RELATIVE TO THE REST PLANE (the R16Float precision doctrine: ~1 cm
                // at 10 m amplitude). SampleHeightRTWorldY adds _VolumeCenter.y back - the
                // SAME global on both sides, so the round trip cannot drift.
                // G is coverage for camera-local consumers. The far height RT is R16 and simply
                // drops it; the lens RT is RG16 and uses it to distinguish a rendered zero-height
                // surface from an untouched texel before falling back to the analytic authority.
                return float4(i.surfaceWorldY - _VolumeCenter.y, 1.0, 0.0, 1.0);
            }
            ENDCG
        }
    }
}
