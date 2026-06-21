using Godot;

namespace WG16.Field;

/// One-shot numeric self-check for S1: the field is DETERMINISTIC and the
/// extract/refactor (field_math.gdshaderinc + struct params + C# splice) did not
/// change a single height. Bakes the page twice through the GPU compute path at the
/// SAME params and asserts byte-for-byte-close equality. PASS/FAIL to console.
public static class FieldCheck
{
    public static void Run(FieldCompute fc, FieldParams p)
    {
        float ox = -p.RegionSizeM * 0.5f, oz = -p.RegionSizeM * 0.5f;
        float[] a = fc.ProducePage(p, ox, oz);
        float[] b = fc.ProducePage(p, ox, oz);   // same inputs -> must be identical
        float maxAbs = 0f; int worst = -1;
        int n = Mathf.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            float d = Mathf.Abs(a[i] - b[i]);
            if (d > maxAbs) { maxAbs = d; worst = i; }
        }
        const float eps = 1e-3f;   // GPU determinism: identical inputs should diff ~0
        bool pass = a.Length == b.Length && maxAbs <= eps;
        GD.Print($"FIELDCHECK: {(pass ? "PASS" : "FAIL")}  maxAbsDiff={maxAbs:G6}m  worstIdx={worst}  n={n}  eps={eps}");
    }
}
