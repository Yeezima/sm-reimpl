using Godot;

namespace SmClient;

/// <summary>
/// W2: the claw's faint blue crackle (design section 5, "Glow": ours, none in the owner's references): a jagged line along each talon, drawn additively, shown only while a claw strike is
/// on. One material for every claw; <see cref="Strike"/> (called each frame a claw strike is posed, from <c>LightningClawClip</c>) lights it and a small driver node fades it out in
/// 0.18 s, so a strike glows from its start to a moment after its end.
/// </summary>
public static class ClawCrackle
{
    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
uniform float strike = 0.0;
uniform vec3 tint = vec3(0.25, 0.6, 1.6);
float hash(float n) { return fract(sin(n * 91.345) * 47453.5453); }
void fragment() {
    float step_t = floor(TIME * 40.0);
    float jag = (hash(floor(UV.x * 14.0) + step_t * 3.7) - 0.5) * 0.7;
    float line = 0.5 + jag * 0.5 * smoothstep(0.0, 0.2, UV.x);
    float d = abs(UV.y - line);
    float bolt = smoothstep(0.10, 0.0, d);
    float tip = smoothstep(0.55, 1.0, UV.x);
    float sheen = smoothstep(0.5, 0.0, abs(UV.y - 0.5)) * (0.55 + 0.45 * tip);   // the always-there glow of the power field
    float flick = 0.85 + 0.15 * sin(TIME * 9.0 + UV.x * 6.0);
    ALBEDO = tint * (0.9 + tip);
    ALPHA = clamp(sheen * (0.06 + 0.40 * strike) * flick + bolt * strike * (0.65 + 0.35 * hash(step_t + UV.x)), 0.0, 1.0);
}
""";

    static ShaderMaterial? _material;
    static ClawCrackleDriver? _driver;

    /// <summary>The shared crackle material (made on first use; dark until a strike).</summary>
    public static ShaderMaterial Material => _material ??= new ShaderMaterial { Shader = new Shader { Code = Code }, RenderPriority = 3 };

    /// <summary>Test aid <c>--claw-crackle-demo</c>: the crackle shows half of every second whatever the strikes (for stills).</summary>
    public static readonly bool Demo = System.Array.IndexOf(OS.GetCmdlineUserArgs(), "--claw-crackle-demo") >= 0;

    /// <summary>A claw strike is shown now: the crackle is at full.</summary>
    public static void Strike()
    {
        Material.SetShaderParameter("strike", 1f);
        Ensure().Level = 1f;
    }

    /// <summary>The driver node (made once, in the scene root).</summary>
    public static ClawCrackleDriver Ensure()
    {
        if (_driver is null || !GodotObject.IsInstanceValid(_driver))
        {
            _driver = new ClawCrackleDriver { Name = "ClawCrackle" };
            (Engine.GetMainLoop() as SceneTree)?.Root.CallDeferred(Node.MethodName.AddChild, _driver);
        }
        return _driver;
    }
}

/// <summary>Fades <see cref="ClawCrackle"/> out after the last strike frame.</summary>
public partial class ClawCrackleDriver : Node
{
    public float Level;
    double _t;
    public override void _Process(double delta)
    {
        if (ClawCrackle.Demo) { _t += delta; Level = Mathf.Sin((float)_t * Mathf.Tau * 0.5f) > -0.2f ? 1f : 0f; ClawCrackle.Material.SetShaderParameter("strike", Level); return; }
        if (Level <= 0f) return;
        Level = Mathf.Max(0f, Level - (float)delta / 0.18f);
        ClawCrackle.Material.SetShaderParameter("strike", Level);
    }
}
