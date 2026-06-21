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

void fragment() {
    v_ao = 1.0;
    NORMAL = v_normal;
    ivec4 idx; vec4 w; placement(idx, w);
    // Task 2: dominant-material flat color on BOTH paths (gv2_debug just forces matte). Tasks 3-4
    // replace the non-debug branch with real array sampling + height-aware blend.
    // NOTE: no 'return' in fragment() — Godot forbids it; branch by writing different values.
    ALBEDO = dbg_mat_color(idx.x);
    ROUGHNESS = (gv2_debug == 1) ? 1.0 : 0.9;
}
