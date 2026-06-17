shader_type fog;

// Gap-aligned god rays (Stage: cloud presence). A FogVolume box over the scene whose
// fog DENSITY is gated by the cloud-shadow map (same map the terrain light() uses):
// where the sun reaches the ground (cloud GAPS) fog is present and scatters sunlight
// into visible shafts; under cloud cover fog is suppressed. So the volumetric light
// shafts line up with the actual holes in the clouds. Godot's volumetric fog does the
// scattering (driven by the directional light's volumetric fog energy); this shader
// only shapes WHERE the fog is. Cheap — a couple texture reads per fog voxel.

uniform sampler2D cloud_shadow_tex : filter_linear, repeat_disable, hint_default_white;
uniform float shadow_region = 8192.0;     // world span the shadow map covers
uniform float godray_density = 0.02;      // base fog density in lit gaps
uniform float godray_gap_bias = 1.5;      // how sharply shafts favor the sunlit gaps
uniform bool godray_on = false;

void fog() {
    if (!godray_on) { DENSITY = 0.0; }
    else {
        // sun visibility at this voxel's world XZ (1 = full sun gap, 0 = under cloud)
        vec2 uv = WORLD_POSITION.xz / shadow_region + 0.5;
        float vis = texture(cloud_shadow_tex, uv).r;
        // fog (and thus visible scattering) lives in the LIT gaps → shafts through holes
        float gap = pow(clamp(vis, 0.0, 1.0), godray_gap_bias);
        DENSITY = godray_density * gap;
    }
}
