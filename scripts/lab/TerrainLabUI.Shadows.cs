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
}
