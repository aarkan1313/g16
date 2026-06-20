# Spec: God-Ray Cloud Occlusion from the Cloud Shadow Map (mood-independent)

Date: 2026-06-19. Status: APPROVED (design). Owner: TBD (new chat).
Companion: memory `godray-emission-vs-albedo-rootcause`; `godray_screen.gdshader`; `GodRaysScreen.cs`.

## Why this exists

The screen-space god-ray shader (`shaders/godray_screen.gdshader`, `scripts/lab/GodRaysScreen.cs`)
detects **cloud occlusion** with a LUMINANCE HEURISTIC: a sky pixel darker than `cloud_lum` (0.78) and
within `cloud_radius` of the sun is treated as cloud. This works for a hand-tuned shot but is
**mood/weather-dependent**: the threshold must be re-tuned when the sky brightness changes (Golden Hour
vs Blue Dawn vs Stormy), and it can produce phantom shafts in dark-but-clear sky or miss shafts where
lit cloud edges occlude. The user does not want to keep tuning per game/weather/cloud setup.

**Goal:** drive god-ray cloud occlusion from the ACTUAL cloud field so it's mood/weather/brightness
independent — set it once, works everywhere.

## The key unlock (discovered during design)

The cloud system **already bakes exactly the right data**: `cloud_shadow_tex` — a 512×512 top-down map,
R channel = **sun transmittance at world column (x,z)** (1 = sun reaches ground, 0 = under cloud),
baked EVERY FRAME from the same 3D cloud density field that draws the visible clouds (`shaders/cloud_shadow.glsl`).
It is **mood/brightness-independent** (pure density, not color). The terrain already samples it with a
proven `Texture2Drd` binding. So the refinement is NOT "plumb the cloud field into a screen-space pass"
— it's "sample the shadow map the project already has," which is cheap and reuses proven plumbing.

Verbatim facts (from the investigation):
- `CloudVolume.ShadowTexture : Texture2Drd?` (CloudVolume.cs:532) — the map. `CloudVolume.RegionSize : float` (:533).
- `CloudVolume.ComputeReady : bool` (:474) — RID is live; do NOT sample before this (terrain gates on it).
- World-XZ → UV: `uv = world.xz / region + 0.5` (terrain_lab.gdshader:666; bake: cloud_shadow.glsl:132-136).
- Value: `vis ∈ [0,1]`, `1` = sun reaches ground (open), `0` = fully cloud-occluded (cloud_shadow.glsl:150-173).
- Binding a Texture2Drd to a material sampler works (terrain does it; TerrainLabUI.cs:89). Gate sampling on `ComputeReady`.

## The technical wrinkle (the real work)

The shadow map is **2D top-down (ground-XZ)**. A screen-space pass needs the world-XZ for each pixel:
- **Terrain pixels** (depth < far): reconstruct world position from depth (`INV_PROJECTION_MATRIX` /
  `INV_VIEW_MATRIX`, Reverse-Z aware), take `.xz`.
- **Sky pixels** (depth ≈ far/0.0 under Reverse-Z): no geometry. Project the pixel's VIEW RAY up to the
  cloud altitude and use that XZ — i.e. "where does this view ray pierce the cloud layer." This is the
  XZ whose shadow-map transmittance says whether the sun is blocked along that direction.

The occlusion buffer becomes: `occluder if (transmittance at the pixel's cloud-XZ is low)`. Crucially —
the god-ray radial blur samples the occlusion buffer along the ray toward the sun, so each sample's
transmittance is read at ITS pixel's cloud-XZ. Where clouds block the sun (low transmittance) → occluder
→ the gaps (high transmittance) become the bright shafts. Same physical meaning as the luminance
heuristic, but from real density.

NOTE the projection subtlety (carried over from the froxel work, memory `godray-emission-vs-albedo-rootcause`):
the shadow map at (x,z) is the transmittance for the GROUND point (x,z) along the slant to the sun. For a
sky pixel we want "is the sun blocked along this VIEW direction." The cleanest correct sample: march the
view ray to the cloud-deck altitude, take that XZ, sample there. This is an approximation (the shadow map
is ground-referenced, not per-altitude) but it's the same map the terrain uses and is consistent with the
ground shadows — good enough and mood-independent, which is the goal. If it proves visibly off, the
fallback is to also unshift by the sun angle (see the froxel note's `g.xz = world.xz - toSun.xz*(y-ground_y)/toSun.y`).

## Scope

IN: replace the luminance occlusion path with a cloud-shadow-map sample (keep luminance as a fallback
`occ_mode` for the test scene / debugging). Bind the shadow texture + region to `GodRaysScreen`, gated on
`ComputeReady`. Reconstruct world-XZ from depth (terrain) / view-ray-to-cloud-plane (sky). Keep all
existing tunables; the cloud-detection sliders (`cloud_lum`/`cloud_radius`) stay meaningful only for the
luminance fallback. Add a `cloud_occlusion_on` toggle (default true in the real scene).

OUT: changing the radial-scatter math, the sun-projection/gate, the froxel layer (deleted), or the cloud
system itself. No new render passes (reuse the existing shadow map). No CompositorEffect.

## Acceptance

- God rays look correct (shafts through cloud gaps) across ≥3 moods WITHOUT re-tuning any slider — the
  whole point. Verify by capturing the same preset under Golden Hour / Blue Dawn / Stormy.
- The luminance fallback still works (test scene `occ_mode=0`/`1`).
- `--shadowcheck` PASS (this must not touch the cloud density field).
- Build clean; default behavior unchanged when god rays are off.
- Eye-gate: user confirms it no longer needs per-mood tuning.

## Risks

1. Depth→world reconstruction under Reverse-Z (far = 0.0 in 4.6) — get the matrix math right; test with the
   occlusion-mask debug view (`debug_mode=1`) which should show cloud shapes matching the sky.
2. Sky-pixel cloud-plane projection at low sun (long ray) can sample off the finite 512 map → treat
   off-map as open (transmittance 1.0), same as the luminance path does.
3. `ComputeReady` gating — sampling an empty Texture2Drd on frame 1 errors (terrain gates on this; mirror it).
4. The shadow map is ground-XZ, not per-altitude — the view-ray-to-cloud-plane sample is an approximation;
   acceptable (it matches the ground shadows), with the sun-unshift fallback documented if needed.
