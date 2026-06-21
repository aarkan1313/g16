# START HERE — Ground stripped to a clean base; next arc = infinite/CDLOD terrain

**For a fresh chat with zero context.** The ground surfacing was just stripped back to a minimal
placeholder. Your job is the NEXT arc: **plan, then build, the infinite procedural terrain (CDLOD)** —
gate-first, on a base that's already mostly ready. Read this top-to-bottom, then read the two design
docs in §2 before proposing anything.

---

## 1. The one-paragraph why

WG16 is a procedural terrain generator (Godot 4.6.2 mono, C# + GPU compute). Its **sky is genuinely AAA**
(physical Hillaire atmosphere, volumetric clouds, aerial perspective, decoupled Time×Weather×Mood lighting,
sun/moon/stars/Milky-Way/god-rays/cloud-shadows) — the proven pillar. Its **ground** has been the graveyard:
the most recent attempt (per-pixel-procedural "v2" material system) produced a shredded hard-edged material
patchwork at close range that couldn't be tuned out. On **2026-06-21 the user called a full strip**: tear the
ground SURFACING back to a minimal height-color placeholder, keep the proven base field + sky + light, and
**pivot to the real goal — a Skyrim-look ground that is adaptable, INFINITE, and procedural**, following the
pillars. The strip is done and committed. You pick up from the clean base.

## 2. Read these two, in order, before designing

1. **The terrain-LOD roadmap (the design anchor):** `docs/superpowers/specs/2026-06-18-terrain-lod-roadmap-design.md`
   — written post-mortem-first. WG1–15 died from **elevation + quality POPS** (missing *continuous* LOD, not
   topology). Conclusion: **CDLOD** (quadtree + per-vertex geomorph), explicitly **NOT clipmap**. Staged gate:
   **T1 = pop-free continuous LOD on the CURRENT fixed region, in motion, by eye, BEFORE any tiles/streaming**
   → T2 stable world tiles → T3 streaming = true-infinite.
2. **The north-star roadmap:** `docs/ROADMAP-northstar.md` — the whole feature surface, status-audited,
   with the LOD/chunk block flagged as the graveyard.

## 3. The audit (already done this session — its payoff de-risks everything)

**The heightfield is ALREADY infinite/LOD-ready.** `shaders/field_height.glsl` is a pure world-space function
`field_height(world_xz, seed, spacing)`; `scripts/field/FieldCompute.cs` `ProducePage(origin, res, spacing)`
makes **any** tile at **any** LOD — seamless + deterministic by construction (adjacent tiles share edge
world-positions); octaves are **band-limited by `spacing`** so the same terrain renders cleanly at any
coarseness (no LOD resample mismatch — the classic pop source is gone for free). So "infinite" is the
**management layer** (which tiles, what LOD, geomorph, seam-stitch), NOT a generation problem.
- **Caveats (T3/streaming-era, flag but don't block):** generation is **synchronous-blocking** with a **CPU
  readback per page** (streaming wants async / GPU-resident heights); `world_xz` is **float32** with
  integer-cell hashing → precision degrades very far from origin (floating-origin rebasing eventually).

## 4. ⚠ Guardrails — do NOT skip (this is why WG1–15 died)

- **The graveyard.** Terrain LOD / clipmap / infinite streaming killed WG1–15. Memory rule:
  *"do a full terrain roadmap starting from why the clipmap failed before any mesh-LOD work; never
  unilaterally add a clipmap."* That roadmap EXISTS (§2.1) — execute it, don't reinvent. **Use CDLOD, not
  clipmap.** The non-negotiable is **pop-free CONTINUOUS LOD, proven in motion (T1) before any infra**.
- **Skin not bones.** Do NOT touch the base field geometry (`field_height.glsl` / `FieldCompute` /
  `FieldParams`) — it's the proven bones (8 seeds + this audit). The CDLOD geomorph samples it; it isn't rebuilt.
- **Pillars:** quality = performance = AAA-ish = best-long-term, regardless of time cost; lead with the
  most-correct option; **one focused pillar at a time**; build behind a toggle where possible; **never big-bang**.
  Perf budget = **8 ms in-motion** (`--profmove`). The current single no-LOD mesh is ~3.8 ms of that floor —
  T1 is what pays it back.
- **The user's live eye is the ONLY look-gate**, judged in MOTION, never from a still. Hard lesson this
  session (`ground-texture-feedback` memory): **downscaled auto-shots HIDE close-range/per-pixel artifacts** —
  a thumbnail read "fine" while the user saw it was shredded at full res. NEVER claim a look PASSes from an
  auto-shot; hand it to the user, or say "unverified." When the user says "still bad" once, STOP tuning and
  question the approach.

## 5. Current state (what exists, post-strip)

- **Committed** on `experiment/presentation`: `afef3b1` (the strip) + the zone_names crash fix. Clean tree
  except the user's sky-lane WIP (see §6).
- **The placeholder ground:** `shaders/ground.gdshader` = minimal — displace from the heightfield + a
  height/slope **color ramp** (green low → tan slope → grey rock → snow) + Godot default lighting. Every
  color/threshold is a tunable uniform. `scripts/lab/TerrainLab.cs` is now just *field → displaced PlaneMesh →
  placeholder material* + the GI/shadow proxy (perf) + generic shader-param passthroughs the sky uses
  (`cloud_shadow_*`, `cam_world`). It renders coherent, lit, ~170 fps — "doesn't look terrible" (user's words).
- **DELETED** (the failed v2 + old material/splat system): `terrain_lab.gdshader`, `height_from_normal.glsl`,
  `splat_weights.glsl`, `SplatCompute.cs`, `HistogramCompute.cs`, `HeightCompute.cs`, `GroundMaterialArrays.cs`,
  `TerrainLabUI.GroundReview.cs`, `data/ground_materials.json`, `data/ground_review.json`.
- **KEPT for later:** the bones (§4); ALL sky/cloud/atmosphere/light; the **material-library + board tooling**
  (`assets/materials/` ~108 curated PBR sets, `data/material_library.json`, `data/ground_palette.json`,
  `MaterialBoard.cs`, `shaders/material_board.gdshader`) — for the eventual real Skyrim surfacing.
- **Lab UI:** `data/lab_controls.json` tabs are now **Light / Clouds / Night / Debug** (the Zones/Surface/
  Color/Detail/Splat material tabs were stripped). Scenes: `scenes/terrain_lab.tscn` (free-fly lab),
  `scenes/review.tscn` (ReviewMode keys 1-9: sky/cloud/night gates; ground gates 3-5 removed in the strip).

## 6. ⚠ Concurrency — the user edits the sky lane LIVE in parallel

The user actively works the **sky/celestial lane** (Celestial-C1 night sky: galaxy/nebula/stars/Milky-Way)
in the SAME `TerrainLabUI.*` partials + `data/lab_controls.json` + `data/night_sky_presets.json` +
`LightingState.cs` + `CloudVolume.cs` / atmosphere. **Stage only YOUR specific files** (`git add <paths>`,
NEVER `git add -A`). If a shared file shows unrelated uncommitted changes, those are the user's WIP — add only
your hunks, don't sweep theirs. Their `git -a` has occasionally swept an uncommitted hunk of mine into their
commit — harmless, but commit per task promptly to shrink the window. Work on `experiment/presentation`;
**push only when asked.**

## 7. Build / run / verify (project convention — NOT pytest)

- **Build:** `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly` → expect `0 Error(s)`.
- **Godot:** windowed `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`;
  headless `..._console.exe`. Always `--rendering-driver vulkan`, absolute `--path /c/Wg16/wg-16-project`.
- **ONE Godot at a time** (two contend for the GPU → grey hang). Kill strays first:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **Gotchas:** the scene CANNOT run `--headless` — `FieldCompute`'s ctor (local RenderingDevice) NullRefs
  without a GPU context; run windowed. `--headless --import` is fine (compile-checks shaders).
- **Verification model (NO TDD — it's GPU/visual, a standing project rule):** build clean → headless
  `--import` → optional mechanical CLI self-check that reads back state + prints PASS/FAIL + quits → an
  `--auto-shot=<png>` for a SANITY check only → **the user's live eye in MOTION for the look** (the real gate,
  per §4). Auto-shots do NOT validate look or catch UI/C# crashes (only grep them for shader/script errors AND
  exceptions — a render can succeed while a panel-build throws).

## 8. Your first move

The user's sequence: A (audit, **done**) + C (infinite terrain) — with B (the placeholder, **done**) just so
the world isn't blank. So **C is the work, and the user said "plan AFTER the strip."** Do it disciplined:

1. **Brainstorm + (re)spec C with the user** — use `superpowers:brainstorming`. The §2.1 CDLOD roadmap is the
   anchor; refresh it against the strip (its geomorph references the now-deleted `terrain_lab.gdshader`
   `height_at` — that displacement now lives in the new minimal `ground.gdshader`; the heightfield is a baked
   `Rf` texture today, single 8 km region). Confirm scope/sequence with the user before building.
2. **Build T1 FIRST and gate it:** pop-free continuous CDLOD (quadtree + vertex geomorph + detail cross-fade)
   on the CURRENT fixed region — proven by the user's eye in motion (ZERO elevation/quality pop) BEFORE any
   T2 tiles / T3 streaming. This is the anti-WG1-15 discipline; it also pays back the ~3.8 ms mesh floor.
3. Only then T2 (world tiles) → T3 (streaming → infinite). The real Skyrim **surfacing** (replacing the
   height-color placeholder, reusing the kept material library) is its own later arc — the user deprioritized
   it behind the infinite geometry; don't braid it in.

**Definition of done for the FIRST step:** a written, user-approved spec/plan for C (refreshed CDLOD), then a
T1 build that the user flies and confirms pop-free in motion. Do NOT enter T2/T3 until T1 passes the eye-gate.
