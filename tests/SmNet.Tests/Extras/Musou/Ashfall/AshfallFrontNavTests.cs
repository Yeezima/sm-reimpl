using System.Diagnostics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The Musou front pilot's walking graph (docs/musou-ashfall-graybox.md "Front pilot"): one graph over the composed world, each band's battle places drawn on that band's own part of it (the plan's blocking fact: the largest
/// connected part alone would have been one band). Slow: builds the whole graph (about three bands' worth).
/// </summary>
public class AshfallFrontNavTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Speed", "Slow")]
    public void Each_band_draws_its_battle_places_on_its_own_part_of_the_composed_graph()
    {
        var data = TfwWorld.Load("unused", out var problem, TfwMap.FrontName);
        Assert.Null(problem);
        var bands = data!.Field.Bands!;
        string dir = Path.Combine(Path.GetTempPath(), "ashfall-front-nav-" + Guid.NewGuid().ToString("N"));
        var watch = Stopwatch.StartNew();
        NavGraph nav;
        try { nav = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out string note, dir); output.WriteLine($"nav: {note}"); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        output.WriteLine($"composed world's walking graph: {nav.NodeCount:N0} nodes, built in {watch.Elapsed.TotalSeconds:0.0} s");
        for (int k = 0; k < bands.Count; k++)
        {
            watch.Restart();
            var sites = new TfwSites(bands[k], nav, data.Collision, bands[k].Bounds);
            output.WriteLine($"band {k}: {sites.Candidates:N0} open nodes ({watch.ElapsedMilliseconds} ms)");
            Assert.True(sites.Candidates > 1000);
            foreach (int node in new[] { 1, 2, 3 })
            {
                var layout = sites.Layout(TfwLane.B, node, 7UL + (ulong)node);
                Assert.All(layout.Positions, p => Assert.Equal(k, AshfallFront.BandAt(p.X)));
                Assert.Equal(k, AshfallFront.BandAt(layout.LandingZone.X));
                Assert.Equal(k, AshfallFront.BandAt(layout.Hq.X));
                Assert.All(layout.Sources, s => Assert.Equal(k, AshfallFront.BandAt(s.X)));
            }
        }
        // the whole graph's own sites (no box) see only its largest part: one band, which is why the run keeps one set a band
        var whole = new TfwSites(data.Field, nav, data.Collision);
        output.WriteLine($"whole graph, no box: {whole.Candidates:N0} open nodes");
    }
}
