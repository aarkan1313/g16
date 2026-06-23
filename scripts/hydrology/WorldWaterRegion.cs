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
    private readonly CoarseDrainage _drainage;   // kept for SurfaceYAt (proxy terrain to drape meshes on)

    public WorldWaterRegion(WG16.Field.FieldParams fp, WaterParams wp, long regionX, long regionZ)
    {
        RegionM = fp.RegionSizeM;
        RegionOriginWorld = new Vector2(regionX * RegionM, regionZ * RegionM);
        _drainage = new CoarseDrainage(fp, wp, regionX, regionZ);
        var t = new WaterTable(wp, fp.Seed);
        Rivers = RiverTracer.Trace(_drainage, wp);
        Lakes = LakeGating.GatedLakes(_drainage, t, wp, RegionM);
        _img = WaterTextureBaker.Bake(Rivers, Lakes, wp, RegionOriginWorld, RegionM);
        Texture = ImageTexture.CreateFromImage(_img);
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

    /// Proxy terrain height (the macro family the carve rides on) — used to drape water meshes into the
    /// carved groove. Cheap; matches the drainage proxy, not the full field, which is fine for the mesh Y.
    public float SurfaceYAt(float wx, float wz) => _drainage.ProxyHeight(wx, wz);
}
