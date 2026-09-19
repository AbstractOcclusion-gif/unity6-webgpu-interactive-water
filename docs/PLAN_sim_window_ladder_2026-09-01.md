# PLAN — Game-controlled simulation focus and optional coverage ladder

Date: 2026-09-01  
Status: **REVISED PROPOSAL — DOCUMENTATION ONLY, NO RUNTIME CODE WRITTEN.**  
Decision sequence: implement and test controller/focus first; authorize the runtime ladder only
if wake clipping remains measurable.

Related documents:

- ANALYSIS_multizone_scaling_2026-08-31.md
- AUDIT_multi_body_defaults_2026-09-01.md
- WebGpuWater_PerfAudit_2026-08-13.md

---

## 1. Outcome

Expose a small public water-zone control surface that a game/level controller — including a
Game Creator adapter — can use to:

- select what a windowed water body follows;
- bias the simulation window for the current gameplay mode;
- request a coverage mode without knowing render-texture or quality-tier details.

The first implementation stops there. It uses the existing fixed-resolution simulation and
tests whether a better focus plus a trailing boat offset preserves the visible wake.

Only if that test still shows the wake dying at the window boundary do we add an optional,
two-rung runtime coverage ladder. The ladder scales window half-size and simulation resolution
together so world metres per texel stay constant.

This is not a multi-window design. Each body continues to own one interactive ripple simulation.
Only large/windowed bodies use the camera/focus-following window; bounded small bodies continue
to simulate their whole footprint.

## 2. Goals and non-goals

### Goals

- Give gameplay code a stable public API instead of access to internal serialized fields.
- Let mount/dismount and boat-speed events control the simulation focus.
- Preserve current behaviour when no controller uses the API.
- Keep per-camera relevance responsible for visibility and budgets.
- If the ladder is authorized, preserve overlapping ripple, flow and foam fields across a swap.
- Let WebGPU/mobile tiers disable runtime resizing completely.

### Non-goals

- Two simultaneous ripple windows on one body.
- Solver ripples for every distant boat.
- Continuous zooming or arbitrary runtime resolutions.
- Making Game Creator a required dependency of the water package.
- Reopening the 256 patch and default caustic-lattice caps in the first implementation.
- Supporting a 512 -> 1024 runtime rung in the first implementation.

## 3. Verified implementation facts

| Area | Current behaviour |
|---|---|
| Window mapping | WaterSimWindow.Track uses texel = 2 * simWindowMeters / SimResolution, snaps the centre to that lattice, and scrolls by integer texels. |
| Shore clamp | With clampWindowToShore, the legal centre depends on simWindowMeters. Changing coverage can therefore move the centre. |
| Scroll | WaterSimulation.Scroll transfers active state + flow in one dispatch and foam in a second. Its single size cannot describe a cross-resolution copy. |
| Runtime resolution | ApplyQuality resolves the resolution before modules and RTs are created. No live rebuild path exists. |
| Lease pool | Idle reuse is keyed by compute shader + resolution. A parked context is cleared on its next Acquire, not on Release. |
| Injection batching | Drops and spheres batch separately. Up to MaxQueuedInjections = 64 stamps of one kind share a dispatch; a full queue flushes early. |
| Injection cost | Dispatch count can stay flat while shader work grows because every texel loops over every queued stamp. |
| Relevance | WaterRuntimeRelevance ranks bodies for simulation, planar, caustic and foam work. It does not encode mount state or boat speed. |
| Public control | simWindowFocus and simWindowOffset exist but have no public runtime setter. |
| Patch cap | MaxPatchGridResolution = 256. |
| Caustic cap | MaxMatchSimLatticeResolution = 256 for the default MatchSim path on windowed bodies. |
| Quality bases | Default desktop High is 256. Web/Mid assets carry High = 512; Mid is normally forced to Medium = 256. LowWaterQuality is forced to Low = 32. |

## 4. What the stress test establishes

The 1/5/15/30-body result validates the practical batching win for the tested scene:

> Boat count is not the current FPS cliff while per-kind stamp counts stay within queue capacity
> and the tested interactors remain representative.

It does not prove boats have zero cost. More stamps increase the loop inside the dispatch, and
more than 64 stamps of one kind add dispatches. The gameplay decision remains that only the
controlled boat needs a long-lived solver wake.

## 5. GO A — public controller and smarter focus

### 5.1 Public water API

Add a minimal public facade on WaterVolume. Exact names should follow the existing facade style,
but it must provide these semantic operations:

- set an explicit simulation focus and local-frame offset;
- clear the explicit focus and return to the target-camera fallback;
- later, request a semantic coverage mode such as Near or Wake without exposing raw resolutions.

The water package must not reference Game Creator. An optional Game Creator adapter/action calls
this public API from its own integration assembly.

Validate inputs at this boundary. Non-finite offsets must fail clearly or be safely clamped. Calls
must remain safe before initialization, after disable, and when the supplied transform is destroyed.

### 5.2 Gameplay policy

- On foot: follow the camera/player with the authored near offset.
- Mounted and moving: follow the controlled boat.
- Bias the window behind the boat to reserve domain for its trailing wake. In the current local
  offset convention this is normally a negative forward offset.
- Resolve velocity, smoothing and offset limits in the gameplay controller. The water core receives
  the resolved focus and offset; it does not own vehicle logic.
- Change focus on discrete mount/dismount or controlled-vehicle events.

A far focus switch can scroll by more than a window width and clear the non-overlap. GO A creates
no new GPU resources, but it is low risk rather than zero risk.

### 5.3 GO A acceptance

- With no controller calls, behaviour and serialized defaults are unchanged.
- Mount/dismount changes focus without allocating simulation RTs.
- The window remains texel-snapped; overlapping ripples remain world-pinned.
- A trailing offset measurably increases how long the controlled boat wake remains in the window.
- Game Creator can drive the feature only through the public facade.
- Repeated focus changes do not leak objects, throw exceptions or dirty authored assets.

### 5.4 Evidence checkpoint

Record a fast-boat pass with:

1. current camera focus;
2. boat focus with zero offset;
3. boat focus with a tuned trailing offset.

Proceed to the ladder only if the third test still shows gameplay-relevant edge clipping. If GO A
is sufficient, the ladder stays unimplemented.

## 6. GO B1 — define a two-rung ladder, pinned to base

### 6.1 Definition

For one body:

    base half-size    = baseSimWindowMeters
    base resolution   = resolved tier SimResolution
    base texel metres = 2 * base half-size / base resolution

The optional wide rung is:

    wide half-size    = 2 * base half-size
    wide resolution   = 2 * base resolution
    wide texel metres = base texel metres

Constant texel metres makes overlapping cell centres map 1:1. This is integer crop/pad with
rest-filled new cells, not a filtered resample.

It preserves local discrete solver scale and world propagation away from changed boundaries. It
does not make whole-field evolution literally identical: the boundary/fade band moves, and
crop/pad can alter the domain used by volume conservation.

### 6.2 Initial limits

- At most two runtime rungs.
- A named hard runtime resolution ceiling of 512 for the first implementation.
- A resolved base already at 512 has ladder ceiling 0.
- Low and WebGPU-oriented defaults use ceiling 0 until device measurements justify otherwise.
- Any profile can set ceiling 0 even when its base is below 512.
- Validate the result against the compute thread-group multiple, integer overflow, platform texture
  limits and the named runtime ceiling.
- GO B1 ships pinned to the base rung and performs no allocation or transfer.

The initial version never attempts 512 -> 1024. The six simulation RTs alone would require a
64 MiB new context, before the old context, obstacle set and other resources are counted.

## 7. Centre and field mapping

Constant texel size keeps the centre lattice unchanged but does not guarantee the same centre.
With clampWindowToShore, changing half-size changes the legal centre range.

A transfer must therefore:

1. resolve old and new snapped world centres;
2. convert their difference to an exact integer-texel offset;
3. copy world-overlapping cells using that offset;
4. rest-fill destination cells with no source coverage.

For an unclamped/ocean window whose focus is unchanged, the offset is zero and the old field maps
to the centre of the new field.

## 8. GO B2 — transactional resolution transfer

The swap occurs at one controlled frame boundary. Gameplay code never manipulates leases or raw
resolutions.

### 8.1 Required compute path

Add cross-resolution transfer kernels with distinct source and destination sizes. Existing Scroll
cannot be reused unchanged because its size, groups and bounds describe one resolution.

Transfer the three active fields:

- current state: height, vertical velocity and stored normal components;
- current horizontal flow;
- current foam and wet mark.

An acquired destination has been reset, so its scratch textures remain at rest. Stale source
scratch textures do not need preservation; the next ping-pong pass overwrites destination scratch.

Like current scrolling, one kernel may transfer state + flow and a second may transfer foam.

### 8.2 Ordered transaction

1. Validate the rung and calculate its half-size, resolution, snapped centre and transfer offset.
2. Enter at a defined frame boundary and block new injections during the swap.
3. Flush queued drops and spheres into the old active field before copying it.
4. Invalidate the CPU height sample and increment a dedicated WaterSurfaceSampler generation.
   ResetSimulationState only guards simulation activity readback and cannot serve this callback.
5. Acquire the new-resolution simulation.
6. Dispatch state/flow and foam transfers.
7. Carry the active/wake state, or explicitly wake the target when a live field was transferred.
   Otherwise a preserved wake can freeze because a reset target starts with its latch false.
8. Atomically publish the new resolution, half-size and simulation reference.
9. Re-run simulation density, anisotropy and horizontal-flow geometry.
10. Refresh all resolution-dependent collaborators in section 8.3.
11. Release the old simulation only after no consumer or readback can reference it.
12. Re-enable injections and new-resolution readbacks.

If validation or allocation fails before atomic publish, retain the old rung and report a clear
error. Never leave the body partly changed.

### 8.3 Resolution-dependent collaborators

| Collaborator | Required action |
|---|---|
| Obstacle textures | Recreate at the new resolution. SetFrame already follows centre and extent, but RT sizes are constructor-fixed. |
| Simulation patch | Ensure min(SimResolution, MaxPatchGridResolution); rebuild only when this resolved size changes. |
| Caustic lattice | Refresh/rebuild dedicated mesh and recorded spacing when CausticGridResolution changes. Current state is constructor-time and readonly. |
| Density/anisotropy | Re-run ResolveSimDensityRatio and ApplySimAnisotropy after atomic publish. |
| CPU sampler | Reject old-generation callbacks, clear height-ready during transition, and resume ripple sampling after a matching callback. |
| Activity state | Preserve/re-arm the latch so transferred ripples continue evolving and later earn sleep normally. |

### 8.4 Consumers already reading live state

Provided the body publishes atomically, these need no persistent resource rebuild:

- shader WaterTexel;
- simulation centre, extent and slope uniforms;
- foam-particle size, geometry and spawn groups;
- base-sheet hole derived from PatchPoolHalf and PatchCoverMargin;
- obstacle frame matrices derived from centre and SimHalfExtent.

Reading live does not mean free. Doubling foam spawn groups still quadruples that grid dispatch.

## 9. GO B3 — trigger policy

The game controller requests semantic coverage; the water package resolves it against quality
ceilings.

- On foot/unmounted: request Near.
- Mounted but slow: retain the current rung.
- Above an authored enter speed for an authored duration: request Wake.
- Return to Near only below a lower exit speed and after a minimum dwell time.
- Prefer mount/dismount, camera cuts or another maskable event for a transition.

Thresholds, dwell times and offset limits must be named serialized settings.

Relevance may gate whether a body receives simulation budget. It must not replace mount/speed
policy or become a second gameplay state machine.

## 10. Geometry and visible consequences

- At or below sim 256, the patch can match the solver grid.
- Above sim 256, solver metres-per-texel remain constant but capped patch and default caustic
  lattice density decrease as coverage grows.
- The base-sheet hole and edge fade move with the new extent.
- Shrinking intentionally discards field content outside the retained overlap.
- A shore-clamped body may change centre during a rung transition.

Transitions must be discrete and infrequent. They are not guaranteed invisible from every camera.

## 11. Memory accounting

The two ARGBFloat plus four RGFloat simulation RTs use 64 bytes per grid cell:

- 128 square: approximately 1 MiB
- 256 square: approximately 4 MiB
- 512 square: approximately 16 MiB

Both simulation contexts coexist during transfer:

- 128 -> 256: approximately 5 MiB simulation-only peak
- 256 -> 512: approximately 20 MiB simulation-only peak

These are not total body figures. With obstacle rendering, its RFloat fields, RHalf mask and 4x/2x
supersample chain add about 54 bytes per base-resolution cell. If old and new obstacle sets overlap,
a 128 -> 256 change can put simulation + obstacle transient near 9.2 MiB before caustics, reduction
buffers, meshes and readback staging.

Profile memory and transition-frame time on WebGPU, not only in the Editor.

## 12. Validation matrix

### Pure/edit-mode tests

- Reject invalid ceilings, overflow, non-thread-group resolutions and non-finite half-sizes.
- Constant-texel calculations remain equal within a defined tolerance.
- 128 -> 256 and 256 -> 128 preserve the exact centre overlap.
- A changed shore-clamped centre yields the correct integer transfer offset.
- Ceiling 0 always resolves to the base pair.

### Play-mode/GPU tests

- Expand/shrink with a live asymmetric wake; overlap remains world-pinned.
- State, flow, foam and wet mark survive; new cells start at rest.
- The transferred field keeps stepping instead of freezing.
- Injections immediately before transition are neither lost nor applied twice.
- No old-resolution height callback becomes sampleable after the swap.
- Buoyancy resumes ripple sampling after a new-resolution callback.
- Obstacle maps have the correct size and frame.
- Patch and caustic lattice use correct capped size and spacing.
- Repeated toggles do not grow leases, RTs, meshes or managed buffers beyond retention policy.
- WebGPU reports no unsupported format, binding or readback errors.

### Profiler and Frame Debugger expectations

- No queued stamp: zero Drop/SphereInteract dispatches.
- 1..64 stamps of one kind: one dispatch for that kind.
- Above 64: ceil(stampCount / MaxQueuedInjections) dispatches for that kind.
- Ceiling 0 is behaviourally identical to the pre-ladder build.
- The initial implementation rejects every 512 -> 1024 request.

## 13. Independent GO checkpoints

1. **GO A — controller/focus:** public facade, optional Game Creator adapter, trailing-offset policy
   and tests. No resolution changes.
2. **Evidence checkpoint:** record the three focus variants and decide whether clipping warrants
   more machinery.
3. **GO B1 — definitions only:** quality ceilings, validation and diagnostics, pinned to base.
4. **GO B2 — manual transactional transfer:** kernels, state/activity/readback handling and
   collaborator refresh.
5. **GO B3 — automatic trigger:** speed hysteresis and dwell, enabled only after manual switching
   passes the validation matrix.

Authorizing one checkpoint does not authorize the next. Each remains independently reviewable and
revertible.

