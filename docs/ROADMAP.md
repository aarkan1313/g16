# WG16 — Roadmap

Living view of what's done, in-flight, and queued. Newest status at the top of each
section. Pairs with HANDOFF.md (orientation) and DECISIONS.md (the why, per decision).
Last updated: 2026-06-17.

**Pillars:** quality = performance = AAA-ish = best-long-term — regardless of time cost.

---

## ✅ Done & settled (don't redo)
- **Base field** — WG15 5-layer GPU heightfield on a displaced plane, no bake. PROVEN.
  Don't touch its math unless asked.
- **Lighting / atmosphere** — soft sun shadows, SDFGI+SSIL, SSAO, aerial+height fog, AgX
  tonemap, color grade, 6 curated MOOD presets. User: "really good."
- **Material judging** — 738 → 108 accepted materials (the library; choices are fine).

## ⚠ Built this session — UNFLOWN, awaiting user's live review (the gate)
> User is unavailable for visual review; these are mechanically verified only. Listed in
> review priority. All live in `scenes/terrain_lab.tscn`.
1. **Ground Unit 1 — anti-repetition** (Surface tab `anti-repeat`). THE GATE for ground
   units 2-6. Verify it breaks the wallpaper tiling, contrast preserved.
2. **Volumetric clouds + matched ground shadows** (Clouds tab). Shadow-cloud alignment esp.
3. **Cloud-presence suite** — mood cloud color, overcast dim + aerial (coverage-driven),
   reflections, **god rays** (default-OFF, needs tuning).
4. **H1 BRDF check** — clouds-off terrain vs the approved look (custom `light()` rewritten
   to faithful Burley+GGX; confirm no regression).

## 🔨 Active arc — GROUND PRESENTATION rebuild (6 units)
> Ground texturing is "functional but bad"; the presentation layer was never built out.
> Spec: `specs/2026-06-17-ground-presentation-arc-design.md`. Build order = impact; each
> eye-gated before the next. Shared seams: `ar_sample_wp` / `distanceWeight` /
> `groundData`+`breakup_tex`.
- [x] **Unit 1 — anti-repetition** (texture bombing). BUILT; awaiting live verify.
- [ ] **Unit 2 — distance detail** (near detail ⊕ far macro). PLANNED.
- [ ] **Unit 3 — surface depth** (parallax-occlusion + AO/normal/rough → BRDF). PLANNED.
- [ ] **Unit 4 — procedural breakup** (GPU-compute slope/curv/cavity/aspect masks). PLANNED.
- [ ] **Unit 5 — color/value** (replace washed macro tint; survive GI+AgX). PLANNED.
- [ ] **Unit 6 — "and more"** (scatter hooks/wetness/snow-by-aspect/hi-Q triplanar). PLANNED.
  Plans: `plans/2026-06-17-ground-unit{1..6}-*.md`. Don't build 2+ until Unit 1 verified.

## 🌍 Parallel (isolated build chats — no project access → return LIBRARY code to integrate)
- **Procedural flora** — trees/grass/forests/shrubs; GPU-instanced scatter, LOD/impostors,
  wind. Returns drop-in units + interfaces (no demo).
- **World editing / terrain deformation** — brush system (raise/lower/flatten/smooth/noise/
  paint) over a GPU-compute editable height-DELTA layer + undo/redo; additive (doesn't touch
  base field). Returns library, no demo.
- **Integration owed on return (our side):** implement the height/biome/scatter providers,
  wire into the scene + terrain shader. World-editing edits invalidate splat + ground
  breakup masks (Unit 4) + flora scatter → re-bake after edits. Flora scatter density should
  later consume the biome/breakup/coverage fields.

## 🧭 Backlog — researched, not started (the remaining "great" levers)
- **Climate / moisture field** — drives material + color + wetness together (ties into
  ground units 4-5 + flora).
- **Water** — surface rivers/lakes/ocean (additive; placement improves once flow/erosion data
  exists). Considered for an isolated chat; deferred.
- **Erosion** — DEFERRED / high-risk: WG15's graveyard, fundamentally sequential (GPU can't
  fully accelerate), and would modify the SETTLED base field. Only on explicit ask.
- **Composition tooling** — more hero-shot / framing aids.
- **Cloud follow-ups** — snapshot cloud settings into mood presets; tune god rays; proper
  temporal reconstruction (current temporal_frames clamped to 1, no reconstruction).

## 📌 Conventions
- main = clean baseline · `experiment/presentation` = HEAD (work here). Git is the undo.
- Verification: build → headless `--import` → `--auto-shot` A/B + `--profile`, then the
  USER's live eye (no TDD; GPU/visual). Never debug a motion artifact from a still.
- Key gotchas (full list in HANDOFF §4 + memory): ONE Godot at a time; always
  `--rendering-driver vulkan`; local-RD compute can't run `--headless`; per-frame
  compute→material via `CallOnRenderThread` (not CompositorEffect), assign Texture2Drd once.
