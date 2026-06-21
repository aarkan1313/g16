# Spec: Celestial C2 — Meteors / Shooting Stars (subtle & occasional)

Date: 2026-06-21. Status: DESIGN (approved "sure"). Owner: Sun/Light lane. Parent: ROADMAP #6 Celestial
expansion ▸ C2 (first piece). Follows C1 (closed — night sky = tuned moon + starfield; galaxy/nebula shelved).

## Why

The night sky is now a clean **moon + starfield**. Occasional shooting stars add quiet life — a faint streak
every so often that's easy to miss and feels special when caught. Small, self-contained, cheap; the natural
first C2 body. **Feel (user): subtle & occasional** — realistic, rare, faint (NOT a meteor shower).

## Constraints (hard)

- **Cheap.** Negligible per-frame cost; ~zero when no streak is active (early-outs bail before the streak math).
  No CPU work, no per-frame uniform churn, no new node.
- **Fits the existing night system.** Lives in `cloud_sky.gdshader` alongside `star_field`, gated by the same
  `night_factor × above-horizon` fade — meteors only at night, never below the horizon, fade in/out at dusk.
- **Tunable + a master toggle**, defaults dialled subtle. Look gates on the user's live eye (review key 2).
- **Stay in sky/light files.** `cloud_sky.gdshader`, `CloudVolume` (sky-material setters), `TerrainLabUI.*`
  (Apply/Lighting + Night controls), `data/lab_controls.json`. No terrain/ground/godray edits.

## Scope

**In:** procedural in-shader shooting stars — a small number of independent "channels" that each occasionally
spawn a single streak that sweeps the sky over ~1 s and fades, then nothing for a long interval. White with a
faint cool tint, a thin head-bright/tail-fading trail. Night-tab tunables + toggle.

**Out (later C2 pieces / not now):** planets · persistent trails/smoke · meteor *showers* / radiant-point
clustering · colored fireballs with fragmentation · impact flashes · meteors casting light on the ground.
(The tunables CAN be pushed toward "more frequent/brighter," but shower mechanics are a separate piece.)

## Architecture (Approach A — procedural in-shader)

- **`cloud_sky.gdshader` — `meteors(vec3 rd)`**, called from `stars_layer` and added into the night composite
  (`return (star + meteors(...) + mw) * fade;`). Returns an additive `vec3` (mostly `vec3(0)`).
- **Channel model:** `METEOR_CHANNELS` (const 2) independent channels, each a `meteor_channel(rd, seed)`:
  - `cyc = floor((TIME + seed) / period)`; `local = fract(...) * period` (seconds into the cycle);
    `period` ≈ 22 s / `meteor_rate` (lower rate → longer gaps).
  - **Early-out 1:** `if (local > WINDOW) return 0;` (WINDOW ≈ 1.1 s active per cycle).
  - **Early-out 2:** `fire = hash13(vec3(cyc, seed, k)); if (fire > meteor_rate) return 0;` (most cycles don't fire
    → "occasional").
  - Hash `cyc` → start dir `A` (upper sky, `y` biased positive), a random travel tangent `v` (⊥ `A`), arc length
    (~20–45°), trail length (~2–6°), brightness (~0.4–1.2).
  - `p = local / WINDOW` (0→1); head `H` sweeps from `A` along `v`. Local frame at `H` (`hv` = travel tangent,
    `hw = cross(H,hv)`); for view `rd`: along-track `xa = dot(rd-H, hv)`, cross-track `ya = dot(rd-H, hw)`.
  - Streak = `smoothstep(thick,0,|ya|)` (thin) × trail window in `xa∈[-trail,0]` (head-bright, tail-fading) ×
    `sin(p·π)` (fade in/out over the window) × brightness. Tint = white with a faint cool bias.
- **`CloudVolume`** (sole `_skyMat` writer): `SetMeteorsOn(bool)` + `SetMeteors(rate, brightness, length, speed)`
  → shader uniforms `meteors_on`, `meteor_rate`, `meteor_brightness`, `meteor_length`, `meteor_speed`.
- **`TerrainLabUI`** (`.Lighting` push from `ComposeLighting` + `.Apply` cases) + a `MeteorState` (or fold into
  the night state) + `data/lab_controls.json` Night-tab rows.
- **Data flow:** static — uniforms set on change (mood/preset/knob); the animation is pure `TIME` in-shader. No
  per-frame CPU. `meteors_on=false` (or `meteor_rate≈0`) → the function early-outs to zero (free, like the galaxy).

## Tunables (Night tab)

`meteors on` (toggle, default ON) · `meteor rate` (0..1, "how often a cycle fires" — the occasional dial,
default ~0.15) · `meteor brightness` (default ~0.8) · `meteor length` (trail length, default ~mid) ·
`meteor speed` (sweep speed / window, default ~mid). All gate-tunable; defaults subtle.

## Performance

- Per frame: 2 channels, each ~2 hashes then an early-out almost always → effectively free. Only during a
  ~1.1 s streak does one channel run the (cheap, branchless-ish) streak math per sky pixel. No loops over many
  meteors, no CPU, no readback. Verify `--profmove --profile=3` night unchanged vs pre-C2.

## Risks

1. **Streak geometry approximation** (head sweep via a tangent frame, local planar streak) — fine because a
   meteor is small (a few °) and fast (~1 s); any distortion is invisible. *Mitigation:* the look gate.
2. **Look is ungated until live** (it's an aesthetic) — perf/correctness are mechanical; "does it read as a
   shooting star" is the user's eye. *Mitigation:* a debug seed that forces a channel to fire on demand so a
   windowed capture lands mid-streak for build-time sanity; the real gate is review key 2.
3. **Too frequent/regular** (channels could feel periodic) — *Mitigation:* per-cycle hashed jitter on the spawn
   time within the window + low default rate; 2 desynced channels (different seeds) break obvious periodicity.

## Acceptance

- Occasional faint streaks cross the night sky, thin with a head-bright fading tail, white/cool, gated to
  night + above horizon, fading in/out cleanly; never below the horizon, never in daytime.
- Tunable (rate/brightness/length/speed) + master toggle on the Night tab; defaults subtle.
- Negligible perf (`--profmove --profile=3` night unchanged); free when off / between streaks.
- Look PASSES the user's live eye-gate (review key 2). On PASS → keep default-on + record.
