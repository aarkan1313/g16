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

    // M1 fix: only re-apply when the overcast amount actually CHANGES (or after a mood/
    // slider sets a new base), so the sun-energy slider is 1:1 in the steady state instead
    // of being overwritten every frame. -1 forces the first apply.
    private float _lastOvercast = -1f;
    /// Force the next UpdateOvercast to re-apply (call after a mood or sun/ambient/fog
    /// base change so overcast scaling picks up the new base immediately).
    private void OvercastDirty() => _lastOvercast = -1f;

    private void UpdateOvercast()
    {
        if (_cloud == null) { return; }
        float oc = _overcastDim ? _cloud.Overcast() : 0f;
        if (Mathf.Abs(oc - _lastOvercast) < 0.002f) { return; }   // nothing changed → leave bases alone
        _lastOvercast = oc;

        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
        const float OvercastAmt = 0.8f;
        float k = 1f - oc * OvercastAmt;
        // Real overcast = flat, dim, diffuse: pull direct sun WAY down + ambient DOWN a bit
        // (the sky becomes a dull grey source, not a brighter one). oc is ~0 for sparse skies
        // now, so this only engages when the sky is genuinely heavily covered.
        env.AmbientLightEnergy = _baseAmbient * Mathf.Lerp(1f, 0.7f, oc);     // sky fill DOWN (grey gloom)
        sun.LightEnergy = _baseSunEnergy * k;                                // direct sun DOWN under cloud
        // fog only tints toward the cloud-grey when actually overcast (oc~0 → no tint shift).
        env.FogLightColor = _baseFogColor.Lerp(_cloud.SkyHorizonColor, 0.55f * oc);
        // God-ray scatter energy only matters (and only written) when god rays are on.
        if (_cloud.GodraysOn)
        {
            float broken = 4f * oc * (1f - oc);   // bell curve, max at oc=0.5 (broken cloud)
            sun.LightVolumetricFogEnergy = Mathf.Lerp(0.5f, 12f, broken);
        }
    }

    private bool _shadowEnabledOnce;
    public override void _Process(double delta)
    {
        if (_ready) { UpdateOvercast(); }
        // push camera world pos for the ground anti-repetition distance LOD (Unit 1)
        // AND the world-space cloud raymarch (rays start at the camera so clouds + the
        // ground shadow map share one world frame — fixes the dome-vs-world mismatch).
        if (_ready)
        {
            Vector3 camPos = GetNode<Camera3D>("/root/TerrainLabRoot/Camera").GlobalPosition;
            _terrain.SetCameraWorld(camPos);
            _cloud?.SetCameraWorld(camPos);
        }
        // L2: enable terrain shadow sampling once the cloud shadow map's RID is live.
        if (!_shadowEnabledOnce && _cloud != null && _cloud.ComputeReady && _cloud.Enabled)
        {
            _terrain.SetBool("cloud_shadow_on", true);
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
    private double _profileT = -1.0, _profileDur = 3.0, _profAccum = 0, _profWorst = 0;
    private int _profFrames = 0;
}
