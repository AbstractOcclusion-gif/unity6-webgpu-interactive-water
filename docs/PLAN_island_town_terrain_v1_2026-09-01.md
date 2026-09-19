# PLAN — Island Town terrain from `Island_Town_TopDown_Map_Concept_v2` (v1)

Date: 2026-09-01
Status: **MARKED COASTLINE AND MULTI-LEVEL WATER TERRAIN IMPLEMENTED AND VALIDATED.**
Revision: v1.5 — the project owner's red coastline mark now drives the organic outer profile;
ocean Y 60 m, central lake Y 85 m, upper river terrain Y 102 m and reservoir Y 112 m are encoded,
with independent left sinkhole/right lake routes and two cascade corridors.
Revision v1.4 — metric coastal shelf/drop-off relief, a seam-matched 513-resolution 2 km seabed
backdrop and a deterministic southeast beach terrace implemented and Unity batchmode validated.
Revision v1.3 — optional `IslandAuthoringMap` pipeline, protected erosion/drainage and reproducible
Island Town greybox factory implemented after project-owner GO on 2026-09-01. The implementation
keeps the radial path as the compatibility default and uses one grouped authoring asset rather than
the originally proposed loose mask references. Earlier design details remain below for traceability.
Revision v1.2 — mangrove and reservoir answers folded in (§0, §3.3, §5.1, §5.4, §6.3, §7, §8);
body-budget claim corrected by the project owner, mangrove walkability narrowed to the platforms.
Source concept: `FishingGameScafold/DevNotes/GameDesign/ConceptArt/Island_Town_TopDown_Map_Concept_v2.png`
(1024x1536, md5 `4ca4d0596d228427d50d89637159c31f`)
Target tool: `ThreeJSWaterPort/Assets/island generator`

---

## 0. Decisions taken (this session)

| Question | Decision |
| --- | --- |
| Terrain layout | **Island terrain + cheap backdrop**, not one 2000 m terrain |
| Geometry fidelity | **Authored mask traced from the concept**; procedural supplies erosion, beaches, substrate |
| Next step | This document, then GO before any code |
| Mangrove shelf | Traversed on **wooden/stone platforms only** — the bed itself is not walkable (answered 2026-09-01) |
| Reservoir | **A real water body** (answered 2026-09-01) |

---

## 1. Verdict on "procedural heightmap?"

**Hybrid. Authored shape, procedural detail.**

The generator's shape stage is `Perlin FBM + Ridged + Voronoi + Continental`, domain-warped, then a
**radial** falloff (`Runtime/Shape/IslandFalloff.cs:32` — `radius = sqrt(nx^2 + ny^2)`). That function
family cannot produce the features that define the concept, and every one of them is
gameplay-load-bearing rather than decorative:

- horseshoe harbour basin with a narrow mouth
- reservoir behind a dam, with a spillway feeding the town creek
- two causeway bridges (district connectivity)
- mangrove / shallow shelf on the east flank
- exactly one sand cove, southeast
- lighthouse point, south

`GAMEPLAY_MEMORY` gates districts, water routes and shortcuts behind story progress. Gates are placed,
not rolled. Seed-fishing for this silhouette would never converge.

**What procedural still earns its place doing** (all already implemented and good): hydraulic +
thermal erosion, coastal morphology, the metric beach profile, depression fill, and the
cliff/river/rock/grass/beach substrate masks. Keep the whole back half of the pipeline.

---

## 2. Scale reality check

The concept is roughly **2:3**, long axis north-south. Mapping the **long axis to 500 m** gives an
island footprint of about **500 x 322 m** — which fits a 500 x 500 box with room for its beaches.

At that mapping (~0.345 m per concept pixel):

| Feature in concept | Implied real size |
| --- | --- |
| Typical drawn building | ~15-20 m frontage |
| Harbour basin | ~120 m across |
| Moored ship, south dock | ~50 m |
| Town core (dense block) | ~230 x 160 m |

Workable at 1:1 human scale. 15-20 m frontages read as warehouse / net-loft architecture rather than
cottages. **If a denser old town is wanted, keep the drawn layout and build 10-12 m buildings** — the
street width comes back for free. This matches `GAMEPLAY_MEMORY`'s "dense district with several
states over a large single-use map".

---

## 3. Traps found in the current generator (verified in code)

These bite specifically when the island stops filling its own terrain.

### 3.1 FBM `Scale` is in **cells**, not metres

`Runtime/Noise/Fbm.cs:21` — `frequency = 1f / max(1, Scale)` applied to cell indices. Only
`Hydraulic`, `Thermal` and `Blur` carry a `ScaledTo` (`ResolutionScaling`). Every noise `Scale` is
therefore silently a function of `WidthInMeters / (Resolution - 1)`.

At the shipped defaults (2000 m @ 1025 = **1.953 m/cell**), `PerlinLayer.Scale = 420` is an ~820 m
wavelength — **larger than the whole island we want to build.** Changing `Resolution` alone rescales
every landform in metres while leaving the erosion sliders honest. This is the single biggest source
of "the preset looks nothing like it did".

Conversion rule to use from now on:

```
Scale_cells = desired_wavelength_metres / pitch_metres
pitch_metres = WidthInMeters / (Resolution - 1)
```

### 3.2 Falloff radii and edge noise are fractions of the **half-map**

`IslandFalloff` normalises to the map half-extent. A 250 m-radius island inside a 2000 m terrain sits
at `radius ~= 0.25`, so `InnerRadius/OuterRadius` would need ~0.13/0.26 — and the default
`EdgeNoiseStrength = 0.18` (`IslandGeneratorSettings.cs:108`) then means **180 m of coastline wander
on a 250 m-radius island**. It would erase the island. Moot once the mask drives the shape
(§5), but fatal in the naive "shrink the falloff" approach.

### 3.3 `DepressionFill` will drain the reservoir

It runs **last** (`HeightmapGenerator.Generate`, after detail noise), floods inward from the map
border, and raises every closed basin to its spill point. `MaxFillDepth = 0.08`
(`IslandGeneratorSettings.cs:133`) x `HeightInMeters` is the escape hatch — 48 m at the shipped
600 m height, but **only 11.2 m at the revised `HeightInMeters` 140 (§4.2)**. A fishable reservoir wants
roughly 4–10 m of water. **The escape hatch therefore does not cover it: the exclusion mask in §6.3
is mandatory, not optional.**

**The harbour needs the same treatment, for a subtler reason.** The flood crosses the harbour mouth
at the sill height, so the basin is only safe while its bed never dips below its own mouth sill. Any
dredged deep berth — exactly what a 50 m ship needs — is a closed basin relative to that sill and
gets raised to it. Exclude the harbour bowl as well.

### 3.4 `HeightInMeters = 600` on a 500 m island is a spire

`TerrainDimensions.Default` is 2000 x 600 (`IslandGeneratorSettings.cs:24-25`). At 500 m across,
600 m of vertical range is unusable. See §4 for the implemented 140 m range.

### 3.5 Resolution economics of the naive layout

2000 m @ 1025 = 1.95 m/cell → the island gets ~256 cells; a quay is 2 cells. 2000 m @ 4097 = 0.49
m/cell, but that is 16.8 M samples with ~94% spent on empty seabed, and `ResolutionScaling` takes
hydraulic erosion to ~1.9 M droplets per bake. This is what motivates the split in §4.

### 3.6 What `ResolutionScaling` does and does not cover

`LinearFactor` compares **cell counts only**, not terrain width. Shrinking `WidthInMeters` is
unaccounted for. Coincidentally this works in our favour: see §4.3.

---

## 4. Proposed terrain layout

### 4.1 Two terrains

| Layer | Size | Resolution | Pitch | Purpose |
| --- | --- | --- | --- | --- |
| **Island** | 640 x 640 m | 2049 | **0.3125 m/cell** | the playable island + its beaches + near shelf |
| **Backdrop** | 2000 x 2000 m | 513 | 3.906 m/cell | seabed, drop-off, distant silhouette |

640 m leaves ~70 m of margin around the 500 x 322 m footprint — enough for the shaped foreshore and
the shelf before the seam. (600 m @ 2049 = 0.293 m/cell is the tighter alternative.)

The ocean itself stays with ThreeJSWater; the backdrop terrain only has to carry bathymetry where it
is actually visible through the water.

### 4.2 Vertical range

| Setting | Value | Result |
| --- | --- | --- |
| `HeightInMeters` | **140** | room for a 60 m ocean plane and the elevated reservoir system |
| `SeaLevel` | **0.42857143** | ocean waterline at world Y 60 m |
| `Falloff.SeaFloorHeight` | **0.17857143** | world Y 25 m: 35 m baseline ocean depth, with procedural relief |
| land ceiling | 1.0 | world Y 140 m, 80 m above sea |

Consequence to carry into §6.3: `DepressionFill.MaxFillDepth 0.08` is **11.2 m** at this height, not 48.

### 4.3 Erosion budget — no manual retune needed

`LinearFactor(2049) = 2048/1024 = 2.0`, so `ScaledTo` doubles every cell-space erosion value. At the
new 0.3125 m pitch that lands on sane physical sizes without touching the authored sliders:

| Authored | Scaled | In metres |
| --- | --- | --- |
| `DropletCount 120000` | 480 000 | over 4.2 M cells |
| `DropletLifetime 40` | 80 cells | 25 m of travel |
| `ErosionRadius 3` | 6 cells | 1.9 m brush |
| `Thermal.Iterations 12` | 24 | 7.5 m of scree reach |
| `Blur.Radius 2 / Sigma 1.1` | 4 / 2.2 | 1.25 m kernel |

**One tune-up to consider:** 25 m of droplet travel is short for carving a valley down a 250 m
flank. Raising authored `DropletLifetime` to ~100 gives 200 cells = 62 m. (The `[Range(4,128)]`
attribute is inspector-only; `ScaledTo` is not clamped.)

### 4.4 Noise scales, restated in metres

Using `Scale_cells = wavelength_m / 0.3125`:

| Layer | Target wavelength | `Scale` (cells) | was |
| --- | --- | --- | --- |
| Perlin (main landform) | 220 m | **700** | 420 |
| Ridged (spine, headlands) | 110 m | **350** | 620 |
| Continental (broad tilt) | 500 m | **1600** | 1200 |
| Domain warp `Scale` | 100 m | **320** | 380 |
| Domain warp `Strength` | 25 m | **80** | 90 |
| MountainBase | 70 m | **224** | 260 |
| Detail | 9 m | **29** | 28 |
| Beach `EdgeNoiseFbm` | 70 m | **224** | 260 |

Metric knobs (`Beach.*Meters`, `CoastalMorphology.AlongshoreCoherenceMeters`, talus angle) stay at
**physical** values — the world is 1:1 human scale, so a beach is a beach. Suggested starting point:
`InlandWidthMeters 20`, `SubmergedWidthMeters 25`, `BermHeightMeters 1.2`,
`EdgeNoiseAmplitudeMeters 8` (small coves, not sweeping strands).

### 4.5 The seam

Different terrain sizes cannot be stitched with `SetNeighbors`. Handle it as:

1. Sample the finished inner terrain along the 640 m square boundary so the low-resolution
   backdrop leaves it at the same underwater height.
2. Blend outward over 180 m to the procedurally varied deep floor; blend inward beneath the
   high-detail terrain with a 2 m clearance so the two surfaces do not z-fight.
3. **Keep the entire seam under >=15 m of water.** ThreeJSWater's depth extinction hides any residual
   normal/LOD mismatch. This is the cheap win and it should be treated as a hard constraint on where
   the 640 m boundary falls.

---

## 5. Authoring the shape

### 5.1 Masks (painted over the concept, 2048 x 2048)

Three textures, not two. The first draft packed carve *depths* into colour channels; a walkable
mangrove shelf, a dredged harbour berth, a reservoir bed and a dam crest are all the same operation —
"put the ground at **this** height here" — so one authored height map plus weight channels covers
every case with one mechanism.

| File | Format | Meaning |
| --- | --- | --- |
| `island_land.png` | **16-bit grey** | 0 = sea floor, 1 = full land. Feathered at the coast. Replaces the radial falloff term. |
| `island_height.png` | **16-bit grey** | Authored target height, normalized against `HeightInMeters`. Read only where a stamp weight is non-zero. |
| `island_stamp.png` | RGBA8, no sRGB, uncompressed | **R** soft-stamp weight (pre-erosion) · **G** hard-stamp weight (post-erosion; also suppresses beach shaping) · **B** erosion protection, 0 = fully protected · **A** depression-fill exclusion |

**`island_height` must be 16-bit.** At 8 bits and `HeightInMeters 140`, one step is 0.55 m — visible
terracing across a harbour bed or a mangrove flat. At 16 bits it is 1.5 mm.

Weights are fine at 8 bits (256 levels), but they must be **smooth**, never binary — a hard edge in
the erosion-protection channel produces a discontinuity in the eroded surface right where a quay
meets natural ground.

Import settings matter and are the same failure mode `DemoSceneBuilder.WriteControlTexture` already
documents for the splat control map: sRGB off, compression off, alpha kept from file. Consider
importing them as texture assets rather than PNGs for the same reason.

### 5.2 Pipeline injection

Two insertion points, both small:

1. **`IIslandShape`** — `RadialIslandShape` (today's behaviour, default) and `MaskIslandShape`
   (bilinear sample of `island_land`). `IslandFalloff.Apply` takes the source instead of computing
   `radius` itself. `LiftInterior` maths unchanged, `InteriorLift` and `SeaFloorHeight` keep working.
2. **Two stamp passes**, deliberately split around erosion:
   - **Soft stamps, BEFORE erosion** — reservoir bed, creek/spillway line, harbour bowl. Erosion
     then enriches them and the flow map routes water into the harbour naturally.
   - **Hard stamps, AFTER erosion and blur** — quay walls, town platform, dock aprons, dam crest.
     These are built structures; erosion must not have an opinion about them.

### 5.4 What the two answers of 2026-09-01 cost the terrain

**Reservoir as a water body**

- It is a secondary `WaterVolume` (Pond) at its own rest level. `WaterVolume` **defaults to
  `IsPrimary = true`** (audit inconsistency #5, `docs/AUDIT_multi_body_defaults_2026-09-01.md`), so
  the builder must clear it explicitly or the runtime warns about two primaries.
- **Body count is not a limit — concurrency is.** *(Corrected by the project owner, 2026-09-01: the
  system dispatches as many bodies as needed, at any elevation, and connects them. The earlier draft
  of this bullet wrongly read `MaxSimulatedBodies` as an authoring cap.)* Bodies are unbounded and
  stackable at different elevations; `MaxSimulatedBodies` (default 4) governs how many run **ripple
  simulation concurrently**, chosen by the shared relevance rank, with the lease pool acquiring and
  releasing sim resources.
  What survives from the concern, in its correct and narrower form: the island is only ~500 m long
  and the default `ActivationDistance` is 100 m measured to the nearest point of `CullBounds`, so
  ocean + reservoir + creek + harbour + drains can easily be **in range at the same time**. Two real
  consequences: relevance ranking will be doing continuous work rather than sitting idle, and —
  per the pooling v1 honesty ledger — **paused bodies keep their RTs**, so memory tracks the number
  of bodies in range, not the number being simulated. Worth a metrics-overlay look once the island is
  standing, not a reason to author fewer bodies.
- The terrain owes the body a **bed strictly below rest level across the whole body footprint**,
  or the water surface pokes through ground (per-body bed-depth terrain clipping). That is an
  authored stamp, which is exactly what `island_height` is for.
- **The dam wall must be a prop, not terrain.** A heightmap is single-valued: it cannot hold a
  vertical face separating 52 m of water from 45 m of ground. The terrain authors a ridge; the dam
  face, spillway gate and parapet are meshes sitting on it. The terrain must rise above reservoir
  rest level around the *entire* perimeter except the spillway notch, or the body leaks.
- The spillway → creek → harbour chain is precisely the shipped connected-waters topology
  (`WaterConnectionPort` + `WaterConnection`, and the R1 `WaterRiver` facade's per-end
  "Connect to body"). The terrain plan should fix the **port positions** now, because the creek
  channel in `island_height` has to line up with them.

**Walkable mangrove shelf**

*(Narrowed by the project owner, 2026-09-01: the mangroves are not walkable by themselves — the
player traverses them on wooden and stone platforms. The bed carries no footfall.)*

- It still belongs in the **high-res island terrain** rather than the 513 backdrop, but for a weaker
  reason than the first draft claimed. Not collision: **visibility**. Shallow water has almost no
  depth extinction, so the bed is fully read by the player, and 3.9 m/cell triangles under 1 m of
  clear water are obvious. Pilings also need a believable bed height to sit on. Measured off the
  concept the shelf reaches ~100 m east of the town edge and sits inside the 322 m width, so it fits
  — and it **constrains where the 640 m box is centred**: centre on the island bounding box
  *including* the mangroves, not on the dry land.
- **What the answer buys back:** the shelf no longer has to be walkable-shallow or flat. Freed from
  footfall it can sit at **0.8–2.0 m** and stay uneven — roots, channels, mud banks — which reads
  better and gives the platforms something to be *for*. Deck height, not wading depth, sets the
  target; author against a ~1.2 m deck so the platforms always read as standing over water.
- It is still a **stamp**, not a beach. `BeachShaper` produces a profile, not a 100 m shelf, and its
  `MaxCoastSlopeDegrees` / `MaxShapedReliefMeters` logic has no concept of a mangrove flat. Author it
  in `island_height` with the hard-stamp weight set, which (per §5.1) also suppresses beach shaping
  there. Same for the quays.
- Open consequence: if nothing walks on the bed, does the mangrove water need to be its own body
  (for a distinct fishing surface and current), or is it the ocean body running under the platforms?
  The concept shows it continuous with the sea, which argues for the ocean body plus an exclusion or
  a connection rather than a new one.

### 5.3 Revised pipeline order

```
Base shape (noise)
  -> Island shape            [MASK-DRIVEN, replaces radial falloff]
  -> Mountain base variation
  -> Soft stamps             [NEW: reservoir bed, creek, harbour bowl]
  -> Hydraulic erosion       [weighted by protection mask B]
  -> Thermal erosion         [same weight]
  -> Gaussian blur
  -> Hard stamps             [NEW: quays, town platform, dam crest, aprons]
  -> Shoreline distance + coast slope
  -> Coastal morphology + beach shaping
  -> Detail noise
  -> Final depression fill   [NEW: exclusion mask]
  -> Slope / flow / substrate masks
```

The existing invariant is preserved: every published mask is still derived after the last
height-changing stage.

---

## 6. Code impact (nothing written yet)

### 6.1 New files

- `Runtime/Shape/IIslandShape.cs`
- `Runtime/Shape/RadialIslandShape.cs`
- `Runtime/Shape/MaskIslandShape.cs`
- `Runtime/Shape/AuthoredStamps.cs`
- `Runtime/Masks/AuthoringMaskSet.cs` (load + bilinear sample + validation at the boundary)

### 6.2 Modified

- `Shape/IslandFalloff.cs` — take an `IIslandShape`
- `HeightmapGenerator.cs` — two stamp stages, thread the masks through
- `IslandGeneratorSettings.cs` — mask references, stamp settings, fill exclusion
- `Erosion/HydraulicErosion.cs`, `Erosion/ThermalErosion.cs` — per-cell protection weight
- `Erosion/DepressionFill.cs` — exclusion (see below)
- `Editor/IslandGeneratorWindow.cs` — mask slots + preview modes

### 6.3 `DepressionFill` exclusion — proposed mechanism

**Now mandatory** (§3.3): at `HeightInMeters 140` the `MaxFillDepth` escape hatch is only 11.2 m, and
both the reservoir and any dredged harbour berth sit inside that.

The flood already carries a `visited[]` array seeded from the border (`DepressionFill.cs:50`,
`:102`). Seeding excluded cells (`island_stamp` alpha) as **visited at their current height** before
the flood starts means the frontier never raises them, so the reservoir and the harbour bowl survive
at their stamped depth while the rest of the island still drains. Roughly 15 lines plus a settings
field. Needs a test that the basin rim still floods correctly around an excluded interior.

---

## 7. Risks

| Risk | Mitigation |
| --- | --- |
| Excluded basins break the priority-flood's guarantee that all land drains | Verify the flow map after the change; excluded cells must not become flow sinks that strand a river upstream |
| Hard stamps leave 1-cell cliffs at their borders | Feather stamp edges in the mask; a very small post-stamp local blur restricted to stamp borders |
| Protection mask makes erosion discontinuous at its boundary | Use a smooth weight, not a binary one |
| Seam between island and backdrop visible at low tide / low water | Hard constraint: seam under >=15 m water (§4.5) |
| Mask import settings drift on reimport | Import as texture assets, per `DemoSceneBuilder`'s existing precedent |
| Town platform flattening fights the beach shaper near the docks | Hard-stamp weight suppresses beach shaping (§5.1) — verify the transition at the quay/beach boundary |
| Many bodies simultaneously within the 100 m activation distance on a 500 m island | Not a body-count limit (§5.4). Watch the relevance churn and the RT footprint of paused-but-in-range bodies in the metrics overlay once the island stands |
| Reservoir leaks: terrain dips below rest level somewhere on the perimeter | Validate the stamped ring against rest level as a generation-time check, fail loud |
| Reservoir bed shallower than 8 m gets drained despite the exclusion | Exclusion is by mask, not by depth — but add a warning when an excluded basin is also under `MaxFillDepth`, so a forgotten mask is visible |

---

## 8. Open questions

1. ~~Backdrop walkable?~~ **ANSWERED 2026-09-01: the mangrove shelf is walkable on wooden/stone
   platforms.** It moves into the high-res island terrain as a stamp — see §5.4. The backdrop stays
   pure bathymetry.
2. ~~Is the reservoir a water body?~~ **ANSWERED 2026-09-01: yes.** Consequences in §5.4; the
   `DepressionFill` exclusion in §6.3 is now mandatory.
3. ~~Water-body budget?~~ **ANSWERED 2026-09-01 by the project owner: not a constraint — bodies
   dispatch freely at any elevation and connect.** Remaining sub-question is a perf observation, not
   a design gate: how many end up inside `ActivationDistance` at once on a 500 m island (§5.4).
4. **NEW:** reservoir rest level and depth — drives the dam ridge height, the spillway notch, and
   whether the fishing depth curve has anything to work with.
5. **NEW:** mangrove shelf depth versus platform deck height — 0.8–2.0 m proposed (§5.4).
   And: is the mangrove water the ocean body, or its own?
6. Harbour bed depth: a flat authored depth, or profiled? Boats, the fishing depth curve, and the
   moored 50 m ship all care.
7. Are the two causeway bridges terrain, or props over a carved channel? (Recommend props — terrain
   cannot arch.)
8. Which mask channels does the town builder need back at runtime for prop placement (flatten
   weight, road spine)? Cheap to publish now, expensive to retrofit.
9. Does the island long axis stay at 500 m, or does the town core want the extra room that 10-12 m
   buildings would free up (§2)?

---

## 9. Files read for this analysis

`Assets/island generator/` — `Runtime/IslandGeneratorSettings.cs`, `HeightmapGenerator.cs`,
`Heightmap.cs`, `GenerationResult.cs`, `BaseShapeBuilder.cs`, `ResolutionScaling.cs`,
`Shape/IslandFalloff.cs`, `Noise/Fbm.cs`, `Erosion/DepressionFill.cs`, `Editor/DemoSceneBuilder.cs`,
`README.md`.
`FishingGameScafold/DevNotes/GameDesign/` — `StorySessions/2026-08-23_Float_Melissa_Island_Map.md`,
`STORY_WORLD_MEMORY.md`, `GAMEPLAY_MEMORY.md`, `POCKET_BRIEF.md`.
Addendum pass 2026-09-01: `docs/AUDIT_multi_body_defaults_2026-09-01.md` and the shipped water-domain
record (`WaterVolume` / `WaterConnectionPort` / `WaterConnection` / `MaxSimulatedBodies`).
