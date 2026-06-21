# S1.5 — Cheaper Terrain Normal (2-tap forward difference) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut the per-vertex normal cost in `ground.gdshader`'s analytic branch from 4 central-difference taps to 2 forward-difference taps (5 `field_height` evals/vertex → 3), recovering the bulk of the audit's ~54% perf lever with zero change to the field math.

**Architecture:** A ~3-line edit inside one `if` branch of one shader's `vertex()`. Reuse the already-computed center height `VERTEX.y`; replace the 4 offset taps with 2 forward offsets (`+x`, `+z`). `normalize()` makes the gradient magnitude immaterial, so the y-component changes from `2.0*e` to `e` with no visual consequence. The baked (`else`) branch and the shared field math are untouched.

**Tech Stack:** Godot 4.6.2 mono spatial shading language, `dotnet build`.

## Global Constraints

- **Build:** `dotnet build WG16.csproj -v q -clp:ErrorsOnly` → must end `0 Error(s)`.
- **Skin not bones:** do NOT touch `field_math.gdshaderinc` or any field math. S1.5 changes ONLY the normal *reconstruction* in `ground.gdshader`'s `use_analytic` branch. `--fieldcheck` (heights) MUST stay `PASS maxAbsDiff=0m` — that is the regression guard proving the field is untouched.
- **Perf budget context:** the 8 ms in-motion target; this change is a step toward it (the quadtree in S2 is the rest). Record the before/after `--profmove` numbers.
- **NO TDD** (GPU/visual project rule). "Test" = build clean → `--fieldcheck` PASS → `--profile --profmove` number → the user's live eye in motion. Auto-shots are a SANITY check only, never the look-gate.
- **⚠ RUN-INVOCATION RULE (or commands silently no-op):** user `--flags` go AFTER a bare `--` separator (`OS.GetCmdlineUserArgs()` is empty without it → flags ignored, scene never quits). `--auto-shot=` needs a real OS path (e.g. `C:/tmp/wg16shots/x.png`), NOT `user://`. Canonical:
  `Godot…_console.exe --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- <USER FLAGS>`. A correct run quits on its own in a few seconds; still alive at ~20s ⇒ a flag no-op'd (check the `--`).
- **ONE Godot at a time** (two contend for the GPU → grey hang). Kill strays first: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`. The scene CANNOT run `--headless`.
- **Gate viewport:** `scenes/terrain_lab.tscn` (the user's chosen look-judging scene; where S1 + the 34 ms baseline live). `--analytic=1` = live field, `--analytic=0` = baked.
- **Git:** work on `experiment/presentation`; stage ONLY this plan's files (NEVER `git add -A` — the sky lane edits in parallel). Commit message footer: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

### Key paths (verified this session)
- `shaders/ground.gdshader` — `vertex()` at lines 87-107; the `if (use_analytic)` branch (lines 88-96) is the ONLY edit site. `analytic_h(vec2)` defined at line 64; `analytic_spacing` is the `e` step.
- `shaders/field_math.gdshaderinc` — shared field math; **do not touch.**
- Perf harness: `--profile=<secs> --profmove` prints `PROFILE: avg … fps (… ms) worst …` then quits (`scripts/lab/TerrainLabUI.Process.cs`). `--fieldcheck` prints `FIELDCHECK: PASS/FAIL` (`scripts/field/FieldCheck.cs`).

---

### Task 1: 2-tap forward-difference normal in the analytic branch

**Files:**
- Modify: `shaders/ground.gdshader:88-96` (the `if (use_analytic)` branch of `vertex()`).

**Interfaces:**
- Consumes: `analytic_h(vec2 world_xz)` (existing, line 64); `analytic_spacing` uniform; `VERTEX`, `NORMAL`, `v_normal`, `v_h` (existing).
- Produces: no new interface — same `VERTEX.y` / `NORMAL` / `v_normal` / `v_h` outputs, computed with 3 field evals instead of 5.

- [ ] **Step 1: Baseline the perf numbers BEFORE the change (so the win is measured, not assumed)**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=0 --profile=5 --profmove > /tmp/s15_baked.log 2>&1 &
P=$!; for i in $(seq 1 12); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s15_baked.log
taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --profile=5 --profmove > /tmp/s15_an_before.log 2>&1 &
P=$!; for i in $(seq 1 15); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s15_an_before.log
```
Expected: two `PROFILE:` lines. Record the baked ms (anchor, ~5.9 ms) and the analytic-BEFORE ms (the 5-eval baseline, ~34 ms on this machine). These are the numbers Step 6 compares against.

- [ ] **Step 2: Make the edit (5 evals → 3, forward difference)**

In `shaders/ground.gdshader`, replace the `if (use_analytic)` block (lines 88-96):

```glsl
    if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        VERTEX.y = analytic_h(wxz);
        float e = analytic_spacing;
        float hl = analytic_h(wxz - vec2(e, 0.0)), hr = analytic_h(wxz + vec2(e, 0.0));
        float hd = analytic_h(wxz - vec2(0.0, e)), hu = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(hl - hr, 2.0 * e, hd - hu));
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
```

with (note: `h0` reuses the height eval; 2 forward taps instead of 4; sign flips because forward diff is `h0 - h(x+e)` = `-dh/dx`, keeping the same outward-normal orientation as the old `hl - hr`):

```glsl
    if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        float e = analytic_spacing;
        float h0 = analytic_h(wxz);                        // center: reused for height AND the normal
        VERTEX.y = h0;
        float hx = analytic_h(wxz + vec2(e, 0.0));         // forward diff (2 taps, not 4): 5 evals -> 3
        float hz = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));   // normalize() makes the gradient magnitude immaterial
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
```

- [ ] **Step 3: Build**

Run: `cd /c/Wg16/wg-16-project && dotnet build WG16.csproj -v q -clp:ErrorsOnly`
Expected: ends with `0 Error(s)`. (C# is untouched, but the editor recompiles the shader on next load — Step 4 is the real shader compile-check.)

- [ ] **Step 4: Regression guard — heights unchanged (`--fieldcheck` PASS) + shader compiles**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
mkdir -p /c/tmp/wg16shots
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --fieldcheck --auto-shot=C:/tmp/wg16shots/s15.png > /tmp/s15_chk.log 2>&1 &
P=$!; for i in $(seq 1 8); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep -iE "FIELDCHECK: (PASS|FAIL)|SHADER ERROR|ground.gdshader|exception" /tmp/s15_chk.log | head
```
Expected: `FIELDCHECK: PASS  maxAbsDiff=0m` (the field math is untouched, so heights are identical) AND no `SHADER ERROR` (the new `vertex()` compiles). If FIELDCHECK is anything but PASS → you touched the field by mistake; revert and redo only the normal lines. If a SHADER ERROR appears → check the edited block's syntax.

- [ ] **Step 5: Perf AFTER the change**

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
"C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --profile=5 --profmove > /tmp/s15_an_after.log 2>&1 &
P=$!; for i in $(seq 1 15); do sleep 2; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
grep PROFILE /tmp/s15_an_after.log
```
Expected: one `PROFILE:` line. Compare to Step 1's analytic-BEFORE: expect a substantial drop (toward the audit's ~25 ms-class number on this machine — the 5→3 eval portion). If there's NO improvement, the shader didn't recompile (re-import) or the wrong branch ran — investigate before claiming the win.

- [ ] **Step 6: Eye-gate — hand the A/B to the USER (do NOT self-judge from thumbnails)**

Capture the analytic(2-tap) vs baked reference at the same camera for the user to compare, AND tell the user to fly `scenes/terrain_lab.tscn` with `--analytic=1` and judge the normals/shading in motion:

```bash
taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe 2>/dev/null; taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null; sleep 1
GE="C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=1 --cam=0,300,0,-30,0 --auto-shot=C:/tmp/wg16shots/s15_2tap.png > /tmp/s15_ab1.log 2>&1 &
P=$!; for i in $(seq 1 8); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
sleep 1
"$GE" --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- --analytic=0 --cam=0,300,0,-30,0 --auto-shot=C:/tmp/wg16shots/s15_baked.png > /tmp/s15_ab2.log 2>&1 &
P=$!; for i in $(seq 1 8); do sleep 3; kill -0 $P 2>/dev/null || break; done; kill -0 $P 2>/dev/null && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
ls -la /c/tmp/wg16shots/s15_2tap.png /c/tmp/wg16shots/s15_baked.png
```
**Gate (USER, in motion):** the 2-tap analytic normals must look the same as before — no flattening, no faceting, no ridge/cliff shading artifacts. This is a REAL check (2-tap forward diff can read subtly more directional than 4-tap central on sharp ridges). The auto-shots are a coarse sanity pair only; the verdict is the user's eye flying the scene. **If the user reports artifacts → fall back to 4-tap (revert Step 2) or revisit; do NOT ship over a "still looks off."**

- [ ] **Step 7: Commit (only after the user approves the look)**

```bash
cd /c/Wg16/wg-16-project
git add shaders/ground.gdshader
git commit -m "$(cat <<'EOF'
S1.5: cheaper analytic normal — 2-tap forward difference (5 evals -> 3)

ground.gdshader's analytic vertex normal used 4 central-difference taps
(+1 height = 5 field_height evals/vertex); replace with 2 forward-diff
taps reusing the center height (3 evals). Recovers the bulk of the audit's
~54% normal lever with ZERO change to the field math (--fieldcheck still
PASS maxAbsDiff=0m). Same KIND of finite-difference normal as before, just
cheaper; baked path untouched. Perf: <before> -> <after> ms in motion.
Look A/B approved by the user in motion (terrain_lab.tscn).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```
(Fill the `<before> -> <after>` ms from Steps 1 & 5 before committing.)

---

## Self-Review

**1. Spec coverage:** The spec's single change (2-tap forward diff, 5→3 evals, `ground.gdshader` `use_analytic` branch only) → Task 1 Step 2. The spec's verification ladder (build → `--fieldcheck` PASS → `--profmove` → eye-gate via terrain_lab.tscn) → Steps 3/4/5/6. Spec's "field math untouched" guard → Step 4's `--fieldcheck`. Spec's out-of-scope (proxy shadows, analytic derivatives, baked branch) → not touched. ✅
**2. Placeholder scan:** No TBD/TODO. Every code step shows full before/after; every run step shows the exact command + expected output. The only fill-in is the commit's `<before> -> <after>` ms, which is data measured in Steps 1/5 (explicitly flagged), not an open placeholder. ✅
**3. Type consistency:** No new symbols. Reuses existing `analytic_h(vec2)`, `analytic_spacing`, `VERTEX`/`NORMAL`/`v_normal`/`v_h`. The sign convention is checked (forward diff `h0 - hx` preserves the old `hl - hr` outward orientation). ✅

## Notes for the executor
- **The win must be MEASURED, not assumed:** Step 1 baselines before, Step 5 measures after, the commit records both. If after ≈ before, the shader didn't re-import — re-run or `--headless --import` first.
- **`--fieldcheck` is the skin-not-bones guard:** if it's not PASS after the edit, you changed the field by accident. The edit is ONLY the 3 normal lines.
- **The look-gate is the user's, in motion** — never claim the 2-tap looks fine from a downscaled auto-shot (the `ground-texture-feedback` lesson). Hand both PNGs over AND have the user fly it.
