using System;

namespace WG16.Hydrology;

/// Tunable knobs for drainage synthesis + valley carving. std430-packed for the carve shader's Params block.
/// Every value is live-tunable in the lab; nothing about shape is hard-coded elsewhere.
public sealed class HydrologyParams
{
    // --- coarse drainage graph (CPU; NOT packed for the shader) ---
    public float CoarseSpacing = 64f;   // metres between coarse nodes (km-scale network at low cost)
    public float HaloMetres    = 2048f; // region halo so cross-chunk rivers resolve identically (>= max segment reach)
    public float ChannelMinArea = 8f;   // min upstream cell-count for a coarse cell to seed a channel/segment
    public int   TributarySteps = 0;    // extra headward-growth passes (0 = steepest-descent only; raised in tuning)
    public float MfdExp        = 3.5f;  // multiple-flow-direction slope exponent (≈3.5 ≈ rotationally symmetric;
                                        // higher = more channelized/D8-like, lower = more dispersed/hillslope-like)
    public int   CarveMinOrder  = 2;    // min Strahler order a reach must have to CARVE a visible valley (lower-
                                        // order rivulets still feed the substrate but don't gouge hillslopes →
                                        // kills the herringbone of hundreds of tiny parallel tributary valleys)

    // --- valley carve (GPU; packed below) ---
    public int   Res        = 512;      // high-res carve grid per side
    public float CellSize   = 8f;       // metres per high-res cell
    public float CarveStrength = 1.0f;  // GLOBAL valley depth multiplier; 0 => base field returned untouched
    public float DepthPerOrder = 12f;   // metres of incision added per Strahler order at the channel line
    public float WidthPerOrder = 40f;   // valley half-width (m) added per Strahler order (smooth falloff radius)
    public float BankSediment  = 0.3f;  // sediment value written within the valley (placeholder for surfacing)
    public float LakeMinDepth  = 3f;     // min fill-depth (m) for a coarse cell to count as a lake (else dry flat)
    public int   PolishSteps   = 40;     // stream-power erosion-relaxation steps on the CARVED field (0 = off).
                                         // The decisive "make it natural" lever (Schott 2023): relax confluence
                                         // seams / valley-width steps / basin flats into natural form. A LAYER on
                                         // the good macro structure, NOT the shaping authority.

    // std430 for valley_carve.glsl Params: slot0 res(int bits), slots1..6 floats, 7..15 pad -> 16 scalars=64B.
    // (segment data is a SEPARATE buffer, not in Params.)
    public byte[] Pack()
    {
        var f = new float[16];
        f[1] = CellSize; f[2] = CarveStrength; f[3] = DepthPerOrder;
        f[4] = WidthPerOrder; f[5] = BankSediment; f[6] = CarveMinOrder;   // compared as float in shader
        // 7..15 pad/reserved
        var bytes = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        BitConverter.GetBytes(Res).CopyTo(bytes, 0);   // slot 0 read as int res in GLSL
        return bytes;
    }
}
