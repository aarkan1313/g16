# 01 · Current-State Audit (grounded baseline)

Honest snapshot of WG16 as of 2026-06-28, written to anchor the rest of this dossier. No
aspiration here — only what is actually in the tree, how mature it is, and where the real
gaps are. Sources: `ROADMAP.md`, `performance.md`, `TECH_STACK.md`, `HANDOFF.md`, the
`scripts/` and `shaders/` tree, and the `data/` registry.

## What WG16 is

A **game-agnostic procedural world generator** in Godot 4.6 mono (C# + GPU compute). Design
spine: no bake stage, all field math live on the GPU; everything data-driven (JSON, hot-
reloadable); everything modular (one unit one job, talk through interfaces); the **user's eye
is the only gate for look**; **8 ms whole-frame budget** (≈125 fps) is the hard ceiling, and
flora + water + caves + roads + POIs must ALL fit inside it.

## Maturity ladder (what's real)

| Subsystem | Status | Evidence |
|---|---|---|
| **Base field** (5-layer heightfield, GPU, no bake) | ✅ SHIPPED, proven, frozen math | `field_height.glsl`, `FieldCompute.cs` |
| **CDLOD infinite terrain** (quadtree + geomorph + stitch + floating-origin + per-chunk field cache) | ✅ SHIPPED, eye-gated | `CdlodTerrain/Quadtree/Mesh.cs`, `ChunkFieldCache.cs` |
| **Sky / atmosphere / clouds / celestial** (Hillaire LUTs, volumetric clouds, N-luminary) | ✅ SHIPPED, look-complete | `AtmosphereCompute.cs`, `CloudVolume.cs`, `cloud_*.glsl` |
| **Material library** (738 → 108 curated PBR) | ✅ curated; ⚠ **no height maps on disk** | `material_library.json`, `MaterialBoard.cs` |
| **Ground surfacing** (zone splat / triplanar / anti-tiling) | 🟡 STRIPPED to height-color placeholder; reset to per-pixel procedural pending | `ground.gdshader`, ground reset spec |
| **Erosion + hydrology** (droplet + thermal + priority-flood + breach-fill, CPU+GPU) | 🟡 CORE PROVEN in lab; not yet in-world parity | `erosion-lab/`, `scripts/water/core/`, drainage-conditioning memory |
| **Water rendering** (rivers/lakes ribbon mesh) | 🟡 WIP uncommitted; 5 prior approaches rejected | `scripts/water/`, WATER-TRASHED handoff |
| **Weather** | 🟠 MOCKUP — coverage/type 2D field only; no wind/precip/climate driver | `CloudWeather.cs`, `weather-lab/` (40 files, un-scaffolded into WG16) |
| **Shadows** | 🟡 CSM code-side done; pure heightfield-march rebuild in progress | shadow-hybrid-rebuild memory |
| **Trees / flora** | 🔴 NOT IMPLEMENTED (a mockup existed, discarded) | — |
| **Caves / underground / digging** | 🔴 NOT IMPLEMENTED | — |
| **Roads / paths** | 🔴 NOT IMPLEMENTED | — |
| **POIs / structures** | 🟠 FRAMEWORK ONLY — the `objectlist` registry type exists (luminaries use it); no terrain POIs | `ItemSchema.cs`, `ObjectListControl.cs` |
| **Biomes / macro variety** | 🔴 NOT IMPLEMENTED (palette/rule system is data-ready for it) | — |
| **Lab / knobs framework** | ✅ SHIPPED, production, decomposed | `lab_controls.json`, `ILabControls.cs`, registry loader |
| **Compute infra** (local-RD + render-thread async + std430 packer) | ✅ SHIPPED, proven | `FieldCompute`, `ChunkFieldCache`, `Std430Writer` |

## The performance reality (measured, RTX 5090 laptop, scale ×2.5–4 for mid-range)

The number that matters: **real forward-flight is ~10.0 ms today, OVER the 8 ms budget**, and
that's BEFORE trees/caves/roads/POIs. The orbit "5.7 ms healthy" was an understatement (it
never left its home region cell). Decomposition of the 10 ms (ARC A-2, `performance.md`):

- **Shadows (CSM) ~2.6 ms** — biggest single feature; atlas/distance dials are measured no-ops
  (caster-geometry bound). Real lever: drop finest LOD from far cascades (architectural).
- **Base floor ~3.7 ms** — mesh raster + ground fragment + streaming. Levers: analytic-gradient
  normal (5×→1.4× field eval), adaptive far-chunk grid.
- **SSAO ~1.8 ms** — currently INVISIBLE on smooth placeholder terrain; **cut = free 1.8 ms**.
- **Clouds ~1.7 ms** — temporal stride; `cloudsteps=32` saves ~1.0 ms.
- Atmosphere AT-1/2/3, aerial, god rays: ~0 each (already efficient — the sky lane's #7 pass
  proved there's no big GPU win left there).

The single biggest win already banked: the **per-chunk field cache** (10.7→7.2 ms flying,
−33%) — one GPU-compute change beat every config dial combined. **This is the template**: the
base floor was vertex-bound by 5×/vertex field eval; baking it once on chunk birth fixed it.

**Headline for this dossier:** the cheap config levers are exhausted. The path under 8 ms with
MORE features on is **architectural** — bake/cache/presolve more, and budget each new module.

## The non-negotiable constraints (every new design must obey)

1. **8 ms whole-frame**, all desired modules ON, medium-high quality. Heavy modules that can't
   fit stay **default-off but budgeted** (see `04-module-knobs-framework.md`).
2. **Game-agnostic, data-driven, modular** — new feature = new unit + a registry line, never a
   reshape of a neighbor. All constants in `*_params.json`, hot-reloadable.
3. **Discipline rule** — build at most ONE phase past the last PASSED eye-gate. Design-ahead is
   fine (this dossier IS design-ahead); build-ahead is the trap that caused the resets.
4. **No teardowns** — iterate, don't rebuild (the WG15 graveyard was teardowns). The CDLOD
   chunk contract and the field-cache seam are the keystones everything plugs into.
5. **Profile the parallel fraction, not the time share.** Never judge a motion artifact from a
   still. Ladder: C# → GPU compute → Rust (only for measured serial bottlenecks).

## The biggest cross-cutting question (deferred to `03`)

Everything the user wants — caves, digging, roads contiguous across infinity, handcrafted POIs,
all features on under 8 ms — collides with **pure infinite streaming**. Some of it wants to be
*presolved* (drainage, road networks, POI placement) which is natural in a **bounded world
generated at spawn** (Factorio model). This is the one decision that gates every subsystem
below, so it gets its own doc: **`03-world-architecture-fork.md`**. The user's steer: prefer
infinite; accept prebake/presolve/cache; bounded-world only as a last resort — but analyze it
honestly as a real fork, because "all features on" may require it for some modules.

## Gaps the user didn't name but this dossier will flag

- **No LOD/impostor system for scattered objects** (needed before flora/POIs are viable).
- **No spatial streaming for non-terrain content** (objects, scatter, road meshes) — CDLOD only
  streams terrain chunks today.
- **No persistence/edit layer** (digging, roads, placed POIs all imply a world-delta store).
- **No global-vs-local solve boundary** defined (what's presolved once vs per-chunk procedural).
- **Weather lab (40 files) is never scaffolded into WG16** — design debt waiting to be wired.
- **Shadow system is mid-rebuild** — flora/caves both need shadows; coordinate, don't fork.
