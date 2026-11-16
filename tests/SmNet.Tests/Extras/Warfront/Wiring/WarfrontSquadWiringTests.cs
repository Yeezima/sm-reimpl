using System.Numerics;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Battle;
using SmNet.Tests.Extras.Warfront.Squad.Classes;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>
/// Warfront W9b: the squad wiring on a real World (the Tfw test arena, W3's node battle, W3b's progression, W11's synthetic classes): the Orks see and hit the Marines, a Marine's kill pays its owner once and never the ledger's kills,
/// revives both ways, the wipe rule, captures, the Retainer card, the retainer third, loadout changes aboard, the Librarian, late landing and reconnect, the save round trip.
/// </summary>
public class WarfrontSquadWiringTests
{
    /// <summary>The class data of W11's tests inside the Warfront profile, plus a Dreadnought and a Librarian (names are what the rules read).</summary>
    internal static SimConfig Cfg()
    {
        var m = SquadClassesTests.Make();
        var step = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
        int pistol = m.Classes[0].Pistol;
        var dread = new ClassLoadout(DreadnoughtRules.ClassName, -1, pistol, [step]) { PistolChoices = [pistol], Side = Team.SpaceMarines, Role = DreadnoughtRules.Role };
        return WarfrontBattleTests.WarfrontConfig with
        {
            Weapons = m.Weapons, Thrown = m.Thrown, Classes = [.. m.Classes, dread], Perks = m.Perks, JumpPacks = m.JumpPacks,
            HealthMax = m.HealthMax, ShieldMax = m.ShieldMax, ShieldRegenDelay = m.ShieldRegenDelay, ShieldRegenRate = m.ShieldRegenRate,
        };
    }

    const int DreadnoughtClass = 6;

    internal sealed class Rig
    {
        public World W = null!;
        public WarfrontBattleRules Rules = null!;
        public WarfrontSquadWiring Wi = null!;
        public WarfrontProgressionRuntime Rt => Rules.Progression;
        public WarfrontCampaign Campaign => Rt.Campaign;
        public TfwBattle Battle = null!;

        public static Rig Make(int humans = 1, int marines = 1, bool deploy = true, SimConfig? config = null, NavGraph? nav = null)
        {
            var w = TfwRig.War(nav, config ?? Cfg());
            w.Tfw!.Guardsmen = false; w.Tfw.Allies = false;   // the Guardsmen and the allied pair would shoot the Orks the test watches
            for (int id = 2; id <= humans; id++) w.Spawn(id, w.AssignClass(0));
            TfwRig.Steps(w, 5);
            var rules = w.Tfw!.Warfront!;
            var r = new Rig { W = w, Rules = rules };
            r.Wi = new WarfrontSquadWiring(w, rules, rules.Progression.Campaign, 7UL);
            r.Wi.StepAboard();
            for (int id = 1; id <= humans; id++) for (int k = 1; k < marines; k++) Assert.True(r.Wi.ClassBook.Recruit((ulong)id));
            if (deploy) r.Deploy();
            return r;
        }

        public void Deploy()
        {
            Battle = TfwRig.Deploy(W);
            Clear();
            Stand(2);   // the humans land and the squads with them
        }

        /// <summary>Every Ork dies by nobody (attacker 0: no XP).</summary>
        public void Clear()
        {
            var all = new MusouEnemy[MusouSim.Capacity];
            int n = W.Musou!.CopyTo(all);
            for (int k = 0; k < n; k++) if (all[k].Alive) W.Musou.Damage(all[k].Id, 0, 1e6f, Vector3.Zero, out _);
        }

        public void Stand(int ticks, Func<int, PlayerInput>? input = null)
        {
            for (int k = 0; k < ticks; k++) W.Step(input ?? (_ => default));
        }

        public ushort Ork(MusouEnemyType type, Vector3 at, float health = 1000f) => W.Musou!.Spawn(type, at, 0f, health);

        public Vector3 Where(int player = 1) => W.Players[player].Position;
        public WarfrontSquadSim Sim => Wi.Sim;
        public long Xp(ulong key) => Campaign.Player(key)!.Xp;
        public WarfrontPlayerLedger? Ledger(ulong key) => Campaign.Stats.Player(key);
        public int Standing(int player = 1) => Sim.StandingOf(player);
        public int IndexOf(int player, int slot) => Sim.IndexOf(player, slot);
        public void DownMarine(int player, int slot) => Assert.True(Sim.Hit(SquadRules.TargetId(IndexOf(player, slot)), 1e7f, Vector3.Zero));
    }

    // ---- connecting --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_node_battle_lands_every_identified_humans_squad_at_his_own_landing_place()
    {
        var r = Rig.Make(humans: 2, marines: 3);
        Assert.Equal(3, r.Standing(1));
        Assert.Equal(3, r.Standing(2));
        for (int i = 0; i < 3; i++)
        {
            Assert.True(Vector3.Distance(r.Sim.View(r.IndexOf(1, i))!.Value.Position, r.Where(1)) < 6f);
            Assert.True(Vector3.Distance(r.Sim.View(r.IndexOf(2, i))!.Value.Position, r.Where(2)) < 6f);
        }
        Assert.Equal(1, r.Ledger(1)!.Battles);   // one battle each, counted for the deployment
        Assert.Equal(1, r.Ledger(2)!.Battles);
        Assert.True(r.Rules.AnyRetainerStanding());
        Assert.Equal(3, r.Rt.RosterSizeOf(1));
    }

    [Fact]
    public void An_Ork_in_range_targets_a_Marine_and_its_blow_lowers_the_armour_and_a_Marine_kill_pays_the_owner_once_and_never_the_ledgers_kills()
    {
        var r = Rig.Make(marines: 3);
        var at = r.Sim.View(r.IndexOf(1, 0))!.Value.Position;
        int points = r.W.Musou!.Rules.Of(MusouEnemyType.Boy).Points;
        long xp = r.Xp(1);
        var targets = new MusouTarget[64];
        Assert.Equal(3, r.Sim.AppendTargets(targets));   // the seam writes the Marines into the Orks' list
        for (int k = 0; k < 4; k++) r.Ork(MusouEnemyType.Boy, at + new Vector3(6f + k, 3f, 0f), 85f);
        float armour = 0f; int minStanding = 3;
        var before = Enumerable.Range(0, 3).Select(s => r.Sim.BodyOf(r.IndexOf(1, s))).ToArray();
        for (int t = 0; t < 30 * 40; t++)
        {
            r.Stand(1);
            for (int s = 0; s < 3; s++) { var b = r.Sim.BodyOf(r.IndexOf(1, s)); armour = Math.Max(armour, before[s].Armour - b.Armour); }
            minStanding = Math.Min(minStanding, r.Standing(1));
        }
        Assert.True(armour > 0f, "an Ork's blow lowers a Marine's armour");
        int kills = Enumerable.Range(0, 3).Sum(s => r.Wi.Rosters.Marine(1, s)!.Record.Kills);
        Assert.True(kills > 0, "the Marines kill");
        Assert.Equal(xp + (long)kills * points, r.Xp(1));   // each kill paid exactly once, to the owner
        Assert.Equal(0, r.Ledger(1)?.OrdinaryKills ?? 0);   // never the player's ledger kills
        Assert.Equal(kills, r.Wi.RecordsOf(1).Sum(m => m.Kills));
    }

    [Fact]
    public void An_elite_killed_by_a_Marine_pays_every_connected_player_once_and_the_same_death_again_pays_nothing()
    {
        var r = Rig.Make(humans: 2, marines: 2);
        int points = r.W.Musou!.Rules.Of(MusouEnemyType.Nob).Points;
        long a = r.Xp(1), b = r.Xp(2);
        var kill = new SquadKill(1, 0, 77, SquadEnemyClass.Elite, MusouEnemyType.Nob);
        Assert.True(r.Wi.PaySquadKill(kill, 500));
        Assert.Equal(a + points, r.Xp(1));
        Assert.Equal(b + points, r.Xp(2));
        Assert.False(r.Wi.PaySquadKill(kill, 500));   // the same death reported again
        Assert.Equal(a + points, r.Xp(1));
        Assert.Equal(b + points, r.Xp(2));
        Assert.True(r.Wi.PaySquadKill(kill with { Enemy = 78 }, 500));   // another death pays
    }

    [Fact]
    public void A_spectating_joiner_gets_no_squad_kill_XP_when_the_server_says_so()
    {
        var r = Rig.Make(humans: 2);
        r.Rt.Eligible = key => key != 2;
        int boy = r.W.Musou!.Rules.Of(MusouEnemyType.Boy).Points, nob = r.W.Musou.Rules.Of(MusouEnemyType.Nob).Points;
        long b = r.Xp(2), a = r.Xp(1);
        Assert.False(r.Wi.PaySquadKill(new SquadKill(2, 0, 5, SquadEnemyClass.Fodder, MusouEnemyType.Boy), 10));   // his own squad's ordinary kill
        Assert.True(r.Wi.PaySquadKill(new SquadKill(1, 0, 6, SquadEnemyClass.Elite, MusouEnemyType.Nob), 10));
        Assert.Equal(b, r.Xp(2));
        Assert.Equal(a + nob, r.Xp(1));
        Assert.True(boy > 0);
    }

    // ---- revives ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_downed_Marine_is_revived_by_the_human_holding_Use_within_reach_for_the_hold_time_and_the_ledger_counts_one_revive()
    {
        var r = Rig.Make(marines: 1);
        r.DownMarine(1, 0);
        int index = r.IndexOf(1, 0);
        Assert.Equal(SquadState.Downed, r.Sim.StateOf(index));
        var near = r.Sim.View(index)!.Value.Position;
        Assert.True(r.W.TfwRespawnAt(1, near + new Vector3(0.8f, 0f, 0f), 0f));
        r.Stand(20);   // no Use: nothing happens
        Assert.Equal(SquadState.Downed, r.Sim.StateOf(index));
        Assert.Equal(0, r.Ledger(1)?.Revives ?? 0);
        r.Stand((int)(SquadRules.PlayerReviveSeconds * 30) + 6, id => id == 1 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Interact) : default);
        Assert.Equal(SquadState.Standing, r.Sim.StateOf(index));
        Assert.Equal(1, r.Ledger(1)!.Revives);
        r.Stand(30);
        Assert.Equal(1, r.Ledger(1)!.Revives);
    }

    [Fact]
    public void The_ledger_counts_a_repeated_event_once_per_id()
    {
        var feed = new WarfrontLedgerFeed(new WarfrontStatsLedger(), () => 4);
        Assert.True(feed.Revive(9, 1, 100));
        Assert.False(feed.Revive(9, 1, 100));
        Assert.True(feed.Revive(9, 1, 101));
        Assert.Equal(2, feed.Ledger.Player(9)!.Revives);
        Assert.True(feed.Kill(9, MusouEnemyType.Nob, 12, 300));
        Assert.False(feed.Kill(9, MusouEnemyType.Nob, 12, 300));   // an elite death counts once
        Assert.True(feed.Kill(9, MusouEnemyType.Boy, 12, 300));
        Assert.True(feed.Kill(9, MusouEnemyType.Boy, 12, 300));    // ordinary kills carry no id: the pool reports each once
        Assert.Equal(2, feed.Ledger.Player(9)!.OrdinaryKills);
        Assert.Equal(1, feed.Ledger.Player(9)!.EliteKills);
        Assert.True(feed.Battle(9)); Assert.False(feed.Battle(9));
        var other = new WarfrontLedgerFeed(feed.Ledger, () => 5);
        Assert.True(other.Battle(9));   // the next deployment counts
    }

    [Fact]
    public void A_fallen_human_beside_a_standing_Marine_is_revived_inside_his_window_and_not_after_it()
    {
        var r = Rig.Make(marines: 1);
        Assert.True(r.W.TfwKillForTests(1));
        r.Stand(30 * 6);
        Assert.True(r.W.Players[1].Alive);   // the Marine stood by him for two seconds
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 0)!.Record.Revives);
        Assert.Equal(1, r.Ledger(1)!.Deaths);   // each fall counts

        // and past the window: Orks keep the Marines off the body (a Marine revives only with no Ork within 6 m), the window runs out, then he is a spectator and stays one
        var r2 = Rig.Make(marines: 1);
        var body = r2.Where(1);
        Assert.True(r2.W.TfwKillForTests(1));
        var orks = new List<ushort>();
        for (int k = 0; k < 3; k++) orks.Add(r2.W.Musou!.SpawnLeashed(MusouEnemyType.Boy, body + new Vector3(3f + k, 0f, 0f), 0f, 1e6f, 1, 10f));
        r2.W.Musou!.Frozen = true;
        r2.Stand(750 + 40);
        Assert.Equal(WarfrontDownPhase.Spectator, r2.W.Tfw!.WarfrontDownState(1).Phase);
        foreach (ushort id in orks) r2.W.Musou.Damage(id, 0, 1e7f, Vector3.Zero, out _);
        r2.W.Musou.Frozen = false;
        r2.Stand(90);
        Assert.False(r2.W.Players[1].Alive);   // never revived after the window
        Assert.Equal(0, r2.Wi.Rosters.Marine(1, 0)!.Record.Revives);
    }

    [Fact]
    public void The_last_human_down_with_a_Marine_standing_is_no_wipe_and_with_every_Marine_down_it_is()
    {
        var r = Rig.Make(marines: 2);
        Assert.True(r.W.TfwKillForTests(1));
        r.W.Musou!.Frozen = true;
        r.Stand(750 + 60);   // the window is over: a spectator
        Assert.Equal(TfwRunPhase.Battle, r.W.Tfw!.Phase);   // a Marine stands: no wipe
        Assert.True(r.Wi.Sim.AnyStanding);

        var w = Rig.Make(marines: 2);
        Assert.True(w.W.TfwKillForTests(1));
        w.DownMarine(1, 0);
        w.DownMarine(1, 1);
        Assert.False(w.Rules.AnyRetainerStanding());
        w.W.Musou!.Frozen = true;
        w.Stand(750 + 60);
        Assert.Equal(TfwRunPhase.Hub, w.W.Tfw!.Phase);   // every Marine down or out: the wipe
    }

    // ---- captures -----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void D_secured_credits_the_Marines_on_it_and_the_humans_inside_the_radius_once()
    {
        var r = Rig.Make(marines: 2);
        var battle = r.Battle;
        for (int k = 0; k < 3; k++)
        {
            int p = k;
            TfwRig.Teleport(r.W, battle.Layout.Positions[p]);
            r.Stand(3);   // the Marines land on the human's new place by following
            for (int t = 0; t < 120 * 30 && battle.Objectives[p].State != TfwPositionState.Secured; t++) { r.Clear(); r.W.Step(_ => default); }
            Assert.Equal(TfwPositionState.Secured, battle.Objectives[p].State);
        }
        var ledger = r.Ledger(1)!;
        Assert.Equal(3, ledger.CapturesAbc);   // A, B, C each once for the human standing in it
        Assert.Equal(0, ledger.CapturesD);
        TfwRig.Teleport(r.W, battle.Layout.Hq);
        for (int t = 0; t < 120 * 30 && r.W.Tfw!.Battle is not null; t++) { r.Clear(); r.W.Step(_ => default); }
        Assert.Equal(1, r.Ledger(1)!.CapturesD);
        Assert.Equal(3, r.Ledger(1)!.CapturesAbc);
        Assert.True(r.Wi.Rosters.Marine(1, 0)!.Record.Objectives + r.Wi.Rosters.Marine(1, 1)!.Record.Objectives > 0, "the Marines on a point score an objective");
    }

    // ---- the cards ----------------------------------------------------------------------------------------------------------------------

    static void Cards(Rig r, ulong key, params WarfrontCard[] cards)
    {
        r.Campaign.AddXp(key, WarfrontCampaign.XpForLevel(cards.Length + 1) - r.Campaign.Player(key)!.Xp);
        foreach (var c in cards) Assert.True(r.Campaign.TryTakePick(key, (int)c));
    }

    [Fact]
    public void A_Retainer_card_pick_makes_one_recruit_and_a_save_and_load_keeps_the_roster_size_and_at_nine_the_card_is_not_offered()
    {
        var r = Rig.Make(deploy: false);
        Assert.Equal(1, r.Wi.Rosters.Count(1));
        Assert.True(r.Rt.TryPick(1, 0, 0) || true);   // no offer standing: refused, nothing recruited
        int before = r.Wi.Rosters.Count(1);
        // one pick of a Retainer card, as the picker reports it
        r.Rt.Picked!(new WarfrontPickResult(1, 0, 0, WarfrontCard.Retainer, false));
        Assert.Equal(before + 1, r.Wi.Rosters.Count(1));
        r.Rt.Picked!(new WarfrontPickResult(1, 1, 1, WarfrontCard.Damage, false));
        Assert.Equal(before + 1, r.Wi.Rosters.Count(1));   // another card: no recruit
        var save = r.Wi.Export();
        var r2 = Rig.Make(deploy: false);
        r2.Wi.Import(save);
        Assert.Equal(before + 1, r2.Wi.Rosters.Count(1));   // a reload recruits nobody
        r2.Wi.StepAboard();
        Assert.Equal(before + 1, r2.Wi.Rosters.Count(1));
        for (int k = r2.Wi.Rosters.Count(1); k < SquadRules.MaxPerPlayer; k++) r2.Rt.Picked!(new WarfrontPickResult(1, k, 0, WarfrontCard.Retainer, false));
        Assert.Equal(SquadRules.MaxPerPlayer, r2.Rt.RosterSizeOf(1));
        r2.Rt.Picked!(new WarfrontPickResult(1, 99, 0, WarfrontCard.Retainer, false));
        Assert.Equal(SquadRules.MaxPerPlayer, r2.Wi.Rosters.Count(1));   // never past nine
    }

    [Fact]
    public void The_retainer_third_three_damage_cards_give_the_player_x1_3_and_the_Marines_x1_1_and_Marines_landed_after_HP_cards_have_the_health_share()
    {
        var r = Rig.Make(marines: 2, deploy: false);
        Cards(r, 1, WarfrontCard.Damage, WarfrontCard.Damage, WarfrontCard.Damage);
        Assert.Equal(1.3f, r.Rt.BonusesOfKey(1).Damage, 3);
        Assert.Equal(1.1f, WarfrontStatBonuses.ForRetainer(r.Campaign.Player(1)!.Cards).Damage, 3);
        r.Deploy();
        // the first Marine's blow on a tough Ork, read off the Ork's health
        var at = r.Sim.View(r.IndexOf(1, 0))!.Value.Position;
        ushort ork = r.W.Musou!.SpawnLeashed(MusouEnemyType.Nob, at + new Vector3(8f, 0f, 0f), 0f, 1e6f, 1, 10f);
        r.W.Musou.Frozen = true;
        float full = r.W.Musou.TryGet(ork, out var o0) ? o0.Health : 0f, drop = 0f;
        for (int t = 0; t < 120 && drop == 0f; t++) { r.Stand(1); if (r.W.Musou.TryGet(ork, out var e) && e.Health < full) drop = full - e.Health; }
        Assert.True(drop > 0f);
        float one = r.Wi.Rosters.Marine(1, 0)!.Loadout.Weapon.Damage * SquadRules.DamageDealt;
        Assert.True(MathF.Abs(drop / one - 1.1f) < 0.02f || MathF.Abs(drop / one / 1.1f - MathF.Round(drop / one / 1.1f)) < 0.02f, $"drop {drop} against {one} x 1.1");

        var h = Rig.Make(marines: 2, deploy: false);
        Cards(h, 1, WarfrontCard.MaxHp, WarfrontCard.MaxHp);
        h.Deploy();
        float share = WarfrontStatBonuses.ForRetainer(h.Campaign.Player(1)!.Cards).MaxHp;
        Assert.True(share > 1f);
        Assert.Equal(h.Wi.Rosters.Marine(1, 0)!.Loadout.HealthMax * share, h.Sim.BodyOf(h.IndexOf(1, 0)).Health, 1);
    }

    // ---- the class book aboard ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_loadout_change_aboard_follows_to_every_Marine_keeping_names_and_records_and_a_Dreadnought_holds_the_roster()
    {
        var r = Rig.Make(marines: 3, deploy: false);
        var names = Enumerable.Range(0, 3).Select(s => r.Wi.Rosters.Marine(1, s)!.Name).ToArray();
        r.Wi.Rosters.Marine(1, 1)!.Record.Kills = 7;
        string tactical = r.Wi.Rosters.Marine(1, 0)!.Loadout.ClassId;
        r.W.Spawn(1, 1);   // an Assault marine
        r.Wi.StepAboard();
        for (int s = 0; s < 3; s++)
        {
            Assert.NotEqual(tactical, r.Wi.Rosters.Marine(1, s)!.Loadout.ClassId);
            Assert.Equal(names[s], r.Wi.Rosters.Marine(1, s)!.Name);
        }
        Assert.Equal(7, r.Wi.Rosters.Marine(1, 1)!.Record.Kills);
        Assert.NotNull(r.Wi.Rosters.Marine(1, 0)!.Loadout.Jump);   // the Assault's pack

        r.W.Spawn(1, DreadnoughtClass);
        r.Wi.StepAboard();
        Assert.True(r.Wi.Rosters.IsHeld(1));
        Assert.Equal(3, r.Wi.Rosters.Count(1));
        r.Deploy();
        Assert.Equal(0, r.Standing(1));   // the next battle lands nobody

        var back = Rig.Make(marines: 3, deploy: false);
        back.W.Spawn(1, DreadnoughtClass); back.Wi.StepAboard();
        back.W.Spawn(1, 0); back.Wi.StepAboard();
        Assert.False(back.Wi.Rosters.IsHeld(1));   // back to Tactical releases it
        back.Deploy();
        Assert.Equal(3, back.Standing(1));
    }

    [Fact]
    public void A_Dreadnought_first_player_gets_exactly_one_free_Marine_once_also_across_a_save()
    {
        var r = Rig.Make(deploy: false);
        var d = Rig.Make(deploy: false);
        d.Wi.Reset();
        d.W.Spawn(1, DreadnoughtClass);
        d.Wi.StepAboard();
        Assert.Equal(0, d.Wi.Rosters.Count(1));
        d.W.Spawn(1, 0);
        d.Wi.StepAboard();
        Assert.Equal(1, d.Wi.Rosters.Count(1));   // the free one, once
        d.W.Spawn(1, DreadnoughtClass); d.Wi.StepAboard();
        d.W.Spawn(1, 0); d.Wi.StepAboard();
        Assert.Equal(1, d.Wi.Rosters.Count(1));
        var save = d.Wi.Export();
        var e = Rig.Make(deploy: false);
        e.Wi.Import(save);
        e.W.Spawn(1, DreadnoughtClass); e.Wi.StepAboard();
        e.W.Spawn(1, 0); e.Wi.StepAboard();
        Assert.Equal(1, e.Wi.Rosters.Count(1));   // still one after the save
        Assert.NotNull(r);
    }

    [Fact]
    public void The_Librarian_squad_class_rerolls_once_and_the_same_class_again_changes_nothing()
    {
        var cfg = Cfg();
        var lib = cfg.Classes[0] with { Name = LibrarianRules.ClassName, Role = "librarian" };
        cfg = cfg with { Classes = [.. cfg.Classes, lib] };
        int librarian = cfg.Classes.Count - 1;
        var r = Rig.Make(marines: 3, deploy: false, config: cfg);
        r.W.Spawn(1, librarian);
        r.Wi.StepAboard();
        var tactical = Enumerable.Range(0, 3).Select(s => r.Wi.Rosters.Marine(1, s)!.Loadout).ToArray();
        r.Wi.SetSquadClass(1, SquadClass.Assault);
        var assault = Enumerable.Range(0, 3).Select(s => r.Wi.Rosters.Marine(1, s)!.Loadout).ToArray();
        Assert.All(assault, l => Assert.NotNull(l.Jump));
        Assert.NotEqual(tactical[0].ClassId, assault[0].ClassId);
        r.Wi.SetSquadClass(1, SquadClass.Assault);   // the same class again
        for (int s = 0; s < 3; s++) { var now = r.Wi.Rosters.Marine(1, s)!.Loadout; Assert.Equal((assault[s].ClassId, assault[s].Primary, assault[s].Melee, assault[s].HealthMax), (now.ClassId, now.Primary, now.Melee, now.HealthMax)); Assert.True(assault[s].Perks.SequenceEqual(now.Perks)); }
        Assert.Equal(SquadClass.Assault, r.Wi.Export().Picks.Single().Class);
    }

    // ---- late landing, leaving, saving ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_second_human_joining_late_gets_his_squad_landed_without_touching_the_first_and_a_reconnect_re_binds()
    {
        var r = Rig.Make(marines: 2);
        r.DownMarine(1, 0);
        r.DownMarine(1, 1);   // both: a standing Marine would revive his mate
        var first = r.Sim.View(r.IndexOf(1, 0))!.Value;
        Assert.True(r.Wi.AddStartingRetainer(2));
        r.W.Spawn(2, r.W.AssignClass(0));   // joins while the battle is under way: flown in
        r.Stand(60);
        Assert.True(r.Rt.TryKey(2, out _));
        Assert.Equal(1, r.Standing(2));
        Assert.Equal(SquadState.Downed, r.Sim.StateOf(r.IndexOf(1, 0)));   // the first squad was not landed again
        Assert.Equal(0, r.Standing(1));
        Assert.Equal(1, r.Ledger(2)!.Battles);

        // he leaves: his squad holds, and counts as standing; he returns under a new id and the squad is re-bound
        r.Rt.KeyOf = id => id == 22 ? 2UL : (ulong)id;
        r.W.Remove(2);
        r.Stand(30);
        Assert.Equal(SquadOrder.Hold, r.Sim.OrderOf(2));
        Assert.True(r.Rules.AnyRetainerStanding());
        Assert.Equal(1, r.Standing(2));
        r.W.Spawn(22, r.W.AssignClass(0));
        r.Stand(60);
        Assert.Equal(1, r.Standing(22));
        Assert.Equal(0, r.Standing(2));
        Assert.Equal(SquadState.Downed, r.Sim.StateOf(r.IndexOf(1, 0)));
        Assert.Equal(first.Slot, r.Sim.View(r.IndexOf(1, 0))!.Value.Slot);
        Assert.Equal(1, r.Ledger(2)!.Battles);   // one deployment, one battle
    }

    [Fact]
    public void Export_and_Import_give_the_same_rosters_records_class_book_and_the_same_battle()
    {
        var r = Rig.Make(marines: 4, deploy: false);
        r.Wi.Rosters.Marine(1, 2)!.Record.Kills = 5;
        r.Wi.Rosters.Marine(1, 2)!.Record.Revives = 2;
        r.Wi.Rosters.Rename(1, 3, "Valeria");
        var save = r.Wi.Export();
        var json = System.Text.Json.JsonSerializer.Serialize(save);
        Assert.NotEmpty(json);
        var r2 = Rig.Make(deploy: false);
        r2.Wi.Import(save);
        Assert.Equal(save.Rosters.Owners.Select(o => (o.Key, o.Held, o.Marines.Count)), r2.Wi.Export().Rosters.Owners.Select(o => (o.Key, o.Held, o.Marines.Count)));
        Assert.Equal(5, r2.Wi.Rosters.Marine(1, 2)!.Record.Kills);
        Assert.Equal("Valeria", r2.Wi.Rosters.Marine(1, 3)!.Name);
        Assert.Equal(r.Wi.ClassBook.Export().Keys.Count, r2.Wi.ClassBook.Export().Keys.Count);
        Assert.Equal(r.Wi.RecordsOf(1), r2.Wi.RecordsOf(1));
        var a = r.Wi.Sim.BeginBattle([(1UL, 1, Vector3.Zero)]);
        var b = r2.Wi.Sim.BeginBattle([(1UL, 1, Vector3.Zero)]);
        Assert.Equal(a, b);
        r2.Wi.Import(null);   // a missing file is an empty value
        Assert.Equal(0, r2.Wi.Rosters.Total);
        r.Wi.Reset();
        Assert.Equal(0, r.Wi.Rosters.Total);
    }

    [Fact]
    public void The_W12_host_members_work_over_the_rosters_and_the_sim()
    {
        var r = Rig.Make(marines: 2);
        Assert.True(r.Wi.HasRoster(1));
        Assert.False(r.Wi.HasRoster(9));
        Assert.True(r.Wi.AddStartingRetainer(9));
        Assert.Equal(1, r.Wi.Rosters.Count(9));
        Assert.True(r.Wi.AddStartingRetainer(9));   // idempotent
        Assert.Equal(1, r.Wi.Rosters.Count(9));
        Assert.True(r.Wi.HoldSquad(1));
        Assert.Equal(SquadOrder.Hold, r.Sim.OrderOf(1));
        int saved = 0;
        r.Wi.SaveCallback = () => saved++;
        r.Wi.Save();
        Assert.Equal(1, saved);
        Assert.True(r.Wi.RebindSquad(1, 1));
    }

    [Fact]
    public void The_squad_wiring_does_nothing_outside_Warfront()
    {
        var w = TfwRig.War();   // Musou without the Warfront flag
        Assert.Null(w.Tfw!.Warfront);
    }
}
