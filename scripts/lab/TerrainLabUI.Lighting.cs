using Godot;

namespace WG16.Lab;

/// Lighting FORWARDERS + host glue. The composition logic + state moved to `LightingComposer` (C3 Unit 1
/// — de-god-objecting the lighting half). This partial keeps thin forwarding properties/methods under the
/// OLD names so the rest of the UI (sliders, presets, review, CLI) compiles unchanged, and implements
/// `ILightingHost` so the composer can reach the scene without owning the UI. The forwarders are a
/// migration shim: callers can move to `_lighting.X` directly over time. Spec:
/// 2026-06-21-celestial-c3-n-luminaries-design.md (+ 2026-06-20-lighting-decouple-and-time-axis-design.md).
public partial class TerrainLabUI : Control, ILightingHost
{
    // Lazily created with `this` as host (a field initializer can't use `this`); first access wins, so
    // ordering vs _Ready / other field initializers doesn't matter.
    private LightingComposer? _lightingImpl;
    private LightingComposer _lighting => _lightingImpl ??= new LightingComposer(this);

    // Stage 4 (ST4-1): auto day/night clock (UI-side, NOT composer state). When _timeRunning, _Process
    // advances _time.TimeOfDay by _timeSpeed h/real-second and re-DriveTime()s — manual scrub still works.
    private bool _timeRunning = false;
    private float _timeSpeed = 1.0f;   // in-world hours per real second

    // ── Forwarding shims: same names the rest of TerrainLabUI already uses → the composer's state. ──
    // Reference-type state: get-only is enough (callers mutate `.X` through the returned instance).
    private TimeState _time => _lighting.Time;
    private SunDiscState _sunDisc => _lighting.SunDisc;
    private WeatherState _weather => _lighting.Weather;
    private GradeState _grade => _lighting.Grade;
    private MoonState _moon => _lighting.Moon;
    private StarsState _stars => _lighting.Stars;
    // Value-type state externally written (sliders / presets) → read+write forwards.
    private Color _skyTint { get => _lighting.SkyTint; set => _lighting.SkyTint = value; }
    private float _baseAmbient { get => _lighting.BaseAmbient; set => _lighting.BaseAmbient = value; }
    private float _baseSunEnergy { get => _lighting.BaseSunEnergy; set => _lighting.BaseSunEnergy = value; }
    private Color _baseFogColor { get => _lighting.BaseFogColor; set => _lighting.BaseFogColor = value; }
    private float _sunAngle { get => _lighting.SunAngle; set => _lighting.SunAngle = value; }
    private float _sunAzimuth { get => _lighting.SunAzimuth; set => _lighting.SunAzimuth = value; }
    private Vector3 _lastMoonDir => _lighting.LastMoonDir;   // read-only externally (--lookatmoon / review)

    // Method forwarders (the 23 call sites across the partials stay untouched).
    private void ComposeLighting() => _lighting.Compose();
    private void DriveTime(float hour) => _lighting.DriveTime(hour);
    private void ApplyOvercastScaling() => _lighting.ApplyOvercastScaling();
    private void MoodToStates(Godot.Collections.Dictionary m) => _lighting.MoodToStates(m);

    // ── U2: data-driven luminaries (data/luminaries.json -> composer source of truth). ──
    private const string LuminariesPath = "res://data/luminaries.json";
    private ObjectListControl? _luminaryList;   // the Night-tab "Sky bodies" list editor (built in BuildPanel)

    /// Load data/luminaries.json -> List<Luminary> -> composer. Called at startup after LoadRegistry, before
    /// the first compose. Missing/malformed file => the composer keeps its built-in defaults (no extras).
    private void LoadLuminariesFromDisk()
    {
        string abs = ProjectSettings.GlobalizePath(LuminariesPath);
        if (!System.IO.File.Exists(abs)) { return; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(abs));
            var bodies = new System.Collections.Generic.List<Luminary>();
            foreach (System.Text.Json.JsonElement e in doc.RootElement.EnumerateArray()) { bodies.Add(LuminaryFromJson(e)); }
            if (bodies.Count > 0) { _lighting.LoadLuminaries(bodies); }
        }
        catch (System.Exception ex) { GD.PushWarning($"[luminaries] parse failed ({ex.Message}) -> defaults"); }
    }

    private static Luminary LuminaryFromJson(System.Text.Json.JsonElement e)
    {
        float G(string k, float fb) => e.TryGetProperty(k, out var v) ? v.GetSingle() : fb;
        bool B(string k, bool fb) => e.TryGetProperty(k, out var v) ? v.GetBoolean() : fb;
        Color C(string k, Color fb)
        {
            if (e.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                var a = new System.Collections.Generic.List<float>();
                foreach (var x in v.EnumerateArray()) { a.Add(x.GetSingle()); }
                if (a.Count >= 3) { return new Color(a[0], a[1], a[2]); }
            }
            return fb;
        }
        int kind = e.TryGetProperty("kind", out var kv) ? kv.GetInt32() : 0;
        return new Luminary
        {
            Kind = kind == 1 ? LuminaryKind.Moon : LuminaryKind.Sun,
            Color = C("color", new Color(1f, 0.95f, 0.86f)),
            Size = G("size", 0.6f), Phase = G("phase", 1.0f), AzOffset = G("az_offset", 0f),
            DeclScale = G("decl_scale", 1.0f), LightEnergy = G("energy", 1.3f),
            CastsShadow = B("casts_shadow", true), ContributesToAtmosphere = B("atmosphere", true),
            Priority = G("priority", 50f),
        };
    }

    /// Adapter: the objectlist's Variant dicts -> List<Luminary> -> composer -> recompose (live edits).
    private void ApplyLuminaryDicts(System.Collections.Generic.List<Godot.Collections.Dictionary> items)
    {
        var bodies = new System.Collections.Generic.List<Luminary>();
        foreach (var d in items) { bodies.Add(LuminaryFromDict(d)); }
        _lighting.LoadLuminaries(bodies);
        ComposeLighting();
    }

    private static Luminary LuminaryFromDict(Godot.Collections.Dictionary d)
    {
        float G(string k, float fb) => d.ContainsKey(k) ? d[k].AsSingle() : fb;
        bool B(string k, bool fb) => d.ContainsKey(k) ? d[k].AsBool() : fb;
        Color C(string k, Color fb) => d.ContainsKey(k) ? d[k].AsColor() : fb;
        int kind = d.ContainsKey("kind") ? d["kind"].AsInt32() : 0;
        return new Luminary
        {
            Kind = kind == 1 ? LuminaryKind.Moon : LuminaryKind.Sun,
            Color = C("color", new Color(1f, 0.95f, 0.86f)),
            Size = G("size", 0.6f), Phase = G("phase", 1.0f), AzOffset = G("az_offset", 0f),
            DeclScale = G("decl_scale", 1.0f), LightEnergy = G("energy", 1.3f),
            CastsShadow = B("casts_shadow", true), ContributesToAtmosphere = B("atmosphere", true),
            Priority = G("priority", 50f),
        };
    }

    /// dict for one Luminary (objectlist seed + U3 preset save). Inverse of LuminaryFromDict.
    private static Godot.Collections.Dictionary DictFromLuminary(Luminary b) => new()
    {
        { "kind", b.Kind == LuminaryKind.Moon ? 1 : 0 },
        { "color", b.Color }, { "size", b.Size }, { "phase", b.Phase },
        { "az_offset", b.AzOffset }, { "decl_scale", b.DeclScale }, { "energy", b.LightEnergy },
        { "casts_shadow", b.CastsShadow }, { "atmosphere", b.ContributesToAtmosphere },
        { "priority", b.Priority },
    };

    // ── ILightingHost: what the composer reads/calls back. ──
    public Node SceneOwner => this;
    public CloudVolume? Cloud => _cloud;
    public float Overcast => _overcast;
    public bool AtmosphereOn => _atmosphereOn;
    public AtmosphereCompute? Atmosphere => _atmosphere;
    public TerrainLab? Terrain => _terrain;   // relight #1: indirect-fill uniform target
    // ARC B Task 3: the CDLOD load boundary (LoadRing·rootSize) the fog density couples to — 0 when CDLOD is
    // off (finite single mesh) so the composer keeps the plain mood fog. FogViewScale dials the coupled baseline.
    public float CdlodViewDistance => _terrain.CdlodActive ? _terrain.LoadRing * _params.RegionSizeM : 0f;
    public float FogViewScale { get; set; } = 1.0f;
    // ARC A.1: directional shadow atlas size (px). 8192 default; 6144/4096 = the in-motion shadow-spike dial-down.
    public int ShadowAtlasSize { get; set; } = 8192;
    // OrientSun + SyncLightControlsToScene are defined in TerrainLabUI.Apply.cs (now public to satisfy the
    // interface) — they stay there because they're shared with the sun-angle sliders + other callers.
}
