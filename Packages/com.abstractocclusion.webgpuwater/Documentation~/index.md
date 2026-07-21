# WebGpuWater — Documentation

**Version 1.0.0** | Unity 2022.2+ | URP 12+ | Desktop · WebGPU/WebGL · Mobile

Support: abstractocclusion@outlook.com

---

GPU water for URP: interactive ripple simulation, two-way buoyancy, surface + edge
foam, GPU foam particles, caustics, god rays, and hybrid planar/SSR/sky reflections —
authored from a single window (**Window > AbstractOcclusion > WebGpuWater > Water
Wizard**). A modern URP port and expansion of Evan Wallace's
[WebGL Water](https://madebyevan.com/webgl-water/) (MIT).

## Where to start

- **[Getting Started](GettingStarted.md)** — requirements, install, the Water Wizard,
  core components, the scripting API, and troubleshooting. Read this first.
- **[Particle & Foam System](ParticleSystem.md)** — how foam and spray spawn: the
  event → simulation → particles chain, the GPU foam pool vs. the Shuriken splash/crown
  (and which is a fallback), the spawn decision, timing, and every tuning knob. Illustrated.
- **[WebGpuWater — Complete Documentation (PDF)](WebGpuWater_Documentation.pdf)** — the
  full system reference: architecture, every module in depth (simulation, waves & FFT
  ocean, buoyancy, foam, rendering/optics, the surface shader, shorelines/exclusion/chunks,
  authoring), plus engineering notes and troubleshooting.
- **[WebGpuWater — API Reference (PDF)](WebGpuWater_API_Reference.pdf)** — the public
  scripting surface symbol by symbol: `WaterVolume`, the height-query seam, components,
  ScriptableObjects, and key shader uniforms.
- **Quality tiers & mobile preview** — below.

## Quality tiers & visual tuning

The **WaterQuality** asset ships three cost tiers — **High**, **Medium**, **Low** —
selected automatically by a hardware probe, or forced manually. Each tier changes the
things that cost GPU time: simulation and caustic resolution, render scale, god-ray
step count, wind-wave count, refraction, mesh detail, update intervals, and
foam-particle caps.

Because those resolutions and scales differ from tier to tier, **the High and Low
tiers usually need different visual-tuning values to look correct**. A look dialed in
at High — ripple radius and strength, foam thresholds and feather, wave amplitude, and
similar surface settings — can read too strong, too weak, or too coarse at Low, where
the sim runs on a smaller grid and lower render scale. Treat per-tier tuning as
expected, not as a bug: set the look you want on the tier you are targeting.

> **To preview what will actually render on mobile, set the Quality asset to Force
> Low.** Mobile devices run the Low tier, so forcing Low in the editor is the only way
> to see the resolution, render scale, and particle caps your phone/tablet build will
> use. Tuning on High and shipping to a Low-tier device will not match.

### What each tier actually changes

The three tiers are immutable `WaterQuality.Tier` presets. `Auto` resolves them with a
hardware probe: **Low** on WebGL/WebGPU/mobile or any device without async GPU readback,
**Medium** on desktops below the mid-range VRAM threshold, and **High** otherwise. You can
also force a tier for testing. Exactly which knobs move, and to what:

| Setting | High | Medium | Low | What you see |
| --- | --- | --- | --- | --- |
| Sim resolution | 256² | 128² | 128² | Ripple grid fineness — coarser ripples at Low |
| Caustic resolution | 1024² | 512² | 256² | Sharpness of the floor caustics |
| Caustic interval | every frame | every frame | every 2nd | Caustic update rate |
| Render scale | 1.0 | 1.0 | 0.7 | Overall image resolution (upscaled at Low) |
| God-ray steps | 24 | 16 | 12 | Shaft smoothness — god rays stay **on** at every tier |
| Wind-wave count | 32 | 12 | 8 | Richness of the ambient wave spectrum |
| Refine steps | 5 | 3 | 2 | Surface peaked-refinement (per-pixel fetches) |
| Rich reflections | on | on | **off** (SkyOnly) | SSR/planar allowed; Low falls back to the sky |
| Real refraction | on | on | **off** | Screen-space refraction vs. the analytic pool look |
| Underwater fog | Full | Full | Simple | Per-pixel wavy waterline vs. a flat closed-form one |
| Foam-particle cap | 65 536 | 65 536 | 1 024 | Live GPU foam/spray particle budget |
| Mesh detail | authored | authored | 100 | Low rebuilds the surface grid at a fixed detail |

The visible consequences of these differences are why the two ends of the range need
their **own** tuning. At Low the sim grid is coarser and the frame is rendered at 0.7×
scale, so a ripple radius or foam feather that looks crisp at High reads soft or blocky;
reflections drop to sky-only and refraction to the analytic pool, so water tuned around
real SSR/refraction can look flat. Dial the look on the tier you ship to.

> To preview the Low look on desktop, set the Quality asset's selection to **Force Low**
> — it applies the same resolutions, render scale, reflection fallback, and particle cap
> a phone/tablet build will use. (Drop a side-by-side High/Low capture here once you have
> one; the numbers above are the ground truth in the meantime.)

## Support & license

abstractocclusion@outlook.com · SEE LICENSE IN LICENSE.md

---

*WebGpuWater v1.0.0 — 2026 Abstract Occlusion*
