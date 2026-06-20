using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // ---- hero shots (camera viewpoints) --------------------------------------
    private OptionButton _shotPick = null!;
    private readonly List<(string name, Vector3 pos, Vector3 rot)> _shots = new();

    private void SeedShots()
    {
        // a few decent starting vantages found while probing (low-angle, depth).
        _shots.Add(("ridge vista", new Vector3(-1800, 120, -1200), new Vector3(4, 55, 0)));
        _shots.Add(("misty dawn", new Vector3(800, 90, 2600), new Vector3(6, 180, 0)));
        _shots.Add(("high overlook", new Vector3(0, 650, 1900), new Vector3(-14, 20, 0)));
    }
    private void RefreshShotList()
    {
        _shotPick.Clear();
        for (int i = 0; i < _shots.Count; i++) { _shotPick.AddItem(_shots[i].name, i); }
    }
    private void GoToShot()
    {
        int i = _shotPick.Selected;
        if (i < 0 || i >= _shots.Count) { return; }
        var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
        cam.Position = _shots[i].pos;
        cam.RotationDegrees = _shots[i].rot;
    }
    private void SaveShot()
    {
        var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
        _shots.Add(($"shot{_shots.Count + 1}", cam.Position, cam.RotationDegrees));
        RefreshShotList();
        _shotPick.Select(_shots.Count - 1);
        GD.Print($"TerrainLab: saved view (pos {cam.Position}, rot {cam.RotationDegrees})");
    }

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
}
