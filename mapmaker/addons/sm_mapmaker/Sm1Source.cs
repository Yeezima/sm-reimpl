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
    readonly Dictionary<int, bool> _composite = new();
    readonly Dictionary<(string, int), (Num.Vector3 Min, Num.Vector3 Max)?> _blockBounds = new();
    readonly Dictionary<string, (Num.Vector3 Min, Num.Vector3 Max)?> _definitionBounds = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<int>> _byName = new(StringComparer.OrdinalIgnoreCase);
    PartBox[]? _picks;

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

    /// <summary>Frees the cached view templates.</summary>
    public void Release()
    {
        foreach (var t in _templates.Values) if (t is not null && GodotObject.IsInstanceValid(t)) t.Free();
        _templates.Clear();
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
    public Node3D? Instantiate(string definition, int? part = null)
    {
        string key = part is { } pk ? $"{definition}#{pk}" : definition;
        if (!_templates.TryGetValue(key, out var template))
        {
            template = null;
            if (Scene.Definitions.TryGetValue(definition, out var whole) && whole.NearestLod().Any()
                && (part is not { } pi || pi < whole.NearestLod().Count()))
            {
                // One part: a definition holding only that visual (NearestLod of a single visual is itself).
                var def = part is { } p ? whole with { Visuals = [whole.NearestLod().ElementAt(p)] } : whole;
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
            _templates[key] = template;
        }
        if (template is null) return null;
        var root = new Node3D { Name = key.Replace('#', '_') };
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

    sealed record PartBox(int Object, int Part, Num.Matrix4x4 Inverse, Num.Vector3 Min, Num.Vector3 Max, string Mesh, int Block);

    /// <summary>
    /// A placement picked part by part rather than whole: SM1 bakes much of a map into a few placements (Manufactorum's tarmacgroundplane holds
    /// the floor, buildings, gates and cargo cages in about 48 parts), so picking it whole made every click on those structures pick "the floor"
    /// (owner's report 2026-11-05). Rule (ours): 8 or more drawn parts, or wider than 40 m.
    /// </summary>
    public bool IsComposite(int obj)
    {
        if (_composite.TryGetValue(obj, out bool c)) return c;
        int parts = Scene.Definitions.TryGetValue(Scene.Objects[obj].Definition, out var def) ? def.NearestLod().Count() : 0;
        c = parts >= 8 || (WorldBounds(obj) is { } b && MathF.Max(b.Max.X - b.Min.X, b.Max.Y - b.Min.Y) > 40f);
        return _composite[obj] = c;
    }

    /// <summary>What a click picks at this ray: the part for a composite placement, else the whole placement; null when nothing is hit.</summary>
    public PickRef? ClickPick(Num.Vector3 origin, Num.Vector3 direction)
    {
        var hit = RayPickPart(origin, direction, out _);
        if (hit is not { } h) return null;
        return IsComposite(h.Object) ? h : new PickRef(h.Object);
    }

    /// <summary>The placement's name, or for a part its SM1 node name with the placement after it ("CFactory_Gate (in tarmacgroundplane)").</summary>
    public string Label(PickRef p)
    {
        string name = Scene.Objects[p.Object].Name is { Length: > 0 } n ? n : Scene.Objects[p.Object].Definition;
        if (p.Part < 0) return name;
        string? node = Scene.Definitions.TryGetValue(Scene.Objects[p.Object].Definition, out var def) ? def.NearestLod().ElementAtOrDefault(p.Part)?.RefNode : null;
        return node is { Length: > 0 } ? $"{node} (in {name})" : $"{name} part {p.Part}";
    }

    /// <summary>A short name for a pick: the part's node name, else the placement's name.</summary>
    public string ShortName(PickRef p)
    {
        string label = Label(p);
        int cut = label.IndexOf(" (in ", StringComparison.Ordinal);
        return cut > 0 ? label[..cut] : label;
    }

    /// <summary>World box of a pick: the whole placement, or one part.</summary>
    public (Num.Vector3 Min, Num.Vector3 Max)? WorldBounds(PickRef p)
    {
        if (p.Part < 0) return WorldBounds(p.Object);
        var placed = Scene.Objects[p.Object];
        if (!Scene.Definitions.TryGetValue(placed.Definition, out var def)) return null;
        var part = def.NearestLod().ElementAtOrDefault(p.Part);
        return part is not null && BlockBounds(part.MeshName, part.MeshId) is { } b ? Transformed(b, part.NodeTransform * placed.Transform) : null;
    }

    PartBox[] Picks()
    {
        if (_picks is not null) return _picks;
        var list = new List<PartBox>();
        for (int o = 0; o < Scene.Objects.Count; o++)
        {
            if (Hidden.Contains(o) || !Scene.Definitions.TryGetValue(Scene.Objects[o].Definition, out var def)) continue;
            int index = -1;
            foreach (var part in def.NearestLod())
            {
                index++; // counts every part, so it matches the core's Sm1Definition.Parts order
                if (BlockBounds(part.MeshName, part.MeshId) is not { } b) continue;
                var world = part.NodeTransform * Scene.Objects[o].Transform;
                if (Num.Matrix4x4.Invert(world, out var inverse)) list.Add(new PartBox(o, index, inverse, b.Min, b.Max, part.MeshName, part.MeshId));
            }
        }
        return _picks = list.ToArray();
    }

    /// <summary>The nearest placement under a ray given in the game frame (oriented boxes of each drawn part), or -1.</summary>
    /// <summary>
    /// The placement whose drawn triangles the ray meets first, or -1. Boxes only shortlist: picking by box alone made every click inside
    /// Manufactorum hit the floor piece, whose box encloses the camera (owner's report 2026-11-05). Triangles are tested from both sides.
    /// </summary>
    public int RayPick(Num.Vector3 origin, Num.Vector3 direction, out float distance) => RayPickPart(origin, direction, out distance)?.Object ?? -1;

    /// <summary>The placement and part whose drawn triangles the ray meets first, or null.</summary>
    public PickRef? RayPickPart(Num.Vector3 origin, Num.Vector3 direction, out float distance)
    {
        PickRef? best = null; distance = float.MaxValue;
        var shortlist = new List<(float T, PartBox Pick, Num.Vector3 O, Num.Vector3 D)>();
        foreach (var p in Picks())
        {
            var o = Num.Vector3.Transform(origin, p.Inverse);
            var d = Num.Vector3.TransformNormal(direction, p.Inverse);
            if (RayBox(o, d, p.Min, p.Max, out float t)) shortlist.Add((t, p, o, d));
        }
        foreach (var (boxT, p, o, d) in shortlist.OrderBy(x => x.T))
        {
            if (boxT >= distance) break;
            if (RayTriangles(p.Mesh, p.Block, o, d, out float t) && t < distance) { distance = t; best = new PickRef(p.Object, p.Part); }
        }
        return best;
    }

    bool RayTriangles(string mesh, int block, Num.Vector3 o, Num.Vector3 d, out float best)
    {
        best = float.MaxValue;
        if (!Scene.Meshes.TryGetValue(mesh, out var dcm) || block < 0 || block >= dcm.Blocks.Count) return false;
        var b = dcm.Blocks[block];
        var pos = b.Positions; var idx = b.Indices;
        foreach (var range in b.Materials)
        {
            if (MapDecor.HidesSurface(Scene.Map, mesh, range.Name)) continue;
            int count = range.IndexCount - range.IndexCount % 3;
            if (count < 3 || range.StartIndex < 0 || range.StartIndex + count > idx.Length) continue;
            for (int i = range.StartIndex; i < range.StartIndex + count; i += 3)
            {
                int ia = idx[i], ib = idx[i + 1], ic = idx[i + 2];
                if (ia >= pos.Length || ib >= pos.Length || ic >= pos.Length) continue;
                if (RayTriangle(o, d, pos[ia], pos[ib], pos[ic], out float t) && t < best) best = t;
            }
        }
        return best < float.MaxValue;
    }

    /// <summary>Möller-Trumbore, both faces; t in units of <paramref name="d"/>.</summary>
    static bool RayTriangle(Num.Vector3 o, Num.Vector3 d, Num.Vector3 a, Num.Vector3 b, Num.Vector3 c, out float t)
    {
        t = 0f;
        var e1 = b - a; var e2 = c - a;
        var p = Num.Vector3.Cross(d, e2);
        float det = Num.Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        var s = o - a;
        float u = Num.Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        var q = Num.Vector3.Cross(s, e1);
        float v = Num.Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Num.Vector3.Dot(e2, q) * inv;
        return t > 1e-4f;
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

    /// <summary>
    /// Box of the playable core (game frame) for framing the camera: the 10th to 90th percentile of drawn placement centres on each axis, padded.
    /// BoundsAll is km-wide on Manufactorum (far scenery about 2 km out), which put the camera beyond the editor's zoom range.
    /// </summary>
    public (Num.Vector3 Min, Num.Vector3 Max)? BoundsCore()
    {
        var xs = new List<float>(); var ys = new List<float>(); var zs = new List<float>();
        for (int i = 0; i < Scene.Objects.Count; i++)
            if (!Hidden.Contains(i) && WorldBounds(i) is { } b) { var c = (b.Min + b.Max) / 2; xs.Add(c.X); ys.Add(c.Y); zs.Add(c.Z); }
        if (xs.Count == 0) return null;
        static (float Lo, float Hi) Range(List<float> v)
        {
            v.Sort();
            float lo = v[(int)(0.10f * (v.Count - 1))], hi = v[(int)(0.90f * (v.Count - 1))];
            float pad = (hi - lo) * 0.15f + 5f;
            return (lo - pad, hi + pad);
        }
        var (x0, x1) = Range(xs); var (y0, y1) = Range(ys); var (z0, z1) = Range(zs);
        return (new Num.Vector3(x0, y0, z0), new Num.Vector3(x1, y1, z1));
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
