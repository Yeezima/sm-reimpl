using System.Diagnostics;
using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;

namespace SmNet.Tests.Extras.Warfront.Battle;

/// <summary>
/// Warfront's melee cleave and level-up cards (owner 2026-11-18). Cleave: every melee weapon's reach x1.2 (the Dreadnought x1.4), no cap on the enemies struck, an Ork struck when his collision overlaps the
/// volume; none of it in Musou Mode or Classic. Cards: every offer lists all five stat cards, then +1 Retainer for a squad-capable player with room.
/// </summary>
public class WarfrontCleaveTests
{
    const int Human = TfwRig.Human;
    const float Struck = 300f;      // the swings here deal 500: far above the allies' chips on a frozen Ork (a lone one takes all their fire)
    const float DreadStruck = 60f;  // the Dreadnought's own strikes deal 100 and 125, into a crowd that spreads the allies' chips thin

    static float Health(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? e.Health : 0f;

    /// <summary>Spawns a frozen Ork at flat distance <paramref name="d"/> ahead of the player (+Y), at a small bearing, and returns its id.</summary>
    static ushort Boy(World w, float d, float bearingDegrees = 0f)
    {
        float a = bearingDegrees * MathF.PI / 180f;
        var at = w.Players[Human].Position + new Vector3(MathF.Sin(a) * d, MathF.Cos(a) * d, 0f);
        ushort id = w.Musou!.Spawn(MusouEnemyType.Boy, at, 0f, 100000f);
        Assert.NotEqual(0, id);
        return id;
    }

    /// <summary>The test classes' melee steps at 500 damage, so a swing stands out from the allies' chips (the rig's own steps deal 1).</summary>
    static SimConfig Strong(SimConfig c) => c with { Classes = [.. c.Classes.Select(k => k with { Melee = [.. k.Melee.Select(m => m with { Damage = 500f })], MeleeHeavy = [.. k.MeleeHeavy.Select(m => m with { Damage = 500f })] })] };

    /// <summary>The same configuration without the Warfront flag: Musou Mode's war, every other rule alike.</summary>
    static SimConfig NoWarfront(SimConfig c) => c with { Extras = c.Extras & ~ContentExtras.Warfront };

    static World MusouWorld() => TfwRig.War(null, Strong(NoWarfront(WarfrontBattleTests.WarfrontConfig)));

    static World WarfrontWorld()
    {
        var w = TfwRig.War(null, Strong(WarfrontBattleTests.WarfrontConfig));
        TfwRig.Steps(w, 5);
        return w;
    }

    static World Ready(World w)
    {
        TfwRig.Deploy(w);
        TfwRig.Seconds(w, 1f);
        w.Musou!.Frozen = true;
        return w;
    }

    /// <summary>Swings once and returns the health of each Ork before it and after about a second.</summary>
    static void Swing(World w, IReadOnlyList<ushort> orks, out Dictionary<ushort, float> before)
    {
        before = orks.ToDictionary(i => i, i => Health(w, i));
        for (int k = 0; k < 40; k++) w.Step(_ => new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Melee : InputButtons.None));
    }

    /// <summary>The farthest of a row of Orks every 0.15 m up to 25 m (small bearings) that the swing struck: the centre distance, and the Orks' body radius.</summary>
    static string LastDiag = "";   // the last Reach's counts, for a failure message

    static (float Farthest, float Radius) Reach(World w, float struck = Struck)
    {
        // Three rows (ahead, and 100 degrees to each side: inside the cone), so a wall in one direction does not stop the measure; the farthest Ork struck in any.
        var ids = new List<ushort>();
        var dist = new Dictionary<ushort, float>();
        foreach (float row in new[] { 0f, 100f, -100f })
            for (int i = 0; i < 160; i++)
            {
                float d = 1f + 0.15f * i;
                ushort id = Boy(w, d, row + (i % 5 - 2) * 2f);
                ids.Add(id); dist[id] = d;
            }
        Swing(w, ids, out var before);
        float far = 0f;
        foreach (var id in ids) if (Health(w, id) < before[id] - struck) far = MathF.Max(far, dist[id]);
        LastDiag += $" [far {far}; alive after {ids.Count(i => w.Musou!.TryGet(i, out var e) && e.Alive)} of {ids.Count}; struck per row: {string.Join("/", new[] { 0, 160, 320 }.Select(o => ids.Skip(o).Take(160).Count(i => Health(w, i) < before[i] - struck)))}; max dist per row {string.Join("/", new[] { 0, 160, 320 }.Select(o => ids.Skip(o).Take(160).Where(i => Health(w, i) < before[i] - struck).Select(i => dist[i]).DefaultIfEmpty(0).Max()))}]";
        return (far, w.Musou!.Body(MusouEnemyType.Boy).Radius);
    }

    [Fact]
    public void A_Warfront_swing_reaches_1_2_times_as_far_as_Musou_Modes_shape_by_the_Orks_collision()
    {
        var w = Ready(WarfrontWorld());
        var (far, radius) = Reach(w);
        var step = w.Config.Classes[w.Players[Human].Class].Melee[0];
        float musouReach = MusouCleaveShape.Of(w.Config, step, war: true).Reach;   // what Musou Mode's war gives this swing (x1.3, the human x0.72)
        // Warfront: reach x1.2, and the Ork's collision (his radius) reaching into it: the farthest centre struck is the reach plus a radius, to the row's 0.15 m step.
        Assert.True(far >= musouReach * 1.2f - 0.2f && far <= musouReach * 1.2f + radius + 0.2f, $"farthest struck {far} m, Musou shape {musouReach} m, radius {radius} {LastDiag}");
        Assert.True(far > musouReach + radius + 0.5f, $"beyond Musou Mode's own reach: {far} against {musouReach}");
    }

    [Fact]
    public void An_Ork_whose_collision_overlaps_the_volume_is_struck_and_one_clear_of_it_is_not()
    {
        var w = Ready(WarfrontWorld());
        var (far, radius) = Reach(w);   // the row's farthest struck centre is reach + radius (or the row's limit)
        Assert.True(far > 0f);
        var w2 = Ready(WarfrontWorld());
        float reach = far - radius;
        var inside = Boy(w2, reach + radius * 0.5f);     // the centre is past the reach, the body is not
        var outside = Boy(w2, reach + radius + 0.6f);    // clear of the volume
        Swing(w2, [inside, outside], out var before);
        Assert.True(Health(w2, inside) < before[inside] - Struck, "an Ork whose body reaches into the volume is struck");
        Assert.True(Health(w2, outside) > before[outside] - Struck, "an Ork wholly beyond it is not");
    }

    [Fact]
    public void A_Warfront_swing_strikes_every_Ork_in_the_volume_with_no_cap_and_the_server_step_stays_cheap()
    {
        var w = Ready(WarfrontWorld());
        var ids = new List<ushort>();
        for (int i = 0; i < 200; i++) ids.Add(Boy(w, 1.2f + 0.02f * (i % 100), (i % 9 - 4) * 4f));
        Swing(w, ids, out var before);
        int hit = ids.Count(i => Health(w, i) < before[i] - Struck);
        Assert.True(ids.Count == hit, $"{hit} of {ids.Count} struck; struck indexes {string.Join(",", ids.Select((id, k) => (id, k)).Where(t => Health(w, t.id) < before[t.id] - Struck).Select(t => t.k))}; drops {string.Join(",", ids.Take(12).Select(i => before[i] - Health(w, i)))}");   // all 200: the war's cap is already the whole pool, Warfront keeps it so
        // The swing tick's cost with 200 Orks in the volume, against ordinary ticks (a loose bound: the CI box is shared).
        var w2 = Ready(WarfrontWorld());
        for (int i = 0; i < 200; i++) Boy(w2, 1.2f + 0.02f * (i % 100), (i % 9 - 4) * 4f);
        var watch = Stopwatch.StartNew();
        double worst = 0;
        for (int k = 0; k < 10; k++)
        {
            long t0 = watch.ElapsedTicks;
            w2.Step(_ => new PlayerInput(0, 0f, 0f, 0f, k == 0 ? InputButtons.Melee : InputButtons.None));
            worst = Math.Max(worst, (watch.ElapsedTicks - t0) * 1000.0 / Stopwatch.Frequency);
        }
        Assert.True(worst < 33.3, $"the worst of 10 ticks with a 200-Ork swing was {worst:F2} ms (a tick is 33 ms)");
    }

    [Fact]
    public void The_hook_leaves_a_Musou_run_alone_and_the_scales_are_1_2_and_1_4()
    {
        var w = Ready(MusouWorld());
        Assert.Null(w.Tfw!.Warfront);   // the hook's gate: no Warfront object, nothing changes
        Assert.Equal(1.2f, WarfrontBattleRules.CleaveScale(false));
        Assert.Equal(1.4f, WarfrontBattleRules.CleaveScale(true));
        var step = new MeleeStep(1f, 0.2f, 0.4f, 0.8f, 0.5f, 0.5f, 100f, 0f, 0f);
        var musou = MusouCleaveShape.Of(w.Config, step, war: true);
        var warfront = MusouCleaveShape.Of(w.Config, step, war: true, warfrontScale: 1.2f);
        var dread = MusouCleaveShape.Of(w.Config, step, war: true, warfrontScale: 1.4f);
        Assert.Equal(musou.Reach * 1.2f, warfront.Reach, 3);
        Assert.Equal(musou.Reach * 1.4f, dread.Reach, 3);
        Assert.Equal(musou.HalfAngleDegrees, warfront.HalfAngleDegrees);   // the cone is untouched
        Assert.Equal(MusouSim.Capacity, warfront.Most);
        Assert.Equal(musou.Reach, MusouCleaveShape.Of(w.Config, step, war: true).Reach);   // the default is 1: Classic and Musou numbers stand
    }

    [Fact]
    public void The_Dreadnought_swings_1_4_times_as_far_as_Musou_Modes_Dreadnought_with_no_cap()
    {
        int Dread(SimConfig c) => c.Classes.ToList().FindIndex(k => k.Name == DreadnoughtRules.ClassName);
        var still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };
        float Far(SimConfig config)
        {
            var w = TfwRig.War(null, config);
            Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = Dread(config) }));
            TfwRig.Command(w, TfwCommand.ChangeClass);
            TfwRig.Steps(w, 3);
            TfwRig.Deploy(w);
            TfwRig.Seconds(w, DreadnoughtRules.EntranceSeconds + 0.3f);
            w.Musou!.Frozen = true;
            return Reach(w, DreadStruck).Farthest;
        }
        var warfrontConfig = MusouLoadout.Prepare(WarfrontBattleTests.WarfrontConfig with { MusouRules = still });
        Assert.True(Dread(warfrontConfig) >= 0, "the Dreadnought class exists");
        float warfront = Far(warfrontConfig);
        float musouReach = MusouCleaveShape.Of(warfrontConfig, DreadnoughtMelee.Light()[0], war: true).Reach;   // his reach in Musou Mode's war
        float radius = 0.4f;   // the Boy's capsule
        Assert.True(warfront >= musouReach * 1.4f - 0.2f && warfront <= musouReach * 1.4f + radius + 0.3f, $"farthest struck {warfront} m, Musou shape {musouReach} m x1.4 = {musouReach * 1.4f}");
    }

    // ---------------------------------------------------------------- level-up cards

    static readonly WarfrontCard[] AllStats = [WarfrontCard.AttackSpeed, WarfrontCard.MaxHp, WarfrontCard.Damage, WarfrontCard.CooldownReduction, WarfrontCard.MaxArmor];

    [Fact]
    public void An_offer_lists_all_five_stat_cards_and_the_Retainer_only_while_eligible()
    {
        for (int pick = 0; pick < 20; pick++)
        {
            var squad = WarfrontOffers.Roll(7, 42, pick, true);
            Assert.Equal(AllStats, squad);   // since 2026-11-23 no +1 Retainer card (owner card 2): the stats alone for everyone
            Assert.Equal(AllStats, WarfrontOffers.Roll(7, 42, pick, false));   // no squad (the Dreadnought, a full roster): the stats alone
        }
        Assert.Equal(AllStats.Length, WarfrontOffers.Roll(1, 1, 0, true).Length);
        Assert.False(WarfrontOffers.RetainerEligible(1, true));    // the Dreadnought has no squad
        Assert.False(WarfrontOffers.RetainerEligible(9, false));   // a full roster
        Assert.False(WarfrontOffers.RetainerEligible(0, false));   // and nobody else since 2026-11-23
    }

    [Fact]
    public void Only_human_players_and_their_retainers_get_the_bonus_not_the_allied_Marines()
    {
        var w = Ready(WarfrontWorld());
        Assert.True(w.WarfrontCleaveBonus(Human));
        var run = w.Tfw!;
        var retainer = run.RetainerOf(Human);
        if (retainer is not null) Assert.True(w.WarfrontCleaveBonus(retainer.Id), "a human's retainer");
        foreach (var ally in run.Pair) Assert.False(w.WarfrontCleaveBonus(ally.Id), "an allied AI Marine");
        Assert.False(w.WarfrontCleaveBonus(TfwContracts.PuppetIdLast), "an unregistered puppet id");
    }

    [Fact]
    public void The_query_slack_covers_the_largest_Ork_body()
    {
        var w = Ready(WarfrontWorld());
        float biggest = Enum.GetValues<MusouEnemyType>().Max(t => w.Musou!.Body(t).Radius);
        Assert.True(WarfrontBattleRules.CleaveBodySlack > biggest, $"slack {WarfrontBattleRules.CleaveBodySlack} against the largest body {biggest}");   // the Warboss, about 3.4 m
        Assert.True(WarfrontBattleRules.CleaveBodySlack >= 3.5f);
    }
}
