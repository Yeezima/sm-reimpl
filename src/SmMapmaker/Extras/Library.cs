using System.Text.Json.Nodes;
using SmContent;

namespace SmMapmaker;

public sealed record PrefabSummary(string Id, string Name, IReadOnlyList<string> Tags, int Parts, Bounds? Bounds);

/// <summary>
/// The private library (docs/mapmaker.md): <c>maps/</c>, <c>library/prefabs/&lt;id&gt;.smprefab</c>, <c>library/assets/&lt;id&gt;/</c> (the imported glTF
/// and <c>asset.json</c>) and <c>library/index.json</c>. Lives outside the repository; default <c>%LOCALAPPDATA%\sm-reimpl\mapmaker\</c>.
/// Only references and transforms are stored for SM1 content; no original asset is ever written.
/// </summary>
public sealed class MapLibrary
{
    public string Root { get; }

    /// <param name="root">The library folder; null: <see cref="DefaultRoot"/>.</param>
    public MapLibrary(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);

    public static string DefaultRoot => Path.Combine(GameInstall.DataRoot, "mapmaker");

    public string MapsDir => Path.Combine(Root, "maps");
    public string PrefabsDir => Path.Combine(Root, "library", "prefabs");
    public string AssetsDir => Path.Combine(Root, "library", "assets");
    public string IndexPath => Path.Combine(Root, "library", "index.json");

    public string PrefabPath(string id) => Path.Combine(PrefabsDir, CheckId(id) + ".smprefab");
    public string AssetDir(string id) => Path.Combine(AssetsDir, CheckId(id));
    public string AssetFilePath(AssetInfo a) => Path.Combine(AssetDir(a.Id), a.File);

    static string CheckId(string id) => Json.IsSlug(id) ? id : throw new MapException("bad-id", $"'{id}' is not a valid id (lowercase letters, digits, '-' and '_').");

    public Prefab? GetPrefab(string id)
    {
        if (!Json.IsSlug(id)) return null;
        var path = PrefabPath(id);
        if (!File.Exists(path)) return null;
        var p = Prefab.Parse(File.ReadAllText(path));
        if (p.Id != id) throw new MapException("bad-file", $"{path}: the prefab's id '{p.Id}' does not match its file name.");
        return p;
    }

    public AssetInfo? GetAsset(string id)
    {
        if (!Json.IsSlug(id)) return null;
        var path = Path.Combine(AssetDir(id), "asset.json");
        if (!File.Exists(path)) return null;
        var a = AssetInfo.Parse(File.ReadAllText(path));
        if (a.Id != id) throw new MapException("bad-file", $"{path}: the asset's id '{a.Id}' does not match its folder name.");
        return a;
    }

    public IReadOnlyList<PrefabSummary> ListPrefabs()
    {
        if (!Directory.Exists(PrefabsDir)) return [];
        var list = new List<PrefabSummary>();
        foreach (var f in Directory.EnumerateFiles(PrefabsDir, "*.smprefab").Order(StringComparer.Ordinal))
            if (GetPrefab(Path.GetFileNameWithoutExtension(f)) is { } p) list.Add(new PrefabSummary(p.Id, p.Name, p.Tags, p.Parts.Count, p.Bounds));
        return list;
    }

    public IReadOnlyList<AssetInfo> ListAssets()
    {
        if (!Directory.Exists(AssetsDir)) return [];
        var list = new List<AssetInfo>();
        foreach (var d in Directory.EnumerateDirectories(AssetsDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
            if (GetAsset(d) is { } a) list.Add(a);
        return list;
    }

    /// <summary>Writes the prefab file (atomically) and refreshes the index.</summary>
    public void SavePrefab(Prefab p)
    {
        WriteAtomic(PrefabPath(p.Id), CanonicalJson.Bytes(p.ToNode()));
        RebuildIndex();
    }

    public void DeletePrefab(string id)
    {
        var path = PrefabPath(id);
        if (File.Exists(path)) File.Delete(path);
        RebuildIndex();
    }

    /// <summary>Rewrites <c>library/index.json</c> from the folders (sorted by id).</summary>
    public void RebuildIndex()
    {
        var index = new JsonObject
        {
            ["format"] = "smlibrary", ["formatVersion"] = 1,
            ["assets"] = new JsonArray(ListAssets().Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["name"] = a.Name }).ToArray()),
            ["prefabs"] = new JsonArray(ListPrefabs().Select(p => (JsonNode)new JsonObject
            {
                ["id"] = p.Id, ["name"] = p.Name, ["tags"] = new JsonArray(p.Tags.Select(t => (JsonNode)JsonValue.Create(t)!).ToArray()),
            }).ToArray()),
        };
        WriteAtomic(IndexPath, CanonicalJson.Bytes(index));
    }

    /// <summary>Writes through a temporary file in the same folder, so a crash never leaves half a document.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}
