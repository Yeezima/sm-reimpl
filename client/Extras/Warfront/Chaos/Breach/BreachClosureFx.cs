using Godot;

namespace SmClient.Extras.Warfront.Chaos.Breach;

/// <summary>
/// The code-made part of a breach's closure (Chaos Warfront C4c; ours, no texture from a reference): a violet sphere that implodes from <see cref="ChaosBreachLook.ImplosionRadius"/> m to nothing over
/// <see cref="ChaosBreachLook.ImplosionSeconds"/> s, then a flat shockwave ring out to <see cref="ChaosBreachLook.ShockwaveRadius"/> m over <see cref="ChaosBreachLook.ShockwaveSeconds"/> s, fading. It lives in
/// game space (Z up, metres) at the breach's centre, steps itself and frees itself. The install's effect flash and the explosion sound are played beside it by the networked view.
/// </summary>
public sealed partial class BreachClosureFx : Node3D
{
    /// <summary>Godot's meshes stand along Y; game space stands along Z.</summary>
    static readonly Basis Upright = new(Vector3.Right, Mathf.Pi / 2f);
    static StandardMaterial3D? _sphereMaterial, _ringMaterial;

    readonly MeshInstance3D _sphere, _ring;
    readonly StandardMaterial3D _sphereLook, _ringLook;
    double _age;

    /// <summary>The whole show's length (s).</summary>
    public static double Seconds => ChaosBreachLook.ImplosionSeconds + ChaosBreachLook.ShockwaveSeconds;

    static StandardMaterial3D Glow(float alpha) => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BlendMode = BaseMaterial3D.BlendModeEnum.Add, Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = false, DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
        AlbedoColor = new Color(ChaosBreachLook.WarpColour.R, ChaosBreachLook.WarpColour.G, ChaosBreachLook.WarpColour.B, alpha),
    };

    public BreachClosureFx()
    {
        Name = "BreachClosureFx";
        _sphereLook = (StandardMaterial3D)(_sphereMaterial ??= Glow(0.9f)).Duplicate();
        _ringLook = (StandardMaterial3D)(_ringMaterial ??= Glow(0.85f)).Duplicate();
        _sphere = new MeshInstance3D { Name = "Implosion", Mesh = new SphereMesh { Radius = 1f, Height = 2f, RadialSegments = 24, Rings = 12 }, MaterialOverride = _sphereLook, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        _ring = new MeshInstance3D { Name = "Shockwave", Mesh = new TorusMesh { InnerRadius = 0.9f, OuterRadius = 1f, Rings = 64, RingSegments = 6 }, MaterialOverride = _ringLook, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
        AddChild(_sphere);
        AddChild(_ring);
        Step(0.0);
    }

    /// <summary>The implosion's radius at <paramref name="t"/> s (m): an ease-in collapse; 0 after it.</summary>
    public static float SphereRadius(double t) => t >= ChaosBreachLook.ImplosionSeconds ? 0f : ChaosBreachLook.ImplosionRadius * (1f - Mathf.Pow((float)(t / ChaosBreachLook.ImplosionSeconds), 2f));

    /// <summary>The ring's radius at <paramref name="t"/> s (m): 0 before the implosion ends, then an ease-out to the full reach.</summary>
    public static float RingRadius(double t)
    {
        double u = (t - ChaosBreachLook.ImplosionSeconds) / ChaosBreachLook.ShockwaveSeconds;
        if (u <= 0) return 0f;
        float k = (float)System.Math.Min(1.0, u);
        return ChaosBreachLook.ShockwaveRadius * (1f - (1f - k) * (1f - k));
    }

    void Step(double t)
    {
        float r = SphereRadius(t);
        _sphere.Visible = r > 0.01f;
        if (_sphere.Visible) _sphere.Scale = Vector3.One * r;
        float ring = RingRadius(t);
        _ring.Visible = ring > 0.01f && t < Seconds;
        if (_ring.Visible)
        {
            _ring.Basis = Upright.Scaled(new Vector3(ring, ring, 1.6f));
            float fade = 1f - (float)((t - ChaosBreachLook.ImplosionSeconds) / ChaosBreachLook.ShockwaveSeconds);
            _ringLook.AlbedoColor = _ringLook.AlbedoColor with { A = 0.85f * Mathf.Clamp(fade, 0f, 1f) };
        }
        if (_sphere.Visible) _sphereLook.AlbedoColor = _sphereLook.AlbedoColor with { A = 0.9f * Mathf.Clamp(0.4f + r / ChaosBreachLook.ImplosionRadius, 0f, 1f) };
    }

    public override void _Process(double delta)
    {
        _age += delta;
        Step(_age);
        if (_age >= Seconds) QueueFree();
    }
}
