using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    private Label? _fpsLabel;
    private double _fpsAccum;
    private int _fpsFrames;
    // overcast → GI/sun dimming + aerial-perspective tint (driven by cloud coverage)
    private float _baseAmbient = 0.4f, _baseSunEnergy = 1.3f;
    private Color _baseFogColor = new Color(0.71f, 0.78f, 0.86f);
    private bool _overcastDim = true;
    private float _overcast = 0f;   // current overcast amount; the composer's ApplyOvercastScaling reads this

    // M1 fix: only re-apply when the overcast amount actually CHANGES (or after a mood/
    // slider sets a new base), so the sun-energy slider is 1:1 in the steady state instead
    // of being overwritten every frame. -1 forces the first apply.
    private float _lastOvercast = -1f;

    // Inspection headlamp (press L): a movable omni light at the camera to check how surfaces read,
    // especially at night before real moonlight (3c). Toggle on/off; follows the camera while on.
    private OmniLight3D? _inspectLight;
    private bool _inspectOn;
    private bool _lastLDown;

    /// Toggle the inspection headlamp (press L). Lazily creates a shadow-casting omni light parented to
    /// the scene root; it follows the camera while on. A debug aid to check how surfaces read under a
    /// movable light (e.g. dark night before moonlight lands in 3c).
    private void ToggleInspectLight()
    {
        if (_inspectLight == null)
        {
            _inspectLight = new OmniLight3D
            {
                OmniRange = 2000f,
                LightEnergy = 8f,
                LightColor = new Color(1f, 0.96f, 0.90f),
                ShadowEnabled = true,
            };
            GetNode<Node3D>("/root/TerrainLabRoot").AddChild(_inspectLight);
        }
        _inspectOn = !_inspectOn;
        _inspectLight.Visible = _inspectOn;
        GD.Print($"[inspect light] {(_inspectOn ? "ON (follows camera)" : "off")}");
    }

    // Per-frame overcast tracker. NO scene writes here — it only updates the overcast amount and
    // re-runs the composer's ApplyOvercastScaling (the one writer of sun-energy/ambient/fog-color), so
    // this can't diverge from / fight LightingComposer (Stage 2 T2-fix).
    private void UpdateOvercast()
    {
        if (_cloud == null) { return; }
        float oc = _overcastDim ? _cloud.Overcast() : 0f;
        if (Mathf.Abs(oc - _lastOvercast) < 0.002f) { return; }   // nothing changed → leave it alone
        _lastOvercast = oc;
        _overcast = oc;
        ApplyOvercastScaling();
    }

    private bool _shadowEnabledOnce;
    public override void _Process(double delta)
    {
        // --profmove: orbit the camera during a profile so the MOTION costs (SDFGI cascade
        // re-rasterization, shadow-frustum updates, cloud temporal reprojection, AR distance)
        // are paid every frame — a static --profile lets them converge and understates flying.
        if (_profMove && _profileT >= 0.0)
        {
            var pcam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            float t = (float)_profileT;
            float ang = t * 0.6f;                                   // ~0.6 rad/s orbit
            var center = new Vector3(0f, 120f, 0f);
            var pos = center + new Vector3(Mathf.Cos(ang) * 700f,
                                           140f + 60f * Mathf.Sin(t * 0.3f),
                                           Mathf.Sin(ang) * 700f);
            pcam.GlobalPosition = pos;
            pcam.LookAt(center, Vector3.Up);
        }

        if (_ready) { UpdateOvercast(); }
        // push camera world pos for the ground anti-repetition distance LOD (Unit 1)
        // AND the world-space cloud raymarch (rays start at the camera so clouds + the
        // ground shadow map share one world frame — fixes the dome-vs-world mismatch).
        if (_ready)
        {
            Vector3 camPos = GetNode<Camera3D>("/root/TerrainLabRoot/Camera").GlobalPosition;
            _terrain.SetCameraWorld(camPos);
            _cloud?.SetCameraWorld(camPos);

            // Inspection headlamp (L): toggle a movable omni light at the camera to check surfaces.
            bool lDown = Input.IsKeyPressed(Key.L);
            if (lDown && !_lastLDown) { ToggleInspectLight(); }
            _lastLDown = lDown;
            if (_inspectOn && _inspectLight != null) { _inspectLight.GlobalPosition = camPos; }
        }
        // L2: enable terrain shadow sampling once the cloud shadow map's RID is live.
        if (!_shadowEnabledOnce && _cloud != null && _cloud.ComputeReady && _cloud.Enabled)
        {
            _terrain.SetBool("cloud_shadow_on", true);
            _godraysScreen?.SetCloudOcclusionReady(true);   // god-ray cloud sampling: same frame the RID goes live
            _shadowEnabledOnce = true;
        }

        // FPS / frame-time HUD (top-right). Cheap; updated ~4×/sec. The perf gate
        // needs a number, not a feeling — this is it.
        if (_fpsLabel != null)
        {
            _fpsAccum += delta; _fpsFrames++;
            if (_fpsAccum >= 0.25)
            {
                double avg = _fpsAccum / _fpsFrames;
                _fpsLabel.Text = $"{1.0 / avg,5:0} fps   {avg * 1000.0,5:0.0} ms";
                _fpsAccum = 0; _fpsFrames = 0;
            }
        }

        if (_autoShotT >= 0.0 && _autoShotPath != null)
        {
            _autoShotT += delta;
            if (_autoShotT > 1.5)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_autoShotPath)!);
                GetViewport().GetTexture().GetImage().SavePng(_autoShotPath);
                // print steady-state frame-time for the perf gate (averaged over warm-up)
                GD.Print($"TerrainLab: auto-shot -> {_autoShotPath}  (frame ~{Engine.GetFramesPerSecond():0} fps)");
                _autoShotT = -1.0;
                GetTree().Quit();
            }
        }

        // --godrayab=<path>: drift-free A/B — capture <path>_on.png (god rays on), toggle them OFF, a few
        // frames later capture <path>_off.png, quit. Same near-identical frame, so the diff is PURELY the
        // god-ray pass (no cloud-shadow drift between separate launches confounding it).
        if (_godrayAbT >= 0.0 && _godrayAbPath != null)
        {
            _godrayAbT += delta;
            if (_godrayAbStage == 0 && _godrayAbT > 1.5)
            {
                Engine.TimeScale = 0.0;   // FREEZE the scene (clouds stop evolving) so OFF == ON except the god rays
                GetViewport().GetTexture().GetImage().SavePng(_godrayAbPath + "_on.png");
                _godraysScreen?.SetEnabled(false);
                _godrayAbStage = 1; _godrayAbFrames = 0;
            }
            else if (_godrayAbStage == 1)
            {
                if (++_godrayAbFrames >= 2)
                {
                    GetViewport().GetTexture().GetImage().SavePng(_godrayAbPath + "_off.png");
                    GD.Print($"TerrainLab: godray A/B -> {_godrayAbPath}_on.png / _off.png (2-frame gap)");
                    _godrayAbT = -1.0;
                    GetTree().Quit();
                }
            }
        }

        // --profile=<secs>: warm up 1s, then average frame time, print fps + worst, quit
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
                    GD.Print($"PROFILE: avg {1.0/avg:0} fps ({avg*1000:0.0} ms)  worst {1.0/_profWorst:0} fps ({_profWorst*1000:0.0} ms)  over {_profFrames} frames");
                    _profileT = -1.0;
                    GetTree().Quit();
                }
            }
        }
    }
    private string? _godrayAbPath; private double _godrayAbT = -1.0; private int _godrayAbStage = 0; private int _godrayAbFrames = 0;
    private double _profileT = -1.0, _profileDur = 3.0, _profAccum = 0, _profWorst = 0;
    private bool _profMove = false;   // --profmove: orbit camera during profile (motion cost)
    private int _profFrames = 0;
}
