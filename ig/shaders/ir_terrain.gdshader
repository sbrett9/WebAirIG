// Procedural pseudo-MWIR ground: field patchwork, fine texture, roads, warm urban blocks and cool water.
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_opaque, shadows_disabled;

#include "res://shaders/ir_common.gdshaderinc"

uniform float base_temp = 0.42;
uniform float field_contrast = 0.12;
uniform float water_fraction = 0.08;
uniform float water_temp = 0.25;
uniform float road_temp = 0.58;
uniform float urban_fraction = 0.05;
uniform float seed = 1.0;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

// Jittered-grid cell id for agricultural fields.
float field_value(vec2 p, float cell) {
	vec2 warp = vec2(ir_vnoise(p / 900.0), ir_vnoise(p / 900.0 + 31.7)) * cell * 0.6;
	vec2 id = floor((p + warp) / cell);
	return ir_hash21(id + seed);
}

float road_mask(vec2 p, float spacing, float width) {
	vec2 warp = vec2(ir_vnoise(p / 3000.0 + 5.0), ir_vnoise(p / 3000.0 + 9.0)) * spacing * 0.35;
	vec2 q = (p + warp) / spacing;
	vec2 d = abs(fract(q) - 0.5) * spacing;
	float m = min(d.x, d.y);
	return 1.0 - smoothstep(width * 0.5, width, m);
}

void fragment() {
	// Keep the seed offset small (< ~12 km) so float precision stays at the millimeter level.
	vec2 p = world_pos.xz + vec2(mod(seed, 97.0) * 113.0, mod(seed, 89.0) * 127.0);

	float t = base_temp + 0.06 * (ir_fbm(p / 6000.0) - 0.5);
	t += field_contrast * 2.0 * (field_value(p, 380.0) - 0.5);
	t += 0.025 * (ir_vnoise(p / 18.0) - 0.5) + 0.015 * (ir_vnoise(p / 3.5) - 0.5);

	// Urban patches: warmer with a block pattern.
	float urban = smoothstep(0.7 - urban_fraction, 0.72 - urban_fraction, ir_fbm(p / 5000.0 + 3.3));
	float blocks = step(0.5, ir_hash21(floor(p / 60.0) + 2.0)) * 0.05;
	t = mix(t, base_temp + 0.1 + blocks, urban);

	// Road network.
	float roads = max(road_mask(p, 2200.0, 9.0), road_mask(p + 1100.0, 5300.0, 14.0));
	t = mix(t, road_temp, roads * 0.9);

	// Water bodies (cold, very uniform).
	float wn = ir_fbm(p / 9000.0 + 11.0);
	float water = 1.0 - smoothstep(water_fraction - 0.02, water_fraction + 0.02, wn - 0.18);
	t = mix(t, water_temp + 0.01 * ir_vnoise(p / 40.0), water);

	ALBEDO = vec3(ir_attenuate(t, length(VERTEX)));
}
