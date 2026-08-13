// WebGpuWater - depth-only PREPASS for MESH-shape water exclusion volumes.
// Writes each mesh volume's FRONT-face depth (pass 0) and BACK-face depth (pass 1) into two depth
// RTs the carve consumers read by texel LOAD (zero sampler cost) to take the DRY column's entry and
// exit from an arbitrary closed mesh instead of the analytic box/sphere. Twin of WaterChunkDepth -
// same Crest front/back-face mask pattern, same convex assumption - with one difference: an
// exclusion volume has a real transform, so its mesh is placed by the DRAW MATRIX
// (WaterExclusionDepthPass passes the volume's shape-to-world) rather than by a frame in-shader.
// The mesh is therefore authored in the volume's own local space, spanning [-0.5, 0.5] like the
// unit cube the Box shape carves, and Size scales it exactly as it scales a box.
//
// Convex assumption (same as Crest, same as the chunk): with one front face and one back face along
// each ray, standard depth rendering keeps the correct entry/exit. A concave mesh's internal cavity
// biases the exit - documented v1 limitation, upgradeable later.
Shader "AbstractOcclusion/WebGpuWater/WaterExclusionDepth"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }

        // Depth only: no colour target is bound, so mask colour writes off. ZWrite On is the whole
        // point; the frag returns 0 to SV_Target the way URP's own shadow caster does under a
        // depth-only target (valid, no colour attachment needed).
        ColorMask 0
        ZWrite On
        ZTest LEqual

        Pass
        {
            Name "ExclusionFrontDepth"
            Cull Back // keep FRONT faces -> nearest entry into the dry volume

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target { return 0.0; }
            ENDHLSL
        }

        Pass
        {
            Name "ExclusionBackDepth"
            Cull Front // keep BACK faces -> exit from the dry volume

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target { return 0.0; }
            ENDHLSL
        }
    }
}
