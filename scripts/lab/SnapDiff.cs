using Godot;
using System.Collections.Generic;
using System.Text;

namespace WG16.Lab;

/// S3 snap-seam diagnostic (--snapdiff). The user sees a POP exactly when renderOrigin SNAPS (HUD confirmed:
/// the terrain reorganizes as origin goes 0→-8192). The snap is SUPPOSED to be a pure coordinate reframe with
/// zero visual change. This walks two camera positions 20 m apart straddling a snap boundary and, for a set of
/// FIXED world points, prints the leaf (origin,size) each gets on each side. If a point's leaf origin/size
/// DIFFERS across the snap → the field is sampled at a different place there → the visible pop. PASS = every
/// fixed point gets an identical leaf on both sides (snap is seamless). Pure logic — no GPU.
public static class SnapDiff
{
    public static bool Run(float regionSize, int maxDepth, float splitFactor, out string msg)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        float snap = regionSize;   // matches CdlodTerrain._coarseSnap = _regionSize = rootSize

        // Two cameras straddling the x = 0 snap boundary (origin -snap vs 0). Same Y, same Z.
        var camA = new Vector3(-10f, 400f, 3000f);   // floor(-10/snap) = -1 → roaming window + renderOrigin shifted
        var camB = new Vector3(10f, 400f, 3000f);    // floor( 10/snap) =  0

        // Fixed world points spread in front of both cameras (the NEAR terrain the user saw change).
        var pts = new List<Vector2>();
        for (int gz = 0; gz <= 6; gz++)
        for (int gx = -3; gx <= 3; gx++)
            pts.Add(new Vector2(gx * 600f, 600f + gz * 600f));

        int mismatches = 0; var sb = new StringBuilder();
        foreach (Vector2 p in pts)
        {
            // The leaf that contains p, under each camera's split decision.
            float sizeA = qt.LeafSizeAt(p.X, p.Y, camA);
            float sizeB = qt.LeafSizeAt(p.X, p.Y, camB);
            // The leaf ORIGIN (floor to leaf size) — the chunk's true-world corner, must be identical for the
            // rendered surface at p to be identical (the morph + field sample both key off it).
            Vector2 oA = new Vector2(Mathf.Floor(p.X / sizeA) * sizeA, Mathf.Floor(p.Y / sizeA) * sizeA);
            Vector2 oB = new Vector2(Mathf.Floor(p.X / sizeB) * sizeB, Mathf.Floor(p.Y / sizeB) * sizeB);
            if (sizeA != sizeB || oA != oB)
            {
                mismatches++;
                if (mismatches <= 6)
                    sb.Append($"  p=({p.X:F0},{p.Y:F0}): A leaf={sizeA:F0}@({oA.X:F0},{oA.Y:F0})  B leaf={sizeB:F0}@({oB.X:F0},{oB.Y:F0})\n");
            }
        }

        // --- float32 reconstruction test: the shader computes wxz = (trueX - renderOrigin) + renderOrigin in
        // FLOAT. Algebraically identity, but does float32 preserve it across the two origins at real distances?
        // Mirror it in float (C# float == GPU 32-bit) for trueX out to several km. A non-zero delta here that
        // lands near a noise-cell boundary becomes a LARGE height change (field uses floor()).
        float originA = -snap, originB = 0f;   // the two renderOrigins straddling x=0
        float maxRecon = 0f; float worstX = 0f;
        for (float tx = -6000f; tx <= 6000f; tx += 13.7f)
        {
            float reconA = (float)((float)(tx - originA) + originA);
            float reconB = (float)((float)(tx - originB) + originB);
            float delta = Mathf.Abs(reconA - reconB);
            if (delta > maxRecon) { maxRecon = delta; worstX = tx; }
        }

        bool reconOk = maxRecon < 0.01f;
        bool ok = mismatches == 0 && reconOk;
        msg = (mismatches == 0
                ? $"{pts.Count} fixed pts: identical leaf+origin both sides — leaf SEAMLESS"
                : $"SNAP NOT SEAMLESS: {mismatches}/{pts.Count} pts DIFFER:\n{sb}")
            + $" | float-recon maxΔ={maxRecon:E3}m at trueX={worstX:F0} ({(reconOk ? "ok" : "RECON BREAKS — precision pop")})";
        return ok;
    }
}
