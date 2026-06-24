using Godot;
using System;

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
    private string? _fillAbPath; private double _fillAbT = -1.0; private int _fillAbStage; private int _fillAbFrames;
    private double _profileT = -1.0, _profileDur = 3.0, _profAccum, _profWorst;
    private int _profFrames;
    private bool _profMove;

    public LabCliSequences(Node host, GodRaysScreen? godrays, LightingComposer lighting)
    {
        _host = host;
        _godrays = godrays;
        _lighting = lighting;
    }

    // ---- arming (called from ParseCli) ----
    public void ArmAutoShot(string path) { _autoShotPath = path; _autoShotT = 0.0; }
    public void ArmGodrayAb(string path) { _godrayAbPath = path; _godrayAbT = 0.0; }
    public void ArmFillAb(string path) { _fillAbPath = path; _fillAbT = 0.0; }
    public void EnableProfMove() => _profMove = true;
    public void ArmProfile(double? dur)
    {
        _profileT = 0.0;
        if (dur.HasValue) { _profileDur = dur.Value; }
        DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
        Engine.MaxFps = 0;
    }

    /// --profmove: orbit the camera during a profile so MOTION costs (SDFGI cascade re-raster, shadow-frustum
    /// updates, cloud temporal reprojection) are paid every frame — a static --profile understates flying.
    /// Called at the TOP of _Process (before the floating-origin frame), as in the original.
    public void TickProfMove(double delta)
    {
        if (_profMove && _profileT >= 0.0)
        {
            var pcam = _host.GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            float t = (float)_profileT;
            float ang = t * 0.6f;                                   // ~0.6 rad/s orbit
            var center = new Vector3(0f, 120f, 0f);
            var pos = center + new Vector3(Mathf.Cos(ang) * 700f,
                                           140f + 60f * Mathf.Sin(t * 0.3f),
                                           Mathf.Sin(ang) * 700f);
            pcam.GlobalPosition = pos;
            pcam.LookAt(center, Vector3.Up);
        }
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

        // --fillab=<path>: DRIFT-FREE indirect-fill A/B (relight #1). Freeze the day cycle, capture fill ON,
        // toggle FillEnabled OFF + recompose, capture, quit. The two frames differ ONLY by the fill term.
        if (_fillAbT >= 0.0 && _fillAbPath != null)
        {
            _fillAbT += delta;
            if (_fillAbStage == 0 && _fillAbT > 1.5)
            {
                Engine.TimeScale = 0.0;                       // FREEZE: sun/day-cycle stop -> pure A/B
                _lighting.FillEnabled = true; _lighting.Compose();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_fillAbPath)!);
                _host.GetViewport().GetTexture().GetImage().SavePng(_fillAbPath + "_fillon.png");
                _lighting.FillEnabled = false; _lighting.Compose();
                _fillAbStage = 1; _fillAbFrames = 0;
            }
            else if (_fillAbStage == 1)
            {
                if (++_fillAbFrames >= 3)                     // let the recompose flush
                {
                    _host.GetViewport().GetTexture().GetImage().SavePng(_fillAbPath + "_filloff.png");
                    GD.Print($"TerrainLab: fillab -> {_fillAbPath}_fillon.png / _filloff.png (frozen)");
                    _fillAbT = -1.0;
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
                _profAccum += delta; _profFrames++;
                _profWorst = Math.Max(_profWorst, delta);
                if (_profileT > 1.0 + _profileDur)
                {
                    double avg = _profAccum / Math.Max(_profFrames, 1);
                    GD.Print($"PROFILE: avg {1.0 / avg:0} fps ({avg * 1000:0.0} ms)  worst {1.0 / _profWorst:0} fps ({_profWorst * 1000:0.0} ms)  over {_profFrames} frames");
                    _profileT = -1.0;
                    _host.GetTree().Quit();
                }
            }
        }
    }
}
