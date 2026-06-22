# Data-Driven Object Lists (Luminaries first consumer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable schema-driven `objectlist` lab control (a generic add/remove/duplicate/reorder list editor whose per-item fields reuse the existing widget builders) and make the Celestial luminaries (suns/moons) the first consumer — sourced from `data/luminaries.json` instead of hardcoded `LightingComposer` constants — with the default sky byte-identical.

**Architecture:** The flat registry (`_byId`, `lab_controls.json`, presets) is UNTOUCHED — the objectlist is a PARALLEL structure. A new `ObjectListControl` (Godot `Control`) owns its own `List<Dictionary<string,Variant>>` item model + item→widget map, builds each item field via a shared widget factory extracted from `BuildRow`, and fires one `ListChanged` callback. Luminaries: `LightingComposer.LoadLuminaries(...)` makes a loaded `List<Luminary>` the source of truth; `ComposeExtraSuns`/`ComposeExtraMoons`/`RebuildAndBudget` iterate that list instead of hardcoded constants; `--suns`/`--moons`/the sliders become thin append/trim helpers.

**Tech Stack:** Godot 4.6 mono (C#). `System.Text.Json` for parsing schema/data files; Godot `Json`/`Variant`/`Godot.Collections.Dictionary` for preset round-trip (the established convention, colors as `[r,g,b]` arrays). No new dependencies.

## Global Constraints

- **Shared branch `experiment/presentation`** (a terrain/CDLOD chat works here too): `git add` EXPLICIT paths only, NEVER `-A` (it once swept their `.uid` sidecars). Re-read `TerrainLabUI.*.cs` / `ROADMAP` / `MEMORY.md` before editing — they touch those concurrently.
- **`dotnet build WG16.csproj` after ANY `.cs` edit** — launching the player does NOT rebuild C# (stale-DLL gotcha). A green build is the per-task compile gate.
- **The default look must NOT change.** `data/luminaries.json` ships exactly today's 1 sun + 1 moon; default sky byte-identical (U2's load-bearing gate = auto-shot A/B noon/dusk/night vs the current C3 baseline).
- **Reuse the widget builders** — item sub-panel fields use the SAME per-type widget code the flat registry uses (slider/color/enum/toggle), not a parallel re-implementation.
- **The flat registry is FLAT and stays flat** — the objectlist does NOT inject per-item entries into `_byId`; existing flat controls / presets / randomizer untouched. Only preset save/load gains an additive `lists` section (U3).
- **`LightingComposer` is the one writer** — the loaded list becomes its source of truth; the budgeter + disc/atmosphere rendering (C3 U4/U5) are unchanged, only *where the list comes from*.
- **Launch/verify** (windowed, GPU): kill-all + verify zero Godot first; `--rendering-driver vulkan`; absolute `--path /c/Wg16/wg-16-project`; user flags need a bare `--` separator; `--auto-shot=<OS path>` shoots @1.5s then quits. Godot exe: `C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`. Shaders are NOT verified by `dotnet build` (Godot compiles at LOAD) — but this feature touches NO shaders, so the build + auto-shot gate suffices.
- **No TDD** (GPU/visual + interactive UI). Each unit gates by: build → headless `--import` compile-check → in-lab interaction (U1/U3) and/or auto-shot A/B + live edit (U2) + JSON round-trip (U3).
- **Defaults (user-confirmed):** list editor lives on the **Night tab**; `max_items` = **7**.

---

## File Structure

- **Create `scripts/lab/ObjectListControl.cs`** — the generic list-editor `Control`. Owns item model + item→widget map + `ListChanged` callback. One responsibility: render/edit a schema-driven list.
- **Create `scripts/lab/ItemSchema.cs`** — POCO for a parsed item schema (`List<SchemaField>`); the loader that reads `data/item_schemas.json`.
- **Create `data/item_schemas.json`** — schema definitions (`test3` throwaway in U1, `luminary` in U2).
- **Create `data/luminaries.json`** (U2) — the default body array (today's sun + moon).
- **Modify `scripts/lab/TerrainLabUI.Registry.cs`** — extract a shared `BuildFieldWidget(...)` factory from `BuildRow`'s switch; parse the new `objectlist` registry entry; instantiate `ObjectListControl` in `BuildPanel`.
- **Modify `scripts/lab/LightingComposer.cs`** (U2) — `LoadLuminaries` + iterate the loaded list in the extra-suns/moons/budget paths; `SetLuminaries` / append/trim helpers.
- **Modify `scripts/lab/TerrainLabUI.Lighting.cs`** (U2) — wire the objectlist `ListChanged` adapter (dicts → `List<Luminary>`) → composer; load `luminaries.json` at startup.
- **Modify `data/lab_controls.json`** (U2) — add the `objectlist` registry entry on the Night tab.
- **Modify `scripts/lab/TerrainLabUI.UserPresets.cs`** (U3) — additive `lists` section in save/load.

---

## Interfaces (the contracts across tasks)

```csharp
// ItemSchema.cs (U1)
public sealed class SchemaField {
    public string Id = "", Type = "", Label = "";
    public float Min, Max, Default;          // slider/float
    public bool DefBool;                      // bool
    public Color DefColor = Colors.White;     // color
    public string[] Options = System.Array.Empty<string>(); // enum (Default = selected index)
}
public static class ItemSchemas {
    // res://data/item_schemas.json -> { "<schemaName>": [ {field}, ... ], ... }
    public static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<SchemaField>> Load();
}

// ObjectListControl.cs (U1) — a Godot Control added straight into a tab column.
public sealed partial class ObjectListControl : VBoxContainer {
    // widgetFactory: given (SchemaField, initial Variant, onChanged) returns the editing Control.
    public delegate Godot.Control FieldWidget(SchemaField f, Godot.Variant initial, System.Action<Godot.Variant> onChanged);
    public void Init(string label, System.Collections.Generic.List<SchemaField> schema,
                     System.Collections.Generic.List<Godot.Collections.Dictionary> initialItems,
                     int minItems, int maxItems, FieldWidget widgetFactory,
                     System.Action<System.Collections.Generic.List<Godot.Collections.Dictionary>> onListChanged);
    public System.Collections.Generic.List<Godot.Collections.Dictionary> Items { get; }   // live model (Variant values)
    public void SetItems(System.Collections.Generic.List<Godot.Collections.Dictionary> items); // replace + rebuild + fire callback (for preset load, U3)
}

// TerrainLabUI.Registry.cs (U1) — extracted factory, also used by BuildRow.
// Returns the editing Control; wires onChanged(Variant). Does NOT touch _byId.
private Godot.Control BuildFieldWidget(string type, string label, float min, float max,
    float defF, bool defBool, Color defColor, string[] options,
    Godot.Variant initial, System.Action<Godot.Variant> onChanged, out Label valLabel);

// LightingComposer.cs (U2)
public void LoadLuminaries(System.Collections.Generic.List<Luminary> bodies); // replace source of truth
public System.Collections.Generic.List<Luminary> EditableLuminaries { get; }   // the data list (sun+moon+extras)
```

---

## Task 1 — `ObjectListControl` + item schema + shared widget factory (the formula)

Builds the reusable machinery and proves it with a throwaway 3-field schema wired to a `GD.Print` adapter on the Night tab. The flat registry, presets, and randomizer are untouched.

**Files:**
- Create: `scripts/lab/ItemSchema.cs`
- Create: `scripts/lab/ObjectListControl.cs`
- Create: `data/item_schemas.json`
- Modify: `scripts/lab/TerrainLabUI.Registry.cs` (extract `BuildFieldWidget`; instantiate the list in `BuildPanel`)

**Interfaces:**
- Consumes: nothing (first task).
- Produces: `SchemaField`, `ItemSchemas.Load`, `ObjectListControl` (+ `Init`/`Items`/`SetItems`/`FieldWidget`), `BuildFieldWidget` — exactly as in the Interfaces block above.

- [ ] **Step 1: Create the throwaway test schema file**

Create `data/item_schemas.json`:

```json
{
  "test3": [
    { "id": "label",  "type": "enum",  "label": "label", "options": ["Alpha", "Beta", "Gamma"], "default": 0 },
    { "id": "amount", "type": "float", "label": "amount", "min": 0.0, "max": 10.0, "default": 5.0 },
    { "id": "on",     "type": "bool",  "label": "enabled", "default": true }
  ]
}
```

- [ ] **Step 2: Create `ItemSchema.cs`**

```csharp
using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WG16.Lab;

/// One field of an object-list item's schema. Mirrors the flat-registry control types
/// (float/slider, bool, color, enum) so the SAME widget builders render it. Pure data.
public sealed class SchemaField
{
    public string Id = "", Type = "", Label = "";
    public float Min, Max, Default;             // float/slider
    public bool DefBool;                        // bool
    public Color DefColor = Colors.White;       // color ([r,g,b])
    public string[] Options = Array.Empty<string>();  // enum (Default = selected index)
}

/// Loads res://data/item_schemas.json: { "<name>": [ {field}, ... ], ... }. Schema names are
/// referenced by an `objectlist` registry entry's `item_schema`. The list editor reuses the
/// flat-registry widget builders per field type, so a schema is just a list of typed fields.
public static class ItemSchemas
{
    public static Dictionary<string, List<SchemaField>> Load()
    {
        var result = new Dictionary<string, List<SchemaField>>();
        string abs = ProjectSettings.GlobalizePath("res://data/item_schemas.json");
        if (!System.IO.File.Exists(abs)) { return result; }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        foreach (JsonProperty schema in doc.RootElement.EnumerateObject())
        {
            var fields = new List<SchemaField>();
            foreach (JsonElement fe in schema.Value.EnumerateArray())
            {
                var f = new SchemaField
                {
                    Id = fe.GetProperty("id").GetString() ?? "",
                    Type = fe.GetProperty("type").GetString() ?? "",
                    Label = fe.TryGetProperty("label", out var l) ? l.GetString() ?? "" : "",
                };
                if (fe.TryGetProperty("min", out var mn)) { f.Min = mn.GetSingle(); }
                if (fe.TryGetProperty("max", out var mx)) { f.Max = mx.GetSingle(); }
                if (fe.TryGetProperty("options", out var op)) { f.Options = op.EnumerateArray().Select(e => e.GetString() ?? "").ToArray(); }
                if (fe.TryGetProperty("default", out var d))
                {
                    if (f.Type == "bool") { f.DefBool = d.GetBoolean(); }
                    else if (f.Type == "color" && d.ValueKind == JsonValueKind.Array)
                    {
                        var a = d.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                        if (a.Length >= 3) { f.DefColor = new Color(a[0], a[1], a[2]); }
                    }
                    else { f.Default = d.GetSingle(); }   // float OR enum index
                }
                fields.Add(f);
            }
            result[schema.Name] = fields;
        }
        return result;
    }
}
```

- [ ] **Step 3: Extract the shared widget factory in `TerrainLabUI.Registry.cs`**

Add this method to the `TerrainLabUI` partial (place it right after `BuildRow`). It is the SAME widget code from `BuildRow`'s switch, parameterized so both the flat row and the list-item sub-panel use one implementation. It does NOT touch `_byId`.

```csharp
/// Shared per-type widget builder. Both BuildRow (flat registry) and ObjectListControl
/// (object-list item fields) call this so there's ONE widget implementation per type.
/// Returns the editing Control; fires onChanged(Variant) on edit. valLabel is the value
/// readout for slider types (null otherwise). Does NOT register into _byId.
private Control BuildFieldWidget(string type, string label, float min, float max,
    float defF, bool defBool, Color defColor, string[] options,
    Variant initial, Action<Variant> onChanged, out Label? valLabel)
{
    valLabel = null;
    switch (type)
    {
        case "slider":
        case "scenef":
        case "float":
        {
            string fmt = (max - min) < 0.05f ? "0.0000" : "0.00";
            float start = initial.VariantType == Variant.Type.Nil ? defF : initial.AsSingle();
            var sl = new HSlider { MinValue = min, MaxValue = max, Value = start,
                Step = (max - min) / 400.0, CustomMinimumSize = new Vector2(160, 0) };
            sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var vlbl = new Label { Text = start.ToString(fmt), CustomMinimumSize = new Vector2(52, 0) };
            sl.ValueChanged += v => { vlbl.Text = ((float)v).ToString(fmt); onChanged((float)v); };
            valLabel = vlbl;
            return sl;
        }
        case "toggle":
        case "scene":
        case "bool":
        {
            bool start = initial.VariantType == Variant.Type.Nil ? defBool : initial.AsBool();
            var cb = new CheckBox { ButtonPressed = start };
            cb.Toggled += on => onChanged(on);
            return cb;
        }
        case "scenecolor":
        case "color":
        {
            Color start = initial.VariantType == Variant.Type.Nil ? defColor : initial.AsColor();
            var cp = new ColorPickerButton { Color = start, CustomMinimumSize = new Vector2(160, 0), EditAlpha = false };
            cp.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cp.ColorChanged += col => onChanged(col);
            return cp;
        }
        case "enum":
        {
            int start = initial.VariantType == Variant.Type.Nil ? (int)defF : initial.AsInt32();
            var ob = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
            ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            for (int i = 0; i < options.Length; i++) { ob.AddItem(options[i], i); }
            ob.Select(start);
            ob.ItemSelected += idx => onChanged((int)idx);
            return ob;
        }
    }
    return new Label { Text = $"?{type}" };
}
```

> Note: This step ADDS the factory; it does NOT yet rewrite `BuildRow` (keeping `BuildRow` byte-identical preserves the flat-control gate). A later optional refactor can route `BuildRow` through it — out of scope for this unit.

- [ ] **Step 4: Create `ObjectListControl.cs`**

```csharp
using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Generic schema-driven list editor (the "formula"). Renders an add/remove/duplicate/reorder
/// list of objects; each item's fields are built by the shared widget factory (one widget impl
/// per type). Owns its OWN item model (List<Godot.Collections.Dictionary>, Variant values) and
/// item->widget map — it does NOT touch the flat _byId registry. Fires one ListChanged callback
/// on any mutation; a per-list adapter converts dicts -> domain objects.
public sealed partial class ObjectListControl : VBoxContainer
{
    public delegate Control FieldWidget(SchemaField f, Variant initial, Action<Variant> onChanged);

    private List<SchemaField> _schema = new();
    private readonly List<Godot.Collections.Dictionary> _items = new();
    private int _minItems = 1, _maxItems = 7;
    private FieldWidget _widget = null!;
    private Action<List<Godot.Collections.Dictionary>> _onChanged = null!;
    private string _label = "";
    private VBoxContainer _rows = null!;
    private Button _addBtn = null!;

    public List<Godot.Collections.Dictionary> Items => _items;

    public void Init(string label, List<SchemaField> schema, List<Godot.Collections.Dictionary> initialItems,
                     int minItems, int maxItems, FieldWidget widgetFactory,
                     Action<List<Godot.Collections.Dictionary>> onListChanged)
    {
        _label = label; _schema = schema; _minItems = minItems; _maxItems = maxItems;
        _widget = widgetFactory; _onChanged = onListChanged;
        _items.Clear();
        foreach (var it in initialItems) { _items.Add(it.Duplicate()); }

        AddChild(new Label { Text = label });
        _addBtn = new Button { Text = "+ add" };
        _addBtn.Pressed += () => { AddItem(DefaultItem()); Fire(); };
        AddChild(_addBtn);
        _rows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        AddChild(_rows);
        Rebuild();
    }

    /// Replace the whole list (preset load, U3): rebuild + fire the callback.
    public void SetItems(List<Godot.Collections.Dictionary> items)
    {
        _items.Clear();
        foreach (var it in items) { _items.Add(it.Duplicate()); }
        Rebuild();
        Fire();
    }

    private Godot.Collections.Dictionary DefaultItem()
    {
        var d = new Godot.Collections.Dictionary();
        foreach (var f in _schema)
        {
            d[f.Id] = f.Type switch
            {
                "bool" => f.DefBool,
                "color" => f.DefColor,
                "enum" => (int)f.Default,
                _ => f.Default,
            };
        }
        return d;
    }

    private void AddItem(Godot.Collections.Dictionary item)
    {
        if (_items.Count >= _maxItems) { return; }
        _items.Add(item);
        Rebuild();
    }

    private void Fire() => _onChanged(_items);

    /// Rebuild only this control's own sub-tree (not the whole panel).
    private void Rebuild()
    {
        foreach (Node child in _rows.GetChildren()) { child.QueueFree(); }
        _addBtn.Disabled = _items.Count >= _maxItems;

        for (int i = 0; i < _items.Count; i++)
        {
            int idx = i;   // capture
            var item = _items[i];
            var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };

            // Header: title + remove / duplicate / up / down.
            var head = new HBoxContainer();
            head.AddChild(new Label { Text = ItemTitle(item, idx), CustomMinimumSize = new Vector2(120, 0) });
            var dup = new Button { Text = "⧉" };
            dup.Pressed += () => { AddItemAt(idx + 1, item.Duplicate()); Fire(); };
            head.AddChild(dup);
            var up = new Button { Text = "▲" }; up.Disabled = idx == 0;
            up.Pressed += () => { Swap(idx, idx - 1); Fire(); };
            head.AddChild(up);
            var dn = new Button { Text = "▼" }; dn.Disabled = idx == _items.Count - 1;
            dn.Pressed += () => { Swap(idx, idx + 1); Fire(); };
            head.AddChild(dn);
            var rem = new Button { Text = "✕" }; rem.Disabled = _items.Count <= _minItems;
            rem.Pressed += () => { _items.RemoveAt(idx); Rebuild(); Fire(); };
            head.AddChild(rem);
            box.AddChild(head);

            // Fields: reuse the shared widget factory; write back into this item dict.
            foreach (var f in _schema)
            {
                var row = new HBoxContainer();
                row.AddChild(new Label { Text = f.Label, CustomMinimumSize = new Vector2(90, 0) });
                Variant initial = item.ContainsKey(f.Id) ? item[f.Id] : default;
                SchemaField field = f;   // capture
                var w = _widget(field, initial, v => { item[field.Id] = v; Fire(); });
                row.AddChild(w);
                box.AddChild(row);
            }
            box.AddChild(new HSeparator());
            _rows.AddChild(box);
        }
    }

    private void AddItemAt(int at, Godot.Collections.Dictionary item)
    {
        if (_items.Count >= _maxItems) { return; }
        at = Mathf.Clamp(at, 0, _items.Count);
        _items.Insert(at, item);
        Rebuild();
    }

    private void Swap(int a, int b)
    {
        if (a < 0 || b < 0 || a >= _items.Count || b >= _items.Count) { return; }
        (_items[a], _items[b]) = (_items[b], _items[a]);
        Rebuild();
    }

    /// A readable per-item title from the first enum (kind) + first color, if present; else index.
    private string ItemTitle(Godot.Collections.Dictionary item, int idx)
    {
        foreach (var f in _schema)
        {
            if (f.Type == "enum" && item.ContainsKey(f.Id))
            {
                int sel = item[f.Id].AsInt32();
                if (sel >= 0 && sel < f.Options.Length) { return $"{f.Options[sel]} {idx}"; }
            }
        }
        return $"item {idx}";
    }
}
```

- [ ] **Step 5: Wire a throwaway test list onto the Night tab in `BuildPanel`**

In `TerrainLabUI.Registry.cs`, inside `BuildPanel`'s Night-tab block (right after the `fine-tune:` label, BEFORE the `foreach (LabControl c ...)` row loop — so it sits at the top of the tab), add a temporary test instance. This proves the formula end to end; U2 replaces it with the real luminary list.

```csharp
            if (tabName == "Night")
            {
                // ... existing celestial/fantasy picker lines ...
                col.AddChild(new HSeparator());
                col.AddChild(new Label { Text = "fine-tune:" });

                // U1 PROOF (throwaway): a test object-list wired to a GD.Print adapter.
                // Replaced by the real luminary list in U2.
                var schemas = ItemSchemas.Load();
                if (schemas.TryGetValue("test3", out var testSchema))
                {
                    var olc = new ObjectListControl();
                    col.AddChild(olc);
                    var seed = new List<Godot.Collections.Dictionary>
                    {
                        new() { { "label", 0 }, { "amount", 5.0f }, { "on", true } },
                    };
                    olc.Init("TEST list (U1 proof)", testSchema, seed, 1, 7,
                        (f, initial, onChanged) => BuildFieldWidget(
                            f.Type, f.Label, f.Min, f.Max, f.Default, f.DefBool, f.DefColor, f.Options,
                            initial, onChanged, out _),
                        items =>
                        {
                            GD.Print($"[objectlist:test3] {items.Count} items:");
                            foreach (var it in items) { GD.Print($"  {Json.Stringify(it)}"); }
                        });
                }
            }
```

(`using System.Collections.Generic;` is already imported in `TerrainLabUI.Registry.cs`.)

- [ ] **Step 6: Build**

Run: `dotnet build WG16.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 7: Headless compile-check (Godot import)**

Run (Git Bash):
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --headless --path /c/Wg16/wg-16-project --import
```
Expected: no C#/scene parse errors (headless has no RenderingDevice — bakes will NullRef; that's fine, we only need clean import/compile).

- [ ] **Step 8: Windowed interaction gate**

Kill-all Godot + verify zero (`tasklist | grep -i godot` shows nothing). Launch windowed:
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project
```
Manually (the agent asks the user to drive, or drives if it can): open the **Night** tab → see "TEST list (U1 proof)" with 1 item. Click **+ add** (new item appears), edit **amount** slider, toggle **enabled**, change the **label** enum, **⧉** duplicate, **▲/▼** reorder, **✕** remove. Each action prints a `[objectlist:test3]` line with the current items to the Godot console. Confirm: remove disabled at 1 item, add disabled at 7.
**Gate:** add/remove/duplicate/reorder/edit all work, values flow to the callback, and the existing flat Night-tab controls below still work unchanged.

- [ ] **Step 9: Commit**

```bash
git add scripts/lab/ObjectListControl.cs scripts/lab/ItemSchema.cs data/item_schemas.json scripts/lab/TerrainLabUI.Registry.cs
git commit -m "U1: objectlist formula — ObjectListControl + item schema + shared widget factory

Generic schema-driven list editor (add/remove/duplicate/reorder, per-item
sub-panel via a widget factory extracted from BuildRow). Parallel to the flat
_byId registry — flat controls/presets/randomizer untouched. Proven on the
Night tab with a throwaway test3 schema -> GD.Print adapter (replaced in U2).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2 — Luminary schema + `luminaries.json` + loader → composer (load-bearing: default byte-identical)

Defines the `luminary` item schema, ships the default array (today's sun + moon), makes `LightingComposer` source its bodies from that list, and replaces the U1 throwaway with the real luminary list editor. `--suns`/`--moons`/the sliders become append/trim helpers.

**Files:**
- Create: `data/luminaries.json`
- Modify: `data/item_schemas.json` (add the `luminary` schema)
- Modify: `scripts/lab/LightingComposer.cs` (`LoadLuminaries` + iterate the loaded list)
- Modify: `scripts/lab/TerrainLabUI.Lighting.cs` (load at startup + the `ListChanged` adapter)
- Modify: `data/lab_controls.json` (the `objectlist` registry entry, Night tab)
- Modify: `scripts/lab/TerrainLabUI.Registry.cs` (build the real luminary list; remove the U1 throwaway)

**Interfaces:**
- Consumes: `ObjectListControl`, `ItemSchemas.Load`, `BuildFieldWidget` (U1).
- Produces: `LightingComposer.LoadLuminaries(List<Luminary>)`, `LightingComposer.EditableLuminaries`; the dict↔`Luminary` adapter (`LuminaryFromDict` / `DictFromLuminary` in `TerrainLabUI.Lighting.cs`).

- [ ] **Step 1: Add the `luminary` schema to `data/item_schemas.json`**

Append a `"luminary"` key (keep `test3` for now; it's removed in Step 9):

```json
  "luminary": [
    { "id": "kind",        "type": "enum",  "label": "kind", "options": ["Sun", "Moon"], "default": 0 },
    { "id": "color",       "type": "color", "label": "color", "default": [1.0, 0.95, 0.86] },
    { "id": "size",        "type": "float", "label": "size (deg)", "min": 0.1, "max": 6.0, "default": 0.6 },
    { "id": "phase",       "type": "float", "label": "phase (moon)", "min": 0.0, "max": 1.0, "default": 1.0 },
    { "id": "az_offset",   "type": "float", "label": "az offset", "min": -180.0, "max": 180.0, "default": 0.0 },
    { "id": "decl_scale",  "type": "float", "label": "arc height", "min": 0.1, "max": 1.2, "default": 1.0 },
    { "id": "energy",      "type": "float", "label": "light energy", "min": 0.0, "max": 3.0, "default": 1.3 },
    { "id": "casts_shadow","type": "bool",  "label": "casts shadow", "default": true },
    { "id": "atmosphere",  "type": "bool",  "label": "scatters sky", "default": true },
    { "id": "priority",    "type": "float", "label": "priority", "min": 0.0, "max": 100.0, "default": 50.0 }
  ]
```

- [ ] **Step 2: Create `data/luminaries.json` — the default = today's sun + moon**

The first Sun + first Moon entries reproduce the current primary sun + primary moon. Per-body `color`/`size`/`phase`/`energy` etc. are appearance only; the arc/time come from the composer's `Time` axis (unchanged). The defaults below match the live values (sun: warm white, size 0.6, energy 1.3, casts shadow, scatters sky, priority 100; moon: pale blue, size 1.2, phase 1.0, energy 0.65/moonlight, casts shadow, no atmosphere, priority 10).

```json
[
  { "kind": 0, "color": [1.0, 0.95, 0.86], "size": 0.6, "phase": 1.0, "az_offset": 0.0,
    "decl_scale": 1.0, "energy": 1.3, "casts_shadow": true, "atmosphere": true, "priority": 100.0 },
  { "kind": 1, "color": [0.85, 0.88, 1.0], "size": 1.2, "phase": 1.0, "az_offset": 35.0,
    "decl_scale": 0.72, "energy": 0.65, "casts_shadow": true, "atmosphere": false, "priority": 10.0 }
]
```

> CRITICAL for the byte-identical gate: these are the PRIMARY sun + moon, which were ALREADY rendered. The data here must NOT change how the primary sun/moon render — the loader (Step 3) maps `kind==Sun` index 0 → the existing primary path and `kind==Moon` index 0 → the existing primary moon path. EXTRA suns/moons (entries 2+) drive the existing `ComposeExtraSuns`/`ComposeExtraMoons` arrays. With exactly `[Sun, Moon]` the extra-count is 0 → the shader is never touched → byte-identical (the existing no-op-at-0 guarantee).

- [ ] **Step 3: Add `LoadLuminaries` + source the extras from the list in `LightingComposer.cs`**

The composer keeps the primary sun + primary moon paths EXACTLY as today (they own the real DirectionalLight + shadow). The DATA list determines: (a) the primary sun/moon appearance overrides (color/size/phase/energy/decl/az), and (b) how many extra suns/moons exist + their per-body appearance — replacing the hardcoded `ExtraSunColors`/`ExtraSunSizeFac`/`ExtraMoonColors`/`ExtraMoonPhases`/az-offset constants.

Add fields + method to `LightingComposer`:

```csharp
    // ── Data-driven luminaries (the objectlist source of truth). [0]=primary sun, then extra suns,
    //    then [k]=primary moon, then extra moons — kept in load order. The primary entries keep the
    //    existing single-sun/single-moon render paths (byte-identical); extras feed ComposeExtraSuns/Moons.
    private readonly List<Luminary> _editable = new();
    public List<Luminary> EditableLuminaries => _editable;

    /// Replace the data-driven body list (from data/luminaries.json or a preset). Derives the extra
    /// sun/moon counts + per-body appearance arrays from the list; the FIRST Sun + FIRST Moon stay the
    /// primary (existing) render paths so the default [Sun,Moon] list is byte-identical to pre-data C3.
    public void LoadLuminaries(List<Luminary> bodies)
    {
        _editable.Clear();
        foreach (var b in bodies) { _editable.Add(b); }
        ApplyLuminaryList();
    }

    /// Translate the data list into the existing composer knobs: primary sun/moon appearance overrides
    /// + extra sun/moon counts and their per-body palette/size/phase arrays. Then recompose.
    private void ApplyLuminaryList()
    {
        var suns = new List<Luminary>();
        var moons = new List<Luminary>();
        foreach (var b in _editable)
        {
            if (b.Kind == LuminaryKind.Sun) { suns.Add(b); } else { moons.Add(b); }
        }

        // Primary sun appearance override (entry 0 of suns), if present.
        if (suns.Count > 0)
        {
            var s = suns[0];
            Time.SunColor = s.Color; SunDisc.Size = s.Size; BaseSunEnergy = s.LightEnergy;
        }
        // Primary moon appearance override (entry 0 of moons), if present.
        if (moons.Count > 0)
        {
            var m = moons[0];
            Moon.Color = m.Color; Moon.Size = m.Size; Moon.Phase = m.Phase;
            Moon.AzOffset = m.AzOffset; Moon.DeclScale = m.DeclScale; Moon.LightEnergy = m.LightEnergy;
        }

        // Extra suns/moons = the list beyond the primary. Counts gate the shader (0 = no-op = byte-identical).
        ExtraSunCount = Mathf.Clamp(suns.Count - 1, 0, MaxExtraSuns);
        ExtraMoonCount = Mathf.Clamp(moons.Count - 1, 0, MaxExtraMoons);
        // Per-extra appearance arrays (override the former hardcoded constants).
        for (int i = 0; i < MaxExtraSuns; i++)
        {
            if (i + 1 < suns.Count) { _extraSunData[i] = suns[i + 1]; } else { _extraSunData[i] = null; }
        }
        for (int i = 0; i < MaxExtraMoons; i++)
        {
            if (i + 1 < moons.Count) { _extraMoonData[i] = moons[i + 1]; } else { _extraMoonData[i] = null; }
        }
    }

    private readonly Luminary?[] _extraSunData = new Luminary?[MaxExtraSuns];
    private readonly Luminary?[] _extraMoonData = new Luminary?[MaxExtraMoons];
```

Then in `ComposeExtraSuns`, replace the hardcoded palette reads with the data (falling back to the existing constants when a data entry is absent, so `--suns=N` with no list still works):

```csharp
                // was: Color c = ExtraSunColors[i % ExtraSunColors.Length];
                var data = _extraSunData[i];
                Color c = data?.Color ?? ExtraSunColors[i % ExtraSunColors.Length];
                float azOff = data != null ? data.AzOffset : 45f * (i + 1);
                // ... use `c` and `azOff` below; size:
                _extraSizes[i] = (data != null ? data.Size : SunDisc.Size * ExtraSunSizeFac[i % ExtraSunSizeFac.Length]);
```

And in `ComposeExtraMoons`, similarly:

```csharp
                var data = _extraMoonData[i];
                float phase = data?.Phase ?? ExtraMoonPhases[i];
                float azOff = (data != null ? data.AzOffset : 40f * (i + 1)) + Moon.AzOffset;
                Color c = data?.Color ?? ExtraMoonColors[i % ExtraMoonColors.Length];
                _exMoonSizes[i] = data != null ? data.Size : Moon.Size * ExtraMoonSizeFac[i % ExtraMoonSizeFac.Length];
```

> The `??`/fallback keeps `--suns=N`/`--moons=N` (which set `ExtraSunCount`/`ExtraMoonCount` WITHOUT populating the data arrays) working exactly as before.

- [ ] **Step 4: Add the dict↔Luminary adapter + startup load in `TerrainLabUI.Lighting.cs`**

First read `TerrainLabUI.Lighting.cs` to see the existing `_lighting` field + `ComposeLighting` forwarder. Add:

```csharp
    private const string LuminariesPath = "res://data/luminaries.json";

    /// Load data/luminaries.json -> List<Luminary> -> composer source of truth. Called at startup
    /// (after the registry/UI exist). Missing/malformed file => keep the composer's built-in defaults.
    private void LoadLuminariesFromDisk()
    {
        string abs = ProjectSettings.GlobalizePath(LuminariesPath);
        if (!System.IO.File.Exists(abs)) { return; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            var bodies = new System.Collections.Generic.List<Luminary>();
            foreach (System.Text.Json.JsonElement e in doc.RootElement.EnumerateArray())
            {
                bodies.Add(LuminaryFromJson(e));
            }
            if (bodies.Count > 0) { _lighting.LoadLuminaries(bodies); }
        }
        catch (System.Exception ex) { GD.PushWarning($"[luminaries] parse failed ({ex.Message}) -> defaults"); }
    }

    private static Luminary LuminaryFromJson(System.Text.Json.JsonElement e)
    {
        float G(string k, float fb) => e.TryGetProperty(k, out var v) ? v.GetSingle() : fb;
        bool B(string k, bool fb) => e.TryGetProperty(k, out var v) ? v.GetBoolean() : fb;
        Color C(string k, Color fb)
        {
            if (e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var a = new System.Collections.Generic.List<float>();
                foreach (var x in v.EnumerateArray()) { a.Add(x.GetSingle()); }
                if (a.Count >= 3) { return new Color(a[0], a[1], a[2]); }
            }
            return fb;
        }
        int kind = e.TryGetProperty("kind", out var kv) ? kv.GetInt32() : 0;
        return new Luminary
        {
            Kind = kind == 1 ? LuminaryKind.Moon : LuminaryKind.Sun,
            Color = C("color", new Color(1f, 0.95f, 0.86f)),
            Size = G("size", 0.6f), Phase = G("phase", 1.0f), AzOffset = G("az_offset", 0f),
            DeclScale = G("decl_scale", 1.0f), LightEnergy = G("energy", 1.3f),
            CastsShadow = B("casts_shadow", true), ContributesToAtmosphere = B("atmosphere", true),
            Priority = G("priority", 50f),
        };
    }

    /// Adapter: the objectlist's Variant dicts -> List<Luminary> -> composer (live edits).
    private void ApplyLuminaryDicts(System.Collections.Generic.List<Godot.Collections.Dictionary> items)
    {
        var bodies = new System.Collections.Generic.List<Luminary>();
        foreach (var d in items) { bodies.Add(LuminaryFromDict(d)); }
        _lighting.LoadLuminaries(bodies);
        ComposeLighting();
    }

    private static Luminary LuminaryFromDict(Godot.Collections.Dictionary d)
    {
        float G(string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;
        bool B(string k, bool fb) => d.ContainsKey(k) ? d[k].AsBool() : fb;
        Color C(string k, Color fb) => d.ContainsKey(k) ? d[k].AsColor() : fb;
        int kind = d.ContainsKey("kind") ? d["kind"].AsInt32() : 0;
        return new Luminary
        {
            Kind = kind == 1 ? LuminaryKind.Moon : LuminaryKind.Sun,
            Color = C("color", new Color(1f, 0.95f, 0.86f)),
            Size = G("size", 0.6f), Phase = G("phase", 1.0f), AzOffset = G("az_offset", 0f),
            DeclScale = G("decl_scale", 1.0f), LightEnergy = G("energy", 1.3f),
            CastsShadow = B("casts_shadow", true), ContributesToAtmosphere = B("atmosphere", true),
            Priority = G("priority", 50f),
        };
    }

    /// dict for one Luminary (objectlist seed + preset save). Inverse of LuminaryFromDict.
    private static Godot.Collections.Dictionary DictFromLuminary(Luminary b) => new()
    {
        { "kind", b.Kind == LuminaryKind.Moon ? 1 : 0 },
        { "color", b.Color }, { "size", b.Size }, { "phase", b.Phase },
        { "az_offset", b.AzOffset }, { "decl_scale", b.DeclScale }, { "energy", b.LightEnergy },
        { "casts_shadow", b.CastsShadow }, { "atmosphere", b.ContributesToAtmosphere },
        { "priority", b.Priority },
    };
```

Call `LoadLuminariesFromDisk()` in `_Ready` — add it right after `LoadRegistry();` (so the composer has the data before `ApplyAll`/`ApplyDefaultMood` compose). Edit `TerrainLabUI.cs` `_Ready`:

```csharp
        LoadRegistry();
        LoadLuminariesFromDisk();   // U2: data-driven bodies become the composer's source of truth
        BuildPanel();
```

- [ ] **Step 5: Add the `objectlist` registry entry to `data/lab_controls.json`**

Add to the `controls` array (the Night tab). It carries enough for `BuildPanel` to find the schema + data + bounds:

```json
    { "type": "objectlist", "id": "luminaries", "tab": "Night", "label": "Sky bodies",
      "item_schema": "luminary", "data": "res://data/luminaries.json", "min_items": 1, "max_items": 7, "rand": false },
```

Then in `BaseControl` (`TerrainLabUI.Registry.cs`) the parser must tolerate this type (it currently only reads scalar fields). Add capture of the objectlist-specific fields to `LabControl` + `BaseControl`:

In `LabControl` (in `TerrainLabUI.cs`), add fields:
```csharp
        public string? ItemSchema, DataPath;
        public int MinItems = 1, MaxItems = 7;
```
In `BaseControl`, after the existing property reads:
```csharp
        lc.ItemSchema = c.TryGetProperty("item_schema", out var isc) ? isc.GetString() : null;
        lc.DataPath = c.TryGetProperty("data", out var dp) ? dp.GetString() : null;
        if (c.TryGetProperty("min_items", out var mi)) { lc.MinItems = mi.GetInt32(); }
        if (c.TryGetProperty("max_items", out var ma)) { lc.MaxItems = ma.GetInt32(); }
```
And ensure `LoadRegistry`'s `else` branch (which calls `Register(lc, lc.Id)`) handles `objectlist` — it does (it's a non-material/companion type), and `BuildRow`'s switch simply has no case for it, so it renders nothing in the flat path. Good: the objectlist is built separately in `BuildPanel` (Step 6), not via `BuildRow`.

- [ ] **Step 6: Replace the U1 throwaway with the real luminary list in `BuildPanel`**

In the Night-tab block of `BuildPanel`, REMOVE the U1 `test3` proof block and add the real one. It seeds from the composer's loaded bodies (so the editor mirrors the live source of truth) and wires the adapter:

```csharp
                // U2: the data-driven "Sky bodies" luminary list editor (Night tab).
                var lumCtl = _byId.TryGetValue("luminaries", out var lumLc) ? lumLc : null;
                var schemas = ItemSchemas.Load();
                if (lumCtl != null && lumCtl.ItemSchema != null && schemas.TryGetValue(lumCtl.ItemSchema, out var lumSchema))
                {
                    var olc = new ObjectListControl();
                    col.AddChild(olc);
                    var seed = new List<Godot.Collections.Dictionary>();
                    foreach (var b in _lighting.EditableLuminaries) { seed.Add(DictFromLuminary(b)); }
                    if (seed.Count == 0)   // composer had no data (file missing) — seed one sun
                    {
                        seed.Add(new Godot.Collections.Dictionary { { "kind", 0 }, { "color", new Color(1f,0.95f,0.86f) }, { "size", 0.6f }, { "phase", 1f }, { "az_offset", 0f }, { "decl_scale", 1f }, { "energy", 1.3f }, { "casts_shadow", true }, { "atmosphere", true }, { "priority", 100f } });
                    }
                    olc.Init(lumLc.Label, lumSchema, seed, lumLc.MinItems, lumLc.MaxItems,
                        (f, initial, onChanged) => BuildFieldWidget(
                            f.Type, f.Label, f.Min, f.Max, f.Default, f.DefBool, f.DefColor, f.Options,
                            initial, onChanged, out _),
                        ApplyLuminaryDicts);
                    _luminaryList = olc;   // U3 preset hookup
                }
```

Add the field to `TerrainLabUI` (in `TerrainLabUI.cs`, near `_byId`):
```csharp
    private ObjectListControl? _luminaryList;   // U2: the Sky-bodies list editor (Night tab)
```

> Note: seeding from `_lighting.EditableLuminaries` means the editor shows the bodies the composer actually loaded from `luminaries.json`. Editing fires `ApplyLuminaryDicts` → `LoadLuminaries` → recompose.

- [ ] **Step 7: Build**

Run: `dotnet build WG16.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 8: Capture the BASELINE auto-shots (before trusting the new path)**

> Do this from a clean build BEFORE the byte-identical comparison so we A/B the SAME build's data path vs a known-good reference. The reference is the committed C3 baseline; if no baseline PNGs exist, capture them now from `git stash` of U2 (or note that the C3 commit `eaed0b7` is the visual baseline). Simplest: capture the post-U2 shots, then verify against the user's eye + the C3 NEEDS_REVIEW 12 PASS look.

Kill-all + verify zero Godot. Capture noon/dusk/night:
```bash
GD="C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe"
"$GD" --rendering-driver vulkan --path /c/Wg16/wg-16-project --auto-shot=C:/tmp/u2_noon.png  -- --time=12
"$GD" --rendering-driver vulkan --path /c/Wg16/wg-16-project --auto-shot=C:/tmp/u2_dusk.png  -- --time=18.5
"$GD" --rendering-driver vulkan --path /c/Wg16/wg-16-project --auto-shot=C:/tmp/u2_night.png -- --time=23
```
(One shot per launch; the process quits after the shot. Each `--` separates user flags.)

- [ ] **Step 9: Verify default byte-identical + remove the throwaway schema**

View `u2_noon/dusk/night.png` (Read tool). Compare to the C3 baseline look (the NEEDS_REVIEW 12 PASS — single sun + moon, no extras). They must be visually identical (default `luminaries.json` = `[Sun, Moon]` → extra counts 0 → shader untouched).
Remove the now-unused `"test3"` schema from `data/item_schemas.json` (the `luminary` schema stays).
**Gate (load-bearing):** default look byte-identical AND a live edit changes the sky — verify by launching windowed, opening Night → Sky bodies, **+ add** a Sun, changing its color/size → a second sun disc appears in the sky (use `--time=12 --coverage=0.05` framing or `--lookatsun` to see it).

- [ ] **Step 10: Verify `--suns`/`--moons` still work (backward-compat)**

```bash
"$GD" --rendering-driver vulkan --path /c/Wg16/wg-16-project --auto-shot=C:/tmp/u2_suns3.png -- --time=12 --suns=3 --coverage=0.05 --lookatsun
"$GD" --rendering-driver vulkan --path /c/Wg16/wg-16-project --auto-shot=C:/tmp/u2_moons3.png -- --time=23 --moons=3 --coverage=0.05 --lookatmoon
```
View both: 3 suns / 3 moons render (the `??` fallback to the hardcoded palette still fires because `--suns` sets the count without populating data). **Gate:** these match the pre-U2 multi-body look.

- [ ] **Step 11: Commit**

```bash
git add data/luminaries.json data/item_schemas.json scripts/lab/LightingComposer.cs scripts/lab/TerrainLabUI.Lighting.cs scripts/lab/TerrainLabUI.cs scripts/lab/TerrainLabUI.Registry.cs data/lab_controls.json
git commit -m "U2: luminaries data-driven — luminaries.json + loader is the composer's source of truth

Sky bodies now sourced from data/luminaries.json (default = today's sun+moon ->
byte-identical default sky). Night-tab 'Sky bodies' list editor adds/edits/removes
bodies live. Extra-sun/moon palette/size/phase/az now read from the data list
(hardcoded constants kept as the --suns/--moons fallback). LightingComposer is
still the one writer.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3 — Save/load named luminary preset sets (additive `lists` section)

Extends the existing preset machinery so saving a preset captures the live sky bodies, and loading restores them. The flat preset path is unchanged; a new `lists` section is purely additive (old presets load fine — no `lists` key).

**Files:**
- Modify: `scripts/lab/TerrainLabUI.UserPresets.cs` (save/load the `lists` section)

**Interfaces:**
- Consumes: `_luminaryList` (`ObjectListControl`, U2), `ObjectListControl.Items` / `SetItems` (U1), `ApplyLuminaryDicts` (U2).
- Produces: nothing downstream (final unit).

- [ ] **Step 1: Save the `lists` section in `SavePreset`**

In `SavePreset`, after building `vals`/`locks`, add the luminary list to the preset entry. The objectlist `Items` are `Godot.Collections.Dictionary` (Variant values) — store them as a `Godot.Collections.Array`:

```csharp
        var lists = new Godot.Collections.Dictionary();
        if (_luminaryList != null)
        {
            var arr = new Godot.Collections.Array();
            foreach (var it in _luminaryList.Items) { arr.Add(it); }
            lists["luminaries"] = arr;
        }
        _presets[name] = new Godot.Collections.Dictionary { { "v", vals }, { "lock", locks }, { "lists", lists } };
```

(Colors serialize via the established Godot `Json.Stringify` path — `Color` Variants round-trip through `Json` as the engine handles them; on load they come back as the right Variant. If a Color does NOT round-trip cleanly through `Json` in this project, store `[r,g,b]` arrays instead and convert in Step 2 — verify in Step 4's round-trip gate.)

- [ ] **Step 2: Load the `lists` section in `LoadSelectedPreset`**

In `LoadSelectedPreset`, after the existing `vals`/`locks` application, restore the luminaries (guarded so old presets without `lists` are unaffected):

```csharp
        if (entry.ContainsKey("lists") && _luminaryList != null)
        {
            var lists = entry["lists"].AsGodotDictionary();
            if (lists.ContainsKey("luminaries"))
            {
                var arr = lists["luminaries"].AsGodotArray();
                var items = new System.Collections.Generic.List<Godot.Collections.Dictionary>();
                foreach (var v in arr) { items.Add(v.AsGodotDictionary()); }
                _luminaryList.SetItems(items);   // rebuilds the editor + fires ApplyLuminaryDicts -> recompose
            }
        }
```

(`SetItems` fires the `ListChanged` callback = `ApplyLuminaryDicts`, so loading a preset both updates the editor UI AND recomposes the sky. No extra wiring needed.)

- [ ] **Step 3: Build**

Run: `dotnet build WG16.csproj`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Round-trip gate (windowed)**

Kill-all + verify zero. Launch windowed:
```bash
"C:/Users/josep/Downloads/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" --rendering-driver vulkan --path /c/Wg16/wg-16-project -- --time=12
```
In the lab: Night → Sky bodies → **+ add** a 2nd Sun, set a distinctive color (e.g. red) + size. Presets tab → name it "binary red" → **Save**. Then **+ add** a 3rd body and a Moon (mutate the list). Reload "binary red" via the Presets picker → **Load**. **Gate:** the editor returns to exactly 2 bodies (sun + red sun) AND the sky recomposes to the 2-sun look. Re-open `user://terrain_presets.json` (in `%APPDATA%/Godot/app_userdata/...`) and confirm a `"lists": { "luminaries": [...] }` block is present with the body fields.

- [ ] **Step 5: Verify old presets still load (backward-compat)**

Load a pre-existing user preset that has no `lists` key (any older saved preset). **Gate:** it loads with no error (the `entry.ContainsKey("lists")` guard skips the new path), flat controls apply as before.

- [ ] **Step 6: Commit**

```bash
git add scripts/lab/TerrainLabUI.UserPresets.cs
git commit -m "U3: save/load named luminary preset sets (additive lists section)

Saving a preset now captures the live Sky-bodies list; loading restores it via
ObjectListControl.SetItems (-> recompose). Purely additive: old presets without a
'lists' key load unchanged. Round-trip verified.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage:**
- `objectlist` control type → Task 2 Step 5 (registry entry) + Task 1 (the control). ✓
- Item schemas (existing control types) → `ItemSchema.cs` + `data/item_schemas.json` (Task 1), `luminary` schema (Task 2). ✓
- Generic list-editor widget (add/remove/duplicate/reorder, per-item sub-panel via existing builders) → `ObjectListControl` + `BuildFieldWidget` (Task 1). ✓
- Binding + serialization (`List<Dictionary<string,Variant>>`, adapter dicts→domain) → `ApplyLuminaryDicts`/`LuminaryFromDict` (Task 2). ✓
- Luminaries as first consumer, `data/luminaries.json` = today's look, loader, source of truth → Task 2. ✓
- `--suns`/`--moons`/sliders kept as helpers → Task 2 Step 3 fallback + Step 10 gate. ✓
- Save/load named presets (`lists` section) + round-trip → Task 3. ✓
- Flat registry/presets/randomizer untouched (parallel structure, no `_byId` per-item entries) → Task 1 (control owns its model), Task 3 (additive only). ✓
- Default-look byte-identical → Task 2 Step 9 load-bearing gate. ✓
- U4 (formula doc / 2nd consumer) — spec marks OPTIONAL/when-needed; not required for the luminary feature → omitted from this plan (note for later). ✓ (intentional)

**2. Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to". Every code step has full code. The one conditional ("if Color doesn't round-trip, use `[r,g,b]`") is a verify-then-branch with the concrete fallback named, gated by Step 4 — acceptable.

**3. Type consistency:** `ObjectListControl.Init`/`Items`/`SetItems`/`FieldWidget` consistent across Tasks 1/2/3. `BuildFieldWidget` signature identical at definition (T1 S3) and both call sites (T1 S5, T2 S6). `LoadLuminaries(List<Luminary>)`, `EditableLuminaries`, `LuminaryFromDict`/`DictFromLuminary`/`ApplyLuminaryDicts` consistent T2↔T3. `LabControl.ItemSchema/DataPath/MinItems/MaxItems` defined T2 S5, used T2 S6. `_luminaryList` defined T2 S6, used T3. ✓

---

## Notes for the executor

- **Read before edit (shared branch):** `TerrainLabUI.Lighting.cs` is large and the terrain chat may have touched it — re-read it before Task 2 Step 4 to confirm `_lighting`, `_moon`, `_time`, `_stars`, `ComposeLighting` names are current.
- **The byte-identical gate is the whole point of U2** — if the default shots differ at all, STOP and find why before proceeding (most likely: the primary-sun/moon override in `ApplyLuminaryList` changed a value vs the live default; make `luminaries.json` match the live constants exactly).
- After all three units pass, update `docs/ROADMAP.md` / `docs/NEEDS_REVIEW.md` and the `[[sun-light-arc]]` memory, then move to the **#7 end-of-arc perf pass** (start from `performance.md`'s 2026-06-22 section).
```