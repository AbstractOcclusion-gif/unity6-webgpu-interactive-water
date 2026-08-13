# AbstractOcclusion.WebGpuWater

GPU water for Unity URP: interactive ripple simulation, two-way buoyancy, surface +
edge foam, GPU foam particles, caustics, god rays, and hybrid planar/SSR/sky
reflections. Everything is authored from one window — the **Water Wizard**. A modern
URP port and expansion of Evan Wallace's
[WebGL Water](https://madebyevan.com/webgl-water/) (MIT).

**Version 1.0.0** | Unity 6 (6000.3.9f1+) | URP 17+ | Desktop · WebGPU/WebGL · Mobile

## Scope

Built primarily for **small and mid-size** water bodies — pools, ponds, small-to-mid lakes — where
the interactive ripple simulation and analytic wind waves do the heavy lifting.

**Large lakes and oceans are supported via a separate open-water path** (spectral/FFT waves with
their own whitecap foam, clipmap surface, underwater fog and god rays). Pick **Open Water / Ocean**
in the Water Wizard to use it. That path is newer than the pool path and is still being hardened —
treat the very large, fully opaque cases as a preview rather than a finished product.

**Unity Terrain support is experimental** — the bed-depth bake approximates a shoreline gradient
from a Terrain heightmap; full terrain integration is not there yet. Treat it as a preview.

## Requirements

- **Unity 6 (6000.3.9f1 or newer).** The runtime uses Unity 6 APIs (`Rigidbody.linearVelocity`)
  and the URP 17 RenderGraph render-pass API, so earlier Unity versions will not compile.
- **URP 17+** for rendering. The base runtime assembly compiles without URP installed;
  URP-only code activates automatically via the `WEBGPUWATER_URP` define.
- On your **active URP asset**, enable **Depth Texture**, **Opaque Texture** (SSR and
  refraction), and **Transparent Receive Shadows** (god-ray shafts).

## Install

Add the package via **Package Manager > Install from disk/tarball** (or your registry),
then open **Package Manager > AbstractOcclusion.WebGpuWater > Samples** and import
**Demo Scenes** to try it immediately.

## Quick start

**Window > AbstractOcclusion > WebGpuWater > Water Wizard** builds a complete water
body — sim volume, surface renderers, splash emitter, and editable materials. Configure
size and features, press **Create Water**, then **Play**. Each creation writes a new
`Assets/WebGpuWater/Waters/Water`, `Water 1`, `Water 2`, etc. folder for that water's
materials. All waters reuse the editable project foam profile at
`Assets/WebGpuWater/Profiles/DefaultFoamProfile.asset`; immutable meshes, textures, sky,
and quality defaults remain under the package's `Runtime/Defaults` folder. Drag on the surface for ripples; drop a Rigidbody with
`WaterBuoyancy` in and it floats, rocks, and rides the wind waves.

## Demo scenes

Import **Demo Scenes** from Package Manager to get the numbered sample catalog. The scenes
progress from the original and classic pools through lakes, underwater rendering, open water,
reflections, buoyancy, splashes, ocean, boats, chunks, exclusion volumes, catapult impacts, and
the animated city-night sky.

Sample-specific materials, lighting settings, and the shared post-processing Volume Profile are
contained in the imported sample. Canonical water textures, meshes, skies, profiles, and shaders
remain package-owned under `Runtime/Defaults`; sample scenes reference those assets directly.

## Quality tiers & mobile preview

The **WaterQuality** asset ships **High / Medium / Low** cost tiers (auto hardware
probe) that scale sim and caustic resolution, render scale, god-ray steps, wave count,
refraction, mesh detail, update intervals, and foam-particle caps.

Because those resolutions and scales differ per tier, **the High and Low tiers usually
need different visual-tuning values to look correct** — a look dialed in at High
(ripple radius/strength, foam thresholds/feather, wave amplitude) can read too strong,
too weak, or too coarse at Low. Tune per tier.

**To preview what will actually render on mobile, set the Quality asset to Force Low.**
Mobile runs the Low tier, so forcing Low in the editor is the only way to see the
resolution, render scale, and particle caps your device build will use.

## Documentation

Full docs — Getting Started, core components, scripting API, WebGPU/mobile notes, and
troubleshooting — open from **Package Manager > this package > View documentation**
(`Documentation~/index.md`).

## Support & license

abstractocclusion@outlook.com · SEE LICENSE IN [LICENSE.md](LICENSE.md)
