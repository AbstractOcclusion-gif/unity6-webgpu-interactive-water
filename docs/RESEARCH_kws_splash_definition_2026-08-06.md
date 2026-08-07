# KWS splash particles — full decomposition, and why ours are less defined

**Date:** 2026-08-06 · **Method:** every claim below was verified on disk in the FlockReflections
project (KWS2 / WaterSystem2) — prefab YAML parsed, shaders read in full, textures channel-split
and inspected visually. Nothing is guessed.

**The realism reference:** `FishSplashEffect.cs` (your fish splash) instantiates KWS's stock
`WaterSplashes.prefab` / `WaterSplashes2.prefab` / `WaterSplashes_big.prefab` with random pick,
random scale 0.5–1.2, at the waterline. So "the level of realism I want" **is exactly the content
of those prefabs** — decomposed completely below.

---

## 1. The texture is the realism (the single biggest factor)

`WaterSplash.png` — 2048×512, **4 chunks of 512×512**, channel-packed, imported **linear**
(`sRGBTexture: 0`), mips on, standard BC compression. Every splash material in KWS uses this one
texture. `WaterSplash_0.png` is the same layout (softer variant) used by the GPU sim particles
(`KWS_SplashTex0`).

| Channel | Content | Verified appearance |
|---|---|---|
| R | splashMain — the mass | **Photographic real water splash**: droplet strands, filaments, lace, individual airborne drops. Extremely high-frequency detail. |
| G | splashShine — sparkle cores | Sparse bright droplet highlights (photo highlights only) |
| B | noise — erosion threshold | Soft cellular/soap-bubble pattern, unique per chunk |
| A | splashDepth — thickness | Heavily blurred envelope of the chunk |

The 4 chunks are 4 **different hero splash photographs/renders**, not an animation. There is no
64-frame simulation flipbook anywhere in KWS — this confirms and sharpens the 08-01 finding
("KWS on-disk: zero water flipbooks"). **Per-sprite resolution: 512 px. Ours: 128 px** (8×8 grid
in a 1024 sheet) — 16× fewer pixels per sprite, and our content is synthesized blobs from
`gen_splash_flipbook.py`'s crown-anatomy model, with no filament/strand structure. Side-by-side
at particle size, theirs reads as water, ours reads as dots. **This is the definition gap.**

## 2. The shader consumes it with erosion, not fade

`KWS_DynamicWavesSplash.shader` (Shuriken prefabs) and
`KWS_DynamicWavesSplashParticles.shader` (GPU sim pool) share the same fragment recipe:

```
lifeTime   = 1 - color.a                      // Shuriken colorOverLifetime drives age
noise      = saturate(noise - lifeTime*2 + 1) // burn threshold marches through B channel
main      *= noise;  shine *= noise
shine      = shine³                            // tight sparkle
alpha      = saturate(main*0.5 + main²*2 + shine) // contrast curve, no mushy mid-tones
color      = (float3(0.75,0.85,1)*0.9 + shine*3) * vertexLight
```

Key properties, all verified:

- **The sprite never alpha-fades — it disintegrates** through its own noise channel. Definition
  survives to the last frame.
- **Color is not in the texture.** A fixed water tint + lighting + shine×3. The texture carries
  only structure.
- **Soft particles against `min(waterDepth, sceneDepth)`**, and the fade band is modulated by
  `pow10(splashDepth)·5` — thin lace vanishes at intersections first, thick cores hold.
- **Particles are lit and shadowed** (`KWS_ComputeLighting`, directional shadows; per-vertex by
  default, per-pixel behind a toggle).
- **Particles CAST shadows**: a ShadowCaster pass does stochastic blue-noise discard from the
  splash alpha (fast mode casts from only ~30% of particles). A splash that darkens the water
  under it is a massive grounding cue we don't have.
- Underwater pixels are killed in the vertex shader (`o.pos.w = NAN`), queue `Transparent+1`.

## 3. The Shuriken construction — what a "KWS splash" actually is

`WaterSplashes.prefab` = **two overlapping systems**, one burst each, non-looping, 1.5 s:

**Layer A — vertical streaks** (the "throw"):
4–6 particles · cone 10°, point source · speed 0.5–2 · life 0.75–1.5 s · size 0.35–0.5 ·
**gravity 0** + velocity-damping on · renderer **Stretched Billboard, lengthScale 4** →
long vertical water columns.

**Layer B — droplet cloud** (the body): 40–50 particles · cone 20° · speed 0.75–6 ·
life 0.75–4 s · size 0.2–0.5 · **gravity modifier 1–1.5** · **Limit Velocity 0.25–1,
dampen 0.03** (air drag → arcs over and rains down) · rotation over life ±30°/s ·
Max Particle Size 0.2 (screen-space cap).

Both layers use **Texture Sheet Animation 4×1, cycles = 1, start frame 0**: every particle steps
through the 4 photographic chunks across its life, while ColorOverLifetime (alpha 1→0) drives the
erosion. ~50 sprites × 4 silhouettes × random rotation × random scale = enormous apparent variety
from ONE texture. SizeOverLifetime pops to ~50 % in the first 3–5 % of life, then grows slowly.

**The prefab root also carries `KWS_DynamicWavesSimulationEffector` + `KWS_EffectorAnimationCurve`**
— the splash stamps a force ring into the dynamic-waves sim, so rings + sim foam spread under the
sprites. The surface reacts; the sprites are only the airborne part. (Your fish script optionally
duplicates this with its own bell-curve effector.)

## 4. The GPU sim pool (shoreline/river auto-splashes) — motion tricks worth stealing

`KWS_DynamicWavesSplashParticles.shader` vertex stage, per particle from a structured buffer:

- **Velocity-aligned vertical stretch**: `pow5(downwardness) · speed · 0.1 → ×20`, scaled by
  distance-to-surface and by `fade·(1-life)` — falling fast = long streak, landing = round.
- **Size distribution `lerp(min, 8 m, pow10(rand01) · saturate(initialSpeed·0.1))`** — a rare
  huge chunk among many small ones instead of uniform mid-size. (Same shaping idea as our
  Pow-family curves; theirs is deliberately extreme.)
- Size envelope `0.5 + 0.5·sin(π·life)`, ramped in over the first 5 %.
- Sprites near the ocean **ride the FFT displacement** (shoreline-masked) so they don't detach
  from moving waves; billboard rotated by a per-particle random; per-particle `uvOffset` picks ONE
  of the 4 chunks (no frame stepping on the GPU path); zone-border + camera-distance fades.

## 5. Why ours reads "not well defined" — the honest audit

- **Our `SplashParticles.shader` is NOT the problem.** It already implements the packed path:
  same erosion (`FoamErosionAlpha`), same cubed shine ×3, thickness-modulated soft fade, plus two
  things KWS doesn't have (six-way directional lightmaps, backlit transmission). Shader parity is
  done.
- **The texture is the problem.** Procedural 128 px frames with blob/dot structure vs 512 px
  photographic chunks. No shader can add frequency content the texture doesn't have.
- **The construction is different.** Ours: ONE crown billboard playing a 64-frame animation +
  droplet dots. KWS: ~50 independent sprites each carrying a full photographic splash, eroding
  individually, plus a stretched-streak layer. Their "definition" is dozens of overlapping
  high-frequency silhouettes; an animated single card can't reach it at any flipbook resolution.
- **Missing grounding cues**: our splashes cast no shadow; (the ripple side we do have — sim,
  foam mask, exclusion — is already ahead of KWS on reactivity, per the 07-31 verification.)

## 6. What this means for the plan (updates the 08-01 decision, makes it EASIER)

The 08-01 conclusion ("the missing piece is authored splash ART, Houdini sheets") stands, but the
target just shrank dramatically: **KWS-level needs 4 static hero chunks at 512 px, not a coherent
animated sequence.** Single-frame renders are far easier to produce than a stable 64-frame sim
flipbook — no temporal coherence problem, no picking a window, cherry-pick the 4 best frames of
any sim (or even process licensed splash photos).

Proposed increments (nothing touched yet — authorization needed):

1. **ART — `WaterSplashChunks` atlas** (2048×512, 4×512² chunks, linear):
   R = photographic/rendered mass, G = highlight cores, B = soft cellular noise (generated,
   per-chunk), A = blurred envelope. Source: Houdini FLIP stills (you own Indie since 08-04) or
   processed photo references. I supply a channel-packer script (input: 4 grayscale/beauty
   renders → packed atlas + auto noise/depth channels), so the pipeline is: render 4 frames,
   run one script.
2. **WIRE — emitter construction parity** (config-level, small): droplet layer switches its sheet
   to the 4×1 chunk atlas (cycles 1, ColorOverLifetime alpha 1→0 — already our erosion driver);
   add/verify a stretched-streak layer (lengthScale ≈ 4, gravity 0, drag, 4–6 per burst); droplet
   layer gets gravity ≈ 1.2 + Limit Velocity dampen ≈ 0.03. Existing `_PackedChannels = 1` path
   consumes the atlas unchanged.
3. **OPTIONAL parity extras**, each separately gated: shadow-caster pass with blue-noise
   stochastic discard; per-particle random chunk (`uvOffset`) + velocity stretch shaping for the
   GPU pool; pow10 size distribution on burst sizes.

**Files read end-to-end for this report:** KWS_DynamicWavesSplash.shader,
KWS_DynamicWavesSplashParticles.shader (both passes), SplashParticles*.mat, WaterSplashes.prefab
(+2 variants, module-level), WaterSplash.png / WaterSplash_0.png / FluidsFoamTex.png +
import metas (channel-split), KWS_Particles.cs, FishSplashEffect.cs, our SplashParticles.shader,
gen_splash_flipbook.py header, WaterSplashEmitter.cs (structure).
FluidsFoamTex.png (4096², photographic whitewater, 4 variants in channels) is the SURFACE foam
texture, not a particle asset — noted for a later foam-quality pass.
