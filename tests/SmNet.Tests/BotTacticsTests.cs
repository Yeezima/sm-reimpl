using System.Numerics;

namespace SmNet.Tests;

/// <summary>Stream C (owner's batch 2026-11-06 late): the Classic PvP test bots' melee misses, strafing, weaving and flanking (a test aid, ours, estimates).</summary>
public class BotTacticsTests
{
    [Fact]
    public void About_three_in_ten_melee_attacks_miss()
    {
        var t = new BotTactics(42);
        int misses = 0, n = 10000;
        for (int i = 0; i < n; i++) if (t.RollMeleeMiss()) misses++;
        Assert.InRange(misses / (float)n, BotTactics.MeleeMissChance - 0.02f, BotTactics.MeleeMissChance + 0.02f);
    }

    [Fact]
    public void A_missed_swing_turns_out_of_the_lock_and_hit_cones()
    {
        // The lock's cone (45 degrees, apex 1 m behind) and the hit cone (60 degrees) must not hold a target at the turned angle; closer than the fan start every front target locks.
        Assert.True(BotTactics.MissTurn(3f) > 60f);
        Assert.True(BotTactics.MissTurn(1.5f) > 90f);
        foreach (float d in new[] { 1.9f, 2.5f, 3f, 4f })
        {
            float a = BotTactics.MissTurn(d) * MathF.PI / 180f;
            var target = new Vector2(d * MathF.Sin(a), d * MathF.Cos(a));
            var fromApex = target - new Vector2(0, -1f);
            float angle = MathF.Atan2(MathF.Abs(fromApex.X), fromApex.Y) * 180f / MathF.PI;
            Assert.True(angle > 45f, $"{d} m: {angle} degrees from the apex");
        }
    }

    [Fact]
    public void The_strafe_goes_left_and_right_in_legs_of_0_7_to_2_s()
    {
        var t = new BotTactics(7);
        int side = t.StrafeSide(0), changes = 0;
        double lastChange = 0;
        for (double now = 0; now < 60; now += 1.0 / 30)
        {
            int s = t.StrafeSide(now);
            Assert.True(s is 1 or -1);
            if (s != side)
            {
                double leg = now - lastChange;
                if (changes > 0) Assert.InRange(leg, BotTactics.StrafeMin - 0.05, BotTactics.StrafeMax + 0.05);
                changes++; lastChange = now; side = s;
            }
        }
        Assert.InRange(changes, 25, 90);
    }

    [Fact]
    public void The_weave_alternates_sides()
    {
        var t = new BotTactics(3);
        float first = t.WeaveAngle(0);
        Assert.Equal(BotTactics.WeaveDegrees, MathF.Abs(first));
        float later = t.WeaveAngle(BotTactics.WeaveMax + 0.01);
        Assert.Equal(-first, later);
    }

    [Fact]
    public void An_ally_facing_the_enemy_within_range_engages_it()
    {
        var enemy = new Vector2(0, 20);
        Assert.True(BotTactics.Engages(Vector2.Zero, 0f, enemy));            // yaw 0 faces +y
        Assert.False(BotTactics.Engages(Vector2.Zero, MathF.PI / 2, enemy)); // faces -x
        Assert.False(BotTactics.Engages(Vector2.Zero, 0f, new Vector2(0, 60)));
    }

    [Fact]
    public void The_flank_strafe_opens_the_angle_around_the_enemy_until_70_degrees()
    {
        var enemy = new Vector2(0, 0);
        var ally = new Vector2(0, -20);
        var me = new Vector2(2, -18);
        for (int i = 0; i < 400 && BotTactics.FlankSide(enemy, me, ally) is var side and not 0; i++)
        {
            float before = BotTactics.AngleAround(enemy, me, ally);
            var toEnemy = Vector2.Normalize(enemy - me);
            var right = new Vector2(toEnemy.Y, -toEnemy.X);
            me += right * side * 0.2f;
            me = enemy + Vector2.Normalize(me - enemy) * 18f;   // strafing keeps about the same distance
            Assert.True(BotTactics.AngleAround(enemy, me, ally) > before - 1e-3f);
        }
        Assert.True(BotTactics.AngleAround(enemy, me, ally) >= BotTactics.FlankDegrees - 1f);
        Assert.Equal(0, BotTactics.FlankSide(enemy, me, ally));
    }

    [Fact]
    public void The_flank_point_is_a_right_angle_around_on_the_bots_side()
    {
        var enemy = new Vector2(0, 0);
        var ally = new Vector2(0, -20);
        var p = BotTactics.FlankPoint(enemy, new Vector2(5, -25), ally);
        Assert.True(p.X > 0);
        Assert.InRange(BotTactics.AngleAround(enemy, p, ally), 89f, 91f);
        Assert.InRange(p.Length(), BotTactics.FlankRadius - 0.01f, BotTactics.FlankRadius + 0.01f);
        var close = BotTactics.FlankPoint(enemy, new Vector2(-3, -2), ally);
        Assert.True(close.X < 0);
        Assert.True(close.Length() < BotTactics.FlankRadius);
    }
}
