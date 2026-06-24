using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// CDLOD quadtree over stable world-XZ. Each frame, Select() walks from the root
/// square and returns leaf chunks (large-far / small-near) by camera distance.
/// Pure logic — no rendering. Level 0 = finest (smallest chunks).
public struct CdlodChunk
{
    public Vector2 OriginXZ;   // world-XZ min corner
    public float Size;         // side length (m)
    public int Level;          // 0 = finest
    public int StitchMask;     // S2d: bit0=-X bit1=+X bit2=-Z bit3=+Z set iff that edge faces a COARSER neighbor
}

public sealed class CdlodQuadtree
{
    private readonly float _rootX, _rootZ, _rootSize;
    private readonly int _maxDepth;
    private readonly float _splitFactor;   // subdivide when camDist < size*splitFactor

    public CdlodQuadtree(float rootOriginX, float rootOriginZ, float rootSize, int maxDepth, float splitFactor)
    {
        _rootX = rootOriginX; _rootZ = rootOriginZ; _rootSize = rootSize;
        _maxDepth = Mathf.Max(0, maxDepth); _splitFactor = Mathf.Max(0.01f, splitFactor);
    }

    public List<CdlodChunk> Select(Vector3 camPos)
    {
        var leaves = new List<CdlodChunk>();
        Recurse(_rootX, _rootZ, _rootSize, 0, camPos, leaves);
        FillStitchMasks(leaves, camPos);
        return leaves;
    }

    /// Fill each leaf's 4-bit edge-stitch mask (bit0=-X bit1=+X bit2=-Z bit3=+Z): a bit is set iff the
    /// neighbor across that edge midpoint is COARSER. Shared by Select + SelectRoaming.
    private void FillStitchMasks(List<CdlodChunk> leaves, Vector3 camPos)
    {
        const float eps = 0.25f;   // probe inset (m): < smallest chunk (128 m), safely inside the neighbor
        for (int i = 0; i < leaves.Count; i++)
        {
            CdlodChunk c = leaves[i];
            float ox = c.OriginXZ.X, oz = c.OriginXZ.Y, s = c.Size, hs = s * 0.5f;
            int mask = 0;
            if (LeafSizeAt(ox - eps,     oz + hs,      camPos) > s) { mask |= 1; }   // bit0 -X
            if (LeafSizeAt(ox + s + eps, oz + hs,      camPos) > s) { mask |= 2; }   // bit1 +X
            if (LeafSizeAt(ox + hs,      oz - eps,     camPos) > s) { mask |= 4; }   // bit2 -Z
            if (LeafSizeAt(ox + hs,      oz + s + eps, camPos) > s) { mask |= 8; }   // bit3 +Z
            c.StitchMask = mask;
            leaves[i] = c;   // struct — write back
        }
    }

    /// S3: select leaves over a 3x3 block of root-size cells centered on the camera's root cell (cell-aligned
    /// so a world point always falls in the same chunk → no shimmer). Root roams with the camera → infinite.
    public List<CdlodChunk> SelectRoaming(Vector3 camPos)
    {
        float cx = Mathf.Floor(camPos.X / _rootSize) * _rootSize;   // camera's root-cell origin
        float cz = Mathf.Floor(camPos.Z / _rootSize) * _rootSize;
        var leaves = new List<CdlodChunk>();
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)   // 3x3 cells → camera never near a window edge
        {
            Recurse(cx + dx * _rootSize, cz + dz * _rootSize, _rootSize, 0, camPos, leaves);
        }
        FillStitchMasks(leaves, camPos);
        return leaves;
    }

    /// Size (m) of the leaf that would contain world point (px,pz) under the current split rule, or 0 if
    /// the point is outside the root region. Walks the tree like Recurse but follows only the child that
    /// contains the point — O(depth). Used by S2d stitch-mask resolution (a coarser neighbor = bigger size).
    public float LeafSizeAt(float px, float pz, Vector3 camForSplit)
    {
        // S3: root the walk at the root-cell containing (px,pz) (the world is infinite now — any probe has a
        // containing cell). MUST use the SAME cell grid as SelectRoaming (floor to _rootSize) so neighbor
        // lookups across a cell seam are correct.
        float x = Mathf.Floor(px / _rootSize) * _rootSize;
        float z = Mathf.Floor(pz / _rootSize) * _rootSize;
        float size = _rootSize; int depth = 0;
        while (depth < _maxDepth)
        {
            float d = DistanceToCellXZ(x, z, size, camForSplit);
            if (!(d < size * _splitFactor)) { break; }   // this cell is a leaf (not split) — stop
            float h = size * 0.5f;
            if (px >= x + h) { x += h; }                 // pick the child quadrant containing the point
            if (pz >= z + h) { z += h; }
            size = h; depth++;
        }
        return size;
    }

    private void Recurse(float x, float z, float size, int depth, Vector3 cam, List<CdlodChunk> outLeaves)
    {
        bool canSplit = depth < _maxDepth;
        float d = DistanceToCellXZ(x, z, size, cam);
        if (canSplit && d < size * _splitFactor)
        {
            float h = size * 0.5f;
            Recurse(x,     z,     h, depth + 1, cam, outLeaves);
            Recurse(x + h, z,     h, depth + 1, cam, outLeaves);
            Recurse(x,     z + h, h, depth + 1, cam, outLeaves);
            Recurse(x + h, z + h, h, depth + 1, cam, outLeaves);
        }
        else
        {
            outLeaves.Add(new CdlodChunk { OriginXZ = new Vector2(x, z), Size = size, Level = _maxDepth - depth });
        }
    }

    /// Nearest-point XZ distance from the camera to a cell (ignores Y so altitude doesn't starve LOD).
    private static float DistanceToCellXZ(float x, float z, float size, Vector3 cam)
    {
        float cx = Mathf.Clamp(cam.X, x, x + size);
        float cz = Mathf.Clamp(cam.Z, z, z + size);
        float dx = cam.X - cx, dz = cam.Z - cz;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    /// <=1-level neighbor invariant: any two EDGE-ADJACENT leaves differ by <=1 level.
    /// Conservative O(n^2) check (fine for a self-check; the runtime build relies on the
    /// restricted-quadtree property, but this verifies it empirically).
    public bool NeighborInvariantHolds(List<CdlodChunk> leaves, out string msg)
    {
        for (int i = 0; i < leaves.Count; i++)
        for (int j = i + 1; j < leaves.Count; j++)
        {
            if (EdgeAdjacent(leaves[i], leaves[j]) && Mathf.Abs(leaves[i].Level - leaves[j].Level) > 1)
            {
                msg = $"levels {leaves[i].Level} vs {leaves[j].Level} adjacent at " +
                      $"({leaves[i].OriginXZ}) / ({leaves[j].OriginXZ})";
                return false;
            }
        }
        msg = "ok";
        return true;
    }

    private static bool EdgeAdjacent(CdlodChunk a, CdlodChunk b)
    {
        float ax0 = a.OriginXZ.X, ax1 = ax0 + a.Size, az0 = a.OriginXZ.Y, az1 = az0 + a.Size;
        float bx0 = b.OriginXZ.X, bx1 = bx0 + b.Size, bz0 = b.OriginXZ.Y, bz1 = bz0 + b.Size;
        const float e = 0.5f;
        bool xTouch = Mathf.Abs(ax1 - bx0) < e || Mathf.Abs(bx1 - ax0) < e;
        bool zTouch = Mathf.Abs(az1 - bz0) < e || Mathf.Abs(bz1 - az0) < e;
        bool zOverlap = az0 < bz1 - e && bz0 < az1 - e;
        bool xOverlap = ax0 < bx1 - e && bx0 < ax1 - e;
        return (xTouch && zOverlap) || (zTouch && xOverlap);
    }

    public static bool SelfCheck(float regionSize, int maxDepth, float splitFactor, Vector3 camPos)
    {
        var qt = new CdlodQuadtree(-regionSize * 0.5f, -regionSize * 0.5f, regionSize, maxDepth, splitFactor);
        var leaves = qt.Select(camPos);
        bool inv = qt.NeighborInvariantHolds(leaves, out string msg);
        int minL = int.MaxValue, maxL = int.MinValue;
        foreach (var c in leaves) { minL = Mathf.Min(minL, c.Level); maxL = Mathf.Max(maxL, c.Level); }
        GD.Print($"CDLODCHECK: {(inv ? "PASS" : "FAIL")}  leaves={leaves.Count}  levels={minL}..{maxL}  invariant={msg}  cam=({camPos.X:F0},{camPos.Z:F0})");
        int stitched = 0; foreach (var c in leaves) { if (c.StitchMask != 0) { stitched++; } }   // S2d
        GD.Print($"CDLODCHECK: stitch — {stitched}/{leaves.Count} leaves have >=1 stitched edge");
        return inv;
    }
}
