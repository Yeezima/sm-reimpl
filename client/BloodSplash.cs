using System;
using Godot;

namespace SmClient;

/// <summary>
/// fx/red-glow-blood (owner, 2026-10-31): the red splash a hit on a character with no armour throws out of him, in the colour and style of the break glow (<see cref="CharacterViewer.RedGlowColour"/>): a
/// spray of soft additive droplet streaks bursting outward from the hit point (most back toward the shooter, a few on through the body), each a head with a thin tail that stretches out and fades in
/// <see cref="Life"/> seconds. The data's own blood effects (<c>blood_dmg_med_01</c>, <c>blood_light</c>, <c>flesh_dmg_00</c>: dark, alpha-blended beams, specks and decals) are not of this look and the flesh
/// hit's own effect still plays unchanged; this is added on top. Streak count, speeds, widths and life are **ours** (refine register). <c>--old-break-glow</c> leaves it out.
/// </summary>
public partial class BloodSplash : Node3D
{
    public const float Life = 0.45f;
    const int Streaks = 12, Pool = 10;

    static string F(float v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    static readonly string Code = $$"""
shader_type spatial;
render_mode blend_add, unshaded, depth_draw_never, cull_disabled, fog_disabled;

uniform float age = 0.0;
uniform float life = {{F(Life)}};
uniform float size = 1.0;
varying float fade;

float reach(float t) { return (1.0 - exp(-max(t, 0.0) * 7.0)) / 7.0; }

void vertex() {
    vec3 d = COLOR.rgb * 2.0 - 1.0;
    float spd = mix(2.0, 5.5, COLOR.a) * size;
    vec3 headp = d * spd * reach(age);
    vec3 tailp = d * spd * reach(age - 0.14);
    vec3 pv = (MODELVIEW_MATRIX * vec4(mix(tailp, headp, UV.x), 1.0)).xyz;
    vec3 hv = (MODELVIEW_MATRIX * vec4(headp, 1.0)).xyz;
    vec3 tv = (MODELVIEW_MATRIX * vec4(tailp, 1.0)).xyz;
    vec3 al = hv - tv;
    al = length(al) > 0.0001 ? normalize(al) : vec3(0.0, 1.0, 0.0);
    vec3 side = normalize(cross(al, normalize(pv)));
    float w = 0.1 * size * (0.15 + 0.85 * UV.x);
    POSITION = PROJECTION_MATRIX * vec4(pv + side * (UV.y * 2.0 - 1.0) * w, 1.0);
    fade = pow(clamp(1.0 - age / life, 0.0, 1.0), 1.4) * step(0.0, age);
}

void fragment() {
    float across = 1.0 - abs(UV.y * 2.0 - 1.0);
    float body = across * across * (0.25 + 0.75 * smoothstep(0.0, 0.7, UV.x));
    vec3 col = vec3({{F(CharacterViewer.RedGlowColour.R)}}, {{F(CharacterViewer.RedGlowColour.G)}}, {{F(CharacterViewer.RedGlowColour.B)}}) * 3.0 + vec3(0.4, 0.05, 0.1) * smoothstep(0.6, 1.0, UV.x) * across * 2.0;
    ALBEDO = col * body * fade;
}
""";

    static ArrayMesh? _mesh;
    static Shader? _shader;
    readonly MeshInstance3D[] _nodes = new MeshInstance3D[Pool];
    readonly ShaderMaterial[] _materials = new ShaderMaterial[Pool];
    readonly float[] _age = new float[Pool];
    readonly bool[] _on = new bool[Pool];
    int _next;
    /// <summary>Splashes started, for tests and logs.</summary>
    public int Spawned { get; private set; }

    /// <summary>One shared mesh of <see cref="Streaks"/> quads: each carries its direction and speed in the vertex colour (the shader moves it), directions in a fixed spray about +Y.</summary>
    static ArrayMesh SharedMesh()
    {
        if (_mesh is not null) return _mesh;
        var rng = new Random(4711);
        var verts = new Vector3[Streaks * 4];
        var uvs = new Vector2[Streaks * 4];
        var cols = new Color[Streaks * 4];
        var idx = new int[Streaks * 6];
        for (int i = 0; i < Streaks; i++)
        {
            // the first 9 back along +Y (toward the shooter, up to ~75 degrees off), the rest on through the body (the -Y side, flatter)
            bool back = i < 9;
            float tilt = back ? (float)(0.15 + 1.15 * rng.NextDouble()) : (float)(2.0 + 0.8 * rng.NextDouble());
            float az = (float)(i * 2.399963 + rng.NextDouble() * 0.6);
            var d = new Vector3(MathF.Sin(tilt) * MathF.Cos(az), MathF.Cos(tilt), MathF.Sin(tilt) * MathF.Sin(az)).Normalized();
            float spd = (float)rng.NextDouble();
            var c = new Color(d.X * 0.5f + 0.5f, d.Y * 0.5f + 0.5f, d.Z * 0.5f + 0.5f, spd);
            for (int k = 0; k < 4; k++)
            {
                int v = i * 4 + k;
                verts[v] = Vector3.Zero;
                uvs[v] = new Vector2(k / 2, k % 2);
                cols[v] = c;
            }
            int b = i * 4, t = i * 6;
            idx[t] = b; idx[t + 1] = b + 1; idx[t + 2] = b + 2; idx[t + 3] = b + 1; idx[t + 4] = b + 3; idx[t + 5] = b + 2;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Color] = cols;
        arrays[(int)Mesh.ArrayType.Index] = idx;
        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _mesh.CustomAabb = new Aabb(new Vector3(-3f, -3f, -3f), new Vector3(6f, 6f, 6f));
        return _mesh;
    }

    public override void _Ready()
    {
        _shader ??= new Shader { Code = Code };
        for (int i = 0; i < Pool; i++)
        {
            _materials[i] = new ShaderMaterial { Shader = _shader };
            _nodes[i] = new MeshInstance3D { Mesh = SharedMesh(), MaterialOverride = _materials[i], CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false, TopLevel = true };
            AddChild(_nodes[i]);
        }
    }

    /// <summary>Starts a splash at <paramref name="position"/> with its main spray along <paramref name="axis"/> (back toward the shooter) after <paramref name="delay"/> seconds.</summary>
    public void Spawn(Vector3 position, Vector3 axis, float size, double delay)
    {
        if (_nodes[0] is null) return;
        int i = _next; _next = (_next + 1) % Pool;
        axis = axis.LengthSquared() < 1e-6f ? Vector3.Up : axis.Normalized();
        var x = Mathf.Abs(axis.Y) > 0.95f ? Vector3.Right : Vector3.Up.Cross(axis).Normalized();
        var z = x.Cross(axis).Normalized();
        _nodes[i].GlobalTransform = new Transform3D(new Basis(x, axis, z), position);
        _age[i] = (float)-delay; _on[i] = true;
        _materials[i].SetShaderParameter("age", _age[i]);
        _materials[i].SetShaderParameter("size", size);
        _nodes[i].Visible = true;
        Spawned++;
    }

    public override void _Process(double delta)
    {
        for (int i = 0; i < Pool; i++)
        {
            if (!_on[i]) continue;
            _age[i] += (float)delta;
            if (_age[i] >= Life) { _on[i] = false; _nodes[i].Visible = false; continue; }
            _materials[i].SetShaderParameter("age", _age[i]);
        }
    }
}
