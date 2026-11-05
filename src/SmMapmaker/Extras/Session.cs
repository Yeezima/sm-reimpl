using System.Text;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// An open map plus its undo/redo journal. Every change is a batch of JSON operations (<see cref="Apply(IEnumerable{JsonObject}, bool)"/>): the batch is one
/// transaction (all or nothing) and one undo step. The editor and <c>smtool map</c> both go through this class, so they validate identically.
/// </summary>
public sealed class MapSession
{
    sealed record Entry(string DocBefore, string DocAfter, IReadOnlyList<(string Id, string? Before, string? After)> Prefabs);

    readonly List<Entry> _undo = [];
    readonly List<Entry> _redo = [];

    public MapSession(MapDocument document, MapLibrary library, ISm1Source? sm1 = null, string? path = null)
    {
        Document = document;
        Library = library;
        Sm1 = sm1;
        Path = path;
    }

    public static MapSession New(string name, MapLibrary library, ISm1Source? sm1 = null) => new(MapDocument.Create(name), library, sm1) { Dirty = true };

    public static MapSession Load(string path, MapLibrary library, ISm1Source? sm1 = null)
    {
        if (!File.Exists(path)) throw new MapException("bad-file", $"No file '{path}'.");
        return new MapSession(MapDocument.Parse(File.ReadAllText(path)), library, sm1, System.IO.Path.GetFullPath(path));
    }

    /// <summary>The live document. Read it; change it only through <see cref="Apply(IEnumerable{JsonObject}, bool)"/>.</summary>
    public MapDocument Document { get; }
    public MapLibrary Library { get; }
    public ISm1Source? Sm1 { get; set; }
    public string? Path { get; private set; }
    public string Name => Document.Name;
    /// <summary>True when the document changed since it was loaded, created or saved.</summary>
    public bool Dirty { get; private set; }
    /// <summary>Raised-by-count: grows by one with every committed batch, undo and redo.</summary>
    public int Version { get; private set; }
    /// <summary>Raised after a commit, undo or redo (also when the batch changed only the library, e.g. a prefab master edit).</summary>
    public event EventHandler? Changed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public MapContext Context => new(Library, Sm1);

    public OpResult Apply(JsonObject op, bool dryRun = false) => Apply([op], dryRun);

    /// <summary>A JSON array of operations, a single operation object, or <c>{ "ops": [...] }</c>.</summary>
    public OpResult Apply(string json, bool dryRun = false)
    {
        try { return Apply(ParseOps(json), dryRun); }
        catch (MapException e) { return OpResult.Fail(e.Code, e.Message); }
    }

    public static IReadOnlyList<JsonObject> ParseOps(string json)
    {
        var node = Json.ParseText(json, "The operations");
        if (node is JsonObject o && o["ops"] is JsonArray inner) node = inner;
        if (node is JsonObject single) return [single];
        return Json.Arr(node, "The operations", "bad-op").Select(x => Json.Obj(x, "An operation", "bad-op")).ToList();
    }

    public OpResult Apply(IEnumerable<JsonObject> ops, bool dryRun = false)
    {
        var list = ops.ToList();
        var batch = new Batch(Document.Clone(), Library, Sm1);
        for (int i = 0; i < list.Count; i++)
        {
            string name = list[i]["op"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
            try { batch.Run(list[i], out name); }
            catch (MapException e) { return Failure(new OpError(e.Code, e.Message, name, e.Id) { Index = i }, dryRun); }
            catch (Exception e) when (e is InvalidOperationException or FormatException or InvalidCastException)
            {
                return Failure(new OpError("bad-op", $"Malformed operation: {e.Message}", name) { Index = i }, dryRun);
            }
        }
        var result = new OpResult(true, batch.Created, batch.Warnings, [])
        {
            CreatedPrefabs = batch.CreatedPrefabs, CreatedAssets = batch.CreatedAssets, Mapping = batch.Mapping, DryRun = dryRun,
        };
        if (dryRun) return result;
        try { Commit(batch); }
        catch (MapException e) { return Failure(new OpError(e.Code, e.Message, "commit"), false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failure(new OpError("io-error", $"Could not write the library: {e.Message}", "commit"), false);
        }
        return result;
    }

    static OpResult Failure(OpError error, bool dryRun) => new(false, [], [], [error]) { DryRun = dryRun };

    void Commit(Batch batch)
    {
        string before = Document.ToJson(), after = batch.Doc.ToJson();
        var prefabChanges = new List<(string, string?, string?)>();
        foreach (var (id, p) in batch.Prefabs)
        {
            string? was = Library.ReadPrefabText(id), now = p.ToJson();
            if (was != now) prefabChanges.Add((id, was, now));
        }
        foreach (var id in batch.DeletedPrefabs.Where(d => !batch.Prefabs.ContainsKey(d)))
            if (Library.ReadPrefabText(id) is { } was) prefabChanges.Add((id, was, null));
        if (before == after && prefabChanges.Count == 0 && batch.Assets.Count == 0) return;

        foreach (var staged in batch.Assets.Values) WriteAsset(staged);
        foreach (var (id, _, now) in prefabChanges)
        {
            if (now is null) File.Delete(Library.PrefabPath(id));
            else MapLibrary.WriteAtomic(Library.PrefabPath(id), Encoding.UTF8.GetBytes(now));
        }
        if (prefabChanges.Count > 0 || batch.Assets.Count > 0) Library.RebuildIndex();

        CopyDocument(batch.Doc);
        if (before != after || prefabChanges.Count > 0)
        {
            _undo.Add(new Entry(before, after, prefabChanges));
            _redo.Clear();
        }
        Touch();
    }

    void WriteAsset(StagedAsset staged)
    {
        var dir = Library.AssetDir(staged.Info.Id);
        Directory.CreateDirectory(dir);
        foreach (var rel in staged.Companions.Prepend(staged.MainFile))
        {
            var target = System.IO.Path.Combine(dir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(System.IO.Path.Combine(staged.SourceDir, rel), target, overwrite: true);
        }
        MapLibrary.WriteAtomic(System.IO.Path.Combine(dir, "asset.json"), CanonicalJson.Bytes(staged.Info.ToNode()));
    }

    void CopyDocument(MapDocument from)
    {
        Document.Name = from.Name;
        Document.NextId = from.NextId;
        Document.Objects.Clear();
        Document.Objects.AddRange(from.Objects);
    }

    void Touch()
    {
        Dirty = true;
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reverts the last batch (the document and any prefab files it changed; imported assets stay in the library).</summary>
    public OpResult Undo()
    {
        if (_undo.Count == 0) return OpResult.Fail("nothing-to-undo", "There is nothing to undo.", "undo");
        var e = _undo[^1];
        try { Restore(e.DocBefore, e.Prefabs.Select(p => (p.Id, p.Before))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OpResult.Fail("io-error", ex.Message, "undo"); }
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(e);
        Touch();
        return new OpResult(true, [], [], []);
    }

    public OpResult Redo()
    {
        if (_redo.Count == 0) return OpResult.Fail("nothing-to-redo", "There is nothing to redo.", "redo");
        var e = _redo[^1];
        try { Restore(e.DocAfter, e.Prefabs.Select(p => (p.Id, p.After))); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OpResult.Fail("io-error", ex.Message, "redo"); }
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(e);
        Touch();
        return new OpResult(true, [], [], []);
    }

    void Restore(string docJson, IEnumerable<(string Id, string? Text)> prefabs)
    {
        var list = prefabs.ToList();
        foreach (var (id, text) in list)
        {
            if (text is null) { var path = Library.PrefabPath(id); if (File.Exists(path)) File.Delete(path); }
            else MapLibrary.WriteAtomic(Library.PrefabPath(id), Encoding.UTF8.GetBytes(text));
        }
        if (list.Count > 0) Library.RebuildIndex();
        CopyDocument(MapDocument.Parse(docJson));
    }

    /// <summary>Writes the canonical document (atomically) to <paramref name="path"/> or the path it was loaded from.</summary>
    public void Save(string? path = null)
    {
        path ??= Path ?? throw new MapException("bad-args", "This map has no file yet; pass a path.");
        MapLibrary.WriteAtomic(path, Encoding.UTF8.GetBytes(Document.ToJson()));
        Path = System.IO.Path.GetFullPath(path);
        Dirty = false;
    }

    public OpResult Validate() => Document.Validate(Library);

    /// <summary>The objects unfolded into drawable parts (instances expanded, overrides applied).</summary>
    public IReadOnlyList<ResolvedPart> Resolve() => MapResolver.Flatten(Document, Context);

    public string PreviewSvg() => MapPreview.Render(Document, Context);
}
