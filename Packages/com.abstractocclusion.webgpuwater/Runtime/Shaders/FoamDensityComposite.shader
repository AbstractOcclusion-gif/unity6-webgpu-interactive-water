// WebGL Water - screen-space density-foam composite (KWS-inspired flagship).
//
// The floating foam particles are NOT drawn as quads: WaterFoamParticles.compute splats
// them into a low-res screen-space density buffer (InterlockedAdd) plus a min-depth
// buffer. This fullscreen triangle then:
//   1. reads the density with a 4-neighbour MAX dilation (KWS pass 1: closes pinholes
//      between sparse splats and grows connected foam),
//   2. maps density -> foam with a two-term curve (KWS: a soft "low" film plus a
//      quadratic "high" core, so overlapping particles read as dense white patches),
//   3. occludes against the opaque scene using the splatted min depth (soft), and
//   4. lights the result with the shared foam model, cool-tinted like sea foam.
// Drawn per body via Graphics.RenderPrimitives (3 vertices) after the water surface -
// no render feature, no scene-colour copy, WebGPU-safe (read-only structured buffers
// in the fragment stage; the driver checks device support and falls back to quads).
Shader "AbstractOcclusion/WebGpuWater/FoamDensityComposite"
{
    Properties
    {
        _Tint ("Tint", Color) = (0.75, 0.85, 1.0, 1.0) // KWS's cool sea-foam tint as the default
        _ParticleOpacity ("Opacity", Range(0, 1)) = 1.0
        _DensityLowGain ("Density Low Gain (thin film response)", Range(0, 4)) = 0.6
        _DensityHighGain ("Density High Gain (dense core response)", Range(0, 1)) = 0.15
    }
    SubShader
    {
        // Transparent+5: after the water surface (+0), BEFORE the spray quads (+10) and the
        // splash particles (+10) so airborne droplets draw over the surface-hugging foam layer.
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent+5" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            // Premultiplied-alpha over the scene: dense foam can go solid white (additive
            // alone could only brighten, washing out over bright sky reflections).
            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.5 // structured buffers in the fragment stage
            #include "UnityCG.cginc"
            #include "WaterFoamCommon.hlsl" // FoamLitColor / FoamWrappedDiffuseNdotL

            // KWS curve weights: thin film contributes up to LOW_WEIGHT, the quadratic
            // core up to HIGH_WEIGHT (their 0.2 / 0.5 split).
            #define DENSITY_LOW_WEIGHT   0.2
            #define DENSITY_HIGH_WEIGHT  0.5
            // Soft occlusion band (metres) against the opaque scene depth.
            #define OCCLUSION_SOFTNESS   0.15

            StructuredBuffer<uint> _FoamDensity;      // fixed-point accumulated weight per texel
            StructuredBuffer<uint> _FoamDensityDepth; // min eye depth per texel (millimetres)
            float2 _DensitySize;
            float  _DensityWeightScale;
            float4 _Tint;
            float  _ParticleOpacity;
            float  _DensityLowGain;
            float  _DensityHighGain;
            float3 _LightDir; // globals published by the primary WaterVolume
            float3 _SunColor;
            sampler2D _CameraDepthTexture;

            struct v2f
            {
                float4 pos       : SV_POSITION;
                float2 uv01      : TEXCOORD0; // OUR uv convention: ndc*0.5+0.5, no platform flip -
                                              // matches ProjectToScreen in the splat compute exactly
                float4 screenPos : TEXCOORD1; // platform-correct, for the scene depth tap only
            };

            // Fullscreen triangle from SV_VertexID: (-1,-1) (3,-1) (-1,3) covers the viewport.
            v2f vert(uint vid : SV_VertexID)
            {
                float2 ndc = float2(vid == 1 ? 3.0 : -1.0, vid == 2 ? 3.0 : -1.0);
                v2f o;
                o.pos = float4(ndc, 0.0, 1.0);
                o.uv01 = ndc * 0.5 + 0.5;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            // One density texel, bounds-clamped.
            uint LoadDensity(int2 p)
            {
                p = clamp(p, int2(0, 0), (int2)_DensitySize - 1);
                return _FoamDensity[(uint)p.y * (uint)_DensitySize.x + (uint)p.x];
            }

            uint LoadDepth(int2 p)
            {
                p = clamp(p, int2(0, 0), (int2)_DensitySize - 1);
                return _FoamDensityDepth[(uint)p.y * (uint)_DensitySize.x + (uint)p.x];
            }

            fixed4 frag(v2f i) : SV_Target
            {
                int2 px = (int2)(i.uv01 * _DensitySize);

                // MAX dilation over the 4-neighbourhood (KWS pass 1): closes pinholes and
                // connects sparse splats into patches. The depth takes the matching MIN so
                // dilated texels inherit the nearest contributing particle.
                uint d0 = LoadDensity(px);
                uint dL = LoadDensity(px + int2(-1, 0));
                uint dR = LoadDensity(px + int2( 1, 0));
                uint dD = LoadDensity(px + int2( 0,-1));
                uint dU = LoadDensity(px + int2( 0, 1));
                uint densityRaw = max(d0, max(max(dL, dR), max(dD, dU)));
                if (densityRaw == 0u) return fixed4(0, 0, 0, 0);

                uint depthMm = min(LoadDepth(px),
                              min(min(LoadDepth(px + int2(-1, 0)), LoadDepth(px + int2(1, 0))),
                                  min(LoadDepth(px + int2(0, -1)), LoadDepth(px + int2(0, 1)))));

                // Two-term density curve (KWS): a soft film that saturates early plus a
                // quadratic core that needs real overlap - sparse foam reads as a veil,
                // piles of particles read as solid white.
                float density = densityRaw / max(_DensityWeightScale, 1.0);
                float foamLow  = saturate(density * _DensityLowGain) * DENSITY_LOW_WEIGHT;
                float foamHigh = saturate(density * density * _DensityHighGain) * DENSITY_HIGH_WEIGHT;
                float alpha = saturate(foamLow + foamHigh) * _ParticleOpacity;

                // Soft occlusion against the opaque scene: foam behind geometry fades out
                // over OCCLUSION_SOFTNESS instead of clipping.
                float2 suv = i.screenPos.xy / max(i.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(saturate(suv), 0, 0)));
                float foamEye = depthMm * 0.001;
                alpha *= saturate(1.0 + (sceneEye - foamEye) / OCCLUSION_SOFTNESS);
                if (alpha <= 0.0) return fixed4(0, 0, 0, 0);

                // Shared foam lighting, as upward-facing foam (a screen-space layer has no
                // normal); the default _Tint carries KWS's cool sea-foam cast.
                float wrapped = FoamWrappedDiffuseNdotL(_LightDir.y);
                float3 lit = FoamLitColor(_Tint.rgb, _SunColor, wrapped);
                return fixed4(lit * alpha, alpha); // premultiplied
            }
            ENDCG
        }
    }
}
