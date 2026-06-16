# WG16 — Base Field

A procedural terrain generator, rebuilt clean from WG15's proven base field with
**no bake stage**. Godot 4.6 mono, C# + GPU compute. One region, generated live.

This is the foundation the WG15 work confirmed good (the 5-layer field that read
well across 8 seeds), stripped of the erosion/water/channel/skeleton stack that
churned and kept getting judged bad. We build out from here **slowly**, one
judged piece at a time.

## Run it

```
Godot_v4.6.2-stable_mono_win64.exe --path . --rendering-driver vulkan scenes/lab.tscn
```

Run **one Godot process at a time** — two contend for the GPU and you get a
grey-screen hang (a WG15 lesson). Build C# first if scripts don't register:
`dotnet build WG16.csproj`.

### Controls
| Key | Action |
|-----|--------|
| RMB/LMB + drag | look |
| WASD / Q E | fly / down-up |
| Shift | boost |
| wheel | fly speed |
| 0–5 | view layer: 0 full · 1 continent · 2 uplift · 3 hills · 4 ridges · 5 macro base |
| R | reseed |
| G | walk mode (eye-level on the ground) |
| `[` `]` | sun elevation |
| F12 | screenshot → `D:\tmp\wg16_shots\` |

`data/field_params.json` and `data/presentation_params.json` hot-reload while
running — edit, save, watch it rebuild.

## Layout

```
scripts/field/      heightfield generation (GPU compute) — the "what the world is"
scripts/presenter/  draw the heightfield as a displaced plane
scripts/workbench/  wire input, hot-reload, HUD, camera
shaders/            field_height.glsl (compute) + lab_terrain.gdshader (material)
data/               hot-reloadable JSON knobs
scenes/lab.tscn     the lab
docs/               this, TECH_STACK.md, DECISIONS.md, the design spec
```

## Docs

- **[HANDOFF.md](docs/HANDOFF.md)** — START HERE in a new session. What the project
  is, how to run, current state, and what to do next. Written to pick up cold.
- **[TECH_STACK.md](docs/TECH_STACK.md)** — what we use, the modularity rules,
  the C#/Rust/GPU-compute policy. Read this to understand the shape.
- **[DECISIONS.md](docs/DECISIONS.md)** — running log of choices made, so we
  don't re-litigate. One entry per decision.
- **[2026-06-15-wg16-base-field-design.md](docs/2026-06-15-wg16-base-field-design.md)**
  — the design that started this project.

Scenes: `lab.tscn` (base-field lab) · `terrain_lab.tscn` (the look lab, active) ·
`material_board.tscn` (material judging loop) · `lab_experiment.tscn` (sandbox).

## Posture (the "low plans" rule)

No plans → thrash. Lots of plans → process overhead outran results (the WG15
failure mode). **Low plans:** a short spec when a feature is genuinely new, a
one-line DECISIONS entry otherwise, and the user's eye as the gate. Spike a look
cheap and judge it early — never build a full system to first-judgment.
# g16
