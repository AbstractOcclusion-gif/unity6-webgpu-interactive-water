# Convex hull generator — root-cause diagnosis (2026-08-01)

**Symptom:** `WaterBuildKit.BuildConvexHullMesh` (named-single-mesh path, ski boat
`S5AG2IB5Q2O5JTPFJFR6AXXL6 1`) keeps producing hull meshes with holes.

**Verdict: two distinct broken outputs, one shared root cause. The quickhull's
ε-thresholded horizon construction breaks on coplanar-heavy meshes, and the final
validator only checks topology — so geometrically wrecked hulls ship as "valid".**

---

## 1. What is actually in your project right now

All three `Assets/WebGpuWater/Generated/*_ConvexHull_DryInterior.asset` files are the
**same broken mesh** (byte-level identical geometry):

| metric | shipped asset | legal maximum / expected |
|---|---|---|
| vertices | 295 | 295 (correct) |
| triangles | **1330** | 586 = 2(V−2) Euler ceiling |
| open edges (used ≠ 2×) | **679** | 0 |
| same-direction duplicated edges (flipped windings) | **589** | 0 |
| inward-facing triangles (backface-culled → holes) | **17** | 0 |
| volume (normalized space) | **2.26** | true hull ≈ 0.62 → it self-overlaps |

These assets were generated **before** the `IsClosedConvexTriangulation` validator
existed (it would reject 679 open edges outright). They are stale wreckage — and
they stay on disk and stay assigned, because `SaveAsset` only overwrites on a
*successful* regeneration.

## 2. The current code still produces hole-y hulls on this exact mesh

Verbatim port of today's `WaterBuildKit.ConvexHull.cs` (float32, Unity rounding
semantics), fed the exact welded cloud (2329 points) of the ski-boat mesh:

- Output: 341 verts / 678 faces — **passes the shipped validator** (closed, every
  edge used twice, Euler exact).
- Reality: **7 inward-wound faces** (backface-culled in the editor → holes),
  signed volume **1.376 vs the true hull's 0.618 (2.23×)** — a folded,
  self-overlapping crust. True hull (double precision reference): 295 V / 586 F.
- Not a corner case: across 467 harness runs (ski boat, Nao Victoria per-submesh,
  synthetic decks/spheres/boxes), **128 runs returned topologically-"valid" but
  geometrically broken hulls**.

## 3. Root cause — pinned to the exact insertion

Per-insertion audit in double precision, exact failing mesh:

- Insertions #1–55: crust is a perfect convex hull. Every apex chosen is genuinely
  outside (0/337 phantom apexes). No sliver seeds. Stored face planes match true
  planes to `normal·true = 1.000000`.
- **Insertion #56: the horizon construction itself fails.** The apex sits within ε
  of several supporting planes near the silhouette (this mesh has large flat,
  ε-coplanar panels — decks, transom). Faces the apex is *just barely* above
  (0 < d ≤ ε) are treated as "not visible" and survive; the horizon therefore cuts
  through the coplanar band instead of following the true silhouette, and a fresh
  face `(1903,1895,1897)` is erected whose plane leaves existing crust vertex 59
  **9.4 mm = 34.9×ε strictly outside the hull**. Silent — nothing checks this.
- Cascade: the crust is now non-convex/self-intersecting, so the "visible region
  is connected" premise of `CollectVisible` genuinely no longer holds → 686
  visible-faces-left-alive events, 10 196 orphan re-homes for 2 329 points, 1 841
  points silently dropped, folds accumulate → the 2.23×-volume crust with inward
  faces.
- The earlier fixes in the file (horizon-edge winding trust, connected-region walk,
  orphan re-home) all held up — the output has 0 open edges and 0 winding
  conflicts. The remaining defect is deeper: **plain ε-thresholded float quickhull
  cannot survive coplanar-heavy real meshes.** This is precisely why qhull does
  facet merging / joggle.

## 4. Why it ships anyway

`IsClosedConvexTriangulation` checks **topology only** (undirected edge counts +
Euler). A folded, self-overlapping, inward-faced crust is combinatorially a perfect
sphere — it passes. The file's own contract ("degenerate returns null so the caller
falls back to the box LOUDLY") is not enforced for *geometric* degeneracy.

## 5. Proposed fixes (awaiting your go-ahead — no code touched)

1. **Stopgap, ~15 lines, low risk:** extend the final validation with the two
   geometric checks the harness used — (a) every face wound outward, (b) no input
   point more than K·ε outside any face (O(F·V), author-time only). Broken results
   then hit the existing loud box fallback instead of shipping holes.
   `TryMeasureConcavity` shares `QuickHull`, so it benefits too.
2. **Real fix:** make the builder robust — compute planes/distances in double
   precision and treat the ε-coplanar band as part of the visible region when
   expanding the cone (facet-merging in spirit), then re-run the 467-case sweep.
   Hard guarantee still comes from (1).
3. **Cleanup:** delete the three stale broken `*_ConvexHull_DryInterior.asset`
   files — regeneration only overwrites them on success, so today they keep being
   reused.

## Repro harness

`WaterDebug/_hull_diag/` — standalone .NET port (`Hull.cs` verbatim algorithm,
`UnityStub.cs` float-faithful Unity math, `Program.cs` sweep + audits). Build with
any C# 12 compiler; `dotnet repro.dll` runs the 467-case sweep, `dotnet repro.dll
dump` runs the instrumented deep-dive on the ski mesh.
