# Shadow view-yaw blocker - START HERE (2026-06-27)

Use this handoff when resuming the WG16 shadow/lighting work in a fresh chat.

## Copy/paste starter prompt

You are in `C:\Wg16\wg-16-project` on branch `experiment/presentation`. Start by reading:

1. `docs/handoffs/2026-06-27-shadow-view-yaw-start-here.md`
2. `docs/SHADOWS_AUDIT_2026_06_27.md`
3. `scripts/lab/LabReviewController.cs`
4. `shaders/ground.gdshader`
5. `scripts/lab/TerrainLabUI.Apply.cs`
6. `scripts/lab/TerrainLabUI.Process.cs`

Current user-reported blocker: with terrain horizon shadows enabled, shadows seem to stop existing or shift when the user yaws the camera left/right slightly. This is not physically correct. A real terrain shadow or terrain self-occlusion mask should be world-anchored: if camera position, sun direction, and time are fixed, rotating view should not make the same visible landform's shadow slide, fade out, or reproject. Treat this as a view-dependent shadow bug until proven otherwise.

Do not reduce terrain/texture/LOD quality to hide the issue. Do not re-enable old CSM/cloud-ground/SSAO/SSIL shadow systems. Preserve the current speed/testing knobs. First isolate the active owner mathematically, then patch the smallest wrong owner.

## Current branch state

- Branch: `experiment/presentation`
- Remote: `origin/experiment/presentation`
- Latest pushed commit at handoff time: `9d29aaf Isolate shadow review toggle`
- Recent shadow commits:
  - `e93d88a Checkpoint terrain culling and shadow diagnostics`
  - `18b6cea Add explicit terrain horizon shadow owner`
  - `04b8b13 Add shadow A/B review key`
  - `b4ec245 Harden shadow review toggle`
  - `9d29aaf Isolate shadow review toggle`
- Working tree was clean after `9d29aaf`.

## What was just fixed

Key `4` in `scenes/review.tscn` is now a shadow review A/B:

- First press after entering review key 4: OFF/isolation. It sets `hz_on=false`, hides the sun, kills fog/god rays/sun glow, disables normal maps/specular, and sets `dbg_unlit=true` so the terrain is self-lit. This is a diagnostic baseline, not a realistic look target.
- Second press: ON/review. It sets `hz_on=true`, sun on, low-sun time, matte/no normal maps, and `dbg_unlit=false`.
- Any non-4 review key clears `dbg_unlit`.
- `ground.gdshader` now actually honors:
  - `dbg_use_normalmap`
  - `dbg_fullrough`
  - `dbg_unlit`

Validated before this handoff:

```powershell
dotnet build WG16.csproj
```

Build passed with the existing warning set.

Shadow diagnostics for key-4 OFF reported:

```text
PROFILE-SHADOWS: clean=YES lights=0/0 geomCasters=0/0 cdlodCasters=0 terrainShaderOwners=0 ssao=False ssil=False sdfgi=False ... owners=none
```

Known Godot shutdown warnings still appear and were already present: invalid texture binding, null uniform-set parameter, RID/font leaks at exit.

## Latest human observation

After the key-4 isolation fix, user said:

- "Ok i think its ok..."
- Then asked whether shadows should shift when moving view left/right.
- After explanation, user confirmed the issue: "Yeah shadows essentially stop existing when you shift view. It seems weird to me but idk."

Interpretation: the user is probably judging key `4` ON, or a similar low-sun terrain frame. They are not describing normal physical parallax. If the camera position is fixed and only yaw changes, a world-space terrain shadow should remain attached to the same terrain features. The next chat should reproduce live and determine whether the visible change is:

- the terrain horizon shadow mask disappearing,
- regular directional slope lighting being mistaken for shadow,
- a screen-space/volumetric artifact,
- time/sun direction changing during review,
- or camera/render-origin/CDLOD coordinate mismatch.

## Current intended shadow architecture

Read `docs/SHADOWS_AUDIT_2026_06_27.md` for the full catalog. Short version:

- Old Godot CSM terrain shadows were stripped/disabled.
- CDLOD chunks should not cast engine shadows.
- Cloud ground shadow receiver was removed.
- SSAO/SSIL/SDFGI are parked out of the default landscape pass.
- The only planned terrain shadow owner is `hz_on` in `shaders/ground.gdshader`.
- `ShadowDiagnostics` should say `terrainShaderOwners=1` only when horizon terrain shadow is explicitly enabled.

The horizon path is intended to be a world-space heightfield/macro-height march:

- `horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to))`
- Samples `field_macro_height(surf_xz + hdir * dist, ...)`
- Does not sample visible CDLOD chunks.
- Should not depend on camera yaw except for the distance fade through `cam_world.xz`, which should be position-only.

If yaw changes the horizon mask while camera position and sun are fixed, something is wrong.

## High-priority suspects

### 1. Time-of-day may still be running during key 4

`LabReviewController.ApplyReview(4)` sets `time_of_day=17f`, but it does not currently force the day/night cycle off with `_setTimeRunning(false)` or `Set("time_running", false)`. If the user previously enabled play day/night, time can keep advancing after the key-4 setup. The horizon shader has this gate:

```glsl
if (sun_up <= 0.02 || sun_up >= hz_sun_gate) { return 1.0; }
```

At time 17 the sun is low enough for `hz_on` to matter. If time continues and the sun elevation crosses `hz_sun_gate`, shadows can fade/disappear. This would be time-driven, not yaw-driven, but the user might notice it while moving the mouse.

First patch to consider if reproduced:

```csharp
_setTimeRunning(false);
Set("time_running", false);
```

inside review key `4`, before or after `Set("time_of_day", 17f)`.

### 2. Key 4 does not clear every previous luminary/light state

Key `4` disables clouds/fog/god rays and the main sun overlay, but does not explicitly reset `extra_suns`, `extra_moons`, moonlight, cloud light, or every exotic/fantasy lighting state. If the user previously pressed other review keys or randomized, leftover non-shadowing lights may change the perceived dark patches.

For review correctness, key `4` should probably reset:

```csharp
Set("extra_suns", 0f);
Set("extra_moons", 0f);
Set("moonlight_energy", 0f); // if this control id exists
Set("cloud_light", false);   // if clouds remain disabled, this is belt and suspenders
```

Verify control ids before patching.

### 3. The real mask may be stable, but Godot `AO` application or lighting perception is view-sensitive

`ground.gdshader` applies the horizon result through:

```glsl
AO = horizon_shadow(...);
AO_LIGHT_AFFECT = 1.0;
```

The mask itself should be world-space, but the visible shaded result also includes BRDF, normals, material albedo, direct light, sky, and Godot's AO integration. Do not guess. Add or temporarily expose a debug overlay that renders the raw horizon scalar in emission, independent of lighting:

```glsl
// Proposed temporary/debug uniform:
uniform bool dbg_hz_mask = false;

// After computing horizon_shadow:
float hz = horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to));
if (dbg_hz_mask) {
    ALBEDO = vec3(0.0);
    EMISSION = vec3(1.0 - hz);
    AO = 1.0;
    AO_LIGHT_AFFECT = 0.0;
}
```

Then yaw the camera at a fixed position. If the emission mask is stable but the normal shaded result disappears, the bug is not the heightfield shadow math; it is lighting/material/perception. If the emission mask itself changes, inspect coordinates and uniforms.

### 4. Camera/render-origin/CDLOD coordinate mismatch

The shader has multiple coordinate frames:

- `render_origin` uniform
- `cam_world`
- `vtrue_fine`
- `wxz`
- `v_surf_xz`

For CDLOD chunks, `v_surf_xz` is intentionally set to `vtrue_fine` for texture stability, while horizon height uses `v_h` from the morphed field sample. That means the horizon test currently mixes:

- surface XZ: actual vertex true-world XZ (`v_surf_xz`)
- surface height: morphed/sample height (`v_h`)

This was correct for the texture ring fix, but it is worth verifying for shadows. A mismatch could produce subtle distance/LOD-dependent drift. It should not normally change on pure yaw, but it is a prime candidate if the shadow mask moves with LOD/render-origin state.

Useful A/B:

- `--cdlod=0` vs `--cdlod=1`
- `--pinorigin`
- `T` terrain debug cycle, especially LOD-viz and single mesh

If `--cdlod=0` is stable and CDLOD is not, inspect `v_surf_xz`/`v_wxz`/`render_origin` use for the shadow path. The horizon shadow may need its own shadow sample coordinate, separate from texture UV coordinate.

### 5. Screen-space/volumetric effects being mistaken for terrain shadow

Key `4` tries to disable fog, volumetric fog, god rays, sun surface, corona, halo, aerial, and atmosphere. Still, verify active state from logs/diagnostics, not just UI checkboxes. Some visible darkening can also be cloud/atmosphere/screen-space perception if the scene was not relaunched after code changes.

Use `PROFILE-SHADOWS` to confirm old owners are off.

## Repro plan

Use live windowed Godot for the actual yaw test. Avoid `--headless`; LocalRD compute cannot run there. Use Vulkan.

Launch:

```powershell
cd C:\Wg16\wg-16-project
. .\tools\godot_vulkan_env.ps1
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn
```

Manual live test:

1. Relaunch fresh after build.
2. Press `4` once: OFF/unlit. Confirm sun blob and dark directional shadowing are gone.
3. Press `4` again: ON/horizon terrain shadow.
4. Do not move WASD. Only yaw camera left/right slightly.
5. Watch a fixed visible landform. The dark mask should stay glued to the landform. If it fades/slides/disappears, bug confirmed.
6. Press `4` again to return OFF and ensure the same area loses all lighting/shadow contribution.

Useful CLI checks:

```powershell
dotnet build WG16.csproj
```

Clean OFF state through review key:

```powershell
. .\tools\godot_vulkan_env.ps1
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --profile=1 --review=4 --quit-after=2
```

Direct horizon-owner check without key-4 toggle:

```powershell
. .\tools\godot_vulkan_env.ps1
& 'C:\Users\josep\Downloads\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe' --path C:\Wg16\wg-16-project --rendering-driver vulkan scenes\review.tscn -- --horizon=1 --time=17 --nofog --profile=1 --quit-after=2
```

Note: `--review=4` applies the first key-4 state, which is OFF. For automated ON screenshots, use `--horizon=1 --time=17` or add a dedicated CLI flag for shadow-review ON.

## Recommended next implementation slice

Keep it small:

1. Freeze review key `4` completely:
   - `_setTimeRunning(false)`
   - `Set("time_running", false)`
   - reset extra suns/moons if those controls exist
2. Add a temporary or Debug-tab raw horizon mask overlay (`dbg_hz_mask`) so yaw can be tested mathematically.
3. Reproduce on single mesh (`--cdlod=0`) and CDLOD (`--cdlod=1`).
4. If raw mask is view-stable, tune/replace the visible application path (`AO`/`AO_LIGHT_AFFECT`) or explain that the user is seeing slope/specular rather than shadow.
5. If raw mask changes with yaw, inspect coordinate inputs and render-origin/camera push order.
6. Only after yaw stability is confirmed, resume quality tuning: softness, strength, distance fade, sample count, and performance.

## Acceptance criteria

- With key `4` ON and camera position fixed, yawing left/right does not make the same terrain-attached shadow disappear, slide, or reproject.
- With key `4` OFF, `PROFILE-SHADOWS clean=YES terrainShaderOwners=0` and the terrain is visibly unlit/self-lit.
- With horizon ON, `PROFILE-SHADOWS` reports no engine shadow draws and exactly one intended terrain shader owner.
- No old shadow systems are re-enabled.
- No terrain texture/height/LOD quality is reduced to hide the issue.

