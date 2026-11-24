// Cold sky with a warmer horizon (path radiance). Output is apparent temperature as gray.
shader_type sky;

uniform float zenith_temp = 0.06;
uniform float horizon_temp = 0.32;

void sky() {
	float e = clamp(EYEDIR.y, 0.0, 1.0);
	float t = mix(horizon_temp, zenith_temp, pow(e, 0.35));
	COLOR = vec3(t);
}
