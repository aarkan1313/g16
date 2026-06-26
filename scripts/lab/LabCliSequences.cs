using Godot;
using System;
using System.Collections.Generic;

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
    private bool _profCollecting;
    private int _profStartSnaps, _profStartBirths, _profStartRebirths;
    private readonly List<double> _profFrameMs = new(2048);
    private readonly List<ProfileSpike> _profSpikes = new(5);
    private long _visDrawSum, _visObjSum, _visPrimSum, _shDrawSum, _shObjSum, _shPrimSum;
    private long _visDrawMax, _visObjMax, _visPrimMax, _shDrawMax, _shObjMax, _shPrimMax;
    private bool _profMove;
    private float _profSpeed = 1200f;   // --profspeed= forward m/s for the border-crossing traverse

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
    public void ArmProfile(double? dur)
    {
        _profileT = 0.0;
        if (dur.HasValue) { _profileDur = dur.Value; }
        ResetProfileStats();
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
    }

    private readonly struct ProfileSpike
    {
        public readonly int Frame;
        public readonly double Ms;
        public readonly long VisDraw, VisObj, VisPrim, ShDraw, ShObj, ShPrim;
        public readonly int Snaps, Births, Rebirths, Active;

        public ProfileSpike(int frame, double ms, long visDraw, long visObj, long visPrim, long shDraw, long shObj, long shPrim, int snaps, int births, int rebirths, int active)
        {
            Frame = frame; Ms = ms;
            VisDraw = visDraw; VisObj = visObj; VisPrim = visPrim;
            ShDraw = shDraw; ShObj = shObj; ShPrim = shPrim;
            Snaps = snaps; Births = births; Rebirths = rebirths; Active = active;
        }
    }

    private void ResetProfileStats()
    {
        _profAccum = 0.0; _profWorst = 0.0; _profFrames = 0; _profCollecting = false;
        _profFrameMs.Clear(); _profSpikes.Clear();
        _visDrawSum = _visObjSum = _visPrimSum = _shDrawSum = _shObjSum = _shPrimSum = 0;
        _visDrawMax = _visObjMax = _visPrimMax = _shDrawMax = _shObjMax = _shPrimMax = 0;
        _profStartSnaps = _profStartBirths = _profStartRebirths = 0;
    }

    private void BeginProfileCollect()
    {
        _profCollecting = true;
        var cd = _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
        if (cd != null && cd.Enabled)
        {
            _profStartSnaps = cd.TotalSnaps;
            _profStartBirths = cd.TotalBirths;
            _profStartRebirths = cd.TotalRebirths;
        }
    }

    private void RecordProfileSample(double delta)
    {
        var vp = _host.GetViewport();
        long visDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
        long shDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
        long visObj = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame);
        long shObj = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame);
        long visPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame);
        long shPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
        var cd = _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");

        _profAccum += delta; _profFrames++;
        _profWorst = Math.Max(_profWorst, delta);
        double ms = delta * 1000.0;
        _profFrameMs.Add(ms);

        _visDrawSum += visDraw; _visObjSum += visObj; _visPrimSum += visPrim;
        _shDrawSum += shDraw; _shObjSum += shObj; _shPrimSum += shPrim;
        _visDrawMax = Math.Max(_visDrawMax, visDraw); _visObjMax = Math.Max(_visObjMax, visObj); _visPrimMax = Math.Max(_visPrimMax, visPrim);
        _shDrawMax = Math.Max(_shDrawMax, shDraw); _shObjMax = Math.Max(_shObjMax, shObj); _shPrimMax = Math.Max(_shPrimMax, shPrim);

        int snaps = cd?.TotalSnaps ?? 0;
        int births = cd?.TotalBirths ?? 0;
        int rebirths = cd?.TotalRebirths ?? 0;
        int active = cd?.ActiveCount ?? 0;
        if (_profSpikes.Count < 5 || ms > _profSpikes[_profSpikes.Count - 1].Ms)
        {
            _profSpikes.Add(new ProfileSpike(_profFrames, ms, visDraw, visObj, visPrim, shDraw, shObj, shPrim, snaps, births, rebirths, active));
            _profSpikes.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            if (_profSpikes.Count > 5) { _profSpikes.RemoveAt(_profSpikes.Count - 1); }
        }
    }

    private static double Percentile(List<double> samples, double pct)
    {
        if (samples.Count == 0) { return 0.0; }
        var sorted = samples.ToArray();
        Array.Sort(sorted);
        int idx = (int)Math.Ceiling((pct / 100.0) * sorted.Length) - 1;
        if (idx < 0) { idx = 0; }
        if (idx >= sorted.Length) { idx = sorted.Length - 1; }
        return sorted[idx];
    }

    private static double FpsFromMs(double ms) => ms > 0.0 ? 1000.0 / ms : 0.0;

    private void PrintProfileSummary()
    {
        double avg = _profAccum / Math.Max(_profFrames, 1);
        GD.Print($"PROFILE: avg {1.0 / avg:0} fps ({avg * 1000:0.0} ms)  worst {1.0 / _profWorst:0} fps ({_profWorst * 1000:0.0} ms)  over {_profFrames} frames");

        double p50 = Percentile(_profFrameMs, 50.0);
        double p95 = Percentile(_profFrameMs, 95.0);
        double p99 = Percentile(_profFrameMs, 99.0);
        GD.Print($"PROFILE-PERCENTILES: p50 {FpsFromMs(p50):0} fps ({p50:0.0} ms)  p95 {FpsFromMs(p95):0} fps ({p95:0.0} ms)  p99 {FpsFromMs(p99):0} fps ({p99:0.0} ms)");

        var vp = _host.GetViewport();
        long visDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame);
        long shDraw = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame);
        long visObj = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame);
        long shObj = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame);
        long visPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame);
        long shPrim = vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame);
        GD.Print($"PROFILE-RENDER: visible draws={visDraw} objects={visObj} prim={visPrim} | shadow draws={shDraw} objects={shObj} prim={shPrim}");

        long frames = Math.Max(_profFrames, 1);
        GD.Print($"PROFILE-RENDER-AVG: visible draws={_visDrawSum / frames} objects={_visObjSum / frames} prim={_visPrimSum / frames} | shadow draws={_shDrawSum / frames} objects={_shObjSum / frames} prim={_shPrimSum / frames}");
        GD.Print($"PROFILE-RENDER-MAX: visible draws={_visDrawMax} objects={_visObjMax} prim={_visPrimMax} | shadow draws={_shDrawMax} objects={_shObjMax} prim={_shPrimMax}");

        for (int i = 0; i < _profSpikes.Count; i++)
        {
            ProfileSpike s = _profSpikes[i];
            GD.Print($"PROFILE-SPIKE: rank={i + 1} frame={s.Frame} {FpsFromMs(s.Ms):0} fps ({s.Ms:0.0} ms) visible draws={s.VisDraw} objects={s.VisObj} prim={s.VisPrim} | shadow draws={s.ShDraw} objects={s.ShObj} prim={s.ShPrim} | stream snaps={s.Snaps - _profStartSnaps} births={s.Births - _profStartBirths} rebirths={s.Rebirths - _profStartRebirths} active={s.Active}");
        }

        var cd = _host.GetNodeOrNull<CdlodTerrain>("/root/TerrainLabRoot/CdlodTerrain");
        if (cd != null && cd.Enabled)
        {
            cd.ActiveDiagnostics(out int near, out int far, out int shadowCasters, out int cacheReady);
            GD.Print($"PROFILE-STREAM: snaps={cd.TotalSnaps} births={cd.TotalBirths} rebirths={cd.TotalRebirths} activeChunks={cd.ActiveCount} (cumulative since enable; rebirths=thrash)");
            GD.Print($"PROFILE-STREAM-DELTA: snaps={cd.TotalSnaps - _profStartSnaps} births={cd.TotalBirths - _profStartBirths} rebirths={cd.TotalRebirths - _profStartRebirths}");
            GD.Print($"PROFILE-CDLOD: active={cd.ActiveCount} near={near} far={far} shadowCasters={shadowCasters} cacheReady={cacheReady}");
        }
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

        // --profile=<secs>: warm up 1s, then profile frame-time percentiles, render stats, spike frames, and quit.
        if (_profileT >= 0.0)
        {
            _profileT += delta;
            if (_profileT > 1.0)
            {
                if (!_profCollecting) { BeginProfileCollect(); }
                RecordProfileSample(delta);
                if (_profileT > 1.0 + _profileDur)
                {
                    PrintProfileSummary();
                    _profileT = -1.0;
                    _host.GetTree().Quit();
                }
            }
        }
    }
}
