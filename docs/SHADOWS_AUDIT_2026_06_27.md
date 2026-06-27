# Shadows Audit and Rebuild Plan - 2026-06-27

## Checkpoint

- Tracked checkpoint before this audit: `8177e6f Checkpoint project before shadow audit`.
- Remote tag pushed before edits: `backup/pre-shadow-audit-20260626-234354`.
- Local bundle kept: `C:\Wg16\backups\wg16-pre-shadow-audit-20260626_235234.bundle`.
- Old Wg16 backup folders/copies and generated `artifacts/` were removed to recover C: drive space.

## Catalog

| System | Files | Pre-audit state | Action |
| --- | --- | --- | --- |
| Engine sun CSM | `scenes/review.tscn`, `scenes/terrain_lab.tscn`, `scripts/lab/LightingComposer.cs`, `scripts/lab/TerrainLabUI.*` | Scene default was off, but UI/CLI/hotkeys could re-enable it. Composer still tuned atlas/splits. | Runtime now forces `Sun.ShadowEnabled=false`; UI/CLI/hotkeys removed. |
| CDLOD terrain shadow casters | `scripts/lab/CdlodTerrain.cs`, `scripts/lab/TerrainLab.cs` | Camera-centered caster ring decided per chunk, causing separate shadow LOD/caster state from visible terrain LOD. | `TerrainShadowsEnabled=false`; terrain meshes and GI proxy never cast. |
| Heightfield horizon shadows | `shaders/ground.gdshader`, `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Apply.cs`, `scripts/lab/TerrainLabUI.Cli.cs` | `hz_on=true` despite the original plan requiring default-off until eye/perf gates. | Reintroduced as the only approved terrain shadow owner: default-off, explicit Light-tab controls, `--horizon=0|1`, sun vector synced from the existing composer/sliders, no hotkey. |
| Cloud ground shadows | `scripts/lab/CloudVolume.cs`, `scripts/lab/TerrainLabUI.cs`, `shaders/ground.gdshader`, `shaders/cloud_shadow*.glsl` | Compute map existed, terrain receive/debug could be armed, and controls exposed ground shadow strength. | Terrain receiver/debug, compute map, and numeric `--shadowcheck` path removed. |
| Cloud self-lighting/shadowing | `shaders/cloud_raymarch.glsl`, `scripts/lab/CloudVolume.cs` | Part of cloud volume shape and contouring, not the bad terrain shadow system. | Preserved. |
| God-ray occlusion | `scripts/lab/GodRaysScreen.cs`, `shaders/godray_screen.gdshader` | Default screen-luminance mode; dormant cloud-shadow mode existed but was not active. | Preserved screen-luminance path; deleted shadow-map input/fallback. |
| SSAO/SSIL/SDFGI/GI proxy | `scenes/*.tscn`, `scripts/lab/LightingComposer.cs`, `scripts/lab/TerrainLabUI.*` | Scene defaults off, but UI/CLI/hotkeys could re-enable SSIL/SDFGI and proxy shadow behavior. | Composer keeps occlusion parked; UI/CLI/hotkeys/proxy shadow casting removed. |
| Moon/inspection/material board lights | `scripts/lab/LightingComposer.cs`, `scripts/lab/TerrainLabUI.Process.cs`, `scenes/material_board.tscn` | Moonlight and inspect light could cast; material board light had shadows on. | All are now non-shadowing. |
| Review scene key 4 | `scripts/lab/LabReviewController.cs` | Stale "Shadow tuning" preset still referenced removed knobs. | Replaced with a shadowless lighting baseline. |

## Research Summary

- Godot directional shadows are split-based CSM/PSSM. Godot documents that increasing `directional_shadow_max_distance` makes shadows visible farther away but lowers overall detail and performance because more objects enter the directional shadow rendering path; split distances are relative to that max distance. Source: https://docs.godotengine.org/en/stable/classes/class_directionallight3d.html
- Unreal's Virtual Shadow Maps target film-quality assets and large dynamic open worlds with a unified high-resolution path. Directional lights use camera-centered clipmaps, allocate 128x128 pages only where needed by visible pixels, and cache pages between frames. Source: https://dev.epicgames.com/documentation/en-us/unreal-engine/virtual-shadow-maps-in-unreal-engine
- Unreal's Distance Field Shadows show the large-world hybrid pattern: CSM covers sharp near shadows, and distance fields take over farther out, doing work for visible pixels and avoiding common distant shadow-map bias/leak issues. Source: https://dev.epicgames.com/documentation/en-us/unreal-engine/distance-field-soft-shadows-in-unreal-engine
- NVIDIA GPU Gems' Parallel-Split Shadow Maps chapter describes the classic split-frustum approach: split the camera frustum into layers and render an independent shadow map per layer. It also calls out the cost of extra rendering passes. Source: https://developer.nvidia.com/gpugems/gpugems3/part-ii-light-and-shadows/chapter-10-parallel-split-shadow-maps-programmable-gpus

## Pillars-Aligned Direction

The old system failed because it had several independent shadow owners: Godot CSM, CDLOD caster rings, horizon march, terrain cloud receive, and screen-space/GI toggles. A AAA-quality open-world setup should have one explicit owner per distance band.

Recommended rebuild:

1. Keep the current shadowless terrain baseline until texture/height/LOD stability is accepted again.
2. Add a single shadow registry/state object before adding new effects. It should expose active owners, cost counters, and a hard invariant: only one ground-shadow owner can affect terrain at a time.
3. Rebuild near contact shadows first, only for close hero objects or future dense local geometry. Keep terrain chunks out of engine CSM until terrain surfacing/mesh density is high enough. If Godot CSM is used, cap it to a near-only band and never use it for far CDLOD terrain.
4. For terrain self-shadowing, build a world-anchored heightfield/clipmap shadow cache rather than relying on visible CDLOD meshes as shadow casters. It should behave like terrain LOD: same answer far away, more detail near the player, no lit/unlit ownership pop.
5. For clouds, leave terrain receive deleted until a separate cloud-shadow spec exists. If it returns, it should be a world-anchored transmittance cache with an explicit visual/perf gate, not hidden coupling to the sky raymarch.
6. Keep SSAO/SSIL/SDFGI out of the default landscape pass. Reintroduce only as small-radius contact/cavity support after terrain material detail exists.

## Rebuild Slice 1 - Terrain Horizon Shadow

- `shaders/ground.gdshader` owns one optional terrain self-shadow path: a world-space macro-height march toward the sun, applied through `AO` + `AO_LIGHT_AFFECT` so it attenuates direct light without enabling engine shadow maps.
- The path samples `field_macro_height`, not visible CDLOD chunks, so terrain shadowing is independent of chunk load, caster rings, and visible LOD. The expected behavior is same large-scale answer at distance, not lit/unlit popping as chunks load.
- `hz_on` remains default `false`. The Light tab exposes the tuning knobs, and CLI verification uses `--horizon=1`.
- The default active setting is deliberately cheap: one broad geometric sample, full strength to 1 km, smooth fade to 4 km. The sample count still tunes from 1 to 32 for quality sweeps, but higher counts are explicit review/perf choices.
- `ShadowDiagnostics` now distinguishes the shader owner from engine shadow work: horizon enabled reports `terrainShaderOwners=1` and `owners=terrain:horizon:/root/TerrainLabRoot/TerrainLab` while shadow draw calls remain zero.

## Current Validation

- Edited JSON files parse (`lab_controls`, `lighting_moods`, `luminaries`, `item_schemas`, `cloud_presets`, `cloud_layers`, `cloud_params`).
- `dotnet build WG16.csproj` succeeds with existing warnings.
- Active-code scan found no cloud-ground shadow receiver and no `ShadowEnabled=true`, `shadow_enabled=true`, `CastShadow.On`, `SSAO`, `SSIL`, or `SDFGI` enable path. The only terrain shadow hits are the intended `hz_on` shader/control/CLI/diagnostic paths.
- `ShadowDiagnostics` now reports actual active shadow lights, geometry casters, CDLOD shadow casters, SSAO/SSIL/SDFGI flags, and Godot shadow render counters through `PROFILE-SHADOWS` and `LIVEPROFILE-SHADOWS`.
- Runtime profile, stationary default: `avg 138 fps (7.2 ms)`, `worst 104 fps (9.6 ms)`, shadow draws/objects/primitives all `0`, `PROFILE-SHADOWS clean=YES terrainShaderOwners=0`.
- Runtime profile, low sun shadowless `--time=17`: `avg 136 fps (7.4 ms)`, `worst 108 fps (9.3 ms)`, shadow draws/objects/primitives all `0`, `PROFILE-SHADOWS clean=YES`.
- Runtime profile, active low-sun horizon `--horizon=1 --time=17`: `avg 120 fps (8.3 ms)`, `worst 98 fps (10.2 ms)`, shadow draws/objects/primitives all `0`, `PROFILE-SHADOWS clean=NO terrainShaderOwners=1 owners=terrain:horizon:/root/TerrainLabRoot/TerrainLab`.
- Runtime profile, 5000 m/s default: `avg 142 fps (7.0 ms)`, `worst 103 fps (9.7 ms)`, shadow draws/objects/primitives all `0`, `PROFILE-SHADOWS clean=YES`.
- Runtime profile, 5000 m/s active low-sun horizon `--horizon=1 --time=17`: `avg 128 fps (7.8 ms)`, `worst 100 fps (10.0 ms)`, shadow draws/objects/primitives all `0`, `PROFILE-SHADOWS clean=NO terrainShaderOwners=1`.
- Numeric terrain gates pass: `--fieldcheck` reports `maxAbsDiff=0m`; `--popcheck` reports `height worst-at-swap Δh=0.000m` and `normal worst-at-swap Δn=0.0°`.
- Known unrelated shutdown warnings still print in Godot runs: invalid texture binding, null uniform-set parameter, and RID/font leak messages at exit.
