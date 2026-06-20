# GM1 — Curated Palette Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (recommended for this — sequential, shared tree, eye-gated) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Make the 7 ground roles load a **data-driven, curated, contrast-rich** material palette (instead of a hardcoded C# array), then curate it live so the ground reads photoreal, not drab — the first foundation step of the GROUND roadmap.

**Architecture:** A new `data/ground_palette.json` holds named palettes (role→material-name maps). A loader reads the `active` one into `_groundPalette[7]`; the existing `ZoneDefaultMaterialIndex` reads from it (with an explicit warning on any name that isn't in the library — no more silent arbitrary fallback). The existing per-role `zone_mat` dropdowns + `SetZoneMaterial` path then load/refine it live. Then a **live audit + curation** task (the eye-gate) diagnoses *why* the current look reads drab (lighting wash vs material saturation vs value-only contrast vs placement) and locks the default palette.

**Tech Stack:** Godot 4.6 mono; C# (`scripts/lab/TerrainLabUI.Registry.cs`, `TerrainLabUI.Shots.cs`); data-driven JSON (`data/ground_palette.json`); the look lab (`scenes/terrain_lab.tscn`).

## Global Constraints

- **PILLARS:** quality = performance = AAA-ish = best-long-term. Palette load is one-time/startup; **zero per-frame cost** — protects the 8 ms budget. Authoritative roadmap: `specs/2026-06-20-ground-roadmap-to-aaa-design.md`.
- **The user's live eye is the only gate for look.** Mechanical checks gate correctness (loads, applies, no warnings); the user flying it at close/mid decides "photoreal, not drab." No TDD — GPU/visual.
- **Default to the current look where it changes anything judged:** seed `active` to a palette and let the user pick live; the prior hardcoded set is preserved as the `alpine_green` palette so nothing is lost.
- **Find seams by NAME, not line number.** Every palette material NAME must exist in `data/material_library.json` or it warns + clamps (lab-registry gotcha cousin). Verify names against the library.
- **⚠ Shared tree:** stage with `git add` by path; never `git add -A` (another chat owns `GodRays*`/`TerrainLabUI.Cli/Clouds/Process/Shots/cs` *cloud/godray* edits — touch only the palette seam in `Shots.cs`/`Registry.cs`).

## Current-state facts (verified 2026-06-20)

- The per-role material picker **already exists**: `lab_controls.json` `zone_mat` (type `material`) expands to 7 rows; `TerrainLabUI.Registry.cs:241-245` builds each dropdown and sets its initial selection to `ZoneDefaultMaterialIndex(zone)`; `TerrainLabUI.Apply.cs:51` `SetZoneMaterial(zone, name)` loads albedo/normal/rough/ao; `TerrainLab.cs:138` binds `z{zone}_alb` etc.
- `_materials` is the **sorted** list of 108 names, loaded by `LoadLibrary()` (`TerrainLabUI.Registry.cs:16-28`) from `data/material_library.json`.
- `ZoneDefaultMaterialIndex(zone)` (`TerrainLabUI.Shots.cs:45-54`) is a **hardcoded** 7-name contrast palette (`m8_grass_calm`, `dirt`, `16_glacial_till`, `02_coarse_talus`, `rock_dark`, `m14_tundra_moss`, `01_fresh_powder`) with `_materials.IndexOf` and a silent `clamp(zone)` fallback on miss. **All 7 currently resolve.**
- **Finding (auto-shot `C:/tmp/gm1_current_palette.png`):** a distinct palette IS assigned, yet the ground reads uniformly warm-reddish-brown from altitude → "drab" is NOT a missing palette; it's lighting/aerial wash + grey-brown mid materials + value-carried (not hue) contrast. **→ Task 4 (live audit) must isolate this before locking picks.**
- All seed-palette names below were verified present in `data/material_library.json`.

## Environment

- ONE Godot at a time: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (+ `..._console.exe`). Always `--rendering-driver vulkan`.
- Console exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe`; windowed = same without `_console`.
- Build `dotnet build WG16.csproj`. Import `"<console>" --headless --path /c/Wg16/wg-16-project --import`. Launch `"<exe>" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1`.

---

## Task 1: Add `data/ground_palette.json` (data-driven palettes)

**Files:** Create `data/ground_palette.json`.

**Interfaces produced:** a JSON with `active` (string) + `palettes` (map of name → `{label, roles[7]}`), each `roles[i]` a material NAME indexed valley(0)…peak/snow(6).

- [ ] **Step 1: Write the file.** `alpine_green` reproduces the prior hardcoded set (preserves current look); `alpine_stone` is the new default; `arid` is a contrast alternative. All names verified in the library.
```json
{
  "_comment": "Curated ground palettes. 'active' selects which palette's 7 role materials load as the zone_mat dropdown defaults (valley=0 .. peak/snow=6). roles[] entries are material NAMES from data/material_library.json. Per-role live tuning is via the Zones-tab dropdowns; this is the starting point. A name missing from the library warns + clamps (see ZoneDefaultMaterialIndex). Add palettes freely; switch by editing 'active' and relaunching.",
  "active": "alpine_stone",
  "palettes": {
    "alpine_stone": {
      "label": "Alpine Stone",
      "roles": ["13_sun_baked_clay", "01_fine_sand", "13_dry_loose_scree", "02_coarse_talus", "01_columnar_basalt_face", "04_arctic_rock_with_orange_lichen", "01_fresh_powder"]
    },
    "alpine_green": {
      "label": "Alpine Green (prior default)",
      "roles": ["m8_grass_calm", "dirt", "16_glacial_till", "02_coarse_talus", "01_dark_slate", "m14_tundra_moss", "01_fresh_powder"]
    },
    "arid": {
      "label": "Arid",
      "roles": ["13_sun_baked_clay", "05_granite_desert", "01_weathered_sandstone", "13_dry_loose_scree", "01_columnar_basalt_face", "12_dry_lichen_carpet", "01_fine_sand"]
    }
  }
}
```
- [ ] **Step 2: Validate + verify names resolve.**
```bash
cd /c/Wg16/wg-16-project && python -c "
import json
lib=set(m['name'] for m in json.load(open('data/material_library.json'))['materials'])
d=json.load(open('data/ground_palette.json'))
for pn,p in d['palettes'].items():
    miss=[r for r in p['roles'] if r not in lib]
    assert len(p['roles'])==7, (pn,'not 7 roles')
    print(pn, 'OK' if not miss else f'MISSING {miss}')
print('active:', d['active'])
"
```
Expected: each palette `OK`, `active: alpine_stone`.
- [ ] **Step 3: Commit.** `git add data/ground_palette.json && git commit -m "GM1 T1: data-driven ground palettes (alpine_stone default, alpine_green/arid)"`

---

## Task 2: Load the palette — `scripts/lab/TerrainLabUI.Registry.cs`

**Files:** Modify `scripts/lab/TerrainLabUI.Registry.cs`.

**Interfaces produced:** field `private string[]? _groundPalette` (role→material name, or null if file absent/malformed); `LoadGroundPalette()` populates it from the `active` palette.

- [ ] **Step 1: Add the field + loader.** Place the field near `_materials`; place `LoadGroundPalette()` next to `LoadLibrary()`:
```csharp
    private string[]? _groundPalette;   // GM1: role->material name from the active ground_palette.json palette

    private void LoadGroundPalette()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/ground_palette.json");
        if (!System.IO.File.Exists(abs)) { return; }   // null → ZoneDefaultMaterialIndex uses its fallback
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        JsonElement root = doc.RootElement;
        string active = root.GetProperty("active").GetString() ?? "";
        if (root.GetProperty("palettes").TryGetProperty(active, out var pal)
            && pal.TryGetProperty("roles", out var roles))
        {
            _groundPalette = roles.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
            GD.Print($"[ground_palette] active '{active}' loaded ({_groundPalette.Length} roles)");
        }
        else { GD.PushWarning($"[ground_palette] active '{active}' not found → using fallback"); }
    }
```
- [ ] **Step 2: Call it right after `LoadLibrary()`.** Find the `LoadLibrary();` invocation (in `_Ready`/setup, before the registry builds the material controls) and add `LoadGroundPalette();` on the next line. (Must run before Task-3's `ZoneDefaultMaterialIndex` is hit by the `material` control build.)
- [ ] **Step 3: Verify build.** `dotnet build WG16.csproj` → 0 errors.
- [ ] **Step 4: Commit.** `git add scripts/lab/TerrainLabUI.Registry.cs && git commit -m "GM1 T2: load active ground palette into _groundPalette"`

---

## Task 3: Read the palette in `ZoneDefaultMaterialIndex` — `scripts/lab/TerrainLabUI.Shots.cs`

**Files:** Modify `scripts/lab/TerrainLabUI.Shots.cs:45-54`.

**Interfaces consumed:** `_groundPalette` (Task 2), `_materials` (existing).

- [ ] **Step 1: Replace the method body** to prefer the loaded palette, keep the prior hardcoded set as the fallback, and **warn (not silently clamp) on any miss**:
```csharp
    private int ZoneDefaultMaterialIndex(int zone)
    {
        // GM1: data-driven palette (data/ground_palette.json, loaded into _groundPalette).
        // Falls back to the prior hardcoded contrast set, then to a clamped index — with a
        // warning on any miss so a bad name is VISIBLE, not silently arbitrary.
        string[] fallback = { "m8_grass_calm", "dirt", "16_glacial_till",
                              "02_coarse_talus", "rock_dark", "m14_tundra_moss", "01_fresh_powder" };
        string want = (_groundPalette != null && zone >= 0 && zone < _groundPalette.Length)
                      ? _groundPalette[zone]
                      : (zone >= 0 && zone < fallback.Length ? fallback[zone] : "");
        int idx = _materials.IndexOf(want);
        if (idx < 0)
        {
            GD.PushWarning($"[ground_palette] role {zone} material '{want}' not in library → clamped fallback");
            idx = Math.Min(Math.Max(zone, 0), _materials.Count - 1);
        }
        return Math.Max(idx, 0);
    }
```
- [ ] **Step 2: Verify** `dotnet build WG16.csproj` (0 errors) → `"<console>" --headless --path /c/Wg16/wg-16-project --import` (clean).
- [ ] **Step 3: Windowed apply-check.** Launch the auto-shot; confirm the console prints `[ground_palette] active 'alpine_stone' loaded` and **no** `not in library` warnings, and the bake runs:
```bash
cd /c/Wg16/wg-16-project && taskkill //F //IM Godot_v4.6.2-stable_mono_win64_console.exe 2>/dev/null
EXE="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64_console.exe"
"$EXE" --path /c/Wg16/wg-16-project --rendering-driver vulkan scenes/terrain_lab.tscn -- --clouds=0 --groundrules=1 --auto-shot=C:/tmp/gm1_alpine_stone.png 2>&1 | grep -iE "ground_palette|splat baked" | head -5
```
Expected: `[ground_palette] active 'alpine_stone' loaded (7 roles)` + two `splat baked` lines, no palette warnings.
- [ ] **Step 4: Commit.** `git add scripts/lab/TerrainLabUI.Shots.cs && git commit -m "GM1 T3: ZoneDefaultMaterialIndex reads ground_palette + warns on miss"`

---

## Task 4: Live audit + curation (USER + driver) — the eye-gate

**Files:** edits to `data/ground_palette.json` (the chosen picks) only.

This is the gate. The auto-shot showed a distinct palette already reads drab from altitude — so first **isolate why**, then curate against the real cause. Drive the changes; the user judges.

- [ ] **Step 1: Isolate the drabness.** Launch windowed. With the user, A/B these to find the dominant cause (the lab already has the tools — no code):
  - **Lighting wash:** Light tab → switch the mood from the warm default to **midday/neutral**; fly **close** (out of aerial-perspective range). Does the palette suddenly read varied? → the warm/aerial look is masking it (a Light-arc/Unit-5 concern, not palette).
  - **Material saturation/value:** at close range, do `13_sun_baked_clay` / `02_coarse_talus` / `01_columnar_basalt_face` actually look like distinct *colors*, or only distinct *values* (light/dark greys)? → informs whether to pick higher-chroma materials.
  - **Placement concentration:** Splat tab → `splat debug` 1 (dominant role as color); is the visible region dominated by one role? → placement (G1/GM4), not palette.
- [ ] **Step 2: Curate live.** Based on Step 1, tune the 7 roles via the **Zones-tab dropdowns** at close/mid under neutral AND warm light. Try the three seed palettes (edit `active` + relaunch: `alpine_stone` / `alpine_green` / `arid`) as starting points; mix in higher-chroma picks where contrast reads only as value (candidates: lichen `04_arctic_rock_with_orange_lichen`/`12_dry_lichen_carpet`, `m8_grass_calm`, `05_granite_desert`, `07_black_volcanic_sand`, `01_dark_slate`, `01_fresh_powder`). Goal: distinct under the *actual* lighting, coherent biome.
- [ ] **Step 3: Persist the winner.** Write the user-approved 7 picks back into the chosen palette's `roles[]` in `data/ground_palette.json` (and set `active` to it). Relaunch to confirm it loads as default.
- [ ] **Step 4: Record the verdict.** Capture A/B auto-shots (before = `alpine_green`/prior, after = approved) to `C:/tmp/`. If the audit found the cause is lighting-wash or placement (not the materials), **note it** — it re-prioritizes GM3 (within-area + macro color) / the Light arc, and GM1's gate becomes "best achievable palette given current lighting."

---

## Task 5: Sign-off + doc update

- [ ] **Step 1:** On user approval, update `docs/NEEDS_REVIEW.md` (GM1 ✅ + verdict), `docs/ROADMAP.md` (GM1 done → GM2 next), `docs/DECISIONS.md` (one line: chosen palette + any drabness-cause finding), and the GROUND roadmap spec's GM1 status.
- [ ] **Step 2:** Commit by path: `git add data/ground_palette.json docs/NEEDS_REVIEW.md docs/ROADMAP.md docs/DECISIONS.md docs/superpowers/specs/2026-06-20-ground-roadmap-to-aaa-design.md && git commit -m "GM1: curated palette APPROVED (eye-gated) + docs"`
- [ ] **Step 3:** Next is **GM2 (real per-material height maps)** — write its plan. If Task 4 surfaced that lighting/placement (not materials) dominate drabness, reconsider whether GM3 macro-color or the Light arc should jump ahead of GM2.

---

## Self-Review notes

- **Spec coverage:** implements the GROUND roadmap's GM1 ("curated distinct palette; gate reads photoreal, not drab") — data-driven `ground_palette.json` + load path + live curation, exactly as the roadmap's GM1 mechanism describes. The picker reuse (no new UI system) matches the verified current state.
- **Placeholder scan:** no stubs; all code (JSON, loader, `ZoneDefaultMaterialIndex`) is concrete and verified against the live seams; all seed material names verified in the library.
- **Type consistency:** `_groundPalette` (`string[]?`) defined in Task 2, consumed in Task 3; `LoadGroundPalette()` called after `LoadLibrary()`; `_materials.IndexOf` matches the existing usage.
- **Honest scope note:** Task 4 explicitly allows the audit to conclude "materials aren't the dominant drabness cause" → that's a *finding*, not a failure; it re-sequences GM2/GM3/Light, consistent with the roadmap's multi-factor view of "drab." GM1 still delivers the data-driven palette + the best achievable picks.
- **Risk/undo:** data-driven + additive; `git checkout`. The prior hardcoded look is preserved as the `alpine_green` palette. Zero per-frame cost.
