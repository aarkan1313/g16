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
}
