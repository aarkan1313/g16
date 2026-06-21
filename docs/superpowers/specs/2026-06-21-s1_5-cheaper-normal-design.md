# WG16 S1.5 — Cheaper Terrain Normal (2-tap forward difference)

Date: 2026-06-21. Status: SPEC (brainstormed + approved). Small single-unit change.
Parent arc: `2026-06-21-infinite-terrain-cdlod-design.md` (§10 S1 result + perf-audit + sequencing).
Follows: S1 (commits 121b583…57b5983 + audit 6bac0ff). Precedes: S2 (quadtree).

## Why this exists
The S1 perf audit (independent, verified) found the live analytic field render costs ~34 ms on the full
2048² no-LOD mesh, and that **the single largest, look-neutral lever (~54%)** is the per-vertex normal:
`ground.gdshader`'s `vertex()` (under `use_analytic`) does **5 `field_height` evaluations per vertex** —
1 for height + **4** for a central-difference normal (`±x`, `±z`). S1.5 cuts the 4 normal taps to **2**
(forward difference, reusing the already-computed center height) → **3 evals/vertex**, recovering the bulk
of that win at zero risk to the field math.

## The approach decision (why 2-tap, not exact analytic derivatives)
The audit floated "analytic derivatives → 1 eval, exact normal." Tracing the field showed that is a large,
risky rewrite, NOT a small fix: `field_height` shapes height through `smoothstep`/`mix` gates
(`uplift.amount`, `w_mtn`, the `belt`/`skirt` bands) and **domain-warp Jacobians**, and only
`slope_damped_fbm` currently carries a gradient — `continent`, `uplift`, `ridged_fbm`, and all the gate/mix
logic carry none. Threading exact derivatives through all of it reworks the proven 5-layer field — directly
against the **skin-not-bones** guardrail — for a gain the eye cannot see (the current normal is *already* a
finite-difference approximation). Per the **long-term-best** pillar (most-correct that's *feasible* and
won't hurt later): the proportionate choice is **2-tap forward difference**. It is the same *kind* of
approximation as today (finite difference), just cheaper; it leaves the bones untouched; and it does not
over-invest in a normal that **S2 will rework anyway** (geomorph computes the normal at the morphed vertex
position — the vertex-shader normal story changes under CDLOD). Full analytic derivatives remain a *future,
measured* option if S2 ever shows the normal cost still matters (it should not, once vertex count drops).

## The change (exact)
**File:** `shaders/ground.gdshader`, `vertex()`, the `if (use_analytic)` branch ONLY. Nothing else.

- **Today (5 evals):** `VERTEX.y = analytic_h(wxz)`; then `hl,hr,hd,hu = analytic_h(wxz ± (e,0) / ± (0,e))`
  (4 taps); `v_normal = normalize(vec3(hl - hr, 2.0*e, hd - hu))`.
- **S1.5 (3 evals):** keep the center height `h0 = analytic_h(wxz)` (already computed for `VERTEX.y`); add
  `hx = analytic_h(wxz + vec2(e,0))` and `hz = analytic_h(wxz + vec2(0,e))`;
  `v_normal = normalize(vec3(h0 - hx, e, h0 - hz))`. Forward difference; `normalize` handles magnitude so
  the gradient scale is immaterial — only the slope ratio matters.
- `e = analytic_spacing` (unchanged).

## Modularity note
The fix lives in the live-field viewport shader (`ground.gdshader`), but the **field math
(`field_math.gdshaderinc`) is untouched and stays shared** with the compute bake — so generation, parity,
and any other consumer are unaffected. The change is a property of *how a vertex shader samples the field
for a normal*, not of the field itself.

## Scope
- **Touches:** `shaders/ground.gdshader` (`vertex()`, `use_analytic` branch) — ~3 lines.
- **Does NOT touch:** `field_math.gdshaderinc`, `field_height.glsl`, `FieldCompute.cs`, `FieldParams.cs`,
  `TerrainLab.cs`, the CLI, the baked (`else`) branch.
- **Out of scope (folded into S2 per the parent spec):** proxy shadows (audit Finding 2) — fixing the
  shared `use_analytic` on the GI proxy needs a separate shadow material, and S2 rewrites the shadow path.
- **Out of scope (YAGNI):** full analytic derivatives; any field-math change.

## Verification (NO TDD — GPU/visual; gate viewport = `scenes/terrain_lab.tscn`)
1. **Build clean** — `dotnet build WG16.csproj -v q -clp:ErrorsOnly` → `0 Error(s)`.
2. **Regression guard (heights unchanged):** `--fieldcheck` still `PASS maxAbsDiff=0m` — proves the field
   math is untouched (S1.5 only changes the normal, never the height). Run via the canonical invocation
   (`-- ` separator; OS auto-shot path).
3. **Perf win:** `-- --analytic=1 --profile=5 --profmove` — expect a substantial drop from the ~34 ms (this
   machine's) / ~54 ms (hot-run) baseline toward the audit's ~25 ms-class number (the 5→3 portion). Record
   baked baseline + analytic-before + analytic-after.
4. **The look-gate — the user's live eye, A/B in motion, through `terrain_lab.tscn`** (the only look
   authority; never a downscaled still, per `ground-texture-feedback`). Capture analytic(2-tap) vs the
   baked reference at the same camera; the user flies it and confirms terrain **shading/normals look the
   same** — no flattening, no faceting. This is a REAL gate, not a formality: a 2-tap forward difference can
   read subtly more directional than 4-tap central on sharp ridges/cliffs. If the user sees ridge/cliff
   shading artifacts → fall back to 4-tap (revert 3 lines) or revisit. Pop/quality regressions are not in
   play here (no LOD yet); this is purely "does the cheaper normal still look right."

## Definition of done
Build clean + `--fieldcheck` PASS (math untouched) + a recorded `--profmove` improvement + the user's
eye-in-motion A/B approval that the 2-tap normal looks the same as before. Then proceed to S2.
