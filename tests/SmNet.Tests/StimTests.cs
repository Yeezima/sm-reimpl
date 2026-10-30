using System.Numerics;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// perks/inert-2: combat stims in the simulation (synthetic values shaped like the install's: 5 s, health-only ×0.75 / ×0.5 / ×0.75, melee ×1.35,
/// reaction immunity; Master-Crafted 7 s and stronger; Favor of the Armory two). The use runs as a throw with the stim's own times and launches nothing;
/// the effects apply to the user only while it lasts; the client's prediction keeps the same time left.
/// </summary>
public class StimTests(Xunit.Abstractions.ITestOutputHelper output)
{
    const float Dt = 1f / 30f;
    const string Armory = "mp_perk_equipment_double", Master = "mp_perk_equipment_effectiveness";

    static readonly StimSpec Base = new(5f, 0.3f, 0.9f)
    {
        ReceivedWorld = 0.75f, ReceivedRanged = 0.75f, ReceivedMelee = 0.5f, ReceivedExplosive = 0.75f, MeleeDamage = 1.35f, ReactionImmune = true,
    };
    static readonly StimSpec Upgraded = Base with { Lifetime = 7f, ReceivedRanged = 0.6f, ReceivedMelee = 0.35f, ReceivedExplosive = 0.6f, MeleeDamage = 2f };
    static readonly ThrownSpec StimBase = new("sm_mp_eq_stimpack", 1, -1, 6f, 0f, ThrownSpec.NoProjectile) { Stim = Base };
    static readonly ThrownSpec Stim = StimBase with
    {
        Variants =
        [
            new([Master], StimBase with { Stim = Upgraded }),
            new([Armory], StimBase with { Count = 2 }),
            new([Master, Armory], StimBase with { Count = 2, Stim = Upgraded }),
        ],
    };

    static readonly ReactionEntry Interrupt = new(0f, ReactionKind.Interrupt);
    /// <summary>A light attack: hit at tick 7 (0.2 s), 40 damage (20 secondary), an interrupt.</summary>
    static readonly MeleeStep Light = new MeleeStep(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f) { PrimaryReaction = Interrupt, SecondaryReaction = Interrupt };
    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 20f, 0.15f, 30, 270, 1.5f, 250f, 245f, 250f, 1f, 2.5f, 1f, 1f, 1f) { HitType = DamageType.Ranged };

    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle],
        Thrown = [Stim, GrenadeTests.Frag],
        Perks = [new PerkSpec(Armory, Armory, PerkKind.Class, 1), new PerkSpec(Master, Master, PerkKind.Class, 1)],
        Classes =
        [
            new ClassLoadout("tac", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [0, 1], Side = Team.SpaceMarines, PerkChoices = [0, 1] },
            new ClassLoadout("chaos", 0, -1, [Light]) { PrimaryChoices = [0], EquipmentChoices = [0, 1], Side = Team.Chaos, PerkChoices = [0, 1] },
        ],
        MeleeRange = 4f, MeleeConeDegrees = 60f, MeleeVerticalReach = 1.5f,
        HealthMax = 1000f, ShieldMax = 0f, HealthRegenDelay = 1e6f, ShieldRegenDelay = 1e6f,
    };

    /// <summary>Player 1 (tactical, at the origin, facing +Y) and player 2 (chaos, <paramref name="gap"/> m ahead), with their equipment and perks.</summary>
    static World Arena(SimConfig c, float gap, int equipment1 = 0, int equipment2 = 0, int perk1 = -1, int perk2 = -1)
    {
        var w = new World(c, null, [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0f, gap, 0f), 0f)]);
        w.Spawn(1, 0, equipmentChoice: equipment1, perk1Choice: perk1);
        w.Spawn(2, 1, equipmentChoice: equipment2, perk1Choice: perk2);
        return w;
    }

    static void Run(World w, int ticks, Func<int, int, InputButtons> buttons, Action<int>? each = null)
    {
        for (int k = 0; k < ticks; k++)
        {
            int tick = k;
            uint view = w.Tick;
            w.Step(id => new PlayerInput(0, 0f, 0f, id == 2 ? MathF.PI : 0f, buttons(tick, id)) with { ViewTick = view });
            each?.Invoke(tick);
        }
    }

    /// <summary>Player <paramref name="id"/> uses the stim at tick 0; the world runs <paramref name="ticks"/> ticks.</summary>
    static World Stimmed(SimConfig c, int id, float gap, int ticks, int perk = -1)
    {
        var w = Arena(c, gap, perk1: id == 1 ? perk : -1, perk2: id == 2 ? perk : -1);
        Run(w, ticks, (t, p) => p == id && t == 0 ? InputButtons.Grenade : InputButtons.None);
        return w;
    }

    [Fact]
    public void The_use_is_a_throw_with_the_stims_own_times_and_launches_nothing()
    {
        var w = Arena(Config, 50f);
        Assert.Equal(1, w.Players[1].Grenades);
        float? releasedAt = null, endedAt = null;
        Run(w, 40, (t, id) => id == 1 && t == 0 ? InputButtons.Grenade : InputButtons.None, each: t =>
        {
            var p = w.Players[1];
            if (releasedAt is null && p.StimLeft > 0f) releasedAt = t * Dt;
            if (endedAt is null && releasedAt is not null && !p.Throwing) endedAt = t * Dt;
            Assert.Empty(w.Projectiles);   // a stim launches nothing
        });
        // The press tick is use time 0; the release in the tick whose interval holds 0.3 s (tick 9), the end at 0.9 s (tick 27).
        Assert.Equal(9 * Dt, releasedAt!.Value, 3);
        Assert.Equal(27 * Dt, endedAt!.Value, 3);
        var after = w.Players[1];
        Assert.Equal(0, after.Grenades);
        Assert.True(after.GrenadeCooldown > 5f);   // the 6 s activation spacing, from the release
        Assert.Equal(5f - (39 - 9) * Dt, after.StimLeft, 3);
        Assert.Null(Stims.Active(Config, w.Players[2]));
    }

    [Fact]
    public void The_stim_wears_off_after_its_lifetime_and_death_ends_it()
    {
        var w = Stimmed(Config, 1, 50f, 9 + 150 + 2);   // 5 s = 150 ticks after the release
        Assert.Equal(0f, w.Players[1].StimLeft);
        Assert.Null(Stims.Active(Config, w.Players[1]));
        var dead = Combat.Killed(Stimmed(Config, 1, 50f, 12).Players[1], Config);
        Assert.Equal(0f, dead.StimLeft);
        Assert.Equal(0f, Combat.Respawned(dead, Config).StimLeft);
    }

    [Fact]
    public void A_stimmed_player_takes_less_health_damage_by_type_and_the_shield_part_is_not_reduced()
    {
        float Health(bool stim, bool melee, SimConfig c)
        {
            var w = Arena(c, melee ? 2f : 5f);
            if (stim) Run(w, 12, (t, id) => id == 2 && t == 0 ? InputButtons.Grenade : InputButtons.None);
            float health = 0f, shield = 0f;
            Run(w, 10, (t, id) => id == 1 && t == 0 ? (melee ? InputButtons.Melee : InputButtons.Fire) : InputButtons.None,
                each: _ => { foreach (var h in w.LastHits) if (h.Target == 2) { health += h.HealthDamage; shield += h.ShieldDamage; } });
            return health + shield * 1000f;   // shield damage in the thousands, health below
        }
        Assert.Equal(20f, Health(false, melee: false, Config), 3);
        Assert.Equal(15f, Health(true, melee: false, Config), 3);   // ranged ×0.75
        Assert.Equal(40f, Health(false, melee: true, Config), 3);
        Assert.Equal(20f, Health(true, melee: true, Config), 3);    // melee ×0.5
        // With a shield in front: the shield takes the shot as before, only what reaches health is scaled.
        var shielded = Config with { ShieldMax = 10f, ShieldBodyMultiplier = 1f };
        Assert.Equal(10f * 1000f + 10f, Health(false, melee: false, shielded), 2);
        Assert.Equal(10f * 1000f + 7.5f, Health(true, melee: false, shielded), 2);
    }

    [Fact]
    public void A_stimmed_attacker_deals_more_melee_damage_and_Master_Crafted_more_still()
    {
        float Dealt(int perk)
        {
            var w = Arena(Config, 2f, perk1: perk);
            Run(w, 30, (t, id) => id == 1 && t == 0 ? InputButtons.Grenade : InputButtons.None);   // past the 0.9 s use
            float health = 0f;
            Run(w, 10, (t, id) => id == 1 && t == 0 ? InputButtons.Melee : InputButtons.None,
                each: _ => { foreach (var h in w.LastHits) if (h.Target == 2) health += h.HealthDamage; });
            return health;
        }
        Assert.Equal(40f * 1.35f, Dealt(-1), 2);
        Assert.Equal(40f * 2f, Dealt(1), 2);   // Master-Crafted Wargear: ×2
        // The shot is not melee: no bonus.
        var w2 = Arena(Config, 5f);
        Run(w2, 30, (t, id) => id == 1 && t == 0 ? InputButtons.Grenade : InputButtons.None);
        float shot = 0f;
        Run(w2, 25, (t, id) => id == 1 && t == 0 ? InputButtons.Fire : InputButtons.None, each: _ => { foreach (var h in w2.LastHits) if (h.Target == 2) shot += h.HealthDamage; });
        Assert.Equal(20f, shot, 3);
    }

    [Fact]
    public void A_stimmed_player_does_not_react_to_hits_while_it_lasts()
    {
        ReactionKind Reacted(bool stim)
        {
            var w = Arena(Config, 2f);
            if (stim) Run(w, 12, (t, id) => id == 2 && t == 0 ? InputButtons.Grenade : InputButtons.None);
            var seen = ReactionKind.None;
            Run(w, 10, (t, id) => id == 1 && t == 0 ? InputButtons.Melee : InputButtons.None, each: _ => { if (w.Players[2].Reaction != ReactionKind.None) seen = w.Players[2].Reaction; });
            return seen;
        }
        Assert.Equal(ReactionKind.Interrupt, Reacted(false));
        Assert.Equal(ReactionKind.None, Reacted(true));
    }

    [Fact]
    public void Perks_pick_the_stims_variants()
    {
        Assert.Same(Stim, Stim.With(default));
        Assert.Equal(7f, Stim.With(new PerkUpgrades(Master, null)).Stim!.Lifetime);
        Assert.Equal(2, Stim.With(new PerkUpgrades(Armory, null)).Count);
        var both = Stim.With(new PerkUpgrades(Armory, Master));
        Assert.Equal((2, 7f), (both.Count, both.Stim!.Lifetime));
        // Favor of the Armory: two uses, the second after the 6 s spacing.
        var w = Arena(Config, 50f, perk1: 0);
        Assert.Equal(2, w.Players[1].Grenades);
        Run(w, 180, (t, id) => id == 1 && t % 2 == 0 ? InputButtons.Grenade : InputButtons.None);
        Assert.Equal(1, w.Players[1].Grenades);   // 180 ticks = 6 s < 6.3 s (release + spacing): the spacing holds the second
        Run(w, 30, (t, id) => id == 1 && t % 2 == 0 ? InputButtons.Grenade : InputButtons.None);
        Assert.Equal(0, w.Players[1].Grenades);
        // Master-Crafted lasts 7 s.
        var m = Stimmed(Config, 1, 50f, 9 + 180, perk: 1);
        Assert.True(m.Players[1].StimLeft > 0.9f);
    }

    [Fact]
    public void The_prediction_keeps_the_same_time_left_and_the_carry_keeps_it_at_a_reconcile()
    {
        var w = Arena(Config, 50f);
        var predicted = w.Players[1];
        for (int k = 0; k < 60; k++)
        {
            var input = new PlayerInput(w.Tick, 0f, 0f, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None) with { ViewTick = w.Tick };
            predicted = Combat.StepSelf(predicted, input, Config, out _);
            w.Step(id => id == 1 ? input : new PlayerInput(w.Tick, 0f, 0f, MathF.PI, InputButtons.None));
            Assert.Equal(w.Players[1].StimLeft, predicted.StimLeft, 4);
        }
        // The record carries no stim time: while the item's cooldown runs it is derived from the record alone (a stall cannot lose it), whatever the prediction.
        var record = w.Players[1] with { StimLeft = 0f };
        Assert.True(record.GrenadeCooldown > 0f);
        Assert.Equal(predicted.StimLeft, Stims.Carry(Config, record, predicted).StimLeft, 3);
        Assert.Equal(predicted.StimLeft, Stims.Carry(Config, record, null).StimLeft, 3);
        Assert.Equal(predicted.StimLeft, Stims.Carry(Config, record, predicted with { StimLeft = 0f }).StimLeft, 3);
        Assert.Equal(0f, Stims.Carry(Config, record with { Alive = false }, predicted).StimLeft);
        Assert.Equal(0f, Stims.Carry(Config, record with { Equipment = 1 }, predicted).StimLeft);   // a frag carrier has no stim
    }

    [Fact]
    public void A_use_the_server_interrupted_before_its_release_shows_no_stim_at_the_next_snapshots()
    {
        // The client predicted the whole use (release at use tick 9); the server interrupted it at use tick 8: nothing spent, no cooldown, no stim.
        var w = Arena(Config, 50f);
        var predicted = w.Players[1];
        for (int k = 0; k < 11; k++)
            predicted = Combat.StepSelf(predicted, new PlayerInput((uint)k, 0f, 0f, 0f, k == 0 ? InputButtons.Grenade : InputButtons.None), Config, out _);
        Assert.True(predicted.StimLeft > 4.9f);
        var interrupted = w.Players[1] with { Throwing = false, ThrowTime = 0f, Grenades = 1, GrenadeCooldown = 0f, Reaction = ReactionKind.Interrupt, ReactionTicks = 2 };
        Assert.Equal(0f, Stims.Carry(Config, interrupted, predicted).StimLeft);   // the snapshot at use tick 9
        Assert.Equal(0f, Stims.Carry(Config, interrupted with { ReactionTicks = 3 }, predicted).StimLeft);   // and at 10
        // A lost press: the server's record shows the stim unused.
        Assert.Equal(0f, Stims.Carry(Config, w.Players[1], predicted).StimLeft);
    }

    [Fact]
    public void Past_the_cooldown_only_an_agreeing_prediction_keeps_the_last_of_a_Master_Crafted_stim()
    {
        // Master-Crafted: 7 s against the 6 s spacing; 1 s can remain after the cooldown, which the record no longer shows.
        var record = Arena(Config, 50f, perk1: 1).Players[1] with { Grenades = 0, GrenadeCooldown = 0f };
        Assert.Equal(0.8f, Stims.Carry(Config, record, record with { StimLeft = 0.8f }).StimLeft, 4);
        Assert.Equal(1f, Stims.Carry(Config, record, record with { StimLeft = 3f }).StimLeft, 4);   // never above lifetime - cooldown
        Assert.Equal(0f, Stims.Carry(Config, record, record with { StimLeft = 0.8f, Grenades = 1 }).StimLeft);   // disagrees on the count
        Assert.Equal(0f, Stims.Carry(Config, record, record with { StimLeft = 0.8f, GrenadeCooldown = 0.5f }).StimLeft);
        Assert.Equal(0f, Stims.Carry(Config, record, null).StimLeft);
        // The base stim (5 s) never outlasts its 6 s spacing.
        var plain = Arena(Config, 50f).Players[1] with { Grenades = 0, GrenadeCooldown = 0f };
        Assert.Equal(0f, Stims.Carry(Config, plain, plain with { StimLeft = 0.8f }).StimLeft);
    }

    [Fact]
    public void The_scoreboard_carries_each_players_equipment_so_clients_tell_a_stim_from_a_throw()
    {
        // Protocol 40: one byte a row (255: none); slim snapshot records still carry no equipment.
        var board = new Scoreboard(7, true, 41, 600f, false, [new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2, 3, 0), new ScoreEntry(2, Team.Chaos, 1, 0, 0, 0, 1), new ScoreEntry(3, Team.Chaos, 1, 0, 0)]);
        var read = Assert.IsType<Scoreboard>(Protocol.Read(Protocol.Write(board)));
        Assert.Equal([0, 1, -1], read.Players.Select(p => p.Equipment));
        Assert.Equal(board.Players, read.Players);
        // The server names the resolved item: the choice, else the class default.
        var w = Arena(Config, 50f, equipment1: 0, equipment2: 1);
        Assert.Equal((0, 1), (Loadouts.EquipmentIndex(Config, w.Players[1]), Loadouts.EquipmentIndex(Config, w.Players[2])));
        Assert.Equal(44, Protocol.Version);
    }

    [Theory]
    [InlineData(531)]
    [InlineData(532)]
    public void Under_150ms_rtt_and_5pct_loss_the_client_never_keeps_a_stim_the_server_did_not_apply(int seed)
    {
        // The victim (client 0) presses Grenade twice a cycle: once while the attacker (client 1, 2 m away) hits it with interrupts (most uses are cut before
        // their release on the server only: the client does not predict hits), once in a quiet spell (the use goes through). Plenty of stims, a 1 s spacing.
        var v = new[] { new Vector3(-2000, -2000, 0), new Vector3(2000, -2000, 0), new Vector3(2000, 2000, 0), new Vector3(-2000, 2000, 0) };
        var mesh = new StaticMesh(v, [0, 1, 2, 0, 2, 3]);
        var collision = new CollisionWorld(mesh, mesh);
        var many = StimBase with { Count = 99, Cooldown = 1f, Variants = [], Stim = Base with { Lifetime = 1f } };   // short, so the next attacked use is not immune
        var config = Config with
        {
            Thrown = [many, GrenadeTests.Frag],
            Classes = Config.Classes.Select(k => k with { Side = Team.None, Melee = [Light with { Damage = 0.01f, SecondaryDamage = 0f }] }).ToArray(),
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var map = new GameMap("flat", collision, [new SpawnPoint(new Vector3(0, 0, 0), 0f), new SpawnPoint(new Vector3(0, 2, 0), 0f)], 93);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) }, map: map);
        for (int i = 0; i < 2; i++) h.AddClient(nonce: (uint)(300 + i), options: new ClientOptions { RequestedClass = (byte)i, LoadMap = _ => new LoadedMap(collision, 93) });
        float YawTo(int i)
        {
            var me = h.Server.World.Players.GetValueOrDefault(h.Clients[i].PlayerId);
            var other = h.Server.World.Players.GetValueOrDefault(h.Clients[1 - i].PlayerId);
            var d = other.Position - me.Position;
            return MathF.Atan2(-d.X, d.Y);
        }
        PlayerInput Input(int i, uint t)
        {
            uint cycle = t % 150;
            if (i == 1) return new PlayerInput(t, 0f, 0f, YawTo(1), cycle == 21 ? InputButtons.Melee : InputButtons.None);
            return new PlayerInput(t, 0f, 0f, YawTo(0), cycle is 20 or 100 ? InputButtons.Grenade : InputButtons.None);
        }
        var truth = new Dictionary<uint, PlayerState>();
        var shown = new Dictionary<uint, PlayerState>();
        h.Run(TimeSpan.FromSeconds(40), Input, () =>
        {
            var c = h.Clients[0];
            if (h.Server.World.Players.TryGetValue(c.PlayerId, out var real)) truth[h.Server.World.Tick] = real;
            if (c.Status == ClientStatus.Connected) shown[c.Tick] = c.Predicted;
        });
        // Runs of ticks where the client shows a stim the server does not have (or misses one it has); the uses that went through and that were cut.
        int phantom = 0, worstPhantom = 0, missing = 0, worstMissing = 0, compared = 0;
        foreach (var (tick, p) in shown.OrderBy(kv => kv.Key))
        {
            if (!truth.TryGetValue(tick, out var real) || tick < 60) continue;
            compared++;
            phantom = p.StimLeft > 0f && real.StimLeft <= 0f ? phantom + 1 : 0;
            missing = real.StimLeft > 0f && p.StimLeft <= 0f ? missing + 1 : 0;
            worstPhantom = Math.Max(worstPhantom, phantom); worstMissing = Math.Max(worstMissing, missing);
        }
        // Server uses: a release is the stim time jumping up; a use that ends without one was cut.
        int applied = 0, cut = 0;
        bool released = false;
        PlayerState? before = null;
        foreach (var (_, real) in truth.OrderBy(kv => kv.Key))
        {
            if (before is { } b)
            {
                if (real.StimLeft > b.StimLeft + 1e-3f) { applied++; released = true; }
                if (b.Throwing && !real.Throwing) { if (!released) cut++; released = false; }
            }
            before = real;
        }
        int predictedTicks = shown.Values.Count(p => p.StimLeft > 0f), serverTicks = truth.Values.Count(p => p.StimLeft > 0f);
        output.WriteLine($"seed {seed}: compared {compared}, stims applied {applied}, uses cut {cut}, stim ticks client {predictedTicks} server {serverTicks}, " +
            $"worst phantom run {worstPhantom} ticks, worst missing run {worstMissing} ticks");
        Assert.True(compared > 600, $"only {compared} ticks compared");
        Assert.True(applied >= 3 && cut >= 3, $"applied {applied}, cut {cut}");
        // A phantom lasts until the first snapshot after the server's release tick reaches the client (about the round trip); a 5 s stim would be 150 ticks.
        Assert.True(worstPhantom <= 15, $"a stim the server never applied was shown for {worstPhantom} ticks");
        Assert.True(worstMissing <= 15, $"a stim the server applied was missing for {worstMissing} ticks");
    }

    [Fact]
    public void A_thrown_item_without_a_stim_hashes_as_before_and_a_stim_changes_the_hash()
    {
        var frag = GrenadeTests.Frag;
        Assert.Null(frag.Stim);
        Assert.Equal(Config.ThrowRelease, frag.ReleaseAt(Config));
        Assert.Equal(Config.ThrowDuration, frag.DurationOf(Config));
        Assert.Equal(0.3f, Stim.ReleaseAt(Config));
        Assert.NotEqual(Variants.Bytes(StimBase), Variants.Bytes(StimBase with { Stim = Base with { MeleeDamage = 1.4f } }));
        Assert.NotEqual(Config.Hash(), (Config with { Thrown = [StimBase, GrenadeTests.Frag] }).Hash());
        Assert.Equal("combat stims", Stim.PlayerName);
    }
}
