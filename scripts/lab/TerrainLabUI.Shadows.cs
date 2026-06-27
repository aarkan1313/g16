using Godot;

namespace WG16.Lab;

public partial class TerrainLabUI
{
    private ShadowRegistry _shadowRegistry = null!;

    /// Phase 0: construct the registry + register the existing horizon march (FarCast). Later phases
    /// register NearCast (CSM) and ContactAo owners here. Called from _Ready after _terrain is resolved.
    private void InitShadows()
    {
        _shadowRegistry = new ShadowRegistry(this);
        _shadowRegistry.Register(new HorizonMarchOwner(_terrain));
    }

    private void TickShadows(double delta) => _shadowRegistry?.Tick(delta);

    private bool _shadowCheckRan;

    /// Runs once, after the scene is up and the registry exists, when --shadowcheck was passed. Exits the
    /// process 0 (pass) / 1 (fail) so CI can gate. Runs in _Process (not _Ready) so the first frame's
    /// owners/diagnostics are real.
    private void RunShadowCheckIfRequested()
    {
        if (!_shadowCheckCli || _shadowCheckRan || !_ready || _shadowRegistry == null) { return; }
        _shadowCheckRan = true;
        GetTree().Quit(ShadowCheck.Run(_shadowRegistry, this) ? 0 : 1);
    }
}
