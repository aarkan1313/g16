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

// Terrain heightfield horizon shadow. This is the only planned ground-shadow owner in the review baseline:
// a world-locked macro-height march toward the sun that attenuates direct light through AO_LIGHT_AFFECT.
// It is default-off, independent of CDLOD chunk residency/LOD, and does not enable any engine shadow maps.
uniform bool  hz_on = false;
uniform vec3  sun_dir_to = vec3(0.0, 1.0, 0.0);
uniform float hz_steps    : hint_range(1.0, 32.0) = 12.0;     // Slice 1: 12-step march (was 1=no-op; 32 was too costly)
uniform float hz_maxdist  : hint_range(500.0, 16000.0) = 12000.0;  // march reach toward the sun (tall distant occluders)
uniform float hz_stride0  : hint_range(4.0, 200.0) = 30.0;
uniform float hz_growth   : hint_range(1.0, 2.0) = 1.45;
uniform float hz_softness : hint_range(0.005, 0.30) = 0.20;
uniform float hz_strength : hint_range(0.0, 1.0) = 0.70;      // clearly visible for the eye-gate (dial down to taste)
uniform float hz_sun_gate : hint_range(0.05, 1.0) = 0.50;     // active up to sun elevation sin ~0.5 (covers low+mid sun)
uniform float hz_full_dist : hint_range(250.0, 12000.0) = 8000.0;  // CONSTANT strength to 8km (world-locked, not a camera-distance ramp)
uniform float hz_fade_dist : hint_range(500.0, 24000.0) = 11000.0; // fade 8-11km then cull (also limits marching pixels)


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
uniform float detail_fade_on = 0.0;   // live A/B (key G): 1 = anti-moiré detail-fade ON, 0 = OFF (default, raw detail)

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
// Erosion-lab debug overlay: false-colour a scalar field (water/sediment/flow) by region UV. 0 = off.
// Zero-impact on the terrain (default off); only the erosion lab sets it.
uniform float debug_field = 0.0;
uniform sampler2D debug_field_tex : filter_linear;
uniform float debug_wxz = 0.0;   // DEBUG: 1.0 = output reconstructed world XZ (mod 512) as R/G color
uniform float diag_mode = 0.0;   // RING HUNT: 0 normal, 1 grey base, 2 +albedo, 3 +roughness, 4 +normalmap (key H steps)
uniform bool dbg_use_normalmap = true;
uniform bool dbg_fullrough = false;
uniform bool dbg_unlit = false;
uniform bool dbg_hz_mask = false;   // DIAGNOSTIC (default off): paint the raw horizon-shadow scalar as
                                    // view-independent emission (white = fully shadowed). A stationary yaw
                                    // test must leave it unchanged — proof the mask is world-anchored.

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

// Triplanar albedo/scalar sample by world position (no UV needed) — kills cliff stretching on steep faces.
// `dissolve` (0..1) blends each plane's sample toward the texture mean so far faces lose their speckle.
vec3 tri_sample(sampler2D t, vec3 wpos, vec3 wn, float scale, float dissolve) {
    vec3 bw = pow(abs(wn), vec3(4.0));
    bw /= max(bw.x + bw.y + bw.z, 1e-4);
    vec3 mean = tex_mean(t);
    vec3 cx = mix(texture(t, wpos.zy / scale).rgb, mean, dissolve);
    vec3 cy = mix(texture(t, wpos.xz / scale).rgb, mean, dissolve);
    vec3 cz = mix(texture(t, wpos.xy / scale).rgb, mean, dissolve);
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

float horizon_shadow(vec2 surf_xz, float surf_h, vec3 sdir) {
    float sun_up = sdir.y;
    if (sun_up <= 0.02 || sun_up >= hz_sun_gate) { return 1.0; }

    float cam_dist = distance(cam_world.xz, surf_xz);
    if (cam_dist >= hz_fade_dist) { return 1.0; }
    float dist_weight = 1.0 - smoothstep(hz_full_dist, hz_fade_dist, cam_dist);

    vec2 hdir = sdir.xz;
    float hlen = length(hdir);
    if (hlen < 1e-4) { return 1.0; }
    hdir /= hlen;

    float tan_sun = sun_up / hlen;
    float max_angle = -1e9;
    int count = int(hz_steps);
    float denom = max(float(count - 1), 1.0);

    for (int i = 0; i < count; i++) {
        float t = count <= 1 ? 0.5 : float(i) / denom;
        float dist = mix(hz_stride0, hz_maxdist, pow(t, hz_growth));
        if (dist > hz_maxdist) { break; }
        // Sample the BAKED region heightmap (one cheap texture fetch) instead of EVALUATING the procedural
        // field (continent+uplift FBM ~27ms/12 steps — the perf collapse). The region covers world XZ in
        // [-region/2, region/2]; outside it clamps (repeat_disable) so far-from-origin shadows fade to flat
        // (acceptable for the near-origin eye-gate; the roaming clipmap is the infinite-world follow-up).
        vec2 suv = (surf_xz + hdir * dist + vec2(region_size * 0.5)) / region_size;
        float h = texture(heightmap, suv).r;
        max_angle = max(max_angle, (h - surf_h) / dist);
    }

    float occ = smoothstep(0.0, hz_softness, max_angle - tan_sun);
    // Smooth the high-sun boundary: fade the shadow OUT as the sun climbs toward hz_sun_gate (instead of the
    // hard early-out popping) so a moving sun doesn't snap shadows on/off across the gate.
    float sun_fade = 1.0 - smoothstep(hz_sun_gate * 0.6, hz_sun_gate, sun_up);
    return clamp(1.0 - occ * hz_strength * dist_weight * sun_fade, 0.0, 1.0);
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
        float dis = detail_dissolve(dist) * detail_fade_on;    // 0 near .. 1 far (gated by the live G toggle)
        // rock (mat3) uses triplanar (steep faces); others use planar world-XZ uv (cheaper, they're flat-ish).
        vec3 a0 = mix(texture(mat0_alb, uv).rgb, tex_mean(mat0_alb), dis);
        vec3 a1 = mix(texture(mat1_alb, uv).rgb, tex_mean(mat1_alb), dis);
        vec3 a2 = mix(texture(mat2_alb, uv).rgb, tex_mean(mat2_alb), dis);
        vec3 a3 = tri_sample(mat3_alb, wpos, v_normal, mat_tiling, dis);
        vec3 a4 = mix(texture(mat4_alb, uv).rgb, tex_mean(mat4_alb), dis);
        vec3 alb = (w0 * a0 + w1 * a1 + w2 * a2 + w3 * a3 + w4 * a4) / sum;
        float r0 = mix(texture(mat0_rgh, uv).r, texr_mean(mat0_rgh), dis);
        float r1 = mix(texture(mat1_rgh, uv).r, texr_mean(mat1_rgh), dis);
        float r2 = mix(texture(mat2_rgh, uv).r, texr_mean(mat2_rgh), dis);
        float r3 = mix(texture(mat3_rgh, uv).r, texr_mean(mat3_rgh), dis);
        float r4 = mix(texture(mat4_rgh, uv).r, texr_mean(mat4_rgh), dis);
        float rgh = (w0 * r0 + w1 * r1 + w2 * r2 + w3 * r3 + w4 * r4) / sum;
        // Normal-map perturbation (restored, for the diag stepper). diag_mode controls which channel is live so
        // we can pin the ring source by adding ONE channel at a time onto the known-clean flat-grey base.
        // Normal-map perturbation, rolled OFF with distance (nfade: 1 near .. 0 far) so far normals collapse to
        // the smooth geometric normal (belt-and-suspenders with Toksvig). Proper tangent-space basis.
        float nfade = dbg_use_normalmap ? (1.0 - dis) : 0.0;
        vec3 nrm_pert = v_normal;
        nrm_pert = apply_nrm_tangent(nrm_pert, mat1_nrm, uv, (w1 + w2) / sum * nfade);
        nrm_pert = apply_nrm_tangent(nrm_pert, mat0_nrm, uv, w0 / sum * nfade);

        // TOKSVIG roughness floor from the ground normal maps (mat0/mat1), weighted like the perturbation.
        // Raises roughness where sub-pixel normal variance is high → kills the normal-map specular/shading rings.
        float tk = toksvig_rough_floor(toksvig_gloss(mat1_nrm, uv, toksvig_k)) * ((w1 + w2) / sum)
                 + toksvig_rough_floor(toksvig_gloss(mat0_nrm, uv, toksvig_k)) * (w0 / sum);
        float rgh_aa = max(clamp(rgh, 0.04, 1.0), tk);

        // diag_mode: 0 = normal render (all channels). 1 = flat grey base (no textures). 2 = +albedo only.
        // 3 = +roughness only. 4 = +normal-map only. Each builds on the grey base so exactly one channel varies.
        // Each mode tinted a DISTINCT colour so you can identify it without counting H presses:
        // 0 = normal (untinted), 1 = RED grey, 2 = GREEN +albedo, 3 = BLUE +roughness, 4 = YELLOW +normalmap.
        int dm = int(diag_mode + 0.5);
        if (dm == 0) {
            // LIVE render (the shipping look): faded albedo + Toksvig roughness + tangent normal rolled off far.
            ALBEDO = water_debug_tint(alb, v_surf_xz); ROUGHNESS = rgh_aa; NORMAL = normalize(nrm_pert);
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

    if (hz_on) {
        // Only march where the surface faces the sun. Back-facing pixels are already shadowed by N·L, so
        // marching them is wasted work (the field march is the dominant per-pixel cost). Big perf win at low sun.
        if (dot(v_normal, normalize(sun_dir_to)) > 0.0) {
            AO = horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to));
            AO_LIGHT_AFFECT = 1.0;
        }
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
    if (dbg_hz_mask) {
        // Stationary yaw test: this scalar depends ONLY on world surface XZ/height, the world sun direction,
        // and camera POSITION — never camera orientation. It must NOT change as you turn. If it does, the
        // horizon math has a hidden view dependency; if it's stable, any visible "shadow shift" is lighting.
        float hz_dbg = horizon_shadow(v_surf_xz, v_h, normalize(sun_dir_to));
        ALBEDO = vec3(0.0);
        EMISSION = vec3(1.0 - hz_dbg);
        AO = 1.0;
        AO_LIGHT_AFFECT = 0.0;
        ROUGHNESS = 1.0;
        SPECULAR = 0.0;
    }
}
