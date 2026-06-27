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
| Heightfield horizon shadows | `shaders/ground.gdshader`, `data/lab_controls.json`, `scripts/lab/TerrainLabUI.Process.cs` | `hz_on=true` despite the original plan requiring default-off until eye/perf gates. | Default returned to `false`; controls and `P` hotkey removed. |
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

## Current Validation

- Edited JSON files parse (`lab_controls`, `lighting_moods`, `luminaries`, `item_schemas`, `cloud_presets`, `cloud_layers`, `cloud_params`).
- `dotnet build WG16.csproj` succeeds with existing warnings.
- Active-code scan found no old terrain/cloud/horizon shadow APIs or controls and no `ShadowEnabled=true`, `shadow_enabled=true`, `CastShadow.On`, `SSAO`, `SSIL`, or `SDFGI` enable path.
- Runtime FPS/profile data from this already-running Codex shell is not authoritative right now: the stale HKCU `VK_INSTANCE_LAYERS=VK_LAYER_NV_nomad` / `VK_LAYER_PATH` values were cleared after diagnosis, but this process still inherits the old values unless cleared per child. Even with those cleared, `vulkaninfo --summary` still fails to detect a valid GPU/ICD in this session while Windows sees the Intel and RTX adapters, so Godot can fall back to Microsoft Basic Render Driver here.
