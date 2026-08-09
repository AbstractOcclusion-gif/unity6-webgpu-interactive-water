// WebGpuWater - Shuriken splash particle rendering (crown + droplets)
//
// Replaces Sprites/Default on the splash emitters so event splashes sit in the same
// light as the water's foam: wrapped sun diffuse over an ambient floor (driven by the
// _LightDir/_SunColor globals the primary WaterVolume publishes), erosion-based
// dissolve driven by the particle's own colorOverLifetime alpha, and a soft fade
// against the opaque scene. Queued after the water surface so ordering is stable.
//
// Optional packed-path transmission adds a backlit glow: thin spray is strongly forward-scattering,
//                    so when the sun sits behind the splash its thin parts light up
//                    (uses the packed thickness channel; free at other sun angles).
//
// Works with standard Shuriken vertex data (position/color/uv), including the crown's
// Texture Sheet Animation - no custom vertex streams required.
Shader "AbstractOcclusion/WebGpuWater/SplashParticles"
{
    Properties
    {
        _MainTex ("Sprite (or flipbook sheet)", 2D) = "white" {}
        _Tint ("Tint", Color) = (0.95, 0.98, 1.0, 1.0)
        _ParticleOpacity ("Opacity", Range(0, 1)) = 1.0
        _SoftFadeDistance ("Soft Fade vs Scene Depth (world)", Range(0.001, 0.5)) = 0.05
        // 0 = legacy sprite (RGB tint carrier, A = shape). 1 = KWS-style channel packing:
        // R = mass (opacity shape), G = shine (specular sparkle, cubed), B = dissolve noise
        // (lifetime erosion threshold), A = thickness (soft-fade band). Default 0 so existing
        // materials with legacy textures keep their exact look; the build kit sets 1 when it
        // assigns the packed textures.
        _PackedChannels ("Packed Channels (0 legacy, 1 packed)", Float) = 0
        // Backlit forward-scatter glow through thin spray (packed path only). 0 = off.
        _TransmissionStrength ("Backlit Transmission", Range(0, 3)) = 0
    }
    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+10" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            // Foam lighting + erosion dissolve, matched to WaterSurface/FoamParticles so
            // every foam-like element in the scene shades consistently.
            #include "WaterFoamCommon.hlsl"
            // Dry-interior exclusion volumes (globals from WaterUniformPublisher): the crown
            // and CPU-fallback droplets were the ONE foam element with no exclusion awareness,
            // so a splash at a hull edge painted straight through the dry box. Per-fragment
            // dissolve here, since a crown billboard can straddle the boundary.
            #include "WaterExclusion.hlsl"
            // After-fog reroute frames: the emitter's systems draw AFTER the fullscreen fog
            // (forceRenderingOff + DrawAfterFog), so the sprite prices its own camera->splash
            // wet path here. Identity mul/add on fog-off frames - queue-time look untouched.
            #include "WaterParticleFog.hlsl"

            // Packed-path look constants (KWS splash): shine is CUBED for tight sparkle then
            // boosted; the soft-fade band stretches with the packed thickness so thin splash
            // edges dissolve against intersections while thick cores hold.
            #define SPLASH_SHINE_GAIN      3.0
            #define SPLASH_SOFT_FADE_THIN  0.5
            #define SPLASH_SOFT_FADE_THICK 1.5
            // Backlit transmission: how tightly the glow hugs the anti-sun direction, and
            // how fast the packed thickness extinguishes it (thin edges glow, cores do not).
            #define SPLASH_TRANSMISSION_SHARPNESS 4.0
            #define SPLASH_TRANSMISSION_DENSITY   3.0

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Tint;
            float _ParticleOpacity;
            float _SoftFadeDistance;
            float _PackedChannels;
            float _TransmissionStrength;
            float3 _LightDir; // globals published by the primary WaterVolume (toward the sun)
            // _SunColor comes from WaterFog.hlsl, reached TRANSITIVELY via WaterParticleFog.hlsl - declaring it here again is a redefinition.
            float _CameraUnderwater;
            sampler2D _CameraDepthTexture;

            struct appdata
            {
                float4 vertex : POSITION;
                fixed4 color  : COLOR;     // Shuriken per-particle color (incl. colorOverLifetime)
                float2 uv     : TEXCOORD0; // Texture Sheet Animation writes the flipbook frame here
            };

            struct v2f
            {
                float4 pos       : SV_POSITION;
                fixed4 color     : COLOR;
                float2 uv        : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
                float2 fade      : TEXCOORD2; // x = lit sun factor, y = fragment eye depth
                float backlit    : TEXCOORD3;
                float3 worldPos  : TEXCOORD4; // for the per-fragment exclusion dissolve
                float3 fogMul    : TEXCOORD5; // camera->splash fog transmittance (1 when fog is off)
                float3 fogAdd    : TEXCOORD6; // camera->splash fog in-scatter (0 when fog is off)
                float sceneFogFactor : TEXCOORD7;
            };

            float SceneFogFactor(float eyeDepth)
            {
                #if defined(FOG_LINEAR)
                    return saturate(eyeDepth * unity_FogParams.z + unity_FogParams.w);
                #elif defined(FOG_EXP)
                    return saturate(exp2(-unity_FogParams.y * eyeDepth));
                #elif defined(FOG_EXP2)
                    float fogDepth = unity_FogParams.x * eyeDepth;
                    return saturate(exp2(-fogDepth * fogDepth));
                #else
                    return 1.0;
                #endif
            }

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.color = v.color;
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.screenPos = ComputeScreenPos(o.pos);
                // Splash sheets/droplets have no meaningful normal; light them as
                // upward-facing foam so brightness tracks the sun's height and color.
                float wrapped = FoamWrappedDiffuseNdotL(_LightDir.y);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                float eyeDepth = -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z;
                o.fade = float2(wrapped, eyeDepth);
                o.sceneFogFactor = SceneFogFactor(eyeDepth);

                float3 lightDir = normalize(_LightDir + 1e-5);
                float3 viewDir = normalize(worldPos - _WorldSpaceCameraPos + 1e-5);
                float backlit = pow(saturate(dot(viewDir, lightDir)),
                                    SPLASH_TRANSMISSION_SHARPNESS);
                o.backlit = backlit;
                o.worldPos = worldPos;
                ParticleUnderwaterFog(worldPos, lightDir, _SunColor, o.fogMul, o.fogAdd);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 sprite = tex2D(_MainTex, i.uv);
                float envelope = i.color.a;

                // Lit base is shared by both paths: the sprite's true color is flat _Tint
                // (legacy sheets are premultiplied; packed sheets carry data, not color).
                float3 albedo = _Tint.rgb * i.color.rgb;
                float3 lit = FoamLitColor(albedo, _SunColor, i.fade.x);

                // soft fade against the opaque scene (pool walls, floating objects)
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0, 0)));
                float behind = sceneEye - i.fade.y;

                float alpha;
                if (_PackedChannels > 0.5)
                {
                    // ---- KWS-packed path: R mass / G shine / B dissolve noise / A thickness. ----
                    // The noise channel is a burn threshold: as the lifetime envelope decays the
                    // splash DISINTEGRATES into its own turbulence pattern instead of ghosting out.
                    float dissolve = FoamErosionAlpha(sprite.b, envelope);
                    alpha = sprite.r * dissolve * envelope * _ParticleOpacity;

                    // Backlit forward scatter: thin spray glows when the sun is view-opposed.
                    // exp(-thickness) confines the glow to edges and lace; mass keeps it on
                    // the splash. Free (multiplies to zero) with the sun anywhere else.
                    lit += _SunColor * (_TransmissionStrength * i.backlit
                                        * exp(-sprite.a * SPLASH_TRANSMISSION_DENSITY)
                                        * sprite.r * envelope);

                    // Thickness-aware soft fade: thin edges vanish first at intersections.
                    float fadeBand = _SoftFadeDistance
                                   * lerp(SPLASH_SOFT_FADE_THIN, SPLASH_SOFT_FADE_THICK, sprite.a);
                    alpha *= saturate(behind / fadeBand);

                    // Cubed shine: tight sun-lit sparkle on the droplet cores.
                    float shine = sprite.g;
                    lit += _SunColor * (shine * shine * shine * SPLASH_SHINE_GAIN * envelope);
                }
                else
                {
                    // ---- Legacy path: shape in A, texture-preserving erosion driven by the
                    // lifetime alpha (gate-only erosion saturated the sheet into a disc). ----
                    alpha = FoamErosionLace(sprite.a, envelope);
                    alpha *= envelope * _ParticleOpacity;
                    alpha *= saturate(behind / _SoftFadeDistance);
                }

                // Dry-interior exclusion: dissolve the fragments that protrude into a dry
                // volume (per-volume fade band; volumes can opt their particles out). Applied
                // to BOTH texture paths, after their alpha shaping, so the cut ignores the
                // packing mode. Shuriken knows nothing of the volumes, so unlike the GPU
                // particles there is no sim-side kill to lean on - this IS the cull.
                if (_ExclusionCount > 0.5)
                    alpha *= ExclusionParticleAttenuation(i.worldPos);

                // Per-splash underwater fog (identity on fog-off frames), after all the lit
                // terms - shine and transmission fog out with the rest of the sprite.
                lit = lit * i.fogMul + i.fogAdd;
                if (_CameraUnderwater < 0.5)
                    lit = lerp(unity_FogColor.rgb, lit, i.sceneFogFactor);

                return fixed4(lit, alpha);
            }
            ENDCG
        }
    }
}
