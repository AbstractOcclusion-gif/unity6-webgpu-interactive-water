// WebGL Water - Shuriken splash particle rendering (crown + droplets)
//
// Replaces Sprites/Default on the splash emitters so event splashes sit in the same
// light as the water's foam: wrapped sun diffuse over an ambient floor (driven by the
// _LightDir/_SunColor globals the primary WaterVolume publishes), erosion-based
// dissolve driven by the particle's own colorOverLifetime alpha, and a soft fade
// against the opaque scene. Queued after the water surface so ordering is stable.
//
// Two OPTIONAL packed-path upgrades (both default OFF so existing materials keep
// their exact look; the build kit turns them on when it assigns the baked sheets):
//   _SixWay        - six-way directional lightmaps (two extra sheets baked by
//                    gen_splash_flipbook.py) replace the single sun-height scalar,
//                    so the crown shades correctly for ANY sun direction. Baked in
//                    the VerticalBillboard frame: +X = billboard right, +Y = up,
//                    +Z = toward the viewer.
//   _TransmissionStrength - backlit glow: thin spray is strongly forward-scattering,
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
        // Six-way lightmaps (packed path only). A: RGB = lit from +X/+Y/+Z, B: RGB = lit
        // from -X/-Y/-Z, in the billboard frame. Default 0 = keep the scalar foam lighting.
        _SixWay ("Six-Way Lighting (0 off, 1 on)", Float) = 0
        _LightSheetA ("Six-Way Sheet A (+X +Y +Z)", 2D) = "white" {}
        _LightSheetB ("Six-Way Sheet B (-X -Y -Z)", 2D) = "white" {}
        // Set to -1 if the sun appears to come from the wrong side on a vertical billboard
        // (Unity's billboard U orientation is not documented; this flips the baked X axis).
        _SixWayFlipX ("Six-Way Flip X", Float) = 1
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
            // Backlit transmission: how tightly the glow hugs the anti-sun direction, and
            // how fast the packed thickness extinguishes it (thin edges glow, cores do not).
            #define SPLASH_TRANSMISSION_SHARPNESS 4.0
            #define SPLASH_TRANSMISSION_DENSITY   3.0

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D _LightSheetA;
            sampler2D _LightSheetB;
            float4 _Tint;
            float _ParticleOpacity;
            float _SoftFadeDistance;
            float _PackedChannels;
            float _SixWay;
            float _SixWayFlipX;
            float _TransmissionStrength;
            float3 _LightDir; // globals published by the primary WaterVolume (toward the sun)
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
                float4 sixway    : TEXCOORD3; // xyz = light dir in billboard space, w = backlit
            };

            // Sun direction expressed in the VerticalBillboard frame the lightmaps were
            // baked in: +Y = world up, +Z = horizontal toward the camera, +X = right.
            // Constant per particle to within billboard curvature, so per-vertex is enough.
            float3 BillboardSpaceLightDir(float3 worldPos, float3 lightDir)
            {
                float3 up = float3(0.0, 1.0, 0.0);
                float3 toCamera = _WorldSpaceCameraPos - worldPos;
                float3 front = normalize(float3(toCamera.x, 0.0, toCamera.z) + 1e-5);
                float3 right = cross(up, front) * _SixWayFlipX;
                return float3(dot(lightDir, right), lightDir.y, dot(lightDir, front));
            }

            // Blend the six baked lightmaps by how much of the sun comes from each axis.
            float SixWayLight(float3 lightBillboard, float2 uv)
            {
                float4 sheetA = tex2D(_LightSheetA, uv); // lit from +X / +Y / +Z
                float4 sheetB = tex2D(_LightSheetB, uv); // lit from -X / -Y / -Z
                float3 wPos = saturate(lightBillboard);
                float3 wNeg = saturate(-lightBillboard);
                float total = wPos.x + wPos.y + wPos.z + wNeg.x + wNeg.y + wNeg.z + 1e-4;
                float gathered = dot(wPos, sheetA.rgb) + dot(wNeg, sheetB.rgb);
                return gathered / total;
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
                o.fade = float2(wrapped, -mul(UNITY_MATRIX_V, float4(worldPos, 1.0)).z);

                float3 lightDir = normalize(_LightDir + 1e-5);
                float3 viewDir = normalize(worldPos - _WorldSpaceCameraPos + 1e-5);
                float backlit = pow(saturate(dot(viewDir, lightDir)),
                                    SPLASH_TRANSMISSION_SHARPNESS);
                o.sixway = float4(BillboardSpaceLightDir(worldPos, lightDir), backlit);
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

                    // Six-way relight: the baked directional field replaces the sun-height
                    // scalar, so the sun-facing side of the crown brightens and the far side
                    // shades - for any sun azimuth, including behind the splash.
                    if (_SixWay > 0.5)
                    {
                        lit = FoamLitColor(albedo, _SunColor, SixWayLight(i.sixway.xyz, i.uv));
                    }

                    // Backlit forward scatter: thin spray glows when the sun is view-opposed.
                    // exp(-thickness) confines the glow to edges and lace; mass keeps it on
                    // the splash. Free (multiplies to zero) with the sun anywhere else.
                    lit += _SunColor * (_TransmissionStrength * i.sixway.w
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

                return fixed4(lit, alpha);
            }
            ENDCG
        }
    }
}
