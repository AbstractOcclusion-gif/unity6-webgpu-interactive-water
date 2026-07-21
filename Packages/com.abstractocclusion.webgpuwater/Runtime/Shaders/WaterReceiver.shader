// WebGL Water - lit receiver for interactable objects (Unity 6 / URP port)
// A proper URP surface: real main-light lighting, casts + receives shadows, and
// receives the projected caustics where it sits below the water surface. Driven by
// the same directional light as everything else (its direction also reaches the
// analytic water via the _LightDir global), so there is no separate fake light.
Shader "AbstractOcclusion/WebGpuWater/WaterReceiver"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.82, 0.52, 0.30, 1)
        _BaseMap ("Base Map", 2D) = "white" {}
        [Normal] _BumpMap ("Normal Map", 2D) = "bump" {}
        _BumpScale ("Normal Scale", Float) = 1
        [Toggle(_AUTOTILEOBJSIZE)] _AutoTileByObjectSize ("Auto Tile By Object Size", Float) = 0
        _TilesTiling ("Tiles Tiling (tiles per world unit)", Vector) = (1,1,0,0)
        _Smoothness ("Smoothness", Range(0,1)) = 0.5
        _SpecColor ("Specular Color", Color) = (0.2, 0.2, 0.2, 1)
        _CausticStrength ("Caustic Strength", Range(0,8)) = 4
        _CausticTint ("Caustic Tint", Color) = (1,1,1,1)
        _UnderwaterTint ("Underwater Tint", Color) = (0.4, 0.9, 1.0, 1)
        [Toggle] _ShadeInnerFacesOnly ("Shade Inner Faces Only (solid pool)", Float) = 0
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            ZWrite On ZTest LEqual Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma shader_feature_local _AUTOTILEOBJSIZE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl" // IOR_*, ProjectCausticUV

            TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);    SAMPLER(sampler_BumpMap);
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
            float3 _LightDir;   // global "toward the light", driven from the Unity sun
            float4 _WaterTexel; // (1/w, 1/h, w, h) of _WaterTex, pushed from C#
            float _CausticOccluderActive; // 1 when caustic.g is this body's valid refracted occluder-shadow channel (see WaterCommon.hlsl)

            // Manual bilinear height sample: WebGPU cannot hardware-filter the float32 sim
            // texture, so a filtered SAMPLE_TEXTURE2D silently point-samples there and the
            // underwater/caustic cut on objects goes blocky in builds.
            float SampleWaterHeightBilinear(float2 uv)
            {
                float2 texel = _WaterTexel.xy;
                float2 st = uv * _WaterTexel.zw - 0.5;
                float2 f = frac(st);
                float2 baseUV = (floor(st) + 0.5) * texel;
                float c00 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV, 0).r;
                float c10 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(texel.x, 0.0), 0).r;
                float c01 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + float2(0.0, texel.y), 0).r;
                float c11 = SAMPLE_TEXTURE2D_LOD(_WaterTex, sampler_WaterTex, baseUV + texel, 0).r;
                return lerp(lerp(c00, c10, f.x), lerp(c01, c11, f.x), f.y);
            }

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _BaseMap_ST;
                float4 _TilesTiling;
                float4 _SpecColor;
                float4 _CausticTint;
                float4 _UnderwaterTint;
                float _BumpScale;
                float _Smoothness;
                float _CausticStrength;
                float _ShadeInnerFacesOnly;
            CBUFFER_END

            struct Attributes { float4 positionOS:POSITION; float3 normalOS:NORMAL; float4 tangentOS:TANGENT; float2 uv:TEXCOORD0; };
            // tangentWS.w carries the bitangent sign (handedness) so the frag can rebuild B.
            struct Varyings   { float4 positionCS:SV_POSITION; float3 positionWS:TEXCOORD0; float3 normalWS:TEXCOORD1; float2 uv:TEXCOORD2; float4 tangentWS:TEXCOORD3; };

            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                VertexNormalInputs normalInput = GetVertexNormalInputs(IN.normalOS, IN.tangentOS);
                o.normalWS   = normalInput.normalWS;
                o.tangentWS  = float4(normalInput.tangentWS, IN.tangentOS.w * GetOddNegativeScale());
                o.uv         = IN.uv; // raw mesh UV; tiling applied in frag (Base Map ST, or object size)
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Tangent frame first: needed for the normal map and (when enabled) to project the
                // object world size onto the surface U/V axes for size-based tiling.
                float3 vertexNormalWS = normalize(IN.normalWS);
                float3 tangentWS = normalize(IN.tangentWS.xyz);
                float3 bitangentWS = normalize(cross(vertexNormalWS, tangentWS) * IN.tangentWS.w);

                // Sampling UV. Default: standard Base Map tiling/offset (_BaseMap_ST), unchanged.
                // Auto Tile By Object Size: scale the raw mesh UV by the object WORLD size (from its
                // object-to-world matrix) projected onto the surface U/V axes, so texel density stays
                // even however the object is scaled - the receiver analogue of AnalyticPool face-size
                // tiling. _TilesTiling is then tiles-per-world-unit.
            #ifdef _AUTOTILEOBJSIZE
                float3 objectSize = float3(
                    length(unity_ObjectToWorld._m00_m10_m20),
                    length(unity_ObjectToWorld._m01_m11_m21),
                    length(unity_ObjectToWorld._m02_m12_m22));
                float2 faceWorld = float2(dot(abs(tangentWS), objectSize), dot(abs(bitangentWS), objectSize));
                float2 uv = IN.uv * faceWorld * _TilesTiling.xy;
            #else
                float2 uv = IN.uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
            #endif

                float3 albedo = _BaseColor.rgb * SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, uv).rgb;

                // Tangent-space normal map -> world normal. Rebuild the bitangent from the
                // interpolated normal/tangent and the stored handedness sign so mirrored UVs
                // light correctly. Default "bump" map is flat, so untouched materials are
                // identical to before.
                float3 normalTS = UnpackNormalScale(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, uv), _BumpScale);
                float3 N = normalize(normalTS.x * tangentWS + normalTS.y * bitangentWS + normalTS.z * vertexNormalWS);

                // Water-column geometry FIRST: the underwater flag and the refracted occluder shadow are
                // needed by the main-light lighting below, so a submerged face is shadowed by the refracted
                // occluder (matching the pool floor + caustics) instead of URP's un-refracted shadow map -
                // which, applied here as well, painted a SECOND, offset shadow beside the caustic one.
                float3 poolPos = WorldToPool(IN.positionWS);
                float inside = FootprintMaskPool(poolPos);

                // Wet-face gate for a SOLID / arbitrary mesh used as a pool (Shade Inner Faces
                // Only): a face is in contact with water only if its GEOMETRIC normal points
                // inward (toward the pool's vertical axis, so inner walls) or up (the floor);
                // an outer wall or underside of a tank stays dry. Uses the vertex normal, not
                // the normal-mapped N. Off (default) = every submerged face shades, which is
                // what the wizard's open-top single-sided pool and ordinary props want.
                float wetFace = 1.0;
                if (_ShadeInnerFacesOnly > 0.5)
                {
                    float towardAxis = dot(vertexNormalWS.xz, _VolumeCenter.xz - IN.positionWS.xz);
                    wetFace = (towardAxis > 0.0 || vertexNormalWS.y > 0.0) ? 1.0 : 0.0;
                }
                float waterMask = inside * wetFace;

                // One surface height for this fragment: the sampled sim (ripple) surface - the
                // same one the underwater cut uses - converted to world Y. Downwelling, caustic
                // fade and fog all measure depth against THIS, instead of the old flat
                // _VolumeCenter.y plane, so the shader never disagrees with itself about where
                // the surface sits (and a body at any Y is handled by its own volume frame).
                float2 wuv = poolPos.xz * 0.5 + 0.5;
                float simH = SampleWaterHeightBilinear(wuv);
                float surfaceY = PoolToWorld(float3(poolPos.x, simH, poolPos.z)).y;
                bool underwater = (waterMask > 0.5 && poolPos.y < simH);

                // Caustic map sampled ONCE up front with explicit gradients (WGSL-safe: an implicit-
                // derivative sample inside the per-fragment waterline branch below is undefined on WebGPU).
                // Green = this body's refracted occluder shadow (1 = lit); red = the caustic pattern.
                float3 refractedLight = -refract(-_LightDir, float3(0,1,0), IOR_AIR / IOR_WATER);
                // Pool-space refracted ray: ProjectCausticUV's xz/y ratio is only valid in pool space, so
                // convert here - otherwise the projection is wrong on non-uniform (deep) bodies. Uniform
                // extents preserve the ratio, so those are byte-identical.
                float2 cuv = ProjectCausticUV(poolPos, WorldDirToPool(refractedLight));
                float4 causticSample = SAMPLE_TEXTURE2D_GRAD(_CausticTex, sampler_CausticTex, cuv, ddx(cuv), ddy(cuv));

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndl = saturate(dot(N, mainLight.direction));
                float3 ambient = SampleSH(N);
                // Underwater, the direct-light shadow follows the REFRACTED occluder (caustic green,
                // 1 = lit) like the caustics and the pool floor - NOT URP's un-refracted shadow map,
                // which lands offset at depth and drew the second shadow. Above water / no occluder wired:
                // the real shadow map. (URP's shadow on shaders we DON'T own - e.g. Standard Lit - stays
                // un-refracted and we cannot intercept it; use WaterReceiver on submerged objects instead.)
                float lightShadow = (underwater && _CausticOccluderActive > 0.5)
                                    ? OccluderLitFromGreen(poolPos.y, causticSample.g)
                                    : mainLight.shadowAttenuation;
                float3 color = albedo * (ambient + mainLight.color * (ndl * lightShadow));

                // Smoothness-driven specular from the main light (Blinn-Phong with URP's
                // smoothness -> exponent remap). Gated by ndl so a back-lit face never
                // speculates; folded in before downwelling so depth dims it too.
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float3 halfDirWS = normalize(mainLight.direction + viewDirWS);
                float specExponent = exp2(_Smoothness * 10.0 + 1.0);
                float specTerm = pow(saturate(dot(N, halfDirWS)), specExponent) * ndl * lightShadow;
                color += mainLight.color * _SpecColor.rgb * specTerm;

                // Less light reaches the object the deeper it sits (downwelling), applied to
                // the ambient + direct term. No-op above the surface / when the feature is off.
                if (waterMask > 0.5) color *= DownwellingAttenuation(IN.positionWS.y, surfaceY);

                // projected caustics where this point is below the surface AND inside footprint.
                if (underwater)
                {
                    float caustic = causticSample.r;
                    // Caustics soften with depth at their own independent rate (world depth,
                    // consistent with the downwelling term above).
                    float causticFade = DepthFadeScalar(IN.positionWS.y, surfaceY, _CausticDepthFade);
                    // Same refracted occluder shadow the direct light used above, so the shadow and the
                    // caustic ripples stay registered.
                    color += albedo * _CausticTint.rgb * (caustic * _CausticStrength * causticFade * lightShadow);
                    color *= _UnderwaterTint.rgb;
                }

                // depth absorption (shared with the surface so fog is consistent); measured
                // against the sampled surface Y above. Gated on the footprint so fog never
                // tints geometry outside the body.
                if (waterMask > 0.5)
                    color = ApplyWaterFog(color, WaterPathLength(IN.positionWS, _WorldSpaceCameraPos, surfaceY));
                return half4(color, 1);
            }
            ENDHLSL
        }

        // Cast real shadows onto the pool and other objects.
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode"="ShadowCaster" }
            ZWrite On ZTest LEqual ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"

            float3 _LightDirection;
            float3 _LightPosition;

            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct V { float4 positionCS:SV_POSITION; };

            float4 GetShadowPositionHClip(A IN)
            {
                float3 positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                float3 normalWS   = TransformObjectToWorldNormal(IN.normalOS);
            #if _CASTING_PUNCTUAL_LIGHT_SHADOW
                float3 lightDirectionWS = normalize(_LightPosition - positionWS);
            #else
                float3 lightDirectionWS = _LightDirection;
            #endif
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, lightDirectionWS));
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, UNITY_NEAR_CLIP_VALUE);
            #endif
                return positionCS;
            }

            V vert(A IN) { V o; o.positionCS = GetShadowPositionHClip(IN); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // Write depth so SSR / screen-space refraction see the object.
        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode"="DepthOnly" }
            ZWrite On ColorMask 0 Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS:POSITION; };
            struct V { float4 positionCS:SV_POSITION; };

            V vert(A IN) { V o; o.positionCS = TransformObjectToHClip(IN.positionOS.xyz); return o; }
            half4 frag(V IN) : SV_Target { return 0; }
            ENDHLSL
        }

        // Depth-normals prepass so the receiver populates _CameraDepthTexture when a depth-NORMALS
        // (SSAO) prepass is active - with only DepthOnly it vanished from that texture and the
        // volumetric god rays drew over the floor. Depth is what the god-ray occlusion needs.
        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode"="DepthNormals" }
            ZWrite On Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct A { float4 positionOS:POSITION; float3 normalOS:NORMAL; };
            struct V { float4 positionCS:SV_POSITION; float3 normalWS:TEXCOORD0; };

            V vert(A IN)
            {
                V o;
                o.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                return o;
            }
            half4 frag(V IN) : SV_Target { return half4(normalize(IN.normalWS), 0.0); }
            ENDHLSL
        }
    }
}
