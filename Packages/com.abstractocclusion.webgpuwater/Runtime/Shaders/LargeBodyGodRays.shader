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
// Four passes: 0 = raymarch into a half-res persistent history target (reads scene depth + main-
// light shadows via URP globals; animated-jitter march + temporal reprojection accumulation);
// 1+2 = separable Gaussian blur of the shafts; 3 = additive composite of the blurred result
// (global _LargeGodRayTex) over the camera colour. Jitter + temporal + blur are the calm trio -
// few march steps read as many, and fast flicker cannot survive the accumulation.
// Runs only when the camera is submerged (the shader fades in over the first centimetres below the
// surface and early-outs above it - spatial, so wave-driven crossings never pop). Requires the
// URP asset's Depth Texture ON and main-light shadows enabled. All tuning comes from published globals.
Shader "AbstractOcclusion/WebGpuWater/LargeBodyGodRays"
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
            #include "WaterExclusion.hlsl" // dry-interior volumes: marched samples inside are air
            #include "WaterFog.hlsl"    // shared water fog + downwelling helpers/globals (view-fog tint, depth fade)

            float3 _LightDir;   // global, normalized direction toward the sun
            float3 _SunColor;   // global, sun colour * intensity

            // Published by the underwater fog path; reused here so the shafts share the exact submersion
            // state and surface height the fog uses (one source of truth, no separate god-ray copy).
            float _UnderwaterSurfaceY; // world Y of the water surface above the camera

            float4 _LargeGodRayColor;
            float  _LargeGodRayDensity;
            float  _LargeGodRaySteps;
            float  _LargeGodRayAnisotropy;
            float  _LargeGodRayCausticStrength; // near-field surface-caustic shimmer (0 = plain shadow shafts)
            // Depth softening of the caustic shimmer (mip levels per metre below the surface): real
            // caustic light decorrelates with depth, so deep samples should carry broad slow beams,
            // not the razor-sharp surface focus. Mip averaging converges toward the RT's mean, so one
            // depth-scaled LOD gives BOTH the blur and the contrast fade. 0 = legacy sharp-at-any-depth.
            // Needs the caustic RT's mips (WaterCausticsPass generates them for ocean-clipmap bodies);
            // without mips the LOD clamps to 0 and this degrades to the legacy look.
            float  _LargeGodRayCausticDepthSoften;

            // Temporal reprojection (the KWS calm): the pass renders into a persistent history RT and
            // blends each pixel with last frame's value reprojected by scene world position. Combined
            // with the per-frame animated jitter below, the march noise averages out over a few frames
            // and fast flicker physically cannot survive the accumulation. Set by the C# pass:
            // blend = 0 on the first frames, after a resize, and for non-game cameras.
            float4x4 _GodRayPrevVP;        // previous frame's view-projection (GPU convention)
            float4x4 _GodRayCurrVP;        // CURRENT frame's, same construction - see reprojection
            float    _GodRayTemporalBlend; // history weight [0,1); 0 = no accumulation
            float    _GodRayFrame;         // frame counter for the animated jitter
            TEXTURE2D(_LargeGodRayHistory); SAMPLER(sampler_LargeGodRayHistory);

            // The body's near-field caustic RT (window frame), published as a global. Sampled by light-
            // projection so the shafts flicker with the surface focusing, like the pool god rays.
            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            // Window-border fraction over which the near-field caustic fades to plain shafts (no hard edge).
            #define CAUSTIC_WINDOW_FADE 0.15
            // Shafts are a near/mid-field underwater effect; cap the march to a bounded visible distance
            // rather than the camera far plane (now horizon-sized on an ocean, so averaging over it would
            // dilute the shafts into invisibility). The fog hides anything past this anyway.
            #define SHAFT_MAX_DISTANCE 100.0
            // Volumetric caustic reach: KWS-style distance fade (theirs dies by 200m) - far shafts
            // read as steady light, near ones dapple.
            //
            // IMPORTANT - no base-LOD mip floor here, and one smoothing stage per axis ONLY. Over
            // the open ocean nothing casts shadows, so the shadow term is 1 everywhere and the
            // ENTIRE beam structure comes from this caustic term: the surface-focus banding IS the
            // god ray. Stacking a flat mip floor on top of the source band-limit knob (wavelet
            // harshness) and the depth-soften knob (depth calm) flattened the term to near-DC and
            // left only the anisotropic glow - shafts gone. Wavelet filtering belongs to
            // _LargeGodRayCausticSmooth alone; the LARGE-wave banding must reach the march sharp.
            #define GODRAY_CAUSTIC_DISTANCE_FADE  0.005
            // Base calm (the "mix"): in the top few metres the surface focusing is sharpest and
            // most transient - physically correct rays BLINK there, because only some wave
            // configurations focus. Extra mip blur confined to that zone converts the blinking
            // into a steady broad glow, and the gain restores the energy the blur averages away -
            // so the base trades flicker for BREADTH, not for presence, while the beam BODY
            // (below the calm depth) keeps its sharp structure. All three fade out together.
            // KEEP THE ZONE SHALLOW: with the camera near the surface most of a ray's path sits in
            // the top metres, so a deep calm zone blurs the whole BEAM, not just its base (v7's 3-4m
            // zones read as "lost god rays"). One metre treats only the attachment point.
            #define GODRAY_BASE_CALM_DEPTH  1.0  // metres below the surface the calm zone spans
            #define GODRAY_BASE_CALM_LOD    1.0  // extra mip blur right at the surface
            #define GODRAY_BASE_CALM_GAIN   0.3  // energy restored to the blurred base (+30% at 0m)
            // NOTE: KWS also applies a sun-elevation kill (smoothstep(-0.25,1,sunDir.y)). Tried and
            // REMOVED here: it crushed the shafts and the anisotropy glow to ~20% in exactly the
            // low-sun sunset scenes this ocean is built to show off. Low-sun shafts stay.

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
            // 'lod' is the depth-softening mip (see _LargeGodRayCausticDepthSoften).
            float LargeBodyCausticAt(float3 p, float3 refractedSun, float refPlaneY, float lod)
            {
                float2 projXZ = p.xz + refractedSun.xz * ((refPlaneY - p.y) / SafeRefractedLightY(refractedSun.y));
                float2 windowNorm = (projXZ - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                float2 edge = 1.0 - abs(windowNorm);
                if (edge.x <= 0.0 || edge.y <= 0.0) return 0.0;
                float fade = saturate(min(edge.x, edge.y) / CAUSTIC_WINDOW_FADE);
                float focus = SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, windowNorm * 0.5 + 0.5, lod).r;
                return focus * fade;
            }

            // The shafts' submersion fade: zero at the surface, full this many metres below. SPATIAL
            // and current-frame (same pattern as the fog's murk ramp): the binary _CameraUnderwater
            // flag carries the CPU gate's readback staleness and hysteresis, so gating the scatter
            // on it popped the shafts a frame early/late whenever WAVES drove the crossing.
            #define GODRAY_SUBMERGE_FADE_METERS 0.25

            half4 FragRaymarch(Varyings input) : SV_Target
            {
                // Underwater only: these shafts are the view from BELOW the surface. Fade over the
                // first centimetres of submersion instead of switching on the binary flag, so the
                // scatter rises with the water taking the lens rather than popping. (The feature
                // also gates on an active god-ray ocean.)
                float submergeFade = saturate((_UnderwaterSurfaceY - _WorldSpaceCameraPos.y)
                                              / GODRAY_SUBMERGE_FADE_METERS);
                if (_LargeGodRayDensity <= 0.0 || submergeFade <= 0.0) return half4(0.0, 0.0, 0.0, 1.0);

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
                // ANIMATED jitter (Jimenez): shifting the noise pattern every frame turns the static
                // dither into per-frame samples the temporal accumulation below averages - a few
                // marched steps behave like many.
                float jitter = InterleavedGradientNoise(input.positionCS.xy + 5.588238 * _GodRayFrame);

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
                // Sum of the per-sample transmittance weights (rgb mean, so the relative red-first
                // extinction along a ray survives the normalisation below). With fog off every
                // weight is 1 and this equals the step count - byte-identical to the old average.
                float viewFogWeightSum = 0.0;
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float t = (s + jitter) * dt;
                    float3 p = camWorld + rayDir * t;
                    float shadow = MainLightRealtimeShadow(TransformWorldToShadowCoord(p));
                    // Carved presence: a dry volume between this sample and the sun blocks the
                    // direct beam (analytic box shadow, refraction-aware, matching the fog's
                    // in-scatter shadowing).
                    shadow *= ExclusionSunVisibility(p, _LightDir, _UnderwaterSurfaceY);
                    // downwelling: less sun reaches deeper samples (shared depth-darken knob).
                    float depthFade = DepthFadeScalar(p.y, _UnderwaterSurfaceY, _GodRayDepthFade);
                    // surface-focused caustic brightens/flickers the shaft near the camera; neutral far
                    // out, and softened/calmed with the SAMPLE's depth (broad slow beams down deep).
                    // Near the surface the base-calm mix applies (see GODRAY_BASE_CALM_* above):
                    // blur + gain confined to the top metres, sharp beam body below.
                    float depthBelow = max(0.0, _UnderwaterSurfaceY - p.y);
                    float baseCalm = 1.0 - saturate(depthBelow / GODRAY_BASE_CALM_DEPTH);
                    float causticLod = GODRAY_BASE_CALM_LOD * baseCalm
                                     + depthBelow * _LargeGodRayCausticDepthSoften;
                    float caustic = wantCaustic ? LargeBodyCausticAt(p, refractedSun, causticRefPlaneY, causticLod) : 0.0;
                    caustic *= 1.0 + GODRAY_BASE_CALM_GAIN * baseCalm;
                    caustic *= 1.0 - saturate(t * GODRAY_CAUSTIC_DISTANCE_FADE);
                    // Dry-interior exclusion: samples inside an exclusion volume are air - skip their
                    // scatter; the view-fog transmittance still advances along the ray.
                    if (!InsideExclusion(p))
                        accum += shadow * depthFade * viewFog * (1.0 + caustic * _LargeGodRayCausticStrength);
                    viewFogWeightSum += (viewFog.r + viewFog.g + viewFog.b) / 3.0;
                    viewFog *= viewFogStep;
                }
                // SELF-NORMALIZING average: divide by the summed transmittance weights, not the raw
                // step count. The old /steps made shaft brightness scale with the MEAN transmittance
                // over the whole march - with any fog density, most of a 100m march contributes
                // ~nothing yet still counts in the divisor, so the shafts collapsed toward invisible
                // ("we almost lose god rays when fog density > 0"). Weight-normalised, brightness
                // stays O(1) at any density; dense fog instead shifts the STRUCTURE toward the
                // near-camera dapple (the KWS look - their rays die by 200m but the near field stays
                // lively). Fog off: every weight is 1, the divisor equals the step count, and this
                // is byte-identical to the old average. The rgb-mean weight keeps the relative
                // red-first spectral loss along the ray; the floor guards a fully-extinct march.
                accum /= max(viewFogWeightSum, 1e-4);

                float3 col = _LargeGodRayColor.rgb * _SunColor * (accum * _LargeGodRayDensity * phase);
                col *= submergeFade; // submersion fade (see GODRAY_SUBMERGE_FADE_METERS above)

                // Temporal accumulation: blend with last frame's value at this scene point. The history
                // is the pre-blur RT (ping-ponged by the C# pass), so accumulation sharpness is kept
                // and the blur only shapes the composited result. Off-screen history = fresh value.
                if (_GodRayTemporalBlend > 0.0)
                {
                    // SELF-CALIBRATING reprojection: project this pixel's scene point through BOTH
                    // frames' matrices with identical math and apply only the DELTA to the raster
                    // uv. Any constant convention mismatch (y-flip, half-texel, GL-vs-D3D clip)
                    // cancels exactly - a static camera reprojects onto itself by construction.
                    // (The earlier absolute-uv form with a UNITY_UV_STARTS_AT_TOP flip guessed the
                    // convention wrong here, and with feedback the mismap DISSOLVED the shafts into
                    // a dim haze over a few frames. Never reproject absolutely; always delta.)
                    float4 currClip = mul(_GodRayCurrVP, float4(sceneWorld, 1.0));
                    float4 prevClip = mul(_GodRayPrevVP, float4(sceneWorld, 1.0));
                    if (currClip.w > 1e-4 && prevClip.w > 1e-4)
                    {
                        // Both matrices are built with GetGPUProjectionMatrix(renderIntoTexture:
                        // true), which bakes the platform y-flip into clip space - so the ndc
                        // delta maps to uv with a plain 0.5 scale on every backend.
                        float2 delta = prevClip.xy / prevClip.w - currClip.xy / currClip.w;
                        float2 prevUV = input.uv + delta * 0.5;
                        if (prevUV.x > 0.0 && prevUV.x < 1.0 && prevUV.y > 0.0 && prevUV.y < 1.0)
                        {
                            float3 history = SAMPLE_TEXTURE2D_LOD(_LargeGodRayHistory,
                                                 sampler_LargeGodRayHistory, prevUV, 0).rgb;
                            col = lerp(col, history, _GodRayTemporalBlend);
                        }
                    }
                }
                return half4(col, 1.0);
            }
            ENDHLSL
        }

        // ---- Passes 1+2: separable Gaussian blur of the half-res shafts (the KWS pyramid-blur
        // equivalent) - the third calm pillar after jitter + temporal accumulation. Linear-sampled
        // 9-tap Gaussian in two directions; the composite reads the blurred result. --------------
        Pass
        {
            Name "LargeBodyGodRaysBlurH"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 4.0
            #define BLUR_DIR float2(1.0, 0.0)
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_GodRayBlurSrc); SAMPLER(sampler_GodRayBlurSrc);
            float4 _GodRayBlurSrc_TexelSize;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragBlur(Varyings input) : SV_Target
            {
                // Linear-sampled 9-tap Gaussian: 5 fetches, bilinear does the pairing.
                float2 step = BLUR_DIR * _GodRayBlurSrc_TexelSize.xy;
                half3 c = SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv, 0).rgb * 0.227027;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 3.230769, 0).rgb * 0.070270;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 3.230769, 0).rgb * 0.070270;
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "LargeBodyGodRaysBlurV"
            Blend One Zero

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            #pragma target 4.0
            #define BLUR_DIR float2(0.0, 1.0)
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"

            TEXTURE2D(_GodRayBlurSrc); SAMPLER(sampler_GodRayBlurSrc);
            float4 _GodRayBlurSrc_TexelSize;

            struct Attributes { uint vertexID : SV_VertexID; };
            struct Varyings   { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };

            Varyings Vert(Attributes IN)
            {
                Varyings o;
                o.positionCS = GetFullScreenTriangleVertexPosition(IN.vertexID);
                o.uv = GetFullScreenTriangleTexCoord(IN.vertexID);
                return o;
            }

            half4 FragBlur(Varyings input) : SV_Target
            {
                float2 step = BLUR_DIR * _GodRayBlurSrc_TexelSize.xy;
                half3 c = SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv, 0).rgb * 0.227027;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 1.384615, 0).rgb * 0.316216;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv + step * 3.230769, 0).rgb * 0.070270;
                c += SAMPLE_TEXTURE2D_LOD(_GodRayBlurSrc, sampler_GodRayBlurSrc, input.uv - step * 3.230769, 0).rgb * 0.070270;
                return half4(c, 1.0);
            }
            ENDHLSL
        }

        // ---- Pass 3: additive composite of the half-res shafts over the camera colour --
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
