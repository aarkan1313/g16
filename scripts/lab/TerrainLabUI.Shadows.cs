using Godot;

namespace WG16.Lab;

public partial class TerrainLabUI
{
    private ShadowRegistry _shadowRegistry = null!;

    /// ILightingHost: the shadow registry owns whether the sun casts (read by LightingComposer.Compose).
    public bool WantsSunShadow => _shadowRegistry?.WantsSunShadow ?? false;

    /// Phase 0: construct the registry + register the existing horizon march (FarCast). Later phases
    /// register NearCast (CSM) and ContactAo owners here. Called from _Ready after _terrain is resolved.
    private void InitShadows()
    {
        _shadowRegistry = new ShadowRegistry(this);
        _shadowRegistry.Register(new HorizonMarchOwner(_terrain));
        // DESTROYED back to the clean shadowless baseline (2026-06-27): the near-CSM owner went default-on
        // + UNVERIFIED + off its own "finest leaves only" safety rail (CasterTopLevels=7=all => graveyard
        // acne, masked by cranked bias). Registered DISABLED so the scaffolding stays but nothing casts:
        // WantsSunShadow=false => sun.ShadowEnabled=false, horizon hz_on default-off, all geometry CastShadow.Off.
        // Re-enable ONLY behind a real eye-gate + graveyard motion test, one owner at a time, never default-on.
        _shadowRegistry.Register(new CsmCastOwner(this) { Enabled = false });
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
