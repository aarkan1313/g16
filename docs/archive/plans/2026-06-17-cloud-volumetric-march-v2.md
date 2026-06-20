# Volumetric Cloud March v2 (curved shell, camera-anchored) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans (inline) — the author is executing this directly. Steps use `- [ ]`. The GATE is the USER's live eye in motion; mechanical checks (builds, runs windowed, no shader errors, no full-sky-fog at high coverage) are the automatable part.

**Goal:** Replace the flat world-space slab (which fixed jitter+coupling but looked bad far off, vanished from above, and turned dense presets into full-sky fog) with a proper **camera-anchored volumetric march through a curved cloud shell** — restoring AAA cloud quality from every angle while keeping clouds and the ground shadow map in one world-space frame. Plus a coverage remap that keeps gaps at high coverage, a camera-above clamp, dense-preset colour/look fixes, and performant AAA god rays.

**Architecture:** Keep everything proven — the baked Perlin-Worley volumes, the compute→`Texture2Drd`→sky-shader path, the 2D shadow-map compute, the render-thread `CallOnRenderThread` plumbing. Change only the MARCH GEOMETRY and the DENSITY REMAP. Rays start at the camera (world space, so shadow stays coupled) and intersect a **curved shell** `[PLANET_R+altitude, PLANET_R+altitude+thickness]` via the existing `ray_sphere` (restored) — at the horizon the ray traverses a longer chord through the shell → a real horizon cloud band (the flat slab's constant thin path was the "washed out far off" bug). When the camera rises above the layer, **clamp the march origin to just below the layer** (clayjohn's trick — clouds aren't geometry, always rendered as if viewed from below) → fixes "disappear from above". Coverage raises the noise THRESHOLD (carving gaps) instead of scaling global density → dense presets keep holes instead of becoming fog.

**Tech Stack:** Godot 4.6 mono; GLSL compute on the main RD via `CallOnRenderThread`; `Texture2Drd` sampled by `cloud_sky.gdshader` (by EYEDIR) + terrain `light()` (world XZ).

---

## File Structure

- `shaders/cloud_raymarch.glsl` — MODIFY: restore curved `ray_sphere` shell intersection from the camera (keep `cam_world`); add camera-above clamp; rework the coverage→gap remap + height-fraction density gradient + lighting so dense looks read right and colour is correct.
- `shaders/cloud_shadow.glsl` — MODIFY: mirror the SAME curved-shell intersection + the SAME coverage/density remap (must stay byte-identical to the raymarch's `sample_density` or shadows desync).
- `shaders/cloud_godray_fog.gdshader` + `scenes/terrain_lab.tscn` (Sun) — MODIFY: AAA god rays — reduce `light_volumetric_fog_energy` so the directional light's volumetric SHADOWS don't paint hard dark bands; make the fog a thin, clamped, sun-gated haze. (If volumetric-fog god rays still read poorly, fall back to a screen-space radial light-shaft post pass — noted as the alternative, not built unless needed.)
- `data/cloud_presets.json` — MODIFY: re-tune Overcast (flat, real gaps, light grey) + Stormy (dark, towering, gaps, NOT full sky) once the remap is fixed.

## Key facts (verified, in context)

- The OLD pre-slab raymarch used `ray_sphere(ro, rd, baseR/topR)` with `ro=(0,PLANET_R+1,0)` and `PLANET_R=200000`. That gave the good far-off dome look. The bug was only that `ro` was the ORIGIN (not the camera) → dome decoupled from shadows + swam. Fix = same curved shell, but `ro = cam_world` (+ planet offset).
- `sample_density` currently: `lo = 1-coverage; base = remap(base, lo, lo+band, 0,1); base *= type_gradient(h,type); base *= P.density`. The `*= P.density` with density→2.4 (Stormy) is what makes solid fog. The remap DOES threshold but density then floods it.
- Terrain `light()` samples the shadow map at world XZ (`terrain_lab.gdshader:541`) — UNCHANGED; world-space coupling is preserved as long as the shadow march stays world-XZ (it does).
- `dir_from_texel`/`dir_to_uv` lat-long mapping is shared raymarch↔sky — leave it; with a curved shell + camera origin the texture is still sampled by EYEDIR correctly.

---

### Task 1: Restore the curved camera-anchored shell in the raymarch

**Files:** Modify `shaders/cloud_raymarch.glsl`

- [ ] **Step 1: Re-add PLANET_R and ray_sphere; build the shell from the camera**

Re-add at top: `const float PLANET_R = 200000.0;` and the `ray_sphere(ro,rd,R)` helper (it was removed for the slab). In `main()`, replace the flat-plane block:
```glsl
    vec3 rd = dir_from_texel(px);
    // Camera-anchored shell: ray origin = camera, lifted into planet space. Clamp the
    // origin to just below the cloud base when the camera is above the layer (clouds are
    // not geometry — always march as if viewed from under the shell). Fixes "vanish from
    // above"; the curved shell gives the horizon a real cloud band (flat slab did not).
    float camY = P.cam_world.y;
    float belowBase = min(camY, P.altitude - 1.0);            // never start above the base
    vec3 ro = vec3(P.cam_world.x, PLANET_R + belowBase, P.cam_world.z);
    float baseR = PLANET_R + P.altitude;
    float topR  = baseR + P.thickness;
    vec2 hitB = ray_sphere(ro, rd, baseR);
    vec2 hitT = ray_sphere(ro, rd, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);
```
Keep `cam_world` in the params (already added). Keep `if (rd.y > 0.02 && tEnd > tStart)`.

- [ ] **Step 2: Restore height_fraction for the shell + pass baseR/topR**

`sample_density`/`light_march` were changed to `base_y/top_y` (world Y) for the slab. For the shell, height fraction is radial again. Change them back to take `baseR,topR` and use:
```glsl
float h = clamp((length(p) - baseR) / max(topR - baseR, 1.0), 0.0, 1.0);
vec3 lp = vec3(p.x, length(p) - baseR, p.z);   // XZ world (matches shadow), Y = height above base
```
Update both calls in `main()` to `sample_density(p, baseR, topR, windOff)` and `light_march(p, L, baseR, topR, windOff)`.

- [ ] **Step 3: Build + run windowed; confirm clouds look right near AND far, and from above**

Run: `dotnet build WG16.csproj`, launch `scenes/terrain_lab.tscn` (`--rendering-driver vulkan`).
Expected (MECHANICAL): builds, "compute initialized" prints, no shader errors.
Expected (VISUAL GATE — user): clouds form a band toward the horizon (not washed out), still good overhead, and DON'T vanish when flying above the layer. Flag for user — judge in motion.

- [ ] **Step 4: Commit**
```bash
git add shaders/cloud_raymarch.glsl
git commit -m "Clouds: curved camera-anchored shell raymarch (fixes far-off + above-layer; keeps world coupling)"
```

---

### Task 2: Coverage→gap remap + height gradient so dense ≠ fog

**Files:** Modify `shaders/cloud_raymarch.glsl` (`sample_density`)

- [ ] **Step 1: Make coverage carve gaps; stop density flooding**

Replace the remap tail of `sample_density`:
```glsl
    // Coverage raises the THRESHOLD the base noise must beat → higher coverage = bigger
    // blobs but STILL gaps (never a solid fill). density scales optical depth of what
    // remains, NOT whether the gap exists — so Overcast/Stormy keep holes, not full fog.
    float cov = clamp(coverage, 0.0, 1.0);
    float thresh = mix(0.6, 0.05, cov);          // high cov → low threshold → more cloud, but >0 always carves
    float shape = smoothstep(thresh, min(thresh + mix(0.35, 0.12, P.edge), 1.0), base);
    shape *= type_gradient(h, type);             // rounded bottoms, wispy tops
    if (shape <= 0.0) return 0.0;                 // hard gap — sky shows through
    // detail erosion (unchanged intent)
    if (P.detail > 0.0){
        float dScale = 0.004 / max(P.detail_size, 0.01);
        vec3 duv = lp * dScale + vec3(windOff.x, h, windOff.y) * dScale;
        float det = texture(detail_tex, duv).r;
        shape = clamp(remap(shape, det * 0.5 * P.detail, 1.0, 0.0, 1.0), 0.0, 1.0);
    }
    return shape * P.density;
```
(Replace the existing `float band=...; float lo=...; base=remap(...); base*=type_gradient; if(detail){...}; return base*P.density;` block. Keep the earlier `coverage`/`type` weather-field lines + the shape-noise `base` computation above it.)

- [ ] **Step 2: Build + run; verify high coverage keeps gaps**

Run: build, launch, set coverage 0.9 (or pick Overcast).
Expected (MECHANICAL): at coverage 0.9 the sky is NOT a uniform fill — gaps/holes remain. No full-sky fog.
Expected (VISUAL GATE — user): dense ≠ fog; reads as heavy cloud cover with structure. Flag for user.

- [ ] **Step 3: Commit**
```bash
git add shaders/cloud_raymarch.glsl
git commit -m "Clouds: coverage raises noise threshold (carves gaps) instead of flooding density (kills full-sky fog)"
```

---

### Task 3: Mirror the shell + remap in the shadow shader

**Files:** Modify `shaders/cloud_shadow.glsl`

- [ ] **Step 1: Re-add PLANET_R + ray_sphere; curved shell from the ground point**

The shadow marches from a ground world-XZ point toward the sun. Re-add `PLANET_R` + `ray_sphere`. Set:
```glsl
    vec3 ro = vec3(wxz.x, PLANET_R + P.ground_height, wxz.y);
    vec3 L = normalize(P.sun_dir.xyz);
    float baseR = PLANET_R + P.altitude;
    float topR  = baseR + P.thickness;
    vec2 hitB = ray_sphere(ro, L, baseR);
    vec2 hitT = ray_sphere(ro, L, topR);
    float tStart = max(hitB.y, 0.0);
    float tEnd   = max(hitT.y, 0.0);
```

- [ ] **Step 2: Make sample_density byte-identical to the raymarch's**

Copy the EXACT `sample_density` body from `cloud_raymarch.glsl` (the new threshold/gap version + the radial `h`/`lp`). They MUST match or the shadow desyncs from the visible cloud. Use `baseR,topR` signature + radial `h = (length(p)-baseR)/...`, `lp = vec3(p.x, length(p)-baseR, p.z)`.

- [ ] **Step 3: Build + run; check a cloud's shadow lands under it**

Run: build, launch, fly under a cloud.
Expected (VISUAL GATE — user): shadow sits roughly beneath its cloud (offset by sun angle). Flag for user (the headline coupling check).

- [ ] **Step 4: Commit**
```bash
git add shaders/cloud_shadow.glsl
git commit -m "Cloud shadow: mirror curved shell + gap remap (stays coupled to the visible cloud)"
```

---

### Task 4: AAA god rays — tame the volumetric shadow bands

**Files:** Modify `scenes/terrain_lab.tscn` (Sun), `shaders/cloud_godray_fog.gdshader`

- [ ] **Step 1: Lower the directional light's volumetric fog energy**

The hard dark vertical bands are the SUN's volumetric shadows in fog (`light_volumetric_fog_energy = 1.5` + `directional_shadow_max_distance = 8000`). In `terrain_lab.tscn` Sun node, drop `light_volumetric_fog_energy` to ~0.4 (enough for shafts, not opaque shadow slabs). Keep shadows for the terrain (that's separate from the fog energy).

- [ ] **Step 2: Make the gap fog additive-thin and tied to sun visibility**

In `cloud_godray_fog.gdshader` (already clamped + footprint-guarded from the prior pass), confirm `godray_density` ~0.0025, `godray_max` ~0.006. The shafts come from the LIT gaps (high `vis`) scattering the (now lower-energy) sun. The point: shafts appear where sun reaches through cloud holes, NOT dark bands where terrain shadows the fog.

- [ ] **Step 3: Build + run; toggle god rays ON**

Run: build, launch, enable "god rays (live-tune)".
Expected (VISUAL GATE — user): subtle light shafts through cloud gaps; NO hard black vertical bands; scene not darkened. Flag for user tuning.
**FALLBACK (only if still bad after a fair try):** screen-space radial light shafts — sample the depth/sun-occlusion in a post pass and radial-blur from the sun screen position. Cheaper and decoupled from the fog. Note to user; don't build unless the volumetric path can't be made good (avoid grinding — STOP clause).

- [ ] **Step 4: Commit**
```bash
git add scenes/terrain_lab.tscn shaders/cloud_godray_fog.gdshader
git commit -m "God rays: lower sun volumetric-fog energy (kills shadow bands); thin sun-gated gap fog"
```

---

### Task 5: Re-tune Overcast + Stormy against the fixed system

**Files:** Modify `data/cloud_presets.json`

- [ ] **Step 1: Re-tune now the remap keeps gaps**

User feedback: Overcast/Stormy "totally fill the sky, fog-like, too many clouds, look + colour wrong." With the threshold remap, lower coverage gives real structure:
- **Overcast:** coverage ~0.7 (not 0.92), flat (type ~0.15), even light grey (brightness ~0.9, ambient ~1.0), thin (thickness ~900). A complete but textured grey ceiling WITH some variation, not a fog wall.
- **Stormy:** coverage ~0.6 (not 0.78), dark (brightness ~0.45, ambient ~0.4), towering (type ~0.9), thick (thickness ~2600), lower base (~900). Menacing dark cells with gaps + bright edges, not a full-sky blanket. Pair note: best under a storm MOOD (dimmer sun).
Colour: ensure `ambient` pulls from the mood sky (already wired via `sky_top/sky_horizon`); for storm the mood should be grey/dark — document that Stormy + a bright midday mood will look wrong (it's a coordinated look).

- [ ] **Step 2: Build + run; cycle Overcast/Stormy**

Run: launch, pick Overcast then Stormy.
Expected (VISUAL GATE — user): both read as their name, have structure + gaps (not fog), colour reads right under a matching mood. Flag for user.

- [ ] **Step 3: Commit**
```bash
git add data/cloud_presets.json
git commit -m "Clouds: re-tune Overcast/Stormy for the gap remap (structured cover, not full-sky fog)"
```

---

### Task 6: Docs + re-review

- [ ] **Step 1: DECISIONS.md (newest-first)** — curved-shell volumetric march supersedes the flat slab; why (slab fixed coupling+jitter but wrong shape); coverage-gap remap; god-ray fog-energy fix. Reference this plan + the memory note.
- [ ] **Step 2: HANDOFF §6 + ROADMAP** — update the cloud review-backlog item to "v2 world-space volumetric march, pending review."
- [ ] **Step 3: Launch + hand the user the checklist:** near+far cloud quality, above-layer, shadow-under-cloud, gaps at high coverage, Overcast/Stormy sensible, god rays subtle.
- [ ] **Step 4: Commit docs.**

---

## Self-Review

- **Spec coverage:** curved shell (T1) fixes far-off + above; gap remap (T2) fixes full-sky fog; shadow mirror (T3) keeps coupling; god rays (T4); presets (T5). All user feedback items covered: "bad far off," "vanish above," "Overcast/Stormy fog/too many/wrong colour," "god rays bad," + jitter (already better, preserved by camera-anchoring).
- **Type consistency:** `sample_density`/`light_march` revert to `baseR,topR` signature in BOTH raymarch and shadow — and the bodies MUST stay identical (the coupling guarantee). `cam_world` param stays (Task already shipped). `ParamFloats=52` unchanged.
- **Risk:** all changes are in the two cloud shaders + presets + one Sun setting; `git checkout .` reverts; clouds toggle off → original sky. The identical-`sample_density` requirement is the main footgun — copy-paste it, don't paraphrase. God rays have a STOP/fallback (screen-space) so they don't become a grind.
- **Gate:** user's eye in motion; mechanical steps prove build/run/no-fog-at-0.9-coverage only.
