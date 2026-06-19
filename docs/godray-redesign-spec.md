# WG16 God Rays — Redesign Spec

Date: 2026-06-18. Status: APPROVED (design), Component A in implementation.
Companion docs: `cloud-system-overview.md` (cloud architecture), `cloud-next-steps.md` (review).
Checkpoint before this work: git tag `backup-clouds-pre-godrays-2026-06-18` (pushed) +
`c:\tmp\wg16-backup-pre-godrays-2026-06-18.zip`.

## Why this exists

There were **two different "god ray" features** in the lab, in two tabs, doing two unrelated things:

| | Light tab — "godray power" (`godray_e`) | Clouds tab — "god rays (in-scatter)" (`cloud_godrays`) |
|---|---|---|
| Drove | `sun.LightVolumetricFogEnergy` | hand-coded in-scatter inside `cloud_raymarch.glsl` |
| Technique | Godot built-in volumetric fog (uniform) | in-march, inside the sky dome only |
| Cloud-aware? | No (source of the old black-wedge bug) | Yes, but only in the dome, narrowly gated to `pow(cosA,3)` |
| Where shafts show | the real 3D air near terrain | only the sky-dome texture, between decks |

Neither ever looked good. The in-cloud version can't rake a shaft across the terrain/valley (it
lives in the dome); the engine-fog version isn't cloud-shaped. Decision (user, 2026-06-18): build a
**hybrid** that is **best-long-term / AAA**, behind toggles, default OFF (opt-in — a full cloud
perf pass comes later).

## The key architectural unlock

The cloud system already bakes a **cloud shadow map**: world-XZ, top-down sun march, finite region,
already bound to the terrain material (`cloud_shadow_tex` + `cloud_shadow_region`, owned by
`CloudVolume`). **That map is the shared occluder for god rays.** Both god-ray components sample it
by world-XZ position, so the shafts automatically line up with the cloud gaps AND with the shadows
already on the ground. This makes the fog cloud-occluded **without touching the directional light's
own shadow map** — which is precisely what produced the old black wedges. We sidestep that failure
mode entirely: the cloud occlusion is *additive emission driven by a sampled texture*, never a
subtractive collision with the light's volumetric shadow.

## Component A — In-scene cloud-occluded volumetric shafts (the foundation)

A large **FogVolume** centered on the camera (XZ-follow) with a custom `shader_type fog;` shader.

Confirmed Godot fog-shader built-ins (input): `TIME`, `WORLD_POSITION` (froxel world pos),
`OBJECT_POSITION`, `UVW`, `SIZE`, `SDF`. Outputs: `DENSITY` (required), `ALBEDO`, `EMISSION`.
Fog shaders support custom `uniform` parameters incl. samplers (noise-fog shaders sample
`texture(noise_tex, WORLD_POSITION.xz)` — same pattern we need).

Shader logic:
- `sunVis = texture(cloud_shadow_tex, worldToShadowUV(WORLD_POSITION.xz)).r` → 1 where sun breaks
  through a gap, 0 under a cloud. (Same UV mapping as the terrain: `(world.xz / region) + 0.5`.)
- `DENSITY = base_haze * heightFalloff(WORLD_POSITION.y)` — a light medium for scattering; more in
  valleys, thinning with altitude. Small, so it doesn't fog the whole scene — just enough to carry light.
- `EMISSION = sun_color * strength * sunVis * heightFalloff` — the air glows where sun reaches.

Result: sun pouring through a cloud gap lights a shaft of air down to the terrain; under clouds the
air stays dim. Real 3D crepuscular rays, cloud-shaped, near the ground where they read best. Rides
Godot's froxel volumetric fog (TAA-stable, reasonably cheap). **Requires** Environment volumetric
fog ON (`volfog_on`, default true).

Module: `scripts/lab/GodRays.cs` (separation of concerns — owns the FogVolume + Fog/Shader material;
consumes the cloud shadow texture + sun from `CloudVolume`; does NOT touch cloud internals). Public
surface: `Attach(envOwner, camera)`, `SetShadowTexture(tex, region)`, `SetSun(dir,color,energy)`,
`SetEnabled(bool)`, `SetStrength(float)`, `SetHaze(float)`, camera XZ-follow in its own `_Process`.

## Component B — Screen-space radial shafts (the drama layer, AFTER A is eye-approved)

The classic GPU Gems 3 Ch.13 post-process (Kenny Mitchell, "Volumetric Light Scattering as a
Post-Process"): radial-blur an occlusion buffer outward from the sun's screen position, add it back —
the bright "fan" of shafts. Full-screen quad reading screen + depth (NOT a CompositorEffect — avoids
the RID race noted in memory `compute-to-material-callonrenderthread`). Gated to sun on/near screen;
fades out when the sun is off-screen or behind the camera. Layers additively on top of A.

## Unification (resolves the duplication)

One **"God Rays"** control group, in the Clouds tab (it's cloud-occlusion-driven):
- master on/off (`cloud_godrays`, default false)
- volumetric strength (`cloud_godray_strength`)
- [later] screen-space on/off + strength

Retire: the Light-tab `godray_e` control + its `case "godray"` scene handler (the uniform-fog one).
The in-cloud in-scatter code in `cloud_raymarch.glsl` is disconnected (toggle no longer drives the
`godrays` uniform → stays 0); removed in a cleanup pass once A is validated (it's raymarch-only, so
removal is coupling-safe — does not touch the density field the shadow shaders mirror).

## Risks / validation items (confirm during build — do not assume)
1. **Texture2Drd in a fog shader.** The cloud shadow is RID-backed (`Texture2Drd`). Terrain samples
   it fine; must confirm a FogVolume's ShaderMaterial can take it as a `shader_parameter`. If a fog
   shader can't bind a Texture2Drd, fallback: have `CloudVolume` also expose the shadow as a normal
   `ImageTexture`/`Texture2D` readback for the fog (cost: a copy), or render the shadow to a viewport.
2. **Froxel resolution.** Godot volumetric fog can look soft/low-res → mushy shafts. Levers: env
   `volumetric_fog_length`, froxel detail, a tighter FogVolume, higher density contrast.
3. **Screen-space sun behind camera** (Component B) — must fade cleanly, no smear.
4. **Perf** — both toggleable, default OFF. Numbers go in `cloud-system-overview.md` perf table.

## Health guards (unchanged)
- `--shadowcheck` still PASS (god rays don't touch the density field → coupling unaffected).
- `dotnet build WG16.csproj` clean; launch `scenes/terrain_lab.tscn --rendering-driver vulkan`.
- Judge in motion, never a still (memory `wg16-mipmap-fuzz-gotcha` discipline).
