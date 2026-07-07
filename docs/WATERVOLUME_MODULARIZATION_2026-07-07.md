# WaterVolume modularization — Phase 1 shipped, Phase 2 recipe

Date: 2026-07-07. Branch: `main`. Package: `Packages/com.abstractocclusion.webgpuwater`.
Author note: done autonomously while Bert was away ("go all-in"). Every change below is meant to be
**byte-for-byte behaviour-preserving**. Nothing was pixel-verified (I can't run Unity) — compile + Play
check still required.

---

## What this refactor is

`WaterVolume` is a ~2,000-line god-class: ~80 serialized fields across 22 `[Header]` blocks **plus** all
runtime logic. Goal: a thin **master** that orchestrates optional, tickable **modules**, each owning its
own logic and its own settings, toggleable independently.

The work splits in two:

- **Phase 1 (DONE this session): formalise the lifecycle.** The 6 collaborators the master already
  constructed by hand are now `IWaterModule`s driven through a registry. No serialized field moved, so
  **scenes/prefabs and the custom editor are untouched**. This is the "formalise these first" step.
- **Phase 2 (NOT done — recipe below): migrate the ~80 settings off `WaterVolume`** into per-module
  nested `Settings` blocks. This is the part that actually breaks the god-class. I did **not** do it
  blind because it re-serialises your scene (a silent-data-loss failure mode git does not cleanly
  cover — see the `FormerlySerializedAs` trap below). It's mechanical with the recipe; best done with
  the editor open so we can confirm values survived.

---

## Phase 1 — what changed (compile + Play should look identical)

### New files
- `Runtime/IWaterModule.cs` — lifecycle contract: `bool Enabled { get; }`, `Initialize(WaterContext)`,
  `Dispose()`. (`Tick`/`PublishUniforms` are deliberately **not** on the interface yet — they arrive
  when the per-frame schedule is restructured with you at the editor, so there are no dead no-op members.)
- `Runtime/WaterContext.cs` — the shared seam. Phase 1 carries only `Owner`; the per-frame fields
  (camera, wave time, wind, sim window, …) get lifted here as Tick migrates.
- `Runtime/WaterCollaboratorModules.cs` — 6 thin adapters wrapping the **untouched** collaborator
  classes: `SimulationModule`, `ObstacleModule`, `CausticsModule`, `SurfaceSamplerModule`,
  `OceanFftModule`, `SimWindowModule`. Each owns its instance; `Enabled` reproduces the original
  construction condition exactly (e.g. `ObstacleModule.Enabled => obstacleShader != null`;
  `OceanFftModule.Enabled => IsOceanClipmap && oceanFftCompute != null`).

### `WaterVolume.cs` edits
1. The 6 eager collaborator **fields** became private **module fields** + read-only **forwarding
   accessors** with the same names (`_water`, `_obstacle`, `_caustics`, `_sampler`, `_oceanFft`,
   `_simWindow`). So every existing reader (Update, the ripple/sampling facade, caustics render, the
   `internal` accessors) compiles and behaves unchanged. The lazy trio (`_bedBaker`, `_publisher`,
   `_inputRouter`) was left as-is by design.
2. Added `internal int SimResolution => _simRes;` (modules read it at Initialize).
3. `TryInitialize`: the 6 inline `new …()` constructions replaced by one `BuildAndInitializeModules()`
   call, **relocated** to just after `_windowed = ShouldWindow()` and before `ApplySimAnisotropy()`.
   - Why there: `OceanFftModule.Enabled` gates on `_windowed` (via `IsOceanClipmap`), so it must run
     after `_windowed` is set; `ApplySimAnisotropy()` reads `_water`, so the sim must exist before it.
   - **Verified safe:** `ShouldWindow()` (line ~2047) reads only config
     (`enableLargeBodyWindow`, `openWater`, `unboundedOcean`, `VolumeExtentSafe`, `largeBodyThreshold`) —
     no collaborator — so moving the three previously-early constructions (sim/sampler/simWindow) to
     just after it changes nothing.
4. `OnDisable`: the 4 inline `?.Dispose()` lines + the `_sampler/_simWindow = null` lines replaced by
   one `DisposeModules()`. Same GPU resources released; the lazy `_bedBaker?.Dispose()` and
   `_inputRouter = null` are unchanged.
5. Added helpers `BuildAndInitializeModules()` and `DisposeModules()`.

### Verify (please)
- Clean compile of `AbstractOcclusion.WebGpuWater` (+ Editor).
- Play `Assets/ocean test.unity`: pool/ocean look, ripples, buoyancy, caustics, foam identical to before.
- Toggle a body off/on in play (OnDisable→OnEnable) to exercise dispose+rebuild of the registry.

### Heads-up: the sandbox mount was stale
While working, the bash view of `WaterVolume.cs` was truncated (1949 lines, `ShouldWindow` invisible);
the authoritative file is longer and intact. All edits were made against the authoritative file. If
anything looks off after you "save the project", ping me.

---

## Phase 2 — settings migration (recipe, NOT yet applied)

### The trap (why we can't just move fields + `[FormerlySerializedAs]`)
`[FormerlySerializedAs]` only renames a field **within the same serialized container**. Moving
`windSpeed` from `WaterVolume` into `windSettings.windSpeed` changes the serialization **path**
(`windSpeed` → `windSettings.windSpeed`), which crosses a container boundary — `FormerlySerializedAs`
does **not** bridge it, so old scenes load the **default** and your tuned values are lost silently.
(There is zero precedent in the package: no `FormerlySerializedAs`, no nested `[Serializable]`, no
`ISerializationCallbackReceiver` — so this is new ground and worth verifying on one field first.)

### The working pattern (per feature)
Two things make it safe and keep the tree compiling after **each** feature:

1. **Nested settings + compatibility accessors.** Move the fields (with their `[Tooltip]`/`[Range]`/…)
   into a nested `[System.Serializable] class XxxSettings`, add `[SerializeField] XxxSettings xxx = new();`
   Keep a same-named forwarding accessor on `WaterVolume` (`internal float windSpeed => wind.windSpeed;`)
   so the ~25 referencing files (publisher, buoyancy, editor, shaders' C# feeders) keep compiling
   unchanged. The default inspector auto-draws the nested block as a foldout — **`WaterVolumeEditor`
   needs no change** (it only does scene gizmos and reads *placement* fields, which stay on the master).

2. **Legacy capture + one-time copy (this is what preserves scene values).**
   ```csharp
   // hidden, keeps the OLD serialized name so existing scenes still deserialize into it
   [SerializeField, HideInInspector, FormerlySerializedAs("windSpeed")] float _legacyWindSpeed = 3f;
   [SerializeField, HideInInspector] int _settingsVersion = 0;   // 0 = pre-migration scene

   public void OnAfterDeserialize() {
       if (_settingsVersion >= CurrentSettingsVersion) return;    // new/already-migrated: skip
       wind.windSpeed = _legacyWindSpeed;                         // copy legacy -> nested (plain field ops only)
       // …one line per migrated field…
       _settingsVersion = CurrentSettingsVersion;
   }
   public void OnBeforeSerialize() { }
   ```
   Here `FormerlySerializedAs("windSpeed")` **is** valid — `_legacyWindSpeed` is still top-level on
   `WaterVolume` (same container, just a C# rename), so it captures the old scene value; the callback
   then copies it into the nested block once. New objects serialize with the current version and skip.
   `WaterVolume` implements `ISerializationCallbackReceiver`.

   Verify per feature: open the scene, confirm the foldout shows the *tuned* values (not defaults),
   then let Unity re-save.

### Field → module map (base stays on master)
- **Master (do NOT move):** `Assigned by the scene builder`, `Look / surfaces`, `Water volume (placement)`,
  `Large-water sim window`, `Water body (multi-instance)`, `Performance`, `Camera`, `Splash`,
  `Simulation`(`lightDir`,`causticResolution`). These are placement/identity/schedule and some are read
  by `WaterVolumeEditor`.
- **OceanModule** (largest, and inactive on the pool test scene → lowest regression risk, good first
  target): `Open water`, `Ocean clipmap`, `Ocean god rays`, `Ocean foam` (+ the swell/heading derived
  `internal` accessors already grouped near them).
- **WaterFogModule:** `Water fog (Beer-Lambert)` (`waterFog`,`fogColor`,`fogExtinction`,`fogDensity`,`waterOpacity`).
- **DepthAttenuationModule:** `Depth attenuation (downwelling)` (publisher-only reads, no public API — clean 2nd target).
- **BedDepthModule:** `Bed depth` (pairs with the existing `WaterBedBaker` / lazy `BedBaker`).
- **WindWaveModule:** `Wind waves (spectral)` (read by publisher, buoyancy, `WaterWaveBank`).
- **FoamModule:** `Foam` (read heavily by `WaterUniformPublisher`).
- **RippleModule:** `Ripple tuning` + `Object interaction`.
- **ReflectionModule:** `Reflections` (`reflectionMode` has public `Reflections` prop + `ApplyReflections`).

Suggested order: DepthAttenuation (smallest clean proof) → Ocean (biggest win, low risk) → WaterFog →
BedDepth → WindWaves → Foam → Ripple → Reflections. Tree compiles after each; stop anywhere.

### After settings move, Phase 3 (optional): per-frame Tick
Add `Tick(WaterContext, float)` to `IWaterModule`, lift camera/waveTime/wind/simWindow onto
`WaterContext` (refresh once per frame at the top of `Update`), and move each module's per-frame call
off `Update` into its `Tick` — done **with the editor open**, one module at a time, because it touches
the render/sim schedule and only pixels can confirm the interleaving stayed identical. The master keeps
owning the multi-collaborator orchestration (`Step`, `RenderCausticsForThisBody`) as "the sim schedule".

---

## TL;DR
Phase 1 (framework + collaborator lifecycle) is in and should be behaviour-identical — please compile +
Play to confirm. Phase 2 (the field migration that truly slims the class) is recipe-ready; let's run it
together with the editor open so we can watch your tuned values survive each feature.
