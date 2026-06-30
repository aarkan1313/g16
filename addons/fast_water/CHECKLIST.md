# Fast Water Completion Checklist

This file tracks the long-running goal of turning Fast Water into a modular AAA-ready Godot water add-on. Keep edits scoped to `addons/fast_water` and `artifacts/fast_water*`.

## Phase 0: Hero Water Quality And Debug Views

- [x] Add first-class shader debug view uniform on `FastWaterSurface`.
- [x] Add shader debug views for depth, foam, wake, normals, flow, reflection, and optics.
- [x] Add benchmark HUD/control support for cycling shader debug views.
- [x] Add benchmark render-gate argument for capturing a specific debug view.
- [x] Split reusable visual profiles into `gameplay_lake()` and `hero_pool_reference()`.
- [x] Add initial hero profile contrast/clarity tuning pass.
- [ ] Tune hero profile against accepted screenshots until the water reads as premium in motion.
- [x] Add dedicated hero lake/pool visual reference scene.
- [x] Remove obvious rectangular proof props from the current hero-reference visual capture.
- [x] Guard finite wake-map sampling so out-of-bounds UVs return a neutral value instead of drawing a square footprint.
- [x] Root-cause and eliminate remaining live square/rectangle shapes on the water surface. (Traced to analytic hero ripples displacing coarse vertices + hard finite-map/mesh edges; ripples now drive fragment normal/foam only, wake/flow/foam maps feather to neutral at borders, and near/far mesh edges alpha-fade. Headless ocean/full-proof/world-integration gates render clean.)
- [x] Add or verify usable review-camera controls for launched full-proof/hero visual scenes. (Demo: free-fly — right-mouse look, WASD move, Q/E up/down, Shift fast. Benchmark: RMB-drag orbit + wheel zoom + quality/backend/view hotkeys.)
- [x] Polish underwater overlay color, particulate feel, and waterline transition.
- [x] Add debug view screenshots to the standard artifact set.

## Phase 1: Stable Water Body Contracts

- [x] Add `FastWaterBodyProfile`.
- [x] Define shared query semantics for height, altitude, depth, flow, and containment.
- [x] Add helper for nearest/active water body lookup.
- [x] Add debug overlays for body bounds, flow vectors, and altitude probes.
- [x] Verify interactors and buoyancy against both `FastWaterSurface` and `FastWaterPath`.

## Phase 2: Flow Fields And Downhill Rivers

- [x] Add initial `FastWaterFlowField`.
- [x] Wire flow fields into path/surface shader parameters.
- [x] Add initial downhill river demo.
- [x] Add stronger river authoring controls for per-point width, depth, current, bank foam, and turbulence.
- [x] Add reliable flow-field debug capture/view.
- [x] Stabilize flow-field compile, rebuild, and texture-resize behavior under render gates.
- [x] Make river demo look production-like with terrain banks, rocks, rapids zones, and believable scale.
- [x] Prove buoyant object drift with authored current in a render/test gate.

## Phase 3: Unified Foam And Whitewater

- [x] Add source-aware foam model for shoreline, wake, rain, rapids, waterfall lip, plunge pool, and eddies.
- [x] Add persistent foam/whitewater field or extend flow field channels.
- [x] Make foam age, advect, dissipate, and thicken from turbulence/current.
- [x] Tie foam to color, opacity, roughness, normal flattening, bubbles, and spray.
- [x] Ensure bow wake/ribbon helpers can be disabled without losing main interaction readability.

## Phase 4: Weather Response

- [x] Add initial `FastWaterEnvironmentState`.
- [x] Add initial `FastWaterWeatherResponse`.
- [x] Add initial `FastWaterWeatherAdapter`.
- [x] Add initial rain ripple wake-map emission.
- [x] Add visible rain impact particles/mist.
- [x] Add storm whitecaps and wind gust state.
- [x] Add clear/drizzle/heavy-rain/storm/calm-after-storm demo.
- [x] Polish underwater turbidity and visibility response.

## Phase 5: Waterfalls, Spray, And Plunge Pools

- [x] Add `FastWaterWaterfall`.
- [x] Generate falling sheet/ribbon mesh from lip curves.
- [x] Add falling-sheet material with thickness/noise masks.
- [x] Add mist/spray emitters at lips, shelves, and plunge pools.
- [x] Stamp plunge foam and downstream turbulence into foam/flow systems.
- [x] Add LOD/culling controls.

## Phase 6: Ocean/Open-World Tier

- [x] Add camera-relative large-water mesh or clipmapped tier.
- [x] Add far-water material behavior.
- [x] Add profile-driven swell, chop, whitecaps, and horizon fade.
- [x] Keep wake maps local around camera/hero actors.
- [x] Add cheaper distant reflection mode.
- [x] Make only the `FastWaterOcean` facade answer `FastWaterBodyQuery`; render tiers are non-queryable so wakes/splashes never route to the non-interactive far tier.
- [x] Make ocean height queries track the analytic swell (shared with the shader) instead of a flat plane, and forward `get_flow_at` to the near tier.
- [x] Dissolve the far tier's geometric boundary (`far_edge_fade_width`) and complete the horizon fade inside the far half-extent so no hard ocean edge shows from elevation.
- [x] Skip the 3-call foam-noise stack on the far tier so far water is genuinely cheap in fragment cost, not just feature-reduced.

## Phase 7: Authoring Tools And Debug Views

- [x] Add editor authoring overlay for paths, widths, flow direction, fall lips, foam fields, and foam sources.
- [x] Add import/export for flow and foam maps where baking is wanted.
- [x] Add debug view switching from the benchmark UI.
- [x] Add docs for each module and common setup paths.
- [x] Add sample scenes for pool/lake, river, rain, underwater, waterfall, ocean, and a combined full-proof scene.

## Phase 8: Packaging And Compatibility

- [x] Keep plugin startup clean in fresh Godot projects.
- [x] Audit generated `.uid` and `.import` sidecars before release.
- [x] Add minimal examples and troubleshooting notes.
- [x] Add a compatibility matrix for renderer, Godot version, mobile/desktop, Forward Plus/Mobile.
- [x] Verify copying `addons/fast_water` into a clean project.
- [x] Freeze and document stable public API.

## Gate Checklist

- [x] Parser sweep.
- [x] Import scan.
- [x] Clean-copy project verification.
- [x] Body contract runtime check.
- [x] Interaction contract runtime check.
- [x] River authoring contract runtime check.
- [x] River demo contract runtime check.
- [x] Foam field contract runtime check.
- [x] Foam reactive FX contract runtime check.
- [x] Optional bow/ribbon helper contract runtime check.
- [x] Rain impact FX contract runtime check.
- [x] Weather whitecap contract runtime check.
- [x] Weather sequence contract runtime check.
- [x] Underwater turbidity contract runtime check.
- [x] Waterfall contract runtime check.
- [x] Ocean contract runtime check.
- [x] World integration contract runtime check (lake + stream/river + near/far ocean composition; facade-only ocean query).
- [x] Floating-origin contract runtime check (wave_sample_offset propagates equally to both tiers, pushed to shader, pure frame translation).
- [x] Buoyancy-on-ocean contract runtime check (queried surface has wave relief, resolves the ocean facade, altitude consistent).
- [x] Far-foam GPU benchmark (foam_detail_enabled on/off; ~14% water-pass GPU time saved on a full-screen view).
- [x] Presets load contract (16 shipped .tres resources load with script + key property).
- [x] Integration contract (surface signals, FastWater service queries, FastWaterVolume re-emit + depth, FastWaterSwimmer submersion).
- [x] Reflection throttle contract (update_hz skips ~75% of full-scene reflection renders at 30 Hz / 120 fps).
- [x] Gerstner contract (height-query inversion degrades exactly to sine at choppiness 0; bounded with choppiness).
- [x] Hero-reflection GPU benchmark (per-render reflection cost + amortization).
- [x] Showcase build contract (island scene: ocean facade offshore, river path + flow, hillside pool, splash ball, ocean floaters).
- [x] Showcase render + perf metrics (overview / river / pool-splash shots; ~1.1 ms GPU at 1440p).

## Phase 9: Productionization (v0.3.0)

- [x] Data-driven `.tres` preset library under `presets/` (visual / ocean / body / quality) regenerated from code via `tools/generate_fast_water_presets.gd`.
- [x] Gameplay/engine integration layer: outbound `splashed`/`wake_added` signals, `FastWaterVolume` (Area3D enter/exit + depth), `FastWaterSwimmer` (CharacterBody3D submersion/float), and the `FastWater` autoload service.
- [x] `docs/INTEGRATION_GUIDE.md` 10-minute integration path.
- [x] Distant-specular LOD (normal flatten + roughness floor by distance) to kill far-water shimmer.
- [x] Hero-water GPU efficiency: planar reflection `update_hz` throttle (kept portable SubViewport path; verified on Forward+/Mobile/Compatibility).
- [x] Optional Gerstner displacement waves with a synchronized CPU height query.
- [x] `demo/fast_water_showcase.tscn` proof scene: an island in the ocean with a river down the mountainside, buoyant objects offshore, and a ball splashing into a hillside pool; free-fly camera; performant (single ~1.1 ms GPU water/scene pass at 1440p on desktop).
- [x] Authoring overlay and map import/export contract runtime check.
- [x] Public API contract runtime check.
- [x] Release audit contract runtime check.
- [x] Body debug overlay render.
- [x] Benchmark render.
- [x] Hero reference render.
- [x] Hero visual metrics artifact.
- [x] Hero accepted-reference comparison has an enforceable tolerance mode.
- [x] Hero motion review strip and temporal metrics.
- [x] Visual review packet contact sheet and manifest.
- [x] Demo render.
- [x] Underwater render.
- [x] Underwater turbidity render.
- [x] Rain render.
- [x] River render.
- [x] Waterfall render.
- [x] Ocean render.
- [x] Debug-view render artifacts.
- [x] No Fast Water Godot processes left running after the latest launch check.
