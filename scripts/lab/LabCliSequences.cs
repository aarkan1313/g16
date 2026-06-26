using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace WG16.Lab;

/// CLI-armed capture / profile sequences for headless runs (decomposition Phase 1c). Each is armed by a
/// flag in ParseCli, ticks once per frame, then captures/measures and quits — self-contained state machines
/// carved out of the 435-line _Process. Covers: --auto-shot, the drift-free A/B captures (--godrayab,
/// --fillab), --profile (frame-time gate), and --profmove (orbit the camera during a profile so motion costs
/// are paid). Depends only on the host Node (viewport/tree/camera), the god-rays node, and the lighting
/// composer. The FPS HUD and the async --aabbspike probe stay in _Process (label-owned / async-coupled).
public sealed class LabCliSequences
{
    private readonly Node _host;
    private readonly GodRaysScreen? _godrays;
    private readonly LightingComposer _lighting;

    private string? _autoShotPath; private double _autoShotT = -1.0;
    private string? _godrayAbPath; private double _godrayAbT = -1.0; private int _godrayAbStage; private int _godrayAbFrames;
    private double _profileT = -1.0, _profileDur = 3.0, _profAccum, _profWorst;
    private int _profFrames;
    private bool _profMove;
    private float _profSpeed = 1200f;   // --profspeed= forward m/s for the border-crossing traverse
    private string? _profileLogPath;
    private CdlodTerrain? _cdlod;
    private readonly List<ProfileFrame> _profSamples = new();
    private readonly List<ProfileFrame> _profTop = new();
    private const int ProfileTopCount = 8;

    private sealed class ProfileFrame
    {
        public int Sample;
        public double Delta;
        public int CdlodFrame;
        public bool Snapped;
        public int Leaves;
        public int Active;
        public int NearBirths;
        public int FarBirths;
        public int Retires;
        public int BakePending;
        public int CachePending;
        public int NearBudget;
        public int RetireGrace;
        public int Tightened;
        public float Speed;
        public int TotalSnaps;
        public int TotalBirths;
        public int TotalRebirths;
        public long VisibleDraws;
        public long ShadowDraws;
        public long VisibleObjects;
        public long ShadowObjects;
        public long VisiblePrimitives;
        public long ShadowPrimitives;
    }

    public LabCliSequences(Node host, GodRaysScreen? godrays, LightingComposer lighting)
    {
        _host = host;
        _godrays = godrays;
        _lighting = lighting;
    }

    // ---- arming (called from ParseCli) ----
    public void ArmAutoShot(string path) { _autoShotPath = path; _autoShotT = 0.0; }
    public void ArmGodrayAb(string path) { _godrayAbPath = path; _godrayAbT = 0.0; }
    public void EnableProfMove() => _profMove = true;
    public void SetProfSpeed(float mps) { if (mps > 0f) { _profSpeed = mps; } }
    public void SetProfileLogPath(string path) { if (!string.IsNullOrWhiteSpace(path)) { _profileLogPath = path; } }
    public void ArmProfile(double? dur)
    {
        _profileT = 0.0;
        if (dur.HasValue) { _profileDur = dur.Value; }
        _profAccum = 0.0;
        _profWorst = 0.0;
        _profFrames = 0;
        _profSamples.Clear();
        _profTop.Clear();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
    }

    /// --profmove: fly the camera along a BORDER-CROSSING TRAVERSE during a profile so the REAL streaming costs
    /// are paid every frame — renderOrigin snaps (every 8192 m of travel), leading-edge chunk births, window
    /// shifts in both axes, and LOD churn — NOT just steady-state. The old 700 m orbit never left its home cell
    /// (zero snaps, zero window shifts) and badly understated flying. This path:
    ///   • forward translation (X = spd·t) → a renderOrigin SNAP every 8192 m + continuous leading-edge births;
    ///   • lateral serpentine (Z, amplitude > one region) → crosses Z region/chunk borders, shifts the window in Z;
    ///   • altitude oscillation → varies LOD selection / chunk count / shadow coverage;
    ///   • camera faces the direction of travel (samples the path slightly ahead) → the real player view, with
    ///     terrain resolving toward the camera (where pop/stream artifacts actually show).
    /// Use a longer window (e.g. --profile=8) so several borders are crossed inside the measured interval.
    /// Tunable via --profspeed= (forward m/s; default 1200). Called at the TOP of _Process (before the
    /// floating-origin frame), as in the original.
    public bool ProfMoveActive => _profMove && _profileT >= 0.0;
    public Vector3 ProfTruePos { get; private set; }       // desired TRUE-world camera position this frame
    public Vector3 ProfLookTarget { get; private set; }    // TRUE-world look target (direction of travel)

    /// Compute the traverse pose in TRUE world space and STORE it (does NOT touch the camera node — the
    /// floating-origin block in _Process consumes ProfTruePos directly so renderOrigin isn't double-counted;
    /// the prior version set GlobalPosition to the true pos, which the `camPos = camN.Position + renderOrigin`
    /// reconstruction then re-added renderOrigin to → a per-frame snap runaway once the path left cell 0).
    public void TickProfMove(double delta)
    {
        if (!ProfMoveActive) { return; }
        float t = (float)_profileT;
        Vector3 PathAt(float tt) => new Vector3(
            _profSpeed * tt,                       // forward → region snaps every 8192 m + leading-edge births
            900f + 180f * Mathf.Sin(tt * 0.27f),   // altitude 720..1080 m (clears the 639 m peaks) → LOD/chunk/shadow churn
            8500f * Mathf.Sin(tt * 0.35f));        // lateral serpentine (>1 region) → Z border crossings + window shifts
        ProfTruePos = PathAt(t);
        ProfLookTarget = PathAt(t + 0.15f);        // look in the direction of travel (terrain rushes toward view)
    }

    /// The capture + profile sequences. Called at the BOTTOM of _Process (after the frame's work), as in the
    /// original — the viewport texture is the last completed render either way.
    public void Tick(double delta)
    {
        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                _host.GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                GD.Print($"TerrainLab: auto-shot -> {_autoShotPath}  (frame ~{Engine.GetFramesPerSecond():0} fps)");
                _autoShotT = -1.0;
                _host.GetTree().Quit();
            }
        }

        // --godrayab=<path>: drift-free A/B — capture <path>_on.png, toggle god rays OFF, a few frames later
        // capture <path>_off.png, quit. Freezing the scene makes the diff PURELY the god-ray pass.
        if (_godrayAbT >= 0.0 && _godrayAbPath != null)
        {
            _godrayAbT += delta;
            if (_godrayAbStage == 0 && _godrayAbT > 1.5)
            {
                Engine.TimeScale = 0.0;   // FREEZE the scene so OFF == ON except the god rays
                _host.GetViewport().GetTexture().GetImage().SavePng(_godrayAbPath + "_on.png");
                _godrays?.SetEnabled(false);
                _godrayAbStage = 1; _godrayAbFrames = 0;
            }
            else if (_godrayAbStage == 1)
            {
                if (++_godrayAbFrames >= 2)
                {
                    _host.GetViewport().GetTexture().GetImage().SavePng(_godrayAbPath + "_off.png");
                    GD.Print($"TerrainLab: godray A/B -> {_godrayAbPath}_on.png / _off.png (2-frame gap)");
                    _godrayAbT = -1.0;
                    _host.GetTree().Quit();
                }
            }
        }

        // --profile=<secs>: warm up 1s, then average frame time, print fps + worst, quit.
        if (_profileT >= 0.0)
        {
            _profileT += delta;
            if (_profileT > 1.0)
            {
                ProfileFrame sample = CaptureProfileFrame(delta, _profFrames + 1);
                _profSamples.Add(sample);
                TrackTopSpike(sample);
                _profAccum += delta; _profFrames++;
                _profWorst = Math.Max(_profWorst, delta);
                if (_profileT > 1.0 + _profileDur)
                {
                    double avg = _profAccum / Math.Max(_profFrames, 1);
                    List<ProfileFrame> sorted = new(_profSamples);
                    sorted.Sort((a, b) => a.Delta.CompareTo(b.Delta));
                    double p50 = Percentile(sorted, 0.50);
                    double p95 = Percentile(sorted, 0.95);
                    double p99 = Percentile(sorted, 0.99);
                    GD.Print($"PROFILE: avg {1.0 / avg:0} fps ({avg * 1000:0.0} ms)  p50 {p50 * 1000:0.0} ms  p95 {p95 * 1000:0.0} ms  p99 {p99 * 1000:0.0} ms  worst {1.0 / _profWorst:0} fps ({_profWorst * 1000:0.0} ms)  over {_profFrames} frames");
                    var vp = _host.GetViewport();
                    long visDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
                    long shDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
                    long visObj = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame);
                    long shObj = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame);
                    long visPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame);
                    long shPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
                    GD.Print($"PROFILE-RENDER: visible draws={visDraw} objects={visObj} prim={visPrim} | shadow draws={shDraw} objects={shObj} prim={shPrim}");
                    PrintTopSpikes();
                    WriteProfileLogIfRequested();
                    // Streaming churn over the measured window (validates the traverse crossed borders + quantifies it).
                    var cd = _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
                    if (cd != null && cd.Enabled) { GD.Print($"PROFILE-STREAM: snaps={cd.TotalSnaps} births={cd.TotalBirths} rebirths={cd.TotalRebirths} activeChunks={cd.ActiveCount} (cumulative since enable; rebirths=thrash)"); }
                    _profileT = -1.0;
                    _host.GetTree().Quit();
                }
            }
        }
    }

    private CdlodTerrain? GetCdlod()
    {
        return _cdlod ??= _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
    }

    private ProfileFrame CaptureProfileFrame(double delta, int sampleIndex)
    {
        var vp = _host.GetViewport();
        var cd = GetCdlod();
        return new ProfileFrame
        {
            Sample = sampleIndex,
            Delta = delta,
            CdlodFrame = cd?.LastFrameIndex ?? -1,
            Snapped = cd?.LastOriginSnapped ?? false,
            Leaves = cd?.LastLeafCount ?? -1,
            Active = cd?.LastActiveCount ?? -1,
            NearBirths = cd?.LastNearBirths ?? -1,
            FarBirths = cd?.LastFarBirths ?? -1,
            Retires = cd?.LastRetires ?? -1,
            BakePending = cd?.LastBakePending ?? -1,
            CachePending = cd?.LastCachePending ?? -1,
            NearBudget = cd?.LastEffectiveNearBudget ?? -1,
            RetireGrace = cd?.LastEffectiveRetireGrace ?? -1,
            Tightened = cd?.LastTightenedCount ?? -1,
            Speed = cd?.LastSpeedXZ ?? 0f,
            TotalSnaps = cd?.TotalSnaps ?? -1,
            TotalBirths = cd?.TotalBirths ?? -1,
            TotalRebirths = cd?.TotalRebirths ?? -1,
            VisibleDraws = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame),
            ShadowDraws = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame),
            VisibleObjects = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame),
            ShadowObjects = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame),
            VisiblePrimitives = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame),
            ShadowPrimitives = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame),
        };
    }

    private void TrackTopSpike(ProfileFrame sample)
    {
        _profTop.Add(sample);
        _profTop.Sort((a, b) => b.Delta.CompareTo(a.Delta));
        if (_profTop.Count > ProfileTopCount) { _profTop.RemoveAt(_profTop.Count - 1); }
    }

    private static double Percentile(List<ProfileFrame> sorted, double p)
    {
        if (sorted.Count == 0) { return 0.0; }
        int idx = Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1);
        return sorted[idx].Delta;
    }

    private void PrintTopSpikes()
    {
        if (_profTop.Count == 0) { return; }
        GD.Print($"PROFILE-SPIKES: top {_profTop.Count} frames");
        for (int i = 0; i < _profTop.Count; i++)
        {
            ProfileFrame s = _profTop[i];
            int missing = (s.Leaves >= 0 && s.Active >= 0) ? Math.Max(0, s.Leaves - s.Active) : -1;
            GD.Print($"PROFILE-SPIKE {i + 1}: {s.Delta * 1000:0.0} ms sample={s.Sample} cdlodFrame={s.CdlodFrame} snap={(s.Snapped ? 1 : 0)} leaves={s.Leaves} active={s.Active} missing={missing} births={s.NearBirths}+{s.FarBirths} retires={s.Retires} bakePend={s.BakePending} cachePend={s.CachePending} budget={s.NearBudget} grace={s.RetireGrace} speed={s.Speed:0} visDraw={s.VisibleDraws} shDraw={s.ShadowDraws} shObj={s.ShadowObjects}");
        }
    }

    private void WriteProfileLogIfRequested()
    {
        if (string.IsNullOrWhiteSpace(_profileLogPath)) { return; }
        string? dir = Path.GetDirectoryName(_profileLogPath);
        if (!string.IsNullOrWhiteSpace(dir)) { Directory.CreateDirectory(dir); }

        using StreamWriter w = new(_profileLogPath);
        w.WriteLine("sample,ms,cdlod_frame,snap,leaves,active,missing,near_births,far_births,retires,bake_pending,cache_pending,near_budget,retire_grace,tightened,speed_mps,total_snaps,total_births,total_rebirths,visible_draws,shadow_draws,visible_objects,shadow_objects,visible_primitives,shadow_primitives");
        foreach (ProfileFrame s in _profSamples)
        {
            int missing = (s.Leaves >= 0 && s.Active >= 0) ? Math.Max(0, s.Leaves - s.Active) : -1;
            w.WriteLine(string.Join(",",
                s.Sample.ToString(CultureInfo.InvariantCulture),
                (s.Delta * 1000.0).ToString("0.###", CultureInfo.InvariantCulture),
                s.CdlodFrame.ToString(CultureInfo.InvariantCulture),
                s.Snapped ? "1" : "0",
                s.Leaves.ToString(CultureInfo.InvariantCulture),
                s.Active.ToString(CultureInfo.InvariantCulture),
                missing.ToString(CultureInfo.InvariantCulture),
                s.NearBirths.ToString(CultureInfo.InvariantCulture),
                s.FarBirths.ToString(CultureInfo.InvariantCulture),
                s.Retires.ToString(CultureInfo.InvariantCulture),
                s.BakePending.ToString(CultureInfo.InvariantCulture),
                s.CachePending.ToString(CultureInfo.InvariantCulture),
                s.NearBudget.ToString(CultureInfo.InvariantCulture),
                s.RetireGrace.ToString(CultureInfo.InvariantCulture),
                s.Tightened.ToString(CultureInfo.InvariantCulture),
                s.Speed.ToString("0.###", CultureInfo.InvariantCulture),
                s.TotalSnaps.ToString(CultureInfo.InvariantCulture),
                s.TotalBirths.ToString(CultureInfo.InvariantCulture),
                s.TotalRebirths.ToString(CultureInfo.InvariantCulture),
                s.VisibleDraws.ToString(CultureInfo.InvariantCulture),
                s.ShadowDraws.ToString(CultureInfo.InvariantCulture),
                s.VisibleObjects.ToString(CultureInfo.InvariantCulture),
                s.ShadowObjects.ToString(CultureInfo.InvariantCulture),
                s.VisiblePrimitives.ToString(CultureInfo.InvariantCulture),
                s.ShadowPrimitives.ToString(CultureInfo.InvariantCulture)));
        }
        GD.Print($"PROFILE-LOG: {_profileLogPath}");
    }
}
