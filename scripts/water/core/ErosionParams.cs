using System;
using System.Collections.Generic;
namespace Erosion.Core;

public sealed class ErosionParams
{
    public int DropletCount = 200_000;
    public int MaxLifetime = 64;
    public float Inertia = 0.05f;          // 0=pure gradient, 1=keeps direction
    public float CapacityFactor = 4f;      // sediment capacity scale
    public float MinSlope = 0.01f;         // floor so flats still carry a little
    public float ErosionRate = 0.3f;       // fraction of (capacity-sediment) cut per step
    public float DepositionRate = 0.3f;    // fraction of excess deposited per step
    public float Evaporation = 0.02f;      // water lost per step
    public float Gravity = 4f;
    public float InitialWater = 1f;
    public float InitialSpeed = 1f;
    public int ErosionRadius = 3;          // brush radius (cells) — spreads the cut, kills single-cell pits
    public int Seed = 1;

    // thermal/talus companion (runs after the droplet pass) — fills pit rims, kills holes
    public float TalusAngleDeg = 38f;      // repose angle; only steeper slopes slump
    public float ThermalStrength = 0.5f;
    public int ThermalIterations = 50;
}
