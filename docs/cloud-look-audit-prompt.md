# Cloud Look Audit — Review Prompt

Paste the block below into a fresh chat with a graphics/shader reviewer (or a fresh
Claude). It is self-contained.

---

You are auditing a **volumetric cloud system** in Godot 4.6 (Vulkan, compute shaders via
RenderingDevice → Texture2Drd sampled by a sky shader). The clouds RENDER correctly and are
mechanically proven (a numeric self-check confirms density and shadow coupling), but after
~5 rounds of iteration the **LOOK still reads "uniform / procedural / not great"** and the
goal (AAA, photoreal↔stylized↔sparse, believable) is not landing. I need a root-cause
diagnosis of WHY it looks procedural and a concrete, prioritized fix list — not reassurance.

## The persistent symptoms (across many iterations, all still present)
1. Clouds look "clearly procedural / uniform" — no organic variety; reads like sampled noise.
2. "Overcast" is one big solid fog sheet with occasional holes — not believable heavy cover.
3. No visible **height/size variation** even though there are now 2 cloud decks at different
   altitudes (1500m cumulus + 5500m cirrus). The decks don't read as distinct.
4. General look is flat/dull; lighting doesn't give shape or depth.

## Architecture (what exists)
- **Noise bake** (once): a 96³ "shape" volume — R = Perlin-Worley, G/B/A = single Worley
  octaves at freq 3/6/12 (tileable); a 32³ "detail" volume — single Worley (freq 8).
- **Weather**: a 256² low-freq 2D FBM, R=coverage bias, G=type, B=density bias, sampled at
  ~1/80km world scale.
- **Raymarch** (compute): writes a **512×128 lat-long hemisphere texture** (azimuth×elevation)
  that the sky shader samples by view direction. Camera-anchored curved shell (planet radius
  200km). Per-step it SUMS N cloud decks (each deck: own altitude/thickness/size/clump/
  density/opacity/type/edge/detail). `int steps = clamp(P.steps,16,160)` over the WHOLE span
  (minBase..maxTop of all decks), uniform `dt = (tEnd-tStart)/steps`; empty-air step
  acceleration. Lighting = 6-step light march toward sun + Beer + Henyey-Greenstein phase +
  a powder term + flat sky-ambient fill.

## The actual density recipe (per deck, GLSL)
```glsl
// world point p; baseR/topR = deck shell radii; coverage/type from knob*weight + weather
float h = clamp((length(p)-baseR)/thickness, 0,1);          // radial height fraction
vec3 lp = vec3(p.x, length(p)-baseR, p.z);                  // world XZ kept
coverage = clamp(P.coverage*covW + (weather.r-0.5)*0.7, 0,1);
// domain warp:
vec3 warp = (detail_tex(lp*(1/1300*0.25)).r - 0.5) * 600.0;
vec3 lpw = lp + warp;
// shape (mismatched scale ~1/9000 / size):
vec4 sh = shape_tex(lpw*sScale + windscroll);
float fbm = sh.g*0.625 + sh.b*0.25 + sh.a*0.125;
float base = remap(sh.r, fbm*0.3, 1.0, 0,1);
// coverage→threshold (cov0→0.92 clear, cov1→0.02 overcast):
float thresh = mix(0.92, 0.02, coverage);
float shape = smoothstep(thresh, thresh+mix(0.30,0.10,edge), base);
// cellularity gate (low-freq shape.G at a coarser scale):
float cell = shape_tex(lpw*sScale*0.35*clumpScale + windscroll).g;
shape *= smoothstep(mix(0.78,0.35,coverage), mix(1.0,0.6,coverage), cell);
shape *= type_gradient(h, type);     // rounded base, faded top
// detail erosion (mismatched scale ~1/1300, height-varied):
float det = detail_tex(lp*dScale + windscroll).r;
shape = remap(shape, det*mix(0.25,0.6,h)*detail, 1.0, 0,1);
return shape * density * densBias;   // densBias = mix(0.7,1.3, weather.b)
```
Lighting in the march:
```glsl
float lightT = exp(-(sum of density over 6 steps toward sun)*250m * sun_absorb*0.02);
float powder = mix(1.0, 1.0-exp(-dens*2*powderK), 0.5);
float sigma  = dens*0.02*opacity;  float beer = exp(-sigma*dt);
float sun    = lightT*(phase+0.4);
vec3 lum     = (sunColor*sun + skyAmbient*ambient) * brightness * powder;
scattered += T*lum*(1-beer);  T *= beer;
```

## What I SUSPECT but want verified or corrected
- **Too few view steps for the span**: 16–160 steps over a min-base..max-top span that can be
  ~4–7 km (with a high cirrus deck) → ~300m/step at low step counts → no fine structure, mushy
  uniform look. Should step count scale with span, or march be adaptive?
- **Lat-long 512×128 texture** is low-res, esp. near the horizon (elevation compressed) → blurry/
  blocky clouds. Is this resolution a primary cause of the "procedural/blocky" read?
- **Noise authoring**: single Worley octaves per channel (no real FBM in the volume), Perlin-
  Worley only in R. Is the noise itself too simple/low-frequency to look natural? Is the
  detail volume (single freq-8 Worley) too weak to erode believable edges?
- **Lighting is flat**: no multiple-scattering / ambient occlusion by height / silver-lining
  emphasis; powder term may be wrong. Is the flat lighting the main reason it reads dull/no-shape?
- **Coverage→threshold remap**: does `smoothstep(thresh, thresh+band, base)` inherently produce
  the "solid sheet with holes" overcast rather than distinct clumps?
- **Multi-deck not reading**: decks are summed into one density field then lit together — does
  summing (vs compositing front-to-back per deck) destroy the height separation? How should N
  decks at different altitudes be made to read as visually distinct layers?

## What I need from you
1. **Rank the root causes** of the procedural/uniform/flat look (most impactful first), with
   reasoning tied to the recipe above.
2. **Concrete fixes** for each, with the specific technique and rough parameter targets
   (step counts, texture res, noise octaves/freqs, lighting model). Cite the standard
   references (HZD/Nubis Schneider, clayjohn, GPU Pro) where relevant.
3. Call out anything in the recipe that is **wrong** (not just suboptimal).
4. A **minimal high-impact change set** — if I could only do 2–3 things to most improve the
   look, what are they?
5. Whether the **multi-deck "sum then light"** approach can ever read as distinct layers, or if
   it needs per-deck compositing / different handling.

Performance budget: target a mid-range GPU (temporal amortization is available but currently
disabled — stride clamped to 1). Dev machine is an RTX 5090, so headroom exists for the look
lab. The system must stay tunable across photoreal/stylized/sparse via knobs + presets.
```
