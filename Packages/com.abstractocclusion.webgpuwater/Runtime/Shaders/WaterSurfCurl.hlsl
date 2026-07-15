// WebGpuWater - procedural plunging lip sheet for the surf breaker fronts (CURL layer).
//
// Where a front classifies as PLUNGING (Iribarren, WaterSurfWaves.hlsl) and its crest segment is
// strong, the cresting face grows an actual overturning lip for the break moment - the KWS1
// spectacle, generated from the front field instead of hand-placed flipbook patches. The overhang
// cannot live on the heightfield, so it renders on a dedicated dense strip mesh (the WaterHeroWave
// sheet pattern: vertex branch + fragment discard below a min weight), spawned and placed by the
// WaterSurfCurl component.
//
// The lip is the hero wave's ATTRACTOR CURL (rotation of crest points around a pivot ahead of the
// face, US7561993 family) driven by the front's OWN terms (SurfComputeFrontTerms) instead of the
// hero uniforms - so the sheet, the base surface, the whitewash and the breaker glow all read one
// canonical front. Fully closed-form (no textures, no sim state, no readback): WebGPU-safe.
//
// CURL-1 (this file's first life): a STATIC TEST RIBBON over a synthetic plane beach - the ribbon
// carries the whole front + curl so the lifecycle can be judged over a flat ocean with no shore
// field baked. CURL-2 flips _SurfCurlParams.z to delta mode: the base surface already renders the
// front (EvaluateSurfWaves), the sheet adds only the curl rotation delta, and placement follows
// the break line from the CPU shore arrays.
//
// RENDER-ONLY: buoyancy keeps the heightfield-safe base surface (LargeWaveField mirrors
// SurfFrontHeight, never the curl) - a floater under the lip rides the un-curled front.
#ifndef WEBGPUWATER_SURF_CURL_INCLUDED
#define WEBGPUWATER_SURF_CURL_INCLUDED

// Requires WaterSurfWaves.hlsl (SurfFrontTerms, SurfFieldMask + the _Surf* front knobs, which stay
// the ONE source of wavelength/period/amplitude/lean for the sheet too) and WaterShore.hlsl
// (ShoreSample for the live field). WaterSurface.shader includes both via WaterLargeWaves.hlsl
// before this header.

// Per-strip uniforms (property block, set by WaterSurfCurl; all-zero = inert).
float  _IsSurfCurl;      // 1 = this renderer is the surf lip-sheet strip
float4 _SurfCurlFrame;   // xy = ribbon centre (world xz), zw = along-crest unit direction (world xz)
float4 _SurfCurlShape;   // x = max roll angle (rad), y = curl start fraction (0..1)
                         // z = pivot ahead fraction (of face length), w = pivot height fraction (of H)
float4 _SurfCurlParams;  // x = master gain, y = plunge-gate override (0 = use the front's own
                         //     Iribarren plunge weight; >0 = force this weight, test knob)
                         // z = 1 render the FULL front + curl (CURL-1 test over a flat ocean),
                         //     0 = delta-only (CURL-2: the base surface already carries the front)
                         // w = overCap value where the roll COMPLETES (the roll-speed knob:
                         //     closer to SURF_CURL_ROLL_START = a snappier overturn)
float4 _SurfCurlExtent;  // x = along half-length (m), y = across half-width (m) of the ribbon
                         // z = shoulder start fraction of the along half-length,
                         // w = lip BASE thickness (x face length): how far down the face the
                         //     rolling sheet extends
float4 _SurfCurlField;   // x = still-water depth (m) at the ribbon centre (synthetic beach),
                         // y = beach slope tan(beta) (synthetic beach),
                         // z = 1 use the synthetic plane beach (CURL-1 static test),
                         //     0 sample the REAL Layer A shore field per vertex (CURL-2 live)
                         // w = lip TIP thickness (share of the base length on the crest's back
                         //     side): how much of the back rolls over as the tip of the curl

#define SURF_CURL_MIN_WEIGHT     0.02  // sheet fragments below this curl weight discard (delta mode)
#define SURF_CURL_MIN_HEIGHT     0.25  // lip fades in as the local front height passes this (m)
#define SURF_CURL_NORMAL_EPSILON 0.12  // param-space FD step (m) for the sheet's geometric normal
// Lever gate: a rotated point's displacement is ~angle x distance-to-pivot, so water far from the
// pivot flings wildly even at small angles. The lip is the water within about the crest tip's own
// orbit radius; participation fades between these multiples of it and the tube stays bounded by
// construction (pivot geometry, not mask shape, sets the arc size).
#define SURF_CURL_LEVER_GATE_LO  1.0
#define SURF_CURL_LEVER_GATE_HI  1.6
// The roll angle is driven by a MONOTONIC progress over the front's break-criterion clock
// (overCap), starting where cresting starts. It must never track the visibility gates: scaling
// theta by cresting*(1-broken) made the lip visibly UN-ROLL as the bore took the wave - the
// "folds on itself" read. Visibility fades the finished roll out; the roll itself only advances.
#define SURF_CURL_ROLL_START     0.75
// Geometry eases exactly onto the base surface before the discard line: at the old hard cut the
// delta was still ~10 cm at weight = MIN_WEIGHT, printing a visible stitch against the water.
#define SURF_CURL_FOOT_BLEND_END 0.10
// CURL-3 lip dressing (render-only): the sheet's curl weight feeds the surf whitewash coverage
// and the cresting-lip SSS glow the fragment already consumes - the lip whitens toward its tip
// and glows like a breaking face instead of shading as clean ocean glued onto the wave (the KWS
// read is mostly this foam, not the geometry).
#define SURF_CURL_FOAM_WHITEWASH 0.9
#define SURF_CURL_FOAM_BREAKER   0.6
// Full/test mode keeps fragments wherever the front itself is visible (the ribbon IS the wave on a
// flat ocean); the window keeps near-flat strip regions discarded so they never z-fight the plane.
#define SURF_CURL_KEEP_HEIGHT_LO 0.02
#define SURF_CURL_KEEP_HEIGHT_HI 0.08

// World xz -> ribbon-local coordinates: x = u along the crest line, y = d across it, +d = travel
// (SHOREWARD - shore distance shrinks along +d). Same frame convention as HeroLocalCoords, so the
// curl rotation math below is the hero's verbatim.
float2 SurfCurlLocalCoords(float2 worldXZ)
{
    float2 rel = worldXZ - _SurfCurlFrame.xy;
    float2 along = _SurfCurlFrame.zw;
    float2 travel = float2(-along.y, along.x);
    return float2(dot(rel, along), dot(rel, travel));
}

// Synthetic plane beach under the ribbon (CURL-1): depth shrinks shoreward at the authored slope;
// shore distance is the depth walked up that slope (the waterline is where depth hits 0). CURL-2
// replaces this with the real Layer A sample at the vertex's world xz.
void SurfCurlTestField(float d, out float depth, out float shoreDist, out float tanBeta)
{
    tanBeta = max(_SurfCurlField.y, 1e-3);
    depth = _SurfCurlField.x - d * tanBeta;
    shoreDist = max(depth, 0.0) / tanBeta;
}

// Canonical sheet evaluation at ribbon-local (u, d).
//   worldOffset : WORLD-space offset from the flat (still-level) surface point. Full/test mode
//                 carries the whole front + curl; delta mode carries only the curl rotation.
//   weight      : lip visibility weight 0..1 (drives the fragment discard + normal blend)
// The curl rotation happens per vertex in the point's OWN (local toward-shore, up) plane - a
// straight ribbon frame folded the spirals wherever the real break line curved, because dAcross
// (a field quantity) was being treated as a ribbon-axis distance.
void SurfCurlEvaluate(float2 uv, out float3 worldOffset, out float weight)
{
    float u = uv.x;
    float d = uv.y;
    worldOffset = float3(0.0, 0.0, 0.0);
    weight = 0.0;

    float2 along = _SurfCurlFrame.zw;
    float2 travel = float2(-along.y, along.x);
    float2 worldXZ = _SurfCurlFrame.xy + along * u + travel * d;

    // Field source: the synthetic plane beach (CURL-1 static test) or the REAL Layer A shore
    // field at this vertex's world xz (CURL-2 live - the same sample the base surface fronts
    // read, so the sheet and the water under it always agree). The live path also inherits the
    // surface's field MASK so the lip dies exactly where the fronts do (band edge, lee side,
    // field border), and is inert while the surf layer itself is off.
    float depth;
    float shoreDist;
    float tanBeta;
    float fieldMask;
    float2 toShoreDir;
    if (_SurfCurlField.z > 0.5)
    {
        SurfCurlTestField(d, depth, shoreDist, tanBeta);
        fieldMask = 1.0;
        toShoreDir = travel;
    }
    else
    {
        ShoreData shore = ShoreSample(worldXZ);
        depth = shore.depth;
        shoreDist = max(shore.sdfDist, 0.0);
        tanBeta = shore.slopeTan;
        toShoreDir = (dot(shore.toShore, shore.toShore) > 1e-6) ? shore.toShore : travel;
        fieldMask = (_SurfActive > 0.5)
            ? SurfFieldMask(shore.depth, shore.toShore, shore.influence)
            : 0.0;
    }

    SurfFrontTerms t = SurfComputeFrontTerms(worldXZ, SurfWarpDistance(shoreDist), depth, tanBeta,
                                             _WaveTime);
    // The base surface renders front.x * mask (EvaluateSurfWaves) - the sheet composes the SAME
    // masked height so delta mode's foot lands on the rendered water, not the raw profile.
    // (fieldMask is 1 on the synthetic test beach.)
    float frontHeight = SurfFrontHeightFromTerms(t) * fieldMask;

    // Shoulder: the lip dies smoothly toward the ribbon's along ends so it never cuts off in a
    // hard vertical line where the strip mesh ends.
    float alongHalf = max(_SurfCurlExtent.x, 1e-3);
    float shoulder = 1.0 - smoothstep(_SurfCurlExtent.z * alongHalf, alongHalf, abs(u));

    // The LIP mask is its own footprint, NOT the surface profile (whose 2.4x-longer back once
    // rolled up as a giant backward balloon - the "unroll" bug): a face-length-scaled sech^2,
    // asymmetric. Base thickness (x face length) sets how far DOWN the face the sheet extends;
    // tip thickness (share of the base length) sets how much of the crest's back rolls over as
    // the tip of the curl.
    float curlStart = saturate(_SurfCurlShape.y);
    float lipBaseLen = t.faceLen * max(_SurfCurlExtent.w, 0.05);
    float lipLen = (t.dAcross < 0.0) ? lipBaseLen : lipBaseLen * max(_SurfCurlField.w, 0.05);
    float lipSech = 1.0 / cosh(min(abs(t.dAcross) / max(lipLen, 1e-3), SURF_SECH_ARG_MAX));
    float lipMask = lipSech * lipSech;
    float crestness = saturate((lipMask - curlStart) / max(1.0 - curlStart, 1e-3));
    float plunge = (_SurfCurlParams.y > 0.0) ? _SurfCurlParams.y : t.breakType.y;

    // Everything that shapes BOTH the roll angle and the visibility (spatial mask + regime +
    // gain gates), WITHOUT the lifecycle fade terms - see SURF_CURL_ROLL_START.
    float rollGates = crestness * plunge * fieldMask
                    * smoothstep(0.0, SURF_CURL_MIN_HEIGHT, t.height)
                    * shoulder * saturate(_SurfCurlParams.x);
    // Monotonic roll progress over the break clock (overCap): starts with cresting, completes at
    // the roll-speed knob's end value, NEVER reverses while 'broken' takes the wave over.
    float rollEnd = max(_SurfCurlParams.w, SURF_CURL_ROLL_START + 0.05);
    rollGates *= smoothstep(SURF_CURL_ROLL_START, rollEnd, t.overCap);

    float2 deltaLocal = float2(0.0, 0.0); // (local toward-shore metres, up metres)
    if (rollGates > 0.0)
    {
        // Crest-local frame: +x = the point's own toward-shore axis, +y = up, origin ON the
        // crest. a = shoreward metres from the crest (dAcross grows offshore).
        float a = -t.dAcross;
        float2 q = float2(a, frontHeight);
        float2 pivot = float2(_SurfCurlShape.z * t.faceLen, _SurfCurlShape.w * t.height);
        float2 rel = q - pivot;
        // Lever gate (see the constants block): participation fades past the crest tip's own
        // orbit radius, so no distant water is ever flung on a long lever arm. Gated into the
        // shared factor so the roll angle, discard and normal blend all agree.
        float tipRadius = length(float2(_SurfCurlShape.z * t.faceLen,
                                        (1.0 - _SurfCurlShape.w) * t.height));
        rollGates *= 1.0 - smoothstep(SURF_CURL_LEVER_GATE_LO * tipRadius,
                                      SURF_CURL_LEVER_GATE_HI * tipRadius, length(rel));

        // Visibility: the finished roll FADES OUT through the break (never un-rotates) - and the
        // lip only shows while the front is actually cresting.
        weight = rollGates * t.cresting * (1.0 - t.broken);

        if (rollGates > 0.0)
        {
            // Attractor curl (the hero wave's pivot rotation): the roll angle scales with the
            // per-point factor, so the lip tip travels furthest - a continuous spiral that
            // pitches forward over the pivot (clockwise in (toward-shore, up)) and plunges.
            float theta = _SurfCurlShape.x * rollGates;
            float sinT = sin(theta);
            float cosT = cos(theta);
            float2 rotated = pivot + float2(rel.x * cosT + rel.y * sinT,
                                            -rel.x * sinT + rel.y * cosT);
            deltaLocal = rotated - q;
            // Ease the geometry exactly onto the base surface before the discard line: at a hard
            // cut the delta was still ~10 cm at MIN_WEIGHT - the visible stitch against the water.
            deltaLocal *= smoothstep(SURF_CURL_MIN_WEIGHT, SURF_CURL_FOOT_BLEND_END, weight);
        }
    }

    worldOffset = float3(toShoreDir.x * deltaLocal.x, deltaLocal.y, toShoreDir.y * deltaLocal.x);

    // Full mode: the ribbon carries the whole front + curl (flat-ocean look test); the weight is
    // widened so the visible front's fragments survive the discard, not just the lip's.
    if (_SurfCurlParams.z > 0.5)
    {
        worldOffset.y += frontHeight;
        weight = max(weight, smoothstep(SURF_CURL_KEEP_HEIGHT_LO, SURF_CURL_KEEP_HEIGHT_HI,
                                        frontHeight) * shoulder);
    }
}

// World-space vertex offset for a strip vertex whose UNDISPLACED world xz is worldXZ.
float3 SurfCurlOffset(float2 worldXZ, out float weight)
{
    float3 offset;
    SurfCurlEvaluate(SurfCurlLocalCoords(worldXZ), offset, weight);
    return offset;
}

// The full sheet surface point for a ribbon-param uv, at the still-water level (the ambient
// swell/ripples the vertex shader adds on top cancel in the FD below).
float3 SurfCurlWorldPoint(float2 uv)
{
    float2 along = _SurfCurlFrame.zw;
    float2 travel = float2(-along.y, along.x);
    float2 flatXZ = _SurfCurlFrame.xy + along * uv.x + travel * uv.y;
    float3 offset;
    float weightUnused;
    SurfCurlEvaluate(uv, offset, weightUnused);
    return float3(flatXZ.x, 0.0, flatXZ.y) + offset;
}

// Geometric normal of the curled sheet from a param-space finite difference - the sheet overhangs
// (multi-valued in world xz), so slopes in xz would be wrong; tangents in ribbon (u, d) parameter
// space stay single-valued through the overturn. World-space throughout (the per-vertex local
// curl frames make a fixed ribbon-local construction wrong on curved shores).
float3 SurfCurlSheetNormal(float2 worldXZ)
{
    float2 uv = SurfCurlLocalCoords(worldXZ);
    float3 pointAtUv    = SurfCurlWorldPoint(uv);
    float3 pointAlongU  = SurfCurlWorldPoint(uv + float2(SURF_CURL_NORMAL_EPSILON, 0.0));
    float3 pointAcrossD = SurfCurlWorldPoint(uv + float2(0.0, SURF_CURL_NORMAL_EPSILON));
    return normalize(cross(pointAcrossD - pointAtUv, pointAlongU - pointAtUv));
}

#endif // WEBGPUWATER_SURF_CURL_INCLUDED
