# WebGpuWater — Features Roadmap / Wishlist

*Started 2026-07-20, post publish-ready pass. Nothing here is committed or authorized to code — this is the backlog to discuss and prioritize. `[B]` = Bert's idea, `[C]` = Claude's suggestion. Check a box when we lock it in.*

The guiding spirit: this is a **free** Crest/KWS-level URP water (Evan Wallace lineage), so every addition should punch above what a free asset usually offers, and should reuse the pipelines we already have rather than bolt on new ones.

---

## 1. Cheap, high-wow, reuse-heavy
Quick wins that lean almost entirely on systems already shipped (ripple sim, GPU foam-particle pool, splash path).

- [ ] **Underwater particles** `[B]` — suspended dust / marine-snow motes drifting in the fog volume; caustic-lit for extra pop. Reuses the fog + god-ray + particle stack. *Adjacent to the "bubbles" showcase station already noted.*
- [ ] **Bioluminescent plankton** `[B]` — night-time glow particles that light up on disturbance (wake / footstep / hand drag). Reuse the GPU foam-particle spawn-from-flow pipeline with an additive emissive pass; disturbance signal already exists (sphere-interactor step + sim velocity).
- [ ] **Rising bubbles** `[C]` — bubble columns from submerged vents / sinking objects; foam-particle pool with an upward buoyant integrator. Already earmarked as a showcase station.
- [ ] **Rain system** `[C]` — procedural rain ripples stamped into the sim (`AddDrop` at random texels) + entry splashes + surface-wetness darkening. Classic wow, all-reuse.
- [ ] **Debris** `[B]` — floating detritus / leaves / planks riding flow + waves. Buoyancy + membership already float objects; needs a lightweight instanced-floater system + coupling to the current field (below).

## 2. Systems (bigger — each unlocks several features)

- [ ] **Currents / flow field** `[B]` — a vector field that advects foam & debris and pushes buoyant objects. Foam already advects along surface flow; needs an authored/sampled current feeding foam advection **and** buoyancy drift + a rigidbody push force.
- [ ] **Flow-map painting** `[C]` — one authored vector-field primitive (paint or spline) that **currents, debris, rivers, and wind maps all sample**. Build this once instead of three separate systems.
- [ ] **FFT wave/current coupling (P5)** `[B]` — **deferred: waiting for full current harmonization.** Do not implement this as an FFT-only flow-map warp. First establish one persistent world-space current contract shared by foam/debris advection, buoyancy/body drift, interactive state, and river authoring. Once that moving-medium authority exists, the FFT ocean may consume it for phase-continuous Doppler advection and a separately reviewed, bounded wave-action response. Dependency order: shared current field → current consumers/body coupling → river integration and boundaries → FFT Doppler → wave-action response.
- [ ] **Wind maps for lakes** `[B]` — spatially varying wind (gusts, sheltered coves, cat's-paws) modulating the wind-wave bank + foam generation per region, instead of one global wind vector.
- [ ] **Cheap FFT waves on lakes** `[B]` — a single low-res cascade FFT for mid bodies (real spectral chop without full-ocean cost). The interface already exists (`_OceanFftActive` swaps the large-wave body); this is a "lite" config + gating on lakes.
- [ ] **Water wave solver / baker** `[B]` — offline bake of the wave/surf field to a VAT/texture "cassette" for perf + mobile. *Same track as the earlier `wave-bake-tool-idea` (SPH→particle-VAT sketch) — fold them together.*
- [ ] **Wading / swimming controller** `[C]` — fills a known gap: buoyancy has no shallow-water / ground-clearance term, `SampleHeight` returns surface only. A char-controller helper (wade drag, swim buoyancy, shallow clamp) makes it gameplay-turnkey.
- [ ] **High-level gameplay event API** `[C]` — enter/exit-water events + a clean façade over internals (README roadmap item; `WaterProbe` is the seed). Turnkey for game integrators.

## 3. Coastline polish (fills known gaps)

- [ ] **Wet-sand darkening + wrack line** `[C]` — known gap: nothing writes to the terrain today (bed is read-only, only tints the water). Darken wet sand and leave a receding foam/wrack line as swash retreats.
- [ ] **Tidal / flood level animation** `[C]` — animate water level over a region (tide, rising flood). `WaterChunkFillAnimator` already animates chunk fill — generalize to bodies.
- [ ] **Waterfalls / cascades + mist** `[C]` — falling-water ribbon + base mist/spray; extends the spray path and feeds ripples into the receiving body.

## 4. Stretch / exploratory

- [ ] **River scaffolding** `[B]` — spline-authored flowing water with directional current, rapids, weirs. Big; leans on currents + flow-maps + the volume-frame authoring.
- [ ] **Boat wake TRAIL persistence** `[C]` — a decaying Kelvin-wake texture behind fast movers (beyond the live dipole) so wakes linger.
- [ ] **Ice / frozen-water variant** `[C]` — a freeze state / frozen-surface shader for seasonal water.
- [ ] **Underwater screen droplets / lens splash** `[C]` — camera-lens droplet decals on surfacing. Cheap polish.
- [ ] **Luminex fog integration** `[C]` — the asset-store copy already teases integration with your Luminex volumetric fog; underwater + volumetric-fog interop is a natural cross-sell.

---

## Notes / open questions

- *Dependency chain worth respecting:* **flow field / flow-map first** → it unlocks debris, rivers, and (partly) wind maps cleanly. Building debris or rivers before the current primitive risks a throwaway.
- *Which two or three to lead with?* My instinct for max "wow per hour": **rain + underwater particles + plankton** (all reuse, all visible in a demo), then tackle the **flow field** as the first real system.
- Add your own ordering / cuts below.
