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
using SmSim.Extras.Warfront.Retainers;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Wiring;

/// <summary>
/// Warfront W9b and Phase B T2: the squad wiring on a real World (the Tfw test arena, W3's node battle, W3b's progression, W11's synthetic classes) with the retainers as World players: the landing at 1, 2 and 4 humans and a Dreadnought's none,
/// the brain hook, orders, the damage scales, a Marine's kill paid to its owner once and never the ledger's kills, down and revive by anyone, out for the battle, the wipe rule, captures, the Retainer card, the retainer third,
/// loadout changes aboard, the Librarian, late landing and reconnect, the save round trip.
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
            // a run holds two Marines per player (owner 2026-11-23, card 2); the rig sets the count it wants as a bench fills the capacity, each Marine in the first one's loadout
            for (int id = 1; id <= humans; id++) if (r.Wi.Rosters.Has((ulong)id)) r.Wi.Rosters.Conform((ulong)id, marines);
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
        public WarfrontRetainers Ret => Wi.Retainers;
        public long Xp(ulong key) => Campaign.Player(key)!.Xp;
        public WarfrontPlayerLedger? Ledger(ulong key) => Campaign.Stats.Player(key);
        public int Standing(int player = 1) => Ret.StandingOf(player);
        public int IndexOf(int player, int slot) => Ret.IndexOf(player, slot);
        public WarfrontRetainer Of(int player, int slot) => Ret.Of(player, slot)!;
        public int IdOf(int player, int slot) => Of(player, slot).WorldId;
        public PlayerState Body(int player, int slot) => W.Players[IdOf(player, slot)];
        /// <summary>The retainer's World player falls; the next tick's squad rules down it.</summary>
        public void DownMarine(int player, int slot) { Assert.True(W.TfwKillForTests(IdOf(player, slot))); Stand(1); Assert.Equal(SquadState.Downed, Of(player, slot).State); }
    }

    /// <summary>The test-only stand/follow stub (never shipped; Phase B T4 brings the real brain): walk to within 3 m of the owner, holding Use when <paramref name="use"/>.</summary>
    internal static Func<int, PlayerState, PlayerInput?> StandFollow(World w, bool use = false) => (id, self) =>
    {
        var buttons = use ? InputButtons.Interact : InputButtons.None;
        if (w.Tfw?.MarineOf(id) is not { } m || !w.Players.TryGetValue(m.Owner, out var owner)) return new PlayerInput(0, 0f, 0f, self.Yaw, buttons);
        var d = owner.Position - self.Position;
        if (new Vector2(d.X, d.Y).Length() < 3f) return new PlayerInput(0, 0f, 0f, self.Yaw, buttons);
        float yaw = MathF.Atan2(-d.X, d.Y);
        return new PlayerInput(0, 0f, 1f, yaw, buttons);
    };

    // ---- connecting --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_node_battle_lands_every_identified_humans_squad_as_World_players_at_his_own_landing_place()
    {
        var r = Rig.Make(humans: 2, marines: 3);
        Assert.Equal(3, r.Standing(1));
        Assert.Equal(3, r.Standing(2));
        var ids = new HashSet<int>();
        for (int p = 1; p <= 2; p++)
            for (int i = 0; i < 3; i++)
            {
                int id = r.IdOf(p, i);
                Assert.True(TfwContracts.IsPuppet(id));
                Assert.True(ids.Add(id));
                Assert.Equal(MarineRole.Squad, r.W.Tfw!.MarineOf(id)!.Role);
                Assert.Equal(p, r.W.Tfw.MarineOf(id)!.Owner);   // the look is the owner's (GameServer.TfwLook)
                Assert.Equal(r.Wi.Rosters.Marine((ulong)p, i)!.Name, r.W.Tfw.PuppetName(id));   // the scoreboard name is the roster's
                Assert.Equal(Team.SpaceMarines, r.Body(p, i).Team);
                Assert.True(Vector3.Distance(r.Body(p, i).Position, r.Where(p)) < 6f);
            }
        Assert.Equal(1, r.Ledger(1)!.Battles);   // one battle each, counted for the deployment
        Assert.Equal(1, r.Ledger(2)!.Battles);
        Assert.True(r.Rules.AnyRetainerStanding());
        Assert.Equal(3, r.Rt.RosterSizeOf(1));
        Assert.Null(r.Rules.SquadTargets);   // the squad sim's targets are retired: the Orks see World players
    }

    [Fact]
    public void Four_humans_with_two_retainers_each_fill_eight_puppet_ids_and_the_battle_ends_with_the_field()
    {
        var r = Rig.Make(humans: 4, marines: 2);
        for (int p = 1; p <= 4; p++) Assert.Equal(2, r.Standing(p));
        Assert.Equal(12, r.W.Players.Count);   // 4 + 8: inside the snapshot's 16
        Assert.Equal(8, r.W.Tfw!.Marines.Count(m => m.Role == MarineRole.Squad));
        r.W.Tfw.PickupForTests(r.W);   // the battle is left: the field goes with it
        Assert.Empty(r.W.Tfw.Marines);
        Assert.Equal(4, r.W.Players.Count);
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 0)!.Record.BattlesSurvived);   // every standing Marine survived it
    }

    [Fact]
    public void A_Dreadnought_player_lands_no_retainer()
    {
        var r = Rig.Make(marines: 2, deploy: false);
        r.W.Spawn(1, DreadnoughtClass);
        r.Wi.StepAboard();
        Assert.True(r.Wi.Rosters.IsHeld(1));
        r.Deploy();
        Assert.Equal(0, r.Standing(1));
        Assert.Empty(r.Ret.All);
        Assert.DoesNotContain(r.W.Players.Keys, TfwContracts.IsPuppet);
    }

    [Fact]
    public void A_retainers_input_comes_from_the_brain_hook_and_without_a_brain_it_stands()
    {
        var r = Rig.Make(marines: 1);
        int id = r.IdOf(1, 0);
        var before = r.Body(1, 0).Position;
        r.Stand(30);
        Assert.True(Vector3.Distance(before, r.Body(1, 0).Position) < 0.05f, "no brain: it stands");
        TfwRig.Teleport(r.W, r.Where(1) + new Vector3(12f, 0f, 0f));
        r.Ret.Brain = StandFollow(r.W);
        float far = Vector3.Distance(r.Where(1), r.Body(1, 0).Position);
        r.Stand(30 * 4);
        Assert.True(Vector3.Distance(r.Where(1), r.Body(1, 0).Position) < far - 4f, "the stub walks it after its owner");
        Assert.Equal(id, r.IdOf(1, 0));
    }

    [Fact]
    public void Orders_reach_the_owners_retainers_and_a_Hold_holds_the_named_point()
    {
        var r = Rig.Make(marines: 2);
        var point = r.Where(1) + new Vector3(5f, 5f, 0f);
        Assert.True(r.W.TfwOrderMarines(1, new TfwCommandRecord(3, TfwCommand.Hold, 0, point)) is false);   // Warfront takes the order (the Musou retainer path returns false)
        Assert.Equal(SquadOrder.Hold, r.Ret.OrderOf(1));
        Assert.All(new[] { 0, 1 }, s => Assert.Equal(point, r.W.Tfw!.MarineOf(r.IdOf(1, s))!.HoldAt));
        Assert.True(r.Ret.Order(1, new TfwCommandRecord(4, TfwCommand.Attack, 0, point)));
        Assert.Equal(SquadOrder.Attack, r.Ret.OrderOf(1));
        Assert.Equal(point, r.W.Tfw!.MarineOf(r.IdOf(1, 0))!.AttackAt);
        Assert.False(r.Ret.Order(1, new TfwCommandRecord(4, TfwCommand.Follow, 0)));   // a resend
    }

    // ---- damage ------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_retainer_takes_the_squads_share_not_the_puppets_tenth_and_none_of_the_Orks_level_damage()
    {
        var r = Rig.Make(marines: 1);
        int id = r.IdOf(1, 0);
        Assert.Equal(WarfrontRetainerRules.DamageTaken, r.W.DamageTakenScale(id), 4);   // level 1, no card
        Assert.Equal(1f, r.W.DamageTakenScale(1), 4);   // the human's own
        Assert.Equal(0.35f, WarfrontRetainerRules.DamageTaken);
        Assert.Equal(0f, WarfrontRetainerRules.OrkLevelDamageShare);
        Assert.True(r.W.WarfrontIsRetainer(id));
        Assert.False(r.W.WarfrontIsRetainer(1));
    }

    [Fact]
    public void A_retainer_deals_base_player_damage_without_the_human_only_scales_and_its_kill_pays_the_owner_once_and_never_the_ledgers_kills()
    {
        Assert.False(WarfrontRetainerRules.HumanOnlyScales);   // owner 2026-11-25: "Base player damage only"
        Assert.False(WarfrontRetainerRules.CleaveBonus);   // nor Warfront's wider cleave (lead 38, series-37 plan "Lead notes on T2")
        var r = Rig.Make(marines: 2);
        int id = r.IdOf(1, 0);
        Assert.False(r.W.WarfrontCleaveBonus(id));   // a retainer swings with the class's own reach
        Assert.True(r.W.WarfrontCleaveBonus(1));     // its owner keeps the x1.2
        int points = r.W.Musou!.Rules.Of(MusouEnemyType.Boy).Points;
        long xp = r.Xp(1);
        r.Stand(1);
        Assert.Equal(1f, r.W.Musou.MeleeScaleOf(id), 4);   // level 1, no card: the base player damage (no 0.40)
        var at = r.Body(1, 0).Position + new Vector3(4f, 0f, 0f);
        ushort a = r.Ork(MusouEnemyType.Boy, at, 85f), b = r.Ork(MusouEnemyType.Boy, at + new Vector3(1f, 0f, 0f), 85f);
        r.W.Musou.Damage(b, r.IdOf(1, 1), 10f, Vector3.Zero, out _);   // the second Marine hurts b first: an assist
        Assert.True(r.W.Musou.Damage(a, id, 1e6f, Vector3.Zero, out bool killedA) && killedA);
        Assert.True(r.W.Musou.Damage(b, id, 1e6f, Vector3.Zero, out bool killedB) && killedB);
        r.Stand(2);
        Assert.Equal(2, r.Wi.Rosters.Marine(1, 0)!.Record.Kills);
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 1)!.Record.Assists);
        Assert.Equal(xp + 2L * points, r.Xp(1));   // each kill paid exactly once, to the owner
        Assert.Equal(0, r.Ledger(1)?.OrdinaryKills ?? 0);   // never the player's ledger kills
        Assert.Equal(2, r.Wi.RecordsOf(1)[0].Kills);
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

    // ---- down and revive ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_downed_Marine_is_revived_by_the_human_holding_Use_within_reach_for_the_hold_time_and_the_ledger_counts_one_revive()
    {
        var r = Rig.Make(marines: 1);
        r.DownMarine(1, 0);
        var near = r.Body(1, 0).Position;
        Assert.True(r.W.TfwRespawnAt(1, near + new Vector3(0.8f, 0f, 0f), 0f));
        r.Stand(20);   // no Use: nothing happens
        Assert.Equal(SquadState.Downed, r.Of(1, 0).State);
        Assert.False(r.Body(1, 0).Alive);   // its respawn is held: the core never brings it back
        Assert.Equal(0, r.Ledger(1)?.Revives ?? 0);
        r.Stand((int)(SquadRules.PlayerReviveSeconds * 30) + 6, id => id == 1 ? new PlayerInput(0, 0f, 0f, 0f, InputButtons.Interact) : default);
        Assert.Equal(SquadState.Standing, r.Of(1, 0).State);
        Assert.True(r.Body(1, 0).Alive);
        Assert.Equal(1, r.Ledger(1)!.Revives);
        r.Stand(30);
        Assert.Equal(1, r.Ledger(1)!.Revives);
    }

    [Fact]
    public void A_downed_Marine_not_revived_in_20_s_is_out_for_the_battle_and_back_in_the_next()
    {
        var r = Rig.Make(marines: 2);
        int id = r.IdOf(1, 0);
        r.DownMarine(1, 0);
        r.Stand((int)(WarfrontRetainerRules.DownedSeconds * 30) - 5);
        Assert.Equal(SquadState.Downed, r.Of(1, 0).State);
        r.Stand(10);
        Assert.Equal(SquadState.Out, r.Of(1, 0).State);
        Assert.False(r.W.Players.ContainsKey(id));   // it left the World: its id is free
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 0)!.Record.TimesDowned);
        Assert.Equal(1, r.Standing(1));
        r.W.Tfw!.PickupForTests(r.W);
        Assert.Equal(0, r.Wi.Rosters.Marine(1, 0)!.Record.BattlesSurvived);
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 1)!.Record.BattlesSurvived);
        r.Deploy();
        Assert.Equal(2, r.Standing(1));   // back in the next
    }

    [Fact]
    public void A_retainer_holding_Use_revives_a_downed_mate_and_a_fallen_human_and_its_record_counts_both()
    {
        var r = Rig.Make(marines: 2);
        r.DownMarine(1, 1);
        var at = r.Body(1, 1).Position;
        Assert.True(r.W.TfwRespawnAt(r.IdOf(1, 0), at + new Vector3(0.6f, 0f, 0f), 0f));
        r.Ret.Brain = (_, self) => new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.Interact);
        r.Stand((int)(SquadRules.PlayerReviveSeconds * 30) + 6);
        Assert.Equal(SquadState.Standing, r.Of(1, 1).State);
        Assert.Equal(1, r.Wi.Rosters.Marine(1, 0)!.Record.Revives);

        Assert.True(r.W.TfwRespawnAt(1, r.Body(1, 0).Position + new Vector3(0.6f, 0f, 0f), 0f));
        Assert.True(r.W.TfwKillForTests(1));
        r.Stand((int)(SquadRules.PlayerReviveSeconds * 30) + 10);
        Assert.True(r.W.Players[1].Alive);   // revived inside his window
        Assert.True(r.Wi.Rosters.Marine(1, 0)!.Record.Revives + r.Wi.Rosters.Marine(1, 1)!.Record.Revives >= 2);
        Assert.Equal(1, r.Ledger(1)!.Deaths);   // each fall counts
    }

    [Fact]
    public void A_fallen_human_is_not_revived_after_his_window()
    {
        var r = Rig.Make(marines: 1);
        Assert.True(r.W.TfwKillForTests(1));
        r.W.Musou!.Frozen = true;
        r.Stand(750 + 40);   // nobody holds Use: the window runs out
        Assert.Equal(WarfrontDownPhase.Spectator, r.W.Tfw!.WarfrontDownState(1).Phase);
        Assert.True(r.W.TfwRespawnAt(r.IdOf(1, 0), r.Where(1) + new Vector3(0.6f, 0f, 0f), 0f));
        r.Ret.Brain = (_, self) => new PlayerInput(0, 0f, 0f, self.Yaw, InputButtons.Interact);
        r.Stand(90);
        Assert.False(r.W.Players[1].Alive);   // never revived after the window
        Assert.Equal(0, r.Wi.Rosters.Marine(1, 0)!.Record.Revives);
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
    public void The_last_human_down_with_a_Marine_standing_is_no_wipe_and_with_every_Marine_down_it_is()
    {
        var r = Rig.Make(marines: 2);
        Assert.True(r.W.TfwKillForTests(1));
        r.W.Musou!.Frozen = true;
        r.Stand(750 + 60);   // the window is over: a spectator
        Assert.Equal(TfwRunPhase.Battle, r.W.Tfw!.Phase);   // a Marine stands: no wipe
        Assert.True(r.Ret.AnyStanding);

        var w = Rig.Make(marines: 2);
        w.DownMarine(1, 0);
        w.DownMarine(1, 1);
        Assert.True(w.W.TfwKillForTests(1));
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
        void Move(Vector3 to)
        {
            TfwRig.Teleport(r.W, to);
            for (int s = 0; s < 2; s++) if (r.Of(1, s).State == SquadState.Standing) Assert.True(r.W.TfwRespawnAt(r.IdOf(1, s), to + new Vector3(1f + s, 0f, 0f), 0f));
        }
        for (int k = 0; k < 3; k++)
        {
            int p = k;
            Move(battle.Layout.Positions[p]);
            for (int t = 0; t < 120 * 30 && battle.Objectives[p].State != TfwPositionState.Secured; t++) { r.Clear(); r.W.Step(_ => default); }
            Assert.Equal(TfwPositionState.Secured, battle.Objectives[p].State);
        }
        var ledger = r.Ledger(1)!;
        Assert.Equal(3, ledger.CapturesAbc);   // A, B, C each once for the human standing in it
        Assert.Equal(0, ledger.CapturesD);
        Move(battle.Layout.Hq);
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
    public void A_retired_Retainer_card_recruits_nobody_and_a_save_and_load_keeps_two_and_no_offer_holds_it()
    {
        // owner 2026-11-23, card 2: every player always has two retainers; the Retainer card is gone
        var r = Rig.Make(marines: 2, deploy: false);
        Assert.Equal(2, r.Wi.Rosters.Count(1));
        r.Rt.Picked!(new WarfrontPickResult(1, 0, 0, WarfrontCard.Retainer, false));   // a stale Retainer pick, as an old picker could report it
        Assert.Equal(2, r.Wi.Rosters.Count(1));
        r.Rt.Picked!(new WarfrontPickResult(1, 1, 1, WarfrontCard.Damage, false));
        Assert.Equal(2, r.Wi.Rosters.Count(1));
        var save = r.Wi.Export();
        var r2 = Rig.Make(deploy: false);
        r2.Wi.Import(save);
        r2.Wi.StepAboard();
        Assert.Equal(2, r2.Wi.Rosters.Count(1));
        Assert.DoesNotContain(WarfrontCard.Retainer, WarfrontOffers.Roll(7, 1, 0, WarfrontOffers.RetainerEligible(r2.Rt.RosterSizeOf(1), false)));
    }

    [Fact]
    public void The_retainer_third_three_damage_cards_give_the_player_x1_3_and_the_Marines_x1_1_and_HP_cards_lower_the_damage_taken_by_the_health_share()
    {
        var r = Rig.Make(marines: 2, deploy: false);
        Cards(r, 1, WarfrontCard.Damage, WarfrontCard.Damage, WarfrontCard.Damage);
        Assert.Equal(1.3f, r.Rt.BonusesOfKey(1).Damage, 3);
        Assert.Equal(1.1f, WarfrontStatBonuses.ForRetainer(r.Campaign.Player(1)!.Cards).Damage, 3);
        r.Deploy();
        r.Stand(1);
        float scale = 1.1f * WarfrontSquadWiring.SquadLevelScale(4).Damage;   // the cards' third times the level part (S1, owner 2026-11-19: three cards = level 4)
        Assert.Equal(scale, r.W.Musou!.MeleeScaleOf(r.IdOf(1, 0)), 3);   // the Orks take the retainer's hits at this scale

        var h = Rig.Make(marines: 2, deploy: false);
        Cards(h, 1, WarfrontCard.MaxHp, WarfrontCard.MaxHp);
        h.Deploy();
        float share = WarfrontStatBonuses.ForRetainer(h.Campaign.Player(1)!.Cards).MaxHp * WarfrontSquadWiring.SquadLevelScale(3).Health;   // S1: two cards = level 3
        Assert.True(share > 1f);
        Assert.Equal(share, h.Ret.ScaleOf(1).Health, 3);   // unshielded hits are taken at 0.35 / share (shielded ones at 0.35 / the armour's)
    }

    // ---- the class book aboard ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_loadout_change_aboard_follows_to_every_Marine_keeping_names_and_records_and_a_Dreadnought_holds_the_roster()
    {
        var r = Rig.Make(marines: 2, deploy: false);
        var names = Enumerable.Range(0, 2).Select(s => r.Wi.Rosters.Marine(1, s)!.Name).ToArray();
        r.Wi.Rosters.Marine(1, 1)!.Record.Kills = 7;
        string tactical = r.Wi.Rosters.Marine(1, 0)!.Loadout.ClassId;
        r.W.Spawn(1, 1);   // an Assault marine
        r.Wi.StepAboard();
        for (int s = 0; s < 2; s++)
        {
            Assert.NotEqual(tactical, r.Wi.Rosters.Marine(1, s)!.Loadout.ClassId);
            Assert.Equal(names[s], r.Wi.Rosters.Marine(1, s)!.Name);
        }
        Assert.Equal(7, r.Wi.Rosters.Marine(1, 1)!.Record.Kills);
        Assert.NotNull(r.Wi.Rosters.Marine(1, 0)!.Loadout.Jump);   // the Assault's pack
        r.Deploy();
        Assert.Equal(r.W.Config.Classes[1].Name, r.W.Config.Classes[r.Body(1, 0).Class].Name);   // the World player wears the roster's class

        var d = Rig.Make(marines: 2, deploy: false);
        d.W.Spawn(1, DreadnoughtClass);
        d.Wi.StepAboard();
        Assert.True(d.Wi.Rosters.IsHeld(1));
        Assert.Equal(2, d.Wi.Rosters.Count(1));
        d.Deploy();
        Assert.Equal(0, d.Standing(1));   // the next battle lands nobody

        var back = Rig.Make(marines: 2, deploy: false);
        back.W.Spawn(1, DreadnoughtClass); back.Wi.StepAboard();
        back.W.Spawn(1, 0); back.Wi.StepAboard();
        Assert.False(back.Wi.Rosters.IsHeld(1));   // back to Tactical releases it
        back.Deploy();
        Assert.Equal(2, back.Standing(1));
    }

    [Fact]
    public void A_Dreadnought_first_player_has_two_held_Marines_and_two_after_the_switch_also_across_a_save()
    {
        // owner 2026-11-23, card 2: two retainers always, none in the field for a Dreadnought; the free retainer is retired
        var d = Rig.Make(deploy: false);
        d.Wi.Reset();
        d.W.Spawn(1, DreadnoughtClass);
        d.Wi.StepAboard();
        Assert.Equal(2, d.Wi.Rosters.Count(1));
        Assert.True(d.Wi.Rosters.IsHeld(1));
        d.W.Spawn(1, 0);
        d.Wi.StepAboard();
        Assert.Equal(2, d.Wi.Rosters.Count(1));
        Assert.False(d.Wi.Rosters.IsHeld(1));
        d.W.Spawn(1, DreadnoughtClass); d.Wi.StepAboard();
        d.W.Spawn(1, 0); d.Wi.StepAboard();
        Assert.Equal(2, d.Wi.Rosters.Count(1));
        var save = d.Wi.Export();
        var e = Rig.Make(deploy: false);
        e.Wi.Import(save);
        e.W.Spawn(1, DreadnoughtClass); e.Wi.StepAboard();
        e.W.Spawn(1, 0); e.Wi.StepAboard();
        Assert.Equal(2, e.Wi.Rosters.Count(1));
    }

    [Fact]
    public void A_Librarians_two_retainers_roll_their_own_classes_and_the_squad_class_option_changes_nothing()
    {
        // the retainer brief (2026-11-23): each retainer rolls Tactical, Assault or Devastator (was one uniform roll of the chosen class)
        var cfg = Cfg();
        var lib = cfg.Classes[0] with { Name = LibrarianRules.ClassName, Role = "librarian" };
        cfg = cfg with { Classes = [.. cfg.Classes, lib] };
        int librarian = cfg.Classes.Count - 1;
        var r = Rig.Make(marines: 2, deploy: false, config: cfg);
        r.W.Spawn(1, librarian);
        r.Wi.StepAboard();
        Assert.Equal(2, r.Wi.Rosters.Count(1));
        var rolled = Enumerable.Range(0, 2).Select(s => r.Wi.Rosters.Marine(1, s)!.Loadout).ToArray();
        for (int s = 0; s < 2; s++)
            Assert.Equal(SquadClassRules.RoleOf(SquadClassBook.LibrarianClassOf(7UL, 1, s)), cfg.Classes.First(c => c.Name == rolled[s].ClassId).Role);
        r.Wi.SetSquadClass(1, SquadClass.Assault);   // the old option: stored, never read for the roll
        for (int s = 0; s < 2; s++) { var now = r.Wi.Rosters.Marine(1, s)!.Loadout; Assert.Equal((rolled[s].ClassId, rolled[s].Primary, rolled[s].Melee, rolled[s].HealthMax), (now.ClassId, now.Primary, now.Melee, now.HealthMax)); Assert.True(rolled[s].Perks.SequenceEqual(now.Perks)); }
    }

    // ---- late landing, leaving, saving ----------------------------------------------------------------------------------------------

    [Fact]
    public void A_second_human_joining_late_gets_his_squad_landed_without_touching_the_first_and_a_reconnect_re_binds()
    {
        var r = Rig.Make(marines: 2);
        r.DownMarine(1, 0);
        int firstSlot = r.Of(1, 0).Slot;
        Assert.False(r.Wi.AddStartingRetainer(2));   // not in the world yet: the first tick aboard (or the landing) makes it
        r.W.Spawn(2, r.W.AssignClass(0));   // joins while the battle is under way: flown in
        r.Stand(60);
        Assert.True(r.Rt.TryKey(2, out _));
        Assert.Equal(SquadRules.RetainersPerPlayer, r.Standing(2));   // a late joiner's roster is the run's two (owner 2026-11-23, card 2)
        Assert.Equal(SquadState.Downed, r.Of(1, 0).State);   // the first squad was not landed again
        Assert.Equal(1, r.Standing(1));
        Assert.Equal(1, r.Ledger(2)!.Battles);

        // he leaves: his squad holds, and counts as standing; he returns under a new id and the squad is re-bound
        r.Rt.KeyOf = id => id == 22 ? 2UL : (ulong)id;
        r.W.Remove(2);
        r.Stand(30);
        Assert.Equal(SquadOrder.Hold, r.Ret.OrderOf(2));
        Assert.True(r.Rules.AnyRetainerStanding());
        Assert.Equal(SquadRules.RetainersPerPlayer, r.Standing(2));
        r.W.Spawn(22, r.W.AssignClass(0));
        r.Stand(60);
        Assert.Equal(SquadRules.RetainersPerPlayer, r.Standing(22));
        Assert.Equal(0, r.Standing(2));
        Assert.Equal(firstSlot, r.Of(1, 0).Slot);
        Assert.Equal(1, r.Ledger(2)!.Battles);   // one deployment, one battle
        Assert.Equal(SquadOrder.Follow, r.Ret.OrderOf(22));   // the squad held for his leaving follows again
    }

    [Fact]
    public void Export_and_Import_give_the_same_rosters_records_and_class_book()
    {
        var r = Rig.Make(marines: 2, deploy: false);   // Import conforms every roster to the run's two (owner 2026-11-23, card 2)
        r.Wi.Rosters.Marine(1, 1)!.Record.Kills = 5;
        r.Wi.Rosters.Marine(1, 1)!.Record.Revives = 2;
        r.Wi.Rosters.Rename(1, 1, "Valeria");
        var save = r.Wi.Export();
        var json = System.Text.Json.JsonSerializer.Serialize(save);
        Assert.NotEmpty(json);
        var r2 = Rig.Make(deploy: false);
        r2.Wi.Import(save);
        Assert.Equal(save.Rosters.Owners.Select(o => (o.Key, o.Held, o.Marines.Count)), r2.Wi.Export().Rosters.Owners.Select(o => (o.Key, o.Held, o.Marines.Count)));
        Assert.Equal(5, r2.Wi.Rosters.Marine(1, 1)!.Record.Kills);
        Assert.Equal("Valeria", r2.Wi.Rosters.Marine(1, 1)!.Name);
        Assert.Equal(r.Wi.ClassBook.Export().Keys.Count, r2.Wi.ClassBook.Export().Keys.Count);
        Assert.Equal(r.Wi.RecordsOf(1), r2.Wi.RecordsOf(1));
        r2.Wi.Import(null);   // a missing file is an empty value
        Assert.Equal(0, r2.Wi.Rosters.Total);
        r.Wi.Reset();
        Assert.Equal(0, r.Wi.Rosters.Total);
    }

    [Fact]
    public void The_W12_host_members_work_over_the_rosters_and_the_retainers()
    {
        var r = Rig.Make(marines: 2);
        Assert.True(r.Wi.HasRoster(1));
        Assert.False(r.Wi.HasRoster(9));
        Assert.False(r.Wi.AddStartingRetainer(9));   // no such player in the world: nothing made
        Assert.True(r.Wi.AddStartingRetainer(1));    // idempotent: the roster exists
        Assert.Equal(2, r.Wi.Rosters.Count(1));
        Assert.True(r.Wi.HoldSquad(1));
        Assert.Equal(SquadOrder.Hold, r.Ret.OrderOf(1));
        int saved = 0;
        r.Wi.SaveCallback = () => saved++;
        r.Wi.Save();
        Assert.Equal(1, saved);
        Assert.True(r.Wi.RebindSquad(1, 1));
        Assert.False(r.Wi.OrderJumpPound(1, Vector3.Zero));   // retired with the squad sim until the retainers' brain (T4)
    }

    [Fact]
    public void The_squad_wiring_does_nothing_outside_Warfront()
    {
        var w = TfwRig.War();   // Musou without the Warfront flag
        Assert.Null(w.Tfw!.Warfront);
        Assert.False(w.WarfrontIsRetainer(TfwContracts.PuppetIdFirst));
    }
}
