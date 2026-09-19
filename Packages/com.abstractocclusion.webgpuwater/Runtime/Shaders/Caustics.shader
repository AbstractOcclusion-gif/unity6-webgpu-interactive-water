// WebGpuWater - caustics pass (Unity 6 / URP port)
// Renders the water grid mesh into the caustic RenderTexture. The vertex shader
// projects each water vertex along the refracted light onto the pool floor and
// outputs clip-space position directly (no view/projection matrix). The fragment
// shader brightens where the projected area shrinks (light focusing). The green
// channel is left at 1.0 (no occluder shadow).
//
// Drawn manually from C# via CommandBuffer.DrawMesh with an identity matrix.
Shader "AbstractOcclusion/WebGpuWater/Caustics"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 4.0
            #include "UnityCG.cginc"
            // Brings WaterShared: CAUSTIC_PROJECTION_SCALE, CAUSTIC_FOCUS_SCALE,
            // CAUSTIC_NORMAL_SOFTEN (shared with LargeBodyCaustics), RIM_SHADOW_*, POOL_*.
            #define WATER_CAUSTIC_FIELD 1 // CausticField + _LargeCausticTime/_LargeCausticRippleScale (WaterShared.hlsl)
            #include "WaterCommon.hlsl"
            // WaveSlope + _WaveTime: the SAME analytic wind-wave layer the surface folds into its
            // normal (EvaluateSurfaceGeometry, WaterSurfaceFragStages.hlsl), so the caustic focuses through the exact
            // waves the surface shows - correlated by construction. The params
            // (_WaveA/_WaveB/_WaveCount/_WaveMetersPerUnit/_WaveTime) are per-body, so they are set
            // on THIS material in WaterCausticsPass.Render (the body block isn't applied at caustic
            // time). Inert when Wind Waves is off: _WaveCount == 0 -> WaveSlope() returns 0.
            #include "WaterWaves.hlsl"
            float _WaveNormalStrength; // the same wave-normal strength the surface uses (mirrors LargeBodyCaustics)
            // Normalised pool step between adjacent caustic-mesh vertices (2 / meshResolution), set
            // by WaterCausticsPass. THE epsilon the focusing Jacobian is measured over - see vert.
            float _CausticGridStepNorm;
            // Pond-side caustic shaping (WaterCausticsPass.Render sets both; defaults keep every
            // existing pool byte-identical):
            //  _PoolCausticFieldStrength - strength of the dedicated caustic ripple field (the ocean
            //      generator's CausticField, shared). 0 = off. Independent of _WaveNormalStrength on
            //      purpose: that one is already scaled by Caustic Wind Wave Strength, and the whole
            //      point of this layer is a pattern that does NOT depend on the visible waves.
            //  _CausticSimRippleStrength - weight of the interactive ripple sim in the caustic. 1 = as before.
            float _PoolCausticFieldStrength;
            float _CausticSimRippleStrength;

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos    : SV_POSITION;
                // Focusing ratio, PER VERTEX now: ddx/ddy of a linearly interpolated varying is
                // constant over a triangle, so the RT used to be flat-shaded one value per grid cell
                // and no RT resolution could add detail. Measured in vert, this interpolates.
                float  focus  : TEXCOORD0;
                // newPos SURVIVES: the rim shadow in frag needs the actual projected floor hit.
                float3 newPos : TEXCOORD1;
            };

            // Ripple state under a POOL xy. A whole-body sim is indexed by the pool xy directly. A
            // WINDOWED body's sim texture only covers the camera-following window, so it is indexed
            // through the window frame (the surface's own SampleRipple mapping: WorldToSim + the
            // border fade) and contributes nothing outside it. The wind waves below are analytic
            // and cover the whole body either way, so a windowed pond gets a caustic EVERYWHERE,
            // with the live ripples added on top around the camera (2026-09-19; windowed non-ocean
            // bodies used to get no caustic at all, which also took their god rays away).
            // Single exit on purpose: see the D3D note on SampleRipple in WaterSurfaceVertStage.
            float4 SampleCausticRipple(float2 poolXY)
            {
                float2 uv = poolXY * 0.5 + 0.5;
                float fade = 1.0;
                if (_SimWindowed > 0.5)
                {
                    uv = WorldToSim(PoolToWorld(float3(poolXY.x, 0.0, poolXY.y))).xz * 0.5 + 0.5;
                    float band = max(_SimEdgeFadeTexels, 0.0) * _WaterTexel.x; // texels -> UV
                    float2 d = min(uv, 1.0 - uv);                              // negative outside
                    fade = saturate(min(d.x, d.y) / max(band, 1e-5));
                    uv = saturate(uv);
                }
                return SampleWaterBilinear(uv) * fade;
            }

            // project the ray onto the pool floor plane
            float3 project(float3 origin, float3 ray, float3 refractedLight)
            {
                float2 tcube = IntersectCube(origin, ray, POOL_BOX_MIN, POOL_BOX_MAX);
                origin += ray * tcube.y;
                // SafeRefractedLightY: a near-horizontal sun otherwise divides by ~0.
                float tplane = (-origin.y - 1.0) / SafeRefractedLightY(refractedLight.y);
                return origin + refractedLight * tplane;
            }

            // ONE projected pair for a pool xy: the undisturbed hit and the refracted hit on the
            // floor. Factored out of vert so the same code runs at the vertex AND at +/- epsilon,
            // which is what lets the focusing Jacobian be measured in POOL space instead of from
            // screen-space derivatives. Body moved verbatim from the old vert.
            void PoolCausticProject(float2 poolXY, float baseY, float3 refractedLight,
                                    out float3 oldPos, out float3 newPos)
            {
                // Manual bilinear (not tex2Dlod): WebGPU point-samples float32 textures, so a
                // plain sample makes the projected heights/normals - and therefore the whole
                // caustic focusing - blocky in builds whenever mesh res != sim res.
                float4 info = SampleCausticRipple(poolXY) * _CausticSimRippleStrength;
                // Softens the ripple normal (CAUSTIC_NORMAL_SOFTEN, WaterShared - shared with the
                // large-body caustic): full-strength slopes over-focus into hard sparkles.
                info.ba *= CAUSTIC_NORMAL_SOFTEN;
                // Fold in the wind-wave slope exactly as the surface does (same MINUS sign and raw
                // * _WaveNormalStrength, in EvaluateSurfaceGeometry) so the caustic - and the
                // chunk god-ray shafts that sample it - inherit the wave structure the surface shows.
                // WORLD slopes before the normal is built, exactly as the surface does. Left in pool
                // units the tilt carries the footprint/depth aspect factor, sqrt(1 - dot) floors at
                // zero, and refract() is handed a non-unit, near-horizontal normal - the caustic
                // pattern tears on any wide shallow body. The refracted ray then rides the pool-space
                // trace below on the same world-direction-in-pool-space convention refractedLight
                // already uses, so nothing downstream changes.
                float2 nxz = info.ba * SIM_SLOPE_TO_POOL * _SimSlopeToWorld.xy
                           - WaveSlope(poolXY) * _WaveNormalStrength * _PoolSlopeToWorld.xy;
                // Dedicated caustic ripple field, ON TOP (same composition as the ocean generator).
                // Evaluated in METRES in the pool-aligned frame (pool xy * extent), so its slopes are
                // in the same frame as the two slopes above on a rotated body. A uniform branch.
                float fieldHeightPool = 0.0;
                if (_PoolCausticFieldStrength > 0.0)
                {
                    float3 extent = VolumeExtentSafe();
                    float2 fieldSlope;
                    float fieldHeight;
                    CausticField(poolXY * extent.xz, fieldSlope, fieldHeight);
                    nxz -= fieldSlope * _PoolCausticFieldStrength;
                    fieldHeightPool = fieldHeight * _PoolCausticFieldStrength / extent.y; // metres -> pool y
                }
                float3 normal = float3(nxz.x, sqrt(max(0.0, 1.0 - dot(nxz, nxz))), nxz.y);
                float3 ray = refract(-_LightDir, normal, IOR_AIR / IOR_WATER);
                // v.vertex.xzy put the grid's z into y; baseY carries that through unchanged.
                float3 base = float3(poolXY.x, baseY, poolXY.y);
                oldPos = project(base, refractedLight, refractedLight);
                newPos = project(base + float3(0.0, info.r + fieldHeightPool, 0.0), ray, refractedLight);
            }

            v2f vert(appdata v)
            {
                v2f o;
                float2 poolXY = v.vertex.xy;
                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

                float3 oldPos, newPos;
                PoolCausticProject(poolXY, v.vertex.z, refractedLight, oldPos, newPos);
                o.newPos = newPos;

                // FOCUSING by central differences over ONE MESH CELL - the same span the old
                // screen-space ddx/ddy covered, so the band-limit and the brightness law are
                // unchanged; only the per-triangle flat shading goes away. Epsilon is FLOORED at the
                // cell on purpose: the vertices are the sample points, so a smaller epsilon measures
                // more precisely but the RT still carries only a piecewise-linear reconstruction
                // between them - sub-cell epsilon buys aliasing, not detail.
                // HALF a cell, so the central difference spans exactly ONE cell - the same span
                // the old ddx/ddy covered between adjacent vertices. A full-cell epsilon would
                // span TWO and quietly blur more than the code it replaces.
                float2 e = float2(max(_CausticGridStepNorm * 0.5, 1e-5), 0.0);
                float3 oX0, nX0, oX1, nX1, oZ0, nZ0, oZ1, nZ1;
                PoolCausticProject(poolXY - e.xy, v.vertex.z, refractedLight, oX0, nX0);
                PoolCausticProject(poolXY + e.xy, v.vertex.z, refractedLight, oX1, nX1);
                PoolCausticProject(poolXY - e.yx, v.vertex.z, refractedLight, oZ0, nZ0);
                PoolCausticProject(poolXY + e.yx, v.vertex.z, refractedLight, oZ1, nZ1);

                // Unlike the ocean generator, the undisturbed projection is NOT a constant offset
                // here: project() runs an IntersectCube first, so the pool walls make oldPos vary
                // with position. It therefore needs the same differences rather than a closed form.
                float oldArea = length(oX1 - oX0) * length(oZ1 - oZ0);
                float newArea = length(nX1 - nX0) * length(nZ1 - nZ0);
                // Guard newArea: a degenerate (near-parallel) projection would divide by ~0 and
                // write Inf/NaN into the caustic RT that every other pass samples.
                o.focus = oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE;

                // Raw clip-space output (no MVP), so compensate the platform/context render-target
                // Y-flip ourselves: _ProjectionParams.x is -1 when Unity renders flipped (e.g. via an
                // intermediate target under the Mobile URP asset / WebGPU), which otherwise mirrors the
                // caustic RT vs the desktop editor and shifts everything that samples _CausticTex.
                float2 cpos = CAUSTIC_PROJECTION_SCALE * (newPos.xz + refractedLight.xz / SafeRefractedLightY(refractedLight.y));
                o.pos = float4(cpos.x, cpos.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // r = focusing, computed per VERTEX in vert (see the v2f comment) and interpolated
                // here; g = 1 means unshadowed (the occluder pass min-blends its silhouette in).
                float4 col = float4(i.focus, 1.0, 0.0, 0.0);

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER);

                // Rim shadow. NEGATED on purpose: this shader's 'refractedLight' is the DOWNWARD
                // propagation ray, while PoolRimShadow wants the toward-light direction (see its
                // header in WaterShared.hlsl).
                col.r *= PoolRimShadow(i.newPos, -refractedLight);

                return col;
            }
            ENDCG
        }
    }
}
