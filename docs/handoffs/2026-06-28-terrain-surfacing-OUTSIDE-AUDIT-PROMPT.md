# Outside Audit Prompt — WG16 Terrain Ground Surfacing

> Paste everything below the line into a fresh agent/chat (or hand to a graphics engineer). It is
> self-contained: it assumes **no** access to the originating conversation. Its job is an *independent*
> audit — verify from source, don't take any claim here on faith.

---

## Mission

You are auditing the **ground-surfacing system** of WG16, a Godot 4.6.2 **Mono** (C# + GDShader),
Forward+ / d3d12 infinite-terrain project. The terrain geometry, LOD (CDLOD), lighting, and sky are
considered done. **Only the ground *surface look* is in question.**

The owner's complaint, after many sessions: the ground reads **flat / thin / "like clay"**, surface
**"definition disappears" as you move/turn**, slopes show **directional "corduroy" streaks**, and there's
a **normal-map moiré** at grazing/distance — *despite the project having ~738 full PBR material sets on
disk*. A separate question that is **already settled, do not re-investigate**: "the terrain looks
different facing toward vs away from the sun" was proven to be *correct directional lighting* (toggling
lighting off removes it), not a bug.

Your task: independently determine **(A)** what the surfacing system *actually does* end-to-end,
**(B)** whether the good assets are even reachable by the renderer, **(C)** the ranked root causes of the
flat/thin/streak/moiré look (asset vs shader vs system), and **(D)** the highest-leverage fixes. Cite
`file:line` for every claim. Verify by reading code, running the lab, **and opening the actual texture
images** — do not trust code comments or this document.

## Environment / how to run

- Repo root: `C:/Wg16/wg-16-project`
- Godot binary: `C:/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe`
- Launch the lab (windowed; headless can't run the compute bakes):
  `<godot.exe> --path C:/Wg16/wg-16-project scenes/terrain_lab.tscn`
- **After ANY C# edit you must `dotnet build WG16.csproj`** — launching the player does not recompile C#
  (shaders *do* hot-compile, which can mislead you).
- Useful live keys in the lab (verify they exist in `scripts/lab/TerrainLabUI.Process.cs`):
  `J` cycle diag channel-isolation (0 normal / 1 base / 2 +albedo / 3 +roughness / 4 +normalmap),
  `U` textures on/off, `I` lighting off (unlit = raw albedo), `F` specular off, `T` footprint-fade,
  `X` procedural-relief proof, `N`/`M` shadow/SSAO.

## Where to start (the system map — confirm or correct it)

- `shaders/ground.gdshader` — `fragment()`: `mat0..mat4` albedo/normal/roughness samplers, the
  height-band + slope→rock blend, triplanar (`tri_sample`), Toksvig roughness floor, and several
  distance/footprint "fade" terms.
- `scripts/lab/TerrainLab.cs` — `LoadGroundMaterials()` and the `_matRoles` array.
- `data/ground_palette.json` — named palettes, each 7 role material names (active = `alpine_green`).
- `data/material_library.json` — registered material names (+ has_normal/rough/ao flags).
- `data/material_verdicts.json` — **the owner's own pass/fail/dropped rating of ~700 materials.**
- `scripts/lab/LabRegistryLoader.cs`, `scripts/lab/TerrainLabUI.Registry.cs`,
  `scripts/lab/TerrainLabUI.Apply.cs` (`ApplyControl`) — the registry/UI wiring.
- `assets/materials/<name>/{albedo,normal,roughness,ao}.png` — the ~738 material directories.

## Questions to answer (independently, with evidence)

1. **Real data flow.** How does a material actually reach the GPU? Trace it. Is the
   palette/library/verdicts/Zone-UI connected to the shader, or is the shader fed some other way? Does
   selecting a material in the UI change what renders? Prove it (find the code path, or prove its absence).
2. **Slots & coverage.** How many ground material slots does the shader have? How many materials exist on
   disk, how many are registered in the library, how many are referenced by palettes, how many actually
   render, and how many are orphaned/unreachable?
3. **Channel usage.** Are albedo, normal, roughness, AND ambient-occlusion all sampled and applied? For
   normals: are *all* material slots' normals applied, or only some, and are they faded out — where/how
   fast? For AO: is `ao.png` used at all?
4. **Asset quality (open the images).** View albedo+normal for a representative sample — both currently-
   rendered materials and promising unused ones. Which are genuine high-detail PBR, which are flat/low-
   contrast placeholders, and which are **directional** (anisotropic) and therefore tile into streaks?
   Does the set the owner *rated "pass"* actually match what renders?
5. **Tiling & blend.** Texture-tiles-per-metre scale (per material or global?); is it sane for foreground
   ground? Is there macro tiling-break? How are materials blended (linear vs height-aware), and are there
   in-shader "band-aids" (detail-fade-to-mean, stochastic anti-tiling, triplanar) — what does each trade
   away?
6. **Root cause, ranked.** Given all the above, why does the ground look flat/thin/streaky/moiré? Attribute
   each symptom to assets vs shader vs system, ranked by contribution, with evidence.
7. **Ranked fixes.** List the AAA-terrain levers that would most improve the look *per unit effort*
   (e.g., wiring channels that exist, swapping in non-directional assets, AO, height-aware blend, macro
   variation, per-material scale, detail/meso relief, texture arrays for scale). For each: present/absent
   (file:line), expected impact, rough effort.
8. **One quick proof.** Identify the single smallest change that best demonstrates the ground *can* look
   good with existing assets, and exactly how to apply it.

## Prior internal hypotheses — CONFIRM OR REFUTE (we may be wrong)

These came from an internal review. Treat them as hypotheses to test independently, not as facts:

- The shader is fed by a **hardcoded 5-material array** (`TerrainLab.cs` `_matRoles`), and the
  palette/library/verdicts/Zone-UI are **not connected** to what renders (the dropdowns may be no-ops —
  check `ApplyControl` for a `material`/`companion` case).
- **3 of the 5 hardcoded materials were rated `fail`** by the owner (`material_verdicts.json`), while
  ~100 `pass`-rated and several genuinely AAA sets (e.g. `biome_grassland`, `rock035`) sit **orphaned**.
- **AO is never applied** (no `matN_ao` sampler) though every dir ships `ao.png`.
- **Only 2 of 5 normal maps are applied** (sand + grass), and relief is faded toward the geometric normal
  with distance/grazing — so rock/snow read perfectly smooth and mid-ground loses relief.
- The dominant valley material that *does* render (`01_tussock_grass`) is a high-contrast **directional**
  stalk photo — the literal "corduroy" source.
- Global tiling is `mat_tiling = 12.0` m with no per-material scale; blending is plain linear.

If you find these correct, say so with independent evidence. If any is wrong, that's the most valuable
thing you can report.

## Deliverable

A structured report: (1) the real data flow; (2) coverage/inventory numbers; (3) channel-usage findings;
(4) asset-quality verdicts with the paths you opened; (5) ranked root causes; (6) ranked fixes with
effort; (7) the one quick proof. Be decisive and cite `file:line` throughout.
