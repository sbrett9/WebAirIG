// Pseudo-MWIR material for entity models: skin temperature + solar loading + texture detail + hotspots.
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque, shadows_disabled;

#include "res://shaders/ir_common.gdshaderinc"

uniform float skin_temp = 0.45;
uniform float solar_gain = 0.1;
uniform float detail_strength = 0.05;
uniform bool has_detail = false;
uniform sampler2D detail_tex : source_color, hint_default_white, filter_linear_mipmap, repeat_enable;
uniform int hot_count = 0;
// Hotspot centers in this mesh's local space (xyz) and radius in local units (w).
uniform vec4 hot_pos_radius[8];
uniform float hot_temp[8];

varying vec3 local_pos;

void vertex() {
	local_pos = VERTEX;
}

void fragment() {
	float t = skin_temp;
	vec3 n_world = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
	t += solar_gain * max(dot(n_world, normalize(ir_sun_dir)), 0.0);
	if (has_detail) {
		vec3 d = texture(detail_tex, UV).rgb;
		t += detail_strength * (dot(d, vec3(0.299, 0.587, 0.114)) - 0.5);
	}
	for (int i = 0; i < 8; i++) {
		if (i >= hot_count) {
			break;
		}
		float r = max(hot_pos_radius[i].w, 1e-3);
		float dd = distance(local_pos, hot_pos_radius[i].xyz);
		float w = exp(-(dd * dd) / (r * r));
		t = mix(t, max(t, hot_temp[i]), w);
	}
	ALBEDO = vec3(ir_attenuate(clamp(t, 0.0, 1.5), length(VERTEX)));
}
