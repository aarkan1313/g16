using Godot;
using WG16.Field;

namespace WG16.Hydrology;

/// Mechanical gates for the drainage-synthesis pipeline (windowed, --flag → print PASS/FAIL → quit). Kept out of
/// the interactive lab so the lab stays a thin display harness. Each returns true if it handled the flag.
///   --coarsecheck       deterministic + finite coarse sampler + halo mapping
///   --drainagecheck     fill never lowers; area dynamic range; Strahler hierarchy; every cell drains (undrained=0)
///   --determinismcheck  neighboring regions agree on overlap rivers (tile-coherence for streaming)
///   --carvecheck        carve_strength=0 untouched (modular); anti-terracing isotropy; valleys = low height
public static class HydrologyChecks
{
    /// Run whichever check flag is present. Returns true if a check ran (caller should then quit).
    public static bool TryRun(FieldParams p, FieldCompute fc, int res, float cell)
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a == "--coarsecheck") { Coarse(p, fc); return true; }
            if (a == "--drainagecheck") { Drainage(p, fc); return true; }
            if (a == "--determinismcheck") { Determinism(p, fc); return true; }
            if (a == "--carvecheck") { Carve(p, fc, res, cell); return true; }
            if (a == "--waterbudgetcheck") { WaterBudget(p, fc); return true; }
            if (a == "--watercoverage") { WaterCoverage(p, fc, res, cell); return true; }
            if (a == "--watercarvecheck") { WaterCarveCheck(p, fc, res, cell); return true; }
        }
        return false;
    }

    // proves "limited amounts" numerically: significant lakes must stay within the per-km² density cap on a
    // large region (NOT a pond flood — far below the naive-fill 2602-cell signature).
    private static void WaterBudget(FieldParams p, FieldCompute fc)
    {
        var hp = new HydrologyParams(); var wp = new WaterParams();
        int cres = 160; float sp = hp.CoarseSpacing;
        var cf = CoarseField.Build(fc, p, -6000f, -6000f, sp, cres);
        var g = DrainageGraph.Build(cf, hp);
        var wb = WaterBodies.Build(g, wp);
        float regionKm2 = (cres * sp) * (cres * sp) / 1e6f;
        float lakesPerKm2 = wb.Lakes.Count / regionKm2;
        bool ok = lakesPerKm2 <= wp.LakeDensityPerKm2 + 1e-3f;
        GD.Print($"WATERBUDGETCHECK: {(ok ? "PASS" : "FAIL")} region={regionKm2:F1}km² lakes={wb.Lakes.Count} ({lakesPerKm2:F2}/km², cap {wp.LakeDensityPerKm2}) rivers={wb.Rivers.Count}");
    }

    // Picks a sensible sea level: prints carved-height percentiles + flood coverage at each, so the sea sits low
    // (most land DRY). Use the level near the target coverage (~12%) for "not too much water".
    private static void WaterCoverage(FieldParams p, FieldCompute fc, int res, float cell)
    {
        var hp = new HydrologyParams { Res = res, CellSize = cell };
        var pipe = new HydrologyPipeline(res, cell);
        var carve = pipe.Build(p, fc, hp);
        float[] h = carve.Height; int n = h.Length;
        var s = (float[])h.Clone(); System.Array.Sort(s);
        float Pct(float q) => s[(int)Mathf.Clamp(q * (n - 1), 0, n - 1)];
        System.Text.StringBuilder sb = new("WATERCOVERAGE: percentiles ");
        foreach (float q in new[] { 0.05f, 0.10f, 0.15f, 0.20f, 0.30f, 0.50f })
            sb.Append($"p{(int)(q*100)}={Pct(q):F0} ");
        // coverage at a few candidate levels
        sb.Append(" | coverage ");
        foreach (float lvl in new[] { Pct(0.08f), Pct(0.12f), Pct(0.18f) })
        {
            int wet = 0; foreach (float v in h) { if (v < lvl) { wet++; } }
            sb.Append($"sea={lvl:F0}->{100f*wet/n:F0}% ");
        }
        pipe.Dispose();
        GD.Print(sb.ToString());
    }

    // proves the water carve is actually changing terrain (how many cells, by how much) — so "looks the same"
    // can be diagnosed: tiny delta = invisible at scale; large = it IS carving and the issue is elsewhere.
    private static void WaterCarveCheck(FieldParams p, FieldCompute fc, int res, float cell)
    {
        var hp = new HydrologyParams { Res = res, CellSize = cell }; var wp = new WaterParams();
        var pipe = new HydrologyPipeline(res, cell);
        var carve = pipe.Build(p, fc, hp);
        float[] before = (float[])carve.Height.Clone();
        var wb = WaterBodies.Build(pipe.LastGraph, wp);
        using var wc = new WaterCarve(res, cell);
        float[] after = wc.Apply(before, wb.Lakes, wb.Rivers, wp);
        int changed = 0; float maxDrop = 0f; double sumDrop = 0;
        for (int i = 0; i < before.Length; i++)
        { float d = before[i] - after[i]; if (d > 1e-3f) { changed++; maxDrop = System.MathF.Max(maxDrop, d); sumDrop += d; } }
        float pct = 100f * changed / before.Length;
        // print the biggest lake's center + surface so we can aim a close-up camera AT the water (the only way to
        // judge water detail — a 4km overview can't show a 7m bowl against 700m of relief).
        WaterBodies.Lake big = default; float bestSig = -1f;
        foreach (var lk in wb.Lakes) { if (lk.Significance > bestSig) { bestSig = lk.Significance; big = lk; } }
        float bcx = (big.MinX + big.MaxX) * 0.5f, bcz = (big.MinZ + big.MaxZ) * 0.5f;
        pipe.Dispose();
        GD.Print($"WATERCARVECHECK: lakes={wb.Lakes.Count} rivers={wb.Rivers.Count} cells_changed={changed} ({pct:F1}%) maxDrop={maxDrop:F1}m meanDrop={(changed>0?sumDrop/changed:0):F1}m");
        GD.Print($"  biggest lake center=({bcx:F0},{bcz:F0}) surface={big.SurfaceLevel:F0} extent=({big.MaxX-big.MinX:F0}x{big.MaxZ-big.MinZ:F0}m)  → fly here for a close-up");
    }

    private static void Coarse(FieldParams p, FieldCompute fc)
    {
        var hp = new HydrologyParams();
        int cres = 64;
        var cf1 = CoarseField.Build(fc, p, -1000f, -1000f, hp.CoarseSpacing, cres);
        var cf2 = CoarseField.Build(fc, p, -1000f, -1000f, hp.CoarseSpacing, cres);
        bool same = true, finite = true;
        for (int z = 0; z < cres; z++) for (int x = 0; x < cres; x++)
        { if (cf1.H(x, z) != cf2.H(x, z)) { same = false; } if (!float.IsFinite(cf1.H(x, z))) { finite = false; } }
        var (wx, wz) = cf1.World(1, 0);
        bool mapping = Mathf.Abs(wx - (-1000f + hp.CoarseSpacing)) < 1e-3f && Mathf.Abs(wz - (-1000f)) < 1e-3f;
        bool ok = same && finite && mapping;
        GD.Print($"COARSECHECK: {(ok ? "PASS" : "FAIL")} deterministic={same} finite={finite} mapping={mapping}");
    }

    private static void Drainage(FieldParams p, FieldCompute fc)
    {
        var hp = new HydrologyParams();
        int cres = 96;
        var cf = CoarseField.Build(fc, p, -3000f, -3000f, hp.CoarseSpacing, cres);
        var g = DrainageGraph.Build(cf, hp);
        bool fillOk = true;
        for (int z = 0; z < cres; z++) for (int x = 0; x < cres; x++) if (g.Filled[z*cres+x] < cf.H(x,z) - 1e-3f) fillOk = false;
        float amax = 0, amean = 0; foreach (float v in g.Area) { if (v > amax) amax = v; amean += v; } amean /= g.Area.Length;
        int o1 = 0, ohi = 0; foreach (int o in g.Order) { if (o == 1) o1++; else if (o >= 3) ohi++; }
        // drainage completeness: every interior cell reaches an edge outlet by following DownIdx (no dead-end).
        int undrained = 0, lakes = 0;
        for (int c0 = 0; c0 < cres * cres; c0++)
        {
            if (g.LakeDepth[c0] > hp.LakeMinDepth) { lakes++; }
            int cur = c0, hops = 0; bool drained = false;
            while (hops++ < cres * cres) { int d = g.DownIdx[cur]; if (d < 0) { drained = true; break; } cur = d; }
            if (!drained) { undrained++; }
        }
        bool ok = fillOk && g.Segments.Count > 50 && amax / amean > 20f && o1 > ohi && undrained == 0;
        GD.Print($"DRAINAGECHECK: {(ok ? "PASS" : "FAIL")} fillOk={fillOk} segs={g.Segments.Count} area max/mean={amax/amean:F0} order1={o1} order>=3={ohi} undrained={undrained} lakes={lakes}");
    }

    private static void Determinism(FieldParams p, FieldCompute fc)
    {
        var hp = new HydrologyParams();
        float sp = hp.CoarseSpacing; int cres = 128;
        float ox = -4000f, oz = -4000f; int shift = 16;
        var ga = DrainageGraph.Build(CoarseField.Build(fc, p, ox, oz, sp, cres), hp);
        var gb = DrainageGraph.Build(CoarseField.Build(fc, p, ox + shift * sp, oz, sp, cres), hp);
        // compare segments WELL INSIDE the shared overlap (exclude a 4-coarse-cell margin near each fill edge).
        float margin = 4f * sp;
        float lo = ox + shift * sp + margin, hi = ox + cres * sp - margin;
        System.Func<DrainageGraph, System.Collections.Generic.HashSet<string>> band = g =>
        {
            var s = new System.Collections.Generic.HashSet<string>();
            foreach (var seg in g.Segments)
                if (seg.Ax >= lo && seg.Ax <= hi && seg.Bx >= lo && seg.Bx <= hi)
                    s.Add($"{seg.Ax:F1},{seg.Az:F1}->{seg.Bx:F1},{seg.Bz:F1}:{seg.Order}");
            return s;
        };
        var sa = band(ga); var sb = band(gb);
        int matched = 0, total = 0;
        foreach (var k in sa) { total++; if (sb.Contains(k)) matched++; }
        float frac = total > 0 ? (float)matched / total : 0f;
        bool ok = frac > 0.95f;
        GD.Print($"DETERMINISMCHECK: {(ok ? "PASS" : "FAIL")} overlap segments={total} identical={matched} frac={frac:F3} (need >0.95)");
    }

    private static void Carve(FieldParams p, FieldCompute fc, int res, float cell)
    {
        var hp = new HydrologyParams { Res = res, CellSize = cell };
        float bo = -res * cell * 0.5f;
        float[] baseH = fc.ProducePage(p, bo, bo, cell, res, 0);
        var cf = CoarseField.Build(fc, p, bo - hp.HaloMetres, bo - hp.HaloMetres, hp.CoarseSpacing,
            (int)((res * cell + 2 * hp.HaloMetres) / hp.CoarseSpacing));
        var g = DrainageGraph.Build(cf, hp);
        using var vc = new ValleyCarve(res);
        var hp0 = new HydrologyParams { Res = res, CellSize = cell, CarveStrength = 0f };
        var r0 = vc.Carve(baseH, g, hp0);   // MODULARITY: carve_strength=0 => untouched
        bool untouched = true; for (int k = 0; k < baseH.Length; k++) if (Mathf.Abs(r0.Height[k] - baseH[k]) > 1e-3f) untouched = false;
        var r = vc.Carve(baseH, g, hp);
        double rx = 0, rz = 0; long nn = 0;
        for (int z = 1; z < res-1; z++) for (int x = 1; x < res-1; x++)
        { int j = z*res+x; rx += Mathf.Abs(2f*r.Height[j]-r.Height[j-1]-r.Height[j+1]); rz += Mathf.Abs(2f*r.Height[j]-r.Height[j-res]-r.Height[j+res]); nn++; }
        float aniso = (float)(rz / System.Math.Max(rx, 1e-9));
        var ord = new int[r.Height.Length]; for (int q = 0; q < ord.Length; q++) ord[q] = q;
        System.Array.Sort(ord, (x, y) => r.FlowAccum[y].CompareTo(r.FlowAccum[x]));
        int top = ord.Length / 100; double hTop = 0, hAll = 0;
        for (int q = 0; q < top; q++) hTop += r.Height[ord[q]]; hTop /= top;
        foreach (float v in r.Height) hAll += v; hAll /= r.Height.Length;
        bool ok = untouched && aniso > 0.7f && aniso < 1.4f && hTop < hAll;
        GD.Print($"CARVECHECK: {(ok ? "PASS" : "FAIL")} zeroSeg_untouched={untouched} antiterrace_z/x={aniso:F2} (want ~1) valleys(hTop={hTop:F1}<hAll={hAll:F1})={hTop<hAll}");
    }
}
