using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    private OptionButton _presetPick = null!;
    private OptionButton? _moodPick;
    private LineEdit _presetName = null!;

    private string[]? _groundPalette;   // GM1: role->material name from the active ground_palette.json palette
                                        // (loaded by LabRegistryLoader.LoadGroundPalette in _Ready; read by BuildRow)

    // ---- panel ----------------------------------------------------------------

    private void BuildPanel()
    {
        var panel = new PanelContainer { Position = new Vector2(8, 8) };
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.CustomMinimumSize = new Vector2(380, 0);
        // cap height so the TabContainer scrolls instead of running off-screen
        panel.SetAnchorAndOffset(Side.Bottom, 0, 0);
        panel.OffsetTop = 8; panel.OffsetBottom = -8; panel.OffsetLeft = 8;
        AddChild(panel);

        var outer = new VBoxContainer();
        panel.AddChild(outer);
        outer.AddChild(new Label { Text = "TERRAIN LOOK LAB" });

        // top bar: Randomize + lock all/none
        var bar = new HBoxContainer();
        var rnd = new Button { Text = "🎲 Randomize" };
        rnd.Pressed += Randomize;
        bar.AddChild(rnd);
        var lockAll = new Button { Text = "Lock all" };
        lockAll.Pressed += () => SetAllLocks(true);
        bar.AddChild(lockAll);
        var lockNone = new Button { Text = "Unlock all" };
        lockNone.Pressed += () => SetAllLocks(false);
        bar.AddChild(lockNone);
        outer.AddChild(bar);

        var bar2 = new HBoxContainer();
        var flat = new Button { Text = "⬛ FLAT BASELINE (fuzz hunt)" };
        flat.Pressed += FlatBaseline;
        bar2.AddChild(flat);
        outer.AddChild(bar2);

        var tabs = new TabContainer { CustomMinimumSize = new Vector2(360, 560) };
        tabs.SizeFlagsVertical = SizeFlags.ExpandFill;
        outer.AddChild(tabs);

        foreach (string tabName in TabOrder())
        {
            var scroll = new ScrollContainer { Name = tabName, CustomMinimumSize = new Vector2(360, 0) };
            scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
            scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
            tabs.AddChild(scroll);
            var col = new VBoxContainer();
            col.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            scroll.AddChild(col);

            // Per-tab Randomize + Lock/Unlock (operate only on this tab's controls).
            string tn = tabName;   // capture for closures
            var tabBar = new HBoxContainer();
            var tabRnd = new Button { Text = "🎲 tab" };
            tabRnd.Pressed += () => RandomizeTab(tn);
            tabBar.AddChild(tabRnd);
            var tabLock = new Button { Text = "🔒 tab" };
            tabLock.Pressed += () => SetTabLocks(tn, true);
            tabBar.AddChild(tabLock);
            var tabUnlock = new Button { Text = "🔓 tab" };
            tabUnlock.Pressed += () => SetTabLocks(tn, false);
            tabBar.AddChild(tabUnlock);
            col.AddChild(tabBar);

            // The Clouds tab leads with a cloud PRESET picker (named sky looks).
            if (tabName == "Clouds") { BuildCloudPresetPicker(col); }

            // The Light tab leads with a MOOD selector (curated coordinated looks);
            // the sliders below are live fine-tuning on top of the picked mood.
            if (tabName == "Light" && _moodNames.Count > 0)
            {
                col.AddChild(new Label { Text = "MOOD (pick a vibe — tunes everything)" });
                _moodPick = new OptionButton { CustomMinimumSize = new Vector2(300, 0) };
                for (int i = 0; i < _moodNames.Count; i++) { _moodPick.AddItem(_moodNames[i], i); }
                _moodPick.ItemSelected += idx => ApplyMood((int)idx);
                col.AddChild(_moodPick);
                col.AddChild(new Label { Text = "SUN PRESET (disc look — surface/size)" });
                BuildSunPresetPicker(col);
                col.AddChild(new HSeparator());
                col.AddChild(new Label { Text = "fine-tune:" });
            }

            // The Night tab leads with a CELESTIAL preset picker (moon + stars + night looks).
            if (tabName == "Night")
            {
                col.AddChild(new Label { Text = "CELESTIAL PRESET (moon + stars + night)" });
                BuildCelestialPresetPicker(col);
                col.AddChild(new Label { Text = "FANTASY / EXOTIC SKY (cross-system)" });
                BuildFantasyPresetPicker(col);
                col.AddChild(new HSeparator());
                col.AddChild(new Label { Text = "fine-tune:" });

                // U2: the data-driven "Sky bodies" luminary list editor (Night tab). Seeded from the
                // bodies the composer loaded from luminaries.json; edits -> ApplyLuminaryDicts -> recompose.
                var lumLc = _byId.TryGetValue("luminaries", out var lc0) ? lc0 : null;
                var schemas = ItemSchemas.Load();
                if (lumLc != null && lumLc.ItemSchema != null && schemas.TryGetValue(lumLc.ItemSchema, out var lumSchema))
                {
                    var olc = new ObjectListControl();
                    col.AddChild(olc);
                    var seed = new List<Godot.Collections.Dictionary>();
                    foreach (var b in _lighting.EditableLuminaries) { seed.Add(DictFromLuminary(b)); }
                    if (seed.Count == 0)   // composer had no data (file missing) — seed one sun so the editor isn't empty
                    {
                        seed.Add(new Godot.Collections.Dictionary { { "kind", 0 }, { "color", new Color(1f, 0.95f, 0.86f) }, { "size", 0.6f }, { "phase", 1f }, { "az_offset", 0f }, { "decl_scale", 1f }, { "energy", 1.3f }, { "casts_shadow", true }, { "atmosphere", true }, { "priority", 100f } });
                    }
                    olc.Init(lumLc.Label, lumSchema, seed, lumLc.MinItems, lumLc.MaxItems,
                        (f, initial, onChanged) => LabWidgetFactory.Build(
                            f.Type, f.Label, f.Min, f.Max, f.Default, f.DefBool, f.DefColor, f.Options,
                            initial, onChanged, out _),
                        ApplyLuminaryDicts);
                    _luminaryList = olc;   // U3 preset hookup
                }
            }
            foreach (LabControl c in _controls.Where(c => c.Tab == tabName)) { BuildRow(col, c); }
        }

        BuildPresetsTab(tabs);

        // Frame-time HUD, top-right (independent of the panel).
        _fpsLabel = new Label { Text = "— fps", Position = new Vector2(0, 8) };
        _fpsLabel.SetAnchorsPreset(LayoutPreset.TopRight);
        _fpsLabel.OffsetLeft = -150; _fpsLabel.OffsetRight = -8; _fpsLabel.OffsetTop = 8;
        _fpsLabel.HorizontalAlignment = HorizontalAlignment.Right;
        AddChild(_fpsLabel);
    }

    private IEnumerable<string> TabOrder()
        => _controls.Select(c => c.Tab).Distinct();

    private void BuildRow(VBoxContainer col, LabControl c)
    {
        var row = new HBoxContainer();
        // per-control lock
        var lockBox = new CheckBox { TooltipText = "lock (skip on randomize)", CustomMinimumSize = new Vector2(28, 0) };
        lockBox.Toggled += on => c.Locked = on;
        c.LockBox = lockBox;
        row.AddChild(lockBox);
        row.AddChild(new Label { Text = c.Label, CustomMinimumSize = new Vector2(90, 0) });

        switch (c.Type)
        {
            case "slider":
            case "scenef":
            case "cloudf":
            case "cloudi":
            {
                // fine format for tiny ranges (e.g. fog density 0.0006)
                string fmt = (c.Type == "cloudi") ? "0" : ((c.Max - c.Min) < 0.05f ? "0.0000" : "0.00");
                var sl = new HSlider { MinValue = c.Min, MaxValue = c.Max, Value = c.Default,
                    Step = (c.Max - c.Min) / 400.0, CustomMinimumSize = new Vector2(160, 0) };
                sl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                var vlbl = new Label { Text = c.Default.ToString(fmt), CustomMinimumSize = new Vector2(52, 0) };
                sl.ValueChanged += v => { c.Value = (float)v; vlbl.Text = ((float)v).ToString(fmt); if (_ready) ApplyControl(c, true); };
                c.Value = c.Default; c.Widget = sl; c.ValLabel = vlbl;
                row.AddChild(sl); row.AddChild(vlbl);
                break;
            }
            case "toggle":
            case "scene":
            case "cloud":
            {
                var cb = new CheckBox { ButtonPressed = c.DefBool };
                cb.Toggled += on => { c.Value = on; if (_ready) ApplyControl(c, true); };
                c.Value = c.DefBool; c.Widget = cb;
                row.AddChild(cb);
                break;
            }
            case "scenecolor":
            {
                var cp = new ColorPickerButton { Color = c.DefColor, CustomMinimumSize = new Vector2(160, 0), EditAlpha = false };
                cp.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                cp.ColorChanged += col => { c.Value = col; if (_ready) { ApplyControl(c, true); } };
                c.Value = c.DefColor; c.Widget = cp;
                row.AddChild(cp);
                break;
            }
            case "enum":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < c.Options.Length; i++) { ob.AddItem(c.Options[i], i); }
                ob.Select((int)c.Default);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = (int)c.Default; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
            case "material":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < _materials.Count; i++) { ob.AddItem(_materials[i], i); }
                int start = LabRegistryLoader.ZoneDefaultMaterialIndex(_groundPalette, _materials, c.Zone);
                ob.Select(start);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = start; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
            case "companion":
            {
                var ob = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
                ob.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                for (int i = 0; i < _zoneNames.Length; i++) { ob.AddItem(_zoneNames[i], i); }
                ob.Select((int)c.Default);
                ob.ItemSelected += idx => { c.Value = (int)idx; if (_ready) ApplyControl(c, true); };
                c.Value = (int)c.Default; c.Widget = ob;
                row.AddChild(ob);
                break;
            }
        }
        col.AddChild(row);
    }

    private void BuildPresetsTab(TabContainer tabs)
    {
        var scroll = new ScrollContainer { Name = "Presets" };
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        tabs.AddChild(scroll);
        var vb = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.AddChild(vb);

        var prow = new HBoxContainer();
        _presetName = new LineEdit { PlaceholderText = "preset name", CustomMinimumSize = new Vector2(160, 0) };
        prow.AddChild(_presetName);
        var saveBtn = new Button { Text = "Save" };
        saveBtn.Pressed += SavePreset;
        prow.AddChild(saveBtn);
        vb.AddChild(prow);

        var lrow = new HBoxContainer();
        _presetPick = new OptionButton { CustomMinimumSize = new Vector2(200, 0) };
        lrow.AddChild(_presetPick);
        var loadBtn = new Button { Text = "Load" };
        loadBtn.Pressed += LoadSelectedPreset;
        lrow.AddChild(loadBtn);
        vb.AddChild(lrow);

        vb.AddChild(new Label { Text = "RMB/LMB+WASD fly · wheel speed" });
        LoadPresetsFromDisk();

        // --- Hero SHOTS: composition is a top 'good->great' lever. Fly to a framing
        // you like, save it; reload anytime. Seeded with a few decent vantages. ---
        vb.AddChild(new HSeparator());
        vb.AddChild(new Label { Text = "HERO SHOTS (camera framing)" });
        var srow = new HBoxContainer();
        _heroShots = new LabShots(() => GetNodeOrNull<Camera3D>("/root/TerrainLabRoot/Camera"));
        srow.AddChild(_heroShots.Picker);
        var goBtn = new Button { Text = "Go" };
        goBtn.Pressed += _heroShots.GoToSelected;
        srow.AddChild(goBtn);
        var saveShot = new Button { Text = "Save view" };
        saveShot.Pressed += _heroShots.Save;
        srow.AddChild(saveShot);
        vb.AddChild(srow);
    }

    // Hero-shot camera vantages live in their own class (only moves the camera — no render-path
    // coupling); this partial just builds the UI row and holds the reference.
    private LabShots _heroShots = null!;
}
