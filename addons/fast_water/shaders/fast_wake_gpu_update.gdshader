shader_type canvas_item;

const int MAX_GPU_STAMPS = 32;

uniform sampler2D previous_map : repeat_disable, filter_linear;
uniform bool clear_map = false;
uniform float delta_time = 0.016;
uniform float height_return_rate = 3.6;
uniform float foam_decay_rate = 3.2;
uniform float normal_return_rate = 3.2;
uniform vec2 origin_xz = vec2(0.0);
uniform vec2 previous_origin_xz = vec2(0.0);
uniform float world_size_m = 96.0;

uniform int stamp_count = 0;
uniform vec4 stamp_pos_radius_strength[MAX_GPU_STAMPS];
uniform vec4 stamp_flow_foam[MAX_GPU_STAMPS];

uniform float side_band_width = 0.11;
uniform float side_band_strength = 0.5;
uniform float center_trail_strength = 0.28;
uniform float bow_band_strength = 0.38;
uniform float ring_foam_strength = 0.34;
uniform float core_foam_strength = 0.16;
uniform float foam_gain = 0.62;


float neutral_lerp(float value, float target, float amount) {
	return mix(value, target, clamp(amount, 0.0, 1.0));
}


void fragment() {
	vec2 world_xz = (UV - vec2(0.5)) * max(world_size_m, 0.001) + origin_xz;
	vec2 previous_uv = (world_xz - previous_origin_xz) / max(world_size_m, 0.001) + vec2(0.5);
	vec4 c = vec4(0.5, 0.0, 0.5, 0.5);
	if (previous_uv.x >= 0.0 && previous_uv.x <= 1.0 && previous_uv.y >= 0.0 && previous_uv.y <= 1.0) {
		c = texture(previous_map, previous_uv);
	}
	if (clear_map) {
		c = vec4(0.5, 0.0, 0.5, 0.5);
	} else {
		c.r = neutral_lerp(c.r, 0.5, delta_time * height_return_rate);
		c.g = neutral_lerp(c.g, 0.0, delta_time * foam_decay_rate);
		c.b = neutral_lerp(c.b, 0.5, delta_time * normal_return_rate);
		c.a = neutral_lerp(c.a, 0.5, delta_time * normal_return_rate);
	}

	for (int i = 0; i < MAX_GPU_STAMPS; i++) {
		if (i >= stamp_count) {
			break;
		}

		vec4 stamp = stamp_pos_radius_strength[i];
		vec4 flow_foam = stamp_flow_foam[i];
		float radius_m = max(stamp.z, 0.05);
		vec2 rel = world_xz - stamp.xy;
		float d = length(rel) / radius_m;
		if (d > 3.0) {
			continue;
		}

		vec2 flow = flow_foam.xy;
		float speed = length(flow);
		if (speed > 0.0001) {
			flow /= speed;
		} else {
			flow = vec2(1.0, 0.0);
		}

		// Convention: along > 0 is AHEAD (direction of travel), along < 0 is BEHIND.
		// All trailing foam must grow with distance BEHIND the object (back), and
		// decay as you move further back -- that is what reads as a wake trail.
		vec2 side_axis = vec2(-flow.y, flow.x);
		float moving = step(0.0001, speed);
		float along = dot(rel, flow) / radius_m;
		float side = dot(rel, side_axis) / radius_m;
		float back = max(-along, 0.0);
		float ring = exp(-pow(d - 1.0, 2.0) * 3.2);
		float core = exp(-d * d * 2.0);
		// Soft gate that is 0 ahead of the object and 1 just behind it.
		float behind_gate = smoothstep(0.20, -0.05, along) * moving;
		// Coherent narrow trail directly behind, persisting several radii back.
		float center_trail = exp(-side * side * 5.5) * behind_gate * exp(-back * 0.45);
		// Faint Kelvin-style V arms that widen with distance behind.
		float side_target = 0.30 + back * 0.55;
		float side_band = exp(-pow((abs(side) - side_target) / max(side_band_width, 0.001), 2.0)) * behind_gate * exp(-back * 0.40);
		// Subtle bow lip just AHEAD of the object (water pushed forward).
		float bow_lip = exp(-pow(d - 0.95, 2.0) * 6.0) * smoothstep(-0.05, 0.45, along) * moving;
		float streak = clamp(center_trail * center_trail_strength + side_band * side_band_strength + bow_lip * bow_band_strength, 0.0, 1.0);
		float signed_height = (center_trail * 0.32 + bow_lip * 0.45 - side_band * 0.14) * stamp.w * 0.16;
		float foam = flow_foam.z;
		float flow_weight = streak * clamp(abs(stamp.w) * 1.3, 0.0, 1.0);

		c.r = clamp(c.r + signed_height, 0.0, 1.0);
		c.g = clamp(max(c.g, max(max(ring * ring_foam_strength, core * core_foam_strength), streak) * foam * foam_gain), 0.0, 1.0);
		c.b = clamp(mix(c.b, flow.x * 0.5 + 0.5, flow_weight), 0.0, 1.0);
		c.a = clamp(mix(c.a, flow.y * 0.5 + 0.5, flow_weight), 0.0, 1.0);
	}

	COLOR = c;
}
