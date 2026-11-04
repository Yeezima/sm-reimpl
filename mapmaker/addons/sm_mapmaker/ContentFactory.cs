using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmMapmaker;
using Num = System.Numerics;

namespace SmMapmakerEditor;

/// <summary>
/// Builds what an object looks like: SM1 definitions from the in-memory <see cref="Sm1Source"/>, custom assets by reading the library's glTF with Godot's
/// <see cref="GltfDocument"/> at run time, prefab instances by unfolding the core's <see cref="MapResolver"/>. Every node returned is a runtime node with no
/// owner; none of it is ever saved.
/// </summary>
public static class ContentFactory
{
    static readonly Dictionary<string, Node3D?> GltfTemplates = new();

    /// <summary>glTF (x, y, z) to the document frame (x, -z, y) as a basis (the core's AssetInfo.GltfToDocument).</summary>
    public static readonly Basis GltfToGame = new(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0));

    public static void ForgetAssets() { foreach (var n in GltfTemplates.Values) n?.QueueFree(); GltfTemplates.Clear(); }

    /// <summary>
    /// The content of one document object: its resolved parts, each placed relative to the object (game frame, under a node whose basis converts to Y up).
    /// Returns null content for an empty group. <paramref name="bounds"/> is the union of the known part boxes in the object's local Godot frame.
    /// </summary>
    public static Node3D? BuildObject(IEnumerable<ResolvedPart> parts, Num.Matrix4x4 objectWorld, out Aabb bounds, List<string>? notes = null)
    {
        bounds = default;
        Node3D? content = null;
        Bounds? union = null;
        Num.Matrix4x4.Invert(objectWorld, out var inverse);
        foreach (var part in parts)
        {
            var rel = part.World * inverse;
            var node = BuildAsset(part.Asset, notes);
            content ??= new Node3D { Basis = Frame.C };
            var holder = new Node3D { Name = part.PartPath ?? part.Name, Transform = Frame.FromNumerics(rel), Visible = part.Visible };
            if (node is not null) holder.AddChild(node);
            content.AddChild(holder);
            if (part.Visible && part.LocalBounds is { } lb) { var b = lb.Transformed(rel); union = union is { } u ? u.Union(b) : b; }
        }
        if (union is { } ub) bounds = ToGodot(ub);
        return content;
    }

    /// <summary>One asset in its own frame, already expressed in the game frame (glTF assets are turned from Y up). Null when it cannot be drawn.</summary>
    public static Node3D? BuildAsset(AssetRef asset, List<string>? notes = null)
    {
        if (asset.IsSm1)
        {
            var source = MapHost.Sm1For(asset.Map!);
            if (source is null) { notes?.Add($"SM1 map '{asset.Map}' is not loaded; '{asset.Definition}' is not drawn"); return null; }
            var node = source.Instantiate(asset.Definition!);
            if (node is null) notes?.Add($"SM1 definition '{asset.Definition}' has nothing to draw");
            return node;
        }
        return BuildCustom(asset.Id!, notes);
    }

    static Node3D? BuildCustom(string id, List<string>? notes)
    {
        if (!GltfTemplates.TryGetValue(id, out var template))
        {
            template = LoadGltf(id, notes);
            GltfTemplates[id] = template;
        }
        if (template is null) return null;
        var holder = new Node3D { Name = id, Basis = GltfToGame };
        holder.AddChild(template.Duplicate() as Node3D ?? new Node3D());
        return holder;
    }

    static Node3D? LoadGltf(string id, List<string>? notes)
    {
        try
        {
            var info = MapHost.Library.GetAsset(id);
            if (info is null) { notes?.Add($"custom asset '{id}' is not in the library"); return null; }
            string path = MapHost.Library.AssetFilePath(info);
            var doc = new GltfDocument();
            var state = new GltfState();
            var err = doc.AppendFromFile(path, state);
            if (err != Error.Ok) { notes?.Add($"custom asset '{id}': could not read {info.File} ({err})"); return null; }
            return doc.GenerateScene(state) as Node3D;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            notes?.Add($"custom asset '{id}': {e.Message}");
            return null;
        }
    }

    /// <summary>A box in the document frame as the equivalent box in the Godot (Y up) frame.</summary>
    public static Aabb ToGodot(Bounds b)
    {
        var lo = Frame.ToGodot(new Vector3(b.Min.X, b.Min.Y, b.Min.Z));
        var hi = Frame.ToGodot(new Vector3(b.Max.X, b.Max.Y, b.Max.Z));
        var min = lo.Min(hi); var max = lo.Max(hi);
        return new Aabb(min, max - min);
    }
}
