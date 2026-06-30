# Waterways Feature Review

Reference reviewed: https://github.com/Arnklit/Waterways

Waterways is useful here as an architecture reference, not an implementation source. No Waterways code, shaders, icons, or textures are copied into Fast Water.

## Useful Ideas To Keep

- Author water as paths, not only infinite planes. This supports rivers, canals, pools, and shaped shoreline strips.
- Keep authored water separate from runtime interaction. A river/path mesh should be usable without splashes, bubbles, or post-process.
- Provide water queries for gameplay. A body should be able to ask for water altitude and flow without knowing how the water is rendered.
- Make buoyancy a separate component. Projects can attach it to a `RigidBody3D` only when physics water is needed.
- Treat generated maps as an optional higher tier. Waterways bakes flow, foam, distance, and system maps; Fast Water should first stay real-time and cheap, then add authoring/baking only as an opt-in module later.
- Keep debug/authoring tools separate from runtime nodes.

## Fast Water Mapping

- `FastWaterSurface`: ocean/test-pool plane with event-driven ripples and wake map.
- `FastWaterPath`: shaped river/canal ribbon using the same water material and gameplay query API.
- `FastWaterBuoyant`: optional `RigidBody3D` helper that consumes `get_water_altitude`, `get_surface_height_at`, and `get_flow_at`.
- `FastWaterPlanarReflection`: optional visual tier for hero shots where reflected geometry matters.

## Later, If Needed

- `FastWaterSystemMap`: a generated global query texture for many rivers and lakes.
- Editor path handles and point gizmos.
- Optional generated flow/foam maps for authored rivers.
- Debug views for wake map, flow, foam, and water altitude.
