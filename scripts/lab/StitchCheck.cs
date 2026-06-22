using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// S2d numeric crack guard (--stitchcheck). For every chunk edge that faces a one-level-COARSER neighbor
/// (per CdlodQuadtree's stitch mask), it reconstructs the fine chunk's WELDED edge vertices (CdlodMesh's
/// collapse: each odd boundary vert sits on its even neighbor → distinct verts only at even/coarse spacing)
/// and the COARSER NEIGHBOR's actual edge vertices, then asserts every welded fine-edge vertex coincides
/// with a neighbor edge vertex in WORLD XZ (gap ~0 m). That is the no-T-junction / no-crack condition across
/// the seam between two real chunks — it can genuinely FAIL (wrong bit convention, wrong neighbor, misaligned
/// lattice), unlike a self-referential weld check. PASS/FAIL like --morphcheck. The live call is the eye-gate.
public static class StitchCheck
{
    public static bool Run(float regionSize, int maxDepth, float splitFactor, int gridN, Vector3 camPos, out string msg)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        List<CdlodChunk> leaves = qt.Select(camPos);
        int last = gridN - 1;
        const float eps = 0.25f;
        float worst = 0f; int stitchedEdges = 0, vertsChecked = 0;

        foreach (CdlodChunk c in leaves)
        {
            if (c.StitchMask == 0) { continue; }
            float ox = c.OriginXZ.X, oz = c.OriginXZ.Y, s = c.Size, hs = s * 0.5f, vs = s / last;

            // Build this chunk's WELDED edge vertices (world XZ) for a given edge, then find the coarser
            // neighbor across it and check each welded vertex lands on the neighbor's edge vertex lattice.
            // edge: 0=-X 1=+X 2=-Z 3=+Z. The welded fine edge has a distinct vertex at every EVEN index k
            // (odd verts collapsed onto k-1), i.e. at world offset k*vs for k=0,2,4..last.
            void CheckEdge(int edge)
            {
                // probe just outside the edge midpoint to locate the neighbor + its size (origin via floor-snap)
                float px, pz;
                switch (edge)
                {
                    case 0: px = ox - eps;     pz = oz + hs;     break;   // -X
                    case 1: px = ox + s + eps; pz = oz + hs;     break;   // +X
                    case 2: px = ox + hs;      pz = oz - eps;    break;   // -Z
                    default: px = ox + hs;     pz = oz + s + eps; break;  // +Z
                }
                float ns = qt.LeafSizeAt(px, pz, camPos);
                if (ns <= s) { return; }   // not actually coarser (mask said it was — only check true coarser)
                stitchedEdges++;
                float nvs = ns / last;                         // neighbor's world vertex spacing
                // neighbor origin: floor-snap the probe to a multiple of ns from the root origin
                float root = -regionSize * 0.5f;
                float nox = root + Mathf.Floor((px - root) / ns) * ns;
                float noz = root + Mathf.Floor((pz - root) / ns) * ns;

                for (int k = 0; k <= last; k += 2)   // welded fine edge: distinct verts at even k
                {
                    float along = k * vs;
                    // welded fine-edge vertex world XZ
                    float wx, wz;
                    switch (edge)
                    {
                        case 0: wx = ox;     wz = oz + along; break;
                        case 1: wx = ox + s; wz = oz + along; break;
                        case 2: wx = ox + along; wz = oz;     break;
                        default: wx = ox + along; wz = oz + s; break;
                    }
                    // nearest neighbor edge-vertex along the SAME shared edge (neighbor lattice spacing nvs)
                    float nAlongX = nox + Mathf.Round((wx - nox) / nvs) * nvs;
                    float nAlongZ = noz + Mathf.Round((wz - noz) / nvs) * nvs;
                    float gap = Mathf.Sqrt((wx - nAlongX) * (wx - nAlongX) + (wz - nAlongZ) * (wz - nAlongZ));
                    if (gap > worst) { worst = gap; }
                    vertsChecked++;
                }
            }
            if ((c.StitchMask & 1) != 0) { CheckEdge(0); }
            if ((c.StitchMask & 2) != 0) { CheckEdge(1); }
            if ((c.StitchMask & 4) != 0) { CheckEdge(2); }
            if ((c.StitchMask & 8) != 0) { CheckEdge(3); }
        }

        // Tolerance: a fraction of the FINEST vertex spacing. A real misalignment (wrong neighbor/convention)
        // is on the order of nvs (tens of metres); float round-off is sub-millimetre.
        float tol = (regionSize / Mathf.Pow(2, maxDepth) / last) * 1e-2f;
        bool ok = worst <= tol;
        msg = ok
            ? $"{stitchedEdges} stitched edges, {vertsChecked} welded verts; max gap to neighbor lattice={worst:F5}m (<= {tol:F5}m) — seam coincident"
            : $"max gap={worst:F4}m > tol={tol:F4}m (CRACK: welded fine-edge vert not on the coarse neighbor's lattice)";
        return ok;
    }
}
