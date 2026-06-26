# Lighting Refactor Plan — 2026-06-25

## What's broken and why

### Root cause 1: hardcoded ambient override kills terrain ambient
In `scripts/lab/LightingComposer.cs`, `ApplyOvercastScaling()` (around line 636):

```csharp
env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);  // correct
env.AmbientLightSkyContribution = Time.AmbientSky;                 // correct
// ...then immediately OVERRIDDEN:
env.AmbientLightEnergy = 0.12f;           // BUG: hardcoded, slider never reaches terrain
env.AmbientLightSkyContribution = 0.0f;   // BUG: sky never contributes to ambient
env.AmbientLightColor = new Color(0.5f, 0.5f, 0.5f);  // flat grey
```

The ambient slider has been a no-op for terrain this entire time.

### Root cause 2: EMISSION fill parameters fight each other
`ground.gdshader` injects indirect fill via EMISSION. Shadow color = sky_zenith (very blue at noon).
- Old defaults `sky_strength=0.40, bounce_strength=2.40` accidentally balanced (very warm bounce masked blue sky)
- No way to tune shadow color independently of sky color
- The day_script sky colors (blue at noon) feed directly into shadow tint

### Root cause 3: AT-2 aerial froxel "visor" line
`atmosphere_aerial.glsl` — froxel Z-slice discontinuity at ray-ground crossing.
Already off by default (`_aerialOn = false` in TerrainLabUI.Clouds.cs).
Proper fix (separate, lower priority): 64 Z-slices + log-Z mapping in both shaders.

---

## The fix (3 surgical changes)

### Change 1: `shaders/ground.gdshader` — remove EMISSION fill uniforms and code

**Delete the fill uniforms block** (currently lines 102–114):
```glsl
// DELETE THIS ENTIRE BLOCK:
// ── Analytic indirect FILL (relight sub-project #1) ──────────────────────────────────────────
uniform bool  fill_on = false;
uniform vec3  sky_zenith  : source_color = vec3(0.13, 0.22, 0.40);
uniform vec3  sky_horizon : source_color = vec3(0.40, 0.45, 0.50);
uniform vec3  sky_ground  : source_color = vec3(0.22, 0.19, 0.16);
uniform vec3  sun_radiance : source_color = vec3(1.0, 0.93, 0.82);
uniform vec3  sun_dir_to = vec3(0.0, 1.0, 0.0);
uniform vec3  ground_albedo : source_color = vec3(0.70, 0.56, 0.40);
uniform float sky_strength    : hint_range(0.0, 2.0) = 1.15;
uniform float bounce_strength : hint_range(0.0, 2.0) = 0.85;
```

**Delete the EMISSION fill application in fragment()** (currently lines 500–518):
```glsl
// DELETE THIS ENTIRE BLOCK:
    if (fill_on) {
        vec3 nf = normalize(v_normal);
        float up = clamp(nf.y, -1.0, 1.0);
        vec3 sky = (up >= 0.0) ? mix(sky_horizon, sky_zenith, up)
                               : mix(sky_horizon, sky_ground, -up);
        float sky_vis = 0.5 + 0.5 * up;
        vec3 sky_fill = sky * sky_vis * sky_strength;
        float sun_up = clamp(sun_dir_to.y, 0.0, 1.0);
        float bounce_vis = clamp(1.0 - up, 0.0, 1.0) * 0.5;
        vec3 bounce = sun_radiance * ground_albedo * sun_up * bounce_vis * bounce_strength;
        EMISSION = (sky_fill + bounce) * ALBEDO;
    }
```

**Keep** the horizon shadow uniforms and code as-is (already off by default, useful future feature).
**Keep** the `cam_world` uniform (used by vertex geomorph distance calc, NOT fill).

### Change 2: `scripts/lab/LightingComposer.cs` — fix ambient + remove PushTerrainFill

**In `ApplyOvercastScaling()`**, replace the buggy hardcoded block.

Find this exact text (around lines 636–647):
```csharp
        float oc = _host.Overcast;
        env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);     // sky fill DOWN (grey gloom)
        env.AmbientLightSkyContribution = Time.AmbientSky;
        // Relight #1 (2026-06-23): the terrain's shaded-slope FILL is now owned by ground.gdshader's
        // analytic indirect term (cool sky hemisphere + warm ground bounce), pushed in PushTerrainFill().
        // So the env ambient drops to a low NEUTRAL flat base — sky-contribution 0 so no blue sky is pulled
        // back in to re-create the dead-blue slopes; other lit objects keep a small fill. (Was the warm-flat
        // WIP: energy 0.9 / sky 0.4 / warm-white, which couldn't beat the blue because it was non-directional.)
        env.AmbientLightEnergy = 0.12f;
        env.AmbientLightSkyContribution = 0.0f;
        env.AmbientLightColor = new Color(0.5f, 0.5f, 0.5f);
        sun.LightEnergy = BaseSunEnergy * (1f - oc * 0.8f);                  // direct sun DOWN under cloud
        PushTerrainFill();
```

Replace with:
```csharp
        float oc = _host.Overcast;
        // Ambient: let the real BaseAmbient slider reach terrain. Sky contribution from the day_script
        // (Time.AmbientSky, 0.95 at noon → 0.25 at deep night via DriveTime) so AT-1 physical sky
        // automatically colors the ambient. AmbientLightColor is set in Compose() (white→night tint).
        env.AmbientLightEnergy = BaseAmbient * Mathf.Lerp(1f, 0.7f, oc);
        env.AmbientLightSkyContribution = Time.AmbientSky;
        sun.LightEnergy = BaseSunEnergy * (1f - oc * 0.8f);                  // direct sun DOWN under cloud
```

**Delete `PushTerrainFill()`** — the entire method (currently lines 652–668):
```csharp
    /// Push the analytic indirect-fill uniforms to the terrain (relight #1). ...
    private void PushTerrainFill()
    {
        var t = _host.Terrain;
        if (t == null) { return; }
        Vector3 toSun = SunNode.GlobalTransform.Basis.Z.Normalized();
        Color sunRad = (Time.SunColor * Time.SunEnergy).SrgbToLinear();
        t.SetBool("fill_on", FillEnabled);
        t.SetColor("sky_zenith",  Time.SkyTop.SrgbToLinear());
        t.SetColor("sky_horizon", Time.SkyHorizon.SrgbToLinear());
        t.SetColor("sky_ground",  Time.SkyGround.SrgbToLinear());
        t.SetColor("sun_radiance", new Color(sunRad.R, sunRad.G, sunRad.B));
        t.SetVector3("sun_dir_to", toSun);
        // ground_albedo + sky_strength/bounce_strength keep their shader defaults until the Task 3 sliders set them.
    }
```

**Delete the `FillEnabled` property** (line 45):
```csharp
    public bool FillEnabled { get; set; } = true;        // master A/B for the analytic indirect fill (relight #1)
```

**Also remove from `Compose()`** the `FillEnabled` reference if any exists (search for FillEnabled).

### Change 3: `data/lab_controls.json` — remove orphaned fill entries

**Remove** these two entries (currently around lines 15–16):
```json
    { "id": "sky_strength", "label": "fill: sky (cool)", "tab": "Light", "type": "slider", "param": "sky_strength", "min": 0.0, "max": 2.0, "default": 1.15, "rand": false },
    { "id": "bounce_strength", "label": "fill: ground bounce (warm)", "tab": "Light", "type": "slider", "param": "bounce_strength", "min": 0.0, "max": 3.0, "default": 0.85, "rand": false },
```

**Update `ambient_e` default** to 0.50 (the slider now actually affects terrain):
Find: `"id": "ambient_e"` and set `"default": 0.50`

---

## Verification after changes

1. `dotnet build WG16.csproj` — must compile clean (no reference to fill_on, sky_strength, bounce_strength, FillEnabled, PushTerrainFill)
2. Launch, check ambient slider (ambient_e) now visibly brightens/darkens shadowed terrain
3. Eye-gate shadows: should be lit by the physical AT-1 sky color (warm at golden hour, blue-white at noon) — no manual tuning needed
4. K key: AT-2 aerial is OFF by default, K turns it ON (should show the horizon haze but may still have the froxel line — that's a separate fix)
5. P key: horizon shadows OFF by default, P turns on gentle macro accent

## What this does NOT fix (separate work)
- AT-2 froxel Z-slice "visor" line: needs 64 Z-slices + log-Z mapping in `atmosphere_aerial.glsl` + `aerial_screen.gdshader`
- Horizon shadows tuning: needs eye-gate once base lighting is stable
- `ShadowMaxDist` + split tuning: may need re-tune after ambient change

## Files touched
- `shaders/ground.gdshader` — delete fill uniforms + EMISSION fragment block
- `scripts/lab/LightingComposer.cs` — fix ApplyOvercastScaling + delete PushTerrainFill + delete FillEnabled
- `data/lab_controls.json` — remove sky_strength/bounce_strength entries, update ambient_e default

## Rollback
Tag: `lighting-refactor-baseline-2026-06-25` (commit e52df36) — `git checkout lighting-refactor-baseline-2026-06-25` to revert everything.
