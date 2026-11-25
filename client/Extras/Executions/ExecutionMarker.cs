using Godot;

namespace SmClient;

/// <summary>
/// Executions X6: the Execute marker (owner 2026-11-25, coordination <c>decisions/2026-11-25-executions-x6-prompt-dot.md</c>, replacing the text prompt): "a solid at the center, opaque glowing red dot
/// above the heads of potential execute targets. It should fizzle and drip color down a bit like a melting icicle. About the height of a stim overhead icon. No text." Only over the one target the press
/// would take. A view-facing quad of the stim cross's size (<see cref="CharacterViewer.StimCrossSize"/>, 0.55 m wide) drawn by <see cref="ShaderCode"/>: an opaque red disc with a hot core, a soft red
/// glow round it, an edge that fizzles (animated noise on the rim) and three drips that run down from its lower edge, thin out and fall away in turn. Plain uniforms and GLSL (no instance uniform, no
/// texture), so the Compatibility renderer draws it the same.
/// </summary>
public sealed partial class ExecutionMarker : MeshInstance3D
{
    /// <summary>The quad's width (metres: the stim cross's side) and height (the drips need room below the dot; ours).</summary>
    public const float Width = CharacterViewer.StimCrossSize, Height = 0.80f;
    /// <summary>The dot's radius and its centre's distance below the quad's top (metres; ours: the solid dot about 0.3 m across, the glow filling the stim cross's width).</summary>
    public const float DotRadius = 0.15f, DotFromTop = 0.27f;
    /// <summary>The marker's red (RGB; ours).</summary>
    public static readonly Color Red = new(1f, 0.06f, 0.04f);

    const string ShaderCode = """
        shader_type spatial;
        // executions X6: the Execute marker. A view-facing quad; an opaque glowing red dot whose rim fizzles and from which three drips run down like a melting icicle.
        // Plain uniforms only (the Compatibility renderer draws no instance uniforms); no texture.
        render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, fog_disabled, shadows_disabled;
        uniform vec2 size = vec2(0.55, 0.8);      // metres
        uniform float radius = 0.15;               // the solid dot (metres)
        uniform float from_top = 0.27;             // the dot's centre below the quad's top (metres)
        uniform vec3 red : source_color = vec3(1.0, 0.06, 0.04);
        uniform float amount = 1.0;                // fades the whole marker in and out

        void vertex() {
            MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
            MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
        }

        float hash(float n) { return fract(sin(n) * 43758.5453); }
        float noise1(float x) { float i = floor(x); float f = fract(x); f = f * f * (3.0 - 2.0 * f); return mix(hash(i), hash(i + 1.0), f); }

        // One drip: a thin streak from the dot's lower edge at x, growing over its cycle and narrowing toward its tip, with a bead at the end; it fades as it lets go.
        float drip(vec2 m, float x, float phase, float speed, float c_y) {
            float t = fract(TIME * speed + phase);
            float top = c_y + sqrt(max(radius * radius - x * x, 0.0)) - 0.01;
            float len = 0.04 + 0.20 * t;
            float s = (m.y - top) / len;                 // 0 at the dot, 1 at the tip
            float w = mix(0.020, 0.006, clamp(s, 0.0, 1.0));
            float body = (s >= 0.0 && s <= 1.0) ? 1.0 - smoothstep(w * 0.6, w, abs(m.x - x)) : 0.0;
            float bead = 1.0 - smoothstep(0.012, 0.020, length(m - vec2(x, top + len)));
            float fade = 1.0 - smoothstep(0.75, 1.0, t);
            return max(body, bead) * fade;
        }

        void fragment() {
            vec2 m = vec2((UV.x - 0.5) * size.x, UV.y * size.y);   // metres: x right, y down from the top
            vec2 c = vec2(0.0, from_top);
            vec2 d = m - c;
            float r = length(d);
            float ang = atan(d.y, d.x);
            float fizz = (noise1(ang * 5.0 + TIME * 7.0) - 0.5) * 0.020 + (noise1(ang * 13.0 - TIME * 11.0) - 0.5) * 0.010;
            float edge = r - radius - fizz;
            float core = 1.0 - smoothstep(-0.004, 0.004, edge);            // solid and opaque
            float glow = exp(-max(edge, 0.0) * 22.0) * (1.0 - core);         // the soft red halo
            float sparks = step(0.0, edge) * step(edge, 0.05) * step(0.93, noise1(ang * 40.0 + TIME * 23.0)) * (1.0 - edge / 0.05);
            float drips = max(drip(m, -0.075, 0.00, 0.45, c.y), max(drip(m, 0.010, 0.37, 0.38, c.y), drip(m, 0.085, 0.71, 0.52, c.y)));
            float hot = 1.0 - smoothstep(0.0, radius * 0.75, r);             // the hot centre
            vec3 col = red * (1.0 + 0.6 * hot) + vec3(0.35, 0.10, 0.05) * hot;
            float a = max(max(core, drips), max(glow * 0.75, sparks * 0.9));
            ALBEDO = col * (0.55 + 0.45 * max(core, drips)) + red * glow * 0.6;
            ALPHA = clamp(a * amount, 0.0, 1.0);
        }
        """;

    static Shader? _shader;
    readonly ShaderMaterial _material;

    public ExecutionMarker()
    {
        Name = "ExecutionMarker";
        _shader ??= new Shader { Code = ShaderCode };
        _material = new ShaderMaterial { Shader = _shader };
        _material.SetShaderParameter("size", new Vector2(Width, Height));
        _material.SetShaderParameter("radius", DotRadius);
        _material.SetShaderParameter("from_top", DotFromTop);
        _material.SetShaderParameter("red", Red);
        Mesh = new QuadMesh { Size = new Vector2(Width, Height) };
        MaterialOverride = _material;
        CastShadow = ShadowCastingSetting.Off;
        ExtraCullMargin = 2f;
        Visible = false;
    }

    /// <summary>Times the marker was placed (the self-test's counter).</summary>
    public int Placed { get; private set; }

    /// <summary>Shows the dot with its centre at <paramref name="dotCentre"/> (Godot space).</summary>
    public void Show(Vector3 dotCentre)
    {
        // The quad's centre sits below the dot's centre: the dot is DotFromTop under the top edge.
        GlobalPosition = dotCentre + Vector3.Down * (Height * 0.5f - DotFromTop);
        Visible = true;
        Placed++;
    }

    public void Hide() => Visible = false;
}
