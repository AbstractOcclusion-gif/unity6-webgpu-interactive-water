# Partial submersion & exclusion stitch — deep analysis (2026-08-09)

Scope: ocean above/below transition popping with a **static camera in a raging sea** (fog a frame
too quick, "wrong sorting", transition patch not seamless) + the **exclusion wall / surface stitch**.
Everything below was read from the CURRENT files on disk this session (no memory trust):
`WaterVolume.Underwater.cs`, `WaterUnderwaterFogPass.cs`, `WaterUnderwaterFog.shader`,
`WaterWaterline.hlsl`, `WaterSurface.shader` (Pass 0/1), `WaterSurfaceFragStages.hlsl`,
`WaterExclusionWall.shader`, `WaterExclusion.hlsl`, `WaterOceanFft.cs`, `WaterUniformPublisher.cs` —
and on the reference side Crest 5 (`UnderwaterRenderer.*`, `Mask.compute`, `MaskArtifacts.compute`,
`SurfaceRenderer.Displaced.cs`, `Surface/Data.hlsl`, `Underwater.hlsl`, `UnderwaterShared.hlsl`,
`Meniscus.hlsl`) and KWS2 (`WaterSystemPartial_CommonLogic.cs`, `KWS_Underwater.shader`,
`KWS_WaterHelpers.cginc`, `WaterPrePass.cs`). No code was touched.

---

## 1. The one-sentence diagnosis

The per-pixel fog machinery is sound; what still pops is everything that is **not** per-pixel or
**not** same-frame: two screen-wide CPU flags that ride a 1–2-frame-stale, chop-blind height
readback, a meniscus arm gate that never got the envelope rewrite the fog arm got, **three different
waterline authorities** that only agree on a calm sea, and one genuine transparent-queue sort race
between the sheet and the exclusion wall. The exclusion stitch and the transition patch share root
cause #3 — your instinct that they are linked is correct.

---

## 2. Root causes, ranked

### RC1 — `_CameraUnderwater` flips on stale, chop-blind data with a 5 cm hysteresis
`ComputeCameraSubmerged` decides "the eye is in water" from
`cam.y < surfaceY + hysteresis` with `SubmergeHysteresis = 0.05 m`
(`WaterVolume.Underwater.cs:375-377, 476`). `surfaceY` comes from
`_oceanFft.TrySampleHeightLatest` — explicitly "~1-2 frames stale" (`WaterOceanFft.cs:724-737`),
sampled at the **undisplaced** xz (`.g` lane, no chop inversion — the file's own deferred-improvement
#1), and **not** time-extrapolated. In a raging sea the surface at the eye moves at roughly
`π·Hs/T` ≈ 3–5 m/s, so the stale reading is 10–30 cm off *every frame* — six times the hysteresis
band. The flag therefore flips one or two frames early/late relative to the crest the GPU actually
draws.

That is invisible to the fog *span* (per-pixel, prepass-owned). It is **not** invisible to the
screen-wide consumers that switch on this uniform in one frame:

- `WaterExclusionWall.shader:344` — the wall's fog-reconstruction backdrop arms/disarms
  (`_CameraUnderwater < 0.5 && reconstructFill > 0`). Near a carve this is exactly "the fog pops a
  frame too quick": the wall's reconstructed water disappears one frame before the drawn crest
  actually swallows the lens (and the `unity_FogColor` lerp at line 363 flips with it).
- `WaterSurfaceFragStages.hlsl:992` — `foamDeferredToOverlay` reroutes the pond/sim foam between
  queue-time draw and after-fog overlay on the same flip, plus the overlay pass's own
  `if (_CameraUnderwater > 0.5) discard` (`WaterSurface.shader:491`) and the CPU
  `CameraSubmerged` gate. Foam ownership hops a frame off-beat.

KWS has the identical CPU test (near-plane corners vs async readback,
`WaterSystemPartial_CommonLogic.cs:301-314`) but (a) adds an explicit user-tunable
**downward prediction offset** on the probe points — the comment says verbatim *"underwater
prediction because async readback can't have 100% accuracy position, because ~1 frame delay"* —
and (b) the resulting keyword gates only **cosmetics** (half-line tension, water drops). Their fog
never keys on it; ours keys the wall handoff and foam routing on it.

Note we already own the fix ingredient: `WaterOceanFft.VerticalRateAt` measures d(height)/dt from
the last two landings. `surfaceY + rate × (staleness)` is a one-line prediction, and the hysteresis
band should scale with `rate × frame time`, not sit at a fixed 5 cm.

### RC2 — the meniscus arm gate never got the envelope rewrite the fog arm got
The fog arming was rewritten 2026-07-31 to the wave-envelope ceiling precisely because per-corner
stale readback + fixed slack "routinely exceeded in a heavy sea" made it flap
(`WaterVolume.Underwater.cs:324-336`). The **straddle** test that arms `WaterlineActive` (meniscus
pass + scene copy) still uses the old shape: each near-plane corner against its own **stale
readback** height with a fixed `WaterlineArmPad = 0.5 m` (lines 340-366, 482). The comment argues
arming wide is visually free because the meniscus self-extinguishes — true, but the failure
direction that matters is the opposite one: a fast crest can put the crossing on screen while the
stale corners still say "no straddle", so the line (and the rendered-edge treatment that hides the
mask boundary) is **absent for a frame, then pops in**. Static camera maximizes this: nothing else
is moving, so a one-frame absence of the crossing band reads as a glitch. Same doctrine fix as the
fog: derive straddle from the envelope band (lowest corner below ceiling AND highest corner above
floor), which for a static camera cannot flap at all.

Also worth knowing: the prepass records only when `UnderwaterFogActive || WaterlineActive`
(`WaterUnderwaterFogPass.cs:108`). With a static camera at partial submersion the fog side is
envelope-armed and stable, so `_OceanSurfaceDepthValid` stays 1 — good. But on
camera-well-above-water frames where only the straddle side could arm the prepass, RC2's flap also
flaps the **classification authority** (rendered ↔ analytic) frame to frame. Fixing RC2 fixes both.

### RC3 — three waterline authorities, and they disagree exactly in a raging sea
This is the "patch is not seamless" one, and the code already half-admits it
(`WaterUnderwaterFog.shader` ~line 816: *"Horizontal FFT/Gerstner chop makes that surface
non-single-valued, so an independent height query at the final world XZ can legitimately select the
opposite medium for a frame"*). The three authorities:

1. **Rendered ownership prepass** — the displaced sheet itself, chop included
   (`WaterSurface.shader` Pass 1). Authoritative wherever it rasterised.
2. **Analytic field** `SurfaceHeightAtXZ` / `SurfaceSignedGap` (`WaterWaterline.hlsl:56-78`) —
   **vertical-only**: `LargeBodyWaveHeight` returns height at the *source* xz; horizontal
   displacement is never inverted. Used by: the near-clip strip and every prepass hole (via
   `OceanRenderedCoverage`'s `analyticCoverage · (1 − ownership.g)` fallback), the crossing marcher,
   the meniscus gap, `FogCoverageAtPixel` on the wall.
3. **The sheet's own displaced-fragment silhouette** — the carve discard tests
   `InsideExclusion(i.worldPos)` on the *displaced* world position (`WaterSurface.shader:210, 375`),
   so the hole edge is thrown sideways by chop.

On a calm pond all three coincide, which is why the transition looks fine when calm or when the
camera moves (motion masks one-frame, few-pixel errors). In a raging sea the horizontal
displacement is metres, so at every **boundary between authorities** the waterline jumps by
(horizontal chop × local slope): the edge of the near-clipped strip at the bottom of a straddling
frame, every prepass hole, and every exclusion rim. Those boundaries are exactly where you see the
patch "not seamless", and they crawl with the swell because the disagreement is phase-dependent.
Two amplifiers: the prepass is **half-res** (`PrepassResolutionScale = 0.5`), so the rendered edge
is quantised to ~2 screen pixels against the analytic side's 6-px feather; and the 3-tap ownership
blur only spans two prepass texels along the gradient.

### RC4 — the exclusion stitch is RC3 wearing a different hat
The wall classifies its fragments against **authority 2** — `SurfaceHeightAtXZ(IN.positionWS.xz)`,
vertical-only (`WaterExclusionWall.shader:230-246`) — while the sheet's hole edge it must meet is
**authority 3** (displaced discard), and the fog it must hand off to is **authority 1 where valid,
2 in the holes** — and the prepass *discards inside the carve* (`WaterSurface.shader:375-383`), so
ownership validity is 0 exactly along the stitch, meaning the fog side is analytic right where the
wall meets the sheet. The coverage **curve** is shared (`WaterlineCoverage`, one home — good), but
the **classification points** feeding it come from different geometry. Under chop the wall's
waterline and the sheet's crest silhouette part company by the horizontal displacement — sometimes
a sliver of nothing, sometimes overlap. That is the imperfect stitch, and it is the same defect as
RC3, which is why one fix (below) addresses both.

### RC5 — the wall/sheet sorting race — **ALREADY FIXED ON DISK (hand-shipped)**
The mechanism was real: sheet = Transparent queue, ZWrite On, Blend Off
(`WaterSurface.shader:96-106`), wall = Transparent, ZWrite Off, blended — bounds-distance sorting
made their order arbitrary, and a wall drawn before a sheet twin behind it got overwritten for a
frame. **Correction after checking the current C#:** `WaterExclusionVolume.cs` already carries
`WallRenderQueueOffset = 10` with a comment describing exactly this flip ("surface-vs-wall draw
order FLIPPED as the camera moved… an explicit offset makes the order a fact"), applied in
`ResolveWallMaterial`. So this cause is closed on disk; it is kept here because the same race still
exists for any OTHER ZWrite-Off transparent sharing queue 3000 with the sheet, and because the fix
pattern (explicit queue offset, matching `ChunkShellRenderQueueOffset`) is the one to reuse.

### RC6 — the little things (FOV / near / far audit you asked for)
The near-plane corner machinery is FOV/aspect/roll-exact (`ViewportToWorldPoint` per corner), so no
bug there. Findings, small to trivial: the 5 cm `SubmergeHysteresis` is the one camera constant
that is *not* sized to anything physical (see RC1); `WaterlineArmPad = 0.5 m` is a fixed pad doing
the job the envelope should do (RC2); `FogArmBandMeters = 0.5` claims to cover "the near plane's
own vertical extent" — since each corner is tested individually that claim is moot, but for the
record a near plane of 0.87 m+ at FOV 60 (or 0.5 m at FOV 90) exceeds 0.5 m half-height, so the
comment overpromises even though the code is fine. Far-plane side: the horizon silhouette problem
is handled by the vertical corroboration test (our analogue of Crest's `_FarPlaneMultiplier=0.68`
horizon knob and their `MaskArtifacts` fill), and the carve push clamps to `_ProjectionParams.z` —
both fine. One genuine FOV-adjacent nugget from Crest: their waterline RT is sized
`1 + 2 × nearClipPlane` metres around the camera (`SurfaceRenderer.Displaced.cs:62`) — they encode
the near-plane reach into the data window itself. Note for any port: at wide FOV + large near plane
the near-plane half-diagonal is `near·tan(fov/2)·√(1+aspect²)`, which outgrows their window before
it outgrows ours; size any such RT from that formula, not from `near` alone.

---

## 3. What the references actually do (verified in their sources)

**Crest 5.** One authority, same-frame, chop included: every frame the underwater renderer
rasterises the **actual displaced chunk meshes top-down** into a tiny camera-centred ortho height
RT (`_Crest_WaterLine`, texel 1.25 cm, window `1 + 2·near` m, R32F —
`SurfaceRenderer.Displaced.cs`). The fullscreen mask is *computed*, not rasterised: each pixel's
near-plane point vs `SampleWaterLineHeight` (`Mask.compute:40-48`). The meniscus samples the same
RT (`Meniscus.hlsl:126`), the portal fragment classifies against the same RT, and the volume pass
bounds its span with the rasterised mask **depth** (`UnderwaterShared.hlsl:176-181`,
`rawDepth = max(rawDepth, rawMaskDepth)` in effect) and starts its integral at the near plane
(`fogDistance −= _ProjectionParams.y`). The CPU gate is a coarse superset — skip only if the viewer
is >2 m above water (`UnderwaterRenderer.cs:298`) — and, decisively, the volume renders **before
transparency** (`RenderBeforeTransparency => true`), so the surface draws after it and overwrites
it wherever it rasterises: sorting by construction, no ownership reconstruction at all. The legacy
raster-mask path keeps a 3×3 neighbourhood **artifact-fill compute** (`MaskArtifacts.compute`) for
exactly the coincident-twin/gap class we fight with corroboration.

**KWS2.** Also one authority, differently shaped: a same-frame screen-space **prepass family** —
front mask+depth, back-face mask+depth, and for clip zones (their exclusion) **front/back thickness
depth RTs** (`KWS_WaterHelpers.cginc:349-367`). One function, `GetWaterVolumeDepth`
(lines 517-553), merges scene depth, water depth, mask and clip-thickness into the volume span per
pixel — the exclusion "stitch" cannot disagree with the fog because both are computed from the same
textures in the same expression. Mask reads are **gather-MAX** (`GetWaterMask`, line 426: over-cover
everywhere, our carve-only rule generalised). The CPU near-plane test exists but carries the
readback-latency **prediction offset** and only toggles cosmetics; the volume itself is
stencil + mask gated per pixel. Inside a clip zone the flag is force-cleared
(`WaterSystemPartial_CommonLogic.cs:388-401`) — we already mirror that.

The convergent lesson: **both engines have exactly one waterline authority per frame, produced by
the GPU from the same displaced geometry the player sees, and their CPU gates are coarse supersets
that decide nothing visible.** Our architecture is 80% there (prepass, shared curve, envelope-armed
fog); the remaining pops are precisely the places we still let a second authority or a CPU flag
speak.

---

## 4. Fix directions (no code yet — for discussion), ordered by risk/reward

**F1 — Predict, then widen: kill the RC1 flip.** Extrapolate the eye-height reading with the
already-measured `VerticalRateAt` over the known staleness, and size the hysteresis from
`rate × frame time + pad` instead of 5 cm. Separately (and better long-term): make the wall's
handoff fully per-pixel — it already computes `FogCoverageAtPixel`; `_CameraUnderwater` remains as
a cheap early-out only when the prediction says the eye is deep under every possible crest
(envelope-deep), never in the transition band. Exclusion behaviour is untouched: the
`_CameraDryVolume` split and the carve arming rule stay exactly as they are.

**F2 — Envelope-arm the waterline pass (RC2).** Straddle = lowest corner below
`ceiling = rest + envelope + pad` AND highest corner above `floor = rest − envelope − pad`. No
readback in the test, so a static camera cannot flap it; over-arming costs one meniscus pass +
(only if warp > 0) one copy on frames where the band self-extinguishes. This is the same rewrite the
fog arm already validated on 2026-07-31.

**F3 — One waterline authority for the no-prepass set (RC3+RC4 together — the linked fix).**
Adopt Crest's displaced-height window: rasterise the ocean sheet(s) top-down into a small
camera-centred ortho R32F RT each transition frame (their numbers: ~1.6-3 m window from the
near-plane formula, ~1.25 cm texel, one draw of the inner clipmap ring; cost is trivial next to the
existing camera-sized prepass). Then let every consumer that today falls back to the *analytic*
field in the transition band — the near-clip strip fallback inside `OceanRenderedCoverage`, the
meniscus gap, `FogCoverageAtPixel`, and the **wall's waterline classification** — sample that RT
instead of `SurfaceHeightAtXZ` when it is valid (analytic remains the fallback outside the window
and when the ocean is absent, i.e. today's behaviour). Because the RT is rasterised from the
displaced mesh, chop is baked in: the strip agrees with the prepass above it, and the wall's edge
agrees with the sheet's displaced hole edge — the stitch and the patch close with the same texture.
Exclusion safety: this RT is **classification-only**; the sheet's carve discard must be *disabled*
in this top-down draw (we want "where is the water surface", not "where did the carve open the
sheet") so carve holes keep their current analytic/prepass handling, and none of the carve kernels
(`ExclusionRayLength`, pushes, pane) change at all.

**F4 — Deterministic wall/sheet order (RC5).** ~~Give the wall a render-queue offset above every
sheet twin.~~ **Already on disk** — `WallRenderQueueOffset = 10`, hand-shipped. Nothing to do.

**F5 — Micro-hardening (optional, after the above).** Generalise gather-max over-cover (KWS's rule)
to the ownership sample so half-res quantisation always errs wet-side; consider 0.75× prepass scale
if the 2-px edge is still visible after F3; keep the corroboration test as-is (it is our
MaskArtifacts analogue and the carve-rim exemption is load-bearing).

Suggested order: **F2 → F1 → F3 → F5** (F4 was already done by hand). F2/F1 are small and
independently testable; F3 is the structural one that makes the raging-sea transition and the
exclusion stitch converge on a single rendered truth.

---

## 6. SHIPPED 2026-08-09 (one-shot, authorized "git is fresh") — F2 + F1, UNTESTED

Two files, C#-only, no shader edits, no exclusion math touched. Applied byte-precise on-device
(mixed CRLF/LF preserved outside the edited spans).

**F2 — envelope-armed waterline gate** (`WaterVolume.Underwater.cs`, new md5
`2f19f955d890f2df629074dc025d0b48`). `ComputeCameraSubmerged`'s straddle test no longer reads the
stale per-corner readback heights: the corners are tested against the wave-ENVELOPE band
(`rest ± (SurfaceHeightEnvelope() + WaterlineArmPad)`), the same doctrine as the 2026-07-31 fog-arm
rewrite. A static camera can no longer flap `WaterlineActive` (meniscus + scene-copy + the
straddle-frame prepass trigger), and four readback samples per frame are deleted. Ponds are
byte-equivalent by construction (envelope 0 ⇒ rest ± pad, the old test).

**F1 — dead-reckoned submerge flip** (`WaterVolume.Underwater.cs` +
`WaterOceanFft.cs`, new md5 `e6866b86abbb418b138b09b78a12ea07`). New
`WaterOceanFft.TrySampleHeightPredicted(x, z, atTime)`: the landed readback height extrapolated by
the already-measured `VerticalRateAt` × landing age, clamped ±1 m (`HeightPredictClampMeters`).
`SurfaceHeightAtWorldXZ` now calls it with `_waveTime`, so the eye's submerge flip (and
`_UnderwaterSurfaceY`, i.e. the Simple-tier waterline and every camSurf reference) tracks the
current-frame surface instead of the 1–2-frame-stale one. This is KWS's readback-latency
prediction, measured instead of authored. Degrades to exactly the old value until a second
readback landing exists, or when the wave clock is paused/scrubbed.

**Test checklist (static camera, raging sea — the repro):** 1) hold the camera at the waterline in
a heavy sea: the meniscus band must never vanish for a frame while the crossing is on screen;
2) same spot near an exclusion volume: the wall's reconstructed backdrop must stop popping a frame
early as crests swallow the lens; 3) pond regression: transition behaviour unchanged; 4) Simple
tier: flat waterline should now track the swell slightly better, not worse; 5) buoyancy unchanged
(it keeps `TrySampleField`, untouched). Expected residual: the chop seam at the near-clip strip /
carve rims (RC3/RC4) — that is F3, not shipped.

---

## 5. Invariants that must NOT move (so the fixes can't break exclusion)

The fog stays armed inside a dry carve (`UnderwaterFogActive`'s `|| eyeInDryVolume` — it carves the
room out of every ray). `WaterlineClassifyPoint`'s push-to-carve-exit and the shared
`WaterlineCoverage` curve (+ the carve-only over-cover) stay the single home for both edges. The
prepass keeps discarding inside exclusion volumes so carve pixels keep reaching the marcher (the
2026-07-28 regression proved why). The wall keeps `ZWrite Off` / no depth-texture presence — the
fog must keep integrating to the real scene through the veil. And the eye-in-dry-room split
(`_CameraUnderwater` = in *water*, `_CameraDryVolume` = in a carve) stays two flags; every fix
above respects that split.
