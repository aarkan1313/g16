namespace WG16.Hydrology;

/// Every tunable for the water system. Three tiers (sea/lakes/rivers) each toggleable; every shape + look value
/// here so nothing is hard-coded. Plain C# (shader uniforms are set from these in WaterRenderer).
public sealed class WaterParams
{
    // --- tier toggles ---
    public bool  SeaEnabled    = false;  // OFF by default: a global ocean doesn't fit a high mountain region;
                                         // the sea is a coastal/lowland feature — judge it at world scale, not
                                         // in the mountain lab. Lakes (tarns) + rivers carry mountain scenes.
    public bool  LakesEnabled  = true;
    public bool  RiversEnabled = true;

    // --- tier 1: sea (when enabled, e.g. lowland/coast scenes) ---
    public float SeaLevel      = -104f;  // world Y of the global sea — LOW so most land is dry (~12% flood in the
                                         // lab region; height p50≈+85, basins to -131).

    // --- lake BASIN carve (pillars: water sits IN a carved bowl with banks, not a flooding plane on raw slope) ---
    public float LakeBasinDepthM = 8f;   // how far to deepen terrain below the lake surface (the bowl floor)
    public float LakeBankBlendM  = 30f;  // smooth blend from bowl rim back to base terrain (natural banks)

    // --- tier 2: significant lakes (the "limited amounts" budget) ---
    public float LakeMinAreaM2     = 40000f; // min lake surface area (m²) to qualify (~200m across) — kills speckle
    public float LakeMinDepthM     = 4f;     // min spill depth (m) to qualify — kills shallow puddles
    public float LakeMinInflow     = 30f;    // min upstream drainage-area (coarse cell count) feeding the basin
    public float LakeDensityPerKm2 = 0.4f;   // cap: at most this many significant lakes per km² (rare/deliberate)

    // --- tier 3: thin rivers ---
    public float RiverMinArea  = 200f;   // min coarse drainage area for a reach to be a (carved) river
    public float ChannelWidthM = 12f;    // groove half-width (m) — thin
    public float ChannelDepthM = 6f;     // groove depth (m)
    public float ChannelBlendM = 20f;    // smooth blend distance from groove edge back to base terrain

    // --- look (shader uniforms; see water_surface.gdshader) ---
    public float ShallowDepthM = 10f;    // water depth that reaches full deep colour
    public bool  Reflections   = true;
    public bool  Foam          = true;
    public float FlowSpeed     = 0.15f;  // river normal-scroll speed
    public float WaveScale     = 1.0f;   // sea/lake ripple scale
}
