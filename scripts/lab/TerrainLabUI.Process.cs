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

    // Inspection light (press L): a fixed-angle "studio" directional that lights the whole scene so you
    // can check how surfaces read — especially at night before real moonlight (3c). Toggle on/off.
    private DirectionalLight3D? _inspectLight;
    private bool _inspectOn;
    private bool _lastLDown;
    private float _inspectEnergy = 1.0f;   // L-light brightness (Night tab 'inspect light')

    /// Toggle the inspection light (press L). Lazily creates a fixed-angle shadow-casting directional
    /// ("studio key light") that lights the whole scene, so you can check how surfaces read regardless of
    /// time of day (e.g. a dark night before real moonlight lands in 3c). NOT the sun/moon — a debug aid.
    private void ToggleInspectLight()
    {
        if (_inspectLight == null)
        {
            _inspectLight = new DirectionalLight3D
            {
                LightEnergy = _inspectEnergy,
                LightColor = new Color(1f, 0.97f, 0.92f),
                ShadowEnabled = true,
                RotationDegrees = new Vector3(-55f, 40f, 0f),   // 3/4 studio angle
            };
            GetNode<Node3D>("/root/TerrainLabRoot").AddChild(_inspectLight);
        }
        _inspectOn = !_inspectOn;
        _inspectLight.Visible = _inspectOn;
        GD.Print($"[inspect light] {(_inspectOn ? "ON (studio directional)" : "off")}");
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
        // AT-1 default-on: flip the sky material to the physical LUT only once it's computed (RID bound).
        // Until then the approved keyframed sky shows — no sampling of an unbound Texture2Drd on frame 1.
        if (_atmosphereOn && !_atmoMatActivated && _atmosphere != null && _atmosphere.Ready)
        {
            _cloud?.SetAtmosphereOn(true);
            _atmoMatActivated = true;
        }
        // AT-2 default-on: enable the aerial composite only once the aerial LUT RID is live (avoids sampling
        // an unbound 3D texture on frame 1). The cleared LUT is a safe identity (a=1, rgb=0) until the first
        // recompute lands, so enabling here is harmless even a frame early.
        if (_aerialOn && !_aerialActivated && _atmosphere != null && _atmosphere.AerialReady)
        {
            _aerial?.SetEnabled(true);
            _aerialActivated = true;
        }
        // PERF: enable the Milky Way baked-texture path once the bake texture RID is live (baked in InitCompute,
        // so Ready ⇒ the bake has run). Avoids sampling an unbound texture on frame 1.
        if (_mwBakedOn && !_mwActivated && _atmosphere != null && _atmosphere.Ready)
        {
            _cloud?.SetMilkyWayBaked(true);
            _mwActivated = true;
        }
        // AT-3 default-gated: once the atmosphere has read back its cloud-light colors AND the cloud compute
        // is ready, enable physical cloud lighting at the current strength. While active, keep the 3 colors
        // current as the sun moves (cheap — they're cached Vector3s pushed into the cloud param buffer).
        if (_cloudLightOn && _atmosphere != null && _atmosphere.CloudLightReady && _cloud != null && _cloud.ComputeReady)
        {
            if (!_cloudLightActivated) { _cloud.SetCloudAtmoLight(_cloudLightStr); _cloudLightActivated = true; }
            _cloud.SetCloudAtmoColors(_atmosphere.CloudZenith, _atmosphere.CloudHorizonSun, _atmosphere.CloudSunTrans);
        }
        // ST4-1: auto day/night cycle — advance the Time axis + re-compose; sync the slider so manual
        // scrub still works (grab the slider to pause-and-scrub; toggle off to stop). Wraps at 24→0.
        if (_ready && _timeRunning)
        {
            float h = _time.TimeOfDay + (float)delta * _timeSpeed;
            h -= Mathf.Floor(h / 24f) * 24f;                 // wrap to [0,24) (handles +/- speed)
            DriveTime(h);
            if (_byId.TryGetValue("time_of_day", out var tc)) { SetWidgetValueSilent(tc, h); }
        }
        // push camera world pos for the ground anti-repetition distance LOD (Unit 1)
        // AND the world-space cloud raymarch (rays start at the camera so clouds + the
        // ground shadow map share one world frame — fixes the dome-vs-world mismatch).
        if (_ready)
        {
            var camN = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
            Vector3 camPos = camN.GlobalPosition;
            _terrain.SetCameraWorld(camPos);
            _cloud?.SetCameraWorld(camPos);

            // AT-2: push the camera to the atmosphere so the aerial froxel LUT (camera-frustum aligned)
            // re-marches from the current view each frame. invViewProj reconstructs world from NDC in the
            // aerial compute (same convention as godray_screen). Cheap aerial-only recompute (sun unchanged).
            if (_atmosphere != null && _atmosphereOn)
            {
                var proj = camN.GetCameraProjection();
                var vp = proj * new Godot.Projection(camN.GlobalTransform.AffineInverse());
                _atmosphere.SetCamera(camPos, 32000f, vp.Inverse());
            }

            // Inspection light (L): toggle a fixed-angle studio directional to check surfaces.
            bool lDown = Input.IsKeyPressed(Key.L);
            if (lDown && !_lastLDown) { ToggleInspectLight(); }
            _lastLDown = lDown;
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
