# WebGpuWater — Optimisation Audit
**Target:** Unity URP → WebGPU browser build · **Scope:** `Packages/com.abstractocclusion.webgpuwater/Runtime/` (157 files: 102 C#, 19 `.shader`, 32 `.hlsl`, 3 `.compute` — ~33k lines) · **Date:** 2026-07-29

**No code was changed.** Every finding is read-only and awaits your go-ahead.

## Verification legend

| Mark | Meaning |
|---|---|
| ✅ | Claim re-read adversarially by a second pass and confirmed exactly as stated |
| ⚠️ | Confirmed but a specific number/detail was **wrong** — the correction is inline |
| ❌ | **Refuted.** Listed so it does not get re-proposed |
| ❓ | Mechanism certain, magnitude not measurable from source — needs a capture |

Estimates that could not be derived from the source are marked as estimates. Nothing here is guessed.

---

## 0. The finding that changes everything else

### ✅ **[A-1] The shipped demo scenes pin `ForceHigh`, so the entire quality-tier system never runs on WebGPU**

`Samples/Demos/Common/HighWaterQuality.asset` → `selection: 3`, and `WaterQuality.cs:13` `enum Selection { Auto, ForceLow, ForceMedium, ForceHigh }` → **3 = ForceHigh**.

I checked the GUID references across the 18 demo scenes directly on disk:

| Asset | `selection` | Scenes referencing it |
|---|---|---|
| `HighWaterQuality` | `3` = **ForceHigh** | **10** |
| `MidWaterQuality` | `2` = ForceMedium | 4 |
| `LowWaterQuality` | `1` = ForceLow | 1 |

The capability probe that would force Low on a web build —

```csharp
// WaterQuality.cs:255-260
bool constrained = Application.platform == RuntimePlatform.WebGLPlayer
                   || Application.isMobilePlatform
                   || !SystemInfo.supportsAsyncGPUReadback;
if (constrained) return Low;
```

— is only reachable through `Selection.Auto`. It is **dead in 15 of 15 scenes that assign an asset**.

And a scene with **no** asset is no better: `WaterVolume.Quality.cs:128` `if (quality == null) return;` bails before the probe, and `WaterVolume.State.cs:32-48` initialises every runtime knob from `WaterQuality.Default`, which is the desktop configuration:

```csharp
// WaterQuality.cs:36-45
const int DefaultRefineSteps = 5;
const int DefaultCausticInterval = 1;
const int DefaultReadbackInterval = 1;      // request the height readback every frame
const int DefaultOceanFftInterval = 1;
const UnderwaterMode DefaultUnderwaterMode = UnderwaterMode.Full;
// + RichReflections = true, RealRefraction = true, RenderScale = 1, MaxWaveCount = 16
```

**Why this comes first:** roughly a third of the findings below are already mitigated by the Low tier — refine steps 5→2, wave count 16→8, readback interval 1→3, caustic interval 1→2, FFT interval 1→2, render scale 1→0.7, fog Full→Simple, planar/SSR off. Right now **none of that mitigation is reachable in a WebGPU build of the demos.**

**Fix (two parts, both small):**
1. Set `HighWaterQuality.asset` (and Mid) to `Selection.Auto` — the probe then picks the right tier per device and still lands on High on a desktop editor. `ForceHigh` should be a debugging affordance, not the shipped default.
2. In `ApplyQuality`, when `quality == null`, run the probe anyway and only let an assigned asset *override* it. A no-asset WebGPU build should not be a desktop build.

**Risk:** the demos will look different in a web build — which is the point. Release-note it.

**Caveat I could not settle:** Unity's docs do not state which `RuntimePlatform` a WebGPU build reports. If it is not `WebGLPlayer`, the probe misses WebGPU entirely and you also want `SystemInfo.graphicsDeviceType == GraphicsDeviceType.WebGPU` in the `constrained` test. There is **no `graphicsDeviceType` check anywhere in the Runtime assembly** — verified by exhaustive grep. Worth adding regardless.

---

## 1. Correctness bug found while auditing passes

### ✅ **[B-1] The after-fog particle pass binds camera colour as `Write`, which can discard the rendered scene**

```csharp
// Rendering/WaterUnderwaterFogFeature.cs:127
builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.Write);
```

Every other colour-compositing pass in the package uses `ReadWrite` — `WaterUnderwaterFogPass.cs:121`, `:194`, `WaterCausticProjectionPass.cs:96`, `LargeBodyAtmospherePass.cs:208`. And the sibling pass carries a comment describing **this exact failure, already hit once**:

```csharp
// LargeBodyAtmospherePass.cs:206-208
// ReadWrite (not Write): the Read half forces the rendered scene to be LOADED before the
// additive Blend One One, instead of discarded (Write alone left the screen black).
builder.SetRenderAttachment(cameraColor, 0, AccessFlags.ReadWrite);
```

The draws inside the offending pass are alpha-blended and therefore require the destination: `SplashParticles.shader:52` and `WaterSurface.shader:377` are both `Blend SrcAlpha OneMinusSrcAlpha`. `AccessFlags.Write` lets RenderGraph emit `LoadAction.DontCare`; an immediate-mode backend usually ignores that, a tile-based one — which is what a browser WebGPU implementation on Apple silicon or mobile is — genuinely discards the tile.

The other two `Write` colour attachments (`WaterUnderwaterFogPass.cs:167`, `LargeBodyAtmospherePass.cs:177`) are freshly-created `clearBuffer = true` offscreen targets, so they are correct and not counterexamples.

**Fix:** one word, `Write` → `ReadWrite`. **Risk:** none, `ReadWrite` is strictly more conservative.

---

## 2. High impact — CPU / main thread

WebGPU builds are usually main-thread bound. These three dominate.

### ⚠️ **[C-1] ~3,200 uniform writes per frame on a default ocean, ~97 % of them unchanged**

`WaterUniformPublisher.WriteBodyUniforms` (`:367-590`) contains **138** `sink.Set*` calls — verified by count. `WriteBodyProps` (`:280-285`) does `mpb.Clear()` then runs all 138.

It has 8 call sites. On an ocean the multiplier is the clipmap:

```csharp
// WaterVolume.OceanClipmap.cs:94-102 — called for level.above AND level.under
WriteBodyProps(block);
block.SetFloat(ID_IsClipmap, 1f);
block.SetFloat(ID_PatchDepthBias, level.depthBias);
block.SetFloat(ID_ClipmapMorphStart, level.morphStart);
block.SetFloat(ID_ClipmapMorphScale, level.morphScale);
renderer.SetPropertyBlock(block);
```

**`ClipmapLevelCount` = 9 at shipped defaults** — re-derived independently: `clipmapGridResolution 64` → `HoleHalfCells = 64/4−2 = 14`; `BaseCell = 0.9×32/14 = 2.057`; `Level0Reach = 32×2.057 = 65.83`; `1 + ceil(log2(10000/65.83)) = 1 + ceil(7.247) = 9`.

So per frame: 1 body block + 2 patch blocks + 18 clipmap blocks (9 levels × above/under) + 1 global mirror = **22 × 138 ≈ 3,040 writes**, plus the 52 shore globals (C-2) and the shared set.

Of the 138, the bucket split is:

| Bucket | Count | Share |
|---|---|---|
| (a) genuinely changes per frame | **3** on a pool, 4 windowed (`_WaveTime`, `_WaterTex`, `_FoamMask`, `_SimCenter`) | 2.6 % |
| (b) inspector settings | 118 | 85.5 % |
| (c) constant after init | 17 | 12.3 % |

**⚠️ Correction to the first pass:** all **4** of the level-specific values are immutable after `CreateOceanClipmap` (`WaterVolume.OceanClipmap.cs:172-179`), not 3 of 4. Only `depthBias` genuinely varies per level; `morphScale` is identical across levels and `_IsClipmap` is always `1`. So of 142 writes per level, **exactly 4 could change per frame and 138 cannot.**

There is **no dirty-flag infrastructure anywhere in the package** — no `OnValidate` in the entire Runtime assembly, and the one `_settingsVersion` field (`WaterVolume.Settings.Migration.cs:30`) is an asset-migration counter.

**Fix:** keep a persistent per-level `MaterialPropertyBlock`, write the shared body values into it once (and again only on a settings-dirty flag), and per frame touch only `_WaveTime`, `_WaterTex`, `_FoamMask`, `_SimCenter`. Dropping the `mpb.Clear()` is most of the fix.

**Risk (medium):** `mpb.Clear()` is load-bearing for the documented contract at `WaterVolume.Update.cs:137-139` ("the block is cleared, so any per-object look must live in the material") and for the 14 conditional `SetTexture` calls, which currently unbind by clear-then-skip. A persistent block needs an explicit unbind path.

Also inside this block, re-derived 22× per frame for no reason (`WaterUniformPublisher.cs`): `ResolveReflectionCube()` at `:526` → `RenderSettings.skybox` + `HasProperty` + `GetTexture` (3 native round-trips); `Matrix4x4.Rotate(_body.VolumeRotation)` at `:396`; `WaterTexel`'s two divides on a value fixed at init.

---

### ✅ **[C-2] `WaterShoreDepthField.Publish()` fires 52 globals per body per frame with no gate — the cheapest win in the codebase**

```csharp
// WaterShoreDepthField.cs:133-137
internal void EnsureBakedAndPublish()
{
    if (_body.useBedDepth && !_bakeAttempted) Rebake();
    Publish();          // <- the gate covers only Rebake
}
```

Called every frame per body from `WaterVolume.Update.cs:66`, with no `isPrimary` gate (contrast `:94`). `Publish()` (`:432-523`) issues exactly **52** `Shader.SetGlobal*` calls — that is every SetGlobal in the file. **One** changes per frame (`_SurfBeatTime`, `:469`). Several are literal constants: `SetGlobalFloat(ID_SurfFoamRepartActive, 1f)` at `:510`, plus two debug flags. Two static baked `Texture2D`s are rebound every frame (`:439`, `:447`, `:501`).

A plain swimming pool with `useBedDepth = false` — the default — pays all 52 per body per frame to publish `_ShoreDepthValid = 0` alongside 40 surf knobs no shader will read. That is **25 % of all per-frame globals in a single-body scene.**

**Fix:** early-return after publishing the two `*Valid = 0` flags and the black-texture fallbacks once; the remaining ~40 behind a settings-dirty flag. **Risk:** low — the valid flags already gate every consumer (`WaterShore.hlsl:59,82`).

---

### ✅ **[C-3] Shore/surf data goes through device-wide globals sourced per body — last writer wins, nondeterministically**

All 52 globals above are `Shader.SetGlobal*` sourced from `_body`, a per-body instance. The only disambiguator is a binary opt-in flag:

```csharp
// WaterUniformPublisher.cs:539-542
// Per-body gate on the SHARED _Shore*/_Surf* globals ... only the body that opted into
// bed depth reads the shore substrate
sink.SetFloat(ID_ShoreBodyGate, _body.useBedDepth ? 1f : 0f);
```

`_ShoreBodyGate` distinguishes *opted-in* from *not*. It carries **no body identity**. Two bodies with `useBedDepth = true` both publish their own surf period, amplitude and `SurfBeatTime` every frame; whichever `Update()` ran last wins for both. Unity does not order `Update()` deterministically among MonoBehaviours sharing an execution order, so the winner can differ between runs and between editor and player.

This is a correctness hazard *and* (N−1)/N wasted work. Notably the team already fixed this exact class twice — `_WaveTime` and `_Sky` were both migrated out of shared globals for this reason (`WaterUniformPublisher.cs:211-218`). The shore family was not.

**Fix:** move the shore/surf family onto the same `IUniformSink` per-body path, with the primary mirroring to globals for membership-less receivers — the pattern already proven for `_WaveTime`.

---

### ✅ **[C-4] `WaterMembership.LateUpdate` republishes the full 138-uniform block onto every floating object, every frame**

```csharp
// WaterMembership.cs:33-44
WaterVolume body = WaterVolume.BodyContaining(transform.position);
...
body.WriteBodyProps(_mpb);
_renderer.SetPropertyBlock(_mpb);
```

138 native writes + a block copy per object per frame, plus a `BodyContaining` scan that runs a `Quaternion.Inverse` per body. No visibility check, no distance check, no dirty flag. `AutoLinkReceivers` adds this component to every scene renderer using a water material, so the count is scene-driven: **50 wet objects ≈ 6,900 property writes/frame** for membership alone.

**Fix:** cache the body's block once per frame on the `WaterVolume` and have memberships assign the shared instance; re-resolve `BodyContaining` only on meaningful movement.

---

### ✅ **[C-5] `WriteBodyProps` is called from inside render-graph pass bodies — per body, per pass, per camera**

```csharp
// Rendering/WaterCausticProjectionPass.cs:104-116
builder.SetRenderFunc((PassData d, RasterGraphContext ctx) => {
    for (int i = 0; i < d.bodies.Count; i++) {
        body.WriteBodyProps(d.block);            // all 138
        d.block.SetFloat(ID_ScreenCausticIntensity, body.screenCausticIntensity);
        CoreUtils.DrawFullScreen(ctx.cmd, d.material, d.block, d.shaderPass);
    }});
```

The projection consumes roughly 6 of the 138. `SkipCameraFullscreen` excludes only Preview and Reflection, so **Game view and Scene view both pay**, as does every camera in a stack. Editor, one body, shadow pass on: 2 cameras × 2 passes × 139 = **556 writes/frame** on top of everything else. Same pattern at `WaterChunkDepthPass.cs:93`.

The adjacent `WaterUnderwaterFogPass.cs:171-181` also does `renderer.GetComponent<MeshFilter>()` **and** a full `GetPropertyBlock` copy per ocean surface renderer, per camera — ~21 of them on a default ocean, inside the render function.

**Fix:** a small purpose-built block with only the values each pass reads, filled once per frame.

---

### ✅ **[C-6] `LargeWaveField` recomputes per-component wave constants inside the per-sample-point loop**

```csharp
// LargeWaveField.cs:457-483 — all of these depend only on n and uniforms
float headingJitter = (Hash(fn + phaseSeed) * 2f - 1f) * dirSpread;   // Hash = Mathf.Sin
float directionX = Mathf.Cos(heading);
float directionZ = Mathf.Sin(heading);
float phaseOffset = Hash(fn + phaseSeed + 16f) * TwoPi;                // Mathf.Sin again
float wavenumber  = TwoPi / Mathf.Max(wavelength, 1e-3f);
float angularSpeed = Mathf.Sqrt(Gravity * wavenumber);
```

`EvaluateAtQuery` runs `EvaluateBands` **5×** (4 for `InvertToSource`'s chop inversion + 1 final), and each covers 16 components → **80 component iterations × ~6 avoidable transcendentals ≈ 480 wasted `sin`/`cos`/`sqrt` per sampled point**, on top of the 160 genuinely position-dependent ones.

Hit by: every buoyancy probe, every `TryGetAnalyticWaterline`, every splash droplet, and 5 fog-gate corner samples per frame. Gated on `openWater`. The file's own docstring (`:623-631`) already concedes the shipped stress spawner burns ~2,500 redundant passes per `FixedUpdate`; this is a second multiplier on top.

**Fix:** a per-frame table of `{dirX, dirZ, k, omega, phaseOffset}` for the 16 components — exactly the shape `WaterWaveBank._waves` already uses. **Risk:** the CPU mirror must stay byte-identical with `WaterLargeWaves.hlsl`, and `WaterWaveConstantsValidator` guards that pair.

Related, same file: on an FFT ocean every surface sample pays a **full 5-pass analytic evaluation purely for the vertical rate** (`WaterVolume.Waves.cs:78-85`), even though the height came from the cheap GPU readback and the height-only overload discards the rate. That makes the FFT path more CPU-expensive than the analytic fallback it replaced.

---

## 3. High impact — GPU

### ✅ **[D-1] Async readback IS live on WebGPU, and it is a 1–1.5 MB `RGBAFloat` transfer every frame, demand-ungated**

Unity's own docs confirm `AsyncGPUReadback` is the *supported* path on the WebGPU backend (synchronous readback is what is unsupported). So this is not hypothetical on your target.

```csharp
// WaterVolume.Update.cs:104-108
if (_simulate && Time.frameCount % _readbackInterval == 0) {
    _sampler.RequestReadback();
    if (IsOceanClipmap) _oceanFft?.RequestHeightReadback();
}
```

Source is the full `ARGBFloat` sim state (`WaterSimulation.cs:183`). For a bounded body `_simRes` comes from the ripple-quality table, default `RippleQuality.High` → **256–320 texels × 16 B = 1.0–1.56 MiB per readback**. With `_readbackInterval = 1` (the shipped default, per A-1) that is every frame, per body, up to 4 bodies = **~4–6 MB/frame of GPU→CPU traffic**. Then another full-size managed memcpy on the main thread:

```csharp
// WaterSurfaceSampler.cs:46-49
var data = req.GetData<Color>();
if (_heightCpu == null || _heightCpu.Length != data.Length) _heightCpu = new Color[data.Length];
data.CopyTo(_heightCpu);
```

There is **no check that anyone wants the data.** A scene with decorative water and zero floaters pays in full.

**Fix (two independent wins):**
- Demand-gate it: consumers (`WaterBuoyancy`, `WaterProbe`, gameplay queries) bump a per-body "wanted this frame" stamp; skip the request when nobody asked.
- Read back less: the CPU only consumes `.r`, `.b`, `.a` (`WaterSurfaceSampler.cs:81-82`). A compute bake into a half-res `RGBAHalf` cuts the transfer **4–8×** — exactly what `WaterOceanFft` already does with its 128² `RFloat` height field, which is the model to copy.

---

### ⚠️ **[D-2] Every ripple drop is its own full-grid ping-pong dispatch**

```csharp
// WaterSimulation.cs:463-471
public void AddDrop(float x, float y, float radius, float strength) { ... Dispatch(_kDrop); }
// :249-256
_cs.SetTexture(kernel, ID_Src, _a); _cs.SetTexture(kernel, ID_Dst, _b);
_cs.Dispatch(kernel, _groups, _groups, 1);
(_a, _b) = (_b, _a);
```

The `Drop` kernel writes all 65,536 texels (`WaterSim.compute:336`) to stamp a disc a few texels wide. There is **no drop array uniform and no per-frame budget at the `WaterSimulation` level** — only a per-component one.

**⚠️ Correction:** the per-component cap is **4, not 5**. `WaterInteractable.cs:116` `int budget = MaxDropsPerFrame;` (=4) is *shared*: the vertical loop decrements it and the horizontal drop is guarded by `budget > 0` (`:133`), so 4 vertical emits leave nothing for the horizontal. Max **4N**, not 5N.

Ten interactables bobbing on one lake = up to **40 extra full-grid dispatches/frame**, each reading and writing 1 MB. On WebGPU each `Dispatch` is a separate `beginComputePass` + bind-group rebuild, so the CPU overhead per dispatch is materially higher than native.

**Fix:** accumulate the frame's drops into a `float4[]` uniform (centre.xy, radius, strength) and run **one** dispatch that loops the count. The stamp is additive, so the result is identical. **Risk:** low.

Same shape, worse: **`SphereInteract`** (`WaterSim.compute:344-394`) ping-pongs **both** the height field and the foam buffer — 3 MB of traffic per dispatch — where the early-out at `:358` is a *copy*, not a skip, and a typical boat affects ~1 % of the grid area. Fix by dispatching only the sphere's bounding rectangle of thread groups.

---

### ✅ **[D-3] Dead VRAM: 8.4 MB per body allocated for features that are off by default**

| Allocation | Size @ default | Gate | Actually used? |
|---|---|---|---|
| Obstacle chain, 6 RTs (`WaterObstacle.cs:72-78`) | **3.375 MB** | `obstacleShader != null` | Only in `FootprintDelta` mode; default is `MouseLikeDrops` |
| Caustic RT 1024² ARGB32 (`WaterCausticsPass.cs:80-89`) | **4.0 MB** | `Enabled => true` | `CausticFrame.None` bodies never write it — the enum's own comment says so |
| Foam ping-pong RGFloat (`WaterSimulation.cs:189-191`) | **1.0 MB** | always | `foam = false` and `wetnessMemory = false` by default |

The `Enabled` predicates key on "shader wired" rather than "feature selected". At `ActiveSimBudget = 4` bodies that is up to **~33 MB of resident dead VRAM in a browser tab** — the tightest memory environment the package targets.

The caustic case is also a latent correctness hazard: `WaterVolume.Solver.cs:266-270` documents that the RT "is allocated in the caustic pass's constructor and never even cleared — so its contents are undefined and every consumer must contribute its identity instead of sampling it."

**Fix:** make each module's `Enabled` match the predicate that actually consumes it. Split the reflector-only `_solid` target (0.125 MB) out of the obstacle chain so `MouseLikeDrops` + reflectors pays 0.125 MB instead of 3.375 MB.

---

### ✅ **[D-4] Ocean clipmap under-twins are drawn every frame regardless of eye side — ~38k wasted heavy vertices**

```csharp
// WaterVolume.OceanClipmap.cs:196-200 — both twins, no camera-side test
SetRendererEnabled(_clipmapLevels[i].above, on);
SetRendererEnabled(_clipmapLevels[i].under, on);
```

The two are coincident meshes with opposite cull modes — `WaterSurface.shader:287-289`: *"The above and under sheets are COINCIDENT twins with opposite culling."* Backface culling happens **after** vertex shading. Each level's template is 65×65 = 4,225 vertices running the full displacement path (`WaterMeshBuilder.cs:3-5` documents "4 texture fetches plus the wave-bank sines PER VERTEX").

9 levels × 4,225 = **~38k vertices of the most expensive vertex shader in the package, shaded to produce zero fragments, every frame** — and doubled again by the fog surface-depth prepass, which collects both twins.

**Fix:** gate the twin enable on the existing `CameraSubmerged` / `WaterlineActive` flags (both already hysteresis-maintained), enabling both only while the waterline pass is armed. **Risk (medium):** a distant wave trough can put a far patch on the "wrong" side of the eye even with the camera clearly above water — keep both on whenever the waterline is armed, plus a wave-amplitude margin.

---

### ✅ **[D-5] The exclusion depth prepass costs 2 camera-sized depth passes on every camera including reflection cameras**

`Rendering/WaterExclusionDepthPass.cs:75-76`, with `builder.AllowPassCulling(false)` at `:95` so RenderGraph cannot eliminate them. Gated on `AnyPrepassVolumeActive()` — true for Box and Sphere too, and `Shape.Box` is the default.

> ### ❌ **RETRACTED: "nothing reads the result."** That part is **false** and must not be re-proposed.
>
> Two shader consumers read the prepass depth **without** the `_ExclusionMeshCount` gate: `LargeBodyGodRays.shader:326-327` gates on `_ExclusionCount` (which counts *all* shapes, `WaterUniformPublisher.cs:233`), and `WaterUnderwaterFog.shader:393` calls `ExclusionPrepassExitDistance` with no mesh gate at all — its own comment at `:387-388` says the fact *"only became available for Box and Sphere volumes when WaterExclusionDepthPass was widened past the Mesh tier."* A Box-only scene **does** consume the prepass.

What remains real: the passes run on **each planar-mirror camera** too (`WaterPassCameraGate.cs:36` deliberately lets `CameraType.Reflection` through). With the scene view open and one planar body that is 2 passes × 3 cameras = 6 depth passes per frame. If god rays and the fog carve are both off, the prepass genuinely has no consumer that frame — a runtime gate on *those two features* (not on mesh count) is the correct, safe optimisation.

---

### ✅ **[D-6] SRP Batcher is off for every water renderer**

Every water draw carries a non-empty `MaterialPropertyBlock` (`WaterVolume.Update.cs:146`, `OceanClipmap.cs:107`, `Chunk.cs:302`, `SimWindowPatch.cs:60`) or is a `Graphics.DrawMesh` with an MPB (`WaterExclusionVolume.cs:319`) — both exclude the renderer from SRP batching. On a stock ocean that is **20 un-batched renderers**, each a separate `SetPass` + full constant-buffer upload, per camera, plus a second time by the fog prepass. `InstanceSurfaceMaterial` (`WaterVolume.SurfaceMaterials.cs:97`) additionally forks a `Material` per body.

The uniforms are per-body but **identical across all 20 renderers of one body** — only 4-5 floats are per-renderer.

**Fix:** move the per-body block to a `GraphicsBuffer` keyed by body index and pass the 4 per-level floats through instancing, so 18 clipmap levels become one `RenderMeshInstanced` call. **Risk (medium):** real architectural change. Do the clipmap levels first — they are package-created renderers with no user data — and leave the authored `surfaceAbove/Under` on the block path.

---

### ✅ **[D-7] The underwater-fog surface-depth prepass re-renders the whole ocean at full res on the tier that never reads it**

`Rendering/WaterUnderwaterFogPass.cs:73-84` records the prepass whenever the primary `IsOceanClipmap`, with **no tier check**. The shader's Simple branch never touches the result:

```hlsl
// WaterUnderwaterFog.shader:552-558
if (_UnderwaterFogSimple > 0.5)      OceanFlatPath(...);        // never reads the prepass
else if (_OceanSurfaceDepthValid > 0.5) OceanPrepassPath(...);
```

and Simple is the constrained tier's mode (`WaterQuality.cs:225`). The prepass creates two camera-sized targets (`R32_SFloat` + a separate `Depth32`, `:146-161`) and re-draws every live ocean surface renderer — **20 renderers, ~119k triangles / ~85k heavy-displacement vertices, per armed frame per camera**, with `AllowPassCulling(false)`.

**Fix:** skip it when the tier resolves to `UnderwaterMode.Simple`. Independently, drop it to half resolution — it feeds a low-frequency crossing height and the shader already `LOAD`s neighbouring rows.

---

## 4. High impact — fragment shaders

### ⚠️ **[E-1] The underwater fog computes everything twice: Absorb and Inscatter are separate full-screen passes calling the same function**

```hlsl
// WaterUnderwaterFog.shader:851 (FragAbsorb) and :885 (FragInscatter) — identical
float3 pathTransmittance = UnderwaterFog(input.uv, depthAttenuation, sunVisibility, armWeight, debugColor);
```

Both are recorded together at `WaterUnderwaterFogPass.cs:93-94` with **byte-identical builder configuration** (same colour ReadWrite, same depth read, same `UseAllGlobalTextures`) — two `AddRasterRenderPass` calls for two draws that could sit in one, forced apart only by the blend modes (`Blend Zero SrcColor` then `Blend One One`).

**⚠️ Two corrections to the first pass:**
- Not "unconditional" — both sit under `if (WaterVolume.UnderwaterFogActive)` at `:91`.
- Not always "the 40-step march". The march is a **three-way uniform selector** (`:552-559`) and it is the *fallback*: Simple tier takes `OceanFlatPath` (no march), and an ocean with a valid prepass takes `OceanPrepassPath` (a texture load, reaching the march only on carve pixels). Ponds never march at all — they pay only the 12-iteration `RefineSurfaceCrossing`. My first pass's "~660 fetches per pixel" figure was the worst case, not the common one.

What survives unchanged and is worth fixing: **the whole thing runs twice**, whichever branch it takes.

**Fix:** record both as two draws inside one `AddRasterRenderPass` (attachments identical, ordering guaranteed inside a pass) — free, zero risk. Then merge the maths into one draw with a premultiplied `Blend One OneMinusSrcAlpha` formulation, which is algebraically what absorb-then-add already produces.

---

### ⚠️ **[E-2] `SurfaceHeightAtXZ(cam.xz)` is evaluated per pixel although the argument is screen-uniform**

```hlsl
// WaterUnderwaterFog.shader:160 (OceanWavyPath) and :248 (OceanPrepassPath) — the common path
float camSurf = SurfaceHeightAtXZ(cam.xz);      // cam = _WorldSpaceCameraPos (:545)
```

`SurfaceHeightAtXZ` is documented at `WaterWaterline.hlsl:11-13` as *"roughly SIX texture fetches per call on an ocean body"*, plus ~26 `sin`/`cos` from `EvaluateSurfWaves`. The compiler cannot hoist it (texture work). ×2 for E-1.

**⚠️ Correction:** the proposed one-line fix — substitute `_UnderwaterSurfaceY` — is **wrong**. They are not the same quantity. `_UnderwaterSurfaceY` comes from `WaterVolume.Underwater.cs:380-381` `float y = VolumeCenter.y; if (!openWater) return y;` — for a pond it is the flat rest plane with no wind-wave layer, and for an ocean it is a **1–2 frame stale readback**. And `LargeBodyGodRays.shader:207-211` explicitly says using it as the crossing answer *"is wrong"* — it uses it only as a first guess before iterating `SurfaceHeightAtXZ` 3×.

**Correct fix:** hoist the call to once per draw by publishing a *second, dedicated* uniform computed by the same CPU mirror the GPU function uses, so the waterline stays bit-consistent. Or compute it once in the vertex stage of the full-screen triangle and interpolate the constant. Do not reuse `_UnderwaterSurfaceY`.

---

### ⚠️ **[E-3] The peaked-refine loop is 52 dependent texture fetches on the main water fragment path**

```hlsl
// WaterSurfaceFragStages.hlsl:74-95
float4 info = SampleRipple(i.position, i.worldPos, fade);        // SampleWaterBicubic = 16 fetches
for (int k = 0; k < refineSteps; k++) {
    coord += info.ba * PEAKED_REFINE_STEP;                        // dependent
    info.ba = SampleWaterBilinear(coord).ba * fade;               // 4 fetches
}
if (refineSteps > 0) info.ba = SampleWaterBicubic(coord).ba * fade;  // 16 more
```

`SampleWaterBilinear` = 4 `tex2Dlod` (`WaterCommon.hlsl:40-43`); `SampleWaterBicubic` = 4× that = 16 (`:82-85`). At the shipped `DefaultRefineSteps = 5`: **exactly 52 fetches** (16 + 20 + 16). ⚠️ My first pass said "52–56" — 56 would need 6 steps; 52 is the number. On the Low tier (`lowRefineSteps = 2`) it is 40.

Every loop iteration is a *dependent* fetch — serialised texture-latency stalls, not throughput-hideable work. The file's own comment (`:35-36`) calls it *"the single biggest fragment cost on mobile."*

**Fix:** bake the refined normal into the sim RT's unused channels during the compute step, where it costs once per texel instead of once per pixel. Cheaper interim: on Low/Medium keep the loop's final *bilinear* result and drop the extra 16-fetch bicubic tail.

---

### ✅ **[E-4] `PondFoamOverlay` runs the entire geometry stage — including E-3's 52 fetches — before clipping on foam alpha**

```hlsl
// WaterSurface.shader:423-425
WaterGeomStage geom = EvaluateSurfaceGeometry(i);      // the full 52-fetch chain
FoamLayer foam = PondFoamLayer(i, geom);
clip(foam.alpha - FOAM_OVERLAY_MIN_ALPHA);             // rejects most fragments — too late
```

`PondFoamLayer` reads exactly **two** fields of that result: `g.normal` and `g.nxz` (`WaterSurfaceFragStages.hlsl:582-583`). Everything else — the refine chain, `ShoreSample`, `EvaluateSurfWaves`, the Gerstner/FFT sum, `DetailNormalTilt`, `EffectiveWaterRoughness` — is computed and discarded, and the overwhelmingly common "no foam here" fragment pays full price then throws it away. The pass draws whenever the fog is armed and the camera is in air, so this is effectively **rendering the surface pass twice**.

**Fix:** sample the foam mask first (`SimFoamCoverage`, 4 taps, independent of the normal) and `clip()` on that before any geometry work; then use a `refineSteps = 0` variant of the geometry stage, which is almost certainly indistinguishable at foam-relief scale.

---

### ⚠️ **[E-5] The analytic large-body path sums 16 Gerstner components per fragment, recomputing constants every one**

```hlsl
// WaterLargeWaves.hlsl:62,72 — 12 + 4 = 16, and neither count is tier-capped
#define LBW_WAVE_COUNT 12
#define LBW_SWELL_COUNT 4
```

Reached from the fragment stage at `WaterSurfaceFragStages.hlsl:130` on the `_OceanFftActive < 0.5` path — i.e. every large body that is not an unbounded FFT clipmap. Inside the loop, `heading`, `dir`, `phaseOffset`, `k` and `omega` depend **only on the loop index and uniforms** and are recomputed for every pixel.

**⚠️ Correction:** **5** hoistable transcendentals per component, not ~7 — 2 `sin` inside the two `LbwHash` calls, `cos`+`sin` of the heading, and 1 `sqrt`; `k` is a divide. The `sin(phase)`/`cos(phase)` at `:158-159` are genuinely per-fragment and must stay.

Still **~80 avoidable transcendentals per fragment**. The FFT path right beside it already proves the cheaper design works, and `WaterWaves.hlsl` already uploads `_WaveA`/`_WaveB` constant arrays for the wind-wave layer — the same pattern applied here removes ~5 of 7 ops per component with no visual change.

Note also that `LBW_WAVE_COUNT` is a `#define`, so **this loop is not quality-scaled at all**, unlike `_WaveCount`, `_GodRaySteps`, `_SSRMaxSteps` and `_PeakedRefineSteps`, which all are.

---

### ✅ **[E-6] The main water shader always contains `discard`, so it always forfeits early-Z**

Five clip sites, all gated by **uniforms**, none by `#if`:

```hlsl
// WaterSurface.shader
:180  if (InsideExclusion(i.worldPos)) discard;              // _ExclusionCount
:187  if (_ExclusionMeshCount > 0.5) ... :193 discard;
:201  if (_ChunkSphereClip > 0.5) ... :205 clip(...);
:211  if (_ChunkUseMesh > 0.5) ... :223, :229 clip(...);
// WaterSurfaceFragStages.hlsl:861 if (_UseBedDepth > 0.5 && _BedValid > 0.5) ... :904 clip(...);
```

A shader containing `discard`/`clip` forces late-Z on most desktop GPUs and essentially all tile GPUs, and WGSL gives no way to opt back in. So the package's most expensive shader, over its largest screen area, runs in full before depth can reject it — **on an ordinary pond with no exclusion volumes, no chunks and no baked bed.**

The uniform-driven design is deliberate and mostly excellent (`WaterSurface.shader:114-116` explains it keeps variant count down and updates live in the editor). This is the one place where a keyword buys back a hardware feature wholesale.

**Fix:** one `shader_feature` (`_WATER_CLIPS`) compiling all five sites out for bodies with none of those features. Two variants; the clip-free one regains early-Z. **Risk:** low — the per-body state is already tracked in C#.

---

### ✅ **[E-7] `WaterTerrain` samples all four substrates unconditionally**

`WaterTerrain.shader:261-271` takes 12 taps (3 planar albedo + 3 triplanar rock + 3 planar normals + 3 triplanar rock normals) then weights them at `:266`/`:275`. The weights (`WaterTerrainSubstrate.hlsl:54-64`) make seabed and grass mutually exclusive outside the feather band, and `rock` is exactly 0 below `rockSlope − slopeFeather` — so **6 of 12 taps are multiplied by literal zero on any flat ground, typically 9 of 12.** Terrain covers a large fraction of a coastal frame.

**Fix:** hoist `ddx/ddy` of `planarUV` once in uniform flow, switch to `SAMPLE_TEXTURE2D_GRAD`, then branch each substrate on `w > epsilon`. Divergence at boundaries costs the same as today's unconditional path, so it can only win.

---

## 5. Shader variants and browser first-load

**Calibrated headline: this is not a variant-explosion case.** Total ≈ **252 graphics variants + 31 compute pipelines**. There is no `Fallback` (which would inherit URP/Lit's thousands), no `multi_compile_instancing`, no `multi_compile_fog`, no `LIGHTMAP_ON`, no `_ADDITIONAL_LIGHTS`, no `_FORWARD_PLUS`, and quality tiers are uniform-driven rather than keyword-driven. By commercial-water-package standards that is lean, and the uniform-over-keyword call is the single best variant decision in the package.

The avoidable waste is concentrated in three mechanical fixes:

### ✅ **[F-1] Two pragmas where URP uses one — 70 of 252 variants (28 %) are unreachable states**

All three URP-lit shaders carry, literally:

```hlsl
// AnalyticPool.shader:46-47, WaterReceiver.shader:52-53, WaterTerrain.shader:105-106
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_SCREEN
```

URP's own `Lit.shader` declares these as **one 4-way set**. As two pragmas the compiler emits the 3×2 = 6 cross product; URP sets them mutually exclusively, so `CASCADE+SCREEN` and `SHADOWS+SCREEN` can never occur. AnalyticPool 120→80, WaterReceiver 60→40, WaterTerrain 30→20.

**Fix:** merge each pair into URP's canonical single 4-way `multi_compile`. Three-line edit, behaviour-identical, **−70 variants**.

### ✅ **[F-2] Fragment-only keywords declared unscoped — up to 24× redundant vertex programs**

Verified that **no vertex function references any of them**: `_MAIN_LIGHT_SHADOWS*` in AnalyticPool/WaterReceiver/WaterTerrain/GodRays/LargeBodyGodRays/WaterSurface, and `_USECUSTOMTILES`/`_AUTOTILE`/`_AUTOTILEOBJSIZE`. Every consumer is in `frag`.

And the package **already uses the scoped forms elsewhere** — `multi_compile_fragment` at `AnalyticPool.shader:48`, `WaterReceiver.shader:54`, `WaterTerrain.shader:107`; `multi_compile_vertex` at `WaterReceiver.shader:349`, `WaterTerrain.shader:389` — so this is an oversight, not a design choice. AnalyticPool's ForwardLit compiles 24 identical vertex programs where 1 would do; `WaterSurface`'s vertex stage is the expensive displacement path, so its 3× is real bytes.

**Fix:** `multi_compile` → `multi_compile_fragment`, `shader_feature_local` → `shader_feature_local_fragment` at six sites.

### ✅ **[F-3] `WaterSurface` Pass 0's shadow keyword produces two byte-identical variants of the biggest shader in the package**

`WaterSurface.shader:113` declares the 3-way set, but the only preprocessor consumer is one `#if defined(_MAIN_LIGHT_SHADOWS) || defined(_MAIN_LIGHT_SHADOWS_CASCADE)` in `WaterSurfaceShadow.hlsl:13` — there is no cascade-only branch anywhere in the pass's include closure, and Pass 0 is `CGPROGRAM` including only `UnityCG.cginc`, so URP's `Shadows.hlsl` is not in it either. **Two of the three variants are byte-identical.** And the dimension multiplies the largest translation unit in the package (~392 KB of package HLSL before URP's own closure) to gate a single depth tap that is itself only a *fallback* when the occluder shader is not wired.

**Fix (risk: medium):** replace the keyword with an unconditional texture declaration plus a published float gate, binding a 1×1 white dummy when shadows are off. **The comment at `WaterSurfaceShadow.hlsl:16-21` records a real prior bug here** — mis-declaring this texture caused *"validation error → the whole WaterSurface bind group is invalid → black screen in builds."* Verify any change in an actual WebGPU player build, not the editor.

### ✅ **[F-4] No shader-variant stripping hook exists**

Exhaustive grep for `IPreprocessShaders`, `IPreprocessComputeShaders`, `ShaderKeywordFilter`, `ShaderVariantCollection` across `Editor/` and `Runtime/` returns nothing. Without one, every consumer's build ships the full declared cross-product — including `_MAIN_LIGHT_SHADOWS_SCREEN` in projects with no ScreenSpaceShadows feature, and all five `_SHADOWS_SOFT_*` tiers when a fixed web target uses exactly one. Those two dimensions alone are the 220-of-252 bulk.

**Fix:** an `Editor/` `IPreprocessShaders` dropping `_MAIN_LIGHT_SHADOWS_SCREEN` when no renderer has the feature, and the unselected `_SHADOWS_SOFT_*` tiers. **Risk:** stripping is unforgiving — over-pruning gives a pink shader at runtime with no build-time error. Ship it conservative-by-default.

---

## 6. Cheap, exact, low-risk wins

These are algebraically identical or provably dead. Each is a few lines.

| # | Site | What | Why it is free |
|---|---|---|---|
| ✅ G-1 | `WaterSim.compute:733` | `pow(_WetDrySurvival, _FoamDtSteps)` — **both args are uniforms** set once per dispatch (`WaterSimulation.cs:568,570`) | 65,536 transcendentals/frame for a constant. Compute it on the CPU. (The similar `pow` at `:718` is genuinely per-texel — leave it.) |
| ✅ G-2 | `WaterSurfaceFragStages.hlsl:250` | `length(refractedRay)` is **provably always 1.0** — `refract()` returns unit or zero, and the TIR-zero case is already replaced by the unit `reflectedRay` at `:213` | Dead `sqrt` per underside pixel. Also an intent-drift signal: the guard silently neutralised a look term. |
| ✅ G-3 | `WaterSurfaceSpecular.hlsl:367` | Sky aniso taps `normalize()` twice, and a **cubemap lookup is scale-invariant** so neither is needed for the sample | 5 wasted `rsqrt` per fragment on the default-on path (`_ReflectionAnisoStretch = 0.5`). Drop the outer one — bit-safe. |
| ✅ G-4 | `WaterExclusion.hlsl:251-262` | `ExclusionSunVisibility` computes a `refract` + setup **before** the volume loop, so it runs even when `_ExclusionCount == 0` and the function is a guaranteed `return 1.0` | Called once per march step — up to 64× per god-ray pixel in the common no-carve scene. Add an early-out; `LargeBodyGodRays.shader:484` already hoists the same term for its caustic path. |
| ✅ G-5 | `WaterVolume.hlsl:68-82` | `VolumeRot()` runs a 3×3 `determinant()` on every call to guard against a matrix C# has never published | ~3 calls per `SurfaceHeightAtXZ`, inside marches. Publish `_VolumeRot` pre-sanitised; also publish `1/extent` so `WorldToPool`'s three divides become multiplies. |
| ✅ G-6 | `WaterSurfaceDetailNormal.hlsl:54-55` | `normalize(_WindDirection.xy)` per pixel on a **uniform**, plus two complex multiplies | Publish the normalised direction from C#. ⚠️ The neighbouring "2 of 4 taps dead" claim is **partially wrong** — between 30 m and 120 m both pairs are live and blended; only outside that band is a pair dead. |
| ✅ G-7 | `WaterUniformPublisher.cs:526` | `ResolveReflectionCube()` does `RenderSettings.skybox` + `HasProperty` + `GetTexture` inside the 138-write block — **22× per frame on an ocean** | Cache against the skybox material reference. |
| ✅ G-8 | `WaterFoamParticles.cs:591,599` | `EnableKeyword`/`DisableKeyword` called every frame regardless of state change | Track last state; call on transition only. |
| ✅ G-9 | `WaterOceanFft.cs:460-470` | `BakeHeightField` dispatches on the **FFT** interval but is consumed on the **readback** interval — bakes at frames 2, 4, 8, 10 are never read at Low tier | Pass a `bakeHeightField` bool computed from `frameCount % _readbackInterval == 0 && _readback.CanRequest`. |
| ✅ G-10 | `WaterUniformPublisher.cs:211` | `PublishSharedGlobals` — documented as "global, not per body" — is called from **every** body's `Update`, redoing a selection sort and up to 4 array uploads N times | Frame-guard it exactly like `WaterSimScheduler.EnsureSchedule` (`:24`) already does. |
| ✅ G-11 | `WaterCausticProjectionPass.cs:81-83` | Two `AddRasterRenderPass` with **byte-identical attachment configuration**, differing only in shader pass index | Record both draw loops in one pass. Ordering inside a pass is guaranteed. Halves the pass count. Same for fog Absorb+Inscatter (E-1). |
| ✅ G-12 | `WaterMetricsOverlay.cs:54` | `OnGUI` allocates ~6 heap objects per pass (two `_text.ToString()`, a `GUIContent`, boxed `AppendFormat` args) and has **no `#if UNITY_EDITOR \|\| DEVELOPMENT_BUILD` guard** — it ships | GC pressure in the one component that claims to measure performance. |
| ✅ G-13 | `WaterOceanFftDebugView.cs:32-42` | A 128 KB `Graphics.CopyTexture` per **OnGUI event** (≥2×/frame), not per frame | `if (Event.current.type != EventType.Repaint) return;` — dev-only, but free. |

---

## 7. Structural items worth a decision

### ⚠️ **[H-1] `AsyncGPUReadback.WaitAllRequests()` on every body disable**

```csharp
// WaterVolume.cs:332, from OnDisable via DisposeModules
AsyncGPUReadback.WaitAllRequests();
```

A **global** synchronous drain — every outstanding readback in the process, not just this body's — fired on every scene load/unload, every `SetActive(false)`, every domain reload. On WebGPU the completion is a `mapAsync` promise that can only resolve when control returns to the JS event loop, which a blocking wait inside a WASM tick cannot pump. The comment's own justification is suppressing a console error the channel already absorbs (`AsyncReadbackChannel.cs:64-72`).

**Fix:** give the channel a generation counter checked in `OnCompleted` so a landed-after-teardown request is discarded silently, and delete the global wait (or `#if UNITY_EDITOR` it).

### ✅ **[H-2] Buoyancy returns `Valid = false` until the first readback lands — contradicting the documented contract**

`WaterSurfaceSampler.cs:84` `return false; // readback supported but not ready yet` propagates to `WaterBuoyancy.cs:195` `if (!sample.Valid) continue;`. But `Query/IWaterHeightSampler.cs:7-9` asserts the opposite: *"the implementation is CPU-analytic ... so a batched query returns valid data from frame 0 with no async GPU readback."* Since async readback **is** supported on WebGPU, this branch is live: objects spawned in the first frames free-fall briefly.

Second-order and worse: `_heightReady` is never cleared when a body stops simulating (frustum-culled or outside the sim budget), so a floater on an off-screen body rides an arbitrarily stale ripple field indefinitely.

**Fix:** return the analytic surface with `Valid = true` when the readback has not landed — that path already exists, it is just the `else if`. And clear `_heightReady` when a body stops simulating.

### ✅ **[H-3] Planar mirror is a full unamortised scene re-render with no LOD reduction**

`PlanarMirror.cs:150` `_reflectionCamera.CopyFrom(src)` copies the source camera wholesale: **no `lodBias`, no `shadowDistance` override, no `farClipPlane` reduction, no occlusion-culling change.** On an ocean the far plane is driven to `ClipmapOuterReach`, so the mirror inherits a ~16 km far plane. Resolution is 0.5 linear = 25 % of screen pixels, `DefaultHDR`, with `autoGenerateMips = true` regenerating the full chain every frame. Culling mask is `reflectLayers & src.cullingMask` with `planarExcludeLayers = 0` by default — everything except water is re-rendered. Updates every frame, unconditionally, from `beginCameraRendering`, up to `MaxActivePlanarBodies = 3`.

It is **off by default twice over** (`usePlanarReflection = false`, and `lowRichReflections = false` on the Low tier), which is the best default in the package — but per A-1 the Low tier is not currently reachable, so the second guard is inert.

**Fix:** re-apply `lodBias ≈ 0.5`, a reduced `shadowDistance` and a clamped `farClipPlane` after the `CopyFrom`; add every-Nth-frame amortisation (the RT already persists); refuse planar on web regardless of asset. In a 25 %-resolution, wave-distorted, mip-blurred reflection none of that is visible.

### ✅ **[H-4] `RTHandles.Alloc` inside `RecordRenderGraph`, keyed only on size**

`LargeBodyAtmospherePass.cs:151-161` `EnsureHistory` keys on width/height only, and `temporal` is true for **every** `CameraType.Game` — a stack overlay, split-screen, a minimap RT camera. Two Game cameras of different sizes alternate through it, so the RTHandle is `Release()`d and `Alloc()`ed **twice per frame** and `_historyValid` is reset each time, making the temporal blend permanently 0 — pure cost, zero benefit. Even at equal sizes the two cameras interleave `_prevViewProj`, so each reprojects against the other's matrix. And when the gate goes false at runtime, `_history` is never released.

**Fix:** key on camera identity as well as size (or use URP's per-camera `ContextContainer` history); release entries for unseen cameras; release from `AddRenderPasses` when the gate is false.

### ✅ **[H-5] Passes enqueued on component presence, not on there being work**

`WaterUnderwaterFogFeature.cs:53-62` enqueues the after-fog pass when `WaterFoamParticles.Live.Count > 0 || WaterSplashEmitter.Live.Count > 0` — but `Live` tracks **enabled components, not live particles**. The pass then sets `AllowPassCulling(false)` (`:132`) so RenderGraph cannot remove it even when the render function issues zero draws. An idle splash emitter costs a full render-pass begin/end with colour and depth binds, every armed frame.

**Fix:** sum actual `particleCount` and check `_afterFogArmed` before enqueuing; early-return before `AddRasterRenderPass` when the collected list is empty.

### ✅ **[H-6] Three-to-five `Graphics.ExecuteCommandBuffer` submissions per body per frame, outside the SRP frame**

`WaterCausticsPass.cs:111`, `:185`, `WaterObstacle.cs:164`, `:195` — each opens its own encoder, does `SetRenderTarget` + `Clear` + draws, and submits, outside RenderGraph's scheduling so nothing can be batched, aliased or reordered. Worst case 4 bodies × ~5 passes = **20 extra passes per frame** ahead of the actual render. Fill cost is trivial (256–1024² targets); the cost is entirely pass-boundary and encoder churn, which is precisely what WebGPU charges for.

**Fix:** move both into `ScriptableRenderPass`es at `BeforeRenderingPrePasses`. Preserve the ordering contract documented at `WaterVolume.Solver.cs:4-6`. The two `_cb.Blit` downsamples can also become one 4×4 box filter instead of two 2× bilinear passes.

### ✅ **[H-7] The FFT is structurally unthrottleable, and its vertical pass is uncoalesced**

`WaterCollaboratorModules.cs:112-113` constructs `WaterOceanFft` with `DefaultResolution`/`DefaultCascadeCount` — **no tier value is consulted**, and `FFT_SIZE 128` is a compile-time `#define` sizing the groupshared butterfly. The only knob is temporal (`OceanFftInterval`, capped at 4). So every FFT frame is ~278,500 thread invocations at every tier, on the platform where the tier system exists to shed exactly that.

Separately, `FftVertical` uses `[numthreads(1, 128, 1)]` (`OceanFft.compute:242`) — `col` is constant within a group and `row` varies, so a SIMD wave loads 32 texels that are 512 B apart in a row-major `R32_UInt` texture. `FftHorizontal` (`numthreads(128,1,1)`) has the ideal pattern; the vertical pass is its mirror image and pays for it on 3 loads and 6 stores. Also ~786 KB/frame of **functionally dead writes** at `:260-267`, kept only to preserve a storage-texture binding type.

**Fix:** add `OceanFftResolution`/`OceanFftCascades` to `Tier` — dropping to 3 cascades is a one-line tier change with no shader work. `FFT_SIZE` as a 2-way `multi_compile` is the bigger change and needs `WaterWaveConstantsValidator` updated in lockstep.

### ✅ **[H-8] `WaterShoreDepthField.Rebake` runs a CPU jump-flood SDF synchronously from `Update()`**

`WaterShoreDepthField.cs:170-299` — `res²` main-thread `Terrain.SampleHeight` calls, then a jump flood (`~65k texels × 8 steps × 8 neighbours ≈ 4.2 M inner iterations at res 256`, ~85 M at 1024), then two box blurs and an `res²` `SetPixels` + `Apply`. Plus a second independent `res²` sweep in `WaterBedBaker`. `_bakeAttempted` resets in `Dispose()`, so every disable/enable cycle re-pays it.

Not a GPU sync, but the same failure mode in a browser: an uninterruptible main-thread block inside one frame stalls the entire tab. Opt-in (`useBedDepth = false`), but it gates the whole shore/surf feature set, so any beach scene has it on.

**Fix:** bake at editor time — both fields are pure functions of static terrain plus the body transform — and ship the textures and CPU mirrors as serialized data.

---

## 8. Verified clean — do not re-litigate

Genuinely good work found while auditing. Recording it so nobody spends time here:

**WebGPU format discipline is excellent and correct.** `R32_UINT` packing for read-write spectra; `RFloat` not `RHalf` for foam history because *"r16f is NOT a WebGPU storage format"*; `RHalf` for the additive obstacle chain because *"base WebGPU cannot blend into float32 targets"*; half-float shore textures because *"float32 is not hardware-filterable on WebGPU"*; black-fallback binds everywhere so *"WebGPU never sees an unbound sampler"*; `GenerateMips` treated as best-effort. **Unity's own WebGPU limitations page confirms every one of these constraints.** These formats should not be "downgraded to half" — the reasoning is already right.

**Allocation hygiene.** No LINQ in any per-frame path. No `GetComponent`/`Find*` in the main tick (the two `Camera.main` uses are fallbacks). All ~180 shader property IDs are cached `static readonly int` — **zero string-literal `Set*` overloads anywhere**. `ProfilerMarker`, pooled query results, cached readback delegates, persistent uniform arrays, struct-based state. No per-frame `RenderTexture` allocation. Every RT is `Release()`d **and** `DestroyRuntime`d.

**Per-camera publishing of the main uniform set is correct** — the `beginCameraRendering` handler gates hard on `cam != targetCamera`, so the underwater/waterline globals publish once per frame, not per camera. That is the failure mode I expected to find and the package gets it right on the primary path.

**Volume conservation is fully GPU-resident** — a two-pass compute reduction whose mean is never read back, with a comment explaining it replaced a Blit+GenerateMips path specifically because of WebGPU. Exemplary.

**Compute correctness.** No thread group under 64 threads anywhere. No `numthreads`/C#-divisor mismatch — verified pair by pair with runtime guards. FFT butterfly count correct (7 = log2 128), ping-pong in groupshared (6,144 B, under the 16 KB limit). All barriers in provably-uniform straight-line control flow, with rationale comments accurate for WGSL/Naga. Hard substep clamp: the solver caps at 8 steps and slows the waves rather than death-spiralling.

**Off-screen bodies are culled from the sim** (frustum test + nearest-4 budget), and caustics/readback/FFT are all frame-interval amortised. The gating architecture is there — see A-1 for why it is currently inert.

**Variant discipline.** Include guards on all 32 `.hlsl` files. No `Fallback`, no Meta pass, no instancing/fog/lightmap pragmas. Expensive marches use `[loop]` with uniform, clamped trip counts, `[unroll]` reserved for small fixed counts. `GodRays.shader:51-53` deliberately omits `_SHADOWS_SOFT` with a written rationale — a reasoned 5× saving. The uniform-over-keyword choice for quality tiers is the single best decision in the package.

**God-ray marches are already well optimised** — per-step `exp()` chains hoisted into a multiplicative recurrence, `refractedSun` and the caustic reference plane hoisted, hard step clamps with an explicit note about unbounded dynamic loops causing device-lost on WebGPU.

**Foam sampling is correct throughout** — every tap inside a non-uniform coverage branch uses `tex2Dgrad` with caller-hoisted gradients.

**Debug code is mostly correctly stripped** — `WaterVolume.ObstacleDebug.cs` (the only blocking readback in the package) is `#if UNITY_EDITOR && WEBGPUWATER_DEV`; the FFT preview is `#if`-gated. The exceptions are G-12 and F-VAR-10 (`WaterDebugMode`/`WaterSurfaceDebug`/`WaterFogDebug` are uniform-branched and therefore reachable, so they ship in the WGSL payload — the right call for variant count, but there is no way to compile them out for release).

---

## 9. Suggested order of work

Ranked by (impact × confidence) ÷ effort.

| # | Item | Effort | Why here |
|---|---|---|---|
| 1 | **A-1** — quality assets to `Auto`, probe on null | Minutes | Unlocks every existing mitigation. Do this before measuring anything else. |
| 2 | **B-1** — `Write` → `ReadWrite` | One word | Correctness. Black-screen risk on the target backend. |
| 3 | **C-2** — gate `ShoreDepthField.Publish` | ~10 lines | 52 globals × N bodies × every frame, ~98 % dead. |
| 4 | **G-1, G-2, G-3, G-4, G-6, G-8, G-10** | ~1 line each | Algebraically identical or provably dead. |
| 5 | **F-1, F-2** — variant pragmas | ~9 lines | −70 variants, −24× vertex programs. Mechanical, matches URP convention. |
| 6 | **G-11 / E-1** — merge same-attachment passes | Small | Free pass-count halving in two places. |
| 7 | **D-1** — demand-gate + shrink the readback | Medium | Largest single per-frame bandwidth item, now confirmed live on WebGPU. |
| 8 | **C-1** — persistent per-level blocks | Medium | ~3,000 → ~90 writes/frame. Biggest CPU item; needs care around `mpb.Clear()`. |
| 9 | **E-4** — clip on foam mask before geometry | Small | Effectively stops rendering the surface twice. |
| 10 | **D-2** — batch drops into one dispatch | Small-medium | 4N full-grid dispatches → 1. |
| 11 | **D-3** — allocate on the consuming predicate | Small | ~8 MB per body of dead VRAM. |
| 12 | **E-3, E-5** — refine chain, Gerstner constants | Medium | The two biggest per-fragment items, but both touch look. |
| 13 | **D-4, D-6, D-7** — twins, SRP batching, fog prepass | Medium-large | Real wins, real architectural risk. |
| 14 | **H-3, H-4, H-6, H-7, H-8** | Large | Structural; schedule individually. |

**Measure before and after item 8.** Several ratings here (WebGPU cost per `SetGlobal`, per-dispatch overhead, whether `Graphics.ExecuteCommandBuffer` forces a queue submit, real overdraw for E-6) are reasoned from source, not measured. A browser GPU trace with 1 vs 4 water bodies settles all four at once.

---

## Sources

- [Unity Manual — Limitations of the WebGPU graphics API](https://docs.unity3d.com/6000.2/Documentation/Manual/WebGPU-limitations.html)
- [Unity Scripting API — SystemInfo.supportsAsyncGPUReadback](https://docs.unity3d.com/ScriptReference/SystemInfo-supportsAsyncGPUReadback.html)
- [Unity Scripting API — AsyncGPUReadback](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/Rendering.AsyncGPUReadback.html)
