using Godot;

namespace WG16.Lab;

/// Wraps the EXISTING ground.gdshader horizon march (the hz_on uniform) as a registry owner in the FarCast
/// slot. No behavior change — Enabled mirrors the terrain material's hz_on. This brings the current shadow
/// under the registry so Phase 1 can replace its internals (baked horizon map) behind the same interface.
/// DiagId matches what ShadowDiagnostics tags the horizon owner with ("terrain:horizon...").
public sealed class HorizonMarchOwner : IShadowOwner
{
    private readonly TerrainLab _terrain;

    public HorizonMarchOwner(TerrainLab terrain) { _terrain = terrain; }

    public string Name => "horizon-march";
    public ShadowSlot Slot => ShadowSlot.FarCast;
    public System.Collections.Generic.IReadOnlyList<string> DiagIds => new[] { "terrain:horizon" };

    public bool Enabled
    {
        get => _terrain.MaterialOverride is ShaderMaterial m && m.GetShaderParameter("hz_on").AsBool();
        set => _terrain.SetBool("hz_on", value);
    }

    // The march self-gates by sun elevation + camera distance in-shader; for registry purposes "active"
    // simply means the uniform is on. (Finer "is it contributing this pixel" is not knowable CPU-side.)
    public bool IsActive => Enabled;

    public void Tick(double delta) { }
}
