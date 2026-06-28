shader_type spatial;
#include "res://shaders/field_math.gdshaderinc"
// GROUND — minimal height-color PLACEHOLDER (2026-06-21 strip).
// The full per-pixel material system (placement / arrays / blend / splat / old terrain_lab) was
// stripped back to a clean base. This just displaces the mesh from the heightfield and paints a
// height + slope color ramp, lit by Godot's default lighting (the sky/sun). Not blank; the base
// we plan CDLOD / infinite terrain on. NO render_mode — object space == world at identity, matching
// the field. Everything tunable is a uniform.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

// S1: live-field (analytic) displacement vs. the baked heightmap. ground_fieldp()
// mirrors FieldParams (pushed as fp_* from TerrainLab.cs); analytic_h() evaluates the
// SAME field_height() the compute bake uses, so the two paths agree by construction.
uniform bool  use_analytic = true;
uniform int   analytic_seed = 0;
uniform float analytic_spacing = 4.0;
uniform float use_chunk = 0.0;             // S2a: 1.0 = CDLOD chunk-mode (set once on the shared material)
instance uniform float lod_viz = -1.0;     // S2a debug: >=0 tints this chunk by LOD level; <0 = normal
// Per-chunk field CACHE (GPU-compute): height in .r, fixed-step normal in .gba, baked once per chunk on birth.
uniform sampler2DArray chunk_cache;
uniform float cache_side = 67.0;           // baked grid side = grid_n + 2 (1-texel border each side)
instance uniform float chunk_slot = -1.0;  // this chunk's cache texture-array layer
instance uniform float cache_ready = 0.0;  // 1 = bake landed → sample the cache; 0 = live-eval (dual path)
// S2b geomorph inputs (chunk branch only). cam_world is re-declared post-strip (TerrainLab.SetCameraWorld
// already pushes it every frame; the stripped shader had dropped the uniform so the push was a no-op).
uniform vec3  cam_world = vec3(0.0);    // camera world pos (chunk geomorph per-vertex distance)
uniform vec3  render_origin = vec3(0.0);   // S3: world-XZ offset of the camera-relative render space; add to
                                           // render-relative pos to recover TRUE world XZ for field_height.
uniform float grid_n = 65.0;            // chunk grid resolution (verts/side) — for odd->even snap
uniform float split_factor = 2.5;       // quadtree split rule: subdivide when camDist < size*split_factor
// Godot's shading language requires ONE variable per uniform statement (no comma lists).
uniform float fp_base_freq;
uniform float fp_amplitude;
uniform float fp_lacunarity;
uniform float fp_gain;
uniform int   fp_octaves;
uniform int   fp_field_mode;
uniform int   fp_cont_octaves;
uniform float fp_cont_freq;
uniform float fp_cont_weight;
uniform float fp_uplift_freq;
uniform float fp_uplift_weight;
uniform float fp_uplift_lo;
uniform float fp_uplift_hi;
uniform float fp_macro_pivot;
uniform float fp_macro_amp;
uniform float fp_hill_damp;
uniform float fp_ridge_freq;
uniform float fp_ridge_amp;
uniform float fp_mtn_lo;
uniform float fp_mtn_hi;
uniform float fp_grain_stretch;
uniform float fp_cont_warp;
uniform float fp_uplift_warp;
uniform float fp_massif_freq;
uniform float fp_massif_floor;
uniform float fp_foothill_w;
uniform float fp_foothill_h;

FieldP ground_fieldp() {
    FieldP fp;
    fp.origin_x = 0.0; fp.origin_z = 0.0; fp.spacing = analytic_spacing;
    fp.seed = uint(analytic_seed); fp.res = 0u; fp.octaves = uint(fp_octaves);
    fp.base_freq = fp_base_freq; fp.amplitude = fp_amplitude; fp.lacunarity = fp_lacunarity;
    fp.gain = fp_gain; fp.field_mode = uint(fp_field_mode); fp.cont_freq = fp_cont_freq;
    fp.cont_weight = fp_cont_weight; fp.uplift_freq = fp_uplift_freq; fp.uplift_weight = fp_uplift_weight;
    fp.uplift_lo = fp_uplift_lo; fp.uplift_hi = fp_uplift_hi; fp.macro_pivot = fp_macro_pivot;
    fp.macro_amp = fp_macro_amp; fp.hill_damp = fp_hill_damp; fp.ridge_freq = fp_ridge_freq;
    fp.ridge_amp = fp_ridge_amp; fp.mtn_lo = fp_mtn_lo; fp.mtn_hi = fp_mtn_hi;
    fp.grain_stretch = fp_grain_stretch; fp.cont_octaves = uint(fp_cont_octaves);
    fp.cont_warp = fp_cont_warp; fp.uplift_warp = fp_uplift_warp; fp.massif_freq = fp_massif_freq;
    fp.massif_floor = fp_massif_floor; fp.foothill_w = fp_foothill_w; fp.foothill_h = fp_foothill_h;
    return fp;
}
float analytic_h(vec2 world_xz) {
    return field_height(world_xz, uint(analytic_seed), analytic_spacing, ground_fieldp());
}

// Field-cache UV: a unit chunk coord u01∈[0,1] → the baked (grid_n+2) grid texel (vertex g=u01·(grid_n-1) sits
// at texel index g+1 for the 1-texel border; +0.5 → texel center). slot = the chunk's texture-array layer.
vec3 cuv(vec2 u01, float slot) {
    return vec3((u01 * (grid_n - 1.0) + 1.5) / cache_side, slot);
}

// height-color ramp (meters -> color) + slope->rock. Placeholder palette; tune freely.
uniform vec3  col_low  : source_color = vec3(0.34, 0.40, 0.24);   // valley grass/dirt
uniform vec3  col_mid  : source_color = vec3(0.46, 0.39, 0.28);   // slope earth
uniform vec3  col_high : source_color = vec3(0.43, 0.43, 0.45);   // rock
uniform vec3  col_peak : source_color = vec3(0.93, 0.94, 0.97);   // snow
uniform vec3  col_cliff: source_color = vec3(0.34, 0.33, 0.34);   // steep rock
uniform float h_low  = 40.0;
uniform float h_mid  = 280.0;
uniform float h_high = 500.0;
uniform float h_peak = 600.0;
uniform float cliff_lo = 0.45;
uniform float cliff_hi = 0.72;
uniform float ground_rough = 0.92;


// S2b: the ONE shared distance-fade curve future surface detail (textures/parallax) must ride, so all
// detail transitions share a single soft band (no quality-pop / no stacked rings). UNUSED today — the
// placeholder ground has no distance-varying detail yet. Reserved seam for the surfacing arc.
uniform float detail_fade_near = 250.0;    // albedo speckle dissolves to texture mean across this band — start
uniform float detail_fade_far  = 1400.0;   // close enough to clear the near moiré, done before the far resonance
float detail_fade(float dist) { return 1.0 - smoothstep(detail_fade_near, detail_fade_far, dist); }
// Footprint anti-moiré band (texels per screen-pixel): below lo = full per-texel detail, above hi = flat texture
// mean. Tunable; ~2..8 dissolves high-contrast speckle right as it stops being resolvable (kills the dot-grid).
uniform float detail_fp_lo = 1.0;
uniform float detail_fp_hi = 3.0;
// DEFAULT OFF (2026-06-23): this dissolve-to-mean fade was added to mask the CDLOD UV-ring bug now
// fixed at the source (v_surf_xz = vtrue_fine, commit 38aab96). With the bug gone the fade only HURTS —
// it washes wanted far-field detail toward flat mean colour (eye-confirmed: terrain looks better with it
// off). Kept (not deleted) because real distance detail-management is genuine surfacing-arc work; the
// dissolve-to-mean METHOD is the wrong lever for it (use mip/aniso — present — + detail/macro blending or
// texture-array LOD later). Toksvig (below) is INDEPENDENT of this and stays on: it solves a different,
// real problem (normal-map specular AA). Key G still A/Bs the fade live.
uniform float detail_fade_on = 0.0;   // live A/B (key G): 1 = legacy DISTANCE-driven anti-moiré fade ON, 0 = OFF
// FOOTPRINT (fwidth) detail-fade — the fade the comment block below always INTENDED but never got (the code
// drove it by raw distance, so a near grazing slope never faded → the directional placeholder textures kept
// shimmering into "creeping" streaks). Driven by texture-tiles-per-pixel, so it auto-targets grazing angle AND
// distance and keeps head-on near ground sharp. Default ON; modular A/B via --footprintfade=0.
uniform bool footprint_fade = false;  // DEFAULT OFF (2026-06-27 eye-gate): it washed ground DEFINITION to flat mean past the near foreground — user values the detail over the grazing-shimmer it masked. Opt-in via key T / --footprintfade=1. Real shimmer fix = surfacing arc, not detail removal.
uniform float fp_fade_lo = 0.012;     // footprint (tiles/pixel) where detail starts dissolving to mean
uniform float fp_fade_hi = 0.10;      // footprint where detail is fully mean
uniform float fp_fade_max = 1.0;      // cap on dissolve amount (1 = can go fully flat-mean)
// NORMAL-MAP footprint rolloff — DECOUPLED from the albedo footprint_fade above. The tiled normal map is what
// aliases into the grazing/distance MOIRÉ ripples, so it must ALWAYS fade out with footprint (even when albedo
// stays sharp). These mirror the old fp_fade thresholds (the normal rolloff that had NO moiré when fade was on).
uniform float nrm_fade_lo = 0.06;     // RAISED 2026-06-28 (audit): keep surface RELIEF through near/mid view
uniform float nrm_fade_hi = 0.35;     // was 0.012/0.10 → relief vanished as you turned ("definition disappears,
                                      // flat as unlit"). Now relief only fades at extreme grazing; aniso 16x handles moiré.

varying vec3 v_normal;   // world-space geometric normal from neighbour height taps
varying float v_h;       // height (m)
varying vec2 v_wxz;      // DEBUG: reconstructed TRUE-world XZ the field was sampled at (snap-pop hunt)
varying vec2 v_surf_xz;  // true-world XZ for world-space texture tiling (set in ALL vertex branches)

// --- Minimal surfacing slice (readable landforms). Fixed-sampler textured path, A/B vs the colour ramp. ---
uniform bool use_textures = true;    // true = textured (default, minimal surfacing slice); --textures=0 = legacy colour ramp
uniform float mat_tiling = 12.0;     // world metres per texture tile (smaller = more repeats)
uniform float toksvig_k = 0.5;       // Toksvig specular-AA sensitivity: higher = roughen sooner as normal-map variance rises
// placement bands (metres for height, normalized 0..1 slope = 1 - normal.y). Soft via fwidth.
uniform float surf_h_sand  = 30.0;    // below → sand/low (mat0)
uniform float surf_h_grass = 220.0;   // sand→grass band top (mat1 valley)
uniform float surf_h_earth = 430.0;   // grass→earth band top (mat2 mid)
uniform float surf_h_rock  = 560.0;   // earth→rock; above → snow (mat4) by height
uniform float surf_slope_rock = 0.42; // slope above this forces rock (mat3) regardless of height
uniform float surf_slope_soft = 0.10; // half-width of the slope transition
uniform sampler2D mat0_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat0_nrm : filter_linear_mipmap_anisotropic, repeat_enable, hint_normal;
uniform sampler2D mat0_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat1_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat1_nrm : filter_linear_mipmap_anisotropic, repeat_enable, hint_normal;
uniform sampler2D mat1_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat2_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat2_nrm : filter_linear_mipmap_anisotropic, repeat_enable, hint_normal;
uniform sampler2D mat2_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat3_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat3_nrm : filter_linear_mipmap_anisotropic, repeat_enable, hint_normal;
uniform sampler2D mat3_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat4_alb : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat4_nrm : filter_linear_mipmap_anisotropic, repeat_enable, hint_normal;
uniform sampler2D mat4_rgh : filter_linear_mipmap_anisotropic, repeat_enable;
// Ambient-occlusion channel (every material dir ships ao.png; was previously NEVER bound/used). AO gives
// free contact-shadow "definition" in crevices — exactly the depth the flat-reading ground was missing.
uniform sampler2D mat0_ao : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat1_ao : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat2_ao : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat3_ao : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2D mat4_ao : filter_linear_mipmap_anisotropic, repeat_enable;
// TERRAIN LIGHTING SOFTENING (2026-06-28): the "ground shading swings hard as you turn" was isolated to the
// diffuse directional sun (I-key removes it; SSAO/shadow toggles don't). A custom light() with a wrap/
// half-Lambert diffuse lifts the shadowed faces + gentles the terminator, so turning is no longer a harsh
// dark swing — AO + normal maps still carry micro-detail. Terrain-LOCAL (this shader only); the global
// Sun/sky/mood are untouched. ground_wrap: 0 = hard true-Lambert, 1 = fully soft. Key C toggles it live.
uniform bool  ground_soft_light = true;
uniform float ground_wrap = 0.6;
// Erosion-lab debug overlay: false-colour a scalar field (water/sediment/flow) by region UV. 0 = off.
// Zero-impact on the terrain (default off); only the erosion lab sets it.
uniform float debug_field = 0.0;
uniform sampler2D debug_field_tex : filter_linear;
uniform float debug_wxz = 0.0;   // DEBUG: 1.0 = output reconstructed world XZ (mod 512) as R/G color
uniform float diag_mode = 0.0;   // RING HUNT: 0 normal, 1 grey base, 2 +albedo, 3 +roughness, 4 +normalmap (key H steps)
uniform bool dbg_use_normalmap = true;
uniform bool dbg_fullrough = false;
// SURFACING FIX (2026-06-27): triplanar (world-projected) sampling for ALL ground materials, not just rock.
// The planar top-down UV (v_surf_xz/mat_tiling) smears the texture into directional STREAKS on slopes — the
// "creeping diagonal shadow that moves with you" the user sees on grass/dune faces. Triplanar removes the
// slope stretch at the source (mat3 rock already used it). Gated so --triall=0 restores the old planar look
// for a clean A/B. Default ON. See docs/handoffs/2026-06-27-terrain-surfacing-uv-stretch-HANDOFF.md.
uniform bool tri_all = true;
uniform float tri_sharp = 4.0;    // triplanar plane-blend exponent (higher = sharper plane selection; lower = side planes engage on gentler slopes)
uniform float tri_ybias = 1.0;    // DEBUG/probe: <1 demotes the top-down (Y) plane so slopes sample the un-stretched side planes (0 = no top-down at all)
uniform bool dbg_unlit = false;

// --- HYDROLOGY carve. The region water texture: R=dist-to-river(m), G=bedDepth(m), B=flow, A=wet mask.
// Carve = subtract a compact-support groove where wet. Default OFF (no texture bound) so the base field
// is byte-identical until water is explicitly enabled (--fieldcheck 0m). ---
uniform sampler2D water_tex : filter_linear;
uniform vec2  water_region_origin = vec2(0.0);
uniform float water_region_m = 8192.0;
uniform float carve_width_m = 36.0;
uniform float carve_depth_scale = 1.0;
uniform float carve_profile_exp = 2.0;
uniform bool  water_carve_on = false;

uniform bool water_debug = false;   // DEBUG (key H): paint wet pixels cyan / banks blue so the carve is visible

// Debug overlay: tint by the water texture — bright cyan where wet (river/lake), blue in the carve band.
vec3 water_debug_tint(vec3 base, vec2 wxz) {
    if (!water_debug || !water_carve_on) return base;
    vec2 uv = (wxz - water_region_origin) / water_region_m;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return base;
    vec4 w = texture(water_tex, uv);
    if (w.a > 0.5) return vec3(0.0, 0.9, 1.0);                       // wet channel/lake → bright cyan
    if (w.r < carve_width_m) return mix(base, vec3(0.1, 0.3, 0.8), 0.6); // carve band → blue
    return base;
}

float carve_offset(vec2 wxz) {
    if (!water_carve_on) return 0.0;
    vec2 uv = (wxz - water_region_origin) / water_region_m;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) return 0.0;  // outside the bound region
    vec4 w = texture(water_tex, uv);
    if (w.a < 0.001 && w.r >= carve_width_m) return 0.0;
    float t = clamp(w.r / carve_width_m, 0.0, 1.0);     // 0 at channel centre .. 1 at bank
    float profile = pow(1.0 - t * t, carve_profile_exp); // compact support, 0 at bank (C1)
    return w.g * carve_depth_scale * profile;            // metres to subtract
}

float height_at(vec2 uv) { return texture(heightmap, uv).r; }

void vertex() {
    if (use_chunk > 0.5) {
        // --- S2b GEOMORPH: morph each vertex toward the COARSE grid by a per-vertex factor, so a
        // chunk's odd verts slide onto the coarse grid before it coarsens -> no elevation pop, and
        // per-vertex distance makes the morph continuous across chunk seams. ---
        float chunk_size = length(MODEL_MATRIX[0].xyz);    // chunk world size (X-basis length)

        // fine vertex grid coord (integer) from unit pos; coarse target = snap odd index down to even.
        vec2 u_fine = VERTEX.xz + vec2(0.5);               // unit [0,1]
        vec2 g_fine = u_fine * (grid_n - 1.0);             // [0, grid_n-1]
        vec2 g_coarse = floor(g_fine * 0.5 + 0.5) * 2.0;   // nearest even grid index
        vec2 u_coarse = g_coarse / (grid_n - 1.0);

        // per-vertex morphK: distance from camera to THIS vertex's world pos, mapped across the chunk's
        // LOD band. The chunk coarsens at camDist == chunk_size*split_factor; it was created (subdivided
        // from its parent) at camDist == 2*chunk_size*split_factor. Morph 0->1 over the FAR HALF of that
        // band (start morphing at the midpoint, fully morphed by the coarsen distance) — the standard
        // CDLOD morph window.
        vec3 vworld_fine = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;          // render-relative pre-displacement pos
        vec2 vtrue_fine = vworld_fine.xz + render_origin.xz;               // S3: TRUE world XZ (cam_world is true-world)
        float d = distance(cam_world.xz, vtrue_fine);                       // morph distance in a CONSISTENT (true) frame
        float far_d  = 2.0 * chunk_size * split_factor;    // band outer (this LOD just appeared)
        float near_d = 1.0 * chunk_size * split_factor;    // band inner (about to coarsen)
        float mid_d  = mix(near_d, far_d, 0.5);
        // morphK: 0 at the band's NEAR edge (d=near_d, chunk about to split into FINER children → keep
        // full fine detail, no morph), 1 at the FAR edge (d=far_d, chunk about to be replaced by its
        // COARSER parent → vertices must already sit on the coarse parent's grid so that swap is
        // invisible). This is the standard Strugar CDLOD morph direction. (The original plan code added a
        // `morphK = 1.0 - morphK` inversion here — that was a sign bug: it put the full-coarse morph at
        // the NEAR edge, where the chunk actually gets FINER, so the far-edge hand-off to the coarse
        // parent snapped raw → simultaneous height + normal pop. Removed 2026-06-21 after the eye-gate.)
        float morphK = clamp((d - mid_d) / max(far_d - mid_d, 1e-3), 0.0, 1.0);  // 0 near .. 1 far edge

        vec2 u_morph = mix(u_fine, u_coarse, morphK);
        vec2 wxz = (MODEL_MATRIX * vec4(u_morph.x - 0.5, 0.0, u_morph.y - 0.5, 1.0)).xz + render_origin.xz;  // S3: TRUE morphed world XZ

        // HEIGHT + NORMAL — dual path. CACHE (cache_ready=1): one sample of the per-chunk field cache at the
        // SAME morphed u_morph the live path evaluates analytic_h(wxz) at — so the geomorph (odd verts sliding
        // to the coarse grid) is reproduced EXACTLY and the look is identical; the baked normal is the SAME
        // fixed-step central difference, so LOD-independent shading is preserved (--popcheck). LIVE (un-baked,
        // the few frames after birth): the original 5-eval analytic field — the dual path bridges the async
        // bake landing with no pop (same values). NO SKIRT either way (edge-stitched variants weld seams).
        float h0;
        if (cache_ready > 0.5) {
            vec4 hn = textureLod(chunk_cache, cuv(u_morph, chunk_slot), 0.0);
            h0 = hn.r - carve_offset(wxz);
            v_normal = normalize(hn.gba);
        } else {
            h0 = analytic_h(wxz) - carve_offset(wxz);
            float ns = max(analytic_spacing, 1.0);             // fixed FD step (m), LOD-independent
            float hxp = analytic_h(wxz + vec2(ns, 0.0)) - carve_offset(wxz + vec2(ns, 0.0));
            float hxm = analytic_h(wxz - vec2(ns, 0.0)) - carve_offset(wxz - vec2(ns, 0.0));
            float hzp = analytic_h(wxz + vec2(0.0, ns)) - carve_offset(wxz + vec2(0.0, ns));
            float hzm = analytic_h(wxz - vec2(0.0, ns)) - carve_offset(wxz - vec2(0.0, ns));
            v_normal = normalize(vec3(hxm - hxp, 2.0 * ns, hzm - hzp));
        }
        VERTEX.y = h0;
        NORMAL = v_normal;
        v_h = h0;
        v_wxz = wxz;   // DEBUG: the true-world XZ this vertex sampled the field at
        // RING FIX (2026-06-23): texture at the vertex's ACTUAL world XZ, NOT the morphed field-sample
        // position `wxz`. The geomorph slides `wxz` toward the coarse grid by morphK (a function of camera
        // DISTANCE), but the vertex itself stays at vtrue_fine (only VERTEX.y is morphed). Mapping textures
        // at `wxz` warped the UV by a distance-dependent amount → texture "swims"/shifts within each LOD
        // morph band = concentric rings locked to camera distance that survive every mip/aniso/fade (it's a
        // UV distortion, not aliasing). Texturing at vtrue_fine pins the surface to the real ground.
        v_surf_xz = vtrue_fine;
    } else if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        float e = analytic_spacing;
        float h0 = analytic_h(wxz) - carve_offset(wxz);    // center: reused for height AND the normal
        VERTEX.y = h0;
        float hx = analytic_h(wxz + vec2(e, 0.0)) - carve_offset(wxz + vec2(e, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, e)) - carve_offset(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));   // normalize() makes the gradient magnitude immaterial
        NORMAL = v_normal;
        v_h = VERTEX.y;
        v_surf_xz = wxz;
    } else {
        vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
        VERTEX.y = height_at(uv);
        float t = texel_world / region_size;
        float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
        float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
        v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
        NORMAL = v_normal;   // object-space (== world at identity) -> Godot transforms to view for lighting
        v_h = VERTEX.y;
        v_surf_xz = VERTEX.xz;
    }
}

// Per-texture mean colour = its highest mip (1×1), via a large explicit LOD. Used to FADE per-texel detail
// toward the flat average with distance, so far-field terrain carries no high-contrast speckle (the cause of
// the grazing-angle dot-grid moiré). textureLod with a big lod always lands on the top mip regardless of res.
vec3 tex_mean(sampler2D t) { return textureLod(t, vec2(0.5), 16.0).rgb; }
float texr_mean(sampler2D t) { return textureLod(t, vec2(0.5), 16.0).r; }
// Distance detail-fade amount: 0 near (full per-texel detail) → 1 far (flat mean). Rides the shared
// detail_fade() band so albedo/roughness/normal all dissolve together (no stacked rings).
float detail_dissolve(float dist) { return 1.0 - detail_fade(dist); }

// ---- STOCHASTIC ANTI-TILING (hash rotation + offset, bilinear-blended over 4 world cells) -------------
// The placeholder ground albedos are strongly DIRECTIONAL (vertical grass-stalks / mud streaks); tiled on a
// slope they line up into the "creeping corduroy" streaks. Per-cell random ROTATION decorrelates that grain
// (each cell points a different way); a soft bilinear blend across the 4 nearest cells hides the cell seams;
// textureGrad keeps mips correct under each cell's rotation (no extra blur/alias). Continuous rotation was
// rejected — it swirls within view. Cost: up to 4 fetches/plane (gated; default off until perf-measured).
uniform bool  antitile_on   = false;
uniform float antitile_rot   = 1.0;    // rotation spread (×π); 1 = full ±180° per cell
uniform float antitile_cell  = 3.0;    // cell size in TILE units (world m = ×mat_tiling); bigger = coarser variation
uniform float antitile_blend = 0.28;   // half-width of the cross-cell blend band (smaller = sharper, less ghost)
vec4 hash4(vec2 p) {
    vec4 q = vec4(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)),
                  dot(p, vec2(419.2, 371.9)), dot(p, vec2(57.7, 201.4)));
    return fract(sin(q) * 43758.5453);
}
vec3 stoch_fetch(sampler2D t, vec2 uv) {
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    vec2 g = uv / max(antitile_cell, 0.25);
    vec2 i = floor(g);
    vec2 f = fract(g);
    vec2 w = smoothstep(0.5 - antitile_blend, 0.5 + antitile_blend, f);
    vec3 acc = vec3(0.0); float tw = 0.0;
    for (int yy = 0; yy < 2; yy++) {
        for (int xx = 0; xx < 2; xx++) {
            vec2 cell = i + vec2(float(xx), float(yy));
            vec4 h = hash4(cell);
            float ang = (h.x - 0.5) * 6.2831853 * antitile_rot;
            float s = sin(ang), c = cos(ang);
            mat2 R = mat2(vec2(c, -s), vec2(s, c));
            vec2 uvr = R * uv + h.yz * 17.0;     // rotate + per-cell phase offset
            float bw = (xx == 0 ? 1.0 - w.x : w.x) * (yy == 0 ? 1.0 - w.y : w.y);
            acc += textureGrad(t, uvr, R * dx, R * dy).rgb * bw;
            tw += bw;
        }
    }
    return acc / max(tw, 1e-4);
}
float stoch_fetch_r(sampler2D t, vec2 uv) {
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    vec2 g = uv / max(antitile_cell, 0.25);
    vec2 i = floor(g);
    vec2 f = fract(g);
    vec2 w = smoothstep(0.5 - antitile_blend, 0.5 + antitile_blend, f);
    float acc = 0.0; float tw = 0.0;
    for (int yy = 0; yy < 2; yy++) {
        for (int xx = 0; xx < 2; xx++) {
            vec2 cell = i + vec2(float(xx), float(yy));
            vec4 h = hash4(cell);
            float ang = (h.x - 0.5) * 6.2831853 * antitile_rot;
            float s = sin(ang), c = cos(ang);
            mat2 R = mat2(vec2(c, -s), vec2(s, c));
            vec2 uvr = R * uv + h.yz * 17.0;
            float bw = (xx == 0 ? 1.0 - w.x : w.x) * (yy == 0 ? 1.0 - w.y : w.y);
            acc += textureGrad(t, uvr, R * dx, R * dy).r * bw;
            tw += bw;
        }
    }
    return acc / max(tw, 1e-4);
}
vec3 plane_alb(sampler2D t, vec2 uv) { return antitile_on ? stoch_fetch(t, uv) : texture(t, uv).rgb; }
float plane_rgh(sampler2D t, vec2 uv) { return antitile_on ? stoch_fetch_r(t, uv) : texture(t, uv).r; }

// Triplanar albedo/scalar sample by world position (no UV needed) — kills cliff stretching on steep faces.
// `dissolve` (0..1) blends each plane's sample toward the texture mean so far faces lose their speckle.
vec3 tri_sample(sampler2D t, vec3 wpos, vec3 wn, float scale, float dissolve) {
    vec3 bw = pow(abs(wn), vec3(tri_sharp));
    bw.y *= tri_ybias;   // DEBUG: tri_ybias<1 demotes the top-down plane so slopes use the (un-stretched) side planes
    bw /= max(bw.x + bw.y + bw.z, 1e-4);
    vec3 mean = tex_mean(t);
    vec3 cx = mix(plane_alb(t, wpos.zy / scale), mean, dissolve);
    vec3 cy = mix(plane_alb(t, wpos.xz / scale), mean, dissolve);
    vec3 cz = mix(plane_alb(t, wpos.xy / scale), mean, dissolve);
    return cx * bw.x + cy * bw.y + cz * bw.z;
}
// Scalar (roughness) twin of tri_sample — same world-projection blend, single channel.
float tri_sample_r(sampler2D t, vec3 wpos, vec3 wn, float scale, float dissolve) {
    vec3 bw = pow(abs(wn), vec3(tri_sharp));
    bw.y *= tri_ybias;
    bw /= max(bw.x + bw.y + bw.z, 1e-4);
    float mean = texr_mean(t);
    float cx = mix(plane_rgh(t, wpos.zy / scale), mean, dissolve);
    float cy = mix(plane_rgh(t, wpos.xz / scale), mean, dissolve);
    float cz = mix(plane_rgh(t, wpos.xy / scale), mean, dissolve);
    return cx * bw.x + cy * bw.y + cz * bw.z;
}
// Perturb a world normal by a tangent-space normal map sampled on the dominant (XZ) plane (used by the diag stepper).
vec3 apply_nrm(vec3 geo_n, sampler2D nmap, vec2 uv, float strength) {
    vec3 n = texture(nmap, uv).xyz * 2.0 - 1.0;
    return normalize(geo_n + vec3(n.x, 0.0, n.y) * strength);
}
// Proper tangent-space normal application: build a world-aligned tangent frame from the geometric normal (the
// terrain mesh carries no TANGENT/BINORMAL). For near-flat terrain a world-Z-referenced basis is stable across
// chunks and lights correctly (the old apply_nrm rotated the map's xy straight into world XZ — wrong basis).
// `strength` scales only the tangent deflection; 0 = pure geometric normal.
vec3 apply_nrm_tangent(vec3 geo_n, sampler2D nmap, vec2 uv, float strength) {
    vec3 N = normalize(geo_n);
    vec3 T = cross(vec3(0.0, 0.0, 1.0), N);
    if (!(dot(T, T) > 1e-6)) { T = cross(vec3(1.0, 0.0, 0.0), N); }   // N ~parallel to +Z → different ref axis
    T = normalize(T);
    vec3 B = cross(N, T);
    vec3 ts = texture(nmap, uv).xyz * 2.0 - 1.0;
    ts.xy *= strength;
    return normalize(ts.x * T + ts.y * B + ts.z * N);
}
// Toksvig specular anti-aliasing. The mip-filtered tangent normal SHORTENS (|n| < 1) where sub-pixel normal
// variance is high (i.e. far / grazing). Map that to a gloss factor, then to a roughness FLOOR: low gloss →
// rough → no specular / lit↔shadowed flicker. texture() here uses the same trilinear+aniso mip the lighting
// sees, so it tracks the real footprint → the camera-distance normal-map rings collapse automatically.
float toksvig_gloss(sampler2D nmap, vec2 uv, float k) {
    vec3 ts = texture(nmap, uv).xyz * 2.0 - 1.0;     // mip-filtered → magnitude < 1 under variance
    float len = clamp(length(ts), 1e-3, 1.0);
    return len / mix(1.0, len, clamp(k, 0.0, 1.0));   // k=0 → gloss=1 (off), k=1 → gloss=len (max effect)
}
float toksvig_rough_floor(float gloss) {
    return sqrt(clamp(1.0 - gloss, 0.0, 1.0));         // normal variance → minimum roughness
}

// ---- QUICK SURFACING-DETAIL PROOF (key X, default OFF) ------------------------------------------------
// The I-test proved the bare albedo is nearly flat → almost all "ground definition" is sun+geometry, the
// texture carries ~nothing. This block adds PROCEDURAL meso+fine RELIEF (a detail normal) so the existing
// sun catches micro-shadows → instant definition. It's a PROOF that the missing ingredient is surface
// detail (the surfacing arc), not lighting. Gated, distance-faded, reversible. NOT the ship path — a demo.
uniform bool  detail_demo        = false;
uniform float detail_demo_amp    = 0.7;     // detail-normal perturb strength
uniform float detail_demo_meso   = 0.10;    // meso relief frequency (cycles/m)
uniform float detail_demo_fine   = 0.85;    // fine relief frequency (cycles/m)
uniform float detail_demo_albvar = 0.20;    // albedo break-up amount
uniform float detail_demo_fade   = 350.0;   // metres: detail fully faded out beyond this (anti-alias)
float dd_hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float dd_vnoise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = dd_hash(i), b = dd_hash(i + vec2(1.0, 0.0));
    float c = dd_hash(i + vec2(0.0, 1.0)), d = dd_hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}
float dd_fbm(vec2 p) {
    float s = 0.0, a = 0.5;
    for (int i = 0; i < 4; i++) { s += a * dd_vnoise(p); p *= 2.02; a *= 0.5; }
    return s;
}
float dd_height(vec2 wxz) {   // combined meso+fine relief height at world XZ
    return dd_fbm(wxz * detail_demo_meso) * 0.6 + dd_fbm(wxz * detail_demo_fine) * 0.4;
}
vec3 dd_perturb(vec3 geo_n, vec2 wxz, float strength) {   // tilt normal by the relief gradient (finite diff)
    float e = 0.5;   // metres
    float h  = dd_height(wxz);
    float hx = dd_height(wxz + vec2(e, 0.0));
    float hz = dd_height(wxz + vec2(0.0, e));
    vec3 grad = vec3((hx - h) / e, 0.0, (hz - h) / e);
    return normalize(geo_n - grad * strength);
}

void fragment() {
    if (debug_field > 0.5) {   // erosion-lab false-colour overlay (water/sediment/flow), region-UV sampled
        vec2 ruv = v_surf_xz / region_size + vec2(0.5);
        float val = texture(debug_field_tex, ruv).r;
        // blue→cyan→yellow→red ramp on the scalar (clamped); reads as a heat/flow map.
        vec3 cc = clamp(vec3(val * 2.0 - 1.0, 1.0 - abs(val * 2.0 - 1.0), 1.0 - val * 2.0), 0.0, 1.0);
        ALBEDO = cc; ROUGHNESS = 1.0; NORMAL = v_normal;
    } else if (use_textures) {
        // Minimal surfacing slice. (Godot forbids `return` in fragment(), so the legacy ramp is the else.)
        vec2 uv = v_surf_xz / mat_tiling;
        float h = v_h;
        float slope = 1.0 - clamp(v_normal.y, 0.0, 1.0);   // 0 flat .. 1 vertical

        // height-band weights with fwidth-soft edges (per-pixel boundary → no grid snap).
        float fw = max(fwidth(h), 1.0);
        float w_sand  = 1.0 - smoothstep(surf_h_sand - fw,  surf_h_sand + fw,  h);
        float w_grass = smoothstep(surf_h_sand - fw, surf_h_sand + fw, h) * (1.0 - smoothstep(surf_h_grass - fw, surf_h_grass + fw, h));
        float w_earth = smoothstep(surf_h_grass - fw, surf_h_grass + fw, h) * (1.0 - smoothstep(surf_h_earth - fw, surf_h_earth + fw, h));
        float w_snow  = smoothstep(surf_h_earth - fw, surf_h_earth + fw, h);

        // slope forces rock (mat3): blends OUT the height materials on steep faces.
        float rock = smoothstep(surf_slope_rock - surf_slope_soft, surf_slope_rock + surf_slope_soft, slope);
        float wr = 1.0 - rock;
        float w0 = w_sand * wr, w1 = w_grass * wr, w2 = w_earth * wr, w4 = w_snow * wr;
        float w3 = rock;
        float sum = max(w0 + w1 + w2 + w3 + w4, 1e-4);

        vec3 wpos = vec3(v_surf_xz.x, v_h, v_surf_xz.y);
        // FOOTPRINT DETAIL-FADE: blend every per-texel sample toward its texture MEAN as the texture footprint
        // per screen-pixel grows, so wherever a pixel covers many texels (far AND/OR grazing) the surface goes
        // flat average colour with NO high-contrast speckle. That speckle (stones in mat2, grains in mat0) is
        // what aliased into the fixed-distance "dot-grid"/concentric-rings moiré: it reads as a screen-locked
        // OVERLAY (not sliding with the terrain) because minification moiré's beat frequency is set by the pixel
        // grid, not the surface. Mips+anisotropy can't kill it (contrast too high, 12 m tiling resonates), so we
        // remove the high frequencies at the source. Driving the fade by fwidth(uv) (texels/pixel) — not raw
        // distance — auto-targets BOTH distance and grazing angle, and keeps head-on near ground fully sharp.
        float dist = distance(cam_world, wpos);
        // Footprint = texture tiles per screen-pixel (fwidth of the surface UV). Large where the texture
        // minifies hard — far AND/OR grazing slope — which is exactly where the directional placeholder
        // textures shimmer into the "creeping line that moves with you". Fade detail→mean here.
        vec2 uv_fp = v_surf_xz / mat_tiling;
        float footprint = length(fwidth(uv_fp));
        float dis_fp = footprint_fade ? smoothstep(fp_fade_lo, fp_fade_hi, footprint) * fp_fade_max : 0.0;
        float dis = max(detail_dissolve(dist) * detail_fade_on, dis_fp);    // 0 near sharp .. 1 far/grazing mean
        // rock (mat3) uses triplanar (steep faces); others use planar world-XZ uv (cheaper, they're flat-ish).
        // mat3 (rock) is ALWAYS triplanar. With tri_all (default ON) the other four go triplanar too — this is
        // what removes the slope STREAK. tri_all=0 (A/B) restores the legacy planar `texture(matN, uv)` look.
        vec3 a0, a1, a2, a4; float r0, r1, r2, r4;
        if (tri_all) {
            a0 = tri_sample(mat0_alb, wpos, v_normal, mat_tiling, dis);
            a1 = tri_sample(mat1_alb, wpos, v_normal, mat_tiling, dis);
            a2 = tri_sample(mat2_alb, wpos, v_normal, mat_tiling, dis);
            a4 = tri_sample(mat4_alb, wpos, v_normal, mat_tiling, dis);
            r0 = tri_sample_r(mat0_rgh, wpos, v_normal, mat_tiling, dis);
            r1 = tri_sample_r(mat1_rgh, wpos, v_normal, mat_tiling, dis);
            r2 = tri_sample_r(mat2_rgh, wpos, v_normal, mat_tiling, dis);
            r4 = tri_sample_r(mat4_rgh, wpos, v_normal, mat_tiling, dis);
        } else {
            a0 = mix(texture(mat0_alb, uv).rgb, tex_mean(mat0_alb), dis);
            a1 = mix(texture(mat1_alb, uv).rgb, tex_mean(mat1_alb), dis);
            a2 = mix(texture(mat2_alb, uv).rgb, tex_mean(mat2_alb), dis);
            a4 = mix(texture(mat4_alb, uv).rgb, tex_mean(mat4_alb), dis);
            r0 = mix(texture(mat0_rgh, uv).r, texr_mean(mat0_rgh), dis);
            r1 = mix(texture(mat1_rgh, uv).r, texr_mean(mat1_rgh), dis);
            r2 = mix(texture(mat2_rgh, uv).r, texr_mean(mat2_rgh), dis);
            r4 = mix(texture(mat4_rgh, uv).r, texr_mean(mat4_rgh), dis);
        }
        vec3 a3 = tri_sample(mat3_alb, wpos, v_normal, mat_tiling, dis);
        float r3 = tri_sample_r(mat3_rgh, wpos, v_normal, mat_tiling, dis);
        vec3 alb = (w0 * a0 + w1 * a1 + w2 * a2 + w3 * a3 + w4 * a4) / sum;
        float rgh = (w0 * r0 + w1 * r1 + w2 * r2 + w3 * r3 + w4 * r4) / sum;
        // AMBIENT OCCLUSION (was never bound): sample + blend like roughness, written to AO below for free
        // contact-shadow depth in crevices — the cheapest "definition" win, using ao.png that already ships.
        float ao0 = tri_sample_r(mat0_ao, wpos, v_normal, mat_tiling, dis);
        float ao1 = tri_sample_r(mat1_ao, wpos, v_normal, mat_tiling, dis);
        float ao2 = tri_sample_r(mat2_ao, wpos, v_normal, mat_tiling, dis);
        float ao3 = tri_sample_r(mat3_ao, wpos, v_normal, mat_tiling, dis);
        float ao4 = tri_sample_r(mat4_ao, wpos, v_normal, mat_tiling, dis);
        float ao_blend = clamp((w0 * ao0 + w1 * ao1 + w2 * ao2 + w3 * ao3 + w4 * ao4) / sum, 0.0, 1.0);
        // Normal-map perturbation (restored, for the diag stepper). diag_mode controls which channel is live so
        // we can pin the ring source by adding ONE channel at a time onto the known-clean flat-grey base.
        // Normal-map perturbation, rolled OFF with distance (nfade: 1 near .. 0 far) so far normals collapse to
        // the smooth geometric normal (belt-and-suspenders with Toksvig). Proper tangent-space basis.
        // Normal map ALWAYS rolls off with footprint, decoupled from the albedo footprint_fade toggle: the
        // tiled normal map is what aliases into the grazing/distance MOIRÉ ripples, so fade it there even when
        // albedo stays sharp — we keep ground-colour DEFINITION but the relief flattens out before it moirés.
        float nrm_fp = smoothstep(nrm_fade_lo, nrm_fade_hi, footprint);
        float nfade = dbg_use_normalmap ? (1.0 - max(dis, nrm_fp)) : 0.0;
        vec3 nrm_pert = v_normal;
        // Apply ALL FIVE material normals, each weighted by its own normalized weight (was only mat0+mat1,
        // so rock/snow rendered perfectly smooth). nfade still rolls relief off at grazing/distance to avoid
        // the moiré, but near-ground now gets every material's real relief → the missing 3D "definition".
        nrm_pert = apply_nrm_tangent(nrm_pert, mat0_nrm, uv, w0 / sum * nfade);
        nrm_pert = apply_nrm_tangent(nrm_pert, mat1_nrm, uv, w1 / sum * nfade);
        nrm_pert = apply_nrm_tangent(nrm_pert, mat2_nrm, uv, w2 / sum * nfade);
        nrm_pert = apply_nrm_tangent(nrm_pert, mat3_nrm, uv, w3 / sum * nfade);
        nrm_pert = apply_nrm_tangent(nrm_pert, mat4_nrm, uv, w4 / sum * nfade);

        // TOKSVIG roughness floor from the ground normal maps (mat0/mat1), weighted like the perturbation.
        // Raises roughness where sub-pixel normal variance is high → kills the normal-map specular/shading rings.
        float tk = toksvig_rough_floor(toksvig_gloss(mat1_nrm, uv, toksvig_k)) * ((w1 + w2) / sum)
                 + toksvig_rough_floor(toksvig_gloss(mat0_nrm, uv, toksvig_k)) * (w0 / sum);
        float rgh_aa = max(clamp(rgh, 0.04, 1.0), tk);

        // QUICK PROOF (key X): procedural relief so the sun reveals surface definition. Distance-faded so it
        // doesn't alias far out. Modifies alb + nrm_pert in place → visible in dm==0 (normal render).
        if (detail_demo) {
            float ddfade = 1.0 - smoothstep(detail_demo_fade * 0.4, detail_demo_fade, dist);
            if (ddfade > 0.001) {
                nrm_pert = dd_perturb(normalize(nrm_pert), wpos.xz, detail_demo_amp * ddfade);
                float av = dd_height(wpos.xz) - 0.5;
                alb *= (1.0 + av * detail_demo_albvar * ddfade);
            }
        }

        // diag_mode: 0 = normal render (all channels). 1 = flat grey base (no textures). 2 = +albedo only.
        // 3 = +roughness only. 4 = +normal-map only. Each builds on the grey base so exactly one channel varies.
        // Each mode tinted a DISTINCT colour so you can identify it without counting H presses:
        // 0 = normal (untinted), 1 = RED grey, 2 = GREEN +albedo, 3 = BLUE +roughness, 4 = YELLOW +normalmap.
        int dm = int(diag_mode + 0.5);
        if (dm == 0) {
            // LIVE render (the shipping look): faded albedo + Toksvig roughness + tangent normal rolled off far.
            ALBEDO = water_debug_tint(alb, v_surf_xz); ROUGHNESS = rgh_aa; NORMAL = normalize(nrm_pert); AO = ao_blend; AO_LIGHT_AFFECT = 0.0;
        } else if (dm == 1) {
            ALBEDO = vec3(0.40, 0.12, 0.12); ROUGHNESS = 1.0; NORMAL = normalize(v_normal);    // RED: clean base (no textures)
        } else if (dm == 2) {
            ALBEDO = alb * vec3(0.5, 1.0, 0.5); ROUGHNESS = 1.0; NORMAL = normalize(v_normal); // GREEN: +albedo only
        } else if (dm == 3) {
            ALBEDO = vec3(0.12, 0.12, 0.40); ROUGHNESS = clamp(rgh, 0.04, 1.0); NORMAL = normalize(v_normal); // BLUE: +roughness only
        } else {
            // YELLOW: +normal-map, at a glossy base floored by Toksvig — proves the AA kills the normal rings.
            ALBEDO = vec3(0.45, 0.42, 0.10); ROUGHNESS = max(0.4, tk); NORMAL = normalize(nrm_pert);
        }
    } else {
        vec3 c = mix(col_low, col_mid, smoothstep(h_low, h_mid, v_h));
        c = mix(c, col_high, smoothstep(h_mid, h_high, v_h));
        c = mix(c, col_peak, smoothstep(h_high, h_peak, v_h));
        float slope = 1.0 - v_normal.y;                       // 0 flat .. 1 vertical
        c = mix(c, col_cliff, smoothstep(cliff_lo, cliff_hi, slope));   // steep faces -> rock
        if (lod_viz >= 0.0) {   // S2a debug: distinct hue per LOD level so the bands are visible
            float L = lod_viz;
            vec3 tint = vec3(fract(L * 0.27), fract(L * 0.53 + 0.33), fract(L * 0.81 + 0.66));
            c = mix(c, 0.5 + 0.5 * tint, 0.9);   // strong tint so the LOD bands are unmistakable (was 0.55)
        }
        if (debug_wxz > 0.5) {   // DEBUG: encode the sampled true-world XZ — must be IDENTICAL across a snap
            // FINE band (mod 16 m) makes a sub-meter reconstruction shift visible; coarse band (mod 4096) shows
            // any large offset. R = fine X phase, G = fine Z phase, B = coarse X phase.
            c = vec3(fract(v_wxz.x / 16.0), fract(v_wxz.y / 16.0), fract(v_wxz.x / 4096.0));
        }
        ALBEDO = water_debug_tint(c, v_surf_xz);
        ROUGHNESS = ground_rough;
    }

    if (dbg_fullrough) {
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }
    if (dbg_unlit) {
        vec3 unlit = ALBEDO;
        ALBEDO = vec3(0.0);
        EMISSION = unlit;
        AO = 1.0;
        AO_LIGHT_AFFECT = 0.0;
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }
}

// Custom per-light diffuse for the TERRAIN ONLY. The harsh "shading swings as you turn" is the directional
// sun's N·L self-shading (lit faces bright, shadowed faces near-black). A wrap/half-Lambert term lifts the
// shadowed side and softens the terminator so the swing is gentle, while AO + normal maps keep micro-detail.
// Runs per directional light (sun/moon/extra suns), respects ATTENUATION (cast shadows) + LIGHT_COLOR. The
// engine's ambient/IBL pass still fills separately. Diffuse-only on purpose: ground is matte, and dropping
// the GGX lobe also removes the view-dependent specular sheen. ALBEDO=0 in dbg_unlit → contributes nothing.
void light() {
    vec3 N = normalize(NORMAL);
    vec3 L = normalize(LIGHT);
    float ndl = dot(N, L);
    float lambert = max(ndl, 0.0);
    float half_l = ndl * 0.5 + 0.5;
    half_l *= half_l;                       // squared half-Lambert: soft, never fully black
    float w = ground_soft_light ? clamp(ground_wrap, 0.0, 1.0) : 0.0;
    float diff = mix(lambert, half_l, w);
    DIFFUSE_LIGHT += ALBEDO * LIGHT_COLOR * ATTENUATION * diff;
}
