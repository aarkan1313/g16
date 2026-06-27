using Godot;
using System.Collections.Generic;

namespace WG16.Lab;

/// NearCast owner: Godot cascaded shadow maps on the sun, capped to a near band, with CDLOD casting gated to
/// its finest leaves. The registry drives sun.ShadowEnabled (via WantsSunShadow); this owner sets the sun's
/// CSM mode/distance/bias and the terrain's caster-min-level once both nodes exist. DiagIds cover BOTH the sun
/// light tag and the chunk caster tag so --shadowcheck recognises them (no rogue).
public sealed class CsmCastOwner : IShadowOwner
{
    private readonly Node _host;
    // Resolved LAZILY in Tick: CdlodTerrain is AddChild'd DEFERRED (TerrainLab.cs), so it does NOT exist yet
    // when InitShadows() runs during _Ready. Same for the Sun on the very first frames.
    private CdlodTerrain? _terrain;
    private bool _enabled;
    private bool _applied;

    // Tunables (conservative near band; refine at eye-gate).
    public float MaxDistance = 1500f;   // DirectionalShadowMaxDistance (near band)
    public int   CasterTopLevels = 7;   // finest N LOD levels cast (7=all; MaxDistance is the real limiter,
                                        // so this works at any altitude. Distance-gating is a future perf refinement.)

    public CsmCastOwner(Node host) { _host = host; }

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
        var sun = _host.GetNodeOrNull<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        _terrain ??= _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
        if (sun == null || _terrain == null) { return; }   // retry next frame (both are set up deferred)
        _applied = true;

        // sun.ShadowEnabled is owned by the registry (WantsSunShadow); we only set the CSM PARAMS here.
        sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        sun.DirectionalShadowMaxDistance = _enabled ? MaxDistance : 100f;
        sun.ShadowBias = 0.12f;          // raised for the huge terrain scale (8192 m region) -> kills acne
        sun.ShadowNormalBias = 4.0f;     // normal-offset is the strongest acne lever on big shadow texels
        sun.ShadowBlur = 1.0f;
        sun.DirectionalShadowBlendSplits = true;   // smooth the cascade transitions (the "bands")
        // Caster gate: finest CasterTopLevels levels cast when enabled, none when off.
        _terrain.SetShadowCasterMinLevel(_enabled ? (_terrain.MaxDepth - (CasterTopLevels - 1)) : 99);
    }
}
