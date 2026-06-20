# Ground Unit 5 — Color / Value Variation Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: superpowers:executing-plans — work this plan task-by-task, build/import/auto-shot-under-a-mood + profile each task, and STOP at every checkbox (- [ ]) for the live gate before moving on. This is a GPU/visual unit: never tick a box from a code read or a raw still — the gate is the user flying it under a mood, lit + tonemapped.

**Goal:** Replace the current washed-out macro-color tint (`macro_color`, terrain_lab.gdshader ~line 369) with proper large-scale color/value variation + per-area hue/value break, *authored to survive the scene's SDFGI/SSIL + AgX tonemap + color grade*. The current tint perturbs HSV neutral-centered then gets flattened by GI ambient and compressed by AgX — that is the "drab everywhere" problem. Unit 5 fixes the near-monochrome grey palette by (a) correcting each zone toward its intended hue, (b) driving large-scale warm/cool region drift that AgX can't crush, and (c) adding a per-fragment hue/value break with a desaturation guard so it never goes muddy.

**Architecture:** A new layered color stage, `color_grade_terrain(alb, wp, domZone, secZone)`, replaces `macro_color` at the same fragment seam (~line 505, after the material blend, before contact shading ~508). It runs four sub-passes in order — zone base-color correction → large-scale multi-octave warm/cool tint → per-fragment hue/value break → desaturation guard + AgX-survival value/contrast lift. Default path computes the large-scale tint in-shader (multi-octave value-noise, no bake dependency); a documented optional path consumes a baked `macroTint` from Unit 4's compute (RGBA #2) to remove the per-fragment octave cost. **Standalone: this unit does NOT require Unit 4** — the in-shader path is complete on its own.

**Tech Stack:** GLSL (terrain_lab.gdshader fragment + a new color stage and helpers, reusing existing `vnoise`/`rgb2hsv`/`hsv2rgb`); JSON control registry (data/lab_controls.json Color tab) for live toggles/knobs; C# Lab harness for `--mood`/`--auto-shot`/`--profile`. Optional baked-tint path: shaders/splat_weights.glsl + scripts/lab/SplatCompute.cs (NOT built in this unit unless Unit 4 already shipped the channel — see Task 6, deferred).

> **Cross-cutting (all ground units):** (1) **Seam collision with Unit 4** — Unit 4 *also* edits the `alb = macro_color(alb, wp);` region (~line 505), adding `dbg`/`breakup` blocks around it. If Unit 4 built first, find the CURRENT color-apply call by content (it may no longer be literally `macro_color`), not by line number. (2) Registry `param` rows silently no-op if the uniform is missing — every `param` here names a real new uniform; the per-zone `zone_tint_*` stay shader-side (no registry row, so NOT live-tunable — iterate by editing the shader defaults). (3) **Judge LIT + post-AgX, never raw albedo.**

---

## Environment / Gotchas (READ EVERY TASK)

- **ONE Godot at a time.** Before any run: `taskkill //F //IM Godot_v4.6.2-stable_mono_win64.exe` (ignore "not found").
- **ALWAYS `--rendering-driver vulkan`.** Local-RD compute can't run `--headless`, so the auto-shot harness runs windowed-vulkan; never pass `--headless` to a run that needs the compute splat bake.
- **After ANY change:** `dotnet build WG16.csproj` then headless import:
  `& "C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" --path C:\Wg16\wg-16-project --headless --import` (import is fine headless; the *render* run is not).
- **JUDGE LIT, NOT RAW.** Color is meaningless in raw albedo. Every auto-shot is taken under a mood, fully lit, AgX-tonemapped, post color-grade. The custom `light()` is Burley+GGX; the WorldEnvironment applies AgX + SDFGI/SSIL + adjustment brightness/contrast/saturation from data/lighting_moods.json. A change that looks great on ALBEDO can vanish or go muddy after AgX — only the lit shot counts.
- **Mood CLI** (color reads differently per light, so check three): `--mood=5` Clear Alpine (default/neutral), `--mood=0` Golden Hour (warm — watch the warm tint not blowing out), `--mood=2` Harsh Midday (flat/bright — watch tint not washing to grey). Also available: 1 Soft Overcast, 3 Blue Dawn, 4 Dramatic Storm.
- **Other lab CLIs:** `--cam`, `--clouds`, `--profile` (ms cost + FPS HUD), `--auto-shot` (A/B capture). Controls are data-driven in data/lab_controls.json (Color tab) — UI/randomizer/lock all build from it, no C# needed to add a knob.
- **Verification model (NOT TDD):** per task = `dotnet build` + headless `--import` + `--auto-shot` A/B (variation toggle on/off) under `--mood=5` AND `--mood=0` AND `--mood=2` + `--profile`. THE gate is the user flying it live, judging color life across moods + at close/mid/far. **Commit per task** once the user accepts.

---

## Task 1 — Stand up the new color stage seam + toggle (replace `macro_color`, behavior-preserving first)

Establish `color_grade_terrain` as the single color seam and a master toggle, wiring it in *equivalent to the old tint* so we have a clean A/B baseline before changing the look. No look change yet — this is the seam swap so every later task isolates.

**Files:** shaders/terrain_lab.gdshader, data/lab_controls.json

- [ ] In terrain_lab.gdshader, add the master toggle next to the macro uniforms (~line 95):
  ```glsl
  // --- Unit 5: color / value variation (replaces the old macro_color) ----------
  uniform bool color_on = true;          // master toggle for the whole Unit-5 stage
  uniform bool color_dbg_tint = false;   // debug: show ONLY the large-scale tint (grey base)
  ```
- [ ] Add the new stage function directly above `void fragment()` (~line 458), initially delegating to the old multi-octave drift so output is identical (baseline). Pass dom/sec zone so later tasks can do per-zone correction:
  ```glsl
  // Unit 5 color stage. Runs after the material blend, before contact shading.
  // Order: (1) zone base-color correction → (2) large-scale warm/cool tint →
  //        (3) per-fragment hue/value break → (4) desat-guard + AgX value lift.
  // Tasks 2-5 fill these in; Task 1 keeps the legacy drift so A/B is clean.
  vec3 color_grade_terrain(vec3 alb, vec3 wp, int domZone, int secZone){
      if(!color_on) return alb;
      // TEMP (Task 1): legacy behavior, replaced task-by-task below.
      return macro_color(alb, wp);
  }
  ```
- [ ] **First, HOIST `dom`/`sec` so they're in scope at the color seam (audit H1-A — REQUIRED, not optional; `dom`/`sec` are currently declared *inside* the `if(splat_on)` block ~475-476 and go out of scope before line 505, so the code below won't compile otherwise):**
  1. At ~line 463 (with `alb`/`nrm`/`rgh`/`ao`), add: `int dom = 0; int sec = 0;`
  2. At ~lines 475-476, change the existing `int dom = int(sp.r + 0.5);` / `int sec = clamp(...)` to **plain assignments** (drop the `int` so they assign the hoisted vars, not re-declare/shadow): `dom = int(sp.r + 0.5);` / `sec = clamp(...);`
- [ ] Then replace the `alb = macro_color(alb, wp);` call (~line 505) with:
  ```glsl
  // Unit 5: large-scale color/value variation + per-area hue/value break.
  // dom/sec are now hoisted (above); in the non-splat path pick the dominant of w[].
  int domZ = dom; int secZ = sec;
  if (!splat_on){ float bw=-1.0; domZ=0; secZ=0; for(int i=0;i<7;i++){ if(w[i]>bw){ bw=w[i]; secZ=domZ; domZ=i; } } }
  alb = color_grade_terrain(alb, wp, domZ, secZ);
  ```
- [ ] Add Color-tab controls in data/lab_controls.json after the existing `macro_scale2` row (~line 42):
  ```json
  { "id": "color_on", "label": "color stage", "tab": "Color", "type": "toggle",
    "param": "color_on", "default": true, "rand": false },
  { "id": "color_dbg_tint", "label": "show tint only", "tab": "Color", "type": "toggle",
    "param": "color_dbg_tint", "default": false, "rand": false },
  ```
- [ ] `dotnet build WG16.csproj` → headless `--import` → `--auto-shot` A/B with `color_on` on/off under `--mood=5`, `--mood=0`, `--mood=2` → `--profile`. Confirm: stage ON == legacy look (no regression), toggle isolates cleanly, ms cost unchanged. **Commit** ("Unit 5 Task 1: color stage seam + toggle, behavior-preserving").

---

## Task 2 — Zone base-color correction (fix the drab grey palette)

The 7 materials are a near-monochrome grey set (zone_color debug palette confirms intent: valley green, slope olive, cliff stone-brown, peak blue-white). Tint each zone's albedo toward its *intended* hue so the ground stops reading as uniform grey. This is the single biggest "drab" fix and is independent of any noise.

**Files:** shaders/terrain_lab.gdshader, data/lab_controls.json

- [ ] Add per-zone correction uniforms (~after line 95). Each is a target tint (multiplicative, centered on white = no-op) + a global strength so the user dials the whole correction live:
  ```glsl
  uniform float zone_tint_amp : hint_range(0.0, 1.0) = 0.55; // 0=off, 1=full target tint
  // Intended hue per zone (valley→peak). Multiplicative; >1 brightens that channel.
  // Authored warm/cool, NOT grey: valley lush green, slope dry olive, cliff warm
  // stone, peak cool. These are albedo multipliers (pre-light), kept gentle so the
  // PBR detail survives — the LIT result is what's tuned, see verification.
  uniform vec3 zone_tint_0 = vec3(0.86, 1.06, 0.78); // valley   — green push
  uniform vec3 zone_tint_1 = vec3(0.96, 1.04, 0.82); // valley→slope — yellow-green
  uniform vec3 zone_tint_2 = vec3(1.04, 1.00, 0.84); // slope    — dry olive/khaki
  uniform vec3 zone_tint_3 = vec3(1.08, 0.98, 0.86); // slope→cliff — warm earth
  uniform vec3 zone_tint_4 = vec3(1.10, 1.00, 0.90); // cliff    — warm stone
  uniform vec3 zone_tint_5 = vec3(1.02, 1.01, 1.02); // high     — near-neutral
  uniform vec3 zone_tint_6 = vec3(0.97, 1.00, 1.08); // peak/snow — cool blue
  ```
- [ ] Add the lookup + apply helper above `color_grade_terrain`:
  ```glsl
  vec3 zone_tint_by(int z){
      if(z==0) return zone_tint_0; if(z==1) return zone_tint_1;
      if(z==2) return zone_tint_2; if(z==3) return zone_tint_3;
      if(z==4) return zone_tint_4; if(z==5) return zone_tint_5;
      return zone_tint_6;
  }
  // Pull albedo toward the zone's intended hue. dom/sec blended 70/30 so the
  // correction follows the same material blend the surface already shows (no seam).
  vec3 apply_zone_correction(vec3 alb, int domZone, int secZone){
      vec3 tint = mix(zone_tint_by(domZone), zone_tint_by(secZone), 0.30);
      return alb * mix(vec3(1.0), tint, zone_tint_amp);
  }
  ```
- [ ] In `color_grade_terrain`, make this sub-pass (1) run before the legacy drift:
  ```glsl
  vec3 color_grade_terrain(vec3 alb, vec3 wp, int domZone, int secZone){
      if(!color_on) return alb;
      // (1) zone base-color correction — fix the grey palette toward intended hues.
      alb = apply_zone_correction(alb, domZone, secZone);
      // (2-4) still legacy drift for now.
      return macro_color(alb, wp);
  }
  ```
- [ ] Add the Color-tab knob (the per-zone vec3s stay shader-side defaults; expose the global strength so the user tunes it live):
  ```json
  { "id": "zone_tint_amp", "label": "zone correct", "tab": "Color", "type": "slider",
    "param": "zone_tint_amp", "min": 0, "max": 1, "default": 0.55, "rand": true },
  ```
- [ ] `dotnet build` → `--import` → `--auto-shot` A/B with `zone_tint_amp` at 0 vs 0.55 under `--mood=5/0/2` → `--profile`. **Critically judge the LIT shot**: do valley/slope/cliff/peak now read as distinct, living hues (not grey) AFTER AgX? Watch `--mood=0` (golden) doesn't push warm zones to orange-blowout and `--mood=2` (midday) doesn't flatten them back to grey. Tune the vec3 defaults from the lit result, not albedo. **Commit** ("Unit 5 Task 2: per-zone base-color correction").

---

## Task 3 — Large-scale warm/cool region tint (multi-octave, AgX-survivable)

Replace the legacy neutral-centered HSV drift with a large-scale warm↔cool *value-and-temperature* tint over big regions (Far Cry / Horizon look): some areas read warmer/brighter, others cooler/darker. Authored in linear RGB as a warm/cool *lerp* (not a tiny hue nudge) so AgX — which compresses chroma — still leaves a visible difference. In-shader multi-octave `vnoise` by default (no bake dependency).

**Files:** shaders/terrain_lab.gdshader, data/lab_controls.json

- [ ] Add the large-scale tint uniforms (~after the zone uniforms). Two explicit endpoint colors so the drift is a perceptual warm↔cool ramp, plus value amplitude and two octave wavelengths:
  ```glsl
  uniform float macro_tint_amp : hint_range(0.0, 1.0) = 0.40;  // warm/cool strength
  uniform float macro_val_drift : hint_range(0.0, 0.5) = 0.18; // big-region value drift
  uniform vec3  macro_warm = vec3(1.10, 1.02, 0.88);           // warm-region multiplier
  uniform vec3  macro_cool = vec3(0.90, 0.98, 1.10);           // cool-region multiplier
  uniform float macro_wl1 : hint_range(200.0, 1200.0) = 620.0; // big octave wavelength (m)
  uniform float macro_wl2 : hint_range(80.0, 500.0)  = 230.0;  // medium octave wavelength
  ```
- [ ] Add the tint helper (uses existing `vnoise`; combines two octaves to a signed field in ~[-0.75,0.75]; maps to a warm/cool multiplier + a value drift):
  ```glsl
  // Large-scale warm/cool + value field. Returns an albedo MULTIPLIER, mean≈1 so
  // it both warms+brightens and cools+darkens. In-shader path (no bake needed).
  // To swap to the baked path (Unit 4 macroTint, RGBA #2): replace the two vnoise
  // lines with `vec2 m = texture(grounddata_tex, v_uv).?? ;` decoded to (temp,val).
  vec3 macro_tint_field(vec3 wp){
      float n1 = vnoise(wp.xz, macro_wl1) - 0.5;                       // big region
      float n2 = vnoise(wp.xz + vec2(57.0,131.0), macro_wl2) - 0.5;   // medium region
      float drift = clamp((n1 + 0.5*n2), -0.75, 0.75);                // signed temp field
      // temperature: lerp warm↔cool around neutral by the signed drift.
      vec3 temp = mix(vec3(1.0), (drift > 0.0) ? macro_warm : macro_cool,
                      abs(drift) * macro_tint_amp);
      // value: big regions also breathe brighter/darker (survives AgX better than chroma).
      float val = 1.0 + drift * macro_val_drift;
      return temp * val;
  }
  ```
- [ ] In `color_grade_terrain`, make this sub-pass (2) and DROP the legacy `macro_color`. Honor the `color_dbg_tint` debug (show the tint on a neutral grey so the user can read the region pattern directly):
  ```glsl
  vec3 color_grade_terrain(vec3 alb, vec3 wp, int domZone, int secZone){
      if(!color_on) return alb;
      vec3 base = (color_dbg_tint) ? vec3(0.5) : alb;
      base = apply_zone_correction(base, domZone, secZone);   // (1)
      base *= macro_tint_field(wp);                            // (2) large-scale warm/cool
      // (3-4) added next tasks.
      return max(base, vec3(0.0));
  }
  ```
- [ ] **Retire the now-dead `macro_color` (audit cleanup).** Once nothing calls it, the `macro_color` function AND its Color-tab registry rows (`macro_on`/`macro_val_amp`/`macro_hue_amp`/`macro_sat_amp`/`macro_scale2`, `data/lab_controls.json` ~33-42) are orphaned — they'd show in the UI doing nothing. Remove those 5 rows (or, if you want them for A/B during tuning, keep ONLY and label them "legacy"). Delete the `macro_color` function body. **Do NOT delete `macro_m`/`macro_amp` uniforms** — they're still read by `zone_weights()` (mask logic), unrelated to color. Validate JSON after.
- [ ] Add Color-tab knobs:
  ```json
  { "id": "macro_tint_amp", "label": "warm/cool", "tab": "Color", "type": "slider",
    "param": "macro_tint_amp", "min": 0, "max": 1, "default": 0.40, "rand": true },
  { "id": "macro_val_drift", "label": "region value", "tab": "Color", "type": "slider",
    "param": "macro_val_drift", "min": 0, "max": 0.5, "default": 0.18, "rand": true },
  { "id": "macro_wl1", "label": "region wl1", "tab": "Color", "type": "slider",
    "param": "macro_wl1", "min": 200, "max": 1200, "default": 620, "rand": true },
  { "id": "macro_wl2", "label": "region wl2", "tab": "Color", "type": "slider",
    "param": "macro_wl2", "min": 80, "max": 500, "default": 230, "rand": true },
  ```
- [ ] `dotnet build` → `--import` → `--auto-shot` A/B (`macro_tint_amp` 0 vs 0.40), AND a `color_dbg_tint=true` shot to read the region pattern, under `--mood=5/0/2` → `--profile`. **Judge LIT**: do you see large warm/cool regions breathing across the terrain after AgX, at mid/far range, without banding? Tune wavelengths so regions span the view (not too tight, not one flat color). **Commit** ("Unit 5 Task 3: large-scale warm/cool region tint; retire legacy drift").

---

## Task 4 — Per-fragment hue/value break (fine breakup, in-shader)

Add a high-frequency hue+value micro-break so adjacent texels aren't identical — kills the "plastic uniform" read at close range. This is the per-fragment detail the spec keeps in-shader (cheap, NOT a bake candidate). Uses HSV so the break is gentle and chroma-aware.

**Files:** shaders/terrain_lab.gdshader, data/lab_controls.json

- [ ] Add break uniforms:
  ```glsl
  uniform float hue_break_amp : hint_range(0.0, 0.08) = 0.018; // per-fragment hue jitter
  uniform float val_break_amp : hint_range(0.0, 0.30) = 0.10;  // per-fragment value jitter
  uniform float break_wl : hint_range(2.0, 40.0) = 11.0;       // break wavelength (m), small
  ```
- [ ] Add the break helper (small-wavelength noise into HSV hue + back-to-RGB value):
  ```glsl
  // Fine per-fragment hue/value break. Two decorrelated small-wl fields so hue and
  // value don't move together (avoids a tinted-brightness look). Mean-0 → no drift.
  vec3 apply_hue_value_break(vec3 c, vec3 wp){
      float hn = vnoise(wp.xz, break_wl) - 0.5;
      float vn = vnoise(wp.xz + vec2(211.0, 17.0), break_wl*0.7) - 0.5;
      vec3 hsv = rgb2hsv(c);
      hsv.x = fract(hsv.x + hn * hue_break_amp);
      vec3 rgb = hsv2rgb(hsv);
      return rgb * (1.0 + vn * val_break_amp);
  }
  ```
- [ ] Wire as sub-pass (3) in `color_grade_terrain`, after the large-scale tint:
  ```glsl
      base *= macro_tint_field(wp);                            // (2)
      base = apply_hue_value_break(base, wp);                  // (3) fine breakup
      // (4) desat guard + AgX lift next task.
      return max(base, vec3(0.0));
  ```
- [ ] Add Color-tab knobs:
  ```json
  { "id": "hue_break_amp", "label": "hue break", "tab": "Color", "type": "slider",
    "param": "hue_break_amp", "min": 0, "max": 0.08, "default": 0.018, "rand": true },
  { "id": "val_break_amp", "label": "value break", "tab": "Color", "type": "slider",
    "param": "val_break_amp", "min": 0, "max": 0.30, "default": 0.10, "rand": true },
  { "id": "break_wl", "label": "break wl", "tab": "Color", "type": "slider",
    "param": "break_wl", "min": 2, "max": 40, "default": 11, "rand": true },
  ```
- [ ] `dotnet build` → `--import` → `--auto-shot` A/B (break amps 0 vs default) under `--mood=5/0/2`, especially a CLOSE `--cam` → `--profile`. **Judge LIT**: at close range the surface should have subtle living variation, NOT visible noise/speckle (if it speckles, lower amps or raise `break_wl`). Confirm no specular interaction surprise (this is albedo-only). **Commit** ("Unit 5 Task 4: per-fragment hue/value break").

---

## Task 5 — Desaturation guard + AgX-survival value/contrast lift

AgX desaturates and S-curves; combined with SDFGI ambient fill the ground can still read flat/muddy. This final sub-pass (4) guarantees the result keeps chroma and reads with value contrast AFTER the tonemap: a saturation floor (guard against muddy), a gentle saturation lift, and a value-contrast lift around a pivot so mid-greys separate. This is what makes the whole stage "survive AgX."

**Files:** shaders/terrain_lab.gdshader, data/lab_controls.json

- [ ] Add the guard/lift uniforms:
  ```glsl
  uniform float sat_floor : hint_range(0.0, 0.4) = 0.06;   // min saturation (anti-muddy)
  uniform float sat_lift  : hint_range(0.0, 0.6) = 0.18;   // pre-AgX saturation boost
  uniform float val_contrast : hint_range(0.0, 0.5) = 0.16;// value contrast around pivot
  uniform float val_pivot : hint_range(0.2, 0.7) = 0.42;   // contrast pivot (scene-tuned)
  ```
- [ ] Add the helper (HSV: lift+floor saturation; apply contrast to value around pivot). AgX eats chroma, so we *over*-saturate slightly pre-tonemap on purpose:
  ```glsl
  // Final guard: ensure the graded albedo survives AgX with life. Saturation floor
  // prevents the muddy-grey collapse; sat_lift compensates AgX's desaturation; value
  // contrast around a pivot restores separation AgX's toe/shoulder flattens.
  vec3 agx_survive_grade(vec3 c){
      vec3 hsv = rgb2hsv(c);
      hsv.y = max(hsv.y, sat_floor);                       // anti-muddy floor
      hsv.y = clamp(hsv.y * (1.0 + sat_lift), 0.0, 1.0);   // pre-AgX over-saturate
      // value contrast around pivot in HSV VALUE (bounded), NOT RGB extrapolation
      // (audit MED: mix(pivot,rgb,1+contrast) had no upper bound → blew >1 and AgX
      // desaturated-to-white the brights under --mood=0, the opposite of the goal).
      hsv.z = clamp(val_pivot + (hsv.z - val_pivot) * (1.0 + val_contrast), 0.0, 1.0);
      return max(hsv2rgb(hsv), vec3(0.0));
  }
  ```
- [ ] Wire as sub-pass (4), the last step of `color_grade_terrain`:
  ```glsl
      base = apply_hue_value_break(base, wp);                  // (3)
      base = agx_survive_grade(base);                          // (4) survive AgX
      return max(base, vec3(0.0));
  ```
- [ ] Add Color-tab knobs:
  ```json
  { "id": "sat_floor", "label": "sat floor", "tab": "Color", "type": "slider",
    "param": "sat_floor", "min": 0, "max": 0.4, "default": 0.06, "rand": false },
  { "id": "sat_lift", "label": "sat lift", "tab": "Color", "type": "slider",
    "param": "sat_lift", "min": 0, "max": 0.6, "default": 0.18, "rand": true },
  { "id": "val_contrast", "label": "val contrast", "tab": "Color", "type": "slider",
    "param": "val_contrast", "min": 0, "max": 0.5, "default": 0.16, "rand": true },
  { "id": "val_pivot", "label": "val pivot", "tab": "Color", "type": "slider",
    "param": "val_pivot", "min": 0.2, "max": 0.7, "default": 0.42, "rand": false },
  ```
- [ ] `dotnet build` → `--import` → `--auto-shot` A/B (`color_on` full stack vs off) under `--mood=5/0/2` → `--profile`. **This is the headline judgment, all LIT/post-AgX**: side-by-side the old drab-grey vs the new graded ground — does it read with life, distinct zone hues, large warm/cool regions, and value separation across all three moods at close/mid/far? Verify the sat_floor stops any mood from going muddy and the contrast lift doesn't clip highlights under `--mood=2`. Re-tune `val_pivot`/`val_contrast` to the AgX result. **Commit** ("Unit 5 Task 5: desat guard + AgX-survival value/contrast lift").

---

## Task 6 — (DEFERRED / OPTIONAL) Baked large-scale tint path — only if Unit 4 shipped `macroTint`

The large-scale tint (Task 3 `macro_tint_field`) is a pure function of world position → a per-texel bake candidate. Baking it into the ground-data texture (Unit 4's compute, RGBA #2) removes the two per-fragment `vnoise` octaves. This is the *performant* upgrade, NOT required for the unit to be good. **Do this ONLY if `--profile` (Task 3/5) shows the octave noise is a measurable cost AND Unit 4 already provides a `macroTint`/ground-data channel** — do not add compute speculatively.

**Files:** shaders/splat_weights.glsl, scripts/lab/SplatCompute.cs, shaders/terrain_lab.gdshader

- [ ] Confirm Unit 4 shipped a ground-data texture with a free RGBA channel (`groundData(uv).macroTint` per the arc spec interface). If NOT, STOP — keep the in-shader path; this task is closed.
- [ ] In shaders/splat_weights.glsl, in the per-texel bake, compute the SAME signed temp field as `macro_tint_field` (two value-noise octaves at `macro_wl1`/`macro_wl2`) and pack `(0.5+0.5*drift)` into the chosen channel of the ground-data output (8-bit is plenty for a low-freq field).
- [ ] In scripts/lab/SplatCompute.cs, pass `macro_wl1`/`macro_wl2` as push-constants/uniforms so the bake matches the shader defaults; re-bake on change (it is part of the existing splat/ground bake, no new dispatch).
- [ ] In terrain_lab.gdshader `macro_tint_field`, add a `uniform bool macro_tint_baked = false;` branch: when true, read the baked channel, decode `drift = 2.0*ch - 1.0`, and skip the two `vnoise` calls; reuse the same warm/cool + value mapping so on/off is visually identical.
- [ ] Add `{ "id": "macro_tint_baked", "label": "tint baked", "tab": "Color", "type": "toggle", "param": "macro_tint_baked", "default": false, "rand": false }`.
- [ ] `dotnet build` → `--import` (windowed-vulkan run — compute can't go headless) → `--auto-shot` A/B (baked vs in-shader) under `--mood=5/0/2` → `--profile`. **Judge LIT**: baked path must be visually identical to in-shader AND cheaper in ms. **Commit** ("Unit 5 Task 6: optional baked large-scale tint path").

---

## Self-Review notes

**Spec coverage.** Every Unit-5 requirement from the arc spec (lines 57, 87-88, 94) is a task: replace washed macro tint (Task 1 seam swap + Task 3 retire legacy `macro_color`), large-scale tint warm/cool regions (Task 3), per-zone base-color correction to fix the drab grey palette (Task 2 — the spec's "palette/zone-assignment retune rides along with units 4-5"), per-fragment hue/value break (Task 4), value/contrast lift that survives AgX + desaturation guard so it doesn't go muddy (Task 5). The performant baked-tint path is captured as deferred/justified (Task 6), not speculative.

**Placeholder scan.** No placeholders: all GLSL and JSON is concrete and uses the verified existing symbols — `vnoise(vec2,float)` (line 155), `rgb2hsv`/`hsv2rgb` (lines 355/362), the splat-path `dom`/`sec`/`mix m` (lines 475-486), the `w[7]` weights (line 461), the `macro_color` seam at line 505, and the Color-tab JSON shape (data/lab_controls.json lines 33-42). The only thing left to the worker is numeric tuning of the tint vec3s/amps — which is *correct*, because per the verification model those values can only be set from the LIT, post-AgX result, live.

**Consistency / Unit-4 dependency.** This plan is STANDALONE: Tasks 1-5 require nothing from Unit 4 — the large-scale tint is computed in-shader via `vnoise` (Task 3). Unit 4 is touched only in Task 6, which is explicitly OPTIONAL and gated on (a) a measured perf need and (b) Unit 4 having already shipped a `macroTint` ground-data channel; if either is false, Task 6 closes with no change. Zone indices, the splat read, and the fragment seam all match the current shader, so the unit composes with Units 1-4 (it runs after the material blend, before contact shading, exactly where `macro_color` ran).

**Audit fixes applied.** H1-A — `dom`/`sec` are now explicitly HOISTED to ~463 + the in-branch declarations changed to assignments (the old code block referenced out-of-scope vars → wouldn't compile). H1-B — added the Unit-4 seam-collision note (find the color-apply call by content, not line). MED — value contrast moved to bounded HSV value (was unbounded RGB extrapolation `mix(pivot,rgb,1+contrast)` → AgX-blowout under `--mood=0`); + a step to retire the orphaned `macro_color` + its 5 Color-tab rows (keeping `macro_m`/`macro_amp` which `zone_weights` still uses).

**Validation is UNDER LIGHT + TONEMAP — explicit.** Every task's gate auto-shots the LIT, AgX-tonemapped, color-graded result under a mood (default `--mood=5`, plus `--mood=0` golden and `--mood=2` midday because chroma reads differently per light), never raw albedo — this is the whole point of the unit (the old tint died under GI+AgX). Task 5 specifically over-saturates and lifts contrast *pre-tonemap* to counter AgX's desaturation/S-curve, and the saturation floor is the anti-muddy guard. The final gate is the user flying it live across moods at close/mid/far; commit per task only after acceptance.
