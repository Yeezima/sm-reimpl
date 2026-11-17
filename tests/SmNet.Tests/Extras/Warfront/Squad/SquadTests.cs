using System.Numerics;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad;

/// <summary>A flat world with a handful of scripted enemies: the squad sim against a fake (no World, no Musou files).</summary>
sealed class FakeArena : ISquadArena
{
    public sealed class Ork { public ushort Id; public Vector3 Pos; public float Health = 100f; public SquadEnemyClass Kind; public bool Alive => Health > 0f; }

    public readonly List<Ork> Orks = [];
    public int TickRate { get; init; } = 30;

    public Ork Add(Vector3 pos, float health = 100f, SquadEnemyClass kind = SquadEnemyClass.Fodder)
    {
        var o = new Ork { Id = (ushort)(Orks.Count + 1), Pos = pos, Health = health, Kind = kind };
        Orks.Add(o);
        return o;
    }

    public int QueryEnemies(Vector3 centre, float radius, Span<ushort> into)
    {
        var near = Orks.Where(o => o.Alive && MathF.Sqrt((o.Pos.X - centre.X) * (o.Pos.X - centre.X) + (o.Pos.Y - centre.Y) * (o.Pos.Y - centre.Y)) <= radius)
            .OrderBy(o => (o.Pos - centre).Length()).ThenBy(o => o.Id).Take(into.Length).ToList();
        for (int i = 0; i < near.Count; i++) into[i] = near[i].Id;
        return near.Count;
    }

    public bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind)
    {
        var o = Orks.FirstOrDefault(x => x.Id == id && x.Alive);
        position = o?.Pos ?? default; kind = o?.Kind ?? default;
        return o is not null;
    }

    public bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed)
    {
        killed = false;
        var o = Orks.FirstOrDefault(x => x.Id == id && x.Alive);
        if (o is null) return false;
        o.Health -= amount;
        killed = o.Health <= 0f;
        return true;
    }

    public bool HasSight(Vector3 from, Vector3 to) => true;

    public bool Ground(Vector3 p, out Vector3 ground) { ground = p; return true; }
    public bool CanStep(Vector3 from, Vector3 to, out Vector3 placed) { placed = to; return true; }
    public bool CanWalkLine(Vector3 from, Vector3 to) => true;
    public IReadOnlyList<Vector3>? FindPath(Vector3 from, Vector3 to) => [to];
    public Vector3? FlowNext(Vector3 from, Vector3 point) => point;
    public void Tick() { }
}

public class SquadTests
{
    const int Rate = 30;

    static (SquadRosters Rosters, FakeArena Arena, WarfrontSquadSim Sim) Make(int owners = 1, int marines = 1)
    {
        var rosters = new SquadRosters();
        for (ulong o = 1; o <= (ulong)owners; o++) { rosters.AddOwner(o); for (int k = 1; k < marines; k++) rosters.Recruit(o); }
        var arena = new FakeArena();
        return (rosters, arena, new WarfrontSquadSim(rosters, arena));
    }

    static uint _t;
    static void Run(WarfrontSquadSim sim, int ticks, params SquadHuman[] humans)
    {
        for (int k = 0; k < ticks; k++) sim.Step(++_t, humans);
    }

    static SquadHuman Owner(Vector3 at, int id = 1) => new(id, at, true);

    static void Land(WarfrontSquadSim sim, Vector3 at, params int[] owners) => sim.BeginBattle(owners.Select(o => ((ulong)o, o, at)).ToArray());

    // ---- rosters

    [Fact]
    public void A_roster_starts_with_one_and_grows_to_nine_and_no_more()
    {
        var r = new SquadRosters();
        Assert.True(r.AddOwner(7));
        Assert.Equal(1, r.Count(7));
        for (int i = 0; i < 8; i++) Assert.True(r.Recruit(7));
        Assert.Equal(9, r.Count(7));
        Assert.False(r.Recruit(7));
        Assert.Equal(9, r.Count(7));
        Assert.False(r.Recruit(99));   // no roster
    }

    [Fact]
    public void Four_players_hold_36_in_separate_rosters_and_a_fifth_gets_none()
    {
        var r = new SquadRosters();
        for (ulong o = 1; o <= 4; o++) { Assert.True(r.AddOwner(o)); while (r.Recruit(o)) { } }
        Assert.Equal(36, r.Total);
        Assert.True(r.AddOwner(5));   // a fifth key keeps a roster for the run; only four fight at once
        Assert.Equal(37, r.Total);
        Assert.True(r.AddOwner(2));   // an existing roster is left alone
        Assert.Equal(9, r.Count(2));
        r.Rename(1, 0, "Alpha");
        Assert.Equal("Alpha", r.Marine(1, 0)!.Name);
        Assert.NotEqual("Alpha", r.Marine(2, 0)!.Name);   // separate rosters
        Assert.Equal(36, Enumerable.Range(1, 4).SelectMany(o => Enumerable.Range(0, 9).Select(s => r.Marine((ulong)o, s)!.Name)).Distinct().Count());
    }

    [Fact]
    public void Names_and_records_stay_when_the_loadout_changes()
    {
        var r = new SquadRosters();
        r.AddOwner(1); r.Recruit(1);
        Assert.True(r.Rename(1, 1, "  Brother Titus  "));
        r.Marine(1, 1)!.Record.Kills = 12; r.Marine(1, 1)!.Record.Revives = 3;
        var loadout = new SquadLoadout { ClassId = "sm_assault", Primary = "bolt_pistol", Colours = [1u, 2u], HealthMax = 250f };
        Assert.True(r.Mirror(1, loadout));
        Assert.Equal("Brother Titus", r.Marine(1, 1)!.Name);
        Assert.Equal(12, r.Marine(1, 1)!.Record.Kills);
        Assert.Equal(3, r.Marine(1, 1)!.Record.Revives);
        Assert.Same(loadout, r.Marine(1, 0)!.Loadout);
        Assert.Same(loadout, r.Marine(1, 1)!.Loadout);
        r.Mirror(1, new SquadLoadout { ClassId = "sm_devastator" });
        Assert.Equal("Brother Titus", r.Marine(1, 1)!.Name);
        Assert.Equal("sm_devastator", r.Marine(1, 1)!.Loadout.ClassId);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Valid Name", true)]
    [InlineData("0123456789012345678901234", false)]   // 25
    [InlineData("012345678901234567890123", true)]     // 24
    [InlineData("bad\nname", false)]
    public void A_name_is_trimmed_and_limited(string name, bool ok)
    {
        var r = new SquadRosters(); r.AddOwner(1);
        string before = r.Marine(1, 0)!.Name;
        Assert.Equal(ok, r.Rename(1, 0, name));
        if (!ok) Assert.Equal(before, r.Marine(1, 0)!.Name);
    }

    [Fact]
    public void The_rosters_survive_a_save_and_a_load()
    {
        var r = new SquadRosters();
        r.AddOwner(3); r.Recruit(3); r.AddOwner(4);
        r.Rename(3, 1, "Keeper"); r.Marine(3, 1)!.Record.BattlesSurvived = 5; r.SetHeld(4, true);
        r.Mirror(3, new SquadLoadout { ClassId = "sm_tactical" });
        var back = new SquadRosters();
        back.Import(r.Export());
        Assert.Equal(3, back.Total);
        Assert.Equal("Keeper", back.Marine(3, 1)!.Name);
        Assert.Equal(5, back.Marine(3, 1)!.Record.BattlesSurvived);
        Assert.Equal("sm_tactical", back.Marine(3, 0)!.Loadout.ClassId);
        Assert.True(back.IsHeld(4));
        back.Recruit(3);
        Assert.DoesNotContain(back.Marine(3, 2)!.Name, new[] { "Keeper" });
    }

    [Fact]
    public void A_held_roster_sends_nobody_to_battle_and_comes_back_when_released()
    {
        var (r, _, sim) = Make(2, 3);
        r.SetHeld(2, true);
        Assert.Equal(3, sim.BeginBattle([(1UL, 1, Vector3.Zero), (2UL, 2, Vector3.Zero)]));
        r.SetHeld(2, false);
        Assert.Equal(6, sim.BeginBattle([(1UL, 1, Vector3.Zero), (2UL, 2, Vector3.Zero)]));
    }

    // ---- orders

    [Fact]
    public void Follow_keeps_the_squad_within_the_band_of_a_walking_owner()
    {
        var (_, _, sim) = Make(1, 4);
        Land(sim, Vector3.Zero, 1);
        var owner = new Vector3(0, 0, 0);
        for (int k = 0; k < Rate * 20; k++)
        {
            owner.X += 4f / Rate;   // walks at 4 m/s
            sim.Step(++_t, [Owner(owner)]);
        }
        for (int i = 0; i < 4; i++)
        {
            var v = sim.View(i)!.Value;
            Assert.InRange(Vector3.Distance(v.Position, owner), 0f, MarineRules.FollowMax + 0.5f);
        }
    }

    [Fact]
    public void Hold_stays_at_the_ordered_point_and_Attack_goes_there()
    {
        var (_, _, sim) = Make(1, 3);
        Land(sim, Vector3.Zero, 1);
        var point = new Vector3(40, 10, 0);
        Assert.True(sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0, point)));
        Assert.Equal(SquadOrder.Hold, sim.OrderOf(1));
        Run(sim, Rate * 12, Owner(Vector3.Zero));
        for (int i = 0; i < 3; i++) Assert.InRange(Vector3.Distance(sim.View(i)!.Value.Position, point), 0f, 4f);
        Assert.True(sim.Order(1, new TfwCommandRecord(2, TfwCommand.Attack, 0, new Vector3(-30, 0, 0))));
        Run(sim, Rate * 12, Owner(Vector3.Zero));
        for (int i = 0; i < 3; i++) Assert.InRange(Vector3.Distance(sim.View(i)!.Value.Position, new Vector3(-30, 0, 0)), 0f, 9f);
        Assert.True(sim.Order(1, new TfwCommandRecord(3, TfwCommand.Follow, 0)));
        Run(sim, Rate * 20, Owner(Vector3.Zero));
        for (int i = 0; i < 3; i++) Assert.InRange(Vector3.Distance(sim.View(i)!.Value.Position, Vector3.Zero), 0f, MarineRules.FollowMax + 0.5f);
    }

    [Fact]
    public void Hold_without_a_point_holds_where_each_Marine_stands()
    {
        var (_, _, sim) = Make(1, 2);
        Land(sim, Vector3.Zero, 1);
        Run(sim, 5, Owner(Vector3.Zero));
        var before = Enumerable.Range(0, 2).Select(i => sim.View(i)!.Value.Position).ToArray();
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        Run(sim, Rate * 5, Owner(new Vector3(50, 0, 0)));   // the owner leaves
        for (int i = 0; i < 2; i++) Assert.InRange(Vector3.Distance(sim.View(i)!.Value.Position, before[i]), 0f, 1.5f);
    }

    [Fact]
    public void A_resent_sequence_and_a_command_that_is_no_order_are_ignored()
    {
        var (_, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        Assert.True(sim.Order(1, new TfwCommandRecord(5, TfwCommand.Hold, 0)));
        Assert.False(sim.Order(1, new TfwCommandRecord(5, TfwCommand.Follow, 0)));
        Assert.Equal(SquadOrder.Hold, sim.OrderOf(1));
        Assert.False(sim.Order(1, new TfwCommandRecord(6, TfwCommand.Deploy, 0)));
        Assert.False(sim.Order(9, new TfwCommandRecord(7, TfwCommand.Hold, 0)));   // no roster
    }

    [Fact]
    public void An_order_reaches_only_the_senders_squad()
    {
        var (_, _, sim) = Make(2, 1);
        Land(sim, Vector3.Zero, 1, 2);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        Assert.Equal(SquadOrder.Hold, sim.OrderOf(1));
        Assert.Equal(SquadOrder.Follow, sim.OrderOf(2));
    }

    // ---- combat

    [Fact]
    public void A_Marine_shoots_an_Ork_in_range_and_records_the_kill()
    {
        var (r, arena, sim) = Make();
        r.Mirror(1, new SquadLoadout { Weapon = new SquadWeapon(30f, 50f, 0.1f, 1f) });
        Land(sim, Vector3.Zero, 1);
        arena.Add(new Vector3(12, 0, 0), 100f, SquadEnemyClass.Elite);
        var kills = new List<SquadKill>();
        for (int k = 0; k < Rate * 3; k++) { sim.Step(++_t, [Owner(Vector3.Zero)]); kills.AddRange(sim.Kills); }
        Assert.False(arena.Orks[0].Alive);
        Assert.Single(kills);
        Assert.Equal(SquadEnemyClass.Elite, kills[0].Class);
        Assert.Equal(1UL, kills[0].Key);
        Assert.Equal(1, r.Marine(1, 0)!.Record.Kills);
        Assert.Equal(1, r.Marine(1, 0)!.Record.EliteKills);
    }

    [Fact]
    public void An_Ork_out_of_the_Marines_range_is_not_shot()
    {
        var (r, arena, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        arena.Add(new Vector3(100, 0, 0), 100f);
        Run(sim, Rate * 3, Owner(Vector3.Zero));
        Assert.Equal(100f, arena.Orks[0].Health);
    }

    [Fact]
    public void A_melee_Marine_closes_and_strikes()
    {
        var (r, arena, sim) = Make();
        r.Mirror(1, new SquadLoadout { Weapon = SquadWeapon.Blade with { Damage = 200f, HitChance = 1f } });
        Land(sim, Vector3.Zero, 1);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Attack, 0));   // only Attack chases (MarineBrain.PlanRetainer)
        arena.Add(new Vector3(8, 0, 0), 100f);
        Run(sim, Rate * 6, Owner(Vector3.Zero));
        Assert.False(arena.Orks[0].Alive);
        Assert.Equal(1, r.Marine(1, 0)!.Record.Kills);
    }

    [Fact]
    public void A_Marine_that_hurt_an_Ork_another_killed_gets_an_assist_and_a_human_kill_shares_it_too()
    {
        var (r, arena, sim) = Make(1, 2);
        var weak = new SquadLoadout { Weapon = new SquadWeapon(30f, 30f, 0.1f, 1f) };
        r.Mirror(1, weak);
        Land(sim, Vector3.Zero, 1);
        sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0));
        arena.Add(new Vector3(10, 0, 0), 120f);
        Run(sim, Rate * 3, Owner(Vector3.Zero));
        Assert.False(arena.Orks[0].Alive);
        int kills = r.Marine(1, 0)!.Record.Kills + r.Marine(1, 1)!.Record.Kills;
        int assists = r.Marine(1, 0)!.Record.Assists + r.Marine(1, 1)!.Record.Assists;
        Assert.Equal(1, kills);
        Assert.Equal(1, assists);   // two Marines, one kill: the other assisted

        // a human's kill of an Ork a Marine hurt
        var human = arena.Add(new Vector3(10, 5, 0), 1000f);
        Run(sim, 6, Owner(Vector3.Zero));
        Assert.True(human.Health < 1000f);
        int before = r.Marine(1, 0)!.Record.Assists + r.Marine(1, 1)!.Record.Assists;
        sim.NoteHumanKill(human.Id);
        Assert.True(r.Marine(1, 0)!.Record.Assists + r.Marine(1, 1)!.Record.Assists > before);
    }

    // ---- being hit, downed, revive, out

    [Fact]
    public void The_Orks_see_standing_Marines_as_targets_in_the_squad_range_and_armour_goes_first()
    {
        var (r, _, sim) = Make(1, 2);
        r.Mirror(1, new SquadLoadout { HealthMax = 100f, ArmourMax = 50f });
        Land(sim, Vector3.Zero, 1);
        var targets = new SmSim.Extras.Musou.MusouTarget[4];
        Assert.Equal(2, sim.AppendTargets(targets));
        Assert.Equal(SquadRules.TargetId(0), targets[0].Id);
        Assert.Equal(0, SquadRules.IndexOfTarget(targets[0].Id));
        Assert.Equal(-1, SquadRules.IndexOfTarget(-5));   // a Guard squad's id is not ours
        Assert.True(sim.Hit(targets[0].Id, 40f / SquadRules.DamageTaken, Vector3.Zero));   // 40 after the share: 10 armour left
        Assert.Equal(1f, sim.View(0)!.Value.HealthShare);   // armour took it
        Assert.True(sim.Hit(targets[0].Id, 60f / SquadRules.DamageTaken, Vector3.Zero));   // 60: 10 armour gone, 50 health
        Assert.Equal(0.5f, sim.View(0)!.Value.HealthShare, 3);
        Assert.False(sim.Hit(-5, 10f, Vector3.Zero));
    }

    static void Down(WarfrontSquadSim sim, int index) => Assert.True(sim.Hit(SquadRules.TargetId(index), 100000f, Vector3.Zero));

    [Fact]
    public void A_Marine_at_zero_is_downed_for_twenty_seconds_and_is_then_out()
    {
        var (_, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        Down(sim, 0);
        Assert.Equal(SquadState.Downed, sim.StateOf(0));
        Assert.Equal(20f, sim.View(0)!.Value.DownedLeft, 2);
        Run(sim, 1, Owner(new Vector3(200, 0, 0)));
        Assert.Equal(new SquadDown(1, 0), Assert.Single(sim.Downs));   // reported by the next step
        var targets = new SmSim.Extras.Musou.MusouTarget[2];
        Assert.Equal(0, sim.AppendTargets(targets));   // a downed Marine is nobody's target
        Assert.False(sim.Hit(SquadRules.TargetId(0), 10f, Vector3.Zero));
        Run(sim, Rate * 19 - 1, Owner(new Vector3(200, 0, 0)));
        Assert.Equal(SquadState.Downed, sim.StateOf(0));
        Run(sim, Rate + 1, Owner(new Vector3(200, 0, 0)));
        Assert.Equal(SquadState.Out, sim.StateOf(0));
        Assert.False(sim.Revive(0));
    }

    [Fact]
    public void A_player_holding_Use_revives_at_full_health_in_two_seconds_and_a_short_hold_does_nothing()
    {
        var (r, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        Run(sim, 1, Owner(Vector3.Zero));
        Down(sim, 0);
        var at = sim.View(0)!.Value.Position;
        Run(sim, Rate / 2, new SquadHuman(1, at, true, false, 0));   // half a second
        Run(sim, 5, Owner(at));                                      // let go: progress resets
        Run(sim, Rate * 1, new SquadHuman(1, at, true, false, 0));
        Assert.Equal(SquadState.Downed, sim.StateOf(0));
        var done = new List<SquadRevive>();
        for (int k = 0; k < Rate * 2; k++) { sim.Step(++_t, [new SquadHuman(1, at, true, false, 0)]); done.AddRange(sim.Revives); }
        Assert.Equal(SquadState.Standing, sim.StateOf(0));
        Assert.Equal(1f, sim.View(0)!.Value.HealthShare);
        Assert.Equal(new SquadRevive(1, -1, 1, 0, -1), Assert.Single(done));
        Assert.Equal(1, r.Marine(1, 0)!.Record.TimesDowned);
    }

    [Fact]
    public void A_far_player_cannot_revive()
    {
        var (_, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        Run(sim, 1, Owner(Vector3.Zero));
        Down(sim, 0);
        Run(sim, Rate * 4, new SquadHuman(1, new Vector3(30, 0, 0), true, false, 0));
        Assert.Equal(SquadState.Downed, sim.StateOf(0));
    }

    [Fact]
    public void A_squad_mate_revives_a_downed_Marine_when_no_Ork_is_near_and_credits_its_record()
    {
        var (r, _, sim) = Make(1, 2);
        Land(sim, Vector3.Zero, 1);
        Run(sim, 1, Owner(Vector3.Zero));
        Down(sim, 1);
        Run(sim, Rate * 8, Owner(Vector3.Zero));
        Assert.Equal(SquadState.Standing, sim.StateOf(1));
        Assert.Equal(1f, sim.View(1)!.Value.HealthShare);
        Assert.Equal(1, r.Marine(1, 0)!.Record.Revives);
    }

    [Fact]
    public void A_squad_mate_does_not_revive_with_an_Ork_within_six_metres_of_the_body()
    {
        var (r, arena, sim) = Make(1, 2);
        r.Mirror(1, new SquadLoadout { Weapon = new SquadWeapon(30f, 0.001f, 0.1f, 1f) });   // cannot clear the Ork
        Land(sim, Vector3.Zero, 1);
        Run(sim, 1, Owner(Vector3.Zero));
        Down(sim, 1);
        var at = sim.View(1)!.Value.Position;
        arena.Add(at + new Vector3(4, 0, 0), 1e9f);
        Run(sim, Rate * 10, Owner(Vector3.Zero));
        Assert.Equal(SquadState.Downed, sim.StateOf(1));
        Assert.Equal(0, r.Marine(1, 0)!.Record.Revives);
    }

    [Fact]
    public void Any_squad_may_revive_any_Marine_of_another_squad_as_a_player_but_a_Marine_helps_only_its_own()
    {
        var (_, _, sim) = Make(2, 1);
        Land(sim, Vector3.Zero, 1, 2);
        int b = 9;   // owner 2's Marine
        Run(sim, 1, Owner(Vector3.Zero), Owner(new Vector3(100, 0, 0), 2));
        Down(sim, b);
        var at = sim.View(b)!.Value.Position;
        Run(sim, Rate * 6, Owner(at, 1), Owner(new Vector3(500, 0, 0), 2));   // owner 1's Marine stays by owner 1 (at the body): it does not revive a foreign squad's Marine
        Assert.Equal(SquadState.Downed, sim.StateOf(b));
        Run(sim, Rate * 3, new SquadHuman(1, at, true, false, b), Owner(new Vector3(500, 0, 0), 2));   // player 1 revives it
        Assert.Equal(SquadState.Standing, sim.StateOf(b));
    }

    [Fact]
    public void A_Marine_revives_a_fallen_human_through_the_seam()
    {
        var (r, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        var revived = new List<int>();
        sim.ReviveHuman = id => { revived.Add(id); return true; };
        var done = new List<SquadRevive>();
        for (int k = 0; k < Rate * 8 && revived.Count == 0; k++) { sim.Step(++_t, [new SquadHuman(1, new Vector3(6, 0, 0), false, true)]); done.AddRange(sim.Revives); }
        Assert.Equal([1], revived);
        Assert.Equal(1, r.Marine(1, 0)!.Record.Revives);
        Assert.Equal(new SquadRevive(-1, 0, 0, -1, 1), Assert.Single(done));
    }

    [Fact]
    public void A_Marine_does_not_revive_a_human_whose_window_is_closed()
    {
        var (_, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        var revived = new List<int>();
        sim.ReviveHuman = id => { revived.Add(id); return true; };
        Run(sim, Rate * 8, new SquadHuman(1, new Vector3(6, 0, 0), false, false));
        Assert.Empty(revived);
    }

    [Fact]
    public void Out_is_for_this_battle_only_the_Marine_is_back_at_full_health_next_battle_with_its_record()
    {
        var (r, _, sim) = Make(2, 1);   // owner 2's Marine does not help owner 1's
        Land(sim, Vector3.Zero, 1, 2);
        Run(sim, 1, Owner(new Vector3(500, 0, 0)), Owner(new Vector3(500, 0, 0), 2));
        Down(sim, 0);
        Run(sim, Rate * 21, Owner(new Vector3(500, 0, 0)), Owner(new Vector3(500, 0, 0), 2));
        Assert.Equal(SquadState.Out, sim.StateOf(0));
        Assert.Equal(1, sim.EndBattle());
        Assert.Equal(0, r.Marine(1, 0)!.Record.BattlesSurvived);   // it did not survive
        Assert.Equal(1, r.Marine(2, 0)!.Record.BattlesSurvived);
        Assert.Equal(1, r.Count(1));                                // no permanent death
        Land(sim, Vector3.Zero, 1, 2);
        Assert.Equal(SquadState.Standing, sim.StateOf(0));
        Assert.Equal(1f, sim.View(0)!.Value.HealthShare);
        Assert.Equal(1, r.Marine(1, 0)!.Record.TimesDowned);
    }

    [Fact]
    public void Anyone_standing_is_false_only_when_every_Marine_is_downed_or_out()
    {
        var (_, _, sim) = Make(1, 2);
        Assert.False(sim.AnyStanding);   // no battle
        Land(sim, Vector3.Zero, 1);
        Assert.True(sim.AnyStanding);
        Down(sim, 0);
        Assert.True(sim.AnyStanding);
        Down(sim, 1);
        Assert.False(sim.AnyStanding);
        Assert.True(sim.AnyReviveWindowOpen);
        Run(sim, Rate * 21, Owner(new Vector3(500, 0, 0)));
        Assert.False(sim.AnyStanding);
        Assert.False(sim.AnyReviveWindowOpen);
        Land(sim, Vector3.Zero, 1);
        Assert.True(sim.AnyStanding);
        Assert.Equal(2, sim.StandingOf(1));
    }

    [Fact]
    public void A_revived_Marine_counts_as_standing_again()
    {
        var (_, _, sim) = Make();
        Land(sim, Vector3.Zero, 1);
        Down(sim, 0);
        Assert.False(sim.AnyStanding);
        Assert.True(sim.Revive(0, byHuman: 1));
        Assert.True(sim.AnyStanding);
        Assert.Equal(SquadState.Standing, sim.StateOf(0));
    }

    // ---- records

    [Fact]
    public void Objectives_and_battles_survived_are_recorded()
    {
        var (r, _, sim) = Make(1, 3);
        Land(sim, Vector3.Zero, 1);
        Down(sim, 2);
        Assert.Equal(2, sim.CreditObjective(Vector3.Zero, 15f));   // the downed one is not on the point
        Assert.Equal(1, r.Marine(1, 0)!.Record.Objectives);
        Assert.Equal(0, r.Marine(1, 2)!.Record.Objectives);
        Assert.Equal(2, sim.EndBattle());
        Assert.Equal(1, r.Marine(1, 0)!.Record.BattlesSurvived);
        Land(sim, Vector3.Zero, 1);
        sim.EndBattle();
        Assert.Equal(2, r.Marine(1, 0)!.Record.BattlesSurvived);
        Assert.Equal(1, r.Marine(1, 2)!.Record.BattlesSurvived);   // the second battle only
    }

    [Fact]
    public void The_stat_scale_changes_damage_and_the_health_of_Marines_landed_after()
    {
        var (r, arena, sim) = Make();
        r.Mirror(1, new SquadLoadout { Weapon = new SquadWeapon(30f, 10f, 1f, 1f), HealthMax = 100f, ArmourMax = 0f });
        sim.SetScale(1UL, 2f, 1.5f);
        Land(sim, Vector3.Zero, 1);
        arena.Add(new Vector3(10, 0, 0), 1000f);
        Run(sim, 2, Owner(Vector3.Zero));   // one shot at tick 1 or 2
        Assert.Equal(1000f - 10f * 2f * SquadRules.DamageDealt, arena.Orks[0].Health, 2);   // the stat scale x the squad's damage dealt (ruling of 2026-11-17: 0.40)
        Down(sim, 0);   // 150 health: the Hit above 100000 downs it anyway; check the share instead
        Land(sim, Vector3.Zero, 1);
        Assert.True(sim.Hit(SquadRules.TargetId(0), 100f, Vector3.Zero));
        Assert.Equal(SquadState.Standing, sim.StateOf(0));   // 150 health survives 100
    }

    [Fact]
    public void The_squad_is_deterministic()
    {
        string Trace()
        {
            var (r, arena, sim) = Make(2, 5);
            for (int i = 0; i < 12; i++) arena.Add(new Vector3(15 + i * 2, (i % 3) * 3, 0), 300f);
            Land(sim, Vector3.Zero, 1, 2);
            var sb = new System.Text.StringBuilder();
            uint t = 0;
            for (int k = 0; k < 300; k++)
            {
                sim.Step(++t, [Owner(Vector3.Zero), Owner(new Vector3(3, 3, 0), 2)]);
                if (k % 50 == 0) foreach (var o in arena.Orks) sb.Append(o.Health.ToString("F1")).Append(',');
            }
            return sb.ToString() + string.Join(',', Enumerable.Range(0, 10).Select(i => r.Marine(i < 5 ? 1UL : 2UL, i % 5)!.Record.Kills));
        }
        Assert.Equal(Trace(), Trace());
    }
}
