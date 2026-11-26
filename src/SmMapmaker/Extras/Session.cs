using System.Text;
using System.Text.Json.Nodes;

namespace SmMapmaker;

/// <summary>
/// An open map plus its undo/redo journal. Every change is a batch of JSON operations (<see cref="Apply(IEnumerable{JsonObject}, bool)"/>): the batch is one
/// transaction (all or nothing) and one undo step. The editor and <c>smtool map</c> both go through this class, so they validate identically.
/// </summary>
public sealed class MapSession
{
    sealed record Entry(string DocBefore, string DocAfter, IReadOnlyList<(string Id, string? Before, string? After)> Prefabs, IReadOnlyList<(string Id, string Slot)>? DeletedAssets = null, IReadOnlyList<(string Id, string Slot)>? Replaced = null);

    /// <summary>The document text just before and just after the last committed batch (set by every successful <see cref="Apply(IEnumerable{JsonObject}, bool)"/> that is not a dry run): the same strings the
    /// session serialised for its own undo, so a caller that keeps snapshots needs no second serialisation of the whole map (the editor's edit-hang fix, 2026-11-27). Null before the first.</summary>
    public string? LastBeforeJson { get; private set; }
    public string? LastAfterJson { get; private set; }
    /// <summary>Assets (id, trash slot) the last committed batch moved to the library trash (the editor keeps them for its own undo step).</summary>
    public IReadOnlyList<(string Id, string Slot)> LastTrashed { get; private set; } = [];
    /// <summary>Per id deleted and re-imported by the last batch: the trash slot that parks the new copy while the batch is undone (empty for most batches).</summary>
    public IReadOnlyList<(string Id, string Slot)> LastReplaced { get; private set; } = [];

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
        var session = new MapSession(MapDocument.Parse(File.ReadAllText(path)), library, sm1, System.IO.Path.GetFullPath(path));
        var notes = new List<string>();
        session.RestoreEmbeddedPrefabs(notes);
        // Images that came beside a shared map and the library lacks are added to the library (like embedded prefabs), so a clean library resolves them from then on.
        foreach (var img in session.Document.Images)
            try
            {
                if (File.Exists(System.IO.Path.Combine(MapImages.LibraryDir(library), img.FileName))) continue;
                if (MapImages.Read(img, path, library) is { } data) MapImages.Store(MapImages.LibraryDir(library), img, data);
                else notes.Add($"The image '{img.Name}' of this map is missing (put the map's images folder beside it, or open its .smpack).");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"The image '{img.Name}' could not be added to the library: {e.Message}"); }
        int to = session.Document.WriteVersion;
        if (session.Document.LoadedVersion < to)
        {
            // An older file (format 1 or 2; a format 3 file needs nothing): the original is kept first, never replaced, then the file is written in the lowest format that holds it (nothing in it
            // is lost: the upgrade steps only add). Save makes the backup (it backs up any file on disk older than what it writes).
            int from = session.Document.LoadedVersion;
            try
            {
                // The upgrade rewrite keeps the file's own save time, so merely opening an old map never makes it the "newest" copy of its name
                // for CustomMaps.Locate's newest-file fallback (review, pass 3).
                var savedAt = File.GetLastWriteTimeUtc(path);
                session.Save(null, everyBackup: true);
                try { File.SetLastWriteTimeUtc(path, savedAt); } catch (Exception t) when (t is IOException or UnauthorizedAccessException) { }
                notes.Add($"This map was saved by an older Mapmaker (file format {from}); it was upgraded to format {to}. The original is kept as {System.IO.Path.GetFileName(session.LastBackup ?? "")}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or MapException)
            {
                session.Dirty = true;
                notes.Add($"This map was saved by an older Mapmaker (file format {from}) and is read as format {to}, but the file could not be backed up and rewritten ({e.Message}); Save map will write it.");
            }
        }
        session.OpenNotes = notes;
        return session;
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
    /// <summary>How many batches can be undone / redone (tests and tools).</summary>
    public int UndoDepth => _undo.Count;
    public int RedoDepth => _redo.Count;

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
        batch.Doc.PruneGroups();
        // The document a batch leaves must still be one every build reads back: its file as Save writes it (UTF-8, with the prefabs Save embeds) within the limit.
        string after = batch.Doc.ToJson();
        long bytes = MapDocument.FileBytes(after);
        // The exact pass (the whole file re-serialised with the prefabs Save would embed) runs only when an upper bound says the file could be near the limit: the document as it is plus
        // every used prefab's own text with the indentation it gets inside the map (cached per definition). An ordinary edit on a map with prefabs serialises the document once.
        List<Prefab>? used = batch.Doc.Objects.Any(o => o.Prefab is not null) ? UsedPrefabs(batch.Doc, id => { try { return batch.GetPrefab(id); } catch (MapException) { return null; } }) : null;
        if (used is not null && bytes + used.Sum(EmbeddedBytesBound) + 32 > MapDocument.MaxChars)
        {
            var saved = batch.Doc.Clone();
            saved.EmbeddedPrefabs = used;
            bytes = MapDocument.FileBytes(saved.ToJson());
        }
        if (bytes > MapDocument.MaxChars)
            return Failure(new OpError("too-big", $"The map would be larger than {MapDocument.MaxChars / 1_000_000} MB.", "commit"), dryRun);
        var result = new OpResult(true, batch.Created, batch.Warnings, [])
        {
            CreatedPrefabs = batch.CreatedPrefabs, CreatedAssets = batch.CreatedAssets, Mapping = batch.Mapping, DryRun = dryRun, Refresh = batch.Refresh,
            CreatedMaterials = batch.CreatedMaterials, CreatedImages = batch.CreatedImages,
        };
        if (dryRun) return result;
        try { Commit(batch, after); }
        catch (MapException e) { return Failure(new OpError(e.Code, e.Message, "commit"), false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failure(new OpError("io-error", $"Could not write the library: {e.Message}", "commit"), false);
        }
        return result;
    }

    static OpResult Failure(OpError error, bool dryRun) => new(false, [], [], [error]) { DryRun = dryRun };

    void Commit(Batch batch, string after)
    {
        string before = Document.ToJson();
        LastBeforeJson = before; LastAfterJson = after;
        var prefabChanges = new List<(string, string?, string?)>();
        foreach (var (id, p) in batch.Prefabs)
        {
            string? was = Library.ReadPrefabText(id), now = p.ToJson();
            if (was != now) prefabChanges.Add((id, was, now));
        }
        foreach (var id in batch.DeletedPrefabs.Where(d => !batch.Prefabs.ContainsKey(d)))
            if (Library.ReadPrefabText(id) is { } was) prefabChanges.Add((id, was, null));
        var trashIds = batch.DeletedAssets.ToList();
        // Images are content-addressed (the file name is the SHA-256 of its bytes), so writing one is idempotent and never replaces another picture; one that is already there is kept. Written
        // before the no-change return: re-adding an image the map already lists (its library copy lost) repairs the library without changing the document.
        foreach (var (imgId, data) in batch.StagedImages)
            if (batch.Doc.Images.FirstOrDefault(i => i.Id == imgId) is { } img) MapImages.Store(MapImages.LibraryDir(Library), img, data);
        if (before == after && prefabChanges.Count == 0 && batch.Assets.Count == 0 && trashIds.Count == 0) { LastTrashed = []; LastReplaced = []; return; }   // a batch that changes nothing trashes nothing: the editor must not reuse the last batch's slots for this step

        // Storage steps run as a transaction: each records its inverse as it succeeds; on any failure the inverses run in reverse order (best effort) and the
        // document, LastTrashed and the undo/redo stacks stay as they were. (A journal that survives a process crash is not part of this.)
        // A folder to be replaced that holds a link or a synced-folder placeholder (reparse point) cannot be backed up faithfully: refuse before anything changes.
        foreach (var staged in batch.Assets.Values)
            if (!trashIds.Contains(staged.Info.Id) && Directory.Exists(Library.AssetDir(staged.Info.Id))) EnsureNoReparse(Library.AssetDir(staged.Info.Id), staged.Info.Id);
        var inverses = new List<(string What, Action Undo)>();
        string? scratch = null;
        var trashed = new List<(string, string)>();
        var replaced = new List<(string, string)>();
        bool trashExisted = Directory.Exists(Library.TrashAssetsDir), trashParentExisted = Directory.Exists(System.IO.Path.GetDirectoryName(Library.TrashAssetsDir));
        try
        {
            // The old copy of a deleted asset goes to the trash before a staged import of the same id is written (the import wins).
            foreach (var id in trashIds)
            {
                string slot = MapLibrary.NewTrashSlot(id);
                string src = Library.AssetDir(id), dst = System.IO.Path.Combine(Library.TrashAssetsDir, slot);
                inverses.Add(($"restore trashed asset '{id}'", () => { if (Directory.Exists(dst) && !Directory.Exists(src)) { Directory.CreateDirectory(Library.AssetsDir); Directory.Move(dst, src); } }));
                trashed.Add((id, Library.TrashAsset(id, slot)));
                // delete then import of the same id: the import wins; the new copy gets its own trash slot so undo can park it there and redo bring it back
                if (batch.Assets.ContainsKey(id)) replaced.Add((id, MapLibrary.NewTrashSlot(id)));
            }
            foreach (var staged in batch.Assets.Values)
            {
                var dir = Library.AssetDir(staged.Info.Id);
                if (Directory.Exists(dir))
                {
                    scratch ??= System.IO.Path.Combine(Library.Root, "library", ".txn-" + Guid.NewGuid().ToString("N"));
                    string backup = System.IO.Path.Combine(scratch, staged.Info.Id);
                    CopyDirectory(dir, backup);
                    inverses.Add(($"restore asset '{staged.Info.Id}' (its pre-batch copy is kept in {scratch})", () => RestoreDirectory(backup, dir)));
                }
                else inverses.Add(($"remove asset '{staged.Info.Id}'", () => { if (Directory.Exists(dir)) Directory.Delete(dir, true); }));
                WriteAsset(staged);
            }
            foreach (var (id, _, now) in prefabChanges)
            {
                var path = Library.PrefabPath(id);
                byte[]? old = File.Exists(path) ? File.ReadAllBytes(path) : null;
                inverses.Add(($"restore prefab '{id}'", () =>
                {
                    if (old is null) { if (File.Exists(path)) File.Delete(path); }
                    else MapLibrary.WriteAtomic(path, old);
                }));
                if (now is null) { Library.Fault("prefab-delete"); File.Delete(path); }
                else { Library.Fault("prefab-write"); MapLibrary.WriteAtomic(path, Encoding.UTF8.GetBytes(now)); }
            }
            if (prefabChanges.Count > 0 || batch.Assets.Count > 0) Library.RebuildIndex();
        }
        catch (Exception failure)
        {
            var problems = new List<string>();
            for (int i = inverses.Count - 1; i >= 0; i--)
            {
                try { inverses[i].Undo(); }
                catch (Exception e) { problems.Add($"could not {inverses[i].What}: {e.Message}"); }
            }
            try { Library.RebuildIndex(); }
            catch (Exception e) { problems.Add($"could not rebuild the library index: {e.Message}"); }
            // A failed trash step may have created the (still empty) trash folders.
            if (!trashExisted) RemoveIfEmpty(Library.TrashAssetsDir);
            if (!trashParentExisted) RemoveIfEmpty(System.IO.Path.GetDirectoryName(Library.TrashAssetsDir)!);
            if (problems.Count == 0)
            {
                if (CleanScratch(scratch) is { } leaked) throw new IOException($"{failure.Message} (The save was rolled back; {leaked})", failure);
                throw;
            }
            // Kept for the owner: the pre-batch copies of replaced assets stay in the scratch folder when an inverse failed.
            throw new IOException($"{failure.Message} The save was rolled back only in part: {string.Join("; ", problems)}.", failure);
        }
        if (CleanScratch(scratch) is { } left) batch.Warnings.Add(left);
        LastTrashed = trashed;
        LastReplaced = replaced;

        CopyDocument(batch.Doc);
        if (before != after || prefabChanges.Count > 0 || trashed.Count > 0)
        {
            _undo.Add(new Entry(before, after, prefabChanges, trashed, replaced));
            _redo.Clear();
        }
        Touch();
    }

    /// <summary>Deletes the scratch folder (clearing read-only attributes first); returns a sentence naming it when that fails, else null.</summary>
    static string? CleanScratch(string? scratch)
    {
        if (scratch is null || !Directory.Exists(scratch)) return null;
        try { ClearReadOnly(scratch); Directory.Delete(scratch, true); return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"the temporary folder {scratch} could not be removed ({e.Message}); it is safe to delete"; }
    }

    static void ClearReadOnly(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir))
            if ((File.GetAttributes(f) & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly) File.SetAttributes(f, FileAttributes.Normal);
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!IsReparse(d)) ClearReadOnly(d);
    }

    /// <summary>A junction or symlink: never followed or written through, so nothing outside the library is touched.</summary>
    static bool IsReparse(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    static void RemoveIfEmpty(string dir)
    {
        try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Makes <paramref name="dir"/> match <paramref name="backup"/>: copies the backup over it first (overwriting, clearing read-only), then removes what the backup does not have. Never deletes the folder wholesale, so a failure part-way leaves the backup's files in place.</summary>
    static void RestoreDirectory(string backup, string dir)
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Directory.EnumerateFiles(backup))
        {
            if (IsReparse(f)) continue;
            var target = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(f));
            if (File.Exists(target) && !IsReparse(target)) File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(f, target, overwrite: true);
        }
        foreach (var d in Directory.EnumerateDirectories(backup).Where(d => !IsReparse(d))) RestoreDirectory(d, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(d)));
        // Entries the backup does not have are removed, but never a reparse point (a link or a synced-folder placeholder): it was not backed up, so it is left alone.
        foreach (var f in Directory.EnumerateFiles(dir))
            if (!IsReparse(f) && !File.Exists(System.IO.Path.Combine(backup, System.IO.Path.GetFileName(f)))) { File.SetAttributes(f, FileAttributes.Normal); File.Delete(f); }
        foreach (var d in Directory.EnumerateDirectories(dir))
            if (!IsReparse(d) && !Directory.Exists(System.IO.Path.Combine(backup, System.IO.Path.GetFileName(d)))) { ClearReadOnly(d); Directory.Delete(d, true); }
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from))
        {
            if (IsReparse(f)) throw new IOException($"'{f}' is a link or a synced-folder placeholder and cannot be backed up safely.");
            File.Copy(f, System.IO.Path.Combine(to, System.IO.Path.GetFileName(f)), overwrite: true);
        }
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            if (IsReparse(d)) throw new IOException($"'{d}' is a link or a synced-folder placeholder and cannot be backed up safely.");
            CopyDirectory(d, System.IO.Path.Combine(to, System.IO.Path.GetFileName(d)));
        }
    }

    /// <summary>Throws (naming the first offender) when the folder or anything in it is a reparse point: replacing it could not be rolled back without losing data.</summary>
    static void EnsureNoReparse(string dir, string id)
    {
        if (IsReparse(dir)) throw new IOException($"Asset '{id}' cannot be replaced: its folder is a link or a synced-folder placeholder. Nothing was changed.");
        foreach (var e in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            if (IsReparse(e)) throw new IOException($"Asset '{id}' cannot be replaced: '{System.IO.Path.GetRelativePath(dir, e)}' is a link or a synced-folder placeholder (for example a cloud file not downloaded), which cannot be backed up safely. Nothing was changed.");
    }

    void WriteAsset(StagedAsset staged)
    {
        var dir = Library.AssetDir(staged.Info.Id);
        Directory.CreateDirectory(dir);
        foreach (var rel in staged.Companions.Prepend(staged.MainFile))
        {
            Library.Fault("asset-write");
            var target = System.IO.Path.Combine(dir, rel);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(System.IO.Path.Combine(staged.SourceDir, rel), target, overwrite: true);
        }
        Library.Fault("asset-write");
        MapLibrary.WriteAtomic(System.IO.Path.Combine(dir, "asset.json"), CanonicalJson.Bytes(staged.Info.ToNode()));
    }

    void CopyDocument(MapDocument from)
    {
        Document.Name = from.Name;
        Document.NextId = from.NextId;
        Document.Modes = [.. from.Modes];
        Document.Objects.Clear();
        Document.Objects.AddRange(from.Objects);
        Document.Groups.Clear();
        Document.Groups.AddRange(from.Groups);
        Document.Grid = from.Grid;
        Document.Look = from.Look;
        Document.RemovedSrc = [.. from.RemovedSrc];
        Document.Materials = [.. from.Materials];
        Document.Images = [.. from.Images];
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
        try { Restore(e.DocBefore, e.Prefabs.Select(p => (p.Id, p.Before))); foreach (var (a, slot) in e.DeletedAssets ?? []) Library.UndoAssetSwap(a, slot, e.Replaced?.FirstOrDefault(r => r.Id == a).Slot); }
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
        try { Restore(e.DocAfter, e.Prefabs.Select(p => (p.Id, p.After))); foreach (var (a, slot) in e.DeletedAssets ?? []) Library.RedoAssetSwap(a, slot, e.Replaced?.FirstOrDefault(r => r.Id == a).Slot); }
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
    public void Save(string? path = null) => Save(path, everyBackup: false);

    void Save(string? path, bool everyBackup)
    {
        path ??= Path ?? throw new MapException("bad-args", "This map has no file yet; pass a path.");
        EmbedUsedPrefabs();
        // The map's images go beside it first (images/<sha256>.<format>), so the file and that folder are the whole map for another PC and the map never names a picture not yet written.
        var copyFailures = new List<string>();
        var missing = Document.Images.Count == 0 ? [] : MapImages.CarryBeside(Document, path, Library, Path, copyFailures);
        // A file on disk in an older format than this save writes is kept beside it first (name.smmap.bak-v<n>, never replaced), so a build that reads only that format still has its copy.
        LastBackup = null;
        // One backup per distinct older file: a map flipped between format 3 and 4 again and again keeps one .bak-v3, but a format-3 file that changed since its last backup (format-3 work between
        // the flips) gets a new one beside it (.bak-v3-2, ...), so the copy a format-3 build can open is never stale; nothing is ever replaced. The open-time upgrade of a format 1 or 2 file always
        // keeps a new one.
        if (File.Exists(path) && MapUpgrade.VersionOfFile(path) is var onDisk and > 0 && onDisk < Document.WriteVersion && (everyBackup || !SameAsABackup(path, onDisk)))
            LastBackup = MapUpgrade.Backup(path, onDisk);
        MapLibrary.WriteAtomic(path, Encoding.UTF8.GetBytes(Document.ToJson()));
        SaveNotes = [.. missing.Count == 0 ? [] : new[] { $"{missing.Count} image(s) of this map are neither in the library nor beside the map; the game will refuse the map until they are added again." }, .. copyFailures];
        Path = System.IO.Path.GetFullPath(path);
        Dirty = false;
    }

    /// <summary>
    /// Puts the definition of every prefab the objects use (and the prefabs those use) into the document, so the saved file brings them along (a map sent to another PC no longer has
    /// "unknown prefab" instances). A prefab the library lacks but an earlier save embedded stays embedded.
    /// </summary>
    /// <summary>Whether <paramref name="path"/>'s bytes equal one of its existing <c>.bak-v&lt;version&gt;</c> backups.</summary>
    static bool SameAsABackup(string path, int version)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;
        var name = System.IO.Path.GetFileName(path) + ".bak-v" + version;
        var current = File.ReadAllBytes(path);
        foreach (var f in Directory.EnumerateFiles(dir, name + "*"))
        {
            var rest = System.IO.Path.GetFileName(f)[name.Length..];
            if (rest.Length > 0 && !(rest[0] == '-' && rest.Skip(1).All(char.IsAsciiDigit))) continue;
            try { if (new FileInfo(f).Length == current.LongLength && File.ReadAllBytes(f).AsSpan().SequenceEqual(current)) return true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    void EmbedUsedPrefabs() => Document.EmbeddedPrefabs = UsedPrefabs(Document, id => { try { return Library.GetPrefab(id); } catch (MapException) { return null; } });

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Prefab, System.Runtime.CompilerServices.StrongBox<long>> EmbeddedBytes = new();

    /// <summary>
    /// At least the bytes <paramref name="p"/> adds to a saved map file: its canonical text at depth 0, plus the 4 spaces per line it is indented by inside the map's <c>prefabs</c> list,
    /// plus a separator. Cached per definition (prefabs are immutable records).
    /// </summary>
    static long EmbeddedBytesBound(Prefab p) => EmbeddedBytes.GetValue(p, x =>
    {
        string text = CanonicalJson.Write(x.ToNode());
        long lines = text.Count(c => c == '\n') + 1;
        return new System.Runtime.CompilerServices.StrongBox<long>(MapDocument.FileBytes(text) + 4 * lines + 8);
    }).Value;

    /// <summary>The prefabs <paramref name="doc"/>'s objects use (and the prefabs those use): from <paramref name="lookup"/> first, else the ones the document already embeds.</summary>
    static List<Prefab> UsedPrefabs(MapDocument doc, Func<string, Prefab?> lookup)
    {
        var wanted = new SortedSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(doc.Objects.Where(o => o.Prefab is not null).Select(o => o.Prefab!));
        var result = new List<Prefab>();
        while (queue.Count > 0)
        {
            string id = queue.Dequeue();
            if (!wanted.Add(id)) continue;
            var p = lookup(id) ?? doc.EmbeddedPrefabs.FirstOrDefault(e => e.Id == id);
            if (p is null) continue;
            result.Add(p);
            foreach (var part in p.Parts.Where(x => x.Prefab is not null)) queue.Enqueue(part.Prefab!);
        }
        return result;
    }

    /// <summary>
    /// What opening a file did that the maker should be told: prefabs that came with the map and were added to the library, a library prefab that differs from the saved one (the library's is used,
    /// never overwritten), and an upgrade of an older file format (the original kept as <c>.bak-v&lt;n&gt;</c>, then the file rewritten in the current format).
    /// </summary>
    public IReadOnlyList<string> OpenNotes { get; private set; } = [];

    /// <summary>What the last <see cref="Save"/> could not do (an image that is nowhere to be copied); empty when it saved everything.</summary>
    public IReadOnlyList<string> SaveNotes { get; private set; } = [];

    /// <summary>The backup the last <see cref="Save"/> made of an older-format file it replaced (<c>name.smmap.bak-v&lt;n&gt;</c>), or null.</summary>
    public string? LastBackup { get; private set; }

    void RestoreEmbeddedPrefabs(List<string> notes)
    {
        foreach (var emb in Document.EmbeddedPrefabs)
        {
            Prefab? have = null;
            try { have = Library.GetPrefab(emb.Id); } catch (MapException) { }
            if (have is null) { Library.SavePrefab(emb); notes.Add($"The prefab '{emb.Id}' came with this map and was added to your library."); }
            else if (have.ToJson() != emb.ToJson()) notes.Add($"Your library's prefab '{emb.Id}' differs from the copy saved in this map; yours is used and was not changed.");
        }
    }

    public OpResult Validate() => Document.Validate(Library);

    /// <summary>The objects unfolded into drawable parts (instances expanded, overrides applied).</summary>
    public IReadOnlyList<ResolvedPart> Resolve() => MapResolver.Flatten(Document, Context);

    public string PreviewSvg() => MapPreview.Render(Document, Context);
}
