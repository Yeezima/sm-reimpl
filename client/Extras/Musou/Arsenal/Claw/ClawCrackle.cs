using System;
using Godot;

namespace SmClient;

/// <summary>
/// W2: the claw's blue power glow (design section 5, "Glow": ours, none in the owner's references): a soft sheen along each talon that is always faintly there, and a jagged crackle that
/// shows while that character's claw strike is on. One shared material; the strike level is a uniform of a per-rig copy of the material (an instance uniform is not drawn by the Compatibility renderer), set per rig by <c>NetworkView.ClawGlowFrame</c> (from the
/// character's own strike state, so an ally's strike lights only the ally's claw) and faded out in 0.18 s, ticked by <see cref="ClawCrackleDriver"/>. Test aid <c>--claw-crackle-demo</c>
/// (<see cref="Demo"/>): every claw shows the strike half of every second.
/// </summary>
public static class ClawCrackle
{
    const string Code = """
shader_type spatial;
render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
uniform float strike = 0.0;
uniform float demo = 0.0;
uniform vec3 tint = vec3(0.25, 0.6, 1.6);
float hash(float n) { return fract(sin(n * 91.345) * 47453.5453); }
void fragment() {
    float on = max(strike, demo);
    float step_t = floor(TIME * 40.0);
    float jag = (hash(floor(UV.x * 14.0) + step_t * 3.7) - 0.5) * 0.7;
    float line = 0.5 + jag * 0.5 * smoothstep(0.0, 0.2, UV.x);
    float d = abs(UV.y - line);
    float bolt = smoothstep(0.10, 0.0, d);
    float tip = smoothstep(0.55, 1.0, UV.x);
    float sheen = smoothstep(0.5, 0.0, abs(UV.y - 0.5)) * (0.55 + 0.45 * tip);   // the always-there glow of the power field
    float flick = 0.85 + 0.15 * sin(TIME * 9.0 + UV.x * 6.0);
    ALBEDO = tint * (0.9 + tip);
    ALPHA = clamp(sheen * (0.06 + 0.40 * on) * flick + bolt * on * (0.65 + 0.35 * hash(step_t + UV.x)), 0.0, 1.0);
}
""";

    static ShaderMaterial? _material;
    static ClawCrackleDriver? _driver;

    /// <summary>The shared crackle material (made on first use).</summary>
    public static ShaderMaterial Material => _material ??= new ShaderMaterial { Shader = new Shader { Code = Code }, RenderPriority = 3 };

    /// <summary>The instance uniform's and the demo uniform's names (made once: no allocation per frame).</summary>
    public static readonly StringName StrikeName = "strike", DemoName = "demo";

    /// <summary>Test aid <c>--claw-crackle-demo</c>: every claw shows the strike half of every second whatever the strikes (for stills).</summary>
    public static readonly bool Demo = Array.IndexOf(OS.GetCmdlineUserArgs(), "--claw-crackle-demo") >= 0;

    /// <summary>Test aid <c>--claw-glow-test</c>: the own claw's per-claw strike level (the instance uniform, as a real strike sets it) pulses half of every second, with no strike needed (for stills under any renderer).</summary>
    public static readonly bool GlowTest = Array.IndexOf(OS.GetCmdlineUserArgs(), "--claw-glow-test") >= 0;

    /// <summary>The per-frame call (a view's <c>ClawGlowFrame</c>), set by the first claw strike a view sees; the driver ticks it.</summary>
    public static Action<float>? Frame;

    /// <summary>The view <see cref="Frame"/> belongs to; the driver drops both when it leaves the tree or is freed (leaving a match frees its view, a rejoin makes a new one).</summary>
    public static Node? Owner;

    /// <summary>Test aid: finds the networked view in the tree and registers its glow (a real strike does this from <c>LightningClawClip</c>).</summary>
    public static void Register(Node node, int depth)
    {
        if (depth > 4) return;
        foreach (var child in node.GetChildren())
        {
            if (child is NetworkView view) { view.ClawGlowRegister(); return; }
            Register(child, depth + 1);
        }
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

/// <summary>Ticks the claws' glow every frame (<see cref="ClawCrackle.Frame"/>), and runs the demo's pulse.</summary>
public partial class ClawCrackleDriver : Node
{
    double _t;
    public override void _Process(double delta)
    {
        if (ClawCrackle.Demo)
        {
            _t += delta;
            ClawCrackle.Material.SetShaderParameter(ClawCrackle.DemoName, Mathf.Sin((float)_t * Mathf.Tau * 0.5f) > -0.2f ? 1f : 0f);
        }
        if (ClawCrackle.GlowTest && ClawCrackle.Frame is null && GetTree()?.Root is { } root) ClawCrackle.Register(root, 0);
        if (ClawCrackle.Owner is { } owner && (!GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree())) { ClawCrackle.Frame = null; ClawCrackle.Owner = null; }
        ClawCrackle.Frame?.Invoke((float)delta);
    }
}
