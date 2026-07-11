// WebGL Water - Shuriken splash particle rendering (crown + droplets)
//
// Replaces Sprites/Default on the splash emitters so event splashes sit in the same
// light as the water's foam: wrapped sun diffuse over an ambient floor (driven by the
// _LightDir/_SunColor globals the primary WaterVolume publishes), erosion-based
// dissolve driven by the particle's own colorOverLifetime alpha, and a soft fade
// against the opaque scene. Queued after the water surface so ordering is stable.
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
            #include "UnityCG.cginc"
            // Foam lighting + erosion dissolve, matched to WaterSurface/FoamParticles so
            // every foam-like element in the scene shades consistently.
            #include "WaterFoamCommon.hlsl"

            // Packed-path look constants (KWS splash): shine is CUBED for tight sparkle then
            // boosted; the soft-fade band stretches with the packed thickness so thin splash
            // edges dissolve against intersections while thick cores hold.
            #define SPLASH_SHINE_GAIN      3.0
            #define SPLASH_SOFT_FADE_THIN  0.5
            #define SPLASH_SOFT_FADE_THICK 1.5

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _Tint;
            float _ParticleOpacity;
            float _SoftFadeDistance;
            float _PackedChannels;
            float3 _LightDir; // globals published by the primary WaterVolume
            float3 _SunColor;
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
            };

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
                o.fade = float2(wrapped, -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float4 sprite = tex2D(_MainTex, i.uv);
                float envelope = i.color.a;

                // Lit base is shared by both paths: the sprite's true color is flat _Tint
                // (legacy sheets are premultiplied; packed sheets carry data, not color).
                float3 lit = FoamLitColor(_Tint.rgb * i.color.rgb, _SunColor, i.fade.x);

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
                    // ---- Legacy path: shape in A, erosion dissolve driven by the lifetime alpha. ----
                    alpha = FoamErosionAlpha(sprite.a, envelope);
                    alpha *= envelope * _ParticleOpacity;
                    alpha *= saturate(behind / _SoftFadeDistance);
                }

                return fixed4(lit, alpha);
            }
            ENDCG
        }
    }
}
