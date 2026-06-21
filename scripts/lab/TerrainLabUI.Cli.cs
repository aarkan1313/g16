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
    private int _nsPresetCli = -1;         // --nspreset=N (1-based): apply night-sky (galaxy/nebula) preset N at startup
    private int _fantasyCli = -1;          // --fantasy=N: apply fantasy/exotic cross-system preset N at startup (ST4-2)
    private int _temporalCli = 0;          // --temporal=N: temporal amortization stride (roadmap #4)
    private int _godraysOnCli = -1;
    private int _godrayDbgCli = 0;   // --godraydbg=N → GodRaysScreen debug_mode (1=mask, 2=sun pos)
    private float _godrayHpCli = -1f;   // --godrayhp=N → high-pass amount (0 = old wash+ring, 1 = clean beams); A/B verify
    private int _glowCli = -1;   // --glow=0/1 → force env glow off/on (isolate post-processing effects)
    private bool _lookAtSunCli = false;   // --lookatsun → aim camera at the sun on startup (god-ray verify)
    private bool _lookAtMoonCli = false;  // --lookatmoon → aim camera at the moon on startup (3b moon gate)
    private float _timeCli = -1f;   // --time=H → drive the decoupled Time axis (sun arc + day color script)
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
    private int _nsBakedCli = -1;        // --nsbaked[=1|0] → PERF night-sky baked-texture path at startup (pixel-diff verify)
    private float _atmoExpCli = -1f;     // --atmoexp=N → AT-1 atmosphere exposure override
    private int _reviewCli = -1;         // --review=N → run ApplyReview(N) at startup (drive/verify a review preset headlessly)
    private int _greviewCli = -1;        // --greview=N → run ApplyGroundReview(N) at startup (Shift+N ground bank; auto-shoot the ground gates)
    private int _terrainArCli = -1;
    private int _terrainDetailCli = -1;
    private int _groundRulesCli = -1;
    private int _giProxyCli = -1;
    private int _proxyResCli = -1;
    private int _groundV2Cli = -1;   // --groundv2[=1] → swap to the new per-pixel ground skin at startup (A/B / auto-shots)
    private int _gv2DebugCli = -1;   // --gv2debug=N → ground v2 debug view (1 = placement viz) at startup

    private void ParseCli()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("--histcheck"))
            {
                // Bake one material's albedo histogram LUTs and print the T⁻¹(T(v)) round-trip error, then quit.
                string mat = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "13_sun_baked_clay";
                string hp = ProjectSettings.GlobalizePath($"res://assets/materials/{mat}/albedo.png");
                if (!System.IO.File.Exists(hp)) { GD.Print($"[histcheck] no albedo for {mat}"); GetTree().Quit(); return; }
                var himg = Image.LoadFromFile(hp);
                var luts = HistogramCompute.ComputeLuts(himg);
                var (maxe, meane) = HistogramCompute.RoundTripError(luts, himg);
                // PASS on bulk fidelity (meanErr). maxErr is the inherent ±3σ tail-clamp on the
                // most-extreme ~0.1% of pixels (sub-perceptible) — reported, not gated.
                GD.Print($"[histcheck] {mat}: roundtrip meanErr={meane * 255f:F3}/255 (maxErr={maxe * 255f:F1}/255 = ±3σ tail-clamp, expected)  -> {(meane < 1f / 255f ? "PASS" : "FAIL")}");
                GetTree().Quit();
                return;
            }
            else if (a.StartsWith("--groundarraycheck"))
            {
                // Build the ground v2 texture arrays standalone, print layer counts + PASS/FAIL, quit.
                // Windowed bakes real Poisson height; headless falls back to the flat height proxy (still PASS).
                var g = GroundMaterialArrays.Build("res://data/ground_materials.json");
                bool ok = g.Count > 0 && g.Albedo.GetLayers() == g.Count && g.Normal.GetLayers() == g.Count
                          && g.Orm.GetLayers() == g.Count && g.Height.GetLayers() == g.Count;
                GD.Print($"[groundarraycheck] count={g.Count} res={g.TexRes} scale={g.TexScaleM:F1} " +
                         $"albLayers={g.Albedo.GetLayers()} nrmLayers={g.Normal.GetLayers()} " +
                         $"ormLayers={g.Orm.GetLayers()} hgtLayers={g.Height.GetLayers()} -> {(ok ? "PASS" : "FAIL")}");
                GetTree().Quit();
                return;
            }
            else if (a.StartsWith("--groundv2")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _groundV2Cli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--gv2debug=")) { int.TryParse(a.Substring("--gv2debug=".Length), out _gv2DebugCli); }
            else if (a.StartsWith("--auto-shot=")) { _autoShotPath = a.Substring("--auto-shot=".Length); _autoShotT = 0.0; }
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
            else if (a.StartsWith("--nightgate=")) { _nightGate = a.Substring("--nightgate=".Length) == "1"; }
            else if (a.StartsWith("--nightdark=")) { float.TryParse(a.Substring("--nightdark=".Length), out _nightDarkCli); }
            else if (a.StartsWith("--moonphase=")) { float.TryParse(a.Substring("--moonphase=".Length), out _moonPhaseCli); }
            else if (a.StartsWith("--ar=")) { _terrainArCli = a.Substring("--ar=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--detail=")) { _terrainDetailCli = a.Substring("--detail=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--groundrules=")) { _groundRulesCli = a.Substring("--groundrules=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--giproxy=")) { _giProxyCli = a.Substring("--giproxy=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--proxyres=")) { if (int.TryParse(a.Substring("--proxyres=".Length), out int pr)) _proxyResCli = pr; }
            else if (a.StartsWith("--shadowdbg=")) { _shadowDbgCli = a.Substring("--shadowdbg=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--atmosphere")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _atmosphereCli = (s == "1") ? 1 : 0; }
            else if (a == "--atmoscheck") { _atmoCheckCli = true; }
            else if (a == "--aerialcheck") { _aerialCheckCli = true; }
            else if (a.StartsWith("--aerialstr=")) { float.TryParse(a.Substring("--aerialstr=".Length), out _aerialStrCli); }
            else if (a.StartsWith("--aerial")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _aerialCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--cloudlightstr=")) { float.TryParse(a.Substring("--cloudlightstr=".Length), out _cloudLightStrCli); }
            else if (a.StartsWith("--cloudlight")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _cloudLightCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--nsbaked")) { var s = a.Contains("=") ? a.Substring(a.IndexOf('=') + 1) : "1"; _nsBakedCli = (s == "1") ? 1 : 0; }
            else if (a.StartsWith("--atmoexp=")) { float.TryParse(a.Substring("--atmoexp=".Length), out _atmoExpCli); }
            else if (a.StartsWith("--greview=")) { int.TryParse(a.Substring("--greview=".Length), out _greviewCli); }
            else if (a.StartsWith("--review=")) { int.TryParse(a.Substring("--review=".Length), out _reviewCli); }
            else if (a == "--profmove") { _profMove = true; }
            else if (a == "--shadowcheck") { _shadowCheckCli = true; }
            else if (a == "--lightcheck") { _lightCheckCli = true; }
            else if (a == "--cloudstats") { _cloudStatsCli = true; }
            else if (a.StartsWith("--preset=")) { int.TryParse(a.Substring("--preset=".Length), out _presetCli); }
            else if (a.StartsWith("--sunpreset=")) { int.TryParse(a.Substring("--sunpreset=".Length), out _sunPresetCli); }
            else if (a.StartsWith("--celestial=")) { int.TryParse(a.Substring("--celestial=".Length), out _celestialCli); }
            else if (a.StartsWith("--nspreset=")) { int.TryParse(a.Substring("--nspreset=".Length), out _nsPresetCli); }
            else if (a.StartsWith("--fantasy=")) { int.TryParse(a.Substring("--fantasy=".Length), out _fantasyCli); }
            else if (a.StartsWith("--cloudtex=")) { if (int.TryParse(a.Substring("--cloudtex=".Length), out int th) && th >= 64) { CloudVolume.TexH = th; CloudVolume.TexW = th * 4; } }
            else if (a.StartsWith("--temporal=")) { int.TryParse(a.Substring("--temporal=".Length), out _temporalCli); }
            else if (a.StartsWith("--profile")) { _profileT = 0.0; if (a.Contains("=") && double.TryParse(a.Substring(a.IndexOf('=')+1), out double d)) _profileDur = d;
                DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled); Engine.MaxFps = 0; }
        }
    }

    private void ApplyCliOverrides()
    {
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
        if (_terrainArCli >= 0) { _terrain.SetBool("ar_on", _terrainArCli == 1); }
        if (_terrainDetailCli >= 0) { _terrain.SetBool("detail_on", _terrainDetailCli == 1); }
        if (_groundRulesCli >= 0) { _terrain.RuleBased = _groundRulesCli == 1; _terrain.RebakeSplat(); }
        if (_proxyResCli >= 0) { _terrain.SetProxyRes(_proxyResCli); }
        if (_giProxyCli >= 0) { _terrain.SetGiProxy(_giProxyCli == 1); }
        if (_probeMood >= 0) { ApplyMood(_probeMood); }
    }
    private int _shadowDbgCli = -1;   // --shadowdbg=1 → paint the cloud-shadow map as terrain albedo (proof)
    private bool _shadowCheckCli;     // --shadowcheck → numeric correlation test, PASS/FAIL to console
    private int _cloudDbg = -1;
    private int _cloudSteps = -1;
    private int _cloudsOn = -1;
    private void OverrideEnum(string id, int v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
    private void OverrideToggle(string id, bool v) { if (_byId.TryGetValue(id, out LabControl c)) { SetWidgetValue(c, v); } }
}
