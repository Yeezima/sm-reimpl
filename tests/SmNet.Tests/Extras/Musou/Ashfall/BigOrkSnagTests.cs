using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Ashfall;

/// <summary>
/// The owner, 2026-11-09 (a recording on Ashfall): "boss enemies are getting stuck along objects and moving in slow motion" (<see cref="MusouBigBodyRules"/>). On the Ashfall draft 10 walk graph (the
/// generator alone, no asset bytes): the big Orks cross the map between far places a Boy can walk between, without standing still more than <see cref="MostStandSeconds"/> and at their own speed; and on
/// a floor with a raised block a Boss Ork strikes a player standing on its edge 2.4 m up (before: he stood below it).
/// </summary>
public class BigOrkSnagTests(ITestOutputHelper output)
{
    /// <summary>The longest a big Ork may stand still on his way (s).</summary>
    const float MostStandSeconds = 3f;
    const int Runs = 4, Seconds = 100;

    sealed record Map(NavGraph Graph, CollisionWorld Collision);

    static readonly Lazy<Map> Ashfall = new(() =>
    {
        var level = AshfallMap.BuildGrayboxWithFeatures().Level;
        var data = TfwMap.Compose(AshfallContent.ToMapContent(level).ToCollisionWorld(), level.Points, level.Areas, level.Spawns, manufactorum: false);
        var graph = TfwWorld.Nav(data.Collision, data.Field, new SimConfig(), out _, Path.Combine(Path.GetTempPath(), "sm-bigork-snag-nav"));
        return new Map(graph, data.Collision);
    });

    sealed record Walk(bool Arrived, float LongestStand, float MeanSpeed);

    static Walk Go(Map map, MusouEnemyType type, Vector3 from, Vector3 to, bool old = false)
    {
        var sim = new MusouSim(map.Graph) { Swarm = MusouSwarmRules.Default, OldBigBodies = old };
        ushort id = sim.Spawn(type, from, 0f, 1e6f, map.Graph.Nearest(from));
        var targets = new[] { new MusouTarget(1, to, true) };
        var attacks = new List<MusouAttack>();
        float still = 0f, longest = 0f; double moving = 0, sum = 0; uint tick = 0;
        for (int t = 0; t < 30 * Seconds; t++)
        {
            sim.BeginTick(++tick);
            sim.Step(targets, map.Collision, false, attacks);
            Assert.True(sim.TryGet(id, out var e));
            if (t < 30) continue;
            if (Vector2.Distance(new(e.Position.X, e.Position.Y), new(to.X, to.Y)) < 8f) return new Walk(true, longest, (float)(moving > 0 ? sum / moving : 0));
            float v = e.Velocity.Length();
            if (v < 0.5f) { still += 1f / 30f; longest = MathF.Max(longest, still); }
            else { still = 0f; if (Vector2.Distance(new(e.Position.X, e.Position.Y), new(to.X, to.Y)) > MusouChargeRules.ChargeWithin + 2f) { moving += 1; sum += v; } }   // the speed beyond the charge's 30 m
        }
        return new Walk(false, longest, (float)(moving > 0 ? sum / moving : 0));
    }

    /// <summary>Far pairs (at least 60 m apart) a Boy walks between in time.</summary>
    static List<(Vector3 From, Vector3 To)> Pairs(Map map)
    {
        var rng = new Random(11);
        var pairs = new List<(Vector3, Vector3)>();
        for (int k = 0; k < 40 && pairs.Count < Runs; k++)
        {
            var from = map.Graph.Position(rng.Next(map.Graph.NodeCount));
            var to = map.Graph.Position(rng.Next(map.Graph.NodeCount));
            if (Vector2.Distance(new(from.X, from.Y), new(to.X, to.Y)) < 60f) continue;
            if (Go(map, MusouEnemyType.Boy, from, to).Arrived) pairs.Add((from, to));
        }
        Assert.Equal(Runs, pairs.Count);
        return pairs;
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public void Big_orks_cross_ashfall_without_standing_stuck_and_at_their_own_speed()
    {
        var map = Ashfall.Value;
        var pairs = Pairs(map);
        var rules = new MusouRules();
        {
            foreach (var type in new[] { MusouEnemyType.Nob, MusouEnemyType.BossBoy, MusouEnemyType.BossNob, MusouEnemyType.Warboss })
            {
                float speed = rules.Of(type).Speed * MusouChargeRules.PaceScale;   // the Orks' pace (2026-11-09)
                foreach (var (from, to) in pairs)
                {
                    var now = Go(map, type, from, to);
                    output.WriteLine($"{type} {from.X:F0},{from.Y:F0} -> {to.X:F0},{to.Y:F0}: arrived {now.Arrived}, longest stand {now.LongestStand:F1} s, {now.MeanSpeed:F2} m/s of {speed:F1}");
                    Assert.True(now.Arrived, $"{type} did not arrive from {from} to {to}");
                    Assert.True(now.LongestStand <= MostStandSeconds, $"{type} stood {now.LongestStand:F1} s");
                    Assert.InRange(now.MeanSpeed, 0.93f * speed, 1.05f * speed);
                }
            }
            // Before: a Boss Ork scraped along the walls at under 90 % of his speed.
            float old = pairs.Average(p => Go(map, MusouEnemyType.BossNob, p.From, p.To, old: true).MeanSpeed);
            float paced = rules.Of(MusouEnemyType.BossNob).Speed * MusouChargeRules.PaceScale;
            output.WriteLine($"old BossNob: {old:F2} m/s of {paced:F2}");
            Assert.True(old < 0.92f * paced);
        }
    }

    [Theory]
    [InlineData(MusouEnemyType.BossNob, false, true)]
    [InlineData(MusouEnemyType.BossNob, true, false)]
    [InlineData(MusouEnemyType.Boy, false, false)]
    public void A_boss_ork_strikes_a_player_on_a_ledge_above_him(MusouEnemyType type, bool old, bool strikes)
    {
        // a floor, a block 2.4 m high over x 30..60 with a ramp up 12 m off: the player stands on its edge, the Ork at its foot below.
        var level = new NavigationTests.Level().Floor(0f, 0f, 30f, 32f).Box(new Vector3(30f, 0f, 0f), new Vector3(60f, 40f, 2.4f))
            .Quad(new Vector3(10f, 32f, 0f), new Vector3(30f, 32f, 2.4f), new Vector3(30f, 40f, 2.4f), new Vector3(10f, 40f, 0f));   // a ramp up at the far end: the block's top is on the graph, as a stair's landing is
        var sim = new MusouSim(NavGraph.Build(level.Mesh(), null, NavSettings.From(new SimConfig()))) { Swarm = MusouSwarmRules.Default, OldBigBodies = old };
        {
            var start = new Vector3(28.8f, 20f, 0f);   // at the foot of the ledge, as in the recording
            ushort id = sim.Spawn(type, start, 0f, 1e6f, sim.Walk!.Nearest(start));
            var targets = new[] { new MusouTarget(1, new Vector3(30.8f, 20f, 2.4f), true) };
            var attacks = new List<MusouAttack>();
            int blows = 0; uint tick = 0;
            for (int t = 0; t < 30 * 6; t++)   // 6 s: too short to go round by the ramp (about 40 m)
            {
                sim.BeginTick(++tick);
                attacks.Clear();
                sim.Step(targets, null, true, attacks);
                blows += attacks.Count(a => a.Enemy == id);
            }
            Assert.True(sim.TryGet(id, out var end));
            output.WriteLine($"{type} old {old}: at {end.Position.X:F1},{end.Position.Y:F1},{end.Position.Z:F1} state {end.State}, {blows} blows");
            Assert.Equal(strikes, blows > 0);
        }
    }

    [Fact]
    public void The_reach_and_eye_grow_with_the_body_up_to_three_times()
    {
        Assert.Equal(1.5f, MusouBigBodyRules.VerticalReach(1.9f));
        Assert.Equal(1.5f, MusouBigBodyRules.VerticalReach(1.2f));
        Assert.Equal(4.5f, MusouBigBodyRules.VerticalReach(5.7f), 3);
        Assert.Equal(4.5f, MusouBigBodyRules.VerticalReach(13f));
        Assert.Equal(1.5f, MusouBigBodyRules.Eye(1.9f));
        Assert.Equal(4.5f, MusouBigBodyRules.Eye(5.7f), 3);
    }
}
