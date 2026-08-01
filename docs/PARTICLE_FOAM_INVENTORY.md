# Particle foam / spray / splash — complete inventory & triggers

Package: `com.abstractocclusion.webgpuwater` (+ `Assets/SplashStudio`)
Audit date: 2026-08-01. Every claim below is `file:line`-verified against the source.

> **Scope.** This covers only *particle* systems. The four **non-particle** foam engines
> (sim turbulence mask, ocean FFT whitecaps, surf whitewash, foam-mask shading) are excluded —
> except where one of them **feeds** a particle spawn, which happens three times (1a, 1b, 1c).

---

## 0. The shape of it

There are only **two rendering technologies** plus one standalone experiment:

| | Tech | Owner |
|---|---|---|
| **A** | GPU compute pool → procedural quads (`SV_VertexID`) | `WaterFoamParticles` |
| **B** | Unity Shuriken (2 systems: droplets + crown ring) | `WaterSplashEmitter` |
| **C** | Instanced procedural quads from a baked texture pair | `WaterSplashCassettePlayer` (experiment, standalone) |

Verified exhaustively: the only `RenderPrimitives`/`DrawProcedural` calls in the project are
`WaterFoamParticles.cs:787,814,836,844,849,869` and `WaterSplashCassettePlayer.cs:179`.
The only `ParticleSystem.Emit(` calls are `WaterSplashEmitter.cs:310,335`.

Three more components render **nothing** — they are pure *triggers* into B:
`WaterSplash`, `WaterSprayPump`, `WaterInputRouter`.

---

## 1. `WaterFoamParticles` — the GPU pool (4 independent spawn triggers)

`Runtime/WaterFoamParticles.cs` (893 lines) + `Runtime/Shaders/WaterFoamParticles.compute` (834 lines)

- Pool: `StructuredBuffer<FoamParticle>`, 12 floats/particle (`WaterFoamParticles.cs:84-96`, HLSL twin `.compute:111-121`).
- Foam pass `_DrawKind = 1` → `FoamParticles.shader` (`cs:150,778,787`).
- Spray pass `_DrawKind = 2` → `sprayMaterial`, falls back to `particleMaterial` (`cs:151,796-815`) — **always billboards**, even in density mode.
- Alt foam render: `FoamRenderMode.ScreenSpaceDensity` → compute splat `RasterizeDensity` (`.compute:793-834`) + one fullscreen tri on `FoamDensityComposite.shader` (`cs:856-870`).
- On armed-fog frames, queue-time draws are withheld and re-submitted by the underwater fog feature (`cs:766,828-851` ← `Rendering/WaterUnderwaterFogFeature.cs:172-174`).
- **Play-mode only** — no `[ExecuteAlways]` (`cs:17-18`); the edit-mode preview driver only ticks `ExecuteAlways` scripts (`Editor/WaterEditorPreviewDriver.cs:61`).

### 1a. TRIGGER — ambient turbulence-foam threshold (GPU, no readback)

One thread per **sim texel** reads the sim's foam mask RT:
`cs:587` `cs.SetTexture(_kSpawn, ID_FoamTex, volume.FoamMaskTexture)`
→ `WaterVolume.Facade.cs:275` → written by `WaterSimulation.StepFoam` (`WaterVolume.Solver.cs:158`, dispatch `WaterSimulation.cs:689`).

```hlsl
// WaterFoamParticles.compute:485-524
float foam = FoamTex.Load(int3(texel, 0));
float excess = foam - _SpawnThreshold;
if (excess <= 0.0) return;
float probability = excess * (1.0 + shear * SPAWN_SHEAR_BOOST)
                  * _SpawnRate * _TexelWorldArea * _DeltaTime;
if (Rand01(spawnKey, _FrameSeed) >= probability) return;
```

`SPAWN_SHEAR_BOOST = 4.0` (`.compute:194`) — wake edges and interactor rims emit more.
A fraction becomes airborne **mist**: `bool spray = r0 < _SprayChance;` (`.compute:554`), launched at `_SprayLaunchSpeed` (`.compute:580`). The rest is floating `KIND_SURFACE` foam.

> **Non-particle → particle feed #1.** `FoamMaskTexture` also carries the **surf/shore whitewash
> injection** (`WaterVolume.Solver.cs:157` → `WaterSimulation.cs:688`), so shore whitewash raises
> spawn probability here. The compute says so itself (`.compute:199-200`).
> The foam buffer is only written when `foam || wetnessMemory` (`Solver.cs:141`) — so unticking
> **Foam** starves this trigger.

### 1b. TRIGGER — ocean FFT breaking-crest whitecaps (`OCEAN_CREST_FOAM`)

```csharp
// WaterFoamParticles.cs:595-605
bool oceanCrest = volume.OceanFftActive && volume.OceanFftNormalTexture != null
                  && volume.OceanFftSpatialTexture != null;
```
```hlsl
// .compute:491-492
if (_CrestFoamSpawn > 0.0)
    foam = max(foam, OceanCrestFoamAt(texelWorld.xz) * _CrestFoamSpawn);
```

`OceanCrestFoamAt` sums the FFT cascades' accumulated whitecap channel `.w` (`.compute:279-290`) — **non-particle → particle feed #2**. Spawned foam is then conveyed along the wave heading, gated by local whitecap amount (`.compute:753-759`; world drift vector `cs:750-756`).

Kill switch: **Crest Foam Spawn = 0** (`cs:283`). Deliberately never overridden by a Foam Profile (`cs:282`; `WaterFoamProfile.ApplyTo:118-141`).

### 1c. TRIGGER — surf plunging-lip spray (breaker front)

```hlsl
// .compute:549-585
if (_ShoreFoamActive > 0.5) surfLip = SurfLipAt(texelWorld.xz, surfToShore);
bool spray = r0 < _SprayChance;
// The breaking lip always throws: lip spawns are ballistic jet, never floating foam.
if (surfLip > SURF_SPRAY_MIN_LIP) spray = true;
if (spray && surfLip > SURF_SPRAY_MIN_LIP)
    particle.velocity += float3(surfToShore.x, 0, surfToShore.y)
                       * (SURF_SPRAY_THROW_SPEED * surfLip);
```
`SURF_SPRAY_MIN_LIP = 0.3`, `SURF_SPRAY_THROW_SPEED = 6.0` (`.compute:201-202`) — **non-particle → particle feed #3**.

> **TRAP.** `_ShoreFoamActive` = `ShoreFoamState.Active` = `shore.SurfLayerActive` (`Solver.cs:212`),
> which is **deliberately separate** from `InjectionActive` (`Solver.cs:213-214`). Per the source
> comment (`Solver.cs:205-211`), collapsing the two once "silently switched off the one
> breaking-crest particle path in the package."
> ⇒ **Zeroing `surfFoamGain` / `surfWaterlineFoam` / `surfSwashDepositGain` does NOT stop lip spray.**

### 1d. TRIGGER — CPU event bursts (`QueueSplashBurst` → `SpawnBurst` kernel)

```csharp
// WaterFoamParticles.cs:712-736
public void QueueSplashBurst(Vector3 surfacePos, float strength, float radius,
                             int dropletCount, float upSpeed, float outSpeed,
                             Vector2 dropletLifeRange, float dropletSize)
{
    if (!useParticles || !isActiveAndEnabled || _pendingBursts.Count >= MaxBurstsPerFrame) return;
```
Drained once/frame (`cs:617-628`), one thread group per request, one thread per droplet (`.compute:606-661`). Caps `MaxBurstsPerFrame = 16`, `MaxBurstDroplets = 64` (`cs:46-47`, HLSL twin `.compute:233`).

**Only caller in the entire project:** `WaterSplashEmitter.EmitSplash` (`WaterSplashEmitter.cs:284`).

Bursts keep the sim alive even with ambient foam off, via a computed keep-alive window (`cs:730-735`, checked `cs:472-473`).

### Landing conversion (all airborne droplets — 1a-mist *and* 1d-burst)
`.compute:730-744`: on `worldPos.y <= SPRAY_LANDING_EPSILON && velocity.y < 0` the droplet flips to `KIND_SURFACE` and **re-rolls life/size from the deposit ranges** (`_DepositLifeMin/Max`, `_DepositSizeMin/Max`).

### Budget / rate controls

| Control | Where | Effect |
|---|---|---|
| `useParticles` | `cs:183`, gates `:461,:670,:716` | **Master.** No dispatch, no draw, no bursts |
| `capacity` (256–65536) | `cs:213` | Requested pool, rounded to pow2 |
| Quality tier cap | `WaterParticlePool.cs:71`; `WaterQuality.cs:240` `lowMaxFoamParticles = 1024` | Low tier hard-caps at 1024 |
| `spawnThreshold`/`spawnRate`/`maxSpawnPerFrame` | `cs:217,219,221` | Ambient rate + per-frame cap |
| `sprayChance` / `sprayLaunchSpeed` | `cs:223,225` | Mist fraction + pop speed |
| `spawnMaxDistance` | `cs:239` | Stochastic distance LOD (`.compute:504-510`) |
| Screen-tile spray budget | `.compute:561-571`; `SprayTileCap=6`, `TileGrid=16` (`cs:37,39`) | Over-budget spray **demotes to floating foam**, not dropped |
| Burst frame budget | `.compute:638-639`, `BURST_FRAME_CAP_DIVISOR = 4` | Bursts capped at capacity/4 per frame |
| Slot claim | `.compute:302-313`, `SLOT_CLAIM_PROBES = 8` | Saturated pool **drops** new spawns, never stomps live foam |
| `crestFoamSpawn` | `cs:283` | 1b weight |
| `depositLifeRange`/`depositSizeRange` | `cs:257,259` | Landed-droplet patch |
| `renderMode` + `densityMaterial` | `cs:201,204` | Quads vs screen-space veil |

**Hard device gates:** `maxComputeBufferInputsVertex < 1` → component self-disables (`cs:367-374`);
`maxComputeBufferInputsFragment < 2` → density mode falls back to quads (`cs:385-388`);
missing compute or material → `enabled = false` (`cs:346-361`).

**Per-frame run gates** (`LateUpdate`, `cs:446-490`): `useParticles` → volume active → buffers →
`SimStateTexture`/`FoamMaskTexture` non-null → `volume.Foam || volume.OceanFftActive` **or** inside the
burst window → `volume.IsSimulating` gates sim, `volume.IsVisibleToCamera` gates draw (`cs:485-489`).

---

## 2. `WaterSplashEmitter` — Shuriken crown ring + CPU-fallback droplets

`Runtime/WaterSplashEmitter.cs` (438 lines)

- `particles` — "Droplet Spray (CPU Fallback)", stretched billboards (`:340-382`), mat `Generated/SplashDroplet.mat` on `SplashParticles` shader (`Editor/WaterBuildKit.SceneRig.cs:82-83`).
- `crownParticles` — "Crown Ring", vertical-billboard flipbook (`:402-436`), mat `Generated/SplashCrown.mat`.
- Rig built by `Editor/WaterBuildKit.SceneRig.cs:71-98` (root `"Water Splash FX"`).

### THE ROUTING DECISION — the single most important block in the splash chain

```csharp
// WaterSplashEmitter.cs:271-288
WaterVolume body = WaterVolume.BodyContaining(surfacePos);
WaterFoamParticles gpuSpray = body != null ? body.GetComponent<WaterFoamParticles>() : null;
if (gpuSpray != null && !gpuSpray.UseParticles) return;        // master OFF -> nothing at all
if (gpuSpray != null && gpuSpray.isActiveAndEnabled)
{
    gpuSpray.QueueSplashBurst(...);   // -> 1d
    EmitCrown(surfacePos, strength, radius);
    return;                           // Shuriken droplets SKIPPED
}
```

⇒ **With a GPU pool present, the Shuriken droplet burst never fires — only the crown.**
Without one, the CPU loop `:290-311` runs `particles.Emit(ep, 1)` per droplet.

Crown trigger (`:319-336`): `if (crownParticles == null || strength < crownMinStrength) return;`

Count math (`:264-269`): `count = Clamp(round(strength * maxParticlesPerBurst), 3, maxParticlesPerBurst)`,
then `count = round(count * amountScale)` — amount multiplies **after** the clamp on purpose, so a
boosted caller may exceed the cap. `amountScale <= 0` mutes the whole emit, crown included (`:262-263`).

Per-frame CPU work — droplet surface drift (`LateUpdate:193-246`) reads the live waterline via
`body.TryGetSurface(...)` (`:225`) and snaps settled droplets to it (`:236-245`); early-outs at zero
particles (`:204`), so it costs nothing while the GPU path owns droplets.

Fields: `maxParticlesPerBurst` (1-128, `:89`), `upwardBias` `:91`, `outwardSpread` `:93`,
`dropletSize` `:94`, `lifetime` `:95`, `popDuration`/`driftStrength`/`driftDamping`/`surfaceRideHeight`
`:99-105`, `crownMinStrength` `:111`, `crownBaseSize` `:113`, `crownLifetime` `:115`,
`crownTint`/`crownOpacity` `:117,119`. Shuriken caps `DriftMaxParticles = 2000` (`:69`),
`CrownMaxParticles = 64` (`:80`). **Play-mode only.**

Auto-creation: `WaterVolume.ResolveSplashEmitter()` (`WaterVolume.Wiring.cs:97-110`) — assigned →
child → any in scene → **created on demand at runtime** (`:108-109`). Returns `null` if
`provideSplashEmitter` is off (`:99`).

---

## 3. `WaterSplash` — rigidbody impact trigger (renders nothing)

`Runtime/WaterSplash.cs` (82 lines), `[RequireComponent(Rigidbody)]` (`:10`).

```csharp
// WaterSplash.cs:40-79  (FixedUpdate)
bool under = (center.y - halfY) <= surfaceY;
if (under && !_wasUnder)
{
    float speed = Mathf.Max(0f, -_rb.linearVelocity.y);
    if (speed >= minImpactSpeed)
    {
        float strength = Mathf.Clamp01(speed / Mathf.Max(MinDivisorSpeed, maxImpactSpeed));
        activeEmitter.EmitSplash(new Vector3(center.x, surfaceY, center.z), strength, halfX * 2f);
        body.AddRipple(...);
    }
}
_wasUnder = under;
```

**Not a physics collision callback** — a per-FixedUpdate under/over test against the **analytic**
waterline (`:58` `TryGetAnalyticWaterline`), deliberately not the rippled readback, so the sim
readback isn't held open and a wake ripple can't re-trigger its own maker (`:46-57`).

Fields: `emitter` `:15`, `minImpactSpeed = 0.4` `:18`, `maxImpactSpeed = 3` `:20`, `rippleStrength = 0.04` `:22`.
Auto-added by `Editor/WaterBuildKit.Boat.cs:174` and `WaterBuoyancyStressSpawner.cs:82` (field `:35`).

---

## 4. `WaterSprayPump` — probe-based water-motion trigger (renders nothing)

`Runtime/WaterSprayPump.cs` (271 lines)

```csharp
// WaterSprayPump.cs:200-231  (TryEmit)
if (!state.HasHistory) return;                                    // need two frames
if (Mathf.Abs(world.y - surfaceHeight) > surfaceBand) return;      // not at the waterline
if (Time.time < state.NextEmitTime) return;                        // cooling down
float surfaceRise    = (surfaceHeight - state.PreviousSurfaceHeight) / deltaSeconds;
float probeDescent   = (previous.y - world.y) / deltaSeconds;
float horizontalSpeed = ...;
float signal = TriggerSignal(mode, surfaceRise, probeDescent, horizontalPlowWeight * horizontalSpeed);
if (signal < minImpactSpeed) return;
activeEmitter.EmitSplash(surfacePoint, strength, sprayRadius, amountScale);
state.NextEmitTime = Time.time + emitCooldownSeconds;
```
```csharp
// :235-243
case WaterSprayMode.Rock: return surfaceRise;                            // water rising at a static point
case WaterSprayMode.Boat: return probeDescent + horizontalPlow;          // hull driving in + flat-water plow
default:                  return surfaceRise + probeDescent + horizontalPlow;  // Both
```

**Boat mode reads the analytic surface only** so a hull's own wake can't re-trigger it
(`:170-178` — Boat probes pass `excludeInteractiveRipples: true`; Rock/Both pass `false`).

Fields: `probes[]` with per-probe `localOffset`/`mode`/`amountBoost` (`:66-80,85`),
`surfaceBand = 0.25` `:89`, `minImpactSpeed = 0.6` `:93`, `maxImpactSpeed = 4` `:96`,
`horizontalPlowWeight = 0.5` `:100`, `emitCooldownSeconds = 0.06` `:103`, `sprayRadius = 0.25` `:107`.
`amountBoost = -1` mutes a probe (`:51-52`). Play-mode only.
Editor: "Place bow row" / "Place rock ring" auto-placement (`Editor/WaterSprayPumpEditor.cs:56`), edit-time.
**Not auto-added anywhere** — `WaterBuildKit.Boat.cs` does *not* add one. Must be added by hand.

---

## 5. `WaterInputRouter` — mouse / touch splash trigger (renders nothing)

`Runtime/WaterInputRouter.cs`. Not a component — an internal helper owned by the primary `WaterVolume`.

**A. Mouse drag** (`:174-181`): `strength = Clamp01(moved / DragSplashFullStrengthDistance)`,
fires when `> MinDragSplashStrength`. Throttled by world distance travelled (`:167-169`,
`MinDragWorldSpacing = 0.02`). Constants `:24-27`.

**B. Touch tap** (`:131-149`): fires on release within `TapMaxTravelPixels = 16` travel,
`TapSplashStrength = 0.5`, emit at `:148`.

Gates: clicks inside a dry exclusion carve are rejected first (`:71`, `:163`). Runs **only in play
mode and only on the primary body** (`WaterVolume.Update.cs:35`). No inspector fields of its own —
disabled by unticking `provideSplashEmitter` (`WaterVolume.Settings.cs:126`) or by clearing the
body's target camera (`:81`).

---

## 6. `WaterSplashCassettePlayer` — SplashStudio baked-splash playback (experiment)

`Assets/SplashStudio/Runtime/WaterSplashCassettePlayer.cs` (216 lines) + `WaterSplashCassette.cs`,
`WaterSplashCassetteReader.cs`, `Shaders/SplashCassette.shader`, `Editor/WaterSplashCassetteMenu.cs`

Instanced procedural quads pulled from two RGBA16Half maps (position + attributes):
`Graphics.RenderPrimitives(rp, MeshTopology.Triangles, VerticesPerQuad, rows)` (`:179-180`).
Rows = particles, columns = frames (`WaterSplashCassette.cs:8`).

**TRIGGER: none — it's a timeline cursor, not an emitter.** `playOnEnable` (`:32`), public
`Play()`/`Stop()` (`:68-77`), `loop` (`:33,132`), `startOffsetSeconds` to decorrelate instances
along a crest line (`:36`). Advance/draw in `Update` (`:88-98,118-140`).

Budget: `rowFraction` (0.05–1) literally reduces the instance count (`:43,178`); `sizeMultiplier`
`:39`, `velocityStretch` `:40`, `scale` `:27`, `mirror` `:29`, `tint` `:41`.

**`[ExecuteAlways]` (`:18`) — the only particle system in the project that renders while authoring.**

Relationship to the rest: **none, fully standalone.** Reads no foam buffer, shares no profile,
referenced by no package code. Its own header (`:9-16`) says it is "the PROVING HARNESS, not the
shipping playback path… if a real fluid recording does not visibly beat what already ships, the rest
of the feature should not be built."

---

## 7. Checked and excluded (NOT particle systems)

- **`WaterShowcaseDripper`** (`:22-27`) — timed `_emitter.Emit()`, but `WaterRippleEmitter.Emit()`
  only calls `WaterVolume.TrySpawnRippleAt` (`WaterRippleEmitter.cs:44-49`). **Ripples only.**
  It can *indirectly* feed 1a (ripples → turbulence → foam buffer → spawn).
- **`WaterRippleEmitter`** — same; `emitOnMove` wake (`:51-61`) is ripple injection.
- **`WaterSphereInteractor` / `WaterInteractable` / `WaterBuoyancy`** — sim injection only;
  "splash" appears in tooltips/comments only.
- **`Assets/island generator/.../HydraulicErosion.cs`** — "droplets" are terrain-erosion
  iterations (`:7,19,27-33`), not water FX.
- **`FoamDensityComposite.shader`** — a *render mode* of system 1, not a separate system.

---

## 8. On/off cheat-sheet

| # | System | Turn OFF | Turn ON |
|---|---|---|---|
| 1 | **All GPU foam+spray+mist+splash droplets on a body** | `Water Foam Particles` → untick **Use Particles** (`cs:183`). Also kills #2's droplets *and* crown (`WaterSplashEmitter.cs:276`) | Tick it; add via `AbstractOcclusion/WebGpuWater/Water Foam Particles` or Water Wizard (`Editor/WaterSceneBuilder.cs:65`) |
| 1a | Ambient turbulence foam + mist | `WaterVolume` → Foam → untick **Foam**; or `Spawn Rate = 0` / `Spawn Threshold = 1` | Tick **Foam**, raise **Spawn Rate** |
| 1a | Airborne mist only (keep floating foam) | **Mist Chance = 0** (`sprayChance`, `cs:223`) | Raise Mist Chance / **Mist Launch Speed** |
| 1b | Ocean whitecap-driven particles | **Crest Foam Spawn = 0** (`cs:283`) — shader whitecaps survive | Set to 1 |
| 1c | Surf plunging-lip spray | Disable the **surf layer** on the body. Zeroing `surfFoamGain`/`surfWaterlineFoam`/`surfSwashDepositGain` will **NOT** stop it (`Solver.cs:205-214`) | Enable the surf layer with a baked bed/shore field |
| 1d | Event burst droplets | `Use Particles` off, or stop callers #3/#4/#5, or `provideSplashEmitter` off | — |
| 1 | Screen-space foam veil | `Render Mode = Quads` (`cs:201`) | `Render Mode = Screen Space Density` + assign **Density Material** |
| 1 | Global particle count | `WaterQuality` tier caps (`WaterQuality.cs:240`); or lower **Capacity** | Force High tier |
| 2 | Crown ring flipbook | Clear **Crown System**, or **Crown Min Strength = 1**, or **Crown Opacity = 0** | Assign crown PS + lower Crown Min Strength |
| 2 | Shuriken CPU-fallback droplets | Already never fire when the body has an active `WaterFoamParticles`. Otherwise **Max Particles Per Burst = 1** or clear **Droplet System** | Remove/disable `WaterFoamParticles` on the body |
| 2 | **All splash triggers over a body** | `WaterVolume` → untick **Provide Splash Emitter** (`Settings.cs:126`) | Tick it — emitter auto-creates on first impact |
| 3 | Object entry splashes | Remove `WaterSplash`, or raise **Min Impact Speed** above entry speed | Add `WaterSplash` (needs Rigidbody); or `WaterBuoyancyStressSpawner` → **Add Splash** |
| 4 | Boat/rock spray pump | Remove/disable `WaterSprayPump`, empty **Probes**, all **Amount Boost = -1**, or raise **Min Impact Speed** | Add `WaterSprayPump` + **Auto-place probes** |
| 5 | Mouse/touch drag & tap splashes | Untick **Provide Splash Emitter**; or clear the body's **Target Camera**; or make the body non-primary. *No dedicated toggle exists.* | Default-on in play mode on the primary body |
| 6 | Splash Cassette playback | Untick **Play On Enable** + `Stop()`, or disable/remove, or clear **Cassette**/**Material** | Add `WebGpuWater/Splash Cassette Player`, assign a cassette from the Splash Studio menu |
| all | One-asset tuning | **Water Foam Profile** (`cs:209`, `WaterSplashEmitter.cs:86`); its per-section **Drive** toggles (`WaterFoamProfile.cs:38,53,78,90`) decide what it overrides. "Apply To Selected Body" wires both at once (`Editor/WaterBuildKit.Body.cs:221-242`) | — |

---

## 9. Not verified

1. **No scenes / prefabs / .asset files** were in the audited extract, so which components are
   actually *placed* in the demo content, and the serialized values of any `WaterFoamProfile` /
   `WaterQuality` asset, are unverified. All defaults quoted are C# field initializers.
2. **SplashStudio assembly boundary** — the extract carried no `.asmdef`, so whether SplashStudio is
   a separate assembly could not be confirmed from source alone (the `.csproj` list at repo root
   suggests it *is*: `AbstractOcclusion.WebGpuWater.SplashStudio.csproj`).
3. `WaterSplashCassetteReader` byte layout was not audited (public surface only).
4. `SurfLipAt` → `EvaluateSurfWaves` bottoms out in `WaterSurfWaves.hlsl`, not read line-by-line;
   the compute's own characterisation ("plunge-amplified, surge-killed", `.compute:198-200`) is
   taken at face value for *which* breaker types throw spray.
