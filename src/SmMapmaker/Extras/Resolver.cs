using System.Numerics;

namespace SmMapmaker;

/// <summary>What the operations and views look things up in: the library (and, inside a batch, its pending prefabs and assets) and the SM1 source.</summary>
public class MapContext(MapLibrary library, ISm1Source? sm1)
{
    public MapLibrary Library { get; } = library;
    public ISm1Source? Sm1 { get; } = sm1;

    public virtual Prefab? GetPrefab(string id) => Library.GetPrefab(id);
    public virtual AssetInfo? GetAsset(string id) => Library.GetAsset(id);

    /// <summary>Bounds of an asset in the document frame (Z up), or null when unknown (an SM1 definition without an install or mesh data).</summary>
    public Bounds? AssetBounds(AssetRef asset)
    {
        if (!asset.IsSm1) return GetAsset(asset.Id!)?.BoundsInDocumentFrame;
        try
        {
            var def = Sm1?.LoadMap(asset.Map!).Definitions.GetValueOrDefault(asset.Definition!);
            if (asset.Part is not { } part) return def?.Bounds;
            return def?.Parts is { } parts && part < parts.Count ? parts[part] : null;
        }
        catch (MapException) { return null; }
    }

    /// <summary>The prefab's bounds in its own frame: the stored ones, else the union of its parts.</summary>
    public Bounds? PrefabBounds(string id, int depth = 0)
    {
        if (depth > 16 || GetPrefab(id) is not { } p) return null;
        return p.Bounds ?? PartsBounds(p.Parts, depth + 1);
    }

    /// <summary>The union of the parts' boxes, or null when any part's box is unknown (a partial box would mislead).</summary>
    public Bounds? PartsBounds(IEnumerable<PrefabPart> parts, int depth = 0)
    {
        Bounds? result = null;
        foreach (var part in parts)
        {
            var local = part.Asset is not null ? AssetBounds(part.Asset) : PrefabBounds(part.Prefab!, depth);
            if (local is not { } lb) return null;
            var b = lb.Transformed(part.Transform.ToMatrix());
            result = result is { } r ? r.Union(b) : b;
        }
        return result;
    }
}

/// <param name="PartPath">Null for a plain object; "p1" or "p1/p2" for a part of an instance (nested prefabs add segments).</param>
/// <param name="LocalBounds">The asset's own box, when known; <see cref="World"/> places it.</param>
public sealed record ResolvedPart(string ObjectId, string? PartPath, string Name, AssetRef Asset, Matrix4x4 World, bool Visible, Bounds? LocalBounds);

/// <summary>Expands a document into the assets to draw, instances unfolded and overrides applied. Views and the preview use this; it composes matrices.</summary>
public static class MapResolver
{
    public static IReadOnlyList<ResolvedPart> Flatten(MapDocument doc, MapContext ctx, List<string>? warnings = null)
    {
        var result = new List<ResolvedPart>();
        foreach (var o in doc.Objects)
        {
            bool visible = true;
            int guard = 0;
            for (var cur = o; ; cur = doc.Find(cur.Parent!) ?? throw new MapException("unknown-id", $"'{cur.Id}' has unknown parent '{cur.Parent}'.", cur.Id))
            {
                visible &= cur.Visible;
                if (cur.Parent is null) break;
                if (++guard > 1000) throw new MapException("parent-cycle", $"'{o.Id}' is in a parent cycle.", o.Id);
            }
            var world = doc.World(o.Id);
            if (o.Asset is { } a) result.Add(new ResolvedPart(o.Id, null, o.Name, a, world, visible, ctx.AssetBounds(a)));
            else if (o.Prefab is { } pid) Expand(ctx, o.Id, o.Name, pid, world, visible, o.Overrides, null, 0, result, warnings);
        }
        return result;
    }

    static void Expand(MapContext ctx, string objectId, string name, string prefabId, Matrix4x4 world, bool visible, IReadOnlyList<PartOverride> overrides,
        string? path, int depth, List<ResolvedPart> into, List<string>? warnings)
    {
        if (depth > 16) { warnings?.Add($"'{objectId}': prefab nesting is too deep at '{prefabId}'."); return; }
        if (ctx.GetPrefab(prefabId) is not { } prefab) { warnings?.Add($"'{objectId}': unknown prefab '{prefabId}'."); return; }
        foreach (var part in prefab.Parts)
        {
            var ov = depth == 0 ? overrides.FirstOrDefault(x => x.Part == part.Id) : null;
            var tr = ov?.Transform ?? part.Transform;
            var matrix = tr.ToMatrix() * world;
            bool vis = visible && (ov?.Visible ?? true);
            var partPath = path is null ? part.Id : path + "/" + part.Id;
            if (part.Asset is { } a) into.Add(new ResolvedPart(objectId, partPath, part.Name.Length > 0 ? part.Name : name, a, matrix, vis, ctx.AssetBounds(a)));
            else Expand(ctx, objectId, name, part.Prefab!, matrix, vis, overrides, partPath, depth + 1, into, warnings);
        }
    }
}
