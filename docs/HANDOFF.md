# WG16 — Handoff (read this first, every new chat)

Last updated: 2026-06-20 (doc-set reset — archived the spec/plan sprawl; `ROADMAP.md` rewritten
thin; both active lanes PAUSED at a combined eye-gate). **Refresh §6 each session.**

Written so a fresh chat with zero context gets productive immediately.

---

## 1. What WG16 is (in three sentences)

A procedural terrain generator in **Godot 4.6 mono (C# + GPU compute)**. It takes the **proven
WG15 base field** — a 5-layer GPU heightfield (continent → uplift → hills → ridges → macro base)
the user confirmed looks good — and renders it on a displaced plane with **no bake stage**
(generated live). Everything since is about look: ground material, lighting/sun/weather, clouds.

The whole point: the previous project (WG15) churned for weeks on erosion/water and got torn down
repeatedly. WG16 keeps only the part that worked (the base field) and rebuilds outward slowly, one
judged piece at a time.

## 2. Posture (how to work here — the user cares about this)

- **PILLARS (the standard for every choice): quality = performance = AAA-ish = long-term-best —
  regardless of time cost.** All four weigh equally; none traded for delivery speed. Lead with the
  most-correct option, not the cheap shortcut. "It works" is not the bar.
- **The discipline rule (see `ROADMAP.md` — this is why we reset):** a lane builds **at most ONE
  phase ahead of the last PASSED eye-gate**; when the gate queue holds work the user can't yet see,
  STOP and bank — don't open new depth. **Thin docs:** a roadmap line + a `DECISIONS.md` entry, not
  a plan-per-lane; a spec only when a feature genuinely needs one.
- **The user's eye is the only gate for look.** Mechanical checks gate correctness/cost, never look.
  Spike cheap, put it in front of the user EARLY, behind live toggles defaulting to the approved
  look. **Never debug a motion artifact from a still — fly it.** When you DO drive a review, drive
  the changes yourself (the user judges; don't make them click).
- **Git is the undo.** `main` = clean baseline; work on `experiment/presentation`. Bad result =
  `git checkout .`, never a hand-revert.
- **Right tool** (see `TECH_STACK.md`): C# default, GPU compute for parallel per-cell math, Rust
  only for a measured serial hot path. Modular — swap a unit without rewriting neighbors.

## 3. Orient (read in this order)

1. This doc (esp. §6 Current State).
2. `ROADMAP.md` — the source of truth: the NOW eye-gate session, the two paused lanes, done/backlog.
3. `NEEDS_REVIEW.md` — the live eye-gate queue (how to see / judge / unblocks, per item).
4. `DECISIONS.md` — every decision, newest first, with the *why*.
5. `TECH_STACK.md` (tool policy + modularity) · `../README.md` (controls, layout).

Frozen history (superseded/not-yet-scheduled designs, all plans, old handoffs) lives in
`docs/archive/` — see `docs/archive/README.md`.

## 4. Environment & how to run

- **Project dir:** `C:\Wg16\wg-16-project`
- **Godot (windowed):** `C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe`
  (`_console.exe` variant for headless `--import` / screenshots).
- **.NET:** `dotnet` 8 on PATH. Build: `dotnet build WG16.csproj`.

Run a scene (always `--rendering-driver vulkan`, absolute `--path`):
```
"<godot.exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn
```

**GOTCHAS (learned the hard way):**
- **One Godot at a time.** Two contend for the GPU → grey-screen hang. Kill first:
  `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe`.
- **Stale windows** show OLD output — if "nothing changed," suspect a dead window; kill all, relaunch one.
- **First run after a new .cs / texture:** `dotnet build` → headless `--import` before launching.
- **Self-serve screenshots:** the labs accept `-- --auto-shot=<path>` (launch, wait ~1.5 s, save PNG, quit).
- **Local-RD compute can't run under `--headless`** (`CreateLocalRenderingDevice()` → null). Bake WINDOWED.
- **Per-frame compute→material:** drive via `RenderingServer.CallOnRenderThread`, assign the
  `Texture2Drd` RID ONCE (a CompositorEffect races it). Hand-packed std430 drifts → use `Std430Writer`.

## 5. The scenes

| Scene | What it is | Key controls |
|-------|-----------|--------------|
| `scenes/review.tscn` | **THE VISUAL REVIEW SCENE** (for the eye-gate session). A copy of the look lab where **number keys 1-9 jump to each gate item** — sets toggles/mood/time/camera from the approved baseline + shows an on-screen "what to judge" banner. Full lab UI still present. | 1 sun disc · 2 time-of-day · 3 GM1 palette (3 again=cycle) · 4 GM2 height+POM · 5 GM3-A variation · 6 clouds · 7 god rays · 8 GI/SDFGI+proxy · 9 BRDF baseline. Guide in `NEEDS_REVIEW.md`. |
| `scenes/terrain_lab.tscn` | **The look lab (active work).** Base field + data-driven panel | Tabs: Zones · Surface · Color · Detail · Splat · **Light** · Clouds · Debug + Presets. Randomize/Lock, FLAT BASELINE, MOOD presets, hero shots. RMB/LMB+WASD fly |
| `scenes/lab.tscn` | Plain base-field lab (clean baseline) | 0–5 layer views · R reseed · G walk · F12 shot |
| `scenes/material_board.tscn` | Material judging loop | 1 = pass · 3 = fail · ← undo · RMB+WASD |
| `scenes/godray_test.tscn` | God-ray screen-space test harness | god-ray sliders/presets |

## 6. Current State — REFRESH EVERY SESSION

> **⮕ TAKING OVER AS IMPLEMENTOR?** Start with
> `docs/superpowers/handoffs/2026-06-20-lanes-implementor-handoff.md` — it covers BOTH lanes
> (Ground/Texture + Sun/Light), their built-but-ungated state, the gated build order, and your
> first job: driving the eye-gate session via `scenes/review.tscn`.

> **2026-06-20 — DOC-SET RESET + FULL-SCOPE RE-ROADMAP; both lanes PAUSED at a combined eye-gate.**
>
> Two lanes (GROUND material, SUN & LIGHT) each ran three phases past their last eye-gate while the
> user couldn't be at a screen. We **paused both**, set everything to default-off / approved-look so
> it's all opt-in, archived the doc sprawl (20 specs + 23 plans → `docs/archive/`), and rewrote
> `ROADMAP.md` thin. Then set the **forward shape (3 phases): (A) finish both lanes FULLY** — Sun &
> Light = full sky system, Ground = full material stack **incl. erosion + water hydrology + surface
> height** (water flow co-designs with erosion; fresh spec→plan→review owed) — **(B) make it a WORLD**
> (scale infra CDLOD→chunks→streaming + biomes + procedural + erosion-at-scale + flora/world-editing),
> **(C) climate & elements** (water render, precip, snow). See `ROADMAP.md` for the authoritative path.
> **Review scene: `scenes/review.tscn`** — keys 1–9 jump to each eye-gate item (see `NEEDS_REVIEW.md`).
>
> **⮕ NEXT: the combined eye-gate session** — run the gates in `ROADMAP.md` ▶ NOW order (light first,
> then base shading, then ground under settled light, then sky, then whole-scene AA). Per-item how/
> judge/unblocks in `NEEDS_REVIEW.md`. **Do NOT start new lane depth** (no GM4+, no GPU atmosphere,
> no GM3 B/C) until the batch is judged — then re-roadmap from the results.
>
> **Built & awaiting that gate:** GROUND — GM1 palette, GM2 real height maps, GM3-A within-area
> variation (all default-off). SUN & LIGHT — Stage 1 sun disc, Stage 2 decouple + time-of-day driver.
> **Also owed:** clouds feature review, god-rays final pass, H1 BRDF check, AA-in-motion, GI/SDFGI
> default decision, GI-proxy fidelity, Unit 2 distance-detail (shelved).
>
> **Approved/settled (don't redo):** base field; lighting base + 6 moods ("really good"); 108
> materials; anti-repetition; compositing-core Phase A blend + AO; G1 placement; GI/shadow proxy.
>
> **Parallel chats (no project access; return libraries to integrate, not review):** procedural
> FLORA; WORLD-EDITING brush. On return: wire providers; edits invalidate splat/breakup/scatter → re-bake.
>
> **Perf:** in-motion ≈9.6 ms (clouds on) vs the 8 ms target; levers (CDLOD #1, SDFGI config #2, cloud
> cost #3) all eye-gated/parked. Always profile `--profmove`. See `performance.md`.

## 7. The material library (gitignored — 2.5 GB)

`assets/materials/<name>/{albedo,normal,roughness,ao}.png` — 738 distinct PBR materials copied from
`D:\assets`. **NOT in git** (too big, re-derivable). The *choices* are: `data/material_verdicts.json`
(pass/fail per material) + `data/material_library.json` (the 108 accepted, what the lab loads).

If `assets/materials/` is missing (fresh clone):
```
python tools/copy_materials.py
"<godot_console.exe>" --headless --path . --import
```

## 8. Git

- Branches: `main` = clean baseline · `experiment/presentation` = current work (HEAD here).
- **Convention (2026-06-20): commit by default.** Commit finished/coherent units of work to
  `experiment/presentation` as you go — clean, scoped commits with good messages — without asking each
  time. Push only when explicitly asked; never touch `main`/force-push unless asked.
- Remote: `https://github.com/aarkan1313/g16.git` (`main` pushed; push others if asked).
- Gitignored: `assets/materials/` (2.5 GB), `.godot/`, build output.
- Restore tags worth knowing: `backup-pre-gi-proxy-2026-06-19`, `backup-clouds-skyshader-2026-06-17`,
  `backup-before-cloud-removal-2026-06-16` (+ branch `backup/clouds-system-2026-06-16`).
