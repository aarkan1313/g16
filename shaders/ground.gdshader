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
uniform float detail_fade_near = 400.0;
uniform float detail_fade_far  = 1600.0;
float detail_fade(float dist) { return 1.0 - smoothstep(detail_fade_near, detail_fade_far, dist); }

varying vec3 v_normal;   // world-space geometric normal from neighbour height taps
varying float v_h;       // height (m)

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

        float h0 = analytic_h(wxz);

        // S2d: NO SKIRT. Edge-stitched mesh variants (CdlodMesh.BuildStitchedVariants) collapse a chunk's
        // odd boundary verts onto the coarse neighbor's spacing where edges meet a coarser LOD, so seams
        // coincide vertex-for-vertex — cracks are prevented structurally, not by a dropped skirt ring (and
        // its grazing-angle squares are gone). Proven by --stitchcheck (boundary verts coincident to ~0 m).
        VERTEX.y = h0;

        // Normal from forward differences scaled to the chunk's ACTUAL vertex spacing (S2a review fix:
        // was analytic_spacing=4 m but coarse-chunk verts are 16 m+ apart). Sample at the morphed XZ.
        float vs = chunk_size / max(grid_n - 1.0, 1.0);    // chunk vertex spacing (m)
        float hx = analytic_h(wxz + vec2(vs, 0.0));
        float hz = analytic_h(wxz + vec2(0.0, vs));
        v_normal = normalize(vec3(h0 - hx, vs, h0 - hz));
        NORMAL = v_normal;
        v_h = h0;
    } else if (use_analytic) {
        vec2 wxz = VERTEX.xz;                              // object==world at identity (matches field)
        float e = analytic_spacing;
        float h0 = analytic_h(wxz);                        // center: reused for height AND the normal
        VERTEX.y = h0;
        float hx = analytic_h(wxz + vec2(e, 0.0));         // forward diff (2 taps, not 4): 5 evals -> 3
        float hz = analytic_h(wxz + vec2(0.0, e));
        v_normal = normalize(vec3(h0 - hx, e, h0 - hz));   // normalize() makes the gradient magnitude immaterial
        NORMAL = v_normal;
        v_h = VERTEX.y;
    } else {
        vec2 uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
        VERTEX.y = height_at(uv);
        float t = texel_world / region_size;
        float hl = height_at(uv - vec2(t, 0.0)), hr = height_at(uv + vec2(t, 0.0));
        float hd = height_at(uv - vec2(0.0, t)), hu = height_at(uv + vec2(0.0, t));
        v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
        NORMAL = v_normal;   // object-space (== world at identity) -> Godot transforms to view for lighting
        v_h = VERTEX.y;
    }
}

void fragment() {
    vec3 c = mix(col_low, col_mid, smoothstep(h_low, h_mid, v_h));
    c = mix(c, col_high, smoothstep(h_mid, h_high, v_h));
    c = mix(c, col_peak, smoothstep(h_high, h_peak, v_h));
    float slope = 1.0 - v_normal.y;                       // 0 flat .. 1 vertical
    c = mix(c, col_cliff, smoothstep(cliff_lo, cliff_hi, slope));   // steep faces -> rock
    if (lod_viz >= 0.0) {   // S2a debug: distinct hue per LOD level so the bands are visible
        float L = lod_viz;
        vec3 tint = vec3(fract(L * 0.27), fract(L * 0.53 + 0.33), fract(L * 0.81 + 0.66));
        c = mix(c, 0.5 + 0.5 * tint, 0.55);
    }
    ALBEDO = c;
    ROUGHNESS = ground_rough;
}
