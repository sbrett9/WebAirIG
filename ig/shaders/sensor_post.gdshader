// Per-sensor post-process: optics blur, fixed-pattern + temporal noise, gain/level, polarity and palette.
// Input: linear apparent-temperature render (HDR 2D viewport). Output: 8-bit RGB that gets encoded/streamed.
shader_type canvas_item;

uniform sampler2D scene_tex : filter_linear, repeat_disable;
uniform vec2 texel = vec2(0.00048828125); // 1/2048, overwritten from C# with the real size
uniform bool sensor_on = true;
uniform float gain = 1.0;
uniform float level = 0.5;
uniform bool black_hot = false;
uniform int palette = 0; // 0 = jet, 1 = grayscale, 2 = ironbow
uniform float noise_sigma = 0.01;
uniform float fpn_sigma = 0.004;
uniform float blur = 0.35;
uniform float frame_seed = 0.0;

float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float gauss(vec2 p) {
	float u1 = max(hash12(p), 1e-6);
	float u2 = hash12(p + 19.19);
	return sqrt(-2.0 * log(u1)) * cos(6.2831853 * u2);
}

vec3 jet(float x) {
	return clamp(vec3(1.5 - abs(4.0 * x - 3.0), 1.5 - abs(4.0 * x - 2.0), 1.5 - abs(4.0 * x - 1.0)), 0.0, 1.0);
}

vec3 ironbow(float x) {
	return clamp(vec3(
		1.6 * x - 0.1,
		2.0 * x * x - 0.2 * x,
		0.9 * sin(3.14159 * x) * (1.0 - x) + 0.6 * smoothstep(0.85, 1.0, x)), 0.0, 1.0);
}

void fragment() {
	if (!sensor_on) {
		COLOR = vec4(0.0, 0.0, 0.0, 1.0);
	} else {
		float c = texture(scene_tex, UV).r;
		if (blur > 0.0) {
			float n = texture(scene_tex, UV + vec2(texel.x, 0.0)).r
				+ texture(scene_tex, UV - vec2(texel.x, 0.0)).r
				+ texture(scene_tex, UV + vec2(0.0, texel.y)).r
				+ texture(scene_tex, UV - vec2(0.0, texel.y)).r;
			c = mix(c, n * 0.25, clamp(blur, 0.0, 1.0) * 0.5);
		}
		vec2 px = floor(UV / texel);
		c += fpn_sigma * gauss(px * 0.731 + 17.0);
		c += noise_sigma * gauss(px + vec2(frame_seed * 13.37, frame_seed * 7.77));
		float v = clamp((c - level) * gain + 0.5, 0.0, 1.0);
		if (black_hot) {
			v = 1.0 - v;
		}
		vec3 rgb = palette == 0 ? jet(v) : (palette == 1 ? vec3(v) : ironbow(v));
		COLOR = vec4(rgb, 1.0);
	}
}
