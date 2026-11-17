using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Extras.Warfront.Dress;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The Godot half of the Warfront dressing's pieces (slice "dress", part 1): flat-shaded meshes in game space (Z up; the parent converts), one per material, with a vertex colour for the tone. The plated looks are one procedural spatial shader
/// (<see cref="WarfrontDressShader"/>: plates, seams, rivets, rust streaks, hazard bands, strata rock; no texture is built, so no frame carries an image build), the rest plain vertex-coloured materials. Client only; every colour is ours (placeholder demonstration).
/// </summary>
public static class WarfrontDressBake
{
    static Shader? _shader;

    /// <summary>The plated shader's material for <paramref name="mode"/> (see <see cref="WarfrontDressShader"/>). One shader for every mode: a shader's first draw costs a pipeline compile (measured 1.4 to 1.8 s cold, once per driver cache), so the modes share it and the cost is met once.</summary>
    public static ShaderMaterial Plated(int mode, Color? tint = null)
    {
        _shader ??= new Shader { Code = WarfrontDressShader.Code + (System.Environment.GetEnvironmentVariable("SM_DRESS_SHADER_SALT") is { Length: > 0 } salt ? $"{(char)10}// salt {salt}{(char)10}" : "") };
        var m = new ShaderMaterial { Shader = _shader };
        m.SetShaderParameter("mode", mode);
        m.SetShaderParameter("tint", tint ?? new Color(1f, 1f, 1f));
        return m;
    }

    /// <summary>The shader mode of a level rule (the plated looks; the kit's own ground and boundary have none).</summary>
    public static int ModeOf(DressRule rule) => rule switch
    {
        DressRule.CompoundWall => 0, DressRule.StreetBlock => 1, DressRule.ArenaShell => 2, DressRule.RockCliff => 3,
        DressRule.ScrapTower => 4, DressRule.WreckCover => 5, DressRule.GateLintel => 6,
        DressRule.ImperialWall or DressRule.Parapet or DressRule.ParapetStair => 8, _ => -1,
    };

    static Color ColourOf(in DressPiece p)
    {
        float s = Math.Clamp(p.Shade, 0f, 1f);
        float k = 0.75f + 0.5f * s;
        return p.Mat switch
        {
            DressMat.Iron => new Color(0.13f * k, 0.12f * k, 0.12f * k),
            DressMat.Bone => new Color(0.32f * (0.8f + 0.4f * s), 0.29f * (0.8f + 0.4f * s), 0.23f * (0.8f + 0.4f * s)),
            DressMat.ClothRed => new Color(0.27f * (0.7f + 0.6f * s), 0.03f, 0.022f),
            DressMat.ClothBlack => new Color(0.05f, 0.045f, 0.045f),
            DressMat.Glow => new Color(1.0f, 0.38f + 0.2f * s, 0.07f),
            DressMat.Dais => s > 0.5f ? new Color(0.62f, 0.05f, 0.03f) : new Color(0.075f, 0.07f, 0.07f),
            DressMat.Gold => new Color(0.50f * (0.8f + 0.4f * s), 0.36f * (0.8f + 0.4f * s), 0.09f),
            DressMat.ClothBlue => new Color(0.04f * (0.7f + 0.6f * s), 0.07f * (0.7f + 0.6f * s), 0.20f * (0.7f + 0.6f * s)),
            DressMat.Smoke => new Color(0.13f, 0.12f, 0.115f, 0.42f),
            DressMat.Stone => new Color(0.8f + 0.4f * s, 0.8f + 0.4f * s, 0.8f + 0.4f * s),   // a tone multiplier of the shader's own stone
            _ => new Color(k, k, k),   // plated and wreck: a tone multiplier of the shader's own palette
        };
    }

    /// <summary>The group a piece is baked into: plated and wreck pieces use the shader, the glow ones are unshaded, the dais's dark half is iron.</summary>
    static string GroupOf(in DressPiece p) => p.Mat switch
    {
        DressMat.Plated => "Plated",
        DressMat.Wreck => "Wreck",
        DressMat.Glow => "Glow",
        DressMat.Dais => p.Shade > 0.5f ? "Glow" : "DaisDark",
        DressMat.Stone => "Stone",
        DressMat.Masonry => "Masonry",
        DressMat.Rock => "Rock",
        DressMat.Gold => "Gold",
        DressMat.Smoke => "Smoke",
        DressMat.ClothBlue => "Cloth",
        DressMat.ClothRed or DressMat.ClothBlack => "Cloth",
        DressMat.Bone => "Bone",
        _ => "Iron",
    };

    /// <summary>The material of a baked group.</summary>
    static Material MaterialOf(string group) => group switch
    {
        "Plated" => Plated(4),
        "Wreck" => Plated(5),
        "Glow" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        "Cloth" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1f, MetallicSpecular = 0f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        "Bone" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.9f, MetallicSpecular = 0.1f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        "Stone" => Plated(7),
        "Masonry" => Plated(8),
        "Rock" => Plated(3),
        "Gold" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.4f, Metallic = 0.7f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        "Smoke" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        "DaisDark" => new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.6f, Metallic = 0.4f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
        _ => new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.6f, Metallic = 0.5f, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
    };

    /// <summary>A triangle soup with a flat normal and a colour per vertex.</summary>
    public sealed class Soup
    {
        public readonly List<Vector3> V = [], N = [];
        public readonly List<Color> C = [];
        public int Triangles => V.Count / 3;

        /// <summary>One triangle, its normal pointing away from <paramref name="inside"/> (the shape's centre).</summary>
        public void Tri(Vector3 a, Vector3 b, Vector3 c, Vector3 inside, Color col)
        {
            var n = (b - a).Cross(c - a);
            if (n.LengthSquared() < 1e-12f) return;
            n = n.Normalized();
            var centre = (a + b + c) / 3f;
            if (n.Dot(centre - inside) < 0f) { (b, c) = (c, b); n = -n; }
            V.Add(a); V.Add(b); V.Add(c); N.Add(n); N.Add(n); N.Add(n); C.Add(col); C.Add(col); C.Add(col);
        }

        public ArrayMesh ToMesh()
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
            arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
            arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            return mesh;
        }
    }

    static Vector3 G(System.Numerics.Vector3 v) => new(v.X, v.Y, v.Z);

    static void AddBox(Soup s, in DressPiece p, Color col)
    {
        float sn = MathF.Sin(p.Yaw), cs = MathF.Cos(p.Yaw), hx = p.Size.X / 2f, hy = p.Size.Y / 2f;
        var b = G(p.Base);
        Vector3 P(float lx, float ly, float z) => new(b.X + cs * lx - sn * ly, b.Y + sn * lx + cs * ly, b.Z + z);
        float h = p.Size.Z;
        Vector3[] lo = [P(-hx, -hy, 0f), P(hx, -hy, 0f), P(hx, hy, 0f), P(-hx, hy, 0f)], hi = [P(-hx, -hy, h), P(hx, -hy, h), P(hx, hy, h), P(-hx, hy, h)];
        var mid = P(0f, 0f, h / 2f);
        s.Tri(hi[0], hi[1], hi[2], mid, col); s.Tri(hi[0], hi[2], hi[3], mid, col);
        s.Tri(lo[0], lo[2], lo[1], mid, col); s.Tri(lo[0], lo[3], lo[2], mid, col);
        for (int k = 0; k < 4; k++)
        {
            int n = (k + 1) % 4;
            s.Tri(lo[k], lo[n], hi[n], mid, col); s.Tri(lo[k], hi[n], hi[k], mid, col);
        }
    }

    /// <summary>A cone (<paramref name="tip"/> true: one point) or a cylinder with <paramref name="sides"/> sides along the piece's axis.</summary>
    static void AddRound(Soup s, in DressPiece p, Color col, int sides, bool tip)
    {
        var axis = G(p.Dir);
        var helper = MathF.Abs(axis.Z) < 0.95f ? Vector3.Up : Vector3.Right;
        if (p.Shape == DressShape.Disc) axis = Vector3.Back;   // Godot's Vector3.Back is +Z: the flat disc stands on the ground (this is game space)
        var u = helper.Cross(axis).Normalized(); var v = axis.Cross(u);
        float r = p.Size.X / 2f, len = p.Size.Z;
        var b = G(p.Base);
        var mid = b + axis * (len / 2f);
        var tipPoint = b + axis * len;
        for (int k = 0; k < sides; k++)
        {
            float a0 = k * MathF.Tau / sides, a1 = (k + 1) * MathF.Tau / sides;
            var p0 = b + (u * MathF.Cos(a0) + v * MathF.Sin(a0)) * r; var p1 = b + (u * MathF.Cos(a1) + v * MathF.Sin(a1)) * r;
            if (tip) { s.Tri(p0, p1, tipPoint, mid, col); }
            else
            {
                var q0 = p0 + axis * len; var q1 = p1 + axis * len;
                s.Tri(p0, p1, q1, mid, col); s.Tri(p0, q1, q0, mid, col);
                s.Tri(q0, q1, tipPoint, mid, col);
            }
            s.Tri(p0, p1, b, mid, col);
        }
    }

    /// <summary>Bakes the pieces into one soup per group; returns group name to soup.</summary>
    public static Dictionary<string, Soup> Soups(IEnumerable<DressPiece> pieces)
    {
        var groups = new Dictionary<string, Soup>();
        foreach (var p in pieces)
        {
            string g = GroupOf(p);
            if (!groups.TryGetValue(g, out var soup)) groups[g] = soup = new Soup();
            var col = ColourOf(p);
            switch (p.Shape)
            {
                case DressShape.Box: AddBox(soup, p, col); break;
                case DressShape.Spike: AddRound(soup, p, col, 5, true); break;
                case DressShape.Pole: AddRound(soup, p, col, 8, false); break;
                default: AddRound(soup, p, col, 24, false); break;
            }
        }
        return groups;
    }

    /// <summary>The groups in the order they are attached (one step each: a material's first draw can cost a pipeline compile, so no frame meets two of them).</summary>
    public static readonly string[] GroupOrder = ["Plated", "Wreck", "Iron", "Cloth", "Bone", "Glow", "DaisDark", "Stone", "Masonry", "Rock", "Gold", "Smoke"];

    /// <summary>Attaches one baked group as a mesh node under <paramref name="parent"/>; returns its triangles (0 when the soup has no such group).</summary>
    public static int AttachGroup(Node3D parent, Dictionary<string, Soup> soups, string group, string name, bool shadows)
    {
        if (!soups.TryGetValue(group, out var soup)) return 0;
        parent.AddChild(new MeshInstance3D
        {
            Name = $"{name}{group}", Mesh = soup.ToMesh(), MaterialOverride = MaterialOf(group),
            CastShadow = shadows && group is not ("Glow" or "DaisDark" or "Smoke") ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
        return soup.Triangles;
    }

    /// <summary>Bakes and attaches everything at once (the far massing's single mesh; returns the triangle count).</summary>
    public static int Attach(Node3D parent, IEnumerable<DressPiece> pieces, string name, bool shadows)
    {
        var soups = Soups(pieces);
        int tris = 0;
        foreach (var g in GroupOrder) tris += AttachGroup(parent, soups, g, name, shadows);
        return tris;
    }

    /// <summary>The ground beyond a map's box (x +-<paramref name="half"/>, y <paramref name="south"/>..<paramref name="north"/>): a grid of quads out to 1400 m, heights from <paramref name="height"/>, toned per quad, so no edge of the world shows from the parapets, the plazas or the spires' side.</summary>
    public static ArrayMesh OutsideGround(float half, float south, float north, Func<float, float, float> height, ref int tris)
    {
        float[] xs = [-1400f, -1000f, -700f, -500f, -350f, -250f, -half, half, 250f, 350f, 500f, 700f, 1000f, 1400f];
        float[] ys = [-1400f, -1000f, -700f, -500f, south - 170f, south - 90f, south, north, north + 120f, north + 220f, 500f, 700f, 1000f, 1400f];
        var soup = new Soup();
        for (int i = 0; i + 1 < xs.Length; i++)
            for (int j = 0; j + 1 < ys.Length; j++)
            {
                float x0 = xs[i], x1 = xs[i + 1], y0 = ys[j], y1 = ys[j + 1];
                if (x0 >= -half && x1 <= half && y0 >= south && y1 <= north) continue;   // the map's own box
                float tone = 0.88f + 0.24f * DressHash.Unit(91, (uint)(i * 31 + j));
                var col = new Color(0.47f * tone, 0.44f * tone, 0.41f * tone);
                var a = new Vector3(x0, y0, height(x0, y0)); var b = new Vector3(x1, y0, height(x1, y0)); var c = new Vector3(x1, y1, height(x1, y1)); var d = new Vector3(x0, y1, height(x0, y1));
                var below = (a + b + c + d) / 4f - Vector3.Back * 5f;
                soup.Tri(a, b, c, below, col); soup.Tri(a, c, d, below, col);
            }
        tris += soup.Triangles;
        return soup.ToMesh();
    }

    /// <summary>A flat or sloped quad (four corners, counter-clockwise from above) in a soup, white; the shader gives the colour.</summary>
    public static void Quad(Soup s, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        var below = (a + b + c + d) / 4f - Vector3.Back * 5f;
        s.Tri(a, b, c, below, Colors.White); s.Tri(a, c, d, below, Colors.White);
    }
}
