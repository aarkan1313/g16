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
    // ARC B Task 4: smoothed camera XZ velocity for predictive CDLOD loading (true-world, snap-continuous).
    private Vector3 _camVelSmoothed;
    private Vector3 _lastCamVelPos;
    private bool _camVelInit;
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
    private bool _lastF2, _lastF3, _lastF4, _lastF5, _lastF6, _lastF7, _lastF8, _lastF9;
    private bool _lastG;        // G toggles the anti-moiré detail-fade live
    private bool _lastWaterH;   // H toggles the water debug overlay live
    private bool _waterDebugOn; // water debug overlay state (paints rivers/lakes cyan)
    private bool _detailFadeOn = false;  // anti-moiré detail-fade default OFF (matches shader default; ring bug fixed at source, fade only washed far detail)
    private bool _lastJ;        // J steps the ring-hunt diag_mode (surfacing AA eye-gate)
    private bool _lastLiveProfileDump; // B dumps the last rolling profile window to console + artifacts/
    private int _diagMode;      // 0 normal, 1 grey, 2 +albedo, 3 +roughness, 4 +normalmap
    private bool _lastHzKey;    // P A/Bs horizon shadows (hz_on)
    private bool _hzOn = true;  // mirror of hz_on; MUST match the shader/JSON default or the first P-press no-ops
    private bool _lodVizLive;   // V toggles the LOD-band tint live
    private bool _terrainCloudShadowOn = false;  // opt-in terrain receive for cloud shadows; . flips it once the cloud RID is live
    private bool _godraysOn = true;               // god rays default on; F6 flips it
    private bool _analyticOn = true;        // ground source: live field (default) vs baked; toggled by key 1
    private float _inspectEnergy = 1.0f;   // L-light brightness (Night tab 'inspect light')

    private const int LiveProfileWindowFrames = 512;
    private readonly LiveProfileSample[] _liveProfile = new LiveProfileSample[LiveProfileWindowFrames];
    private int _liveProfileIndex, _liveProfileCount;

    private readonly struct LiveProfileSample
    {
        public readonly double Ms;
        public readonly long VisDraw, VisObj, VisPrim, ShDraw, ShObj, ShPrim;
        public readonly int Snaps, Births, Rebirths, Active;

        public LiveProfileSample(double ms, long visDraw, long visObj, long visPrim, long shDraw, long shObj, long shPrim, int snaps, int births, int rebirths, int active)
        {
            Ms = ms;
            VisDraw = visDraw; VisObj = visObj; VisPrim = visPrim;
            ShDraw = shDraw; ShObj = shObj; ShPrim = shPrim;
            Snaps = snaps; Births = births; Rebirths = rebirths; Active = active;
        }
    }

    private void RecordLiveProfile(double delta)
    {
        var vp = GetViewport();
        var cd = _terrain.Cdlod;
        _liveProfile[_liveProfileIndex] = new LiveProfileSample(
            delta * 1000.0,
            vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.DrawCallsInFrame),
            vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.ObjectsInFrame),
            vp.GetRenderInfo(Viewport.RenderInfoType.Visible, Viewport.RenderInfo.PrimitivesInFrame),
            vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.DrawCallsInFrame),
            vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.ObjectsInFrame),
            vp.GetRenderInfo(Viewport.RenderInfoType.Shadow, Viewport.RenderInfo.PrimitivesInFrame),
            cd?.TotalSnaps ?? 0,
            cd?.TotalBirths ?? 0,
            cd?.TotalRebirths ?? 0,
            cd?.ActiveCount ?? 0);
        _liveProfileIndex = (_liveProfileIndex + 1) % LiveProfileWindowFrames;
        if (_liveProfileCount < LiveProfileWindowFrames) { _liveProfileCount++; }
    }

    private static double Percentile(double[] samples, double pct)
    {
        if (samples.Length == 0) { return 0.0; }
        Array.Sort(samples);
        int idx = (int)Math.Ceiling(samples.Length * pct / 100.0) - 1;
        if (idx < 0) { idx = 0; }
        if (idx >= samples.Length) { idx = samples.Length - 1; }
        return samples[idx];
    }

    private static double FpsFromMs(double ms) => ms > 0.0 ? 1000.0 / ms : 0.0;

    private void DumpLiveProfile()
    {
        if (_liveProfileCount <= 0) { return; }

        double sumMs = 0.0, worstMs = 0.0;
        long visDrawSum = 0, visObjSum = 0, visPrimSum = 0, shDrawSum = 0, shObjSum = 0, shPrimSum = 0;
        long visDrawMax = 0, visObjMax = 0, visPrimMax = 0, shDrawMax = 0, shObjMax = 0, shPrimMax = 0;
        double[] ms = new double[_liveProfileCount];
        LiveProfileSample[] spikes = new LiveProfileSample[Math.Min(5, _liveProfileCount)];

        int firstIdx = _liveProfileCount == LiveProfileWindowFrames ? _liveProfileIndex : 0;
        LiveProfileSample first = _liveProfile[firstIdx];
        LiveProfileSample last = first;
        for (int i = 0; i < _liveProfileCount; i++)
        {
            int idx = _liveProfileCount == LiveProfileWindowFrames ? (_liveProfileIndex + i) % LiveProfileWindowFrames : i;
            LiveProfileSample s = _liveProfile[idx];
            last = s;
            ms[i] = s.Ms;
            sumMs += s.Ms; worstMs = Math.Max(worstMs, s.Ms);
            visDrawSum += s.VisDraw; visObjSum += s.VisObj; visPrimSum += s.VisPrim;
            shDrawSum += s.ShDraw; shObjSum += s.ShObj; shPrimSum += s.ShPrim;
            visDrawMax = Math.Max(visDrawMax, s.VisDraw); visObjMax = Math.Max(visObjMax, s.VisObj); visPrimMax = Math.Max(visPrimMax, s.VisPrim);
            shDrawMax = Math.Max(shDrawMax, s.ShDraw); shObjMax = Math.Max(shObjMax, s.ShObj); shPrimMax = Math.Max(shPrimMax, s.ShPrim);

            for (int j = 0; j < spikes.Length; j++)
            {
                if (s.Ms <= spikes[j].Ms) { continue; }
                for (int k = spikes.Length - 1; k > j; k--) { spikes[k] = spikes[k - 1]; }
                spikes[j] = s;
                break;
            }
        }

        double avgMs = sumMs / _liveProfileCount;
        double p50 = Percentile((double[])ms.Clone(), 50.0);
        double p95 = Percentile((double[])ms.Clone(), 95.0);
        double p99 = Percentile(ms, 99.0);
        long frames = Math.Max(_liveProfileCount, 1);
        var lines = new List<string>
        {
            $"LIVEPROFILE: frames={_liveProfileCount} avg {FpsFromMs(avgMs):0} fps ({avgMs:0.0} ms) worst {FpsFromMs(worstMs):0} fps ({worstMs:0.0} ms)",
            $"LIVEPROFILE-PERCENTILES: p50 {FpsFromMs(p50):0} fps ({p50:0.0} ms) p95 {FpsFromMs(p95):0} fps ({p95:0.0} ms) p99 {FpsFromMs(p99):0} fps ({p99:0.0} ms)",
            $"LIVEPROFILE-RENDER-AVG: visible draws={visDrawSum / frames} objects={visObjSum / frames} prim={visPrimSum / frames} | shadow draws={shDrawSum / frames} objects={shObjSum / frames} prim={shPrimSum / frames}",
            $"LIVEPROFILE-RENDER-MAX: visible draws={visDrawMax} objects={visObjMax} prim={visPrimMax} | shadow draws={shDrawMax} objects={shObjMax} prim={shPrimMax}",
            $"LIVEPROFILE-STREAM-DELTA: snaps={last.Snaps - first.Snaps} births={last.Births - first.Births} rebirths={last.Rebirths - first.Rebirths} active={last.Active}"
        };
        var cd = _terrain.Cdlod;
        if (cd != null && cd.Enabled)
        {
            cd.ActiveDiagnostics(out int near, out int far, out int shadowCasters, out int cacheReady);
            lines.Add($"LIVEPROFILE-CDLOD: active={cd.ActiveCount} near={near} far={far} shadowCasters={shadowCasters} cacheReady={cacheReady}");
        }
        for (int i = 0; i < spikes.Length; i++)
        {
            LiveProfileSample s = spikes[i];
            lines.Add($"LIVEPROFILE-SPIKE: rank={i + 1} {FpsFromMs(s.Ms):0} fps ({s.Ms:0.0} ms) visible draws={s.VisDraw} objects={s.VisObj} prim={s.VisPrim} | shadow draws={s.ShDraw} objects={s.ShObj} prim={s.ShPrim} | stream snaps={s.Snaps - first.Snaps} births={s.Births - first.Births} rebirths={s.Rebirths - first.Rebirths} active={s.Active}");
        }

        string dir = ProjectSettings.GlobalizePath("res://artifacts");
        System.IO.Directory.CreateDirectory(dir);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string path = System.IO.Path.Combine(dir, $"live_profile_{stamp}.txt");
        string latest = System.IO.Path.Combine(dir, "live_profile_last.txt");
        System.IO.File.WriteAllLines(path, lines);
        System.IO.File.WriteAllLines(latest, lines);
        foreach (string line in lines) { GD.Print(line); }
        GD.Print($"LIVEPROFILE-WROTE: {path}");
    }

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
        // The --objectlistcheck / --lumpresetcheck flags early-return from _Ready (before the extracted
        // modules below are constructed) and call Quit(); Godot DEFERS the quit, so _Process still runs once
        // on the quit frame. Bail before touching any not-yet-built module (_cliSeq is the first one built).
        if (_cliSeq == null) { return; }

        _lumCheck.Tick(delta);   // U2 numeric gate (--luminarycheck): no-op unless armed

        _cliSeq.TickProfMove(delta);   // --profmove: orbit the camera during a profile (motion costs) — Phase 1c

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
        // AT-2 v2: bind the froxel LUT Texture3Drd once the compute is ready, then push camera each frame.
        if (_aerialV2On && !_aerialV2Activated && _atmosphere != null && _atmosphere.AerialReady && _aerialV2 != null)
        {
            _aerialV2.SetAerialTexture(_atmosphere.AerialTexture);
            _aerialV2.SetEnabled(true);
            _aerialV2Activated = true;
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
            // --profmove drives a TRUE-world traverse: take its position directly (NOT camN.Position+renderOrigin,
            // which would re-add renderOrigin to an already-true value → per-frame snap runaway once it leaves
            // cell 0). The co-location below puts camN back into the render frame so the render is correct.
            Vector3 camPos = _cliSeq.ProfMoveActive ? _cliSeq.ProfTruePos : camN.Position + renderOrigin;   // TRUE world camera position
            _terrain.SetCameraWorld(camPos);
            // ARC B Task 4: smoothed TRUE-world XZ velocity (m/s) → predictive loading bias. camPos is continuous
            // across renderOrigin snaps, so a frame delta is clean. Frozen scene (TimeScale=0 → delta≈0) → vel 0.
            if (_camVelInit && delta > 1e-5)
            {
                Vector3 inst = (camPos - _lastCamVelPos) / (float)delta; inst.Y = 0f;
                _camVelSmoothed = _camVelSmoothed.Lerp(inst, 0.12f);   // ~0.1 s time constant @ 60 fps
            }
            _lastCamVelPos = camPos; _camVelInit = true;
            _terrain.CdlodTick(camPos, _camVelSmoothed);   // S2a: rebuild the visible chunk set; S3: snaps the new renderOrigin
            Vector3 newOrigin2 = _terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero;   // may have snapped inside CdlodTick
            if (_terrain.CdlodActive)
            {
                camN.Position = camPos - newOrigin2;              // co-locate the camera with the render frame
            }
            // --profmove: aim the camera along the traverse, in the RENDER frame (look target − origin).
            if (_cliSeq.ProfMoveActive)
            {
                Vector3 look = _cliSeq.ProfLookTarget - newOrigin2;
                if ((look - camN.Position).LengthSquared() > 0.001f) { camN.LookAt(look, Vector3.Up); }
            }
            // Water meshes are authored in TRUE world XZ; shift the node by −renderOrigin so they line up with
            // the render-relative terrain (same floating-origin frame as the CDLOD chunks).
            _waterRenderer?.SetRenderOrigin(_terrain.CdlodActive ? _terrain.CdlodRenderOrigin : Vector3.Zero);
            _cloud?.SetCameraWorld(camPos);
            // AT-2 v2: push camera in the SAME render-relative frame as inv_view_proj.
            // Clouds/terrain still need TRUE-world camPos above; the aerial LUT only needs
            // altitude + view ray direction, so mixing true XZ with render-relative wf would
            // skew the ray direction at each floating-origin snap.
            if (_aerialV2On && _atmosphere != null)
            {
                var proj = camN.GetCameraProjection();
                var vp = proj * new Godot.Projection(camN.GlobalTransform.AffineInverse());
                _atmosphere.SetAerialCamera(camN.GlobalPosition, camN.Far, vp.Inverse());
            }
            TickPopMeter(camPos);   // S3 --popmeter: live per-frame pop/snap measurement (no-op unless armed)

            // Inspection light (L): toggle a fixed-angle studio directional to check surfaces.
            bool lDown = Input.IsKeyPressed(Key.L);
            if (lDown && !_lastLDown) { ToggleInspectLight(); }
            _lastLDown = lDown;

            // --- Debug isolation bank (N M , . /) ----------------------------------------------------------
            // Live-flip each camera-locked screen-space effect to pin a "dots in a shifting ring" artifact to
            // its source. Each prints its new state. (Moved off F-keys: those get grabbed by the OS/IDE and
            // never reach the game window.) N SSIL, M SDFGI, , sun shadows, . cloud-shadow, / god rays.
            {
                var env = UiEnv.Environment;
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
                if (kPeriod && !_lastF5) { _terrainCloudShadowOn = !_terrainCloudShadowOn; _cloud?.SetShadowMapWanted(_terrainCloudShadowOn); _terrain.SetBool("cloud_shadow_on", _terrainCloudShadowOn && (_cloud?.Enabled ?? true)); GD.Print($"[dbg] (.) Cloud shadow on terrain = {_terrainCloudShadowOn}"); }
                _lastF5 = kPeriod;
                bool kSlash = Input.IsKeyPressed(Key.Slash);
                if (kSlash && !_lastF6) { _godraysOn = !_godraysOn; _godraysScreen?.SetEnabled(_godraysOn); GD.Print($"[dbg] (/) God rays = {_godraysOn}"); }
                _lastF6 = kSlash;
                // O atmosphere (AT-1 GPU sky tint), K aerial perspective (AT-2 camera froxel) — the two
                // camera-aligned volumes NOT covered above; froxel volumes are the classic concentric-ring suspect.
                // (O not J — J is the ring-hunt diag stepper below.)
                bool kO = Input.IsKeyPressed(Key.O);
                if (kO && !_lastF7) { _atmosphereOn = !_atmosphereOn; _cloud.SetAtmosphereOn(_atmosphereOn); _atmosphere?.SetEnabled(_atmosphereOn); GD.Print($"[dbg] (O) Atmosphere AT-1 = {_atmosphereOn}"); }
                _lastF7 = kO;
                // K: A/B aerial perspective AT-2 v2 (64-slice log-Z froxel haze).
                bool kK = Input.IsKeyPressed(Key.K);
                if (kK && !_lastF8) { _aerialV2On = !_aerialV2On; _atmosphere?.SetAerialEnabled(_aerialV2On); _aerialV2?.SetEnabled(_aerialV2On); _aerialV2Activated = false; GD.Print($"[dbg] (K) Aerial AT-2 v2 = {_aerialV2On}"); }
                _lastF8 = kK;
                // P: A/B horizon shadows (long-range terrain self-shadow). Best seen at a LOW sun.
                bool kP = Input.IsKeyPressed(Key.P);
                if (kP && !_lastHzKey) { _hzOn = !_hzOn; _terrain.SetBool("hz_on", _hzOn); GD.Print($"[dbg] (P) Horizon shadows = {_hzOn}"); }
                _lastHzKey = kP;
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
                bool kB = Input.IsKeyPressed(Key.B);
                if (kB && !_lastLiveProfileDump) { DumpLiveProfile(); }
                _lastLiveProfileDump = kB;
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
        if (_ready) { RecordLiveProfile(delta); }
        // L2: enable terrain shadow sampling once the cloud shadow map's RID is live.
        if (!_shadowEnabledOnce && _cloud != null && _cloud.ComputeReady && _cloud.Enabled)
        {
            _terrain.SetBool("cloud_shadow_on", _terrainCloudShadowOn && _cloud.Enabled);
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
                float spd = _camVelSmoothed.Length();   // smoothed TRUE-world XZ speed (m/s)
                _fpsLabel.Text = $"{1.0 / avg,5:0} fps   {avg * 1000.0,5:0.0} ms\n{spd,7:0} m/s   {spd * 3.6f,8:0} km/h";
                _fpsAccum = 0; _fpsFrames = 0;
            }
        }

        _cliSeq.Tick(delta);   // Phase 1c: --auto-shot / --godrayab / --fillab / --profile capture+measure sequences
    }
}
