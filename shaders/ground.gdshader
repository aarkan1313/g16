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

void fragment() {
    // Task 0: flat tint so the swap is visible but unmistakably "new path". Shape comes from the
    // re-hosted vertex displacement above. Real material (placement -> sample -> blend) in Tasks 2-4.
    v_ao = 1.0;
    NORMAL = v_normal;
    ALBEDO = vec3(0.45, 0.40, 0.35);
    ROUGHNESS = 0.9;
}
