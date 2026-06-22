# Shadow coordination — SKY lane → TERRAIN/CDLOD chat (2026-06-21)

The sky lane did the **shadow & lighting (#5)** pass this session and fixed everything that's *not* coupled to
the terrain mesh. The remaining shadow items are **yours** (they depend on the mesh you're rebuilding). This note
is so we don't duplicate or stomp each other. Full record: `DECISIONS.md` / `ROADMAP.md` #5 (2026-06-21).

## What the SKY lane already did (committed, code-side — DON'T redo)
All in `scripts/lab/TerrainLabUI.Lighting.cs` `ComposeLighting` (set via `RenderingServer` + Sun props, **no
scene/`project.godot` edits** — deliberately, to avoid conflicting with your files):
- Directional CSM was under-resolved at distance (default 4096 atlas, starved far cascades). Now:
  `RenderingServer.DirectionalShadowAtlasSetSize(8192, true)`, `DirectionalSoftShadowFilterSetQuality(SoftHigh)`,
  Sun `DirectionalShadowBlendSplits=true`, `DirectionalShadowMaxDistance=6000`, splits **0.10/0.28/0.60**.
  → **If your CDLOD draw-distance/scale changes, the split *distances* + max may want a retune — ping us, or just
  tweak those lines; they're view-based, not terrain-structural.** 8192 atlas is a **perf lever to dial down**.
- The big "blocky shadow blob" turned out to be **SSAO @ intensity 2.0** raking the faceted 4 m mesh — not a
  shadow. Dropped to **0.6** (`data/lab_controls.json` `ssao_i` default). It'll smooth further on your higher-res mesh.

## What's YOURS (terrain-coupled — deferred to the CDLOD rebuild)
1. **Diffuse-terminator faceting.** The hard lit/unlit line on hills is the **4 m single mesh** showing its
   triangles at the terminator. Higher-res CDLOD verts fix it. (Not a shadow-map issue.)
2. **Caster-AABB shadow-depth precision.** The single mesh (`TerrainLab.cs`) uses one **full-height** `CustomAabb`,
   which inflates the directional shadow ortho depth range → soft self-shadow **blobs**, worst at low sun. Your
   `CdlodTerrain.cs:45-103` already implements **per-chunk tight AABBs** — the correct fix. **Action: make sure the
   shipping path actually runs CDLOD (per-chunk AABBs), not the single full-height mesh.**
3. **Cloud → terrain shadow receive is currently ABSENT.** The stripped placeholder `ground.gdshader` no longer
   samples `cloud_shadow_tex`, so **clouds don't darken the ground** anymore (the cloud shadow map only feeds the
   god-ray pass today). When your new terrain/material shader lands, **re-add it**: bind `cloud_shadow_tex`
   (= `CloudVolume.ShadowTexture`, region `CloudVolume.RegionSize`), sample **`filter_linear`** (NOT `filter_nearest`
   → that reintroduces a blocky cloud-shadow edge), and attenuate light/albedo by the transmittance `.r`. The map is
   512² over the region (8 m/texel) — fine for soft cloud shadows with linear filtering; we can bump it if needed.
4. **Optional: GI/shadow proxy cheap-shadow lever** (`--giproxy`, `TerrainLab.cs`) — a perf path for coarse cast
   shadows; revisit when you settle the mesh/LOD.

## Heads-up / gotchas
- The directional shadow settings live in **sky-lane code** (`TerrainLabUI.Lighting.cs`), not the scene. If you set
  shadow props on the Sun node in `terrain_lab.tscn`/`review.tscn`, `ComposeLighting` will **override** them each
  recompose. Coordinate there.
- Two Godot instances run on this machine (both chats) — kill-all + verify zero before launching, and use the
  `--` separator before user flags (`… scenes/review.tscn -- --cdlod=1 …`) or they silently drop.
