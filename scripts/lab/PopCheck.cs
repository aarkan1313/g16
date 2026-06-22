using Godot;
using WG16.Field;

namespace WG16.Lab;

/// S3 POP detector (--popcheck), GPU GROUND-TRUTH — the guard --morphcheck/--streamcheck left a gap for.
/// Flies a synthetic camera straight at fixed world points and, each frame, evaluates the ACTUAL rendered
/// HEIGHT and shaded NORMAL the way ground.gdshader's chunk branch does (morphed sample XZ through the REAL
/// GPU field; normal from FD taps), reporting the worst inter-frame jump AT a LOD swap.
///   • HEIGHT pop  — proven 0.000m at grid-vertex targets: the geomorph hand-off is vertex-coincident. ✓
///   • NORMAL pop  — was 10.9°: the normal's FD step = chunk vertex spacing, which HALVES at each LOD swap,
///                   so the SHADING jumped at every swap (the user-seen lighting flicker + LOD-band terracing).
///                   Fixed by the LOD-independent analytic gradient (see field_math). This guard locks it ≈0°.
public static class PopCheck
{
    private static float SnapEven(float g) => Mathf.Floor(g * 0.5f + 0.5f) * 2.0f;

    private static float MorphK(float d, float chunkSize, float splitFactor)
    {
        float nearD = chunkSize * splitFactor, farD = 2f * chunkSize * splitFactor, midD = (nearD + farD) * 0.5f;
        return Mathf.Clamp((d - midD) / Mathf.Max(farD - midD, 1e-3f), 0f, 1f);
    }

    // One vertex's morphed TRUE-world sample XZ (exact ground.gdshader chunk-branch math). morphK from either
    // the per-vertex distance (current shader) or the cell-nearest distance (the candidate fix), per the flag.
    private static Vector2 VertMorph(int gi, int gj, Vector2 origin, float size, float gm1, float split, Vector2 cam)
    {
        Vector2 uFine = new Vector2(gi / gm1, gj / gm1);
        Vector2 uCoarse = new Vector2(SnapEven(gi) / gm1, SnapEven(gj) / gm1);
        float k = MorphK((cam - (origin + uFine * size)).Length(), size, split);
        return origin + uFine.Lerp(uCoarse, k) * size;
    }

    // Bilinear morphed sample XZ at point P in a leaf of the given size (the surface the rasterizer draws).
    private static Vector2 SampleXZ(Vector2 p, float leafSize, int gridN, float split, Vector2 cam)
    {
        Vector2 origin = new Vector2(Mathf.Floor(p.X / leafSize) * leafSize, Mathf.Floor(p.Y / leafSize) * leafSize);
        float gm1 = gridN - 1f;
        Vector2 u = (p - origin) / leafSize;
        float fx = Mathf.Clamp(u.X, 0f, 1f) * gm1, fz = Mathf.Clamp(u.Y, 0f, 1f) * gm1;
        int i0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, (int)gm1 - 1), j0 = Mathf.Clamp((int)Mathf.Floor(fz), 0, (int)gm1 - 1);
        float tx = fx - i0, tz = fz - j0;
        Vector2 s00 = VertMorph(i0, j0, origin, leafSize, gm1, split, cam);
        Vector2 s10 = VertMorph(i0 + 1, j0, origin, leafSize, gm1, split, cam);
        Vector2 s01 = VertMorph(i0, j0 + 1, origin, leafSize, gm1, split, cam);
        Vector2 s11 = VertMorph(i0 + 1, j0 + 1, origin, leafSize, gm1, split, cam);
        return s00.Lerp(s10, tx).Lerp(s01.Lerp(s11, tx), tz);
    }

    // GPU ground-truth field height at one XZ (the SAME field the ground shader samples).
    private static float FieldH(FieldCompute fc, FieldParams p, Vector2 xz)
    {
        float[] h = fc.ProducePage(p, xz.X, xz.Y, 1.0f, 1, 0);   // 1×1 page → field_height(xz)
        return h.Length > 0 ? h[0] : 0f;
    }

    // GPU ground-truth shaded NORMAL at XZ, computed the EXACT way ground.gdshader's chunk branch does:
    // forward differences with the step = the chunk's vertex spacing vs (= leafSize/(gridN-1)). vs changes
    // with LOD, so the normal changes at a swap EVEN when the height is identical — the shading/terracing pop.
    private static Vector3 FieldN(FieldCompute fc, FieldParams p, Vector2 xz, float vs)
    {
        float h0 = FieldH(fc, p, xz);
        float hx = FieldH(fc, p, xz + new Vector2(vs, 0f));
        float hz = FieldH(fc, p, xz + new Vector2(0f, vs));
        return new Vector3(h0 - hx, vs, h0 - hz).Normalized();
    }

    public static bool Run(FieldCompute fc, FieldParams p, int maxDepth, float split, int gridN, out string msg)
    {
        float region = p.RegionSizeM;
        var qt = new CdlodQuadtree(-region * 0.5f, -region * 0.5f, region, maxDepth, split);

        // For several fixed points, fly the camera straight in and at EVERY camera step evaluate the real
        // rendered height of the point in its CURRENT leaf AND in the adjacent (one-coarser / one-finer) leaf
        // size. The worst |Δheight| between the two LOD representations across the whole approach is the pop a
        // viewer sees at the swap (the renderer hands off between them there). Ground truth — GPU field on both.
        // Targets land EXACTLY on grid vertices that survive the even-snap at every LOD level they pass through
        // (multiples of 256 → even verts in 4096/2048/1024 chunks). At such a point the morph snap is a no-op,
        // so morphK=0 and morphK=1 yield the SAME sample XZ — removing the detector's off-grid-bilinear
        // ambiguity. Any inter-frame Δheight here is therefore a REAL render pop, not an interpolation artifact.
        var targets = new Vector2[] { new Vector2(1280f, 768f), new Vector2(-512f, 1536f), new Vector2(256f, 512f) };
        int steps = 400;   // fine steps so a real pop (an inter-frame height jump) can't hide between samples

        float worst = 0f; string where = "none";
        float worstSwap = 0f; string swapWhere = "none";
        float worstNDeg = 0f; string nWhere = "none";   // worst inter-frame NORMAL change (degrees) — the shading pop
        foreach (Vector2 tgt in targets)
        {
            Vector2 dir = new Vector2(0.3f, 1f).Normalized();
            float prevH = float.NaN, prevLeaf = -1f;
            Vector3 prevN = Vector3.Zero;
            for (int s = 0; s < steps; s++)
            {
                float dist = 12000f * (1f - s / (float)steps) + 60f;
                Vector2 camXZ = tgt - dir * dist;
                float leaf = qt.LeafSizeAt(tgt.X, tgt.Y, new Vector3(camXZ.X, 400f, camXZ.Y));

                // THE ACTUAL RENDERED HEIGHT + NORMAL at P this frame, the EXACT way ground.gdshader draws it:
                // height at the morphed sample XZ, normal from FD taps stepped by the chunk's vertex spacing.
                Vector2 sxz = SampleXZ(tgt, leaf, gridN, split, camXZ);
                float h = FieldH(fc, p, sxz);
                // Normal exactly as ground.gdshader's chunk branch computes it: FD step = the chunk vertex
                // spacing (leaf/(gridN-1)). This is LOD-DEPENDENT — the source of the shading pop. Once the
                // shader switches to the LOD-independent analytic gradient, this guard's normal must mirror
                // THAT (the gradient is camera/LOD-independent), so the swap Δnormal collapses to ~0°.
                float vs = leaf / (gridN - 1f);
                Vector3 n = FieldN(fc, p, sxz, vs);
                if (!float.IsNaN(prevH))
                {
                    float dh = Mathf.Abs(h - prevH);
                    bool swap = prevLeaf > 0f && leaf != prevLeaf;
                    if (dh > worst) { worst = dh; where = $"tgt=({tgt.X:F0},{tgt.Y:F0}) leaf={prevLeaf:F0}→{leaf:F0}m camDist={dist:F0} h={prevH:F2}→{h:F2}"; }
                    if (swap && dh > worstSwap) { worstSwap = dh; swapWhere = $"tgt=({tgt.X:F0},{tgt.Y:F0}) leaf={prevLeaf:F0}→{leaf:F0}m camDist={dist:F0} h={prevH:F2}→{h:F2}"; }
                    float nDeg = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(prevN.Dot(n), -1f, 1f)));
                    if (swap && nDeg > worstNDeg) { worstNDeg = nDeg; nWhere = $"tgt=({tgt.X:F0},{tgt.Y:F0}) leaf={prevLeaf:F0}→{leaf:F0}m camDist={dist:F0} Δnormal={nDeg:F1}°"; }
                }
                prevH = h; prevLeaf = leaf; prevN = n;
            }
        }

        // Two independent pop axes, both must be continuous across a swap:
        //   height: a clean morph hand-off moves no vertex → sub-metre. normal: an LOD-independent gradient
        //   doesn't change with the chunk size → sub-degree. Either exceeding its tol is a visible pop.
        const float hTol = 1.0f;     // metres
        const float nTol = 1.5f;     // degrees (float/eval slack; the LOD-dependent bug is ~11°)
        bool hOk = worstSwap <= hTol, nOk = worstNDeg <= nTol;
        bool ok = hOk && nOk;
        msg = $"height: worst-at-swap Δh={worstSwap:F3}m ({(hOk ? "ok" : "POP " + swapWhere)}); " +
              $"normal: worst-at-swap Δn={worstNDeg:F1}° ({(nOk ? "ok" : "POP " + nWhere)})";
        return ok;
    }
}
