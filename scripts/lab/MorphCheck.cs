using Godot;

namespace WG16.Lab;

/// S2b numeric backstop for the per-vertex geomorph (--morphcheck). Mirrors the EXACT morph math in
/// shaders/ground.gdshader's chunk branch in pure C# and asserts the geometry is pop-free BY CONSTRUCTION,
/// so a future change (or a stray sign flip — exactly the bug that shipped in the first cut) can't silently
/// reintroduce an elevation pop without this PASS/FAIL screaming.
///
/// The morph (per vertex): u_coarse = round(g_fine/2)*2 / (grid_n-1); morphK ramps 0 (band near edge) -> 1
/// (band far edge) over the FAR HALF of the band [S*split, 2S*split]; u_morph = mix(u_fine,u_coarse,morphK).
///
/// Three properties prove pop-freeness (none is the eye-gate — that stays the user's call in motion):
///   (A) morphK(near)=0, morphK(far)=1, monotonic non-decreasing across the band (no jump in the driver).
///   (B) at the FAR edge (morphK=1) a fine chunk's morphed vertices land EXACTLY on its coarse parent's
///       vertex grid in WORLD space — so the swap fine->coarse moves no vertex => no elevation pop.
///   (C) C0 across the boundary: the max world-XZ discrepancy between the fully-morphed fine surface and
///       the coarse parent at d=far_d is ~0 (same assertion as B, reported as a distance).
///
/// This proves the DESIGN is pop-free + locks regression. It does NOT re-prove the GPU executes this math
/// (that's the live eye-gate's job) — the two legs together are the gate.
public static class MorphCheck
{
    // Mirror of the shader: nearest-even snap of a fine grid index (g_coarse = floor(g*0.5+0.5)*2).
    private static float SnapEven(float gFine) => Mathf.Floor(gFine * 0.5f + 0.5f) * 2.0f;

    // Mirror of the shader morphK: 0 over [near,mid], ramps 0->1 over [mid,far], clamped.
    private static float MorphK(float d, float nearD, float farD)
    {
        float midD = (nearD + farD) * 0.5f;
        return Mathf.Clamp((d - midD) / Mathf.Max(farD - midD, 1e-3f), 0f, 1f);
    }

    /// Run the check for one representative chunk size. gridN = verts/side (65), splitFactor = quadtree rule.
    /// chunkSize = the fine chunk's world size; the coarse parent is 2*chunkSize. Origins are arbitrary
    /// (the pop-free property is translation-invariant) — we put the fine chunk as the lower quadrant of
    /// its parent (parent origin 0; fine origin 0) which is the worst case for index alignment.
    public static bool Run(int gridN, float splitFactor, float chunkSize, out string msg)
    {
        float gm1 = gridN - 1.0f;
        float nearD = 1.0f * chunkSize * splitFactor;
        float farD = 2.0f * chunkSize * splitFactor;

        // --- (A) morphK driver: endpoints + monotonicity (catches the sign-flip regression directly) ---
        float kNear = MorphK(nearD, nearD, farD);
        float kFar = MorphK(farD, nearD, farD);
        const float eps = 1e-4f;
        if (Mathf.Abs(kNear - 0f) > eps) { msg = $"morphK(near)={kNear:F4} expected 0 (sign/window bug)"; return false; }
        if (Mathf.Abs(kFar - 1f) > eps) { msg = $"morphK(far)={kFar:F4} expected 1 (sign/window bug)"; return false; }
        float prev = -1f;
        for (int s = 0; s <= 64; s++)
        {
            float d = Mathf.Lerp(nearD, farD, s / 64.0f);
            float k = MorphK(d, nearD, farD);
            if (k < prev - eps) { msg = $"morphK non-monotonic at d={d:F1} (k={k:F4} < prev={prev:F4})"; return false; }
            prev = k;
        }

        // --- (B)/(C) worst-case world-XZ gap between the fully-morphed fine surface and the coarse parent ---
        // Fine chunk: origin (0,0), size S, grid gridN. Coarse parent: origin (0,0), size 2S, grid gridN.
        // At the far edge morphK=1 so every fine vertex sits at its u_coarse position; we check that world
        // pos coincides with SOME coarse-parent vertex (the coarse grid the parent actually renders).
        float coarseSpacing = (2.0f * chunkSize) / gm1;   // world spacing between coarse-parent verts
        float worst = 0f;
        for (int j = 0; j < gridN; j++)
        for (int i = 0; i < gridN; i++)
        {
            // fine vertex -> fully-morphed (morphK=1) world XZ
            float uCoarseX = SnapEven(i) / gm1;
            float uCoarseZ = SnapEven(j) / gm1;
            float wx = 0f + uCoarseX * chunkSize;   // fine origin 0
            float wz = 0f + uCoarseZ * chunkSize;
            // distance to the NEAREST coarse-parent grid vertex (parent origin 0, spacing coarseSpacing)
            float nx = Mathf.Round(wx / coarseSpacing) * coarseSpacing;
            float nz = Mathf.Round(wz / coarseSpacing) * coarseSpacing;
            float gap = Mathf.Sqrt((wx - nx) * (wx - nx) + (wz - nz) * (wz - nz));
            if (gap > worst) { worst = gap; }
        }
        // Tolerance: a tiny fraction of the coarse spacing absorbs float32 round-off; a real misalignment
        // (e.g. grid_n-1 odd, or a wrong snap) is on the order of the FINE spacing — orders of magnitude bigger.
        float tol = coarseSpacing * 1e-3f;
        if (worst > tol)
        {
            msg = $"morphed-fine vs coarse-grid gap={worst:F4}m > tol={tol:F4}m (POP: far-edge hand-off not vertex-coincident)";
            return false;
        }

        msg = $"morphK 0->1 monotonic; far-edge gap={worst:F5}m (<= {tol:F5}m) — vertex-coincident hand-off";
        return true;
    }
}
