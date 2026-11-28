using System.Linq;
using Godot;
using SmContent;
using SmMapmaker;

namespace SmClient;

/// <summary>
/// Mapmaker maps on the client (added content, docs/mapmaker.md "Playing a map"): a map the server names <c>cm_...</c> is a <c>.smmap</c> file found by that name (or given with
/// <c>--custom-map</c>) and compiled here, with the same code the server ran, into the collision the client predicts with. The core's <c>LoadMap</c> asks <see cref="MapmakerContent"/> for the map's
/// content (nothing is read from the install) and <see cref="MapmakerVisuals"/> to draw it (<see cref="CustomMapView"/>). The handshake's map hash is the first 8 bytes of SHA-256(compiler version + the file's canonical JSON), so a different copy is refused by the server.
/// <para>
/// Which file (devtools-god-custommap, 2026-11-28): the file the developer tools picked, when the server named it before the switch (<see cref="ExtrasDevNextMap"/>; a file name looked up only in the
/// picker's folder, <see cref="CustomMaps.ResolvePick"/>) and it holds the map the server now plays; else the <c>--custom-map</c> file, as long as it holds that map (after a switch to another map it no
/// longer does, and the client says so and finds the new one by name instead of refusing); else the newest file in the search folders with that map's name.
/// </para>
/// </summary>
public partial class NetworkView
{
    CustomMap? _customMap;
    string? _customMapPath;
    static readonly string? CustomMapFile = CmdArg("custom-map");

    /// <summary>
    /// The full path of the map file the developer tools picked last (from the server's state before the switch); kept across the rejoin's new view, dropped once another map loads, when the server
    /// withdraws the pick, when a load or the join is refused, and when the view goes back to the menu (<see cref="MapmakerExit"/>).
    /// </summary>
    static string? _devPickedFile;

    /// <summary>The map file this client plays now (null on a Classic map): the F6 panel's CUSTOM MAP row marks it.</summary>
    public static string? CurrentCustomMapFile { get; private set; }

    /// <summary>
    /// The server's state named the picked map file (empty: the pick was withdrawn, or the join was refused); called only for a server on a loopback address, or for a refused join (NetworkView.DevTools.cs).
    /// </summary>
    partial void ExtrasDevNextMap(string file)
    {
        if (file.Length == 0)
        {
            if (_devPickedFile is not null)
                GD.Print(_client?.Status == SmNet.ClientStatus.Rejected
                    ? $"custom map: the server refused the join, so the developer tools' pick {System.IO.Path.GetFileName(_devPickedFile)} is forgotten; a new join finds the map by its usual file"
                    : "custom map: the developer tools' pick was withdrawn (the match went on); the rejoin uses its usual file");
            _devPickedFile = null;
            return;
        }
        string? path = CustomMaps.ResolvePick(file);
        if (path is null) { _devPickedFile = null; GD.PrintErr($"custom map: the developer tools picked {file}, which is not in {CustomMaps.PickDir()} on this PC; the rejoin finds the map by its name"); return; }
        _devPickedFile = path;
        GD.Print($"custom map: the developer tools picked {file}; the rejoin loads that file");
    }

    /// <summary>The view leaves the tree: unless it is replaced by a rotation's rejoin, the remembered pick is dropped (back to the menu, a refused join, a quit).</summary>
    void MapmakerExit()
    {
        if (RotateRequested || RotationConnectionLost) return;   // the rejoin's view still needs it
        _devPickedFile = null;
        CurrentCustomMapFile = null;
    }

    /// <summary>The map file to load for the server's map <paramref name="name"/>, or null to find it by name in the search folders (<see cref="CustomMaps.PlayFile"/>; the order is in the class summary).</summary>
    static string? CustomMapFileFor(string name, out CustomMaps.PlaySource source)
    {
        string? file = CustomMaps.PlayFile(name, _devPickedFile, CustomMapFile, out source, out bool spent, out string? held);
        if (spent) _devPickedFile = null;   // another map now: the pick is spent
        if (held is not null)
            GD.Print($"custom map {name}: --custom-map {System.IO.Path.GetFileName(CustomMapFile)} holds {held}, not the map the server plays now; finding {name} by its name instead");
        return file;
    }

    /// <summary>A custom map has no install file: "none" makes the readers of map files (sounds, effects) skip.</summary>
    void MapmakerMapFile(ref string file)
    {
        if (CustomMap.IsCustomName(file)) file = "none";
    }

    void MapmakerContent(string name, ref MapContent? map)
    {
        CurrentCustomMapFile = null;
        if (!CustomMap.IsCustomName(name)) { _devPickedFile = null; return; }
        try
        {
            string? file = CustomMapFileFor(name, out var source);
            if (file is null && CustomMaps.LocateAll(name) is { Count: > 1 } many)
                GD.PrintErr($"custom map {name}: {many.Count} files have this map's name ({string.Join(", ", many.Select(System.IO.Path.GetFileName))}); using the newest, {System.IO.Path.GetFileName(many[0])}. Start with --custom-map <file> to name the exact one.");
            // SM1 pieces, prefabs and imported models come from this PC's install and library: solid here exactly as on the server, and drawn below (a PC that lacks one refuses, see CustomMapDecor).
            var started = System.Diagnostics.Stopwatch.StartNew();
            _customMapPath = file ?? CustomMaps.Locate(name);
            GD.Print($"custom map {name}: file {(_customMapPath is null ? "(none found)" : System.IO.Path.GetFileName(_customMapPath))}{source switch { CustomMaps.PlaySource.Picked => " (picked with the developer tools)", CustomMaps.PlaySource.Given => " (--custom-map)", _ => " (found by its name)" }}");
            _customMap = CustomMaps.LoadForPlay(name, _customMapPath, CompileOptions.ForPlay(_gameDir, textures: true));
            CurrentCustomMapFile = _customMapPath is null ? null : System.IO.Path.GetFullPath(_customMapPath);
            if (_customMap.Decor is { Placements.Count: > 0 } decor)
                GD.Print($"custom map {name}: {decor.Placements.Count} SM1 / model placements, {decor.Triangles:N0} solid triangles from this install ({started.Elapsed.TotalSeconds:0.0} s)");
        }
        catch (MapException e) { _devPickedFile = null; throw new System.InvalidOperationException(e.Message); }   // a refused load forgets the pick
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.InvalidProgramException or System.InvalidOperationException))
        {
            _devPickedFile = null;
            throw new System.InvalidOperationException($"The custom map could not be compiled ({e.GetType().Name}: {e.Message}).");
        }
        // The map's look (docs/mapmaker.md "Look in play"): a PC without that Classic map refuses the map here, before the Hello is sent (owner 2026-11-20: no daylight fallback). A lean bot draws nothing and does not need it.
        if (_customMap.Document.Look is { } look && !Lean && LookCheck.Refusal(_gameDir, look.Sm1Map, LookDisplayName(look.Sm1Map)) is { } refusal)
        {
            _devPickedFile = null;
            _mapRefusal = refusal;   // the join's plain error (the server only sees a map it never got a hash for)
            throw new System.InvalidOperationException(refusal);
        }
        map = _customMap.Content;
        GD.Print($"custom map {name}: {_customMap.SolidCount} solid pieces, {map.StartAreas.Count} start areas, {map.ControlPoints.Count} capture points; hash {_customMap.Hash:X16}; nothing read from the install");
    }

    /// <summary>The scene of the Classic map a custom map is drawn with (read on the worker by <see cref="ExtrasMapLook"/>; null: no look set, plain daylight).</summary>
    MapScene? _lookScene;

    /// <summary>The map's name as the install's front end lists it (the archive name when it cannot be read); only for the refusal's sentence.</summary>
    string? LookDisplayName(string map)
    {
        try { return MapCatalog.Read(_gameDir).FirstOrDefault(m => m.Name == map)?.DisplayName; }
        catch (System.Exception e) when (e is System.IO.IOException or System.IO.InvalidDataException or System.ArgumentException or System.NotSupportedException or System.UnauthorizedAccessException) { return null; }
    }

    /// <summary>On the worker: reads the look map's scene (sun, regions, fog, colour grade, decals, sky and meshes) like Classic's <c>LoadScene</c>. A scene that cannot be read refuses the map like a missing one.</summary>
    partial void ExtrasMapLook(string name)
    {
        _lookScene = null;
        if (!CustomMap.IsCustomName(name) || _customMap?.Document.Look is not { } look) return;
        try { _lookScene = MapScene.Load(_gameDir, look.Sm1Map, extendedLighting: !MapDecor.OldMapLighting); }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException or System.OutOfMemoryException))
        {
            string reason = $"This map uses the look of {LookDisplayName(look.Sm1Map) ?? look.Sm1Map}, which could not be read from your game install ({e.GetType().Name}: {e.Message}).";
            _mapRefusal = reason;
            throw new System.InvalidOperationException(reason);
        }
    }

    void MapmakerVisuals(string name, ref bool drawn)
    {
        if (!CustomMap.IsCustomName(name) || _customMap is null || _gameSpace is null) return;
        try
        {
            var view = CustomMapView.Build(_customMap, _customMapPath ?? CustomMaps.Locate(name));
            _gameSpace.AddChild(view);
            if (_lookScene is null && _sun is not null && _environment is not null) CustomMapView.ApplyLook(_sun, _environment);
            drawn = true;
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.Print($"custom map: the view failed ({e.GetType().Name}: {e.Message}); the grey collision is drawn instead");
        }
        if (_lookScene is { } look)
        {
            // The original look of the Classic map (the same code Classic uses): its sun, ambient, fog, regions and grade, the sky, the void floor, the decals and the ortho lights. Its geometry is not drawn.
            try
            {
                var notes = new System.Collections.Generic.List<string>();
                string lightingNote = "lighting: the original map's";
                float? floor = _customMap.SolidBounds is { } b ? (float?)b.Min.Z : null;
                var lookView = MapView.BuildLook(look, floor, notes);
                _gameSpace.AddChild(lookView);
                ApplyMapLighting(look, notes, ref lightingNote);
                GD.Print($"custom map look of {look.Map}: {lightingNote}; {lookView.LevelDecals} decals, {lookView.OrthoLights} ortho lights");
                foreach (var note in notes) GD.Print("  look note: " + note);
            }
            catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
            {
                GD.PrintErr($"custom map: the Classic look could not be drawn ({e.GetType().Name}: {e.Message}); the plain daylight look stays");
                if (_sun is not null && _environment is not null) CustomMapView.ApplyLook(_sun, _environment);
            }
            _lookScene = null;   // drawn: drop the source scene's meshes and textures for the rest of the match (review)
        }
        try
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var notes = new System.Collections.Generic.List<string>();
            if (CustomMapDecor.Build(_customMap, notes) is { } decoration)
            {
                _gameSpace.AddChild(decoration);
                GD.Print($"custom map decoration built in {started.Elapsed.TotalSeconds:0.00} s");
            }
            foreach (var note in notes) GD.Print("custom map decoration: " + note);
        }
        catch (System.Exception e) when (e is not (System.StackOverflowException or System.AccessViolationException))
        {
            GD.PrintErr($"custom map: the decoration could not be drawn ({e.GetType().Name}: {e.Message}); its collision is still solid");
        }
    }
}

/// <summary>
/// The drawing of a custom map: its solid pieces as flat-shaded meshes in game space (Z up) with a world grid texture so the shapes read, and a light look. A piece whose shape was
/// edited, or that has a material, is drawn from the core's <see cref="DrawPiece.Render"/> (its edited shape, normals, tangents and UVs: what the editor shows is what plays;
/// docs/mapmaker.md "Edit mode in play"); an unedited piece without a material keeps the stock path, which draws the same shape.
/// </summary>
public static class CustomMapView
{
    /// <summary>The map's view; <paramref name="file"/> is the map file (a material's pictures are read from beside it first, then the library).</summary>
    public static Node3D Build(CustomMap map, string? file = null)
    {
        var view = new Node3D { Name = "MapVisuals" };
        var material = Material();
        var mats = new MaterialBuild.Cache(map.Document, file, new MapLibrary(null));   // each material and picture built once (M3's shared builder: preview = saved = in game)
        foreach (var p in map.Pieces)
        {
            if (!p.Visible || p.Piece.Info.Solid is false || p.Piece.Kind == PieceKind.CollisionBox) continue;
            if (p.Mesh is not null || p.Material is not null)
            {
                // an edited shape or a material: the core's render mesh (edited shape, normals, tangents, UVs) with the material's surface, or the grid look
                Godot.Material surface = (p.Material is null ? null : (Godot.Material?)mats.For(p.Material)) ?? material;
                view.AddChild(new MeshInstance3D { Name = $"{p.Id}", Mesh = MaterialBuild.Mesh(p.Render()), MaterialOverride = surface });
                continue;
            }
            var (v, tri) = PieceMesh.Unit(p.Piece.Kind)!.Value;
            var corners = new System.Numerics.Vector3[tri.Length];
            for (int i = 0; i < tri.Length; i++) corners[i] = System.Numerics.Vector3.Transform(v[tri[i]], p.World);
            view.AddChild(new MeshInstance3D { Name = $"{p.Id}", Mesh = Flat(corners), MaterialOverride = material });
        }
        foreach (var note in mats.Notes) GD.Print("custom map material: " + note);
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

