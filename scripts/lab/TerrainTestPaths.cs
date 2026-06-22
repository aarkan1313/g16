using Godot;
using System.Collections.Generic;
using System.Text.Json;

namespace WG16.Lab;

/// Flies the camera along scripted LOD-crossing paths (data/terrain_test_paths.json) so pops can be
/// eye-judged in motion and perf is repeatable. Reports avg/worst/spike ms + chunk-count range + the
/// <=1-level invariant sampled ALONG the path. Drive+measure only — auto pop-detection is deferred
/// (the _onFrame hook is reserved for it). S2b verification machinery; reusable beyond S2b.
public sealed partial class TerrainTestPaths : Node
{
    private sealed class PathDef
    {
        public string Name = "";
        public float Duration = 8f;
        public Vector3 From, To, LookOffset;
    }

    private Camera3D _cam = null!;
    private CdlodTerrain _cdlod = null!;
    private readonly List<PathDef> _paths = new();
    private int _active = -1;
    private double _t;
    private bool _cliQuit;
    // accumulators
    private double _accum; private int _frames; private double _worst; private int _spikes;
    private int _chunkLo, _chunkHi; private bool _invOk; private string _invMsg = "ok";

    public bool Running => _active >= 0;

    public void Setup(Camera3D cam, CdlodTerrain cdlod)
    {
        _cam = cam; _cdlod = cdlod;
        Load();
    }

    private void Load()
    {
        string abs = ProjectSettings.GlobalizePath("res://data/terrain_test_paths.json");
        if (!System.IO.File.Exists(abs)) { GD.PrintErr("TerrainTestPaths: missing data/terrain_test_paths.json"); return; }
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(abs));
        foreach (JsonElement p in doc.RootElement.GetProperty("paths").EnumerateArray())
        {
            var d = new PathDef
            {
                Name = p.GetProperty("name").GetString() ?? "?",
                Duration = p.GetProperty("duration").GetSingle(),
                From = Vec3(p, "from"), To = Vec3(p, "to"), LookOffset = Vec3(p, "look_offset"),
            };
            _paths.Add(d);
        }
        GD.Print($"TerrainTestPaths: loaded {_paths.Count} paths");
    }

    private static Vector3 Vec3(JsonElement p, string key)
    {
        JsonElement a = p.GetProperty(key);
        return new Vector3(a[0].GetSingle(), a[1].GetSingle(), a[2].GetSingle());
    }

    /// Start path by index (0-based). cliQuit: quit the tree when the path finishes (for --testpath).
    public void Start(int pathIndex, bool cliQuit = false)
    {
        if (pathIndex < 0 || pathIndex >= _paths.Count) { GD.PrintErr($"TerrainTestPaths: no path {pathIndex}"); return; }
        _active = pathIndex; _t = 0; _cliQuit = cliQuit;
        _accum = 0; _frames = 0; _worst = 0; _spikes = 0;
        _chunkLo = int.MaxValue; _chunkHi = 0; _invOk = true; _invMsg = "ok";
        GD.Print($"TerrainTestPaths: START '{_paths[pathIndex].Name}' ({_paths[pathIndex].Duration:F0}s)");
    }

    public void Tick(double delta)
    {
        if (_active < 0) { return; }
        PathDef p = _paths[_active];
        _t += delta;
        float u = Mathf.Clamp((float)(_t / p.Duration), 0f, 1f);
        Vector3 pos = p.From.Lerp(p.To, u);
        _cam.GlobalPosition = pos;
        _cam.LookAt(pos + p.LookOffset, Vector3.Up);

        // measure (skip the first 0.3 s warm-up so a one-off load frame doesn't dominate 'worst')
        if (_t > 0.3)
        {
            _accum += delta; _frames++;
            if (delta > _worst) { _worst = delta; }
            if (delta > 0.008) { _spikes++; }   // frames over the 8 ms budget
            int lc = _cdlod.LeafCountLastTick;
            if (lc < _chunkLo) { _chunkLo = lc; }
            if (lc > _chunkHi) { _chunkHi = lc; }
            if (!_cdlod.InvariantHoldsNow(out string m)) { _invOk = false; _invMsg = m; }
        }
        // _onFrame hook reserved here for future auto pop-detection (deferred).

        if (u >= 1f) { Finish(); }
    }

    private void Finish()
    {
        double avg = _accum / Mathf.Max(_frames, 1);
        GD.Print($"TESTPATH: {_paths[_active].Name}  avg={avg * 1000.0:F1}ms  worst={_worst * 1000.0:F1}ms  " +
                 $"spikes={_spikes}  chunks={_chunkLo}-{_chunkHi}  invariant={(_invOk ? "PASS" : "FAIL:" + _invMsg)}");
        bool quit = _cliQuit;
        _active = -1;
        if (quit) { GetTree().Quit(); }
    }
}
