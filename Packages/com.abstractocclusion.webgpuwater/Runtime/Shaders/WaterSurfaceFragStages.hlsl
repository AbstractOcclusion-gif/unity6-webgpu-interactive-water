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
    float4 info = SampleRipple(i.position, i.worldPos, fade);

    // make the water look more "peaked": walk a few steps along the ripple normal
    // in the active UV domain (pool for whole-body, sim window for windowed).
    float2 coord = (_SimWindowed < 0.5) ? (i.position.xz * 0.5 + 0.5)
                                        : (WorldToSim(i.worldPos).xz * 0.5 + 0.5);
    int refineSteps = clamp((int)_PeakedRefineSteps, 0, PEAKED_REFINE_MAX_STEPS);
    [loop] // uniform trip count (tier knob); explicit-LOD samples are loop-safe
    for (int k = 0; k < refineSteps; k++)
    {
        // The walk only needs a DIRECTION, so the cheap bilinear tap is enough per step. Each
        // replacement is re-faded because SampleRipple already returns a FADED normal: fading once
        // here and once after the loop would square it at refineSteps = 0 only, so the sim-window
        // border falloff changed shape with the quality tier.
        coord += info.ba * PEAKED_REFINE_STEP;
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
    float2 nxz = info.ba - WaveSlope(WindWaveSampleXZ(i.position.xz, i.largeWaveSourceXZ)) * _WaveNormalStrength;
    float3 normalPool = float3(nxz.x, sqrt(max(1e-4, 1.0 - dot(nxz, nxz))), nxz.y);
    // World-space surface normal + view ray, so reflection/refraction angles
    // are correct even when the volume is rotated or has a rectangular footprint.
    float3 normal = PoolNormalToWorld(normalPool);
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
        float4 normalFoam = ApplyLargeBodyWaveNormalFoamShore(normal, i.largeWaveSourceXZ,
                                                              _WaveNormalStrength,
                                                              shoreFrag, surfFrag);
        normal = normalFoam.xyz;
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
        float2 detailTilt = DetailNormalTilt(i.largeWaveSourceXZ, viewDistWorld);
        normal = normalize(normal + float3(detailTilt.x, 0.0, detailTilt.y)
                                    * detailNormalStrength);
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
    float3 refractedColor = GetSurfaceRayColor(i.worldPos, refractedRay, float3(1.0, 1.0, 1.0)) * UnderwaterViewTint();

    // Real transparency from below: sample the live scene above the surface.
    if (_RealRefraction > 0.5)
    {
        float2 ruvU = ScreenUV(i.screenPos) + normal.xz * _RefractionDistortion;
        refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruvU)).rgb * UnderwaterViewTint();
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

    // ---- Foam seen from below: the same advected mask, but instead of lit
    // white it reads as a SILHOUETTE - dense foam blocks the sky coming
    // through the surface, thin lace scatters a faint sun glow through.
    // No contact foam here: the depth texture holds the scene ABOVE the
    // surface from this side, so the contact heuristic is meaningless. ----
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
        float mask = SimFoamCoverage(i.position.xz, fcoord, 0.0);

        // Same world-space pattern UV as the above-water side. Computed (with its
        // screen derivatives) BEFORE the mask branch: WGSL requires derivatives in
        // uniform control flow, and the branch below is per-fragment.
        float2 fuv = i.worldPos.xz / max(_FoamTileSize, 1e-3)
                   + normal.xz * FOAM_NORMAL_NUDGE;
        float2 fuvDdx = ddx(fuv);
        float2 fuvDdy = ddy(fuv);

        if (mask > FOAM_MASK_EPSILON)
        {
            float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
            float3 pattern; float core, lace, foamAlpha; float2 tilt;
            EvaluateFoam(fuv, fuvDdx, fuvDdy, nxz, mask, foamDist, pattern, core, lace, foamAlpha, tilt);

            // Applied BEFORE the downwelling dim below, so the silhouette
            // and its glow fade with eye depth like the rest of the scene.
            float sunThrough = saturate(_LightDir.y);
            underColor *= 1.0 - _FoamUndersideDarken * foamAlpha;
            underColor += _FoamColor.rgb * pattern * (_FoamUndersideGlow * sunThrough * lace * mask);
        }
    }

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
    float3 reflectedColor = SampleSkyEnvironmentAniso(skyRay, surfaceRoughness);

    // ---- Reflection: analytic -> planar -> SSR (SSR wins where it hits). The toggles
    // are uniform-driven (published per body via the property block), so they are live. ----
    // The mirror covers exactly the screen and is MIRROR-wrapped (PlanarMirror.cs), so every
    // sample has real data and the sky needs no blending back in. Blending it at the border was
    // tried and drew a visible seam around the frame instead.
    if (_UsePlanar > 0.5)
        reflectedColor = SamplePlanarReflection(i.screenPos, normal, surfaceRoughness);
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
        refractedColor = tex2D(_CameraOpaqueTexture, saturate(ruv)).rgb; // tinted by the water absorption below

        // Fog the transmitted view by the water thickness behind the surface
        // (scene eye-depth - surface eye-depth), so heavy fog reads through too.
        // Chunk bodies cap the span at the primitive exit (the scene behind is DRY space).
        float sceneEyeR = LinearEyeDepth(RawSceneDepth(saturate(ruv)));
        float surfEyeR  = EyeDepthOf(i.worldPos);
        float waterSpan = max(0.0, sceneEyeR - surfEyeR);
        if (_ChunkFogClamp > 0.5)
            waterSpan = min(waterSpan, ChunkRefractionSpan(i.position, refractedRay));
        refractedColor = ApplyWaterVolumeClarity(refractedColor, waterSpan, bodyInscatter, waterClarity);
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
        refractedColor = ApplyWaterVolumeClarity(refractedColor, length(exitWorld - i.worldPos), bodyInscatter, waterClarity);
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
    float3 oceanFoamPattern = float3(1.0, 1.0, 1.0);
    float2 oceanFoamSampleXZ = i.largeWaveSourceXZ; // parallax-lifted pattern-sample point
    float coverage = 0.0;
    if (_OceanFftActive > 0.5)
    {
        // The surf band is the surf system's territory: the FFT foam ACCUMULATOR
        // is depth-blind (its small cascades still whitecap at 2 m of water), so
        // accumulated ocean whitecaps fade out where the fronts/whitewash own the
        // shallows. Inert off surf bodies (the gate is 0 there).
        coverage = OceanFftFoam(i.largeWaveSourceXZ)
                 * (1.0 - LbwGeometryFoamGate(shoreFrag));
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
        // Shared KWS contrast/dissolve law (FoamDissolve above); no erosion term.
        oceanFoam = FoamDissolve(oceanFoamPattern.r, coverage, _OceanFoamFeather, 0.0);
    }

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
                                 && _ChunkSphereClip < 0.5 && _ChunkUseMesh < 0.5;
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

        // WORLD-space pattern UV (like the ocean whitecap): scale set by the
        // body's Foam Pattern Size, independent of extent, anchored under a
        // scrolling window; nudged by the surface tilt so foam rides ripples.
        // Computed (with its screen derivatives) BEFORE the mask branch: WGSL
        // requires derivatives in uniform control flow, and the branch below
        // is per-fragment.
        float2 fuv = i.worldPos.xz / max(_FoamTileSize, 1e-3)
                   + normal.xz * FOAM_NORMAL_NUDGE;
        float2 fuvDdx = ddx(fuv);
        float2 fuvDdy = ddy(fuv);

        if (mask > FOAM_MASK_EPSILON)
        {
            float foamDist = distance(i.worldPos.xz, _WorldSpaceCameraPos.xz);
            float3 pattern; float core, lace, foamAlpha; float2 tilt;
            EvaluateFoam(fuv, fuvDdx, fuvDdy, nxz, mask, foamDist, pattern, core, lace, foamAlpha, tilt);

            // ---- Foam relief: tilt the lighting normal by the foam's own
            // normal map so the lace shades three-dimensionally. ----
            float3 foamNormal = ApplyFoamTiltToNormal(normal, tilt);

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
    float3 incomingRay = g.incomingRay;
    SurfWaveSample surfFrag = g.surf;
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
    float surfFoamAlpha = 0.0;
    float3 surfFoamLook = float3(0.0, 0.0, 0.0);
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
        float2 surfSampleXZ = i.largeWaveSourceXZ + surfViewToCam.xz
            * (OCEAN_FOAM_PARALLAX_HEIGHT / max(surfViewToCam.y, OCEAN_FOAM_PARALLAX_MIN_VIEW_Y));
        float surfDist = distance(i.largeWaveSourceXZ, _WorldSpaceCameraPos.xz);
        float surfTile = max(_SurfFoamTileSize, 1e-3);
        // Gradients hoisted with the whitecap's (foamWorldDdx/Ddy above): same base
        // world XZ, additive parallax - exact for this tap too (WGSL uniformity).
        float3 surfPattern = SampleOceanWhitecapPatternTiled(surfSampleXZ, surfDist, surfTile,
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
        float surfFoam = FoamDissolve(surfPattern.r, surfCoverage, _SurfFoamFeather,
                                      surfTrailErode);
        if (surfFoam > FOAM_MASK_EPSILON)
        {
            float2 surfTiltXY = SampleOceanWhitecapTiltTiled(surfSampleXZ, surfTile,
                                                             foamWorldDdx, foamWorldDdy)
                              * (_FoamNormalStrength * surfFoam);
            float3 surfFoamNormal = ApplyFoamTiltToNormal(normal, surfTiltXY);
            float surfWrapped = FoamWrappedDiffuse(surfFoamNormal, _LightDir);
            float3 surfTint = _SurfFoamColor.rgb
                * lerp(surfPattern, float3(1.0, 1.0, 1.0), surfFoam);
            surfFoamLook = FoamLitColor(surfTint, _SunColor, surfWrapped);
            surfFoamAlpha = surfFoam * _SurfFoamColor.a;
        }
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
            float2 depUV = (_SimWindowed < 0.5)
                ? (i.position.xz * 0.5 + 0.5)
                : (WorldToSim(float3(i.largeWaveSourceXZ.x, i.worldPos.y,
                                     i.largeWaveSourceXZ.y)).xz * 0.5 + 0.5);
            if (SampleFoamMaskWindowed(depUV) > FOAM_MASK_EPSILON)
                shoreKeep = max(shoreKeep, -colDepth); // -colDepth = beachRise (lift onto the sand)
        }
        clip(colDepth + SHORE_CLIP_BIAS + shoreKeep);
        // Depth clarity ties the deep tint to the SAME curve as turbidity/fog: murkier
        // (lower clarity) = more deep tint. Falls back to the plain depth gradient when
        // clarity is off (WaterDepthClarity = 1 -> tint = shore), so bodies not using it
        // are byte-identical.
        float shore = 1.0 - exp(-_ShorelineDepthScale * colDepth);
        float tint = (_DepthClarityStrength > 0.0) ? (1.0 - WaterDepthClarity(colDepth)) : shore;
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
        // Wet-sand glaze: fragments above the CURRENT film but under the drying wet line
        // show the darkened scene through a thin glossy sheet - wet sand with zero state.
        float beachRise = -colDepth;                    // metres above the still level
        if (beachRise > 0.0 && wetLevel > 0.0)
        {
            // Thin-film transparency: the swash sheet is centimetres of water ON the
            // sand, not ocean - pull HARD toward the refracted ground so the film
            // reads wet-and-clear ("swash amplitude causes the blue water line" -
            // the band must never look like blue ocean sitting on the beach).
            float filmT = saturate(beachRise / max(wetLevel, 1e-3));
            outColor = lerp(outColor, refractedColor,
                            WET_FILM_MIN_TRANSPARENCY + WET_FILM_DEPTH_GAIN * filmT);
            float aboveFilm = saturate((beachRise - swashLevel)
                                       / max(wetLevel - swashLevel, 1e-3));
            float glaze = aboveFilm * smoothstep(0.0, WET_GLAZE_EDGE,
                                                 (wetLevel - beachRise)
                                                 / max(wetLevel, 1e-3));
            float3 wetLook = refractedColor * WET_GLAZE_REFRACT
                           + reflectedColor * WET_GLAZE_REFLECT;
            outColor = lerp(outColor, wetLook, glaze * WET_GLAZE_STRENGTH);

            // ---- FOAM-3: swash foam. A foamy line rides the film's leading edge
            // up the beach, is STRANDED at the wash border (the wet line) at the
            // apex, then dissolves into holes and stretches into downslope drain
            // streaks through the reflux. Fully analytic: phase + levels are the
            // same closed forms as the film itself, so the foam can never desync
            // from the water it rides. Strength 0 = the block is skipped and the
            // beach is byte-identical. ----
            if (_SurfSwashFoam > 0.0 && _SurfActive > 0.5)
            {
                float swashT = max(_SurfPeriod, 0.5);
                // Same phase convention as EvaluateSurfSwash: 0 = crest arrival.
                float swashPhase = frac(_SurfBeatTime / swashT - 0.5);
                // Backwash age: 0 at the apex (film just turned), 1 at full reflux. Drives the
                // deposit's hole-erosion and the drain-streak stretch, which both intensify as
                // the stranded line dries.
                float refluxAge = smoothstep(SURF_SWASH_UPRUSH, 1.0, swashPhase);
                float swashBand = max(_SurfSwashFoamWidth, 0.01);
                // Bore edge: foam hugging the film's leading edge (rides up with
                // the uprush, retreats with the film - a thin working line).
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
                float swashCoverage = saturate(max(edgeFoamW, depositW) * _SurfSwashFoam);
                if (swashCoverage > FOAM_MASK_EPSILON)
                {
                    // Downslope drain streaks: a LINEAR xz warp stretching the
                    // pattern along the local downslope axis (toward the water-
                    // line), growing with reflux age. Linear, so the hoisted
                    // gradients transform exactly (WGSL uniformity intact).
                    float2 streakAxis = shoreFrag.toShore;
                    float streakAlong = 1.0 / (1.0 + _SurfSwashStreak * refluxAge
                                                     * SURF_SWASH_STREAK_GAIN);
                    // Pivot the anisotropic stretch at the SHORE-FIELD CENTRE, not the world
                    // origin. The old form scaled dot(worldXZ, axis) about (0,0), so the sample
                    // point's along-axis shift = dot(worldXZ, axis) * (streakAlong - 1) grew with
                    // ABSOLUTE world distance; since streakAlong animates with refluxAge, that
                    // shift swept the pattern under the fragment faster the further the beach sat
                    // from the origin - the "weird distortion" in the swash foam. Anchoring the
                    // pivot to the field centre bounds the pivot distance to the field's own
                    // extent, so the stretch stays a local reshape everywhere on the coast.
                    // Gradients (swashDdx/swashDdy below) are differences, so they are
                    // origin-invariant and already correct - only this mapping carried the bug.
                    float2 streakLocalXZ = i.largeWaveSourceXZ - _ShoreDepthCenter.xy;
                    float2 swashXZ = i.largeWaveSourceXZ + streakAxis
                        * (dot(streakLocalXZ, streakAxis) * (streakAlong - 1.0));
                    float2 swashDdx = foamWorldDdx + streakAxis
                        * (dot(foamWorldDdx, streakAxis) * (streakAlong - 1.0));
                    float2 swashDdy = foamWorldDdy + streakAxis
                        * (dot(foamWorldDdy, streakAxis) * (streakAlong - 1.0));
                    float swashDist = distance(i.largeWaveSourceXZ,
                                               _WorldSpaceCameraPos.xz);
                    float3 swashPattern = SampleOceanWhitecapPatternTiled(
                        swashXZ, swashDist, max(_SurfFoamTileSize, 1e-3),
                        swashDdx, swashDdy);
                    // Same shared law as the whitewash (FoamDissolve), plus the
                    // reflux hole-erosion: age raises the dissolve threshold, so
                    // the stranded line rots into lace patches, then filaments.
                    float swashFoam = FoamDissolve(swashPattern.r, swashCoverage,
                                                   _SurfFoamFeather,
                                                   refluxAge * _SurfSwashFoamDissolve
                                                   * SURF_SWASH_ERODE_MAX);
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
        // own screen position, so the seamless water-sky join is preserved. Degenerate
        // azimuth (looking straight down) or a horizon behind the camera (w ~ 0) falls back
        // to the behind-pixel sample - haze is negligible in those poses anyway. Explicit-LOD
        // sample, so the per-pixel UV selection is WGSL-safe. (The SH ambient probe was tried
        // and rejected: unity_SHAr..SHC are never bound for this CGPROGRAM pass under URP
        // Forward+ - zeros, far water faded to BLACK - the same per-object-binding failure
        // as unity_SpecCube0, see SampleSkyEnvironmentGrad.)
        #define HORIZON_AZIMUTH_MIN 1e-4   // below this the view ray is straight down; no azimuth
        float3 skyAtHorizon;
        if (_RealRefraction > 0.5)
        {
            float azimuthLen = length(incomingRay.xz);
            float3 horizonDir = float3(incomingRay.x, 0.0, incomingRay.z)
                              / max(azimuthLen, HORIZON_AZIMUTH_MIN);
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
            float edgeMinY = min(horizonUVraw.y, 1.0 - horizonUVraw.y); // >0 = horizon vertically in frame
            float toCentre = 1.0 - smoothstep(0.0, HORIZON_EDGE_BLEND, edgeMinY);
            // Degenerate azimuth (straight down) or horizon behind the camera: fully the centre band.
            if (azimuthLen <= HORIZON_AZIMUTH_MIN || horizonClip.w <= SCREEN_UV_MIN_W) toCentre = 1.0;
            // Horizontally BLUR the per-azimuth sample: each water column samples ONE horizon point,
            // so a single skybox texel would STRETCH straight down the column as a vertical line. The
            // haze wants the broad horizon COLOUR, not its texels, so average a few taps across x
            // (5-tap, weights sum to 1). Widen HORIZON_BLUR_STEP if the skybox texels read coarse.
            // centreBand is x-fixed (uniform), so it needs no blur.
            #define HORIZON_BLUR_STEP 0.006  // UV x-offset per blur tap
            float2 huv = saturate(horizonUVraw);
            float2 hb1 = float2(HORIZON_BLUR_STEP, 0.0);
            float2 hb2 = float2(2.0 * HORIZON_BLUR_STEP, 0.0);
            float3 perAzimuth =
                  tex2Dlod(_CameraOpaqueTexture, float4(huv, 0, 0)).rgb * 0.34
                + tex2Dlod(_CameraOpaqueTexture, float4(saturate(huv + hb1), 0, 0)).rgb * 0.24
                + tex2Dlod(_CameraOpaqueTexture, float4(saturate(huv - hb1), 0, 0)).rgb * 0.24
                + tex2Dlod(_CameraOpaqueTexture, float4(saturate(huv + hb2), 0, 0)).rgb * 0.09
                + tex2Dlod(_CameraOpaqueTexture, float4(saturate(huv - hb2), 0, 0)).rgb * 0.09;
            float3 centreBand = tex2Dlod(_CameraOpaqueTexture,
                                         float4(0.5, saturate(horizonUVraw.y), 0.0, 0.0)).rgb;
            skyAtHorizon = lerp(perAzimuth, centreBand, toCentre);
        }
        else
        {
            // Tiers without the opaque texture keep the reflection-cube fallback
            // (uniform gate, implicit derivatives allowed here).
            skyAtHorizon = SampleEnvironment(incomingRay);
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
        outColor = lerp(outColor, SampleEnvironment(incomingRay), horizonFade);
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
