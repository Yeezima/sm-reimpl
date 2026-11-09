using System.Numerics;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou;
using Xunit;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>The client's own volleys (musou-net-budget): who joins, the bursts' rhythm, the caps, misses only.</summary>
public class MusouVolleyTests
{
    // Forward is (-sin yaw, cos yaw): yaw 0 faces +Y.
    static MusouVolleyOrk Ork(ushort id, float x, float y, MusouEnemyType type = MusouEnemyType.ShootaBoy, MusouEnemyState state = MusouEnemyState.Seek, float yaw = 0f)
        => new(id, type, state, new Vector3(x, y, 0f), yaw);

    static int Run(MusouVolleyPlanner p, IReadOnlyList<MusouVolleyOrk> orks, IReadOnlyList<Vector3> targets, float seconds, Vector3 camera = default,
        Func<Vector3, Vector3, bool>? clear = null, List<MusouVolleyRound>? into = null)
    {
        int n = 0;
        for (float t = 0f; t < seconds; t += 1f / 60f)
        {
            var r = p.Step(1f / 60f, orks, targets, camera, clear);
            n += r.Count;
            into?.AddRange(r);
        }
        return n;
    }

    [Fact]
    public void A_shoota_in_range_and_facing_fires_three_round_bursts_at_about_the_shootas_rate()
    {
        var p = new MusouVolleyPlanner();
        int rounds = Run(p, [Ork(1, 0f, 0f)], [new Vector3(0f, 15f, 0f)], 20f);
        var spec = MusouRules.Default.Of(MusouEnemyType.ShootaBoy);
        float least = 20f / (spec.RangedPeriod * MusouVolleyRules.PeriodSpread) * spec.RangedRounds, most = 20f / spec.RangedPeriod * spec.RangedRounds + spec.RangedRounds;
        Assert.InRange(rounds, (int)least - spec.RangedRounds, (int)most);
        Assert.InRange(p.Rounds, (p.Bursts - 1) * spec.RangedRounds + 1, p.Bursts * spec.RangedRounds);   // the last burst may still be firing
    }

    [Theory]
    [InlineData(0f, 5f)]     // too near
    [InlineData(0f, 40f)]    // too far
    [InlineData(0f, -15f)]   // behind him
    [InlineData(15f, 0f)]    // beside him (90 degrees)
    public void No_target_in_the_band_and_cone_means_no_fire(float tx, float ty)
    {
        var p = new MusouVolleyPlanner();
        Assert.Equal(0, Run(p, [Ork(1, 0f, 0f)], [new Vector3(tx, ty, 0f)], 5f));
    }

    [Theory]
    [InlineData(MusouEnemyType.Boy, MusouEnemyState.Seek)]
    [InlineData(MusouEnemyType.BigShootaNob, MusouEnemyState.Seek)]
    [InlineData(MusouEnemyType.ShootaBoy, MusouEnemyState.WindUp)]
    [InlineData(MusouEnemyType.ShootaBoy, MusouEnemyState.Stagger)]
    [InlineData(MusouEnemyType.ShootaBoy, MusouEnemyState.Dying)]
    public void Only_standing_gun_orks_join(MusouEnemyType type, MusouEnemyState state)
    {
        var p = new MusouVolleyPlanner();
        Assert.Equal(0, Run(p, [Ork(1, 0f, 0f, type, state)], [new Vector3(0f, 15f, 0f)], 5f));
    }

    [Fact]
    public void A_boss_shoota_boy_joins()
    {
        var p = new MusouVolleyPlanner();
        Assert.True(Run(p, [Ork(1, 0f, 0f, MusouEnemyType.BossShootaBoy)], [new Vector3(0f, 15f, 0f)], 5f) > 0);
    }

    [Fact]
    public void An_ork_firing_for_the_server_is_left_alone()
    {
        var p = new MusouVolleyPlanner();
        var orks = new[] { Ork(1, 0f, 0f) };
        var target = new[] { new Vector3(0f, 15f, 0f) };
        int rounds = 0;
        for (float t = 0f; t < 6f; t += 1f / 60f) { p.RealRound(1); rounds += p.Step(1f / 60f, orks, target, default).Count; }
        Assert.Equal(0, rounds);
    }

    [Fact]
    public void Every_round_is_a_miss_beside_the_target()
    {
        var p = new MusouVolleyPlanner();
        var got = new List<MusouVolleyRound>();
        var target = new Vector3(0f, 15f, 0f);
        Run(p, [Ork(1, 0f, 0f)], [target], 10f, into: got);
        Assert.NotEmpty(got);
        var chest = target + new Vector3(0f, 0f, 1.2f);
        foreach (var r in got)
        {
            float side = MathF.Abs(r.End.X - chest.X);   // the line runs along Y: the miss is off to the side
            Assert.InRange(side, MusouVolleyRules.MissNear - 0.01f, MusouVolleyRules.MissFar + 0.01f);
            Assert.InRange(r.End.Z - chest.Z, -0.41f, 0.41f);
        }
    }

    [Fact]
    public void At_most_the_cap_of_shooters_the_nearest_the_camera_first_and_none_past_its_reach()
    {
        var orks = new List<MusouVolleyOrk>();
        var targets = new List<Vector3>();
        for (int k = 0; k < 120; k++) { orks.Add(Ork((ushort)(k + 1), k * 2f, 0f)); targets.Add(new Vector3(k * 2f, 15f, 0f)); }
        var p = new MusouVolleyPlanner();
        var got = new List<MusouVolleyRound>();
        Run(p, orks, targets, 6f, camera: Vector3.Zero, into: got);
        Assert.Equal(MusouVolleyRules.MaxShooters, p.Shooters);
        var firing = got.Select(r => r.Enemy).ToHashSet();
        Assert.True(firing.Count > MusouVolleyRules.MaxShooters / 2);
        Assert.All(firing, id => Assert.True(id <= MusouVolleyRules.MaxShooters, $"Ork {id} is not among the nearest"));
    }

    [Fact]
    public void A_long_frame_gives_at_most_the_frame_cap()
    {
        var orks = new List<MusouVolleyOrk>();
        var targets = new List<Vector3>();
        for (int k = 0; k < 40; k++) { orks.Add(Ork((ushort)(k + 1), k * 2f, 0f)); targets.Add(new Vector3(k * 2f, 15f, 0f)); }
        var p = new MusouVolleyPlanner();
        p.Step(0.01f, orks, targets, default);
        Assert.True(p.Step(5f, orks, targets, default).Count <= MusouVolleyRules.MaxRoundsPerFrame);
    }

    [Fact]
    public void A_blocked_line_of_sight_starts_no_burst()
    {
        var p = new MusouVolleyPlanner();
        Assert.Equal(0, Run(p, [Ork(1, 0f, 0f)], [new Vector3(0f, 15f, 0f)], 5f, clear: (_, _) => false));
        Assert.True(p.Blocked > 0);
    }

    [Fact]
    public void The_same_inputs_give_the_same_rounds()
    {
        List<MusouVolleyRound> Once()
        {
            var got = new List<MusouVolleyRound>();
            Run(new MusouVolleyPlanner(), [Ork(1, 0f, 0f), Ork(2, 3f, 0f), Ork(3, -3f, 1f)], [new Vector3(0f, 15f, 0f)], 8f, into: got);
            return got;
        }
        Assert.Equal(Once(), Once());
    }
}
