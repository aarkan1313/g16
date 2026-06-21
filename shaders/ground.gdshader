shader_type spatial;
// GROUND v2 — per-pixel procedural material skin (2026-06-21 reset). Built ALONGSIDE
// terrain_lab.gdshader; swapped in via TerrainLab.SetGroundV2 for A/B. Units land here in order:
//   0 scaffold · 1 arrays · 2 placement · 3 sampling+anti-tile · 4 blend.
// See docs/superpowers/plans/2026-06-21-ground-material-system-core.md.
//
// NOTE: NO render_mode — terrain_lab.gdshader has none, so VERTEX is object space and the
// TerrainLab node is at identity (origin), making object space == world space. Mirrored here
// EXACTLY (re-host, don't re-derive) so the A/B compares only the material skin, not the geometry.

uniform sampler2D heightmap : repeat_disable, filter_linear;
uniform float region_size = 8192.0;
uniform float texel_world = 4.0;

varying vec2  v_uv;
varying vec3  v_world;
varying vec3  v_normal;   // world-space geometric normal from neighbour height taps
varying float v_h;        // height (m)
varying float v_slope;    // 0 flat .. 1 vertical
varying float v_curv;     // local curvature: concave/hollow > 0, convex/ridge < 0 (meters)
varying float v_ao;       // material AO carried fragment -> light() for the DIRECT term

float height_at(vec2 uv) { return texture(heightmap, uv).r; }

void vertex() {
    // Re-host of terrain_lab.gdshader vertex() (the proven displacement + field derivation).
    // The mesh is a flat PlaneMesh; the heightfield is applied here, so ground v2 must displace
    // it too or the terrain would render flat.
    v_uv = (VERTEX.xz + vec2(region_size * 0.5)) / region_size;
    VERTEX.y = height_at(v_uv);
    v_world = VERTEX;
    float t = texel_world / region_size;
    float hl = height_at(v_uv - vec2(t, 0)), hr = height_at(v_uv + vec2(t, 0));
    float hd = height_at(v_uv - vec2(0, t)), hu = height_at(v_uv + vec2(0, t));
    v_normal = normalize(vec3(hl - hr, 2.0 * texel_world, hd - hu));
    NORMAL = v_normal;
    v_h = VERTEX.y;
    v_slope = 1.0 - v_normal.y;
    v_curv = (hl + hr + hd + hu) * 0.25 - VERTEX.y;
}

// ============================================================================
// UNIT 2 — per-pixel procedural PLACEMENT (the blockiness killer).
// Material weights are evaluated PER FRAGMENT from the world-space terrain fields
// (height / slope) + domain-warped world-position noise. There is NO baked splat,
// so boundaries are as fine as the screen — no 4 m grid to snap to. All knobs are
// data-driven uniforms (data/ground_materials.json 'placement' block), not magic numbers.
// ============================================================================

uniform int   mat_count = 0;
uniform vec4  rule_bands[32];   // per material: xy=hmin/hmax (m), zw=slopemin/slopemax (0-1)
uniform int   gv2_debug = 0;    // 0 off, 1 placement-viz (flat per-material color)

// Placement tunables (pushed from the manifest by AttachGroundArrays).
uniform float p_band_soft_m = 60.0;   // height-band edge softness (m)
uniform float p_slope_soft  = 0.08;   // slope-band edge softness (0-1)
uniform float p_warp_m      = 120.0;  // domain-warp wavelength (m)
uniform float p_warp_amp_m  = 80.0;   // domain-warp amplitude (m)
uniform float p_patch_m     = 40.0;   // within-area variation patch wavelength (m)
uniform float p_noise_gain  = 0.8;    // how strongly patch noise modulates each weight (0 = uniform)

// Re-host of terrain_lab.gdshader hash2 + vnoise (identical character to the approved ground path).
float hash2(vec2 p){ return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 w, float wl){ vec2 p = w / max(wl, 1e-3); vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash2(i), hash2(i + vec2(1, 0)), f.x), mix(hash2(i + vec2(0, 1)), hash2(i + vec2(1, 1)), f.x), f.y); }

// Per-material placement weight from its rule (height/slope band) + warped patch noise.
// Soft band edges (smoothstep) keep boundaries gradual; the warped noise IS the within-area
// variation (drier/wetter patches) so no separate variation unit is needed.
float rule_weight(int i, float h, float slope, vec2 wxz){
    vec4 b = rule_bands[i];
    float hw = smoothstep(b.x - p_band_soft_m, b.x + p_band_soft_m, h)
             * (1.0 - smoothstep(b.y - p_band_soft_m, b.y + p_band_soft_m, h));
    float sw = smoothstep(b.z - p_slope_soft, b.z + p_slope_soft, slope)
             * (1.0 - smoothstep(b.w - p_slope_soft, b.w + p_slope_soft, slope));
    vec2 warp = vec2(vnoise(wxz + vec2(31.0, 17.0), p_warp_m),
                     vnoise(wxz + vec2(7.0, 91.0), p_warp_m)) - 0.5;
    float n = vnoise(wxz + warp * p_warp_amp_m + float(i) * 53.0, p_patch_m);  // i offset = decorrelate materials
    float patch = mix(1.0, 2.0 * n, p_noise_gain);   // n mean 0.5 -> 2n mean 1.0; gain scales the variation
    return max(hw * sw * patch, 0.0);
}

// Top-4 materials by placement weight at this fragment (descending), weights normalized to sum 1.
void placement(out ivec4 idx, out vec4 w){
    idx = ivec4(0); w = vec4(0.0);
    for(int i = 0; i < mat_count && i < 32; i++){
        float wi = rule_weight(i, v_h, v_slope, v_world.xz);
        if(wi > w.x){ w = vec4(wi, w.x, w.y, w.z); idx = ivec4(i, idx.x, idx.y, idx.z); }
        else if(wi > w.y){ w = vec4(w.x, wi, w.y, w.z); idx = ivec4(idx.x, i, idx.y, idx.z); }
        else if(wi > w.z){ w = vec4(w.x, w.y, wi, w.z); idx = ivec4(idx.x, idx.y, i, idx.z); }
        else if(wi > w.w){ w = vec4(w.x, w.y, w.z, wi); idx = ivec4(idx.x, idx.y, idx.z, i); }
    }
    float s = w.x + w.y + w.z + w.w;
    w = (s > 1e-5) ? w / s : vec4(1.0, 0.0, 0.0, 0.0);
}

vec3 dbg_mat_color(int i){
    return 0.5 + 0.5 * vec3(hash2(vec2(float(i), 1.0)), hash2(vec2(float(i), 2.0)), hash2(vec2(float(i), 3.0)));
}

// ============================================================================
// UNIT 3 — SAMPLING + ANTI-TILING. Each top material's array slot is sampled through the
// re-hosted histogram-preserving stochastic bombing (albedo) + branched triplanar. Mip/aniso
// filtering is asserted on the array uniforms (the documented "fuzz = no mipmaps" gotcha).
// ============================================================================

uniform sampler2DArray albedo_arr : source_color, filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray normal_arr : filter_linear_mipmap_anisotropic, repeat_enable;
uniform sampler2DArray orm_arr    : filter_linear_mipmap_anisotropic, repeat_enable;  // R=AO G=rough B=metal
uniform sampler2DArray height_arr : filter_linear_mipmap_anisotropic, repeat_enable;  // Rf (in .r)
uniform float tex_scale_m   = 11.0;   // world meters per texture tile (bound by AttachGroundArrays)
uniform bool  ar_on         = true;   // master toggle for the stochastic anti-tiling bombing
uniform float tri_sharpness = 8.0;    // triplanar blend exponent
uniform float p_rough_floor  = 0.15;  // min roughness (tunable; keeps the matte ground from going glossy)
uniform float p_nrm_strength = 0.7;   // normal-map perturbation strength (world XZ tilt)

const float TRI_EPS = 0.004;   // branched-triplanar skip threshold (a plane below this is ~free to skip)

// tri-grid + hash helpers re-hosted from terrain_lab.gdshader (shared by the bombing).
vec2 hash2v(vec2 p){ mat2 m = mat2(vec2(127.1, 269.5), vec2(311.7, 183.3)); return fract(sin(m * p) * 43758.5453); }
void tri_grid(vec2 st, out float w1, out float w2, out float w3, out vec2 v1, out vec2 v2, out vec2 v3){
    const mat2 skew = mat2(vec2(1.0, 0.0), vec2(-0.57735027, 1.15470054));
    const mat2 unsk = mat2(vec2(1.0, 0.0), vec2(0.5, 0.86602540));
    vec2 p = skew * st; vec2 i = floor(p); vec2 f = fract(p); float tmp = f.x + f.y;
    if (tmp < 1.0){ v1 = i; w1 = 1.0 - tmp; v2 = i + vec2(1, 0); w2 = f.x; v3 = i + vec2(0, 1); w3 = f.y; }
    else          { v1 = i + vec2(1, 1); w1 = tmp - 1.0; v2 = i + vec2(1, 0); w2 = 1.0 - f.y; v3 = i + vec2(0, 1); w3 = 1.0 - f.x; }
    v1 = unsk * v1; v2 = unsk * v2; v3 = unsk * v3;
}
vec3 tri_w(vec3 nr){ vec3 bw = pow(abs(nr), vec3(tri_sharpness)); return bw / (bw.x + bw.y + bw.z); }

// Histogram-preserving stochastic bombing for one ARRAY LAYER. Translation-only 3 taps on a
// triangle grid + variance-restore blend (inversesqrt(dot(w,w)) keeps contrast). Re-host of
// histo_sample_wp. SIMPLIFICATION (per plan): no per-slot inverse-histogram LUT yet (the variance
// restore preserves contrast on its own; LUT is a refinement if a material washes) and no distance
// LOD (always 3-tap; cam_world isn't wired to the v2 material — a perf follow-up).
vec4 ar_arr(sampler2DArray tex, vec2 uv, float layer, vec3 wp){
    vec2 dx = dFdx(uv), dy = dFdy(uv);
    vec4 plain = textureGrad(tex, vec3(uv, layer), dx, dy);
    if (!ar_on) return plain;
    float w1, w2, w3; vec2 v1, v2, v3; tri_grid(uv, w1, w2, w3, v1, v2, v3);
    vec4 t1 = textureGrad(tex, vec3(uv + hash2v(v1), layer), dx, dy);
    vec4 t2 = textureGrad(tex, vec3(uv + hash2v(v2), layer), dx, dy);
    vec4 t3 = textureGrad(tex, vec3(uv + hash2v(v3), layer), dx, dy);
    vec3 w = vec3(w1, w2, w3); w *= w; w /= max(w.x + w.y + w.z, 1e-5);
    vec4 mean = (t1 + t2 + t3) / 3.0;
    vec4 blend = t1 * w.x + t2 * w.y + t3 * w.z;
    float vc = inversesqrt(max(dot(w, w), 1e-5));
    return mean + (blend - mean) * vc;
}
vec4 tap_arr(sampler2DArray tex, vec2 uv, float layer){ return textureGrad(tex, vec3(uv, layer), dFdx(uv), dFdy(uv)); }

// Branched triplanar per array slot. Albedo through the bombing; normal/orm/height plain single
// tap (proven call: bombing flattens normals + their tiling is imperceptible at terrain range).
vec3 tp_alb_arr(int i, vec3 wp, vec3 nr){ vec3 bw = tri_w(nr); float L = float(i); vec3 c = vec3(0.0);
    if (bw.x > TRI_EPS) c += ar_arr(albedo_arr, wp.zy / tex_scale_m, L, wp).rgb * bw.x;
    if (bw.y > TRI_EPS) c += ar_arr(albedo_arr, wp.xz / tex_scale_m, L, wp).rgb * bw.y;
    if (bw.z > TRI_EPS) c += ar_arr(albedo_arr, wp.xy / tex_scale_m, L, wp).rgb * bw.z; return c; }
vec3 tp_nrm_arr(int i, vec3 wp, vec3 nr){ vec3 bw = tri_w(nr); float L = float(i); vec3 c = vec3(0.0);
    if (bw.x > TRI_EPS) c += tap_arr(normal_arr, wp.zy / tex_scale_m, L).rgb * bw.x;
    if (bw.y > TRI_EPS) c += tap_arr(normal_arr, wp.xz / tex_scale_m, L).rgb * bw.y;
    if (bw.z > TRI_EPS) c += tap_arr(normal_arr, wp.xy / tex_scale_m, L).rgb * bw.z; return c; }
vec3 tp_orm_arr(int i, vec3 wp, vec3 nr){ vec3 bw = tri_w(nr); float L = float(i); vec3 c = vec3(0.0);
    if (bw.x > TRI_EPS) c += tap_arr(orm_arr, wp.zy / tex_scale_m, L).rgb * bw.x;
    if (bw.y > TRI_EPS) c += tap_arr(orm_arr, wp.xz / tex_scale_m, L).rgb * bw.y;
    if (bw.z > TRI_EPS) c += tap_arr(orm_arr, wp.xy / tex_scale_m, L).rgb * bw.z; return c; }
float tp_hgt_arr(int i, vec3 wp, vec3 nr){ vec3 bw = tri_w(nr); float L = float(i); float h = 0.0;
    if (bw.x > TRI_EPS) h += tap_arr(height_arr, wp.zy / tex_scale_m, L).r * bw.x;
    if (bw.y > TRI_EPS) h += tap_arr(height_arr, wp.xz / tex_scale_m, L).r * bw.y;
    if (bw.z > TRI_EPS) h += tap_arr(height_arr, wp.xy / tex_scale_m, L).r * bw.z; return h; }

// Geometric world normal perturbed by a tangent-space normal-map xy (world XZ tilt) — the proven
// terrain_lab.gdshader approach (NO NORMAL_MAP/TBN; triplanar tangents diverge). Returns a WORLD
// normal; fragment() does the VIEW_MATRIX transform (built-ins aren't visible in helper functions).
vec3 perturb_normal_world(vec3 nr, vec3 nrm01){
    vec3 nrm = nrm01 * 2.0 - 1.0;
    return normalize(nr + vec3(nrm.x, 0.0, nrm.y) * p_nrm_strength);
}

void fragment() {
    ivec4 idx; vec4 w; placement(idx, w);
    vec3 nr = normalize(v_normal);
    // NOTE: no 'return' in fragment() — Godot forbids it; branch by writing different values.
    if (gv2_debug == 1) {                       // placement viz: dominant material flat color
        ALBEDO = dbg_mat_color(idx.x);
        ROUGHNESS = 1.0; AO = 1.0; v_ao = 1.0;
        NORMAL = normalize((VIEW_MATRIX * vec4(nr, 0.0)).xyz);
    } else {
        // Task 3: dominant material, REAL array textures through anti-tiling + triplanar.
        // (Top-4 height-aware blend lands in Task 4 — hard material edges are expected here.)
        int i = idx.x;
        vec3 orm = tp_orm_arr(i, v_world, nr);
        ALBEDO = tp_alb_arr(i, v_world, nr);
        ROUGHNESS = clamp(orm.g, p_rough_floor, 1.0);
        AO = orm.r; AO_LIGHT_AFFECT = 1.0; v_ao = orm.r;
        vec3 wn = perturb_normal_world(nr, tp_nrm_arr(i, v_world, nr));
        NORMAL = normalize((VIEW_MATRIX * vec4(wn, 0.0)).xyz);
    }
}
