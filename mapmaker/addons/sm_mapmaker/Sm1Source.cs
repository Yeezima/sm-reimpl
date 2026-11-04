using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient;
using SmContent;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// One SM1 map read from the owner's install into memory (<see cref="MapScene"/>). Nothing here is written to disk: the meshes and textures live
/// as Godot resources of this process and are attached to view nodes that have no owner, so no scene file can serialise them.
/// </summary>
public sealed class Sm1Source
{
    public string Map { get; }
    public MapScene Scene { get; }
    /// <summary>Placements the client does not draw either (armoury wall stations on Manufactorum).</summary>
    public HashSet<int> Hidden { get; }
    readonly Dictionary<string, MapView?> _templates = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, int), (Num.Vector3 Min, Num.Vector3 Max)?> _blockBounds = new();
    readonly Dictionary<string, (Num.Vector3 Min, Num.Vector3 Max)?> _definitionBounds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);
    Sm1Pick[]? _picks;

    Sm1Source(string map, MapScene scene)
    {
        Map = map;
        Scene = scene;
        Hidden = MapDecor.Hidden(scene);
        for (int i = 0; i < scene.Objects.Count; i++)
            if (!Hidden.Contains(i) && scene.Objects[i].Name.Length > 0)
            {
                if (!_byName.TryGetValue(scene.Objects[i].Name, out var list)) _byName[scene.Objects[i].Name] = list = new List<int>();
                list.Add(i);
            }
    }

    /// <summary>The install folder (explicit setting, SM_DATA_DIR, saved settings, Steam, the git-ignored config/local.json read by GameInstall), or null.</summary>
    public static string? GameDir()
    {
        string repo = System.IO.Path.GetFullPath(System.IO.Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
        return GameInstall.TryLocate(null, repo)?.Dir;
    }

    /// <summary>Reads the map's region archive. Slow (seconds); safe to call off the main thread (no Godot objects are made).</summary>
    public static Sm1Source Load(string map)
    {
        string dir = GameDir() ?? throw new InvalidOperationException(GameInstall.NotFoundMessage);
        return new Sm1Source(map, MapScene.Load(dir, map));
    }

    /// <summary>Whole-map view for the backdrop (multimeshes). Add it to the tree without an owner.</summary>
    public MapView BuildBackdrop(List<string> notes) => MapView.Build(Scene, notes);

    public IEnumerable<string> DefinitionNames => Scene.Definitions.Keys.Order(StringComparer.OrdinalIgnoreCase);
    public int CountOf(string definition) => Scene.Objects.Where((o, i) => !Hidden.Contains(i) && o.Definition.Equals(definition, StringComparison.OrdinalIgnoreCase)).Count();
    public bool HasDefinition(string name) => Scene.Definitions.TryGetValue(name, out var d) && d.NearestLod().Any();

    /// <summary>The placement named <paramref name="name"/> (object names are unique in a layer, so the first), or -1.</summary>
    public int CountNamed(string name) => _byName.TryGetValue(name, out var l) ? l.Count : 0;
    public int IndexOf(string name) => _byName.TryGetValue(name, out var l) ? l[0] : -1;

    // ---- definition content ----------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A fresh node with the definition's parts in the game frame (an object's own frame, the pivot being the definition's origin). Meshes and
    /// materials are shared between all instances of the definition. Null when no part can be drawn.
    /// </summary>
    public Node3D? Instantiate(string definition)
    {
        if (!_templates.TryGetValue(definition, out var template))
        {
            template = null;
            if (Scene.Definitions.TryGetValue(definition, out var def) && def.NearestLod().Any())
            {
                var one = new MapScene
                {
                    Map = Scene.Map,
                    Objects = [new PlacedObject("preview", def.Name, "preview", Num.Matrix4x4.Identity)],
                    Definitions = new Dictionary<string, ObjectDefinition>(StringComparer.OrdinalIgnoreCase) { [def.Name] = def },
                    Meshes = Scene.Meshes, Materials = Scene.Materials, Textures = Scene.Textures, Notes = [],
                };
                try { template = MapView.Build(one, new List<string>()); }
                catch (Exception) { template = null; }
                if (template is { Instances: 0 }) { template.Free(); template = null; }
            }
            _templates[definition] = template;
        }
        if (template is null) return null;
        var root = new Node3D { Name = definition };
        foreach (var child in template.GetChildren().OfType<MultiMeshInstance3D>())
            root.AddChild(new MultiMeshInstance3D { Name = child.Name, Multimesh = child.Multimesh, CastShadow = child.CastShadow });
        return root;
    }

    /// <summary>Bounds of a definition's drawn parts in its own frame (game frame), or null.</summary>
    public (Num.Vector3 Min, Num.Vector3 Max)? BoundsOf(string definition)
    {
        if (_definitionBounds.TryGetValue(definition, out var cached)) return cached;
        (Num.Vector3 Min, Num.Vector3 Max)? result = null;
        if (Scene.Definitions.TryGetValue(definition, out var def))
            foreach (var part in def.NearestLod())
                if (BlockBounds(part.MeshName, part.MeshId) is { } b)
                    result = Union(result, Transformed(b, part.NodeTransform));
        _definitionBounds[definition] = result;
        return result;
    }

    (Num.Vector3 Min, Num.Vector3 Max)? BlockBounds(string mesh, int block)
    {
        if (_blockBounds.TryGetValue((mesh.ToLowerInvariant(), block), out var cached)) return cached;
        (Num.Vector3 Min, Num.Vector3 Max)? result = null;
        if (Scene.Meshes.TryGetValue(mesh, out var dcm) && block >= 0 && block < dcm.Blocks.Count && dcm.Blocks[block].Positions.Length > 0)
        {
            var min = new Num.Vector3(float.MaxValue); var max = new Num.Vector3(float.MinValue);
            foreach (var p in dcm.Blocks[block].Positions) { min = Num.Vector3.Min(min, p); max = Num.Vector3.Max(max, p); }
            result = (min, max);
        }
        _blockBounds[(mesh.ToLowerInvariant(), block)] = result;
        return result;
    }

    static (Num.Vector3, Num.Vector3) Transformed((Num.Vector3 Min, Num.Vector3 Max) b, Num.Matrix4x4 m)
    {
        var min = new Num.Vector3(float.MaxValue); var max = new Num.Vector3(float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var c = Num.Vector3.Transform(new Num.Vector3((i & 1) == 0 ? b.Min.X : b.Max.X, (i & 2) == 0 ? b.Min.Y : b.Max.Y, (i & 4) == 0 ? b.Min.Z : b.Max.Z), m);
            min = Num.Vector3.Min(min, c); max = Num.Vector3.Max(max, c);
        }
        return (min, max);
    }

    static (Num.Vector3, Num.Vector3) Union((Num.Vector3 Min, Num.Vector3 Max)? a, (Num.Vector3 Min, Num.Vector3 Max) b) =>
        a is { } x ? (Num.Vector3.Min(x.Min, b.Min), Num.Vector3.Max(x.Max, b.Max)) : b;

    // ---- picking ---------------------------------------------------------------------------------------------------------------------------------------

    sealed record Sm1Pick(int Object, Num.Matrix4x4 Inverse, Num.Vector3 Min, Num.Vector3 Max);

    Sm1Pick[] Picks()
    {
        if (_picks is not null) return _picks;
        var list = new List<Sm1Pick>();
        for (int o = 0; o < Scene.Objects.Count; o++)
        {
            if (Hidden.Contains(o) || !Scene.Definitions.TryGetValue(Scene.Objects[o].Definition, out var def)) continue;
            foreach (var part in def.NearestLod())
            {
                if (BlockBounds(part.MeshName, part.MeshId) is not { } b) continue;
                var world = part.NodeTransform * Scene.Objects[o].Transform;
                if (Num.Matrix4x4.Invert(world, out var inverse)) list.Add(new Sm1Pick(o, inverse, b.Min, b.Max));
            }
        }
        return _picks = list.ToArray();
    }

    /// <summary>The nearest placement under a ray given in the game frame (oriented boxes of each drawn part), or -1.</summary>
    public int RayPick(Num.Vector3 origin, Num.Vector3 direction, out float distance)
    {
        int best = -1; distance = float.MaxValue;
        foreach (var p in Picks())
        {
            var o = Num.Vector3.Transform(origin, p.Inverse);
            var d = Num.Vector3.TransformNormal(direction, p.Inverse);
            if (RayBox(o, d, p.Min, p.Max, out float t) && t < distance) { distance = t; best = p.Object; }
        }
        return best;
    }

    static bool RayBox(Num.Vector3 o, Num.Vector3 d, Num.Vector3 min, Num.Vector3 max, out float t)
    {
        float t0 = 0f, t1 = float.MaxValue;
        t = 0f;
        for (int a = 0; a < 3; a++)
        {
            float oa = a == 0 ? o.X : a == 1 ? o.Y : o.Z, da = a == 0 ? d.X : a == 1 ? d.Y : d.Z, lo = a == 0 ? min.X : a == 1 ? min.Y : min.Z, hi = a == 0 ? max.X : a == 1 ? max.Y : max.Z;
            if (MathF.Abs(da) < 1e-9f) { if (oa < lo || oa > hi) return false; continue; }
            float n = (lo - oa) / da, f = (hi - oa) / da;
            if (n > f) (n, f) = (f, n);
            t0 = MathF.Max(t0, n); t1 = MathF.Min(t1, f);
            if (t0 > t1) return false;
        }
        t = t0;
        return true;
    }

    /// <summary>Box of every drawn placement (game frame), or null.</summary>
    public (Num.Vector3 Min, Num.Vector3 Max)? BoundsAll()
    {
        (Num.Vector3 Min, Num.Vector3 Max)? r = null;
        for (int i = 0; i < Scene.Objects.Count; i++)
            if (!Hidden.Contains(i) && WorldBounds(i) is { } b) r = Union(r, b);
        return r;
    }

    /// <summary>World box of a placement (game frame), or null when none of its parts is drawn.</summary>
    public (Num.Vector3 Min, Num.Vector3 Max)? WorldBounds(int obj)
    {
        var placed = Scene.Objects[obj];
        if (!Scene.Definitions.TryGetValue(placed.Definition, out var def)) return null;
        (Num.Vector3 Min, Num.Vector3 Max)? result = null;
        foreach (var part in def.NearestLod())
            if (BlockBounds(part.MeshName, part.MeshId) is { } b) result = Union(result, Transformed(b, part.NodeTransform * placed.Transform));
        return result;
    }
}
