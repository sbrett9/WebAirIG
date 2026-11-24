// Shared pseudo-MWIR helpers. Scene shaders output a normalized apparent temperature (0 = cold, 1 = hot) as gray;
// the per-sensor post-process (sensor_post.gdshader) applies gain/level, noise and the jet palette.

global uniform float ir_visibility_m;
global uniform float ir_path_temp;
global uniform vec3 ir_sun_dir;

// Simple Beer-Lambert path attenuation toward the path radiance temperature.
float ir_attenuate(float t, float dist_m) {
	float tau = exp(-dist_m / max(ir_visibility_m, 1.0));
	return mix(ir_path_temp, t, tau);
}

float ir_hash21(vec2 p) {
	p = fract(p * vec2(123.34, 456.21));
	p += dot(p, p + 45.32);
	return fract(p.x * p.y);
}

float ir_vnoise(vec2 p) {
	vec2 i = floor(p);
	vec2 f = fract(p);
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = ir_hash21(i);
	float b = ir_hash21(i + vec2(1.0, 0.0));
	float c = ir_hash21(i + vec2(0.0, 1.0));
	float d = ir_hash21(i + vec2(1.0, 1.0));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

float ir_fbm(vec2 p) {
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 5; i++) {
		v += a * ir_vnoise(p);
		p = p * 2.03 + vec2(17.1, 9.2);
		a *= 0.5;
	}
	return v;
}
