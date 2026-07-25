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
            #include "WaterWaves.hlsl"      // _WaveTime (shared clock) for the analytic wave field
            #include "WaterLargeWaves.hlsl" // ApplyLargeBodyWaveNormal, LargeBodyWaveHeight - the open-water swell

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
            float _LargeCausticRippleStrength; // field strength (0 = legacy surface-driven caustic)

            // SELF-CONTAINED: when the field is active it REPLACES the surface height entirely -
            // with an FFT ocean, LargeBodyWaveHeight is the live FFT texture, which no clock can
            // slow, so any surface contribution reintroduces uncontrolled motion. The field brings
            // its own gentle SWELL octave (waves 6-8, at 6/10/17x the ripple scale) so broad slow
            // bands underlie the fine dapple. Time scale 0 = a frozen caustic, by construction.
            void CausticField(float2 p, out float2 slope, out float height)
            {
                slope = float2(0.0, 0.0);
                height = 0.0;
                [unroll]
                for (int i = 0; i < 9; i++)
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
                float4 pos    : SV_POSITION;
                float3 oldPos : TEXCOORD0; // undisturbed projection (flat surface)
                float3 newPos : TEXCOORD1; // refracted projection (displaced surface)
            };

            // March a ray from 'origin' along 'dir' down to the horizontal plane y = planeY.
            // SafeRefractedLightY guards a near-horizontal sun (dir.y ~ 0) from dividing by zero.
            float3 ProjectToPlane(float3 origin, float3 dir, float planeY)
            {
                float t = (planeY - origin.y) / SafeRefractedLightY(dir.y);
                return origin + dir * t;
            }

            v2f vert(appdata v)
            {
                v2f o;
                // The window grid is a normalised [-1,1] plane in xy; map it into the window's world frame.
                float2 windowNorm = v.vertex.xy;
                float2 worldXZ = _SimCenter.xz + windowNorm * _SimExtent.xz; // axis-aligned window (ocean is unrotated)
                float surfaceY = _SimCenter.y;
                float refPlaneY = surfaceY - LARGE_CAUSTIC_REFERENCE_DEPTH;

                // Base tilt from the interactive ripple sim, softened + weighted DOWN: it is coarse over a
                // large window, so it must not dominate. It stays LIVE in every mode - wake/splash
                // caustics must track the thing that made them.
                float4 info = SampleWaterBilinear(windowNorm * 0.5 + 0.5);
                float2 rippleTilt = info.ba * (CAUSTIC_NORMAL_SOFTEN * CAUSTIC_RIPPLE_WEIGHT);
                float3 normal = float3(rippleTilt.x, sqrt(max(0.0, 1.0 - dot(rippleTilt, rippleTilt))), rippleTilt.y);
                float causticFieldHeight = 0.0;
                bool dedicatedField = _LargeCausticRippleStrength > 0.0;
                if (dedicatedField)
                {
                    // Self-contained caustic field on its own clock (see CausticField above): the
                    // surface height is NOT sampled at all in this mode - with an FFT ocean it is
                    // the live FFT texture, which would reintroduce motion no knob controls.
                    float2 fieldSlope;
                    CausticField(worldXZ, fieldSlope, causticFieldHeight);
                    normal.xz -= fieldSlope * (_WaveNormalStrength * _LargeCausticRippleStrength);
                    normal = normalize(normal);
                }
                // Legacy surface-driven paths (field strength 0): fold in the large-body swell so the
                // caustic focuses through the ACTUAL visible wave shape. Smoothed mode (radius > 0):
                // band-limited slope from height differences over the radius (see
                // _LargeGodRayCausticSmooth above) - the sim normal convention is n.xz = -grad h,
                // matching the ripple tilt this normal already carries.
                else if (_LargeGodRayCausticSmooth > 0.0)
                {
                    float r = _LargeGodRayCausticSmooth;
                    float2 slope = float2(
                        LargeBodyWaveHeight(worldXZ + float2(r, 0.0)) - LargeBodyWaveHeight(worldXZ - float2(r, 0.0)),
                        LargeBodyWaveHeight(worldXZ + float2(0.0, r)) - LargeBodyWaveHeight(worldXZ - float2(0.0, r)))
                        / (2.0 * r);
                    normal.xz -= slope * _WaveNormalStrength;
                    normal = normalize(normal);
                }
                else
                {
                    normal = ApplyLargeBodyWaveNormal(normal, worldXZ, _WaveNormalStrength);
                }

                float3 refractedLight = refract(-_LightDir, float3(0.0, 1.0, 0.0), IOR_AIR / IOR_WATER); // undisturbed
                float3 ray           = refract(-_LightDir, normal,               IOR_AIR / IOR_WATER); // through the surface

                // Displaced surface point: the active mode's wave height + the (soft) interactive
                // ripple height. Dedicated mode uses its own field height - same no-surface rule.
                float waveHeight = (dedicatedField ? causticFieldHeight : LargeBodyWaveHeight(worldXZ))
                                 + info.r * _SimExtent.y * CAUSTIC_RIPPLE_WEIGHT;
                float3 flatPos = float3(worldXZ.x, surfaceY, worldXZ.y);
                float3 dispPos = float3(worldXZ.x, surfaceY + waveHeight, worldXZ.y);

                o.oldPos = ProjectToPlane(flatPos, refractedLight, refPlaneY);
                o.newPos = ProjectToPlane(dispPos, ray,            refPlaneY);

                // Index the caustic RT in the window frame: the refracted hit's world xz, normalised
                // back into [-1,1] over the window, so the god-ray march samples it by the same map.
                float2 causticNorm = (o.newPos.xz - _SimCenter.xz) / max(_SimExtent.xz, 1e-3);
                o.pos = float4(causticNorm.x, causticNorm.y * _ProjectionParams.x, 0.0, 1.0);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                // Brighter where the projected triangle shrank (light converging), dimmer where it
                // spread. Guard newArea: a degenerate near-parallel projection would divide by ~0 and
                // write Inf/NaN into the RT that the god rays then sample.
                float oldArea = length(ddx(i.oldPos)) * length(ddy(i.oldPos));
                float newArea = length(ddx(i.newPos)) * length(ddy(i.newPos));
                // r = focusing; g = 1 (no occluder shadow term, matching the pool caustic RT layout).
                return float4(oldArea / max(newArea, 1e-6) * CAUSTIC_FOCUS_SCALE, 1.0, 0.0, 0.0);
            }
            ENDCG
        }
    }
}
