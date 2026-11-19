using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmMapmaker;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The decoration of a custom map in play (docs/mapmaker.md "SM1 pieces in play"): every SM1 placement and every part of a prefab instance, drawn with the models and materials the Classic
/// map renderer uses (<see cref="MapView"/>, one instanced view per source map), and the custom (glTF) models drawn with Godot's own reader. What is drawn is what the compiler made solid
/// (<see cref="Sm1Geometry.DrawnVisuals"/> picks the parts for both). Nothing is read from the install except in memory, and nothing is written to disk.
/// </summary>
public static class CustomMapDecor
{
    static readonly Basis GltfToGame = new(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0));

    /// <summary>The nodes for <paramref name="map"/>'s decoration under one new node (null when the map has none). Problems are added to <paramref name="notes"/>; nothing throws for one bad piece.</summary>
    public static Node3D? Build(CustomMap map, List<string> notes)
    {
        if (map.Decor is not { Placements.Count: > 0 } decor) return null;
        var root = new Node3D { Name = "MapDecoration" };
        int sm1 = 0, custom = 0;
        foreach (var group in decor.Placements.Where(p => p.Asset.IsSm1).GroupBy(p => p.Asset.Map!, System.StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, System.StringComparer.OrdinalIgnoreCase))
        {
            if (!decor.Scenes.TryGetValue(group.Key, out var source)) continue;
            // One scene per source map: a placed object per placement, and a definition per (definition, part) holding exactly the parts the compiler chose (a name the client's hide lists never match).
            var definitions = new Dictionary<string, ObjectDefinition>(System.StringComparer.OrdinalIgnoreCase);
            var names = new Dictionary<(string, int), string>();
            var objects = new List<PlacedObject>();
            var spinDefinitions = LookCheck.SpinningDefinitions(source);
            var spin = new Dictionary<int, float>();
            foreach (var p in group)
            {
                var key = (p.Asset.Definition!.ToLowerInvariant(), p.Asset.Part ?? -1);
                if (!names.TryGetValue(key, out var name))
                {
                    var visuals = Sm1Geometry.DrawnVisuals(source, p.Asset.Definition!, p.Asset.Part);
                    if (visuals is null) continue;
                    names[key] = name = $"decor{names.Count}";
                    definitions[name] = new ObjectDefinition(name, visuals, source.Definitions[p.Asset.Definition!].CollisionNodes);
                }
                if (spinDefinitions.TryGetValue(p.Asset.Definition!, out var degrees)) spin[objects.Count] = degrees;
                objects.Add(new PlacedObject($"{p.ObjectId}", name, "custom-map", p.World));
            }
            if (objects.Count == 0) continue;
            var scene = new MapScene
            {
                Map = source.Map, Objects = objects, Definitions = definitions, Meshes = source.Meshes, Materials = source.Materials, Textures = source.Textures, Notes = [],
                // Imported fans turn as Classic's do: keyed off the definition (the source map's BezierCurve-carried placements name it), every placement of it spins (docs/mapmaker.md "Look in play").
                Spinning = spin,
            };
            var viewNotes = new List<string>();
            var view = MapView.Build(scene, viewNotes);
            foreach (var child in view.GetChildren())
                if (child is not MultiMeshInstance3D) child.QueueFree();   // the dark void floor and the like belong to the original map's own view
            view.Name = $"Sm1_{group.Key}";
            root.AddChild(view);
            sm1 += objects.Count;
            foreach (var n in viewNotes.Where(n => n.Contains("not loaded") || n.Contains("no drawable"))) notes.Add($"{group.Key}: {n}");
        }
        var templates = new Dictionary<string, Node3D?>();
        foreach (var p in decor.Placements.Where(p => !p.Asset.IsSm1))
        {
            string id = p.Asset.Id!;
            if (!templates.TryGetValue(id, out var template)) templates[id] = template = LoadGltf(decor.Library, id, notes);
            if (template is null) continue;
            var holder = new Node3D { Name = $"{p.ObjectId}_{id}", Transform = FromNumerics(p.World) };
            var inner = new Node3D { Basis = GltfToGame };
            inner.AddChild(template.Duplicate() as Node3D ?? new Node3D());
            holder.AddChild(inner);
            root.AddChild(holder);
            custom++;
        }
        foreach (var t in templates.Values) t?.QueueFree();
        GD.Print($"custom map decoration: {sm1} SM1 placements and {custom} imported models drawn");
        return root;
    }

    static Transform3D FromNumerics(Num.Matrix4x4 m) => new(
        new Basis(new Vector3(m.M11, m.M12, m.M13), new Vector3(m.M21, m.M22, m.M23), new Vector3(m.M31, m.M32, m.M33)),
        new Vector3(m.M41, m.M42, m.M43));

    static Node3D? LoadGltf(MapLibrary? library, string id, List<string> notes)
    {
        try
        {
            var info = library?.GetAsset(id);
            if (info is null || library is null) { notes.Add($"imported model '{id}' is not in this PC's library"); return null; }
            string path = library.AssetFilePath(info);
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(path, state, 0, System.IO.Path.GetDirectoryName(path) ?? "");
            if (err != Error.Ok) { notes.Add($"imported model '{id}': could not read {info.File} ({err})"); return null; }
            return doc.GenerateScene(state) is Node3D scene ? ConvertImporterMeshes(scene) : null;
        }
        catch (System.Exception e) when (e is not (System.OutOfMemoryException or System.StackOverflowException))
        {
            notes.Add($"imported model '{id}': {e.Message}");
            return null;
        }
    }

    /// <summary>Godot's runtime glTF reader hands back <see cref="ImporterMeshInstance3D"/> nodes, which never draw in a running scene; this makes each one a real <see cref="MeshInstance3D"/> (the editor does the same).</summary>
    static Node3D ConvertImporterMeshes(Node3D root)
    {
        var found = new List<ImporterMeshInstance3D>();
        void Collect(Node n) { if (n is ImporterMeshInstance3D i) found.Add(i); foreach (var c in n.GetChildren()) Collect(c); }
        Collect(root);
        foreach (var imi in found)
        {
            var mi = new MeshInstance3D { Name = imi.Name, Transform = imi.Transform, Visible = imi.Visible, Mesh = imi.Mesh?.GetMesh() };
            var parent = imi.GetParent();
            if (parent is not null) { int at = imi.GetIndex(); parent.AddChild(mi); parent.MoveChild(mi, at); }
            foreach (var child in imi.GetChildren()) { imi.RemoveChild(child); mi.AddChild(child); }
            parent?.RemoveChild(imi);
            if (imi == root) root = mi;
            imi.Free();
        }
        return root;
    }
}
