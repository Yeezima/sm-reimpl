using System.Diagnostics;
using System.Globalization;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using SmContent.Extras.Musou;
using SmSim.Extras.Warfront.Maps;

namespace SmContent.Extras.Warfront.Maps;

/// <summary>
/// The Warfront lane slots' layout cache (slice W2): a slot's generated level and dressing in the private cache, <c>%LOCALAPPDATA%\sm-reimpl\cache\ashfall\warfront-&lt;slot&gt;-s&lt;seed&gt;-&lt;key&gt;-&lt;process&gt;.bin</c>, in the format and under the
/// key of <see cref="AshfallLayoutCache"/> (its writer, reader and checksum are used as they are; that class is not changed): a changed generator or reader never serves a stale file, a damaged file is refused and generated again. A derived
/// file, nothing from the install in it, never in the repository. Needed because that cache is keyed by layout (one file per band layout) and the nine slots share three layouts with different seeds.
/// </summary>
public static class WarfrontLayoutCache
{
    /// <summary>Newest files kept per slot and process kind.</summary>
    public const int KeepPerSlot = 2;

    static readonly Dictionary<string, (AshfallLevel Level, AshfallDressing Dressing)> Memo = [];
    static readonly object Gate = new();

    /// <summary>What the last build did, for the caller's log.</summary>
    public static string? LastNote { get; private set; }

    /// <summary>The cache folder (the Ashfall layout cache's).</summary>
    public static string Folder => AshfallLayoutCache.Folder;

    /// <summary>The file of a slot in <paramref name="folder"/> for a process kind.</summary>
    public static string PathIn(string folder, WarfrontSlot slot, string kind) =>
        Path.Combine(folder, string.Create(CultureInfo.InvariantCulture, $"warfront-{slot.Name}-s{slot.Seed}-{AshfallLayoutCache.Key}-{kind}.bin"));

    /// <summary>The slot's level and dressing: this process's memory, else the cache file, else generated (and written). A bad file or a disk error never throws from here.</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) Build(WarfrontSlot slot, out string note)
    {
        lock (Gate)
        {
            if (Memo.TryGetValue(slot.Name, out var memo)) { note = LastNote = $"{slot.Name}: already built in this process"; return memo; }
            (AshfallLevel Level, AshfallDressing Dressing) built;
            if (AshfallLayoutCache.Disabled)
            {
                var watch = Stopwatch.StartNew();
                var b = WarfrontSlots.Build(slot);
                built = (b.Level, b.Dressing);
                note = string.Create(CultureInfo.InvariantCulture, $"{slot.Name} generated in {watch.ElapsedMilliseconds} ms (hash {built.Level.Hash:X16}); cache off");
            }
            else built = LoadOrGenerate(slot, Folder, AshfallLayoutCache.ProcessKind, s => { var b = WarfrontSlots.Build(s); return (b.Level, b.Dressing); }, out note);
            Memo[slot.Name] = built;
            LastNote = note;
            return built;
        }
    }

    /// <summary>The disk side of a build (the tests pass their own folder and generator).</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) LoadOrGenerate(WarfrontSlot slot, string folder, string kind, Func<WarfrontSlot, (AshfallLevel Level, AshfallDressing Dressing)> generate, out string note)
    {
        var watch = Stopwatch.StartNew();
        string path = PathIn(folder, slot, kind), unreadable = "";
        try
        {
            if (File.Exists(path))
            {
                (AshfallLevel Level, AshfallDressing Dressing)? read;
                using (var stream = File.OpenRead(path)) read = AshfallLayoutCache.Read(stream, slot.Layout);
                if (read is { } found)
                {
                    note = string.Create(CultureInfo.InvariantCulture, $"{slot.Name} read from the cache in {watch.ElapsedMilliseconds} ms ({found.Level.Parts.Count} parts, hash {found.Level.Hash:X16})");
                    return found;
                }
                unreadable = "; the file there was another format, key or layout";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            unreadable = $"; the cache file was unreadable ({e.GetType().Name}: {e.Message})";
        }
        var built = generate(slot);
        long ms = watch.ElapsedMilliseconds;
        string written;
        try
        {
            Directory.CreateDirectory(folder);
            string temp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";   // unique per writer (another process, or a thread here): the rename below is the only step a reader can see
            try
            {
                using (var stream = File.Create(temp)) AshfallLayoutCache.Write(stream, slot.Layout, built.Level, built.Dressing);
                File.Move(temp, path, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            written = $"; written to {Path.GetFileName(path)}{Prune(folder, slot, kind, path)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            written = $"; not written ({e.GetType().Name}: {e.Message})";
        }
        note = string.Create(CultureInfo.InvariantCulture, $"{slot.Name} generated in {ms} ms (hash {built.Level.Hash:X16}){written}{unreadable}");
        return built;
    }

    /// <summary>Removes this slot's and kind's older files beyond <see cref="KeepPerSlot"/> (the newest by write time, <paramref name="keep"/> always kept); returns a note.</summary>
    static string Prune(string folder, WarfrontSlot slot, string kind, string keep)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles($"warfront-{slot.Name}-s*-{kind}.bin")
                .OrderByDescending(f => string.Equals(f.FullName, keep, StringComparison.OrdinalIgnoreCase)).ThenByDescending(f => f.LastWriteTimeUtc).ToList();
            int removed = 0;
            foreach (var f in files.Skip(KeepPerSlot)) { try { f.Delete(); removed++; } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            return removed > 0 ? $"; {removed} older file(s) removed" : "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    /// <summary>Forgets this process's copies (tests).</summary>
    public static void ForgetAll() { lock (Gate) Memo.Clear(); }

    /// <summary>Forgets this process's copy of one slot.</summary>
    public static void Forget(WarfrontSlot slot) { lock (Gate) Memo.Remove(slot.Name); }
}

/// <summary>
/// A Warfront lane slot's world (slice W2): the slot's band level (through <see cref="WarfrontLayoutCache"/>) as collision, composed with the hub by <see cref="TfwMap.Compose"/> (the Stormbird, the Firing Range, the hash as for any Ashfall
/// map), plus its walking graph in the Musou graph cache under the slot's own name. No Musou file is changed; nothing is read from the install.
/// </summary>
public static class WarfrontWorld
{
    /// <summary>The slot's war world: hub plus its one battlefield; node 1 to 3 of a lane are bands 0 to 2.</summary>
    public static TfwMapData Load(WarfrontSlot slot, out string note)
    {
        var (level, _) = WarfrontLayoutCache.Build(slot, out note);
        return Compose(level);
    }

    /// <summary>The world of a built level (a band level with its three points and two start areas).</summary>
    public static TfwMapData Compose(AshfallLevel level) =>
        TfwMap.Compose(AshfallContent.ToMapContent(level).ToCollisionWorld(), level.Points, level.Areas, level.Spawns, manufactorum: false);

    /// <summary>The map name's world, or null when <paramref name="mapName"/> is not a lane slot.</summary>
    public static TfwMapData? Load(string mapName, out string? problem)
    {
        problem = null;
        if (WarfrontSlots.ByName(mapName) is not { } slot) { problem = $"{mapName} is not a Warfront lane map"; return null; }
        try { return Load(slot, out _); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or AshfallGateException)
        {
            problem = $"the Warfront map {mapName} could not be built: {e.Message}";
            return null;
        }
    }

    /// <summary>The slot's walking graph over its battlefield, through the Musou graph cache as <c>&lt;slot&gt;-musou-&lt;key&gt;.nav</c> (<paramref name="cacheDirectory"/> null: the private cache).</summary>
    public static NavGraph Nav(WarfrontSlot slot, TfwMapData data, SimConfig config, out string note, string? cacheDirectory = null)
    {
        var settings = NavSettings.From(config);
        var area = data.Field.WalkArea();
        var mesh = data.Collision.Character;
        ulong key = NavGraph.KeyFor(mesh, data.Collision.KillVolumes, settings, area);
        return NavCache.LoadOrBuild(cacheDirectory ?? BotNavigation.CacheDirectory, slot.Name + TfwWorld.CacheSuffix, key, settings, () => NavGraph.Build(mesh, data.Collision.KillVolumes, settings, area), out note);
    }

    /// <summary>The slot's places for a battle (<paramref name="seed"/> is the battle's): the sites over the slot's own graph.</summary>
    public static TfwLayout Layout(WarfrontSlot slot, TfwMapData data, NavGraph nav, ulong seed) =>
        new TfwSites(data.Field, nav, data.Collision).Layout(slot.Lane, slot.Node, seed);
}
