using Godot;
using System.Collections.Generic;
namespace WG16.Hydrology;

/// Owns ONE 8km region end-to-end: runs the coarse drainage + lake gating + spline trace + texture
/// bake, caches the result, and exposes the sampling API the carve + meshes use. Deterministic from
/// (seed, regionCell) → infinite-safe and tile-coherent (seams agree via the shared coarse+halo solve).
public sealed class WorldWaterRegion
{
    public ImageTexture Texture { get; }
    public List<RiverReach> Rivers { get; }
    public List<Lake> Lakes { get; }
    public float RegionM { get; }
    public Vector2 RegionOriginWorld { get; }

    private readonly Image _img;
    private readonly CoarseDrainage _drainage;   // proxy fallback when no real-field grid is available
    private readonly RegionHeightGrid? _real;    // REAL field height grid (when a FieldCompute is supplied)

    /// fc is OPTIONAL: when supplied (the live --water path), water meshes drape on the EXACT field height
    /// the terrain uses (no floating). When null (cheap self-checks), the proxy height is used as a fallback.
    public WorldWaterRegion(WG16.Field.FieldParams fp, WaterParams wp, long regionX, long regionZ,
                            WG16.Field.FieldCompute? fc = null)
    {
        RegionM = fp.RegionSizeM;
        RegionOriginWorld = new Vector2(regionX * RegionM, regionZ * RegionM);
        _drainage = new CoarseDrainage(fp, wp, regionX, regionZ);
        var t = new WaterTable(wp, fp.Seed);
        Rivers = RiverTracer.Trace(_drainage, wp);
        Lakes = LakeGating.GatedLakes(_drainage, t, wp, RegionM);
        _img = WaterTextureBaker.Bake(Rivers, Lakes, wp, RegionOriginWorld, RegionM);
        Texture = ImageTexture.CreateFromImage(_img);

        if (fc != null)
        {
            // Real-field height grid over region + halo (so splines exiting the core still drape correctly).
            // Resolution gives ~16 m cells — fine for draping a river ribbon; one GPU dispatch per region.
            float span = RegionM * (1 + 2 * wp.HaloRegions);
            float originX = RegionOriginWorld.X - wp.HaloRegions * RegionM;
            float originZ = RegionOriginWorld.Y - wp.HaloRegions * RegionM;
            int res = Mathf.Clamp((int)(span / 16f), 64, 1024);
            _real = new RegionHeightGrid(fc, fp, originX, originZ, span, res);
        }
    }

    private Color Sample(float wx, float wz)
    {
        float u = (wx - RegionOriginWorld.X) / RegionM;
        float v = (wz - RegionOriginWorld.Y) / RegionM;
        int px = Mathf.Clamp((int)(u * _img.GetWidth()), 0, _img.GetWidth() - 1);
        int py = Mathf.Clamp((int)(v * _img.GetHeight()), 0, _img.GetHeight() - 1);
        return _img.GetPixel(px, py);
    }

    public bool IsWet(float wx, float wz) => Sample(wx, wz).A > 0.5f;
    public float BedAt(float wx, float wz) => Sample(wx, wz).G;

    /// REAL terrain height for draping water meshes — the EXACT field the terrain renders (no floating)
    /// when a FieldCompute was supplied; the cheap proxy otherwise (self-checks only).
    public float SurfaceYAt(float wx, float wz) =>
        _real != null ? _real.HeightAt(wx, wz) : _drainage.ProxyHeight(wx, wz);
}
