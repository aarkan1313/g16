# Spec: Sun Surface Shader + Sun Presets (Stage-1 polish, Sun & Light arc)

Date: 2026-06-20. Status: DESIGN (approved in brainstorm; spec for review). Owner: Sun/Light lane.
Parent: `specs/2026-06-20-sun-light-system-architecture.md` (Celestial layer). Stage 1 sun disc is built +
eye-gate PASSED 2026-06-20 with an open polish note ("the disc is basically just a circle"). This spec
closes that note and adds the per-feature preset pattern the user asked for ("basically every feature
will want presets").

## Why

The Stage-1 sun disc (`sun_layers()` in `cloud_sky.gdshader`) renders the disc as a flat radial gradient
(`disc = discMask * limb`) — no surface detail, so it reads as a plain bright circle. The user wants a
shader that gives the disc a real surface/look, **tunable across the full range** (subtle-realistic →
stylized living-star → bold-fantasy) via knobs — matching the system's realistic→stylized→fantasy
principle. Separately, the user wants a **curated sun-preset set** they review and we build, establishing
the per-feature preset pattern (a `data/<feature>_presets.json` + a Light-tab picker, mirroring the
existing cloud presets).

## Scope

IN: a procedural surface inside `sun_layers()` (sun-local UVs + noise → brightness/color modulation),
its new uniforms behind a `sun_surface_on` gate, animated churn; `data/sun_presets.json` + a Light-tab
dropdown + `ApplySunPreset` + `--sunpreset=N` (mirrors cloud presets); a starter preset set (built by us,
eye-gated by the user). OUT: texture/asset-based surfaces (procedural only); multiple suns and colored/
multiple celestial bodies (Stage 4 fantasy); any sky-gradient / directional-light coupling (the sun
preset is disc-only, orthogonal to the Time/Weather/Grade axes); separate flare/prominence geometry (rim
flare, if built, is a single optional knob, not geometry).

## Part A — Surface shader (in `sun_layers()`, `cloud_sky.gdshader`)

The disc is rendered from `cosSun = dot(rd, LIGHT0_DIRECTION)` and a radial `mu` (1 at center → 0 at rim).
The surface adds 2D structure on top, without disturbing the existing limb darkening, horizon
reddening, sun color, or cloud-extinction compositing.

1. **Sun-local UVs (stable, world-anchored).** Build a tangent frame around `LIGHT0_DIRECTION`:
   `T = normalize(cross(S, up))`, `B = cross(S, T)`, with a pole fallback when `S` is near-parallel to
   world-up (swap `up` to e.g. `vec3(1,0,0)`). Project the view ray: `p = vec2(dot(rd, T), dot(rd, B)) /
   sin(radians(sun_size * grow))` → a coordinate inside the unit disc. World-anchored so the surface is
   attached to the sun (rotates only as the sun moves), not swimming with the camera.
2. **Procedural surface.** `surf = fbm(p * sun_surface_cells + TIME * sun_surface_churn)` (3–4 octaves of
   value/simplex noise) for convection granulation; a cellular/Worley term gives cell structure. A
   low-threshold band of the field forms **sunspots** (darkened patches), weighted by `sun_surface_spots`.
   `sun_surface_churn = 0` → fully static (no TIME dependency that frame).
3. **Modulate the disc.** Brightness: `disc *= mix(1.0 - sun_surface_contrast, 1.0 + sun_surface_contrast,
   surf)` (contrast 0 = current flat look). Color: warm-shift bright cells / darken-cool spots by
   `sun_surface_warm`; `sun_surface_color` (if set) overrides the base disc color for fantasy/alien suns.
   All of this multiplies the existing `disc`/`sunCol`/`ext` terms, so limb darkening, reddening, and
   cloud occlusion still apply unchanged.

**New uniforms:**
| Uniform | Meaning | Default (≈ today's look) |
|---|---|---|
| `sun_surface_on` (bool) | gate the whole surface | **false** (flat disc = approved look) |
| `sun_surface_cells` (float) | granule/noise frequency | tuned in review |
| `sun_surface_contrast` (float) | brightness variation (0 flat → bold) | 0 |
| `sun_surface_spots` (float) | sunspot darkening amount | 0 |
| `sun_surface_churn` (float) | animation speed (0 = static) | gentle |
| `sun_surface_warm` (float) | cell-vs-spot color shift | tuned |
| `sun_surface_color` (source_color) | fantasy base-color override (off = use sun color) | neutral/off |
| `sun_limb_flare` (float) — **OPTIONAL/stretch** | rim prominence glints | 0 (off) |

**Perf:** all surface math is gated to pixels inside the disc (where `discMask`/`mu > 0`) — a handful of
pixels — so cost is negligible. Verify with `--profmove` regardless.

## Part B — Sun preset system (mirrors cloud presets)

- `data/sun_presets.json`: `{ "active": "<name>", "presets": { "<name>": { <sun_* + sun_surface_* values>
  } } }`. Disc-only knobs (size, limb, corona, halo, redden, disc energy + the Part-A surface params).
- New partial `scripts/lab/TerrainLabUI.SunPresets.cs`, mirroring `TerrainLabUI.Clouds.cs`'s preset path:
  `LoadSunPresets()` (load + warn-on-miss), a **Light-tab `OptionButton`** dropdown, `ApplySunPreset(idx)`
  that sets each value through the registry (`_byId` + `SetWidgetValue`, so it routes to the shader and
  never silently no-ops). CLI `--sunpreset=N` (or name), applied at startup like `--preset`.
- Orthogonal to Time/Weather/Grade: picking a sun preset changes only the disc; it composes with any
  time-of-day / weather / grade.

## Part C — Starter preset set (built by us, eye-gated live by the user)

~6 presets spanning the range, tuned live and baked once the user approves each:
- **Realistic Midday** — subtle (low contrast, strong limb, faint spots), near the photoreal default.
- **Golden Hour** — warm, low, grown disc/halo, gentle surface.
- **Hazy** — soft/dim, surface muted.
- **Living Star** — the hero stylized churn (visible cells, moderate contrast, slow boil).
- **Blood Sun** — deep red, heavy spots, high contrast.
- **Alien** — `sun_surface_color` override (e.g. green/violet), exotic.

Each is judged in motion via `scenes/review.tscn` (extend key 1 to A/B the sun presets, like key 3 cycles
palettes), one-line verdict recorded in `NEEDS_REVIEW.md` + a `DECISIONS.md` line.

## Gating / discipline

Stage-1 polish — built behind `sun_surface_on` defaulting to the approved flat-disc look; the user
eye-gates the surface look and each preset in motion. Only after approval does `sun_surface_on` default
on and `active` point at the chosen preset. Does not open Stage 3; the GPU atmosphere stays deferred.

## Acceptance

- Toggling `sun_surface_on` gives the disc a believable surface (granulation + optional spots) that the
  user reads as "a sun with a surface," not a flat circle — judged in motion, close on the disc.
- The surface stays attached to the sun (no swimming/shimmer) as the camera flies; `sun_surface_churn`
  animates a gentle boil, `=0` is static.
- The knob range reaches subtle-realistic AND bold-fantasy (incl. a color override) from the same model.
- Surface off reproduces today's approved disc exactly; in-motion cost unchanged within noise (`--profmove`).
- A Light-tab sun-preset dropdown + `--sunpreset=N` apply the starter presets; each is disc-only and
  composes with any time/weather/grade.
- Build clean; no new RenderingDevice errors.

## Risks

1. **UV stability at the pole** — when the sun is near straight-up, the tangent frame degenerates; the
   pole fallback must be tested (fly with a high sun) or the surface will spin/pop.
2. **Swimming/shimmer** — if UVs are derived from the camera rather than world-anchored, the surface
   crawls in motion. Anchor to world space; judge in motion (project rule), never a still.
3. **Over-bright disc washing out the surface** — the disc core is multiplied by 12 and `sun_disc_energy`;
   surface contrast may clip to white. Tune contrast/energy together; the surface should read at default
   exposure, not only when dimmed.
4. **Preset routing no-op** — a preset key naming a missing uniform/control id silently no-ops (project
   gotcha). `ApplySunPreset` must route through the registry and warn on unknown ids; verify each key.
5. **Default drift** — flipping `sun_surface_on`/`active` defaults on after approval must not change the
   pre-approval baseline look for other review items; keep the flip a single, late, deliberate step.
