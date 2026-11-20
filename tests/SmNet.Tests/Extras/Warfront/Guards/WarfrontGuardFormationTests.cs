using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Guards;

/// <summary>Warfront's Guardsmen (owner 2026-11-20): twice the numbers and the defence in depth: rows of squads on a held point facing the Orks' approach, the rear rows in the gaps of the front, a battered front squad falling back.</summary>
public class WarfrontGuardFormationTests
{
    static int Field(World w) => w.TfwGuards!.SquadsAlive;

    static (World W, TfwBattle B) Secured(SimConfig? config = null)
    {
        var w = TfwRig.War(config: config ?? WarfrontBattleTests.WarfrontConfig);
        var b = TfwRig.Deploy(w);
        TfwRig.Secure(w, b, 0);
        TfwRig.KillAll(w);
        TfwRig.Seconds(w, 12f);
        return (w, b);
    }

    [Fact]
    public void A_warfront_battle_fields_twice_the_squads_and_musou_keeps_its_own()
    {
        var w = TfwRig.War(config: WarfrontBattleTests.WarfrontConfig);
        var battle = TfwRig.Deploy(w);
        Assert.Equal(TfwBattleRules.FieldSquads * 2, Field(w));
        Assert.Equal(TfwBattleRules.SquadsAt(100.0) * 2 >= TfwBattleRules.FieldSquads * 2, battle.Squads >= TfwBattleRules.FieldSquads * 2);
        Assert.Equal(2, battle.GuardScale);
        Assert.Equal(TfwArmyRules.GarrisonCap * 2, battle.GarrisonCapNow);

        var musou = TfwRig.War();
        var old = TfwRig.Deploy(musou);
        Assert.Equal(TfwBattleRules.FieldSquads, Field(musou));
        Assert.Equal(1, old.GuardScale);
        Assert.Equal(TfwArmyRules.GarrisonCap, old.GarrisonCapNow);
        Assert.Equal(GuardRules.Default.Health, musou.TfwGuards!.Rules.Health);
        Assert.Equal(WarfrontGuardRules.Rules.Health, w.TfwGuards!.Rules.Health);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    public void Every_squad_count_gives_rows_whose_rear_squads_stand_in_the_gaps(int count)
    {
        var slots = Enumerable.Range(0, count).Select(i => TfwBattle.SlotGrid(i, count)).ToArray();
        int rows = slots.Max(s => s.Row) + 1;
        Assert.True(rows <= WarfrontGuardRules.MaxRows, $"{rows} rows");
        if (count >= 2) Assert.True(rows >= 2, "two squads or more stand in at least two rows");
        int widest = Math.Max(WarfrontGuardRules.SquadsPerRow, (count + WarfrontGuardRules.MaxRows - 1) / WarfrontGuardRules.MaxRows);
        Assert.All(slots.GroupBy(s => s.Row), g => Assert.True(g.Count() <= widest, $"row {g.Key} holds {g.Count()}"));
        foreach (var a in slots)
            foreach (var b in slots)
            {
                if (a.Row == b.Row && a != b) Assert.True(MathF.Abs(a.Lateral - b.Lateral) >= WarfrontGuardRules.SquadSpacing - 0.01f, $"{count}: squads of row {a.Row} closer than the spacing");
                if (b.Row == a.Row - 1) Assert.True(MathF.Abs(a.Lateral - b.Lateral) >= WarfrontGuardRules.SquadSpacing * 0.25f, $"{count}: a squad of row {a.Row} at {a.Lateral} m stands straight behind one at {b.Lateral} m");
            }
    }

    [Fact]
    public void The_squads_of_a_held_point_stand_in_staggered_rows_facing_the_approach()
    {
        var (w, b) = Secured();
        var guards = w.TfwGuards!;
        var dir = b.WarfrontApproach(0);
        Assert.True(dir.Length() > 0.99f, "the approach is chosen");
        var squads = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0 && b.SquadPost(s) == 0).ToArray();
        Assert.True(squads.Length >= 6, $"{squads.Length} squads on the point");
        Assert.True(b.WarfrontFormationOrders >= squads.Length);
        var point = b.Layout.Positions[0];
        var side = new Vector2(-dir.Y, dir.X);
        var places = squads.Select(s => b.WarfrontSlotOf(s)).ToArray();
        Assert.All(places, p => Assert.NotEqual(Vector3.Zero, p));
        float Fwd(Vector3 p) => Vector2.Dot(new Vector2(p.X - point.X, p.Y - point.Y), dir);
        float Lat(Vector3 p) => Vector2.Dot(new Vector2(p.X - point.X, p.Y - point.Y), side);
        var rows = places.GroupBy(p => MathF.Round(Fwd(p) / WarfrontGuardRules.RowSpacing)).OrderByDescending(g => g.Key).ToArray();
        Assert.True(rows.Length >= 2, $"{rows.Length} rows");
        // The squads stand apart (no two on one spot).
        for (int i = 0; i < places.Length; i++)
            for (int j = i + 1; j < places.Length; j++)
                Assert.True(Vector3.Distance(places[i], places[j]) >= 3f, $"squads {i} and {j} share a place");
        // The next row's squads stand in the front's gaps: none of them is straight behind a front squad.
        var front = rows[0].Select(Lat).ToArray();
        foreach (var p in rows[1])
            Assert.All(front, l => Assert.True(MathF.Abs(Lat(p) - l) >= WarfrontGuardRules.SquadSpacing * 0.25f, "a rear squad is straight behind a front one"));
        // Straight ranks (owner 2026-11-20, rows pass): the front row's squads stand on one straight line across the approach (not bent round the ring), at most SquadsPerRow of them,
        // and the next row is a whole RowSpacing behind it, so the ranks read as ranks.
        if (w.Musou!.Nav is null)
        {
            var frontFwd = rows[0].Select(Fwd).ToArray();
            Assert.True(frontFwd.Max() - frontFwd.Min() < 0.5f, "the front rank is a straight line");
            Assert.True(rows[0].Count() <= WarfrontGuardRules.SquadsPerRow);
            Assert.True(frontFwd.Min() - rows[1].Max(Fwd) >= WarfrontGuardRules.RowSpacing - 0.5f, "the next rank stands a row behind");
        }
        // The men's centres reached their places' rows: the front squads stand ahead of the rear ones.
        float frontMean = rows[0].Average(Fwd), rearMean = rows[^1].Average(Fwd);
        Assert.True(frontMean > rearMean + WarfrontGuardRules.RowSpacing * 0.5f);
        // Each slot is on walkable ground (the flat test arena has no graph; the real maps' walk is WarfrontGuardMapTests).
        if (w.Musou!.Nav is { } nav) Assert.All(places, p => Assert.True(nav.Nearest(p, 2f, 2.5f) >= 0));
    }

    [Fact]
    public void A_battered_front_squad_falls_back_and_the_next_row_steps_up()
    {
        var (w, b) = Secured();
        var guards = w.TfwGuards!;
        var dir = b.WarfrontApproach(0);
        var point = b.Layout.Positions[0];
        float Fwd(Vector3 p) => Vector2.Dot(new Vector2(p.X - point.X, p.Y - point.Y), dir);
        var squads = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0 && b.SquadPost(s) == 0).ToArray();
        int hurt = squads.OrderByDescending(s => Fwd(b.WarfrontSlotOf(s))).First();   // a front squad
        float before = Fwd(b.WarfrontSlotOf(hurt));
        int behind = squads.Where(s => s != hurt).OrderByDescending(s => Fwd(b.WarfrontSlotOf(s))).First();
        float behindBefore = Fwd(b.WarfrontSlotOf(behind));
        while (guards.AliveIn(hurt) > WarfrontGuardRules.BatteredMen) Assert.True(guards.KillOne(hurt, guards.Centre(hurt)));
        TfwRig.Seconds(w, 4f);
        Assert.True(Fwd(b.WarfrontSlotOf(hurt)) < before - WarfrontGuardRules.RowSpacing, "the battered squad went to the back");
        Assert.True(squads.Where(s => s != hurt).Max(s => Fwd(b.WarfrontSlotOf(s))) >= before - 0.01f, "the front line is held by another squad");
        Assert.True(Fwd(b.WarfrontSlotOf(behind)) >= behindBefore - 0.01f);
    }

    [Fact]
    public void A_fallen_front_squad_leaves_the_next_row_as_the_front()
    {
        var (w, b) = Secured();
        var guards = w.TfwGuards!;
        var dir = b.WarfrontApproach(0);
        var point = b.Layout.Positions[0];
        float Fwd(Vector3 p) => Vector2.Dot(new Vector2(p.X - point.X, p.Y - point.Y), dir);
        var squads = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0 && b.SquadPost(s) == 0).ToArray();
        float frontBefore = squads.Max(s => Fwd(b.WarfrontSlotOf(s)));
        int count = squads.Length;
        var fronts = squads.OrderByDescending(s => Fwd(b.WarfrontSlotOf(s))).Take(WarfrontGuardRules.SquadsPerRow).ToArray();
        foreach (int s in fronts) while (guards.AliveIn(s) > 0) Assert.True(guards.KillOne(s, guards.Centre(s)));
        TfwRig.Seconds(w, 5f);
        var left = Enumerable.Range(0, GuardSim.MaxSquads).Where(s => guards.AliveIn(s) > 0 && b.SquadPost(s) == 0).ToArray();
        Assert.Equal(count - fronts.Length, left.Length);
        Assert.True(left.Max(s => Fwd(b.WarfrontSlotOf(s))) >= frontBefore - 1f, "the next row stepped up to the front line");
    }

    [Fact]
    public void Musou_squads_get_no_formation()
    {
        var w = TfwRig.War();
        var b = TfwRig.Deploy(w);
        TfwRig.Secure(w, b, 0);
        TfwRig.KillAll(w);
        TfwRig.Seconds(w, 6f);
        Assert.Equal(0, b.WarfrontFormationOrders);
    }
}
