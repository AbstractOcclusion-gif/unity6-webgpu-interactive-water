// WebGpuWater - underwater god rays (URP RenderGraph fullscreen).
// The MILESTONE shafts: sunlight beams seen from BELOW the surface, broken by shadows and (next
// increment) the surface caustics, living inside the underwater fog volume. This SUPERSEDES the
// earlier above-water atmosphere use of this shader (that look was a misdirection). It reuses the
// pool GodRays technique (shadow-shaft raymarch) but fullscreen and bounded to the water volume -
// exactly as the underwater fog generalised the pool-box fog to the ocean half-space.
//
// Increment 1: shadow + Henyey-Greenstein phase shafts, marched only along the IN-WATER part of the
// view ray (stops at the scene, the far plane, or the surface for an up-ray), tinted + thinned by the
// shared water fog and downwelling depth. Caustic shimmer arrives next (near-field sim caustic).
//
// Two passes: 0 = raymarch into a half-res target (reads scene depth + main-light shadows via URP
// globals); 1 = additive composite of that target (global _LargeGodRayTex) over the camera colour.
// Runs only when the camera is submerged (the shader early-outs on _CameraUnderwater). Requires the
// URP asset's Depth Texture ON and main-light shadows enabled. All tuning comes from published globals.
Shader "WebGpuWater/LargeBodyGodRays"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        // ---- Pass 0: raymarch the shafts into the half-res target --------------------
        Pass
        {
            Name "LargeBodyGodRaysRaymarch"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragRaymarch
            #pragma target 4.0
            // Sample the main light's shadow MAP (cascades), matching the pool GodRays pass. The
            // screen-space variant is intentionally omitted: it is keyed to opaque-surface depth
            // and would be wrong for arbitrary volumetric samples. Without a shadowmap the pass
            // degrades gracefully to unshadowed shafts.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterVolume.hlsl" // _SimCenter/_SimExtent (window frame) + LARGE_CAUSTIC_REFERENCE_DEPTH
            #include "WaterShared.hlsl" // IOR_*, SafeRefractedLightY (caustic light projection)
            #include "WaterFog.hlsl"    // shared water fog + downwelling helpers/globals (view-fog tint, depth fade)

            float3 _LightDir;   // global, normalized direction toward the sun
            float3 _SunColor;   // global, sun colour * intensity

            // Published by the underwater fog path; reused here so the shafts share the exact submersion
            // state and surface height the fog uses (one source of truth, no separate god-ray copy).
            float _CameraUnderwater;   // 1 when the camera is below the surface
            float _UnderwaterSurfaceY; // world Y of the water surface above the camera

            float4 _LargeGodRayColor;
            float  _LargeGodRayDensity;
            float  _LargeGodRaySteps;
            float  _LargeGodRayAnisotropy;
            float  _LargeGodRayCausticStrength; // near-field surface-caustic shimmer (0 = plain shadow shafts)

            // The body's near-field caustic RT (window frame), published as a global. Sampled by light-
            // projection so the shafts flicker with the surface focusing, like the pool god rays.
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            // Window-border fraction over which the near-field caustic fades to plain shafts (no hard edge).
            #define CAUSTIC_WINDOW_FADE 0.15
            // Shafts are a near/mid-field underwater effect; cap the march to a bounded visible distance
            // rather than the camera far plane (now horizon-sized on an ocean, so averaging over it would
            // dilute the shafts into invisibility). The fog hides anything past this anyway.
            #define SHAFT_MAX_DISTANCE 100.0

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            // Interleaved gradient noise (Jimenez 2014): a stable per-pixel [0,1) dither that turns
            // step-count banding into high-frequency noise the eye averages out across the shafts.
            float InterleavedGradientNoise(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

            // Henyey-Greenstein phase: forward-scattering lobe. g -> 1 sharpens the glow toward the
            // sun. Normalised so _LargeGodRayDensity stays the single intensity control.
            float HenyeyGreenstein(float cosTheta, float g)
            {
                float g2 = g * g;
                float denom = 1.0 + g2 - 2.0 * g * cosTheta;
                return (1.0 - g2) / (4.0 * PI * pow(max(denom, 1e-4), 1.5));
            }

            // Near-field caustic focus at a submerged sample: project it along the refracted sun to the
            // shared reference plane, map into the window frame, sample the caustic RT. Returns 0 beyond
            // the window (plain shafts there), matching how LargeBodyCaustics.shader wrote the RT.
            float LargeBodyCausticAt(float3 p, float3 refractedSun, float refPlaneY)
            {
                float2 projXZ = p.xz + refractedSun.xz * ((refPlaneY - p.y) / SafeRefractedLightY(refractedSun.y));
                float2 windowNorm = (projXZ - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                float2 edge = 1.0 - abs(windowNorm);
                if (edge.x <= 0.0 || edge.y <= 0.0) return 0.0;
                float fade = saturate(min(edge.x, edge.y) / CAUSTIC_WINDOW_FADE);
                float focus = SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, windowNorm * 0.5 + 0.5, 0).r;
                return focus * fade;
            }

            half4 FragRaymarch(Varyings input) : SV_Target
            {
                // Underwater only: these shafts are the view from BELOW the surface. Off above water and
                // when the shafts are disabled. (The feature also gates on an active god-ray ocean.)
                if (_LargeGodRayDensity <= 0.0 || _CameraUnderwater < 0.5) return half4(0.0, 0.0, 0.0, 1.0);

                float rawDepth = SampleSceneDepth(input.uv);
                float3 sceneWorld = ComputeWorldSpacePosition(input.uv, rawDepth, UNITY_MATRIX_I_VP);

                float3 camWorld = _WorldSpaceCameraPos;
                float3 toScene = sceneWorld - camWorld;
                float sceneDist = length(toScene);
                float3 rayDir = toScene / max(sceneDist, 1e-5);

                // Bound the march to the IN-WATER span of the view ray: never past the scene, never past
                // the far plane (sky pixels), and - for an up-facing ray - never past the surface, so a
                // shaft stops where the water ends instead of streaking up into the air.
                float marchDist = min(sceneDist, SHAFT_MAX_DISTANCE);
                if (rayDir.y > 1e-4)
                {
                    float toSurface = (_UnderwaterSurfaceY - camWorld.y) / rayDir.y;
                    if (toSurface > 0.0) marchDist = min(marchDist, toSurface);
                }

                int steps = max(1, (int)_LargeGodRaySteps);
                float dt = marchDist / steps;
                float jitter = InterleavedGradientNoise(input.positionCS.xy);

                // Constant along a straight view ray -> hoisted: the sun glow (phase) and the per-step
                // view-fog factor (Beer-Lambert over one step, per channel so red dies first).
                float phase = HenyeyGreenstein(dot(rayDir, _LightDir), _LargeGodRayAnisotropy);
                float3 viewFogStep = (_WaterFogEnabled > 0.5)
                    ? exp(-_WaterExtinction.rgb * (_WaterFogDensity * dt)) : float3(1.0, 1.0, 1.0);

                // Near-field caustic shimmer: the refracted sun and its reference plane are constant along
                // the straight view ray, so hoist them; each sample then projects onto that plane to read
                // the surface focusing. Skipped entirely when the shimmer is off (strength 0).
                bool wantCaustic = _LargeGodRayCausticStrength > 0.0;
                float3 refractedSun = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);
                float causticRefPlaneY = _UnderwaterSurfaceY - LARGE_CAUSTIC_REFERENCE_DEPTH;

                float3 accum = float3(0.0, 0.0, 0.0);
                float3 viewFog = float3(1.0, 1.0, 1.0); // transmittance from the camera to the current sample
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + jitter) * dt;
                    float3 p = camWorld + rayDir * t;
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    // downwelling: less sun reaches deeper samples (shared depth-darken knob).
                    float depthFade = DepthFadeScalar(p.y, _UnderwaterSurfaceY, _GodRayDepthFade);
                    // surface-focused caustic brightens/flickers the shaft near the camera; neutral far out.
                    float caustic = wantCaustic ? LargeBodyCausticAt(p, refractedSun, causticRefPlaneY) : 0.0;
                    accum += shadow * depthFade * viewFog * (1.0 + caustic * _LargeGodRayCausticStrength);
                    viewFog *= viewFogStep;
                }
                // Average over the samples so shaft brightness is independent of march length (a horizon
                // ray marches far, a floor ray marches metres); density then reads ~O(1).
                accum /= steps;

                float3 col = _LargeGodRayColor.rgb * _SunColor * (accum * _LargeGodRayDensity * phase);
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 1: additive composite of the half-res shafts over the camera colour --
        Pass
        {
            Name "LargeBodyGodRaysComposite"
            Blend One One   // additive glow

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragComposite
            #pragma target 4.0

            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_LargeGodRayTex);
            SAMPLER(sampler_LargeGodRayTex);

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragComposite(Varyings input) : SV_Target
            {
                // _LargeGodRayTex is the half-res shaft target, bound as a global by the raymarch pass.
                return SAMPLE_TEXTURE2D(_LargeGodRayTex, sampler_LargeGodRayTex, input.uv);
            }
            ENDHLSL
        }
    }
}
