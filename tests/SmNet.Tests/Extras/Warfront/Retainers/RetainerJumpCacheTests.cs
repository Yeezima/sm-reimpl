using SmBots;
using SmSim;

namespace SmNet.Tests.Extras.Warfront.Retainers;

/// <summary>
/// Review 2026-11-25 (1) of the retainers' jump links: the tick path reads the nav cache only (<see cref="NavCache.TryLoadJumps"/>: a hit, a miss and a damaged file; no build, no build lock, no write),
/// and a failed background job's key can be started again once forgotten (<see cref="BotWork.Forget(System.Threading.Tasks.Task)"/>). Synthetic geometry only (the gap level of the jump tests).
/// </summary>
public class RetainerJumpCacheTests
{
    static readonly SimConfig Config = JumpPackTests.Config with { JumpPacks = [JumpPackTests.Pack with { ReleaseCut = true }] };
    const int Assault = 0;

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
        string dir = Path.Combine(Path.GetTempPath(), "sm-wf-retainer-jumps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void The_tick_read_finds_a_cached_set_returns_null_on_a_miss_and_on_a_damaged_file_and_never_builds_locks_or_writes()
    {
        var (world, graph) = Gap();
        ulong key = NavJumpSet.KeyFor(graph, Config, Assault, new NavJumpOptions());
        string dir = TempDir();
        try
        {
            // a miss: nothing there, nothing made
            Assert.Null(NavCache.TryLoadJumps(dir, "gap-wf-retainer", key, graph.NodeCount));
            Assert.Empty(Directory.GetFileSystemEntries(dir));
            // a hit: the set a build cached is read back whole
            var built = NavCache.LoadOrBuildJumps(dir, "gap-wf-retainer", key, graph.NodeCount, () => NavJumpSet.Build(graph, world, Team.None, Config, Assault, new NavJumpOptions()), out _);
            var read = NavCache.TryLoadJumps(dir, "gap-wf-retainer", key, graph.NodeCount);
            Assert.NotNull(read);
            Assert.Equal(Bytes(built), Bytes(read!));
            // a damaged file: null at once, left as it is (the background build path deals with it); no file in the folder made, moved or deleted (no build lock taken)
            string file = NavCache.JumpFileFor(dir, "gap-wf-retainer", key);
            File.WriteAllBytes(file, [1, 2, 3, 4, 5, 6, 7, 8]);
            var before = Directory.GetFileSystemEntries(dir).Order().ToArray();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Null(NavCache.TryLoadJumps(dir, "gap-wf-retainer", key, graph.NodeCount));
            Assert.True(watch.ElapsedMilliseconds < 1000, $"read in {watch.ElapsedMilliseconds} ms");
            Assert.Equal(before, Directory.GetFileSystemEntries(dir).Order().ToArray());
            Assert.Equal(8, new FileInfo(file).Length);
            // another key's graph size: refused, not misread
            Assert.Null(NavCache.TryLoadJumps(dir, "gap-wf-retainer", key ^ 1UL, graph.NodeCount));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_failed_job_hands_back_its_faulted_task_until_forgotten_and_then_runs_again()
    {
        var work = new BotWork();
        int runs = 0;
        var first = work.Run<int>("wf jump test", () => { runs++; throw new InvalidOperationException("build failed"); }, CancellationToken.None);
        Assert.True(BotBrainSeamTests.PollUntil(work, () => first.IsCompleted));   // up to 60 s: a loaded thread pool can take seconds to start the job (series-37 integration)
        Assert.True(first.IsFaulted);
        Assert.Same(first, work.Run<int>("wf jump test", () => 1, CancellationToken.None));   // the same faulted task while the key is known
        work.Forget(first);
        var second = work.Run<int>("wf jump test", () => { runs++; return 7; }, CancellationToken.None);
        Assert.NotSame(first, second);
        Assert.True(BotBrainSeamTests.PollUntil(work, () => second.IsCompleted));   // bounded: a bare .Result on an unfinished job blocks for ever (it completes only at a poll)
        Assert.Equal(7, second.Result);
        Assert.Equal(2, runs);
    }
}
