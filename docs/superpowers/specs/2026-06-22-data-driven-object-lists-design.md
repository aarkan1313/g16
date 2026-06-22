# Data-Driven Object Lists — a reusable "formula", with Luminaries as the first consumer — DESIGN

> **Status: DESIGN (2026-06-22).** Brainstormed + core approved by the user ("a formula for the future"). This spec
> establishes a **reusable schema-driven list-of-objects pattern** for the lab's data-driven UI, then applies it to
> the Celestial C3 luminaries (which currently hardcode their per-body settings as `LightingComposer` constants).
> Build behind the discipline rule (one unit past the last passed gate); each unit is independently eye/round-trip
> gated. Successor to the C3 build (NEEDS_REVIEW 12, PASS 2026-06-22).

**Goal:** Let a user **select the number of sky bodies and edit every per-body setting easily**, fully data-driven —
and do it as a **generic pattern** so future systems (flora types, biome layers, weather cells, …) data-drive the same
way for ~free. This is also the first nested section of the eventual one-big-"world config" JSON a consumer authors.

**Architecture in one line:** add an `objectlist` registry control type + an **item schema** (one object's fields,
reusing the existing control types) + a **generic list-editor widget** (add/remove/duplicate/reorder, per-item
sub-panel) + **JSON-array save/load** via the existing preset machinery. Luminaries are the first item schema.

**Tech stack:** Godot 4.6 mono (C#); the existing data-driven lab registry (`data/lab_controls.json` →
`TerrainLabUI.Registry.cs` builds the UI) + preset machinery (`terrain_presets.json` already serializes all control
state to JSON); `LightingComposer` + the `Luminary` record (built in C3).

---

## Global Constraints (from the existing systems)

- **The registry is FLAT today.** `lab_controls.json` is a flat array of scalar controls; `_byId` is a flat
  `Dictionary<string, LabControl>`; presets save/load by flat id (`terrain_presets.json`). The objectlist must add a
  **parallel nested structure WITHOUT breaking the flat path** — existing controls/presets keep working unchanged.
- **Reuse the widget builders.** A list item's sub-panel must be built from the SAME per-type widget code the flat
  registry uses (slider/color/enum/toggle in `TerrainLabUI.Registry.cs`), not a parallel re-implementation.
- **The default look must not change.** `data/luminaries.json` ships with exactly today's 1 sun + 1 moon (+ the
  C3-tuned companion values as data, used only when bodies are added) → byte-identical default sky (regression-safe).
- **`LightingComposer` is the one writer.** The loaded luminary list becomes the composer's source of truth; the
  budgeter + disc/atmosphere rendering (C3 Units 4-5) are UNCHANGED — only *where the list comes from* changes.
- **Backward-compat during transition:** existing sun/celestial/fantasy presets + `--suns`/`--moons` + the Night-tab
  `extra suns`/`extra moons` sliders must still work (as conveniences that add/remove default bodies in the list).
- Launch/verify gotchas unchanged (one Godot at a time, `--rendering-driver vulkan`, `--` separator, windowed for GPU).

---

## Current state (what this generalizes)

- **Registry:** `TerrainLabUI.Registry.cs` `LoadRegistry()` parses `lab_controls.json` into flat `LabControl`s; per
  type it builds a widget (`slider`/`scenef`/`scene`/`scenecolor`/`enum`/`material`/`companion`). `_byId` maps id →
  control. `BuildPanel()` lays them out by tab.
- **Presets:** `terrain_presets.json` saves every control's value + lock (`TerrainLabUI.UserPresets.cs`); the sun/
  celestial/fantasy/cloud preset JSONs set control ids via `SetWidgetValue`.
- **Luminaries (C3):** `LightingComposer` holds the live sun/moon state + builds extra suns/moons from **hardcoded
  constants** (`ExtraSunColors`, `ExtraSunSizeFac`, `ExtraMoonColors`, `ExtraMoonPhases`, az offsets) gated by
  `ExtraSunCount`/`ExtraMoonCount`. The `Luminary` record (`Luminary.cs`) is ALREADY the per-body schema; it's just
  not yet sourced from data.

---

## The formula — schema-driven object lists

### 1. The `objectlist` control type
A new entry type in the registry JSON:
```json
{ "type": "objectlist", "id": "luminaries", "tab": "Night", "label": "Sky bodies",
  "item_schema": "luminary", "data": "res://data/luminaries.json", "min_items": 1, "max_items": 7 }
```
- `item_schema` names a schema (below) describing one object's fields.
- `data` is the JSON array file (the live source of truth at runtime); `min/max_items` bound the list.

### 2. Item schemas
A schema describes one object's fields **using the existing control types**, in a new `data/item_schemas.json`
(or a `schemas` block in `lab_controls.json`):
```json
{ "luminary": [
    { "id": "kind",  "type": "enum",  "label": "kind", "options": ["Sun","Moon"], "default": 0 },
    { "id": "color", "type": "color", "label": "color", "default": [1.0,0.72,0.4] },
    { "id": "size",  "type": "float", "label": "size (deg)", "min": 0.1, "max": 6.0, "default": 0.6 },
    { "id": "phase", "type": "float", "label": "phase", "min": 0.0, "max": 1.0, "default": 1.0 },
    { "id": "az_offset", "type": "float", "label": "az offset", "min": -180, "max": 180, "default": 0 },
    { "id": "decl_scale", "type": "float", "label": "decl scale", "min": 0.1, "max": 1.2, "default": 1.0 },
    { "id": "casts_shadow", "type": "bool", "label": "casts shadow", "default": true },
    { "id": "atmosphere",   "type": "bool", "label": "scatters sky", "default": true },
    { "id": "priority", "type": "float", "label": "priority", "min": 0, "max": 100, "default": 50 }
  ] }
```
Every field type already has a widget builder — the list editor reuses them.

### 3. The generic list-editor widget (`ObjectListControl`)
A new UI control (`scripts/lab/ObjectListControl.cs`) rendered for an `objectlist` entry:
- Header: the label + **`+ add`** (clones the schema defaults; disabled at `max_items`).
- One **collapsible row per item**: a title (e.g. "Sun · gold"), **remove** (disabled at `min_items`),
  **duplicate**, **▲/▼ reorder**, and an expandable sub-panel of the item's fields — each field built by the
  **existing widget builder** for its type, bound to that item's value.
- On any change (add/remove/reorder/field-edit) it updates the in-memory `List<Dictionary<string,Variant>>` and
  fires a single **`ListChanged`** callback. No per-field global `_byId` entries (keeps the flat registry clean);
  the list owns its own item→widget map.

### 4. Binding + serialization
- **Runtime model:** `List<Dictionary<string,Variant>>` (one dict per object). `ListChanged` → a per-list **adapter**
  converts dicts → domain objects (for luminaries: `List<Luminary>`) → pushes to the system.
- **Serialization:** the list ⟷ a JSON array. Standalone (`data/luminaries.json`) AND embeddable: the user-preset
  save (`terrain_presets.json`) gains a `lists: { luminaries: [...] }` section, so "save preset" captures the bodies
  too. This array is shaped to drop into the future one-big-world-config unchanged (the bridge to the broader
  data-driven direction).

---

## Luminaries — the first consumer

- **`data/luminaries.json`** ships the default = **today's look**: `[ {kind:Sun, …primary defaults…}, {kind:Moon,
  …primary moon defaults…} ]`. The C3-tuned companion values (gold/pale-blue/rose, sizes, phases) become **schema
  defaults / extra example entries** (commented or in a sample preset), NOT hardcoded constants.
- **Loader:** `LightingComposer` gains `LoadLuminaries(jsonArray)` → builds `List<Luminary>` and makes it the source
  of truth. `ComposeExtraSuns`/`ComposeExtraMoons`/`RebuildAndBudget` iterate the loaded list instead of the
  hardcoded-constant construction; the budgeter + disc/atmosphere rendering (C3 U4/U5) are unchanged.
- **Conveniences kept:** `--suns`/`--moons` + the `extra suns`/`extra moons` sliders become thin helpers that
  **append/trim default bodies** in the list (so existing flows + presets still work). They no longer own per-body
  appearance — the list does.
- **Result:** a "Sky bodies" list editor on the Night tab — add a sun/moon, edit its color/size/phase/arc/capability
  live, reorder, remove; save the set as a named preset.

---

## Build sequence (each unit independently gated)

1. **U1 — `objectlist` type + `ObjectListControl` (the formula).** Parse the new registry type + item schemas; build
   the list-editor widget (add/remove/duplicate/reorder + per-item sub-panel via the existing widget builders);
   `ListChanged` callback. Prove with a **throwaway test schema** (2-3 fields) wired to a `GD.Print` adapter. **Gate:**
   add/remove/edit items in the lab; values flow to the callback; existing flat controls + presets untouched.
2. **U2 — luminary schema + `luminaries.json` + loader → composer.** Define the `luminary` item schema; ship the
   default array (today's sun+moon); `LightingComposer.LoadLuminaries` + make the loaded list the source of truth;
   wire the `objectlist` adapter → composer. Keep `--suns`/`--moons`/sliders as list helpers. **Gate (load-bearing):**
   default look **byte-identical** (auto-shot A/B noon/dusk/night vs the C3 baseline) AND editing a body live changes
   the sky correctly.
3. **U3 — save/load named luminary presets + round-trip.** Extend the preset machinery with the `lists` section;
   save the live bodies to a named preset, load applies them. **Gate:** edit → save → reload → identical; a couple of
   shipped example sets (e.g. "binary gold", "triple moons") load correctly.
4. **U4 — (formula payoff, optional/when needed) document + second consumer.** Write the "how to data-drive a new
   object-list" note; optionally convert one more system to prove reuse. Not required for the luminary feature.

---

## Risks & mitigations

- **Flat-registry assumptions** (the big one): `_byId`, preset save/load, randomizer all assume flat scalar ids. →
  The objectlist is a **parallel structure** that does NOT inject per-item entries into `_byId`; it owns its items.
  Existing flat controls/presets/randomizer are untouched. Only the preset *save/load* gains an additive `lists` section.
- **Default-look regression** (luminaries.json must reproduce today): → U2's load-bearing gate is a byte-identical A/B;
  ship the default array equal to the current hardcoded primary sun + moon.
- **UI churn on add/remove** (rebuilding the panel): → rebuild only the list's own sub-tree, not the whole panel.
- **Backward-compat with `--suns`/presets:** → keep them as list helpers (append/trim default bodies), tested in U2.
- **Scope creep toward the full world-config (B):** → this spec is the *formula* + *luminary* consumer only; the
  one-big-config authoring/export is explicitly out of scope (B is its own later design that builds ON this).
- **Variant/Color serialization** (Godot `Variant` ↔ JSON): → reuse the existing preset JSON read/write helpers
  (colors as `[r,g,b]` arrays, the established convention).

## Out of scope
- The full "consumer authors an entire world → exports one giant config JSON" architecture (direction **B** — this is
  its first module + proof, not the whole thing).
- Per-luminary terrain shadow/light beyond C3's current model (extra suns shadowless, extra moons visual-only).
- Converting other existing systems to object-lists (U4 is optional; the formula just makes it cheap later).

## Testing / gates
No TDD (GPU/visual + UI). Per unit: build → headless `--import` compile-check → for U2 the auto-shot A/B
(default-look byte-identical) + live edit; for U1/U3 the in-lab interaction + JSON round-trip. The load-bearing gate
is **U2: default look unchanged while every per-body setting is now editable from data.**
