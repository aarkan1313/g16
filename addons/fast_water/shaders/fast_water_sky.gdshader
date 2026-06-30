shader_type sky;

uniform vec3 horizon_color : source_color = vec3(0.55, 0.78, 0.92);
uniform vec3 zenith_color : source_color = vec3(0.04, 0.18, 0.42);
uniform vec3 cloud_color : source_color = vec3(0.9, 0.94, 1.0);
uniform float cloud_amount = 0.36;
uniform float cloud_speed = 0.12;


float hash(vec2 p) {
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453123);
}


float noise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	float a = hash(i);
	float b = hash(i + vec2(1.0, 0.0));
	float c = hash(i + vec2(0.0, 1.0));
	float d = hash(i + vec2(1.0, 1.0));
	vec2 u = f * f * (3.0 - 2.0 * f);
	return mix(a, b, u.x) + (c - a) * u.y * (1.0 - u.x) + (d - b) * u.x * u.y;
}


float fbm(vec2 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 4; i++) {
		v += noise(p) * a;
		p *= 2.02;
		a *= 0.5;
	}
	return v;
}


void sky() {
	float up = clamp(EYEDIR.y * 0.5 + 0.5, 0.0, 1.0);
	vec3 sky_col = mix(horizon_color, zenith_color, pow(up, 1.45));

	vec2 cloud_uv = EYEDIR.xz / max(EYEDIR.y + 0.22, 0.08);
	cloud_uv *= 0.72;
	cloud_uv += vec2(TIME * cloud_speed, TIME * cloud_speed * 0.24);
	float cloud = fbm(cloud_uv);
	float mask = smoothstep(1.0 - cloud_amount, 1.0, cloud) * smoothstep(0.02, 0.35, EYEDIR.y);
	COLOR = mix(sky_col, cloud_color, mask * 0.65);
}
