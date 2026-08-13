// WebGpuWater - submerged-object caustic occluder (Unity 6 / URP port)
// Draws each submerged interactable into the caustic RenderTexture's GREEN channel (the
// reserved "occluder shadow" term). Every vertex is projected along the SAME refracted
// light the floor samples the caustics with (ProjectCausticUV), so the object's shadow
// lands where its refracted sunlight would have gone - which is where the caustics are,
// NOT where the un-refracted shadow map puts it. Drawn from WaterCausticsPass via
// CommandBuffer.DrawRenderer, right after the water grid caustic and into the same RT
// (green clears to 1 = unshadowed; this pass writes 0 under an object).
Shader "AbstractOcclusion/WebGpuWater/CausticOccluder"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            // GREEN = normalised occluder DEPTH, MIN-blended so the SHALLOWEST occluder along each ray
            // wins (casts the longest shadow, from its top down). Base is 1 (floor = no occluder), left by
            // the caustic pass; consumers read it via OccluderLitFromGreen (shadow only BELOW that depth).
            Blend One One
            BlendOp Min
            ColorMask G // only the occluder-shadow (green) channel; never touch caustic intensity (red)

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WaterShared.hlsl" // IOR_*, POOL_HEIGHT, RAY_SLAB_EPSILON, ProjectCausticUV
            #include "WaterVolume.hlsl" // WorldToPool / WorldDirToPool + the volume-frame globals

            // Volume frame + light are set EXPLICITLY on this material by WaterCausticsPass: the body
            // publishes _VolumeCenter/_VolumeRot as globals only AFTER the caustic pass, so the material
            // copy is what keeps this projection frame-accurate.
            float3 _LightDir; // normalized direction toward the light

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; float occDepth : TEXCOORD0; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                float3 worldPos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 poolPos  = WorldToPool(worldPos);
                // This vertex's normalised depth (0 surface .. 1 floor) - written to green so consumers know
                // WHERE the shadow starts. Min-blend keeps the shallowest, so the shadow spans from the
                // object's top down to the floor, never above it.
                o.occDepth = saturate(-poolPos.y / POOL_HEIGHT);
                // Refracted light, upward convention - identical to the floor's ProjectCausticUV call.
                float3 refractedLight = -refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                // Walk the vertex down the refracted ray to the pool floor IN POOL SPACE, then project
                // that floor point with the exact formula the floor samples with. The old direct
                // ProjectCausticUV(poolPos, refractedLight) mixed a POOL-space position with the
                // WORLD-space direction - correct only for a uniform volume (the original 1:1:1 pool).
                // On a non-uniform body (a deep pool, extent y >> xz) every silhouette landed
                // displaced/stretched until the green channel was stamped to 0 across most of the map,
                // blacking out the caustics and god rays that multiply by it. For a uniform extent the
                // walk reduces EXACTLY to the old formula (the pool-space ratio equals the world one).
                float3 poolLight = WorldDirToPool(refractedLight);
                // NaN guard only (see RAY_SLAB_EPSILON): a near-horizontal refracted ray throws the
                // shadow far off the map where the rasterizer clips it; it just must not divide by zero.
                float poolLightY = abs(poolLight.y) < RAY_SLAB_EPSILON ? RAY_SLAB_EPSILON : poolLight.y;
                float3 floorPool = poolPos - ((poolPos.y + POOL_HEIGHT) / poolLightY) * poolLight;
                float2 uv  = ProjectCausticUV(floorPool, poolLight); // caustic-map UV in [0,1] (pool-space ray)
                float2 ndc = uv * 2.0 - 1.0;
                // Match Caustics.shader's manual render-target Y-flip so the occluder write and the
                // floor's sample agree on every backend (_ProjectionParams.x is -1 when flipped).
                o.positionCS = float4(ndc.x, ndc.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                return half4(0.0, IN.occDepth, 0.0, 0.0); // green = this occluder's depth (min-blended)
            }
            ENDHLSL
        }
    }
}
