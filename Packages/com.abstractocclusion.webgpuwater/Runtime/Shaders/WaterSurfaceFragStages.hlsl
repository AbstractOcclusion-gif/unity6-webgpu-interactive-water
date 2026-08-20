// WaterSurface pass: the frag() stages (SHADER-SPLIT-3). Stage bodies are
// VERBATIM moves of the old monolithic frag blocks - each stage re-binds the
// shared WaterGeomStage fields to the original local names, so the moved code
// is unchanged; any behavior change here is a bug.
// NOT a standalone library: this is a splinter of WaterSurface.shader's pass,
// included AFTER the pass-local uniforms, SampleRipple, v2f and vert() it
// reads. It must stay the LAST include, directly above frag().
// WGSL derivative contracts hold because every stage is called from frag's
// UNIFORM control flow (the only branch above the calls gates on _Underwater,
// a uniform) - do not call stages from per-fragment branches.
#ifndef WATER_SURFACE_FRAG_STAGES_INCLUDED
#define WATER_SURFACE_FRAG_STAGES_INCLUDED

// ---- Stage tuning constants + chunk-footprint flags (moved from Pass 0 verbatim): the
// after-fog PondFoamOverlay pass includes these stages too, so the values live with the
// code that reads them. The chunk textures/margins stay Pass-0 locals - only the two
// flags are read here, by PondFoamLayer's overlay-skip gate. ----
#define SSS_AMPLITUDE_EPSILON   1e-3   // guards the crest/amplitude ratio when the swell is flat
// Shallow-water clarity (surf run-out): under this column depth the shore band
// blends toward the refracted ground, so centimetres-deep water reads clear
// instead of flat opaque blue between the last bore and the beach.
#define SHALLOW_CLARITY_DEPTH 0.6   // metres; blend fully faded out at this depth
#define SHALLOW_CLARITY_BLEND 0.5   // max blend toward the refracted colour at depth 0
// Wet-sand glaze weights (swash zone): the thin film is centimetres of water ON
// the sand, so it pulls HARD toward the refracted ground (never blue ocean on the
// beach), and the drying glaze behind it mixes darkened ground + a sky sheen.
#define WET_FILM_MIN_TRANSPARENCY 0.6    // film pull toward the ground at the waterline
#define WET_FILM_DEPTH_GAIN       0.3    // extra pull as the film thins up-beach
// Half-width (m of height above/below the still level) of the sea-to-film cross-fade. The film
// pull is a large constant that used to appear the instant the bed crossed the water level, which
// printed a razor line along the waterline; this is the band it ramps over instead. Height, not
// ground distance: on a gentle beach a few centimetres of height is a metre or so of sand.
#define WET_FILM_WATERLINE_BAND   0.15
#define WET_GLAZE_EDGE            0.25   // smoothstep width of the drying wet edge
#define WET_GLAZE_REFRACT         0.7    // refracted-ground weight in the wet look
#define WET_GLAZE_REFLECT         0.12   // reflected-sky weight in the wet look
#define WET_GLAZE_STRENGTH        0.85   // max glaze opacity over the base shading
// Peaked-look refine: short steps along the ripple normal sharpen wave crests.
// The step COUNT is tier-driven (_PeakedRefineSteps via the body's property
// block): each step is a dependent texture fetch per pixel, the single biggest
// fragment cost on mobile. The cap bounds the loop for the compiler.
#define PEAKED_REFINE_MAX_STEPS 8
#define PEAKED_REFINE_STEP  0.005
// Chunk footprint flags (published per body by WaterVolume.Chunk.cs; 0 = ordinary body).
// Declared here - not in Pass 0 - so PondFoamLayer's overlay-skip gate can read them in
// every pass that includes these stages.
float _ChunkSphereClip;
float _ChunkBoxClip;
float _ChunkUseMesh;

// ================== frag stages (SHADER-SPLIT-3) ==================
// frag() is decomposed into single-responsibility stages that read in render
// order. Stage bodies are VERBATIM moves of the old frag blocks: each stage
// re-binds the shared-geometry fields to the original local names, so the
// moved code is unchanged - any behavior change here is a bug.

// Per-fragment surface geometry, evaluated ONCE and shared by every stage.
struct WaterGeomStage
{
    float3 normal;       // world-space shading normal (detail folded in; NOT flipped for underwater)
    float2 nxz;          // pool-space ripple+wind slope (foam flow/relief input)
    float3 incomingRay;  // camera -> surface, normalized
    float viewDist;      // metres from the camera to the surface
    float roughness;     // shared specular roughness (EffectiveWaterRoughness at viewDist)
    ShoreData shore;     // hoisted shore-substrate sample (inert off surf bodies)
    SurfWaveSample surf; // hoisted surf-front sample (inert off surf bodies)
    float surfGeomFoam;  // geometry foam from the surface's own Jacobian/slope
};

// One foam layer's contribution: coverage alpha + lit colour.
struct FoamLayer
{
    float alpha;
    float3 look;
};

WaterGeomStage EvaluateSurfaceGeometry(v2f i)
{
    float fade;
    // SOURCE xz, not i.worldPos: the vertex added the ripple HEIGHT before the FFT chop moved the
    // vertex horizontally, so reading the ripple back at the DISPLACED position takes the wake's
    // normal/foam/pinch from a different sim texel than the one that raised its bump. The two then
    // disagree by lbwDisp, which oscillates at swell frequency - the wake smears across its own
    // geometry. Harmless while a whole-field multiplier crushed the sea (disp was centimetres); at
    // honest metres disp IS metres. Every other consumer in this file already reads
    // largeWaveSourceXZ - the ripple was the one exception. i.position is pool space captured
    // pre-displacement, so the non-windowed branch was already source-correct.
    float3 rippleSourcePos = float3(i.largeWaveSourceXZ.x, i.worldPos.y, i.largeWaveSourceXZ.y);
    float4 info = SampleRipple(i.position, rippleSourcePos, fade);
    float interactiveRippleWeight = 1.0 - saturate(_IsRiver);
    // A river has no valid coordinates in the rectangular interactive-ripple simulation. The
    // vertex stage therefore excludes this field too; matching that gate here keeps its shading
    // normal attached to the analytic wind-wave geometry instead of an unrelated volume texture.
    info *= interactiveRippleWeight;

    // make the water look more "peaked": walk a few steps along the ripple normal
    // in the active UV domain (pool for whole-body, sim window for windowed).
    float2 coord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                        : (WorldToSim(rippleSourcePos).xz * 0.5 + 0.5);
    int refineSteps = clamp((int)(_PeakedRefineSteps * interactiveRippleWeight),
                            0, PEAKED_REFINE_MAX_STEPS);
    [loop] // uniform trip count (tier knob); explicit-LOD samples are loop-safe
    for (int k = 0; k < refineSteps; k++)
    {
        // The walk only needs a DIRECTION, so the cheap bilinear tap is enough per step. Each
        // replacement is re-faded because SampleRipple already returns a FADED normal: fading once
        // here and once after the loop would square it at refineSteps = 0 only, so the sim-window
        // border falloff changed shape with the quality tier.
        // World slope, not the sim-space one: an uncorrected walk collapses on a deep body and
        // over-steps on a shallow one, so the SAME ripple peaked differently with pool depth.
        coord += info.ba * SIM_SLOPE_TO_POOL * _SimSlopeToWorld.xy * PEAKED_REFINE_STEP;
        info.ba = SampleWaterBilinear(coord).ba * fade;
    }
    // The loop's last read is bilinear - exactly the faceting SampleWaterBicubic exists to remove,
    // and it left the fragment normal on a different filter from the vertex height (bicubic, via
    // SampleRipple). One bicubic read at the refined coord fixes both; doing it per step instead
    // would cost 16 tex2Dlod every iteration for a walk that only needs a direction.
    if (refineSteps > 0) info.ba = SampleWaterBicubic(coord).ba * fade;

    // Combine the ripple normal (info.ba = normal.xz) with the wind-wave
    // tilt. A height gradient g contributes normal.xz = -g, so the two
    // slopes simply add in the xz components before re-deriving y.
    float riverWeight = saturate(_IsRiver);
    float2 sampledRiverVelocity = SampleRiverFluidVelocity(
        i.riverBakeUv, i.riverCurrentData.w);
    float4 riverCurrentData = float4(i.riverCurrentData.xy, sampledRiverVelocity);
    float2 gridWaveSample = WindWaveSampleXZ(i.position.xz, i.largeWaveSourceXZ);
    float2 riverWaveSample = RiverCurrentWaveSampleXZ(riverCurrentData);
    float2 windSlope = WaveSlope(lerp(gridWaveSample, riverWaveSample, riverWeight))
                     * _WaveNormalStrength;
    // POOL convention, kept as the foam flow / relief input (g.nxz) so foam is unchanged by this.
    float2 nxz = info.ba - windSlope;

    // The shading normal is built from WORLD slopes, NOT pool ones. Assembling it in pool space put
    // the footprint/depth aspect factor inside sqrt(1 - dot(n,n)): on a 200 m wide, 5 m deep body
    // that factor is 40, so a real 5% chop arrives as a slope of 2, the sqrt hits its floor and the
    // normal collapses to horizontal - the surface breaks into hard lenses that get worse the
    // shallower the body is. PoolNormalToWorld divides the factor back out afterwards, which is
    // exact while nothing saturates and worthless once the normalize in between has eaten it.
    // Converting first means nothing ever exceeds a real slope, at any depth or footprint.
    float2 nxzWorld = info.ba * SIM_SLOPE_TO_POOL * _SimSlopeToWorld.xy
                    - windSlope * _PoolSlopeToWorld.xy;
    // Rotation only - the extent division is already carried by _PoolSlopeToWorld above. The
    // interpolated basis is VolumeRot's x/up/z frame for every existing grid and the mesh's
    // transported width/up/flow frame for a river, so slopes follow descending waterfall spans.
    float3 slopeAxisX = normalize(i.worldTangent.xyz);
    float3 slopeAxisZ = normalize(cross(i.worldNormal, slopeAxisX) * i.worldTangent.w);
    float3 normal = normalize(i.worldNormal
                            + slopeAxisX * nxzWorld.x
                            + slopeAxisZ * nxzWorld.y);
    // ---- Coastline: ONE shore-substrate + surf-front sample at the SOURCE xz, hoisted
    // here and shared by the wave normal, the whitewash foam, the crest glow and the
    // swash below - both cheaper and far less inlining pressure on the shader compiler
    // than re-evaluating per consumer. Inert (zeros / deep water) unless this body runs
    // the surf layer over a baked Layer A field. ----
    ShoreData shoreFrag = ShoreDataInert();
    SurfWaveSample surfFrag = SurfWaveSampleInert();
    if (_SurfActive > 0.5 && _ShoreDepthValid > 0.5)
    {
        shoreFrag = ShoreSample(i.largeWaveSourceXZ);
        surfFrag = EvaluateSurfWaves(i.largeWaveSourceXZ, shoreFrag.depth,
                                     shoreFrag.sdfDist, shoreFrag.toShore,
                                     shoreFrag.slopeTan,
                                     shoreFrag.influence, _SurfBeatTime);
    }
    // Open water: PoolNormalToWorld divides normal.xz by the (large) footprint extent,
    // flattening the surface so screen-space refraction collapses on big bodies. Add a
    // WORLD-space wave slope here (after that division) so open water keeps real normals
    // and refraction holds at any size. No-op for pool/small bodies (_LargeBody = 0).
    // .w = GEOMETRY foam: breaking whiteness derived from the composite surface's own
    // Jacobian pinch + slope - glued to the rendered waves by
    // construction, so foam can never detach from what the eye tracks.
    float surfGeomFoam = 0.0;
    if (_LargeBody > 0.5)
    {
        // Large-body waves are evaluated in world XZ. For a ribbon, ask the established wave
        // function for its world-XZ tilt relative to world-up, then express that same tilt in the
        // transported width/flow frame. The pool/lake/ocean input and result remain unchanged.
        float3 largeWaveBaseNormal = normalize(lerp(normal, float3(0.0, 1.0, 0.0), riverWeight));
        float4 normalFoam = ApplyLargeBodyWaveNormalFoamShore(largeWaveBaseNormal,
                                                              i.largeWaveSourceXZ,
                                                              _WaveNormalStrength,
                                                              shoreFrag, surfFrag);
        float inverseLargeWaveUp = rcp(max(normalFoam.y, LBW_NORMAL_MIN_Y));
        float2 riverLargeWaveTilt = normalFoam.xz * inverseLargeWaveUp;
        float3 riverLargeWaveNormal = normalize(normal
                                              + slopeAxisX * riverLargeWaveTilt.x
                                              + slopeAxisZ * riverLargeWaveTilt.y);
        normal = normalize(lerp(normalFoam.xyz, riverLargeWaveNormal, riverWeight));
        surfGeomFoam = normalFoam.w;
    }
    // View ray + distance from one subtraction (the distance also drives the detail
    // normal fade and the shared specular roughness below).
    float3 toSurface = i.worldPos - _WorldSpaceCameraPos;
    float viewDistWorld = length(toSurface);
    float3 incomingRay = toSurface / max(viewDistWorld, 1e-5);

    // ---- Crest-style crossing scrolling detail normals: micro-ripple detail finer
    // than the FFT cascades resolve, sampled in WORLD metres at the undisplaced source
    // xz (like the foam) so it rides the waves and is body-size independent. Added as
    // an xz tilt exactly like the FFT cascade tilt. Inert with the default "bump"
    // texture or strength 0. The underside has its OWN strength (Underwater Surface
    // block; default 0 = the historical detail-free ceiling), so raising it lets the
    // seen-from-below surface carry the same micro-ripple as the top. Both the side
    // pick and the gate are uniforms (WGSL-safe branch). ----
    float detailNormalStrength = (_Underwater > 0.5) ? _UnderDetailNormalStrength
                                                     : _DetailNormalStrength;
    if (detailNormalStrength > 0.0)
    {
        // Wind ripple is not an even film: it concentrates on the STEEP faces of the waves carrying
        // it and thins out in the flat troughs. length(normal.xz) IS the sine of the local tilt (the
        // normal is unit here), read AFTER the large-body wave normal is folded in above, so ocean
        // swell and pond wind waves both drive it. Uniform-safe by construction: a multiply on the
        // strength, never a branch around the taps' implicit derivatives.
        float slopeSine = length(normal.xz);
        float steepness = saturate(slopeSine / DETAIL_CREST_REFERENCE_SLOPE);
        detailNormalStrength *= 1.0 + _DetailNormalCrestBoost * steepness;
        // Sea-state layer (gusts/slicks): the micro-ripple film is exactly what a real gust thickens
        // and a slick wipes - scale the detail strength by the same local factor the FFT tilt uses,
        // so near-field micro-ripple and far-field cascade roughness tell one story.
        detailNormalStrength *= SeaStateMssScale(i.largeWaveSourceXZ);
        float2 detailTilt;
        // _IsRiver is per-renderer uniform, so derivatives inside either detail-normal path remain
        // uniform across a WebGPU quad. Pools keep their exact wind/world-space path; ribbons use
        // metric UV1 coordinates and interpolated spline speed to visibly travel downstream.
        if (_IsRiver > 0.5)
            detailTilt = RiverDetailNormalTilt(riverCurrentData, viewDistWorld);
        else
            detailTilt = DetailNormalTilt(i.largeWaveSourceXZ, viewDistWorld);
        float3 gridDetailTilt = float3(detailTilt.x, 0.0, detailTilt.y);
        float3 riverDetailTilt = slopeAxisX * detailTilt.x + slopeAxisZ * detailTilt.y;
        // Preserve the original unrotated-pool detail path byte-for-byte while orienting only
        // river microdetail in the transported ribbon frame.
        float3 detailTiltWorld = lerp(gridDetailTilt, riverDetailTilt, saturate(_IsRiver));
        normal = normalize(normal + detailTiltWorld * detailNormalStrength);
    }
    WaterGeomStage g;
    g.normal = normal;
    g.nxz = nxz;
    g.incomingRay = incomingRay;
    g.viewDist = viewDistWorld;
    // Shared by the whole specular family. Pure ALU, so evaluating it for BOTH
    // sides costs nothing - the underwater path never reads it and the compiler
    // strips it there.
    g.roughness = EffectiveWaterRoughness(viewDistWorld);
    g.shore = shoreFrag;
    g.surf = surfFrag;
    g.surfGeomFoam = surfGeomFoam;
    return g;
}

float EvaluateWaterClarity(v2f i, ShoreData shoreFrag)
{
    // Depth clarity (auto transparency): ONE curve from the baked bed depth drives the
    // turbidity + underwater-fog reach below (and the deep-water tint in the shoreline
    // block). Identity (1) when the feature is off or no bed is baked, so every existing
    // body is unchanged. Blended toward the surf field's depth where it is live, so the
    // clarity waterline agrees with the rendered shore.
    float waterClarity = 1.0;
    if (_UseBedDepth > 0.5 && _BedValid > 0.5)
    {
        float bedPoolYClarity = tex2Dlod(_BedTex, float4(i.position.xz * 0.5 + 0.5, 0, 0)).r;
        float colDepthClarity = BedColumnDepthWorld(bedPoolYClarity, i.position.y, VolumeExtentSafe().y);
        if (_SurfActive > 0.5 && shoreFrag.influence > 0.0)
            colDepthClarity = lerp(colDepthClarity, shoreFrag.depth, saturate(shoreFrag.influence));
        waterClarity = WaterDepthClarity(colDepthClarity);
    }
    return waterClarity;
}

// Whitecap COVERAGE only: the dissolved, textured foam fraction, plus the pattern it was dissolved
// from and the parallax-lifted point that pattern was read at. Split out of OceanWhitecapLayer (the
// body is a verbatim move) so the UNDERSIDE stage can silhouette the same caps without paying for
// the lit look, which is above-water-only. Factored rather than copied for the reason
// PondFoamCoverage already records: per-consumer copies of a coverage formula have drifted in this
// file before, and the two faces of ONE surface disagreeing about where the foam is was a real bug.
float OceanWhitecapCoverage(v2f i, WaterGeomStage g, float2 foamWorldDdx, float2 foamWorldDdy,
                            out float3 oceanFoamPattern, out float2 oceanFoamSampleXZ)
{
    float3 incomingRay = g.incomingRay;
    ShoreData shoreFrag = g.shore;
    // ---- Ocean FFT whitecap foam: coverage sampled per pixel from the cascade (.w), on the
    // same crests as the normal tilt, then broken into moving lace by the foam flipbook -
    // the coverage is a black-point threshold that dissolves the pattern in (Crest's
    // WhiteFoamTexture). Whitecaps are matte, so the resulting alpha knocks the specular
    // reflection down before compositing (this surface expresses gloss as the reflection
    // term). Coverage source: FFT cascade accumulator on oceans, instantaneous geometry
    // foam on analytic bodies with the _LbwGeomFoamFloor opt-in (ocean-surface chunks);
    // pools leave this at 0. ----
    float oceanFoam = 0.0;                       // textured coverage: drives matte + blend
    oceanFoamPattern = float3(1.0, 1.0, 1.0);
    oceanFoamSampleXZ = i.largeWaveSourceXZ;     // parallax-lifted pattern-sample point
    float coverage = 0.0;
    if (_OceanFftActive > 0.5)
    {
        // The surf band is the surf system's territory: the FFT foam ACCUMULATOR
        // is depth-blind (its small cascades still whitecap at 2 m of water), so
        // accumulated ocean whitecaps fade out where the fronts/whitewash own the
        // shallows. Inert off surf bodies (the gate is 0 there).
        coverage = OceanFftFoam(i.largeWaveSourceXZ)
                 * (1.0 - LbwFoamOwnershipGate(shoreFrag));
    }
    else if (_LbwGeomFoamFloor > 0.0)
    {
        // ANALYTIC whitecaps (ocean-surface chunks - see _LbwGeomFoamFloor): no accumulator
        // exists on the analytic path, so the coverage is the INSTANTANEOUS geometry foam
        // (Gerstner Jacobian pinch + slope steepness, already computed by the normal stage) -
        // crests whiten as they pinch and fade as they relax. It then rides the exact same
        // pattern/dissolve/lit pipeline as the FFT whitecaps below.
        coverage = g.surfGeomFoam;
    }
    if (coverage > FOAM_MASK_EPSILON)
    {
        // Parallax: sample the PATTERN where a layer floating just above the surface
        // meets the view ray (coverage stays at the true surface point - foam is still
        // WHERE the sim says, it just reads as sitting on top of the water).
        float3 viewToCam = -incomingRay;
        oceanFoamSampleXZ = i.largeWaveSourceXZ + viewToCam.xz
            * (OCEAN_FOAM_PARALLAX_HEIGHT / max(viewToCam.y, OCEAN_FOAM_PARALLAX_MIN_VIEW_Y));

        // Stock white _FoamTex -> pattern ~= 1 -> solid coverage (no regression); a real
        // foam texture dissolves in as lace. Distance anti-tiling (second rotated octave)
        // hides the repeat toward the horizon; the contrast sharpen breaks round blobs.
        float foamCamDist = distance(i.largeWaveSourceXZ, _WorldSpaceCameraPos.xz);
        oceanFoamPattern = SampleOceanWhitecapPattern(oceanFoamSampleXZ, foamCamDist,
                                                      foamWorldDdx, foamWorldDdy);
        // WHO OWNS THE OUTLINE. FoamDissolve thresholds the PATTERN, with coverage only sliding the
        // threshold - so the foam's outline is literally the texture's iso-contours. Our whitecap
        // artwork is cellular, so it printed round caps no matter what the wave field was doing
        // underneath. Ceto splits these two jobs with Ceto_TextureWaveFoam
        // (Assets/Ceto/Shaders/OceanUnderWater.cginc:26 - foam.x = lerp(foam.x, foam.x * foamTexture,
        // Ceto_TextureWaveFoam)): its foam SHAPE comes from the multi-scale Jacobian field and the
        // texture only breaks it up. The other end of this lerp is the COVERAGE FRACTION itself,
        // which is where the crest-aligned structure lives (see OceanFoamAnisotropy).
        //
        // AND IT FADES WITH DISTANCE, which fixes a real far-field defect: a tiled pattern loses its
        // VARIANCE as it mips, so far out the dissolve is thresholding a near-uniform grey. The caps
        // stop being discrete, the result is a flat wash that reads far too BRIGHT, and - because the
        // texture is what gates the result - the foam knobs stop visibly doing anything out there.
        // Past the fade the foam IS the coverage fraction, which is the correct antialiasing of a
        // sub-pixel mask (its expected value) and is driven purely by the wave field, so every knob
        // keeps working all the way to the horizon.
        #define OCEAN_FOAM_TEXTURE_FADE_START 120.0  // metres where the pattern starts handing over
        #define OCEAN_FOAM_TEXTURE_FADE_RANGE 400.0  // ...and over which it fully does
        float oceanFoamTexWeight = _OceanFoamTextureInfluence
            * (1.0 - saturate((foamCamDist - OCEAN_FOAM_TEXTURE_FADE_START)
                              / OCEAN_FOAM_TEXTURE_FADE_RANGE));
        // Shared KWS contrast/dissolve law (FoamDissolve above); no erosion term.
        float oceanFoamDissolved = FoamDissolve(oceanFoamPattern.r, coverage, _OceanFoamFeather, 0.0);
        // The far end of this blend is the dissolve's EXPECTED value, NOT the raw coverage. Coverage
        // is the area the foam COULD occupy; the dissolve only keeps the part of it that clears the
        // pattern threshold, so blending toward coverage made distant foam brighter than the near
        // foam it is supposed to match - which is exactly what it looked like.
        oceanFoam = lerp(FoamDissolveExpected(coverage, _OceanFoamFeather, 0.0),
                         oceanFoamDissolved, oceanFoamTexWeight);
    }
    return oceanFoam;
}

// Whitewash COVERAGE only, the surf twin of OceanWhitecapCoverage and split out for the same
// reason: the underside needs the dissolved fraction and its pattern, never the lit look. Body is a
// verbatim move out of SurfWhitewashLayer.
float SurfWhitewashCoverage(v2f i, WaterGeomStage g, float2 foamWorldDdx, float2 foamWorldDdy,
                            out float3 surfPattern, out float2 surfSampleXZ)
{
    float3 incomingRay = g.incomingRay;
    SurfWaveSample surfFrag = g.surf;
    surfPattern = float3(1.0, 1.0, 1.0);
    surfSampleXZ = i.largeWaveSourceXZ;
    float surfFoam = 0.0;
    // Off surf bodies the front terms are inert, but the geometry foam can now be non-zero
    // there too (_LbwGeomFoamFloor - the ANALYTIC whitecap source for ocean-surface chunks,
    // rendered by OceanWhitecapLayer): keep it out of the whitewash on those bodies or a
    // chunk would draw the same foam through two pipelines at once.
    float surfGeomFoam = (_SurfActive > 0.5) ? g.surfGeomFoam : 0.0;
    // ---- Surf whitewash look: ANALYTIC coverage from the breaker-front layer (broken
    // bores + trailing churn) + GEOMETRY foam (the surface's own Jacobian/slope,
    // computed beside the normal above - white glued to whatever the rendered waves
    // actually do). Rendered through the OCEAN WHITECAP pipeline, not the pond
    // flipbook: whitewash IS seawater whitecap foam, so the surf shares the deep
    // caps' texture + KWS contrast law (one material language from open ocean to
    // the beach) - but through its own DEDICATED _SurfFoam* knobs, fully decoupled
    // from both the ripple-foam and the ocean-whitecap sliders. ----
    // FOAM-1: artist pop curve. The LUT maps the front's lifecycle clock (overCap,
    // 0..SURF_CREST_LUT_OVERCAP_MAX) to crest-foam intensity, times the timing-free
    // lip footprint - the curve alone decides WHEN crest foam pops and how it holds/
    // releases. Inactive = 0 added; the legacy breaker window still feeds the sim
    // injection + SSS, so nothing is lost. tex2Dlod: no derivatives, WGSL-uniform.
    float surfCrestFoam = 0.0;
    if (_SurfCrestFoamLutActive > 0.5 && surfFrag.lipShape > 0.0)
    {
        float crestLutU = saturate(surfFrag.overCap / SURF_CREST_LUT_OVERCAP_MAX);
        float crestCurve = tex2Dlod(_SurfCrestFoamLut,
                                    float4(crestLutU, 0.5, 0.0, 0.0)).r;
        surfCrestFoam = crestCurve * surfFrag.lipShape * _SurfCrestFoamGain;
    }
    // FOAM-4: crest cap. The whitewash coverage above is the bore + its SEAWARD trail (dAcross>0)
    // + the geometry foam - all of which load foam onto the wave's BACK/BASE, while the crisp lip
    // foam (surfFrag.breaker) is spent on the SSS glow + sim injection and never reaches the
    // surface coverage. So a broken front reads bald on TOP and heavy at the BASE ("foam lacks on
    // top, too much at base"). lipShape is the crest-anchored, surge-killed, plunge-widened
    // footprint; gating it by the cresting window keeps it OFF unbroken swell and ON from first
    // curl all the way through the bore (the window saturates past break), so the breaking crest
    // keeps a bright cap. Surface-only and independent of the FOAM-1 pop LUT - it fires even with
    // no authored curve. Gated by _SurfFoamCrestCap: 0 = byte-identical.
    float surfCrestCap = surfFrag.lipShape
                       * smoothstep(SURF_CRESTING_START, SURF_CRESTING_END, surfFrag.overCap)
                       * _SurfFoamCrestCap;
    float surfCoverage = saturate((surfFrag.whitewash + surfCrestFoam + surfCrestCap + surfGeomFoam)
                                  * _SurfFoamStrength);
    if (surfCoverage > FOAM_MASK_EPSILON)
    {
        // Same parallax lift as the ocean caps: foam reads as sitting ON the water.
        float3 surfViewToCam = -incomingRay;
        surfSampleXZ = i.largeWaveSourceXZ + surfViewToCam.xz
            * (OCEAN_FOAM_PARALLAX_HEIGHT / max(surfViewToCam.y, OCEAN_FOAM_PARALLAX_MIN_VIEW_Y));
        float surfDist = distance(i.largeWaveSourceXZ, _WorldSpaceCameraPos.xz);
        // Gradients hoisted with the whitecap's (foamWorldDdx/Ddy above): same base
        // world XZ, additive parallax - exact for this tap too (WGSL uniformity).
        surfPattern = SampleOceanWhitecapPatternTiled(surfSampleXZ, surfDist,
                                                      max(_SurfFoamTileSize, 1e-3),
                                                      foamWorldDdx, foamWorldDdy);
        // FOAM-2: aged deposit rots into HOLES, not a uniform fade - age raises the
        // pattern-dissolve threshold, so old foam breaks into lace patches, then
        // filaments, then nothing (real sea foam dies by holes opening). trailAge
        // is bore-gated, so the bore head (age ~0) stays solid. 0 seconds = off.
        float surfTrailErode = 0.0;
        if (_SurfFoamTrailDissolve > 0.0)
            surfTrailErode = saturate(surfFrag.trailAge / _SurfFoamTrailDissolve)
                           * SURF_TRAIL_ERODE_MAX;
        // Shared KWS contrast/dissolve law (FoamDissolve above) + the trail erosion.
        surfFoam = FoamDissolve(surfPattern.r, surfCoverage, _SurfFoamFeather,
                                surfTrailErode);
    }
    return surfFoam;
}

float RiverFoamCoverage(v2f i)
{
    if (_IsRiver < 0.5 || _RiverFoamActive < 0.5) return 0.0;
    float2 uv = saturate(float2(i.riverBakeUv.x,
                                i.riverBakeUv.y * _RiverFluidInvLength));
    float bakedCoverage = tex2Dlod(_FoamMask, float4(uv, 0.0, 0.0)).b;
    return saturate(bakedCoverage * _RiverFoamStrength * _FoamStrength);
}

float2 PondFoamPatternUv(v2f i, float3 normal, float2 localTilt)
{
    float riverWeight = saturate(_IsRiver);
    float2 riverVelocity = SampleRiverFluidVelocity(
        i.riverBakeUv, i.riverCurrentData.w);
    float2 riverMetres = i.riverCurrentData.xy - riverVelocity * _WaveTime;
    float2 patternMetres = lerp(i.worldPos.xz, riverMetres, riverWeight);
    float2 normalNudge = lerp(normal.xz, localTilt, riverWeight);
    return patternMetres / max(_FoamTileSize, 1e-3)
         + normalNudge * FOAM_NORMAL_NUDGE;
}

float2 PondFoamPatternFlow(float2 localTilt)
{
    // River pattern UV already carries physical speed*time advection. The classic two-phase flow
    // offset remains for simulation foam only, otherwise it would drift the baked foam twice.
    return localTilt * (1.0 - saturate(_IsRiver));
}

float3 ApplyPondFoamTiltToNormal(v2f i, float3 normal, float2 tilt)
{
    float3 gridFoamNormal = ApplyFoamTiltToNormal(normal, tilt);
    float3 riverRight = i.worldTangent.xyz
                      - normal * dot(i.worldTangent.xyz, normal);
    riverRight *= rsqrt(max(dot(riverRight, riverRight), RIVER_FRAME_MIN_LENGTH_SQ));
    float3 riverDownstream = normalize(cross(normal, riverRight) * i.worldTangent.w);
    float3 riverFoamNormal = normalize(normal
                                     + riverRight * tilt.x
                                     + riverDownstream * tilt.y);
    return normalize(lerp(gridFoamNormal, riverFoamNormal, saturate(_IsRiver)));
}

// The whole seen-from-below path; returns the final pixel colour.
float4 UnderwaterStage(v2f i, WaterGeomStage g, float waterClarity)
{
    // Original frag locals, re-bound: this side of the surface faces DOWN,
    // so the shading normal is the geometry normal flipped.
    float3 normal = -g.normal;
    float3 incomingRay = g.incomingRay;
    float2 nxz = g.nxz;
    float3 reflectedRay = reflect(incomingRay, normal);
    float3 refractedRay = refract(incomingRay, normal, IOR_WATER / IOR_AIR);
    // Total internal reflection (common at grazing angles from below, eta > 1)
    // returns a ZERO vector; tracing it divides by zero in IntersectCube and
    // poisons the pixel with NaN. Fall back to the reflected ray.
    if (dot(refractedRay, refractedRay) < 1e-6) refractedRay = reflectedRay;
    // Underside Fresnel. Physical (the default): the SNELL WINDOW - the same ~2% F0 as the
    // above-water side straight up (the ceiling overhead is nearly glass-clear), rising to a
    // true total-internal-reflection mirror past the ~48.6 deg critical angle. Legacy: the
    // original artistic curve, whose hard-coded 0.5 floor mirrored half the environment even
    // straight up and buried the transparency. The mode gate is a uniform (WGSL-safe branch).
    // saturate: float error can push the dot above 1, making the pow base negative -> NaN sparkle.
    float cosIncident = saturate(dot(normal, -incomingRay));
    float fresnel;
    if (_UnderFresnelPhysical > 0.5)
        fresnel = max(FresnelBelowWater(cosIncident, _UnderTirSoftness), _UnderFresnelFloor);
    else
        fresnel = lerp(FRESNEL_MIN_BELOW, 1.0, pow(1.0 - cosIncident, FRESNEL_POWER));

    // TIR reflection reflects the ENVIRONMENT, tinted underwater - never the pool
    // tiles. The reflected ray points back DOWN into the pool, so routing it through
    // GetSurfaceRayColor used to sample the analytic wall (a stale baked-in tile
    // reflection on the underside of the surface). _UnderMirrorWaterBlend then pulls
    // the mirror toward the body's own in-scatter colour: a real TIR mirror shows the
    // DEPTHS, not the sky, so blending toward the water colour reads truer; 0 keeps
    // the legacy tinted-sky mirror.
    float3 bodyInscatterUnder = WaterInscatterColor(-incomingRay, _LightDir, _SunColor, 0.0);
    float3 reflectedColor = lerp(SampleEnvironment(reflectedRay) * UnderwaterViewTint(),
                                 bodyInscatterUnder, _UnderMirrorWaterBlend);
    // VOLUMETRIC COUPLING (KWS increment, phase 1). A real TIR mirror shows THE DEPTHS, and the
    // depths are shaft-lit: without this term the mirror band sat dark against fog the god-ray
    // composite brightens all around it - the "decoupled" underside (Bert, 2026-07-31). Sampled
    // from LAST frame's post-blend shaft history at this pixel's own screen uv: a screen-space
    // stand-in for the reflected direction, and one frame late - both hidden by the ~8 frames
    // the 0.88-blend history already integrates. Added BEFORE the fresnel lerp below so it
    // rides the mirror weight: strongest at grazing, exactly where the TIR band lives, absent
    // in the clear Snell window overhead. Unconditional (their zero-coverage rule: adds black
    // at strength 0), and both the strength (publisher, needs an active god-ray ocean) and the
    // texture (pass binding, black without valid history) gate it to a no-op on legacy scenes.
    reflectedColor += UNITY_SAMPLE_TEX2D_SAMPLER(_LargeGodRayLastFrame, _CameraOpaqueTexture,
                                                 ScreenUV(i.screenPos)).rgb * _UnderMirrorShafts;
    float3 refractedColor = GetSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0)) * UnderwaterViewTint();

    // Real transparency from below: sample the live scene above the surface.
    if (_RealRefraction > 0.5)
    {
        float2 ruvU = ScreenUV(i.screenPos) + normal.xz * _RefractionDistortion;
        refractedColor = UNITY_SAMPLE_TEX2D(_CameraOpaqueTexture, saturate(ruvU)).rgb * UnderwaterViewTint();
    }

    refractedColor = ApplyWaterOpacityTintedClarity(refractedColor, bodyInscatterUnder, waterClarity); // turbidity from below too

    // The underside mirror strength is its OWN knob (it used to ride the above-water
    // _ReflectionStrength): 0 = fully refracted, a glass-clear ceiling.
    // length(refractedRay) is provably 1.0 by this point: refract() returns a unit vector or zero, and
    // the total-internal-reflection zero was already replaced by the unit reflectedRay above. The sqrt
    // was dead the moment that guard was added.
    float tUnder = 1.0 - fresnel;
    tUnder = lerp(1.0, tUnder, _UnderReflectionStrength); // strength 0 = fully refracted
    float3 underColor = lerp(reflectedColor, refractedColor, tUnder);

    // ---- Foam seen from below: the same coverage the top side draws, but instead of lit
    // white it reads as a SILHOUETTE - dense foam blocks the sky coming
    // through the surface, thin lace scatters a faint sun glow through.
    // No contact foam here: the depth texture holds the scene ABOVE the
    // surface from this side, so the contact heuristic is meaningless.
    // Every engine writes these two accumulators and the knob pair is applied ONCE below, so
    // two families overlapping cannot darken the same pixel twice. ----
    float undersideFoam = 0.0;                    // combined silhouette coverage
    float3 undersideGlow = float3(0.0, 0.0, 0.0); // sum of colour * pattern * that engine's coverage
    if (_FoamEnabled > 0.5)
    {
        // Windowed bodies read the foam buffer at the SOURCE xz (undisplaced), exactly like the
        // above-water side (see PondFoamLayer): sampling at the chop-displaced worldPos puts the
        // foam silhouette metres beside the crest carrying it, so the two sides of one surface
        // disagreed about where the foam is.
        float3 foamSourcePos = float3(i.largeWaveSourceXZ.x, i.worldPos.y, i.largeWaveSourceXZ.y);
        float2 fcoord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                             : (WorldToSim(foamSourcePos).xz * 0.5 + 0.5);
        // No contact foam on this side (see above), so nothing extra to add.
        // Same river guard as PondFoamCoverage: rivers either read the baked coverage or
        // show nothing - SimFoamCoverage on a river reads the packed fluid RG as foam.
        float mask = (_IsRiver > 0.5)
                   ? ((_RiverFoamActive > 0.5) ? RiverFoamCoverage(i) : 0.0)
                   : SimFoamCoverage(i.position.xz, fcoord, 0.0);

        // Same world-space pattern UV as the above-water side. Computed (with its
        // screen derivatives) BEFORE the mask branch: WGSL requires derivatives in
        // uniform control flow, and the branch below is per-fragment.
        float2 fuv = PondFoamPatternUv(i, normal, nxz);
        float2 fuvDdx = ddx(fuv);
        float2 fuvDdy = ddy(fuv);

        if (mask > FOAM_MASK_EPSILON)
        {
            float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
            float3 pattern; float core, lace, foamAlpha; float2 tilt;
            EvaluateFoam(fuv, fuvDdx, fuvDdy, PondFoamPatternFlow(nxz), mask,
                         foamDist, pattern, core, lace, foamAlpha, tilt);

            undersideFoam = max(undersideFoam, foamAlpha);
            undersideGlow += _FoamColor.rgb * pattern * (lace * mask);
        }
    }

#ifdef WATER_UNDERSIDE_FOAM
    // ---- Sea foam from below: ocean whitecaps + surf whitewash. These two engines had NO
    // underside representation at all - OceanWhitecapLayer and SurfWhitewashLayer are reached
    // only from the above-water path, which returns before this stage - so a diver under a
    // whitecapping sea saw a bare ceiling. They call the SAME coverage functions the top side
    // does, so the two faces of one surface agree on where the foam is by construction.
    //
    // NOT the swash, which is not an omission: swash foam only draws where the bed rises ABOVE
    // the still level (ShorelineStage's beachRise > 0 branch) - a film on wet sand with no water
    // column beneath it. There is nowhere to put an eye that could see its underside.
    //
    // A KEYWORD, not a uniform: a uniform branch still compiles these pattern taps into the pass,
    // and a fragment shader's register allocation is sized to its worst path - the same trap that
    // made the 40-step fog march cost every Simple-tier pixel. Armed by PublishUnderwater only
    // while the eye is below the surface, which is the only time this sheet is visible.
    {
        // Hoisted with no runtime condition around them (WGSL derivative uniformity), off the same
        // base world XZ as the top side's hoist in WaterSurface.shader. The coverage functions add
        // only an ADDITIVE parallax lift on top, so these gradients stay exact for their taps.
        float2 foamWorldDdx = ddx(i.largeWaveSourceXZ);
        float2 foamWorldDdy = ddy(i.largeWaveSourceXZ);

        // The parallax lift inside these two is left alone rather than zeroed for this side. It
        // exists to make foam read as sitting ON the water and points the wrong way from below,
        // but it is bounded by OCEAN_FOAM_PARALLAX_HEIGHT / OCEAN_FOAM_PARALLAX_MIN_VIEW_Y =
        // 0.16 m of XZ - about 2% of a default foam tile, i.e. below the noise floor of the
        // pattern itself. Threading a per-side height through both signatures would buy nothing.
        // The sample points come back only because the top side needs them to glue its relief tap
        // to the pattern tap; this side draws no relief, so they are written and dropped.
        float3 oceanPattern; float2 unusedOceanSampleXZ;
        float oceanFoam = OceanWhitecapCoverage(i, g, foamWorldDdx, foamWorldDdy,
                                                oceanPattern, unusedOceanSampleXZ);
        float3 surfPattern; float2 unusedSurfSampleXZ;
        float surfFoam = SurfWhitewashCoverage(i, g, foamWorldDdx, foamWorldDdy,
                                               surfPattern, unusedSurfSampleXZ);

        // max, not sum: these all occlude the SAME sky, so the densest layer owns the pixel -
        // the same rule the above-water composite applies with its foamMatte.
        undersideFoam = max(undersideFoam, max(oceanFoam, surfFoam));
        // ONE coverage factor each, where the pond engine above contributes two (lace * mask):
        // that engine keeps its thin-lace and raw-mask terms apart, while these two carry a single
        // dissolved fraction. Squaring it would make sea foam glow dimmer than pond foam at equal
        // coverage for no physical reason.
        undersideGlow += _OceanFoamColor.rgb * oceanPattern * oceanFoam
                       + _SurfFoamColor.rgb * surfPattern * surfFoam;
    }
#endif

    // Applied BEFORE the downwelling dim below, so the silhouette and its glow fade with eye
    // depth like the rest of the scene. Unconditional: at zero coverage the darken is a multiply
    // by one and the glow adds black, so a guard here could only cost the faint lace glow the
    // per-engine form used to draw.
    float sunThrough = saturate(_LightDir.y);
    underColor *= 1.0 - _FoamUndersideDarken * undersideFoam;
    underColor += undersideGlow * (_FoamUndersideGlow * sunThrough);

    // Dim the underwater view by the CAMERA's depth: the deeper the eye, the less
    // downwelling light reaches it, so the whole submerged scene reads darker.
    // Measured against the analytic surface (rest + waves) directly above the eye,
    // not the flat centre plane, so depth stays consistent with the rest of the
    // shading when the surface is wind-driven.
    // ONLY when the fullscreen fog pass will not paint (_UnderwaterFogArmed = 0):
    // that pass applies the SAME camera-depth downwelling to these pixels (the
    // deepest wet point of an up-look ray IS the eye), so applying it here too
    // double-darkened the underside sheet whenever fog + depth darkening were on.
    // The gate is a uniform (WGSL-safe branch).
    if (_UnderwaterFogArmed < 0.5)
    {
        float3 camPool = WorldToPool(_WorldSpaceCameraPos);
        float camSurfaceY = PoolToWorld(float3(camPool.x,
            WaveHeight(WindWaveSampleXZ(camPool.xz, _WorldSpaceCameraPos.xz)), camPool.z)).y;
        underColor *= DownwellingAttenuation(_WorldSpaceCameraPos.y, camSurfaceY);
    }
    return float4(underColor, 1.0);
}

// Fresnel + the reflection ladder: blurred/stretched sky -> planar RT -> SSR.
float3 ReflectionStage(v2f i, WaterGeomStage g, out float fresnel)
{
    // Original frag locals, re-bound (the moved body below is verbatim).
    float3 normal = g.normal;
    float3 incomingRay = g.incomingRay;
    float surfaceRoughness = g.roughness;
    float3 reflectedRay = reflect(incomingRay, normal);
    // Schlick Fresnel from the air/water IOR: ~2% mirror straight down (deep
    // clear water at your feet), full mirror at grazing (the horizon). The
    // exponent is the OVERALL SHININESS dial: 5 is
    // physical; lower lifts reflectivity on tilted wave faces so the whole
    // surface reads glossier while keeping the down/grazing contrast.
    // saturate: float error can push the dot above 1 -> negative pow base -> NaN.
    float fresnelGrazing = pow(saturate(1.0 - dot(normal, -incomingRay)), _FresnelPower);
    fresnel = max(FRESNEL_F0_WATER + (1.0 - FRESNEL_F0_WATER) * fresnelGrazing,
                        _FresnelFloor);

    // Reflection samples the environment (sky / URP probe) for ANY reflected direction.
    // GetSurfaceRayColor would route a below-horizon ray - common at grazing angles and on
    // wave slopes, exactly where Fresnel makes the reflection strongest - into the pool
    // floor and return the TILES, which showed up as tile "highlights" and hid the probe.
    // The underwater branch already samples the environment directly; match it here.
    // SKY only: the sun is added as the GGX lobe after the composite, so the legacy
    // glint must not ride along inside the mirror term (it would double the sun).
    // Sampled at the SHARED roughness mip: the mirror blurs with the same roughness
    // that widens the sun lobe - near-sharp at your feet, hazier toward the horizon.
    // The horizon clamp applies to a COPY of the ray: SSR below must march the true
    // reflection (below-horizon rays legitimately hit scene geometry there), only
    // the sky lookup needs the lift.
    float3 skyRay = reflectedRay;
    skyRay.y = max(skyRay.y, REFLECTION_MIN_UP_Y);
    skyRay = normalize(skyRay);
    float3 reflectedColor;

    // ---- Reflection: analytic -> planar -> SSR (SSR wins where it hits). The toggles
    // are uniform-driven (published per body via the property block), so they are live. ----
    // The mirror covers exactly the screen and is MIRROR-wrapped (PlanarMirror.cs), so every
    // sample has real data and the sky needs no blending back in. Blending it at the border was
    // tried and drew a visible seam around the frame instead. And BECAUSE planar replaces the
    // sky mirror outright, the two are a real if/else: sampling the sky first and overwriting
    // it paid the aniso cube taps on every planar pixel for a value that was always discarded.
    // Both samplers are explicit-LOD, so the branch is derivative-safe.
    if (_UsePlanar > 0.5)
        reflectedColor = SamplePlanarReflection(i.screenPos, normal, surfaceRoughness);
    else
        reflectedColor = SampleSkyEnvironmentAniso(skyRay, surfaceRoughness);
    if (_UseSSR > 0.5)
    {
        float ssrHit;
        float3 ssr = MarchSSR(i.worldPos, reflectedRay, surfaceRoughness, ssrHit); // SSR marches in world space
        reflectedColor = lerp(reflectedColor, ssr, ssrHit * _SSRStrength);
    }
    return reflectedColor;
}

// Crest subsurface glow weight (FFT pinch + surf breaker lips), added
// emissively in the composite stage.
float EvaluateCrestGlow(v2f i, WaterGeomStage g)
{
    float3 incomingRay = g.incomingRay;
    ShoreData shoreFrag = g.shore;
    SurfWaveSample surfFrag = g.surf;
    // ---- Wave-crest subsurface glow: steep crests scatter sunlight toward the viewer,
    // brightest looking INTO the sun. Crest steepness is the TRUE displacement-Jacobian fold
    // exported by the FFT compute (saturate(1 - J), the same fold that seeds whitecaps), so
    // the glow tracks the actual breaking crests. Remapped through [min,max] and raised to a
    // power so it concentrates on the sharp folds. Added emissively after compositing (see
    // below) so it reads regardless of what is behind the crest. Ocean-FFT only + gated. ----
    float sssBoost = 0.0;
    // Edge guard: the feathered border renders flattened crests, so their glow must flatten
    // with them (this raw fold read bypasses the weighted wave-field composition points).
    float lbwEdge = LbwEdgeWeight(i.largeWaveSourceXZ);
    if (_SssEnabled > 0.5 && _OceanFftActive > 0.5)
    {
        // Shore-attenuated fold: no crest glow from waves the depth field has
        // flattened (shoreFrag is inert off surf bodies - deep ocean unchanged).
        float fold = OceanFftJacobianShore(i.largeWaveSourceXZ, shoreFrag) * lbwEdge;
        float ramp = saturate((fold - _SssPinchMin)
                              / max(_SssPinchMax - _SssPinchMin, SSS_AMPLITUDE_EPSILON));
        float pinch = pow(ramp, _SssPinchFalloff);
        float sunFacing = pow(saturate(dot(-incomingRay, _LightDir)), _SssSunFalloff);
        sssBoost = pinch * sunFacing * _SssIntensity;
    }

    // ---- Surf breaker crest glow: cresting lips scatter sunlight exactly like
    // FFT-pinched crests, so reuse the subsurface glow path (same gate/knobs). The
    // shore/front sample itself is hoisted next to the normal above. ----
    if (_SssEnabled > 0.5 && surfFrag.breaker > 0.0)
    {
        float surfSun = pow(saturate(dot(-incomingRay, _LightDir)), _SssSunFalloff);
        sssBoost += surfFrag.breaker * surfSun * _SssIntensity * lbwEdge;
    }
    return sssBoost;
}

// Chunk bodies (WaterVolume.Chunk.cs): the water column under the disc ends at the chunk
// PRIMITIVE, not at the scene behind the pixel - without this cap a floating chunk fogged its
// view against the ground metres below the sphere. Published per body by SetChunkSurfaceProps
// (always written, 0 on ordinary bodies, so the clamp is inert everywhere else).
#include "WaterChunkPrimitive.hlsl" // ChunkIntersect (WaterShared already included, guard no-ops)
float _ChunkFogClamp; // 1 = cap the refracted fog span at the chunk primitive's exit
float _ChunkShape;    // CHUNK_SHAPE_* selector (box / sphere)

// World-metre span from a POOL-space surface point to the chunk primitive's exit along the
// refracted world ray (the pool t of a NORMALISED world ray IS world metres - affine frame).
float ChunkRefractionSpan(float3 poolPos, float3 refractedRayWS)
{
    float3 poolDir = WorldDirToPool(refractedRayWS);
    return max(ChunkIntersect(_ChunkShape, poolPos, poolDir).y, 0.0);
}

// Guard for the closed-form mean-depth denominators below (mirrors the fullscreen fog's
// DOWNWELL_MEAN_SIGMA_MIN): below it the sigma*L -> 0 limit (L/2) is taken explicitly.
#define REFRACTED_DOWNWELL_SIGMA_MIN 1e-4
// Floor on the ray/forward cosine when converting an eye-depth difference into a slant
// distance (below): keeps a grazing ray from blowing the span - and the fog on it - up.
#define REFRACTION_SPAN_COS_MIN 0.2

// Downwelling depth-darkening of the transmitted (refracted) column - the term the fullscreen
// underwater fog applies to every submerged pixel and the sheet's from-above view was MISSING.
// With the fullscreen pass masked off on air-side pond pixels (the pond-ghost fix: the sheet
// owns the from-air column, ocean-style), the sheet must price the whole look itself, or ponds
// read washed-out and flat the moment that mask lands. Same math as the fog pass: the light
// this span delivers in-scatters about one mean free path in, so the darkening is evaluated at
// the transmittance-weighted MEAN depth of the span (closed form), never the abyssal endpoint -
// the per-channel colour stays inside DownwellingAttenuation. Identity when the depth-darken
// feature is off (DownwellingAttenuation returns 1), so bodies not using it are byte-identical.
float3 RefractedColumnDownwelling(float3 sheetWorldPos, float3 refractedRayWS, float spanLen,
                                  float clarity)
{
    float density = _WaterFogDensity * lerp(CLARITY_FOG_DENSITY_MAX, 1.0, saturate(clarity));
    float sigma = dot(_WaterExtinction.rgb, float3(1.0/3.0, 1.0/3.0, 1.0/3.0)) * density;
    float sigmaL = sigma * spanLen;
    // Denominators clamped BEFORE the select: an HLSL ternary evaluates both lanes, so a zero
    // sigma (fog density slid to 0) must not divide by zero in the dead lane.
    float spanExp = exp(-sigmaL);
    float meanT = (sigmaL > REFRACTED_DOWNWELL_SIGMA_MIN)
        ? (1.0 / max(sigma, REFRACTED_DOWNWELL_SIGMA_MIN * 1e-3)
           - spanLen * spanExp / max(1.0 - spanExp, REFRACTED_DOWNWELL_SIGMA_MIN * 1e-3))
        : (0.5 * spanLen);
    // Safety clamp mirroring the fog's deepestY rule: the mean sits on the span, so for the
    // down-going transmitted ray this max() is a no-op; it only guards degenerate spans.
    float downwellY = max(sheetWorldPos.y + refractedRayWS.y * meanT,
                          sheetWorldPos.y + refractedRayWS.y * spanLen);
    // The sheet fragment IS the surface above this column, so it is its own depth reference.
    return DownwellingAttenuation(downwellY, sheetWorldPos.y);
}

// Refraction: analytic pool trace or real screen-space refraction, fogged by
// the traversed water and pulled toward the body in-scatter by the clarity curve.
// bodyInscatter is handed OUT rather than left local: ShorelineStage needs the same value for the
// deep-water tint, and recomputing it there would let the two stages' phase terms drift apart - a
// drifting in-scatter prints a seam exactly at the depth boundary where the two meet.
float3 RefractionStage(v2f i, WaterGeomStage g, float waterClarity, out float3 bodyInscatterOut)
{
    float3 normal = g.normal;
    float3 incomingRay = g.incomingRay;
    float3 refractedRay = refract(incomingRay, normal, IOR_AIR / IOR_WATER);
    // Art-directed bend on the ANALYTIC path: 0 = a flat window (look straight through), 1 = the
    // physical Snell ray. Lerping toward the incoming ray is safe by construction - air->water never
    // gives total internal reflection, so refract() always returns a unit vector, and the two are at
    // most the ~48.6 deg critical angle apart, so their lerp can never reach zero and normalize()
    // can never hand the pool trace below a degenerate direction. Default 1 = physically unchanged.
    refractedRay = normalize(lerp(incomingRay, refractedRay, _RefractionStrength));
    // The water's lit body colour (picked scatter colour + sun/ambient), or the flat fog
    // colour when scattering is off. Used as the in-scatter target for EVERY path below (deep
    // water, scene refraction, pool, turbidity) so the scatter actually shows. The crest glow
    // is NOT folded in here - as a volume target it only shows where the water behind the
    // crest is deep (sky/far behind), so it is added emissively after compositing instead.
    float3 bodyInscatter = WaterInscatterColor(-incomingRay, _LightDir, _SunColor, 0.0);
    bodyInscatterOut = bodyInscatter;

    // No constant tint: for open/deep water GetSurfaceRayColor -> DeepWaterColor already lights the
    // physical body colour via WaterInscatterColor, and the absorption below pulls the rest toward it,
    // so a neutral tint hands the colour to the physical model instead of the old hardcoded cyan.
    float3 refractedColor = GetSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0));

    // ---- Real transparency: sample the actual scene behind the surface, instead of
    // the analytic pool; else fog the ANALYTIC pool by the refracted chord. Only one
    // path runs, so the real-refraction view is never double-fogged. ----
    if (_RealRefraction > 0.5)
    {
        float2 ruv = ScreenUV(i.screenPos);
        ruv += normal.xz * _RefractionDistortion;
        // Screen-space leak guard: the distorted UV can land on the pixels of an ABOVE-water
        // object (a boat hull beside this water pixel), painting a refracted ghost of it around
        // the waterline. Anything nearer the camera than this surface fragment cannot be seen
        // THROUGH the surface, so reject the offset and fall back to the undistorted UV - the
        // scene truly behind this pixel (the opaque copy there cannot hold an above-water
        // object, or it would have occluded this water fragment). The offset's depth was
        // already fetched for the fog span below, so clean pixels pay nothing new (a reorder)
        // and rejected pixels pay one extra depth fetch - no ray march needed. This also fixes
        // the span itself: it used to measure against the GHOST's depth, so the leaked hull
        // rendered un-fogged on top of being wrong. All reads explicit-LOD (branch-safe).
        float surfEyeR  = EyeDepthOf(i.worldPos);
        float sceneEyeR = LinearEyeDepth(RawSceneDepth(saturate(ruv)));
        if (sceneEyeR < surfEyeR)
        {
            ruv = ScreenUV(i.screenPos);
            sceneEyeR = LinearEyeDepth(RawSceneDepth(saturate(ruv)));
        }
        refractedColor = UNITY_SAMPLE_TEX2D(_CameraOpaqueTexture, saturate(ruv)).rgb; // tinted by the water absorption below

        // Fog the transmitted view by the water thickness behind the surface
        // (scene eye-depth - surface eye-depth), so heavy fog reads through too.
        // Chunk bodies cap the span at the primitive exit (the scene behind is DRY space).
        float waterSpan = max(0.0, sceneEyeR - surfEyeR);
        // The eye-depth difference above is measured along the camera FORWARD axis, not along
        // this pixel's ray, so an oblique look under-reports the traversed water by the
        // ray/forward cosine - a large share of "ponds read clearer from above than the same
        // water reads from underwater" (the fullscreen fog integrates true world chords).
        // Divide by that cosine to recover the slant distance. SMALL BODIES ONLY: the ocean's
        // from-above look was tuned on the raw difference and stays byte-identical.
        if (_LargeBody < 0.5)
        {
            float3 cameraForward = -UNITY_MATRIX_V[2].xyz;
            waterSpan /= max(dot(incomingRay, cameraForward), REFRACTION_SPAN_COS_MIN);
        }
        if (_ChunkFogClamp > 0.5)
            waterSpan = min(waterSpan, ChunkRefractionSpan(i.position, refractedRay));
        refractedColor = ApplyWaterVolumeClarity(refractedColor, waterSpan, bodyInscatter, waterClarity);
        // Depth darkening on the transmitted view (fullscreen-fog parity - see the helper
        // above). SMALL BODIES ONLY: the ocean sheet was tuned without this term and its
        // from-air column was never double-painted by the fullscreen pass, so large bodies
        // stay byte-identical. Applied BEFORE the scene-light glow below, mirroring the fog
        // pass: local lights never crossed the surface, so the sun's depth darkening does
        // not apply to them.
        if (_LargeBody < 0.5)
            refractedColor *= RefractedColumnDownwelling(i.worldPos, refractedRay, waterSpan,
                                                         waterClarity);
#ifdef WATER_FOG_POINT_LIGHTS
        // Scene-light glow in the transmitted column: the SAME published list and closed-form
        // integral the fullscreen fog uses below the waterline (WaterSceneLightsInscatter), so
        // a lamp's glow seen through the sheet from above is the glow seen swimming past it -
        // this term is what makes the lights visible from OUT of the water at all (the fog pass
        // deliberately never paints from-above pixels; the sheet owns that column). Span = the
        // sheet point to the scene behind it, along the view ray - the same straight-ray
        // approximation the thickness fog above already accepts. Water begins AT the sheet, so
        // extinction starts there, never in the air between lens and sheet.
        {
            float3 camToSheet = i.worldPos - _WorldSpaceCameraPos;
            float tSheet = max(length(camToSheet), 1e-4);
            float3 viewDirDown = camToSheet / tSheet;
            refractedColor += WaterSceneLightsInscatter(_WorldSpaceCameraPos, viewDirDown,
                                                        tSheet, tSheet + waterSpan, tSheet,
                                                        _VolumeCenter.y)
                            * _UnderwaterLightScatter;
        }
#endif
    }
    else if (_LargeBody < 0.5)
    {
        // Analytic pool fog: WORLD length of the refracted segment through the pool,
        // by intersecting the unit box in pool space then measuring the world chord
        // (correct under non-uniform extent / rotation). Open water has no pool box
        // and its refracted colour is already the deep-water colour, so it is skipped.
        float3 pdFog = WorldDirToPool(refractedRay);
        float2 tfog = IntersectCube(i.position, pdFog, POOL_BOX_MIN, POOL_BOX_MAX);
        float exitTFog = max(0.0, tfog.y);
        // Chunk bodies: the primitive (inscribed in the pool box) is the real water end.
        if (_ChunkFogClamp > 0.5)
            exitTFog = min(exitTFog, max(ChunkIntersect(_ChunkShape, i.position, pdFog).y, 0.0));
        float3 exitWorld = PoolToWorld(i.position + pdFog * exitTFog);
        float poolChord = length(exitWorld - i.worldPos);
        refractedColor = ApplyWaterVolumeClarity(refractedColor, poolChord, bodyInscatter, waterClarity);
        // Same depth darkening for the analytic-pool transmitted view (this branch is already
        // small-bodies-only); before the light glow for the same reason as the real path.
        refractedColor *= RefractedColumnDownwelling(i.worldPos, refractedRay, poolChord,
                                                     waterClarity);
#ifdef WATER_FOG_POINT_LIGHTS
        // Same scene-light glow for the analytic-pool transmitted view (a lamp in a night pool
        // seen from the deck) - the chord through the box is this branch's water span.
        {
            float3 camToSheet = i.worldPos - _WorldSpaceCameraPos;
            float tSheet = max(length(camToSheet), 1e-4);
            float3 viewDirDown = camToSheet / tSheet;
            refractedColor += WaterSceneLightsInscatter(_WorldSpaceCameraPos, viewDirDown,
                                                        tSheet, tSheet + poolChord, tSheet,
                                                        _VolumeCenter.y)
                            * _UnderwaterLightScatter;
        }
#endif
    }

    refractedColor = ApplyWaterOpacityTintedClarity(refractedColor, bodyInscatter, waterClarity); // turbidity toward the body colour
    return refractedColor;
}


// Ocean FFT whitecap: cascade coverage broken into lace by the tiling pattern.
// oceanCoverage returns the raw TEXTURED coverage - the specular matte reads it,
// not the layer alpha (which folds in _OceanFoamColor.a).
FoamLayer OceanWhitecapLayer(v2f i, WaterGeomStage g, float2 foamWorldDdx,
                             float2 foamWorldDdy, out float oceanCoverage)
{
    float3 normal = g.normal;
    float3 oceanFoamPattern;
    float2 oceanFoamSampleXZ;
    float oceanFoam = OceanWhitecapCoverage(i, g, foamWorldDdx, foamWorldDdy,
                                            oceanFoamPattern, oceanFoamSampleXZ);

    float oceanFoamAlpha = 0.0;
    float3 oceanFoamLook = float3(0.0, 0.0, 0.0);

    // ---- Ocean whitecap look: lit with the same wrapped-sun + ambient model as the pond
    // foam so crests shade with the waves instead of reading as flat paint. Reached only
    // when a coverage source above produced foam (FFT ocean, or the analytic floor), so
    // pools stay unchanged. ----
    if (oceanFoam > FOAM_MASK_EPSILON)
    {
        // ---- Foam relief: emboss the lighting normal by the foam normal map (same flipbook,
        // frame-synced to the pattern) so the lace shades three-dimensionally and its specular
        // breakup matches the texture. Built as a LOCAL normal - the base wave normal that the
        // pond foam / haze below rely on is left untouched. Default "bump" map = zero tilt.
        // Tilt is sampled at the SAME parallax-lifted point as the pattern so they stay glued. ----
        float2 oceanFoamTilt = SampleOceanWhitecapTilt(oceanFoamSampleXZ,
                                                       foamWorldDdx, foamWorldDdy)
                             * (_FoamNormalStrength * oceanFoam);
        float3 oceanFoamNormal = ApplyFoamTiltToNormal(normal, oceanFoamTilt);

        // Modulate the tint by the pattern so the foam carries internal light/dark detail
        // instead of reading as a flat wash; whiten toward the peaks so dense foam stays bright.
        float oceanWrap = FoamWrappedDiffuse(oceanFoamNormal, _LightDir);
        float3 oceanTint = _OceanFoamColor.rgb * lerp(oceanFoamPattern, float3(1.0, 1.0, 1.0), oceanFoam);
        // Ceto's foam absorption (Assets/Ceto/Shaders/OceanUnderWater.cginc:44 -
        // Ceto_FoamTint * amount * exp(-Ceto_AbsCof.rgb * (1 - amount))). Thin foam is mostly WATER
        // with bubbles in it, so it should take the water's own colour and only go white once it is
        // dense; painting it flat white at every density is what makes foam read as decals stuck on
        // the surface. Driven by _WaterExtinction - the SAME coefficient the fog and the depth
        // transmittance run on - so foam and sea keep agreeing when the water type is retuned; a
        // separately-picked foam colour drifts away from the water it is floating on. The amount
        // factor Ceto folds in here is already carried by oceanFoamAlpha below, so only the
        // absorption term is applied. 0 = the flat tint, byte-identical.
        oceanTint *= lerp(float3(1.0, 1.0, 1.0),
                          exp(-_WaterExtinction.rgb * (1.0 - saturate(oceanFoam))),
                          _OceanFoamDepthTint);
        oceanFoamLook = FoamLitColor(oceanTint, _SunColor, oceanWrap);
        oceanFoamAlpha = oceanFoam * _OceanFoamColor.a;
    }
    oceanCoverage = oceanFoam;
    FoamLayer layer;
    layer.alpha = oceanFoamAlpha;
    layer.look = oceanFoamLook;
    return layer;
}

// Interactive/pond foam: advected sim buffer + wall border + contact foam.
// Pond-foam COVERAGE, split out of PondFoamLayer so it can be evaluated WITHOUT the geometry
// stage. It reads only v2f interpolants, the advected foam buffer and the depth texture - no
// WaterGeomStage field - which is what lets the overlay pass reject a fragment before paying for
// EvaluateSurfaceGeometry. Factored rather than copied: WaterFoamMask.hlsl's header records that
// per-consumer copies of the coverage formula drifted once already.
// Every tap here is explicit-LOD, so it is legal in any control flow.
float PondFoamCoverage(v2f i)
{
    // Rivers never consume the rectangular sim field (WaterSurfaceFoamSampling.hlsl), and a
    // river with the packed fluid bake bound has REBOUND _FoamMask (RG = encoded velocity,
    // B = foam): falling through to SimFoamCoverage read that RG as foam + wet mark -
    // encoded rest velocity is 0.5, so the whole river grew a half-strength foam haze.
    if (_IsRiver > 0.5) return (_RiverFoamActive > 0.5) ? RiverFoamCoverage(i) : 0.0;

    // Windowed bodies read the foam buffer in the window frame too - at the
    // SOURCE xz (undisplaced), like the whitecap path. Sampling at the displaced
    // worldPos misses foam under horizontally-displaced geometry: the hero wave's
    // crest is thrown metres forward by lean + curl, so its fragments were reading
    // the buffer ahead of where the lip foam was injected (empty crest head). FFT
    // chop caused the same error at a smaller, invisible scale.
    float3 foamSourcePos = float3(i.largeWaveSourceXZ.x, i.worldPos.y, i.largeWaveSourceXZ.y);
    float2 fcoord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                         : (WorldToSim(foamSourcePos).xz * 0.5 + 0.5);
    // The advected buffer read and the shoreline wall border are SimFoamCoverage's job
    // (below); only the contact term is specific to this side.
    //
    // contact foam where geometry pierces the waterline. BOUNDED bodies only (the same
    // _SimWindowed gate SimFoamCoverage applies to its wall border): on a windowed
    // ocean/large body the screen-depth
    // contact test is unreliable (it fought the shore/SWE work) and there are no walls,
    // so it is skipped entirely. Needs the depth texture; the behind-guard only adds
    // foam where the scene is genuinely just BEHIND the surface (fixes "all water
    // foamed" builds).
    float contact = 0.0;
    if (_SimWindowed < 0.5)
    {
        float2 suv = ScreenUV(i.screenPos);
        float sceneEye = LinearEyeDepth(RawSceneDepth(suv));
        float surfEye  = EyeDepthOf(i.worldPos);
        float behind   = sceneEye - surfEye; // > 0 when scene sits below the surface
        contact = behind > 0.0 ? (1.0 - saturate(behind / max(_FoamContactDepth, 1e-4))) : 0.0;
    }

    return SimFoamCoverage(i.position.xz, fcoord, contact);
}

FoamLayer PondFoamLayer(v2f i, WaterGeomStage g)
{
    float3 normal = g.normal;
    float2 nxz = g.nxz;
    float pondFoamAlpha = 0.0;
    float3 pondFoamLook = float3(0.0, 0.0, 0.0);

    // ---- Interactive/pond foam look: advected buffer + shoreline border + contact ----
    //
    // Fog-armed frames with the camera in AIR: skip - the fullscreen underwater fog
    // (BeforeRenderingPostProcessing, after every transparent) paints the water column's
    // fog OVER this pass's output, which washed fading foam toward the fog colour; and
    // cancelling the fog by mask coverage punched clear holes through dense fog instead
    // (the mask is low-frequency, the drawn foam is mask x pattern texture). The foam is
    // re-drawn AFTER the fog by WaterSurface's PondFoamOverlay pass, which defines
    // WATER_FOAM_OVERLAY_PASS and calls THIS function - one look, two draw points, so
    // the two can never drift; the skip and the overlay key on the SAME published
    // globals, so exactly one of them shows the foam each frame.
    // Exceptions that keep the queue-time draw: a submerged camera (the fog is IN FRONT
    // of the foam there) and chunk bodies (their disc footprint clips are Pass-0 state
    // the overlay pass does not replicate; the C# collector excludes them the same way).
    // Every gate term is a uniform, so control flow stays WGSL-uniform.
#ifdef WATER_FOAM_OVERLAY_PASS
    const bool foamDeferredToOverlay = false; // this IS the overlay pass: always evaluate
#else
    bool foamDeferredToOverlay = _UnderwaterFogArmed > 0.5 && _CameraUnderwater < 0.5
                                 && _ChunkSphereClip < 0.5 && _ChunkBoxClip < 0.5
                                 && _ChunkUseMesh < 0.5;
#endif
    if (_FoamEnabled > 0.5 && !foamDeferredToOverlay)
    {
        // Windowed bodies read the foam buffer in the window frame too - at the
        // SOURCE xz (undisplaced), like the whitecap path. Sampling at the displaced
        // worldPos misses foam under horizontally-displaced geometry: the hero wave's
        // crest is thrown metres forward by lean + curl, so its fragments were reading
        // the buffer ahead of where the lip foam was injected (empty crest head). FFT
        // chop caused the same error at a smaller, invisible scale.
        float mask = PondFoamCoverage(i);
        // THE SURF BAND BELONGS TO THE WHITEWASH PIPELINE. The ocean whitecaps have stood down here
        // since 2026-07-28; the ripple/turbulence foam never did, so the shore band drew BOTH - the
        // sim buffer's low-frequency, decayed, advected copy through the pond pattern AND the
        // analytic whitewash through the whitecap pattern, max()ed together. Same weight, same
        // contour, one more consumer.
        //
        // Applied HERE and not in PondFoamCoverage: the overlay pass early-clips on that function
        // BEFORE it builds the geometry stage, and that hoist is only legal because coverage takes
        // no WaterGeomStage. Both draw points call THIS function, so both are covered anyway, and
        // the early clip stays a conservative superset (this can only lower the mask).
        mask *= 1.0 - LbwFoamOwnershipGate(g.shore);

        // WORLD-space pattern UV (like the ocean whitecap): scale set by the
        // body's Foam Pattern Size, independent of extent, anchored under a
        // scrolling window; nudged by the surface tilt so foam rides ripples.
        // Computed (with its screen derivatives) BEFORE the mask branch: WGSL
        // requires derivatives in uniform control flow, and the branch below
        // is per-fragment.
        float2 fuv = PondFoamPatternUv(i, normal, nxz);
        float2 fuvDdx = ddx(fuv);
        float2 fuvDdy = ddy(fuv);

        if (mask > FOAM_MASK_EPSILON)
        {
            float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
            float3 pattern; float core, lace, foamAlpha; float2 tilt;
            EvaluateFoam(fuv, fuvDdx, fuvDdy, PondFoamPatternFlow(nxz), mask,
                         foamDist, pattern, core, lace, foamAlpha, tilt);

            // ---- Foam relief: tilt the lighting normal by the foam's own
            // normal map so the lace shades three-dimensionally. ----
            float3 foamNormal = ApplyPondFoamTiltToNormal(i, normal, tilt);

            // ---- Lit foam: wrapped diffuse from the sun over an ambient
            // floor, so foam shades with the waves instead of flat white. ----
            float wrapped = FoamWrappedDiffuse(foamNormal, _LightDir);
            float3 albedo = _FoamColor.rgb * lerp(pattern, float3(1.0, 1.0, 1.0), core * FOAM_CORE_WHITEN);
            pondFoamLook = FoamLitColor(albedo, _SunColor, wrapped);
            pondFoamAlpha = foamAlpha;
        }
    }
    FoamLayer layer;
    layer.alpha = pondFoamAlpha;
    layer.look = pondFoamLook;
    return layer;
}


// Surf whitewash: analytic breaker-front coverage + geometry foam, rendered
// through the ocean-whitecap pipeline with its dedicated _SurfFoam* knobs.
FoamLayer SurfWhitewashLayer(v2f i, WaterGeomStage g, float2 foamWorldDdx,
                             float2 foamWorldDdy)
{
    float3 normal = g.normal;
    float3 surfPattern;
    float2 surfSampleXZ;
    float surfFoam = SurfWhitewashCoverage(i, g, foamWorldDdx, foamWorldDdy,
                                           surfPattern, surfSampleXZ);

    float surfFoamAlpha = 0.0;
    float3 surfFoamLook = float3(0.0, 0.0, 0.0);
    if (surfFoam > FOAM_MASK_EPSILON)
    {
        float2 surfTiltXY = SampleOceanWhitecapTiltTiled(surfSampleXZ,
                                                         max(_SurfFoamTileSize, 1e-3),
                                                         foamWorldDdx, foamWorldDdy)
                          * (_FoamNormalStrength * surfFoam);
        float3 surfFoamNormal = ApplyFoamTiltToNormal(normal, surfTiltXY);
        float surfWrapped = FoamWrappedDiffuse(surfFoamNormal, _LightDir);
        float3 surfTint = _SurfFoamColor.rgb
            * lerp(surfPattern, float3(1.0, 1.0, 1.0), surfFoam);
        surfFoamLook = FoamLitColor(surfTint, _SunColor, surfWrapped);
        surfFoamAlpha = surfFoam * _SurfFoamColor.a;
    }
    FoamLayer layer;
    layer.alpha = surfFoamAlpha;
    layer.look = surfFoamLook;
    return layer;
}

// ---- Foam layers, evaluated BEFORE the reflection composite so the combined foam
// can matte the specular (foam breaks the mirror sheet - previously only the ocean
// layer did; pond/wake foam stayed glossy, which read as painted-on). Evaluated
// separately (different sources + art direction), composited exclusively after the
// shoreline gradient below. ----
void FoamLayersStage(v2f i, WaterGeomStage g, float2 foamWorldDdx, float2 foamWorldDdy,
                     out FoamLayer oceanFoamLayer, out FoamLayer pondFoamLayer,
                     out FoamLayer surfFoamLayer, out float oceanCoverage)
{
    oceanFoamLayer = OceanWhitecapLayer(i, g, foamWorldDdx, foamWorldDdy, oceanCoverage);
    pondFoamLayer = PondFoamLayer(i, g);
    surfFoamLayer = SurfWhitewashLayer(i, g, foamWorldDdx, foamWorldDdy);
}

// Base composite: refraction vs reflection by fresnel (matted by foam), + the
// GGX sun lobe, + the emissive crest glow.
float3 CompositeSurfaceColor(WaterGeomStage g, float fresnel, float3 reflectedColor,
                             float3 refractedColor, float oceanCoverage,
                             FoamLayer pondFoamLayer, FoamLayer surfFoamLayer, float sssBoost)
{
    float3 normal = g.normal;
    float3 incomingRay = g.incomingRay;
    float surfaceRoughness = g.roughness;
    float oceanFoam = oceanCoverage;
    float pondFoamAlpha = pondFoamLayer.alpha;
    float surfFoamAlpha = surfFoamLayer.alpha;
    // Foam is matte: the combined coverage knocks the specular reflection down before
    // compositing (this surface expresses gloss as the reflection term, so this IS the
    // "foam roughens the surface" cue).
    float foamMatte = max(max(oceanFoam, pondFoamAlpha), surfFoamAlpha);

    float3 outColor = lerp(refractedColor, reflectedColor,
                           fresnel * _ReflectionStrength * (1.0 - foamMatte));

    // ---- GGX sun specular, added AFTER the fresnel composite: the lobe carries its
    // own Schlick term at the half-vector, so folding it into the reflection lerp
    // (which is weighted by the surface fresnel) would double-count Fresnel. Scaled
    // by the reflection dial and matted by foam exactly like the mirror term, which
    // also keeps reflection-off bodies sun-free like the legacy glint did. Shares
    // surfaceRoughness with the sky mip above (computed with reflectedColor). ----
    outColor += SunSpecular(normal, -incomingRay, surfaceRoughness)
              * (_ReflectionStrength * (1.0 - foamMatte));

    // ---- Wave-crest subsurface glow, added emissively so it reads on EVERY sun-facing
    // crest regardless of what is behind it (the earlier in-scatter form only showed where
    // the volume behind the crest was deep, i.e. sky/far behind). Tinted by the scatter
    // body colour and lit by the sun; sssBoost already carries the crest pinch, sun-facing
    // and intensity. Knocked down by foam so whitecaps stay matte over the glow. ----
    if (sssBoost > 0.0)
        outColor += _ScatterColor.rgb * _SunColor * (sssBoost * (1.0 - foamMatte));
    return outColor;
}

// Shallow surf run-out: blend toward the refracted ground so centimetres-deep
// water reads clear instead of opaque blue.
float3 ApplyShallowClarity(float3 outColor, float3 refractedColor, ShoreData shoreFrag)
{
    // ---- Shallow-water clarity (surf bodies): centimetres-deep run-out shows the
    // ground through it instead of reading as flat opaque blue between the last
    // bore and the beach. Keyed off the WORLD-FRAME shore field so it works on the
    // windowed ocean too (the pool-bed block below is bounded-only). ----
    if (_SurfActive > 0.5 && shoreFrag.influence > 0.0
        && shoreFrag.depth > 0.0 && shoreFrag.depth < SHALLOW_CLARITY_DEPTH)
    {
        float shallowClarity = 1.0 - saturate(shoreFrag.depth / SHALLOW_CLARITY_DEPTH);
        outColor = lerp(outColor, refractedColor,
                        shallowClarity * SHALLOW_CLARITY_BLEND * shoreFrag.influence);
    }
    return outColor;
}

// Shoreline: bed-depth clip (dry beach), deep tint, the breathing swash film,
// wet-sand glaze and the FOAM-3 swash foam line. Contains the clip(); it runs
// under the same uniform gates as before, so discard behaviour is unchanged.
float3 ShorelineStage(v2f i, WaterGeomStage g, float3 outColor, float3 refractedColor,
                      float3 reflectedColor, float2 foamWorldDdx, float2 foamWorldDdy,
                      float3 bodyInscatter, out FoamLayer swashFoamLayer)
{
    float3 normal = g.normal;
    ShoreData shoreFrag = g.shore;
    // ---- Shoreline gradient from the real terrain depth (baked bed map).
    // Tint toward the deep-water colour by the water-column depth, so the surface
    // reads clear over shallows and dark over the drop-off. No-op until a bed is
    // baked and the toggle is on.
    // Surf swash (P4): the clip line breathes with the arriving fronts - the film runs
    // up the beach and drains back - and the zone the film has recently covered renders
    // as a dark wet-sand glaze instead of clipping away. Fully analytic (the swash and
    // the drying wet line are closed-form functions of the wave clock); zero when the
    // surf layer is off, so the classic hard waterline is byte-identical. ----
    // FOAM-3 swash foam accumulators - filled inside the bed-depth block below,
    // composited with the other foam layers after it (declared here for scope).
    float swashFoamAlpha = 0.0;
    float3 swashFoamLook = float3(0.0, 0.0, 0.0);
    if (_UseBedDepth > 0.5 && _BedValid > 0.5)
    {
        float2 bedUV = i.position.xz * 0.5 + 0.5;
        float bedPoolY = tex2Dlod(_BedTex, float4(bedUV, 0, 0)).r;
        float colDepth = BedColumnDepthWorld(bedPoolY, i.position.y, VolumeExtentSafe().y);
        // ONE WATERLINE: on surf bodies the fronts/lace/swash/debug all read the
        // world-frame shore field, but the clip/tint here read the pool-frame
        // _BedTex - two bakes on different texel grids whose zero crossings
        // disagree by up to a texel. That strip is the "continuous dry line" the
        // SDF debug shows at the shore: water still renders there while the shore
        // field already says land, so it gets no waves, no lace and a confused
        // swash. Use the SAME depth for the clip/tint/swash so every waterline
        // consumer agrees (feather-blended so leaving the field stays seamless).
        if (_SurfActive > 0.5 && shoreFrag.influence > 0.0)
            colDepth = lerp(colDepth, shoreFrag.depth, saturate(shoreFrag.influence));
        float2 swash = (_SurfActive > 0.5)
            ? EvaluateSurfSwash(i.largeWaveSourceXZ, shoreFrag.toShore,
                                shoreFrag.slopeTan,
                                shoreFrag.influence, _SurfBeatTime)
            : float2(0.0, 0.0);
        float swashLevel = swash.x;
        float wetLevel = swash.y;
        // Terrain mask: cut the water where the bed rises above the surface (dry beach)
        // so the plane doesn't draw over the sand. clip() discards the fragment; the small
        // positive bias keeps a hair of water right at the waterline (no shimmer gap).
        // The swash keeps fragments alive up to the wet line (current film OR still-drying
        // sand), so the film and the glaze have geometry to render on.
        const float SHORE_CLIP_BIAS = 0.02; // metres of water kept past the waterline
        // FOAM-5: keep the beach fragment alive wherever a persistent swash deposit still lives in
        // the foam buffer, so it renders + DISSOLVES on the sand instead of being clipped away when
        // the drying wet line recedes below it (matches the vertex's foam-aware lift, same coord).
        // beachRise = -colDepth, so raising the keep term to it makes colDepth cancel and the
        // fragment survives. Gated: gain 0 = byte-identical (the plain wet-line clip).
        float shoreKeep = max(swashLevel, wetLevel);
        if (_ShoreSwashDepositGain > 0.0)
        {
            // MUST match the vertex twin exactly - same lookup height, same ramp - or the clip and
            // the lifted geometry disagree and the film tears. _ShoreWaterLevel, not i.worldPos.y:
            // that is the POST-lift height here and the PRE-lift height in the vertex, and
            // WorldToSim().xz depends on y on a rotated body. See WaterSurfaceVertStage.hlsl.
            float2 depUV = (_SimWindowed < 0.5)
                ? (i.position.xz * 0.5 + 0.5)
                : (WorldToSim(float3(i.largeWaveSourceXZ.x, _ShoreWaterLevel,
                                     i.largeWaveSourceXZ.y)).xz * 0.5 + 0.5);
            float depositHold = smoothstep(FOAM_MASK_EPSILON, SURF_DEPOSIT_HOLD_FULL,
                                           SampleFoamMaskWindowed(depUV));
            // -colDepth = beachRise (lift onto the sand), faded in by the same hold weight.
            shoreKeep = lerp(shoreKeep, max(shoreKeep, -colDepth), depositHold);
        }
        clip(colDepth + SHORE_CLIP_BIAS + shoreKeep);
        // Depth clarity ties the deep tint to the SAME curve as turbidity/fog: murkier
        // (lower clarity) = more deep tint. BLENDED from the plain depth gradient toward the
        // RAW clarity curve by the strength - never the strength-folded WaterDepthClarity: the
        // old ternary read that fold through (1 - clarity), which at partial strength collapses
        // BELOW both endpoints (strength 0.5 halved the deep fill that both 0 and 1 deliver),
        // so the moment the dial left zero the bed showed through ("water becomes transparent
        // between 0 and 1", worst with Volume Scattering dimming the fill colour). Strength 0 =
        // the shore gradient (byte-identical), 1 = the full clarity look, monotonic between.
        float shore = 1.0 - exp(-_ShorelineDepthScale * colDepth);
        float tint = lerp(shore, 1.0 - WaterDepthClarityCurve(colDepth),
                          saturate(_DepthClarityStrength));
        // DEEP WATER MUST NOT CONVERGE TO AN UNLIT CONSTANT. This used to lerp toward
        // _DeepWaterColor directly, so as the column deepened the surface approached a fixed dark
        // colour that ignored sun, ambient and view angle entirely - which is why deep ocean read as
        // a black hole beside shallower water over terrain, at the same eye level.
        //
        // _DeepWaterColor is now a MULTIPLIER on the body's own in-scatter, which is what its name
        // always implied: it carries the hue shift AND the darkening, but applied to a colour that is
        // actually lit. Deep water therefore goes deep BLUE - a dimmer, more saturated version of the
        // water's real colour - and still responds to the sun the way the shallows do.
        //
        // AUTHORING CHANGE: the value is a multiplier now, not an absolute colour, so scenes tuned
        // against the old behaviour read far too dark until it is raised toward the 0..1 range.
        float3 deepTarget = bodyInscatter * _DeepWaterColor.rgb;
        outColor = lerp(outColor, deepTarget, saturate(tint * _ShorelineStrength));
        float beachRise = -colDepth;                    // metres above the still level
        // Thin-film transparency: the swash sheet is centimetres of water ON the
        // sand, not ocean - pull HARD toward the refracted ground so the film
        // reads wet-and-clear ("swash amplitude causes the blue water line" -
        // the band must never look like blue ocean sitting on the beach).
        //
        // This pull used to live inside the beachRise > 0 glaze gate below, so it went from 0 to
        // WET_FILM_MIN_TRANSPARENCY across ONE texel of the depth field: the sea turned 60% into
        // sand along a single hard line, which is the sharpest edge in the whole sea-to-swash
        // junction. It gets its own block and its own waterline feather now - the glaze and the
        // swash foam keep the original gate, since both already fall to zero on their own at the
        // waterline and neither wants to reach seaward of it.
        if (wetLevel > 0.0 && beachRise > -WET_FILM_WATERLINE_BAND)
        {
            float filmT = saturate(beachRise / max(wetLevel, 1e-3));
            float waterlineFade = smoothstep(-WET_FILM_WATERLINE_BAND,
                                             WET_FILM_WATERLINE_BAND, beachRise);
            outColor = lerp(outColor, refractedColor,
                            (WET_FILM_MIN_TRANSPARENCY + WET_FILM_DEPTH_GAIN * filmT)
                            * waterlineFade);
        }
        // The swash zone. The glaze is wet SAND, so it belongs strictly landward of the waterline -
        // but the swash FOAM band is centred on the film edge and straddles it, and the two used to
        // share one beachRise > 0 gate. That amputated the foam band's entire seaward half - up to
        // _SurfSwashFoamWidth, 0.25 m by default - along a dead-straight line, which is the seam
        // where the shore-wave foam visibly stopped and the swash foam started.
        //
        // The zone now opens ONE BAND-WIDTH OFFSHORE, so the swash foam reaches back into the
        // whitewash's own fade-out (SurfFieldMask's wet term is finished ~5 cm up the sand) instead
        // of starting where it ends. Nothing else is needed to make that seamless: the band's own
        // falloff already takes it to zero, and the final composite maxes the foam layers' alphas
        // over a shared pattern, so the overlap BLENDS the two lines rather than stacking them.
        // Only the glaze keeps the dry-side gate.
        float swashBand = max(_SurfSwashFoamWidth, 0.01);
        if (wetLevel > 0.0 && beachRise > -swashBand)
        {
            // Wet-sand glaze: fragments above the CURRENT film but under the drying wet line
            // show the darkened scene through a thin glossy sheet - wet sand with zero state.
            if (beachRise > 0.0)
            {
                float aboveFilm = saturate((beachRise - swashLevel)
                                           / max(wetLevel - swashLevel, 1e-3));
                float glaze = aboveFilm * smoothstep(0.0, WET_GLAZE_EDGE,
                                                     (wetLevel - beachRise)
                                                     / max(wetLevel, 1e-3));
                float3 wetLook = refractedColor * WET_GLAZE_REFRACT
                               + reflectedColor * WET_GLAZE_REFLECT;
                outColor = lerp(outColor, wetLook, glaze * WET_GLAZE_STRENGTH);
            }

            // ---- FOAM-3: swash foam. A foamy line rides the film's leading edge
            // up the beach, is STRANDED at the wash border (the wet line) at the
            // apex, then dissolves into holes and stretches into downslope drain
            // streaks through the reflux. Fully analytic: phase + levels are the
            // same closed forms as the film itself, so the foam can never desync
            // from the water it rides. Strength 0 = the block is skipped and the
            // beach is byte-identical. ----
            if (_SurfSwashFoam > 0.0 && _SurfActive > 0.5)
            {
                // SURF_MIN_PERIOD, not a literal 0.5: this is the SAME clock EvaluateSurfSwash
                // floors with, and a hand-copied floor would silently desync the foam from the
                // film it rides the moment the define is retuned.
                float swashT = max(_SurfPeriod, SURF_MIN_PERIOD);
                // Same phase convention as EvaluateSurfSwash: 0 = crest arrival.
                float swashPhase = frac(_SurfBeatTime / swashT - 0.5);
                // Backwash age: 0 at the apex (film just turned), 1 at full reflux. Drives the
                // deposit's hole-erosion and the drain-streak stretch, which both intensify as
                // the stranded line dries.
                float refluxAge = smoothstep(SURF_SWASH_UPRUSH, 1.0, swashPhase);
                // Bore edge: foam hugging the film's leading edge (rides up with
                // the uprush, retreats with the film - a thin working line). swashBand is
                // hoisted to the zone gate above - it is what sets the zone's seaward reach.
                float edgeFoamW = saturate(1.0 - abs(beachRise - swashLevel) / swashBand);
                // Deposit VISIBILITY envelope. The line is LAID when the film turns (apex ~ UPRUSH)
                // and then DISSOLVES back to ~0 across the rest of the cycle, so it fades out
                // gradually instead of vanishing at the rollover. The old form multiplied by the
                // backwash progress, which grew the deposit to FULL brightness right AT the wrap
                // and then cut it - THE abrupt disappearance. This is a single monotonic hump
                // (rise just past the apex, decay to zero by the wrap): no wrap snap, and unlike
                // the previous max()-of-two-cycles attempt, no mid-cycle dip either. wetLevel is
                // itself a continuous two-front envelope, so the deposit's POSITION is continuous
                // too. (Lingering across SEVERAL waves would need the persistent sim buffer - this
                // stays fully analytic and self-contained.)
                float depositEnv = smoothstep(SURF_SWASH_UPRUSH, SURF_SWASH_DEPOSIT_PEAK, swashPhase)
                                 * (1.0 - smoothstep(SURF_SWASH_DEPOSIT_PEAK, 1.0, swashPhase));
                float depositW = saturate(1.0 - abs(beachRise - wetLevel) / swashBand)
                               * depositEnv;
                float rawCoverage = max(edgeFoamW, depositW);
                float swashCoverage = saturate(rawCoverage * _SurfSwashFoam);
                // Reflux age is the STRANDED DEPOSIT'S clock, so only the deposit may be eroded by
                // it. It used to erode the whole coverage - the live bore edge included - and that
                // is what popped the foam once per wave: refluxAge is a frac()-driven SAWTOOTH that
                // snaps 1 -> 0 at the wrap, the wrap IS crest arrival, and edgeFoamW is at its
                // MAXIMUM there (the band sits on the waterline while swashLevel is 0). So the
                // dissolve threshold fell ~0.42 in one frame over bright coverage and eroded lace
                // became solid foam: pop, then drift. depositEnv is already 0 at the wrap, so
                // weighting the erosion by the deposit's share of the coverage multiplies the
                // sawtooth by something that vanishes exactly where it jumps - continuous across
                // the wrap, and unchanged mid-backwash where the deposit owns the coverage anyway.
                //
                // The bore edge is fresh foam BY DEFINITION (it is the film's leading edge, renewed
                // every frame), so it never had any business carrying an age in the first place.
                float depositShare = depositW / max(rawCoverage, FOAM_MASK_EPSILON);
                float swashErode = refluxAge * depositShare * _SurfSwashFoamDissolve
                                 * SURF_SWASH_ERODE_MAX;
                if (swashCoverage > FOAM_MASK_EPSILON)
                {
                    // Plain world XZ with the hoisted gradients. This used to be warped by an
                    // anisotropic downslope stretch that grew with reflux age (Swash Streak),
                    // REMOVED 2026-07-30 - it jittered and added nothing readable. Note WHY it
                    // jittered, because the shape recurs: an age-animated COORDINATE WARP slides
                    // the pattern under a static fragment every frame, so it shimmers by
                    // construction. The 2026-07-22 field-centre pivot only bounded how FAST it
                    // slid (it fixed the distance-dependent "weird distortion"); no pivot can
                    // remove motion that is the effect's own definition.
                    float swashDist = distance(i.largeWaveSourceXZ,
                                               _WorldSpaceCameraPos.xz);
                    float3 swashPattern = SampleOceanWhitecapPatternTiled(
                        i.largeWaveSourceXZ, swashDist, max(_SurfFoamTileSize, 1e-3),
                        foamWorldDdx, foamWorldDdy);
                    // Same shared law as the whitewash (FoamDissolve), plus the
                    // reflux hole-erosion: age raises the dissolve threshold, so
                    // the stranded line rots into lace patches, then filaments.
                    float swashFoam = FoamDissolve(swashPattern.r, swashCoverage,
                                                   _SurfFoamFeather, swashErode);
                    if (swashFoam > FOAM_MASK_EPSILON)
                    {
                        // Lit like the whitewash (wrapped sun over the surface
                        // normal); tinted by the shared surf foam colour so the
                        // line matches the bores that fed it. NOTE: the specular
                        // matte skips this layer (the beach zone is already pulled
                        // hard toward the refracted ground above).
                        float swashWrapped = FoamWrappedDiffuse(normal, _LightDir);
                        float3 swashTint = _SurfFoamColor.rgb
                            * lerp(swashPattern, float3(1.0, 1.0, 1.0), swashFoam);
                        swashFoamLook = FoamLitColor(swashTint, _SunColor, swashWrapped);
                        swashFoamAlpha = swashFoam * _SurfFoamColor.a;
                    }
                }
            }
        }
    }
    swashFoamLayer.alpha = swashFoamAlpha;
    swashFoamLayer.look = swashFoamLook;
    return outColor;
}

// How much of what the opaque texture holds at a screen UV is actually SKY.
//
// The horizon haze below reads its target colour out of _CameraOpaqueTexture, on the premise that
// the horizon row of that texture is the rendered sky band. That premise only holds over EMPTY
// ocean: the opaque texture holds every opaque object, so a hull, its rigging, or a coastline
// sitting on the horizon row BECOMES the colour the whole far ocean fades into. A boat riding up
// and down a big sea sweeps its own sails across that row, which is what made the haze flash.
//
// Sky is the only thing left at the far plane (the skybox does not write depth, so those pixels keep
// the cleared far value), so eye depth is a reliable sky test. Weighted, not a binary reject: a tap
// straddling the silhouette of a mast must fade, not pop - which is the failure this whole pass is
// about.
#define HORIZON_SKY_DEPTH_NEAR 0.90   // fraction of the far plane where a tap starts counting as sky
#define HORIZON_SKY_DEPTH_FAR  0.99   // ...and where it fully does
float HorizonSkyWeight(float2 uv)
{
    float eyeDepth = LinearEyeDepth(RawSceneDepth(uv));
    return smoothstep(HORIZON_SKY_DEPTH_NEAR * _ProjectionParams.z,
                      HORIZON_SKY_DEPTH_FAR * _ProjectionParams.z, eyeDepth);
}

// The horizontal direction a view ray looks along - "where is the sky at the horizon on this
// bearing". Every haze path needs it, and every one of them used to be handed incomingRay instead,
// which points camera->surface: from a camera above the water that aims steeply DOWN, so the
// environment cube was read well BELOW the horizon. Near vertical, there is no meaningful bearing;
// return a finite placeholder and let the confidence path below fade it out.
#define HORIZON_AZIMUTH_MIN 1e-4   // below this the view ray is straight down; there is no azimuth
#define HORIZON_FALLBACK_DIRECTION float3(0.0, 0.0, 1.0)
float3 HorizonDirection(float3 viewRay)
{
    float azimuthLength = length(viewRay.xz);
    float3 normalizedAzimuth = float3(viewRay.x, 0.0, viewRay.z)
                            / max(azimuthLength, HORIZON_AZIMUTH_MIN);
    float azimuthValidity = smoothstep(0.0, HORIZON_AZIMUTH_MIN, azimuthLength);
    return normalize(lerp(HORIZON_FALLBACK_DIRECTION, normalizedAzimuth, azimuthValidity));
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
// and the Layer A debug overlays.
float3 FinalCompositeStage(v2f i, WaterGeomStage g, float3 outColor,
                           FoamLayer oceanFoamLayer, FoamLayer pondFoamLayer,
                           FoamLayer surfFoamLayer, FoamLayer swashFoamLayer)
{
    float3 incomingRay = g.incomingRay;
    float oceanFoamAlpha = oceanFoamLayer.alpha;
    float3 oceanFoamLook = oceanFoamLayer.look;
    float pondFoamAlpha = pondFoamLayer.alpha;
    float3 pondFoamLook = pondFoamLayer.look;
    float surfFoamAlpha = surfFoamLayer.alpha;
    float3 surfFoamLook = surfFoamLayer.look;
    float swashFoamAlpha = swashFoamLayer.alpha;
    float3 swashFoamLook = swashFoamLayer.look;
    // ---- Exclusive foam composite (looks evaluated above, before the reflection
    // composite, so the combined coverage could matte the specular): ONE write into
    // outColor, after the shoreline gradient so foam sits over it. Coverage is the max of
    // the layers (never their stack) and the colour is their alpha-weighted blend, so a
    // lone layer is bit-identical to the old per-layer lerp while overlap can no longer
    // double-lay foam. ----
    float foamCombinedAlpha = max(max(max(oceanFoamAlpha, pondFoamAlpha),
                                      surfFoamAlpha), swashFoamAlpha);
    if (foamCombinedAlpha > 0.0)
    {
        float3 foamCombinedLook = (oceanFoamLook * oceanFoamAlpha
                                   + pondFoamLook * pondFoamAlpha
                                   + surfFoamLook * surfFoamAlpha
                                   + swashFoamLook * swashFoamAlpha)
                                / max(oceanFoamAlpha + pondFoamAlpha
                                      + surfFoamAlpha + swashFoamAlpha, 1e-5);
        outColor = lerp(outColor, foamCombinedLook, foamCombinedAlpha);
    }

    // ---- Horizon haze: dissolve the far ocean surface into the sky so the outer mesh
    // edge / water-sky boundary has no hard line. The exponential 1 - exp(-density * dist)
    // falloff reads like real distance haze instead of a hard band. Off when density is 0
    // (bounded bodies, unchanged). ----
    if (_HorizonHazeDensity > 0.0)
    {
        float horizD = distance(i.worldPos, _WorldSpaceCameraPos);
        // _HorizonHazeDensity is now a 0..1 AMOUNT, not a raw per-metre density: horizon distances
        // run to km, so a raw density saturated the whole ocean above ~0.001 (not a volumetric fog).
        // Map the amount to a gentle max per-metre density so the whole 0..1 slider is usable, and
        // ~0.3-0.5 reads as a light haze. HORIZON_HAZE_MAX_DENSITY = the density at amount 1 (set at
        // the old saturation point, so it's the ceiling, not the floor - lower it for a softer max).
        #define HORIZON_HAZE_MAX_DENSITY 0.001
        float haze = 1.0 - exp(-_HorizonHazeDensity * HORIZON_HAZE_MAX_DENSITY * horizD);
        // Haze target = the rendered sky AT THE HORIZON in this pixel's azimuth, read from
        // _CameraOpaqueTexture: URP draws the skybox before the opaque-colour copy and the
        // water pass is transparent-queue, so the opaque texture holds the water-free scene -
        // at the horizon line, the TRUE sky band for ANY sky type (procedural, gradient,
        // cubemap, animating). The horizontal view direction is projected as a DIRECTION
        // (w = 0 -> point at infinity, i.e. exactly where the skybox drew that azimuth).
        // Sampling AT the horizon - not behind the pixel - is what makes a dense haze read
        // as aerial perspective: over deep ocean the opaque pass behind mid-distance pixels
        // is the BELOW-horizon skybox, so the behind-pixel variant turned thick fog into a
        // pasted sky mirror. At the far mesh edge the projection converges to the fragment's
        // own screen position, so the seamless water-sky join is preserved. A degenerate azimuth
        // (looking straight down), a horizon behind the camera (w ~ 0), or a horizon that has left
        // the frame entirely all fade to the environment cube along horizonDir instead - see the
        // confidence terms below; there is no rendered sky left to match in those poses. Explicit-LOD
        // sample, so the per-pixel UV selection is WGSL-safe. (The SH ambient probe was tried
        // and rejected: unity_SHAr..SHC are never bound for this CGPROGRAM pass under URP
        // Forward+ - zeros, far water faded to BLACK - the same per-object-binding failure
        // as unity_SpecCube0, see SampleSkyEnvironmentGrad.)
        // Hoisted out of the branch below: both the opaque path and the cube fallback want it.
        float azimuthLen = length(incomingRay.xz);
        float3 horizonDir = HorizonDirection(incomingRay);
        float3 skyAtHorizon;
        if (_RealRefraction > 0.5)
        {
            float4 horizonClip = mul(UNITY_MATRIX_VP, float4(horizonDir, 0.0));
            float2 horizonUVraw = ScreenUV(ComputeScreenPos(horizonClip));
            // Two problems this handles: (1) LOOKING DOWN the horizon projects off the TOP of the
            // screen; saturate() then pins every azimuth to the same top-edge texel, so pixels on
            // one compass bearing all read one colour = radial "vertical lines" from the nadir.
            // (2) at the pitch where the horizon crosses the edge (~15 deg, worst when the camera is
            // close to the water) a hard switch, or a switch to the SKY CUBEMAP, prints a colour BAND
            // because the cubemap doesn't match the rendered skybox in the opaque texture.
            // FIX: keep BOTH samples in the SAME opaque texture and crossfade by edge distance:
            //  - on screen -> the PER-AZIMUTH horizon UV (exact skybox match = the v2 look);
            //  - leaving/off screen -> a CENTRE-COLUMN sample at the same horizon row (x fixed to
            //    0.5): uniform across azimuth, so it carries NONE of the top-edge per-bearing
            //    structure (no streak), and same colour source as the on-screen sample (no band).
            #define HORIZON_EDGE_BLEND 0.12   // screen fraction over which per-azimuth hands to centre
            // ONLY the vertical (top/bottom) edge matters - the horizon leaves the frame off the TOP
            // when you look down, which is the only time it clamps + streaks. The horizontal screen
            // edges must NOT trigger the centre blend, or a near-horizontal view gets vertical colour
            // BANDS down the LEFT/RIGHT of the water (min-of-both-axes bug). So measure Y alone.
            // horizonUVraw is a PROJECTED coordinate: it goes hyperbolic as the horizon direction
            // approaches the camera plane (ScreenUV divides by w), and its SIGN inverts past it. Used
            // raw as a blend parameter - which it was - the 12%-of-screen soft band collapses to zero
            // ANGULAR width exactly where the camera is pitching through that pose, so the crossfade
            // degenerated into a step: the pop when the boat pitches on a big sea, or the fly cam
            // looks down from high up. Clamp it to a bounded range so the edge test keeps a finite
            // transition, and let the w term below carry the fade through that regime.
            #define HORIZON_UV_GUARD 1.0
            float2 horizonUV = clamp(horizonUVraw, -HORIZON_UV_GUARD, 1.0 + HORIZON_UV_GUARD);
            float edgeMinY = min(horizonUV.y, 1.0 - horizonUV.y); // >0 = horizon vertically in frame

            // horizonDir is a UNIT vector, so horizonClip.w is exactly the cosine of the angle between
            // it and the camera's forward axis - BOUNDED in [-1,1] and smooth in camera pitch, unlike
            // the projected UV. That makes it the right parameter for "is this projection usable":
            // 1 = the horizon is straight ahead, 0 = it lies in the camera plane (projection blows
            // up), negative = behind the camera.
            #define HORIZON_FORWARD_MIN  0.05  // below this the projection is unusable
            #define HORIZON_FORWARD_FADE 0.25  // ...above this it is trustworthy
            #define HORIZON_AZIMUTH_FADE 0.05  // view ray this close to straight down has no azimuth
            // Every reason to keep the per-azimuth sample, as a PRODUCT of smooth terms - all three
            // must hold. The hard "if (... ) toCentre = 1.0" this replaces was a binary flip with no
            // blend: the same step-instead-of-blend shape that caused the god-ray and waterline pops.
            float azimuthConfidence = smoothstep(HORIZON_AZIMUTH_MIN, HORIZON_AZIMUTH_FADE, azimuthLen);
            float keepPerAzimuth =
                  azimuthConfidence
                * smoothstep(HORIZON_FORWARD_MIN, HORIZON_FORWARD_FADE, horizonClip.w)
                * smoothstep(0.0, HORIZON_EDGE_BLEND, edgeMinY);
            float toCentre = 1.0 - keepPerAzimuth;
            // BOTH opaque samples read the horizon ROW - the centre band is just a different COLUMN
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
            float skyConfidence = lerp(perAzimuthSky, centreSky, useCentre)
                                * horizonRowOnScreen * azimuthConfidence;
            skyAtHorizon = lerp(SampleRawSkyEnvironment(horizonDir), opaqueSky, skyConfidence);
        }
        else
        {
            // Tiers without the opaque texture keep the reflection-cube fallback
            // (uniform gate, implicit derivatives allowed here). Along the HORIZON direction, not
            // the view ray: the view ray points down at the water, so the cube would be read well
            // below the horizon.
            skyAtHorizon = SampleRawSkyEnvironment(horizonDir);
        }
        // _HorizonHazeColor stays an optional bias: alpha 0 (default) = pure auto-match;
        // raise alpha to pull the haze toward a fixed atmosphere colour.
        float3 hazeTarget = lerp(skyAtHorizon, _HorizonHazeColor.rgb, _HorizonHazeColor.a);
        outColor = lerp(outColor, hazeTarget, haze);
    }
    // Legacy smoothstep stopgap (retired in a later increment): only when the new haze is
    // off, so a scene still tuned with Horizon Fade Distance keeps its look meanwhile.
    else if (_HorizonFadeDistance > 0.0)
    {
        float horizD = distance(i.worldPos, _WorldSpaceCameraPos);
        float horizonFade = smoothstep(_HorizonFadeDistance * HORIZON_FADE_START, _HorizonFadeDistance, horizD);
        outColor = lerp(outColor, SampleEnvironment(HorizonDirection(incomingRay)), horizonFade);
    }

    // ---- Layer A debug: visualize the world-frame seabed-depth field on the surface
    // (red = dry / seabed above surface, green shallow -> blue deep). Debug only;
    // _ShoreDepthDebug is off unless toggled from the WaterVolume context menu. ----
    if (_ShoreDepthDebug > 0.5 && _ShoreDepthValid > 0.5)
    {
        float2 shoreUV = (i.worldPos.xz - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
        // P0: the field stores the still-water column depth directly (see WaterShore.hlsl).
        float shoreColDepth = tex2Dlod(_ShoreDepthTex, float4(shoreUV, 0, 0)).r;
        const float SHORE_DEBUG_RANGE = 10.0;           // depth (m) mapped shallow -> deep
        float3 shoreDbg = (shoreColDepth < 0.0)
            ? float3(1.0, 0.0, 0.0)
            : lerp(float3(0.1, 0.9, 0.4), float3(0.0, 0.2, 0.9), saturate(shoreColDepth / SHORE_DEBUG_RANGE));
        float shoreInField = all(shoreUV == saturate(shoreUV)) ? 1.0 : 0.0;
        outColor = lerp(outColor, shoreDbg, shoreInField);
    }

    // ---- Layer A debug: visualize the shoreline SDF (signed distance to shore). Water
    // side cyan, land side orange, banded every few metres so distance reads as contours.
    // Debug only; _ShoreSDFDebug is off unless toggled from the context menu. ----
    if (_ShoreSDFDebug > 0.5 && _ShoreSDFValid > 0.5)
    {
        float2 sdfUV = (i.worldPos.xz - _ShoreDepthCenter.xy) / (2.0 * _ShoreDepthSize.xy) + 0.5;
        float4 sdfSample = tex2Dlod(_ShoreSDFTex, float4(sdfUV, 0, 0));
        float signedDist = sdfSample.b;
        const float SHORE_SDF_DEBUG_BAND = 5.0; // metres between distance contours
        float band = frac(abs(signedDist) / SHORE_SDF_DEBUG_BAND);
        float3 sdfDbg = (signedDist >= 0.0) ? float3(0.1, 0.7, 1.0) : float3(1.0, 0.5, 0.1);
        sdfDbg *= 0.55 + 0.45 * band;
        // A now stores the beach slope (SURF-PHYS), not a mask - in-field validity
        // comes from the UV test + _ShoreSDFValid gate above.
        float sdfInField = all(sdfUV == saturate(sdfUV)) ? 1.0 : 0.0;
        outColor = lerp(outColor, sdfDbg, sdfInField);
    }
    return outColor;
}

#endif // WATER_SURFACE_FRAG_STAGES_INCLUDED
