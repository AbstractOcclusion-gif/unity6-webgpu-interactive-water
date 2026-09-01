# AUDIT — Multi-body defaults and setting ownership

Date: 2026-09-01
Scope: Phase 1 performance observability. This audit records current values; it does not rewrite
authored quality assets or introduce runtime resolution changes.

## Effective ownership

| Setting | Authoring/default source | Runtime owner | Notes |
|---|---|---|---|
| Simulation and caustic RT resolutions | `WaterQuality` tier or probed fallback | Per `WaterVolume`, applied before module/RT creation | Fixed for the enable/session; no live switching in Phase 1. |
| Maximum simulated bodies | Resolved tier, default 4 | Primary body; first registered body is fallback | Bodies with culling explicitly disabled retain the established force-on escape hatch outside the finite budget. |
| Maximum caustic projections | Resolved tier, new default 4 | Primary body; first registered body is fallback | One shared per-camera relevance rank selects grants. Four is the established multi-body budget, not a measured final tier tuning. |
| Maximum planar bodies | Runtime constant 3 | Shared relevance manager | Existing maximum-three behavior is preserved. Moving this onto quality remains a Phase 2 product decision. |
| Relevance pin | `WaterVolume.RuntimeImportancePin`, default false | Per body | Safe runtime input; does not reallocate resources. |
| Activation distance | `WaterVolume`, default 100 m | Per body | Now measures to the nearest point of `CullBounds`, not the body center. Unbounded ocean eligibility remains distance-exempt. |
| Pipeline render scale / opaque texture | Resolved tier | Primary body | Existing pipeline-wide ownership is unchanged. |
| Underwater fog | Per-body quality/fog override | One selected source per camera | Existing single-source behavior is unchanged. |
| Exclusion volumes | Global registry | Global | Separate from body relevance and unchanged. |
| Terrain ocean clipping | Per-body bed-depth settings | Per body | Unchanged; one body cannot affect another body’s terrain clip. |

## Verified defaults

`WaterQuality` class and no-asset fallback on an unconstrained desktop resolve High to:

- simulation 256, caustics 1024, god rays 24;
- full render scale, authored mesh, real refraction and rich reflections enabled;
- caustic/readback/ocean FFT intervals of 1;
- maximum 65,536 foam particles;
- Full underwater fog at solve scale 1;
- 4 simulated bodies and 4 caustic projections.

`WaterVolume` serialized defaults relevant to this phase are:

- body type Pond, ripple quality High;
- `IsPrimary = true`;
- culling enabled, activation distance 100 m;
- runtime importance pin false;
- no assigned quality asset until a builder wires one.

The Water Wizard defaults to High ripple quality, SSR enabled, foam disabled, splash enabled and
creates a primary body. The generic build-kit method defaults `withFoamParticles` to true, but the
Wizard passes its foam toggle explicitly. All builders load
`Runtime/Defaults/Profiles/DefaultWaterQuality.asset`; the Connected Waters demo explicitly creates
five bodies without particle foam or pool/god-ray renderers and marks only the lake primary.

## Confirmed inconsistencies — preserved, not silently normalized

1. `Runtime/Defaults/Quality/WebWaterQuality.asset` and `MidWaterQuality.asset` carry 512/1024/32
   High values, while the class/fallback and `DefaultWaterQuality.asset` carry 256/1024/24.
2. `HighWaterQuality.asset` forces High with 64 god-ray steps, versus the class/fallback value 24.
3. `LowWaterQuality.asset` forces Low with a 32 simulation grid, 128 caustic RT, render scale 0.5,
   god rays off, rich reflections on and real refraction on. The current class Low defaults are
   128/256, render scale 0.7, god rays on, with rich reflections and real refraction off. The
   project owner confirmed on 2026-09-01 that real refraction in the authored Low profile is
   intentional; it is not a normalization candidate. The rich-reflection difference remains an
   unresolved authored-versus-class-default choice.
4. The older Web/Mid assets omit several fields added after those assets were serialized (including
   ocean FFT interval/resolution, fog solve scale and body budgets). Unity field initializers supply
   values until the assets are reserialized, but the YAML cannot be treated as a complete statement
   of effective quality.
5. A raw `WaterVolume` defaults to primary, whereas multi-body builders must explicitly clear that
   flag on every secondary. Runtime warns about multiple primaries; the new manager makes the
   ownership conflict visible for every row.
6. The generic builder’s particle-foam default is on while the Wizard UI default is off. Current
   callers pass the choice explicitly, so this is an API/UI default mismatch rather than a runtime
   ambiguity.

No authored quality asset was changed in Phase 1. Choosing which legacy assets should be migrated
to the class defaults, and choosing different caustic budgets per tier, require product profiling
and an explicit compatibility decision.
