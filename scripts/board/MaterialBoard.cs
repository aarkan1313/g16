using Godot;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace WG16.Board;

/// Material judging loop: shows ONE material at a time, centered, rendered with
/// the FULL STACK (full PBR + anti-tile + macro variation). Press 1 = PASS,
/// 3 = FAIL. The verdict is recorded, persisted to data/material_verdicts.json,
/// and the next material is shown. At the end you have a sorted accept/reject
/// list to build the terrain from.
public partial class MaterialBoard : Node3D
{
    private const string VerdictsPath = "res://data/material_verdicts.json";

    private readonly List<string> _materials = new();
    private readonly Dictionary<string, string> _verdicts = new();   // name -> "pass"/"fail"
    private int _index;
    private Shader _shader = null!;
    private MeshInstance3D _sample = null!;
    private Label _hud = null!;

    public override void _Ready()
    {
        _shader = GD.Load<Shader>("res://shaders/material_board.gdshader");
        _hud = GetNode<Label>("HudLayer/Hud");
        _sample = GetNode<MeshInstance3D>("Sample");

        string dir = ProjectSettings.GlobalizePath("res://assets/materials");
        if (System.IO.Directory.Exists(dir))
        {
            foreach (string d in System.IO.Directory.GetDirectories(dir).OrderBy(x => x))
            {
                if (System.IO.File.Exists(System.IO.Path.Combine(d, "albedo.png")))
                {
                    _materials.Add(System.IO.Path.GetFileName(d));
                }
            }
        }

        LoadVerdicts();
        // Resume at the first un-judged material so re-runs continue where you left off.
        _index = 0;
        while (_index < _materials.Count && _verdicts.ContainsKey(_materials[_index])) { _index++; }
        ShowCurrent();
        GD.Print($"MaterialBoard: judging {_materials.Count} materials ({_verdicts.Count} already judged)");

        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--auto-shot=")) { _autoShotPath = a.Substring("--auto-shot=".Length); _autoShotT = 0.0; }
        }
    }

    private void ShowCurrent()
    {
        if (_index >= _materials.Count) { ShowDone(); return; }
        string mat = _materials[_index];
        var sm = new ShaderMaterial { Shader = _shader };
        sm.SetShaderParameter("albedo_tex", Map(mat, "albedo"));
        sm.SetShaderParameter("normal_tex", Map(mat, "normal"));
        sm.SetShaderParameter("rough_tex", Map(mat, "roughness"));
        sm.SetShaderParameter("ao_tex", Map(mat, "ao"));
        sm.SetShaderParameter("height_tex", Map(mat, "ao"));
        sm.SetShaderParameter("technique", 10);   // FULL STACK
        sm.SetShaderParameter("tile", 4.0f);
        _sample.MaterialOverride = sm;
        _hud.Text = HudText();
    }

    private void Judge(string verdict)
    {
        if (_index >= _materials.Count) { return; }
        _verdicts[_materials[_index]] = verdict;
        SaveVerdicts();
        GD.Print($"MaterialBoard: {_materials[_index]} -> {verdict.ToUpper()}");
        _index++;
        ShowCurrent();
    }

    public override void _UnhandledKeyInput(InputEvent ev)
    {
        if (ev is not InputEventKey k || !k.Pressed || k.Echo) { return; }
        switch (k.Keycode)
        {
            case Key.Key1 or Key.Kp1: Judge("pass"); break;
            case Key.Key3 or Key.Kp3: Judge("fail"); break;
            // Back up one (in case of a misclick).
            case Key.Backspace or Key.Left:
                if (_index > 0) { _index--; _verdicts.Remove(_materials[_index]); SaveVerdicts(); ShowCurrent(); }
                break;
        }
    }

    private void ShowDone()
    {
        _sample.Visible = false;
        int pass = _verdicts.Count(v => v.Value == "pass");
        int fail = _verdicts.Count(v => v.Value == "fail");
        var passed = _verdicts.Where(v => v.Value == "pass").Select(v => v.Key).OrderBy(x => x);
        _hud.Text = $"DONE — judged {_verdicts.Count}/{_materials.Count}.  PASS {pass}  ·  FAIL {fail}\n" +
                    $"verdicts saved to {VerdictsPath}\n\nACCEPTED:\n  " + string.Join("\n  ", passed);
    }

    private string HudText()
    {
        int pass = _verdicts.Count(v => v.Value == "pass");
        int fail = _verdicts.Count(v => v.Value == "fail");
        return $"JUDGING  [{_index + 1}/{_materials.Count}]   PASS {pass} · FAIL {fail}\n" +
               $"material:  {_materials[_index]}   (FULL STACK: PBR + anti-tile + macro)\n" +
               "1 = PASS    3 = FAIL    ←/Backspace = undo last    ·    RMB+WASD fly to inspect";
    }

    private Texture2D? Map(string mat, string map)
    {
        string path = $"res://assets/materials/{mat}/{map}.png";
        return ResourceLoader.Exists(path) ? GD.Load<Texture2D>(path) : null;
    }

    private void LoadVerdicts()
    {
        string abs = ProjectSettings.GlobalizePath(VerdictsPath);
        if (!System.IO.File.Exists(abs)) { return; }
        try
        {
            using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
            {
                _verdicts[p.Name] = p.Value.GetString() ?? "fail";
            }
        }
        catch (System.Exception e) { GD.Print($"MaterialBoard: verdicts load failed ({e.Message})"); }
    }

    private void SaveVerdicts()
    {
        string abs = ProjectSettings.GlobalizePath(VerdictsPath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(abs)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        System.IO.File.WriteAllText(abs, JsonSerializer.Serialize(_verdicts, opts));
    }

    private string? _autoShotPath;
    private double _autoShotT = -1.0;

    public override void _Process(double delta)
    {
        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                GD.Print($"MaterialBoard: auto-shot -> {_autoShotPath}");
                _autoShotT = -1.0;
                GetTree().Quit();
            }
        }
    }
}
