// WebGpuWater - volumetric water CHUNK shell (the submerged body below a chunk's surface).
// Owned by a WaterVolume configured as a chunk (WaterVolume.Chunk.cs) and drawn as a body renderer,
// so it is fed THAT body's per-body block (frame + waves + fog) - the shell's waterline is the SAME
// SurfaceHeightAtXZ the disc surface uses, so the two meet with no seam, and it needs no external
// primary (the body publishes its own state). A pool-space box mesh (BuildChunkShellBox, [-1,1])
// placed by the volume frame; the primitive (box / inscribed sphere) is resolved analytically in
// pool space. It FILLS the primitive below the surface with the lit in-scatter colour integrated
// over the water column, tinting + refracting the real scene behind by the water's optical depth.
//
// Cull Front (draw the box's BACK faces) so every covered pixel gets ONE fragment and the column
// integrates entry->exit for a camera OUTSIDE or INSIDE the body. ZTest Always (Crest volume-pass
// pattern): opaque geometry INSIDE the chunk sits nearer than the back face and would z-reject the
// fragment, punching an unfogged hole in the water in front of it - the sceneDist clamp below caps
// the column against the real scene instead. Stays OUT of _CameraDepthTexture (no depth passes) so
// it composites over the real scene; the analytic silhouette discards fragments off the shape or
// above the surface.
//
// TIER GATE (_RealRefraction, 0 on Low where the URP opaque texture is released): High/Med take the
// FULL refracted-backdrop path; Low takes a cheap premultiplied inscatter veil.
Shader "AbstractOcclusion/WebGpuWater/WaterChunkWall"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode"="UniversalForward" }
            Cull Front
            ZWrite Off
            ZTest Always
            Blend One OneMinusSrcAlpha // premultiplied: rgb is the finished composite, a = coverage

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
            #include "WaterChunkPrimitive.hlsl" // ChunkIntersect / ChunkSurfaceNormalPool (+ WaterShared: IOR_*)
            #include "WaterCommon.hlsl"         // SampleWaterBicubic (interactive ripple) + _WaterTex + _LightDir
            #include "WaterFog.hlsl"            // WaterInscatterColor + DownwellingAttenuation + _ScatterAmbient + fog globals
            #include "WaterVolume.hlsl"         // PoolToWorld / WorldToPool / PoolNormalToWorld (this body's frame)
            #include "WaterWaterline.hlsl"      // WaveHeight (wind-wave layer) via its wave includes

            // Published globals (WaterUniformPublisher). _LightDir comes from WaterCommon; _RealRefraction
            // is the tier flag (0 on Low).
            float3 _SunColor;
            float  _RealRefraction;
            sampler2D _CameraOpaqueTexture;

            // Per-chunk state (set on the body block by WaterVolume.Chunk.cs). The chunk's density
            // boost is NOT a shell uniform: SetChunkSurfaceProps bakes it into the body block's
            // _WaterFogDensity once, so the disc column, this shell and membership objects all read
            // the same (boosted) water.
            float _ChunkShape;        // CHUNK_SHAPE_* selector (box / sphere)
            float _ChunkRefraction;   // 0 = flat window; higher bends the backdrop (a lens)
            float _ChunkReflectivity; // fresnel sheen strength (sky + sun reflected toward grazing)
            // 1 = the camera (lowest near-plane corner, hysteresis) is in THIS chunk's water.
            // Decided per FRAME on the CPU (ComputeChunkCameraUnder) - a per-pixel test off the ray
            // interval flickered across the waterline band. Drives the veil-vs-backdrop split below.
            float _ChunkCameraUnderwater;
            // Meniscus line strength (0 = off). Published per-chunk by WaterVolume.Chunk.cs; 0 when
            // unpublished so a build that never sets it draws no line.
            float _ChunkMeniscus;

            #define CHUNK_SUN_WRAP 0.5
            #define CHUNK_COLUMN_EPSILON 1e-4
            #define CHUNK_UV_MIN 0.001
            #define CHUNK_UV_MAX 0.999
            #define CHUNK_CLIP_W_EPS 1e-5
            #define CHUNK_FRESNEL_F0 0.02
            #define CHUNK_SUN_SPEC_POWER 200.0
            #define CHUNK_SUN_SPEC_GAIN 1.0
            // Bisection steps for the ray<->displaced-surface crossing, bounded to the primitive
            // span (<= the chunk diameter), so precision is span / 2^steps regardless of ray angle.
            #define CHUNK_WATERLINE_BISECT_STEPS 6
            // Coarse march that finds waterline crossings the two-endpoint test misses (a crest hump
            // or a trough MID-span - a double crossing). Each detected sign change is refined by the
            // bisection above, so precision per crossing = span / (MARCH_STEPS * 2^BISECT_STEPS).
            #define CHUNK_WATERLINE_MARCH_STEPS 8
            // Meniscus: a thin surface-tension darkening along the on-screen waterline, shown only
            // where the ray crosses the surface CLOSE to the eye (the "at 0" near-plane frames).
            // SPAN_FRACTION = air-side line thickness as a fraction of the span; MAX_DISTANCE fades
            // it out with crossing distance so a distant chunk never gets a dark ring; DARKEN caps
            // the line's opacity. Strength is scaled by the per-chunk _ChunkMeniscus knob.
            #define CHUNK_MENISCUS_SPAN_FRACTION 0.06
            #define CHUNK_MENISCUS_MAX_DISTANCE 6.0
            #define CHUNK_MENISCUS_DARKEN 0.55

            float2 ChunkClipToScreenUV(float4 clipPos)
            {
                float2 uv = clipPos.xy / max(clipPos.w, CHUNK_CLIP_W_EPS) * 0.5 + 0.5;
                if (_ProjectionParams.x < 0.0) uv.y = 1.0 - uv.y;
                return uv;
            }

            // Interactive ripple height at a point, window-aware - mirrors WaterSurface.shader's
            // SampleRipple: whole-body samples the pool UV, a windowed body (ocean) samples the
            // camera-following sim window by WORLD position and fades to flat at its border. Without
            // the window path an ocean chunk read the wrong (static) UV and the fog stopped moving.
            float ChunkRippleHeight(float2 poolXZ, float3 worldPos)
            {
                if (_SimWindowed < 0.5)
                    return SampleWaterBicubic(poolXZ * 0.5 + 0.5).r;

                float2 uv = WorldToSim(worldPos).xz * 0.5 + 0.5;
                if (any(uv < 0.0) || any(uv > 1.0)) return 0.0;
                float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x;
                float2 d = min(uv, 1.0 - uv);
                float fade = saturate(min(d.x, d.y) / max(band, 1e-5));
                return SampleWaterBicubic(uv).r * fade;
            }

            // The EXACT height the disc surface renders here. SurfaceHeightAtXZ is the shared source of
            // truth for the wind (ocean world-metre / pond pool) + swell/large-wave layers - the ocean's
            // dominant motion - and the interactive ripple is added on top, lifted through the frame
            // exactly as the surface vert lifts it. So the shell waterline tracks the surface for BOTH a
            // bounded ocean (wind + swell) and a pond (ripple), with no lag.
            float ChunkSurfaceHeightWorld(float2 worldXZ, float3 worldPos)
            {
                float baseline = SurfaceHeightAtXZ(worldXZ);
                float3 poolAtRest = WorldToPool(float3(worldXZ.x, _VolumeCenter.y, worldXZ.y));
                float2 poolXZ = poolAtRest.xz;
                float ripple = ChunkRippleHeight(poolXZ, worldPos);
                float rippleLift = PoolToWorld(float3(poolXZ.x, ripple, poolXZ.y)).y
                                 - PoolToWorld(float3(poolXZ.x, 0.0, poolXZ.y)).y;
                return baseline + rippleLift;
            }

            float3 ChunkSurfaceReflection(float3 surfaceNormal, float3 viewDirWS)
            {
                float fresnel = CHUNK_FRESNEL_F0 + (1.0 - CHUNK_FRESNEL_F0)
                              * pow(1.0 - saturate(dot(surfaceNormal, viewDirWS)), 5.0);
                float3 reflDir = reflect(-viewDirWS, surfaceNormal);
                float sunGlint = pow(saturate(dot(reflDir, _LightDir)), CHUNK_SUN_SPEC_POWER);
                float3 reflectColor = _ScatterAmbient.rgb + _SunColor * (sunGlint * CHUNK_SUN_SPEC_GAIN);
                return reflectColor * (fresnel * _ChunkReflectivity);
            }

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
            };

            // The box mesh is authored in POOL space [-1,1]; the volume frame places it in the world,
            // exactly like the analytic pool renderer (so a rotated / non-uniform chunk is the frame's).
            Varyings vert(Attributes IN)
            {
                Varyings o;
                o.positionWS = PoolToWorld(IN.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 rayDir = normalize(IN.positionWS - _WorldSpaceCameraPos);
                float3 viewDirWS = -rayDir;

                // Primitive interval along the view ray, in world metres (pool space is an affine image
                // of world, so the pool-space t of a normalised world ray is the world t).
                float3 poolOrigin = WorldToPool(_WorldSpaceCameraPos);
                float3 poolDir    = WorldDirToPool(rayDir);
                float2 t = ChunkIntersect(_ChunkShape, poolOrigin, poolDir);

                // Real scene behind the fragment caps the column at any geometry inside/behind the body.
                float2 screenUV = GetNormalizedScreenSpaceUV(IN.positionCS);
                float3 sceneWorld = ComputeWorldSpacePosition(screenUV, SampleSceneDepth(screenUV),
                                                              UNITY_MATRIX_I_VP);
                float sceneDist = max(dot(sceneWorld - _WorldSpaceCameraPos, rayDir), 0.0);

                float entryT = max(t.x, 0.0);
                float exitT  = min(t.y, sceneDist);

                // Waterline = THIS body's displaced surface (the SAME function the disc surface
                // uses, via the shared per-body block) -> the shell and the disc meet with no seam.
                // Waterline solve by a coarse MARCH + per-crossing bisection over the primitive span,
                // replacing the endpoint-only classification (which saw only the two span ends and so
                // missed a crest hump or a trough MID-span - a double crossing - either over-fogging
                // through the hump or dropping a submerged segment). We accumulate the TRUE submerged
                // length, so 0 / 1 / 2+ crossings are handled uniformly. Bounded to the span and never
                // dividing by rayDir.y, so grazing rays stay robust (the old fixed-point solve
                // confettied them). Reduces to the previous result in the single-crossing common case.
                float spanStart = entryT;
                float spanEnd   = exitT;                 // = min(t.y, sceneDist)
                float spanLen   = max(spanEnd - spanStart, 0.0);

                float sPrev = spanStart;
                float3 pPrev = _WorldSpaceCameraPos + rayDir * sPrev;
                float gapPrev = pPrev.y - ChunkSurfaceHeightWorld(pPrev.xz, pPrev);
                bool nearInAir = gapPrev >= 0.0; // nearest point of the span is above the surface

                float submergedColumn = 0.0;
                float firstEntryT = spanEnd; // start of the first submerged segment (refraction entry)
                float lastExitT   = spanStart;
                float wavyTopY = pPrev.y - gapPrev; // surface over the entry (downwelling reference)
                bool haveEntry = false;

                // [loop] not [unroll]: the nested surface-sampling body is too large to unroll
                // (D3D bails), and SampleWaterBicubic uses tex2Dlod so dynamic flow is valid - the
                // same choice WaterUnderwaterFog.shader makes for its scan + refine.
                [loop]
                for (int march = 1; march <= CHUNK_WATERLINE_MARCH_STEPS; march++)
                {
                    float sCur = spanStart + spanLen * ((float)march / CHUNK_WATERLINE_MARCH_STEPS);
                    float3 pCur = _WorldSpaceCameraPos + rayDir * sCur;
                    float gapCur = pCur.y - ChunkSurfaceHeightWorld(pCur.xz, pCur);
                    bool prevWater = gapPrev < 0.0;
                    bool curWater  = gapCur  < 0.0;

                    if (prevWater && curWater)
                    {
                        // Fully submerged sub-interval.
                        if (!haveEntry) { firstEntryT = sPrev; wavyTopY = pPrev.y - gapPrev; haveEntry = true; }
                        submergedColumn += sCur - sPrev;
                        lastExitT = sCur;
                    }
                    else if (prevWater != curWater)
                    {
                        // One crossing in this sub-interval: bisect for the exact boundary.
                        float a = sPrev, b = sCur;
                        bool aInAir = gapPrev >= 0.0;
                        [loop]
                        for (int bisect = 0; bisect < CHUNK_WATERLINE_BISECT_STEPS; bisect++)
                        {
                            float sM = (a + b) * 0.5;
                            float3 pM = _WorldSpaceCameraPos + rayDir * sM;
                            float gapM = pM.y - ChunkSurfaceHeightWorld(pM.xz, pM);
                            if ((gapM >= 0.0) == aInAir) a = sM; else b = sM;
                        }
                        float sCross = (a + b) * 0.5;
                        if (curWater) // air -> water: a submerged segment begins at the crossing
                        {
                            if (!haveEntry)
                            {
                                firstEntryT = sCross;
                                float3 pC = _WorldSpaceCameraPos + rayDir * sCross;
                                wavyTopY = ChunkSurfaceHeightWorld(pC.xz, pC);
                                haveEntry = true;
                            }
                            submergedColumn += sCur - sCross;
                            lastExitT = sCur;
                        }
                        else // water -> air: a submerged segment ends at the crossing
                        {
                            if (!haveEntry) { firstEntryT = sPrev; wavyTopY = pPrev.y - gapPrev; haveEntry = true; }
                            submergedColumn += sCross - sPrev;
                            lastExitT = sCross;
                        }
                    }
                    // else both endpoints in air: no submerged contribution from this sub-interval.

                    sPrev = sCur; pPrev = pCur; gapPrev = gapCur;
                }

                // Ownership (same semantics as before): the nearest point is in air AND the span
                // reaches water -> the ray looks DOWN through the waterline, which is the disc's pixel.
                bool enteredThroughTop = nearInAir && haveEntry;
                entryT = firstEntryT;
                exitT  = lastExitT;

                float column = submergedColumn;
                clip(column - CHUNK_COLUMN_EPSILON); // no water here (off the shape / above the surface)

                // Ownership split vs the disc surface (deterministic: the shell material renders
                // AFTER the discs via its render queue - WaterVolume.Chunk.cs): a ray that entered
                // through the WATERLINE from above is the disc's pixel - it already rendered the
                // full fogged column (chunk fog clamp), and the disc's sphere clip overshoots the
                // rim slightly (CHUNK_SPHERE_CLIP_MARGIN) so this shared boundary stays covered.
                // The shell must not paint it twice - and must NOT replace it with the opaque
                // backdrop, which never contains transparents (that erased the disc). Normally
                // discard. EXCEPTION: right at the waterline TOUCH, lay a thin premultiplied MENISCUS
                // line (a surface-tension darkening) over the disc, concentrated where the air column
                // before the water is thin and faded by crossing distance, so it appears only on the
                // near-plane "at 0" frames and never as a ring around a distant chunk. Off at
                // _ChunkMeniscus = 0. Rays outside the line still discard (the disc owns them).
                if (enteredThroughTop)
                {
                    float airColumn = entryT - spanStart;                 // air travelled before water
                    float bandWidth = CHUNK_MENISCUS_SPAN_FRACTION * max(spanLen, CHUNK_COLUMN_EPSILON);
                    float atLine   = saturate(1.0 - airColumn / max(bandWidth, CHUNK_COLUMN_EPSILON));
                    float nearFade = saturate(1.0 - entryT / CHUNK_MENISCUS_MAX_DISTANCE);
                    float meniscus = _ChunkMeniscus * CHUNK_MENISCUS_DARKEN * atLine * nearFade;
                    clip(meniscus - CHUNK_COLUMN_EPSILON);                // outside the line: disc owns it
                    return half4(0.0, 0.0, 0.0, meniscus);               // premultiplied darken over the disc
                }

                // Entry surface normal: the analytic shell normal (pool -> world via the frame's
                // inverse-transpose); top entries are discarded above, so no UP branch remains.
                float3 poolEntry = poolOrigin + poolDir * entryT;
                float3 surfaceN = PoolNormalToWorld(ChunkSurfaceNormalPool(_ChunkShape, poolEntry));
                if (dot(surfaceN, viewDirWS) < 0.0) surfaceN = -surfaceN;

                float sunWrap = saturate((dot(surfaceN, _LightDir) + CHUNK_SUN_WRAP) / (1.0 + CHUNK_SUN_WRAP));
                float3 inscatter = WaterInscatterColor(viewDirWS, _LightDir, _SunColor * sunWrap, 0.0);
                float3 transmittance = exp(-_WaterExtinction.rgb * (_WaterFogDensity * column));
                float3 reflection = ChunkSurfaceReflection(surfaceN, viewDirWS);

                float deepestY = min(min(_WorldSpaceCameraPos.y + rayDir.y * entryT,
                                         _WorldSpaceCameraPos.y + rayDir.y * exitT), wavyTopY);
                float3 depthDarken = DownwellingAttenuation(deepestY, wavyTopY);

                // VEIL path: premultiplied in-scatter over the framebuffer. Taken on the CHEAP tier
                // (no opaque-texture copy) AND whenever the camera is IN the water (per-frame CPU
                // state, hysteresis - see _ChunkCameraUnderwater): there the entry is the eye (no
                // interface, no lens), and the surfaces already drawn behind (the disc underside,
                // the scene) must stay visible through the fog rather than being replaced by the
                // opaque backdrop. No fresnel sheen from inside the water.
                bool cameraInWater = _ChunkCameraUnderwater > 0.5;
                if (_RealRefraction < 0.5 || cameraInWater)
                {
                    float3 opacity = 1.0 - transmittance;
                    float coverage = max(opacity.r, max(opacity.g, opacity.b));
                    float3 sheen = cameraInWater ? float3(0.0, 0.0, 0.0) : reflection;
                    float3 veil = (inscatter * opacity + sheen) * depthDarken;
                    return half4(veil, coverage);
                }

                // FULL tier: refract the backdrop sample by the view ray bending at the surface.
                float2 refractUV = screenUV;
                if (_ChunkRefraction > 0.0)
                {
                    float3 entryWS = _WorldSpaceCameraPos + rayDir * entryT;
                    float3 refrDir = refract(rayDir, surfaceN, IOR_AIR / IOR_WATER);
                    float2 uvStraight = ChunkClipToScreenUV(TransformWorldToHClip(entryWS + rayDir  * column));
                    float2 uvBent     = ChunkClipToScreenUV(TransformWorldToHClip(entryWS + refrDir * column));
                    refractUV = clamp(screenUV + (uvBent - uvStraight) * _ChunkRefraction,
                                      CHUNK_UV_MIN, CHUNK_UV_MAX);
                }

                float3 sceneColor = tex2Dlod(_CameraOpaqueTexture, float4(refractUV, 0.0, 0.0)).rgb;
                float3 color = sceneColor * transmittance + inscatter * (1.0 - transmittance);
                color += reflection;
                color *= depthDarken;
                return half4(color, 1.0);
            }
            ENDHLSL
        }

        // NO DepthOnly / DepthNormals passes ON PURPOSE: the shell must stay out of
        // _CameraDepthTexture so it reads the REAL scene behind it.
    }
}
