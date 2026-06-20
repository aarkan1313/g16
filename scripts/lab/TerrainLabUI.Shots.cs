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
        // Contrast palette (2026-06-19): green valley → brown soil → grey gravel → talus →
        // dark rock cliffs → green-brown tundra band → white snow. Breaks the grey-mush look.
        string[] wanted = { "m8_grass_calm", "dirt", "16_glacial_till",
                            "02_coarse_talus", "rock_dark", "m14_tundra_moss", "01_fresh_powder" };
        int idx = (zone >= 0 && zone < wanted.Length) ? _materials.IndexOf(wanted[zone]) : -1;
        if (idx < 0) { idx = Math.Min(Math.Max(zone, 0), _materials.Count - 1); }
        return Math.Max(idx, 0);
    }
}
