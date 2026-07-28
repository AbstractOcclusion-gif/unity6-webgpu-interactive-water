# WebGpuWater — Getting Started

**Version 1.0.0** | Unity 6 (6000.0+) | URP 17+ | Desktop · WebGPU/WebGL · Mobile

Support: abstractocclusion@outlook.com

---

## Requirements

- **Unity 6 (6000.0) or newer.** This is a hard requirement, not a preference: the runtime
  uses `Rigidbody.linearVelocity` and the URP 17 RenderGraph pass API with no version guards,
  so an older Unity will not compile the package.
- **URP 17+** for rendering (declared as a package dependency, so it installs with the
  package). The base runtime assembly compiles without URP installed;
  URP-only code (planar reflections) activates automatically via the `WEBGPUWATER_URP`
  define — no manual Scripting Define needed.
- On your **active URP asset**, enable:
  - **Depth Texture** — required for SSR and screen-space refraction.
  - **Opaque Texture** — required for real refraction.
  - **Transparent Receive Shadows** — required for god-ray shadow shafts
    (they render in the transparent queue; with this off the shafts vanish).

## Install

1. Add the package (Package Manager > Install from disk/tarball, or via your registry).
2. Open **Package Manager > AbstractOcclusion.WebGpuWater > Samples** and import
   **Demo Scenes** to try it immediately, or:

## Quick start — Water Wizard

**Window > AbstractOcclusion > WebGpuWater > Water Wizard** is the single authoring window.
It builds a complete water body in your scene: simulation volume, surface renderers,
splash emitter, quality asset, and a tweakable material saved into your project
(`Assets/WebGLWater/Generated`). The package itself stays read-only — everything the
wizard writes lands in your `Assets/` folder, editable and yours.

Press Play: click/drag the surface for ripples, drop a Rigidbody with `WaterBuoyancy`
into the pool and it floats, rocks, and rides the wind waves.

## Core components

- **WaterVolume** — one per water body. Owns the ripple sim, look settings
  (fog, foam, depth darkening, wind waves, reflections), and the gameplay API.
  Multiple bodies coexist; mark exactly one as primary.
- **WaterBuoyancy** — add to any Rigidbody + Collider. Multi-point sampling gives
  buoyancy, righting torque, drag, and wave drift.
- **WaterInteractable** — add to any Renderer that should disturb the surface
  (bobbing and wakes). `displaceScale` weights it per object.
- **WaterSplash / WaterSplashEmitter** — entry splashes: droplet burst that settles
  and drifts on the live surface, plus an optional flipbook crown.
- **WaterFoamParticles** — GPU-resident foam/spray particles spawned from the foam
  sim (compute spawn, procedural quads, zero readback).
- **WaterQuality** (asset) — High/Medium/Low cost tiers with an automatic hardware
  probe. Low targets WebGPU/mobile: reduced sim/caustic resolution, render scale,
  update intervals, and particle caps.
- **WaterProbe / WaterRippleEmitter / WaterMembership** — sampling, scripted ripple
  emission, and explicit body association for gameplay objects.

## Scripting quick reference

```csharp
using AbstractOcclusion.WebGpuWater;

WaterVolume water = WaterVolume.Primary;              // or BodyContaining(position)

water.TryGetWaterHeight(x, z, out float surfaceY);    // live rippled height
water.AddRipple(x, z, radius, strength);              // inject a ripple
water.TrySampleSubmersion(point, out float depth,
                          out Vector3 up, out Vector3 flow);

// Runtime look/behavior:
water.Foam = true;
water.WindWaves = true;
water.WaterFog = true;
water.Reflections = WaterVolume.ReflectionMode.SSR;   // SkyOnly / SSR / Planar
water.RippleStrength = 0.03f;
```

All other tuning lives in the inspector (fully tooltipped). Anything not exposed as a
property is intentionally internal — tune it on the component, not from script.

## WebGPU / WebGL notes

- The simulation is compute-based; WebGL builds require **WebGPU**. Devices or
  browsers without WebGPU get a clear error message instead of a crash.
- Verified on real hardware: ~30 fps on entry-level phones/tablets (Honor X6,
  Redmi Pad SE) and 30+ fps on Samsung Galaxy A17 with foam, caustics, and god
  rays enabled on the Low tier.
- The sim is frame-rate independent: wave speed is identical in a 30 fps build and
  a 144 fps editor session.
- Hosting tip: if you version your deployed builds behind long-lived
  `Cache-Control: immutable` headers, deploy each build to a new folder — the
  browser (and Unity's IndexedDB cache) will happily serve the old build forever.

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| God-ray shafts invisible | Enable **Transparent Receive Shadows** on the URP asset. |
| No refraction / SSR | Enable **Depth Texture** and **Opaque Texture** on the URP asset. |
| Water looks blocky in a build | You are on a device where float32 filtering is unavailable; the package handles this automatically — make sure you are on 1.0.0+. |
| Nothing floats | The object needs a Rigidbody, a Collider, and `WaterBuoyancy`; the scene needs a `WaterVolume` whose footprint contains it. |
| Ripples too fast/slow | Tune `waveSpeed` / `stepsPerFrame` on the WaterVolume (60 fps reference — identical in builds). |

---

*WebGpuWater v1.0.0 — 2026 Abstract Occlusion — abstractocclusion@outlook.com*
