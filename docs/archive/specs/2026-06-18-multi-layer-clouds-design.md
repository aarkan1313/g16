# Multi-Layer Clouds — Design (N decks at different heights/sizes/opacities)

Date: 2026-06-18 · Status: awaiting user review · Lane: sky/atmosphere (extends the cloud
refactor — `docs/superpowers/specs/2026-06-17-cloud-atmosphere-refactor-design.md`)

## Why

After the cloud refactor (true-clear coverage, anti-repetition, proven shadows, sun disc,
cellularity), the remaining "looks wrong" was the user's: dense cover reads as **one
contiguous mass**, and clouds are all the **same size at one height**. Diagnosed root cause
(user + code): the system models a **single cloud layer** — one altitude, one thickness, one
size — so there is no structural variety to break the uniformity. Real skies get their variety
from **multiple decks at different altitudes with different scales/opacities** (low puffy
cumulus under high wispy cirrus). A clump-scale knob was added as a stopgap; it treats the
symptom. The real fix is structural: **N cloud layers.**

## Mandate (user, this session)

Pillars (quality = performance = AAA-ish = best-long-term, regardless of cost), **tunable**,
**modular**, **separation of concerns**. So: lead with the most capable/correct option, not the
cheapest; make it data-driven and tunable; keep the layer data, the march, and the authoring
as separate units.

## The design

### Layer model (data)

A **cloud layer** is a self-contained deck. Parameters (per layer):

| Param | Meaning |
|-------|---------|
| `altitude`, `thickness` | vertical band the deck occupies |
| `size` | feature scale (big cumulus ↔ fine cirrus) |
| `cell_scale` | clump size (the anti-slab cell gate, per layer) |
| `coverage_weight` | this deck's share of the master coverage (the mix lever) |
| `density`, `opacity` | optical depth / extinction |
| `type` | flat stratus ↔ towering cumulus |
| `edge`, `detail` | shape hardness / high-freq erosion |
| `noise_id` | which baked volume it samples (SHARED shape+detail now; the field exists so a layer can later point at a different volume — per-layer noise type is a future seam, not built now) |

- **Open-ended array, up to ~8 active layers** (covers real skies + exotic/fantasy stacks).
- **Layer 0 == the current flat knobs** (coverage/altitude/size/…): backward-compatible, and a
  single-layer sky reproduces today's look exactly (regression guard).
- A new unit **`CloudLayers.cs`** owns this: parse/validate the layer array from JSON, pack it
  into a GPU storage buffer. ONE job — knows nothing about marching, the sun, or the scene.

### March & compositing

One raymarch pass spans `min(altitude)` … `max(altitude+thickness)` over all ACTIVE layers.
At each step, the point's world Y selects which layer(s) contain it; `sample_density` is
evaluated **per containing layer with that layer's params** and **summed** (overlapping decks
composite naturally; non-overlapping decks contribute only in their band). Lighting
(Beer + Henyey-Greenstein + powder) integrates the combined density, so a thin high cirrus
deck correctly transmits more light than a dense low bank.

- Cost scales with **span × steps, NOT layer count** — and T6 temporal amortization absorbs it.
- **Density-based step acceleration**: large steps through empty air between decks, fine steps
  inside cloud — so a low deck at 1 km + cirrus at 8 km doesn't waste the march on 7 km of gap.
- **`cloud_shadow.glsl` does the identical per-layer sum** along the sun ray → keeps the proven
  shadow coupling (the `--shadowcheck` numeric test must still PASS; per-layer sum mustn't
  decouple it).

### Control flow (coverage + future weather/biome)

- The **master coverage knob scales the whole sky**; each layer's `coverage_weight` sets its
  share, so presets define the **mix** ("heavy low cumulus + thin cirrus"). The single
  sparse↔overcast knob the user liked still works (drives all weights proportionally).
- **Seam for the future weather/biome system**: it writes per-layer weights via a narrow input
  (e.g. `SetLayerWeight(i, w)`) — no march change needed. Presets are just authored layer
  stacks; weather/biome later overrides the weights. (Settled forward direction, not built here.)

## Units / modularity (separation of concerns)

```
CloudLayers (NEW) ── parse/validate JSON layer array → pack GPU buffer
        │
        ▼
cloud_raymarch.glsl / cloud_shadow.glsl ── consume layer buffer, per-step per-layer SUM
        │  (identical sample_density in both — the coupling guarantee)
        ▼
cloud texture + shadow map ── unchanged consumers (sky composite, terrain light(), presence)

CloudParams / presets / lab_controls.json ── AUTHOR the stack (layer 0 = legacy knobs)
CloudVolume.cs ── still owns dispatch/plumbing; gains a layer-buffer upload (one more buffer)
```

One direction, no cycles. Each unit answerable: *what it does / how used / what it depends on.*

## Performance posture

Per pillars, quality isn't traded for cost — but the design is already cheap by construction:
single pass, cost by span not layer count, step-acceleration over gaps, temporal amortization
(T6) on top. Profiled with the FPS HUD / `--profile`; tuned to the mid-range budget from the
refactor spec.

## Testing / validation

- **Mechanical:** builds; shaders compile; **`--shadowcheck` still PASS** (per-layer sum keeps
  shadow↔cloud coupling); **single-layer config == current look** (regression guard); cost
  within budget (HUD/`--profile`); `--shadowcheck` SATURATED handling unchanged.
- **Gate (user's eye, in motion):** distinct decks visible at different heights/sizes; dense
  cover no longer one mass (clumps + multiple decks); the mix is tunable; presets read right.
  Never judged from a still.
- **STOP clause:** if multi-layer can't be made to look right after a fair effort, surface it —
  don't grind.

## Risk / undo

Additive: a new `CloudLayers` unit + a layer buffer + a per-layer loop in two shaders; layer 0
preserves the current look so it's a strict superset; behind the clouds toggle; `git checkout`
/ the `backup-clouds-pre-refactor-2026-06-17` tag revert. Main footgun = the per-layer
`sample_density` must stay identical between raymarch and shadow (the coupling guarantee) — the
plan calls it out. Step-acceleration must not skip thin decks (validate against `--shadowcheck`).

## NOT doing (YAGNI / boundaries)

- NOT per-layer distinct noise volumes yet (seam only; shares shape/detail now).
- NOT the weather/biome system (only the write-seam for it).
- NOT separate render passes/textures per layer (single pass composites — cheaper, simpler).
- NOT a hard cap below 8 unless profiling forces it (open-ended array).
- NOT touching base field / lighting / the proven render-thread plumbing.

## Build order (→ plan)

(1) `CloudLayers` data unit + JSON schema + layer 0 = legacy knobs (regression: single layer ==
today) → (2) layer GPU buffer upload in CloudVolume → (3) raymarch per-step per-layer sum +
step-acceleration → (4) shadow shader identical per-layer sum (re-run `--shadowcheck`) →
(5) authoring: a multi-layer preset or two + registry hooks for layer params. Each eye-gated.
Companion: the implementation plan (`docs/superpowers/plans/2026-06-18-multi-layer-clouds.md`).
