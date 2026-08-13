# WebGpuWater — Documentation

**Version 1.0.0** | Unity 6 (6000.3.9f1+) | URP 17+ | Desktop · WebGPU/WebGL · Mobile

Support: abstractocclusion@outlook.com

---

GPU water for URP: interactive ripple simulation, two-way buoyancy, surface + edge
foam, GPU foam particles, caustics, god rays, and hybrid planar/SSR/sky reflections —
authored from a single window (**Window > AbstractOcclusion > WebGpuWater > Water
Wizard**). A modern URP port and expansion of Evan Wallace's
[WebGL Water](https://madebyevan.com/webgl-water/) (MIT).

## Where to start

- **[Getting Started](GettingStarted.md)** — requirements, install, the Water Wizard,
  its per-water folder/shared-profile asset workflow, core components, the scripting API,
  and troubleshooting. Read this first.
- **[Authoring, Limitations, and Experimental Features](AuthoringLimitations.md)** — the
  practical guardrails: wind-driven sea state, GPU readback, wake/splash ownership, rendering
  boundaries, and what to validate before shipping.
- **[Feature Guide](FeatureGuide.md)** — the current system map: topology, waves, physics,
  interactions, foam, shore/exclusion, rendering, quality, and tooling.
- **[Particle & Foam System](ParticleSystem.md)** — how foam and spray spawn: the
  event → simulation → particles chain, the GPU foam pool vs. the Shuriken splash/crown
  (and which is a fallback), the spawn decision, timing, and every tuning knob. Illustrated.
- **[WebGpuWater — Complete Documentation (PDF)](WebGpuWater_Documentation.pdf)** — the
  full system reference: architecture, every module in depth (simulation, waves & FFT
  ocean, buoyancy, foam, rendering/optics, the surface shader, shorelines/exclusion/chunks,
  authoring), plus engineering notes and troubleshooting. The Markdown guides above are the
  authority for newly added features and current experimental-status notes while the PDF catches up.
- **[WebGpuWater — API Reference (PDF)](WebGpuWater_API_Reference.pdf)** — the public
  scripting surface symbol by symbol: `WaterVolume`, the height-query seam, components,
  ScriptableObjects, and key shader uniforms.
- **Quality tiers & mobile preview** — below.

The Package Manager **Demo Scenes** sample is a numbered catalog covering the original pool,
lake and terrain variants, underwater rendering, open water and ocean, reflections, buoyancy,
splashes, boats, chunks, exclusion volumes, catapult impacts, and the animated city-night sky.
Its editable demo materials and shared Volume Profile are sample-owned; immutable water defaults
remain under the package's `Runtime/Defaults` folder.

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
| FFT ocean interval | every frame | every frame | every 2nd | How often the ocean's FFT cascades refresh (unbounded oceans only) |
| Render scale | 1.0 | 1.0 | 0.7 | Overall image resolution (upscaled at Low) |
| God-ray steps | 24 | 16 | 12 | Shaft smoothness — god rays stay **on** at every tier |
| Wind-wave count | 16 | 12 | 8 | Richness of the ambient wave spectrum (16 is a hard engine cap) |
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

### What the Quality asset replaces, and what it only limits

A tier does not simply win everything. Four different relationships exist, and knowing which
one applies tells you whether your authored value still does anything.

**With no Quality asset assigned, none of this applies.** Every value authored on the
WaterVolume is used exactly as you set it.

| Relationship | Settings | What it means for your authored value |
| --- | --- | --- |
| **Replaced** | Caustic resolution | The tier value is used and yours is ignored outright. The field greys out in the inspector while an asset is assigned — clear the asset to author it per body. |
| **Capped** | Wind-wave count | The effective value is `min(yours, tier)`. Set it *below* the tier's cap and your value is what runs. |
| **Gated** | Screen-space reflections, Planar reflections, Real refraction | The tier decides whether the feature is *permitted*; your toggle still decides whether it is *used*. On Low all three are forbidden, so those toggles have no effect there. |
| **Tier-only** | Render scale, mesh detail, refine steps, foam-particle cap, underwater fog mode, and the caustic / readback / FFT update intervals | No authored counterpart exists — these live only on the Quality asset. |

> **Ocean shaft settings are not tier-driven.** Large God Ray Density, Steps, Anisotropy and
> Extinction are per-body values and the tier never touches them. Only the *pool* god-ray step
> count comes from the tier.

## Behaviour notes

Two things behave differently from what most people expect. Neither is a bug, and both will
cost you time if you meet them without warning.

### Caustic resolution cannot buy detail beyond the simulation

The caustic generator computes its focusing term per grid cell of the simulation, and writes
**one value per cell** — the maths behind it (an area ratio measured across each projected
triangle) is constant over a triangle by construction. So the caustic map's *information
content* is set by the **sim resolution**, not by the caustic resolution.

Raising Caustic Resolution above the sim resolution therefore adds no detail at all: each cell
is simply stored as a larger block of identical pixels. Below a certain window size you will
not notice, but on a wide ocean sim window those blocks become visible as hard pixelation — and
raising the resolution to fix it does nothing, because resolution was never the limit.

**To get finer caustics, raise Ripple Quality (the sim resolution), or narrow the sim window on
an ocean body.** The screen-space projection already compensates automatically: it samples the
map no finer than the grid genuinely resolves, so the blocks are filtered away rather than
magnified. The light shafts are unaffected — they read the sharp map, which is what gives them
their beam structure.

One map serves every surface that shows caustics: pool walls and floor, water receivers,
terrain and other foreign surfaces via the screen-space pass, and the volumetric shafts.

### Depth extinction does not darken Unity Terrain

Depth extinction (Volume tab) darkens things by how deep they sit below the surface. It is
applied by the water's own shaders and by the **WaterReceiver** shader — so a mesh converted to
a receiver darkens correctly as it goes deeper.

A Unity `Terrain` cannot use that shader. Terrain renders through Unity's own terrain pipeline
and is not a `Renderer`, so the receiver converter skips it, and **terrain never receives depth
darkening.** Seen from above the water, the water column above your terrain darkens with depth
while the terrain underneath it does not — which reads as a dark band or seam sitting over
shallow ground.

This is easy to miss on a pond, where the depth range is small. On an **ocean-scale** body the
same extinction value covers a much larger depth range, so the effect is dramatic. If you have
tuned extinction on a pool, expect to retune it — sometimes far lower — on an ocean with a
shallow terrain bed.

Options, in the order most people want them:

- Keep extinction modest on bodies over terrain, and use **Bed Colour & Clarity** for the
  depth-driven look instead — it is designed around a real bed and does not need the terrain to
  cooperate.
- Use mesh geometry converted with **Convert to Water Receiver** for the ground you care about,
  rather than a Unity Terrain.
- Accept the seam where the terrain is deep enough that the water above it is dark anyway.

## Support & license

abstractocclusion@outlook.com · SEE LICENSE IN LICENSE.md

---

*WebGpuWater v1.0.0 — 2026 Abstract Occlusion*
