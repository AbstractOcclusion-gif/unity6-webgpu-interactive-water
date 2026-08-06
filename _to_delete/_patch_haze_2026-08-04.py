import hashlib, os, sys, glob

ROOT = glob.glob('/sessions/*/mnt/ThreeJSWaterPort')[0]
PATH = os.path.join(ROOT, 'Packages/com.abstractocclusion.webgpuwater/Runtime/Shaders/WaterSurfaceFragStages.hlsl')
EXPECT_MD5 = '63f813845b28c1933237889d80bb284a'

raw = open(PATH, 'rb').read()
md5 = hashlib.md5(raw).hexdigest()
assert md5 == EXPECT_MD5, 'md5 drift: %s != %s' % (md5, EXPECT_MD5)
assert b'\r\n' not in raw, 'file is CRLF; patcher assumes LF'

with open(PATH, 'r', encoding='utf-8', newline='') as f:
    text = f.read()

EDITS = []

# ---------------------------------------------------------------- A: file-scope helpers
A_OLD = """
// Final composite: the exclusive foam blend over everything, then horizon haze
"""
A_NEW = """
// The horizontal direction a view ray looks along - "where is the sky at the horizon on this
// bearing" - unit length by construction. Every haze path needs it, and every one of them used to be
// handed incomingRay instead, which points camera->surface: from a camera above the water that aims
// steeply DOWN, so the environment cube was read well BELOW the horizon.
#define HORIZON_AZIMUTH_MIN 1e-4   // below this the view ray is straight down; there is no azimuth
float3 HorizonDirection(float3 viewRay)
{
    return float3(viewRay.x, 0.0, viewRay.z) / max(length(viewRay.xz), HORIZON_AZIMUTH_MIN);
}

// One horizon-sky colour sample: a horizontal 5-tap blur of the opaque texture, each tap weighted by
// HorizonSkyWeight and the sum renormalised. Blurred, because each water column samples ONE horizon
// point, so a single skybox texel would STRETCH straight down the column as a vertical line - the
// haze wants the broad horizon COLOUR, not its texels. Widen HORIZON_BLUR_STEP if the texels read
// coarse.
//
// skyWeight (out) is how much of the kernel was sky at all: 1 = every tap, 0 = none. The caller MUST
// carry it as confidence rather than trusting the colour alone, because a kernel that landed
// entirely on a hull still returns a perfectly well-formed colour - the hull's.
#define HORIZON_BLUR_STEP 0.006   // UV x-offset per blur tap
// Floor on the renormalising divisor when every tap is rejected as non-sky: the colour it produces
// is discarded anyway (skyWeight is 0 there), this only keeps the divide finite.
#define HORIZON_SKY_MIN_WEIGHT 1e-3
float3 SampleHorizonSky(float2 uv, out float skyWeight)
{
    float2 blurStep1 = float2(HORIZON_BLUR_STEP, 0.0);
    float2 blurStep2 = float2(2.0 * HORIZON_BLUR_STEP, 0.0);
    float2 tap0 = uv;
    float2 tap1 = saturate(uv + blurStep1);
    float2 tap2 = saturate(uv - blurStep1);
    float2 tap3 = saturate(uv + blurStep2);
    float2 tap4 = saturate(uv - blurStep2);
    float weight0 = HorizonSkyWeight(tap0) * 0.34;
    float weight1 = HorizonSkyWeight(tap1) * 0.24;
    float weight2 = HorizonSkyWeight(tap2) * 0.24;
    float weight3 = HorizonSkyWeight(tap3) * 0.09;
    float weight4 = HorizonSkyWeight(tap4) * 0.09;
    // The blur weights sum to 1, so this is 1 when every tap is sky and 0 when none is.
    skyWeight = weight0 + weight1 + weight2 + weight3 + weight4;
    return (UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, tap0, 0).rgb * weight0
          + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, tap1, 0).rgb * weight1
          + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, tap2, 0).rgb * weight2
          + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, tap3, 0).rgb * weight3
          + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, tap4, 0).rgb * weight4)
         / max(skyWeight, HORIZON_SKY_MIN_WEIGHT);
}

// Final composite: the exclusive foam blend over everything, then horizon haze
"""
EDITS.append(('A file-scope helpers', A_OLD, A_NEW))

# ---------------------------------------------------------------- E: stale comment
E_OLD = """        // own screen position, so the seamless water-sky join is preserved. Degenerate
        // azimuth (looking straight down) or a horizon behind the camera (w ~ 0) falls back
        // to the behind-pixel sample - haze is negligible in those poses anyway. Explicit-LOD
"""
E_NEW = """        // own screen position, so the seamless water-sky join is preserved. A degenerate azimuth
        // (looking straight down), a horizon behind the camera (w ~ 0), or a horizon that has left
        // the frame entirely all fade to the environment cube along horizonDir instead - see the
        // confidence terms below; there is no rendered sky left to match in those poses. Explicit-LOD
"""
EDITS.append(('E stale comment', E_OLD, E_NEW))

# ---------------------------------------------------------------- B: hoist horizonDir
B_OLD = """        #define HORIZON_AZIMUTH_MIN 1e-4   // below this the view ray is straight down; no azimuth
        // Floor on the renormalising divisor when every horizon tap is rejected as non-sky: the
        // colour it produces is discarded anyway (skyConfidence is 0 there), this only keeps the
        // divide finite.
        #define HORIZON_SKY_MIN_WEIGHT 1e-3
        float3 skyAtHorizon;
        if (_RealRefraction > 0.5)
        {
            float azimuthLen = length(incomingRay.xz);
            float3 horizonDir = float3(incomingRay.x, 0.0, incomingRay.z)
                              / max(azimuthLen, HORIZON_AZIMUTH_MIN);
            float4 horizonClip = mul(UNITY_MATRIX_VP, float4(horizonDir, 0.0));
"""
B_NEW = """        // Hoisted out of the branch below: both the opaque path and the cube fallback want it.
        float azimuthLen = length(incomingRay.xz);
        float3 horizonDir = HorizonDirection(incomingRay);
        float3 skyAtHorizon;
        if (_RealRefraction > 0.5)
        {
            float4 horizonClip = mul(UNITY_MATRIX_VP, float4(horizonDir, 0.0));
"""
EDITS.append(('B hoist horizonDir', B_OLD, B_NEW))

# ---------------------------------------------------------------- C: sampling + confidence
C_OLD = """            // Horizontally BLUR the per-azimuth sample: each water column samples ONE horizon point,
            // so a single skybox texel would STRETCH straight down the column as a vertical line. The
            // haze wants the broad horizon COLOUR, not its texels, so average a few taps across x
            // (5-tap, weights sum to 1). Widen HORIZON_BLUR_STEP if the skybox texels read coarse.
            // centreBand is x-fixed (uniform), so it needs no blur.
            #define HORIZON_BLUR_STEP 0.006  // UV x-offset per blur tap
            float2 huv = saturate(horizonUV);
            float2 hb1 = float2(HORIZON_BLUR_STEP, 0.0);
            float2 hb2 = float2(2.0 * HORIZON_BLUR_STEP, 0.0);
            float2 t0 = huv;
            float2 t1 = saturate(huv + hb1);
            float2 t2 = saturate(huv - hb1);
            float2 t3 = saturate(huv + hb2);
            float2 t4 = saturate(huv - hb2);
            // Each blur weight is scaled by whether that tap is SKY, then the sum is renormalised, so
            // a tap that landed on the hull or the rigging contributes nothing instead of dragging the
            // far ocean toward its colour.
            float s0 = HorizonSkyWeight(t0) * 0.34;
            float s1 = HorizonSkyWeight(t1) * 0.24;
            float s2 = HorizonSkyWeight(t2) * 0.24;
            float s3 = HorizonSkyWeight(t3) * 0.09;
            float s4 = HorizonSkyWeight(t4) * 0.09;
            float skySum = s0 + s1 + s2 + s3 + s4;   // 1 when every tap is sky, 0 when none is
            float3 perAzimuth =
                  (UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, t0, 0).rgb * s0
                 + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, t1, 0).rgb * s1
                 + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, t2, 0).rgb * s2
                 + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, t3, 0).rgb * s3
                 + UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, t4, 0).rgb * s4)
                / max(skySum, HORIZON_SKY_MIN_WEIGHT);
            float2 centreUV = float2(0.5, saturate(horizonUV.y));
            float centreSky = HorizonSkyWeight(centreUV);
            float3 centreBand = UNITY_SAMPLE_TEX2D_LOD(_CameraOpaqueTexture, centreUV, 0).rgb;

            // Two independent reasons to stop trusting the per-azimuth sample - the projection is
            // unusable, or its taps are not sky. Take whichever is stronger; both are smooth, so the
            // handover is too.
            float useCentre = max(toCentre, 1.0 - skySum);
            float3 opaqueSky = lerp(perAzimuth, centreBand, useCentre);
            // How much of the sample finally chosen is really sky. When the horizon row carries
            // geometry all the way across - a hull filling the frame, a coastline - there IS no
            // rendered sky to match, and the honest answer is the environment cube rather than the
            // colour of a sail. Last resort, and WEIGHTED: the cube does not match the rendered
            // skybox exactly, and a hard switch between the two prints a visible band (tried
            // 2026-07-22, rejected - see the horizon-haze notes).
            float skyConfidence = lerp(skySum, centreSky, useCentre);
            skyAtHorizon = lerp(SampleEnvironment(incomingRay), opaqueSky, skyConfidence);
"""
C_NEW = """            // BOTH opaque samples read the horizon ROW - the centre band is just a different COLUMN
            // of that same row - so both stay valid exactly as long as the row is in frame, and
            // neither does once it is not. When the horizon leaves the frame the saturate() below
            // pins every tap to the TOP SCREEN ROW, which over open ocean is the skybox BELOW the
            // horizon: a flat ground colour, one near-discontinuity away from the bright horizon
            // band. Both crossfade ends then deliver that same wrong colour at full confidence, and
            // THAT is the pop with the camera high and pitched down - the horizon sits at eye level
            // for any camera above the water, so it exits the frame at pitch ~= -halfFov.
            //
            // No part of the image can answer the question at that pose, so the honest move is to
            // drop CONFIDENCE and let the environment cube take over. Clamping a blend WEIGHT stays
            // smooth; clamping a SAMPLE POSITION silently substitutes a different part of the image,
            // and the smooth blend then faithfully delivers the wrong colour.
            #define HORIZON_ONSCREEN_FADE 0.04  // edgeMinY over which the row counts as in frame
            float horizonRowOnScreen = smoothstep(0.0, HORIZON_ONSCREEN_FADE, edgeMinY);

            float2 huv = saturate(horizonUV);
            float perAzimuthSky;
            float3 perAzimuth = SampleHorizonSky(huv, perAzimuthSky);
            // Same kernel for the centre column, not a single tap: the centre band sets the colour of
            // the WHOLE far ocean whenever the per-azimuth sample hands over, so one mast crossing
            // one pixel must not swing it. Sharing the helper also keeps its colour and its
            // confidence consistent with each other - a 5-tap confidence over a 1-tap colour would
            // report "mostly sky" while delivering the mast.
            float centreSky;
            float3 centreBand = SampleHorizonSky(float2(0.5, huv.y), centreSky);

            // Two independent reasons to stop trusting the per-azimuth sample - the projection is
            // unusable, or its taps are not sky. Take whichever is stronger; both are smooth, so the
            // handover is too.
            float useCentre = max(toCentre, 1.0 - perAzimuthSky);
            float3 opaqueSky = lerp(perAzimuth, centreBand, useCentre);
            // How much of the sample finally chosen is really sky. Zero when the horizon row carries
            // geometry all the way across - a hull filling the frame, a coastline - and zero when the
            // row is not in frame at all. In both cases there IS no rendered sky to match, and the
            // honest answer is the environment cube rather than the colour of a sail or of the
            // skybox's underside. Last resort, and WEIGHTED: the cube does not match the rendered
            // skybox exactly, and a hard switch between the two prints a visible band (tried
            // 2026-07-22, rejected - see the horizon-haze notes).
            float skyConfidence = lerp(perAzimuthSky, centreSky, useCentre) * horizonRowOnScreen;
            skyAtHorizon = lerp(SampleEnvironment(horizonDir), opaqueSky, skyConfidence);
"""
EDITS.append(('C sampling + confidence', C_OLD, C_NEW))

# ---------------------------------------------------------------- D: cube fallback direction
D_OLD = """            // Tiers without the opaque texture keep the reflection-cube fallback
            // (uniform gate, implicit derivatives allowed here).
            skyAtHorizon = SampleEnvironment(incomingRay);
"""
D_NEW = """            // Tiers without the opaque texture keep the reflection-cube fallback
            // (uniform gate, implicit derivatives allowed here). Along the HORIZON direction, not
            // the view ray: the view ray points down at the water, so the cube would be read well
            // below the horizon.
            skyAtHorizon = SampleEnvironment(horizonDir);
"""
EDITS.append(('D cube fallback direction', D_OLD, D_NEW))

# ---------------------------------------------------------------- F: legacy stopgap, same defect
F_OLD = """        outColor = lerp(outColor, SampleEnvironment(incomingRay), horizonFade);
"""
F_NEW = """        outColor = lerp(outColor, SampleEnvironment(HorizonDirection(incomingRay)), horizonFade);
"""
EDITS.append(('F legacy stopgap direction', F_OLD, F_NEW))

# ================================================================= PHASE 1: verify everything
failures = []
for name, old, new in EDITS:
    count = text.count(old)
    if count != 1:
        failures.append('%s: anchor matches %d times (want 1)' % (name, count))
        continue
    idx = text.index(old)
    if not (idx == 0 or text[idx - 1] == '\n'):
        failures.append('%s: anchor does not start at a line boundary (idx %d)' % (name, idx))

if failures:
    print('ABORTED, nothing written:')
    for f in failures:
        print('  - ' + f)
    sys.exit(1)

# ================================================================= PHASE 2: apply
os.makedirs('/tmp/_base', exist_ok=True)
with open('/tmp/_base/WaterSurfaceFragStages.hlsl', 'wb') as f:
    f.write(raw)

out = text
for name, old, new in EDITS:
    out = out.replace(old, new, 1)

# post-splice sanity
assert out.count('SampleEnvironment(incomingRay)') == 0, 'a downward cube sample survived'
assert out.count('float3 SampleHorizonSky(float2 uv, out float skyWeight)') == 1
assert out.count('SampleHorizonSky(') == 3, 'helper decl + 2 call sites expected'
assert out.count('float3 HorizonDirection(float3 viewRay)') == 1
assert out.count('HorizonDirection(') == 3, 'helper decl + 2 call sites expected'
assert out.count('#define HORIZON_SKY_MIN_WEIGHT') == 1, 'duplicate/lost HORIZON_SKY_MIN_WEIGHT'
assert out.count('#define HORIZON_BLUR_STEP') == 1, 'duplicate/lost HORIZON_BLUR_STEP'
assert out.count('#define HORIZON_AZIMUTH_MIN') == 1, 'duplicate/lost HORIZON_AZIMUTH_MIN'
assert out.count('horizonRowOnScreen') == 2
assert out.count('float3 horizonDir') == 1
assert out.count('{') == text.count('{') + 2, 'brace delta != +2 (two helper bodies)'
assert out.count('}') == text.count('}') + 2, 'brace delta != +2 (two helper bodies)'
assert '\r' not in out, 'CR introduced'

# indentation asserts: inserted in-function blocks must keep their column
for label, block, col in (('C_NEW', C_NEW, 12), ('B_NEW', B_NEW, 8), ('F_NEW', F_NEW, 8)):
    for line in block.split('\n'):
        if line.strip():
            indent = len(line) - len(line.lstrip(' '))
            assert indent >= col, '%s under-indented (%d < %d): %r' % (label, indent, col, line[:50])

with open(PATH, 'w', encoding='utf-8', newline='') as f:
    f.write(out)

new_md5 = hashlib.md5(open(PATH, 'rb').read()).hexdigest()
print('OK  %s -> %s' % (EXPECT_MD5, new_md5))
print('backup: /tmp/_base/WaterSurfaceFragStages.hlsl')
print('lines: %d -> %d' % (text.count('\n'), out.count('\n')))
