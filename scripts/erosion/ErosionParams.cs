using Godot;
using System;

namespace WG16.Erosion;

/// Tunable knobs for the pipe-model hydraulic sim. std430-packed for the params SSBO.
public sealed class ErosionParams
{
    public int   Res        = 512;     // grid cells per side
    public float CellSize   = 8.0f;    // metres per cell (region = Res*CellSize)
    public float Dt         = 0.02f;   // timestep (stability-bounded)
    public float Rain       = 0.012f;  // water added per step
    public float Evaporate  = 0.015f;  // water decay per step (drives convergence)
    public float Gravity    = 9.81f;   // flux acceleration
    public float Capacity   = 0.6f;    // sediment capacity constant
    public float Erode      = 0.5f;    // bedrock->suspension rate
    public float Deposit    = 0.5f;    // suspension->bedrock rate
    public float MaxErode   = 0.05f;   // hard per-step incision cap (m) — anti-overshoot
    public float TalusAngle = 0.7f;    // tan(rest angle); slope above this slumps
    public float TalusRate  = 0.3f;    // thermal slump fraction per step
    public float MinTilt    = 0.001f;  // floor on slope used in capacity (avoids 0-capacity flats)

    // --- drainage / stream-power (the river fix) ---
    public float StreamM    = 0.5f;    // stream-power area exponent m in E ∝ A^m·S^n (~0.5 classic)
    public float StreamN    = 1.0f;    // stream-power slope exponent n
    public float AccumRate  = 0.25f;   // flow-accumulation relaxation per step (stability as h shifts)
    public float ChannelThreshold = 80f; // upstream-area A above which a cell counts as a channel

    // std430: slot 0 = res (int bits), slots 1..16 floats, 17..19 pad → 20 scalars = 80 bytes.
    public byte[] Pack()
    {
        var f = new float[20];
        f[1] = CellSize; f[2] = Dt; f[3] = Rain;
        f[4] = Evaporate; f[5] = Gravity; f[6] = Capacity; f[7] = Erode;
        f[8] = Deposit; f[9] = MaxErode; f[10] = TalusAngle; f[11] = TalusRate;
        f[12] = MinTilt; f[13] = StreamM; f[14] = StreamN; f[15] = AccumRate;
        f[16] = ChannelThreshold; // 17-19 pad (0)
        var bytes = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        BitConverter.GetBytes(Res).CopyTo(bytes, 0);   // slot 0 read as `int res` in GLSL
        return bytes;
    }
}
