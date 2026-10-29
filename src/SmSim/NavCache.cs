namespace SmSim;

/// <summary>
/// The test bots' navigation-graph cache on disk (test aid): one file per map and cache key
/// (<c>&lt;map&gt;-&lt;key&gt;.nav</c>, written by <see cref="NavGraph.Write"/>) in a private folder outside the repo. A damaged
/// or unreadable file is deleted and the graph rebuilt; one process on the machine builds a given graph at a time (a
/// named mutex), the others wait and read its file. Old graphs of a map (beyond <see cref="KeepPerMap"/>) and stale temporary
/// files (older than a day) are deleted when a new graph is written. A map has a graph per side (the other side's spawn-room
/// blockers are walls only for it) and, per jump-pack class, a set of jump links (<c>&lt;map&gt;-&lt;key&gt;.navjump</c>,
/// <see cref="NavJumpSet"/>) kept the same way.
/// </summary>
public static class NavCache
{
    /// <summary>Graphs kept per map (the newest).</summary>
    public const int KeepPerMap = 8;

    /// <summary>The cache file of <paramref name="mapName"/>'s graph with <paramref name="key"/> in <paramref name="dir"/>.</summary>
    public static string FileFor(string dir, string mapName, ulong key) => Path.Combine(dir, $"{mapName}-{key:X16}.nav");

    /// <summary>
    /// The graph for <paramref name="key"/>: read from <paramref name="dir"/> when a valid file is there, else built with
    /// <paramref name="build"/> and written (best effort). <paramref name="note"/> says which, for logs.
    /// </summary>
    public static NavGraph LoadOrBuild(string dir, string mapName, ulong key, NavSettings settings, Func<NavGraph> build, out string note,
                                       TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        string file = FileFor(dir, mapName, key);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string dropped = "";
        if (TryRead(file, settings, key, ref dropped) is { } early)
        {
            note = $"read from the cache in {watch.ElapsedMilliseconds} ms: {Describe(early)}";
            return early;
        }
        using var mutex = new Mutex(false, $@"Local\sm-reimpl-nav-{key:X16}");
        bool owned;
        try { owned = mutex.WaitOne(wait ?? TimeSpan.FromMinutes(5)); }
        catch (AbandonedMutexException) { owned = true; }
        try
        {
            if (TryRead(file, settings, key, ref dropped) is { } late)
            {
                note = $"read from the cache in {watch.ElapsedMilliseconds} ms (after waiting for another build): {Describe(late)}";
                return late;
            }
            var graph = build();
            note = $"{dropped}built in {graph.BuildMilliseconds} ms: {Describe(graph)}; {graph.BuildNote}";
            try
            {
                Directory.CreateDirectory(dir);
                Prune(dir, mapName);
                string temp = file + "." + Environment.ProcessId + ".tmp";
                using (var stream = new BufferedStream(File.Create(temp), 1 << 16)) graph.Write(stream);
                File.Move(temp, file, overwrite: true);
                note += "; cached";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note += $"; not cached ({e.Message})"; }
            return graph;
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    /// <summary>The cache file of the jump links with <paramref name="key"/>.</summary>
    public static string JumpFileFor(string dir, string mapName, ulong key) => Path.Combine(dir, $"{mapName}-{key:X16}.navjump");

    /// <summary>
    /// The jump links for <paramref name="key"/> (<see cref="NavJumpSet.KeyFor"/>) over a graph of <paramref name="nodes"/> nodes:
    /// read from <paramref name="dir"/> when a valid file is there, else built with <paramref name="build"/> and written (best effort).
    /// </summary>
    public static NavJumpSet LoadOrBuildJumps(string dir, string mapName, ulong key, int nodes, Func<NavJumpSet> build, out string note, TimeSpan? wait = null)
    {
        ArgumentNullException.ThrowIfNull(build);
        string file = JumpFileFor(dir, mapName, key);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        if (TryReadJumps(file, nodes, key) is { } early)
        {
            note = $"jump links read from the cache in {watch.ElapsedMilliseconds} ms: {early.LinkCount:N0} links";
            return early;
        }
        using var mutex = new Mutex(false, $@"Local\sm-reimpl-navjump-{key:X16}");
        bool owned;
        try { owned = mutex.WaitOne(wait ?? TimeSpan.FromMinutes(10)); }
        catch (AbandonedMutexException) { owned = true; }
        try
        {
            if (TryReadJumps(file, nodes, key) is { } late)
            {
                note = $"jump links read from the cache in {watch.ElapsedMilliseconds} ms (after waiting for another build): {late.LinkCount:N0} links";
                return late;
            }
            var set = build();
            note = $"jump links built in {set.BuildMilliseconds} ms: {set.LinkCount:N0} links; {set.BuildNote}";
            try
            {
                Directory.CreateDirectory(dir);
                Prune(dir, mapName);
                string temp = file + "." + Environment.ProcessId + ".tmp";
                using (var stream = new BufferedStream(File.Create(temp), 1 << 16)) set.Write(stream);
                File.Move(temp, file, overwrite: true);
                note += "; cached";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { note += $"; not cached ({e.Message})"; }
            return set;
        }
        finally { if (owned) mutex.ReleaseMutex(); }
    }

    static NavJumpSet? TryReadJumps(string file, int nodes, ulong key)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var stream = new BufferedStream(File.OpenRead(file), 1 << 16);
            if (NavJumpSet.Read(stream, nodes, key) is { } set) return set;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    static string Describe(NavGraph g) => $"{g.NodeCount:N0} cells, {g.LinkCount:N0} links ({g.DropLinkCount:N0} drops)";

    /// <summary>The graph in <paramref name="file"/>, or null; a file that cannot be read as this key's graph is deleted.</summary>
    static NavGraph? TryRead(string file, NavSettings settings, ulong key, ref string dropped)
    {
        if (!File.Exists(file)) return null;
        try
        {
            using var stream = new BufferedStream(File.OpenRead(file), 1 << 16);
            if (NavGraph.Read(stream, settings, key) is { } graph) return graph;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { File.Delete(file); dropped = "a damaged cache file was deleted; "; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { dropped = "a damaged cache file could not be deleted; "; }
        return null;
    }

    /// <summary>Deletes temporary files older than a day and all but the newest <see cref="KeepPerMap"/> - 1 graphs of <paramref name="mapName"/>.</summary>
    public static void Prune(string dir, string mapName)
    {
        if (!Directory.Exists(dir)) return;
        var now = DateTime.UtcNow;
        foreach (var temp in Directory.EnumerateFiles(dir, "*.nav.*.tmp").Concat(Directory.EnumerateFiles(dir, "*.navjump.*.tmp")))
            try { if (now - File.GetLastWriteTimeUtc(temp) > TimeSpan.FromDays(1)) File.Delete(temp); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // Exact extensions: Windows matches "*.nav" against ".navjump" too (and the like), so the pattern alone is not enough.
        var graphs = Directory.EnumerateFiles(dir, mapName + "-*.nav")
            .Where(f => Path.GetExtension(f) == ".nav" && Path.GetFileNameWithoutExtension(f).Length == mapName.Length + 17)   // <map>-<16 hex digits>
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var old in graphs.Skip(KeepPerMap - 1))
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var jumps = Directory.EnumerateFiles(dir, mapName + "-*.navjump")
            .Where(f => Path.GetExtension(f) == ".navjump" && Path.GetFileNameWithoutExtension(f).Length == mapName.Length + 17)
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var old in jumps.Skip(KeepPerMap - 1))
            try { File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
