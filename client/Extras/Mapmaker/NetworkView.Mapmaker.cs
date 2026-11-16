using System.Linq;
using Godot;
using SmContent;
using SmMapmaker;

namespace SmClient;

/// <summary>
/// Mapmaker maps on the client (added content, docs/mapmaker.md "Playing a map"): a map the server names <c>cm_...</c> is a <c>.smmap</c> file found by that name (or given with
/// <c>--custom-map</c>) and compiled here, with the same code the server ran, into the collision the client predicts with. The core's <c>LoadMap</c> asks <see cref="MapmakerContent"/> for the map's
/// content (nothing is read from the install) and <see cref="MapmakerVisuals"/> to draw it (<see cref="CustomMapView"/>). The handshake's map hash is the first 8 bytes of SHA-256(compiler version + the file's canonical JSON), so a different copy is refused by the server.
/// </summary>
public partial class NetworkView
{
    CustomMap? _customMap;
    static readonly string? CustomMapFile = CmdArg("custom-map");

    /// <summary>A custom map has no install file: "none" makes the readers of map files (sounds, effects) skip.</summary>
    void MapmakerMapFile(ref string file)
    {
        if (CustomMap.IsCustomName(file)) file = "none";
    }

    void MapmakerContent(string name, ref MapContent? map)
    {
        if (!CustomMap.IsCustomName(name)) return;
        try { _customMap = CustomMaps.LoadForPlay(name, CustomMapFile); }
        catch (MapException e) { throw new System.InvalidOperationException(e.Message); }
        map = _customMap.Content;
        GD.Print($"custom map {name}: {_customMap.SolidCount} solid pieces, {map.StartAreas.Count} start areas, {map.ControlPoints.Count} capture points; hash {_customMap.Hash:X16}; nothing read from the install");
    }

    void MapmakerVisuals(string name, ref bool drawn)
    {
        if (!CustomMap.IsCustomName(name) || _customMap is null || _gameSpace is null) return;
        try
        {
            var view = CustomMapView.Build(_customMap);
            _gameSpace.AddChild(view);
            if (_sun is not null && _environment is not null) CustomMapView.ApplyLook(_sun, _environment);
            drawn = true;
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"custom map: the view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
    }
}

/// <summary>The drawing of a custom map: its solid pieces as flat-shaded meshes in game space (Z up) with a world grid texture so the shapes read, and a light look.</summary>
public static class CustomMapView
{
    public static Node3D Build(CustomMap map)
    {
        var view = new Node3D { Name = "MapVisuals" };
        var material = Material();
        foreach (var p in map.Pieces)
        {
            if (!p.Visible || p.Piece.Info.Solid is false || p.Piece.Kind == PieceKind.CollisionBox) continue;
            var (v, tri) = PieceMesh.Unit(p.Piece.Kind)!.Value;
            var corners = new System.Numerics.Vector3[tri.Length];
            for (int i = 0; i < tri.Length; i++) corners[i] = System.Numerics.Vector3.Transform(v[tri[i]], p.World);
            view.AddChild(new MeshInstance3D { Name = $"{p.Id}", Mesh = Flat(corners), MaterialOverride = material });
        }
        return view;
    }

    static StandardMaterial3D Material()
    {
        var image = Image.CreateEmpty(64, 64, true, Image.Format.Rgba8);
        image.Fill(new Color(0.46f, 0.48f, 0.52f));
        for (int i = 0; i < 64; i++) { image.SetPixel(i, 0, new Color(0.28f, 0.3f, 0.34f)); image.SetPixel(0, i, new Color(0.28f, 0.3f, 0.34f)); }
        image.GenerateMipmaps();
        return new StandardMaterial3D
        {
            AlbedoTexture = ImageTexture.CreateFromImage(image), Uv1Triplanar = true, Uv1WorldTriplanar = true, Uv1Scale = new Vector3(0.25f, 0.25f, 0.25f),
            Roughness = 0.95f, CullMode = BaseMaterial3D.CullModeEnum.Disabled, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
    }

    /// <summary>Flat-shaded triangles (three corners each) in game space; both sides drawn, so the winding does not matter.</summary>
    static ArrayMesh Flat(System.Numerics.Vector3[] corners)
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

    /// <summary>A plain daylight look (ours): a warm sun and a pale sky, so the graybox reads clearly.</summary>
    public static void ApplyLook(DirectionalLight3D sun, Godot.Environment env)
    {
        sun.LightColor = new Color(1f, 0.95f, 0.88f);
        sun.LightEnergy = 0.85f;
        sun.DirectionalShadowMaxDistance = 140f;
        sun.ShadowEnabled = true;
        env.BackgroundColor = new Color(0.55f, 0.62f, 0.72f);
        env.AmbientLightColor = new Color(0.7f, 0.72f, 0.78f);
        env.AmbientLightEnergy = 0.55f;
        env.FogEnabled = false;
    }
}

