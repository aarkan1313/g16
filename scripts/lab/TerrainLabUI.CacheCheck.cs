using Godot;

namespace WG16.Lab;

/// --cachecheck windowed self-check: proves the field cache's per-chunk min/max (now used for the shadow
/// AABB) actually bounds the field over the chunk. Bakes a known chunk into a standalone ChunkFieldCache,
/// pumps it on the render thread, takes its (lo,hi), and compares to a CPU FieldCompute reduction of the
/// SAME chunk footprint. PASS = the cache range brackets the CPU min/max (within a small tolerance). This is
/// the load-bearing proof for the AABB fix — a broken atomics/readback path would print a wildly wrong range.
/// Windowed only (ChunkFieldCache uses the main render-thread RD, which NullRefs headless).
public partial class TerrainLabUI : Control
{
    private ChunkFieldCache? _cacheChk;
    private int _cacheChkFrame = -1;   // -1 = not started; >=0 = frames since request
    private bool _cacheChkDone;
    // The known chunk under test (origin + size in world metres). Off-origin so it also exercises the
    // far-from-region-(0,0) case the AABB envelope bug was about.
    private static readonly Vector2 CacheChkOrigin = new(40000f, 40000f);
    private const float CacheChkSize = 2048f;

    private void ArmCacheCheck()
    {
        _cacheChk = new ChunkFieldCache(_params, 65, 8);
        _cacheChk.Prewarm();
        _cacheChk.Request(1L, 0, CacheChkOrigin, CacheChkSize);   // key 1, slot 0
        _cacheChkFrame = 0;
    }

    private void TickCacheCheck()
    {
        if (_cacheChkFrame < 0 || _cacheChkDone || _cacheChk == null) { return; }
        _cacheChk.Pump();
        if (_cacheChk.TryTake(out long _, out int _, out float lo, out float hi))
        {
            // CPU truth: reduce field_height over the chunk footprint at the field's natural spacing (the same
            // octave-gate the bake uses). Positions differ slightly from the chunk's exact vertices, so compare
            // with a tolerance that catches a broken path (garbage/NaN = off by hundreds) but allows sampling slack.
            int res = Mathf.Max(8, Mathf.CeilToInt(CacheChkSize / _params.Spacing) + 1);
            float[] page = _fc.ProducePage(_params, CacheChkOrigin.X, CacheChkOrigin.Y, _params.Spacing, res, 0);
            float clo = float.MaxValue, chi = float.MinValue;
            foreach (float v in page) { if (v < clo) { clo = v; } if (v > chi) { chi = v; } }
            const float tol = 12f;   // metres: bake includes a 1-texel border + vtx-vs-field sampling slack
            bool ok = Mathf.Abs(lo - clo) < tol && Mathf.Abs(hi - chi) < tol && hi > lo;
            GD.Print($"CACHECHECK: {(ok ? "PASS" : "FAIL")}  cache=({lo:F1},{hi:F1}) cpu=({clo:F1},{chi:F1}) " +
                     $"d=({Mathf.Abs(lo - clo):F1},{Mathf.Abs(hi - chi):F1}) tol={tol}");
            _cacheChkDone = true;
            GetTree().Quit();
        }
        else if (++_cacheChkFrame > 120)
        {
            GD.Print("CACHECHECK: FAIL — no result in 120 frames (cache bake/readback never landed)");
            _cacheChkDone = true;
            GetTree().Quit();
        }
    }
}
