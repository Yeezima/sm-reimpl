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
