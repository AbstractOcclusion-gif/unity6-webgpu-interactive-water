# WebGL Water — Unity 6 / URP port

A faithful port of Evan Wallace's [WebGL Water](https://madebyevan.com/webgl-water/)
(MIT licensed) to Unity 6 + URP, using a compute-shader heightfield simulation and
the original in-shader ray-tracing for reflection, refraction, caustics and shadows.

## What's here

```
Assets/WebGLWater/
  Shaders/
    WaterSim.compute     GPU heightfield: Drop / Update / Normal / Sphere kernels
    WaterCommon.hlsl     shared ray-trace helpers (intersect cube/sphere, wall & sphere shading)
    Caustics.shader      projects the water grid onto the floor -> caustic + shadow texture
    WaterSurface.shader   the water surface (reflection/refraction; above & under variants)
    PoolWall.shader      analytic pool walls/floor with caustics (optional)
    WaterSphere.shader   the draggable ball
  Scripts/
    WaterSimulation.cs   ping-pong RenderTextures + compute dispatch
    WaterController.cs   sim loop, caustics, sphere physics, mouse input, orbit camera
  Editor/
    WaterSceneBuilder.cs one-click scene assembly
```

## Build it

1. Let Unity import the folder (no console errors expected).
2. Menu **Tools ▸ WebGL Water**:
   - **Build Scene (water only — keep my pool)** ← use this, since you already made a pool.
   - **Build Scene (with analytic pool)** — also generates a procedural pool that
     receives caustics, if you want the original look out of the box.
3. Press **Play**.

The builder creates the meshes, a procedural sky cubemap (and a fallback tile
texture) under `Assets/WebGLWater/Generated/`, wires up the materials, the camera
and a `Water Controller` object.

### Using your own pool & tiles
The water surface ray-traces an **analytic** pool defined in normalized space:
floor at `y = -1`, walls up to `y = 2/12`, spanning `x,z ∈ [-1, 1]`. For the
reflections/refractions to match the pool you built, keep your pool at those
dimensions and assign your pool **tile texture** to the `Water Controller ▸ Tiles`
field. Everything is in plain Unity units (1 unit = the demo's unit).

## Tuning (Water Controller inspector)
- **Wave Speed** (0.1–2.0) — propagation stiffness. Higher = faster, livelier
  waves. The scheme is only stable up to ~2.0; past that it explodes.
- **Damping** (0.90–1.0) — how fast ripples die out. 0.995 ≈ original; lower it
  for choppier, shorter-lived waves, raise toward 1.0 for a glassy pool.
- **Steps Per Frame** (1–8) — simulation sub-steps. More steps = faster effective
  propagation and a more stable surface (at more GPU cost).
- **Ripple Strength** — height a click/drag adds (deformation intensity).
- **Ripple Radius** — size of a click/drag ripple.
- **Caustic Smoothness** (0–4) — Gaussian blur radius on the caustic texture.
  0 = original crisp look; ~1–2 softens it nicely.

> Tip: "calm pond" = waveSpeed ~1.0, damping ~0.99, steps 2.  "energetic" =
> waveSpeed 2.0, damping 0.997, steps 3–4, higher ripple strength.

## Controls (same as the original)
- **Drag on the water** — make ripples.
- **Drag the ball** — moves it and displaces water.
- **Drag the background** — orbit the camera.
- **Space** — pause/resume the simulation.
- **G** — toggle sphere gravity/physics.
- **L** (hold) — point the light along the camera view.

## Likely in-editor tweaks
I couldn't run Unity while writing this, so a couple of things may need a quick
adjustment once you see it:

- **Water invisible or inside-out:** flip the `_Cull` field on the
  `WaterAbove`/`WaterUnder` materials (Front ↔ Back). The two are intentionally
  opposite. Same for the `Pool`/`Sphere` materials if a surface looks inverted.
- **Caustics mirrored:** Direct3D flips render-target Y vs OpenGL. If the caustic
  pattern looks flipped in Z, negate the `z` term where `Caustics.shader` outputs
  `o.pos` (use `-0.75 * (...)` on the second component), or flip the read in
  `WaterCommon.hlsl`.
- **Colors look washed out / too dark:** the original is tuned for gamma space.
  If your project is in Linear color space it'll differ slightly; tweak
  `ABOVEWATER_COLOR` / `UNDERWATER_COLOR` in `WaterCommon.hlsl` or switch
  Player ▸ Color Space to taste.
- **Input does nothing:** set Player ▸ **Active Input Handling** to *Both* (the
  scripts support new Input System and the legacy manager).
- **Light direction:** default is `normalize(2, 2, -1)`; change `lightDir` on the
  controller.

## How the simulation maps to the original
- `water.js` → `WaterSim.compute` + `WaterSimulation.cs` (RGBAFloat texture holds
  `height, velocity, normal.x, normal.z`; two textures ping-pong each step).
- `renderer.js` helper functions → `WaterCommon.hlsl`; the water/sphere/cube
  shaders → `WaterSurface`/`WaterSphere`/`PoolWall`; `updateCaustics` →
  `Caustics.shader` drawn with `CommandBuffer.DrawMesh` into a 1024² RT.
- `main.js` interaction/camera/physics → `WaterController.cs`.
