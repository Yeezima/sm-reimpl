using System;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// wfx (owner's playtest of 2026-10-30, items 25): the melta's blast and the plasma weapons' ball, drawn as the owner describes them. Both are **ours** (tagged in the refine register): the
/// data's effects for them are soft additive sprite stacks that read as fog.
/// <para>
/// Melta: the data's <c>meltagun_cylinder_blast_1..4</c> (and the Chaos twins) are a stack of additive <c>rocket_fire</c> rings and beams that read as a purple-blue haze. Owner: "a distinct circular
/// cone, much less purple/blue fog". The blast is a hollow cone drawn from the muzzle along the shot's path (a rim that is brighter than the middle, hot white-orange near the barrel, orange
/// farther out, flickering along its length), growing by the cone's own half angle (5 degrees, the data's <c>angle</c> halved, <see cref="MeltaGrowth"/> per metre) from <see cref="MeltaMuzzleRadius"/>, for
/// <see cref="MeltaSeconds"/>. The muzzle vents, the light and the impacts stay the data's. <c>--old-melta-fx</c> restores the data's blast.
/// </para>
/// <para>
/// Plasma gun and plasma cannon shots: the data's ball is additive soft sprites (<c>add_volume_soft_cool</c>) around a point of light, translucent all through. Owner: "a solid-ish, opaque-ish
/// (especially at the edges) roiling ball of condensed plasma". The projectile's own sphere (before: hidden once the trail played) is now drawn for plasma shots with a shader: a near-opaque
/// ball (alpha <see cref="PlasmaBallAlpha"/>, not lower toward the rim) whose colour roils from a pale core to the side's plasma colour (blue; Chaos purple) through animated noise. The data's trail
/// (the glow, streamers, sparks) still plays around it. <c>--old-plasma-ball</c> restores the hidden sphere.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The blast cone's radius at the muzzle, metres (ours).</summary>
    const float MeltaMuzzleRadius = 0.12f;
    /// <summary>The blast cone's growth in radius per metre of length: tan of the data's 5 degree half angle (the file's <c>angle</c> 10 halved).</summary>
    const float MeltaGrowth = 0.0875f;
    /// <summary>How long a blast is shown, seconds (ours; the data's rings last 0.2 to 0.6 s).</summary>
    const float MeltaSeconds = 0.4f;
    /// <summary>The longest part of the shot drawn as the cone, metres (the data's cone length).</summary>
    const float MeltaMaxLength = 20f;
    /// <summary>The plasma ball's opacity (ours: "opaque-ish"), also at its edge.</summary>
    const float PlasmaBallAlpha = 0.94f;

    bool? _oldMeltaFx, _oldPlasmaBall;
    bool OldMeltaFx => _oldMeltaFx ??= CmdFlag("old-melta-fx");
    bool OldPlasmaBall => _oldPlasmaBall ??= CmdFlag("old-plasma-ball");

    /// <summary>Whether an effect is the melta's blast cone (the Space Marine's and the Chaos twins, one of four by shot).</summary>
    static bool IsMeltaBlast(string effect) => effect.Contains("meltagun_cylinder_blast", StringComparison.OrdinalIgnoreCase);

    struct MeltaCone { public MeshInstance3D Node; public double Start; public bool Live; }
    readonly MeltaCone[] _meltaCones = new MeltaCone[10];
    int _meltaNext, _meltaShown;
    Mesh? _meltaMesh;
    ShaderMaterial? _meltaMaterial;

    const string MeltaShader = @"
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
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
    float d = 1.0 - (lp.y + 0.5);                       // 0 at the muzzle, 1 at the far end
    float ang = atan(lp.z, lp.x);
    float flick = vnoise(vec2(ang * 2.5, d * 7.0 - TIME * 16.0)) * 0.6 + vnoise(vec2(ang * 5.0 + 3.0, d * 17.0 - TIME * 30.0)) * 0.4;
    float rim = pow(1.0 - abs(dot(normalize(NORMAL), normalize(VIEW))), 1.3);
    float fade = (1.0 - d * 0.8) * (1.0 - life) * (1.0 - life);
    // fx/visuals-2 (item 40): back to the data's purple / blue (the rings' colours), a pale blue-white at the barrel to a violet-blue farther out.
    vec3 hot = mix(vec3(0.42, 0.30, 1.0), vec3(0.72, 0.88, 1.0), (1.0 - d) * (1.0 - d));
    float core = pow(abs(dot(normalize(NORMAL), normalize(VIEW))), 2.5);   // the cone seen from the muzzle's side: a faint hot middle
    float a = (0.2 + 1.0 * rim + 0.35 * core * (1.0 - d)) * (0.55 + 0.9 * flick) * fade;
    ALBEDO = hot * a * 1.5;
    ALPHA = 1.0;
}";

    /// <summary>Starts a blast cone from the muzzle <paramref name="from"/> toward <paramref name="end"/> (Godot space).</summary>
    void MeltaConeAdd(Vector3 from, Vector3 end)
    {
        var axis = end - from;
        float length = Math.Min(axis.Length(), MeltaMaxLength);
        if (length < 0.4f) return;
        var dir = axis.Normalized();
        _meltaMesh ??= new CylinderMesh { TopRadius = 0f, BottomRadius = 1f, Height = 1f, RadialSegments = 28, Rings = 1, CapTop = false, CapBottom = false };
        _meltaMaterial ??= new ShaderMaterial { Shader = new Shader { Code = MeltaShader } };
        ref var c = ref _meltaCones[_meltaNext];
        _meltaNext = (_meltaNext + 1) % _meltaCones.Length;
        if (c.Node is null)
        {
            c.Node = new MeshInstance3D { Name = "MeltaCone", Mesh = _meltaMesh, MaterialOverride = _meltaMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 64f, TopLevel = true };
            AddChild(c.Node);
        }
        float radius = MeltaMuzzleRadius + MeltaGrowth * length;
        // The mesh's apex is +Y and its base -Y: +Y points back to the muzzle, the middle sits half the length along the shot; the apex is a small disc (the muzzle radius) rather than a point.
        // fx/visuals-2 (item 40, "points the wrong way"): Basis.Scaled scales the WORLD axes, so the cone's length was stretched along world Y whatever the shot's direction (a cone that leaned
        // away from the aim); the local axes are scaled here.
        var axes = AxesFromY(-dir);
        var basis = new Basis(axes.X * radius, axes.Y * length, axes.Z * radius);
        c.Node.GlobalTransform = new Transform3D(basis, from + dir * (length * 0.5f));
        c.Node.SetInstanceShaderParameter("life", 0f);
        c.Node.Visible = true;
        c.Start = _connectedFor;
        c.Live = true;
        _meltaShown++;
    }

    /// <summary>Each frame: ages the blast cones and hides the finished ones.</summary>
    void UpdateMeltaCones()
    {
        for (int i = 0; i < _meltaCones.Length; i++)
        {
            ref var c = ref _meltaCones[i];
            if (!c.Live) continue;
            float age = (float)(_connectedFor - c.Start) / MeltaSeconds;
            if (age >= 1f) { c.Live = false; c.Node.Visible = false; continue; }
            c.Node.SetInstanceShaderParameter("life", age);
        }
    }

    // ---- the plasma ball ----

    const string PlasmaBallShader = @"
shader_type spatial;
render_mode unshaded, cull_back;
uniform vec3 core_colour : source_color = vec3(0.9, 1.0, 1.0);
uniform vec3 edge_colour : source_color = vec3(0.35, 0.65, 1.0);
uniform float alpha = 0.94;
uniform float radius = 0.2;
varying vec3 lp;
void vertex() { lp = VERTEX / radius; }
float hash(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
void fragment() {
    float t = TIME;
    vec3 q = lp * 2.2;
    float n = vnoise(q + vec3(0.0, t * 1.6, 0.0)) * 0.5 + vnoise(q * 2.1 - vec3(t * 1.9, 0.0, t * 1.2)) * 0.3 + vnoise(q * 4.3 + vec3(t * 2.7)) * 0.2;
    float facing = abs(dot(normalize(NORMAL), normalize(VIEW)));
    float heat = clamp(n * 1.25 + facing * 0.55 - 0.3, 0.0, 1.0);   // brighter toward the middle of the disc, roiling
    vec3 col = mix(edge_colour, core_colour, smoothstep(0.25, 0.95, heat));
    ALBEDO = col * (1.0 + 0.5 * heat);
    ALPHA = alpha;
}";

    /// <summary>The ball material of a plasma shot (<paramref name="colour"/>: the side's plasma colour; <paramref name="radius"/>: the sphere's), or null for a weapon that is not plasma.</summary>
    static ShaderMaterial PlasmaBallMaterial(Color colour, float radius)
    {
        var material = new ShaderMaterial { Shader = PlasmaShader ??= new Shader { Code = PlasmaBallShader } };
        material.SetShaderParameter("edge_colour", new Vector3(colour.R, colour.G, colour.B));
        material.SetShaderParameter("core_colour", new Vector3(Mathf.Lerp(colour.R, 1f, 0.7f), Mathf.Lerp(colour.G, 1f, 0.7f), Mathf.Lerp(colour.B, 1f, 0.7f)));
        material.SetShaderParameter("alpha", PlasmaBallAlpha);
        material.SetShaderParameter("radius", radius);
        return material;
    }

    static Shader? PlasmaShader;

    /// <summary>Whether the projectile of this weapon is a plasma shot (the ball is drawn solid even where the original trail plays). The plasma pistol keeps the ball too: every plasma weapon fires plasma (owner's coordinator decision, intended).</summary>
    bool IsPlasmaShot(int weaponIndex, bool thrown) =>
        !thrown && _config is not null && weaponIndex >= 0 && weaponIndex < _config.Weapons.Count && _config.Weapons[weaponIndex].Name.Contains("plasma", StringComparison.OrdinalIgnoreCase);
}
