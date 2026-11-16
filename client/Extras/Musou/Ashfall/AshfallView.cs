using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim.Extras.Musou.Ashfall;

namespace SmClient;

/// <summary>
/// The graybox drawing of Ashfall Citadel (G1): one mesh per <see cref="AshfallKind"/> with flat normals and a world-space 1 m checker plus a stronger 4 m grid line, so scale reads. Lives in game space
/// (X east, Y north, Z up; the parent converts). Every colour and the look's numbers are ours (placeholder demonstration, refine register).
/// </summary>
public partial class AshfallView : Node3D
{
    // ours: colour per kind (readable and distinct on a grey scale)
    static Color ColourOf(AshfallKind k) => k switch
    {
        AshfallKind.Ground => new Color(0.52f, 0.49f, 0.45f),
        AshfallKind.Road => new Color(0.34f, 0.32f, 0.30f),
        AshfallKind.Paving => new Color(0.68f, 0.65f, 0.60f),
        AshfallKind.Curb => new Color(0.62f, 0.60f, 0.57f),
        AshfallKind.Wall => new Color(0.50f, 0.50f, 0.52f),
        AshfallKind.Cover => new Color(0.74f, 0.74f, 0.75f),
        AshfallKind.Rubble => new Color(0.46f, 0.40f, 0.35f),
        AshfallKind.Floor => new Color(0.44f, 0.52f, 0.62f),
        AshfallKind.Stair => new Color(0.78f, 0.62f, 0.25f),
        AshfallKind.Perch => new Color(0.92f, 0.50f, 0.15f),
        AshfallKind.Stronghold => new Color(0.42f, 0.24f, 0.24f),
        AshfallKind.Boundary => new Color(0.20f, 0.19f, 0.19f),
        AshfallKind.Backdrop => new Color(0.16f, 0.16f, 0.17f),
        _ => new Color(1, 0, 1),
    };

    static readonly Color[] ObjectiveColours = [new Color(0.95f, 0.75f, 0.15f), new Color(0.3f, 0.8f, 0.95f), new Color(0.9f, 0.35f, 0.75f)];
    static readonly string[] ObjectiveLetters = ["A", "B", "C"];

    /// <summary>Parts, triangles and build time of the last build.</summary>
    public static (int Parts, int Triangles, long Ms) Last { get; private set; }

    static ImageTexture? _checker;

    /// <summary>A 4 m tile: 1 m checker squares and a darker line along the tile's edge (the 4 m grid). 16 px a metre.</summary>
    static ImageTexture Checker()
    {
        if (_checker is not null) return _checker;
        const int N = 64, M = 16;
        var img = Image.CreateEmpty(N, N, true, Image.Format.Rgb8);
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
            {
                bool odd = ((x / M) + (y / M)) % 2 == 1;
                float v = odd ? 0.88f : 1.0f;
                if (x < 2 || y < 2) v = 0.45f;   // the 4 m grid line
                img.SetPixel(x, y, new Color(v, v, v));
            }
        img.GenerateMipmaps();
        return _checker = ImageTexture.CreateFromImage(img);
    }

    public static AshfallView Build(AshfallLevel level)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var view = new AshfallView { Name = "MapVisuals" };
        int triangles = 0;
        foreach (var group in level.Parts.Where(p => p.Kind != AshfallKind.Objective).GroupBy(p => p.Kind))
        {
            var corners = group.SelectMany(p => p.Corners).ToArray();
            triangles += corners.Length / 3;
            var material = new StandardMaterial3D
            {
                AlbedoColor = ColourOf(group.Key),
                AlbedoTexture = Checker(),
                TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
                Uv1Triplanar = true,
                Uv1WorldTriplanar = true,
                Uv1Scale = new Vector3(0.25f, 0.25f, 0.25f),   // one texture tile = 4 m
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                Roughness = 1f,
            };
            view.AddChild(new MeshInstance3D { Name = group.Key.ToString(), Mesh = MeshOf(corners), MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.On });
        }
        // Objective discs: unshaded floor markers, each in its point's colour, with a letter above.
        var objectives = level.Parts.Where(p => p.Kind == AshfallKind.Objective).ToList();
        for (int i = 0; i < level.Points.Count; i++)
        {
            var c = level.Points[i].Center;
            var mine = objectives.Where(p => Nearest(level, p) == i).SelectMany(p => p.Corners).ToArray();
            triangles += mine.Length / 3;
            var col = ObjectiveColours[i % ObjectiveColours.Length];
            string letter = ObjectiveLetters[i % ObjectiveLetters.Length];
            if (mine.Length > 0)
                view.AddChild(new MeshInstance3D
                {
                    Name = "Objective" + letter,
                    Mesh = MeshOf(mine),
                    MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(col, 0.8f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha, CullMode = BaseMaterial3D.CullModeEnum.Disabled },
                    CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                });
            // Game space is Z up, so the label is placed in game coordinates and faces the camera.
            view.AddChild(new Label3D
            {
                Name = "Letter" + letter, Text = letter, FontSize = 256, PixelSize = 0.03f, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Modulate = col, OutlineSize = 24, Shaded = false, Position = new Vector3(c.X, c.Y, c.Z + 9f),
            });
        }
        Last = (level.Parts.Count, triangles, watch.ElapsedMilliseconds);
        GD.Print($"ashfall: {Last.Parts} parts, {Last.Triangles:N0} triangles, built in {Last.Ms} ms");
        return view;
    }

    static int Nearest(AshfallLevel level, AshfallPart part)
    {
        var s = System.Numerics.Vector3.Zero;
        foreach (var v in part.Corners) s += v;
        s /= Math.Max(1, part.Corners.Length);
        int best = 0; float bestD = float.MaxValue;
        for (int i = 0; i < level.Points.Count; i++)
        {
            var d = level.Points[i].Center - s;
            float dd = d.X * d.X + d.Y * d.Y;
            if (dd < bestD) { bestD = dd; best = i; }
        }
        return best;
    }

    /// <summary>Flat-shaded mesh of triangles (three corners each) in game space. Both sides are drawn (cull off), so the winding does not matter; the second and third corner are swapped as the core's collision fallback does.</summary>
    internal static ArrayMesh MeshOf(System.Numerics.Vector3[] corners)
    {
        var vertices = new Vector3[corners.Length];
        var normals = new Vector3[corners.Length];
        for (int t = 0; t + 2 < corners.Length; t += 3)
        {
            var a = corners[t]; var b = corners[t + 1]; var c = corners[t + 2];
            var n = System.Numerics.Vector3.Normalize(System.Numerics.Vector3.Cross(b - a, c - a));
            if (!float.IsFinite(n.X)) n = System.Numerics.Vector3.UnitZ;
            vertices[t] = new(a.X, a.Y, a.Z); vertices[t + 1] = new(c.X, c.Y, c.Z); vertices[t + 2] = new(b.X, b.Y, b.Z);
            normals[t] = normals[t + 1] = normals[t + 2] = new(n.X, n.Y, n.Z);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>The provisional look tuned toward ash: ours (a sun dimmed and cooled, an ash-grey sky and fog). The shadow reach is widened for the larger level.</summary>
    public static void ApplyLook(DirectionalLight3D sun, Godot.Environment env)
    {
        sun.LightColor = new Color(0.95f, 0.88f, 0.78f);
        sun.LightEnergy = 1.2f;
        sun.DirectionalShadowMaxDistance = 160f;
        sun.ShadowEnabled = true;
        env.BackgroundColor = new Color(0.46f, 0.45f, 0.44f);
        env.AmbientLightColor = new Color(0.6f, 0.6f, 0.62f);
        env.AmbientLightEnergy = 1.0f;
        env.FogEnabled = true;
        env.FogLightColor = new Color(0.5f, 0.49f, 0.48f);
        env.FogDensity = 0.0035f;
    }
}
