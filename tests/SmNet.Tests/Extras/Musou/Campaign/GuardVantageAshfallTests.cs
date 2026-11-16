using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// musou/guard-vantage on the generated Ashfall Citadel (no install needed): the finder works on whatever the level is (its buildings are redesigned often), so this checks only that it finds vantage
/// points round the base's front and the points, and reports the counts and the finder's time.
/// </summary>
public class GuardVantageAshfallTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Speed", "Slow")]   // the whole level's walking graph: about 5 min the first time (Debug), then from a cache in the temp folder
    public void Ashfall_has_vantage_points_by_its_points_and_in_front_of_its_base()
    {
        var data = TfwWorld.Load("", out var problem)!;
        Assert.Null(problem);
        var field = data.Field;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var graph = TfwWorld.Nav(data.Collision, field, new SimConfig(), out _, Path.Combine(Path.GetTempPath(), "sm-reimpl-tests-nav"));
        var walk = new MusouNav(graph);
        double built = watch.Elapsed.TotalSeconds;
        var rules = GuardVantageRules.Default;
        watch.Restart();
        var found = GuardVantage.Find(walk, data.Collision.Weapons, walk.Nearest(field.MarineStart, 4f, 4f), rules);
        double find = watch.Elapsed.TotalMilliseconds;
        static float Flat(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Y - b.Y).Length();
        var front = new Vector2(field.Commander.X - field.MarineStart.X, field.Commander.Y - field.MarineStart.Y);
        int Front(float radius) => found.Count(v => Flat(v.Position, field.MarineStart) <= radius && Vector2.Dot(new Vector2(v.Position.X - field.MarineStart.X, v.Position.Y - field.MarineStart.Y), front) >= 0f);
        output.WriteLine($"Ashfall: {walk.NodeCount} nodes (graph {built:0.0} s); finder {find:0} ms, {GuardVantage.LastCandidates} candidates, {GuardVantage.LastRays} rays; {found.Length} vantage points, height {found.Min(v => v.Height):0.0} to {found.Max(v => v.Height):0.0} m, open {found.Min(v => v.Open):0.00} to {found.Max(v => v.Open):0.00}");
        output.WriteLine($"in front of the base within {rules.BaseRadius} m: {Front(rules.BaseRadius)}, within {rules.BaseRadiusFar} m: {Front(rules.BaseRadiusFar)}");
        for (int k = 0; k < field.Points.Count; k++)
            output.WriteLine($"within {rules.PointRadius} m of point {k} at {field.Points[k].Centre}: {found.Count(v => Flat(v.Position, field.Points[k].Centre) <= rules.PointRadius)}");
        foreach (var v in found.Take(12)) output.WriteLine($"  ({v.Position.X:0.0}, {v.Position.Y:0.0}, {v.Position.Z:0.0}) height {v.Height:0.0} open {v.Open:0.00} score {v.Score:0.00} posts {v.Posts.Length}");
        Assert.True(found.Length >= 5, $"{found.Length}");
        Assert.True(Front(rules.BaseRadiusFar) >= 1, "nothing in front of the base for the teams of a battle with no point taken");
        Assert.True(Enumerable.Range(0, field.Points.Count).Count(k => found.Any(v => Flat(v.Position, field.Points[k].Centre) <= rules.PointRadius)) >= 2);
        Assert.All(found, v => Assert.True(v.Height >= rules.MinHeight && v.Open >= rules.MinOpen && v.Posts.Length >= rules.MinPosts));
    }
}
