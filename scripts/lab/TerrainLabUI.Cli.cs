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
    private LabCliSequences _cliSeq = null!;   // Phase 1c: CLI capture/profile sequences (auto-shot/godrayab/fillab/profile/profmove)
    private int _overrideBlend = -1, _overrideMask = -1, _overrideTile = -1, _overrideMacro = -1, _overrideContact = -1;
    private int _overrideSplat = -1, _overrideSplatDebug = -1;
    private string? _camArg;
    private float _texScale = -1f;
    private int _probeHb = -1, _probeMood = -1;   // lighting/splat isolation
    private float _probeRoughFloor = -1f, _probeMixStr = -1f;

    private float _covOverride = -1f;
    private float _cloudParallaxCli = -1f;   // --cloudparallax=0..1: visible sky-cloud response to camera XZ motion
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
    private int _cloudLightCli = -1;     // --cloudlight[=1] → AT-3 physical cloud lighting on at startup (default off)
    private float _cloudLightStrCli = -1f; // --cloudlightstr=N → AT-3 cloud-light strength override
    private float _atmoExpCli = -1f;     // --atmoexp=N → AT-1 atmosphere exposure override
    private int _reviewCli = -1;         // --review=N → run ApplyReview(N) at startup (drive/verify a review preset headlessly)
    private bool _meteorDebugCli;        // --meteordebug → force a meteor streak (C2 headless capture)
    private int _giProxyCli = -1;
    private int _proxyResCli = -1;
    private int _analyticCli = -1;   // --analytic[=0|1] → S1 ground source: live field vs baked (default: leave shader default ON)
    private int _texturesCli = -1;   // --textures[=0|1] → minimal surfacing slice on/off at startup
    private bool _cdlodTestCli;      // --cdlodtest → S2a Task-1 sanity: one full-region chunk instance
    // CDLOD default: ON in all scenes. --cdlod=0/1 overrides explicitly.
    private int _cdlodCli = -1;      // --cdlod[=0|1] → infinite quadtree terrain vs the single mesh
    private int _testPathCli = -1;   // --testpath=N → run S2b LOD-crossing test path N (0-based) headlessly, then quit
    private int _lodVizCli = -1;     // --lodviz[=1] → tint chunks by LOD level

    // Order-independent flag match: matches exactly "--foo" or "--foo=value". Using this for bare-prefix
    // flags prevents a StartsWith("--foo") from shadowing a longer flag that shares the prefix (e.g.
    // --aerial vs --aerialdbg=, --water vs --watercheck=) regardless of the if-chain ORDER — the #13 parse
    // hazard, where one reorder used to silently mis-parse. New prefix flags should use this too.

    private static bool MatchFlag(string a, string name) => a == name || a.StartsWith(name + "=");

    private void ParseCli()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--auto-shot=")) { _cliSeq.ArmAutoShot(a.Substring("--auto-shot=".Length)); }
            else if (a.StartsWith("--blend=")) { int.TryParse(a.Substring("--blend=".Length), out _overrideBlend); }
            else if (a.StartsWith("--mask=")) { int.TryParse(a.Substring("--mask=".Length), out _overrideMask); }
            else if (a.StartsWith("--tile=")) { int.TryParse(a.Substring("--tile=".Length), out _overrideTile); }
            else if (a.StartsWith("--macro=")) { _overrideMacro = a.Substring("--macro=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--contact=")) { _overrideContact = a.Substring("--contact=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splat=")) { _overrideSplat = a.Substring("--splat=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--splatdebug=")) { int.TryParse(a.Substring("--splatdebug=".Length), out _overrideSplatDebug); }
            else if (a.StartsWith("--cam=")) { _camArg = a.Substring("--cam=".Length); }
            else if (a.StartsWith("--texscale=")) { if (float.TryParse(a.Substring("--texscale=".Length), out float ts)) _texScale = ts; }
            else if (a.StartsWith("--roughfloor=")) { if (float.TryParse(a.Substring("--roughfloor=".Length), out float rf)) _probeRoughFloor = rf; }
            else if (a.StartsWith("--mixstr=")) { if (float.TryParse(a.Substring("--mixstr=".Length), out float ms)) _probeMixStr = ms; }
            else if (a.StartsWith("--hb=")) { _probeHb = a.Substring("--hb=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--mood=")) { int.TryParse(a.Substring("--mood=".Length), out _probeMood); }
            else if (a.StartsWith("--clouddbg=")) { int.TryParse(a.Substring("--clouddbg=".Length), out _cloudDbg); }
            else if (a.StartsWith("--cloudsteps=")) { int.TryParse(a.Substring("--cloudsteps=".Length), out _cloudSteps); }
            else if (a.StartsWith("--clouds=")) { _cloudsOn = a.Substring("--clouds=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--coverage=")) { float.TryParse(a.Substring("--coverage=".Length), out _covOverride); }
            else if (a.StartsWith("--cloudparallax=")) { float.TryParse(a.Substring("--cloudparallax=".Length), out _cloudParallaxCli); }
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
            else if (a.StartsWith("--godrayab=")) { _cliSeq.ArmGodrayAb(a.Substring("--godrayab=".Length)); }
            else if (a.StartsWith("--yawab=")) { _cliSeq.ArmYawAb(a.Substring("--yawab=".Length)); }
            else if (a.StartsWith("--glow=")) { _glowCli = a.Substring("--glow=".Length) == "1" ? 1 : 0; }
            else if (a == "--lookatsun") { _lookAtSunCli = true; }
            else if (a == "--lookatmoon") { _lookAtMoonCli = true; }
            else if (a == "--clean") { _cleanCli = true; }   // strip to erosion-lab parity (filmic+SSAO only) for A/B
            else if (a.StartsWith("--time=")) { float.TryParse(a.Substring("--time=".Length), out _timeCli); }
            else if (a.StartsWith("--suns=")) { int.TryParse(a.Substring("--suns=".Length), out _sunsCli); }
            else if (a.StartsWith("--moons=")) { int.TryParse(a.Substring("--moons=".Length), out _moonsCli); }
            else if (a.StartsWith("--nightgate=")) { _nightGate = a.Substring("--nightgate=".Length) == "1"; }
            else if (a.StartsWith("--nightdark=")) { float.TryParse(a.Substring("--nightdark=".Length), out _nightDarkCli); }
            else if (a.StartsWith("--moonphase=")) { float.TryParse(a.Substring("--moonphase=".Length), out _moonPhaseCli); }
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
            else if (a == "--streamdbg") { _streamDbgCli = true; }   // per-30-frame CDLOD streaming-state log (births/backlog/active)
            else if (a == "--notighten") { _noTightenCli = true; }   // S3.5: disable async AABB tighten → generous AABB fallback
            else if (a.StartsWith("--aabbres=")) { int.TryParse(a.Substring("--aabbres=".Length), out _aabbResCli); }   // S3.5: ProbeRes
            else if (a.StartsWith("--aabbreq=")) { int.TryParse(a.Substring("--aabbreq=".Length), out _aabbReqCli); }   // S3.5: MaxRequestsPerFrame
            else if (a.StartsWith("--aabbspeed=")) { float.TryParse(a.Substring("--aabbspeed=".Length), System.Globalization.CultureInfo.InvariantCulture, out _aabbSpeedCli); _aabbSpeedSet = true; }   // speed gate for AABB readback probes
            else if (a.StartsWith("--loadring=")) { int.TryParse(a.Substring("--loadring=".Length), out _loadRingCli); }   // ARC B Task 1: load-ring radius (1=3×3, 2=5×5)
            else if (a.StartsWith("--fieldcache=")) { _fieldCacheCli = a.Substring("--fieldcache=".Length) == "1" ? 1 : 0; }   // per-chunk field cache on/off A/B
            else if (a.StartsWith("--bakereq=")) { int.TryParse(a.Substring("--bakereq=".Length), out _bakeReqCli); }   // field-cache bake throttle
            else if (a.StartsWith("--chunkops=")) { int.TryParse(a.Substring("--chunkops=".Length), out _chunkOpsCli); }   // per-frame chunk-birth cap (unthrottle = high)
            else if (a.StartsWith("--fogviewscale=")) { float.TryParse(a.Substring("--fogviewscale=".Length), System.Globalization.CultureInfo.InvariantCulture, out _fogViewScaleCli); _fogViewScaleSet = true; }   // ARC B Task 3
            else if (a.StartsWith("--lookahead=")) { float.TryParse(a.Substring("--lookahead=".Length), System.Globalization.CultureInfo.InvariantCulture, out _lookaheadCli); _lookaheadSet = true; }   // ARC B Task 4
            else if (MatchFlag(a, "--cdlod")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cdlodCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--lodviz")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _lodVizCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--testpath=")) { int.TryParse(a.Substring("--testpath=".Length), out _testPathCli); }   // S2b: run LOD-crossing test path N, print report, quit
            else if (a.StartsWith("--analytic")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _analyticCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--textures")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _texturesCli = (s == "1") ? 1 : 0; }   // minimal surfacing slice on/off
            else if (MatchFlag(a, "--atmosphere")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _atmosphereCli = (s == "1") ? 1 : 0; }
            else if (a == "--atmoscheck") { _atmoCheckCli = true; }
            else if (a.StartsWith("--cloudlightstr=")) { float.TryParse(a.Substring("--cloudlightstr=".Length), out _cloudLightStrCli); }
            else if (MatchFlag(a, "--cloudlight")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cloudLightCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--atmoexp=")) { float.TryParse(a.Substring("--atmoexp=".Length), out _atmoExpCli); }
            else if (a.StartsWith("--review=")) { int.TryParse(a.Substring("--review=".Length), out _reviewCli); }
            else if (a == "--meteordebug") { _meteorDebugCli = true; }
            else if (a == "--profmove") { _cliSeq.EnableProfMove(); }
            else if (a.StartsWith("--profspeed=")) { if (float.TryParse(a.Substring("--profspeed=".Length), System.Globalization.CultureInfo.InvariantCulture, out float ps)) _cliSeq.SetProfSpeed(ps); }
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
            else if (MatchFlag(a, "--water")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _waterCli = (s == "1") ? 1 : 0; }
            else if (MatchFlag(a, "--profile")) { double? dur = (a.Contains("=") && double.TryParse(a.Substring(a.IndexOf('=') + 1), out double d)) ? d : (double?)null; _cliSeq.ArmProfile(dur); }
        }
    }
    private string? _waterCheck;   // --watercheck=<name> → run a HydrologyChecks self-check then quit
    private int _waterCli = -1;     // --water[=1] → bind region (0,0)'s water texture + carve for eye-gate
    private WG16.Hydrology.WorldWaterRegion? _waterRegion;   // kept for Task 7 mesh spawn
    private WG16.Hydrology.WaterParams? _waterParams;        // kept for Task 7/8
    private Material? _waterMat;                             // kept for Task 7/8
    private WG16.Hydrology.WaterRenderer? _waterRenderer;    // river ribbon + lake surface meshes

    // Deferred from ApplyCliOverrides (--water=1): attach the water renderer + build region (0,0)'s meshes
    // once the scene tree is no longer mid-setup.
    private void SpawnWaterRenderer()
    {
        if (_waterRegion == null || _waterParams == null || _waterMat == null) return;
        _waterRenderer = new WG16.Hydrology.WaterRenderer();
        GetNode("/root/TerrainLabRoot").AddChild(_waterRenderer);
        _waterRenderer.BuildForRegion(_waterRegion, _waterParams, _waterMat);
    }

    private void ApplyCliOverrides()
    {
        if (_waterCheck != null) { bool ok = WG16.Hydrology.HydrologyChecks.Run(_waterCheck); GetTree().Quit(ok ? 0 : 1); return; }
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
        if (_probeRoughFloor >= 0f) { _terrain.SetFloat("rough_floor", _probeRoughFloor); }
        if (_probeMixStr >= 0f) { _terrain.SetFloat("mix_strength", _probeMixStr); }
        if (_probeHb >= 0) { _terrain.SetBool("heightblend_on", _probeHb == 1); }
        if (_proxyResCli >= 0) { _terrain.SetProxyRes(_proxyResCli); }
        if (_giProxyCli >= 0) { _terrain.SetGiProxy(_giProxyCli == 1); }
        if (_analyticCli >= 0) { _analyticOn = _analyticCli == 1; _terrain.SetAnalytic(_analyticOn); }   // keep key-1 toggle in sync with the CLI default
        if (_texturesCli >= 0) { _terrain.SetTexturesOn(_texturesCli == 1); }   // minimal surfacing slice
        if (_cdlodTestCli) { _terrain.SetAnalytic(true); _terrain.CdlodTestOneChunk(_params); }   // S2a Task-1 sanity
        // CDLOD default: ON in all scenes. --cdlod=0 overrides to single mesh if needed.
        int cdlodWant = _cdlodCli >= 0 ? _cdlodCli : 1;
        _terrain.SetAnalytic(true); _terrain.SetCdlod(cdlodWant == 1);
        if (_loadRingCli >= 0) { _terrain.SetLoadRing(_loadRingCli); }   // ARC B Task 1: load-ring radius override
        if (_fieldCacheCli >= 0) { _terrain.SetFieldCache(_fieldCacheCli == 1); }   // per-chunk field cache A/B (before first Tick)
        if (_bakeReqCli > 0) { _terrain.SetBakeReq(_bakeReqCli); }   // field-cache bake throttle
        if (_chunkOpsCli > 0) { _terrain.SetChunkOps(_chunkOpsCli); }   // per-frame chunk-birth cap (unthrottle)
        if (_fogViewScaleSet) { FogViewScale = _fogViewScaleCli; ComposeLighting(); }   // ARC B Task 3: fog↔radius coupling scale
        if (_lookaheadSet) { _terrain.SetCdlodLookahead(_lookaheadCli); }   // ARC B Task 4: predictive-loading lookahead
        if (_aabbSpeedSet) { _terrain.SetCdlodAabbSpeed(_aabbSpeedCli); }
        if (_lodVizCli >= 0) { _terrain.SetCdlodViz(_lodVizCli == 1); }
        // S3.5: async AABB tighten tunables (--notighten / --aabbres= / --aabbreq=). Only meaningful with CDLOD on.
        if (cdlodWant == 1 && (_noTightenCli || _aabbResCli > 0 || _aabbReqCli > 0))
        {
            _terrain.ConfigureCdlodAabb(!_noTightenCli, _aabbResCli, _aabbReqCli);
        }
        if (_waterCli == 1)
        {
            _waterParams = WG16.Hydrology.WaterParams.Load();
            // Pass the shared FieldCompute → water meshes drape on the EXACT field height the terrain uses.
            _waterRegion = new WG16.Hydrology.WorldWaterRegion(_params, _waterParams, 0, 0, _fc);
            _terrain.BindWaterRegion(_waterRegion, _waterParams);   // Task 6: carve
            // Task 7: flowing water MESHES. Build the modular water-surface material + spawn the renderer.
            var sh = GD.Load<Shader>("res://shaders/water_surface.gdshader");
            var wmat = new ShaderMaterial { Shader = sh };
            wmat.SetShaderParameter("shallow_color", _waterParams.WaterShallowColor);
            wmat.SetShaderParameter("deep_color", _waterParams.WaterDeepColor);
            wmat.SetShaderParameter("flow_speed", _waterParams.FlowSpeed);
            wmat.SetShaderParameter("wave_scale", _waterParams.WaveScale);
            wmat.SetShaderParameter("foam_width_m", _waterParams.FoamWidthM);
            _waterMat = wmat;
            // Defer the node attach + mesh build: ApplyCliOverrides runs during _Ready (parent busy setting up
            // children → a direct AddChild fails, the known CDLOD deferred-add gotcha).
            CallDeferred(nameof(SpawnWaterRenderer));
            GD.Print($"[water] region(0,0) rivers={_waterRegion.Rivers.Count} lakes={_waterRegion.Lakes.Count} " +
                     $"— MESHES on, carve ON, overlay OFF (key H toggles debug tint). Fly near world origin (0..8192).");
        }
        if (_streamDbgCli) { var c = _terrain.Cdlod; if (c != null) { c.DebugStream = true; } }   // streaming-state log
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
        _volumetricFogOn = false;
        env.VolumetricFogEnabled = false;
        env.FogDensity = 0f;
        env.FogAerialPerspective = 0f;
        env.FogHeightDensity = 0f;
        _cloud?.SetAtmosphereOn(false);      // AT-1 physical sky tint on the terrain
        GD.Print("[nofog] all haze OFF (env fog / volfog / aerial / atmosphere) — raw terrain review");
    }

    /// REVIEW (--clean): strip WG16 down to erosion-lab's lighting baseline so terrain + sun CSM + SSAO can be
    /// judged side-by-side against the reference lab with nothing layered on top. The env-level parity (filmic
    /// tonemap, no grade/glow/fog, fixed sun/ambient, SSAO on) is owned by LightingComposer.CleanParity so it
    /// survives every recompose; here we additionally kill the screen-space / cloud post layers erosion-lab has
    /// none of (cloud shadows, aerial perspective, GPU atmosphere, godrays, volumetric fog) and show the sun.
    /// Applied LAST (after --review) so it wins over any preset that re-enables a post layer.
    private void ApplyCleanMode()
    {
        _lighting.CleanParity = true;
        OverrideToggle("cloud_enabled", false);    // no volumetric clouds → no cloud shadow map on terrain
        OverrideToggle("aerial_on", false);        // AT-2 screen-space aerial perspective off
        OverrideToggle("atmosphere_on", false);    // AT-1 GPU physical sky off
        OverrideToggle("volfog_on", false);
        OverrideToggle("cloud_godrays", false);
        OverrideToggle("dbg_sun", true);           // sun disc visible
        ComposeLighting();                         // apply CleanParity now
        GD.Print("[clean] erosion-lab parity: filmic tonemap + SSAO only — NO grade/glow/fog/aerial/atmosphere/cloud/overcast. Terrain + sun CSM.");
    }
    // Combined exit-code state for the one-shot regression-gate self-checks (audit #1). Any gate check
    // sets _checkRan + ANDs its pass into _checkPass; AttachClouds then Quit(_checkPass?0:1) so CI can gate.
    private bool _checkRan;
    private bool _checkPass = true;
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
    private bool _cleanCli;           // --clean → strip to erosion-lab parity (filmic+SSAO only) for A/B vs the reference lab
    private bool _popMeterCli;        // --popmeter → S3 live per-frame pop meter (HUD + log) while you fly
    private bool _aabbSpikeCli;       // --aabbspike → S3.5 async GPU height-range feasibility spike (vs sync ref)
    private bool _streamDbgCli;       // --streamdbg → CDLOD per-30-frame streaming-state log
    private bool _noTightenCli;       // --notighten → S3.5 disable the async AABB tighten (generous-AABB fallback)
    private int _aabbResCli;          // --aabbres=N → S3.5 ChunkAabbProvider.ProbeRes (0 = leave default)
    private int _aabbReqCli;          // --aabbreq=N → S3.5 ChunkAabbProvider.MaxRequestsPerFrame (0 = leave default)
    private int _loadRingCli = -1;    // --loadring=N → ARC B Task 1 CdlodTerrain.LoadRing (-1 = leave default 2)
    private int _fieldCacheCli = -1;  // --fieldcache=0|1 → per-chunk field cache (-1 = leave default on)
    private int _bakeReqCli = -1;     // --bakereq=N → field-cache bake throttle (-1 = leave default)
    private int _chunkOpsCli = -1;    // --chunkops=N → per-frame chunk-birth cap (unthrottle = high; -1 = leave default 24)
    private float _fogViewScaleCli;   // --fogviewscale=F → ARC B Task 3 FogViewScale (gated by _fogViewScaleSet)
    private bool _fogViewScaleSet;
    private float _lookaheadCli;      // --lookahead=F → ARC B Task 4 PredictLookahead seconds (gated by _lookaheadSet)
    private bool _lookaheadSet;
    private float _aabbSpeedCli;      // --aabbspeed=N -> max camera m/s that may run AABB readback probes
    private bool _aabbSpeedSet;
    private int _cloudDbg = -1;
    private int _cloudSteps = -1;
    private int _cloudsOn = -1;
    private void OverrideEnum(string id, int v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
    private void OverrideToggle(string id, bool v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
}
