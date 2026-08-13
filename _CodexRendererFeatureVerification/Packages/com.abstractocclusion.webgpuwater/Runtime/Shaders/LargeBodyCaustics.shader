// WebGpuWater - large-body caustics (Unity 6 / URP port).
// The ocean version of our pool Caustics.shader: same refraction + area-shrink (Jacobian) focusing,
// but rebuilt in the moving sim-WINDOW's WORLD frame instead of the pool box - because an ocean has
// no fixed floor and the near-field sim covers a camera-following window, not the whole body.
//
// Each vertex of the dense window grid samples the window sim (_WaterTex, sampled in the window's
// normalised space), refracts the sun through the surface normal, and projects onto a REFERENCE
// PLANE a fixed depth below the surface (the ocean analog of the pool floor). The fragment writes
// how much the projected area shrank (light focusing) into the caustic RT, which the underwater god
// rays sample by the same window map. Gated/opt-in: only the windowed ocean renders this; pools and
// bounded bodies keep the pool Caustics.shader untouched.
//
// Drawn manually from C# via CommandBuffer.DrawMesh with an identity matrix (the vertex shader
// outputs clip space directly), exactly like the pool caustic pass.
Shader "AbstractOcclusion/WebGpuWater/LargeBodyCaustics"
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
            #include "WaterCommon.hlsl"     // SampleWaterBilinear, _LightDir, _WaterTexel; WaterShared: IOR_*, SafeRefractedLightY
            #include "WaterVolume.hlsl"     // _SimCenter / _SimExtent (window frame) + LARGE_CAUSTIC_REFERENCE_DEPTH
            #include "WaterLargeCausticWaves.hlsl" // compile-bounded FFT height + normal for this five-sample pass

            float _WaveNormalStrength; // global; the same wave-normal strength the surface uses

            // Reference-plane depth is shared with the god-ray sampler via WaterVolume.hlsl
            // (LARGE_CAUSTIC_REFERENCE_DEPTH), so generation and sampling can't drift apart.
            // CAUSTIC_NORMAL_SOFTEN + CAUSTIC_FOCUS_SCALE now live in WaterShared.hlsl (via
            // WaterCommon), ONE definition shared with the pool caustic generator.
            // The interactive ripple sim is coarse over a large window, so weight it DOWN against the
            // analytic swell; it stays a soft splash/wake detail rather than the dominant (weird) focus.
            #define CAUSTIC_RIPPLE_WEIGHT   0.3

            // God-ray caustic smoothing radius (metres), per body (WaterCausticsPass sets it from the
            // Ocean God Rays block). Caustic focusing is a CURVATURE effect, so the full-spectrum
            // normal is dominated by the SHORTEST wind wavelets - which also move fastest - giving
            // harsh pinpoint shimmer that flickers too quickly. With a radius > 0 the focusing
            // normal comes from finite differences of the wave HEIGHT over +/- this radius instead:
            // everything shorter than ~twice the radius drops out, so the shafts focus through the
            // slow swell only (the surface itself keeps its full detail). 0 = legacy full spectrum.
            float _LargeGodRayCausticSmooth;

            // Dedicated caustic ripple field - the fast, small-wave content of the caustic, fully
            // DECOUPLED from the rendered surface (the KWS arrangement: their caustic source is an
            // independent slow flipbook nobody correlates with the waves). Physically the smallest
            // waves dominate caustic focusing (curvature ~ amplitude * k^2), but the surface's own
            // small content is FFT-texture driven - it ignores any analytic time scale and sweeps
            // too fast to read. So the caustic gets ITS OWN ripples on its own clock: wavelength,
            // strength and speed are direct knobs, the visible surface is untouched, and the
            // smoothed swell above still anchors the pattern to the big waves.
            float _LargeCausticTime;           // owner wave clock * largeCausticTimeScale
            float _LargeCausticRippleScale;    // dominant ripple wavelength (metres)
            float _LargeCausticRippleStrength; // field strength (0 = FFT swell + interactive sim only)

            // Normalised window step between adjacent caustic-mesh vertices (2 / meshResolution),
            // set per body by WaterCausticsPass. THIS IS THE EPSILON the focusing Jacobian is
            // measured over - see vert. It must track the mesh the pass actually draws, which is
            // why it is pushed from C# rather than derived from _WaterTexel (the sim's resolution
            // and the caustic mesh's are the same today, but they are not the same THING).
            float _CausticGridStepNorm;

            // The dedicated field is additive to the FFT swell. Its first six waves provide the
            // independently timed dapple; waves 6-8 add broad slow bands at 6/10/17x the ripple
            // scale. Time scale 0 freezes this layer without freezing the visible ocean.
            #define CAUSTIC_FIELD_WAVE_COUNT 9

            void CausticField(float2 p, out float2 slope, out float height)
            {
                slope = float2(0.0, 0.0);
                height = 0.0;
                // This function is expanded at five projected positions per vertex. Unrolling it
                // duplicates 45 trigonometric wave chains and can time out Unity's shader compiler
                // on a cold package import. A fixed runtime loop preserves the field while keeping
                // the compiled vertex program bounded.
                [loop]
                for (int i = 0; i < CAUSTIC_FIELD_WAVE_COUNT; i++)
                {
                    float ang = 2.399963 * float(i) + 0.7;                // golden-angle spread
                    float2 dir = float2(cos(ang), sin(ang));
                    float jitter = frac(sin(ang * 12.9898) * 43758.5453); // per-wave wavelength variety
                    // Waves 0-5: the ripple octave at the knob scale (the caustic TRIGGER);
                    // 6-8: the swell octave, at a gentler steepness.
                    float octave = (i < 6) ? 1.0 : ((i == 6) ? 6.0 : ((i == 7) ? 10.0 : 17.0));
                    float steep = (i < 6) ? 0.02 : 0.012;                // amplitude = steep * lambda
                    float lambda = _LargeCausticRippleScale * octave * (0.75 + 0.6 * jitter);
                    float k = 6.2831853 / max(lambda, 0.05);
                    float omega = sqrt(9.81 * k);                         // deep-water dispersion
                    float phase = dot(dir, p) * k - omega * _LargeCausticTime + float(i) * 1.7;
                    float amp = steep * lambda;
                    slope += dir * (amp * k * cos(phase));
                    height += amp * sin(phase);
                }
            }

            struct appdata { float4 vertex : POSITION; };
            struct v2f
            {
                float4 pos   : SV_POSITION;
                // Focusing ratio, computed PER VERTEX and interpolated. It used to be two projected
                // positions the fragment took ddx/ddy of - but ddx/ddy of a linearly interpolated
                // varying is CONSTANT over a triangle, so the RT was flat-shaded one value per grid
                // cell and no RT resolution could add detail. Measuring it per vertex instead makes
                // the stored field C0-continuous, which is what removes the blocks.
                float focus  : TEXCOORD0;
            };

            // March a ray from 'origin' along 'dir' down to the horizontal plane y = planeY.
            // SafeRefractedLightY guards a near-horizontal sun (dir.y ~ 0) from dividing by zero.
            float3 ProjectToPlane(float3 origin, float3 dir, float planeY)
            {
                float t = (planeY - origin.y) / SafeRefractedLightY(dir.y);
                return origin + dir * t;
            }

            // ONE projected sample of the caustic map: the displaced surface at 'windowNorm',
            // refracted down onto the reference plane. Factored out of vert so the SAME code can be
            // evaluated at the vertex and at +/- epsilon around it - which is what lets the focusing
            // Jacobian be measured in the window's own frame instead of from screen-space
            // derivatives. Everything inside is the original vert body, moved verbatim.
            float3 CausticProjectedPos(float2 windowNorm, float surfaceY, float refPlaneY)
            {
                float2 worldXZ = _SimCenter.xz + windowNorm * _SimExtent.xz; // axis-aligned window (ocean is unrotated)

                // Base tilt from the interactive ripple sim, softened + weighted DOWN: it is coarse over a
                // large window, so it must not dominate. It stays LIVE in every mode - wake/splash
                // caustics must track the thing that made them.
                float4 info = SampleWaterBilinear(windowNorm * 0.5 + 0.5);
                // info.ba is a SIM-space slope; this normal is built in the world frame, so it
                // converts first or the tilt arrives inflated by the window/depth aspect and
                // saturates sqrt(1 - dot) on a shallow sea. See _PoolSlopeToWorld.
                float2 rippleTilt = info.ba * SIM_SLOPE_TO_POOL * _SimSlopeToWorld.xy
                                  * (CAUSTIC_NORMAL_SOFTEN * CAUSTIC_RIPPLE_WEIGHT);
                float3 normal = float3(rippleTilt.x, sqrt(max(0.0, 1.0 - dot(rippleTilt, rippleTilt))), rippleTilt.y);
                // TWO LAYERS THAT COMPOSE, and that is the fix. These used to be an
                // if / else-if / else, so ANY _LargeCausticRippleStrength above zero shadowed BOTH
                // surface branches: the caustic stopped seeing the waves entirely (normal AND height
                // came only from the synthetic field) and the smoothing radius below became
                // unreachable. A [0..2] slider whose first epsilon silently switches MODE is not a
                // strength. Swell first, dedicated ripple dapple on top, one normalize at the end.

                // ---- Layer 1: THE SWELL - sampled from the already-generated ocean FFT. ----
                // The visible surface owns the complete shore/surf graph. Pulling that graph into
                // this five-projection vertex pass made D3D11 compilation time out on cold imports.
                float swellHeight;
                float2 swellTilt;
                SampleLargeCausticOcean(worldXZ, _LargeGodRayCausticSmooth,
                                        swellHeight, swellTilt);
                normal.xz += swellTilt * _WaveNormalStrength;

                // ---- Layer 2: the dedicated ripple field, ON TOP of the swell. ----
                // Still self-contained and still on its own clock (see CausticField above) - that
                // part was always right; it just should never have replaced the sea to get it.
                float causticFieldHeight = 0.0;
                if (_LargeCausticRippleStrength > 0.0)
                {
                    float2 fieldSlope;
                    CausticField(worldXZ, fieldSlope, causticFieldHeight);
                    normal.xz -= fieldSlope * (_WaveNormalStrength * _LargeCausticRippleStrength);
                }
                normal = normalize(normal);

                float3 ray = refract(-_LightDir, normal, IOR_AIR / IOR_WATER); // through the surface

                // Both wave layers displace the ray origin, matching the two normal layers above.
                // causticFieldHeight is 0 when the dedicated field is off, leaving FFT swell plus
                // the softened interactive ripple simulation.
                float waveHeight = swellHeight + causticFieldHeight
                                 + info.r * _SimExtent.y * CAUSTIC_RIPPLE_WEIGHT;
                return ProjectToPlane(float3(worldXZ.x, surfaceY + waveHeight, worldXZ.y), ray, refPlaneY);
            }

            v2f vert(appdata v)
            {
                v2f o;
                // The window grid is a normalised [-1,1] plane in xy; map it into the window's world frame.
                float2 windowNorm = v.vertex.xy;
                float surfaceY = _SimCenter.y;
                float refPlaneY = surfaceY - LARGE_CAUSTIC_REFERENCE_DEPTH;
                float3 newPos = CausticProjectedPos(windowNorm, surfaceY, refPlaneY);

                // FOCUSING, measured in the WINDOW's frame by central differences over ONE MESH CELL -
                // the same span the old screen-space ddx/ddy covered, so the band-limit and the
                // brightness law are unchanged. What changes is that the result is now per VERTEX and
                // interpolates, instead of being constant across a triangle.
                //
                // HALF a cell, so the central difference spans exactly ONE cell - the same span the
                // old ddx/ddy covered between adjacent vertices (a full-cell epsilon would span TWO
                // and quietly blur more than the code it replaces).
                //
                // AND IT MUST NOT GO BELOW THAT. The vertices ARE the sample points, so a smaller
                // epsilon measures the local Jacobian more precisely while the RT still carries only
                // a piecewise-LINEAR reconstruction between vertices: nothing finer than ~2 cells can
                // be represented however accurately it is measured. A sub-cell epsilon buys aliasing,
                // not detail. Raising that ceiling is the caustic MESH's job, not this epsilon's.
                float2 e = float2(max(_CausticGridStepNorm * 0.5, 1e-5), 0.0);
                float3 nX0 = CausticProjectedPos(windowNorm - e.xy, surfaceY, refPlaneY);
                float3 nX1 = CausticProjectedPos(windowNorm + e.xy, surfaceY, refPlaneY);
                float3 nZ0 = CausticProjectedPos(windowNorm - e.yx, surfaceY, refPlaneY);
                float3 nZ1 = CausticProjectedPos(windowNorm + e.yx, surfaceY, refPlaneY);

                // The UNDISTURBED projection needs no samples: through a flat surface the refracted
                // ray is uniform, so ProjectToPlane is the identity plus a constant offset and the
                // reference area is exactly the grid step mapped to world. Both areas are measured
                // over the same 2*e span, so the span cancels in the ratio exactly as it did before.
                // (Both projections land ON the reference plane, so their y derivative is 0 and this
                // 2D length equals the float3 length the old code took.)
                float oldArea = (2.0 * e.x * abs(_SimExtent.x)) * (2.0 * e.x * abs(_SimExtent.z));
                float newArea = length(nX1.xz - nX0.xz) * length(nZ1.xz - nZ0.xz);
                // Guard newArea: a degenerate (near-parallel) projection would divide by ~0 and write
                // Inf/NaN into the RT that the god rays and every caustic consumer then sample.
                o.focus = oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE;

                // Index the caustic RT in the window frame: the refracted hit's world xz, normalised
                // back into [-1,1] over the window, so the god-ray march samples it by the same map.
                float2 causticNorm = (newPos.xz - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                o.pos = float4(causticNorm.x, causticNorm.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // r = focusing; g = 1 (no occluder shadow term, matching the pool caustic RT layout).
                // Brighter where the projection shrank (light converging), dimmer where it spread -
                // but that ratio is computed per VERTEX now (see vert) and arrives interpolated, which
                // is what stopped the RT being one flat value per grid cell.
                return float4(i.focus, 1.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
