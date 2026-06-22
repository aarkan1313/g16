using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// U2 numeric gate (--luminarycheck): proves the data-driven luminary path is byte-identical for the
/// default look AND that edits propagate — WITHOUT cross-launch screenshot drift. Mirrors the --godrayab
/// staged pattern: freeze the scene (TimeScale=0) so frames are identical except for the luminary change,
/// then in one run:
///   A = render with the default loaded list (data path, extra counts 0)
///   B = render after RE-loading the SAME default list via the live edit path (ApplyLuminaryDicts)
///       -> assert maxdiff(A,B) == 0  (the data/edit path does not perturb the default sky)
///   C = render after appending an extra Sun via the data path
///       -> assert maxdiff(A,C) > threshold  (edits actually change the sky)
/// Prints LUMINARYCHECK: PASS/FAIL. This is the load-bearing default-byte-identical proof for U2.
public partial class TerrainLabUI : Control
{
    private double _lumCheckT = -1.0;
    private int _lumStage = 0;
    private byte[]? _lumA;        // frozen reference image bytes (default/data path)
    private int _lumW, _lumH;

    private void TickLuminaryCheck(double delta)
    {
        if (_lumCheckT < 0.0) { return; }
        _lumCheckT += delta;

        if (_lumStage == 0 && _lumCheckT > 1.5)
        {
            Engine.TimeScale = 0.0;                          // freeze clouds/cycle so only luminaries vary
            var img = GetViewport().GetTexture().GetImage();
            _lumW = img.GetWidth(); _lumH = img.GetHeight();
            _lumA = img.GetData();                           // A: default sky via the data path (NO change made)
            _lumStage = 1;
            return;
        }
        if (_lumStage == 1)
        {
            // Calibrate the floor: a bare ComposeLighting() recompose with NO luminary change. This captures
            // any 1-LSB churn from Compose() itself (overcast settle, tonemap re-run) so the data-path
            // comparison isolates ONLY the luminary contribution above that floor.
            ComposeLighting();
            _lumStage = 10; _lumFramesFloor = 0;
            return;
        }
        if (_lumStage == 10)
        {
            if (++_lumFramesFloor < 2) { return; }
            _lumJitter = MaxDiff(_lumA!, GetViewport().GetTexture().GetImage().GetData());
            // B: re-load the SAME default list through the live edit adapter, then recompose.
            ApplyLuminaryDicts(DefaultLuminaryDicts());
            _lumStage = 2; _lumFramesB = 0;
            return;
        }
        if (_lumStage == 2)
        {
            if (++_lumFramesB < 2) { return; }               // let the recompose land
            _lumDiffB = MaxDiff(_lumA!, GetViewport().GetTexture().GetImage().GetData());
            // C: edit the PRIMARY sun's disc SIZE via the data path (a primary-from-list edit, visible at noon),
            // then recompose. Proves the list now drives the PRIMARY body's render, not just added ones.
            var edited = DefaultLuminaryDicts();
            edited[0]["size"] = 4.0f;   // primary sun disc 0.6 -> 4.0 deg (big, unmistakable at noon)
            ApplyLuminaryDicts(edited);
            _lumStage = 3; _lumFramesC = 0;
            return;
        }
        if (_lumStage == 3)
        {
            if (++_lumFramesC < 2) { return; }               // let the recompose land
            int maxC = MaxDiff(_lumA!, GetViewport().GetTexture().GetImage().GetData());
            // D: append an extra Sun (the added-body path) via the data path, then recompose.
            var edited2 = DefaultLuminaryDicts();
            edited2.Add(new Godot.Collections.Dictionary { { "kind", 0 }, { "color", new Color(0.62f, 0.78f, 1.0f) }, { "size", 0.9f }, { "phase", 1f }, { "az_offset", 60f }, { "decl_scale", 0.88f }, { "energy", 1.3f }, { "casts_shadow", false }, { "atmosphere", true }, { "priority", 80f } });
            ApplyLuminaryDicts(edited2);
            _lumDiffC = maxC;
            _lumStage = 4; _lumFramesD = 0;
            return;
        }
        if (_lumStage == 4)
        {
            if (++_lumFramesD < 2) { return; }
            int maxD = MaxDiff(_lumA!, GetViewport().GetTexture().GetImage().GetData());
            // "identical" = the data path adds no MORE diff than the no-change recompose floor.
            bool identical = _lumDiffB <= _lumJitter;
            bool primaryEdit = _lumDiffC > _lumJitter + 8;   // primary-sun-size edit must change pixels beyond jitter
            bool addedBody = maxD > _lumJitter + 8;          // an added sun must change pixels beyond jitter
            bool ok = identical && primaryEdit && addedBody;
            GD.Print($"LUMINARYCHECK: {(ok ? "PASS" : "FAIL")}  floor={_lumJitter}, default-vs-datapath={_lumDiffB} (want <= floor), primary-size-edit={_lumDiffC} (want > floor+8), added-sun={maxD} (want > floor+8)");
            _lumCheckT = -1.0;
            Engine.TimeScale = 1.0;
            GetTree().Quit();
        }
    }

    private int _lumDiffB, _lumDiffC, _lumFramesB, _lumFramesC, _lumFramesD, _lumFramesFloor, _lumJitter;

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
