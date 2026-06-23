using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using WG16.Field;

namespace WG16.Lab;

public partial class TerrainLabUI : Control
{
    // Headless A/B capture + overrides (CLI verification), unchanged contract.
    private string? _autoShotPath;
    private double _autoShotT = -1.0;
    private int _overrideBlend = -1, _overrideMask = -1, _overrideTile = -1, _overrideMacro = -1, _overrideContact = -1;
    private int _overrideSplat = -1, _overrideSplatDebug = -1;
    private string? _camArg;
    private float _texScale = -1f;
    private int _probeSsao = -1, _probeShadow = -1, _probeHb = -1, _probeMood = -1;   // lighting/splat isolation
    private int _probeSdfgi = -1;   // --sdfgi=0/1: isolate the real-time GI cost (perf pass)
    private float _probeRoughFloor = -1f, _probeMixStr = -1f;

    private float _covOverride = -1f;
    private float _perDeckCli = -1f;   // --perdeck=0/1: A/B per-deck lighting (-1 = leave default ON)
    private string _cloudProfileCli = "";   // --cloudprofile=b,t,a: enable CO-1 vertical profile + set bottom/top/anvil
    private float _stratusCli = -1f;         // --stratus[=v]: CO-2 stratus shape-mode (0..1); bare flag = 1
    private float _cirrusCli = -1f;          // --cirrus[=cov]: CO-2 enable cirrus + set coverage; bare flag = 0.5
    private float _antiRepeatCli = -1f;      // --antirepeat[=v]: CO-3 cumulus macro variety (0..1); bare flag = 1
    private float _autoTimeCli = -1f;        // --autotime[=speed]: ST4-1 start the day/night clock; bare flag = 1.0 h/s
    private bool _lightCheckCli = false;   // --lightcheck: print per-deck lighting difference math
    private int _deckDbgCli = -1;          // --deckdbg=1: deck-ID overlay (flat color per deck)
    private bool _cloudStatsCli = false;   // --cloudstats: read back dome, print coverage/brightness
    private int _presetCli = -1;           // --preset=N: apply cloud preset N at startup (test layer stacks)
    private int _sunPresetCli = -1;        // --sunpreset=N: apply sun-disc preset N at startup
    private int _celestialCli = -1;        // --celestial=N: apply celestial (moon/stars/night) preset N at startup
    private int _fantasyCli = -1;          // --fantasy=N: apply fantasy/exotic cross-system preset N at startup (ST4-2)
    private int _temporalCli = 0;          // --temporal=N: temporal amortization stride (roadmap #4)
    private int _godraysOnCli = -1;
    private int _godrayDbgCli = 0;   // --godraydbg=N → GodRaysScreen debug_mode (1=mask, 2=sun pos)
    private float _godrayHpCli = -1f;   // --godrayhp=N → high-pass amount (0 = old wash+ring, 1 = clean beams); A/B verify
    private int _glowCli = -1;   // --glow=0/1 → force env glow off/on (isolate post-processing effects)
    private bool _lookAtSunCli = false;   // --lookatsun → aim camera at the sun on startup (god-ray verify)
    private bool _lookAtMoonCli = false;  // --lookatmoon → aim camera at the moon on startup (3b moon gate)
    private float _timeCli = -1f;   // --time=H → drive the decoupled Time axis (sun arc + day color script)
    private int _sunsCli = -1;      // --suns=N → total suns (1=default; N-1 become C3 extra suns)
    private int _moonsCli = -1;     // --moons=N → total moons (1=default; N-1 become C3 extra moons)
    private bool _nightGate;        // --nightgate=1 → review keys 1-9 jump to data/review_night.json states (Stage 3a)
    private float _nightDarkCli = -1f;   // --nightdark=N → set night_darkness (night brightness lever) at startup for A/B
    private float _moonPhaseCli = -1f;   // --moonphase=N → set moon_phase (0 new .. 1 full) at startup for A/B
    private int _atmosphereCli = -1;     // --atmosphere[=1] → enable the AT-1 GPU physical sky at startup
    private bool _atmoCheckCli;          // --atmoscheck → one-shot LUT numeric self-check (readback)
    private bool _aerialCheckCli;        // --aerialcheck → one-shot AT-2 aerial froxel self-check (readback)
    private int _aerialCli = -1;         // --aerial[=1] → AT-2 aerial perspective on/off at startup (default ON; =0 restores built-in fog)
    private float _aerialStrCli = -1f;   // --aerialstr=N → AT-2 in-scatter strength override at startup (A/B)
    private int _cloudLightCli = -1;     // --cloudlight[=1] → AT-3 physical cloud lighting on at startup (default off)
    private float _cloudLightStrCli = -1f; // --cloudlightstr=N → AT-3 cloud-light strength override
    private float _atmoExpCli = -1f;     // --atmoexp=N → AT-1 atmosphere exposure override
    private int _reviewCli = -1;         // --review=N → run ApplyReview(N) at startup (drive/verify a review preset headlessly)
    private bool _meteorDebugCli;        // --meteordebug → force a meteor streak (C2 headless capture)
    private int _greviewCli = -1;        // --greview=N → run ApplyGroundReview(N) at startup (Shift+N ground bank; auto-shoot the ground gates)
    private int _terrainArCli = -1;
    private int _terrainDetailCli = -1;
    private int _groundRulesCli = -1;
    private int _giProxyCli = -1;
    private int _proxyResCli = -1;
    private int _analyticCli = -1;   // --analytic[=0|1] → S1 ground source: live field vs baked (default: leave shader default ON)
    private int _texturesCli = -1;   // --textures[=0|1] → minimal surfacing slice on/off at startup
    private bool _cdlodTestCli;      // --cdlodtest → S2a Task-1 sanity: one full-region chunk instance
    private int _cdlodCli = -1;      // --cdlod[=1] → S2a quadtree terrain instead of the single mesh
    private int _testPathCli = -1;   // --testpath=N → run S2b LOD-crossing test path N (0-based) headlessly, then quit
    private int _lodVizCli = -1;     // --lodviz[=1] → tint chunks by LOD level
    private int _groundV2Cli = -1;   // --groundv2[=1] → swap to the new per-pixel ground skin at startup (A/B / auto-shots)
    private int _gv2DebugCli = -1;   // --gv2debug=N → ground v2 debug view (1 = placement viz) at startup

    private void ParseCli()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--auto-shot=")) { _autoShotPath = a.Substring("--auto-shot=".Length); _autoShotT = 0.0; }
            else if (a.StartsWith("--blend=")) { int.TryParse(a.Substring("--blend=".Length), out _overrideBlend); }
            else if (a.StartsWith("--mask=")) { int.TryParse(a.Substring("--mask=".Length), out _overrideMask); }
            else if (a.StartsWith("--tile=")) { int.TryParse(a.Substring("--tile=".Length), out _overrideTile); }
            else if (a.StartsWith("--macro=")) { _overrideMacro = a.Substring("--macro=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--contact=")) { _overrideContact = a.Substring("--contact=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splat=")) { _overrideSplat = a.Substring("--splat=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splatdebug=")) { int.TryParse(a.Substring("--splatdebug=".Length), out _overrideSplatDebug); }
            else if (a.StartsWith("--cam=")) { _camArg = a.Substring("--cam=".Length); }
            else if (a.StartsWith("--texscale=")) { if (float.TryParse(a.Substring("--texscale=".Length), out float ts)) _texScale = ts; }
            else if (a.StartsWith("--ssao=")) { _probeSsao = a.Substring("--ssao=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--sdfgi=")) { _probeSdfgi = a.Substring("--sdfgi=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--shadow=")) { _probeShadow = a.Substring("--shadow=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--roughfloor=")) { if (float.TryParse(a.Substring("--roughfloor=".Length), out float rf)) _probeRoughFloor = rf; }
            else if (a.StartsWith("--mixstr=")) { if (float.TryParse(a.Substring("--mixstr=".Length), out float ms)) _probeMixStr = ms; }
            else if (a.StartsWith("--hb=")) { _probeHb = a.Substring("--hb=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--mood=")) { int.TryParse(a.Substring("--mood=".Length), out _probeMood); }
            else if (a.StartsWith("--clouddbg=")) { int.TryParse(a.Substring("--clouddbg=".Length), out _cloudDbg); }
            else if (a.StartsWith("--cloudsteps=")) { int.TryParse(a.Substring("--cloudsteps=".Length), out _cloudSteps); }
            else if (a.StartsWith("--clouds=")) { _cloudsOn = a.Substring("--clouds=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--coverage=")) { float.TryParse(a.Substring("--coverage=".Length), out _covOverride); }
            else if (a.StartsWith("--perdeck=")) { if (float.TryParse(a.Substring("--perdeck=".Length), out float pd)) _perDeckCli = pd; }
            else if (a.StartsWith("--cloudprofile=")) { _cloudProfileCli = a.Substring("--cloudprofile=".Length); }
            else if (a.StartsWith("--stratus")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; if (float.TryParse(s, out float sv)) { _stratusCli = sv; } }
            else if (a.StartsWith("--cirrus")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "0.5"; if (float.TryParse(s, out float cv)) { _cirrusCli = cv; } }
            else if (a.StartsWith("--antirepeat")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; if (float.TryParse(s, out float av)) { _antiRepeatCli = av; } }
            else if (a.StartsWith("--autotime")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; if (float.TryParse(s, out float tv)) { _autoTimeCli = tv; } }
            else if (a.StartsWith("--deckdbg=")) { _deckDbgCli = a.Substring("--deckdbg=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--godrays=")) { _godraysOnCli = a.Substring("--godrays=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--godraydbg=")) { int.TryParse(a.Substring("--godraydbg=".Length), out _godrayDbgCli); }
            else if (a.StartsWith("--godrayhp=")) { float.TryParse(a.Substring("--godrayhp=".Length), out _godrayHpCli); }
            else if (a.StartsWith("--godrayab=")) { _godrayAbPath = a.Substring("--godrayab=".Length); _godrayAbT = 0.0; }
            else if (a.StartsWith("--glow=")) { _glowCli = a.Substring("--glow=".Length) == "1" ? 1 : 0; }
            else if (a == "--lookatsun") { _lookAtSunCli = true; }
            else if (a == "--lookatmoon") { _lookAtMoonCli = true; }
            else if (a.StartsWith("--time=")) { float.TryParse(a.Substring("--time=".Length), out _timeCli); }
            else if (a.StartsWith("--suns=")) { int.TryParse(a.Substring("--suns=".Length), out _sunsCli); }
            else if (a.StartsWith("--moons=")) { int.TryParse(a.Substring("--moons=".Length), out _moonsCli); }
            else if (a.StartsWith("--nightgate=")) { _nightGate = a.Substring("--nightgate=".Length) == "1"; }
            else if (a.StartsWith("--nightdark=")) { float.TryParse(a.Substring("--nightdark=".Length), out _nightDarkCli); }
            else if (a.StartsWith("--moonphase=")) { float.TryParse(a.Substring("--moonphase=".Length), out _moonPhaseCli); }
            else if (a.StartsWith("--ar=")) { _terrainArCli = a.Substring("--ar=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--detail=")) { _terrainDetailCli = a.Substring("--detail=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--groundrules=")) { _groundRulesCli = a.Substring("--groundrules=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--giproxy=")) { _giProxyCli = a.Substring("--giproxy=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--proxyres=")) { if (int.TryParse(a.Substring("--proxyres=".Length), out int pr)) _proxyResCli = pr; }
            else if (a == "--cdlodtest") { _cdlodTestCli = true; }
            else if (a == "--cdlodcheck") { _cdlodCheckCli = true; }   // exact-match BEFORE StartsWith("--cdlod") or it gets shadowed
            else if (a == "--morphcheck") { _morphCheckCli = true; }   // S2b: geomorph C0-continuity / pop-free numeric backstop
            else if (a == "--stitchcheck") { _stitchCheckCli = true; }   // S2d: edge-stitch crack-free numeric guard
            else if (a == "--streamcheck") { _streamCheckCli = true; }   // S3: streaming invariant + snap-continuity guard
            else if (a == "--popcheck") { _popCheckCli = true; }   // S3: pop detector — fixed-point sample-XZ continuity across a moving cam
            else if (a == "--snapdiff") { _snapDiffCli = true; }   // S3: renderOrigin-snap seamlessness check
            else if (a == "--pinorigin") { _pinOriginCli = true; }   // DEBUG: pin renderOrigin=0 (isolate snap-pop)
            else if (a == "--debugwxz") { _debugWxzCli = true; }   // DEBUG: shader outputs sampled world-XZ as color
            else if (a == "--nofog") { _noFogCli = true; }   // REVIEW: kill all atmospheric haze (env fog + aerial + atmosphere + volfog)
            else if (a == "--popmeter") { _popMeterCli = true; }   // S3: LIVE pop meter — measure height/normal/origin-snap each frame as you fly
            else if (a == "--aabbspike") { _aabbSpikeCli = true; }   // S3.5: one async GPU height-range vs sync, prints AABBSPIKE
            else if (a == "--notighten") { _noTightenCli = true; }   // S3.5: disable async AABB tighten → generous AABB fallback
            else if (a.StartsWith("--aabbres=")) { int.TryParse(a.Substring("--aabbres=".Length), out _aabbResCli); }   // S3.5: ProbeRes
            else if (a.StartsWith("--aabbreq=")) { int.TryParse(a.Substring("--aabbreq=".Length), out _aabbReqCli); }   // S3.5: MaxRequestsPerFrame
            else if (a.StartsWith("--cdlod")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cdlodCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--lodviz")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _lodVizCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--testpath=")) { int.TryParse(a.Substring("--testpath=".Length), out _testPathCli); }   // S2b: run LOD-crossing test path N, print report, quit
            else if (a.StartsWith("--analytic")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _analyticCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--textures")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _texturesCli = (s == "1") ? 1 : 0; }   // minimal surfacing slice on/off
            else if (a.StartsWith("--shadowdbg=")) { _shadowDbgCli = a.Substring("--shadowdbg=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--atmosphere")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _atmosphereCli = (s == "1") ? 1 : 0; }
            else if (a == "--atmoscheck") { _atmoCheckCli = true; }
            else if (a == "--aerialcheck") { _aerialCheckCli = true; }
            else if (a.StartsWith("--aerialstr=")) { float.TryParse(a.Substring("--aerialstr=".Length), out _aerialStrCli); }
            else if (a.StartsWith("--aerial")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _aerialCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--cloudlightstr=")) { float.TryParse(a.Substring("--cloudlightstr=".Length), out _cloudLightStrCli); }
            else if (a.StartsWith("--cloudlight")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cloudLightCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--atmoexp=")) { float.TryParse(a.Substring("--atmoexp=".Length), out _atmoExpCli); }
            else if (a.StartsWith("--greview=")) { int.TryParse(a.Substring("--greview=".Length), out _greviewCli); }
            else if (a.StartsWith("--review=")) { int.TryParse(a.Substring("--review=".Length), out _reviewCli); }
            else if (a == "--meteordebug") { _meteorDebugCli = true; }
            else if (a == "--profmove") { _profMove = true; }
            else if (a == "--shadowcheck") { _shadowCheckCli = true; }
            else if (a == "--fieldcheck") { _fieldCheckCli = true; }
            else if (a == "--lightcheck") { _lightCheckCli = true; }
            else if (a == "--cloudstats") { _cloudStatsCli = true; }
            else if (a.StartsWith("--preset=")) { int.TryParse(a.Substring("--preset=".Length), out _presetCli); }
            else if (a.StartsWith("--sunpreset=")) { int.TryParse(a.Substring("--sunpreset=".Length), out _sunPresetCli); }
            else if (a.StartsWith("--celestial=")) { int.TryParse(a.Substring("--celestial=".Length), out _celestialCli); }
            else if (a.StartsWith("--fantasy=")) { int.TryParse(a.Substring("--fantasy=".Length), out _fantasyCli); }
            else if (a.StartsWith("--cloudtex=")) { if (int.TryParse(a.Substring("--cloudtex=".Length), out int th) && th >= 64) { CloudVolume.TexH = th; CloudVolume.TexW = th * 4; } }
            else if (a.StartsWith("--temporal=")) { int.TryParse(a.Substring("--temporal=".Length), out _temporalCli); }
            else if (a.StartsWith("--watercheck=")) { _waterCheck = a.Substring("--watercheck=".Length); }
            else if (a.StartsWith("--water")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _waterCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--profile")) { _profileT = 0.0; if (a.Contains("=") && double.TryParse(a.Substring(a.IndexOf('=')+1), out double d)) _profileDur = d;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0; }
        }
    }
    private string? _waterCheck;   // --watercheck=<name> → run a HydrologyChecks self-check then quit
    private int _waterCli = -1;     // --water[=1] → bind region (0,0)'s water texture + carve for eye-gate
    private WG16.Hydrology.WorldWaterRegion? _waterRegion;   // kept for Task 7 mesh spawn
    private WG16.Hydrology.WaterParams? _waterParams;        // kept for Task 7/8
    private Material? _waterMat;                             // kept for Task 7/8

    private void ApplyCliOverrides()
    {
        if (_waterCheck != null) { WG16.Hydrology.HydrologyChecks.Run(_waterCheck); GetTree().Quit(); return; }
        if (_overrideMask >= 0) { OverrideEnum("mask_mode", _overrideMask); }
        if (_overrideBlend >= 0) { OverrideEnum("blend_mode", _overrideBlend); }
        if (_overrideTile >= 0) { OverrideEnum("tile_mode", _overrideTile); }
        if (_overrideSplatDebug >= 0) { OverrideEnum("splat_debug", _overrideSplatDebug); }
        if (_overrideMacro >= 0) { OverrideToggle("macro_on", _overrideMacro == 1); }
        if (_overrideContact >= 0) { OverrideToggle("contact_on", _overrideContact == 1); }
        if (_overrideSplat >= 0) { OverrideToggle("splat_on", _overrideSplat == 1); }
        if (_texScale > 0f && _byId.TryGetValue("tex_scale_m", out LabControl ts)) { SetWidgetValue(ts, _texScale); }
        if (_camArg != null)
        {
            string[] p = _camArg.Split(',');
            if (p.Length >= 5)
            {
                var cam = GetNode<Camera3D>("/root/TerrainLabRoot/Camera");
                cam.Position = new Vector3(float.Parse(p[0]), float.Parse(p[1]), float.Parse(p[2]));
                cam.RotationDegrees = new Vector3(float.Parse(p[3]), float.Parse(p[4]), 0f);
            }
        }
        // Lighting isolation probes (fuzz hunt).
        if (_probeSsao >= 0)
        {
            var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
            env.Environment.SsaoEnabled = _probeSsao == 1;
        }
        if (_probeShadow >= 0)
        {
            var sun = GetNode<DirectionalLight3D>("/root/TerrainLabRoot/Sun");
            sun.ShadowEnabled = _probeShadow == 1;
        }
        if (_probeSdfgi >= 0)
        {
            var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env");
            env.Environment.SdfgiEnabled = _probeSdfgi == 1;
        }
        if (_probeRoughFloor >= 0f) { _terrain.SetFloat("rough_floor", _probeRoughFloor); }
        if (_probeMixStr >= 0f) { _terrain.SetFloat("mix_strength", _probeMixStr); }
        if (_probeHb >= 0) { _terrain.SetBool("heightblend_on", _probeHb == 1); }
        if (_proxyResCli >= 0) { _terrain.SetProxyRes(_proxyResCli); }
        if (_giProxyCli >= 0) { _terrain.SetGiProxy(_giProxyCli == 1); }
        if (_analyticCli >= 0) { _analyticOn = _analyticCli == 1; _terrain.SetAnalytic(_analyticOn); }   // keep key-1 toggle in sync with the CLI default
        if (_texturesCli >= 0) { _terrain.SetTexturesOn(_texturesCli == 1); }   // minimal surfacing slice
        if (_cdlodTestCli) { _terrain.SetAnalytic(true); _terrain.CdlodTestOneChunk(_params); }   // S2a Task-1 sanity
        if (_cdlodCli >= 0) { _terrain.SetAnalytic(true); _terrain.SetCdlod(_cdlodCli == 1); }   // S2a quadtree terrain
        if (_lodVizCli >= 0) { _terrain.SetCdlodViz(_lodVizCli == 1); }
        // S3.5: async AABB tighten tunables (--notighten / --aabbres= / --aabbreq=). Only meaningful with CDLOD on.
        if (_cdlodCli == 1 && (_noTightenCli || _aabbResCli > 0 || _aabbReqCli > 0))
        {
            _terrain.ConfigureCdlodAabb(!_noTightenCli, _aabbResCli, _aabbReqCli);
        }
        if (_waterCli == 1)
        {
            _waterParams = WG16.Hydrology.WaterParams.Load();
            _waterRegion = new WG16.Hydrology.WorldWaterRegion(_params, _waterParams, 0, 0);
            _terrain.BindWaterRegion(_waterRegion, _waterParams);   // Task 6: carve. Task 7 adds meshes.
            _terrain.SetBool("water_debug", true);                  // start with the overlay ON (key H toggles)
            GD.Print($"[water] region(0,0) rivers={_waterRegion.Rivers.Count} lakes={_waterRegion.Lakes.Count} " +
                     $"— overlay ON (key H), carve ON. Fly near world origin (0..8192).");
        }
        if (_popMeterCli) { BuildPopMeterHud(); }   // S3 live pop meter — HUD line; the meter inits lazily on first tick
        if (_pinOriginCli) { _terrain.SetPinOrigin(true); }   // DEBUG: pin renderOrigin=0
        if (_debugWxzCli) { _terrain.SetFloat("debug_wxz", 1.0f); }   // DEBUG: shader world-XZ color
        // S2b: --testpath=N. Deferred so the _testPaths sibling-add + SetupTestPaths (both deferred from
        // TerrainLab.Build) have completed before we Start the flight.
        if (_testPathCli >= 0) { CallDeferred(nameof(StartTestPathDeferred)); }
        if (_probeMood >= 0) { ApplyMood(_probeMood); }
        if (_noFogCli) { KillAllFog(); }   // REVIEW: last, so the mood/composer can't re-enable fog
    }

    /// REVIEW (--nofog): kill EVERY atmospheric haze layer so the raw terrain is visible — env distance fog,
    /// volumetric fog, aerial perspective (AT-2), and the GPU atmosphere (AT-1). Applied after mood/compose so
    /// nothing re-enables it for a static review. Toggle the systems back via the Light/Debug tabs.
    private void KillAllFog()
    {
        var env = GetNode<WorldEnvironment>("/root/TerrainLabRoot/Env").Environment;
        env.FogEnabled = false;
        env.VolumetricFogEnabled = false;
        env.FogDensity = 0f;
        env.FogAerialPerspective = 0f;
        env.FogHeightDensity = 0f;
        _aerial?.SetEnabled(false);          // AT-2 screen-space aerial perspective
        _cloud?.SetAtmosphereOn(false);      // AT-1 physical sky tint on the terrain
        GD.Print("[nofog] all haze OFF (env fog / volfog / aerial / atmosphere) — raw terrain review");
    }
    private int _shadowDbgCli = -1;   // --shadowdbg=1 → paint the cloud-shadow map as terrain albedo (proof)
    private bool _shadowCheckCli;     // --shadowcheck → numeric correlation test, PASS/FAIL to console
    private bool _fieldCheckCli;      // --fieldcheck → one-shot field determinism/parity self-check (S1)
    private bool _cdlodCheckCli;      // --cdlodcheck → quadtree neighbor-invariant + stats self-check (S2a)
    private bool _morphCheckCli;      // --morphcheck → S2b geomorph pop-free numeric backstop (PASS/FAIL)
    private bool _stitchCheckCli;     // --stitchcheck → S2d edge-stitch seam-coincidence guard (PASS/FAIL)
    private bool _streamCheckCli;     // --streamcheck → S3 streaming invariant-along-traverse + snap field-continuity
    private bool _popCheckCli;        // --popcheck → S3 fixed-point morphed-sample-XZ continuity (pop detector)
    private bool _snapDiffCli;        // --snapdiff → S3 renderOrigin-snap seamless check (PASS/FAIL)
    private bool _pinOriginCli;       // --pinorigin → DEBUG pin renderOrigin=0
    private bool _debugWxzCli;        // --debugwxz → DEBUG shader world-XZ color
    private bool _noFogCli;           // --nofog → REVIEW kill all haze (fog+aerial+atmosphere)
    private bool _popMeterCli;        // --popmeter → S3 live per-frame pop meter (HUD + log) while you fly
    private bool _aabbSpikeCli;       // --aabbspike → S3.5 async GPU height-range feasibility spike (vs sync ref)
    private bool _noTightenCli;       // --notighten → S3.5 disable the async AABB tighten (generous-AABB fallback)
    private int _aabbResCli;          // --aabbres=N → S3.5 ChunkAabbProvider.ProbeRes (0 = leave default)
    private int _aabbReqCli;          // --aabbreq=N → S3.5 ChunkAabbProvider.MaxRequestsPerFrame (0 = leave default)
    private int _cloudDbg = -1;
    private int _cloudSteps = -1;
    private int _cloudsOn = -1;
    private void OverrideEnum(string id, int v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
    private void OverrideToggle(string id, bool v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
}
