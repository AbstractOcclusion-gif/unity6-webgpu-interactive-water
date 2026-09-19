# 05 - Connections, Water Wizard and Build Kit: how it works today, where the friction is

Read-only analysis, 2026-09-02. Every claim cites file:line in /tmp/ww; CONFIRMED = every consumer read, PLAUSIBLE = needs a runtime check. No code, design directions only.

Scope note: `WaterRuntimeRelevance.cs` (561 lines) is NOT part of the connection layer. It is the per-camera body ranking/budget snapshot (`Runtime/WaterRuntimeRelevance.cs:1-3`, `:86`) that `WaterSimScheduler` (`Runtime/WaterSimScheduler.cs:1-3`) and `WaterNetworkManagerWindow` (`Editor/WaterNetworkManagerWindow.cs:110-113`) read. It never touches `WaterConnection`/`WaterTopology` (grep `Connection|Topology|Port` in that file: 0 hits). `WaterSimLeasePool` is likewise body-only (`Runtime/WaterSimLeasePool.cs:3-7`). Both are relevant to a future "water system" window only as *diagnostics*, not as data model.

---

## 1. CONNECTION ANATOMY TODAY

### 1.1 The runtime types (what a connection IS)

| Type | File | What it stores | Who consumes it |
|---|---|---|---|
| `WaterConnectionPort` (MonoBehaviour) | `Runtime/WaterConnectionPort.cs:16-83` | `portId` GUID string (`:20`), `body` (`:23`), `river` (`:26`, wins over body `:52`), `authoredFlowRate` (`:30`), transform = anchor (`:43`) + downstream direction = `transform.forward` (`:45`) | `ResolveProvider()` (`:49-55`) is the only lookup: river first, then body, both gated on `isActiveAndEnabled`. |
| `WaterConnection` (MonoBehaviour) | `Runtime/WaterConnection.cs:13-107` | `portA` (upstream, `:19`), `portB` (`:22`), `transitionRadiusMeters` (`:27`, min 0.1 default 4), `authoredFlowRate` (`:31`) | Registers itself in `WaterTopology` on enable (`:62-72`), warns once when half-wired (`:64-70`). `IsWired` (`:42`) = both ports non-null - "the only state the seam blend consumes". |
| `WaterTopology` (static) | `Runtime/Query/WaterTopology.cs:14-167` | static `_connections` list + id counter (`:20-21`) | `ConnectionsOf` (`:29-40`), `TryGetPort` (`:47-64`), `ApplySeamBlend` (`:94-165`). |
| `WaterRiverEndConnection` (Serializable) | `Runtime/WaterRiver.cs:32-60` | per end: `body` (`:36`), `upstreamRiver` (`:40`), `transitionRadiusMeters` (`:44`), and the facade-OWNED generated objects `riverPort`/`targetPort`/`connection` (`:50-53`) | `WantsConnection`, `IsGenerated`, `IsPartiallyGenerated` (`:55-59`). |

Persistent identity: `portId` is minted GUID-once in `EnsurePortId` (`WaterConnectionPort.cs:73-77`) from `Reset`/`OnValidate` (`:68-69`) or explicitly by the facade for runtime-created ports (`WaterRiver.cs:698-701`). CONFIRMED consumers of `PortId` in-package: `WaterTopology.TryGetPort` (`WaterTopology.cs:57-58`) and the inspector help box (`Editor/WaterRiverEditor.cs:116`). Nothing else keys on it yet; it exists for game/streaming callers (the tests assert stability: `Tests/Runtime/WaterRiverFacadeFeatureTests.cs:98-123`, `:267-282`).

`BodyId` (session-only) is lazily assigned from one shared counter for both volumes (`Runtime/WaterVolume.Query.cs:152-153`) and ribbons (`Runtime/WaterRiverSurfaceProvider.cs:48-51`) via `WaterSurfaceProviders.NextBodyId` (`Runtime/Query/IWaterSurfaceProvider.cs:67`).

### 1.2 Runtime consumption: the seam blend

`WaterDomainResolver.FillSample` (`Runtime/Query/WaterDomainResolver.cs:273-305`) fills the winner's surface, applies the exclusion veto (`:295-300`), then calls `WaterTopology.ApplySeamBlend` (`:302`) on EVERY valid sample. The blend (`WaterTopology.cs:94-165`):

1. Scans all live connections; skips unwired or ones whose ports resolve to no provider (`:100-105`).
2. Finds the connection touching the winner by `BodyId` (`:107-110`).
3. Seam plane = port midpoint (`:131`), normal = portA->portB axis (`:117-130`), falling back to `portA.FlowDirection + portB.FlowDirection` when the two ports are coincident (the river-to-river stitch case, `:119-129`).
4. Inside `TransitionRadiusMeters`, blend = `0.5 * (1 - d/r)` (`:134-137`), samples the OTHER provider *at the seam plane* (`:138-145`) and lerps height/normal/velocity (`:147-153`), tagging `sample.Connection` (`:154-160`).
5. First matching connection wins; stacked seams are not averaged (`:161-163`).

Cost note (CONFIRMED from code, not measured): this loop runs per resolve call for every live connection, resolving both providers each time (`:103-104`). With 7 connections (the demo rig) and N buoyancy probes per frame that is 14 `ResolveProvider` calls per probe - trivial, but it is not pre-indexed by body.

### 1.3 Shader-side apron (UV1.w) - how it is fed

- The facade converts each generated end into a `BodySeamEnd` descriptor (`Runtime/WaterRiver.cs:566-613`): centre = `end.targetPort.transform.position` (`:572`), outward axis chosen from the body's pool-space border (`:592-601`), `LengthMeters = end.transitionRadiusMeters` (`:607`). Unbounded oceans get a tangent-extended frame instead (`:574-590`).
- `SyncSeams` pushes those two descriptors into `WaterRiverSurface.ConfigureBodySeams` (`WaterRiver.cs:553-564` -> `Runtime/WaterRiverSurface.cs:259-269`), which rebuilds the mesh only when the descriptors change (`:264-268`).
- `WaterRiverRibbonMeshGenerator.Populate` writes UV1.w = seam weight (1 on the body, fading to 0 across the terminal band) (`Runtime/WaterRiverRibbonMeshGenerator.cs:104-105`, `:228`, `:293`).
- The vertex stage reads it as `v.riverCurrentData.w`: `vertexRiverWeight = riverWeight * saturate(v.riverCurrentData.w)` (`Runtime/Shaders/WaterSurfaceVertStage.hlsl:618-621`), then fades ripples (`:660-661`), displacement (`:662-665`) and river disturbance (`:687-693`) by it.
- Per-end wave anchoring (so the apron cross-fades toward the RECEIVING body's wind waves rather than the parent's) is published per renderer via `PublishEndWaveAnchors` (`WaterRiverSurface.cs:713-750`) into `_RiverEndWaveAnchor0/1` (`Runtime/Shaders/WaterWaves.hlsl:383-397`). It reads `facade.SourceEnd.body` / `MouthEnd.body` directly (`:724`), not the ports.
- The whole property block is written every `LateUpdate` (`WaterRiverSurface.cs:187`, `:633-657`) and starts from the PARENT's `WriteBodyProps` (`:638-639`) - so the parent volume is what animates the ribbon.

Note the split: the *query* seam uses the ports' transforms; the *render* seam uses `targetPort.transform.position` plus `end.body` and `end.transitionRadiusMeters` (`WaterRiver.cs:569-572`, `:607`). Both read the same generated objects, but only through the facade.

### 1.4 Fog / medium handoff - how it finds the parent

- Submerged camera in a ribbon: `WaterVolume.TryResolveRiverFogOverride` (`Runtime/WaterVolume.Settings.Underwater.cs:137-162`) asks `WaterSurfaceProviders.ExtraProviderContaining` (`:145`, registry at `IWaterSurfaceProvider.cs:73-83`) for the ribbon, then `SelectRiverFogMediumBody` (`:175-203`) walks `ribbon.Body` (= `surface.waterVolume`, `WaterRiverSurfaceProvider.cs:55`) plus `facade.SourceEnd.body` / `MouthEnd.body` (`:185-186`) and picks the near side by spline `NormalizedT` with a 0.1 hysteresis band (`:171-172`, `:190-199`). Eligibility = `isActiveAndEnabled && fullscreenVolumeFog` (`:214-215`).
- Dry camera looking at a ribbon: `WaterRiverSurface.TryFindExternalFogSource` (`WaterRiverSurface.cs:541-560`) picks `surface.waterVolume` when `waterVolume.CanDriveExternalRiverFog` (`:595-602` -> `Runtime/WaterVolume.Underwater.cs:60-61`: `waterFog && fog mode != Off`). Consumed by `Runtime/Rendering/WaterUnderwaterFogFeature.cs:92` and `WaterUnderwaterFogPass.cs:378`.
- The river's medium therefore comes from THREE places: the parent (`WaterRiver.parentVolume` mirrored into `WaterRiverSurface.waterVolume`, `WaterRiver.cs:212-216`), and the two end bodies. None of them is the port.

### 1.5 The user's checklist: connect body A -> river R -> body B (as of today)

Assuming A and B already exist (each is a Frame+Renderers rig from `WaterBuildKit.CreateWaterBody`, `Editor/WaterBuildKit.Body.cs:80-139`):

| # | Step | Where it is consumed | If forgotten |
|---|---|---|---|
| 1 | Create R: `GameObject > AbstractOcclusion > River` (`Editor/WaterBuildKit.River.cs:16`, `:30-64`). Parent = selected body else `WaterVolume.Resolve()` (`:37-39`). Writes NEW materials into a NEW `Assets/WebGpuWater/Waters/Water N/Materials` folder (`:43`). | `CreateRiverRig` (`:78-107`) adds spline, current field, surface, facade in that order; appends the current field to the parent's `currentFields` (`:105`, `:109-121`). Mouth is auto-connected to the parent (`:56-60`). | Hand-adding `WaterRiver` works via RequireComponent (`Runtime/WaterRiver.cs:64-65`), but no material is assigned and no parent is adopted unless the surface already had one (`:142`). |
| 2 | Set `WaterRiver.parentVolume` (`WaterRiver.cs:91`). | Pushed to `WaterRiverSurface.waterVolume` in `ApplyWiring` (`:212-216`); current field appended to parent (`:225-248`); shader block seeded from parent (`WaterRiverSurface.cs:638`); fog fallback (`Settings.Underwater.cs:177`, `:202`). | Warning at enable (`WaterRiver.cs:728-731`), inspector info box (`Editor/WaterRiverEditor.cs:54-57`). Ribbon renders un-animated and fog-less (`WaterRiverSurface.cs:640-641`). |
| 3 | Set `sourceEnd.body = A` (`WaterRiver.cs:36`). | Only read once `IsGenerated` (`:277-278`, `:569`, `:626`). | **Silent.** `WarnOnInvalidSetup` (`:724-740`) only warns on partial/ambiguous ends, never on "body assigned but never generated" (CONFIRMED - the checks at `:742-755` are the only two). Nothing happens: no ports, no seam, no apron, no outflow. |
| 4 | Press **Generate Connection** on the Source end (`Editor/WaterRiverEditor.cs:128-134` -> `GenerateEnd` `:142-170` -> `WaterRiver.RegenerateConnection` `:485-545`). | Creates two child GameObjects `Port - River Source` and `Port - A` plus `Connection - River to A` (`:518-540`, names at `:82-85`); places them (`:633-656`); pushes seams + outflow (`:543-544`). | Same as 3. Also the ONLY code paths that ever call `RegenerateConnection` are this button and the build-kit recipes (grep: `Editor/WaterBuildKit.River.cs:59`, `ConnectedWatersDemo.cs:212,296,302`, tests). |
| 5 | Set `mouthEnd.body = B`, set `transitionRadiusMeters`, press Generate again. | Mouth is port A (upstream) (`WaterRiver.cs:534-538`); mouth outflow registers with B (`:280-297`) and B gets the current field appended too (`:228-230`). | Silent as in 3. |
| 6 | Turn on `waterFog` on A/B/parent (defaults OFF for fresh bodies - `Editor/WaterBuildKit.ConnectedWatersDemo.cs:178-181`, `:468-476`). | `CanDriveExternalRiverFog` (`WaterVolume.Underwater.cs:60-61`), `IsFogEligible` (`Settings.Underwater.cs:214-215`). | Warning only for `fullscreenVolumeFog` off on the PARENT (`WaterRiver.cs:732-735`); `waterFog` off on any body is silent - river fog just never engages (`WaterRiverSurface.cs:595-602`). |
| 7 | Keep terminal knots ON the body border (the "border contract", `WaterRiver.cs:68-71`, `:418-426`). | `DeriveBodyPortPosition` snaps the body port to the nearest border edge (`:427-447`). | Warning only at Generate time if the knot is > 0.5 m inside (`:464-480`); OnValidate syncs are deliberately silent (`:464-465`). |
| 8 | After moving A, B or a knot: press **Update Connection** again. | `SyncGeneratedConnectionAnchors` (`:617-631`) only runs from `WaterRiver.OnValidate` (`:185`) and `RegenerateConnection`. `WaterRiver` does NOT subscribe to `WaterRiverSpline.Changed` (grep `+=` in `WaterRiver.cs`: 0 subscriptions; the surface does at `WaterRiverSurface.cs:419`). The transform router only re-notifies splines/surfaces (`Editor/WaterRiverAuthoringChangeRouter.cs:43-46`). | **Silent stale ports** (CONFIRMED by reading; visual effect PLAUSIBLE): the query seam plane and the mesh apron both read the stale `targetPort` transform (`WaterTopology.cs:113-114`, `WaterRiver.cs:572`) until some WaterRiver property is touched. |
| 9 | (river-to-river) On the DOWNSTREAM river only: `sourceEnd.upstreamRiver = R1`, same parent volume, Generate (`WaterRiver.cs:495-511`). | `ConfigureSourceBoundary` copies R1's mouth row (`:560-563` -> `WaterRiverSurface.cs:272-287`). | Exceptions logged as errors by the editor (`WaterRiverEditor.cs:158-162`): mouth-side river link, self-link, cycle, different parent. |

Hand-authored alternative (no river, lake<->lake): add `WaterConnectionPort` to two objects, set `body` on each, add `WaterConnection`, drag the ports in. There is no inspector help for this beyond default property drawers (grep `CustomEditor.*WaterConnection` in Editor/: 0 hits; `WaterVolumeEditor.*` "Topology" section is open-water/unbounded flags only, `Editor/WaterVolumeEditor.Body.cs:17-24`). Half-wired -> `Debug.LogWarning` once (`WaterConnection.cs:64-70`) and counted by the Network Manager (`Editor/WaterNetworkManagerWindow.cs:229-239`).

---

## 2. FRICTION LIST

F1. **Parent body is stored twice.** `WaterRiver.parentVolume` (`Runtime/WaterRiver.cs:91`) and `WaterRiverSurface.waterVolume` (`Runtime/WaterRiverSurface.cs:71`), reconciled in `ApplyWiring` (`:212-216`) with adoption on `Reset` (`:142`). Both are `[SerializeField]`, both visible in their inspectors. CONFIRMED.

F2. **The same body is referenced by four objects for one seam.** `sourceEnd.body` on the facade (`WaterRiver.cs:36`), `targetPort.body` on the generated port (`:528`), the body's `currentFields` array gets the river field appended (`:239-247`, serialized by the kit at `Editor/WaterBuildKit.River.cs:109-121`), and the body's `RegisterRiverMouthOutflow` registration (`:288`). Only the first is authored; the others are derived but persist in the scene file.

F3. **Flow rate stored three times.** `riverPort.authoredFlowRate`, `targetPort.authoredFlowRate`, `connection.authoredFlowRate` all set to `width*speed` (`WaterRiver.cs:523`, `:530`, `:540`). No in-package reader of any of them (grep `AuthoredFlowRate|DownstreamPort|OtherPortFor` outside the two defining files: 0 hits) - public advisory API only. CONFIRMED.

F4. **Transition radius stored twice** - `end.transitionRadiusMeters` (`WaterRiver.cs:44`) copied to `connection.transitionRadiusMeters` (`:539`, `:629`). The `WaterConnection` inspector shows an editable copy that the facade overwrites on the next OnValidate.

F5. **Everything on the port is derivable.** Anchor = spline terminal frame (`TryGetEndFrame`, `:395-416`); body port = nearest footprint border (`DeriveBodyPortPosition`, `:427-447`) or ocean tangent offset (`:452-462`); direction = spline tangent (`:413`, `:636-637`). The port GameObjects add nothing the facade could not recompute except the GUID.

F6. **"Assigned but not generated" is silent** (see 1.5 step 3). `WantsConnection && !IsGenerated` is never validated (`:724-740`). Missing validation. CONFIRMED.

F7. **Ports go stale on body/knot moves** (see 1.5 step 8). No `spline.Changed` subscription in the facade, no body-transform hook. Missing sync. CONFIRMED by reading.

F8. **Fog on the end bodies is not validated.** The facade warns on `fullscreenVolumeFog` for the parent only (`:732-735`); `waterFog` (the master toggle, default OFF per `ConnectedWatersDemo.cs:178-181`) is checked nowhere at authoring time.

F9. **Only reachable through the demo rig builder:** the full "several bodies + rivers + stitched junction + exclusion + fog on" composition exists once, hard-coded in `CreateConnectedWatersRig` (`Editor/WaterBuildKit.ConnectedWatersDemo.cs:138-277`). `CreateConnectedRiver` (`:282-305`) - "THE recipe (facade-owned wiring), then connect to bodies at each named end" - is `static` private in that partial and reused by the stress rig (`Editor/WaterBuildKit.MultiBodyStressTest.cs:246-276`), but NOT by the `GameObject > River` menu (`WaterBuildKit.River.cs:51-60` re-implements the mouth half inline) and not by the wizard (grep `River|Connection` in `Editor/WaterWizardWindow*.cs`: 0 hits). `EnableUnderwaterFog` (`ConnectedWatersDemo.cs:468-476`) and `ConfigureUnboundedOcean` (`:443-460`) are likewise rig-private.

F10. **The River creator makes a new asset folder per river.** `CreateRiverFromMenu` calls `CreateUniqueWaterFolder()` and builds fresh `WaterAbove.mat`/`WaterUnder.mat` (`WaterBuildKit.River.cs:43`, `Editor/WaterBuildKit.Materials.cs:39-41`) instead of reusing the parent's materials via the existing `ResolveOrCreateMaterialsFolder(WaterVolume)` (`Materials.cs:11-27`, used only by `WaterFoamParticlesEditor.cs:274` and `WaterWizardWindow.cs:1049`). Three rivers into one lake = three `Water N` folders with identical materials. CONFIRMED.

F11. **The wizard cannot make a second body in the same rig.** "Add Water Body (secondary)" (`WaterWizardWindow.cs:883-885`) goes through `WaterSceneBuilder.AddSecondaryBody` (`Editor/WaterSceneBuilder.cs:153-195`), which CLONES the primary's renderers (`:198-214`) rather than calling `CreateWaterBody`; it takes no position/extent/name, always offsets +X by `2*extent + 1 m` (`:171-172`).

F12. **Quality is shared, not per system.** Every kit body gets the package's `DefaultWaterQuality.asset` (`Editor/WaterBuildKit.cs:73`, loaded at `Body.cs:43`, wired at `Wiring.cs:65`). No recipe writes a project-owned `WaterQuality` asset; the only way is the `CreateAssetMenu` (`Runtime/WaterQuality.cs:10`) plus hand assignment on each body.

F13. **Gizmo disagrees with the blend for stitched ports.** `WaterConnection.OnDrawGizmosSelected` returns when the port axis is ~0 (`Runtime/WaterConnection.cs:89-90`), while `ApplySeamBlend` falls back to the flow directions in exactly that case (`WaterTopology.cs:119-129`). A river-to-river connection therefore blends but draws no slab. CONFIRMED.

F14. **The Network Manager cannot see river-only seams.** `BuildComponentRoots` unions by `ResolveProvider()?.Body` (`Editor/WaterNetworkManagerWindow.cs:194-198`), so a standalone ribbon (Body == null, `WaterRiverSurfaceProvider.cs:55`) never joins a component, and the window lists bodies only (rows from `WaterRuntimeRelevance` snapshot, `:110-111`).

---

## 3. SIMPLIFICATION DIRECTIONS (ranked)

**D1 (highest value, lowest risk): one "Connect" verb + derive everything, keep the runtime types.**
Make `sourceEnd.body`/`mouthEnd.body` the only authored input and have the facade own generation *and* maintenance: subscribe to `WaterRiverSpline.Changed` (the surface already does, `WaterRiverSurface.cs:419`) and to the end bodies' transform/extent changes, re-run `SyncGeneratedConnectionAnchors`; auto-generate on assignment from the editor (creation must stay in the editor - `WaterRiverEditor.cs:3-6`), and warn on `WantsConnection && !IsGenerated` at enable. Reuses: `RegenerateConnection` (`WaterRiver.cs:485-545`), `GenerateEnd` undo plumbing (`WaterRiverEditor.cs:142-170`), `CreateConnectedRiver` (`ConnectedWatersDemo.cs:282-305`). Deletes: the Generate/Update buttons (become "Regenerate" for repair), the duplicated mouth-connect in `WaterBuildKit.River.cs:56-60`. Back-compat: none needed - serialized fields unchanged; already-generated scenes keep their ports and GUIDs.

**D2: fold the port and connection GameObjects into hidden/derived children (or drop them for facade-owned seams).**
Today ports/connections are visible scene children (`WaterRiver.cs:715-720`). Two options: (a) keep the components but create them with `HideFlags.HideInHierarchy` + `NotEditable` so users cannot half-delete them (removes `IsPartiallyGenerated` handling `:742-748`); (b) make `WaterConnection` hold `(providerA, providerB, anchorA, dirA, anchorB, dirB, radius, portIdA, portIdB)` as plain data registered by the facade, with `WaterConnectionPort` MonoBehaviours remaining only for HAND-authored lake<->lake seams. (b) touches `WaterTopology.ApplySeamBlend` (reads `connection.portA.Anchor` etc., `:113-116`) and `TryGetPort` (`:47-64`), and the tests that build ports with `providerOverride` (`Tests/Runtime/WaterTopologyFeatureTests.cs:61-73`). Back-compat: a facade migration on enable can read old child ports' `portId` into the new data (GUID preserved) and destroy the children. Recommend (a) first; (b) only if the hierarchy noise is the real complaint.

**D3: a scene-level `WaterSystem` root component that owns quality, fog defaults and enumerates members - NOT topology.**
Topology already lives in static registries (`WaterTopology.cs:20`, `WaterSurfaceProviders`, `WaterVolume.Bodies`), and the Network Manager already computes connected components from them (`WaterNetworkManagerWindow.cs:185-220`). A root that *owns* the graph would duplicate that. Instead a lightweight `WaterSystem` MonoBehaviour: holds the shared `WaterQuality` asset + materials folder, lists its bodies/rivers/exclusions (serialized refs created by the window), and offers "apply quality to all", "enable fog on all" (reusing `EnableUnderwaterFog`, `ConnectedWatersDemo.cs:468-476`). Reuses `BuildContext` (`Editor/WaterBuildContext.cs`) as the editor-time equivalent. Deletes nothing at runtime. Back-compat: optional component; scenes without it behave as today.

**D4: auto-derive connections from overlap (NOT recommended now).**
Deriving seams from footprint/ribbon overlap would remove the authored body reference, but the border contract explicitly rejects overlapping footprints (`WaterRiver.cs:68-71`, demo v3 "REAL GAPS", `ConnectedWatersDemo.cs:2-4`), the ocean case needs an authored tangent (`:47-49`), and `WaterTopology`'s "stacked seams are an authoring smell" stance (`WaterTopology.cs:161-163`) assumes explicit authoring. Keep explicit.

**D5: connection as a ScriptableObject asset (NOT recommended).**
Ports need scene transforms (`WaterConnectionPort.cs:43-45`) and scene component references; an asset would need scene GUID lookups and break the Undo model in `WaterRiverEditor.cs:149-168`. No benefit over D1/D2.

**D6: gizmo-based endpoint snapping.**
Small, independent: in `WaterRiverSplineEditor`, snap a terminal knot to `DeriveBodyPortPosition(body, knot)` when within `MaxKnotInsetWarnMeters` (`WaterRiver.cs:71`, `:427-447`) and draw the target border. Also fix F13 by reusing the `WaterTopology` seam-normal rule in the gizmo. No data changes.

---

## 4. THE WIZARD AND BUILD KIT TODAY

### 4.1 WaterWizardWindow (`Editor/WaterWizardWindow.cs` + 3 partials, menu `Window/AbstractOcclusion/WebGpuWater/Water Wizard`, `:23`, `MenuRoot` at `Editor/WaterBuildKit.cs:66`)

Sections (`:164-176`): Create Water, Floating Objects, Boat, Dry Interior, Fit Spray To Object, Splash & Crown, Utilities.

- **Create Water** (`:349-419`): ONE primary body named "Water Body" under a "WebGPU Water" root (`:32-33`, `:383`, `:396-398`), kind = LegacyAnalyticPool / SurfaceOnly / SurfaceWithFog / OpenWaterOcean (`:56`), extent, ripple quality, reflections, foam, edge foam, splash, god rays, floor collider, terrain bed, camera mode (`:96-111`). Calls `CreateUniqueWaterFolder` + `CreateContext` (camera+sun rigged, `:386-387`) then `CreateWaterBody` (`:396`). Refuses (dialog) if the root already exists (`:363-372`). Writes: `Assets/WebGpuWater/Waters/Water N/Materials/{WaterAbove,WaterUnder,Pool?,GodRays?,FoamParticles?...}.mat`.
- **Utilities** (`:869-891`): prefab (`WaterSceneBuilder.CreateWaterVolumePrefab`, writes `Assets/WebGpuWater/Waters/WaterVolumePrefab/WaterVolume.prefab`, `WaterSceneBuilder.cs:19-20`), foam retrofit, foam textures, splash upgrade, **Add Water Body (secondary)** (clone, F11), chunk shader registration, **renderer feature Install/Repair** (`:917-988` -> `Editor/WaterRendererFeatureInstaller.cs:40`).
- No river, no connection, no exclusion volume, no quality asset, no multi-body layout. CONFIRMED (grep).

### 4.2 WaterBuildKit public entry points (all `[MenuItem]`, plus internal recipes)

| Entry | File:line | Instantiates | Writes to Assets |
|---|---|---|---|
| `GameObject/AbstractOcclusion/Water Exclusion Volume` | `ExclusionVolume.cs:16-29` | one `WaterExclusionVolume` box 4x3x4 | none |
| `GameObject/AbstractOcclusion/Connected Waters Test Rig` | `ConnectedWatersDemo.cs:27`, `:138-277` | 5 bodies (lake primary, pond, sewer, reservoir, unbounded ocean), coastal Terrain, 4 rivers (7 connections incl. 1 river-to-river), exclusion carve, floor, 8 crates | `Waters/Water N/Materials/*`, `Waters/Water N/Connected Coast Terrain.asset` (`:330-332`) |
| `GameObject/AbstractOcclusion/River` | `River.cs:16`, `:30-64` | spline+field+surface+facade, mouth auto-connected to parent | NEW `Waters/Water N/Materials/WaterAbove|Under.mat` (F10) |
| `GameObject/AbstractOcclusion/Multi-Body Water Stress Test Rig` | `MultiBodyStressTest.cs:16-17`, `:118-126` | 30 bodies in tiers, 4 connected rivers, banks/ground | `Waters/Water N/Materials/*` + `Stress Concrete.mat` |
| `Tools/Abstract Occlusion/Water/Create Multi-Body Stress Test Scene` | `:18-19`, `:128-146` | same, into a new scene | `Assets/Multi-Body Water Stress Test.unity` (`:23`) |
| `Window/AbstractOcclusion/WebGpuWater/Water Wizard` | `WaterWizardWindow.cs:23` | see 4.1 | see 4.1 |
| `Window/Abstract Occlusion/Water Network Manager` | `WaterNetworkManagerWindow.cs:12` | window only | none |

Internal recipes a system window can compose today: `TryBuildSharedAssets` / `CreateContext` / `RigScene` (`Body.cs:16-74`), `CreateWaterBody` (`Body.cs:80-139`), `CreateRiverRig` (`River.cs:78-107`), `CreateConnectedRiver` (`ConnectedWatersDemo.cs:282-305`, private), `AddProceduralRiverFoam` (`:307-314`, private), `EnableUnderwaterFog` (`:468-476`, private), `ConfigureUnboundedOcean` (`:443-460`, private, takes a Terrain), `CreateExclusionVolume` (menu-only, `MenuCommand` signature), `WireWaterVolumeAssets`/`WireWaterVolumeFrom` (`Wiring.cs:46-88`), `CreateBuoyantObjectBody` (`Props.cs:35`), `CreateBoat` (`Boat.cs:123`).

### 4.3 Coverage of a "water system" today

The demo rig IS a complete water system (bodies + rivers + stitch + exclusion + fog + ocean/terrain) - hard-coded to one layout. Everything the window needs already exists as a recipe; what is missing is (a) parameterisation (positions/extents/names/knots come from `static readonly` constants, `ConnectedWatersDemo.cs:31-136`), (b) the three private helpers above being reachable, (c) a per-system quality asset (F12), (d) a body-in-existing-scene creator that is not a clone (F11).

### 4.4 Duplicated rig-building code (CONFIRMED)

- Body construction exists three ways: `CreateWaterBody` (`Body.cs:80-139`), `AddSecondaryBody` clone (`WaterSceneBuilder.cs:153-195`, re-creates Frame/Renderers objects by hand `:166-183`), `CreateWaterVolumePrefab` (`WaterSceneBuilder.cs:27-66`, hand-builds volume + two renderers `:40-49`). The `Wiring.cs:43-45` comment records this drift once already.
- Mouth auto-connect: `River.cs:56-60` vs `CreateConnectedRiver` `:298-303` (identical three lines).
- Fog-on-for-a-set-of-bodies: `EnableUnderwaterFog` (`ConnectedWatersDemo.cs:468-476`) vs the wizard's `ApplyBaseType` (`WaterWizardWindow.cs:421-424`) + `DefaultFogDensity` (`:456`) - the demo re-declares `RigFogDensity = 0.2f` (`:466`) with a comment explaining it mirrors the wizard.
- Root-replace idiom `GameObject.Find(root) -> Undo.DestroyObjectImmediate -> NewUndoableGameObject -> CreateContext -> RevertAllDownToGroup` appears in `ConnectedWatersDemo.cs:141-152` and `MultiBodyStressTest.cs:151-164`; the wizard has its own dialog variant (`WaterWizardWindow.cs:363-372`).
- Ocean configuration through `SerializedObject` property paths: `ConfigureUnboundedOcean` (`ConnectedWatersDemo.cs:447-458`) vs the wizard's `ApplyOpenWater`/`OceanDefaults` partial (`WaterWizardWindow.cs:47-53` aliases) vs `ConfigureStressRenderFeatures` (`MultiBodyStressTest.cs:231-244`).

### 4.5 WaterNetworkManagerWindow - what it is

A **read-only runtime observability table** (`Editor/WaterNetworkManagerWindow.cs:1`): per body - type, primary, connected-component label from live `WaterTopology` connections (union-find `:185-272`), assigned/resolved quality, relevance rank, visibility, distance, coverage, sim/planar/caustic/foam grants, fog source, activation distance, approx GPU MB, validation flags (`:322-361`), with Select/Frame buttons (`:327-333`) and an "Unwired connections" counter (`:229-239`). Data from `WaterRuntimeRelevance` (`:110-113`) and `WaterRuntimeValidation.ValidateBody` (`:167`). It creates nothing and edits nothing. Overlap with the future window: the connected-component computation and the per-body quality/fog columns are exactly the "system view" a creation window needs for its right-hand status pane; it should be reused (extract `BuildComponentRoots`/`BuildComponentLabels` into a shared editor helper) rather than re-implemented. Its menu path is off-root (`:12`) - see Q3.

---

## 5. WATER SYSTEM WINDOW SKETCH (no code)

**Minimal data model** (window-side, serialized in the EditorWindow like the wizard does, `WaterWizardWindow.cs:87-91`):

```
System        { name, assetFolder, quality (WaterQuality), fogOn, cameraRig? }
  Body[]      { name, kind (pool/surface/fog/ocean), center, extent, primary, terrainBed? }   -> WaterVolume (exists)
  River[]     { name, parentBody, knots[] or (from,to,width,speed), foam?, fluid? }            -> WaterRiver facade (exists)
  Connection[]{ river, end (Source/Mouth), target (Body | River), radius }                     -> WaterRiverEndConnection (exists)
  Exclusion[] { name, center, size, shape }                                                   -> WaterExclusionVolume (exists)
```
Runtime types already cover every row; only the window's *plan* is new. Bodies map 1:1 to `CreateWaterBody`, rivers to `CreateRiverRig`/`CreateConnectedRiver`, connections to `GenerateEnd`, exclusions to the `ExclusionVolume` creator body (`ExclusionVolume.cs:24-28`), quality to `WireWaterVolumeAssets`'s last argument (`Wiring.cs:47`, `:65`) once a project-owned asset is created (`AssetDatabase.CreateAsset` of a `WaterQuality` into the system folder - new, small).

**Recommended shape:** a new **"Water System" tab/section inside the existing Water Wizard** (the wizard is already "the single authoring entry point", `WaterWizardWindow.cs:1-10`, uses `WaterEditorUI.Section` foldouts `:166-173`, and already owns renderer-feature install `:917-988` and asset-folder creation). Rationale over a new window: one menu entry, shared `BuildContext`, shared idempotency dialog, shared renderer check. Pair it with a lightweight scene `WaterSystem` root component (D3) that remembers the folder/quality and lists members, so "Add river to this system" later works from the inspector too. Do NOT put topology ownership on that root (see D3).

Flow: plan in the window (list editors with +/- like `DrawObjectSlots`, `:748-765`) -> "Build System" = one undo group: `CreateContext` once (`Body.cs:16`), N x `CreateWaterBody`, `EnableUnderwaterFog` if fogOn, N x `CreateConnectedRiver` (with `AddProceduralRiverFoam` optional), exclusions, quality assignment, then `Selection` + `MarkSceneDirty` exactly as `CreateWater` (`:409-415`). Show the Network Manager's component/validation columns as a post-build status pane.

**Risks:**
- *Renderer-feature install*: `TryInstallOrRepair` modifies the URP Renderer Data asset with a confirm dialog (`:955-988`); a system build should run the check first and refuse to build with `NeedsRepair` rather than auto-install.
- *Asset writing*: each `CreateContext` writes materials; rivers today create extra folders (F10) - the window must pass ONE folder and the parent's materials into `CreateRiverRig` (`River.cs:78-81` already takes materials as parameters, so this is a call-site fix).
- *Prefab/scene serialization*: generated ports/connections are scene children with serialized back-references (`WaterRiver.cs:50-53`); building inside a prefab stage or saving a body as a prefab would either drag connections into the prefab or break the references. `CreateWaterVolumePrefab` deliberately avoids scene refs (`WaterSceneBuilder.cs:24-26`); a system prefab is out of scope.
- *GUID ports*: `RegenerateConnection` reuses existing children (`:691-703`) so re-running "Build" on an existing system must find and reuse, not duplicate - the wizard's root-exists dialog (`:363-372`) is the precedent; the demo rig's destroy-and-rebuild (`ConnectedWatersDemo.cs:143-145`) would regenerate GUIDs and break any external save keyed on `PortId`.
- *Order of operations*: `CreateRiverRig` adds the facade LAST so `RequireComponent` does not duplicate components (`River.cs:76-77`); river-to-river stitch must generate the UPSTREAM river's mouth before the downstream source (`ConnectedWatersDemo.cs:186-212`).
- *Primary uniqueness*: only one `IsPrimary` (`Body.cs:79`); the plan needs a single-select.

---

## 6. CODE-QUALITY NOTES (top 10, owner's standards)

Q1. **Dead helper (CONFIRMED, 0 call sites).** `WaterRuntimeValidation.ValidateConnection` (`Runtime/WaterRuntimeValidation.cs:45-51`) is never called; `WaterNetworkManagerWindow` re-implements the same `IsWired` check inline twice (`Editor/WaterNetworkManagerWindow.cs:229-239`, `:241-255`). Direction: call the helper from the window or delete it.

Q2. **Duplicated "connect mouth to parent" recipe.** `Editor/WaterBuildKit.River.cs:56-60` duplicates `CreateConnectedRiver`'s mouth half (`Editor/WaterBuildKit.ConnectedWatersDemo.cs:298-303`); `CreateConnectedRiver` is private to the demo partial. Direction: promote it to the River partial and have the menu call it.

Q3. **Three menu roots.** `MenuRoot = "Window/AbstractOcclusion/WebGpuWater/"` (`Editor/WaterBuildKit.cs:66`) vs `"Window/Abstract Occlusion/Water Network Manager"` (`Editor/WaterNetworkManagerWindow.cs:12`) vs `"Tools/Abstract Occlusion/Water/..."` (`Editor/WaterBuildKit.MultiBodyStressTest.cs:18-19`); GameObject-menu entries are hand-spelled literals in four files (`River.cs:16`, `ExclusionVolume.cs:16`, `ConnectedWatersDemo.cs:27`, `MultiBodyStressTest.cs:16-17`) with no shared `GameObjectMenuRoot` constant. Direction: one `GameObjectMenuRoot` beside `MenuRoot`; move the Network Manager under `MenuRoot`.

Q4. **Log-prefix literal drift persists.** `LogPrefix` is defined once (`Editor/WaterBuildKit.cs:25`) with an honesty note about ~37 inlined sites (`:21-23`); today `"[WebGpuWater]"` is still inlined 10x in `Editor/WaterWizardWindow.cs`, 10x in `Editor/WaterSceneBuilder.cs`, 2x in `WaterWizardWindow.HullSpray.cs`, 1x each in `ConnectedWatersDemo.cs:255`, `Wiring.cs`, `Boat.cs` (grep counts). Direction: mechanical replace.

Q5. **Hand-summed layout literal.** `TableWidth = 2360f` (`Editor/WaterNetworkManagerWindow.cs:66`) is not derived from the 19 column-width constants (`:67-86`, which sum to 1982 before GUILayout spacing). PLAUSIBLE that the scroll width is wrong today; CONFIRMED it will drift on the next column. Direction: sum the widths.

Q6. **Test seam in a production type.** `WaterConnectionPort.providerOverride` (`Runtime/WaterConnectionPort.cs:34-36`, "Never set in production code paths") is an `internal` field read on every `ResolveProvider` (`:51`). Direction: keep, but move under `#if UNITY_INCLUDE_TESTS` or route tests through a fake `WaterRiverSurface`.

Q7. **Gizmo math duplicated and divergent.** Seam centre/axis is computed in `Runtime/WaterConnection.cs:87-102` and again in `Runtime/Query/WaterTopology.cs:113-131`; the gizmo lacks the coincident-port fallback (F13). Direction: one internal `TryGetSeamFrame(out center, out normal)` on `WaterConnection` used by both.

Q8. **Editor-only static UI helper called from the build kit.** `WaterRiverEditor.GenerateEnd` (an inspector class, `Editor/WaterRiverEditor.cs:142`) is the build kit's connection entry point (`River.cs:59`, `ConnectedWatersDemo.cs:212,296,302`). Direction: move `GenerateEnd`/`RemoveEnd` into a `WaterRiverAuthoring` static (or a `WaterBuildKit.RiverConnection` partial) that the inspector and the kit both call.

Q9. **`WaterVolume` inspector has no connection surface.** The "Topology" section (`Editor/WaterVolumeEditor.Body.cs:22-40`) is the open-water/unbounded pair; connections touching a body are only visible via the generated children under the river, or the Network Manager. Direction: a read-only "Connections" list in the body inspector built from `WaterTopology.ConnectionsOf` (edit mode: scan `FindObjectsByType<WaterConnection>`).

Q10. **Magic numerals in the connection path.** `0.5f` for seam midpoint appears in `WaterTopology.cs:131`, `WaterConnection.cs:95`, `WaterRiver.cs:655` (three "midpoint" literals) while `SeamPlaneBlend = 0.5f` (`WaterTopology.cs:18`) is the only named one; `RiverFogMidpointT = 0.5f` and `RiverFogHandoffNormalizedBand = 0.1f` are named (`Settings.Underwater.cs:171-172`) but `armBandMeters` is passed as a parameter with no named source visible here (PLAUSIBLE - defined at the caller). Direction: one `Midpoint` helper; not a correctness issue.

Not findings (verified): `SeamPlaneBlend`, `DomainSwitchMarginMeters`, `MinTransitionRadiusMeters` are tuning knobs with consumers; `WaterQuality` per-body wiring is intentional; `WaterTopology.ConnectionsOf`/`TryGetPort`/`OtherPortFor`/`DownstreamPort` are public game-facing API with tests (`Tests/Runtime/WaterTopologyFeatureTests.cs:83-91`, `WaterRiverFacadeFeatureTests.cs:266-282`), not dead code.
