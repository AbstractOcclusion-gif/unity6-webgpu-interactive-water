# Bubbles, spray & the wave-patch doubt — research synthesis

Date: 2026-08-01. Sources: physics/coastal-engineering literature, SIGGRAPH/GDC material,
KWS2 files **verified on your disk** (`KWSWater` project), and our own
`docs/PARTICLE_FOAM_INVENTORY.md`. Discussion only — no code was touched.

---

## 0. Scorecard on your four intuitions

| Your idea | Verdict |
|---|---|
| "Pool = few/no foam, lots of underwater bubbles" | ✅ **Textbook-correct physics.** Fresh vs salt water is a regime switch (§1) |
| "We miss a plunging spray particle for breaking waves" | ⚠️ **Half-right.** The *throw* exists (trigger 1c). What's missing is the *cascade* and the *bubble class* (§4, §5) |
| Rolling/plunge → crown on collision → plunge again → swash | ✅ **This is literally the measured physics** (Kimmoun & Branger: 2–4 splash-up cycles per breaker) (§5) |
| "Patch emission looks like jumping fishes / cheap foam → doubt" | ✅ **Doubt confirmed.** No verified shipped system emits sprites from open-ocean swell. Everyone does shader foam + near-field event particles (§6) |

And one answer up front: **flipbooks are mostly the wrong tool for bubbles.** KWS ships
*zero* water flipbooks — every "animated" effect is a static sprite animated in-shader (§2, §7).

---

## 1. Why your pool behaved like that (and why it matters for the asset)

The controlling variable is **bubble coalescence**, not the splash:

- **Salt inhibits coalescence.** Above ~50–250 mM NaCl, the film between two touching
  bubbles drains too slowly to merge (Marangoni effect). Seawater is ~600 mM — far above.
  A pool is effectively fresh (<10 mM). So ocean = clouds of *small, numerous* bubbles
  (white!), pool = bubbles merge into *larger, faster* ones that pop instantly on surfacing.
  (PNAS 2023 / Craig 1993.)
- **Foam persistence needs salt + organics.** Whitecap foam e-folding decay ≈ **3.5–4.5 s**
  in seawater; fresh water ≈ **0.65×** that, with far less foam produced at all. Individual
  fresh surface bubbles live well under a second; surfactant-laden seawater ~2× clean water.
- **Bubble plume depth ≈ 1× wave height** (field acoustics: 0.97–1.14 × Hs). Pool splash
  plume: ~0.5 m.
- **Rise speeds:** ~1–2 cm/s at 0.1 mm, **20–30 cm/s at 1–2 mm** (the visual bulk), ~30–40
  cm/s for cm-class caps. Below ~1.9 mm diameter bubbles rise *straight*; above that they
  **zigzag/spiral** — wobble is physical and size-gated.
- **Cloud timing:** the dense plume evolves on ~1 s; mm bubbles from 0.5 m surface in ~2–3 s;
  micro-bubble haze lingers much longer.

**Asset consequence:** "pool water" vs "ocean water" should differ by *regime*, not tint —
a fresh body wants bubbles-dominant splashes and near-zero deposited foam; the ocean wants
foam-dominant. One or two floats on the profile (bubble share, foam lifetime multiplier)
buy a physically-true distinction almost no competitor sells.

## 2. What KWS actually ships (verified file-by-file on your disk)

Checked `Assets/KriptoFX/WaterSystem2/WaterResources` — textures opened, shaders and
prefabs read:

- **Zero flipbooks anywhere in the water package.** `UVModule` (texture-sheet animation)
  is **disabled** in all bubble prefabs; enabled only on the splash Shuriken over a
  **4-tile variant sheet** (`WaterSplash.png`, 2048×512 = 4 × 512² channel-packed tiles) —
  variant selection, not animation.
- **Underwater bubbles = Shuriken + ONE static 256² sprite + shader wobble.**
  `KWS_UnderwaterBubbles.shader`: color/shine packed in R/G, alpha in A, 128² normal map,
  and the "life" comes from **scrolling Perlin noise distorting the sprite UVs**
  (`_NoiseStrength` scaled by bubble size and distance-to-surface). Lit, fogged by their
  volumetrics, `Queue = Transparent+2`.
- Their bubble shader has two smart guards worth stealing conceptually:
  **surface clamp** (a bubble reaching the waterline is pinned to `surface − size`, so it
  visually *arrives* at the surface instead of poking through) and **near-surface alpha fade**.
- **Their Shuriken numbers** (the tuning that "looks right"):

  | System | Max | Rate/s | Size | Life | Buoyancy | Wobble |
  |---|---|---|---|---|---|---|
  | UnderwaterBubbles (small) | 8000 | 700 | **1.7 cm** | 4 s | gravity −0.012 | noise 0.15 @ 4.5 |
  | UnderwaterBubblesBig | 1000 | 50 | 5 cm | 2.5 s | −0.015 | 0.1 @ 4 |
  | InfiniteUnderwaterBubbles | 5000 | 1500 | 6 cm | 4 s | 0 (drift) | 0.5 @ 2 |

  Gravity −0.012 → upward accel ≈ 0.12 m/s² → ≈ 0.2–0.4 m/s over a 4 s life.
  **They tuned into the physically-measured rise band (20–30 cm/s) — almost certainly by eye.**
- **"Infinite" ambient bubbles**: the emitter follows the camera (`KWS_Particles.cs`
  repositions it pre-render), and the shader tile-warps particles around the camera
  (`KWS_USE_TILE_WARP_PARTICLES`) → a boundless drifting-particulate volume from 5000
  particles. Their `UnderwaterParticles.png` is a 4×1 sheet of *gunk motes*, not bubbles.
- **No open-ocean particle emission.** Docs + quality settings confirm: FFT whitecaps are
  shader-side accumulated foam; splash/foam particles exist only inside the dynamic-waves
  sim (objects, effectors, waterfalls), distance-culled, with "Particle Scale" flagged by
  their own docs as "the most performance-critical parameter".

## 3. What the industry does (for the same three questions)

- **Sea of Thieves** (SIGGRAPH 2018): open-sea foam entirely in-shader (wave-peak +
  intersection foam in a camera-centered buffer, progressive blur for dispersal). No
  crest spray sprites.
- **Horizon Forbidden West** (SIGGRAPH 2022): breaking shoreline waves = **baked Houdini
  sim as vector-displacement flipbook strips** deforming the mesh — the wave *shape* is
  displacement, never a particle cloud. (Your FLIP baker is this exact pipeline.)
- **AC3/Black Flag**: FFT foam maps at shader level; spray is **event-driven** at the hull
  (depth probes along the gunnel — the same architecture as our `WaterSprayPump`).
- **Why big sprites read as fake:** a billboard has zero internal parallax; the bigger it is
  on screen the more the missing perspective cues register — plus sort-popping and
  grazing-angle flattening. That is your "jumping fishes" in one sentence.
- **Underwater bubbles in shipped games** (Subnautica-class): simple round additive or
  refractive/rim-fresnel sprites, wobble ∝ size, size-by-speed rise, alpha-out near
  surface. Nobody flipbooks them.
- **CG whitewater canon** (Ihmsen 2012 — the Houdini-whitewater model): one diffuse-particle
  pool, classified every step by neighborhood: **spray** (airborne, ballistic) / **foam**
  (on surface, advected, the only class with a lifetime) / **bubble** (submerged, buoyancy
  + drag). Classes *convert* into each other: spray lands → foam; bubble surfaces → foam
  (salt) or pops (fresh).

## 4. Where we actually stand (vs `PARTICLE_FOAM_INVENTORY.md`)

Our GPU pool already implements **two of Ihmsen's three classes** with the same
conversion logic: airborne mist/burst droplets (spray) that land and convert to
`KIND_SURFACE` foam with re-rolled life/size. What's missing is exactly one class:

- **No `KIND_BUBBLE`.** Nothing in the package goes *below* the surface. A splash burst
  (1d) throws droplets up; physically, the same impact drives a bubble plume *down* to
  ~1× the throw height. That's the entire pool-observation gap.
- **No surface-bubble visual.** Our deposited foam is flecks/veil; the "few drifting
  bubbles that pop" of fresh water don't exist.
- **Plunging spray**: trigger 1c already throws ballistic lip spray shoreward
  (`SURF_SPRAY_THROW_SPEED = 6`, plunge-amplified, surge-killed). So "a plunging spray
  particle" exists — what's missing is what happens when it **lands** (§5).

## 5. Your cascade idea vs the measured physics

Kimmoun & Branger (JFM 2007, PIV on beach plungers): the overturning jet strikes the front
face → the impact ejects a **splash-up jet** → that jet plunges at a second point → again —
**at least 4 successive splash-up/vortex cycles**, marching shoreward, until the bore
degenerates into swash. Peak void fraction at the first splash-up ≈ 0.88.

That is your "rolling/plunging spray that generates crown splash on collision then another
plunging/rolling until reaching swash", almost word for word. The idea is good.

Mapping onto what we already own (discussion, not commitment):

1. Lip throw = existing 1c ballistic spray (already shoreward-biased).
2. **Landing today silently converts to floating foam** (`.compute:730-744`). The cascade
   version: a landing above some energy threshold *also* queues a secondary, weaker burst
   (crown ring + droplets + **downward bubble plume**) at the landing site, with a
   generation counter (2–3 max) so it terminates by construction into swash foam.
3. The crown ring we already have (`WaterSplashEmitter.EmitCrown`) is the right visual for
   the collision ring — your "crown splash on collision" is already in the box.
4. "Shore wave choppiness" — the splash-ups are what visually roughen the surf zone; a
   cascade fed from 1c would put that chop where the sim says the breaker front is, not
   where an artist scattered emitters.

Caveat from the inventory: bursts route through `QueueSplashBurst` with
`MaxBurstsPerFrame = 16` and **overflow is dropped, not deferred** — a cascade multiplies
burst requests, so this cap is the first wall (same trap already flagged for the hull
petal spray).

## 6. The wave-patch emitters — your doubt is right

Verdict: **the industry consensus is against sprite emission on open-ocean swell, and the
failure mode you saw is the documented one.** Distant whitecaps belong to the water shader
(our FFT whitecap accumulation — which we have and which survives `crestFoamSpawn = 0`);
particles belong to *near-field events*.

That does **not** mean deleting 1b. It means re-scoping it from "emit over the patch" to
"garnish the shader whitecaps near the camera":

- keep `crestFoamSpawn` low/off by default; it is detail, not the foam itself;
- rely on `spawnMaxDistance` aggressively (tens of meters, not the patch);
- keep individual sprites **small** — the moment a sprite is sized "correctly for a big
  wave" it becomes a flat card with no parallax = jumping fish. Many small > one big,
  always;
- the far field is already handled: the whitecap channel drives the shader foam and the
  screen-space density veil.

KWS reaches the same architecture from the other side: they simply *have no* ocean-patch
particle path at all.

## 7. Flipbooks: where they pay and where they don't

| Use | Flipbook? | What instead |
|---|---|---|
| Underwater bubbles | **No** | Static round sprite (rim/fresnel or additive) + shader UV-noise wobble ∝ size + buoyant rise + near-surface clamp&fade. KWS-proven, near-free |
| Surface drifting bubbles | **No** (for the drift) | 2–4 static dome-cluster variants riding the surface like foam flecks |
| Surface bubble **pop** | Tiny one, maybe | 4–8 frame "dome → ring" burst at death; only visible close-up. This is the one new flipbook worth making |
| Crown splash | Have it | `SplashCrown` flipbook already ships |
| Breaking-wave *shape* | Different kind | That's the FLIP baker's job (HFW-style displacement / cassette), never sprites |

Where flipbooks would come from if/when we make them: **our own FLIP baker** — bake a small
bubble-burst or crown variant to an 8×8 sheet (Klemen Lozar motion-vector blending if we
want long smooth playback from few frames). No good free bubble flipbook source exists
(verified: Unity's CC0 VFX library has no water; CGHEVEN unconfirmed) — and KWS proves we
may not need one at all. So: can I create the flipbooks? Yes — but for the two bubble
systems the honest recommendation is static sprites + shader animation first; a pop
flipbook as a garnish increment after.

## 8. Proposed increment ladder (for discussion — nothing authorized)

- **B1 — `KIND_BUBBLE` in the GPU pool.** New kind in `WaterFoamParticles.compute`:
  spawned downward by 1d bursts (and optionally 1c landings), depth ≈ impact strength,
  buoyancy toward 20–30 cm/s, wobble above ~2 mm visual size, life ~2–4 s, kill at surface
  with class conversion: fresh → brief surface bubble, salt → foam fleck. Rendered as a
  third `_DrawKind` with a rim-fresnel round sprite. This is the pool-realism feature.
- **B2 — surface bubble sprite variants.** 2–4 static dome-cluster sprites as a deposit
  variant of `KIND_SURFACE`, short-lived in fresh water; optional 4–8-frame pop at death.
- **B3 — splash-up cascade.** Landing-triggered secondary bursts with generation counter
  (needs the `MaxBurstsPerFrame` overflow question answered first).
- **B4 — re-scope 1b defaults.** Lower `crestFoamSpawn` default, tighten
  `spawnMaxDistance`, document the "small sprites only" rule; the patch look is the shader
  whitecaps' job.
- **Regime knob** on the foam profile: fresh/salt (bubble share, foam lifetime ×0.65/×1,
  spawn-rate bias) — one enum, two floats, sells the pool story.

Ordering logic: B1 is the only structural change and unblocks B2/B3 visuals; B4 is pure
tuning; everything reuses the existing pool, burst plumbing, and crown.

---

## Sources (main)

- Physics: PNAS 2023 coalescence ([APS summary](https://physics.aps.org/articles/v16/155)); Craig 1993 salt thresholds ([arXiv](https://ar5iv.labs.arxiv.org/html/2007.10972)); [Harb & Foroutan 2022](https://acp.copernicus.org/articles/22/11759/2022/) fresh-vs-salt spray/foam; [Deane & Stokes 2002](https://pdodds.w3.uvm.edu/files/papers/others/2002/deane2002.pdf) bubble spectra/Hinze ~1 mm; [Kimmoun & Branger 2007](https://data-ww3.ifremer.fr/BIB/Kimmoun_Branger_JFM2007.pdf) splash-up cascade; [Veron 2015](https://bpb-us-w2.wpmucdn.com/sites.udel.edu/dist/b/10612/files/2020/12/Veron-2015-spray-annurev.pdf) spray classes; [NOAA whitecap decay](https://repository.library.noaa.gov/view/noaa/60518/noaa_60518_DS1.pdf); [bubble plume depth ≈ Hs](https://repository.library.noaa.gov/view/noaa/65649/noaa_65649_DS1.pdf); rise/wobble ([review](https://www.scielo.org.mx/scielo.php?script=sci_arttext&pid=S1665-27382012000200006), [PNAS path instability](https://people.maths.bris.ac.uk/~majge/pnas.2216830120.pdf)).
- CG: [Ihmsen 2012 unified spray/foam/bubbles](https://cg.informatik.uni-freiburg.de/publications/2012_CGI_sprayFoamBubbles.pdf); [Chentanez & Müller 2010](https://matthias-research.github.io/pages/publications/hfFluid.pdf).
- Industry: [Sea of Thieves tech art](https://history.siggraph.org/wp-content/uploads/2022/09/2018-Talks-Ang_The-Technical-Art-of-Sea-of-Thieves.pdf); [HFW water](https://advances.realtimerendering.com/s2022/SIGGRAPH2022-Advances-Water-Malan.pdf); [AC3 water](https://www.fxguide.com/fxfeatured/assassins-creed-iii-the-tech-behind-or-beneath-the-action/); [billboard failure modes](https://vfxdoc.readthedocs.io/en/latest/vfx/particlesystems/); [MV flipbooks](https://www.klemenlozar.com/frame-blending-with-motion-vectors/); [underwater bubble recipe](https://alastaira.wordpress.com/2014/10/07/underwater-effects/); [KWS2 docs](https://kripto289.gitbook.io/kripto289-docs/llms-full.txt).
- Local: KWS2 package files under `KWSWater/Assets/KriptoFX/WaterSystem2/` (textures, `KWS_UnderwaterBubbles.shader`, `KWS_Particles.cs`, prefab YAML); `ThreeJSWaterPort/docs/PARTICLE_FOAM_INVENTORY.md`.

Flagged as unverified: KWS Discord/site specifics beyond the docs dump; God of War internals; Subnautica implementation (observation only); CGHEVEN water inventory.
