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
    /// <summary>Why a custom asset did not load (the last note), for the library's placeholder icon.</summary>
    public static readonly Dictionary<string, string> Failures = new();

    /// <summary>glTF (x, y, z) to the document frame (x, -z, y) as a basis (the core's AssetInfo.GltfToDocument).</summary>
    public static readonly Basis GltfToGame = new(new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, -1, 0));

    /// <summary>
    /// The box of everything drawn under <paramref name="root"/>, in the frame of <paramref name="root"/>'s parent (so it includes the root's own transform).
    /// Empty (zero size) when nothing under it draws. This is the fallback for every box the library does not know.
    /// </summary>
    public static Aabb MeasureBounds(Node3D root)
    {
        Aabb? total = null;
        void Walk(Node3D n, Transform3D parent)
        {
            var world = parent * n.Transform;
            if (n is VisualInstance3D v && n.Visible)
            {
                var box = v.GetAabb();
                if (box.Size.LengthSquared() > 0f)
                {
                    var moved = world * box;
                    total = total is { } t ? t.Merge(moved) : moved;
                }
            }
            foreach (var child in n.GetChildren()) if (child is Node3D c) Walk(c, world);
        }
        Walk(root, Transform3D.Identity);
        return total ?? default;
    }

    /// <summary>
    /// Godot's runtime glTF / FBX reader (<see cref="GltfDocument.GenerateScene"/>) hands back <see cref="ImporterMeshInstance3D"/> nodes, the editor-import kind that
    /// never draws in a running scene. That is why a custom asset showed as an empty bounding box: this turns every one into a real <see cref="MeshInstance3D"/>
    /// (same name, transform and children) and returns the root (which changes when the root itself was one).
    /// </summary>
    public static Node3D ConvertImporterMeshes(Node3D root)
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
            if (parent is not null) parent.RemoveChild(imi);
            if (imi == root) root = mi;
            imi.Free();
        }
        return root;
    }

    public static void ForgetAssets()
    {
        foreach (var n in GltfTemplates.Values) n?.QueueFree();
        GltfTemplates.Clear();
        foreach (var n in PartTemplates.Values) n?.QueueFree();   // the clones keep the shared meshes alive
        PartTemplates.Clear();
    }

    /// <summary>
    /// The content of one document object: its resolved parts, each placed relative to the object (game frame, under a node whose basis converts to Y up).
    /// Returns null content for an empty group. <paramref name="bounds"/> is the union of the known part boxes in the object's local Godot frame.
    /// </summary>
    public static Node3D? BuildObject(IEnumerable<ResolvedPart> parts, Num.Matrix4x4 objectWorld, out Aabb bounds, List<string>? notes = null)
    {
        bounds = default;
        Node3D? content = null;
        Bounds? union = null;
        bool unmeasured = false;
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
            else if (part.Visible && node is not null) unmeasured = true;   // no stored box (a glTF without POSITION min/max, an SM1 definition): measure the drawn mesh
        }
        if (union is { } ub) bounds = ToGodot(ub);
        if (content is not null && unmeasured)
        {
            var measured = MeasureBounds(content);
            bounds = bounds.Size.LengthSquared() < 1e-10f ? measured : measured.Size.LengthSquared() < 1e-10f ? bounds : bounds.Merge(measured);
        }
        return content;
    }

    /// <summary>SM1 maps that pieces needed but that were not loaded (the plugin loads them and redraws).</summary>
    public static readonly HashSet<string> MissingMaps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One asset in its own frame, already expressed in the game frame (glTF assets are turned from Y up). Null when it cannot be drawn.</summary>
    public static Node3D? BuildAsset(AssetRef asset, List<string>? notes = null)
    {
        if (asset.IsSm1)
        {
            var source = MapHost.Sm1For(asset.Map!);
            if (source is null)
            {
                MissingMaps.Add(asset.Map!);
                notes?.Add($"SM1 map '{asset.Map}' is not loaded yet; '{asset.Definition}' is drawn once it is");
                return null;
            }
            var node = asset.Part is { } part ? BuildSm1Part(source, asset.Definition!, part) : source.Instantiate(asset.Definition!);
            if (node is null) notes?.Add($"SM1 definition '{asset.Label}' has nothing to draw");
            return node;
        }
        return BuildCustom(asset.Id!, notes);
    }

    static Node3D? BuildCustom(string id, List<string>? notes)
    {
        string key = id;
        try { if (MapHost.Library.GetAsset(id) is { } known) key = id + ":" + known.Sha256; } catch (MapException) { }   // changed content is a new template
        if (!GltfTemplates.TryGetValue(key, out var template))
        {
            template = LoadGltf(id, notes);
            GltfTemplates[key] = template;
            if (template is null) Failures[id] = notes?.LastOrDefault() ?? "load failed";
            else Failures.Remove(id);
        }
        else if (template is null) notes?.Add(Failures.TryGetValue(id, out var why) ? why : $"custom asset '{id}' failed to load earlier");
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
            var err = doc.AppendFromFile(path, state, 0, System.IO.Path.GetDirectoryName(path) ?? "");
            if (err != Error.Ok) { notes?.Add($"custom asset '{id}': could not read {info.File} ({err}); it may be compressed (Draco/meshopt) or damaged"); return null; }
            var scene = doc.GenerateScene(state) as Node3D;
            if (scene is not null) scene = ConvertImporterMeshes(scene);
            if (scene is not null && MeasureBounds(scene).Size.LengthSquared() < 1e-10f) notes?.Add($"custom asset '{id}': {info.File} has no mesh");
            return scene;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            notes?.Add($"custom asset '{id}': {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// A preview node for an SM1 definition the client leaves out on purpose (<see cref="SmClient.MapDecor.Hidden"/>: Manufactorum's armoury wall stations and fittings):
    /// the same build under another definition name, so the hide rule does not match. Null when it still cannot be built. The caller owns the node.
    /// </summary>
    public static Node3D? BuildHiddenSm1Preview(Sm1Source src, string definition) =>
        src.Scene.Definitions.TryGetValue(definition, out var def) ? BuildFresh(src, definition, def.DrawnParts(false).ToList(), def.CollisionNodes) : null;

    static readonly Dictionary<(Sm1Source Source, string Definition, int Part), Node3D?> PartTemplates = new();

    /// <summary>
    /// One part of an SM1 definition (<see cref="AssetRef.Part"/> indexes <c>NearestLod()</c>, like the picks), drawn alone. This is what keeps an extracted
    /// part from dragging its neighbours along: <c>Sm1Source.Instantiate(definition, part)</c> copies the definition with <c>with { Visuals = ... }</c>, and that copy
    /// keeps the whole definition's cached <c>HighestLod()</c> list once the map was drawn, so it drew every part (the owner's "attached" objects). A new
    /// <see cref="SmContent.ObjectDefinition"/> has no such cache.
    /// </summary>
    public static Node3D? BuildSm1Part(Sm1Source src, string definition, int part)
    {
        var key = (src, definition.ToLowerInvariant(), part);
        if (!PartTemplates.TryGetValue(key, out var template))
        {
            template = null;
            if (src.Scene.Definitions.TryGetValue(definition, out var whole))
            {
                var near = whole.NearestLod().ToList();
                if (part >= 0 && part < near.Count) template = BuildFresh(src, definition, [near[part]], whole.CollisionNodes);
            }
            PartTemplates[key] = template;
        }
        return template is null ? null : CloneTemplate(template, $"{definition}_{part}");
    }

    static Node3D CloneTemplate(Node3D template, string name)
    {
        var root = new Node3D { Name = name };
        foreach (var child in template.GetChildren().OfType<MultiMeshInstance3D>())
            root.AddChild(new MultiMeshInstance3D { Name = child.Name, Multimesh = child.Multimesh, CastShadow = child.CastShadow });
        return root;
    }

    /// <summary>Draws exactly <paramref name="visuals"/> through the client's own map view (materials, mirroring, sky and all), as a definition made new for the purpose.</summary>
    static Node3D? BuildFresh(Sm1Source src, string name, IReadOnlyList<SmContent.VisualPart> visuals, IReadOnlyList<(string Node, Num.Matrix4x4 Transform)> collision)
    {
        if (visuals.Count == 0) return null;
        var def = new SmContent.ObjectDefinition("preview_definition", visuals, collision);
        var one = new SmContent.MapScene
        {
            Map = src.Scene.Map, Objects = [new SmContent.PlacedObject("preview", def.Name, "preview", Num.Matrix4x4.Identity)],
            Definitions = new Dictionary<string, SmContent.ObjectDefinition>(StringComparer.OrdinalIgnoreCase) { [def.Name] = def },
            Meshes = src.Scene.Meshes, Materials = src.Scene.Materials, Textures = src.Scene.Textures, Notes = [],
        };
        SmClient.MapView? view = null;
        try
        {
            view = SmClient.MapView.Build(one, new List<string>());
            return view.Instances == 0 ? null : CloneTemplate(view, name);
        }
        catch (Exception) { return null; }
        finally { view?.Free(); }   // the multimesh resources live on in the clones
    }

    /// <summary>Why an SM1 definition has nothing to draw, in two or three words for the library's placeholder icon (the client's default LOD rule, so it matches what the map shows).</summary>
    public static string WhySm1Empty(Sm1Source src, string definition)
    {
        if (!src.Scene.Definitions.TryGetValue(definition, out var def)) return "no mesh";
        var drawn = def.DrawnParts(false).ToList();
        if (drawn.Count == 0) return "no mesh";
        int missing = 0, empty = 0;
        foreach (var part in drawn)
        {
            if (!src.Scene.Meshes.TryGetValue(part.MeshName, out var dcm)) missing++;
            else if (part.MeshId < 0 || part.MeshId >= dcm.Blocks.Count || dcm.Blocks[part.MeshId].Positions.Length == 0) empty++;
        }
        if (missing == drawn.Count) return "mesh not loaded";
        if (missing + empty == drawn.Count) return "no geometry";
        // The client leaves some placements out on purpose (Manufactorum's armoury wall stations and fittings, MapDecor.Hidden); a preview of that definition is left out too.
        var probe = new SmContent.MapScene
        {
            Map = src.Scene.Map, Objects = [new SmContent.PlacedObject("preview", def.Name, "preview", Num.Matrix4x4.Identity)],
            Definitions = new Dictionary<string, SmContent.ObjectDefinition>(StringComparer.OrdinalIgnoreCase) { [def.Name] = def },
            Meshes = src.Scene.Meshes, Materials = src.Scene.Materials, Textures = src.Scene.Textures, Notes = [],
        };
        if (SmClient.MapDecor.Hidden(probe).Count > 0) return "hidden decor";
        return "build failed";
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
