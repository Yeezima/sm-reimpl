using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Ammunition and grenade drops (fidelity gap 1, protocol 26): the 3:1 draw and its determinism, the drop at the death point, the interact
/// press edge and reach, amounts and caps (reserve, clip-only weapons, equipment), the lifetime, contested pickups (exactly one collector),
/// the world cap, the round restart, the config hash, the PickupUpdate message (round trip, malformed input, size) and the end-to-end delivery
/// to clients under loss. Synthetic values shaped like the install's (typed in; no game data).
/// </summary>
public class PickupTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Gun = new("test_bolter", WeaponSlot.Ranged, TriggerKind.FullAuto, 10000f, 0.1f, 30, 270, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    static readonly WeaponSpec Sidearm = new("test_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 10f, 0.2f, 10, 40, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    /// <summary>A heavy-bolter-like weapon: everything in the clip, no reserve.</summary>
    static readonly WeaponSpec Heavy = new("test_heavy", WeaponSlot.Ranged, TriggerKind.FullAuto, 10f, 0.1f, 400, 0, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);
    const int Tactical = 0, Devastator = 1;

    /// <summary>Ammo (weight 3): bolter +60, pistol +20, heavy +100; grenades (weight 1): each item +1. Reach 4 m, 25 s, as the install's.</summary>
    internal static readonly PickupSpec Drops = new(
    [
        new PickupType("test_drop_ammo", PickupKind.Ammo, 3, 4f, 25f, [60, 20, 100], [0, 0]),
        new PickupType("test_drop_grenades", PickupKind.Grenades, 1, 4f, 25f, [0, 0, 0], [1, 1]),
    ]);
    const int AmmoType = 0, GrenadeType = 1;

    internal static readonly SimConfig Config = new()
    {
        Weapons = [Gun, Sidearm, Heavy],
        Thrown = [GrenadeTests.Frag, GrenadeTests.Krak],
        Classes =
        [
            new ClassLoadout("test - tactical", 0, 1, []) { EquipmentChoices = [0, 1] },
            new ClassLoadout("test - devastator", 2, 1, []) { EquipmentChoices = [0] },
        ],
        HealthMax = 100f, ShieldMax = 0f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f, RespawnTime = 1000f,
        Pickups = Drops,
    };

    static PlayerState Fresh(int playerClass = Tactical, int equipment = -1) =>
        Combat.Respawned(Loadouts.Choose(Config, new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, -1, -1, -1, equipment), Config);

    static World NewWorld(int players, SimConfig? c = null)
    {
        var w = new World(c ?? Config);
        for (int id = 1; id <= players; id++) w.Spawn(id, Tactical);
        return w;
    }

    /// <summary>The input that turns <paramref name="from"/> toward <paramref name="to"/> (yaw), fire held or not.</summary>
    static PlayerInput Aim(PlayerState from, PlayerState to, InputButtons buttons)
    {
        var d = to.Position - from.Position;
        return new PlayerInput(0, 0, 0, MathF.Atan2(-d.X, d.Y), buttons);
    }

    /// <summary>Steps until <paramref name="victim"/> is dead (shot by <paramref name="shooter"/>); returns the tick it died in.</summary>
    static uint Kill(World w, int shooter, int victim)
    {
        for (int k = 0; k < 60 && w.Players[victim].Alive; k++)
            w.Step(id => id == shooter ? Aim(w.Players[shooter], w.Players[victim], InputButtons.Fire) : new PlayerInput(0, 0, 0, w.Players[id].Yaw, InputButtons.None));
        Assert.False(w.Players[victim].Alive);
        return w.Tick;
    }

    static void Idle(World w, int ticks, Func<int, InputButtons>? buttons = null)
    {
        for (int k = 0; k < ticks; k++) w.Step(id => new PlayerInput(0, 0, 0, w.Players[id].Yaw, buttons?.Invoke(id) ?? InputButtons.None));
    }

    // ---------------------------------------------------------------- the draw

    [Fact]
    public void The_draw_follows_the_3_to_1_weights_and_depends_only_on_the_death()
    {
        int ammo = 0, n = 0;
        for (uint tick = 1; tick <= 2000; tick++)
            for (int victim = 1; victim <= 16; victim++)
            {
                int type = Pickups.Draw(Drops, tick, victim);
                Assert.InRange(type, 0, 1);
                Assert.Equal(type, Pickups.Draw(Drops, tick, victim));   // the same death, the same drop
                if (type == AmmoType) ammo++;
                n++;
            }
        double share = ammo / (double)n;
        output.WriteLine($"ammo share over {n} deaths: {share:F4} (weights 3:1 → 0.75)");
        Assert.InRange(share, 0.74, 0.76);
        // Weights that sum to 0 drop nothing; a single positive weight always wins.
        Assert.Equal(-1, Pickups.Draw(new PickupSpec([Drops.Types[0] with { Weight = 0 }]), 5, 1));
        Assert.Equal(1, Pickups.Draw(new PickupSpec([Drops.Types[0] with { Weight = 0 }, Drops.Types[1]]), 5, 1));
        Assert.Equal(4, Drops.TotalWeight);
    }

    [Fact]
    public void A_death_drops_one_pickup_on_the_ground_where_the_victim_died()
    {
        var w = NewWorld(2);
        var at = w.Players[2].Position;
        uint tick = Kill(w, 1, 2);
        var drop = Assert.Single(w.LivePickups);
        Assert.Equal(drop, Assert.Single(w.LastPickupSpawns));
        Assert.Equal(Pickups.Draw(Drops, tick, 2), drop.Type);
        Assert.Equal(new Vector3(at.X, at.Y, 0f), drop.Position);   // flat ground: z = 0
        Assert.Equal((2, tick, tick + 750u), (drop.Dropper, drop.SpawnTick, drop.ExpireTick));   // 25 s at 30 Hz
        Assert.True(drop.Id is >= 1 and <= ushort.MaxValue);
        Idle(w, 5);
        Assert.Single(w.LivePickups);   // one per death, not one per tick dead
        Assert.Empty(w.LastPickupSpawns);
    }

    [Fact]
    public void The_same_match_drops_the_same_pickups_every_time()
    {
        static IReadOnlyList<Pickup> Run()
        {
            var w = NewWorld(4);
            Kill(w, 1, 2); Idle(w, 3); Kill(w, 1, 3); Kill(w, 4, 1);
            return w.LivePickups.ToList();
        }
        var a = Run();
        Assert.Equal(3, a.Count);
        Assert.Equal(a, Run());
    }

    [Fact]
    public void No_drops_without_a_pickup_spec()
    {
        var w = NewWorld(2, Config with { Pickups = null });
        Kill(w, 1, 2);
        Assert.Empty(w.LivePickups);
        Assert.Null(w.SpawnPickup(0, Vector3.Zero));
    }

    // ---------------------------------------------------------------- collection

    [Fact]
    public void Ammo_tops_up_both_weapons_reserves_capped_at_their_sizes()
    {
        var s = Fresh() with { Reserve = 100, OtherReserve = 35 };   // bolter in hand 100 / 270, pistol 35 / 40
        var (after, gain) = Pickups.Collect(Config, s, Drops.Types[AmmoType]);
        Assert.Equal((160, 40), (after.Reserve, after.OtherReserve));   // +60, and +20 capped to +5
        Assert.Equal(new PickupGain(60, 5, 0), gain);
        Assert.Equal((s.Clip, s.OtherClip, s.Grenades), (after.Clip, after.OtherClip, after.Grenades));   // clips and grenades untouched
        // Full reserves: nothing more (the pickup is still taken, as the data has no condition).
        var (full, none) = Pickups.Collect(Config, Fresh(), Drops.Types[AmmoType]);
        Assert.Equal((270, 40), (full.Reserve, full.OtherReserve));
        Assert.False(none.Any);
        // The pistol in hand: the amounts follow the weapons, not the slots.
        var pistolOut = Fresh() with { Slot = WeaponSlot.Pistol, Clip = 4, Reserve = 0, OtherClip = 30, OtherReserve = 0 };
        var (p, pg) = Pickups.Collect(Config, pistolOut, Drops.Types[AmmoType]);
        Assert.Equal((20, 60), (p.Reserve, p.OtherReserve));
        Assert.Equal(new PickupGain(60, 20, 0), pg);
    }

    [Fact]
    public void A_weapon_without_a_reserve_takes_the_ammo_in_its_clip_capped_at_the_clip()
    {
        var s = Fresh(Devastator) with { Clip = 250 };
        Assert.Equal((Heavy.Name, 0), (Loadouts.InHand(Config, s)!.Name, s.Reserve));
        var (after, gain) = Pickups.Collect(Config, s, Drops.Types[AmmoType]);
        Assert.Equal((350, 0, 100), (after.Clip, after.Reserve, gain.Primary));
        var (capped, g2) = Pickups.Collect(Config, s with { Clip = 380 }, Drops.Types[AmmoType]);
        Assert.Equal((400, 20), (capped.Clip, g2.Primary));
    }

    [Fact]
    public void Grenades_add_one_of_the_carried_item_capped_at_its_count()
    {
        var s = Fresh();
        Assert.Equal((0, 1), (s.Equipment, s.Grenades));   // the frag: 1 of 2 at spawn
        var (one, g) = Pickups.Collect(Config, s, Drops.Types[GrenadeType]);
        Assert.Equal((2, 1), (one.Grenades, g.Grenades));
        var (two, g2) = Pickups.Collect(Config, one, Drops.Types[GrenadeType]);
        Assert.Equal((2, 0), (two.Grenades, g2.Grenades));   // capped at the count
        var krak = Fresh(equipment: 1) with { Grenades = 0 };
        Assert.Equal(1, Pickups.Collect(Config, krak, Drops.Types[GrenadeType]).State.Grenades);
        // The grenade drop gives no ammunition, the ammo drop no grenades.
        Assert.Equal((s.Reserve, s.OtherReserve), (one.Reserve, one.OtherReserve));
        Assert.Equal(1, Pickups.Collect(Config, s, Drops.Types[AmmoType]).State.Grenades);
        // The dead gain nothing.
        Assert.False(Pickups.Collect(Config, s with { Alive = false, Grenades = 0 }, Drops.Types[GrenadeType]).Gain.Any);
    }

    [Fact]
    public void One_interact_press_in_reach_collects_holding_does_not_repeat()
    {
        var w = NewWorld(2);
        var me = w.Players[1];
        var near = w.SpawnPickup(GrenadeType, me.Position + new Vector3(3.9f, 0f, 0f))!.Value;
        var far = w.SpawnPickup(GrenadeType, me.Position + new Vector3(4.2f, 0f, 0f))!.Value;
        Assert.Equal(1, me.Grenades);
        Idle(w, 1, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        var e = Assert.Single(w.LastPickupEvents);
        Assert.Equal((near.Id, PickupEnd.Collected, 1, new PickupGain(0, 0, 1)), (e.Id, e.End, e.Collector, e.Gain));
        Assert.Equal(2, w.Players[1].Grenades);
        Assert.Equal(far.Id, Assert.Single(w.LivePickups).Id);   // 4.2 m: out of reach
        // Held for 10 more ticks: no new press, nothing happens; walked into reach while holding: still nothing.
        Idle(w, 10, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        Assert.Single(w.LivePickups);
        Assert.Empty(w.LastPickupEvents);
    }

    [Fact]
    public void A_press_out_of_reach_or_while_dead_collects_nothing()
    {
        var w = NewWorld(3);
        var p = w.SpawnPickup(AmmoType, w.Players[3].Position)!.Value;
        Kill(w, 1, 3);   // player 3 dies on its own pickup's spot (and drops another there)
        int live = w.LivePickups.Count;
        Idle(w, 1, id => id == 3 ? InputButtons.Interact : InputButtons.None);
        Assert.Empty(w.LastPickupEvents);
        Assert.Equal(live, w.LivePickups.Count);
        Assert.Contains(w.LivePickups, q => q.Id == p.Id);
        // Player 2 is 5+ m away: nothing either.
        Assert.True(Vector3.Distance(w.Players[2].Position, p.Position) > 5f);
        Idle(w, 1);
        Idle(w, 1, id => id == 2 ? InputButtons.Interact : InputButtons.None);
        Assert.Empty(w.LastPickupEvents);
        Assert.Equal(live, w.LivePickups.Count);
    }

    [Fact]
    public void Players_pressing_on_one_pickup_in_the_same_tick_exactly_one_gets_it()
    {
        var w = NewWorld(3);
        // Players 1 and 2 stand 5.5 m apart; a pickup between them, a little nearer player 2.
        var a = w.Players[1].Position; var b = w.Players[2].Position;
        var mid = a + (b - a) * 0.55f;
        var only = w.SpawnPickup(AmmoType, mid)!.Value;
        Idle(w, 1, id => InputButtons.Interact);   // everyone presses in the same tick
        var e = Assert.Single(w.LastPickupEvents);
        Assert.Equal((only.Id, 2), (e.Id, e.Collector));   // the nearer one
        Assert.Empty(w.LivePickups);
        // Two pickups in reach of both: the nearest claim first (player 2 on the second), then the one who lost it gets the other.
        var w2 = NewWorld(2);
        var c = (w2.Players[1].Position + w2.Players[2].Position) / 2f;
        var first = w2.SpawnPickup(GrenadeType, c)!.Value;
        var second = w2.SpawnPickup(GrenadeType, c + Vector3.Normalize(w2.Players[2].Position - w2.Players[1].Position) * 1.2f)!.Value;
        Idle(w2, 1, id => InputButtons.Interact);
        Assert.Equal(2, w2.LastPickupEvents.Count);
        Assert.Equal(2, w2.LastPickupEvents.Single(x => x.Id == second.Id).Collector);
        Assert.Equal(1, w2.LastPickupEvents.Single(x => x.Id == first.Id).Collector);
        Assert.Empty(w2.LivePickups);
        Assert.Equal((2, 2), (w2.Players[1].Grenades, w2.Players[2].Grenades));   // one each
        // Three presses of the same tick on a single pickup, many times over: always exactly one collection.
        for (int round = 0; round < 20; round++)
        {
            var w3 = NewWorld(3);
            var centre = (w3.Players[1].Position + w3.Players[2].Position + w3.Players[3].Position) / 3f + new Vector3(round * 0.01f, 0, 0);
            w3.SpawnPickup(AmmoType, centre);
            Idle(w3, 1, id => InputButtons.Interact);
            Assert.Single(w3.LastPickupEvents);
            Assert.Empty(w3.LivePickups);
        }
    }

    [Fact]
    public void A_pickup_expires_after_its_25_s_lifetime()
    {
        var w = NewWorld(2);
        var p = w.SpawnPickup(AmmoType, new Vector3(50, 50, 0))!.Value;
        uint expired = 0;
        for (int k = 0; k < 800 && expired == 0; k++)
        {
            Idle(w, 1);
            if (w.LastPickupEvents.Count > 0) expired = w.Tick;
        }
        var e = Assert.Single(w.LastPickupEvents);
        Assert.Equal((p.Id, PickupEnd.Expired, 0), (e.Id, e.End, e.Collector));
        Assert.Equal(p.SpawnTick + 750u, expired);
        Assert.Empty(w.LivePickups);
    }

    [Fact]
    public void More_than_the_world_cap_expires_the_oldest()
    {
        var w = new World(Config with { Pickups = Drops with { MaxLive = 3 } });
        var ids = Enumerable.Range(0, 4).Select(k => w.SpawnPickup(AmmoType, new Vector3(k, 0, 0))!.Value.Id).ToList();
        Assert.Equal(ids.Skip(1), w.LivePickups.Select(p => p.Id));
        Assert.Equal((ids[0], PickupEnd.Expired), (w.LastPickupEvents.Single().Id, w.LastPickupEvents.Single().End));
        // A cap above what one message carries is clamped to it (review 2026-10-28).
        var big = new World(Config with { Pickups = Drops with { MaxLive = 1000 } });
        for (int k = 0; k < PickupSpec.MaxLiveLimit + 10; k++) big.SpawnPickup(AmmoType, new Vector3(k, 0, 0));
        Assert.Equal(PickupSpec.MaxLiveLimit, big.LivePickups.Count);
        // Ids are unique among the living and stay within 16 bits.
        Assert.Equal(w.LivePickups.Count, w.LivePickups.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void A_new_round_clears_the_pickups()
    {
        var c = Config with { Classes = [Config.Classes[0] with { Side = Team.SpaceMarines }, Config.Classes[0] with { Name = "test - chaos", Side = Team.Chaos }] };
        var w = new World(c) { Rules = new MatchRules { TimeLimit = 0f, ScoreLimit = 1, RestartDelay = 0.1f } };
        w.Spawn(1, 0); w.Spawn(2, 1);
        Idle(w, 2);
        Kill(w, 1, 2);   // the score limit: the match ends
        Assert.NotEmpty(w.LivePickups);
        // Once the match has ended nothing is collected (review: pickups act in warm-up and play only); expiry still runs.
        Assert.Equal(MatchState.Ended, w.State);
        Assert.True(w.PickupsActive);   // the tick whose death ended the match still drops (the assertion above)
        Idle(w, 1);
        Assert.False(w.PickupsActive);
        w.SpawnPickup(AmmoType, w.Players[1].Position);
        int lying = w.LivePickups.Count;
        Idle(w, 1, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        Assert.Empty(w.LastPickupEvents);
        Assert.Equal(lying, w.LivePickups.Count);
        Idle(w, 10);   // the restart
        Assert.Empty(w.LivePickups);
    }

    [Fact]
    public void The_countdown_clears_the_pickups_when_everyone_respawns_and_nothing_is_collected_until_play()
    {
        var c = Config with { Classes = [Config.Classes[0] with { Side = Team.SpaceMarines }, Config.Classes[0] with { Name = "test - chaos", Side = Team.Chaos }] };
        var w = new World(c) { Rules = new MatchRules { TimeLimit = 0f, ScoreLimit = 100, CountdownTime = 1f } };
        w.Spawn(1, 0);
        Idle(w, 2);
        Assert.Equal(MatchState.Warmup, w.State);
        Assert.True(w.PickupsActive);
        w.SpawnPickup(AmmoType, new Vector3(50, 50, 0));
        Assert.Single(w.LivePickups);
        // The other side joins: the countdown starts and everyone respawns for it; the warm-up's pickups go with it.
        w.Spawn(2, 1);
        Idle(w, 1);
        Assert.Equal(MatchState.Countdown, w.State);
        Assert.Contains(1, w.LastRespawns);
        Assert.Empty(w.LivePickups);
        Assert.False(w.PickupsActive);
        // During the countdown a press in reach collects nothing (the pickup stays; it still expires as usual).
        var p = w.SpawnPickup(GrenadeType, w.Players[1].Position)!.Value;
        Idle(w, 1, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        Idle(w, 1);
        Idle(w, 1, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        Assert.Empty(w.LastPickupEvents);
        Assert.Contains(w.LivePickups, q => q.Id == p.Id);
        // Play begins (no respawn, the pickup stays): the next press collects it.
        for (int k = 0; k < 40 && w.State == MatchState.Countdown; k++) Idle(w, 1);
        Assert.Equal(MatchState.Playing, w.State);
        Assert.Contains(w.LivePickups, q => q.Id == p.Id);
        Idle(w, 1);
        Idle(w, 1, id => id == 1 ? InputButtons.Interact : InputButtons.None);
        Assert.Equal((p.Id, 1), (w.LastPickupEvents.Single().Id, w.LastPickupEvents.Single().Collector));
    }

    [Fact]
    public void The_drops_are_part_of_the_config_hash()
    {
        Assert.NotEqual(Config.Hash(), (Config with { Pickups = null }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Pickups = new PickupSpec([Drops.Types[0] with { Lifetime = 20f }, Drops.Types[1]]) }).Hash());
        Assert.NotEqual(Config.Hash(), (Config with { Pickups = new PickupSpec([Drops.Types[0] with { WeaponAmounts = [61, 20, 100] }, Drops.Types[1]]) }).Hash());
        Assert.Equal(Config.Hash(), (Config with { Pickups = new PickupSpec([.. Drops.Types]) }).Hash());
    }

    // ---------------------------------------------------------------- the wire

    static PickupUpdate Sample(int live, int events) => new(1234,
        Enumerable.Range(1, live).Select(k => new PickupView(k * 7, k % 2, new Vector3(k * 1.5f, -k, 0.25f))).ToArray(),
        Enumerable.Range(0, events).Select(k => new PickupEventView(65530 + k & 0xFFFF, 100 + k, k % 2, k % 3 == 0 ? PickupEnd.Expired : PickupEnd.Collected,
            k % 3 == 0 ? 0 : 1 + k, new Vector3(-k, k * 0.5f, 3f))).ToArray());

    [Fact]
    public void The_pickup_message_round_trips_and_refuses_truncation()
    {
        foreach (var (live, events) in new[] { (0, 0), (1, 0), (0, 1), (5, 3), (Protocol.MaxPickupRecords, Protocol.MaxPickupEvents) })
        {
            var m = Sample(live, events);
            var bytes = Protocol.Write(m);
            Assert.Equal(7 + live * Protocol.PickupRecordBytes + events * Protocol.PickupEventBytes, bytes.Length);
            var back = Assert.IsType<PickupUpdate>(Protocol.Read(bytes));
            Assert.Equal(m.ServerTick, back.ServerTick);
            Assert.Equal(m.Live!.Select(Protocol.Quantize), back.Live);
            Assert.Equal(m.Events.Select(Protocol.Quantize), back.Events);
            for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));
            Assert.Null(Protocol.Read([.. bytes, 0]));
        }
        Assert.Equal(1031, Protocol.MaxPickupMessageBytes);
        Assert.True(Protocol.MaxPickupMessageBytes <= Protocol.SnapshotBudget);
        Assert.Equal(Protocol.MaxPickupMessageBytes, Protocol.Write(Sample(Protocol.MaxPickupRecords, Protocol.MaxPickupEvents)).Length);
        // Too many: the writer keeps the first pickups and the newest events.
        var over = Sample(Protocol.MaxPickupRecords + 5, Protocol.MaxPickupEvents + 4);
        var cut = Assert.IsType<PickupUpdate>(Protocol.Read(Protocol.Write(over)));
        Assert.Equal(over.Live!.Take(Protocol.MaxPickupRecords).Select(p => p.Id), cut.Live!.Select(p => p.Id));
        Assert.Equal(over.Events.Skip(4).Select(e => e.Sequence), cut.Events.Select(e => e.Sequence));
        // Events only (no list): the live count byte is NoPickupList and the list reads back as null (the client keeps its own).
        var eventsOnly = Sample(0, 3) with { Live = null };
        var eb = Protocol.Write(eventsOnly);
        Assert.Equal((7 + 3 * Protocol.PickupEventBytes, Protocol.NoPickupList), (eb.Length, eb[5]));
        var eback = Assert.IsType<PickupUpdate>(Protocol.Read(eb));
        Assert.Null(eback.Live);
        Assert.Equal(eventsOnly.Events.Select(Protocol.Quantize), eback.Events);
        for (int n = 0; n < eb.Length; n++) Assert.Null(Protocol.Read(eb.AsSpan(0, n)));
        Assert.Equal(PickupSpec.MaxLiveLimit, Protocol.MaxPickupRecords);
    }

    static byte[] Patch(byte[] bytes, int at, byte value) { var copy = (byte[])bytes.Clone(); copy[at] = value; return copy; }

    [Fact]
    public void Malformed_pickup_messages_are_refused()
    {
        var bytes = Protocol.Write(Sample(2, 2));   // live at 6.., events at 7 + 24 = 31..
        Assert.NotNull(Protocol.Read(bytes));
        Assert.Null(Protocol.Read(Patch(bytes, 5, Protocol.MaxPickupRecords + 1)));          // too many pickups
        Assert.Null(Protocol.Read(Patch(bytes, 5, Protocol.NoPickupList - 1)));               // 254 is no count either
        Assert.Null(Protocol.Read(Patch(Patch(bytes, 6, 0), 7, 0)));                           // pickup id 0
        Assert.Null(Protocol.Read(Patch(bytes, 8, PickupSpec.MaxTypes)));                      // type out of range
        Assert.Null(Protocol.Read(Patch(Patch(bytes, 18, bytes[6]), 19, bytes[7])));           // the same pickup twice
        int ev = 6 + 1 + 2 * Protocol.PickupRecordBytes - 1;                                   // the event count byte
        Assert.Null(Protocol.Read(Patch(bytes, ev, Protocol.MaxPickupEvents + 1)));            // too many events
        int e0 = ev + 1;
        Assert.Null(Protocol.Read(Patch(Patch(bytes, e0 + 2, 0), e0 + 3, 0)));                 // event pickup id 0
        Assert.Null(Protocol.Read(Patch(bytes, e0 + 4, (byte)(bytes[e0 + 4] & 15 | 3 << 4)))); // end 3 unknown
        Assert.Null(Protocol.Read(Patch(bytes, e0 + 4, (byte)(bytes[e0 + 4] & 15))));         // end 0
        // Event 0 is an expiry (collector 0): naming a collector is malformed; event 1 a collection: collector 0 is.
        Assert.Null(Protocol.Read(Patch(bytes, e0 + 5, 9)));
        int e1 = e0 + Protocol.PickupEventBytes;
        Assert.Null(Protocol.Read(Patch(Patch(bytes, e1 + 5, 0), e1 + 6, 0)));
        Assert.Null(Protocol.Read(Patch(Patch(bytes, e1, bytes[e0]), e1 + 1, bytes[e0 + 1])));   // the same event twice
        // The writer refuses what cannot travel.
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [new PickupView(0, 0, default)], [])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [new PickupView(70000, 0, default)], [])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [new PickupView(1, 16, default)], [])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [], [new PickupEventView(1, 1, 0, PickupEnd.Collected, 0, default)])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [], [new PickupEventView(1, 1, 0, PickupEnd.Expired, 3, default)])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new PickupUpdate(1, [], [new PickupEventView(1, 1, 0, (PickupEnd)7, 3, default)])));
        // Random garbage after the kind byte never throws.
        var rng = new Random(24);
        for (int k = 0; k < 2000; k++)
        {
            var junk = new byte[rng.Next(1, 200)];
            rng.NextBytes(junk);
            junk[0] = bytes[0];
            _ = Protocol.Read(junk);
        }
    }

    [Fact]
    public void The_interact_button_survives_the_input_batch()
    {
        var batch = new InputBatch([new PlayerInput(7, 0, 0, 0, InputButtons.Interact | InputButtons.Fire)], 5, 6);
        var back = Assert.IsType<InputBatch>(Protocol.Read(Protocol.Write(batch)));
        Assert.Equal(InputButtons.Interact | InputButtons.Fire, back.Inputs[0].Buttons);
        Assert.Equal(4096, (int)InputButtons.Interact);
    }

    // ---------------------------------------------------------------- end to end

    [Fact]
    public void Clients_see_drops_collections_and_expiries_through_a_lossy_link()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10), 0.25);
        var h = new Harness(link, 24, 2, Config with { Pickups = Drops with { Types = [Drops.Types[0] with { Lifetime = 2f }, Drops.Types[1] with { Lifetime = 2f }] } });
        h.Run(TimeSpan.FromSeconds(1.5));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        int me = h.Clients[0].PlayerId;
        var at = h.Server.World.Players[me].Position;
        var collect = h.Server.World.SpawnPickup(GrenadeType, at + new Vector3(1f, 0, 0))!.Value;
        var expire = h.Server.World.SpawnPickup(AmmoType, at + new Vector3(30f, 0, 0))!.Value;
        h.Run(TimeSpan.FromSeconds(0.5));
        Assert.All(h.Clients, c => Assert.Equal(new[] { collect.Id, expire.Id }, c.Pickups.Select(p => p.Id).Order()));
        var shown = h.Clients[1].Pickups.Single(p => p.Id == collect.Id);
        Assert.Equal(Protocol.Quantize(collect.Position), shown.Position);
        Assert.Equal(GrenadeType, shown.Type);
        int grenadesBefore = h.Server.World.Players[me].Grenades;
        // Client 0 presses interact for a few ticks (a press edge, then held).
        h.Run(TimeSpan.FromMilliseconds(200), (i, t) => new PlayerInput(t, 0, 0, 0, i == 0 ? InputButtons.Interact : InputButtons.None));
        h.Run(TimeSpan.FromSeconds(2.5));
        Assert.Equal(grenadesBefore + 1, h.Server.World.Players[me].Grenades);
        foreach (var c in h.Clients)
        {
            var events = c.TakePickupEvents();
            output.WriteLine($"client {c.PlayerId}: {c.PickupMessages} pickup messages, events {string.Join(", ", events.Select(e => $"#{e.Id} {e.End} by {e.Collector}"))}");
            Assert.Contains(events, e => e.Id == collect.Id && e.End == PickupEnd.Collected && e.Collector == me);
            Assert.Contains(events, e => e.Id == expire.Id && e.End == PickupEnd.Expired && e.Collector == 0);
            Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());   // each once
            Assert.Empty(c.Pickups);
            Assert.Empty(c.TakePickupEvents());
        }
        Assert.Equal(grenadesBefore + 1, h.Clients[0].Predicted.Grenades);   // the own record carries the gain
        output.WriteLine($"server sent {h.Server.PickupMessagesSent} pickup messages");
    }

    [Fact]
    public void A_late_joiner_does_not_replay_the_last_second_and_an_unread_queue_stays_bounded()
    {
        var h = new Harness(LinkConditions.Perfect, 2, 1, Config with { Pickups = Drops with { Types = [Drops.Types[0] with { Lifetime = 0.1f }, Drops.Types[1]] } });
        h.Run(TimeSpan.FromSeconds(1));
        // 400 short-lived pickups: their expiries are events; the client never takes them.
        for (int t = 0; t < 40; t++)
        {
            for (int k = 0; k < 10; k++) h.Server.World.SpawnPickup(AmmoType, new Vector3(40 + k, t, 0));
            h.Run(TimeSpan.FromMilliseconds(34));
        }
        h.Run(TimeSpan.FromMilliseconds(400));
        var late = h.AddClient(nonce: 4242);
        h.Run(TimeSpan.FromMilliseconds(300));   // joins while the last expiries are still in the messages
        Assert.Equal(ClientStatus.Connected, late.Status);
        Assert.True(late.PickupMessages > 0);
        Assert.Empty(late.TakePickupEvents());
        var queued = h.Clients[0].TakePickupEvents();
        output.WriteLine($"queued for the reader that never took them: {queued.Count} (cap {GameClient.MaxQueuedPickupEvents})");
        Assert.InRange(queued.Count, 200, GameClient.MaxQueuedPickupEvents);
        Assert.Equal(queued.Count, queued.Select(e => e.Sequence).Distinct().Count());
    }

    [Fact]
    public void An_unchanged_list_goes_out_about_once_a_second_and_events_keep_it()
    {
        var h = new Harness(LinkConditions.Perfect, 3, 1, Config);
        h.Run(TimeSpan.FromSeconds(1));
        for (int k = 0; k < Protocol.MaxPickupRecords; k++) h.Server.World.SpawnPickup(AmmoType, new Vector3(30 + k % 8, 30 + k / 8, 0));
        h.Run(TimeSpan.FromMilliseconds(200));
        int before = h.Server.PickupMessagesSent; long bytes = h.Server.PickupBytesSent;
        h.Run(TimeSpan.FromSeconds(5), after: () => Assert.Equal(Protocol.MaxPickupRecords, h.Clients[0].Pickups.Count));
        int sent = h.Server.PickupMessagesSent - before;
        double rate = (h.Server.PickupBytesSent - bytes) / 5.0;
        output.WriteLine($"64 lying, nothing changing: {sent} messages in 5 s, {rate:F0} bytes/s per client");
        Assert.InRange(sent, 4, 6);
        Assert.InRange(rate, 0, 800);
        // Churn: one new pickup every tick over a full world (each expires the oldest): a list and an event every tick, the worst case.
        bytes = h.Server.PickupBytesSent; before = h.Server.PickupMessagesSent;
        for (int t = 0; t < 60; t++) { h.Server.World.SpawnPickup(GrenadeType, new Vector3(-30, t, 0)); h.Run(TimeSpan.FromMilliseconds(1000.0 / 30)); }
        double churn = (h.Server.PickupBytesSent - bytes) / 2.0;
        output.WriteLine($"churn (one drop per tick over 64 lying): {h.Server.PickupMessagesSent - before} messages in 2 s, {churn:F0} bytes/s per client, largest {h.Server.LargestPickupMessage} bytes");
        Assert.True(h.Server.LargestPickupMessage <= Protocol.MaxPickupMessageBytes);
        Assert.InRange(churn, 0, 30 * Protocol.MaxPickupMessageBytes + 1);
        Assert.Equal(Protocol.MaxPickupRecords, h.Clients[0].Pickups.Count);
    }

    [Fact]
    public void Without_drops_no_pickup_message_is_sent()
    {
        var h = new Harness(LinkConditions.Perfect, 1, 1, Config with { Pickups = null });
        h.Run(TimeSpan.FromSeconds(2));
        Assert.Equal(0, h.Server.PickupMessagesSent);
        Assert.Equal(0, h.Clients[0].PickupMessages);
    }
}
