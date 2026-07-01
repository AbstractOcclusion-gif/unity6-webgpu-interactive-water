// WebGL Water - underwater god rays (Unity 6 / URP port)
// A self-contained additive VOLUME: a box mesh spanning the pool interior
// (x,z in [-1,1], y in [-1,0]). The fragment ray-marches the view ray through the
// volume and accumulates the projected caustic intensity at each step, so bright
// focused light becomes vertical shafts that flicker in sync with the floor
// caustics and follow the sun.
//
// HYBRID shadow shafts: each marched sample is additionally multiplied by the main
// light's realtime shadow at that point, so floating objects carve dark silhouette
// beams through the haze WITHOUT losing the caustic shimmer that reads as underwater.
//
// Renders after the water (Transparent+100), additively, ignoring the water surface
// depth; occlusion against solid geometry is done per-step against the camera depth
// texture (opaque geometry only).
Shader "WebGLWater/GodRays"
{
    Properties
    {
        _GodRayColor ("God Ray Color", Color) = (1.0, 0.97, 0.85, 1)
        _GodRayDensity ("Density", Range(0,6)) = 1.5
        _GodRaySteps ("Steps", Range(8,64)) = 24
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "GodRays"
            Tags { "LightMode"="UniversalForward" }

            Blend One One       // additive glow
            ZWrite Off
            ZTest Always        // not occluded by the (transparent) water surface
            Cull Front          // render back faces so the volume covers the screen

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0

            // Sample the main light's shadow MAP (cascades). The screen-space shadow
            // variant is intentionally omitted: it is keyed to opaque-surface depth and
            // would be wrong for arbitrary volumetric samples. Without a shadowmap the
            // pass degrades gracefully to the original caustic-only shafts.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl" // IOR_*, IntersectCube, ProjectCausticUV

            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            float3 _LightDir;       // global, normalized direction toward the light
            float3 _SunColor;       // global, sun colour * intensity

            CBUFFER_START(UnityPerMaterial)
                float4 _GodRayColor;
                float  _GodRayDensity;
                float  _GodRaySteps;
            CBUFFER_END

            // The box mesh is authored in POOL space ([-1,0] in y, [-1,1] in x,z) with an
            // identity transform; the volume frame places it in the world.
            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 poolPos    : TEXCOORD0; // pool-space box position (drives the march)
                float4 screenPos  : TEXCOORD1;
            };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                // The object's own transform defines the POOL-space box ([-1,0] in y,
                // [-1,1] in x,z), whether that's an identity-transform pool-space mesh
                // (rebuilt scene) or the legacy unit cube scaled (2,1,2) at (0,-0.5,0).
                // The volume frame then places that pool box in the world.
                float3 poolPos = TransformObjectToWorld(IN.positionOS.xyz);
                float3 worldPos = PoolToWorld(poolPos);
                o.poolPos = poolPos;
                o.positionCS = TransformWorldToHClip(worldPos);

                // manual ComputeScreenPos (handles the platform V-flip)
                float4 ndc = o.positionCS * 0.5;
                ndc.xy = float2(ndc.x, ndc.y * _ProjectionParams.x) + ndc.w;
                ndc.zw = o.positionCS.zw;
                o.screenPos = ndc;
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // March in POOL space: caustics + the box bounds live there. Convert each
                // sample back to world only for the depth test and the shadow lookup.
                float3 roPool = WorldToPool(_WorldSpaceCameraPos);
                float3 rdPool = normalize(IN.poolPos - roPool);

                float2 t = IntersectCube(roPool, rdPool, float3(-1.0, -1.0, -1.0), float3(1.0, 0.0, 1.0));
                float tEnter = max(t.x, 0.0);
                float tExit  = t.y;
                if (tExit <= tEnter) return half4(0, 0, 0, 0);

                float2 uv = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

                int steps = max(1, (int)_GodRaySteps); // guard against divide-by-zero at 0 steps
                float dt = (tExit - tEnter) / steps;
                float3 refractedLight = -refract(-_LightDir, float3(0, 1, 0), IOR_AIR / IOR_WATER);

                float accum = 0.0;
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float tt = tEnter + (s + 0.5) * dt;
                    float3 pPool = roPool + rdPool * tt;
                    float3 pWorld = PoolToWorld(pPool);
                    float pe = -mul(UNITY_MATRIX_V, float4(pWorld, 1.0)).z; // eye depth of sample
                    if (pe > sceneEye) break;                              // behind solid geometry

                    // project the sample down the refracted light onto the caustic map
                    float2 cuv = ProjectCausticUV(pPool, refractedLight);
                    float caustic = SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv, 0).r;

                    // hybrid: occlude this sample by the main light's shadow so solid
                    // objects punch dark shafts while the caustic flicker is preserved.
                    float4 shadowCoord = TransformWorldToShadowCoord(pWorld);
                    float shadow = MainLightRealtimeShadow(shadowCoord);

                    accum += caustic * shadow;
                }
                accum *= dt * _GodRayDensity;

                return half4(_GodRayColor.rgb * _SunColor * accum, 1.0);
            }
            ENDHLSL
        }
    }
}
