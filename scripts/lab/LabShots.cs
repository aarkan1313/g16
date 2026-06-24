using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// Hero-shot camera vantages — pure dev-tooling that only MOVES the camera; it never touches the
/// render path, so it cannot affect the terrain look. Owns the saved-vantage list and its picker.
///
/// Extracted from the TerrainLabUI god-class (consolidation, audit #2): the vantage list + picker
/// were raw shared state across the Shots/Registry/Review partials. This is a plain class (not a
/// Node), so it has no scene tree — the camera is fetched lazily through a delegate supplied by the
/// owner. The narrow surface is: build the UI row from Picker, wire Go→GoToSelected, Save→Save.
public sealed class LabShots
{
    private readonly Func<Camera3D?> _getCamera;
    private readonly List<(string name, Vector3 pos, Vector3 rot)> _shots = new();

    /// The vantage dropdown. The owner adds this to its UI row.
    public OptionButton Picker { get; } = new() { CustomMinimumSize = new Vector2(150, 0) };

    public LabShots(Func<Camera3D?> getCamera)
    {
        _getCamera = getCamera;
        Seed();
        RefreshList();
    }

    private void Seed()
    {
        // a few decent starting vantages found while probing (low-angle, depth).
        _shots.Add(("ridge vista", new Vector3(-1800, 120, -1200), new Vector3(4, 55, 0)));
        _shots.Add(("misty dawn", new Vector3(800, 90, 2600), new Vector3(6, 180, 0)));
        _shots.Add(("high overlook", new Vector3(0, 650, 1900), new Vector3(-14, 20, 0)));
    }

    private void RefreshList()
    {
        Picker.Clear();
        for (int i = 0; i < _shots.Count; i++) { Picker.AddItem(_shots[i].name, i); }
    }

    /// Jump the camera to the picker's current selection (the 'Go' button).
    public void GoToSelected() => GoTo(Picker.Selected);

    /// Jump the camera to a vantage by index.
    public void GoTo(int i)
    {
        Camera3D? cam = _getCamera();
        if (cam == null || i < 0 || i >= _shots.Count) { return; }
        cam.Position = _shots[i].pos;
        cam.RotationDegrees = _shots[i].rot;
    }

    /// Save the camera's current framing as a new vantage (the 'Save view' button).
    public void Save()
    {
        Camera3D? cam = _getCamera();
        if (cam == null) { return; }
        _shots.Add(($"shot{_shots.Count + 1}", cam.Position, cam.RotationDegrees));
        RefreshList();
        Picker.Select(_shots.Count - 1);
        GD.Print($"TerrainLab: saved view (pos {cam.Position}, rot {cam.RotationDegrees})");
    }
}
