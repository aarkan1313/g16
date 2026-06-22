using Godot;
using WG16.Field;

namespace WG16.Lab;

/// LIVE pop meter (--popmeter): turns YOUR free-flight into the measurement. Each frame it samples, at a fixed
/// world point a little ahead of the camera, the EXACT rendered height + normal the chunk shader draws (morphed
/// sample XZ → real GPU field; fixed-step central-diff normal), and watches for:
///   • renderOrigin SNAP  — the floating-origin reframe (every ~root-size of travel); a one-time event that, if
///     the reconstruction is imperfect, shows as the "jump on top of me". Logged with the before/after height.
///   • HEIGHT step         — an inter-frame rendered-height jump bigger than smooth motion would give (a pop).
///   • NORMAL step         — an inter-frame shaded-normal jump (the shading flicker / contour terracing).
/// Prints to the console AND an on-screen HUD line so you see it live as you fly. GPU eval is ~0.1 ms/frame
/// (1×1 pages) — fine for a debug meter. Windowed only (FieldCompute needs a RenderingDevice).
public sealed class LivePopMeter
{
    private readonly FieldCompute _fc;
    private readonly FieldParams _p;
    private readonly CdlodTerrain _terrain;
    private readonly int _gridN;
    private readonly float _split;

    private float _prevH = float.NaN;
    private Vector3 _prevN = Vector3.Zero;
    private Vector3 _prevOrigin = new(float.NaN, 0f, float.NaN);
    private Vector2 _prevSample;
    private float _prevLeaf = -1f;
    private Vector2 _prevCam;
    public string Hud = "";
    public string LastEvent = "—";
    private float _worstH, _worstN;
    private int _tick;

    public LivePopMeter(FieldCompute fc, FieldParams p, CdlodTerrain terrain, int gridN, float split)
    { _fc = fc; _p = p; _terrain = terrain; _gridN = gridN; _split = split; }

    private static float SnapEven(float g) => Mathf.Floor(g * 0.5f + 0.5f) * 2.0f;

    private float MorphK(float d, float size)
    {
        float nearD = size * _split, farD = 2f * size * _split, midD = (nearD + farD) * 0.5f;
        return Mathf.Clamp((d - midD) / Mathf.Max(farD - midD, 1e-3f), 0f, 1f);
    }

    // Morphed sample XZ for a vertex nearest P in the leaf, mirroring ground.gdshader.
    private Vector2 SampleXZ(Vector2 p, float leaf, Vector2 cam)
    {
        Vector2 origin = new Vector2(Mathf.Floor(p.X / leaf) * leaf, Mathf.Floor(p.Y / leaf) * leaf);
        float gm1 = _gridN - 1f;
        Vector2 u = (p - origin) / leaf;
        float gi = Mathf.Round(Mathf.Clamp(u.X, 0f, 1f) * gm1), gj = Mathf.Round(Mathf.Clamp(u.Y, 0f, 1f) * gm1);
        Vector2 uFine = new Vector2(gi / gm1, gj / gm1);
        Vector2 uCoarse = new Vector2(SnapEven(gi) / gm1, SnapEven(gj) / gm1);
        float k = MorphK((cam - (origin + uFine * leaf)).Length(), leaf);
        return origin + uFine.Lerp(uCoarse, k) * leaf;
    }

    private float H(Vector2 xz) { var a = _fc.ProducePage(_p, xz.X, xz.Y, 1f, 1, 0); return a.Length > 0 ? a[0] : 0f; }

    // One vertex's MORPHED world height: morph the vertex toward its coarse target (the shader does this),
    // then sample field_height there + assign to the vertex. (gi,gj) are integer fine-grid indices.
    private float VertH(int gi, int gj, Vector2 origin, float leaf, float gm1, Vector2 cam)
    {
        Vector2 uFine = new Vector2(gi / gm1, gj / gm1);
        Vector2 uCoarse = new Vector2(SnapEven(gi) / gm1, SnapEven(gj) / gm1);
        float k = MorphK((cam - (origin + uFine * leaf)).Length(), leaf);
        Vector2 sampleXz = origin + uFine.Lerp(uCoarse, k) * leaf;
        return H(sampleXz);
    }

    // THE ACTUAL RASTERIZED HEIGHT at world point P: the GPU draws each grid cell as triangles whose corner
    // heights are the morphed-vertex heights, and interpolates LINEARLY across them. So the surface at P is the
    // bilinear blend of P's 4 surrounding morphed-vertex heights — NOT field_height(P). On a curved hill the
    // coarse cell's linear chord sits BELOW the field; the finer LOD's chord hugs it → the rendered height at P
    // JUMPS at the swap (the "existing terrain changes shape" pop) even though the field is continuous. This
    // models that. 4 GPU evals/point.
    private float RenderedHeight(Vector2 p, float leaf, Vector2 cam)
    {
        Vector2 origin = new Vector2(Mathf.Floor(p.X / leaf) * leaf, Mathf.Floor(p.Y / leaf) * leaf);
        float gm1 = _gridN - 1f;
        Vector2 u = (p - origin) / leaf;                       // [0,1] in the leaf
        float fx = Mathf.Clamp(u.X, 0f, 1f) * gm1, fz = Mathf.Clamp(u.Y, 0f, 1f) * gm1;
        int i0 = Mathf.Clamp((int)Mathf.Floor(fx), 0, (int)gm1 - 1), j0 = Mathf.Clamp((int)Mathf.Floor(fz), 0, (int)gm1 - 1);
        float tx = fx - i0, tz = fz - j0;
        float h00 = VertH(i0, j0, origin, leaf, gm1, cam);
        float h10 = VertH(i0 + 1, j0, origin, leaf, gm1, cam);
        float h01 = VertH(i0, j0 + 1, origin, leaf, gm1, cam);
        float h11 = VertH(i0 + 1, j0 + 1, origin, leaf, gm1, cam);
        return Mathf.Lerp(Mathf.Lerp(h00, h10, tx), Mathf.Lerp(h01, h11, tx), tz);
    }
    private Vector3 N(Vector2 xz, float ns)
    {
        float hxp = H(xz + new Vector2(ns, 0f)), hxm = H(xz - new Vector2(ns, 0f));
        float hzp = H(xz + new Vector2(0f, ns)), hzm = H(xz - new Vector2(0f, ns));
        return new Vector3(hxm - hxp, 2f * ns, hzm - hzp).Normalized();
    }

    // A PERSISTENT fixed-world anchor. Seeded once on a world lattice; its Xz NEVER moves while live. Each
    // frame we recompute the height the renderer draws AT that fixed point and compare to last frame. A change
    // at a motionless world point = a true LOD/morph pop. Re-seeded only when it falls outside the view range.
    private struct Anchor { public Vector2 Xz; public float H; public float Leaf; public bool Live; }
    private Anchor[] _anchors = new Anchor[64];   // dense world grid around the camera

    /// Each frame: keep a grid of FIXED world points within ~3 km of the camera; recompute each one's rendered
    /// height; flag any whose height jumps vs last frame (a pop). Re-seed a cell only when the camera has moved
    /// so the previous point left the tracking radius. ~64 GPU evals/frame (1×1 pages, ~a few ms — debug only).
    public void Tick(Vector3 camPos)
    {
        var qt = new CdlodQuadtree(-_p.RegionSizeM * 0.5f, -_p.RegionSizeM * 0.5f, _p.RegionSizeM, 6, _split);
        Vector3 origin = _terrain.RenderOrigin;
        bool originSnapped = !float.IsNaN(_prevOrigin.X) && (origin.X != _prevOrigin.X || origin.Z != _prevOrigin.Z);
        var cam2 = new Vector2(camPos.X, camPos.Z);

        float worstDh = 0f; string where = "none"; int popHits = 0, swaps = 0;
        for (int i = 0; i < _anchors.Length; i++)
        {
            Anchor a = _anchors[i];
            // (re)seed this slot if empty or its fixed point has fallen too far behind (>3.5 km) the camera.
            if (!a.Live || (a.Xz - cam2).Length() > 3500f)
            {
                // 8×8 world lattice spanning ±~3 km around the camera, snapped to a fixed 768 m world grid so a
                // re-seeded point lands on a STABLE world location (not camera-relative jitter).
                int gx = i % 8 - 4, gz = i / 8 - 4;
                Vector2 raw = cam2 + new Vector2(gx, gz) * 768f;
                Vector2 xz = new Vector2(Mathf.Round(raw.X / 768f) * 768f, Mathf.Round(raw.Y / 768f) * 768f);
                float leaf0 = qt.LeafSizeAt(xz.X, xz.Y, camPos);
                _anchors[i] = new Anchor { Xz = xz, H = RenderedHeight(xz, leaf0, cam2), Leaf = leaf0, Live = true };
                continue;   // just seeded — nothing to compare this frame
            }
            // FIXED point (a.Xz unchanged): recompute its rendered height this frame.
            float leaf = qt.LeafSizeAt(a.Xz.X, a.Xz.Y, camPos);
            float h = RenderedHeight(a.Xz, leaf, cam2);
            if (!originSnapped)
            {
                float dh = Mathf.Abs(h - a.H);    // motionless world point's rendered-height change = pure pop
                if (a.Leaf != leaf) { swaps++; }
                if (dh > worstDh) { worstDh = dh; where = $"world=({a.Xz.X:F0},{a.Xz.Y:F0}) dist={(a.Xz - cam2).Length():F0}m leaf {a.Leaf:F0}→{leaf:F0}m"; }
                if (dh > 0.5f) { popHits++; }
            }
            _anchors[i] = new Anchor { Xz = a.Xz, H = h, Leaf = leaf, Live = true };
        }

        if (worstDh > _worstH) { _worstH = worstDh; }
        string evt = null;
        if (originSnapped) { evt = $"renderOrigin SNAP ({_prevOrigin.X:F0},{_prevOrigin.Z:F0})→({origin.X:F0},{origin.Z:F0})"; for (int i = 0; i < _anchors.Length; i++) { _anchors[i].Live = false; } }
        else if (worstDh > 0.5f) { evt = $"POP Δ{worstDh:F2}m at {where}  ({popHits} fixed pts popped, {swaps} LOD swaps this frame)"; }
        if (evt != null) { LastEvent = evt; GD.Print($"[popmeter] {evt}"); }
        if (++_tick % 120 == 0) { GD.Print($"[popmeter] alive: origin=({origin.X:F0},{origin.Z:F0}) worstΔh={_worstH:F2}m"); }

        Hud = $"POPMETER (fixed-pt)  worstΔh={_worstH:F2}m  origin=({origin.X:F0},{origin.Z:F0})\nlast: {LastEvent}";
        _prevOrigin = origin;
    }
}
