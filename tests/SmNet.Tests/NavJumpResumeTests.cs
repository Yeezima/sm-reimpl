using SmSim;

namespace SmNet.Tests;

/// <summary>
/// bots/a1-perf (2026-11-23): a jump-link build that is stopped (the match or the bot process ended) keeps its finished chunks of take-off cells and goes on from them the next
/// time; the set it ends with is byte for byte the one an uninterrupted build makes. Synthetic geometry only (the gap level of <see cref="NavJumpTests"/>).
/// </summary>
public class NavJumpResumeTests
{
    static readonly SimConfig Config = JumpPackTests.Config with { JumpPacks = [JumpPackTests.Pack with { ReleaseCut = true }] };
    const int Assault = 0;
    static readonly NavJumpOptions Options = new();
    const int Chunk = 8;

    static (CollisionWorld World, NavGraph Graph) Gap()
    {
        var mesh = new NavigationTests.Level().Floor(-12, -8, 0, 8).Quad(new(14, -8, 0), new(60, -8, 0), new(60, 8, 0), new(14, 8, 0)).Mesh();
        var world = new CollisionWorld(mesh, mesh, null, null);
        return (world, NavGraph.Build(world.CharacterFor(Team.None), null, NavSettings.From(Config)));
    }

    static byte[] Bytes(NavJumpSet set)
    {
        using var stream = new MemoryStream();
        set.Write(stream);
        return stream.ToArray();
    }

    static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-navjump-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Saves through to the files and cancels the build after <c>stopAfter</c> saved chunks.</summary>
    sealed class StopAfter(INavJumpCheckpoint inner, int stopAfter, CancellationTokenSource cancel) : INavJumpCheckpoint
    {
        public int Saved, Loaded;
        public List<(int From, int To, float Cost, byte Direction, byte Hold, float Power)>? TryLoad(int chunk, int takeOffs, int firstNode, int count)
        {
            var r = inner.TryLoad(chunk, takeOffs, firstNode, count);
            if (r is not null) Loaded++;
            return r;
        }
        public void Save(int chunk, int takeOffs, int firstNode, int count, IReadOnlyList<(int From, int To, float Cost, byte Direction, byte Hold, float Power)> links)
        {
            inner.Save(chunk, takeOffs, firstNode, count, links);
            if (++Saved >= stopAfter) cancel.Cancel();
        }
    }

    [Fact]
    public void A_stopped_build_goes_on_from_its_saved_chunks_and_ends_with_the_same_set()
    {
        var (world, graph) = Gap();
        var whole = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        Assert.True(whole.LinkCount > 0, "the gap level has jump links");
        Assert.True(whole.TakeOffsTried > 3 * Chunk, $"enough take-off cells for several chunks ({whole.TakeOffsTried})");
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            using var cts = new CancellationTokenSource();
            var first = new StopAfter(NavCache.JumpParts(dir, "gap", key, graph.NodeCount), 2, cts);
            Assert.ThrowsAny<OperationCanceledException>(() => NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options with { Threads = 2 }, cts.Token, first, Chunk));
            Assert.Equal(2, first.Saved);
            Assert.Equal(2, Directory.GetFiles(dir, "gap-*.navjump.part*").Length);

            var second = new StopAfter(NavCache.JumpParts(dir, "gap", key, graph.NodeCount), int.MaxValue, new CancellationTokenSource());
            var resumed = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options with { Threads = 2 }, default, second, Chunk);
            Assert.Equal(2, second.Loaded);   // the two finished chunks were read back, not flown again
            Assert.Contains("read back from an unfinished build", resumed.BuildNote);
            Assert.Equal(Bytes(whole), Bytes(resumed));

            // A build that reads every chunk back flies nothing and still makes the same set.
            var third = new StopAfter(NavCache.JumpParts(dir, "gap", key, graph.NodeCount), int.MaxValue, new CancellationTokenSource());
            var replay = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, third, Chunk);
            Assert.Equal(0, replay.Simulated);
            Assert.Equal(Bytes(whole), Bytes(replay));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_damaged_or_foreign_chunk_is_not_read_back()
    {
        var (world, graph) = Gap();
        var whole = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            var parts = NavCache.JumpParts(dir, "gap", key, graph.NodeCount);
            NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, parts, Chunk);
            var files = Directory.GetFiles(dir, "gap-*.navjump.part*").OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.True(files.Length > 3);
            // damage one chunk; another key's checkpoint and another chunk size do not match
            var bytes = File.ReadAllBytes(files[1]);
            bytes[bytes.Length / 2] ^= 0x20;
            File.WriteAllBytes(files[1], bytes);
            Assert.Null(NavCache.JumpParts(dir, "gap", key ^ 1, graph.NodeCount).TryLoad(0, whole.TakeOffsTried, 0, Chunk));
            var again = new StopAfter(NavCache.JumpParts(dir, "gap", key, graph.NodeCount), int.MaxValue, new CancellationTokenSource());
            var afterDamage = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, again, Chunk);
            Assert.Equal(files.Length - 1, again.Loaded);   // the damaged chunk was dropped and flown again
            Assert.Equal(Bytes(whole), Bytes(afterDamage));
            var counting = new StopAfter(parts, int.MaxValue, new CancellationTokenSource());
            var rebuilt = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, counting, Chunk * 2);   // other chunk bounds: nothing matches (the stale chunks are dropped)
            Assert.Equal(0, counting.Loaded);
            Assert.Equal(Bytes(whole), Bytes(rebuilt));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static int DeadPid()
    {
        for (int pid = 999_983; pid > 1000; pid -= 7)
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); }
            catch (ArgumentException) { return pid; }
        throw new InvalidOperationException("no free process id");
    }

    [Fact]
    public void One_process_holds_a_maps_build_lock_and_a_stale_or_orphaned_lock_is_taken_over()
    {
        string dir = TempDir();
        try
        {
            using (var held = NavBuildLock.TryAcquire(dir, "gap"))
            {
                Assert.NotNull(held);
                Assert.Null(NavBuildLock.TryAcquire(dir, "gap"));   // fresh and its holder runs: taken
                using var other = NavBuildLock.TryAcquire(dir, "other");
                Assert.NotNull(other);   // another map's lock is its own
            }
            Assert.False(File.Exists(NavBuildLock.FileFor(dir, "gap")));   // released
            // an orphaned lock (its process is gone) is taken over at once
            string file = NavBuildLock.FileFor(dir, "gap");
            File.WriteAllText(file, $"{DeadPid()} 2026-11-24T00:00:00Z");
            using (var takeover = NavBuildLock.TryAcquire(dir, "gap")) Assert.NotNull(takeover);
            // a lock of a running process that has not been refreshed for longer than the stale time is taken over too
            File.WriteAllText(file, $"{Environment.ProcessId} 2026-11-24T00:00:00Z");
            Assert.Null(NavBuildLock.TryAcquire(dir, "gap"));
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-NavBuildLock.StaleSeconds - 5));
            using (var stale = NavBuildLock.TryAcquire(dir, "gap")) Assert.NotNull(stale);
            // a waiter stops waiting when its build is cancelled
            using var blocker = NavBuildLock.TryAcquire(dir, "gap");
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            Assert.ThrowsAny<OperationCanceledException>(() => NavBuildLock.Acquire(dir, "gap", cts.Token, poll: TimeSpan.FromMilliseconds(50)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_process_that_cannot_be_asked_counts_as_running_so_the_locks_age_decides()
    {
        Assert.True(NavBuildLock.IsRunning(DeadPid(), _ => throw new System.ComponentModel.Win32Exception(5)));   // access denied under a reused id
        Assert.True(NavBuildLock.IsRunning(DeadPid(), _ => throw new NotSupportedException()));
        Assert.False(NavBuildLock.IsRunning(DeadPid(), _ => throw new ArgumentException("no such process")));
        Assert.False(NavBuildLock.IsRunning(DeadPid(), _ => true));   // exited
        Assert.True(NavBuildLock.IsRunning(DeadPid(), _ => false));
        Assert.False(NavBuildLock.IsRunning(DeadPid()));   // the real query: gone
        Assert.True(NavBuildLock.IsRunning(Environment.ProcessId));
    }

    [Fact]
    public void A_build_that_saves_no_chunk_lets_its_lock_go_stale_and_a_saved_chunk_refreshes_it()
    {
        string dir = TempDir();
        try
        {
            // Two locks, so no check races a timer on a loaded thread pool: a late callback only re-reads the clock, so a stalled lock stays stalled however late it runs,
            // and the moving build's stall window (60 s) is far longer than any wait here.
            var old = DateTime.UtcNow.AddHours(-1);
            string moving = NavBuildLock.FileFor(dir, "moving");
            using (NavBuildLock.TryAcquire(dir, "moving", heartbeat: TimeSpan.FromMilliseconds(40), stall: TimeSpan.FromSeconds(60)))
            {
                File.SetLastWriteTimeUtc(moving, old);
                Assert.True(SpinWait.SpinUntil(() => File.GetLastWriteTimeUtc(moving) > old.AddMinutes(30), TimeSpan.FromSeconds(30)), "the heartbeat refreshes a lock whose build moves on");
            }
            string file = NavBuildLock.FileFor(dir, "gap");
            using var held = NavBuildLock.TryAcquire(dir, "gap", heartbeat: TimeSpan.FromMilliseconds(40), stall: TimeSpan.FromMilliseconds(200))!;
            Thread.Sleep(600);   // past the stall time with no chunk saved
            File.SetLastWriteTimeUtc(file, old);
            Thread.Sleep(300);
            Assert.Equal(old, File.GetLastWriteTimeUtc(file));   // the heartbeat stopped
            Assert.True(NavBuildLock.IsStale(file, TimeSpan.FromSeconds(NavBuildLock.StaleSeconds)));
            // a chunk saved through the build's checkpoint counts as progress: the lock is fresh again at once
            var (_, graph) = Gap();
            NavCache.JumpParts(dir, "gap", 7, graph.NodeCount).Save(0, 1, 0, 1, []);
            Assert.False(NavBuildLock.IsStale(file, TimeSpan.FromSeconds(NavBuildLock.StaleSeconds)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_waiter_gives_up_after_its_upper_bound()
    {
        string dir = TempDir();
        try
        {
            using var blocker = NavBuildLock.TryAcquire(dir, "gap");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(NavBuildLock.Acquire(dir, "gap", poll: TimeSpan.FromMilliseconds(30), maxWait: TimeSpan.FromMilliseconds(250)));
            Assert.InRange(watch.ElapsedMilliseconds, 200, 10_000);
            Assert.True(NavBuildLock.MaxWaitMinutes >= 10);   // never shorter than the old mutex's 10 minutes
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_folder_that_cannot_hold_the_lock_builds_and_caches_without_it()
    {
        var (world, graph) = Gap();
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            Directory.CreateDirectory(NavBuildLock.FileFor(dir, "gap"));   // the lock's path is taken by a folder: no lock file can be made there
            int builds = 0;
            var set = NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () => { builds++; return NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options); }, out string note);
            Assert.Equal(1, builds);
            Assert.Contains("built without the map's build lock", note);
            Assert.Contains("cached", note);
            var again = NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () => throw new InvalidOperationException("read, not built"), out _);
            Assert.Equal(Bytes(set), Bytes(again));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Two_keys_of_one_map_build_one_after_the_other()
    {
        var (world, graph) = Gap();
        ulong keyA = NavJumpSet.KeyFor(graph, Config, Assault, Options), keyB = keyA ^ 0x5A5A;   // the two sides' keys of a map
        var whole = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int running = 0, most = 0, aBuilds = 0, bBuilds = 0;
            NavJumpSet Builder(ref int count, bool wait)
            {
                Interlocked.Increment(ref count);
                int now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                if (wait) { started.Set(); release.Wait(TimeSpan.FromSeconds(60)); }
                Interlocked.Decrement(ref running);
                return whole;
            }
            var a = Task.Run(() => NavCache.LoadOrBuildJumps(dir, "gap", keyA, graph.NodeCount, () => Builder(ref aBuilds, wait: true), out _));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            string bNote = "";
            var b = Task.Run(() => { var s = NavCache.LoadOrBuildJumps(dir, "gap", keyB, graph.NodeCount, () => Builder(ref bBuilds, wait: false), out string n); bNote = n; return s; });
            Assert.False(b.Wait(TimeSpan.FromSeconds(3)));   // the other key waits for the map's build
            Assert.Equal(0, Volatile.Read(ref bBuilds));
            release.Set();
            Assert.True(a.Wait(TimeSpan.FromSeconds(60)) && b.Wait(TimeSpan.FromSeconds(60)));
            Assert.Equal(1, aBuilds);
            Assert.Equal(1, bBuilds);   // then it builds its own key
            Assert.Equal(1, most);   // never two at once
            Assert.Contains("for another build of the map", bNote);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>Lead decision 2026-11-24: the one-step build (lockPerKey) keeps the old per-key exclusion: the two sides' keys build at once, a second build of the same key still waits.</summary>
    [Fact]
    public void On_the_one_step_path_two_keys_build_at_once_and_one_key_still_waits()
    {
        var (world, graph) = Gap();
        ulong keyA = NavJumpSet.KeyFor(graph, Config, Assault, Options), keyB = keyA ^ 0x5A5A;
        var whole = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int aBuilds = 0, bBuilds = 0, aAgainBuilds = 0;
            var a = Task.Run(() => NavCache.LoadOrBuildJumps(dir, "gap", keyA, graph.NodeCount, () =>
            {
                Interlocked.Increment(ref aBuilds); started.Set(); release.Wait(TimeSpan.FromSeconds(60)); return whole;
            }, out _, lockPerKey: true));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            string aAgainNote = "";
            var aAgain = Task.Run(() => { var s = NavCache.LoadOrBuildJumps(dir, "gap", keyA, graph.NodeCount, () => { Interlocked.Increment(ref aAgainBuilds); return whole; }, out string n, lockPerKey: true); aAgainNote = n; return s; });
            // the other side's key does not wait for the first build
            var b = Task.Run(() => NavCache.LoadOrBuildJumps(dir, "gap", keyB, graph.NodeCount, () => { Interlocked.Increment(ref bBuilds); return whole; }, out _, lockPerKey: true));
            Assert.True(b.Wait(TimeSpan.FromSeconds(30)), "the other key builds beside the first");
            Assert.Equal(1, bBuilds);
            Assert.False(a.IsCompleted);
            Assert.False(aAgain.Wait(TimeSpan.FromSeconds(2)));   // the same key waits for its build
            Assert.Equal(0, Volatile.Read(ref aAgainBuilds));
            release.Set();
            Assert.True(a.Wait(TimeSpan.FromSeconds(60)) && aAgain.Wait(TimeSpan.FromSeconds(60)));
            Assert.Equal(1, aBuilds);
            Assert.Equal(0, aAgainBuilds);   // it read the finished file
            Assert.Contains("after waiting for another build", aAgainNote);
            Assert.False(File.Exists(NavBuildLock.FileFor(dir, NavBuildLock.NameFor("gap", keyA, perKey: true))));
            Assert.False(File.Exists(NavBuildLock.FileFor(dir, "gap")));   // the map's lock was never taken
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    [Fact]
    public void An_unfinished_builds_chunks_untouched_for_a_week_are_pruned()
    {
        string dir = TempDir();
        try
        {
            var (_, graph) = Gap();
            var parts = NavCache.JumpParts(dir, "gap", 11, graph.NodeCount);
            parts.Save(0, 2, 0, 1, []);
            parts.Save(1, 2, 1, 1, []);
            string stale = NavCache.JumpPartFor(dir, "gap", 11, 0), fresh = NavCache.JumpPartFor(dir, "gap", 11, 1);
            File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-8));
            File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-6));
            // build locks a killed process left behind go after a day; a recent one stays
            string oldLock = NavBuildLock.FileFor(dir, NavBuildLock.NameFor("gap", 11, perKey: true)), newLock = NavBuildLock.FileFor(dir, "gap");
            File.WriteAllText(oldLock, $"{DeadPid()} x");
            File.WriteAllText(newLock, $"{DeadPid()} y");
            File.SetLastWriteTimeUtc(oldLock, DateTime.UtcNow.AddDays(-2));
            NavCache.Prune(dir, "gap");
            Assert.False(File.Exists(stale));
            Assert.True(File.Exists(fresh));
            Assert.False(File.Exists(oldLock));
            Assert.True(File.Exists(newLock));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_holder_whose_lock_was_taken_over_leaves_the_new_holders_lock_alone()
    {
        string dir = TempDir();
        try
        {
            string file = NavBuildLock.FileFor(dir, "gap");
            var old = NavBuildLock.TryAcquire(dir, "gap")!;
            Assert.True(old.Held);
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(-NavBuildLock.StaleSeconds - 5));   // the old holder hung
            using var taker = NavBuildLock.TryAcquire(dir, "gap");
            Assert.NotNull(taker);
            Assert.False(old.Held);
            old.Dispose();   // it wakes up and lets go: the new holder's lock stays
            Assert.True(File.Exists(file));
            Assert.True(taker!.Held);
            Assert.Null(NavBuildLock.TryAcquire(dir, "gap"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>bots/a1-perf: the lean bot's one-thread build on a small machine finds byte for byte the set of a build on all cores, and it resumes the same.</summary>
    [Fact]
    public void A_one_thread_build_is_byte_for_byte_an_all_core_build()
    {
        var (world, graph) = Gap();
        var all = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        var one = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options with { Threads = 1 });
        Assert.Equal(Bytes(all), Bytes(one));
        string dir = TempDir();
        try
        {
            ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options with { Threads = 1 });
            Assert.Equal(NavJumpSet.KeyFor(graph, Config, Assault, Options), key);
            using var cts = new CancellationTokenSource();
            var first = new StopAfter(NavCache.JumpParts(dir, "gap", key, graph.NodeCount), 1, cts);
            Assert.ThrowsAny<OperationCanceledException>(() => NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options with { Threads = 1 }, cts.Token, first, Chunk));
            var resumed = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, NavCache.JumpParts(dir, "gap", key, graph.NodeCount), Chunk);
            Assert.Equal(Bytes(all), Bytes(resumed));   // chunks of a one-thread build go on under all cores, and the set is the same
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_process_that_finds_the_map_being_built_waits_and_then_reads_the_finished_set()
    {
        var (world, graph) = Gap();
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        var whole = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            using var started = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            int firstBuilds = 0, secondBuilds = 0;
            var first = Task.Run(() => NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () =>
            {
                firstBuilds++; started.Set(); release.Wait(TimeSpan.FromSeconds(60));
                return NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, NavCache.JumpParts(dir, "gap", key, graph.NodeCount), Chunk);
            }, out _));
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            string secondNote = "";
            var second = Task.Run(() => { var s = NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () => { secondBuilds++; return whole; }, out string n); secondNote = n; return s; });
            Assert.False(second.Wait(TimeSpan.FromSeconds(3)));   // the map is being built: it waits instead of building beside it
            release.Set();
            Assert.True(first.Wait(TimeSpan.FromSeconds(60)) && second.Wait(TimeSpan.FromSeconds(60)));   // bounded: fails instead of hanging
            var a = first.Result; var b = second.Result;
            Assert.Equal(1, firstBuilds);
            Assert.Equal(0, secondBuilds);   // it read the finished file
            Assert.Contains("after waiting for another build", secondNote);
            Assert.Equal(Bytes(whole), Bytes(a));
            Assert.Equal(Bytes(whole), Bytes(b));
            Assert.False(File.Exists(NavBuildLock.FileFor(dir, "gap")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_cache_deletes_a_sets_chunks_once_the_whole_set_is_written()
    {
        var (world, graph) = Gap();
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, Options);
        string dir = TempDir();
        try
        {
            var parts = NavCache.JumpParts(dir, "gap", key, graph.NodeCount);
            var set = NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () =>
            {
                var built = NavJumpSet.Build(graph, world, Team.None, Config, Assault, Options, default, parts, Chunk);
                Assert.NotEmpty(Directory.GetFiles(dir, "gap-*.navjump.part*"));   // the chunks exist while the build runs
                return built;
            }, out string note);
            Assert.Contains("cached", note);
            Assert.True(File.Exists(NavCache.JumpFileFor(dir, "gap", key)));
            Assert.Empty(Directory.GetFiles(dir, "gap-*.navjump.part*"));
            Assert.Equal(set.LinkCount, NavCache.LoadOrBuildJumps(dir, "gap", key, graph.NodeCount, () => throw new InvalidOperationException("read, not built"), out _).LinkCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
