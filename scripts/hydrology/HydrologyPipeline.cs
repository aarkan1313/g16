using WG16.Field;

namespace WG16.Hydrology;

/// The Arc-1 drainage-substrate PRODUCER: base field -> coarse drainage graph -> analytic valley carve ->
/// optional stream-power erosion polish -> CarveResult (carved height + substrate: flow_accum, channel_mask,
/// water_level incl. lakes+rivers, sediment). Pure build orchestration — no scene/UI/render dependencies.
/// This is what the lab, the bake step, and the live game all call; keep it free of Godot-node concerns.
///
/// Reuses the race-free pipe-model ErosionSim ONLY as a gentle relaxation LAYER for polish (not as the macro
/// shape author — that's the structure-first carve). See memory clean-river-terrain-pipeline.
public sealed class HydrologyPipeline : System.IDisposable
{
    private readonly int _res;
    private readonly float _cell;
    private ValleyCarve _vc;

    public HydrologyPipeline(int res, float cell) { _res = res; _cell = cell; }

    /// Build the full substrate for the region centered on world origin (matches how the lab/base field is
    /// seeded). Caller supplies a FieldCompute (local-RD) for base-field sampling.
    public ValleyCarve.CarveResult Build(FieldParams p, FieldCompute fc, HydrologyParams hp)
    {
        hp.Res = _res; hp.CellSize = _cell;
        float bo = -_res * _cell * 0.5f;
        float[] baseH = fc.ProducePage(p, bo, bo, _cell, _res, 0);
        int cres = (int)((_res * _cell + 2 * hp.HaloMetres) / hp.CoarseSpacing);
        var cf = CoarseField.Build(fc, p, bo - hp.HaloMetres, bo - hp.HaloMetres, hp.CoarseSpacing, cres);
        var g = DrainageGraph.Build(cf, hp);
        _vc ??= new ValleyCarve(_res);
        var carve = _vc.Carve(baseH, g, hp);
        if (hp.PolishSteps > 0) { carve.Height = Polish(carve.Height, hp.PolishSteps); }
        SegmentCount = g.Segments.Count;
        LastGraph = g;
        return carve;
    }

    /// Last build's river-segment count (for HUD / logging).
    public int SegmentCount { get; private set; }

    /// The DrainageGraph from the last Build (consumed by WaterBodies for lakes/rivers). Null before first Build.
    public DrainageGraph LastGraph { get; private set; }

    /// Short gentle stream-power relaxation on the CARVED field: relaxes confluence seams / valley-width steps /
    /// carve shoulders into natural weathered form (research war52lnu6, the "make it natural" lever). Gentle
    /// params so it relaxes rather than re-authoring the macro valleys. Reuses ErosionSim as a one-shot layer.
    private float[] Polish(float[] carvedHeight, int steps)
    {
        var pp = new WG16.Erosion.ErosionParams
        {
            Res = _res, CellSize = _cell,
            Erode = 0.15f, MaxErode = 0.02f,   // gentle incision: relax, don't re-carve macro valleys
            Deposit = 0.6f,                     // fill the seams/steps it smooths
            Rain = 0.02f, Evaporate = 0.02f,
            TalusAngle = 0.6f, TalusRate = 0.4f,// thermal relaxes sharp carve shoulders into natural slopes
        };
        using var sim = new WG16.Erosion.ErosionSim(_res) { Params = pp };
        sim.Seed(carvedHeight);
        sim.Step(steps);
        return sim.ReadHeight();
    }

    public void Dispose() { _vc?.Dispose(); _vc = null; }
}
