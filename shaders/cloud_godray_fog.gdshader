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
// Density is intentionally TINY: the scene's base volumetric_fog_density is 0.003, and
// the old default (0.02) made the gap fog ~7× denser than the scene — dense enough that
// the terrain's own sun-shadow rendered into it as hard black wedges (the reported bug).
// Keep this fog a faint additive haze that only READS as shafts via the light's
// volumetric scattering, never thick enough to show hard shadow volumes. Clamped below.
uniform float godray_density : hint_range(0.0, 0.01) = 0.0025;  // base fog density in lit gaps
uniform float godray_max     : hint_range(0.0, 0.02) = 0.006;   // hard ceiling (anti-wedge)
uniform float godray_gap_bias = 1.5;      // how sharply shafts favor the sunlit gaps
uniform bool godray_on = false;

void fog() {
    if (!godray_on) { DENSITY = 0.0; }
    else {
        // sun visibility at this voxel's world XZ (1 = full sun gap, 0 = under cloud).
        // Guard the UV: outside the shadow-map footprint, treat as no extra fog (0), not
        // white (the hint_default_white sampler would otherwise paint full fog out there).
        vec2 uv = WORLD_POSITION.xz / shadow_region + 0.5;
        float inFootprint = step(0.0, uv.x) * step(uv.x, 1.0) * step(0.0, uv.y) * step(uv.y, 1.0);
        float vis = texture(cloud_shadow_tex, uv).r;
        // fog (and thus visible scattering) lives in the LIT gaps → shafts through holes
        float gap = pow(clamp(vis, 0.0, 1.0), godray_gap_bias);
        DENSITY = min(godray_density * gap * inFootprint, godray_max);
    }
}
