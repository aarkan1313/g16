# WG17 Terrain Surfacing — New-Chat Kickoff Prompt

Paste the block below into a fresh Claude Code chat (ideally opened in `C:\Wg16\WG17\terrainengine-10k`).

**Godot 4.6 binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`

---

```
We're migrating my Godot terrain engine into WG17 (C:\Wg16\WG17\terrainengine-10k), Godot 4.6/C#. This slice
is TERRAIN SURFACING — turning the height-color placeholder ground into real PBR materials. WG16's surfacing
was NEVER done and failed ORGANIZATIONALLY: a hardcoded 5-texture array, ~108 good materials orphaned, a
near-black rock force-painted on slopes, and an always-on view-angle relief fade. So this is closer to a fresh
design than a port. "Make whatever works" — but works WELL (Enshrouded-ish bar).

Read in order:
1. Spec:   C:\Wg16\wg-16-project\docs\superpowers\specs\2026-06-28-wg17-terrain-surfacing-design.md
2. Plan 1 (C# core: palette→Texture2DArrays, rules, surfacer, asset import, check):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-surfacing-plan1-palette-rules.md
3. Plan 2 (ground.gdshader path: blend, macro-variation, slope-triplanar, bug-fix rules, eye-gate):
           C:\Wg16\wg-16-project\docs\superpowers\plans\2026-06-28-wg17-surfacing-plan2-shader.md

Assets to draw from: C:\Wg16\wg-16-project\assets\materials\ (738 dirs, each albedo/normal/roughness/ao),
curated by C:\Wg16\wg-16-project\data\material_verdicts.json (~108 owner-PASS). Copy ONLY passing picks into
WG17; EXCLUDE biome_grassland (fail) and rock035 (dropped/near-black).

Execute Plan 1 then Plan 2 task-by-task via superpowers:subagent-driven-development.

The architecture (the anti-WG16): material placement is DATA, not a hardcoded slot array. N materials load into
4 Texture2DArrays; a JSON rule table maps height+slope→top-2 layer weights; the shader blends, macro-variation
anti-tiles, and triplanars on slopes. Adding/swapping a material = edit JSON.

THE TWO BUGS TO DESIGN OUT (structural, not tuned):
- NO view-angle relief fade — normal-map strength rolls off by DISTANCE only, never a fwidth(uv)/view-angle
  term. (This was the "definition vanishes when I move the camera" bug.) surf_relief_view_fade defaults 0;
  SurfaceCheck asserts it.
- NO renderer darkening — AO_LIGHT_AFFECT stays low (not 1.0); no slope-multiply toward black; a material's
  darkness is its own. (No near-black rock on slopes.)

Gotchas: dotnet build after every .cs edit; absolute --path; bare -- for flags; texture import = mipmaps ON,
albedo sRGB, normal/roughness/ao LINEAR. ground.gdshader is SHARED with the (shipped) terrain slice — put the
surfacing path behind a surf_enabled branch so the placeholder still works and the terrain path isn't broken.

My eye-gate (I call it): fly the terrain — grass low/flat, rock steep, snow high, soft transitions, no obvious
tiling, no stretched cliffs. Then the two explicit bug tests: (1) pitch down / move forward→down → surface
definition STAYS; (2) slopes not unexplainably dark. --surfacecheck prints PASS. Record a frame-ms number.
Tuning is JSON-edit + relaunch (no recompile).

Confirm you've read the spec + 2 plans, then show me your plan for Plan 1 Task 1 before executing.
```

---

## Notes for me (not the new chat)
- Two plans: C# core (palette/rules/surfacer/check + asset import), then the shader path + eye-gate.
- This is the ONE slice that edits a shared file (ground.gdshader) — behind surf_enabled, after the terrain slice shipped (it has). Safe.
- The eye-gate IS the gate (look feature); SurfaceCheck structurally guards the relief-fade regression.
- Independent of the sky stack — can run anytime. Pairs well visually with Lighting (Slice A) being on.
- Future (noted, not scoped): biomes (rules are biome-ready), splat maps, stochastic hex-tiling, wetness/snow dynamics.
```
