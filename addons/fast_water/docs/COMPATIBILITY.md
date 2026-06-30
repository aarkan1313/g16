# Fast Water Compatibility

## Godot Version

Current validation target:

- Godot `4.6.2.stable.mono`
- Forward Plus renderer
- D3D12 render gates on Windows

The addon uses standard Godot 4.x APIs, `ShaderMaterial`, `GPUParticles3D`, `SubViewport`, `ImageTexture`, and GDScript tool scripts. Projects on older Godot 4.x versions should run the parser/import/render gates before shipping.

## Renderer Notes

| Target | Status | Notes |
| --- | --- | --- |
| Godot 4.6.2 stable mono | Verified | Parser, import, runtime contracts, clean-copy gate. |
| Forward Plus + D3D12 + desktop Windows | Verified | Current render-gate target for hero, ocean, river, rain, underwater, waterfall, benchmark, and debug views. |
| Forward Plus + Vulkan | Expected | Uses standard Godot 4.x rendering APIs, but should be rendered in the destination project. |
| Mobile renderer (Forward Mobile, D3D12) | Verified | Rendered the GPU-wake ocean tier with full depth refraction/absorption and no seam. Still prefer `FastWaterVisualProfile.mobile_low()` / `FastWaterOceanProfile.performance()`, disable planar reflections, and lower particles on real devices. |
| Compatibility renderer (GLES3) | Renders with reduced optics | Water surface, color, horizon fade, and the GPU wake map render without shader errors, but depth-texture-driven refraction/absorption is reduced (submerged geometry is not shown through the surface). Acceptable graceful degradation; not a hero target. |
| Desktop GPU wake map | Verified | Used by the ocean tier under render gates on Forward+ and Mobile. Use CPU wake maps for deterministic contracts, GPU wake maps for runtime quality. |
| Mobile GPU wake map | Verified (Forward Mobile) | Rendered the ocean GPU wake without errors. SubViewport ping-pong cost still scales with resolution/update rate; keep `local_wake_resolution`/`local_wake_update_hz` modest on devices. |

Planar reflections are optional and should be disabled for distant or background water.

## Platform Notes

- CPU wake maps use `ImageTexture` updates and are simplest for deterministic tests.
- GPU wake maps use SubViewport ping-pong resources and are recommended for hero water when renderer support is available.
- Waterfall, rain, bubble, and splash FX use `GPUParticles3D`; projects can replace these helpers with custom effect targets.

## Weather Boundary

Fast Water is weather-aware, not a weather system. External systems should own:

- clouds
- seasons
- time of day
- precipitation scheduling
- fog and lightning
- global lighting

Fast Water consumes only water-relevant state: wind direction/speed, gusts, rain intensity, storm intensity, water turbidity, and sun color.
