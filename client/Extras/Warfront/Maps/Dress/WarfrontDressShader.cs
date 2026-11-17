namespace SmClient;

/// <summary>
/// The Ork plated looks' shader (slice "dress", part 1; every colour and size ours): one procedural spatial shader, no texture. Modes: 0 compound wall (rust plates with rivets and streaks), 1 street block (patched plates), 2 arena shell (heavy plates with a
/// hazard band at 11.6 to 13.4 m), 3 rock strata, 4 scrap tower, 5 wreck, 6 gate lintel (hazard band at 14.3 to 15.7 m), 7 ruined cathedral stone (ribs, courses and a few lit window slits; the spire, the far massing). Local space is game space (X east,
/// Y north, Z up), so the plates run with the level's own axes; the vertex colour is a tone multiplier. Mode 0 darkens the breach's two broken ends (x -68 and -60 on the south wall).
/// </summary>
public static class WarfrontDressShader
{
    /// <summary>The shader's code.</summary>
    public const string Code = """
shader_type spatial;
render_mode cull_disabled;
uniform int mode = 0;
uniform vec3 tint : source_color = vec3(1.0);
uniform float dark = 1.0;
uniform vec3 dark_tint = vec3(1.0);
varying vec3 lp;
varying vec3 ln;
varying vec4 vcol;
void vertex() { lp = VERTEX; ln = NORMAL; vcol = COLOR; }
float h11(float n) { return fract(sin(n * 127.1) * 43758.5453); }
float h21(vec2 p) { vec3 p3 = fract(vec3(p.xyx) * 0.1031); p3 += dot(p3, p3.yzx + 33.33); return fract((p3.x + p3.y) * p3.z); }
float vn(vec2 p) {
	vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
	return mix(mix(h21(i), h21(i + vec2(1.0, 0.0)), f.x), mix(h21(i + vec2(0.0, 1.0)), h21(i + vec2(1.0, 1.0)), f.x), f.y);
}
float fbm(vec2 p) { float v = 0.0; float a = 0.5; for (int i = 0; i < 4; i++) { v += a * vn(p); p = p * 2.07 + vec2(3.1, 7.7); a *= 0.5; } return v; }
vec3 pick(float r, vec3 a, vec3 b, vec3 c, vec3 d) { return r < 0.35 ? a : (r < 0.65 ? b : (r < 0.9 ? c : d)); }
void fragment() {
	vec3 an = abs(ln);
	bool top = an.z > an.x && an.z > an.y;
	vec2 uv = top ? lp.xy : (an.x > an.y ? vec2(lp.y, lp.z) : vec2(lp.x, lp.z));
	vec2 ps = vec2(4.0, 2.5);
	if (mode == 1) ps = vec2(2.0, 1.25);
	else if (mode == 2) ps = vec2(4.0, 3.0);
	else if (mode == 4) ps = vec2(1.5, 1.5);
	else if (mode == 5) ps = vec2(1.2, 0.9);
	else if (mode == 6) ps = vec2(2.0, 1.0);
	vec3 c;
	vec3 em = vec3(0.0);
	if (mode == 7) {
		float n = fbm(uv * 0.35);
		c = vec3(0.115, 0.11, 0.105) * (0.75 + 0.5 * n);
		c *= mix(1.0, 0.6, step(0.45, abs(fract(uv.x / 6.0) - 0.5)));
		c *= mix(1.0, 0.7, step(0.97, fract(uv.y / 7.0)));
		vec2 cellw = floor(vec2(uv.x / 6.0, uv.y / 16.0));
		vec2 w = vec2(fract(uv.x / 6.0), fract(uv.y / 16.0));
		if (!top && abs(w.x - 0.5) < 0.08 && w.y > 0.25 && w.y < 0.7) {
			bool lit = h21(cellw + 3.0) > 0.6;
			c = vec3(0.02);
			if (lit) em = vec3(0.95, 0.42, 0.12) * 0.9;
		}
	} else if (mode == 3) {
		float n = fbm(vec2(uv.x * 0.12, uv.y * 0.22));
		float n2 = fbm(uv * 1.4);
		float layer = sin(uv.y * 1.9 + n * 5.0) * 0.5 + 0.5;
		c = mix(vec3(0.075, 0.058, 0.047), vec3(0.23, 0.175, 0.135), n) * (0.65 + 0.7 * n2) * (0.72 + 0.4 * layer);
		if (top) c *= 0.7;
		c *= 0.7 + 0.3 * smoothstep(0.0, 6.0, uv.y);
	} else {
		float row = floor(uv.y / ps.y);
		float off = h11(row + (top ? 7.0 : 0.0)) > 0.72 ? 0.5 * ps.x : 0.0;
		float col = floor((uv.x + off) / ps.x);
		vec2 cell = vec2(col, row);
		vec2 f = vec2(fract((uv.x + off) / ps.x), fract(uv.y / ps.y));
		float r = h21(cell + float(mode) * 17.0 + (top ? 31.0 : 0.0));
		vec3 rust = vec3(0.26, 0.13, 0.07);
		if (mode == 0 || mode == 6) c = pick(r, vec3(0.17, 0.10, 0.065), vec3(0.12, 0.115, 0.10), vec3(0.20, 0.12, 0.08), vec3(0.105, 0.11, 0.095));
		else if (mode == 1) c = pick(r, vec3(0.19, 0.11, 0.07), vec3(0.11, 0.12, 0.08), vec3(0.13, 0.135, 0.15), r > 0.95 ? vec3(0.34, 0.26, 0.06) : vec3(0.22, 0.06, 0.045));
		else if (mode == 2) c = pick(r, vec3(0.095, 0.09, 0.09), vec3(0.12, 0.105, 0.10), vec3(0.15, 0.09, 0.06), vec3(0.08, 0.08, 0.085));
		else if (mode == 4) c = pick(r, vec3(0.085, 0.08, 0.08), vec3(0.12, 0.10, 0.085), vec3(0.07, 0.07, 0.075), vec3(0.13, 0.085, 0.06));
		else c = pick(r, vec3(0.26, 0.11, 0.045), vec3(0.15, 0.15, 0.15), vec3(0.10, 0.14, 0.065), vec3(0.28, 0.22, 0.06));
		c *= 0.8 + 0.4 * h21(cell * 3.1 + 5.0);
		vec2 q = f * ps;
		vec2 e = min(q, ps - q);
		float s = min(e.x, e.y);
		if (s < 0.05) c *= 0.3;
		else if (s < 0.11) c *= (q.y > ps.y * 0.5 ? 1.3 : 0.75);
		bool rv = (abs(e.y - 0.17) < 0.055 && abs(mod(uv.x, 0.55) - 0.275) < 0.055) || (abs(e.x - 0.17) < 0.055 && abs(mod(uv.y, 0.55) - 0.275) < 0.055);
		if (rv && s > 0.1) c *= 1.6;
		float st = fbm(vec2(uv.x * 2.2, uv.y * 0.12));
		c = mix(c, rust, smoothstep(0.5, 0.85, st) * 0.7);
		float patch = fbm(uv * 0.55 + vec2(r * 9.0));
		c = mix(c, c * vec3(0.55, 0.5, 0.5), smoothstep(0.45, 0.75, patch) * 0.7);
		float gr = vn(uv * 6.0);
		c *= 0.85 + 0.3 * gr;
		if (!top) c *= 0.5 + 0.5 * smoothstep(0.0, 3.5, uv.y);
		if (mode == 0 && !top && an.x > 0.99 && (abs(lp.x + 68.0) < 0.05 || abs(lp.x + 60.0) < 0.05) && lp.y < -91.9 && lp.y > -100.1) {
			float rg = fbm(vec2(lp.y * 0.9, lp.z * 0.5));
			c = mix(c, vec3(0.03, 0.028, 0.026), smoothstep(0.35, 0.6, rg + 0.25));
		}
		if (!top && ((mode == 2 && uv.y > 11.6 && uv.y < 13.4) || (mode == 6 && uv.y > 14.3 && uv.y < 15.7))) {
			c = mix(vec3(0.05, 0.045, 0.045), vec3(0.34, 0.05, 0.03), step(0.5, fract((uv.x + uv.y) * 0.35)));
		}
	}
	ALBEDO = c * tint * vcol.rgb * dark * dark_tint;
	EMISSION = em;
	ROUGHNESS = 0.88;
	METALLIC = (mode == 3 || mode == 7) ? 0.0 : 0.15;
}
""";
}
