// WebGpuWater - depth-only PREPASS for a MESH-footprint water chunk.
// Writes the chunk mesh's FRONT-face depth (pass 0) and BACK-face depth (pass 1) into two depth RTs
// that the chunk wall reads (via texel LOAD - zero sampler cost) to take the water column's ENTRY
// and EXIT distance from ANY closed mesh, instead of the analytic sphere/box. This is what makes a
// chunk generalise past the primitives (Crest portals' front/back-face mask pattern), and because
// the wall then keys off depth instead of draw order it also stops relying on the render queue.
//
// The mesh is authored in POOL space [-1,1] and placed by the volume frame (PoolToWorld) exactly
// like the analytic shell box, so a rotated / non-uniform chunk is the frame's. The frame globals
// (_VolumeCenter/_VolumeExtent/_VolumeRot) arrive per-draw through the chunk's MaterialPropertyBlock
// (WaterVolume.WriteBodyProps), the same block the wall uses - so both agree on placement.
//
// Convex assumption (same as Crest): with one front face and one back face along each ray, standard
// depth rendering keeps the correct entry/exit. A concave mesh's internal cavity can bias the exit;
// acceptable and documented, upgradeable later.
Shader "AbstractOcclusion/WebGpuWater/WaterChunkDepth"
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
            Name "ChunkFrontDepth"
            Cull Back // keep FRONT faces -> nearest entry into the volume

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WaterVolume.hlsl" // PoolToWorld (this body's frame, from the per-draw block)

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = TransformWorldToHClip(PoolToWorld(IN.positionOS.xyz));
                return o;
            }

            half4 frag(Varyings IN) : SV_Target { return 0.0; }
            ENDHLSL
        }

        Pass
        {
            Name "ChunkBackDepth"
            Cull Front // keep BACK faces -> exit from the volume

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "WaterVolume.hlsl"

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings   { float4 positionCS : SV_POSITION; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = TransformWorldToHClip(PoolToWorld(IN.positionOS.xyz));
                return o;
            }

            half4 frag(Varyings IN) : SV_Target { return 0.0; }
            ENDHLSL
        }
    }
}
