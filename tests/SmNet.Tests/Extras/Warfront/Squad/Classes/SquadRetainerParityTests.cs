using System.Diagnostics;
using System.Numerics;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Squad;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.Squad.Classes;

/// <summary>
/// Warfront retainers as the owner asked on 2026-11-18 (docs/warfront.md "Retainers"): each takes its owner's exact class and loadout; a Librarian's group is one uniform random roll; a Dreadnought's is shelved
/// until he switches to a squad class; an Assault retainer shoots (its pistol at range, the blade up close) and uses its jump pack. Sim level, against the fake arena of <see cref="SquadClassesTests"/>.
/// </summary>
public class SquadRetainerParityTests(ITestOutputHelper output)
{
    const ulong Key = 7;
    static uint _t;
    static void Run(WarfrontSquadSim sim, int ticks, params SquadHuman[] humans) { for (int k = 0; k < ticks; k++) sim.Step(++_t, humans); }

    static (SquadRosters R, SquadClassBook B, WarfrontSquadSim Sim, ClassArena A) Setup(SquadOwnerLoadout owner, int recruits, ulong runSeed = 12345UL, ulong key = Key)
    {
        var r = new SquadRosters();
        var book = new SquadClassBook(SquadClassesTests.Make(), r, runSeed);
        book.OnOwnerLoadout(key, owner);
        for (int k = 0; k < recruits; k++) Assert.True(book.Recruit(key));
        var arena = new ClassArena();
        return (r, book, new WarfrontSquadSim(r, arena), arena);
    }

    [Fact]
    public void Every_retainer_copies_the_owners_class_weapons_pistol_blade_and_pack_exactly()
    {
        var c = SquadClassesTests.Make();
        // the owner: an assault marine with the second pistol and the power sword
        var choice = new LoadoutChoice(1, Pistol: 1, Melee: 1);
        var (r, _, _, _) = Setup(SquadOwnerLoadout.Standard(choice), 8);
        var want = SquadLoadoutBuilder.FromChoice(c, choice, AppearanceCode.Default)!;
        Assert.Equal(9, r.Count(Key));
        for (int s = 0; s < 9; s++)
        {
            var l = r.Marine(Key, s)!.Loadout;
            Assert.Equal(want.Weapon, l.Weapon); Assert.Equal(want.Sidearm, l.Sidearm); Assert.Equal(want.Jump, l.Jump);
            Assert.Equal(want.HealthMax, l.HealthMax); Assert.Equal(want.ArmourMax, l.ArmourMax);
            Assert.Equal(want.Perks, l.Perks); Assert.Equal(want.Armour, l.Armour); Assert.Equal(want.Colours, l.Colours);
            Assert.Equal("sm - assault marine", l.ClassId);
            Assert.Equal("sm_mp_pistol_plasma_pistol", l.Secondary);
            Assert.Equal("sm_mp_melee_powersword", l.Melee);
            Assert.NotNull(l.Jump);                 // the real pack
            Assert.NotNull(l.Sidearm);              // the pistol that shoots
            Assert.False(l.Sidearm!.Melee); Assert.True(l.Weapon.Melee);
        }
    }

    [Fact]
    public void A_class_change_swaps_the_whole_roster_in_place_without_respawning_names_or_records()
    {
        var (r, book, _, _) = Setup(SquadOwnerLoadout.Standard(new LoadoutChoice(0)), 3);
        r.Rename(Key, 2, "Keeper"); r.Marine(Key, 2)!.Record.Kills = 5;
        var change = book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(1)));
        Assert.Equal(4, change.Changed);
        for (int s = 0; s < 4; s++) Assert.Equal("sm - assault marine", r.Marine(Key, s)!.Loadout.ClassId);
        Assert.Equal("Keeper", r.Marine(Key, 2)!.Name); Assert.Equal(5, r.Marine(Key, 2)!.Record.Kills);
    }

    [Fact]
    public void A_librarian_group_is_one_uniform_random_loadout_fixed_by_the_run_seed_and_key()
    {
        string Group(ulong seed, ulong key, SquadClass cls)
        {
            var (r, _, _, _) = Setup(SquadOwnerLoadout.ForLibrarian(cls), 8, seed, key);
            var set = Enumerable.Range(0, 9).Select(s => { var l = r.Marine(key, s)!.Loadout; return $"{l.ClassId}/{l.Primary}/{l.Secondary}/{l.Melee}/{string.Join(",", l.Perks)}/{l.Sidearm is not null}"; }).Distinct().ToList();
            Assert.Single(set);   // uniform: one class and one loadout for all nine
            return set[0];
        }
        Assert.Equal(Group(1, Key, SquadClass.Tactical), Group(1, Key, SquadClass.Tactical));   // deterministic
        var tactical = Enumerable.Range(1, 40).Select(seed => Group((ulong)seed, Key, SquadClass.Tactical)).Distinct().Count();
        Assert.True(tactical > 1, "the pick is random across seeds");
        Assert.All(new[] { SquadClass.Assault, SquadClass.Tactical, SquadClass.Devastator }, cls => Assert.Contains(cls == SquadClass.Assault ? "assault" : cls == SquadClass.Tactical ? "tactical" : "devastator", Group(3, Key, cls)));
    }

    [Fact]
    public void A_dreadnoughts_retainers_are_shelved_and_come_back_when_he_takes_a_squad_class()
    {
        var (r, book, sim, _) = Setup(SquadOwnerLoadout.Standard(new LoadoutChoice(1)), 2);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        Assert.True(r.IsHeld(Key));
        Assert.Equal(0, sim.Land(Key, 1, Vector3.Zero));
        Run(sim, 5, new SquadHuman(1, Vector3.Zero, true));
        Assert.Equal(0, sim.StandingOf(1)); Assert.False(sim.AnyStanding);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(2)));
        Assert.False(r.IsHeld(Key));
        Assert.Equal(3, sim.Land(Key, 1, Vector3.Zero));
        Assert.Equal("sm - devastator", r.Marine(Key, 0)!.Loadout.ClassId);
    }

    [Fact]
    public void Assault_retainers_shoot_the_pistol_at_range_and_strike_up_close()
    {
        var (_, _, sim, arena) = Setup(SquadOwnerLoadout.Standard(new LoadoutChoice(1)), 3);
        Assert.Equal(4, sim.Land(Key, 1, Vector3.Zero));
        var far = arena.Add(new Vector3(15f, 0f, 0f));    // outside the blade's reach, inside the pistol's
        far.Health = 1e6f;
        bool shot = false, shotMelee = true;
        var owner = new SquadHuman(1, Vector3.Zero, true);
        for (int k = 0; k < 60 && !shot; k++)
        {
            Run(sim, 1, owner);
            for (int i = 0; i < 4; i++)
                if (sim.View(i) is { Firing: true } v && Vector3.Distance(v.Position, far.Pos) > MarineRules.MeleeRange + 0.5f) { shot = true; shotMelee = sim.FiringMelee(i); }
        }
        Assert.True(shot, "an assault retainer fires at an Ork 15 m away");
        Assert.False(shotMelee, "the round is the pistol's, not a blow");
        Assert.True(far.Health < 1e6f, "and it lands");
        // up close: the blade
        far.Pos = new Vector3(1.5f, 0f, 0f);
        for (int i = 0; i < 4; i++) sim.Order(1, new TfwCommandRecord((ushort)(100 + i), TfwCommand.Hold, 0));
        bool blow = false;
        for (int k = 0; k < 90 && !blow; k++)
        {
            Run(sim, 1, owner);
            for (int i = 0; i < 4; i++) if (sim.View(i) is { Firing: true } v && Vector3.Distance(v.Position, far.Pos) <= MarineRules.MeleeRange + 0.1f && sim.FiringMelee(i)) blow = true;
        }
        Assert.True(blow, "within reach a retainer strikes");
    }

    [Fact]
    public void Assault_retainers_leap_with_their_packs_when_the_owner_runs_ahead()
    {
        var (_, _, sim, _) = Setup(SquadOwnerLoadout.Standard(new LoadoutChoice(1)), 3);
        sim.Land(Key, 1, Vector3.Zero);
        bool air = false;
        var owner = new SquadHuman(1, new Vector3(60f, 0f, 0f), true);
        for (int k = 0; k < 120; k++) { Run(sim, 1, owner); for (int i = 0; i < 4; i++) air |= sim.IsAirborne(i); }
        Assert.True(air, "the pack carries a retainer after its owner");
        Assert.True(sim.FuelOf(0) <= sim.Rosters.Marine(Key, 0)!.Loadout.Jump!.MaxPower);
    }

    [Fact]
    public void Thirty_six_assault_retainers_in_a_fight_cost_little_per_tick()
    {
        var c = SquadClassesTests.Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 1UL);
        var arena = new ClassArena();
        var sim = new WarfrontSquadSim(r, arena);
        var humans = new List<SquadHuman>();
        for (int o = 0; o < 4; o++)
        {
            ulong key = (ulong)(100 + o);
            book.OnOwnerLoadout(key, SquadOwnerLoadout.Standard(new LoadoutChoice(1)));
            for (int k = 0; k < 8; k++) book.Recruit(key);
            Assert.Equal(9, sim.Land(key, o + 1, new Vector3(o * 30f, 0f, 0f)));
            humans.Add(new SquadHuman(o + 1, new Vector3(o * 30f, 0f, 0f), true));
        }
        for (int k = 0; k < 40; k++) arena.Add(new Vector3(10f + k % 10 * 12f, k / 10 * 9f - 12f, 0f)).Health = 1e6f;
        var span = humans.ToArray();
        for (int k = 0; k < 60; k++) sim.Step(++_t, span);   // warm up
        var w = Stopwatch.StartNew();
        const int ticks = 600;
        for (int k = 0; k < ticks; k++) sim.Step(++_t, span);
        double ms = w.Elapsed.TotalMilliseconds / ticks;
        output.WriteLine($"squad step, 36 assault retainers vs 40 Orks (fake arena): {ms:F3} ms a tick");
        Assert.True(ms < 5.0, $"{ms:F3} ms a tick");
    }
}
