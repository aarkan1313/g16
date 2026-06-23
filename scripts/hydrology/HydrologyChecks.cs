using Godot;
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
}
