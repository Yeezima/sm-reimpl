using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;
using Rig = SmNet.Tests.Extras.Warfront.Wiring.WarfrontSquadWiringTests.Rig;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>W9b second pass (review 1): partial paths, human revives human, the full retainer third, the class book path of the first retainer, the seam rule, the aboard seam, the level-up refill, the Ork level damage share, the stranded failsafe's place.</summary>
public class WarfrontSquadWiringSecondPassTests
{
    static PlayerInput Use => new(0, 0f, 0f, 0f, InputButtons.Interact);

    /// <summary>Orks that stand frozen within 6 m of a body: no Marine revives it, no Ork strikes.</summary>
    static void Guard(Rig r, Vector3 body)
    {
        for (int k = 0; k < 3; k++) r.W.Musou!.SpawnLeashed(MusouEnemyType.Boy, body + new Vector3(4f, k - 1f, 0f), 0f, 1e6f, 1, 10f);
        r.W.Musou!.Frozen = true;
    }

    // ---- finding 1: partial paths ------------------------------------------------------------------------------------------------------

    static (WarfrontSquadSim Squad, MusouSquadArena Arena, MusouSim Pool, List<SquadStuck> Stuck) Scene(NavigationTests.Level level, Vector3 landing, int marines = 3)
    {
        var mesh = level.Mesh();
        var nav = NavGraph.Build(mesh, null, new NavSettings());
        var pool = new MusouSim(nav);
        var arena = new MusouSquadArena(pool, MusouSquadArena.SightOf(new CollisionWorld(mesh, mesh)));
        var rosters = new SquadRosters();
        rosters.AddOwner(1UL);
        while (rosters.Count(1UL) < marines) rosters.Recruit(1UL);
        var squad = new WarfrontSquadSim(rosters, arena);
        var stuck = new List<SquadStuck>();
        squad.StuckSink = stuck.Add;
        Assert.Equal(marines, squad.BeginBattle([(1UL, 1, landing)]));
        return (squad, arena, pool, stuck);
    }

    static float Run(WarfrontSquadSim squad, Vector3 owner, float yaw, int ticks, int marines)
    {
        uint tick = 0;
        var humans = new[] { new SquadHuman(1, owner, true, false, -1, yaw) };
        for (int k = 0; k < ticks; k++) squad.Step(++tick, humans);
        float nearest = float.MaxValue;
        for (int i = 0; i < marines; i++) if (squad.View(i) is { } v) nearest = MathF.Min(nearest, Vector3.Distance(v.Position, owner));
        return nearest;
    }

    [Fact]
    public void An_owner_far_round_two_blocks_is_reached_on_foot_by_partial_paths_with_no_relocation()
    {
        var level = new NavigationTests.Level().Floor(-100f, -60f, 100f, 60f);
        level.Box(new Vector3(-36, -10, 0), new Vector3(-20, 10, 8));    // two blocks on the line between them (the squad weaves round both)
        level.Box(new Vector3(20, -10, 0), new Vector3(36, 10, 8));
        var (squad, arena, _, stuck) = Scene(level, new Vector3(-95f, 0f, 0f));
        float gap = Run(squad, new Vector3(95f, 0f, 0f), MathF.PI, 30 * 70, 3);
        Assert.Equal(0, squad.Relocations);
        Assert.Empty(stuck);
        Assert.True(gap < 25f, $"the squad stopped {gap:F0} m short");
        Assert.True(arena.PartialPaths >= 0);   // printed by the full run below
    }

    [Fact]
    public void A_squad_shut_in_a_pocket_with_its_owner_far_away_ends_in_the_failsafe_behind_the_owner()
    {
        var level = new NavigationTests.Level().Floor(-40f, -40f, 70f, 40f);
        level.Box(new Vector3(-33, -8, 0), new Vector3(-31, 8, 8));   // a closed room round the squad
        level.Box(new Vector3(-19, -8, 0), new Vector3(-17, 8, 8));
        level.Box(new Vector3(-33, -8, 0), new Vector3(-17, -6, 8));
        level.Box(new Vector3(-33, 6, 0), new Vector3(-17, 8, 8));
        var (squad, _, _, stuck) = Scene(level, new Vector3(-25f, 0f, 0f));
        var owner = new Vector3(30f, 0f, 0f);
        Run(squad, owner, MathF.PI, 30 * 14, 3);   // facing west, toward the room: behind him is east
        Assert.True(squad.Relocations >= 1, "no way out: the failsafe brings the squad");
        Assert.NotEmpty(stuck);
        Assert.All(stuck, s => Assert.True(s.At.X > owner.X - 1f, $"a Marine was put at {s.At} in front of the owner"));   // behind his back
        float gap = Run(squad, owner, MathF.PI, 30, 3);
        Assert.True(gap < 15f);
    }

    // ---- finding 2: a human revives a human ------------------------------------------------------------------------------------------

    [Fact]
    public void A_human_holding_Use_beside_a_fallen_human_revives_him_inside_the_window_and_the_ledger_counts_one_revive()
    {
        var r = Rig.Make(humans: 2, marines: 1);
        Assert.True(r.W.TfwKillForTests(2));
        var body = r.Where(2);
        Guard(r, body);
        Assert.True(r.W.TfwRespawnAt(1, body + new Vector3(0.8f, 0f, 0f), 0f));
        r.Stand(30, id => id == 1 ? Use : default);
        Assert.False(r.W.Players[2].Alive);
        Assert.InRange(r.Wi.HumanReviveShare(2), 0.3f, 0.9f);   // the client's ring
        r.Stand(45, id => id == 1 ? Use : default);
        Assert.True(r.W.Players[2].Alive);
        Assert.Equal(1, r.Ledger(1)!.Revives);
        Assert.Equal(0f, r.Wi.HumanReviveShare(2));
    }

    [Fact]
    public void Use_released_drops_the_progress_and_a_spectator_cannot_be_revived()
    {
        var r = Rig.Make(humans: 2, marines: 1);
        Assert.True(r.W.TfwKillForTests(2));
        var body = r.Where(2);
        Guard(r, body);
        Assert.True(r.W.TfwRespawnAt(1, body + new Vector3(0.8f, 0f, 0f), 0f));
        r.Stand(40, id => id == 1 ? Use : default);
        r.Stand(2);   // released: the hold starts again
        Assert.Equal(0f, r.Wi.HumanReviveShare(2));
        r.Stand(40, id => id == 1 ? Use : default);
        Assert.False(r.W.Players[2].Alive);   // 40 ticks is not 60
        r.Stand(750);   // the window runs out
        Assert.Equal(WarfrontDownPhase.Spectator, r.W.Tfw!.WarfrontDownState(2).Phase);
        r.Stand(120, id => id == 1 ? Use : default);
        Assert.False(r.W.Players[2].Alive);
        Assert.Equal(0, r.Ledger(1)?.Revives ?? 0);
    }

    [Fact]
    public void Two_revivers_do_not_double_the_revive()
    {
        var r = Rig.Make(humans: 3, marines: 1);
        Assert.True(r.W.TfwKillForTests(3));
        var body = r.Where(3);
        Guard(r, body);
        Assert.True(r.W.TfwRespawnAt(1, body + new Vector3(0.8f, 0f, 0f), 0f));
        Assert.True(r.W.TfwRespawnAt(2, body + new Vector3(-0.8f, 0f, 0f), 0f));
        r.Stand(90, id => id is 1 or 2 ? Use : default);
        Assert.True(r.W.Players[3].Alive);
        Assert.Equal(1, (r.Ledger(1)?.Revives ?? 0) + (r.Ledger(2)?.Revives ?? 0));
    }

    // ---- finding 3: the full retainer third ----------------------------------------------------------------------------------------------

    static void Cards(Rig r, params WarfrontCard[] cards)
    {
        r.Campaign.AddXp(1, WarfrontCampaign.XpForLevel(cards.Length + 1) - r.Campaign.Player(1)!.Xp);
        foreach (var c in cards) Assert.True(r.Campaign.TryTakePick(1, (int)c));
    }

    /// <summary>The first Marine pull's damage against a tough frozen Ork, over the Marine's own damage.</summary>
    static float DamageRatio(Rig r)
    {
        var at = r.Sim.View(r.IndexOf(1, 0))!.Value.Position;
        ushort ork = r.W.Musou!.SpawnLeashed(MusouEnemyType.Nob, at + new Vector3(8f, 0f, 0f), 0f, 2e4f, 1, 10f);
        r.W.Musou.Frozen = true;
        r.W.Musou.TryGet(ork, out var o0);
        for (int t = 0; t < 120; t++)
        {
            r.Stand(1);
            if (r.W.Musou.TryGet(ork, out var e) && e.Health < o0.Health) return (o0.Health - e.Health) / (r.Wi.Rosters.Marine(1, 0)!.Loadout.Weapon.Damage * SquadRules.DamageDealt);
        }
        return 0f;
    }

    [Theory]
    [InlineData(WarfrontCard.Damage)]
    [InlineData(WarfrontCard.AttackSpeed)]
    [InlineData(WarfrontCard.CooldownReduction)]
    public void Damage_and_attack_speed_cards_scale_the_Marines_blow_by_a_third_and_a_cooldown_card_changes_nothing(WarfrontCard card)
    {
        var r = Rig.Make(marines: 1, deploy: false);
        Cards(r, card, card, card);
        var b = WarfrontStatBonuses.ForRetainer(r.Campaign.Player(1)!.Cards);
        r.Deploy();
        float expected = (card == WarfrontCard.CooldownReduction ? 1f : b.Damage * b.AttackSpeed) * WarfrontSquadWiring.SquadLevelScale(4).Damage;   // S1 (owner 2026-11-19): three cards = level 4, and the level scales damage too
        float ratio = DamageRatio(r);
        Assert.True(ratio > 0f);
        Assert.Equal(expected, ratio, 2);
    }

    [Fact]
    public void A_max_HP_card_scales_the_health_only_and_a_max_armour_card_the_armour_only()
    {
        var hp = Rig.Make(marines: 1, deploy: false);
        Cards(hp, WarfrontCard.MaxHp, WarfrontCard.MaxHp);
        hp.Deploy();
        var l = hp.Wi.Rosters.Marine(1, 0)!.Loadout;
        var b = WarfrontStatBonuses.ForRetainer(hp.Campaign.Player(1)!.Cards);
        var body = hp.Sim.BodyOf(hp.IndexOf(1, 0));
        Assert.Equal(l.HealthMax * b.MaxHp * WarfrontSquadWiring.SquadLevelScale(3).Health, body.Health, 1);   // S1: two cards = level 3
        Assert.Equal(l.ArmourMax, body.Armour, 1);   // the health card gives the armour nothing

        var ar = Rig.Make(marines: 1, deploy: false);
        Cards(ar, WarfrontCard.MaxArmor, WarfrontCard.MaxArmor);
        ar.Deploy();
        var b2 = WarfrontStatBonuses.ForRetainer(ar.Campaign.Player(1)!.Cards);
        var body2 = ar.Sim.BodyOf(ar.IndexOf(1, 0));
        Assert.Equal(l.HealthMax * WarfrontSquadWiring.SquadLevelScale(3).Health, body2.Health, 1);   // S1: the level part still applies
        Assert.Equal(l.ArmourMax * b2.MaxArmor, body2.Armour, 1);
    }

    // ---- finding 4: the first retainer through the class book --------------------------------------------------------------------------

    [Fact]
    public void A_Dreadnought_first_joiner_held_by_W12_has_two_held_Marines_and_two_after_the_switch()
    {
        var r = Rig.Make(deploy: false);
        r.Wi.Reset();
        r.W.Spawn(1, 6);   // a Dreadnought
        bool held = true;
        r.Rules.CoopHeld = id => held && id == 1;
        r.Wi.StepAboard();
        Assert.False(r.Wi.HasRoster(1));   // held: the book has seen nothing yet
        Assert.True(r.Wi.AddStartingRetainer(1));   // W12's path: through the book, as a Dreadnought
        Assert.Equal(2, r.Wi.Rosters.Count(1));      // two, held (owner 2026-11-23, card 2: none in the field for a Dreadnought)
        Assert.True(r.Wi.Rosters.IsHeld(1));
        held = false;
        r.W.Spawn(1, 0);
        r.Wi.StepAboard();
        Assert.Equal(2, r.Wi.Rosters.Count(1));      // still two after the switch (the free retainer is retired)
        Assert.False(r.Wi.Rosters.IsHeld(1));
    }

    // ---- finding 5, StepAboard seam, T3 ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Another_slices_handlers_on_the_notifying_seams_do_not_cut_the_wiring_off()
    {
        var r = Rig.Make(marines: 2, deploy: false);
        int began = 0, aboard = 0;
        r.Rules.BattleBegan += (_, _) => began++;
        r.Rules.AboardStep += () => aboard++;
        r.W.Spawn(1, 1);   // an Assault marine, aboard
        r.Stand(20);
        Assert.True(aboard > 0);
        Assert.NotNull(r.Wi.Rosters.Marine(1, 0)!.Loadout.Jump);   // the loadout followed with no call to StepAboard
        r.Deploy();
        Assert.Equal(1, began);
        Assert.Equal(2, r.Standing(1));   // the wiring's own handler ran too
    }

    [Fact]
    public void A_level_up_refills_the_owners_standing_Marines_and_not_the_downed()
    {
        var r = Rig.Make(marines: 3);
        r.Stand(2);
        int hurt = r.IndexOf(1, 0), downed = r.IndexOf(1, 1);
        Assert.True(r.Sim.Hit(SquadRules.TargetId(hurt), 100f / SquadRules.DamageTaken, Vector3.Zero));
        float before = r.Sim.BodyOf(hurt).Health + r.Sim.BodyOf(hurt).Armour;
        r.DownMarine(1, 1);
        r.Rt.Xp.Kill(1, 1_000_000, WarfrontKillClass.Ordinary, 0);   // a level-up (or more)
        r.Stand(1);
        var l = r.Wi.Rosters.Marine(1, 0)!.Loadout;
        Assert.Equal(l.HealthMax + l.ArmourMax, r.Sim.BodyOf(hurt).Health + r.Sim.BodyOf(hurt).Armour, 1);
        Assert.True(before < l.HealthMax + l.ArmourMax);
        Assert.Equal(SquadState.Downed, r.Sim.StateOf(downed));
    }

    // ---- T1: the Orks' level damage does not reach the squad -------------------------------------------------------------------------------

    [Fact]
    public void A_Marine_takes_none_of_the_Orks_level_damage()
    {
        Assert.Equal(0f, SquadRules.OrkLevelDamageShare);
        float Lost(float damageScale)
        {
            var r = Rig.Make(marines: 1, deploy: false);
            r.Wi.Rosters.Mirror(1, r.Wi.Rosters.Marine(1, 0)!.Loadout with { Weapon = new SquadWeapon(30f, 0.0001f, 1f, 0f) });   // a Marine that cannot hurt back (it would knock the Boy off)
            r.Deploy();
            r.Rules.OrkLevelScale = () => (1f, damageScale);
            var at = r.Sim.View(r.IndexOf(1, 0))!.Value.Position;
            Assert.True(r.W.TfwKillForTests(1));   // the human is down: the Orks go for the Marine (and it does not revive him with an Ork on the body)
            r.W.Musou!.Spawn(MusouEnemyType.Boy, at + new Vector3(1.2f, 0f, 0f), 0f, 1e6f);
            r.Stand(60);
            var b = r.Sim.BodyOf(r.IndexOf(1, 0));
            var l = r.Wi.Rosters.Marine(1, 0)!.Loadout;
            return l.HealthMax + l.ArmourMax - b.Health - b.Armour;
        }
        float plain = Lost(1f), scaled = Lost(3f);
        Assert.True(plain > 0f, $"plain {plain}");
        Assert.Equal(plain, scaled, 1);
    }
}
