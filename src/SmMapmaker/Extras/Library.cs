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

    /// <summary>The raw text of a prefab file, or null when there is none (no parsing, so a corrupt file can still be snapshotted for undo).</summary>
    public string? ReadPrefabText(string id)
    {
        var path = PrefabPath(id);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>The prefabs in the library. A corrupt or mismatched file is skipped and named in <paramref name="skipped"/>.</summary>
    public IReadOnlyList<PrefabSummary> ListPrefabs(List<string>? skipped = null)
    {
        if (!Directory.Exists(PrefabsDir)) return [];
        var list = new List<PrefabSummary>();
        foreach (var f in Directory.EnumerateFiles(PrefabsDir, "*.smprefab").Order(StringComparer.Ordinal))
        {
            try { if (GetPrefab(Path.GetFileNameWithoutExtension(f)) is { } p) list.Add(new PrefabSummary(p.Id, p.Name, p.Tags, p.Parts.Count, p.Bounds)); }
            catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException) { skipped?.Add($"{Path.GetFileName(f)}: {e.Message}"); }
        }
        return list;
    }

    /// <summary>The assets in the library; an unreadable asset.json is skipped and named in <paramref name="skipped"/>.</summary>
    public IReadOnlyList<AssetInfo> ListAssets(List<string>? skipped = null)
    {
        if (!Directory.Exists(AssetsDir)) return [];
        var list = new List<AssetInfo>();
        foreach (var d in Directory.EnumerateDirectories(AssetsDir).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
        {
            try { if (GetAsset(d) is { } a) list.Add(a); }
            catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException) { skipped?.Add($"{d}: {e.Message}"); }
        }
        return list;
    }

    public const int MaxNameLength = 80;

    /// <summary>A display name as it will be stored: trimmed, inner runs of whitespace made one space. Throws <c>bad-name</c> for an empty, over-long or control-character name.</summary>
    public static string CleanName(string? name)
    {
        string n = string.Join(' ', (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (n.Length == 0) throw new MapException("bad-name", "A name cannot be empty.");
        if (n.Length > MaxNameLength) throw new MapException("bad-name", $"A name can be at most {MaxNameLength} characters.");
        if (n.Any(char.IsControl)) throw new MapException("bad-name", "A name cannot contain control characters.");
        return n;
    }

    /// <summary>
    /// Renames a custom asset's display name (asset.json). The id and folder stay, so every map and prefab that refers to the asset still resolves it.
    /// A name another asset already has is rejected (<c>duplicate-name</c>, compared without case) so the list stays unambiguous. Returns the stored name.
    /// </summary>
    public string RenameAsset(string id, string newName)
    {
        var name = CleanName(newName);
        var asset = GetAsset(id) ?? throw new MapException("unknown-asset", $"There is no asset '{id}' in the library.");
        if (asset.Name == name) return name;
        if (ListAssets().Any(a => a.Id != id && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new MapException("duplicate-name", $"Another custom asset is already called '{name}'. Choose a different name.");
        WriteAtomic(Path.Combine(AssetDir(id), "asset.json"), CanonicalJson.Bytes((asset with { Name = name }).ToNode()));
        RebuildIndex();
        return name;
    }

    /// <summary>Renames a prefab's display name (its file). The id and file name stay, so instances in maps and parts in other prefabs still resolve it. Duplicates are rejected as for assets.</summary>
    public string RenamePrefab(string id, string newName)
    {
        var name = CleanName(newName);
        var prefab = GetPrefab(id) ?? throw new MapException("unknown-prefab", $"There is no prefab '{id}' in the library.");
        if (prefab.Name == name) return name;
        if (ListPrefabs().Any(p => p.Id != id && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new MapException("duplicate-name", $"Another prefab is already called '{name}'. Choose a different name.");
        SavePrefab(prefab with { Name = name });
        return name;
    }

    /// <summary>Writes the prefab file (atomically) and refreshes the index.</summary>
    public void SavePrefab(Prefab p)
    {
        WriteAtomic(PrefabPath(p.Id), CanonicalJson.Bytes(p.ToNode()));
        RebuildIndex();
    }

    public string TrashAssetsDir => Path.Combine(Root, "library", "trash", "assets");

    /// <summary>Moves a custom asset's folder to a new folder in the library trash and returns its slot; <see cref="RestoreAsset"/> with that slot brings it back (undo).</summary>
    public string TrashAsset(string id) => TrashAsset(id, NewTrashSlot(id));

    public static string NewTrashSlot(string id) => id + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>Test seam: called with the step name ("asset-write", "trash", "prefab-write", "prefab-delete", "index") just before a storage step of a commit; a test throws to simulate a failing disk. Null in production.</summary>
    internal Action<string>? StorageFault { get; set; }

    internal void Fault(string step) => StorageFault?.Invoke(step);

    /// <summary>Moves the asset folder to its own trash folder (<paramref name="slot"/>; every deleted copy keeps its own, so deleting, re-importing and deleting again loses nothing). Returns the slot.</summary>
    public string TrashAsset(string id, string slot)
    {
        var src = AssetDir(id);
        if (!Directory.Exists(src)) return slot;
        var dst = Path.Combine(TrashAssetsDir, slot);
        Directory.CreateDirectory(TrashAssetsDir);
        Fault("trash");
        Directory.Move(src, dst);
        RebuildIndex();
        return slot;
    }

    public void RestoreAsset(string id, string slot)
    {
        var src = Path.Combine(TrashAssetsDir, slot);
        var dst = AssetDir(id);
        if (!Directory.Exists(src) || Directory.Exists(dst)) return;
        Directory.CreateDirectory(AssetsDir);
        Directory.Move(src, dst);
        RebuildIndex();
    }

    /// <summary>Saved maps in the library's maps folder (other than <paramref name="except"/>) that place the custom asset or the prefab; unreadable files are skipped.</summary>
    public IReadOnlyList<string> MapsUsing(string? assetId, string? prefabId, string? except = null)
    {
        var found = new List<string>();
        if (!Directory.Exists(MapsDir)) return found;
        string? skip = except is null ? null : Path.GetFullPath(except);
        foreach (var f in Directory.EnumerateFiles(MapsDir, "*.smmap").Order(StringComparer.OrdinalIgnoreCase))
        {
            if (skip is not null && string.Equals(Path.GetFullPath(f), skip, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if (new FileInfo(f).Length > 8_000_000) continue;
                var doc = MapDocument.Parse(File.ReadAllText(f));
                if (doc.Objects.Any(o => (assetId is not null && o.Asset is { IsSm1: false } a && a.Id == assetId) || (prefabId is not null && o.Prefab == prefabId)))
                    found.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch (Exception e) when (e is MapException or IOException or UnauthorizedAccessException) { }
        }
        return found;
    }

    public void DeletePrefab(string id)
    {
        var path = PrefabPath(id);
        if (File.Exists(path)) File.Delete(path);
        RebuildIndex();
    }

    /// <summary>Rewrites <c>library/index.json</c> from the folders (sorted by id); corrupt entries are left out, never fatal.</summary>
    public void RebuildIndex()
    {
        Fault("index");
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
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }
}
