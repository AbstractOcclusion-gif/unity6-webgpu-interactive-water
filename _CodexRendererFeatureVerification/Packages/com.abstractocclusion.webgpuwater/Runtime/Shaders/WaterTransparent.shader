// WebGpuWater - lit TRANSPARENT surface that carries the water medium (the public fog API's
// reference consumer).
//
// WHY THIS SHADER EXISTS: WaterFogTransparent.cs is the SORTING half only - it suppresses the
// queue-time draw and has the water feature re-draw the renderer after the whole water stack.
// It cannot tint anything. The tinting half lives in WebGpuWaterFogAPI.hlsl, which a MATERIAL
// has to include. A stock URP Lit set to Surface Type = Transparent therefore stays visible in
// the water (the component's job) while ignoring Water Opacity and Depth Attenuation entirely,
// because its shader never reads those uniforms. This is the same wall WaterReceiverConverter
// already documents for opaque meshes: "a MonoBehaviour / MaterialPropertyBlock can only push
// property VALUES, it cannot add that logic to a shader it doesn't own". WaterReceiver is the
// opaque answer; this is the transparent one.
//
// USAGE: convert a renderer with AbstractOcclusion > WebGpuWater > Convert Selection To Water
// Transparent (WaterTransparentConverter), which swaps the material AND adds the sorting
// component. Hand-wiring works too: assign this shader, then add WaterFogTransparent.
//
// The ForwardLit pass MUST stay pass index 0 - WaterParticlesAfterFogPass.DrawUserTransparents
// submits `cmd.DrawRenderer(target, materials[m], m, 0)` by index, not by LightMode tag.
//
// Lighting is the same Blinn-Phong model WaterReceiver uses rather than URP's full PBR: the two
// have to agree pixel-for-pixel where a transparent prop meets an opaque one at the waterline,
// and a metalness workflow on one side of that seam and not the other is exactly the mismatch
// the receiver's own header warns about.
Shader "AbstractOcclusion/WebGpuWater/WaterTransparent"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (1, 1, 1, 0.5)
        _BaseMap ("Base Map", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _SpecColor ("Specular Color", Color) = (0.2, 0.2, 0.2, 1)
        // The water medium is global (published per frame by the body). This is the ONE
        // per-material knob on it: 0 opts a material out entirely, which is what makes the
        // shader safe to convert onto a prop that must not pick up the water yet.
        _WaterFogStrength ("Water Medium Strength", Range(0,1)) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
    }
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        // Pass 0 - see the header note: the after-fog reroute draws this pass BY INDEX.
        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off ZTest LEqual Cull [_Cull]

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile_fog
            // ONE 4-way set, exactly as URP's own Lit.shader declares it (the receiver's note:
            // two independent pragmas compile unreachable *_SCREEN cross products).
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            // THE one include that carries the medium. Pulls WaterVolume.hlsl + WaterParticleFog.hlsl
            // + WaterFog.hlsl behind it, so no other water header is needed for the water itself.
            #include "WebGpuWaterFogAPI.hlsl"
            // WaterSpecularExponent only - the receiver and the terrain shader read the smoothness
            // remap from here, and a fourth copy of that curve is how a waterline seam appears.
            // Safe to include anywhere: the file declares no textures and includes nothing.
            #include "WaterWetness.hlsl"

            TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
            // Global "toward the sun", published by the primary WaterVolume - the SAME direction the
            // sheet and the receivers feed the in-scatter, so a prop's tint cannot drift from the
            // water around it. _SunColor is declared by WaterFog.hlsl.
            float3 _LightDir;
            float _CameraUnderwater;

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST;
                float4 _SpecColor;
                float _BumpScale;
                float _Smoothness;
                float _WaterFogStrength;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
                float2 uv : TEXCOORD0;
            };

            // tangentWS.w carries the bitangent sign (handedness) so the frag can rebuild B.
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float4 tangentWS : TEXCOORD3;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs positions = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normals = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                OUT.positionCS = positions.positionCS;
                OUT.positionWS = positions.positionWS;
                OUT.normalWS = normals.normalWS;
                OUT.tangentWS = float4(normals.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                OUT.uv = TRANSFORM_TEX(IN.uv, _BaseMap);
                return OUT;
            }

            float3 ResolveNormal(Varyings IN)
            {
                float3 normalTS = UnpackNormalScale(
                    SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, IN.uv), _BumpScale);
                float3 N = normalize(IN.normalWS);
                float3 T = normalize(IN.tangentWS.xyz);
                float3 B = normalize(cross(N, T) * IN.tangentWS.w);
                return normalize(mul(normalTS, float3x3(T, B, N)));
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float4 baseSample = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv) * _BaseColor;
                float3 albedo = baseSample.rgb;
                float alpha = saturate(baseSample.a);

                float3 N = ResolveNormal(IN);
                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndl = saturate(dot(N, mainLight.direction));
                float shadow = mainLight.shadowAttenuation;

                float3 ambient = SampleSH(N);
                float3 color = albedo * (ambient + mainLight.color * (ndl * shadow));

                float3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float3 halfDirWS = normalize(mainLight.direction + viewDirWS);
                float specExponent = WaterSpecularExponent(_Smoothness);
                // ndl-gated so a back-lit face never speculates, and folded in BEFORE the medium
                // so depth dims the highlight too - the receiver's ordering.
                float specTerm = pow(saturate(dot(N, halfDirWS)), specExponent) * ndl * shadow;
                color += mainLight.color * _SpecColor.rgb * specTerm;

                // THE WATER MEDIUM. Four terms in one call: path extinction toward the lit
                // in-scatter, downwelling depth darkening, the scene-lamp glow, and Water Opacity
                // turbidity on rays that cross the waterline. Applied AFTER the albedo multiply -
                // exact, because the pair is linear in the colour (see the API header).
                float3 fogMul, fogAdd;
                WebGpuWaterFogTransparent(IN.positionWS, _LightDir, _SunColor, fogMul, fogAdd);
                // Per-material opt-out: lerp the PAIR, not the result, so strength 0 is provably
                // identity (fogMul -> 1, fogAdd -> 0) rather than "almost the original colour".
                float strength = saturate(_WaterFogStrength);
                fogMul = lerp(float3(1.0, 1.0, 1.0), fogMul, strength);
                fogAdd *= strength;
                color = WebGpuWaterApplyFog(color, fogMul, fogAdd);

                // The water-medium term above is the below-surface optical path. Unity fog is
                // a scene-atmosphere term, so it is applied only while the camera remains in air.
                if (_CameraUnderwater < 0.5)
                    color = MixFog(color, ComputeFogFactor(IN.positionCS.z));

                return half4(color, alpha);
            }
            ENDHLSL
        }

        // No ShadowCaster / DepthOnly / DepthNormals pass by design: an alpha-blended surface that
        // wrote depth would occlude the water sheet behind it in the opaque-only depth copy the
        // reroute restores (WaterRestoreOpaqueDepth), and the prop would then z-fail against its
        // own silhouette. Casting shadows has the same problem in reverse - a half-transparent prop
        // would drop a fully opaque shadow. Use WaterReceiver for props that need either.
    }
    Fallback Off
}
