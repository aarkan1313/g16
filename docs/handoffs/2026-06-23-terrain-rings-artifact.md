# Handoff — terrain "rings" artifact (RESOLVED 2026-06-23)

## RESOLUTION (2026-06-23)

Root cause: the CDLOD chunk vertex branch textured at the **geomorphed** field-sample position
`wxz` (`v_surf_xz = wxz`), but the vertex only morphs in HEIGHT — its real world XZ stays at
`vtrue_fine`. `wxz` is slid toward the coarse grid by `morphK`, which is a function of camera
DISTANCE, so the texture UV was warped by a distance-dependent amount → the surface "swam"/shifted
within each LOD morph band, reading as concentric rings locked to camera distance.

Why every prior fix missed it: aniso / mips / detail-fade / Toksvig / triplanar / constant-mip all
fight texture *frequency* (aliasing). This was a UV-coordinate *distortion*, not aliasing — none of
them touched the cause. The diag ladder was right (modes 2/4 dirty, 1/3 clean = it's the texture UV);
the wrong turn was assuming "texture UV dirty" meant "texture content aliasing."

Fix: one line in `shaders/ground.gdshader` chunk branch — `v_surf_xz = vtrue_fine;` (texture at the
vertex's actual world XZ). Height-morph untouched; single-mesh path already did this (the working
example). Eye-gated PASS by user on the live scene (rings gone near AND far, no shift under motion).

Follow-up (optional, separate): the anti-moiré band (`detail_fade*`, `detail_dissolve`) and Toksvig
machinery were added to compensate for this and may now be redundant — verify with key G OFF, then
consider simplifying.

---

# Original handoff (artifact was unresolved at time of writing)

Date: 2026-06-23
Branch: `experiment/presentation`
Reporter goal: remove a visual artifact the user sees on the CDLOD terrain.

## What the user reports (their words, verbatim where possible)

- "concentric rings or squares of dots that are consistently the same distance apart from the camera"
- The pattern reads "like an overlay or something" — "the detail doesn't change" with the terrain under it.
- "they look the same all the time" / "regardless of material they are one" (looks the same on different ground materials).
- "it's like that texture changes as you move, because of this circle thing" — the surface texture appears to change at a ring as the camera moves.
- "Ring bud. its like that texture changes as you move."
- On approaching closely, the grass texture "comes up" / shows long vertical streaks (see user screenshots from the session).

This is observed live in the running scene. The user's eye is the gate; downscaled auto-shots were not a reliable substitute this session.

## Repro

Launch (windowed, user flags after a bare `--`):
```
"/c/Godot/v4.6.2/Godot_v4.6.2-stable_mono_win64/Godot_v4.6.2-stable_mono_win64.exe" \
  --rendering-driver vulkan --path /c/Wg16/wg-16-project scenes/terrain_lab.tscn -- \
  --cdlod=1 --clouds=0 --cam=0,300,0,-20,0
```
Fly over flat/sloped ground (sand + a grass mound). Artifact visible near→far; appears to track camera distance.

Notes that cost time this session (keep in mind):
- Kill all Godot processes before each relaunch; stale windows caused confusion.
- The player reads the USER shader cache: `app_userdata/"WG16 base field"/shader_cache` (NOT the project `.godot/shader_cache`). Clear it if a shader edit seems to have no effect.
- C# changes need `dotnet build WG16.csproj` before the player reflects them; shaders hot-compile.

## Files involved

- `shaders/ground.gdshader` — terrain surface shader. Textured branch is `use_textures` (default true);
  legacy colour-ramp branch is the `else`. CDLOD chunk vertex branch is `use_chunk > 0.5`.
- `scripts/lab/TerrainLab.cs` — `LoadGroundMaterials()` binds 5 material sets (albedo/normal/roughness) by role
  from `assets/materials/<name>/`.
- `scripts/lab/CdlodTerrain.cs`, `CdlodQuadtree.cs`, `CdlodMesh.cs` — the CDLOD chunk LOD/streaming.
- `scripts/lab/TerrainLabUI.Process.cs` — live debug keys (see below).

## Current repo state

- `shaders/ground.gdshader` is at committed HEAD (`1c60671`). All exploratory edits from this session were
  reverted to baseline so the next person starts clean.
- Uncommitted working-tree changes exist in `scripts/hydrology/RiverTracer.cs` and `WaterTextureBaker.cs` — these
  belong to a SEPARATE concurrent chat (hydrology/water), not to this artifact. Do not revert them without
  coordinating.
- HEAD's `ground.gdshader` already contains, from prior session work (committed):
  - a distance detail-fade (`detail_fade_near/far`, `detail_dissolve`, `tex_mean`) that blends each material
    sample toward its mean colour with distance; live A/B on key **G** (`detail_fade_on`).
  - a tangent-space normal apply (`apply_nrm_tangent`) and a Toksvig roughness term (`toksvig_gloss`,
    `toksvig_rough_floor`, `toksvig_k`).
  - a colored diagnostic stepper `diag_mode` (key **J** in `TerrainLabUI.Process.cs`): 0 normal render,
    1 red = flat base (no textures), 2 green = +albedo, 3 blue = +roughness, 4 yellow = +normal-map.
  - a water debug overlay on key **H** (`water_debug`) — belongs to the hydrology chat.

## Spec / plan written this session (may need revisiting if the cause is different)

- `docs/superpowers/specs/2026-06-23-terrain-surface-antialiasing-design.md`
- `docs/superpowers/plans/2026-06-23-terrain-surface-antialiasing.md`

These were written under the hypothesis that the artifact is texture aliasing (albedo speckle + normal-map).
That hypothesis is NOT confirmed (see below). Treat the spec/plan as one candidate, not ground truth.

## Observations gathered (facts, not conclusions)

Using the colored `diag_mode` stepper (key J) and the G toggle, the user reported, across multiple checks:
- Flat-grey base with NO textures (mode 1): NO artifact.
- +roughness only (mode 3): NO artifact (reported as clean alongside mode 1).
- +albedo only (mode 2): artifact present.
- +normal-map only (mode 4): artifact present.
- Full render (mode 0): artifact present.
- Pressing **G** (toggles the detail-fade, which also scales the normal perturbation and Toksvig): REDUCES the
  artifact, more so far away than up close; "does nothing to close."
- Forcing a single CONSTANT mip level for all texture sampling (a `textureLod` diagnostic, since reverted):
  artifact STILL present.
- Disabling SSAO (`--ssao=0`): artifact still present.
- The artifact's spacing/position is described as camera-relative ("same distance from camera," "changes as you
  move"), repeating ("a consistent distance multiple one").
- Pressing **V** (LOD-band tint) in the last launch: user "didn't see anything" change — LOD-vs-rings alignment
  was NOT conclusively established (the V tint result was unclear; worth re-checking that the V path actually
  tints as intended before drawing any conclusion from it).

Things that were tried as fixes and did NOT resolve it (so the next person doesn't repeat them):
- Anisotropic filtering on the samplers (reduced, did not remove).
- Distance detail-fade toward mean colour (reduced near/mid; user still saw rings, esp. farther).
- Footprint-driven (`fwidth(uv)`) detail-fade (concentrated/“ringed” rather than removed at some tunings).
- In-shader Toksvig roughness + tangent-space normals.
- Triplanar mapping for ALL materials (was: only rock triplanar; others planar world-XZ). Did not resolve;
  artifact still reported.
- Forcing constant mip (ruling mip-LOD selection out, per the user's "still does it").

## What is NOT yet established

- Whether the pattern is screen-locked, camera-distance-locked, or world-locked (never measured directly;
  only inferred from description).
- Whether it correlates with the CDLOD chunk/LOD boundaries or vertex grid (the V-tint check was inconclusive).
- The pattern's actual geometry (spacing in pixels/metres, orientation, square vs diamond vs concentric) was
  never measured from the rendered frame — only eyeballed. Two frames at a small camera offset were captured
  this session (`C:/tmp/wg16shots/m_a.png`, `m_b.png`) but not analyzed before handoff.

## Suggested next step (one option, not a directive)

Gather direct evidence before any further code change: capture frames and measure the pattern (e.g. FFT /
autocorrelation for spacing+orientation; A/B frames at a small camera translation and at a small camera
rotation to classify screen- vs world- vs distance-locked). Let that measurement — not a hypothesis — pick the
next isolation. The `diag_mode` stepper (J), G toggle, V LOD tint, `--ssao=`, `--textures=` flags are all
available as isolation aids; verify each aid actually does what its name says before trusting its result.

## Debug aids available

- Key **J**: cycle `diag_mode` 0–4 (colored channel isolation).
- Key **G**: toggle the detail-fade.
- Key **V**: LOD-band tint (verify it works before relying on it).
- Key **H**: water overlay (hydrology chat's).
- Flags: `--cdlod=1`, `--clouds=0`, `--ssao=0/1`, `--textures=0/1`, `--cam=x,y,z,pitch,yaw`,
  `--auto-shot=C:/tmp/.../x.png`.
