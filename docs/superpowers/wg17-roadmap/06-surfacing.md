# Slice S — Terrain Surfacing  🟡 STAGED  (fresh design)

**What:** turn the height-color placeholder ground into real PBR materials, placed by terrain attributes,
blended + anti-tiled for an infinite world.

**Docs:**
- Spec: [../specs/2026-06-28-wg17-terrain-surfacing-design.md](../specs/2026-06-28-wg17-terrain-surfacing-design.md)
- Plans: [palette+rules (C# core)](../plans/2026-06-28-wg17-surfacing-plan1-palette-rules.md) ·
  [shader path + eye-gate](../plans/2026-06-28-wg17-surfacing-plan2-shader.md)
- Kickoff: [../plans/2026-06-28-wg17-surfacing-KICKOFF-PROMPT.md](../plans/2026-06-28-wg17-surfacing-KICKOFF-PROMPT.md)

**Why fresh, not port:** WG16 surfacing was never done and failed ORGANIZATIONALLY — a hardcoded 5-slot
`_matRoles` array, ~108 owner-passing materials orphaned, near-black rock force-painted on slopes, an
always-on view-angle relief fade.

**Architecture (the anti-WG16):** placement is DATA, not code. N curated materials load into 4
`Texture2DArray`s; a JSON rule table maps height+slope→top-2 layer weights; the shader blends, macro-variation
anti-tiles (always on), triplanars on slopes. Adding/swapping a material = JSON edit. Biome-ready (optional
rule input) but biomes out of scope.

**Two bugs designed out (structural + `SurfaceCheck`):** (1) NO view-angle relief fade — normal strength rolls
off by DISTANCE only (the "definition vanishes when I move camera" bug); (2) NO renderer darkening —
`AO_LIGHT_AFFECT` sane, no near-black slope rock, material darkness is its own.

**Note:** edits the SHARED `ground.gdshader` (behind a `surf_enabled` branch, so the shipped terrain path is
safe). The ~108 passing assets live in WG16 `assets/materials/` curated by `material_verdicts.json`.
