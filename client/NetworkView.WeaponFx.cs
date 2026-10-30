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
    const float MeltaGrowth = 0.065f;
    /// <summary>How long a blast is shown, seconds (ours; the data's rings last 0.2 to 0.6 s).</summary>
    const float MeltaSeconds = 0.55f;
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

    // fx/melta-fuzzy (owner, 2026-10-31: "blurred and stretched out a lot more ... no hard outlines ... fade out with a cloudy effect on the edges. fuzzy"). Values are ours.
    // The cone is a stack of nested open cone shells (COLOR.r = the shell's radius fraction). Each shell fades by view angle (the facing of its wall against the view, normalised for the
    // view's angle to the axis, so a silhouette never shows as a line from the side and the head-on view keeps its body), the shells together give a soft radial falloff, the muzzle and the
    // tip fade out, scrolling 3D noise in metres breaks the body into clouds, and the fragment fades near any geometry in the depth buffer (no intersection line).
    const string MeltaShader = @"
shader_type spatial;
render_mode unshaded, blend_add, cull_disabled, depth_draw_never;
instance uniform float life = 0.0;
instance uniform vec2 dims = vec2(1.0, 20.0);   // the cone's base radius and length, metres
uniform float half_angle = 0.06;
uniform sampler2D depth_tex : hint_depth_texture, repeat_disable, filter_nearest;
varying vec3 lp;
varying float shell;
varying vec3 axisv;
void vertex() { lp = VERTEX; shell = COLOR.r; axisv = (MODELVIEW_MATRIX * vec4(0.0, 1.0, 0.0, 0.0)).xyz; }
float hash(vec3 p) { p = fract(p * 0.3183099 + 0.1); p *= 17.0; return fract(p.x * p.y * p.z * (p.x + p.y + p.z)); }
float vnoise(vec3 x) {
    vec3 i = floor(x), f = fract(x);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(mix(hash(i), hash(i + vec3(1,0,0)), f.x), mix(hash(i + vec3(0,1,0)), hash(i + vec3(1,1,0)), f.x), f.y),
               mix(mix(hash(i + vec3(0,0,1)), hash(i + vec3(1,0,1)), f.x), mix(hash(i + vec3(0,1,1)), hash(i + vec3(1,1,1)), f.x), f.y), f.z);
}
void fragment() {
    float d = clamp(0.5 - lp.y, 0.0, 1.0);                     // 0 at the muzzle, 1 at the far end
    vec3 n = normalize(NORMAL);
    vec3 v = normalize(VIEW);
    vec3 axis = normalize(axisv);
    float beta = acos(clamp(abs(dot(axis, v)), 0.0, 1.0));      // the view's angle to the axis
    float maxf = cos(max(0.0, abs(beta - (1.5707963 - half_angle))));
    float facing = abs(dot(n, v)) / max(maxf, 0.05);
    float edge = smoothstep(0.0, 0.85, facing);
    edge *= edge;                                              // soft both across the wall and toward the silhouette
    vec3 p = vec3(lp.x * dims.x, lp.y * dims.y, lp.z * dims.x) * 0.7;
    float c1 = vnoise(p * 0.55 + vec3(0.0, TIME * 7.0, 0.0));
    float c2 = vnoise(p * 1.3 - vec3(TIME * 0.8, TIME * 11.0, TIME * 0.6));
    float cloud = smoothstep(0.15, 0.85, c1 * 0.65 + c2 * 0.35) * 1.25 + 0.08;
    cloud = mix(cloud, cloud * cloud * 1.5, shell);              // the outer shells break up more: ragged, cloudy edges
    float weight = 0.22 + 0.7 * (1.0 - shell);          // the outer shells dim, the inner ones bright: a soft radial falloff, also head-on
    float muzzle = smoothstep(0.0, 0.14, d);
    float tip = 1.0 - smoothstep(0.35, 1.0, d);
    tip *= tip;
    float fade = (1.0 - life) * (1.0 - life);
    float depth = texture(depth_tex, SCREEN_UV).r;
    vec4 vw = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
    vw.xyz /= vw.w;
    float soft = smoothstep(0.0, 0.9, VERTEX.z - vw.z);          // fades out within 0.9 m of a wall or floor
    vec3 hot = mix(vec3(0.24, 0.12, 1.0), vec3(0.45, 0.65, 1.0), pow(1.0 - d, 5.0));
    float near = smoothstep(0.8, 5.0, -VERTEX.z);               // a cone that passes the camera never veils the screen
    float a = edge * weight * cloud * muzzle * tip * fade * soft * near;
    ALBEDO = hot * a * 0.11;
    ALPHA = 1.0;
}";

    /// <summary>The nested open cone shells (apex +Y, base -Y, unit height and base radius), COLOR.r the shell's radius fraction.</summary>
    static Mesh BuildMeltaMesh()
    {
        float[] shells = { 1f, 0.88f, 0.76f, 0.65f, 0.54f, 0.44f, 0.34f, 0.25f, 0.16f };
        const int segments = 28, rings = 4;
        const float top = 0.03f;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        foreach (float s in shells)
        {
            st.SetColor(new Color(s, 0f, 0f));
            Vector3 P(int ring, int seg)
            {
                float t = ring / (float)rings, ang = seg * Mathf.Tau / segments, r = Mathf.Lerp(top, 1f, t) * s;
                return new Vector3(Mathf.Cos(ang) * r, 0.5f - t, Mathf.Sin(ang) * r);
            }
            Vector3 N(int seg)
            {
                float ang = seg * Mathf.Tau / segments;
                return new Vector3(Mathf.Cos(ang), 1f, Mathf.Sin(ang)).Normalized();
            }
            for (int ring = 0; ring < rings; ring++)
                for (int seg = 0; seg < segments; seg++)
                {
                    int s1 = (seg + 1) % segments;
                    foreach (var (r, g) in new[] { (ring, seg), (ring + 1, seg), (ring, s1), (ring, s1), (ring + 1, seg), (ring + 1, s1) })
                    {
                        st.SetNormal(N(g));
                        st.AddVertex(P(r, g));
                    }
                }
        }
        return st.Commit();
    }

    /// <summary>Starts a blast cone from the muzzle <paramref name="from"/> toward <paramref name="end"/> (Godot space).</summary>
    void MeltaConeAdd(Vector3 from, Vector3 end)
    {
        var axis = end - from;
        if (axis.Length() < 0.4f) return;
        float length = MeltaMaxLength;   // fx/melta-fuzzy: always the data's reach; walls hide the part behind them (depth test), the depth fade softens the meeting
        var dir = axis.Normalized();
        _meltaMesh ??= BuildMeltaMesh();
        _meltaMaterial ??= new ShaderMaterial { Shader = new Shader { Code = MeltaShader } };
        _meltaMaterial.SetShaderParameter("half_angle", Mathf.Atan(MeltaGrowth));
        ref var c = ref _meltaCones[_meltaNext];
        _meltaNext = (_meltaNext + 1) % _meltaCones.Length;
        if (c.Node is null)
        {
            c.Node = new MeshInstance3D { Name = "MeltaCone", Mesh = _meltaMesh, MaterialOverride = _meltaMaterial, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, ExtraCullMargin = 64f, TopLevel = true };
            AddChild(c.Node);
        }
        float radius = MeltaMuzzleRadius + MeltaGrowth * length;
        // The mesh's apex is +Y and its base -Y: +Y points back to the muzzle, the middle sits half the length along the shot.
        // fx/visuals-2 (item 40): Basis.Scaled scales the WORLD axes; the local axes are scaled here.
        var axes = AxesFromY(-dir);
        var basis = new Basis(axes.X * radius, axes.Y * length, axes.Z * radius);
        c.Node.GlobalTransform = new Transform3D(basis, from + dir * (length * 0.5f));
        c.Node.SetInstanceShaderParameter("life", 0f);
        c.Node.SetInstanceShaderParameter("dims", new Vector2(radius, length));
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
