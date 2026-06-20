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
    private bool _lightCheckCli = false;   // --lightcheck: print per-deck lighting difference math
    private int _deckDbgCli = -1;          // --deckdbg=1: deck-ID overlay (flat color per deck)
    private bool _cloudStatsCli = false;   // --cloudstats: read back dome, print coverage/brightness
    private int _presetCli = -1;           // --preset=N: apply cloud preset N at startup (test layer stacks)
    private int _sunPresetCli = -1;        // --sunpreset=N: apply sun-disc preset N at startup
    private int _temporalCli = 0;          // --temporal=N: temporal amortization stride (roadmap #4)
    private int _godraysOnCli = -1;
    private int _godrayDbgCli = 0;   // --godraydbg=N → GodRaysScreen debug_mode (1=mask, 2=sun pos)
    private float _godrayHpCli = -1f;   // --godrayhp=N → high-pass amount (0 = old wash+ring, 1 = clean beams); A/B verify
    private int _glowCli = -1;   // --glow=0/1 → force env glow off/on (isolate post-processing effects)
    private bool _lookAtSunCli = false;   // --lookatsun → aim camera at the sun on startup (god-ray verify)
    private float _timeCli = -1f;   // --time=H → drive the decoupled Time axis (sun arc + day color script)
    private int _terrainArCli = -1;
    private int _terrainDetailCli = -1;
    private int _groundRulesCli = -1;
    private int _giProxyCli = -1;
    private int _proxyResCli = -1;

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
            else if (a.StartsWith("--deckdbg=")) { _deckDbgCli = a.Substring("--deckdbg=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--godrays=")) { _godraysOnCli = a.Substring("--godrays=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--godraydbg=")) { int.TryParse(a.Substring("--godraydbg=".Length), out _godrayDbgCli); }
            else if (a.StartsWith("--godrayhp=")) { float.TryParse(a.Substring("--godrayhp=".Length), out _godrayHpCli); }
            else if (a.StartsWith("--godrayab=")) { _godrayAbPath = a.Substring("--godrayab=".Length); _godrayAbT = 0.0; }
            else if (a.StartsWith("--glow=")) { _glowCli = a.Substring("--glow=".Length) == "1" ? 1 : 0; }
            else if (a == "--lookatsun") { _lookAtSunCli = true; }
            else if (a.StartsWith("--time=")) { float.TryParse(a.Substring("--time=".Length), out _timeCli); }
            else if (a.StartsWith("--ar=")) { _terrainArCli = a.Substring("--ar=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--detail=")) { _terrainDetailCli = a.Substring("--detail=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--groundrules=")) { _groundRulesCli = a.Substring("--groundrules=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--giproxy=")) { _giProxyCli = a.Substring("--giproxy=".Length) == "1" ? 1 : 0; }
            else if (a.StartsWith("--proxyres=")) { if (int.TryParse(a.Substring("--proxyres=".Length), out int pr)) _proxyResCli = pr; }
            else if (a.StartsWith("--shadowdbg=")) { _shadowDbgCli = a.Substring("--shadowdbg=".Length) == "1" ? 1 : 0; }
            else if (a == "--profmove") { _profMove = true; }
            else if (a == "--shadowcheck") { _shadowCheckCli = true; }
            else if (a == "--lightcheck") { _lightCheckCli = true; }
            else if (a == "--cloudstats") { _cloudStatsCli = true; }
            else if (a.StartsWith("--preset=")) { int.TryParse(a.Substring("--preset=".Length), out _presetCli); }
            else if (a.StartsWith("--sunpreset=")) { int.TryParse(a.Substring("--sunpreset=".Length), out _sunPresetCli); }
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
