# GROUND lane handoff — start a fresh chat here (2026-06-20)

Copy the block below as the opening prompt for a new chat to take over the WG16 **GROUND / texture
rendering** lane. (This file is the durable copy; the prompt is self-contained.)

---

You're the IMPLEMENTOR for the WG16 GROUND / TEXTURE RENDERING lane. The user flies + judges visuals;
you drive setup, build, and verification. Project: a Godot 4.6 mono (C# + GPU compute) procedural
terrain generator at `C:\Wg16\wg-16-project`.

READ THESE FIRST, in order (authoritative — don't skip):
1. `docs/HANDOFF.md` — env, how to run, gotchas, posture (§2 + §4 non-negotiable).
2. `docs/superpowers/specs/2026-06-20-ground-rendering-system-master-design.md` — **THE ground design.**
   The lane was just reset to a from-scratch, **game-agnostic ground rendering SYSTEM** (north star ≈
   Skyrim/No-Man's-Sky quality bar, NOT photoreal-locked; the tech is the product). 8 components with
   keep/rebuild calls + a foundation-first build order.
3. `docs/superpowers/specs/2026-06-20-ground-g1-compositing-core-design.md` + plan
   `docs/superpowers/plans/2026-06-20-ground-g1-compositing-core.md` — the CURRENT phase (G-1).
4. `docs/NEEDS_REVIEW.md` §1c + §1d — the eye-gate queue (ground items).
5. `docs/DECISIONS.md` (newest first) — the why; don't re-litigate settled calls.

THE STORY SO FAR (this session):
- **Anti-tiling SHIPPED + PASSED.** The blocky tile-seams were the old IQ 2-tap anti-tiling; replaced
  with AAA histogram-preserving tiling (`tile_mode=3`, now default). User: "the new system is good!"
  Dead IQ/hex shader code removed. (Owed cleanup: trim the `tile mode` dropdown to none+histogram +
  drop the hex controls — blocked on `lab_controls.json` being clear of the Sun/Light lane's edits.)
- **Ground RESET to a from-scratch redesign** (master spec above) after the user asked "are we building
  on a bad foundation?" Keepers: base-field geometry · histogram anti-tiling · the zone-placement
  *concept*. Out of scope: erosion/geometry + scale/CDLOD (separate arcs).
- **Phase G-1 (compositing core) — Task 1 BUILT, AWAITING the user's eye-gate.** The close-up
  blocky/stair-stepped/smeary blend was root-caused to the splat **blend weight**: weightmaps were
  8-bit at 4 m/texel and the interlock used that quantized low-res weight as the boundary threshold.
  T1 (`eb4733d`): weightmaps now baked **half-float (`Rgbah`)** → de-quantizes the blend in the DEFAULT
  path (no toggle; placement byte-stable, verified 0.13/255). T2 (sampling mip/aniso): verified clean.
  T3 (`hq_blend` fragment-resolution AA interlock — boundary from full-res height+noise, weight as bias)
  is WRITTEN in the plan but NOT built — build only if T1 isn't enough at the gate.

YOUR FIRST JOB — drive the G-1 Task-1 eye-gate (one Godot at a time, `--rendering-driver vulkan`):
`"<godot>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/review.tscn`
Have the user press `3`, fly CLOSE to a material transition, and check **Splat tab → `splat debug =
mix amt`**: did de-quantizing the weights kill the **stair-stepping**? Is placement unchanged?
- PASS / "good enough" → record (NEEDS_REVIEW §1d + a DECISIONS line) → **Phase G-2** (material data +
  surface depth: real height as a first-class input so surfaces read 3D).
- "Better but still soft/smeary at ~4 m" → build **Task 3 (`hq_blend`)** per the plan, re-gate.
Record every verdict in `NEEDS_REVIEW.md` + a one-line `DECISIONS.md` entry.

NON-NEGOTIABLE RULES:
- **Discipline:** build AT MOST ONE phase ahead of the last PASSED eye-gate. Designing ahead is fine;
  *building* ahead is the trap. Everything behind a toggle defaulting to the approved look. Thin docs.
- **The user's live eye is the only look-gate.** Never judge a motion artifact from a still — fly it.
  Drive the setup for the user; don't make them click. Auto-shots (`-- --auto-shot=<path>`) + `--profmove`
  are for YOUR mechanical checks, not the look verdict.
- **Pillars:** quality = performance = AAA-ish = best-long-term, regardless of time cost. Lead with the
  better option. Perf budget 8 ms in-motion (`--profmove`).
- **Stay in your lane's files:** `shaders/terrain_lab.gdshader`, `shaders/splat_weights.glsl`,
  `scripts/lab/TerrainLab.cs`, `SplatCompute.cs`, `HistogramCompute.cs`, `TerrainLabUI.GroundReview.cs`,
  `data/ground_palette.json`. **Do NOT** edit cloud/lighting files (`cloud_sky.gdshader`, `CloudVolume.cs`,
  `LightingState.cs`, `TerrainLabUI.Lighting/Moods/Process.cs`) — the Sun/Light lane owns them.
- **Shared files (other lane edits them, often uncommitted):** `data/lab_controls.json`, `docs/ROADMAP.md`,
  `docs/DECISIONS.md`, `docs/NEEDS_REVIEW.md`, `scripts/lab/TerrainLabUI.Review.cs`. Keep edits minimal +
  additive; **at commit, stage ONLY your hunks** (`git diff <file>` to confirm) — never drag their
  in-progress work into your commit. The ground review bank lives on **Shift+1-9** (`TerrainLabUI.
  GroundReview.cs`, your own partial via `_ShortcutInput`); plain 1-9 are the shared light/sky presets.
- **Commit by default** to `experiment/presentation` (HEAD). Push only when asked. End commit messages
  with `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.

GOTCHAS: ONE Godot at a time (`taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` first; two contend →
grey hang). Local-RD compute (splat/height/histogram bakes) **won't run `--headless`** — bake windowed;
`--headless --import` only compile-checks. A control `param` naming a missing uniform silently no-ops.
The cloud "not valid texture / us is null" error burst at lighting changes is a **documented benign**
radiance-rebake spam (Sun/Light lane) — ignore it.

Start by reading the docs above, then tell me your understanding of the ground lane's current state and
how you'll drive the G-1 Task-1 gate. Then we go.
