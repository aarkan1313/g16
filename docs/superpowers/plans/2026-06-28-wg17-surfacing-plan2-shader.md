# WG17 Terrain Surfacing — Plan 2 of 2: ground.gdshader path + Eye-Gate

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the surfacing fragment path to `ground.gdshader` — sample the top-2 rule-selected material layers from the texture arrays, blend them, apply macro-variation anti-tiling and slope-triplanar, output PBR — with the two WG16 look-bugs designed out (no view-angle relief fade, no renderer darkening). End at the eye-gate.

**Architecture:** Per fragment: evaluate the packed rules (from Plan 1's uniforms) against height+slope → top-2 layer indices + blend factor; sample both layers (triplanar where steep); macro-variation modulation; blend → ALBEDO/NORMAL/ROUGHNESS/AO. Normal strength rolls off by DISTANCE only, never view angle.

**Tech Stack:** Godot 4.6 GDShader (spatial), `Texture2DArray` sampling.

**Plan set:** Plan 1 = C# core (palette/rules/surfacer/check, prerequisite). **Plan 2 (this) = shader path + eye-gate.**

**Reference:** Spec `…specs/2026-06-28-wg17-terrain-surfacing-design.md` (§5 shader path + the two bug rules, §7 eye-gate). The `surf_*` uniforms are bound by Plan 1's `TerrainSurfacer.Bind`.

## Global Constraints

- **Target repo:** `C:\Wg16\WG17\terrainengine-10k`.
- **Godot binary:** `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- **C# rebuild after every .cs edit; absolute `--path`; bare `--` for flags. Shaders hot-compile (no rebuild needed for .gdshader-only edits) — but rebuild if any .cs changed.**
- **Modifies a SHARED file** (`ground.gdshader`, owned by the shipped terrain slice). Add the surfacing path behind a `surf_enabled` branch so the placeholder still works when off; do not break the terrain slice's height/normal/geomorph/cache path.
- **THE TWO BUG RULES (structural, from spec §5):**
  - **NO view-angle relief fade.** Normal-map strength may roll off by DISTANCE (mip/LOD) but NEVER by a view-angle term like `length(fwidth(uv))`. Expose `surf_relief_view_fade` defaulting to 0 (off) so `SurfaceCheck` can assert it.
  - **NO renderer darkening.** `AO_LIGHT_AFFECT` stays low/sane (not 1.0); no slope-based multiply toward black; material darkness is the material's own.
- **Anti-tiling (always on):** macro-variation modulation, not a toggle.
- **Triplanar slope-gated:** only steep fragments pay the 3-axis cost.
- **Depends on Plan 1** (palette arrays + rule uniforms bound via `surf_*`).

---

## File Structure (this plan)

```
shaders/ground.gdshader (MODIFY)   # Tasks 1-4 — add the surfacing fragment path behind surf_enabled
shaders/surface_lib.gdshaderinc    # Task 1 (optional) — rule-eval + triplanar + macro helpers (keep ground.gdshader lean)
```

---

### Task 1: Uniform block + rule evaluation (top-2 selection)

**Files:** Modify `shaders/ground.gdshader`; optionally create `shaders/surface_lib.gdshaderinc`

**Interfaces:**
- Consumes: the `surf_*` uniforms bound by Plan 1's `TerrainSurfacer` (texture arrays, `surf_rule_data`, `surf_rule_count`, `surf_tiling[]`, `surf_tint[]`, blend/macro/triplanar scalars, `surf_enabled`, `surf_relief_view_fade`).
- Produces: a fragment-stage function `surf_select(float height, float slopeDeg, out int l0, out int l1, out float blend)` returning the top-2 layer indices + blend factor from the rules.

- [ ] **Step 1: Declare the uniform block**

In `ground.gdshader`, add the uniform declarations matching Plan 1's `TerrainSurfacer.Bind` exactly:
```glsl
uniform sampler2DArray surf_albedo_arr : source_color;
uniform sampler2DArray surf_normal_arr;
uniform sampler2DArray surf_rough_arr;
uniform sampler2DArray surf_ao_arr;
uniform sampler2D surf_rule_data;      // or a float[] — match Plan 1's choice (RGBA32F, 1 texel/rule)
uniform int   surf_rule_count = 0;
uniform float surf_tiling[16];
uniform vec3  surf_tint[16];
uniform float surf_blend_width_m = 80.0;
uniform float surf_slope_blend_deg = 8.0;
uniform float surf_macro_scale_m = 1200.0;
uniform float surf_macro_strength = 0.35;
uniform float surf_tri_on_deg = 38.0;
uniform float surf_tri_sharpness = 6.0;
uniform bool  surf_enabled = false;
uniform float surf_relief_view_fade = 0.0;   // MUST default 0 — the banned view-angle fade (SurfaceCheck asserts)
```

- [ ] **Step 2: Write the rule-eval (top-2 by weight)**

Add `surf_select(...)`: loop `surf_rule_count` rules; for each, compute a weight = `rule.weight × smoothstep height-band (± surf_blend_width_m) × smoothstep slope-band (± surf_slope_blend_deg)`; track the two highest-weight layers; output their indices + a normalized blend factor. (Read each rule's 6 floats from `surf_rule_data`.) Keep it in `surface_lib.gdshaderinc` if it keeps `ground.gdshader` lean.

- [ ] **Step 3: Build + commit (compiles; not wired into output yet)**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" --import
git add -A && git commit -m "feat(surfacing): ground.gdshader uniform block + top-2 rule selection"
```
Expected: shader compiles (import clean).

---

### Task 2: Layer sampling + slope triplanar

**Files:** Modify `shaders/ground.gdshader` (+ `surface_lib.gdshaderinc`)

**Interfaces:**
- Consumes: `surf_select` (Task 1); the fragment's world position + geometric normal (already in the shader).
- Produces: `surf_sample_layer(int layer, vec3 worldPos, vec3 worldNormal, float slopeDeg, out vec3 alb, out vec3 nrm, out float rough, out float ao)` — top-down UV sampling on flat ground, triplanar where `slopeDeg > surf_tri_on_deg`.

- [ ] **Step 1: Write surf_sample_layer with slope-gated triplanar**

Top-down path: `uv = worldPos.xz / surf_tiling[layer]`; sample the four arrays at `vec3(uv, layer)`. Triplanar path (when steep): project on X/Y/Z planes, blend by `pow(abs(normal), surf_tri_sharpness)` normalized; sample each plane, blend. Lerp top-down↔triplanar across the `surf_tri_on_deg` boundary so there's no seam. Apply `surf_tint[layer]` to albedo.

- [ ] **Step 2: Build/import + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" --import
git add -A && git commit -m "feat(surfacing): per-layer sampling with slope-gated triplanar"
```

---

### Task 3: Macro-variation + blend + PBR output (the bug-fix rules)

**Files:** Modify `shaders/ground.gdshader`

**Interfaces:**
- Consumes: `surf_select`, `surf_sample_layer`.
- Produces: the `surf_enabled` fragment branch that writes ALBEDO/NORMAL/ROUGHNESS/AO.

- [ ] **Step 1: Wire the surfacing branch**

In the fragment function, `if (surf_enabled) { ... }` (else keep the placeholder height-color path):
1. compute `height = world Y`, `slopeDeg = degrees(acos(worldNormal.y))`.
2. `surf_select(...)` → l0, l1, blend.
3. `surf_sample_layer` for l0 and l1; lerp the four outputs by `blend`.
4. **Macro-variation (always on):** sample a low-freq value noise at `worldPos.xz / surf_macro_scale_m`; modulate albedo brightness/tint by `mix(1.0, noise, surf_macro_strength)`. (Optionally also blend a 2nd UV scale of the albedo for near/far break-up.)
5. Output: `ALBEDO = blendedAlbedo; NORMAL = blendedNormalMapped; ROUGHNESS = blendedRough; AO = blendedAo;`.

- [ ] **Step 2: Apply the two bug-fix rules EXPLICITLY**

- **Relief:** when applying the tangent-space normal map, roll its strength by DISTANCE only (e.g. a mip/LOD or world-distance factor) — do NOT multiply by any `fwidth(uv)`/view-angle term. Gate the entire normal-map perturbation behind `surf_relief_view_fade` being a no-op by default (it stays 0 → no view fade). A comment must state: "view-angle relief fade is BANNED (WG16 bug); distance rolloff only."
- **Darkening:** set `AO_LIGHT_AFFECT` to a low value (e.g. 0.2–0.3, NOT 1.0); do NOT multiply ALBEDO toward black by slope. The only darkness is the material's own albedo + correct N·L lighting.

- [ ] **Step 3: Build/import + commit**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && "C:/Users/josep/Downloads/.../Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" --import
git add -A && git commit -m "feat(surfacing): macro-variation + 2-layer blend + PBR out — no view-relief-fade, no renderer darkening"
```

---

### Task 4: Enable + run SurfaceCheck

**Files:** none (flips `surf_enabled` via Plan 1's binder; runs the check)

- [ ] **Step 1: Default surfacing on**

Ensure `TerrainSurfacer.Bind` sets `surf_enabled = true` (Plan 1 Task 4). Rebuild.

- [ ] **Step 2: Run SurfaceCheck**

```bash
cd "C:/Wg16/WG17/terrainengine-10k" && dotnet build Terrainengine10k.csproj && \
  "C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --path "C:/Wg16/WG17/terrainengine-10k" -- --surfacecheck
```
Expected: `SURFACE PASS materials=<N> relief-view-fade=OFF`, exit 0.

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat(surfacing): enable surfacing by default — SurfaceCheck PASS"
```

---

### Task 5: The eye-gate (the primary gate)

**Files:** none (manual visual gate + tuning loop)

- [ ] **Step 1: Launch + fly the terrain**

```bash
"C:/Wg16/WG17/terrainengine-10k" ... (launch, ideally with Lighting Slice A on for correct look)
```
Expected: grass low/flat, rock steep, snow high; **soft blended transitions** (no hard bands); **no obvious tiling** at distance; **no stretched textures on cliffs**.

- [ ] **Step 2: The two explicit bug tests**

- **Relief:** pitch down / move the camera forward→down at a fixed spot → surface **definition STAYS** (does not flatten to "clay"). This is the exact WG16 failure; it must not reproduce.
- **Darkening:** slopes are **not unexplainably dark** beyond correct sun-angle shading.

- [ ] **Step 3: Tune via JSON (no recompile) if needed**

If placement/blend/scale need work, edit `data/surface_rules.json` (thresholds, blend widths, macro scale/strength, triplanar angle) and/or `data/surface_palette.json` (swap a layer dir / tiling) and relaunch. Iterate until the eye-gate passes.

- [ ] **Step 4: Profile + slice marker + migration note**

Record frame ms (surfacing on vs placeholder). Commit a marker; append a "WG17 terrain surfacing outcome" note (final palette, bug tests PASS, frame ms) to `C:\Wg16\wg-16-project\docs\MIGRATION-AUDIT-2026-06-28.md` in WG16.
```bash
git commit --allow-empty -m "milestone(surfacing): eye-gate PASS — blended PBR ground, relief stable, no darkening (<X>ms)"
```

---

## Self-Review

**Spec coverage (Plan 2 = shader path):** §5 step 1 rule weights → Task 1; step 2 sample → Task 2; step 3 triplanar → Task 2; step 4 macro-variation → Task 3; step 5 blend → Task 3; the two bug rules (no view relief-fade, no renderer darkening) → Task 3 Step 2 (structural) + Task 1 (`surf_relief_view_fade=0` default); §7 SurfaceCheck + eye-gate (both bug tests) + JSON tuning loop → Tasks 4–5; §9 DoD → Task 5. ✓

**Placeholder scan:** the `...` in some launch lines abbreviates the full Godot path in Global Constraints; `<X>ms`/`<N>` are measured/loaded values. The macro-noise function is specified (low-freq value noise at `surf_macro_scale_m`), not "add noise". No TBD/"handle edge cases". ✓

**Type consistency:** every `surf_*` uniform matches Plan 1's `TerrainSurfacer.Bind` names; `surf_select`/`surf_sample_layer` signatures consistent across Tasks 1–3; `surf_relief_view_fade` default 0 matches `SurfaceCheck`'s assertion (Plan 1 Task 5). `surf_enabled` branch preserves the terrain slice's placeholder path. ✓

**Note:** surfacing is a look feature — the eye-gate (Task 5) is the real gate, with the two WG16 bugs as explicit, named tests. `SurfaceCheck` structurally guards the relief-fade regression. The shared-file edit (`ground.gdshader`) is behind `surf_enabled` so it can't break the shipped terrain path.
