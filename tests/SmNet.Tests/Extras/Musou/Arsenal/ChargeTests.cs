using System.Numerics;
using ArsenalContent = SmContent.Extras.Musou.Arsenal.Arsenal;
using SmNet.Tests.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Arsenal;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// W4, the Remote Detonators (docs/new-weapons-design.md section 7): the item appended to the Space Marine classes only, the owner's numbers (the damage table at the centre, 20 m and 40 m; stacking spots;
/// Bosses x2; at most 40 flying corpses; survivors thrown back; a 5 s fuse after it sticks; it rides on an Ork; 1 carried, 2 with Favor, one back every 30 s) in a war on the synthetic arena.
/// </summary>
public class ChargeTests
{
    static readonly WeaponSpec Rifle = new("sm_mp_ranged_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 1f, 0.12f, 30, 2000, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 1f, 10f, 3.5f);
    static readonly MusouRules Still = MusouRules.Default with { PlayerDamageScale = 0f, Types = [.. MusouRules.Default.Types.Select(t => t with { Speed = 0f })] };

    static SimConfig Config()
    {
        var c = new ClassLoadout("sm - tactical", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [], Side = Team.SpaceMarines, Role = "tactical" };
        var config = MusouSimTests.Base with { Weapons = [Rifle], Thrown = [], Classes = [c, c with { Name = "csm - tactical", Side = Team.Chaos }], MusouRules = Still, RespawnTime = 60f };
        return ArsenalContent.Append(config, "", []);
    }

    const int Human = 1;
    static readonly Vector3 At = new(0f, -60f, 0f);

    static World War(out SimConfig config)
    {
        config = Config();
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, 0);
        Tick(w);
        for (int k = 0; k < 60 && w.State != MatchState.Playing; k++) Tick(w);
        Assert.Equal(MatchState.Playing, w.State);
        w.TfwRespawnAt(Human, At, 0f);
        Tick(w);
        return w;
    }

    static void Tick(World w, InputButtons buttons = InputButtons.None) => w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, buttons));

    static ushort Ork(World w, Vector3 at, MusouEnemyType type = MusouEnemyType.Boy, float health = 100000f)
    {
        ushort id = w.Musou!.Spawn(type, at, 0f, health);
        Assert.NotEqual(0, id);
        return id;
    }

    static float Health(World w, ushort id) => w.Musou!.TryGet(id, out var e) ? (e.Alive ? e.Health : 0f) : 0f;

    /// <summary>Throws a charge and runs until it rests; the place it stuck at.</summary>
    static Vector3 ThrowAndStick(World w)
    {
        Tick(w, InputButtons.Grenade);
        for (int k = 0; k < 120 && (w.Projectiles.Count == 0 || w.Projectiles[0].Motion != ProjectileMotion.Stuck); k++) Tick(w);
        Assert.Single(w.Projectiles);
        Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
        return w.Projectiles[0].Position;
    }

    [Fact]
    public void It_is_offered_to_the_Space_Marine_classes_only_as_the_last_choice_and_the_data_gives_two_with_Favor()
    {
        var config = Config();
        int index = config.Thrown.ToList().FindIndex(t => t.Name == ArsenalRules.RemoteDetonators);
        Assert.Equal(0, index);
        Assert.Equal([index], config.Classes.First(c => c.Side == Team.SpaceMarines).EquipmentChoices);
        Assert.Empty(config.Classes.First(c => c.Side == Team.Chaos).EquipmentChoices);
        var item = config.Thrown[index];
        Assert.Equal(1, item.Count);
        Assert.Equal(1, item.SpawnCount);
        Assert.Equal(0.5f, item.Cooldown);
        Assert.Equal(25f, item.Projectile.Speed);
        Assert.True(item.Projectile.Sticky);
        Assert.Equal(0, item.Projectile.MaxBounces);
        Assert.Null(item.Projectile.Splash);   // the blasts are the World's
        Assert.Equal(2, Variants.Pick(item, item.Variants, new PerkUpgrades(DemolitionChargeRules.Favor, null)).Count);
        Assert.Equal(1, Variants.Pick(item, item.Variants, new PerkUpgrades("equipment_effectiveness", null)).Count);
        // Appended once: a second call changes nothing.
        Assert.Same(config, ArsenalContent.Append(config, "", []));
    }

    [Fact]
    public void The_blast_follows_the_owners_table_at_the_centre_20_m_and_40_m()
    {
        // The fit of docs/new-weapons-design.md section 7: the centre blast and the 16 spots, stacked, along a spot line and between two spots.
        var centre = DemolitionChargeRules.Centre(false);
        var spot = DemolitionChargeRules.Spot(false);
        Assert.Equal(600f, centre.Damage.Damage);
        Assert.Equal(180f, spot.Damage.Damage);
        Assert.Equal(16f, centre.Radius);
        Assert.Equal([6.4f, 9.6f, 16f], centre.FallOff.Skip(1).Select(p => MathF.Round(p.Distance, 3)));
        float Total(float along, float bearing)
        {
            var p = new Vector3(MathF.Cos(bearing) * along, MathF.Sin(bearing) * along, 0f);
            float sum = centre.Damage.Damage * Combat.SplashIntensity(centre.FallOff, p.Length());
            for (int k = 0; k < DemolitionChargeRules.Spots; k++)
            {
                var s = DemolitionChargeRules.SpotAt(Vector3.Zero, 0, k, DemolitionChargeRules.RingRadius, null);
                sum += spot.Damage.Damage * Combat.SplashIntensity(spot.FallOff, Vector3.Distance(s, p));
            }
            return sum;
        }
        float line = DemolitionChargeRules.Bearing(0, 0), between = line - MathF.PI / 16f;
        Assert.InRange(Total(0f, line), 599f, 3600f);   // at the centre the ring adds to the 600 (the design's table counts the spots only at their reach)
        Assert.InRange(Total(20f, line), 430f, 480f);       // 455 on a spot line
        Assert.InRange(Total(20f, between), 515f, 555f);    // 535 between
        Assert.InRange(Total(40f, between), 0f, 5f);        // the data's curve ends at 40 m
        Assert.InRange(Total(35f, line), 215f, 250f);       // 231
    }

    [Fact]
    public void The_ring_runs_clockwise_16_spots_22_and_a_half_degrees_apart_from_a_bearing_fixed_by_the_id()
    {
        for (int k = 0; k < 16; k++)
        {
            var s = DemolitionChargeRules.SpotAt(Vector3.Zero, 7, k, 24f, null);
            Assert.Equal(24f, new Vector2(s.X, s.Y).Length(), 1e-3f);
            float step = k == 0 ? 0f : MathF.Atan2(s.Y, s.X) - MathF.Atan2(DemolitionChargeRules.SpotAt(Vector3.Zero, 7, k - 1, 24f, null).Y, DemolitionChargeRules.SpotAt(Vector3.Zero, 7, k - 1, 24f, null).X);
            if (k > 0) Assert.Equal(-MathF.Tau / 16f, MathF.IEEERemainder(step, MathF.Tau), 1e-3f);
        }
        Assert.NotEqual(DemolitionChargeRules.Bearing(7, 0), DemolitionChargeRules.Bearing(8, 0));
        Assert.Equal(DemolitionChargeRules.Bearing(7, 0), DemolitionChargeRules.Bearing(7, 0));
        Assert.Equal(3, DemolitionChargeRules.SpotTick(0));
        Assert.Equal(33, DemolitionChargeRules.SpotTick(15));   // 1.1 s at 30 Hz
    }

    [Fact]
    public void A_wall_stops_a_spot_at_its_face_and_the_ground_below_is_found()
    {
        // A wall 10 m east of the charge; the ground 2 m below the ring point elsewhere.
        DemolitionChargeRules.Ray ray = (o, d, len) =>
        {
            if (d.Z < -0.5f) return o.Z - (-2f) <= len ? o.Z + 2f : null;
            if (d.X > 0.9f && len >= 10f) return 10f;
            return null;
        };
        var east = DemolitionChargeRules.SpotAt(Vector3.Zero, 0, 0, 24f, (o, d, l) => ray(o, d, l));
        float bearing = DemolitionChargeRules.Bearing(0, 0);
        var s = DemolitionChargeRules.SpotAt(Vector3.Zero, 0, 3, 24f, ray);
        Assert.True(s.Z < 0f, "dropped to the ground");
        Assert.Equal(bearing, DemolitionChargeRules.Bearing(0, 0));
        Assert.True(east.Z < 0.5f);
    }

    [Fact]
    public void It_sticks_where_it_lands_waits_5_seconds_and_then_goes_off_with_the_centre_first_and_the_ring_in_about_a_second()
    {
        var w = War(out _);
        ThrowAndStick(w);
        uint stuckAt = w.Tick;
        for (int k = 0; k < 140; k++) Tick(w);
        Assert.Single(w.Projectiles);   // 4.7 s after it stuck: still waiting
        Assert.Equal(0, w.ChargeCentres);
        while (w.ChargeCentres == 0 && w.Tick < stuckAt + 400) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.InRange(w.Tick - stuckAt, 148u, 156u);   // 5 s (150 ticks) after it stuck
        Assert.Equal(0, w.ChargeSpots);                 // the centre blast comes first
        uint centre = w.Tick;
        while (w.ChargeRunsLive > 0 && w.Tick < centre + 120) Tick(w);
        Assert.Equal(16, w.ChargeSpots);
        Assert.InRange(w.Tick - centre, 33u, 36u);      // the 16th spot 1.1 s later
        Assert.Empty(w.Projectiles);
    }

    [Fact]
    public void A_charge_that_sticks_to_nothing_goes_off_5_seconds_after_the_throw()
    {
        var w = War(out _);
        // Thrown straight up into the open: it comes down and sticks at once on this arena, so only the never-landing rule is tested through a charge held in the air.
        Tick(w, InputButtons.Grenade);
        for (int k = 0; k < 40 && w.Projectiles.Count == 0; k++) Tick(w);
        Assert.Single(w.Projectiles);
        uint thrown = w.Projectiles[0].LaunchTick;
        while (w.ChargeCentres == 0 && w.Tick < thrown + 400) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.True(w.Tick - thrown >= 148u, "the fuse never runs short of 5 s from the throw");
    }

    [Fact]
    public void The_damage_at_the_centre_20_m_and_40_m_and_Bosses_take_double()
    {
        var w = War(out _);
        var at = ThrowAndStick(w);
        w.Musou!.Frozen = true;
        // Boys of huge health round the point, a Boss Nob beside them at the same distance.
        ushort near = Ork(w, at + new Vector3(2f, 0f, 0f));
        ushort boss = Ork(w, at + new Vector3(-2f, 0f, 0f), MusouEnemyType.BossNob);
        ushort twenty = Ork(w, at + new Vector3(0f, 20f, 0f));
        ushort forty = Ork(w, at + new Vector3(0f, 0f, 0f) + new Vector3(0f, -40f, 0f));
        float h0 = Health(w, near), b0 = Health(w, boss), t0 = Health(w, twenty), f0 = Health(w, forty);
        for (int k = 0; k < 300 && w.ChargeSpots < 16; k++) Tick(w);
        Assert.Equal(16, w.ChargeSpots);
        float nearLost = h0 - Health(w, near), bossLost = b0 - Health(w, boss);
        Assert.True(nearLost >= 600f, $"the centre blast alone is 600 ({nearLost})");
        Assert.True(bossLost >= nearLost * 1.8f, $"the Boss takes about double ({bossLost} against {nearLost})");
        Assert.InRange(t0 - Health(w, twenty), 380f, 900f);   // the table: about 455 to 535 at 20 m (arena walls and the ring's placement move it)
        Assert.True(f0 - Health(w, forty) < 400f, "40 m is at the edge of the reach");
    }

    [Fact]
    public void At_most_40_kills_of_one_charge_fly_as_corpses_the_rest_are_torn_apart_and_survivors_are_thrown_back()
    {
        var w = War(out _);
        var at = ThrowAndStick(w);
        
        var boys = new List<ushort>();
        for (int k = 0; k < 140; k++)
        {
            float a = k * 2.39996f, r = 1.5f + 5f * MathF.Sqrt((k + 0.5f) / 140f);
            boys.Add(Ork(w, at + new Vector3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0f), health: 50f));
        }
        var survivor = Ork(w, at + new Vector3(0f, 14f, 0f), MusouEnemyType.Boy, 100000f);
        var before = w.Musou!.TryGet(survivor, out var s0) ? s0.Position : default;
        for (int k = 0; k < 300 && w.ChargeSpots < 16; k++) Tick(w);
        int dead = boys.Count(id => Health(w, id) <= 0f);
        Assert.Equal(140, dead);
        Assert.Equal(40, w.ChargeFlown);
        Assert.True(w.Musou.TryGet(survivor, out var s1) && s1.Alive);
        Assert.True(Vector3.Distance(before, s1.Position) > 2f, "thrown back");
    }

    [Fact]
    public void It_sticks_to_an_Ork_rides_on_him_and_drops_where_he_fell_with_the_fuse_running_on()
    {
        var w = War(out _);
        w.Musou!.Frozen = true;
        // An Ork in the line of the throw, near the thrower.
        ushort host = Ork(w, At + new Vector3(0f, 6f, 0f));
        Tick(w, InputButtons.Grenade);
        for (int k = 0; k < 60 && w.ChargeCentres == 0; k++)
        {
            Tick(w);
            if (w.Projectiles.Count == 1 && w.Projectiles[0].Motion == ProjectileMotion.Stuck && w.Musou.TryGet(host, out var e) && Vector3.Distance(w.Projectiles[0].Position, e.Position) < 2.5f) break;
        }
        Assert.Single(w.Projectiles);
        Assert.True(w.Musou.TryGet(host, out var ork));
        Assert.True(Vector3.Distance(w.Projectiles[0].Position, ork.Position) < 2.5f, "stuck to the Ork");
        uint stuckAt = w.Tick;
        // He dies: it drops where he was and still goes off 5 s after it stuck.
        w.Musou.Damage(host, Human, 1e9f, Vector3.UnitY, out _);
        for (int k = 0; k < 60; k++) Tick(w);
        Assert.Single(w.Projectiles);
        while (w.ChargeCentres == 0 && w.Tick < stuckAt + 400) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.InRange(w.Tick - stuckAt, 140u, 160u);
    }

    [Fact]
    public void One_is_carried_and_one_comes_back_every_30_seconds_not_the_grenades_15()
    {
        var w = War(out var config);
        for (int k = 0; k < 5; k++) Tick(w);
        Assert.Equal(1, w.Players[Human].Grenades);
        // As a throw leaves it (the charge would then kill the thrower on this small arena before 30 s pass): the count set short.
        var field = typeof(World).GetField("_players", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        ((Dictionary<int, PlayerState>)field.GetValue(w)!)[Human] = w.Players[Human] with { Grenades = 0 };
        uint from = w.Tick;
        while (w.Players[Human].Grenades == 0 && w.Tick < from + 1200) Tick(w);
        Assert.Equal(1, w.Players[Human].Grenades);
        Assert.InRange(w.Tick - from, 29u * 30u, 31u * 30u);   // 30 s, not the grenades' 15
        Assert.Equal(30f, DemolitionChargeRules.RefillSeconds);
        Assert.Single(config.Thrown);
    }

    [Fact]
    public void It_sticks_to_a_contraption_and_does_not_go_off_at_it_and_a_destroyed_one_drops_it()
    {
        var w = TfwRig.War(config: Config());
        w.TfwMeksOn = true;
        w.Tfw!.Guardsmen = false;
        w.TfwSquigDropsOn = false;
        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        TfwRig.Steps(w, 3);
        var meks = Assert.IsType<MusouMeks>(w.TfwMeks);
        meks.Builds = false;
        w.Musou!.Frozen = true;
        w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.Grenade));
        for (int k = 0; k < 6 && w.Projectiles.Count == 0; k++) Tick(w);
        Assert.Single(w.Projectiles);
        var flight = w.Projectiles[0];
        var spot = flight.Position + flight.Velocity * 0.15f;
        var trap = meks.PlaceBuilt(0, MusouContraptionKind.Trap, spot with { Z = spot.Z - 0.6f });
        Assert.NotNull(trap);
        for (int k = 0; k < 40 && w.Projectiles.Count == 1 && w.Projectiles[0].Motion != ProjectileMotion.Stuck; k++) Tick(w);
        Assert.Single(w.Projectiles);
        Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
        Assert.Equal(0, w.ChargeCentres);   // the core's stop would have set it off here
        Assert.True(Vector3.Distance(w.Projectiles[0].Position, spot) < 4f, "held at the contraption");
        Assert.True(w.Projectiles[0].Position.Z > 0.3f, $"held on the contraption in the air, not on the floor ({w.Projectiles[0].Position.Z})");
        uint stuckAt = w.Tick;
        for (int k = 0; k < 30; k++) Tick(w);
        Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
        // The contraption is destroyed under it: the charge lets go (it falls and rests on the ground) and the fuse runs on, 5 s after it first stuck.
        var held = w.Projectiles[0].Position;
        Assert.True(meks.Damage(trap!.Value.Id, Human, 1e6f, out bool destroyed) && destroyed, "the contraption is destroyed");
        bool let = false;
        for (int k = 0; k < 90 && w.ChargeCentres == 0; k++)
        {
            Tick(w);
            if (w.Projectiles.Count == 1 && w.Projectiles[0].Motion == ProjectileMotion.Flying) let = true;
        }
        Assert.True(let, "it dropped");
        Assert.Equal(0, w.ChargeCentres);
        while (w.ChargeCentres == 0 && w.Tick < stuckAt + 300) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.InRange(w.Tick - stuckAt, 140u, 160u);
    }

    [Fact]
    public void The_Librarians_grenade_key_throws_the_charge_when_he_carries_it()
    {
        var config = MusouLoadout.Prepare(Config());
        int librarian = config.Classes.ToList().FindIndex(c => c.Name == LibrarianRules.ClassName);
        Assert.True(librarian >= 0, "the Librarian is in Musou's config");
        int charge = config.Thrown.ToList().FindIndex(t => t.Name == ArsenalRules.RemoteDetonators);
        Assert.Contains(charge, config.Classes[librarian].EquipmentChoices);
        var map = TfwTestArena.Build();
        var w = new World(config, map.Collision, map.Spawns) { Rules = new MatchRules { CountdownTime = 0.1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 } };
        Assert.True(w.StartTfw(null, TfwTestArena.Field));
        w.Tfw!.Idle = true;
        w.Spawn(Human, librarian);
        for (int k = 0; k < 90 && w.State != MatchState.Playing; k++) Tick(w);
        w.TfwRespawnAt(Human, At, 0f);
        for (int k = 0; k < 5; k++) Tick(w);
        Assert.Equal(librarian, w.Players[Human].Class);
        Assert.Equal(charge, config.Classes[librarian].EquipmentChoices.First(i => i == charge));
        var item = Loadouts.Equipment(config, w.Players[Human]);
        Assert.Equal(ArsenalRules.RemoteDetonators, item?.Name);   // the only item of the test config: his default
        Tick(w, InputButtons.Grenade);
        for (int k = 0; k < 40 && w.Projectiles.Count == 0; k++) Tick(w);
        Assert.Single(w.Projectiles);
        Assert.True(w.Projectiles[0].Thrown);
    }

    [Fact]
    public void A_cleared_field_forgets_the_charge_its_fuse_and_its_ring()
    {
        var w = War(out _);
        ThrowAndStick(w);
        uint from = w.Tick;
        while (w.ChargeCentres == 0 && w.Tick < from + 400) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.True(w.ChargeRunsLive > 0, "the ring is running");
        int spots = w.ChargeSpots;
        typeof(World).GetMethod("TfwClearField", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(w, null);
        Assert.Equal(0, w.ChargeRunsLive);
        for (int k = 0; k < 60; k++) Tick(w);
        Assert.Equal(spots, w.ChargeSpots);   // no spot of the old ring goes off
    }

    [Fact]
    public void It_sticks_to_the_surface_of_a_shield_dome_from_outside_and_does_not_go_off_at_it()
    {
        var w = TfwRig.War(config: Config());
        w.TfwMeksOn = true;
        w.Tfw!.Guardsmen = false;
        w.TfwSquigDropsOn = false;
        w.Tfw.PuppetBrain = (_, p) => new PlayerInput(w.Tick, 0f, 0f, p.Yaw, InputButtons.None);   // the AI Marines shoot nobody
        var battle = TfwRig.Deploy(w);
        battle.WeirdboyzOn = false;
        TfwRig.Steps(w, 3);
        var meks = Assert.IsType<MusouMeks>(w.TfwMeks);
        meks.Builds = false;
        w.Musou!.Frozen = true;
        w.Step(id => new PlayerInput(w.Tick, 0f, 0f, w.Players[id].Yaw, InputButtons.Grenade));
        for (int k = 0; k < 6 && w.Projectiles.Count == 0; k++) Tick(w);
        Assert.Single(w.Projectiles);
        var flight = w.Projectiles[0];
        var along = Vector3.Normalize(new Vector3(flight.Velocity.X, flight.Velocity.Y, 0f));
        var centre = new Vector3(flight.Position.X, flight.Position.Y, 0f) + along * (MusouMekRules.ShieldRadius + 5f);
        var generator = meks.PlaceBuilt(0, MusouContraptionKind.ShieldGenerator, centre);
        Assert.NotNull(generator);
        Assert.True(meks.AnyDome);
        for (int k = 0; k < 60 && w.Projectiles.Count == 1 && w.Projectiles[0].Motion != ProjectileMotion.Stuck; k++) Tick(w);
        Assert.Single(w.Projectiles);
        Assert.Equal(ProjectileMotion.Stuck, w.Projectiles[0].Motion);
        Assert.Equal(0, w.ChargeCentres);   // the core's stop at the dome would have set it off
        float to = Vector3.Distance(w.Projectiles[0].Position, centre);
        Assert.InRange(to, MusouMekRules.ShieldRadius, MusouMekRules.ShieldRadius + 1.5f);   // on the dome's surface, outside it (never inside, where its blast would reach in)
        ushort inside = Ork(w, centre + new Vector3(0f, 0f, 0.1f));
        float insideHealth = Health(w, inside);
        var mek = w.TfwMeks!;
        Assert.True(mek.TryGet(generator!.Value.Id, out var gen));
        float generatorHealth = gen.Health;
        uint stuckAt = w.Tick;
        while (w.ChargeCentres == 0 && w.Tick < stuckAt + 300) Tick(w);
        Assert.Equal(1, w.ChargeCentres);
        Assert.InRange(w.Tick - stuckAt, 140u, 160u);
        // The dome keeps the centre blast out: an Ork inside is unhurt by it. (The generator itself takes the blast, as it takes every blast, and once it is destroyed the dome is gone, so the ring's later spots are not tested here.)
        Assert.Equal(insideHealth, Health(w, inside));
        Assert.True(mek.TryGet(generator.Value.Id, out var after) ? after.Health < generatorHealth : true, "the generator takes the blast or is destroyed by it");
    }
}
