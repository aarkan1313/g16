using System;
using System.Collections.Generic;
namespace Erosion.Core;

public sealed class HeightField
{
    public int Width { get; }
    public int Height { get; }
    public float CellSizeM { get; }
    public float[] Data { get; }

    public HeightField(int width, int height, float cellSizeM)
    {
        Width = width; Height = height; CellSizeM = cellSizeM;
        Data = new float[width * height];
    }

    public float Get(int x, int y) => Data[y * Width + x];
    public void Set(int x, int y, float v) => Data[y * Width + x] = v;

    public HeightField Clone()
    {
        var c = new HeightField(Width, Height, CellSizeM);
        Array.Copy(Data, c.Data, Data.Length);
        return c;
    }

    public float Sample(float fx, float fy)
    {
        fx = Math.Clamp(fx, 0f, Width - 1.0001f);
        fy = Math.Clamp(fy, 0f, Height - 1.0001f);
        int x0 = (int)fx, y0 = (int)fy;
        int x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float tx = fx - x0, ty = fy - y0;
        float top = Get(x0, y0) * (1 - tx) + Get(x1, y0) * tx;
        float bot = Get(x0, y1) * (1 - tx) + Get(x1, y1) * tx;
        return top * (1 - ty) + bot * ty;
    }
}
