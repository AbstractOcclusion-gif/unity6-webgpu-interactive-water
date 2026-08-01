# PLAN — Plunging particles v1 (splash-up cascade + spray pump continuous mode)

Date: 2026-08-01. Grounding: `docs/RESEARCH_bubbles_spray_particles_2026-08-01.md` §5
(Kimmoun & Branger: a plunging breaker runs **2–4 jet → splash-up cycles marching shoreward**
before degenerating into swash). Design-gated — **no code until §6 is locked.**

## 0. Where we start (after today's removal)

On ocean bodies the ambient spawn is now OFF and **the surf lip (1c) is the only thrower left in
the Spawn kernel** — exactly the "reuse the throw we already have" starting point. The chain today:

    lip throws ballistic jet (1c) → droplets arc shoreward → LANDING silently converts to
    floating foam (.compute landing conversion) → end of story.

The physics says landing is not the end — it's the next plunge point.

## 1. The cascade (Bert's rolling→crown→rolling→swash idea)

**Generation-tagged landings re-burst at reduced energy until swash.**

- Each airborne droplet carries a small **generation counter** (0 = lip-thrown; bursts = 0 too).
- On landing, a droplet with `generation < MAX_GENERATIONS` (2–3) and impact energy above a
  threshold registers a **landing event**: position + landing speed + shoreward direction +
  generation+1.
- Next frame, each landing event fires a mini-burst: a few droplets thrown shoreward-biased at
  ~60–70% of the parent energy (the measured splash-ups weaken as they march) + a **bubble plume
  down** (ties into PLAN_bubbles §2 source 2) + the normal foam deposit. Generation MAX lands as
  plain swash foam — termination by construction.
- Result: the breaker front visually "walks" — throw, white collision, throw again, swash — and
  the shore chop comes from the water's own breaker signal, not hand-placed emitters.

### Mechanism decision (the core of this plan)

Landings are discovered on the **GPU** (Update kernel). Secondary bursts today can only be queued
from the **CPU** (`QueueSplashBurst`). Three candidate mechanisms:

- **(A) GPU landing-event ring buffer** *(proposed)*: Update appends landing events to a small
  structured buffer (`InterlockedAdd` cursor, cap ~64/frame); the next frame's `SpawnBurst`-style
  kernel consumes it directly. No readback, 1-frame latency (invisible), all on the existing
  compute. New plumbing: one buffer + one kernel loop, no CPU involvement.
- (B) Async readback of landing events to CPU → `QueueSplashBurst`: reuses everything, but
  readback latency is 2–4 frames and it drags the CPU into a purely-GPU story. Reject unless (A)
  hits a wall.
- (C) Probabilistic re-throw at landing (no events: a landing just *becomes* a new thrown droplet
  with some probability). Cheapest, but no crown hook, no plume anchor, and energy bookkeeping is
  per-droplet instead of per-impact — the collision stops being an *event*. Fallback option.

### Crown at the collision

`EmitCrown` is Shuriken/CPU — a GPU landing can't call it. v1: **no crown on cascade landings**;
the collision reads as burst droplets + bubble plume + deposit ring. If the crown is missed
visually, v2 adds a GPU-drawn ring (cassette-style quad or a decal-stamped whitewash ring) —
decide after seeing v1. (Readback just to flash a crown is not worth the latency.)

## 2. Spray pump: constant emission by speed (Bert's gap)

`WaterSprayPump` today is **event-edge** logic: threshold + cooldown → discrete bursts. A planing
hull or a rock in a standing bore wants a *steady* rooster/sheet, and "burst spam" is the wrong
tool (and eats `MaxBurstsPerFrame`).

- New per-probe **emission mode**: `Burst` (today's behavior, default) | `Continuous`.
- Continuous: while the probe is in the surface band and `signal` (same `TriggerSignal` — Rock
  rise / Boat plow / Both) exceeds `minImpactSpeed`, emit at a **rate ∝ signal**:
  `rate = baseRatePerSecond * saturate(signal / maxImpactSpeed)`, accumulated in a float
  (`_emitAccumulator += rate * dt; while >= 1 → EmitSplash(smallAmount)`), cooldown ignored.
  Strength per emit stays signal-derived so droplet speed still scales with boat speed.
- This reuses `EmitSplash` unchanged → same routing (GPU pool bursts + crown gating).
- Interaction with the hull petal spray plan: petal direction/arc parameters (already in
  `QueueSplashBurst`'s signature) apply per probe — continuous mode is the *cadence*, petals are
  the *shape*. Compatible, and the same burst-budget fix serves both.

## 3. The shared prerequisite: the burst budget

`MaxBurstsPerFrame = 16` and **request #17 is DROPPED, not deferred** (`_pendingBursts` cleared
after upload). Cascade landings (up to ~64/frame in mechanism A's own budget) + continuous pumps
+ gameplay splashes all collide with this cap, and drops land on whoever queued late (one side of
a boat, one end of a crest — the already-flagged trap).

**P1 fixes it once for everyone:** `_pendingBursts` becomes a carry-over queue — upload the first
16, KEEP the rest for next frame (bounded total, e.g. 64, oldest-dropped past that), plus a
round-robin start index so no caller's position in the list is systematically last. GPU landing
events (mechanism A) bypass this CPU queue entirely — their cap is their own ring size.

## 4. Increment order

| # | Increment | Size | Depends on |
|---|---|---|---|
| P1 | Burst queue carry-over + fairness | small, CPU only | — |
| P2 | Pump `Continuous` mode | small, CPU only | P1 (budget pressure) |
| P3 | Cascade v1: generation lane + landing-event ring + re-burst kernel + shoreward bias | the real work | P1; bubble plume hook optional until B1 exists |
| P4 | Cascade landings inject bubble plumes + (option) ripple chop injection at landings | small | P3 + PLAN_bubbles B1 |

P4's ripple option: each landing event also calls the sim's ripple injection (landing = real
water displacement) → "shore wave choppiness" emerges from the cascade itself. Height-risk is the
known concern from the 07-22 shore audit (#2 front chop) — default-off knob.

## 5. Tuning targets (from the papers, so v1 lands near-real)

- Generations: 2–3 (measured: at least 4 cycles, but the last ones are foam-only).
- Energy decay per generation: ~0.6–0.7; throw stays shoreward-biased (`surfToShore` is already
  in the landing droplet's velocity heritage).
- Landing→re-burst latency: 1 frame (mechanism A) — the real splash-up rises in ~0.1–0.3 s, so
  an *optional* short delay lane (2–6 frames) is a polish knob, not v1.
- Cascade droplet counts: small (4–10 per landing) — the *march* sells it, not the density.

## 6. Decisions to lock before any code

1. Mechanism **A / B / C** for landings (proposal: A).
2. Crown on cascade landings: skip in v1 (proposal) or block v1 on a GPU ring visual?
3. Generation storage: pack into the existing particle seed/flag lane vs a new float
   (same stride question as PLAN_bubbles §6.6 — decide the two plans TOGETHER).
4. P1 queue semantics: carry-over bound (64?), oldest-drop vs newest-drop, round-robin — OK?
5. Pump continuous mode: per-probe enum (proposal) vs whole-component mode? `baseRatePerSecond`
   range? Does continuous mode suppress the crown (a steady sheet flashing crowns at 10 Hz will
   look wrong — proposal: crown only on the first emit of a continuous run)?
6. Ripple injection at landings (P4 option): in or out of scope entirely for now?

## 7. Test checklist (when built)

Shore demo, plunging preset: lip throw → visible secondary white bursts marching shoreward, 2–3
hops, ending in swash foam; no cascade on surging/spilling breakers (lip signal already
plunge-amplified/surge-killed). Boat at speed with Continuous probes: steady sheet, no
16-burst starvation on either side (P1 fairness), crown not strobing. Frame budget: cascade at a
full crest stays within the landing-ring cap without evicting gameplay bursts.
