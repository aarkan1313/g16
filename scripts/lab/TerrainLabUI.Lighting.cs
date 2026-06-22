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

    // ── ILightingHost: what the composer reads/calls back. ──
    public Node SceneOwner => this;
    public CloudVolume? Cloud => _cloud;
    public float Overcast => _overcast;
    public bool AtmosphereOn => _atmosphereOn;
    public bool AerialOn => _aerialOn;
    public AtmosphereCompute? Atmosphere => _atmosphere;
    // OrientSun + SyncLightControlsToScene are defined in TerrainLabUI.Apply.cs (now public to satisfy the
    // interface) — they stay there because they're shared with the sun-angle sliders + other callers.
}
