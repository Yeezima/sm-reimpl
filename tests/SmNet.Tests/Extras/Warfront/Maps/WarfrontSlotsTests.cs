using System.Diagnostics;
using System.Numerics;
using SmContent.Extras.Warfront.Maps;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Maps;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Maps;

/// <summary>
/// Warfront slice W2: the nine lane slot maps (plan section 2). The table is pinned; every slot builds from the pilot's generator (<c>AshfallFront.Plan</c> then <c>AshfallDraft5Pass.Run</c>, which throw on a failed gate A or B) with
/// the pilot's band profiles unchanged; the hashes are deterministic; the layout cache round-trips; the stuck log counts.
/// </summary>
public class WarfrontSlotsTests(ITestOutputHelper output)
{
    // ---------------------------------------------------------------- the table (fast)

    [Fact]
    public void The_table_has_nine_slots_with_the_plans_names_lanes_nodes_and_bands()
    {
        var all = WarfrontSlots.All;
        Assert.Equal(9, all.Count);
        string[] names = ["wf_a1", "wf_a2", "wf_a3", "wf_b1", "wf_b2", "wf_b3", "wf_c1", "wf_c2", "wf_c3"];
        Assert.Equal(names, all.Select(s => s.Name));
        for (int i = 0; i < 9; i++)
        {
            var s = all[i];
            Assert.Equal(i + 1, s.Number);
            Assert.Equal((TfwLane)(i / 3), s.Lane);
            Assert.Equal(i % 3 + 1, s.Node);
            Assert.Equal(s.Node - 1, s.Band);   // node 1 wasteland, 2 outskirts, 3 urban core on every lane
            Assert.Equal(new[] { "wasteland", "outskirts", "urban core" }[s.Band], s.Profile);
            Assert.Equal(s.Band, AshfallFront.BandOf(s.Layout));
            Assert.Equal(WarfrontSlots.NameOf(s.Lane, s.Node), s.Name);
            Assert.Same(s, WarfrontSlots.ByName(s.Name));
            Assert.Same(s, WarfrontSlots.Of(s.Lane, s.Node));
            Assert.True(WarfrontSlots.IsLaneMap(s.Name));
        }
        Assert.False(WarfrontSlots.IsLaneMap(TfwMap.Name));
        Assert.False(WarfrontSlots.IsLaneMap(TfwMap.FrontName));
        Assert.False(WarfrontSlots.IsLaneMap("wf_hq"));
        Assert.Equal(1u, WarfrontSlots.Version);
    }

    [Fact]
    public void Seeds_are_distinct_and_never_below_their_candidate_nor_past_the_walk_limit()
    {
        var all = WarfrontSlots.All;
        Assert.Equal(9, all.Select(s => s.Seed).Distinct().Count());
        foreach (var s in all)
        {
            Assert.Equal((uint)(16 + 100 * (int)s.Lane + 10 * (s.Node - 1)), s.Candidate);
            Assert.InRange(s.Seed, s.Candidate, s.Candidate + (uint)WarfrontSlots.WalkLimit - 1u);
        }
        // the three bands' default seed (16) is slot 1's candidate: the pilot's own wasteland build
        Assert.Equal(AshfallFront.DefaultSeed, WarfrontSlots.All[0].Candidate);
    }

    // ---------------------------------------------------------------- the stuck-unit log (fast)

    [Fact]
    public void The_stuck_log_writes_a_line_and_counts_per_slot_and_type()
    {
        var lines = new List<string>();
        var log = new WarfrontStuckLog(lines.Add);
        Assert.Equal("warfront stuck units: none", log.Summary());
        string line = log.Record("wf_b2", "Boss Ork", new Vector3(12.34f, -5.6f, 0.5f), WarfrontStuckAction.Removed);
        log.Record("wf_b2", "Boy", new Vector3(1f, 2f, 3f), WarfrontStuckAction.Relocated, "stuck");
        log.Record("wf_a1", "Boy", new Vector3(0f, 0f, 0f), WarfrontStuckAction.Relocated);
        Assert.Equal("warfront stuck: slot wf_b2, Boss Ork at (12.3, -5.6, 0.5): removed by the stranded failsafe", line);
        Assert.Equal(3, lines.Count);
        Assert.Equal(line, lines[0]);
        Assert.Equal(2, log.CountFor("wf_b2"));
        Assert.Equal(1, log.CountFor("wf_a1"));
        Assert.Equal(0, log.CountFor("wf_c3"));
        var summary = log.Summary().Split('\n');
        Assert.Equal(2, summary.Length);
        Assert.StartsWith("warfront stuck units, slot wf_a1: 1 (1 relocated, 0 removed; Boy x1)", summary[0]);
        Assert.StartsWith("warfront stuck units, slot wf_b2: 2 (1 relocated, 1 removed; Boss Ork x1, Boy x1)", summary[1]);
    }

    // ---------------------------------------------------------------- the generator's gates (slow)

    [Fact]
    [Trait("Speed", "Slow")]   // builds all nine bands (a minute each, cold)
    public void All_nine_slots_build_with_gates_A_and_B_passing_and_distinct_hashes()
    {
        var levels = new List<(WarfrontSlot Slot, ulong Hash, ulong MapHash)>();
        foreach (var slot in WarfrontSlots.All)
        {
            var watch = Stopwatch.StartNew();
            var build = WarfrontSlots.Build(slot);   // throws AshfallGateException on a failed gate A (in Plan) or gate B (in the pass)
            Assert.Equal(slot.Layout, build.Level.Layout);
            Assert.Equal(slot.Seed, build.Plan.Params.LayoutSeed);
            Assert.Equal(slot.Band, build.Plan.Band);
            Assert.NotEmpty(build.Plan.GateA);
            Assert.All(build.Plan.GateA, r => Assert.True(r.Passed, r.ToString()));
            Assert.NotEmpty(build.GateB);
            Assert.All(build.GateB, r => Assert.StartsWith("[pass]", r));
            Assert.Equal(3, build.Level.Points.Count);   // A, B, C of the band
            Assert.Equal(2, build.Level.Areas.Count);
            var data = WarfrontWorld.Compose(build.Level);   // TfwMap.Compose: the hub, the range, the hash
            Assert.Equal(3, data.Field.Points.Count);
            levels.Add((slot, build.Level.Hash, data.Hash));
            WarfrontSlots.Release(slot);   // nine bands in one test host are a lot of memory
            output.WriteLine($"{slot.Name} seed {slot.Seed} {slot.Profile}: {build.Level.Parts.Count} parts, level {build.Level.Hash:X16}, map {data.Hash:X16}, {watch.Elapsed.TotalSeconds:0.0} s");
        }
        Assert.Equal(9, levels.Select(l => l.Hash).Distinct().Count());
        Assert.Equal(9, levels.Select(l => l.MapHash).Distinct().Count());
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Each_pinned_seed_is_the_first_that_passes_from_its_candidate()
    {
        foreach (var slot in WarfrontSlots.All)
        {
            for (uint seed = slot.Candidate; seed < slot.Seed; seed++)
                Assert.Throws<AshfallGateException>(() => WarfrontSlots.BuildFresh(slot.Band, seed));   // a skipped seed failed a gate
            WarfrontSlots.Build(slot);   // and the pinned one passes
        }
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void A_second_build_in_the_same_process_has_the_same_hashes()
    {
        var slot = WarfrontSlots.All[0];
        var first = WarfrontSlots.Build(slot);
        var again = WarfrontSlots.BuildFresh(slot.Band, slot.Seed);
        Assert.Equal(first.Level.Hash, again.Level.Hash);
        Assert.Equal(WarfrontWorld.Compose(first.Level).Hash, WarfrontWorld.Compose(again.Level).Hash);
    }

    [Fact]
    [Trait("Speed", "Slow")]   // two child processes of the tool, one slot each
    public void Two_separate_tool_processes_print_identical_slot_hashes()
    {
        string? dll = FindTool();
        if (dll is null) { output.WriteLine("SmTool.dll is not built; the two-process check is skipped (build src/SmTool first)"); return; }
        foreach (string slot in new[] { "wf_a1", "wf_c2" })
        {
            string one = RunTool(dll, slot), two = RunTool(dll, slot);
            output.WriteLine(one);
            Assert.Equal(one, two);
            var expected = WarfrontSlots.ByName(slot)!;
            var build = WarfrontSlots.Build(expected);
            Assert.Contains($"level={build.Level.Hash:X16}", one);
            Assert.Contains($"map={WarfrontWorld.Compose(build.Level).Hash:X16}", one);
        }
    }

    static string? FindTool()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx"))) dir = dir.Parent;
        if (dir is null) return null;
        string config = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string path = Path.Combine(dir.FullName, "src", "SmTool", "bin", config, "net10.0", "SmTool.dll");
        return File.Exists(path) ? path : null;
    }

    static string RunTool(string dll, string slot)
    {
        var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(dll); info.ArgumentList.Add("warfront-maps"); info.ArgumentList.Add("--hashes"); info.ArgumentList.Add("--slot"); info.ArgumentList.Add(slot);
        using var process = Process.Start(info)!;
        string text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return text.Trim();
    }

    // ---------------------------------------------------------------- the layout cache

    [Fact]
    [Trait("Speed", "Slow")]
    public void The_layout_cache_reads_a_written_file_back_and_regenerates_a_damaged_one()
    {
        var slot = WarfrontSlots.All[0];
        var build = WarfrontSlots.Build(slot);
        string dir = Path.Combine(Path.GetTempPath(), "warfront-layout-" + Guid.NewGuid().ToString("N"));
        try
        {
            int generated = 0;
            Func<WarfrontSlot, (AshfallLevel, AshfallDressing)> gen = s => { generated++; return (build.Level, build.Dressing); };
            var first = WarfrontLayoutCache.LoadOrGenerate(slot, dir, "tests", gen, out string note1);
            Assert.Equal(1, generated);
            Assert.Contains("written", note1);
            string path = WarfrontLayoutCache.PathIn(dir, slot, "tests");
            Assert.True(File.Exists(path));
            var second = WarfrontLayoutCache.LoadOrGenerate(slot, dir, "tests", gen, out string note2);
            Assert.Equal(1, generated);   // read from the file
            Assert.Contains("read from the cache", note2);
            Assert.Equal(build.Level.Hash, second.Level.Hash);
            Assert.Equal(slot.Layout, second.Level.Layout);
            Assert.Equal(build.Level.Parts.Count, second.Level.Parts.Count);
            Assert.Equal(WarfrontWorld.Compose(build.Level).Hash, WarfrontWorld.Compose(second.Level).Hash);   // a cached level plays as the generated one
            var bytes = File.ReadAllBytes(path);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            var third = WarfrontLayoutCache.LoadOrGenerate(slot, dir, "tests", gen, out string note3);
            Assert.Equal(2, generated);   // the damaged file is refused and generated again
            Assert.Contains("unreadable", note3);
            Assert.Equal(build.Level.Hash, third.Level.Hash);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    // ---------------------------------------------------------------- objective D (slow: the walking graph of each slot)

    [Fact]
    [Trait("Speed", "Slow")]
    public void D_is_on_the_graph_reachable_from_the_landing_zone_with_two_army_width_approaches_on_every_slot()
    {
        Assert.Equal(TfwMap.HqRadius, WarfrontSiteCheck.DiscRadius);
        string? dir = null;   // the private nav cache: one graph file per slot (about 100 s cold each, then read back), the same files the server and the tool use
        {
            foreach (var slot in WarfrontSlots.All)
            {
                var watch = Stopwatch.StartNew();
                var build = WarfrontSlots.Build(slot);
                var data = WarfrontWorld.Compose(build.Level);
                var nav = WarfrontWorld.Nav(slot, data, new SimConfig(), out string note, dir);
                var layout = WarfrontWorld.Layout(slot, data, nav, 7UL);
                var report = WarfrontSiteCheck.Measure(build.Level, build.Plan, nav, layout.Hq, layout.LandingZone, data.Field.MarineStart);
                output.WriteLine($"{slot.Name}: D ({report.Site.X:0.0}, {report.Site.Y:0.0}) graph {nav.NodeCount:N0} nodes, {report.Approaches} approaches at {string.Join(", ", report.ApproachDegrees)}, path {report.PathLength:0} m ({watch.Elapsed.TotalSeconds:0} s) {note}");
                Assert.True(report.OnGraph, $"{slot.Name}: D is off the graph");
                Assert.True(report.ReachableFromLanding, $"{slot.Name}: D is not reachable from the landing zone");
                Assert.True(report.ReachableFromBase, $"{slot.Name}: D is not reachable from the Imperial base");
                Assert.True(report.Approaches >= WarfrontSiteCheck.RequiredApproaches, $"{slot.Name}: only {report.Approaches} army-width approaches to D");
                Assert.True(report.Passed);
                Assert.Equal(AshfallFront.StrongholdCentre.X, layout.Hq.X, 8f);   // D is the stronghold's centre on the through road
                Assert.Equal(AshfallFront.StrongholdCentre.Y, layout.Hq.Y, 8f);
                WarfrontSlots.Release(slot);
            }
        }
    }
}
