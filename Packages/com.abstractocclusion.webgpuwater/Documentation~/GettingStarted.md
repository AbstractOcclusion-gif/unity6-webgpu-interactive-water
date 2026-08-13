# WebGpuWater — Getting Started

**Version 1.0.0** | Unity 6 (6000.3.9f1+) | URP 17+ | Desktop · WebGPU/WebGL · Mobile

Support: abstractocclusion@outlook.com

---

## Requirements

- **Unity 6 (6000.3.9f1) or newer.** This is a hard requirement, not a preference: the runtime
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

### URP Renderer Features

The package's custom render passes are added to the **URP Renderer Data asset**, not to
the URP asset above. Select the Renderer Data used by the water camera, choose
**Add Renderer Feature**, and add the features needed by your scene:

- **WaterUnderwaterFogFeature** — underwater fog while the camera is submerged.
- **WaterCausticProjectionFeature** — screen-space caustics on non-water surfaces.
- **WaterChunkDepthFeature** — mesh-footprint water chunks.
- **WaterExclusionDepthFeature** — mesh-shaped exclusion volumes.
- **LargeBodyAtmosphereFeature** — ocean god-ray shafts.
- **WaterSkyFogFeature** — Unity scene fog on the skybox.

The Water Wizard's **Utilities > Renderer Setup** section can install all six features on
the active URP asset's default Renderer Data and assign their seven shaders automatically.
It adds only missing features, fills only empty shader fields, and preserves custom shader
assignments. You can still add them manually with **Add Renderer Feature**. Cameras using a
Renderer override need the same setup on that Renderer Data asset. These features are
optional unless you use their corresponding effect; real refraction itself requires
**Opaque Texture** and **Depth Texture** on the active URP asset.

## Install

1. Add the package (Package Manager > Install from disk/tarball, or via your registry).
2. Open **Package Manager > AbstractOcclusion.WebGpuWater > Samples** and import
   **Demo Scenes** to try it immediately, or:

## Quick start — Water Wizard

**Window > AbstractOcclusion > WebGpuWater > Water Wizard** is the single authoring window.
It builds a complete water body in your scene. Immutable meshes, textures, sky, and the
default quality policy come from the package's `Runtime/Defaults` folder. Each new water
gets independent editable materials under `Assets/WebGpuWater/Waters/<Water Name>/` and
shares the editable project foam profile in `Assets/WebGpuWater/Profiles/`.

### Wizard asset workflow

1. Choose the water type, size, and optional features.
2. Press **Create Water**.
3. The Wizard creates a uniquely named project folder for that water:
   `Assets/WebGpuWater/Waters/Water`, then `Water 1`, `Water 2`, and so on.
4. That folder owns the water's editable materials. Changing those materials does not
   change another Wizard-created water.
5. The first water also creates
   `Assets/WebGpuWater/Profiles/DefaultFoamProfile.asset`. Later waters reuse this
   shared editable foam profile, so foam-profile changes intentionally affect every
   water linked to it.
6. Meshes, default textures, sky, and the default quality policy remain package-owned
   under `Runtime/Defaults`; the Wizard references them instead of copying them.

> **Folder notice:** every press of **Create Water** creates a new `Water`, `Water 1`,
> `Water 2`, etc. folder, even when the scene already contains another water. Delete an
> unwanted water's scene objects and its corresponding folder together. Do not edit assets
> under `Packages/com.abstractocclusion.webgpuwater/Runtime/Defaults`; package updates may
> replace them.

Press Play: click/drag the surface for ripples, drop a Rigidbody with `WaterBuoyancy`
into the pool and it floats, rocks, and rides the wind waves.

## Wind-driven ambient sea state

For an open-water body, **Motion > Ocean Sea State > Wind Drives Ambient Sea State** turns
the single Wind Speed control into an opt-in local-weather control. It drives the FFT wind
sea, small wind-wave layer, detail normals, and wind-gated whitecaps together. Wakes, impact
ripples, and remote swell remain independent.

The authored **Significant Wave Height** and **Peak Wavelength** are the exact sea you get at
**Reference Wind Speed**. For example, to author a raging 15 m/s sea: set Wind Speed and
Reference Wind Speed to 15 m/s, then tune height and wavelength. Lower wind speeds scale that
local wind sea down; 0 m/s makes it flat.

Swell is deliberately not driven by this option: a calm local wind can still carry long waves
from a distant storm. For runtime weather, animate Wind Speed gradually because changing the
FFT wind-sea size refreshes its spectrum.

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

### Wake and splash choice

- Use **WaterSphereInteractor** for a boat or moving floater's continuous directional wake.
  Its **Vertical Force Cap** limits a plunge/heave disturbance without weakening the travelling
  horizontal wake.
- Use **WaterSplash** for a one-time Rigidbody entry splash.
- Use **WaterBreachSplash** only for repeated surface crossings such as projectiles, fish, or
  diving birds. It uses live GPU-height readback and is not needed for a boat wake.

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
