using Godot;
using System.Linq;
namespace WG16.Hydrology;

/// CLI self-check host (the codebase's TDD equivalent — no unit-test framework). Each check is
/// deterministic and prints `WATERCHECK <name>: PASS/FAIL ...`. Run via `--watercheck=<name>`.
public static class HydrologyChecks
{
    public static bool Run(string check)
    {
        switch (check)
        {
            case "params": return CheckParams();
            case "drainage": return CheckDrainage();
            case "rivers": return CheckRivers();
            case "lakes": return CheckLakes();
            case "texture": return CheckTexture();
            case "continuity": return CheckContinuity();
            default: GD.PrintErr($"WATERCHECK: unknown check '{check}'"); return false;
        }
    }

    private static bool CheckParams()
    {
        var a = WaterParams.Load();
        var b = WaterParams.Load();
        bool ok = a.CoarseRes == b.CoarseRes && Mathf.IsEqualApprox(a.CarveDepthScale, b.CarveDepthScale)
                  && a.CoarseRes > 0 && a.HaloRegions >= 1;
        GD.Print($"WATERCHECK params: {(ok ? "PASS" : "FAIL")} coarseRes={a.CoarseRes} halo={a.HaloRegions}");
        return ok;
    }

    private static bool CheckDrainage()
    {
        var fp = WG16.Field.FieldParams.Load();
        var wp = WaterParams.Load();
        var d = new CoarseDrainage(fp, wp, 0, 0);
        // (a) accumulation conserved: total accum >= cell count (every cell contributes >=1)
        double total = 0; float maxA = 0;
        for (int i = 0; i < d.Accum.Length; i++) { total += d.Accum[i]; maxA = Mathf.Max(maxA, d.Accum[i]); }
        bool conserve = total >= d.Accum.Length;
        // (b) channels exist + are concentrated (drainage concentrates, not everywhere)
        int channels = 0; for (int i = 0; i < d.Accum.Length; i++) if (d.Accum[i] > wp.RiverAccumThreshold) channels++;
        bool hasChannels = channels > 0 && channels < d.Accum.Length / 4;
        bool ok = conserve && hasChannels;
        GD.Print($"WATERCHECK drainage: {(ok ? "PASS" : "FAIL")} cells={d.Accum.Length} maxAccum={maxA:0} channels={channels}");
        return ok;
    }

    private static bool CheckRivers()
    {
        var fp = WG16.Field.FieldParams.Load();
        var wp = WaterParams.Load();
        var d = new CoarseDrainage(fp, wp, 0, 0);
        var rivers = RiverTracer.Trace(d, wp);
        bool any = rivers.Count > 0;
        bool descend = true;
        foreach (var r in rivers)
        {
            if (r.Points.Length < 2) { descend = false; break; }
            for (int i = 1; i < r.Points.Length; i++)
            {
                float h0 = d.ProxyHeight(r.Points[i - 1].X, r.Points[i - 1].Y);
                float h1 = d.ProxyHeight(r.Points[i].X, r.Points[i].Y);
                if (h1 > h0 + 5f) { descend = false; break; }   // allow tiny coarse noise
            }
            if (!descend) break;
        }
        bool ok = any && descend;
        GD.Print($"WATERCHECK rivers: {(ok ? "PASS" : "FAIL")} reaches={rivers.Count} descend={descend}");
        return ok;
    }

    private static bool CheckLakes()
    {
        var fp = WG16.Field.FieldParams.Load();
        var wp = WaterParams.Load();
        var d = new CoarseDrainage(fp, wp, 0, 0);
        var t = new WaterTable(wp, fp.Seed);
        var lakes = LakeGating.GatedLakes(d, t, wp, fp.RegionSizeM);
        float km2 = (fp.RegionSizeM / 1000f) * (fp.RegionSizeM / 1000f);
        float perKm2 = lakes.Count / km2;
        float avgArea = lakes.Count > 0 ? lakes.Average(l => l.AreaM2) : 0f;
        // PASS = under the density cap AND lakes actually exist (a gate that yields zero is a starved gate).
        bool ok = perKm2 <= wp.LakeDensityPerKm2 + 1e-3f && lakes.Count > 0;
        GD.Print($"WATERCHECK lakes: {(ok ? "PASS" : "FAIL")} lakes={lakes.Count} perKm2={perKm2:0.000} " +
                 $"cap={wp.LakeDensityPerKm2} avgAreaM2={avgArea:0}");
        return ok;
    }

    private static bool CheckTexture()
    {
        var fp = WG16.Field.FieldParams.Load();
        var wp = WaterParams.Load();
        var reg = new WorldWaterRegion(fp, wp, 0, 0);
        var img = reg.Texture.GetImage();
        int wet = 0; bool rangeOk = true;
        for (int y = 0; y < img.GetHeight(); y += 4)
            for (int x = 0; x < img.GetWidth(); x += 4)
            {
                Color c = img.GetPixel(x, y);
                if (c.A > 0.5f) wet++;
                if (c.A < -0.01f || c.A > 1.01f) rangeOk = false;
            }
        bool ok = wet > 0 && rangeOk;
        GD.Print($"WATERCHECK texture: {(ok ? "PASS" : "FAIL")} wetSamples={wet} rangeOk={rangeOk}");
        return ok;
    }

    private static bool CheckContinuity()
    {
        var fp = WG16.Field.FieldParams.Load();
        var wp = WaterParams.Load();
        var a = new WorldWaterRegion(fp, wp, 0, 0);
        var b = new WorldWaterRegion(fp, wp, 1, 0);             // neighbor to the +X
        float seamX = fp.RegionSizeM;
        int agree = 0, total = 0;
        for (float z = 200; z < fp.RegionSizeM; z += 200)
        {
            bool wa = a.IsWet(seamX - 1f, z);
            bool wb = b.IsWet(seamX + 1f, z);
            total++; if (wa == wb) agree++;
        }
        float frac = total > 0 ? agree / (float)total : 1f;
        bool ok = frac >= 0.95f;
        GD.Print($"WATERCHECK continuity: {(ok ? "PASS" : "FAIL")} agree={frac:0.000}");
        return ok;
    }
}
