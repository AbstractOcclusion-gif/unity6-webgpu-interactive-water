// WaterSurface pass: analytic pool ray shading (deep-water in-scatter fallback,
// gradient-fed wall/caustic clones, GetSurfaceRayColor).
// Split out of WaterSurface.shader (SHADER-SPLIT-2) as VERBATIM moves - any
// behavior change here is a bug. Needs WaterSurfaceSpecular.hlsl
// (SampleEnvironmentGrad, _RealRefraction) and WaterSurfaceShadow.hlsl
// (WaterMainLightShadow). The WGSL derivative-uniformity comments in here are
// CONTRACTS - keep them glued to their functions.
#ifndef WATER_SURFACE_POOL_TRACE_INCLUDED
#define WATER_SURFACE_POOL_TRACE_INCLUDED

float _ProceduralPool; // 1 = this body draws the analytic/procedural pool (tiles); 0 = surface only

// Shade a WORLD-space ray: a DOWN ray refracts into the pool and samples the analytic
// floor/walls (the tiles seen THROUGH the water); an UP ray is a reflection and samples
// the environment only. Reflections never return the pool tiles - the floor is seen via
// refraction alone. The pool box is intersected in POOL space so rotation / non-uniform
// extent is handled exactly, while the environment uses the WORLD ray.
// Deep-water in-scatter for the refracted ray: the lit body colour (the crest SSS is added
// emissively after compositing, not here). The view direction is reconstructed from the camera
// to this fragment so the scatter phase tracks the real view.
float3 DeepWaterColor(float3 worldOrigin, float3 waterColor)
{
    float3 viewDirWS = normalize(_WorldSpaceCameraPos - worldOrigin);
    return WaterInscatterColor(viewDirWS, _LightDir, _SunColor, 0.0) * waterColor;
}

// WGSL derivative uniformity: GetSurfaceRayColor reaches the wall colour inside a PER-FRAGMENT
// (non-uniform) ray branch, so every tap here uses explicit gradients fed by the caller's hoisted
// floor-point derivatives. The caustic tap lives in WaterCommon's GetWallShadeSplit, which takes
// the same hoisted derivatives (one shared implementation - no drift-prone clone).
float3 GetWallColorShadowedGrad(float3 p, float causticShadow, float3 pDdx, float3 pDdy)
{
    float2 uv; float3 normal, tangent, bitangent;
    WallSurface(p, uv, normal, tangent, bitangent);
    // The wall UV is a per-face linear pick of two position components (WallSurface):
    // mirror the same face selection on the hoisted position derivatives.
    float2 uvDdx, uvDdy;
    if (abs(p.x) > POOL_WALL_FACE_EPS)      { uvDdx = pDdx.yz * 0.5; uvDdy = pDdy.yz * 0.5; }
    else if (abs(p.z) > POOL_WALL_FACE_EPS) { uvDdx = pDdx.yx * 0.5; uvDdy = pDdy.yx * 0.5; }
    else                       { uvDdx = pDdx.xz * 0.5; uvDdy = pDdy.xz * 0.5; }
    float causticTerm;
    float scale = GetWallShadeSplit(p, normal, pDdx, pDdy, causticTerm);
    float shade = scale + causticTerm * WALL_CAUSTIC_LEGACY_STRENGTH * causticShadow;
    return tex2Dgrad(_Tiles, uv, uvDdx, uvDdy).rgb * shade;
}

float3 GetSurfaceRayColor(float3 worldOrigin, float3 worldRay, float3 waterColor)
{
    // WGSL derivative uniformity: the down/up ray split below is per-fragment
    // (non-uniform) and BOTH sides sample textures (pool tiles/caustic, sky cube).
    // Hoist the screen derivatives of every sampling coordinate here, in uniform
    // control flow (both call sites branch only on uniforms), so the in-branch
    // samples can use explicit gradients. The floor point is computed for every
    // fragment purely so its derivative is well-defined; up rays never read it.
    float3 rayDdx = ddx(worldRay);
    float3 rayDdy = ddy(worldRay);
    float3 po = WorldToPool(worldOrigin);
    float3 pd = WorldDirToPool(worldRay);
    float2 t = IntersectCube(po, pd, POOL_BOX_MIN, POOL_BOX_MAX);
    float3 floorPool = po + pd * t.y;
    float3 floorDdx = ddx(floorPool);
    float3 floorDdy = ddy(floorPool);
    if (worldRay.y < 0.0)
    {
        // Open water has no pool floor to sample: return the deep-water inscattering
        // colour so the analytic refraction reads as "can't see the bottom" rather than
        // pool tiles. The _REAL_REFRACTION path (in frag) samples the actual scene where
        // geometry exists and overrides this; this is the no-geometry fallback.
        if (_LargeBody > 0.5)
            return DeepWaterColor(worldOrigin, waterColor);

        // Pool tiles only when this body draws the PROCEDURAL (analytic) pool AND real
        // refraction isn't already sampling the actual scene. Surface-only bodies (no pool)
        // and the real-refraction path fall back to the deep-water/fog colour, never tiles.
        if (_ProceduralPool < 0.5 || _RealRefraction > 0.5)
            return DeepWaterColor(worldOrigin, waterColor);

        // When the occluder pass is active (the normal case) the refracted object shadow is
        // already baked into the caustic green channel (caustic.r * caustic.g in
        // GetWallShadeSplit), so the un-refracted shadow map must NOT also apply - besides
        // double-shadowing, a hand-rolled tap at a DEEP floor can sample outside the cascade
        // range and read fully shadowed, deleting the caustics wholesale (the Deep Lake bug).
        // The shadow-map gate remains only for setups without the occluder shader wired.
        float causticShadow = (_CausticOccluderActive > 0.5) ? 1.0 : WaterMainLightShadow(PoolToWorld(floorPool));
        return GetWallColorShadowedGrad(floorPool, causticShadow, floorDdx, floorDdy) * waterColor;
    }
    return SampleEnvironmentGrad(worldRay, rayDdx, rayDdy);
}

#endif // WATER_SURFACE_POOL_TRACE_INCLUDED
