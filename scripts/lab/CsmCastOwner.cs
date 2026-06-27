using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// NearCast owner: Godot cascaded shadow maps on the sun, capped to a near band, with CDLOD casting gated to
/// its finest leaves. The registry drives sun.ShadowEnabled (via WantsSunShadow); this owner sets the sun's
/// CSM mode/distance/bias and the terrain's caster-min-level when enabled. DiagIds cover BOTH the sun light
/// tag and the chunk caster tag so --shadowcheck recognises them (no rogue).
public sealed class CsmCastOwner : IShadowOwner
{
    private readonly Node _host;
    private readonly CdlodTerrain? _terrain;
    private bool _enabled;
    private bool _applied;

    // Tunables (conservative near band; refine at eye-gate).
    public float MaxDistance = 1500f;   // DirectionalShadowMaxDistance (near band)
    public int   CasterTopLevels = 2;   // finest N LOD levels cast

    public CsmCastOwner(Node host, CdlodTerrain? terrain) { _host = host; _terrain = terrain; }

    public string Name => "csm-near";
    public ShadowSlot Slot => ShadowSlot.NearCast;
    public IReadOnlyList<string> DiagIds => new[]
    {
        "light:/root/TerrainLabRoot/Sun",
        "caster:/root/TerrainLabRoot/CdlodTerrain",
    };

    public bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _applied = false; }   // re-apply config on next Tick
    }

    public bool IsActive => _enabled;

    public void Tick(double delta)
    {
        if (_applied) { return; }
        _applied = true;

        // sun.ShadowEnabled is owned by the registry (WantsSunShadow); we only set the CSM PARAMS here.
        var sun = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        if (sun != null)
        {
            sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
            sun.DirectionalShadowMaxDistance = _enabled ? MaxDistance : 100f;
            sun.ShadowBias = 0.04f;
            sun.ShadowNormalBias = 1.5f;
            sun.ShadowBlur = 1.0f;
        }
        // Caster gate: finest CasterTopLevels levels cast when enabled, none when off.
        if (_terrain != null)
        {
            _terrain.SetShadowCasterMinLevel(_enabled ? (_terrain.MaxDepth - (CasterTopLevels - 1)) : 99);
        }
    }
}
