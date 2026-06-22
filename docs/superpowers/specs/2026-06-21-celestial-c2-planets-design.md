# Celestial C2 — Planets + Named Stars (design, 2026-06-21)

Adds bright planet points and a few "landmark" bright stars to the WG16 night sky, alongside the existing
moon + starfield + meteors. Roadmap #6 Celestial C2 (continued). Small, contained, additive.

Context: galaxy/nebula was killed 2026-06-21 (see DECISIONS); night sky = moon + stars + meteors. Planets are
bright discs/points — the easy, believable celestial accent (unlike gaseous objects).

## Goals / non-goals

**Goals**
- ~5 **planets**: bright, steady (NO twinkle), subtly colored points with a tiny core + faint soft glow,
  distinctly brighter than field stars.
- ~7 **named/landmark stars**: brighter than the field stars, slightly colored, faint glow, a slow gentle
  twinkle — standout "landmark" stars among the faint field.
- Both **ride the celestial sphere** (rotate with the starfield), gated to night + above-horizon.
- Live global tuning (on/off + brightness) via the Night tab; one `_skyMat` writer (CloudVolume).
- Performant: a handful of dot-products per pixel, free in daytime (night-gated).

**Non-goals**
- No per-planet placement/color UI (curated in-shader; see D2). No orbital simulation. No daytime visibility.
- No planet surface detail/phase (they're points, not resolved discs — user chose "bright colored points + glow").
- No separate wander relative to the stars (user chose "ride the sky"); positions are fixed on the celestial sphere.

## Decisions

- **D1 — Render inside `stars_layer`.** Planets + named stars rotate with the starfield and share its night
  `fade`, so they live in `stars_layer(rd)` using the same rotated direction `sr` the starfield already
  computes. No separate layer/rotation.
- **D2 — Curated const tables, not C#-pushed arrays.** Planets/stars are a fixed handful; their directions,
  colors, relative brightness, and sizes are `const` tables in the shader. Only **global** tuning (on/off +
  brightness) is exposed as uniforms. This is the simplest believable version with minimal plumbing. (Promotable
  to a pushed per-object array later if per-planet control is ever wanted — YAGNI now.)
- **D3 — Planets steady, named stars twinkle slowly.** No-twinkle + extra brightness is the planet "tell";
  landmark stars get a gentle, slower-than-field twinkle.

## Architecture

### Unit A — shader rendering (`shaders/cloud_sky.gdshader`)
Two helpers + two const tables, called from `stars_layer`.

- `const int NPLANETS = 5;` with `const vec4 PLANET_DIR_SIZE[5]` (xyz = unit dir on the celestial sphere,
  w = angular core size in radians) and `const vec4 PLANET_COL[5]` (rgb = color, w = relative brightness).
  Curated set: Venus (white, brightest), Mars (red), Jupiter (gold, bright), Saturn (pale gold), one blue.
- `const int NBSTARS = 7;` with `const vec4 BSTAR_DIR[7]` (xyz dir, w relative brightness) and
  `const vec3 BSTAR_COL[7]` (blue-white / white / orange landmark colors).

```
vec3 planets(vec3 sr):
  acc = 0
  for i in NPLANETS:
    dir = normalize(PLANET_DIR_SIZE[i].xyz); size = PLANET_DIR_SIZE[i].w
    cd = dot(sr, dir); if cd < 0.0: continue            // early-out (behind)
    ang = acos(clamp(cd,0,1))
    core = smoothstep(size, 0.0, ang)                   // tiny bright disc
    glow = exp(-ang * GLOW_K)                            // faint soft halo
    acc += PLANET_COL[i].rgb * (core * PLANET_COL[i].w + glow * HALO_AMT)
  return acc                                            // NO twinkle

vec3 bright_stars(vec3 sr):
  acc = 0
  for i in NBSTARS:
    dir = normalize(BSTAR_DIR[i].xyz)
    cd = dot(sr, dir); if cd < 0.0: continue
    ang = acos(clamp(cd,0,1))
    point = smoothstep(POINT_SIZE, 0.0, ang)
    glow  = exp(-ang * BSTAR_GLOW_K) * 0.4
    tw = mix(1.0, 0.6 + 0.4*sin(TIME*0.6 + float(i)*9.7), 0.5)   // slow gentle twinkle
    acc += BSTAR_COL[i] * (point * BSTAR_DIR[i].w * tw + glow)
  return acc
```

Integration — `stars_layer` return becomes:
```
vec3 p = planets_on    ? planets(sr) * planet_brightness     : vec3(0.0);
vec3 b = bright_stars_on ? bright_stars(sr) * bright_star_brightness : vec3(0.0);
return (star + p + b + meteors(rd)) * fade;
```

New uniforms: `uniform bool planets_on; uniform float planet_brightness; uniform bool bright_stars_on;
uniform float bright_star_brightness;` (defaults: on, ~1.0). Tuning constants (GLOW_K, HALO_AMT, POINT_SIZE,
BSTAR_GLOW_K) are in-shader consts.

### Unit B — C# wiring
- `LightingState.StarsState`: `bool PlanetsOn=true; float PlanetBrightness=1.0f; bool BrightStarsOn=true;
  float BrightStarBrightness=1.0f;`
- `CloudVolume`: `SetPlanets(bool on, float brightness)` and `SetBrightStars(bool on, float brightness)` →
  `_skyMat.SetShaderParameter(...)` (CloudVolume stays the sole writer).
- `TerrainLabUI.Lighting.ComposeLighting` (night block, by the star/meteor pushes): call both setters.
- `TerrainLabUI.Apply.cs`: cases `planets_on`, `planet_brightness`, `bright_stars_on`, `bright_star_brightness`.
- `data/lab_controls.json` (Night tab): toggle + `scenef` (0–3) for each of planets and bright stars.

## Lab controls & review
- Night-tab knobs: `planets (on)`, `planet brightness`, `bright stars (on)`, `bright star brightness`.
- Eye-gate via review key `2` (night). Confirm: planets read as bright steady colored points clearly brighter
  than field stars; landmark stars stand out with a slow twinkle; nothing washes the sky; daytime unaffected.

## Performance
- Night only (night_factor gate). Per night pixel: ~5 + ~7 dot products + a few cheap ops; no noise, no loops
  over big arrays. Negligible. Daytime: `stars_layer` early-outs at `night_factor <= 0.001`.

## Risks
- **R1 — planets look like just fat stars.** Mitigation: extra brightness + a faint glow halo + no twinkle +
  saturated-ish color; eye-gate and bump `planet_brightness`/size consts if needed.
- **R2 — too many bright points clutter the sky.** Mitigation: small curated counts (5 + 7), global brightness
  knobs, per-type toggles to A/B.

## Out of scope (later)
Per-planet placement UI, planet phases/discs, meteor presets, #5 shadow & lighting pass, #7 perf pass.
