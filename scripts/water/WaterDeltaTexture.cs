using Godot;

namespace WG16.Water;

// Packs a per-region rendered-height delta (Carved - rawField, metres, row-major) into a single-
// channel float texture the terrain shader samples at true world XZ. Bilinear filtering smooths the
// ~4 m grid into continuous displacement; the solver already feathered the region edges to 0.
public static class WaterDeltaTexture
{
    public static ImageTexture Build(float[] delta, int grid)
    {
        var bytes = new byte[delta.Length * 4];
        System.Buffer.BlockCopy(delta, 0, bytes, 0, bytes.Length);
        var img = Image.CreateFromData(grid, grid, false, Image.Format.Rf, bytes);
        return ImageTexture.CreateFromImage(img);
    }
}
