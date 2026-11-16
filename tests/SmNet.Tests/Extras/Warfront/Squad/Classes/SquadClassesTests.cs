using System.Numerics;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Squad;

namespace SmNet.Tests.Extras.Warfront.Squad.Classes;

/// <summary>A flat floor with a band of no floor (a pit), a few scripted Orks and a route with one jump link: the squad sim's class rules against a fake (no World, no original data).</summary>
sealed class ClassArena : ISquadArena, ISquadJumpArena
{
    public sealed class Ork { public ushort Id; public Vector3 Pos; public float Health = 1000f; public bool Alive => Health > 0f; }
    public readonly List<Ork> Orks = [];
    public int TickRate => 30;
    /// <summary>Flat x range with no floor.</summary>
    public (float From, float To)? Pit;
    public IReadOnlyList<SquadRouteStep>? Route;
    public int RouteCalls;
    /// <summary>A roof over the middle of a flight: no clear line straight up.</summary>
    public bool Roof;

    public Ork Add(Vector3 pos) { var o = new Ork { Id = (ushort)(Orks.Count + 1), Pos = pos }; Orks.Add(o); return o; }

    public int QueryEnemies(Vector3 centre, float radius, Span<ushort> into)
    {
        int n = 0;
        foreach (var o in Orks.Where(o => o.Alive && MathF.Sqrt((o.Pos.X - centre.X) * (o.Pos.X - centre.X) + (o.Pos.Y - centre.Y) * (o.Pos.Y - centre.Y)) <= radius).OrderBy(o => o.Id))
            if (n < into.Length) into[n++] = o.Id;
        return n;
    }

    public MusouEnemyType EnemyType = MusouEnemyType.Nob;
    public bool TryEnemyType(ushort id, out MusouEnemyType type) { type = EnemyType; return Orks.Any(o => o.Id == id); }

    public bool TryEnemy(ushort id, out Vector3 position, out SquadEnemyClass kind)
    {
        var o = Orks.FirstOrDefault(x => x.Id == id && x.Alive);
        position = o?.Pos ?? default; kind = SquadEnemyClass.Fodder;
        return o is not null;
    }

    public bool DamageEnemy(ushort id, float amount, Vector3 push, out bool killed)
    {
        killed = false;
        var o = Orks.FirstOrDefault(x => x.Id == id && x.Alive);
        if (o is null) return false;
        o.Health -= amount; killed = o.Health <= 0f;
        return true;
    }

    public bool HasSight(Vector3 from, Vector3 to) => !(Roof && to.Z - from.Z > 2f);

    public bool Ground(Vector3 p, out Vector3 ground)
    {
        ground = new Vector3(p.X, p.Y, 0f);
        if (Pit is { } pit && p.X >= pit.From && p.X <= pit.To) ground.X = p.X - pit.From < pit.To - p.X ? pit.From - 0.1f : pit.To + 0.1f;   // the nearest floor
        return true;
    }

    public bool CanStep(Vector3 from, Vector3 to, out Vector3 placed) { placed = new Vector3(to.X, to.Y, 0f); return !(Pit is { } pit && to.X >= pit.From && to.X <= pit.To); }
    public bool CanWalkLine(Vector3 from, Vector3 to) => true;
    public IReadOnlyList<Vector3>? FindPath(Vector3 from, Vector3 to) => [to];
    public Vector3? FlowNext(Vector3 from, Vector3 point) => point;
    public void Tick() { }
    public IReadOnlyList<SquadRouteStep>? FindRoute(Vector3 from, Vector3 to) { RouteCalls++; return Route; }
}

public class SquadClassesTests
{
    static WeaponSpec Gun(string name, WeaponSlot slot, float damage = 20f) => new(name, slot, TriggerKind.FullAuto, damage, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static ThrownSpec Thrown(string name) => new(name, 1, 1, 1f, 1f, new ProjectileSpec("p", 1, 1, "raycast", 0, [], false, false, false, 0, 0, 0, 0, null, null));
    static PerkSpec Perk(string name) => new(name, name, PerkKind.Class, 1);

    static readonly string[] TacticalPerks = ["mp_perk_friendly_spawn_target", "mp_perk_secondary_weapon", "mp_perk_shield_recharge", "mp_perk_weap_bolter_accuracy", "mp_perk_weap_storm_bolter_hip_fire"];
    static readonly string[] AssaultPerks = ["mp_perk_improved_bullrush", "mp_perk_jump_pack_reduced_cooldown", "mp_perk_ranged_damage_resistance", "mp_perk_weap_chainsword_heal_on_hit"];
    static readonly string[] HeavyPerks = ["mp_perk_melee_damage_resistance", "mp_perk_iron_halo", "mp_perk_shield_regen_delay"];

    /// <summary>Space Marine classes 0 tactical, 1 assault (jump pack), 2 devastator; the Chaos side is left out (the squad is Space Marines).</summary>
    static SimConfig Make(float packPower = 100f)
    {
        var weapons = new List<WeaponSpec>();
        var perks = TacticalPerks.Concat(AssaultPerks).Concat(HeavyPerks).Select(Perk).ToList();
        int P(string name) => perks.FindIndex(p => p.Name == name);
        var thrown = new List<ThrownSpec> { Thrown("sm_mp_eq_frag_grenade"), Thrown("sm_mp_eq_krak_grenade"), Thrown("sm_mp_eq_stimpack") };
        int W(string name, WeaponSlot slot, float damage = 20f) { weapons.Add(Gun(name, slot, damage)); return weapons.Count - 1; }
        var classes = new List<ClassLoadout>();
        foreach (var (side, team) in new[] { ("sm", Team.SpaceMarines), ("csm", Team.Chaos) })
        {
            int boltPistol = W($"{side}_mp_pistol_bolt_pistol", WeaponSlot.Pistol), plasmaPistol = W($"{side}_mp_pistol_plasma_pistol", WeaponSlot.Pistol);
            var primaries = new List<int> { W($"{side}_mp_ranged_bolter", WeaponSlot.Ranged), W($"{side}_mp_ranged_sniper_rifle", WeaponSlot.Ranged, 90f), W($"{side}_mp_ranged_plasma_gun", WeaponSlot.Ranged),
                W($"{side}_mp_ranged_melta", WeaponSlot.Ranged), W($"{side}_mp_ranged_storm_bolter", WeaponSlot.Ranged) };
            classes.Add(new ClassLoadout($"{side} - tactical", primaries[0], boltPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = primaries, PistolChoices = [boltPistol], EquipmentChoices = [0, 1, 2], Side = team, Role = "tactical",
                PerkChoices = TacticalPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_friendly_spawn_target"),
            });
            var melees = new List<MeleeCombo> { new($"{side}_mp_melee_chainsword", SimConfig.PlaceholderMeleeSteps), new($"{side}_mp_melee_powersword", SimConfig.PlaceholderMeleeSteps),
                new(side == "sm" ? "sm_mp_melee_2hand_thunder_hammer" : "csm_mp_melee_2hand_power_mace", SimConfig.PlaceholderMeleeSteps) };
            classes.Add(new ClassLoadout($"{side} - assault marine", -1, boltPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PistolChoices = [boltPistol, plasmaPistol], MeleeChoices = melees, EquipmentChoices = [0, 1, 2], Side = team, Role = "assault",
                PerkChoices = AssaultPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_improved_bullrush"), JumpPack = 0,
            });
            int devPistol = W($"{side}_mp_pistol_bolt_pistol_devastator", WeaponSlot.Pistol);
            var heavies = new List<int> { W($"{side}_mp_exotic_heavy_bolter", WeaponSlot.Ranged), W($"{side}_mp_exotic_las_cannon", WeaponSlot.Ranged, 200f), W($"{side}_mp_exotic_plasma_cannon", WeaponSlot.Ranged) };
            classes.Add(new ClassLoadout($"{side} - devastator", heavies[0], devPistol, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = heavies, PistolChoices = [devPistol], EquipmentChoices = [0, 1, 2], Side = team, Role = "heavy",
                PerkChoices = HeavyPerks.Select(P).ToList(), DefaultPerk = P("mp_perk_melee_damage_resistance"),
            });
        }
        var pack = new JumpPackSpec("sm_mp_abil_jumppack", [new DistancePoint(0.1f, 2f), new DistancePoint(1f, 8f)], [new DistancePoint(0.1f, 10f), new DistancePoint(1f, 50f)],
            packPower, 10f, 20f, 5f, 0.3f, 15f, 10f, 0.5f, 0.1f)
        {
            PoundEnabled = true,
            Pound = new SplashSpec(4f, new DamageSpec(80f, 0f, 1f, 1f, 1f, 1f, false), [], false, true, ""),
            PoundRecovery = 0.4f,
        };
        return new SimConfig
        {
            Weapons = weapons, Thrown = thrown, Classes = classes, Perks = perks, JumpPacks = [pack],
            HealthMax = 150f, ShieldMax = 120f, ShieldRegenDelay = 3f, ShieldRegenRate = 20f,
        };
    }

    const ulong Key = 7;
    static uint _t;
    static void Run(WarfrontSquadSim sim, int ticks, params SquadHuman[] humans) { for (int k = 0; k < ticks; k++) sim.Step(++_t, humans); }
    static SquadHuman Owner(Vector3 at, int id = 1) => new(id, at, true);

    static (SquadRosters R, SquadClassBook B, WarfrontSquadSim Sim, ClassArena A) Setup(SimConfig c, SquadOwnerLoadout owner, int recruits = 0)
    {
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 12345UL);
        book.OnOwnerLoadout(Key, owner);
        for (int k = 0; k < recruits; k++) Assert.True(book.Recruit(Key));
        var arena = new ClassArena();
        var sim = new WarfrontSquadSim(r, arena);
        return (r, book, sim, arena);
    }

    static SquadOwnerLoadout Assault(SquadClass cls = SquadClass.Assault) => SquadOwnerLoadout.ForLibrarian(cls);

    // ---- mirror

    [Fact]
    public void A_standard_retainer_mirrors_class_weapons_body_and_colours_and_keeps_name_and_record()
    {
        var c = Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 1UL);
        var look = AppearanceCode.Default.WithPalette(ArmourSlot.Head, 3, 4, 5).WithPiece(ArmourSlot.Torso, 2);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0, 1), look));   // tactical, the sniper rifle
        Assert.True(book.Recruit(Key));
        r.Rename(Key, 0, "Keeper"); r.Marine(Key, 0)!.Record.Kills = 12;
        foreach (int slot in new[] { 0, 1 })
        {
            var l = r.Marine(Key, slot)!.Loadout;
            Assert.Equal("sm - tactical", l.ClassId);
            Assert.Equal("sm_mp_ranged_sniper_rifle", l.Primary);
            Assert.Equal(150f, l.HealthMax); Assert.Equal(120f, l.ArmourMax);              // from the class data, not the W9 placeholders
            Assert.Equal(20f, l.ArmourRegenRate); Assert.Equal(3f, l.ArmourRegenDelay);
            Assert.Equal(90f, l.Weapon.Damage); Assert.Equal(SquadClassRules.RangeCap, l.Weapon.Range);
            Assert.Equal(3u | 4u << 8 | 5u << 16, l.Colours[(int)ArmourSlot.Head]);
            Assert.Contains("torso:2", l.Armour);
            Assert.Null(l.Jump);
        }
        // the player changes class: the squad follows, names and records stay
        var change = book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(2), look));
        Assert.True(change.Applied); Assert.Equal(2, change.Changed);
        Assert.Equal("sm - devastator", r.Marine(Key, 1)!.Loadout.ClassId);
        Assert.Equal("Keeper", r.Marine(Key, 0)!.Name); Assert.Equal(12, r.Marine(Key, 0)!.Record.Kills);
        // the assault class has the pack and a melee weapon
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(1), look));
        var a = r.Marine(Key, 0)!.Loadout;
        Assert.True(a.Weapon.Melee); Assert.NotNull(a.Jump);
        Assert.Equal(50f, a.Jump!.FullCost); Assert.Equal(8f, a.Jump.Height); Assert.True(a.Jump.Range > 10f);
        Assert.Equal("Keeper", r.Marine(Key, 0)!.Name);
    }

    // ---- Librarian rolls

    [Fact]
    public void Librarian_rolls_are_stable_per_selection_reroll_on_change_and_never_mix_classes()
    {
        var c = Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 99UL);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Tactical));
        for (int k = 0; k < 8; k++) book.Recruit(Key);
        string Snapshot() => string.Join("|", Enumerable.Range(0, 9).Select(s => { var l = r.Marine(Key, s)!.Loadout; return $"{l.ClassId}/{l.Primary}/{l.Secondary}/{l.Melee}/{string.Join(",", l.Perks)}"; }));
        string tactical = Snapshot();
        Assert.All(Enumerable.Range(0, 9), s => Assert.Equal("sm - tactical", r.Marine(Key, s)!.Loadout.ClassId));   // no mixed squads
        Assert.Equal(0, book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Tactical)).Changed);   // same selection: nothing moves
        Assert.Equal(tactical, Snapshot());
        // a Marine's roll is a function of (run seed, key, slot, selection)
        Assert.Equal(SquadLoadoutBuilder.RollSeed(99, Key, 3, 2), SquadLoadoutBuilder.RollSeed(99, Key, 3, 2));
        Assert.NotEqual(SquadLoadoutBuilder.RollSeed(99, Key, 3, 2), SquadLoadoutBuilder.RollSeed(99, Key, 3, 1));
        Assert.NotEqual(SquadLoadoutBuilder.RollSeed(99, Key, 3, 2), SquadLoadoutBuilder.RollSeed(99, Key, 4, 2));
        Assert.NotEqual(SquadLoadoutBuilder.RollSeed(99, Key, 3, 2), SquadLoadoutBuilder.RollSeed(98, Key, 3, 2));
        // the owner's presets are spread over the squad (three tactical presets, nine Marines)
        Assert.True(Enumerable.Range(0, 9).Select(s => r.Marine(Key, s)!.Loadout.Primary + r.Marine(Key, s)!.Loadout.Secondary).Distinct().Count() > 1);
        // changing the selection rerolls the whole squad, to the new class
        var change = book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Devastator));
        Assert.Equal(9, change.Changed);
        Assert.All(Enumerable.Range(0, 9), s => Assert.Equal("sm - devastator", r.Marine(Key, s)!.Loadout.ClassId));
        Assert.All(Enumerable.Range(0, 9), s => Assert.Contains(r.Marine(Key, s)!.Loadout.Primary, new[] { "sm_mp_exotic_las_cannon", "sm_mp_exotic_plasma_cannon" }));   // the heavy presets
        Assert.NotEqual(tactical, Snapshot());
        // and back: the first selection's rolls are the same again
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Tactical));
        Assert.Equal(tactical, Snapshot());
        // class armour (no own pieces), in the player's colours
        var look = AppearanceCode.Default.WithPalette(ArmourSlot.Head, 9, 8, 7);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Assault, look));
        var l0 = r.Marine(Key, 0)!.Loadout;
        Assert.Equal(["class:assault"], l0.Armour); Assert.Equal(9u | 8u << 8 | 7u << 16, l0.Colours[0]);
        Assert.NotNull(l0.Jump);
    }

    // ---- Dreadnought

    [Fact]
    public void A_dreadnought_roster_is_held_and_restored_and_the_free_retainer_comes_once()
    {
        var c = Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 5UL);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0)));
        book.Recruit(Key); r.Rename(Key, 1, "Remus");
        Assert.Equal(2, r.Count(Key));
        var held = book.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        Assert.True(held.Held); Assert.True(r.IsHeld(Key)); Assert.Equal(2, r.Count(Key));
        var sim = new WarfrontSquadSim(r, new ClassArena());
        Assert.Equal(0, sim.Land(Key, 1, Vector3.Zero));            // no active retainers
        var back = book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(2)));
        Assert.True(back.Released); Assert.False(back.FreeRetainer);   // he did not start as a Dreadnought
        Assert.Equal(2, r.Count(Key)); Assert.Equal("Remus", r.Marine(Key, 1)!.Name);
        Assert.Equal("sm - devastator", r.Marine(Key, 1)!.Loadout.ClassId);
        Assert.Equal(2, sim.Land(Key, 1, Vector3.Zero));
    }

    [Fact]
    public void A_player_who_started_as_dreadnought_gets_one_free_retainer_once_also_across_a_save()
    {
        var c = Make();
        var r = new SquadRosters();
        var book = new SquadClassBook(c, r, 5UL);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        Assert.Equal(0, r.Count(Key)); Assert.True(r.IsHeld(Key));
        var first = book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0)));
        Assert.True(first.FreeRetainer); Assert.Equal(1, r.Count(Key)); Assert.False(r.IsHeld(Key));
        // back to the Dreadnought and out again: no second one
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        Assert.False(book.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0))).FreeRetainer);
        Assert.Equal(1, r.Count(Key));
        // save and restore (rosters and class state), then the same again
        var r2 = new SquadRosters(); r2.Import(r.Export());
        var book2 = new SquadClassBook(c, r2, 5UL); book2.Import(book.Export());
        Assert.True(book2.FreeRetainerGiven(Key));
        book2.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        Assert.False(book2.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0))).FreeRetainer);
        Assert.Equal(1, r2.Count(Key));
        // a save taken before the first switch still has the free retainer to give
        var r3 = new SquadRosters(); var book3 = new SquadClassBook(c, r3, 5UL);
        book3.OnOwnerLoadout(Key, SquadOwnerLoadout.Dreadnought());
        var r4 = new SquadRosters(); r4.Import(r3.Export());
        var book4 = new SquadClassBook(c, r4, 5UL); book4.Import(book3.Export());
        Assert.True(book4.OnOwnerLoadout(Key, SquadOwnerLoadout.Standard(new LoadoutChoice(0))).FreeRetainer);
        Assert.Equal(1, r4.Count(Key));
        // an eligible player's roster starts with one retainer and gets no free one
        var r5 = new SquadRosters(); var book5 = new SquadClassBook(c, r5, 5UL);
        Assert.False(book5.OnOwnerLoadout(9UL, SquadOwnerLoadout.Standard(new LoadoutChoice(0))).FreeRetainer);
        Assert.Equal(1, r5.Count(9UL));
    }

    // ---- regeneration

    [Fact]
    public void Armour_regenerates_after_the_class_delay_and_not_past_its_maximum()
    {
        var (r, _, sim, _) = Setup(Make(), SquadOwnerLoadout.Standard(new LoadoutChoice(0)));
        sim.Land(Key, 1, Vector3.Zero);
        var human = Owner(Vector3.Zero);
        Assert.True(sim.Hit(SquadRules.TargetId(0), 600f, Vector3.Zero));   // 60 after the puppet share: all of it on the armour
        Assert.Equal(60f, sim.BodyOf(0).Armour, 1);
        Run(sim, 60, human);                                                 // 2 s: inside the 3 s delay
        Assert.Equal(60f, sim.BodyOf(0).Armour, 1);
        Run(sim, 120, human);                                                // 6 s in: 3 s of regeneration at 20 a second
        Assert.InRange(sim.BodyOf(0).Armour, 110f, 120f);
        Run(sim, 300, human);
        Assert.Equal(120f, sim.BodyOf(0).Armour, 3);
        Assert.Equal(150f, sim.BodyOf(0).Health, 3);
        Assert.Equal(120f, r.Marine(Key, 0)!.Loadout.ArmourMax);
        sim.Hit(SquadRules.TargetId(0), 100f, Vector3.Zero);                 // a hit restarts the delay
        float after = sim.BodyOf(0).Armour;
        Run(sim, 30, human);
        Assert.Equal(after, sim.BodyOf(0).Armour, 3);
    }

    // ---- the jump-pound order

    static (WarfrontSquadSim Sim, ClassArena Arena, SquadRosters R) Squad(int marines, float packPower = 100f)
    {
        var (r, _, sim, arena) = Setup(Make(packPower), Assault(), marines - 1);
        Assert.Equal(marines, sim.Land(Key, 1, Vector3.Zero));
        return (sim, arena, r);
    }

    [Fact]
    public void The_jump_pound_lands_each_marine_on_its_own_floor_point_within_5_m_staggered_in_time()
    {
        var (sim, arena, _) = Squad(9);
        arena.Pit = (12f, 14f);                                   // a gap with no floor right by the mark
        Run(sim, 1, Owner(Vector3.Zero));
        var mark = new Vector3(10f, 0f, 0f);
        var plan = sim.OrderJumpPound(1, mark);
        Assert.NotNull(plan);
        Assert.Equal(9, plan!.Entries.Count);
        Assert.All(plan.Entries, e => Assert.True(e.Leaps));
        Assert.All(plan.Entries, e =>
        {
            Assert.True(Flat(e.Landing - plan.Mark) <= SquadClassRules.PoundRadius + 1e-3f);          // within 5 m of the mark
            Assert.False(e.Landing.X >= 12f && e.Landing.X <= 14f);                                    // on the graph: never in the pit
            arena.Ground(e.Landing, out var g); Assert.Equal(g, e.Landing);
        });
        for (int i = 0; i < plan.Entries.Count; i++)                                                  // no stacking
            for (int j = i + 1; j < plan.Entries.Count; j++)
                Assert.True(Flat(plan.Entries[i].Landing - plan.Entries[j].Landing) >= SquadClassRules.PoundSpacing - 1e-3f, $"{i} and {j} share a point");
        var ticks = plan.Entries.Select(e => e.ImpactTick).OrderBy(t => t).ToList();
        int stagger = SquadClassRules.StaggerTicks(30);
        Assert.True(stagger * (1f / 30f) >= SquadClassRules.PoundStagger - 1e-4f);
        for (int i = 1; i < ticks.Count; i++) Assert.True(ticks[i] - ticks[i - 1] >= stagger);        // staggered, never two on one tick
        // run it: every Marine lands where planned, on the planned tick, and the pack's power was spent
        var human = Owner(Vector3.Zero);
        var seen = new Dictionary<int, SquadPoundImpact>();
        for (int k = 0; k < 400; k++) { sim.Step(++_t, [human]); foreach (var im in sim.PoundImpacts) seen[im.Index] = im; }
        Assert.Equal(9, seen.Count);
        foreach (var e in plan.Entries)
        {
            Assert.Equal(e.Landing, seen[e.Index].Position);
            Assert.Equal(e.ImpactTick, seen[e.Index].Tick);
        }
        Assert.All(plan.Entries, e => Assert.False(sim.IsAirborne(e.Index)));
    }

    static float Flat(Vector3 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    [Fact]
    public void The_pound_blast_hurts_the_orks_it_lands_among_and_credits_the_kill()
    {
        var (sim, arena, r) = Squad(3);
        Run(sim, 1, Owner(Vector3.Zero));
        var ork = arena.Add(new Vector3(10f, 0.5f, 0f)); ork.Health = 50f;
        var far = arena.Add(new Vector3(60f, 0f, 0f)); far.Health = 50f;
        Assert.NotNull(sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f)));
        var human = Owner(Vector3.Zero);
        int kills = 0;
        for (int k = 0; k < 300; k++) { sim.Step(++_t, [human]); kills += sim.Kills.Count; }
        Assert.False(ork.Alive); Assert.True(far.Alive);
        Assert.Equal(1, kills);
        Assert.Equal(1, Enumerable.Range(0, 3).Sum(s => r.Marine(Key, s)!.Record.Kills));
    }

    [Fact]
    public void The_packs_power_limits_the_pound_a_marine_without_it_walks_and_a_second_order_waits_for_recharge()
    {
        var (sim, _, _) = Squad(3, packPower: 40f);   // a full jump costs 50
        Run(sim, 1, Owner(Vector3.Zero));
        var plan = sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        Assert.All(plan!.Entries, e => { Assert.False(e.Leaps); Assert.Equal(0u, e.ImpactTick); });
        var human = Owner(Vector3.Zero);
        Run(sim, 200, human);
        Assert.Empty(sim.PoundImpacts);
        Assert.True(Enumerable.Range(0, 3).All(i => sim.View(i)!.Value.Position.X > 4f));   // they walked toward the mark
        // a full pack: two orders in a row, the second after only a short time, uses the power left
        var (sim2, _, _) = Squad(2);
        Run(sim2, 1, human);
        var p1 = sim2.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        Run(sim2, 60, human);
        float left = sim2.FuelOf(p1!.Entries[0].Index);
        Assert.True(left < 100f);                                    // the first jump spent power
        var p2 = sim2.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        Assert.Equal(left >= 50f - 1e-3f, p2!.Entries[0].Leaps);
    }

    [Fact]
    public void Only_jump_pack_marines_take_part_and_the_order_needs_a_floor_at_the_mark()
    {
        var (_, _, sim, _) = Setup(Make(), SquadOwnerLoadout.Standard(new LoadoutChoice(0)));
        sim.Land(Key, 1, Vector3.Zero);
        Assert.Null(sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f)));   // a tactical squad has no pack
        Assert.Null(sim.OrderJumpPound(99, new Vector3(10f, 0f, 0f)));  // no squad
    }

    // ---- jump follow

    [Fact]
    public void An_assault_marine_leaps_after_a_far_owner_and_lands_on_the_graph_without_stacking()
    {
        var (sim, arena, _) = Squad(4);
        var human = Owner(new Vector3(40f, 0f, 0f));
        bool air = false;
        for (int k = 0; k < 60; k++) { sim.Step(++_t, [human]); air |= Enumerable.Range(0, 4).Any(sim.IsAirborne); }
        Assert.True(air);
        for (int k = 0; k < 300; k++)
        {
            sim.Step(++_t, [human]);
            for (int i = 0; i < 4; i++) { var p = sim.View(i)!.Value.Position; if (!sim.IsAirborne(i)) { arena.Ground(p, out var g); Assert.Equal(g.Z, p.Z, 2); } }
        }
        Assert.All(Enumerable.Range(0, 4), i => Assert.True(Flat(sim.View(i)!.Value.Position - human.Position) < 14f));
    }

    [Fact]
    public void A_walk_that_ends_at_a_jump_link_continues_by_the_links_far_side()
    {
        var (sim, arena, _) = Squad(1);
        arena.Route = [new SquadRouteStep(new Vector3(5, 0, 0), false), new SquadRouteStep(new Vector3(12, 0, 0), true)];
        sim.JumpArena = arena;
        var human = Owner(new Vector3(80f, 0f, 0f));   // beyond one jump
        arena.Pit = (15f, 30f);                         // the straight leap toward the owner would land in the pit: no valid landing, so the route's link is used
        bool air = false;
        for (int k = 0; k < 40; k++) { sim.Step(++_t, [human]); air |= sim.IsAirborne(0); }
        Assert.True(arena.RouteCalls > 0);
        Assert.True(air);
    }

    [Fact]
    public void A_marine_knocked_down_in_flight_lies_on_the_floor_and_a_battle_end_clears_flights()
    {
        var (sim, _, _) = Squad(2);
        Run(sim, 1, Owner(Vector3.Zero));
        sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        var human = Owner(Vector3.Zero);
        int air = -1;
        for (int k = 0; k < 200 && air < 0; k++) { sim.Step(++_t, [human]); for (int i = 0; i < 2; i++) if (sim.IsAirborne(i)) air = i; }
        Assert.True(air >= 0);
        Assert.True(sim.Hit(SquadRules.TargetId(air), 100000f, Vector3.Zero));
        Assert.False(sim.IsAirborne(air));
        Assert.Equal(0f, sim.View(air)!.Value.Position.Z, 3);
        sim.EndBattle();
        Assert.False(sim.IsAirborne(0));
    }

    // ---- lead additions: Rebind and the view's fields

    [Fact]
    public void Rebind_changes_only_the_player_id_where_Land_would_refill_the_squad()
    {
        var (_, _, sim, _) = Setup(Make(), SquadOwnerLoadout.Standard(new LoadoutChoice(0)), 1);
        Assert.Equal(2, sim.Land(Key, 1, new Vector3(5f, 0f, 0f)));
        Assert.True(sim.Order(1, new TfwCommandRecord(1, TfwCommand.Hold, 0)));
        Run(sim, 5, Owner(new Vector3(5f, 0f, 0f)));
        sim.Hit(SquadRules.TargetId(0), 700f, Vector3.Zero);   // armour 120 -> 50
        sim.Hit(SquadRules.TargetId(1), 1000000f, Vector3.Zero);   // downed
        var before0 = sim.View(0)!.Value; var armour = sim.BodyOf(0).Armour;
        Assert.True(sim.Rebind(Key, 9));
        Assert.False(sim.Rebind(99UL, 9));
        Assert.Equal(before0, sim.View(0)!.Value);
        Assert.Equal(armour, sim.BodyOf(0).Armour);
        Assert.Equal(SquadState.Downed, sim.StateOf(1));
        Assert.Equal(SquadOrder.Hold, sim.OrderOf(9));
        Assert.True(sim.Order(9, new TfwCommandRecord(2, TfwCommand.Follow, 0)));   // the new id commands it, the old one does not
        Assert.False(sim.Order(1, new TfwCommandRecord(3, TfwCommand.Hold, 0)));
        Assert.Equal(1, sim.StandingOf(9));
        // Land is the real landing: it stands everyone up at full strength
        Assert.Equal(2, sim.Land(Key, 9, new Vector3(5f, 0f, 0f)));
        Assert.Equal(120f, sim.BodyOf(0).Armour, 3);
        Assert.Equal(SquadState.Standing, sim.StateOf(1));
    }

    [Fact]
    public void The_marine_view_shows_firing_target_and_revive_progress()
    {
        var (_, _, sim, arena) = Setup(Make(), SquadOwnerLoadout.Standard(new LoadoutChoice(0)), 1);
        sim.Land(Key, 1, Vector3.Zero);
        var ork = arena.Add(new Vector3(15f, 0f, 0f));
        var human = Owner(Vector3.Zero);
        Assert.False(sim.View(0)!.Value.Firing); Assert.Equal(-1, sim.View(0)!.Value.Target); Assert.Equal(0f, sim.View(0)!.Value.ReviveShare);
        bool firing = false; int target = -2;
        for (int k = 0; k < 30; k++) { sim.Step(++_t, [human]); var v = sim.View(0)!.Value; if (v.Firing) { firing = true; target = v.Target; } }
        Assert.True(firing); Assert.Equal(ork.Id, target);
        ork.Health = 0f;
        Run(sim, 10, human);
        Assert.False(sim.View(0)!.Value.Firing);
        Assert.True(sim.Hit(SquadRules.TargetId(1), 1000000f, Vector3.Zero));
        var holder = new SquadHuman(2, sim.View(1)!.Value.Position, true, false, 1);
        Run(sim, 30, human, holder);                                   // 1 s of 2 s
        Assert.InRange(sim.View(1)!.Value.ReviveShare, 0.4f, 0.6f);
    }

    // ---- second pass (review 1)

    [Fact]
    public void Route_searches_are_one_a_second_per_squad_and_kept_until_the_owner_moves_even_when_no_landing_ever_works()
    {
        var (sim, arena, _) = Squad(3);
        arena.Pit = (15f, 200f);                      // every straight leap lands in the pit
        arena.Route = [new SquadRouteStep(new Vector3(5, 0, 0), false), new SquadRouteStep(new Vector3(20, 0, 0), true)];   // and the link's far side is in it too
        sim.JumpArena = arena;
        var still = Owner(new Vector3(300f, 0f, 0f));
        for (int k = 0; k < 300; k++) sim.Step(++_t, [still]);
        Assert.Equal(1, arena.RouteCalls); Assert.Equal(1, sim.RouteSearches);          // the failed answer is kept
        Assert.False(Enumerable.Range(0, 3).Any(sim.IsAirborne));
        for (int k = 0; k < 300; k++) sim.Step(++_t, [Owner(new Vector3(300f + (k / 15) * 3f, 0f, 0f))]);   // the owner moves 3 m every half second
        Assert.InRange(arena.RouteCalls, 2, 12);                                          // ten seconds: at most one a second
    }

    [Fact]
    public void Firing_is_held_for_0_3_s_after_a_round()
    {
        var c = Make();
        var r = new SquadRosters(); r.AddOwner(Key);
        r.Mirror(Key, new SquadLoadout { Weapon = new SquadWeapon(40f, 1f, 5f, 1f) });   // one round every 5 s
        var arena = new ClassArena(); var ork = arena.Add(new Vector3(15f, 0f, 0f)); ork.Health = 1e6f;
        var sim = new WarfrontSquadSim(r, arena);
        sim.Land(Key, 1, Vector3.Zero);
        var human = Owner(Vector3.Zero);
        int run = 0, best = 0;
        for (int k = 0; k < 120; k++) { sim.Step(++_t, [human]); if (sim.View(0)!.Value.Firing) { run++; best = Math.Max(best, run); } else run = 0; }
        Assert.Equal(9, best);                           // 0.3 s at 30 Hz
    }

    [Fact]
    public void The_view_shows_a_flight_as_airborne()
    {
        var (sim, _, _) = Squad(2);
        Run(sim, 1, Owner(Vector3.Zero));
        Assert.False(sim.View(0)!.Value.Airborne);
        sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        bool air = false;
        for (int k = 0; k < 200; k++) { sim.Step(++_t, [Owner(Vector3.Zero)]); air |= sim.View(0)!.Value.Airborne; }
        Assert.True(air); Assert.False(sim.View(0)!.Value.Airborne);
    }

    [Fact]
    public void The_squad_class_side_follows_the_owner_a_chaos_librarian_rolls_raptors_and_havocs()
    {
        var c = Make();
        var r = new SquadRosters(); var book = new SquadClassBook(c, r, 3UL);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Assault, default, Team.Chaos));
        Assert.StartsWith("csm - ", r.Marine(Key, 0)!.Loadout.ClassId);
        Assert.StartsWith("csm_", r.Marine(Key, 0)!.Loadout.Melee);
        book.OnOwnerLoadout(Key, SquadOwnerLoadout.ForLibrarian(SquadClass.Assault, default, Team.SpaceMarines));
        Assert.StartsWith("sm - ", r.Marine(Key, 0)!.Loadout.ClassId);
        var back = new SquadClassBook(c, new SquadRosters(), 3UL); back.Import(book.Export());
        Assert.Equal(Team.SpaceMarines, back.LastOf(Key)!.Side);
    }

    [Fact]
    public void A_leap_under_a_roof_keeps_a_low_arc()
    {
        var (sim, arena, _) = Squad(1);
        arena.Roof = true;
        Run(sim, 1, Owner(Vector3.Zero));
        Assert.NotNull(sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f)));
        float top = 0f;
        for (int k = 0; k < 200; k++) { sim.Step(++_t, [Owner(Vector3.Zero)]); top = MathF.Max(top, sim.View(0)!.Value.Position.Z); }
        Assert.InRange(top, 0.1f, SquadClassRules.RoofedApex + 0.05f);
    }

    [Fact]
    public void A_squad_kill_carries_the_Ork_type_for_shots_and_pounds_and_none_when_the_arena_does_not_know_it()
    {
        var (sim, arena, _) = Squad(2);
        Run(sim, 1, Owner(Vector3.Zero));
        var ork = arena.Add(new Vector3(10f, 0.5f, 0f)); ork.Health = 50f;
        sim.OrderJumpPound(1, new Vector3(10f, 0f, 0f));
        SquadKill? kill = null;
        for (int k = 0; k < 300 && kill is null; k++) { sim.Step(++_t, [Owner(Vector3.Zero)]); if (sim.Kills.Count > 0) kill = sim.Kills[0]; }
        Assert.Equal(MusouEnemyType.Nob, kill!.Value.Type);
        Assert.Equal(ork.Id, kill.Value.Enemy);
        Assert.Null(new SquadKill(1UL, 0, 1, SquadEnemyClass.Fodder).Type);
    }
}
