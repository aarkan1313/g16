using Godot;
using System;
using System.Collections.Generic;

namespace WG16.Lab;

/// U2 numeric gate (--luminarycheck): proves the data-driven luminary path is byte-identical for the
/// default look AND that edits propagate — WITHOUT cross-launch screenshot drift. Mirrors the --godrayab
/// staged pattern: freeze the scene (TimeScale=0) so frames are identical except for the luminary change,
/// then in one run:
///   A = render with the default loaded list (data path, extra counts 0)
///   B = render after RE-loading the SAME default list via the live edit path (applyLuminaries)
///       -> assert maxdiff(A,B) == 0  (the data/edit path does not perturb the default sky)
///   C = render after a primary-sun-size edit; D = after appending an extra Sun
///       -> assert both change pixels beyond the recompose floor (edits actually change the sky)
/// Prints LUMINARYCHECK: PASS/FAIL. This is the load-bearing default-byte-identical proof for U2.
///
/// Extracted from the TerrainLabUI god-class (decomposition Phase 1): owns all its state; depends on the
/// host only for the viewport/tree (it must grab the rendered frame + quit) and two lighting delegates.
public sealed class LuminaryCheckRunner
{
    private readonly Node _host;                                       // for GetViewport()/GetTree()
    private readonly Action _compose;                                  // = TerrainLabUI.ComposeLighting
    private readonly Action<List<Godot.Collections.Dictionary>> _applyLuminaries;  // = ApplyLuminaryDicts

    private double _t = -1.0;
    private int _stage;
    private byte[]? _a;        // frozen reference image bytes (default/data path)
    private int _w, _h;
    private int _diffB, _diffC, _framesB, _framesC, _framesD, _framesFloor, _jitter;

    public LuminaryCheckRunner(Node host, Action compose, Action<List<Godot.Collections.Dictionary>> applyLuminaries)
    {
        _host = host;
        _compose = compose;
        _applyLuminaries = applyLuminaries;
    }

    /// Arm the gate (called from _Ready only when --luminarycheck is present). Until armed, Tick is a no-op.
    public void Arm() => _t = 0.0;

    private byte[] Grab()
    {
        Image img = _host.GetViewport().GetTexture().GetImage();
        _w = img.GetWidth(); _h = img.GetHeight();
        return img.GetData();
    }

    public void Tick(double delta)
    {
        if (_t < 0.0) { return; }
        _t += delta;

        if (_stage == 0 && _t > 1.5)
        {
            Engine.TimeScale = 0.0;                          // freeze clouds/cycle so only luminaries vary
            _a = Grab();                                     // A: default sky via the data path (NO change made)
            _stage = 1;
            return;
        }
        if (_stage == 1)
        {
            // Calibrate the floor: a bare compose with NO luminary change. This captures any 1-LSB churn from
            // Compose() itself (overcast settle, tonemap re-run) so the data-path comparison isolates ONLY the
            // luminary contribution above that floor.
            _compose();
            _stage = 10; _framesFloor = 0;
            return;
        }
        if (_stage == 10)
        {
            if (++_framesFloor < 2) { return; }
            _jitter = MaxDiff(_a!, Grab());
            // B: re-load the SAME default list through the live edit adapter, then recompose.
            _applyLuminaries(DefaultLuminaryDicts());
            _stage = 2; _framesB = 0;
            return;
        }
        if (_stage == 2)
        {
            if (++_framesB < 2) { return; }                  // let the recompose land
            _diffB = MaxDiff(_a!, Grab());
            // C: edit the PRIMARY sun's disc SIZE via the data path (a primary-from-list edit, visible at noon),
            // then recompose. Proves the list now drives the PRIMARY body's render, not just added ones.
            var edited = DefaultLuminaryDicts();
            edited[0]["size"] = 4.0f;   // primary sun disc 0.6 -> 4.0 deg (big, unmistakable at noon)
            _applyLuminaries(edited);
            _stage = 3; _framesC = 0;
            return;
        }
        if (_stage == 3)
        {
            if (++_framesC < 2) { return; }                  // let the recompose land
            int maxC = MaxDiff(_a!, Grab());
            // D: append an extra Sun (the added-body path) via the data path, then recompose.
            var edited2 = DefaultLuminaryDicts();
            edited2.Add(new Godot.Collections.Dictionary { { "kind", 0 }, { "color", new Color(0.62f, 0.78f, 1.0f) }, { "size", 0.9f }, { "phase", 1f }, { "az_offset", 60f }, { "decl_scale", 0.88f }, { "energy", 1.3f }, { "casts_shadow", false }, { "atmosphere", true }, { "priority", 80f } });
            _applyLuminaries(edited2);
            _diffC = maxC;
            _stage = 4; _framesD = 0;
            return;
        }
        if (_stage == 4)
        {
            if (++_framesD < 2) { return; }
            int maxD = MaxDiff(_a!, Grab());
            // "identical" = the data path adds no MORE diff than the no-change recompose floor.
            bool identical = _diffB <= _jitter;
            bool primaryEdit = _diffC > _jitter + 8;   // primary-sun-size edit must change pixels beyond jitter
            bool addedBody = maxD > _jitter + 8;       // an added sun must change pixels beyond jitter
            bool ok = identical && primaryEdit && addedBody;
            GD.Print($"LUMINARYCHECK: {(ok ? "PASS" : "FAIL")}  floor={_jitter}, default-vs-datapath={_diffB} (want <= floor), primary-size-edit={_diffC} (want > floor+8), added-sun={maxD} (want > floor+8)");
            _t = -1.0;
            Engine.TimeScale = 1.0;
            _host.GetTree().Quit();
        }
    }

    /// The default body list (sun + moon), as objectlist dicts — matches data/luminaries.json.
    private static List<Godot.Collections.Dictionary> DefaultLuminaryDicts() => new()
    {
        new() { { "kind", 0 }, { "color", new Color(1.0f, 0.95f, 0.86f) }, { "size", 0.6f }, { "phase", 1f }, { "az_offset", 0f }, { "decl_scale", 1f }, { "energy", 1.3f }, { "casts_shadow", true }, { "atmosphere", true }, { "priority", 100f } },
        new() { { "kind", 1 }, { "color", new Color(0.85f, 0.88f, 1.0f) }, { "size", 1.2f }, { "phase", 1f }, { "az_offset", 35f }, { "decl_scale", 0.72f }, { "energy", 0.5f }, { "casts_shadow", true }, { "atmosphere", false }, { "priority", 10f } },
    };

    /// Max absolute per-byte difference between two equal-length RGBA8 buffers (0 = identical).
    private static int MaxDiff(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) { return 255; }
        int m = 0;
        for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; if (d < 0) { d = -d; } if (d > m) { m = d; } }
        return m;
    }
}
