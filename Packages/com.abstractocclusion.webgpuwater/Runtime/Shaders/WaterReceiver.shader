// WebGpuWater - lit receiver for interactable objects (Unity 6 / URP port)
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
        [Header(Wetness)]
        // Master. 0 = every wetness term is skipped and this material shades exactly as it did
        // before the feature existed - which is what keeps the pool-SHELL receivers safe.
        _WetStrength ("Wetness", Range(0,1)) = 0
        _WetBandHeight ("Wet Band Above Waterline (m)", Range(0,2)) = 0.15
        _WetDarken ("Porous Darkening", Range(0,1)) = 0.7
        _WetSmoothness ("Wet Smoothness", Range(0,1)) = 0.8
        _WetNormalFlatten ("Wet Normal Flatten", Range(0,1)) = 0.6
        _WetSwashStrength ("Wet From Beach Swash", Range(0,1)) = 1
        _WetFoamStrength ("Wet From Foam", Range(0,1)) = 0.5
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

            // Mark these pixels so the screen-space WaterCausticProjection pass SKIPS them (this shader
            // already adds caustics in-shader below - the pass must not add them a second time). Bit 3 is
            // in URP's user stencil range (StencilUsage.UserMask = bits [0,3]); WriteMask 8 touches only it.
            Stencil { Ref 8 WriteMask 8 Comp Always Pass Replace }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            // ONE 4-way set, exactly as URP's own Lit.shader declares it. URP sets these keywords
            // mutually exclusively, so two independent pragmas were compiling a 3x2 cross product in
            // which both *_SCREEN combinations are unreachable states.
            // _fragment on all of them: every consumer of these keywords lives in frag, so the
            // unscoped forms were also compiling a separate, byte-identical vertex program per combination.
            #pragma multi_compile_fragment _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma shader_feature_local_fragment _AUTOTILEOBJSIZE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "WaterFog.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl" // IOR_*, ProjectCausticUV
            #include "WaterCausticMap.hlsl" // ResolveCausticMap - frame-aware caustic uv/footprint
            #include "WaterFoamMask.hlsl" // SimFoamCoverage (also declares _WaterTexel for us)
            #include "WaterShore.hlsl"    // ShoreSample / ShoreData - the baked shore substrate
            #include "WaterSurfWaves.hlsl" // EvaluateSurfSwash + _SurfBeatTime (pure math, no samplers)
            #include "WaterWetness.hlsl"  // THE wetness model, shared with the terrain shader

            TEXTURE2D(_BaseMap);    SAMPLER(sampler_BaseMap);
            TEXTURE2D(_BumpMap);    SAMPLER(sampler_BumpMap);
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            TEXTURE2D(_WaterTex);   SAMPLER(sampler_WaterTex);
            float3 _LightDir;   // global "toward the light", driven from the Unity sun
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
                float _WetStrength;
                float _WetBandHeight;
                float _WetDarken;
                float _WetSmoothness;
                float _WetNormalFlatten;
                float _WetSwashStrength;
                float _WetFoamStrength;
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
                // THE SIM'S OWN FRAME. On a windowed (large) body the ripple sim covers a
                // camera-following WINDOW, not the whole body, so pool xz addresses the wrong texels
                // entirely - and because the window follows the camera, the error travels with you.
                // Every other consumer already indexes it this way (WaterSurfaceVertStage:74, the
                // chunk wall, the foam particles); these two shaders were the last reading pool xz.
                //
                // THE HEIGHT UNIT IS UNAFFECTED, which is what keeps this a three-line change:
                // SimHalfExtent.y IS VolumeExtentSafe.y (WaterVolume.Frames.cs:120-123), so a sampled
                // height still converts through the volume frame in both cases.
                float3 simPos = (_SimWindowed > 0.5) ? WorldToSim(IN.positionWS) : poolPos;
                float2 wuv = simPos.xz * 0.5 + 0.5;
                // Outside the sim's coverage there is no ripple data at all - a clamped read there
                // repeats the border texel across the whole world, which is the same class of bug the
                // foam window fade exists to stop.
                float simCovered = (max(abs(simPos.x), abs(simPos.z)) <= 1.0) ? 1.0 : 0.0;
                float simH = SampleWaterHeightBilinear(wuv) * simCovered;
                float surfaceY = PoolToWorld(float3(poolPos.x, simH, poolPos.z)).y;
                bool underwater = (waterMask > 0.5 && poolPos.y < simH);

                // Wetness. The hard 'underwater' bool above still gates the tint and the caustics;
                // this is its CONTINUOUS sibling, so a surface stops being bone dry one texel above
                // the waterline. Both are built from the same sampled simH, so the feathered weight
                // and the hard bool cannot disagree along their shared contour - the wet line simply
                // refuses to fall below wetFloorY, the drying high-water mark, as a trough passes.
                float wet = 0.0;
                if (_WetStrength > 0.0 && waterMask > 0.5)
                {
                    // THE WET LINE'S FLOOR. markH is the sim's high-water memory for this column
                    // (pool height units, drying toward 0 = the still level), taken through the SAME
                    // volume frame as surfaceY so rotation and non-uniform extents can never make the
                    // two heights disagree. Because markH can never go below 0, this is EXACTLY the
                    // still plane when there is no memory yet, when the foam pass is idle, or when the
                    // buffer is the black fallback - the no-memory behaviour needs no separate path.
                    // Read HERE, not in the prologue: it costs four taps, and every receiver material
                    // ships with wetness off.
                    float markH = (_WetMarkActive > 0.5 && simCovered > 0.5) ? SampleWetMarkWindowed(wuv) : 0.0;
                    float wetFloorY = PoolToWorld(float3(poolPos.x, markH, poolPos.z)).y;
                    float bandWet = WaterWetBand(IN.positionWS.y, surfaceY, wetFloorY, _WetBandHeight);

                    // Beach swash: the SAME closed form on the SAME clock (_SurfBeatTime) the water
                    // mesh's glaze runs on, so a rock at the waterline cannot dry out of step with
                    // the sand around it. ShoreSample returns inert off-field / on unbaked bodies,
                    // and EvaluateSurfSwash returns 0 there, so this collapses to nothing on a pool.
                    ShoreData shore = ShoreSample(IN.positionWS.xz);
                    float2 swash = (_SurfActive > 0.5)
                        ? EvaluateSurfSwash(IN.positionWS.xz, shore.toShore, shore.slopeTan,
                                            shore.influence, _SurfBeatTime)
                        : float2(0.0, 0.0);
                    // shore.depth is + in water and - on dry land, so -depth is height above the
                    // still plane: exactly the quantity the surface's glaze calls beachRise.
                    float swashWet = WaterWetSwash(-shore.depth, swash.y) * _WetSwashStrength;

                    // Foam through the package's one coverage formula, read at the SAME pool-frame
                    // UV as the height above so the foam can never disagree with the waterline it
                    // is drawn against.
                    // poolPos.xz stays POOL space here on purpose: SimFoamCoverage uses it only for
                    // the wall-border term, which is a whole-body concept (a scrolling window has no
                    // walls). Only the advection lookup moves to the sim frame.
                    float foamWet = (_FoamEnabled > 0.5 && simCovered > 0.5)
                        ? WaterWetFoam(SimFoamCoverage(poolPos.xz, wuv, 0.0)) * _WetFoamStrength
                        : 0.0;

                    wet = _WetStrength * WaterWetCombine(bandWet, swashWet, foamWet);
                }

                WaterWetLook wetLook;
                wetLook.darken = _WetDarken;
                wetLook.smoothness = _WetSmoothness;
                wetLook.normalFlatten = _WetNormalFlatten;
                // Flattens toward the GEOMETRIC normal, not the normal-mapped one - the film lies
                // over the micro-relief, which is what the normal map encodes.
                WaterWetSurface wetSurface = WaterApplyWetness(wetLook, wet, albedo, _Smoothness,
                                                               N, vertexNormalWS);
                albedo = wetSurface.albedo;
                N = wetSurface.normal;
                float smoothness = wetSurface.smoothness;

                // Caustic map sampled ONCE up front with explicit gradients (WGSL-safe: an implicit-
                // derivative sample inside the per-fragment waterline branch below is undefined on WebGPU).
                // Green = this body's refracted occluder shadow (1 = lit); red = the caustic pattern.
                // FRAME-AWARE caustic map (shared with WaterTerrain / the screen-space projection):
                // a receiver can sit under an OCEAN, whose caustic RT is written in the sim WINDOW's
                // frame rather than the pool box. On a POOL body the resolver returns the identical
                // uv, gradScale 1 and footprint 1 wherever 'underwater' can be true (it requires
                // waterMask > 0.5, hence FootprintMaskPool == 1) - pool look unchanged by construction.
                WaterCausticMap causticMap = ResolveCausticMap(IN.positionWS, poolPos, _LightDir);
                float2 cuv = causticMap.uv;
                float4 causticSample = SAMPLE_TEXTURE2D_GRAD(_CausticTex, sampler_CausticTex, cuv,
                                                             ddx(cuv) * causticMap.gradScale,
                                                             ddy(cuv) * causticMap.gradScale);

                float4 shadowCoord = TransformWorldToShadowCoord(IN.positionWS);
                Light mainLight = GetMainLight(shadowCoord);
                float ndl = saturate(dot(N, mainLight.direction));
                float3 ambient = SampleSH(N);
                // Underwater, the direct-light shadow follows the REFRACTED occluder (caustic green,
                // 1 = lit) like the caustics and the pool floor - NOT URP's un-refracted shadow map,
                // which lands offset at depth and drew the second shadow. Above water / no occluder wired:
                // the real shadow map. (URP's shadow on shaders we DON'T own - e.g. Standard Lit - stays
                // un-refracted and we cannot intercept it; use WaterReceiver on submerged objects instead.)
                float lightShadow = mainLight.shadowAttenuation;
                if (underwater && _CausticOccluderActive > 0.5)
                {
                    // Shared distance-grown PCF penumbra (WaterShared): four extra explicit-LOD
                    // taps around the silhouette (branch-safe); radius 0 collapses onto the
                    // centre sample = the legacy look.
                    float occRadius = OccluderPenumbraRadiusUV(poolPos.y);
                    float4 occGreens = float4(
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP0 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP1 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP2 * occRadius, 0).g,
                        SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv + OCCLUDER_PCF_TAP3 * occRadius, 0).g);
                    lightShadow = OccluderLitFromGreenPCF(poolPos.y, causticSample.g, occGreens);
                }
                float3 color = albedo * (ambient + mainLight.color * (ndl * lightShadow));

                // Smoothness-driven specular from the main light (Blinn-Phong with URP's
                // smoothness -> exponent remap). Gated by ndl so a back-lit face never
                // speculates; folded in before downwelling so depth dims it too.
                float3 viewDirWS = normalize(GetWorldSpaceViewDir(IN.positionWS));
                float3 halfDirWS = normalize(mainLight.direction + viewDirWS);
                float specExponent = WaterSpecularExponent(smoothness);
                float specTerm = pow(saturate(dot(N, halfDirWS)), specExponent) * ndl * lightShadow;
                // Restore the energy the narrowing lobe would otherwise throw away - without this a
                // wet surface gets a SMALLER highlight than a dry one, not a brighter one. Exactly
                // 1.0 while dry, so an opted-out material is unchanged.
                float specGain = WaterWetSpecularGain(WaterSpecularExponent(_Smoothness), specExponent);
                color += mainLight.color * _SpecColor.rgb * (specTerm * specGain);

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
                    color += albedo * _CausticTint.rgb * (caustic * _CausticStrength * causticFade
                                                         * lightShadow * causticMap.footprint);
                    color *= _UnderwaterTint.rgb;
                }

                // depth absorption (shared with the surface so fog is consistent); measured
                // against the sampled surface Y above. Gated on the footprint so fog never
                // tints geometry outside the body.
                if (waterMask > 0.5)
                    color = ApplyWaterFog(color, WaterPathLength(IN.positionWS, _WorldSpaceCameraPos, surfaceY),
                                          WaterInscatterColor(normalize(_WorldSpaceCameraPos - IN.positionWS),
                                                              _LightDir, _SunColor, 0.0));
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
