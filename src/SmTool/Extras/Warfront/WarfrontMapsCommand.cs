using System.Diagnostics;
using System.Globalization;
using SmContent.Extras.Warfront.Maps;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Warfront.Maps;

namespace SmTool;

/// <summary>
/// <c>smtool warfront-maps</c> (Warfront slice W2): the nine lane slots' builds, hashes, gates, D site checks and overhead PNGs. Every build is generated here (the layout cache is not consulted, so two processes prove the generator's
/// determinism); nothing is read from the install and nothing is written except the PNGs and the private nav cache.
/// <c>--hashes</c>: one line per slot (name, lane, node, band, seed, level hash, map hash), nothing else on stdout; <c>--walk</c>: reruns the seed walk and prints every seed it rejects and the seed it pins; <c>--sites</c>: builds each slot's
/// walking graph and measures D (<c>TfwLayout.Hq</c>); <c>--overheads &lt;dir&gt;</c>: one annotated overhead PNG per slot (the pilot's front view: through road, routes, points); <c>--slot &lt;name&gt;</c>: only that slot.
/// </summary>
static class WarfrontMapsCommand
{
    public static int Run(string[] rest)
    {
        bool hashes = false, walk = false, sites = false, prebuild = false; string? overheads = null; string? only = null;
        for (int i = 0; i < rest.Length; i++)
        {
            switch (rest[i])
            {
                case "--hashes": hashes = true; break;
                case "--walk": walk = true; break;
                case "--sites": sites = true; break;
                case "--prebuild": prebuild = true; break;
                case "--overheads" when i + 1 < rest.Length: overheads = Path.GetFullPath(rest[++i]); break;
                case "--slot" when i + 1 < rest.Length: only = rest[++i]; break;
                default:
                    Console.Error.WriteLine($"warfront-maps: unknown argument \"{rest[i]}\" (usage: warfront-maps [--hashes] [--walk] [--sites] [--prebuild] [--overheads dir] [--slot wf_b2])");
                    return 2;
            }
        }
        var slots = WarfrontSlots.All.Where(s => only is null || s.Name == only).ToList();
        if (slots.Count == 0) { Console.Error.WriteLine($"warfront-maps: no slot named \"{only}\""); return 2; }
        if (walk) return Walk(slots);
        if (prebuild) return Prebuild(slots);
        int failed = 0;
        foreach (var slot in slots)
        {
            var watch = Stopwatch.StartNew();
            WarfrontBuild build;
            try { build = WarfrontSlots.Build(slot); }
            catch (AshfallGateException e) { Console.WriteLine($"{slot.Name}: GATE FAILED {e.Message}"); failed++; continue; }
            long buildMs = watch.ElapsedMilliseconds;
            var data = WarfrontWorld.Compose(build.Level);
            string line = string.Create(CultureInfo.InvariantCulture, $"{slot.Name} lane={slot.Lane} node={slot.Node} band={slot.Band} seed={slot.Seed} level={build.Level.Hash:X16} map={data.Hash:X16}");
            if (hashes) { Console.WriteLine(line); WarfrontSlots.Release(slot); continue; }
            int aFail = build.Plan.GateA.Count(r => !r.Passed), bFail = build.GateB.Count(r => r.StartsWith("[FAIL]", StringComparison.Ordinal));
            Console.WriteLine($"{line}\n  {slot.Profile}: {build.Level.Parts.Count} parts, {build.Dressing.Items.Count} items, built in {buildMs / 1000.0:0.0} s; gate A {build.Plan.GateA.Count - aFail}/{build.Plan.GateA.Count} pass, gate B {build.GateB.Count - bFail}/{build.GateB.Count} pass");
            if (aFail + bFail > 0) failed++;
            AshfallBattlefield? bf = null;
            if (overheads is not null)
            {
                Directory.CreateDirectory(overheads);
                bf = new AshfallBattlefield(slot.Layout, build.Level, build.Dressing, build.Plan);
                string png = Path.Combine(overheads, $"{slot.Name}-{slot.Profile.ToLowerInvariant().Replace(' ', '-')}-seed{slot.Seed}.png");
                AshfallFrontViews.Png(bf, build.Plan, png);
                Console.WriteLine($"  overhead -> {png}");
            }
            if (sites && !Sites(slot, build, data)) failed++;
            WarfrontSlots.Release(slot);   // a run over all nine does not keep nine levels
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>--prebuild</c>: the layout cache file and the walking graph of every slot, one at a time with progress and timings (the private cache only; a warm run reads them back). A slot's world is released before the next.
    /// A server's travel wait and a client's first join then find both warm.
    /// </summary>
    static int Prebuild(List<WarfrontSlot> slots)
    {
        var total = Stopwatch.StartNew();
        int done = 0;
        foreach (var slot in slots)
        {
            var watch = Stopwatch.StartNew();
            var (level, _) = WarfrontLayoutCache.Build(slot, out string layoutNote);
            long layoutMs = watch.ElapsedMilliseconds;
            var data = WarfrontWorld.Compose(level);
            // W5d: the client reads this file too (any process kind's file for the same key; WarfrontSharedCache), so one prebuild warms both; read it back as the client would and compare the level hash
            var shared = SmContent.Extras.Warfront.Maps.WarfrontSharedCache.TryRead(WarfrontLayoutCache.Folder, slot, SmContent.Extras.Musou.AshfallLayoutCache.ProcessKind, out string sharedNote);
            string clientSees = shared is { } s ? (s.Level.Hash == level.Hash ? "client-readable, same hash" : "CLIENT READ DIFFERS") : "client-readable: no (" + sharedNote + ")";
            var nav = WarfrontWorld.Nav(slot, data, new SimConfig(), out string navNote);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{++done}/{slots.Count}] {slot.Name}: layout {layoutMs / 1000.0:0.0} s ({layoutNote}; {clientSees}); graph {nav.NodeCount:N0} nodes, {(watch.ElapsedMilliseconds - layoutMs) / 1000.0:0.0} s ({(navNote.Length > 90 ? navNote[..90] : navNote)}); slot total {watch.Elapsed.TotalSeconds:0.0} s; elapsed {total.Elapsed.TotalSeconds:0} s"));
            WarfrontSlots.Release(slot); WarfrontLayoutCache.Forget(slot);
        }
        Console.WriteLine($"warfront-maps --prebuild: cache key {SmContent.Extras.Musou.AshfallLayoutCache.Key} (a client reads these files only when its own key is this one: the same build configuration, Debug or Release; the client logs its key in its \"warm\" lines)");
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"warfront-maps --prebuild: {done} slots, {total.Elapsed.TotalSeconds:0.0} s in all (HQ and fortress maps do not exist yet; they join the list with W13 and W14)"));
        return 0;
    }

    static bool Sites(WarfrontSlot slot, WarfrontBuild build, SmSim.Extras.Musou.Campaign.TfwMapData data)
    {
        var watch = Stopwatch.StartNew();
        var nav = WarfrontWorld.Nav(slot, data, new SimConfig(), out string note);
        var layout = WarfrontWorld.Layout(slot, data, nav, 7UL);
        var report = WarfrontSiteCheck.Measure(build.Level, build.Plan, nav, layout.Hq, layout.LandingZone, data.Field.MarineStart);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  graph: {nav.NodeCount:N0} nodes ({note}); D at ({report.Site.X:0.0}, {report.Site.Y:0.0}): on graph {report.OnGraph} ({report.GraphDistance:0.0} m), from landing {report.ReachableFromLanding} ({report.PathLength:0} m), from base {report.ReachableFromBase}; {report.Approaches} army-width approaches at {string.Join(", ", report.ApproachDegrees.Select(d => d.ToString("0", CultureInfo.InvariantCulture)))} degrees ({report.CapableDirections} capable directions of {report.ArmyWidth:0} m width, ring {report.RingRadius:0} m); {(report.Passed ? "pass" : "FAIL")} ({watch.Elapsed.TotalSeconds:0.0} s)"));
        return report.Passed;
    }

    static int Walk(List<SmSim.Extras.Warfront.Maps.WarfrontSlot> slots)
    {
        int bad = 0;
        foreach (var slot in slots)
        {
            uint seed = WarfrontSlots.Walk(slot.Lane, slot.Node, out var failures);
            foreach (var f in failures) Console.WriteLine($"{slot.Name}: rejected {f}");
            bool same = seed == slot.Seed;
            Console.WriteLine($"{slot.Name}: candidate {slot.Candidate}, walk pins {seed}{(same ? "" : $" (the table says {slot.Seed}: UPDATE)")}");
            if (!same) bad++;
        }
        return bad == 0 ? 0 : 1;
    }
}
