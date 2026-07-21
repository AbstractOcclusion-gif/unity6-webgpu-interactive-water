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
// REQUIRES "Transparent Receive Shadows" ENABLED on the active URP asset: this pass is in the
// Transparent queue, and URP only feeds the main-light shadow to transparent passes when that
// toggle is on. With it OFF, MainLightRealtimeShadow returns "lit" for every marched sample and
// the shadow shafts silently vanish (they then only read where caustics brighten, not on calm
// water). This bit us on the Mobile URP asset / WebGPU build, where the toggle defaulted off.
//
// Renders after the water (Transparent+100), additively, ignoring the water surface
// depth; occlusion against solid geometry is done per-step against the camera depth
// texture (opaque geometry only).
Shader "AbstractOcclusion/WebGpuWater/GodRays"
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
            // _SHADOWS_SOFT is intentionally NOT compiled here: the march already averages
            // many samples along the ray, so multi-tap soft shadows per step cost several
            // extra shadowmap fetches per pixel for no visible gain in a volumetric.
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterVolume.hlsl"
            #include "WaterShared.hlsl" // IOR_*, IntersectCube, ProjectCausticUV
            #include "WaterExclusion.hlsl" // dry-interior volumes: marched samples inside are air
            #include "WaterFog.hlsl"    // DepthFadeScalar, WaterPathLength, fog uniforms

            TEXTURE2D(_CausticTex); SAMPLER(sampler_CausticTex);
            float3 _LightDir;       // global, normalized direction toward the light
            float3 _SunColor;       // global, sun colour * intensity
            float _CausticOccluderActive; // 1 when submerged objects wrote the refracted occluder shadow into caustic.g

            // Interleaved gradient noise (Jimenez, "Next Generation Post Processing in
            // Call of Duty" 2014): a stable per-pixel value in [0,1) from the pixel coords.
            // Used to jitter each pixel's march start by a fraction of one step, which
            // converts step-count banding into high-frequency noise that the additive
            // accumulation visually averages out - so a tier can run roughly half the
            // steps at equal perceived quality.
            float InterleavedGradientNoise(float2 pixel)
            {
                return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
            }

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
                // The GodRayBox mesh is authored in POOL space ([-1,1] in x,z, [-1,0] in y), so its
                // vertices ARE the pool-space box position - place it purely by the volume frame, exactly
                // like WaterSurface/AnalyticPool. The old TransformObjectToWorld baked the renderer's own world
                // transform in first, which double-counted once the body was moved off origin (or extended):
                // the box then no longer scaled/tracked the pond, and the fragment's pool-space march
                // (WorldToPool + IntersectCube in [-1,1]) received world coords instead of pool coords.
                float3 poolPos = IN.positionOS.xyz;
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
                // Open-water bodies suppress this pool-box god-ray volume; the large-body render
                // feature provides scalable shafts instead. Keeps this pass free of any pool
                // assumption when the surface is standing alone. (_LargeBody defaults to 0.)
                if (_LargeBody > 0.5) return half4(0, 0, 0, 0);

                // March in POOL space: caustics + the box bounds live there. Convert each
                // sample back to world only for the depth test and the shadow lookup.
                float3 roPool = WorldToPool(_WorldSpaceCameraPos);
                float3 rdPool = normalize(IN.poolPos - roPool);

                float2 t = IntersectCube(roPool, rdPool, POOL_WATER_BOX_MIN, POOL_WATER_BOX_MAX);
                float tEnter = max(t.x, 0.0);
                float tExit  = t.y;
                if (tExit <= tEnter) return half4(0, 0, 0, 0);

                float2 uv = IN.screenPos.xy / max(IN.screenPos.w, 1e-5);
                float sceneEye = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);

                int steps = max(1, (int)_GodRaySteps); // guard against divide-by-zero at 0 steps
                float dt = (tExit - tEnter) / steps;
                float3 refractedLight = -refract(-_LightDir, float3(0, 1, 0), IOR_AIR / IOR_WATER);
                // Pool-space refracted ray for ProjectCausticUV: its xz/y ratio is only valid in pool
                // space, so a WORLD direction mis-projects mid-water samples on non-uniform (deep) bodies -
                // the shafts then drift off the floor/caustic shadow with depth. Hoisted out of the march
                // (constant per fragment); uniform extents preserve the ratio, so those stay byte-identical.
                float3 poolRefract = WorldDirToPool(refractedLight);
                float surfaceLevel = _VolumeCenter.y; // world Y of the water surface

                // Dithered march: per-pixel [0,1) start offset (see InterleavedGradientNoise).
                // SV_POSITION.xy in the fragment stage is the pixel centre.
                float jitter = InterleavedGradientNoise(IN.positionCS.xy);

                // Hoisted exponentials: along a fixed ray, both the view-path fog and the
                // depth fade are exp() of arguments LINEAR in t (every marched sample sits
                // below the surface, so the max(0, depth) clamps never engage mid-march).
                // Evaluate once at the first sample, then advance per step by a constant
                // multiplicative factor - removes 4 exp() and a path-length solve from
                // every marched sample (this pass can cover the whole screen).
                float3 firstPool = roPool + rdPool * (tEnter + jitter * dt);
                float3 firstWorld = PoolToWorld(firstPool);
                float3 secondWorld = PoolToWorld(firstPool + rdPool * dt);
                float stepRiseY = secondWorld.y - firstWorld.y; // world dY per step (sign matters)

                // Depth fade of the shaft (light thinned on its way down), master-switch gated.
                float depthFade = DepthFadeScalar(firstWorld.y, surfaceLevel, _GodRayDepthFade);
                float depthFadeStep = (_DepthDarkenEnabled > 0.5) ? exp(_GodRayDepthFade * stepRiseY) : 1.0;

                // View-path fog: per-channel so receding shafts tint like everything else
                // (red dies first). Gated by the fog toggle alone, consistent with the scene.
                float3 viewFog = float3(1.0, 1.0, 1.0);
                float3 viewFogStep = float3(1.0, 1.0, 1.0);
                if (_WaterFogEnabled > 0.5)
                {
                    float worldStep = length(secondWorld - firstWorld);
                    viewFog = exp(-_WaterExtinction.rgb *
                                  (_WaterFogDensity * WaterPathLength(firstWorld, _WorldSpaceCameraPos, surfaceLevel)));
                    viewFogStep = exp(-_WaterExtinction.rgb * (_WaterFogDensity * worldStep));
                }

                float3 accum = float3(0.0, 0.0, 0.0);
                [loop]
                for (int s = 0; s < steps; s++)
                {
                    float tt = tEnter + (s + jitter) * dt;
                    float3 pPool = roPool + rdPool * tt;
                    float3 pWorld = PoolToWorld(pPool);
                    float pe = -mul(UNITY_MATRIX_V, float4(pWorld, 1.0)).z; // eye depth of sample
                    if (pe > sceneEye) break;                              // behind solid geometry

                    // project the sample down the refracted light onto the caustic map (pool-space ray)
                    float2 cuv = ProjectCausticUV(pPool, poolRefract);
                    float4 causticSample = SAMPLE_TEXTURE2D_LOD(_CausticTex, sampler_CausticTex, cuv, 0);
                    float caustic = causticSample.r;

                    // hybrid: occlude this sample so solid objects punch dark shafts while the caustic
                    // flicker is preserved. When the occluder pass is active the object shadow lives in
                    // the caustic GREEN channel, sampled at the SAME refracted-projected uv - so the shafts
                    // BEND with the caustics instead of following the un-refracted shadow map. The shadow
                    // map is the legacy fallback (also carries non-object casters when no occluder ran).
                    float4 shadowCoord = TransformWorldToShadowCoord(pWorld);
                    // Green now stores the occluder's DEPTH: this sample is shadowed only where it lies
                    // BELOW that occluder, so a shaft darkens under the object, not above it too.
                    float shadow = (_CausticOccluderActive > 0.5)
                                 ? OccluderLitFromGreen(pPool.y, causticSample.g)
                                 : MainLightRealtimeShadow(shadowCoord);
                    // Carved presence: a dry volume between this sample and the sun blocks the
                    // direct beam (analytic box shadow, refraction-aware, matching the fog's
                    // in-scatter shadowing).
                    shadow *= ExclusionSunVisibility(pWorld, _LightDir, surfaceLevel);

                    // Dry-interior exclusion: a marched sample inside an exclusion volume is AIR -
                    // no shaft contribution there. The running fog/depth factors still advance with
                    // the ray (the light path continues; only this sample's scatter is skipped).
                    if (!InsideExclusion(pWorld))
                        accum += caustic * shadow * (depthFade * viewFog);
                    depthFade *= depthFadeStep;
                    viewFog *= viewFogStep;
                }
                accum *= dt * _GodRayDensity;

                return half4(_GodRayColor.rgb * _SunColor * accum, 1.0);
            }
            ENDHLSL
        }
    }
}
