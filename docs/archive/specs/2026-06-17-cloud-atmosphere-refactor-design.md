# Cloud + Atmosphere System — Refactor Design (from scratch, tunable AAA)

Date: 2026-06-17 · Status: awaiting user review · Lane: sky/atmosphere (full replacement of
the cloud render path; presence + god rays rebuilt on the new foundation)

## Why (and why from scratch)

The existing cloud system was patched three times in one session — origin-anchored infinity
dome → flat world-space slab → curved camera-anchored shell — and the user rejected all three
on LOOK: "clouds look kind of repetitive, clearly procedural now," "every preset has a ton of
clouds," "can't get clouds to go [clear] without a lot of settings being messed with," "god
rays need destroyed and remade." Verdict: **full refactor.** The patches fixed coordinate/
jitter bugs but never the underlying problem — the **density model and noise authoring were
hand-rolled and too small/aligned to look natural**, the coverage remap never reached true
clear, and god rays were a FogVolume that collided with the directional light's volumetric
shadows (the black wedges). This is a fresh, look-first design.

## The mandate (user, this session)

- **Tunable range — photoreal volumetric AND stylized AND sparse-realistic must ALL be
  reachable by tuning.** Not one look; a system whose knob range spans all three. Keep the
  rich adaptability of the current knob set + presets as entry points.
- **Kill repetition at ALL ranges (near, overhead, horizon) while staying performant.** The
  #1 visible failure. Hard bar: never reads as a tiling grid; not at the cost of framerate.
- **True clear → full overcast from the coverage knob, plus great presets.** Coverage=0 = a
  genuinely clear blue sky; coverage=1 = full overcast; smooth between. The raw knob must
  "just work" end to end, AND the curated presets must each look right.
- **Mid-range GPU budget** (not the dev's RTX 5090). Lean on temporal amortization + half-res
  + smart sampling so it ships broadly; the 5090 then has headroom.
- **Keep ALL presence features**, rebuilt right on the new foundation: matched ground cloud-
  shadows, overcast scene dimming, aerial-perspective tint, reflections/GI, mood-tinted cloud
  colour.

## Approach (chosen: blend A+B)

Take the **proven HZD/Nubis/clayjohn RECIPE** (noise authoring, density model, lighting,
coverage remap — the parts that make clouds look correct, which our hand-rolled version got
wrong) and build it into **OUR architecture** (local-RD compute bake + render-thread
`CallOnRenderThread` → `Texture2Drd` → sky shader — the plumbing that already works), adding
**temporal reconstruction** for the mid-range budget and our knobs/presets/presence on top.
Bespoke control, grounded look. (Rejected: a pure hand-rolled rebuild — that's the family that
failed 3×; and a from-the-reference verbatim port — loses bespoke control over our tunable
range + presence wiring. The blend keeps the reference's look-correctness and our control.)

## Architecture — units (one job each, swappable, testable)

```
CloudNoise (bake once) ─┐
CloudWeather (large 2D) ┼─► CloudRaymarch (compute, half-res, amortized)
CloudParams/presets ────┘        │  HZD recipe march + in-scatter god rays
                                 ▼
                        Temporal reconstruction (reproject history + blend + disocclusion)
                                 ▼
                        reconstructed cloud buffer (radiance + transmittance)
                          │                         │
                          ▼                         ▼
                   Sky composite            Presence consumers:
                  (sky shader,              matched ground shadow map · overcast dim ·
                   by view dir)             aerial tint · reflections/GI · mood colour
                                 │
                                 ▼  (optional, toggle)
                        Screen-space radial light-shaft boost
```

One direction, no cycles. Mirrors WG16's proven DNA (heavy compute → field → cheap consumers).

| Unit | What | Depends on |
|------|------|-----------|
| **CloudNoise** | Bake-once 3D volumes: a large tileable **Perlin-Worley shape** volume + a separate higher-freq **Worley detail** volume, authored at **mismatched tiling scales** so their combined period is enormous (anti-repetition foundation). Local-RD bake (windowed). | RenderingDevice |
| **CloudWeather** | A large, **low-frequency 2D weather field** (coverage / cloud-type / density per region) at a world scale FAR larger than the shape noise (~50–100 km) so macro cloud placement never repeats in view. Drives the coverage→clear-to-overcast remap. | (CPU or small bake) |
| **CloudParams + presets** | Full knob set (look + perf) as a record + JSON; curated presets (Clear…Stormy) as entry points into the tunable range. | data files |
| **CloudRaymarch** | The HZD-recipe march: ramped step count, cone-sampled light march, Beer + Henyey-Greenstein + powder; multi-scale noise + domain warp + height erosion (anti-repetition); **in-scatter accumulation toward the eye = god rays**. Runs **half-res, amortized** (strided subset/frame). Compute on the main RD via CallOnRenderThread. | CloudNoise, CloudWeather, CloudParams |
| **Temporal reconstruction** | Reproject the previous reconstructed buffer by camera motion, blend the frame's new strided samples, reject stale history on disocclusion (neighborhood clamp). Makes half-res cheap AND stable (solves jitter properly — not the old stride=1 clamp). | CloudRaymarch output + history buffer |
| **Sky composite** | Sky shader samples the reconstructed buffer by view direction, composites over the sky gradient. | reconstructed buffer |
| **Presence** | Matched ground **shadow map** (same density field, sun-march → terrain `light()` already samples world-XZ); **overcast dimming** + **aerial tint** (coverage proxy, no readback); **reflections/GI** (sky radiance cubemap); **mood-tinted** cloud colour. | the field + scene Env/Sun |
| **God rays** | **In-scatter within the cloud march** (crepuscular rays emergent from the real cloud+sun integration — no FogVolume). Optional **screen-space radial light-shaft** boost on a toggle. | CloudRaymarch (in-scatter); a post pass (boost) |

## Anti-repetition — five layered defenses (the #1 requirement)

None alone is enough; together the sky never reads as a grid, near→horizon:
1. **Mismatched tiling periods** — shape (~9 km) and detail (~1.3 km) volumes tile at non-
   integer-related world scales; their LCM is enormous → no catchable repeat. (Old version:
   small, roughly-aligned noises — the root of the gridded look.)
2. **Domain warping** — offset the shape sample position by a low-freq noise lookup; bends
   straight tiling seams into organic shapes. The single biggest "kills procedural look" lever.
3. **Large low-freq weather field** (~50–100 km) — macro cloud placement (cloudy here, clear
   there) never repeats in view → placement stops looking gridded.
4. **Height-varied erosion** — detail erodes edges differently by in-cloud altitude (wispy
   tops, firm bases) → breaks the vertical "stamped" uniformity.
5. **Curl/wind offset over time** — even a static viewpoint shows non-repeating evolution, not
   a sliding tile.

Cost: all five are cheap per-sample texture math (~2–3 extra reads/step), NOT march steps —
so the natural look doesn't blow the budget; the budget goes to temporal reconstruction, which
buys the per-sample cost back.

## Coverage — true clear → full overcast

Coverage drives a proper HZD-style remap where the value raises the **threshold the base noise
must exceed** AND scales the weather field's contribution, tuned so **coverage=0 yields zero
cloud (clear blue sky)** and **coverage=1 yields full overcast**, smooth between. The single
knob works end to end; presets pick points along it plus shape/lighting/altitude per look.

## God rays (destroyed + remade — no FogVolume)

- **Foundation: in-scatter in the cloud march.** We already march the cloud volume toward the
  sun for lighting; accumulate sun in-scatter toward the EYE along the view ray too →
  crepuscular rays emerge where sun streams through cloud gaps, matching the real clouds+sun by
  construction. Cheap (folds into the existing march), physically coherent, no FogVolume, no
  collision with the directional light's volumetric shadows (the old black-wedge cause).
- **Optional: screen-space radial light-shaft boost** (toggle) — a post pass radial-blurring
  sun occlusion from the sun's screen position, for stronger dramatic ground-beams when wanted.
- Both behind toggles. The old `cloud_godray_fog.gdshader` FogVolume + its scene wiring are
  REMOVED.

## Performance posture

Half-res raymarch (¼ the pixels), amortized over N frames; reproject + blend + disocclusion-
reject = stable temporal reconstruction. Target cloud cost ≈ the old ~2 ms even with the richer
per-sample work, because far fewer rays march per frame. **Jitter is solved by reconstruction**,
not by clamping the stride to 1 (the old non-fix). Tuned to a mid-range GPU; the 5090 = headroom.

## Testing / validation

- **Mechanical:** builds; runs WINDOWED (local-RD compute can't run headless); shaders compile;
  coverage=0 → no cloud (clear sky); a static overhead shot shows **no visible tiling**; cloud
  cost within budget (FPS HUD / `--profile`).
- **THE gate — user's eye in motion:** all three looks (photoreal / stylized / sparse)
  reachable by tuning; **repetition gone near → horizon**; true-clear works from the knob;
  presets each read right; ground shadow matches the cloud overhead; god rays read as real
  shafts (not wedges); presence (dimming/aerial/GI/mood colour) coherent. Never judged from a
  still (the motion-artifact lesson).
- **STOP clause:** if a great look can't be reached after a fair effort, surface it — do not
  grind (this system has already had 3 rejected passes; a 4th dead end is a real stop point, not
  a cue to keep twiddling).

## Risk / undo

Full replacement of the cloud render path — but additive in shape (new units + wiring; base
field + lighting untouched) and behind the clouds toggle, so `git checkout .` / toggle-off
reverts to the current sky. Reuses the proven render-thread plumbing (the one part never in
question). Biggest risk = temporal reconstruction correctness (disocclusion ghosting) — mitigated
by the neighborhood-clamp history reject and by judging in motion. Backup the current cloud
system on a tag/branch before ripping it out.

## NOT doing (YAGNI / boundaries)

- NOT keeping the FogVolume god-ray path (removed; replaced by in-march in-scatter).
- NOT a verbatim reference port (lose bespoke control) nor a pure hand-roll (the failed family).
- NOT a cheap analytic/billboard model (can't span to photoreal — fails the range requirement).
- NOT touching the base-field generation or the approved lighting/BRDF (settled).
- NOT building cloud LOD/streaming for an infinite world here (single-region look first).

## Build order (units → plan)

The implementation plan sequences: (1) CloudNoise authoring + verify non-tiling volumes →
(2) CloudWeather large-scale field + coverage remap (prove true-clear→overcast) → (3) CloudRaymarch
core (HZD recipe, full-res first for correctness) → (4) anti-repetition layers (prove no grid) →
(5) temporal reconstruction + half-res (hit the budget) → (6) in-scatter god rays (+ optional
screen-space) → (7) presence rebuild (shadow/dim/aerial/GI/mood) → (8) presets + knobs + judge.
Each eye-gated; clouds-off fallback always present. Companion: the implementation plan
(`docs/superpowers/plans/2026-06-17-cloud-atmosphere-refactor.md`).
