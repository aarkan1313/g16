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
    // _baseAmbient / _baseSunEnergy / _baseFogColor moved to LightingComposer (C3 Unit 1); accessed here
    // via the forwarding properties in TerrainLabUI.Lighting.cs (same names), so this code is unchanged.
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
    private bool _lastKey1Down;             // debounce for the analytic/baked toggle (key 1)
    private bool _lastKey5Down, _lastKey6Down, _lastKey7Down;   // S2b: debounce for the test-path keys (5/6/7)
    // Debug isolation bank (F1..F6): live-flip the screen-space effects that produce camera-locked stipple/ring
    // artifacts, so a "dots in a shifting ring" report can be pinned to ONE system in the running window.
    private bool _lastF1, _lastF2, _lastF3, _lastF4, _lastF5, _lastF6, _lastF7, _lastF8, _lastF9;
    private bool _lastG;        // G toggles the anti-moiré detail-fade live
    private bool _lastWaterH;   // H toggles the water debug overlay live
    private bool _waterDebugOn; // water debug overlay state (paints rivers/lakes cyan)
    private bool _detailFadeOn = true;   // anti-moiré detail-fade default state (matches shader default)
    private bool _lastJ;        // J steps the ring-hunt diag_mode (surfacing AA eye-gate)
    private int _diagMode;      // 0 normal, 1 grey, 2 +albedo, 3 +roughness, 4 +normalmap
    private bool _lodVizLive;   // V toggles the LOD-band tint live
    private bool _terrainCloudShadowOn = true;   // mirrors cloud_shadow_on (set true once the cloud RID is live); F5 flips it
    private bool _godraysOn = true;               // god rays default on; F6 flips it
    private bool _analyticOn = true;        // ground source: live field (default) vs baked; toggled by key 1
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
        TickLuminaryCheck(delta);   // U2 numeric gate (--luminarycheck): no-op unless armed

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

        // S3.5 SPIKE driver (--aabbspike): pump the async provider; once the result lands, compare to a sync
        // FieldCompute.ProducePage min/max of the SAME footprint and print AABBSPIKE match=YES/NO, then quit.
        if (_spikeFrame >= 0 && !_spikeDone)
        {
            _spikeProvider.Pump();
            if (_spikeProvider.TryTake(out long _, out float alo, out float ahi))
            {
                float[] page = _fc.ProducePage(_params, 0f, 0f, 2048f / (7 - 1), 7, 0);   // sync reference, same grid
                float slo = float.MaxValue, shi = float.MinValue;
                foreach (float v in page) { if (v < slo) { slo = v; } if (v > shi) { shi = v; } }
                bool match = Mathf.Abs(alo - slo) < 0.01f && Mathf.Abs(ahi - shi) < 0.01f;
                GD.Print($"AABBSPIKE: async=({alo:F3},{ahi:F3}) sync=({slo:F3},{shi:F3}) match={(match ? "YES" : "NO")} (landed frame {_spikeFrame})");
                _spikeDone = true;
                GetTree().Quit();
            }
            else if (++_spikeFrame > 120) { GD.Print("AABBSPIKE: NO RESULT in 120 frames — async collect FAILED"); _spikeDone = true; GetTree().Quit(); }
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
            // S3 FLOATING-ORIGIN (snap-pop fix). The CDLOD chunks render at worldXZ − renderOrigin (bounded
            // coords for far-distance precision). For the rendered image to be correct, the CAMERA must live in
            // that SAME render frame — otherwise it draws terrain offset by renderOrigin, and that offset jumps
            // 8192 m at every snap → the whole terrain "changes shape" in one frame (the pop the user saw).
            //   • TRUE world pos = camera node render pos + renderOrigin (renderOrigin is last tick's; 0 on f0).
            //   • CdlodTick(trueCam) snaps the NEW renderOrigin and places chunks render-relative to it.
            //   • Then co-locate the camera node into the render frame (trueCam − newOrigin). On a snap, node and
            //     geometry shift by the SAME −Δorigin in lockstep → no relative jump → no pop. FlyCamera's
            //     Position += delta integrator is frame-agnostic, so navigation is unaffected.
            //   • EVERY world-space consumer (sky/clouds/atmosphere/quadtree/popmeter) gets the TRUE pos so the
            //     sky-lane's world-anchored cloud/shadow/aerial math is unchanged.
            // When CDLOD is off (single full mesh at true origin), renderOrigin stays 0 → this is a no-op.
            Vector3 renderOrigin = _terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero;
            Vector3 camPos = camN.Position + renderOrigin;   // TRUE world camera position
            _terrain.SetCameraWorld(camPos);
            _terrain.CdlodTick(camPos);   // S2a: rebuild the visible chunk set; S3: snaps the new renderOrigin
            if (_terrain.CdlodActive)
            {
                Vector3 newOrigin = _terrain.CdlodRenderOrigin;   // may have snapped inside CdlodTick
                camN.Position = camPos - newOrigin;               // co-locate the camera with the render frame
            }
            _cloud?.SetCameraWorld(camPos);
            TickPopMeter(camPos);   // S3 --popmeter: live per-frame pop/snap measurement (no-op unless armed)

            // AT-2: push the camera to the atmosphere so the aerial froxel LUT (camera-frustum aligned)
            // re-marches from the current view each frame. invViewProj reconstructs world from NDC in the
            // aerial compute (same convention as godray_screen). The view matrix is render-relative now, but the
            // aerial reconstructs positions in the SAME render frame as the depth buffer, so passing the TRUE
            // camPos keeps the in-scatter distance/world math consistent with the (true-world) sun + sky.
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

            // --- Debug isolation bank (B N M , . /) --------------------------------------------------------
            // Live-flip each camera-locked screen-space effect to pin a "dots in a shifting ring" artifact to
            // its source. Each prints its new state. (Moved off F-keys: those get grabbed by the OS/IDE and
            // never reach the game window.) B SSAO, N SSIL, M SDFGI, , sun shadows, . cloud-shadow, / god rays.
            {
                var env = UiEnv.Environment;
                bool kB = Input.IsKeyPressed(Key.B);
                if (kB && !_lastF1) { env.SsaoEnabled = !env.SsaoEnabled; GD.Print($"[dbg] (B) SSAO = {env.SsaoEnabled}"); }
                _lastF1 = kB;
                bool kN = Input.IsKeyPressed(Key.N);
                if (kN && !_lastF2) { env.SsilEnabled = !env.SsilEnabled; GD.Print($"[dbg] (N) SSIL = {env.SsilEnabled}"); }
                _lastF2 = kN;
                bool kM = Input.IsKeyPressed(Key.M);
                if (kM && !_lastF3) { env.SdfgiEnabled = !env.SdfgiEnabled; GD.Print($"[dbg] (M) SDFGI = {env.SdfgiEnabled}"); }
                _lastF3 = kM;
                bool kComma = Input.IsKeyPressed(Key.Comma);
                if (kComma && !_lastF4) { UiSun.ShadowEnabled = !UiSun.ShadowEnabled; GD.Print($"[dbg] (,) Sun shadows = {UiSun.ShadowEnabled}"); }
                _lastF4 = kComma;
                bool kPeriod = Input.IsKeyPressed(Key.Period);
                if (kPeriod && !_lastF5) { _terrainCloudShadowOn = !_terrainCloudShadowOn; _terrain.SetBool("cloud_shadow_on", _terrainCloudShadowOn); GD.Print($"[dbg] (.) Cloud shadow on terrain = {_terrainCloudShadowOn}"); }
                _lastF5 = kPeriod;
                bool kSlash = Input.IsKeyPressed(Key.Slash);
                if (kSlash && !_lastF6) { _godraysOn = !_godraysOn; _godraysScreen?.SetEnabled(_godraysOn); GD.Print($"[dbg] (/) God rays = {_godraysOn}"); }
                _lastF6 = kSlash;
                // J atmosphere (AT-1 GPU sky tint), K aerial perspective (AT-2 camera froxel) — the two
                // camera-aligned volumes NOT covered above; froxel volumes are the classic concentric-ring suspect.
                bool kJ = Input.IsKeyPressed(Key.J);
                if (kJ && !_lastF7) { _atmosphereOn = !_atmosphereOn; _cloud.SetAtmosphereOn(_atmosphereOn); _atmosphere?.SetEnabled(_atmosphereOn); GD.Print($"[dbg] (J) Atmosphere AT-1 = {_atmosphereOn}"); }
                _lastF7 = kJ;
                bool kK = Input.IsKeyPressed(Key.K);
                if (kK && !_lastF8) { _aerialOn = !_aerialOn; _aerial?.SetEnabled(_aerialOn); GD.Print($"[dbg] (K) Aerial AT-2 = {_aerialOn}"); }
                _lastF8 = kK;
                // V: live LOD-band tint toggle — flip on to see if the dot-rings line up with LOD boundaries.
                bool kV = Input.IsKeyPressed(Key.V);
                if (kV && !_lastF9) { _lodVizLive = !_lodVizLive; _terrain.SetCdlodViz(_lodVizLive); GD.Print($"[dbg] (V) LOD-band tint = {_lodVizLive}"); }
                _lastF9 = kV;
                // G: live A/B the anti-moiré detail-fade (the dot-grid fix). Flip OFF to see the speckle moiré
                // return, ON to see it dissolve to flat mean colour with distance.
                bool kG = Input.IsKeyPressed(Key.G);
                if (kG && !_lastG) { _detailFadeOn = !_detailFadeOn; _terrain.SetFloat("detail_fade_on", _detailFadeOn ? 1f : 0f); GD.Print($"[dbg] (G) anti-moiré detail-fade = {_detailFadeOn}"); }
                _lastG = kG;
                // H: live WATER debug overlay — paint wet pixels cyan / carve band blue so the rivers/lakes
                // + the carve are visible right on the terrain (the carve groove alone is subtle from altitude).
                bool kH = Input.IsKeyPressed(Key.H);
                if (kH && !_lastWaterH) { _waterDebugOn = !_waterDebugOn; _terrain.SetBool("water_debug", _waterDebugOn); GD.Print($"[dbg] (H) water overlay = {_waterDebugOn}"); }
                _lastWaterH = kH;
                // J: RING HUNT stepper (surfacing AA eye-gate). Cycles diag_mode 0→1→2→3→4. 0 normal,
                // 1 red=flat base, 2 green=+albedo, 3 blue=+roughness, 4 yellow=+normal-map — colored so each
                // channel is unmistakable; pins which channel makes the camera-fixed rings.
                bool kJ = Input.IsKeyPressed(Key.J);
                if (kJ && !_lastJ)
                {
                    _diagMode = (_diagMode + 1) % 5;
                    _terrain.SetFloat("diag_mode", _diagMode);
                    string[] names = { "0 NORMAL render", "1 RED base (no textures)", "2 GREEN +albedo", "3 BLUE +roughness", "4 YELLOW +normal-map" };
                    GD.Print($"[ringhunt] (J) diag_mode = {names[_diagMode]}");
                }
                _lastJ = kJ;
            }
            // ----------------------------------------------------------------------------------------------

            // Key 1: live-flip ground source between the analytic field and the baked texture
            // (A/B the S1.5 normal in motion). Skipped when ReviewMode owns the number keys.
            if (!ReviewMode)
            {
                bool k1 = Input.IsKeyPressed(Key.Key1);
                if (k1 && !_lastKey1Down)
                {
                    _analyticOn = !_analyticOn;
                    _terrain.SetAnalytic(_analyticOn);
                }
                _lastKey1Down = k1;
            }

            // S2b: advance an active LOD-crossing test-path flight; keys 5/6/7 start the 3 paths
            // (live eye-gate). Guarded by !ReviewMode like key 1 (ReviewMode owns the number keys).
            _terrain.TickTestPath(delta);
            if (!ReviewMode)
            {
                if (Input.IsKeyPressed(Key.Key5) && !_lastKey5Down) { _terrain.RunTestPath(0); }
                if (Input.IsKeyPressed(Key.Key6) && !_lastKey6Down) { _terrain.RunTestPath(1); }
                if (Input.IsKeyPressed(Key.Key7) && !_lastKey7Down) { _terrain.RunTestPath(2); }
                _lastKey5Down = Input.IsKeyPressed(Key.Key5);
                _lastKey6Down = Input.IsKeyPressed(Key.Key6);
                _lastKey7Down = Input.IsKeyPressed(Key.Key7);
            }
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
