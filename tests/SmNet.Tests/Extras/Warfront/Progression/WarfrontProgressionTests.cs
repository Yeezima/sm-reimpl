using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Progression;

/// <summary>Warfront W7: XP credit, card offers, the picker's server-side rules, stat bonuses, Ork scaling and the tuning table. Pure, no World, no original data.</summary>
public class WarfrontProgressionTests
{
    const ulong A = 101, B = 102, C = 103;

    static WarfrontCampaign Squad(params ulong[] keys)
    {
        var c = new WarfrontCampaign(42);
        foreach (var k in keys) c.Join(k, out _);
        c.Embark();
        return c;
    }

    // ---- XP -------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ordinary_kill_pays_only_the_killer_or_squad_owner()
    {
        var c = Squad(A, B);
        var x = new WarfrontXpLedger(c);
        Assert.Equal([A], x.Kill(A, 10, WarfrontKillClass.Ordinary, 1));
        Assert.Equal(10, c.Player(A)!.Xp);
        Assert.Equal(0, c.Player(B)!.Xp);
        Assert.Empty(x.Kill(0, 10, WarfrontKillClass.Ordinary, 2));   // nobody's kill
    }

    [Fact]
    public void Elite_boss_and_capture_pay_every_player_once_and_the_killer_never_twice()
    {
        var c = Squad(A, B, C);
        var x = new WarfrontXpLedger(c);
        Assert.Equal(3, x.Kill(A, 100, WarfrontKillClass.Elite, 11).Count);
        Assert.Equal(100, c.Player(A)!.Xp);   // not 200: shared only, no personal on top
        Assert.Equal(100, c.Player(B)!.Xp);
        Assert.Empty(x.Kill(A, 100, WarfrontKillClass.Elite, 11));   // the same event again
        Assert.Equal(100, c.Player(A)!.Xp);
        x.Kill(B, 2000, WarfrontKillClass.Boss, 12);
        Assert.Equal(2100, c.Player(C)!.Xp);
        Assert.Equal(3, x.Capture(400, 21).Count);
        Assert.Empty(x.Capture(400, 21));
        Assert.Equal(2500, c.Player(A)!.Xp);
    }

    [Fact]
    public void Shared_events_skip_disconnected_players()
    {
        var c = Squad(A, B);
        c.Leave(B);
        var x = new WarfrontXpLedger(c);
        Assert.Equal([A], x.Capture(400, 22));
        Assert.Equal(0, c.Player(B)!.Xp);
    }

    [Fact]
    public void Level_ups_are_reported_with_w4_levels()
    {
        var c = Squad(A);
        var x = new WarfrontXpLedger(c);
        x.Capture((int)WarfrontCampaign.XpForLevel(2), 23);
        var ups = x.Drain();
        Assert.Single(ups);
        Assert.Equal(new WarfrontLevelUp(A, 1, 2), ups[0]);
        Assert.Equal(1, c.Player(A)!.PendingPicks);
        Assert.Empty(x.Drain());
    }

    [Fact]
    public void Classify_matches_the_plan()
    {
        Assert.Equal(WarfrontKillClass.Elite, WarfrontXpLedger.Classify(MusouEnemyType.Nob));
        Assert.Equal(WarfrontKillClass.Elite, WarfrontXpLedger.Classify(MusouEnemyType.BigShootaNob));
        Assert.Equal(WarfrontKillClass.Elite, WarfrontXpLedger.Classify(MusouEnemyType.ScrapMek));
        Assert.Equal(WarfrontKillClass.Boss, WarfrontXpLedger.Classify(MusouEnemyType.Warboss));
        Assert.Equal(WarfrontKillClass.Boss, WarfrontXpLedger.Classify(MusouEnemyType.BossNob));
        Assert.Equal(WarfrontKillClass.Elite, WarfrontXpLedger.Classify(MusouEnemyType.Weirdboy));
        Assert.Equal(WarfrontKillClass.Ordinary, WarfrontXpLedger.Classify(MusouEnemyType.Boy));
    }

    // ---- offers ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Offers_are_four_distinct_cards_slot_four_a_retainer_when_eligible()
    {
        for (int pick = 0; pick < 200; pick++)
            foreach (ulong key in new[] { A, B })
            {
                var o = WarfrontOffers.Roll(7, key, pick, true);
                Assert.Equal(4, o.Length);
                Assert.Equal(4, o.Distinct().Count());
                Assert.All(o.Take(3), c => Assert.Contains(c, WarfrontProgressionRules.StatCards));
                Assert.Equal(WarfrontCard.Retainer, o[3]);
            }
    }

    [Fact]
    public void Ineligible_slot_four_is_a_fourth_distinct_stat_card()
    {
        for (int pick = 0; pick < 200; pick++)
        {
            var o = WarfrontOffers.Roll(7, A, pick, false);
            Assert.Equal(4, o.Distinct().Count());
            Assert.All(o, c => Assert.Contains(c, WarfrontProgressionRules.StatCards));
        }
    }

    [Fact]
    public void Retainer_eligibility_roster_and_dreadnought()
    {
        Assert.True(WarfrontOffers.RetainerEligible(8, false));
        Assert.False(WarfrontOffers.RetainerEligible(9, false));
        Assert.False(WarfrontOffers.RetainerEligible(1, true));
    }

    [Fact]
    public void Offers_are_deterministic_and_vary_with_seed_player_and_pick()
    {
        Assert.Equal(WarfrontOffers.Roll(7, A, 3, true), WarfrontOffers.Roll(7, A, 3, true));
        var set = new HashSet<string>();
        for (int pick = 0; pick < 30; pick++) set.Add(string.Join(",", WarfrontOffers.Roll(7, A, pick, true).Take(3)));
        Assert.True(set.Count > 3);
        Assert.NotEqual(string.Join(",", WarfrontOffers.Roll(7, A, 0, true)) + string.Join(",", WarfrontOffers.Roll(7, A, 1, true)) + string.Join(",", WarfrontOffers.Roll(7, A, 2, true)),
                        string.Join(",", WarfrontOffers.Roll(8, A, 0, true)) + string.Join(",", WarfrontOffers.Roll(8, A, 1, true)) + string.Join(",", WarfrontOffers.Roll(8, A, 2, true)));
    }

    // ---- picker ---------------------------------------------------------------------------------------------------------------------------

    static (WarfrontCampaign c, WarfrontPicker p) WithPending(int levels, params ulong[] keys)
    {
        var c = Squad(keys);
        foreach (var k in keys) c.AddXp(k, WarfrontCampaign.XpForLevel(1 + levels));
        return (c, new WarfrontPicker(c));
    }

    [Fact]
    public void A_pick_opens_only_in_a_lull_or_aboard()
    {
        var (c, p) = WithPending(1, A);
        Assert.Null(p.TryOpen(A, 100, aboard: false, lull: false, 1, false));
        Assert.False(p.Protected(A));
        var o = p.TryOpen(A, 100, aboard: false, lull: true, 1, false);
        Assert.NotNull(o);
        Assert.True(p.Protected(A));
        Assert.Equal(c.Player(A)!.PicksTaken, o!.PickIndex);
        Assert.True(WarfrontPicker.IsLull(3.0, 12.5f));
        Assert.False(WarfrontPicker.IsLull(2.9, 50f));
        Assert.False(WarfrontPicker.IsLull(10, 12f));
    }

    [Fact]
    public void Openings_in_the_field_are_45_seconds_apart_but_the_ship_has_no_gap()
    {
        var (c, p) = WithPending(3, A);
        Assert.NotNull(p.TryOpen(A, 100, false, true, 1, false));
        Assert.True(p.TryPick(A, 0, 0, out _, out _));
        Assert.Null(p.TryOpen(A, 120, false, true, 1, false));   // 20 s later: queued
        Assert.NotNull(p.TryOpen(A, 145, false, true, 1, false));
        Assert.True(p.TryPick(A, 1, 1, out _, out _));
        Assert.NotNull(p.TryOpen(A, 146, aboard: true, lull: false, 1, false));   // aboard: at once
        Assert.Equal(1, c.Player(A)!.PendingPicks);
    }

    [Fact]
    public void Server_rejects_wrong_index_wrong_player_and_no_open_offer()
    {
        var (c, p) = WithPending(1, A, B);
        Assert.False(p.TryPick(A, 0, 0, out _, out var e1));   // nothing open
        Assert.Equal("no open offer", e1);
        p.TryOpen(A, 0, true, false, 1, false);
        Assert.False(p.TryPick(B, 0, 0, out _, out var e2));   // B has no offer
        Assert.Equal("no open offer", e2);
        Assert.False(p.TryPick(999, 0, 0, out _, out _));   // unknown player
        Assert.False(p.TryPick(A, 4, 0, out _, out var e3));
        Assert.Equal("no such card", e3);
        Assert.False(p.TryPick(A, -1, 0, out _, out _));
        Assert.False(p.TryPick(A, 0, 5, out _, out var e4));
        Assert.Equal("stale offer", e4);
        Assert.True(p.Protected(A));   // nothing above changed the state
        Assert.Equal(1, c.Player(A)!.PendingPicks);
        Assert.True(p.TryPick(A, 2, 0, out var r, out _));
        Assert.Equal(1, c.Player(A)!.PicksTaken);
        Assert.Equal((int)r.Card, c.Player(A)!.Cards[0]);
        Assert.False(p.TryPick(A, 2, 0, out _, out _));   // never twice
        Assert.Equal(1, c.Player(A)!.PicksTaken);
    }

    [Fact]
    public void Protection_ends_on_pick_and_on_timeout_and_others_are_untouched()
    {
        var (c, p) = WithPending(1, A, B);
        p.TryOpen(A, 0, true, false, 1, false);
        p.TryOpen(B, 5, true, false, 1, false);
        Assert.True(p.Protected(A) && p.Protected(B));
        Assert.True(p.TryPick(A, 1, 0, out _, out _));
        Assert.False(p.Protected(A));
        Assert.True(p.Protected(B));
        Assert.Empty(p.Tick(24.9));   // B opened at 5: 19.9 s
        var auto = p.Tick(25.0);
        Assert.Single(auto);
        Assert.True(auto[0].Timeout);
        Assert.Equal(0, auto[0].Slot);
        Assert.Equal(B, auto[0].Key);
        Assert.False(p.Protected(B));
        Assert.Equal(1, c.Player(B)!.PicksTaken);
        Assert.Empty(p.Tick(100));
    }

    [Fact]
    public void Leaving_drops_the_offer_and_a_return_reopens_the_same_cards()
    {
        var (c, p) = WithPending(1, A);
        var first = p.TryOpen(A, 0, true, false, 1, false)!;
        p.Leave(A);
        Assert.False(p.Protected(A));
        Assert.Equal(1, c.Player(A)!.PendingPicks);
        var again = p.TryOpen(A, 50, true, false, 1, false)!;
        Assert.Equal(first.Cards, again.Cards);
    }

    [Fact]
    public void A_dreadnought_or_full_roster_is_offered_no_retainer()
    {
        var (_, p) = WithPending(2, A);
        var o = p.TryOpen(A, 0, true, false, 1, dreadnought: true)!;
        Assert.DoesNotContain(WarfrontCard.Retainer, o.Cards);
        p.Leave(A);
        var o2 = p.TryOpen(A, 1, true, false, rosterSize: 9, dreadnought: false)!;
        Assert.DoesNotContain(WarfrontCard.Retainer, o2.Cards);
    }

    // ---- stats ----------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Stat_cards_stack_additively_and_the_cooldown_has_a_floor()
    {
        var b = WarfrontStatBonuses.For([(int)WarfrontCard.Damage, (int)WarfrontCard.Damage, (int)WarfrontCard.MaxHp, (int)WarfrontCard.AttackSpeed,
            (int)WarfrontCard.MaxArmor, (int)WarfrontCard.Retainer, (int)WarfrontCard.Retainer]);
        Assert.Equal(1.2f, b.Damage, 4);
        Assert.Equal(1.1f, b.MaxHp, 4);
        Assert.Equal(1.1f, b.AttackSpeed, 4);
        Assert.Equal(1.15f, b.MaxArmor, 4);
        Assert.Equal(2, b.Retainers);
        Assert.Equal(1f, b.CooldownScale);
        var cd = WarfrontStatBonuses.For(Enumerable.Repeat((int)WarfrontCard.CooldownReduction, 30));
        Assert.Equal(0.4f, cd.CooldownScale, 4);
        Assert.Equal(1f / 1.1f, b.EffectiveDamageTakenScale, 4);
    }

    [Fact]
    public void A_retainer_gets_one_third_of_the_magnitude()
    {
        var cards = new[] { (int)WarfrontCard.Damage, (int)WarfrontCard.Damage, (int)WarfrontCard.Damage, (int)WarfrontCard.MaxHp, (int)WarfrontCard.CooldownReduction };
        var r = WarfrontStatBonuses.ForRetainer(cards);
        Assert.Equal(1f + 0.3f / 3f, r.Damage, 4);
        Assert.Equal(1f + 0.1f / 3f, r.MaxHp, 4);
        Assert.Equal(1f - 0.08f / 3f, r.CooldownScale, 4);
    }

    // ---- Ork scaling ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Ork_scaling_follows_the_plan_table_and_adds_20_percent_per_extra_human()
    {
        var l1 = WarfrontOrkScaling.For(1, 1);
        Assert.Equal(1f, l1.Health); Assert.Equal(1f, l1.Damage);
        var l5 = WarfrontOrkScaling.For(5, 1);
        Assert.Equal(1.32f, l5.Health, 3); Assert.Equal(1.20f, l5.Damage, 3);
        var l10 = WarfrontOrkScaling.For(10, 1);
        Assert.Equal(1.72f, l10.Health, 3); Assert.Equal(1.45f, l10.Damage, 3);
        Assert.Equal(1.2f * l5.Health, WarfrontOrkScaling.For(5, 2).Health, 4);
        Assert.Equal(1.6f * l5.Health, WarfrontOrkScaling.For(5, 4).Health, 4);
        Assert.Equal(l5.Damage, WarfrontOrkScaling.For(5, 4).Damage);   // humans scale HP only
    }

    [Fact]
    public void The_level_scale_is_the_level_part_alone_and_the_humans_factor_stays_with_the_battle()
    {
        Assert.Equal(WarfrontOrkScale.One, WarfrontOrkScaling.LevelScale(1));
        var l5 = WarfrontOrkScaling.LevelScale(5);
        Assert.Equal(WarfrontOrkScaling.For(5, 1), l5);
        Assert.Equal(1.32f, l5.Health, 3);
        Assert.Equal(WarfrontOrkScaling.For(5, 4).Damage, l5.Damage);
        Assert.Equal(1.6f * l5.Health, WarfrontOrkScaling.For(5, 4).Health, 4);   // For() is the level part times the humans' (the tuning table); the battle reads the two apart
    }

    [Fact]
    public void Damage_taken_rule_uses_armour_while_the_shield_stands_else_hp()
    {
        var b = WarfrontStatBonuses.For([(int)WarfrontCard.MaxHp, (int)WarfrontCard.MaxHp, (int)WarfrontCard.MaxArmor]);
        Assert.Equal(1f / 1.15f, WarfrontStatBonuses.DamageTaken(50f, b), 4);
        Assert.Equal(1f / 1.2f, WarfrontStatBonuses.DamageTaken(0f, b), 4);
        Assert.Equal(1f, WarfrontStatBonuses.DamageTaken(10f, WarfrontStatBonuses.None));
    }

    [Fact]
    public void Leaving_clears_the_opening_gap()
    {
        var (_, p) = WithPending(2, A);
        p.TryOpen(A, 100, false, true, 1, false);
        p.Leave(A);
        Assert.NotNull(p.TryOpen(A, 101, false, true, 1, false));
    }

    // ---- tuning table ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Tuning_table_matches_the_scaling_and_has_the_representative_orks()
    {
        var rows = WarfrontTuningTable.Build(20, 1);
        Assert.Equal(20, rows.Count);
        Assert.Equal(1f, rows[0].HpMult);
        Assert.Equal(WarfrontOrkScaling.For(10, 1).Health, rows[9].HpMult);
        var boy = rows[0].Orks.First(o => o.Type == MusouEnemyType.Boy);
        Assert.Equal(85f, boy.Hp);
        Assert.Equal(8f, boy.Hit);
        Assert.True(boy.BolterTtkNone > 0);
        Assert.Equal(5, rows[0].Orks.Count);
        Assert.True(rows[19].Orks.First(o => o.Type == MusouEnemyType.Nob).BolterTtkNone > rows[0].Orks.First(o => o.Type == MusouEnemyType.Nob).BolterTtkNone);
        Assert.True(rows[19].Orks.First(o => o.Type == MusouEnemyType.Nob).BolterTtkDamage < rows[19].Orks.First(o => o.Type == MusouEnemyType.Nob).BolterTtkNone);
    }
}
