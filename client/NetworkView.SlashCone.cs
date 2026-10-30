using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// fx/visuals-2 (owner's playtest of 2026-10-30, round 2, item 35; client presentation only): the Assault / Raptor air-slash's landing shows a **cone**, in the style of the ground pound's impact
/// (the pound's dust colours, soft shockwave ring at the rim, the data's landing dust spread through it). The shape is the one the simulation tests: the melee hit is every target whose direction from the
/// attacker is within <c>MeleeConeDegrees</c> (60) of the heading and within <c>MeleeRange</c> (4 m): a true 3D cone with the apex at the attacker's feet at the impact, its axis along the slash's
/// heading, slant length = the reach (the diagram's l), half angle = 60 (theta), so h = l cos(theta) = 2 m and r = l sin(theta) = 3.46 m. A shell of that cone swells from 35 % to full size in 0.18 s and fades
/// by <see cref="SlashConeSeconds"/>; a pale ring marks its rim (the base). Ours: the look (the install has no cone effect). The flat ground wedge and the dust stay. <c>--no-slash-cone</c> removes the shell.
/// </summary>
public partial class NetworkView
{
    /// <summary>How long the cone shell lasts (s, ours).</summary>
    const float SlashConeSeconds = 0.65f;

    bool NoSlashCone => _noSlashCone ??= CmdFlag("no-slash-cone");
    bool? _noSlashCone;

    struct SlashConeNode { public MeshInstance3D Node; public double Start; public bool Live; public Vector3 Apex, Heading; public float Radius, Height; }
    readonly SlashConeNode[] _slashCones = new SlashConeNode[6];
    int _slashConeNext;
    Mesh? _slashConeMesh;
    ShaderMaterial? _slashConeMaterial;

    const string SlashConeShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never, fog_disabled;
instance uniform float life = 0.0;
varying vec3 lp;
void vertex() { lp = VERTEX; }
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
void fragment() {
    float d = 0.5 - lp.y;                                   // 0 at the apex, 1 at the base (the rim)
    float ang = atan(lp.z, lp.x);
    float n = vnoise(vec2(ang * 3.0, d * 4.0 - life * 3.0)) * 0.6 + vnoise(vec2(ang * 7.0 + 2.0, d * 9.0)) * 0.4;
    float rim = exp(-pow((d - 0.97) / 0.07, 2.0));         // the shockwave ring at the base
    float edge = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 1.4);
    float fade = pow(1.0 - life, 1.8) * smoothstep(0.0, 0.06, life);
    float a = (0.03 + 0.30 * n * n + 0.16 * edge * n + 0.5 * rim * (0.5 + 0.5 * n)) * (0.25 + 0.75 * smoothstep(0.0, 0.6, d)) * fade;
    vec3 dust = mix(vec3(0.36, 0.32, 0.26), vec3(0.78, 0.70, 0.56), clamp(rim + 0.2 * edge, 0.0, 1.0));
    ALBEDO = dust;
    ALPHA = clamp(a, 0.0, 0.7);
}";

    /// <summary>Starts the cone shell with its apex at <paramref name="apex"/> (Godot space), its axis along <paramref name="heading"/>, slant <paramref name="reach"/> and half angle <paramref name="halfAngleDegrees"/>.</summary>
    void SlashConeAdd(Vector3 apex, Vector3 heading, float reach, float halfAngleDegrees)
    {
        if (NoSlashCone || heading.LengthSquared() < 1e-6f) return;
        float half = Mathf.DegToRad(Math.Clamp(halfAngleDegrees, 5f, 85f));
        float height = reach * MathF.Cos(half), radius = reach * MathF.Sin(half);
        var dir = heading.Normalized();
        _slashConeMesh ??= new CylinderMesh { TopRadius = 0f, BottomRadius = 1f, Height = 1f, RadialSegments = 36, Rings = 4, CapTop = false, CapBottom = false };
        _slashConeMaterial ??= new ShaderMaterial { Shader = new Shader { Code = SlashConeShader } };
        ref var c = ref _slashCones[_slashConeNext];
        _slashConeNext = (_slashConeNext + 1) % _slashCones.Length;
        if (c.Node is null || !IsInstanceValid(c.Node))
        {
            c.Node = new MeshInstance3D { Name = "SlashCone", Mesh = _slashConeMesh, MaterialOverride = _slashConeMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 16f, TopLevel = true };
            AddChild(c.Node);
        }
        c.Apex = apex; c.Heading = dir; c.Radius = radius; c.Height = height; c.Start = _connectedFor; c.Live = true;
        c.Node.Visible = true;
        c.Node.SetInstanceShaderParameter("life", 0f);
        PlaceSlashCone(ref c, 0.35f);
    }

    /// <summary>The mesh's apex is +Y: +Y points back to the apex point, so the axis (-Y) runs along the heading; scaled per axis (the columns, not the world axes).</summary>
    static void PlaceSlashCone(ref SlashConeNode c, float grow)
    {
        var axes = AxesFromY(-c.Heading);
        var basis = new Basis(axes.X * (c.Radius * grow), axes.Y * (c.Height * grow), axes.Z * (c.Radius * grow));
        c.Node.GlobalTransform = new Transform3D(basis, c.Apex + c.Heading * (c.Height * grow * 0.5f));
    }

    /// <summary><c>--slash-cone-demo</c> (test aid): every 4 s the own Assault / Raptor shows the slash's landing effects as if it had just landed (no jump needed).</summary>
    bool SlashConeDemo => _slashConeDemo ??= CmdFlag("slash-cone-demo");
    bool? _slashConeDemo;
    double _slashConeDemoAt = 3.0;

    /// <summary>Each frame: swells and fades the cone shells and hides the finished ones.</summary>
    void UpdateSlashCones()
    {
        if (SlashConeDemo && _client is not null && _config is not null && _connectedFor >= _slashConeDemoAt)
        {
            _slashConeDemoAt = _connectedFor + 4.0;
            var me = _client.Predicted;
            if (Loadouts.JumpPack(_config, me) is { } pack) SlashHitPresentation(me, true, pack);
        }
        for (int i = 0; i < _slashCones.Length; i++)
        {
            ref var c = ref _slashCones[i];
            if (!c.Live) continue;
            double age = _connectedFor - c.Start;
            float life = (float)(age / SlashConeSeconds);
            if (life >= 1f) { c.Live = false; c.Node.Visible = false; continue; }
            float swell = Math.Clamp((float)(age / 0.18), 0f, 1f);
            PlaceSlashCone(ref c, 0.35f + 0.65f * (1f - (1f - swell) * (1f - swell)));
            c.Node.SetInstanceShaderParameter("life", life);
        }
    }
}
