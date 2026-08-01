# PLAN v2 — Addendum A: the tool moves into the Water Wizard

**Replaces §6.0, §6.1 and §7 of plan v2. Everything else stands unchanged.**
Still design only. No code written.

---

## Why this is the better call

Your instinct removes the single most dangerous part of the plan and, unexpectedly, makes the
implementation *smaller* rather than larger.

### It deletes increment I0 entirely

The Scene view overlay needed to suppress the water, and suppressing the water meant mutating state
the user owns. Plan v2 §6.0 carried, for that one capability:

- editor-only visibility instead of `Renderer.enabled` (which is serialized and would leak into the
  scene asset)
- a `ScriptableSingleton` with `[FilePath]` so the hidden set survives domain reload
- restore hooks on five separate exits — close, recompile, play mode, scene save, editor restart
- a "Restore hidden water" escape-hatch menu item
- **an unresolved question I could not answer from source** — whether `SceneVisibilityManager` state
  persists into the `.unity` file on save

A wizard preview touches none of it. **Nothing is hidden, so nothing has to be restored.** The
riskiest increment in the plan, and the one open verification question, both disappear.

### It is where the flow already lives

The wizard is described in its own header as *"the single authoring entry point"*, and it already
carries the two sections either side of this one:

- **`DrawBoatSection`** (`WaterWizardWindow.cs:562`) — creates the boat, primitive or custom hull mesh
- **`DrawSplashSection`** (`:824`) — retrofits splash emitters onto a body and triggers onto objects

"Fit spray to hull" sits between them and completes the sentence: *create the boat → give it a
splash emitter → fit spray to its hull.* It also inherits the wizard's existing selection idiom
(`SelectedBody()`, `Selection.gameObjects`) and its disabled-state + HelpBox conventions for free.

---

## The preview should be 2D, not a 3D render

This is the part worth being deliberate about, because the obvious implementation is the wrong one.

The reflex for "preview window" is `PreviewRenderUtility` — a second camera, its own lighting rig,
and gizmo picking reimplemented from scratch because `Handles` does not work in a preview rect.
That is a lot of machinery.

**But a draft is read from a side elevation, and a side elevation is a 2D drawing.** No camera, no
lights, no shaders, no picking framework:

| Element | How |
|---|---|
| hull backdrop | project mesh vertices to the side plane, take the 2D convex hull, fill it |
| draft line | a horizontal line in the rect; drag maps mouse Y → world Y through one linear transform |
| slice result | the §2 outline, drawn as dots at the draft line |
| top view | the same rect, projecting to XZ instead, with the petal wedges drawn as in the mockup |

Everything renders with `Handles.DrawAAPolyLine` / `GUI.DrawTexture` inside an IMGUI `Rect`, and the
drag is a `Rect.Contains` + `Event.current.delta.y` — the same pattern the wizard's existing controls
already use.

**Honest split:** the backdrop silhouette is *approximate* (a 2D convex hull loses the sheer and the
keel's concavity), while the **slice is exact** — it comes from the real plane/triangle walk in §2.
That is the right way round. The silhouette only has to tell you *where you are on the hull*; the
dots tell you what you will get.

### What you give up

Context. You no longer see the hull against the rest of the scene, the real water, or your lighting.

I do not think that costs anything here, because **context checking already has a home**: the probes
are ordinary gizmos in the Scene view the moment they are applied, drawn by the existing
`WaterSprayPumpEditor.OnSceneGUI` with per-mode colours and draggable handles. So the split is:

> **Wizard = choose the draft and the flower. Scene view = check it in place and nudge.**

That is a cleaner division than the overlay had, where both jobs fought over the same viewport.

---

## §6.1 replacement — the wizard section

`DrawFitSprayToHullSection()`, placed after `DrawBoatSection`.

```
┌─ Fit Spray To Hull ────────────────────────────────┐
│ HelpBox: rings a hull's waterline with spray probes │
│                                                     │
│ Hull object      [ Boat (selected) ]                │
│ Hull mesh        [ Hull (Mesh Filter) ▾ ]  optional │
│                                                     │
│ ┌───────────── preview ─────────────┐  [side][top]  │
│ │                                   │               │
│ │        ╭─────────────────╮        │               │
│ │  ══════╪═════════════════╪══════  │  ← draft, drag│
│ │        ╰──────╌╌╌────────╯        │               │
│ └───────────────────────────────────┘               │
│                                                     │
│ Draft source     [ Solve from buoyancy ▾ ]          │
│ Draft offset     ──●────────────  -0.18             │
│ Probe count      ────────●──────  24                │
│ Outward inset    ─●─────────────  0.03              │
│ ── petals ──                                        │
│ Arc width        ──●────────────  70°               │
│ Rake at rest     ●──────────────  0.05              │
│ Rake at speed    ───────────●───  0.75              │
│                                                     │
│ ⚠ 24 probes × 0.06 s → peaks at 24/frame, cap is 16 │
│                                                     │
│ [   Apply To Selected Boat   ]                      │
└─────────────────────────────────────────────────────┘
```

Behaviour follows the wizard's existing conventions exactly:

- **Disabled + HelpBox when there is no valid selection**, the same as `DrawSplashSection` does with
  `SelectedBody()`.
- **The button names its target** — `Apply To "Boat"` — matching `Add Splashes To "{body.name}"`.
- **Nothing is serialized until Apply** (decision 7 stands). Preview state lives in the wizard's own
  fields, which is exactly what wizard state already is.
- Apply writes through `SerializedProperty` in one Undo step, so prefab overrides keep working.

The **live burst-budget warning** stays — it is the one thing that stops §6.3 from being discovered
later as mysterious one-sided spray.

---

## §7 replacement — build order, now five increments

| # | Increment | Files | Testable by |
|---|---|---|---|
| **I1** | Wizard section + 2D side preview + draft line, **no slicing yet** — the line just reports a world Y | 1 editor | drag the line, read the Y, confirm the silhouette matches the hull |
| **I2** | Hull slice + probe dots in the preview + Apply | 1–2 editor | probes land on the real outline; zero runtime change |
| **I3** | `outwardLocal` on the probe + top view with arrows + Scene-view arrows | 1 runtime, 1 editor | arrows point out of the hull; still zero behaviour change |
| **I4** | Arc reaches the GPU, fixed width, no rake | 4 files (v2 §4) | arc 60° → wedges; legacy scenes byte-identical |
| **I5** | Runtime rake from measured velocity + cooldown staggering | 1 runtime | petals swing astern; a 32-probe hull stops dropping one side |

Changes from v2's order:

- **I0 is gone.** No scene state is touched, so there is nothing to prove safe first.
- **I1 is now a pure drawing increment** — silhouette and a draggable line, no geometry maths at all.
  It either looks like your boat or it does not, and you know within a minute.
- **I5 merges the rake and the staggering**, because the staggering only becomes observable once
  many probes are firing hard, which is exactly when the rake is being tuned.

I1–I3 touch no rendering and cannot regress anything. I4 remains the one needing care; its safety net
is still the zero-direction sentinel (v2 §3).

---

## Open questions — updated

Carried from v2, still unanswered:

1. **Which mesh is the hull** — whole visual root, or a specific child? The wizard's boat section
   already has a `Hull model (optional)` field for creation, so mirroring it here is natural, but I
   want to know what your actual boat looks like.
2. **Prefab or scene objects?** Changes how Apply should write.
3. **Rake shear** — astern is per-probe, so on a hard turn inner and outer probes rake differently
   and the flower shears. I think that reads as alive; say if you want it rigid off one hull-wide
   velocity.
4. **Anything to drop** to keep I1–I4 tight.
5. **`WaterBuoyancy` lattice refactor** — the solver needs the point lattice, which is private and
   built in `OnEnable`. Extracting it to an `internal static` is a pure refactor with no behaviour
   change, but it touches a file otherwise out of scope. Confirm, or I drop the solver and keep the
   draft on the rest plane.

**Retired by this addendum:** the `SceneVisibilityManager` persistence question, the five restore
hooks, the `ScriptableSingleton`, and the "Restore hidden water" menu item. None of them are needed
any more.

Nothing gets written until you say go.
