using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the Chaos Lord's duel kit on the Warboss's boss code: no Waaagh!, the slam's longer telegraph and the longer recoveries, the bolt only beyond 12 m, the leap after 4 s beyond 18 m with its 1 s marker and 8 s cooldown, and a kiting duelist reached.</summary>
public class LordAiTests(ITestOutputHelper output)
{
    static readonly MusouBossRules Warboss = MusouBossRules.Default;

    [Fact]
    public void NoWaaaghSummons_SlamTelegraphed_RecoveriesLonger()
    {
        var (w, b, d) = FinaleRig.Duel();
        FinaleRig.Steps(w, 2);
        var boss = Assert.IsType<MusouBoss>(w.MusouBoss);
        Assert.Equal(b.BossId, boss.Id);
        Assert.Empty(boss.Rules.WaaaghAt);
        Assert.Equal(Warboss.SlamWindUp * WarfrontDuelRules.SlamWindUpScale, boss.Rules.SlamWindUp, 4);
        Assert.Equal(Warboss.SlamRecover * WarfrontDuelRules.RecoveryScale, boss.Rules.SlamRecover, 4);
        Assert.Equal(Warboss.ChargeRecover * WarfrontDuelRules.RecoveryScale, boss.Rules.ChargeRecover, 4);
        Assert.True(w.Musou!.Damage(b.BossId, 1, FinaleRig.Lord(w, b).Health * 0.8f, Vector3.Zero, out _));   // past every Waaagh! share
        FinaleRig.Steps(w, 10);
        Assert.Equal(0, boss.Waaaghs);
    }

    [Fact]
    public void An_orks_fortress_warboss_keeps_the_warboss_rules()
    {
        var w = TfwRig.War(config: SmNet.Tests.Extras.Warfront.Battle.WarfrontBattleTests.WarfrontConfig);
        TfwRig.Steps(w, 5);
        w.Tfw!.Warfront!.Fortress = SmSim.Extras.Warfront.Fortress.WarfrontFortressSetup.For(new SmNet.Tests.Extras.Warfront.Fortress.WarfrontFortressBattleTests.StubArena());
        w.Tfw.War.SetFrontsForTests(SmSim.Extras.Musou.Campaign.WarRules.FrontStronghold);
        TfwRig.Deploy(w);
        TfwRig.Steps(w, 2);
        var boss = Assert.IsType<MusouBoss>(w.MusouBoss);
        Assert.Equal(Warboss.WaaaghAt, boss.Rules.WaaaghAt);
        Assert.Equal(Warboss.SlamWindUp, boss.Rules.SlamWindUp);
        Assert.Null(w.WarfrontChaosDuel);
    }

    /// <summary>Holds the duelist at <paramref name="distance"/> m from the Lord on the plaza (south of him, toward the gate), each tick, recording the telegraphs with the distance at their tick.</summary>
    static List<(WarfrontLordTelegraph T, float Distance)> Hold(World w, SmSim.Extras.Musou.Campaign.TfwBattle b, float distance, int ticks, Func<int, bool>? stop = null)
    {
        var told = new List<(WarfrontLordTelegraph, float)>();
        int seen = w.Musou!.LordTelegraphCount;
        for (int t = 0; t < ticks; t++)
        {
            var lord = FinaleRig.Lord(w, b).Position;
            var spot = b.FortressPlan!.BossSpot;
            float radius = b.FortressPlan.ArenaRadius - 1f, z = w.Players[1].Position.Z;
            Vector3 at;
            if (distance > radius * 0.8f)
            {
                // far: the point of the plaza's edge farthest from him (always beyond the distance asked while he is near the middle; the plaza's pull-back never fires)
                at = spot;
                float best = -1f;
                for (int k = 0; k < 32; k++)
                {
                    var c = spot + new Vector3(MathF.Cos(k * MathF.Tau / 32f) * radius, MathF.Sin(k * MathF.Tau / 32f) * radius, 0f);
                    float dd = FinaleRig.Flat(c, lord);
                    if (dd > best) { best = dd; at = c with { Z = z }; }
                }
            }
            else
            {
                var way = new Vector2(spot.X - lord.X, spot.Y - lord.Y);
                var dir = way.LengthSquared() > 1f ? Vector2.Normalize(way) : -Vector2.UnitY;
                at = lord + new Vector3(dir * distance, 0f) with { Z = 0f };
                if (FinaleRig.Flat(at, spot) > radius) at = lord - new Vector3(dir * distance, 0f);
                at = at with { Z = z };
            }
            w.TfwRespawnAt(1, at, 0f, faceCentre: false);
            w.Step(_ => default);
            int count = w.Musou.LordTelegraphCount;
            for (; seen < count; seen++)
            {
                var tel = w.Musou.LordTelegraphs[w.Musou.LordTelegraphs.Count - (count - seen)];
                told.Add((tel, FinaleRig.Flat(FinaleRig.Lord(w, b).Position, w.Players[1].Position)));
            }
            if (stop?.Invoke(t) == true) break;
        }
        return told;
    }

    [Fact]
    public void BoltOnlyBeyond12m()
    {
        var (w, b, d) = FinaleRig.Duel();
        var far = Hold(w, b, 15f, 30 * 3);
        var bolts = far.Where(x => x.T.Ability == WarfrontLordTelegraph.Bolt).ToList();
        output.WriteLine($"bolts at 15 m: {bolts.Count}");
        Assert.NotEmpty(bolts);
        foreach (var (_, dist) in bolts) Assert.True(dist > WarfrontDuelRules.BoltBeyond - 1.5f, $"a bolt at {dist} m");
        // up close: no bolt
        var (w2, b2, _) = FinaleRig.Duel();
        var near = Hold(w2, b2, 6f, 30 * 3);
        Assert.DoesNotContain(near, x => x.T.Ability == WarfrontLordTelegraph.Bolt);
    }

    [Fact]
    public void LeapAfter4sBeyond18m_LeapMarkerOneSecond_LeapCooldown8s()
    {
        var (w, b, d) = FinaleRig.Duel();
        uint start = w.Musou!.Tick;   // the telegraphs carry the enemy pool's tick
        var told = Hold(w, b, 22f, 30 * 20);
        var leaps = told.Where(x => x.T.Ability == WarfrontLordTelegraph.Leap).Select(x => x.T).ToList();
        foreach (var l in leaps) output.WriteLine($"leap at tick {l.Tick - start}, {l.Seconds} s, at {l.At}");
        Assert.True(leaps.Count >= 2, $"{leaps.Count} leaps");
        Assert.True(leaps[0].Tick - start >= 4 * 30, "not before 4 s beyond 18 m");
        Assert.Equal(WarfrontDuelRules.LeapMarkerSeconds, leaps[0].Seconds);
        Assert.True(leaps[1].Tick - leaps[0].Tick >= (WarfrontDuelRules.LeapMarkerSeconds + WarfrontDuelRules.LeapCooldownSeconds) * 30 - 1, "the cooldown");
    }

    [Fact]
    public void KitingBounded_the_leap_lands_on_a_still_kiter()
    {
        var (w, b, d) = FinaleRig.Duel();
        var spot = b.FortressPlan!.BossSpot;
        var kite = new Vector3(spot.X, spot.Y - 22f, w.Players[1].Position.Z);
        float before = w.Players[1].Health + w.Players[1].Shield;
        bool hit = false;
        for (int t = 0; t < 30 * 8 && !hit; t++)
        {
            if (FinaleRig.Flat(FinaleRig.Lord(w, b).Position, kite) < 10f) kite = new Vector3(spot.X, spot.Y + 22f * MathF.Sign(spot.Y - kite.Y + 0.01f), kite.Z);
            if (FinaleRig.Flat(w.Players[1].Position, kite) > 0.5f) w.TfwRespawnAt(1, kite, 0f, faceCentre: false);
            before = MathF.Max(before, 0f);
            float h0 = w.Players[1].Health + w.Players[1].Shield;
            w.Step(_ => default);
            if (w.MusouBoss?.Leaping == false && FinaleRig.Flat(FinaleRig.Lord(w, b).Position, w.Players[1].Position) <= WarfrontDuelRules.LeapRadius + 0.5f) hit = true;
        }
        Assert.True(hit, "the Lord reached the kiter within 8 s");
    }

    [Fact]
    public void ChargeClosesGap()
    {
        var (w, b, d) = FinaleRig.Duel();
        var told = Hold(w, b, 10f, 30 * 6);
        Assert.Contains(told, x => x.T.Ability == WarfrontLordTelegraph.Charge);   // 6 to 15 m: the Warboss's charge
    }
}
