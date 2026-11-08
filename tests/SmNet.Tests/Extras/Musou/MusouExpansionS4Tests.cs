using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>Expansion stage S4 (docs/musou-expansion.md "Scrap Meks"): the five Meks, their 300 s replacement, their flight and builds, the limits, the cleanup, and the four contraptions.</summary>
public class MusouExpansionS4Tests
{
    const int Human = 1;
    const int Rate = 30;
    static readonly Vector3[] Spawns = [new(0f, 0f, 0f), new(20f, 0f, 0f), new(-20f, 0f, 0f), new(0f, 20f, 0f)];

    // ---------------------------------------------------------------- helpers

    /// <summary>A bare pool in a war (no graph, no world) and its Meks.</summary>
    static (MusouSim Sim, MusouMeks Meks) Pool()
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        return (sim, new MusouMeks(sim));
    }

    /// <summary>One step of the pool and the Meks with these targets.</summary>
    static void Step(MusouSim sim, MusouMeks meks, uint tick, params MusouTarget[] targets)
    {
        sim.BeginTick(tick);
        meks.Step(targets, Spawns);
        sim.Step(targets, null, true, new List<MusouAttack>());
        var dead = new List<MusouKill>();
        sim.TakeKills(dead);
    }

    static List<MusouEvent> Cues(MusouSim sim, byte cue) => sim.Events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == cue).ToList();

    static List<MusouContraption> All(MusouMeks meks) { var list = new List<MusouContraption>(); meks.CopyTo(list); return list; }

    static float Total(PlayerState p) => p.Health + p.Shield;

    static void Tick(World w) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.None));

    static List<MusouEvent> Cues(World w, byte cue) => w.Musou!.Events.Where(e => e.Kind == MusouEventKind.Telegraph && e.Value == cue).ToList();

    /// <summary>A war battle on the test arena (no graph): the player at the landing zone, no Guardsmen, Squigs or Weirdboyz; the Meks have come.</summary>
    static (World W, TfwBattle Battle, MusouMeks Meks) Battle(SimConfig? config = null)
    {
        var w = TfwRig.War(config: config);
        w.TfwMeksOn = true;
        w.Tfw!.Guardsmen = false;
        w.TfwSquigDropsOn = false;
        w.Tfw.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, p.Yaw, InputButtons.None);   // the AI Marines stand by: they shoot nothing
        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        TfwRig.Steps(w, 3);
        var meks = Assert.IsType<MusouMeks>(w.TfwMeks);
        Assert.Equal(MusouMekRules.MaxMeks, meks.AliveMeks);
        meks.Builds = false;
        return (w, battle, meks);
    }

    // ---------------------------------------------------------------- five Meks, 300 s

    [Fact]
    public void Five_Meks_come_at_once_and_each_dead_one_is_replaced_300_s_after_his_own_death()
    {
        var (sim, meks) = Pool();
        var far = new MusouTarget(Human, new Vector3(0f, -200f, 0f), true);
        uint tick = 0;
        Step(sim, meks, ++tick, far);
        Assert.Equal(MusouMekRules.MaxMeks, meks.AliveMeks);
        Assert.Equal(MusouMekRules.MaxMeks, sim.CountAlive(MusouEnemyType.ScrapMek));

        ushort first = meks.MekId(0), second = meks.MekId(1);
        Assert.True(sim.Damage(first, Human, 1e4f, Vector3.Zero, out bool k1) && k1);
        Step(sim, meks, ++tick, far);
        Assert.Equal(4, meks.AliveMeks);
        Assert.Equal(MusouMekRules.RespawnSeconds * Rate, meks.RespawnStepsLeft(0));
        int gap = (int)(100f * Rate);
        for (int k = 0; k < gap; k++) Step(sim, meks, ++tick, far);
        Assert.True(sim.Damage(second, Human, 1e4f, Vector3.Zero, out bool k2) && k2);
        Step(sim, meks, ++tick, far);
        Assert.Equal(3, meks.AliveMeks);
        int most = 0;
        // The first is due 300 s after his death, not before; the second 100 s later.
        for (int k = 0; k < (int)(MusouMekRules.RespawnSeconds * Rate) - gap - 3; k++) { Step(sim, meks, ++tick, far); most = Math.Max(most, meks.AliveMeks); }
        Assert.Equal(0, meks.MekId(0));
        Assert.Equal(3, most);
        for (int k = 0; k < 4 && meks.MekId(0) == 0; k++) Step(sim, meks, ++tick, far);
        Assert.NotEqual(0, meks.MekId(0));
        Assert.Equal(4, meks.AliveMeks);
        Assert.InRange(meks.RespawnStepsLeft(1), gap - 10, gap + 2);   // the second waits his own 300 s
        for (int k = 0; k < gap + 2; k++) { Step(sim, meks, ++tick, far); Assert.True(meks.AliveMeks <= MusouMekRules.MaxMeks); }
        Assert.Equal(MusouMekRules.MaxMeks, meks.AliveMeks);
        Assert.Equal(7, meks.MeksCome);
    }

    [Fact]
    public void A_Mek_runs_from_a_player_within_twenty_metres_and_not_from_one_farther()
    {
        var sim = new MusouSim(null) { War = TfwCombatRules.Default };
        ushort mek = sim.Spawn(MusouEnemyType.ScrapMek, Vector3.Zero, 0f, 250f);
        var near = new MusouTarget(Human, new Vector3(0f, -15f, 0f), true);
        uint tick = 0;
        for (int k = 0; k < 30; k++) { sim.BeginTick(++tick); sim.Step([near], null, true, new List<MusouAttack>()); }
        Assert.True(sim.MekFleeing(mek));
        Assert.True(sim.TryGet(mek, out var ran) && ran.Position.Y > 1.5f, $"he ran away from the player: {ran.Position}");

        ushort calm = sim.Spawn(MusouEnemyType.ScrapMek, new Vector3(100f, 0f, 0f), 0f, 250f);
        var farther = new MusouTarget(Human, new Vector3(100f, -25f, 0f), true);
        for (int k = 0; k < 30; k++) { sim.BeginTick(++tick); sim.Step([farther], null, true, new List<MusouAttack>()); }
        Assert.False(sim.MekFleeing(calm));
        Assert.True(sim.TryGet(calm, out var stood) && Vector3.Distance(stood.Position, new Vector3(100f, 0f, 0f)) < 0.5f, "a player 25 m off: he stays");
        var attacks = new List<MusouAttack>();
        sim.BeginTick(++tick);
        sim.Step([near with { Position = new Vector3(100f, -1f, 0f) }], null, true, attacks);
        Assert.Empty(attacks);   // he never attacks
    }

    // ---------------------------------------------------------------- the builds and the limits

    [Fact]
    public void Each_Mek_hides_and_builds_at_most_three_and_the_map_has_one_shield_generator()
    {
        var (sim, meks) = Pool();
        var far = new MusouTarget(Human, new Vector3(0f, -220f, 0f), true);
        uint tick = 0;
        int builds = 0, firstBuilt = -1;
        for (int k = 0; k < 120 * Rate; k++)
        {
            Step(sim, meks, ++tick, far);
            builds += Cues(sim, MusouMekRules.BuildCue).Count;
            if (firstBuilt < 0 && Cues(sim, MusouMekRules.BuiltCue).Count > 0) firstBuilt = k;
            var all = All(meks);
            Assert.True(all.Count(c => c.Kind == MusouContraptionKind.ShieldGenerator) <= MusouMekRules.MaxShields, "one shield generator on the map");
            foreach (var group in all.GroupBy(c => c.Mek)) Assert.True(group.Count() <= MusouMekRules.MaxPerMek, "at most three a Mek");
        }
        var done = All(meks);
        Assert.Equal(MusouMekRules.MaxMeks * MusouMekRules.MaxPerMek, done.Count);
        Assert.All(done, c => Assert.Equal(MusouContraptionState.Active, c.State));
        Assert.Equal(1, done.Count(c => c.Kind == MusouContraptionKind.ShieldGenerator));
        Assert.Equal(done.Count, builds);
        Assert.True(firstBuilt >= MusouMekRules.BuildSeconds * Rate, $"a build takes 15 s (first done at step {firstBuilt})");
        for (int slot = 0; slot < MusouMekRules.MaxMeks; slot++)
        {
            Assert.True(meks.HideOf(slot) is { } hide && Vector3.Distance(hide, far.Position) >= MusouMekRules.HideMinPlayerDistance);
            Assert.Equal(MusouMekRules.MaxPerMek, done.Count(c => c.Mek == meks.MekId(slot)));
        }
        // The test aid keeps the limits too.
        Assert.Null(meks.PlaceBuilt(0, MusouContraptionKind.Trap, Vector3.Zero));
    }

    [Fact]
    public void A_build_takes_fifteen_seconds_and_a_flight_breaks_it_off()
    {
        var (sim, meks) = Pool();
        var far = new MusouTarget(Human, new Vector3(0f, -220f, 0f), true);
        uint tick = 0;
        MusouEvent begun = default;
        for (int k = 0; k < 40 * Rate && begun.Value == 0; k++) { Step(sim, meks, ++tick, far); if (Cues(sim, MusouMekRules.BuildCue).FirstOrDefault() is { Value: > 0 } e) begun = e; }
        Assert.NotEqual(0, begun.Value);
        byte id = (byte)begun.Player;
        for (int k = 0; k < (int)(MusouMekRules.BuildSeconds * Rate) - 2; k++) Step(sim, meks, ++tick, far);
        Assert.True(meks.TryGet(id, out var nearly) && nearly.State == MusouContraptionState.Building);
        Assert.True(sim.TryGet(begun.Enemy, out var working) && working.State == MusouEnemyState.Building, "he stands at work");
        // A player comes close: he runs, and the build is lost.
        Assert.True(sim.TryGet(begun.Enemy, out var mek));
        var close = new MusouTarget(Human, mek.Position + new Vector3(0f, -8f, 0f), true);
        Step(sim, meks, ++tick, close);
        Step(sim, meks, ++tick, close);
        Assert.False(meks.TryGet(id, out _), "the build broken off is gone");
    }

    [Fact]
    public void A_Meks_death_destroys_all_his_contraptions_and_no_trap_goes_off()
    {
        var (sim, meks) = Pool();
        var far = new MusouTarget(Human, new Vector3(0f, -220f, 0f), true);
        uint tick = 0;
        Step(sim, meks, ++tick, far);
        meks.Builds = false;
        ushort mek = meks.MekId(2);
        Assert.NotNull(meks.PlaceBuilt(2, MusouContraptionKind.Trap, new Vector3(50f, 50f, 0f)));
        Assert.NotNull(meks.PlaceBuilt(2, MusouContraptionKind.Turret, new Vector3(55f, 50f, 0f)));
        Assert.NotNull(meks.PlaceBuilt(2, MusouContraptionKind.Beacon, new Vector3(60f, 50f, 0f)));
        var other = meks.PlaceBuilt(3, MusouContraptionKind.Trap, new Vector3(-50f, 50f, 0f));
        Assert.NotNull(other);
        Assert.True(sim.Damage(mek, Human, 1e4f, Vector3.Zero, out _));
        Step(sim, meks, ++tick, far);
        Assert.Equal(3, Cues(sim, MusouMekRules.DestroyedCue).Count(e => e.Enemy == mek));
        var left = Assert.Single(All(meks));
        Assert.Equal(other!.Value.Id, left.Id);
        var blasts = new List<MusouTrapBlast>(); var triggers = new List<MusouTrapTrigger>();
        meks.TakeBlasts(blasts); meks.TakeTriggers(triggers);
        Assert.Empty(blasts);
        Assert.Empty(triggers);
    }

    // ---------------------------------------------------------------- the beacon

    [Fact]
    public void A_beacon_drops_ten_every_thirty_seconds_up_to_forty_alive_and_its_destruction_cancels_the_drop_in_flight_and_the_queue()
    {
        var (sim, meks) = Pool();
        var far = new MusouTarget(Human, new Vector3(0f, -400f, 0f), true);
        uint tick = 0;
        Step(sim, meks, ++tick, far);
        meks.Builds = false;
        sim.Frozen = true;   // the Boyz stay where they land
        var beacon = meks.PlaceBuilt(0, MusouContraptionKind.Beacon, new Vector3(0f, 100f, 0f))!.Value;
        int calls = 0, lands = 0, mostAlive = 0, firstCall = -1;
        for (int k = 0; k < (int)(5.5f * MusouMekRules.BeaconPeriod * Rate); k++)
        {
            Step(sim, meks, ++tick, far);
            if (Cues(sim, MusouMekRules.DropCue).Any(e => e.Player == beacon.Id)) { calls++; if (firstCall < 0) firstCall = k; }
            lands += Cues(sim, MusouMekRules.DropLandCue).Count(e => e.Player == beacon.Id);
            mostAlive = Math.Max(mostAlive, meks.BoyzOf(beacon.Id).Count);
        }
        Assert.InRange(firstCall, (int)(MusouMekRules.BeaconPeriod * Rate) - 2, (int)(MusouMekRules.BeaconPeriod * Rate) + 1);
        Assert.Equal(MusouMekRules.BeaconMaxAlive, mostAlive);
        Assert.Equal(4, lands);   // 4 x 10 = 40: the fifth waits for room
        Assert.Equal(4, calls);
        Assert.True(meks.TryGet(beacon.Id, out var full) && full.Queued == MusouMekRules.BeaconDrop && !full.DropInFlight, "one drop queued, none flying");

        // Five of its Boyz die: the queued drop flies at once with the five the cap leaves.
        var boyz = meks.BoyzOf(beacon.Id).ToArray();
        foreach (var id in boyz.Take(5)) sim.Damage(id, Human, 1e4f, Vector3.Zero, out _);
        Step(sim, meks, ++tick, far);
        Assert.True(meks.TryGet(beacon.Id, out var flying) && flying.DropInFlight, "the queued drop flies");
        // Destroyed while it flies: the drop and the queue are cancelled; the landed Boyz stay and fight on.
        Assert.True(meks.Damage(beacon.Id, Human, 1e4f, out bool destroyed) && destroyed);
        for (int k = 0; k < (int)((MusouMekRules.DropFlightSeconds + MusouMekRules.BeaconPeriod) * Rate); k++)
        {
            Step(sim, meks, ++tick, far);
            Assert.Empty(Cues(sim, MusouMekRules.DropLandCue));
        }
        Assert.Equal(4, meks.DropsLanded);
        Assert.Equal(35, boyz.Skip(5).Count(id => sim.TryGet(id, out var e) && e.Alive && e.Type == MusouEnemyType.Boy));
    }

    // ---------------------------------------------------------------- the turret

    [Fact]
    public void A_turret_fires_the_Rokkit_rocket_every_four_seconds_within_fifty_metres()
    {
        var (sim, meks) = Pool();
        uint tick = 0;
        var far = new MusouTarget(Human, new Vector3(0f, -400f, 0f), true);
        Step(sim, meks, ++tick, far);
        meks.Builds = false;
        sim.Frozen = true;
        var turret = meks.PlaceBuilt(1, MusouContraptionKind.Turret, new Vector3(0f, 100f, 0f))!.Value;
        var outOfRange = new MusouTarget(Human, new Vector3(0f, 100f + MusouMekRules.TurretRange + 5f, 0f), true);
        for (int k = 0; k < 6 * Rate; k++) { Step(sim, meks, ++tick, outOfRange); Assert.Empty(Cues(sim, MusouMekRules.TurretFireCue)); }
        var inRange = new MusouTarget(Human, new Vector3(0f, 100f + 45f, 0f), true);
        var fired = new List<uint>();
        for (int k = 0; k < 13 * Rate; k++)
        {
            Step(sim, meks, ++tick, inRange);
            foreach (var e in Cues(sim, MusouMekRules.TurretFireCue)) { fired.Add(tick); Assert.Equal(turret.Id, (byte)e.Player); }
            var blasts = new List<MusouRocketBlast>();
            sim.TakeRocketBlasts(blasts);
        }
        Assert.Equal(4, fired.Count);   // at 0, 4, 8 and 12 s
        Assert.Equal(MusouMekRules.TurretPeriod * Rate, fired[1] - fired[0]);
        Assert.Equal(4, meks.TurretRockets);
    }

    [Fact]
    public void Turret_rockets_hurt_Orks_and_set_off_Squigs()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        musou.Frozen = true;
        var at = new Vector3(40f, -100f, 0f);
        TfwRig.Teleport(w, new Vector3(0f, -100f, 0f));
        ushort boy = musou.Spawn(MusouEnemyType.Boy, at + new Vector3(-3.5f, 0f, 0f), 0f, 85f);   // in the rocket's path whichever Marine near the landing zone it aims at
        ushort squig = musou.Spawn(MusouEnemyType.BombSquig, at + new Vector3(-3.5f, 1.5f, 0f), 0f, 30f);
        var turret = meks.PlaceBuilt(0, MusouContraptionKind.Turret, at);
        Assert.NotNull(turret);
        bool fired = false, rocket = false, squigBurst = false;
        for (int k = 0; k < 6 * Rate && !squigBurst; k++)
        {
            Tick(w);
            fired |= Cues(w, MusouMekRules.TurretFireCue).Any(e => e.Player == turret!.Value.Id);
            rocket |= Cues(w, MusouRokkitRules.BlastCue).Count > 0;
            squigBurst |= Cues(w, MusouSquigRules.BlastCue).Any(e => e.Enemy == squig);
        }
        Assert.True(fired, "the turret fired at the player");
        Assert.True(rocket, "its rocket burst");
        Assert.True(squigBurst, "its Ork blast set the Squig off");
        Assert.False(musou.TryGet(boy, out var b) && b.Alive, "the Boy in its path (an Ork) is dead");
    }

    // ---------------------------------------------------------------- the trap

    [Fact]
    public void A_trap_set_off_by_a_player_takes_half_his_maximum_and_spares_the_Orks_beside_it()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        musou.Frozen = true;
        var spot = new Vector3(30f, -90f, 0f);
        ushort boy = musou.Spawn(MusouEnemyType.Boy, spot + new Vector3(1f, 0f, 0f), 0f, 85f);
        var trap = meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot)!.Value;
        Tick(w);
        Assert.True(meks.TryGet(trap.Id, out _), "nobody near: it waits");
        var body = Loadouts.Body(w.Config, w.Players[Human]);
        float max = body.HealthMax + body.ShieldMax;
        TfwRig.Teleport(w, spot + new Vector3(-2f, 0f, 0f));
        float before = Total(w.Players[Human]);
        Assert.Equal(max, before, 2);
        Tick(w);
        Assert.Single(Cues(w, MusouMekRules.TrapCue));
        Assert.False(meks.TryGet(trap.Id, out _), "it is spent");
        Assert.Equal(max * MusouMekRules.TrapShare, before - Total(w.Players[Human]), 1);
        Assert.True(musou.TryGet(boy, out var b) && b.Health == 85f, "the Boy beside it is spared");
        Assert.Equal(max * MusouMekRules.TrapShare, w.TfwTrapFlatDamage, 2);   // a Guardsman's flat share: a standard Marine's half
    }

    [Fact]
    public void A_trap_destroyed_by_a_players_damage_goes_off_as_an_Ork_blast_that_hurts_Orks_with_his_credit()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        musou.Frozen = true;
        var spot = new Vector3(30f, -80f, 0f);
        ushort boy = musou.Spawn(MusouEnemyType.Boy, spot + new Vector3(1f, 0f, 0f), 0f, 85f);
        var trap = meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot)!.Value;
        TfwRig.Teleport(w, spot + new Vector3(0f, -20f, 0f));
        Tick(w);
        int kills = w.Scores.GetValueOrDefault(Human).Kills;
        Assert.True(meks.Damage(trap.Id, Human, MusouMekRules.TrapHealth, out bool destroyed) && destroyed);
        Assert.Single(Cues(w, MusouMekRules.TrapBlastCue));
        Tick(w);
        Assert.False(musou.TryGet(boy, out var b) && b.Alive, "the Ork beside it dies in its blast");
        Assert.True(w.Scores.GetValueOrDefault(Human).Kills > kills, "the kill is the shooter's");
        // Destroyed by no player (an uncredited Ork blast): it only breaks.
        var quiet = meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot + new Vector3(10f, 0f, 0f))!.Value;
        Assert.True(meks.Damage(quiet.Id, 0, 1e4f, out _));
        Assert.Empty(Cues(w, MusouMekRules.TrapBlastCue));
        var none = new List<MusouTrapBlast>();
        meks.TakeBlasts(none);
        Assert.Empty(none);
    }

    [Fact]
    public void A_players_shot_strikes_a_contraption_and_ends_on_it()
    {
        var (sim, meks) = Pool();
        Step(sim, meks, 1, new MusouTarget(Human, new Vector3(0f, -400f, 0f), true));
        var turret = meks.PlaceBuilt(1, MusouContraptionKind.Turret, new Vector3(0f, 20f, 0f))!.Value;
        byte hit = meks.Raycast(new Vector3(0f, 0f, 1f), Vector3.UnitY, 100f, out float distance);
        Assert.Equal(turret.Id, hit);
        Assert.Equal(20f - MusouMekRules.Body(MusouContraptionKind.Turret).Radius, distance, 3);
        Assert.Equal(0, meks.Raycast(new Vector3(0f, 0f, 1f), Vector3.UnitY, 10f, out _));
        Assert.Equal(0, meks.Raycast(new Vector3(5f, 0f, 1f), Vector3.UnitY, 100f, out _));
    }

    // ---------------------------------------------------------------- the shield

    [Fact]
    public void The_dome_stops_shots_from_outside_only()
    {
        var (sim, meks) = Pool();
        Step(sim, meks, 1, new MusouTarget(Human, new Vector3(0f, -400f, 0f), true));
        var centre = new Vector3(0f, 50f, 0f);
        Assert.NotNull(meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, centre));
        Assert.Null(meks.PlaceBuilt(4, MusouContraptionKind.ShieldGenerator, new Vector3(80f, 0f, 0f)));   // one on the map
        var outside = centre + new Vector3(3f, -25f, 1.5f);
        float entry = meks.DomeEntry(outside, Vector3.UnitY, 100f);
        Assert.InRange(entry, 25f - MusouMekRules.ShieldRadius - 0.1f, 25f - 9f);
        var inside = centre + new Vector3(3f, -6f, 1.5f);
        Assert.True(float.IsPositiveInfinity(meks.DomeEntry(inside, Vector3.UnitY, 100f)), "fired inside: never stopped");
        Assert.True(float.IsPositiveInfinity(meks.DomeEntry(centre + new Vector3(30f, -25f, 1.5f), Vector3.UnitY, 100f)), "past it: not stopped");
        var boy = centre + new Vector3(3f, 0f, 1f);
        Assert.True(meks.Shielded(outside, boy), "a blast or a cone from outside does not reach in");
        Assert.False(meks.Shielded(inside, boy), "from inside it does");
        Assert.False(meks.Shielded(boy, outside), "from inside out it does");
    }

    [Fact]
    public void In_a_war_a_players_shot_from_outside_the_dome_stops_on_it_and_from_inside_reaches_the_Ork()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        musou.Frozen = true;
        var centre = new Vector3(40f, -70f, 0f);
        Assert.NotNull(meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, centre));
        ushort boy = musou.Spawn(MusouEnemyType.ArdBoy, centre + new Vector3(3f, 0f, 0f), MathF.PI, 1e5f);
        float Fire(Vector3 from)
        {
            TfwRig.Teleport(w, from);
            Tick(w);
            musou.TryGet(boy, out var before);
            for (int k = 0; k < 20; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, id == Human ? InputButtons.Fire : InputButtons.None, Pitch: -0.05f));
            for (int k = 0; k < 3; k++) Tick(w);
            musou.TryGet(boy, out var after);
            return before.Health - after.Health;
        }
        Assert.Equal(0f, Fire(centre + new Vector3(3f, -16f, 0f)));   // outside: the dome stops every round
        Assert.True(Fire(centre + new Vector3(3f, -6f, 0f)) > 0f, "inside: the rounds reach him");
    }

    // ---------------------------------------------------------------- the wire, the versions, the isolation

    [Fact]
    public void The_contraptions_and_the_cues_round_trip()
    {
        var list = new List<MusouContraption>
        {
            new(1, MusouContraptionKind.ShieldGenerator, MusouContraptionState.Active, 12, new Vector3(10.2f, -33.4f, 2.1f), 800f, 1f, 0f, false, 0),
            new(7, MusouContraptionKind.Beacon, MusouContraptionState.Active, 13, new Vector3(-50f, 20f, 0f), 200f, 1f, 0f, true, 10),
            new(9, MusouContraptionKind.Turret, MusouContraptionState.Active, 14, new Vector3(5f, 6f, 7f), 500f, 1f, 1.5f, false, 0),
            new(255, MusouContraptionKind.Trap, MusouContraptionState.Building, 2047, new Vector3(-1f, -2f, -3f), 150f, 0.4f, 0f, false, 0),
        };
        var bytes = MekWire.Write(list);
        Assert.Equal(1 + list.Count * MekWire.RecordBytes, bytes.Length);
        var back = MekWire.Read(bytes);
        Assert.NotNull(back);
        Assert.Equal(list.Select(c => (c.Id, c.Kind, c.State, c.Mek, c.DropInFlight, c.Queued)), back.Select(c => (c.Id, c.Kind, c.State, c.Mek, c.DropInFlight, c.Queued)));
        for (int k = 0; k < list.Count; k++)
        {
            Assert.True(Vector3.Distance(list[k].Position, back[k].Position) <= 0.08f);
            Assert.InRange(back[k].Health - list[k].Health, 0f, back[k].MaxHealth / 255f + 0.01f);
            Assert.Equal(list[k].Progress, back[k].Progress, 1);
            Assert.Equal(list[k].Aim, back[k].Aim, 1);
        }
        Assert.Equal(0.5f, back[1].Health / back[1].MaxHealth, 2);
        Assert.Null(MekWire.Read(bytes.AsSpan(0, bytes.Length - 1)));
        var bad = (byte[])bytes.Clone(); bad[2] = 9;   // an unknown kind
        Assert.Null(MekWire.Read(bad));
        Assert.Empty(MekWire.Read(MekWire.Write([]))!);
        Assert.Equal(17, TfwWire.MekTag);
        Assert.True(TfwWire.Owns(TfwWire.MekTag));
        Assert.True(MekWire.MaxPayloadBytes <= Protocol.MaxExtrasPayloadBytes);

        var box = new MusouBox(new Vector3(-150f, -150f, -10f), new Vector3(150f, 150f, 40f));
        var events = new List<MusouEvent>();
        for (byte cue = MusouMekRules.BuildCue; cue <= MusouMekRules.TurretFireCue; cue++) events.Add(new(MusouEventKind.Telegraph, 200u + cue, 40000, 255, cue, new Vector3(cue, -cue, 1f)));
        var read = MusouWire.ReadEvents(MusouWire.WriteEvents(events, box), box);
        Assert.NotNull(read);
        Assert.Equal(events.Select(e => (e.Tick, e.Enemy, e.Player, e.Value)), read.Select(e => (e.Tick, e.Enemy, e.Player, e.Value)));
        var entities = new List<MusouEntity> { new(3, new Vector3(1f, 2f, 0f), 0.5f, MusouEnemyType.ScrapMek, MusouEnemyState.Building) };
        Assert.Equal(MusouEnemyState.Building, Assert.Single(MusouWire.ReadEntities(MusouWire.WriteEntities(entities, box), box)!).State);

        Assert.True(MusouSim.Version >= 18u);   // S4 raised it; later stages raise it further
        Assert.True(TfwRules.Version >= 36u);
        Assert.Equal(63u, Protocol.Version);
    }

    [Fact]
    public void A_battle_sends_its_contraptions_on_tag_17_through_the_client_state()
    {
        var (_, _, meks) = Battle();
        Assert.NotNull(meks.PlaceBuilt(0, MusouContraptionKind.Trap, new Vector3(30f, -80f, 0f)));
        var list = new List<MusouContraption>();
        meks.CopyTo(list);
        var state = new TfwClientState();
        Assert.True(state.Apply(new ExtrasSection(100, TfwWire.MekTag, MekWire.Write(list)), null, 0.0));
        Assert.Equal(list.Select(c => c.Id), state.Contraptions.Select(c => c.Id));
    }

    [Fact]
    public void The_skirmish_and_a_war_without_a_battle_have_no_Meks()
    {
        var w = new World(MusouSimTests.Base) { Rules = MusouSimTests.Endless };
        Assert.True(w.StartMusou(null));
        w.Spawn(Human, 0);
        for (int k = 0; k < 30 * 20; k++) w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, InputButtons.None));
        Assert.Null(w.TfwMeks);
        Assert.Equal(0, w.Musou!.CountAlive(MusouEnemyType.ScrapMek));

        var war = TfwRig.War();
        war.TfwMeksOn = true;
        TfwRig.Seconds(war, 5f);   // in the Stormbird: no battle
        Assert.Null(war.TfwMeks);
        Assert.Equal(0, war.Musou!.CountAlive(MusouEnemyType.ScrapMek));
    }

    // ---------------------------------------------------------------- the owner's follow-up, 2026-11-08

    [Fact]
    public void A_trap_kills_an_AI_Marine_outright()
    {
        var (w, _, meks) = Battle();
        w.Musou!.Frozen = true;
        int marine = w.Players.Keys.First(TfwContracts.IsPuppet);
        var spot = new Vector3(30f, -90f, 0f);
        TfwRig.Teleport(w, spot + new Vector3(0f, -40f, 0f));
        meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot);
        Assert.True(w.TfwRespawnAt(marine, spot + new Vector3(1f, 0f, 0f), 0f));
        Assert.True(w.Players[marine].Alive);
        Tick(w);
        Assert.Single(Cues(w, MusouMekRules.TrapCue));
        Assert.False(w.Players[marine].Alive, "the AI Marine dies at once");
    }

    [Fact]
    public void The_dome_keeps_an_Ork_blast_from_outside_off_Orks_players_and_contraptions_inside()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        musou.Frozen = true;
        var centre = new Vector3(40f, -70f, 0f);
        Assert.NotNull(meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, centre));
        ushort inside = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(0f, -9f, 0f), 0f, 85f);
        ushort outside = musou.Spawn(MusouEnemyType.Boy, centre + new Vector3(0f, -13f, 0f), 0f, 85f);
        TfwRig.Teleport(w, centre + new Vector3(1.5f, -9f, 0f));
        Tick(w);
        float before = Total(w.Players[Human]);
        var point = centre + new Vector3(0f, -11f, 1f);   // 1 m outside the dome, 2 m from each Boy
        w.MusouOrkBlast(point, new OrkBlastSpec(5f, 60f));
        Assert.True(musou.TryGet(inside, out var a) && a.Health == 85f, "the Boy inside is spared");
        Assert.True(musou.TryGet(outside, out var b) && b.Health < 85f, "the Boy outside is hurt");
        Assert.Equal(before, Total(w.Players[Human]), 2);
        // From inside, the blast reaches in.
        w.MusouOrkBlast(centre + new Vector3(0f, -8f, 1f), new OrkBlastSpec(5f, 60f));
        Assert.True(musou.TryGet(inside, out a) && a.Health < 85f, "a blast inside hurts him");
    }

    [Fact]
    public void The_dome_stops_an_Ork_rocket_from_outside_where_it_enters()
    {
        var (w, _, meks) = Battle();
        var musou = w.Musou!;
        var centre = new Vector3(40f, -70f, 0f);
        Assert.NotNull(meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, centre));
        TfwRig.Teleport(w, centre + new Vector3(0f, -80f, 0f));
        Tick(w);
        ushort serial = musou.LaunchTurretRocket(meks.MekId(0), 1, centre + new Vector3(0f, -30f, 1.4f), centre + new Vector3(0f, 0f, 1.4f));
        Assert.NotEqual(0, serial);
        MusouEvent? blast = null;
        for (int k = 0; k < 4 * Rate && blast is null; k++)
        {
            Tick(w);
            blast = Cues(w, MusouRokkitRules.BlastCue).Cast<MusouEvent?>().FirstOrDefault(e => e!.Value.Player == serial);
        }
        Assert.NotNull(blast);
        Assert.InRange(Vector3.Distance(blast!.Value.Vector, centre), MusouMekRules.ShieldRadius - 0.5f, MusouMekRules.ShieldRadius + 0.5f);
    }

    [Fact]
    public void The_dome_stops_a_grenade_thrown_from_outside()
    {
        float Nearest(bool dome)
        {
            var frag = new ThrownSpec("test_frag", 2, 2, 1f, 10f, GrenadeTests.FragBall);
            var config = TfwRig.Config with
            {
                Thrown = [frag],
                Classes = TfwRig.Config.Classes.Select(c => c with { EquipmentChoices = [0] }).ToArray(),
            };
            var (w, _, meks) = Battle(config);
            w.Musou!.Frozen = true;
            var centre = new Vector3(40f, -70f, 0f);
            if (dome) Assert.NotNull(meks.PlaceBuilt(3, MusouContraptionKind.ShieldGenerator, centre));
            Assert.True(w.TfwRespawnAt(Human, centre + new Vector3(0f, -13f, 0f), 0f));
            Tick(w);
            float nearest = float.PositiveInfinity;
            bool thrown = false;
            for (int k = 0; k < 6 * Rate; k++)
            {
                var buttons = k < 2 ? InputButtons.Grenade : InputButtons.None;
                w.Step(id => new PlayerInput(w.Tick, 0f, 0f, 0f, id == Human ? buttons : InputButtons.None, Pitch: 0.3f));
                foreach (var p in w.Projectiles.Where(p => p.Owner == Human)) { thrown = true; nearest = MathF.Min(nearest, Vector3.Distance(p.Position, centre)); }
            }
            Assert.True(thrown, "a grenade was thrown");
            return nearest;
        }
        Assert.True(Nearest(dome: false) < MusouMekRules.ShieldRadius - 1f, "with no dome it flies into the circle");
        Assert.True(Nearest(dome: true) >= MusouMekRules.ShieldRadius - 0.2f, "the dome stops it at its edge");
    }

    static (MusouSim Sim, GuardSim Guards) Squad()
    {
        var sim = new MusouSim(null) { Frozen = true };
        var guards = new GuardSim(sim);
        guards.SpawnSquad(new Vector3(0f, 0f, 0f));
        return (sim, guards);
    }

    static void Run(MusouSim sim, GuardSim guards, int ticks) { for (uint k = 1; k <= ticks; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); } }

    [Fact]
    public void The_dome_stops_the_Guardsmens_fire_and_spares_them_an_Ork_blast_from_outside()
    {
        var (sim, open) = Squad();
        sim.Spawn(MusouEnemyType.Warboss, new Vector3(20f, 0f, 0f), 0f, 1e7f);
        Run(sim, open, 300);
        Assert.True(open.Shots > 0, "with no dome they fire");
        var (sim2, domed) = Squad();
        sim2.Spawn(MusouEnemyType.Warboss, new Vector3(20f, 0f, 0f), 0f, 1e7f);
        domed.Shielded = (_, _) => true;
        Run(sim2, domed, 300);
        Assert.Equal(0, domed.Shots);
        Assert.Equal(0, domed.Blast(new Vector3(0f, 0f, 1f), 5f, _ => 1f, 10f, out _, _ => true));
        Assert.True(domed.Blast(new Vector3(0f, 0f, 1f), 5f, _ => 1f, 10f, out _) > 0);
    }

    [Fact]
    public void The_Guardsmen_shoot_a_Rokkit_turret_and_no_other_contraption()
    {
        var (sim, meks) = Pool();
        Step(sim, meks, 1, new MusouTarget(Human, new Vector3(0f, -400f, 0f), true));
        var trap = meks.PlaceBuilt(0, MusouContraptionKind.Trap, new Vector3(15f, 0f, 0f))!.Value;
        var beacon = meks.PlaceBuilt(1, MusouContraptionKind.Beacon, new Vector3(0f, 15f, 0f))!.Value;
        var guards = new GuardSim(sim);
        guards.SpawnSquad(new Vector3(0f, 0f, 0f));
        float damage = 0f;
        guards.Turrets = (Vector3 from, float range, out Vector3 centre) => meks.NearestTurret(from, range, out centre);
        guards.TurretHit = (id, d) => { damage += d; meks.Damage(id, 0, d, out _); };
        Run(sim, guards, 300);
        Assert.Equal(0f, damage);
        Assert.True(meks.TryGet(trap.Id, out var t) && t.Health == MusouMekRules.TrapHealth, "the trap is left alone");
        Assert.True(meks.TryGet(beacon.Id, out var b) && b.Health == MusouMekRules.BeaconHealth, "the beacon is left alone");
        var turret = meks.PlaceBuilt(2, MusouContraptionKind.Turret, new Vector3(-20f, 0f, 0f))!.Value;
        for (uint k = 301; k <= 600; k++) { sim.BeginTick(k); guards.BeginTick(k); guards.Step(true); }
        Assert.True(damage > 0f, "they shoot the turret");
        Assert.True(!meks.TryGet(turret.Id, out var after) || after.Health < MusouMekRules.TurretHealth);
    }
}
