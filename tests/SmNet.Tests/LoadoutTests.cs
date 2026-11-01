using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Classes and loadouts: spawn loadouts, weapon switching, semi/full automatic triggers, per-weapon hit values, class
/// choice in the handshake, per-recipient snapshots, the config hash, and exact prediction while switching and firing.
/// Synthetic weapons and classes only (no game data).
/// </summary>
public class LoadoutTests(ITestOutputHelper output)
{
    static readonly WeaponSpec Rifle = new("test_rifle", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 10f, ShotCooldown: 0.1f, ClipSize: 20, ReserveSize: 60, ReloadTime: 1.0f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f);
    static readonly WeaponSpec Pistol = new("test_pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto,
        Damage: 30f, ShotCooldown: 0.02f, ClipSize: 8, ReserveSize: 24, ReloadTime: 0.5f, MaxRange: 20f,
        FallOffStart: 20f, FallOffEnd: 20f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 3f, BodyMultiplier: 1.5f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 0.5f);
    static readonly WeaponSpec Heavy = new("test_heavy", WeaponSlot.Ranged, TriggerKind.FullAuto,
        Damage: 15f, ShotCooldown: 0.1f, ClipSize: 60, ReserveSize: 120, ReloadTime: 2.0f, MaxRange: 100f,
        FallOffStart: 100f, FallOffEnd: 100f, FallOffEndMultiplier: 1f,
        HeadMultiplier: 2f, BodyMultiplier: 1f, ShieldHeadMultiplier: 1f, ShieldBodyMultiplier: 1f);

    static readonly MeleeStep Knife = new(1.0f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 40f, 20f, 3.5f);

    const int Tactical = 0, Assault = 1, Devastator = 2;

    /// <summary>Tactical: rifle + pistol + knife; Assault: pistol + knife, no primary; Devastator: heavy + pistol, no melee.</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol, Heavy],
        Classes =
        [
            new ClassLoadout("test - tactical", 0, 1, [Knife, Knife]),
            new ClassLoadout("test - assault", -1, 1, [Knife]),
            new ClassLoadout("test - devastator", 2, 1, []),
        ],
    };

    static float SwitchTicks => Config.WeaponSwitchTime * Config.TickRate;   // 18 at 0.6 s and 30 Hz

    static PlayerState Fresh(int playerClass, SimConfig? c = null) =>
        Combat.Respawned(new PlayerState(1, Vector3.Zero, Vector3.Zero, 0f, true) { Class = playerClass }, c ?? Config);

    static PlayerState Step(ref PlayerState s, InputButtons buttons, out bool fired, SimConfig? c = null)
    {
        s = Combat.StepSelf(s, new PlayerInput(0, 0, 0, 0, buttons), c ?? Config, out fired, out _);
        return s;
    }

    static int Shots(ref PlayerState s, IEnumerable<InputButtons> script)
    {
        int shots = 0;
        foreach (var b in script) { Step(ref s, b, out bool fired); if (fired) shots++; }
        return shots;
    }

    // ---------------------------------------------------------------- spawn loadouts

    [Fact]
    public void Each_class_spawns_with_its_weapon_in_hand_and_both_weapons_full()
    {
        var t = Fresh(Tactical);
        Assert.Equal((WeaponSlot.Ranged, 20, 60, 8, 24, 0f), (t.Slot, t.Clip, t.Reserve, t.OtherClip, t.OtherReserve, t.SwitchLeft));
        var a = Fresh(Assault);   // no primary: the pistol is in hand, nothing holstered
        Assert.Equal((WeaponSlot.Pistol, 8, 24, 0, 0), (a.Slot, a.Clip, a.Reserve, a.OtherClip, a.OtherReserve));
        var d = Fresh(Devastator);
        Assert.Equal((WeaponSlot.Ranged, 60, 120, 8, 24), (d.Slot, d.Clip, d.Reserve, d.OtherClip, d.OtherReserve));
        Assert.Same(Heavy, Loadouts.InHand(Config, d));

        // The world spawns the requested class.
        var world = new World(Config);
        world.Spawn(1, Assault); world.Spawn(2, Devastator);
        Assert.Equal((Assault, WeaponSlot.Pistol, 8), (world.Players[1].Class, world.Players[1].Slot, world.Players[1].Clip));
        Assert.Equal((Devastator, WeaponSlot.Ranged, 60), (world.Players[2].Class, world.Players[2].Slot, world.Players[2].Clip));
    }

    [Fact]
    public void Respawn_keeps_the_class_and_refills_both_weapons_with_the_primary_in_hand()
    {
        var s = Fresh(Tactical);
        Step(ref s, InputButtons.Switch, out _);
        for (int i = 0; i < 20; i++) Step(ref s, i % 2 == 0 ? InputButtons.Fire : InputButtons.None, out _);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        Assert.True(s.Clip < 8);
        var back = Combat.Respawned(Combat.Killed(s, Config), Config);
        Assert.Equal((Tactical, WeaponSlot.Ranged, 20, 60, 8, 24), (back.Class, back.Slot, back.Clip, back.Reserve, back.OtherClip, back.OtherReserve));

        // In the world: a one-shot kill, respawn after 0 s, same class.
        var lethal = Config with { Weapons = [Rifle with { Damage = 1e4f }, Pistol, Heavy], RespawnTime = 0f };
        var world = new World(lethal, spawns: [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, 5, 0), 0f)]);
        world.Spawn(1, Tactical); world.Spawn(2, Devastator);
        float pitch = MathF.Atan2(1.0f - lethal.EyeHeight, 5f);
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, InputButtons.Fire, pitch, world.Tick) : default);
        Assert.True(Assert.Single(world.LastHits).Killed);
        world.Step(_ => default);
        Assert.Equal((true, Devastator, 60), (world.Players[2].Alive, world.Players[2].Class, world.Players[2].Clip));
    }

    // ---------------------------------------------------------------- switching

    [Fact]
    public void Switching_swaps_ammo_and_blocks_firing_for_the_switch_time()
    {
        Assert.Equal(18, (int)MathF.Round(SwitchTicks));
        var s = Fresh(Tactical);
        Step(ref s, InputButtons.Fire, out bool fired);   // one rifle shot: 19 left
        Assert.True(fired);
        Step(ref s, InputButtons.Switch, out fired);
        Assert.False(fired);
        Assert.Equal((WeaponSlot.Pistol, 8, 24, 19, 60), (s.Slot, s.Clip, s.Reserve, s.OtherClip, s.OtherReserve));
        Assert.Equal(Config.WeaponSwitchTime, s.SwitchLeft, 5);

        // Ticks 1..17 after the press: fire pressed every other tick, nothing leaves the barrel.
        for (int k = 1; k <= 17; k++)
        {
            Step(ref s, k % 2 == 0 ? InputButtons.Fire : InputButtons.None, out fired);
            Assert.False(fired, $"fired {k} ticks into the switch");
            Assert.True(s.SwitchLeft > 0f);
        }
        // Tick 18 = 0.6 s after the press: the switch is done and the press fires.
        Step(ref s, InputButtons.Fire, out fired);
        Assert.True(fired);
        Assert.Equal((0f, 7), (s.SwitchLeft, s.Clip));

        // Switching back restores the rifle's own ammunition.
        Step(ref s, InputButtons.Switch, out _);
        Assert.Equal((WeaponSlot.Ranged, 19, 60, 7, 24), (s.Slot, s.Clip, s.Reserve, s.OtherClip, s.OtherReserve));
    }

    [Fact]
    public void Switch_needs_a_press_edge_both_weapons_and_no_attack_or_switch_in_progress()
    {
        var s = Fresh(Tactical);
        Step(ref s, InputButtons.Switch, out _);
        Assert.Equal(WeaponSlot.Pistol, s.Slot);
        for (int i = 0; i < 40; i++) Step(ref s, InputButtons.Switch, out _);   // held: no further switch
        Assert.Equal(WeaponSlot.Pistol, s.Slot);

        // A press while a switch is under way cancels it and switches back (sim/switch-cancel, owner's recollection 2026-10-31).
        s = Fresh(Tactical);
        Step(ref s, InputButtons.Switch, out _);
        Step(ref s, InputButtons.None, out _);
        Step(ref s, InputButtons.Switch, out _);
        Assert.Equal((WeaponSlot.Ranged, Config.WeaponSwitchTime), (s.Slot, s.SwitchLeft));

        // No primary: nothing to switch to.
        var a = Fresh(Assault);
        Step(ref a, InputButtons.Switch, out _);
        Assert.Equal((WeaponSlot.Pistol, 0f, 8), (a.Slot, a.SwitchLeft, a.Clip));

        // During a melee attack the press is ignored (not buffered).
        var m = Fresh(Tactical);
        Step(ref m, InputButtons.Melee, out _);
        Assert.Equal(1, m.MeleeStep);
        Step(ref m, InputButtons.None, out _);
        Step(ref m, InputButtons.Switch, out _);
        Assert.Equal(WeaponSlot.Ranged, m.Slot);
        while (m.MeleeStep > 0) Step(ref m, InputButtons.None, out _);
        Assert.Equal(WeaponSlot.Ranged, m.Slot);
    }

    [Fact]
    public void Switching_keeps_a_reload_running_on_the_holstered_weapon_and_it_completes_on_its_timer()
    {
        var s = Fresh(Tactical) with { Clip = 5 };
        Step(ref s, InputButtons.Reload, out _);
        Assert.Equal(Rifle.ReloadTime, s.ReloadLeft, 5);
        for (int i = 0; i < 10; i++) Step(ref s, InputButtons.None, out _);
        Step(ref s, InputButtons.Switch, out _);
        // sim/combat-pass (protocol 45; owner's recollection 2026-10-30): the reload is not lost; the holstered weapon's runs on and completes on its own timer.
        Assert.Equal((WeaponSlot.Pistol, 0f, 5), (s.Slot, s.ReloadLeft, s.OtherClip));
        Assert.InRange(s.OtherReloadLeft, 0.01f, Rifle.ReloadTime);
        for (int i = 0; i < 20; i++) Step(ref s, InputButtons.None, out _);
        Assert.Equal((0f, 20, 45), (s.OtherReloadLeft, s.OtherClip, s.OtherReserve));   // 15 rounds moved (the rifle's data in this test: clip 20, reserve 60)
        Step(ref s, InputButtons.Switch, out _);
        Assert.Equal((WeaponSlot.Ranged, 20, 45, 0f), (s.Slot, s.Clip, s.Reserve, s.ReloadLeft));
        // Reloading is blocked during the switch.
        Step(ref s, InputButtons.Reload, out _);
        Assert.Equal(0f, s.ReloadLeft);
    }

    // ---------------------------------------------------------------- triggers

    [Fact]
    public void Semi_auto_fires_once_per_press_and_full_auto_while_held()
    {
        var p = Fresh(Assault);
        Assert.Equal(1, Shots(ref p, Enumerable.Repeat(InputButtons.Fire, 10)));
        p = Fresh(Assault);
        Assert.Equal(5, Shots(ref p, Enumerable.Range(0, 10).Select(i => i % 2 == 0 ? InputButtons.Fire : InputButtons.None)));

        var r = Fresh(Tactical);   // 0.1 s cooldown at 30 Hz: ticks 0, 3, 6, 9
        Assert.Equal(4, Shots(ref r, Enumerable.Repeat(InputButtons.Fire, 10)));
        Assert.Equal(16, r.Clip);
    }

    // ---------------------------------------------------------------- hits use the weapon in hand

    /// <summary>Shooter (class) at the origin, target (devastator) <paramref name="distance"/> m ahead; one shot at the chest.</summary>
    static HitEvent? ShootAt(SimConfig c, int shooterClass, float distance, bool pistol)
    {
        var world = new World(c, spawns: [new SpawnPoint(Vector3.Zero, 0f), new SpawnPoint(new Vector3(0, distance, 0), 0f)]);
        world.Spawn(1, shooterClass); world.Spawn(2, Devastator);
        float pitch = MathF.Atan2(1.0f - c.EyeHeight, distance);
        if (pistol && world.Players[1].Slot != WeaponSlot.Pistol)
        {
            world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, InputButtons.Switch, pitch, world.Tick) : default);
            for (int i = 0; i < 20; i++) world.Step(_ => default);
        }
        world.Step(id => id == 1 ? new PlayerInput(0, 0, 0, 0, InputButtons.Fire, pitch, world.Tick) : default);
        return world.LastHits.Count == 0 ? null : Assert.Single(world.LastHits);
    }

    [Fact]
    public void A_hit_uses_the_damage_range_and_multipliers_of_the_weapon_in_hand()
    {
        // Shield first: rifle 10 x shield-body 1, pistol 30 x shield-body 0.5.
        var rifle = ShootAt(Config, Tactical, 5f, pistol: false);
        var pistol = ShootAt(Config, Tactical, 5f, pistol: true);
        Assert.Equal((HitZone.Body, 10f, 0f), (rifle!.Value.Zone, rifle.Value.ShieldDamage, rifle.Value.HealthDamage));
        Assert.Equal((HitZone.Body, 15f, 0f), (pistol!.Value.Zone, pistol.Value.ShieldDamage, pistol.Value.HealthDamage));
        Assert.Equal(15f, ShootAt(Config, Assault, 5f, pistol: true)!.Value.ShieldDamage);

        // No shield: health at the body multipliers (rifle x1, pistol x1.5); the heavy bolter its own 15.
        var bare = Config with { ShieldMax = 0f };
        Assert.Equal(10f, ShootAt(bare, Tactical, 5f, pistol: false)!.Value.HealthDamage, 4);
        Assert.Equal(45f, ShootAt(bare, Tactical, 5f, pistol: true)!.Value.HealthDamage, 4);
        Assert.Equal(15f, ShootAt(bare, Devastator, 5f, pistol: false)!.Value.HealthDamage, 4);

        // Range: 30 m is inside the rifle's 100 m and beyond the pistol's 20 m.
        Assert.NotNull(ShootAt(Config, Tactical, 30f, pistol: false));
        Assert.Null(ShootAt(Config, Tactical, 30f, pistol: true));

        // Fall-off of the weapon in hand (x0.5 from 10 m to 20 m: 15 m gives x0.75).
        var falling = bare with { Weapons = [Rifle with { FallOffStart = 10f, FallOffEnd = 20f, FallOffEndMultiplier = 0.5f }, Pistol, Heavy] };
        var far = ShootAt(falling, Tactical, 15f, pistol: false)!.Value;   // distance along the ray to the capsule: about 14.6 m
        Assert.InRange(far.Distance, 14f, 15f);
        Assert.Equal(10f * (1f - 0.5f * (far.Distance - 10f) / 10f), far.HealthDamage, 3);
        Assert.Equal(45f, ShootAt(falling, Tactical, 15f, pistol: true)!.Value.HealthDamage, 3);
    }

    // ---------------------------------------------------------------- melee per class

    [Fact]
    public void A_class_without_melee_ignores_the_melee_button()
    {
        var d = Fresh(Devastator);
        Step(ref d, InputButtons.Melee | InputButtons.Fire, out bool fired);
        Assert.Equal((0, true), (d.MeleeStep, fired));
        for (int i = 0; i < 10; i++) Step(ref d, i % 2 == 0 ? InputButtons.Melee : InputButtons.None, out _);
        Assert.Equal(0, d.MeleeStep);

        var t = Fresh(Tactical);
        Step(ref t, InputButtons.Melee, out _);
        Assert.Equal(1, t.MeleeStep);
        // The assault's combo has one step: a buffered press wraps to step 1 again, never step 2.
        var a = Fresh(Assault);
        var steps = new List<int>();
        for (int i = 0; i < 60; i++) { Step(ref a, i % 4 == 0 ? InputButtons.Melee : InputButtons.None, out _); steps.Add(a.MeleeStep); }
        Assert.DoesNotContain(2, steps);
        Assert.Contains(1, steps);
    }

    // ---------------------------------------------------------------- class choice in the handshake

    [Fact]
    public void The_requested_class_is_honoured_and_an_invalid_or_default_request_gets_the_server_default()
    {
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 3, 0, Config, new ServerOptions { DefaultClass = Devastator });
        var assault = h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Assault });
        var invalid = h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = 3 });
        var unset = h.AddClient(nonce: 102);
        var tactical = h.AddClient(nonce: 103, options: new ClientOptions { RequestedClass = Tactical });
        h.Run(TimeSpan.FromSeconds(1));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        int ClassOf(GameClient c) => h.Server.World.Players[c.PlayerId].Class;
        Assert.Equal((Assault, Devastator, Devastator, Tactical), (ClassOf(assault), ClassOf(invalid), ClassOf(unset), ClassOf(tactical)));
        // Each client predicts from its own class (it learnt it from the full own-player record).
        Assert.Equal((Assault, WeaponSlot.Pistol, 8), (assault.Predicted.Class, assault.Predicted.Slot, assault.Predicted.Clip));
        Assert.Equal((Devastator, 60), (unset.Predicted.Class, unset.Predicted.Clip));

        // An out-of-range server default falls back to class 0; ClassFor is what the handshake uses.
        var odd = new Harness(link, 3, 0, Config, new ServerOptions { DefaultClass = 7 });
        Assert.Equal((0, 1, 0, 0), (odd.Server.ClassFor(Protocol.ServerDefaultClass), odd.Server.ClassFor(1), odd.Server.ClassFor(3), odd.Server.ClassFor(254)));
    }

    [Fact]
    public void Hello_carries_the_requested_class()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, 4, 5, 6, 2);
        Assert.Equal(hello, Protocol.Read(Protocol.Write(hello)));
        Assert.Equal(Protocol.ServerDefaultClass, new Hello(Protocol.Version, 1, 2, 3).RequestedClass);
        Assert.Equal(53, Protocol.Version);
    }

    // ---------------------------------------------------------------- per-recipient snapshots

    sealed class Recorder(IServerTransport inner) : IServerTransport
    {
        public readonly List<(int Connection, Message Message)> Sent = new();
        public void Send(int connection, byte[] packet)
        {
            if (Protocol.Read(packet) is { } m) Sent.Add((connection, m));
            inner.Send(connection, packet);
        }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }

    [Fact]
    public void Snapshots_carry_the_recipients_own_player_in_full_and_the_others_slim()
    {
        Recorder? recorder = null;
        var link = new LinkConditions(TimeSpan.FromMilliseconds(10), TimeSpan.Zero, 0);
        var h = new Harness(link, 5, 0, Config, wrapServer: t => recorder = new Recorder(t));
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Devastator });
        // Client 0 reloads (a reload window an enemy must not see), client 1 idles.
        h.Run(TimeSpan.FromSeconds(1), (i, t) => new PlayerInput(t, 0, 0, 0, i == 0 && t % 2 == 0 ? InputButtons.Fire : InputButtons.None));
        h.Run(TimeSpan.FromMilliseconds(200), (i, t) => new PlayerInput(t, 0, 0, 0, i == 0 ? InputButtons.Reload : InputButtons.None));

        var playerOf = recorder!.Sent.Where(x => x.Message is Welcome).ToDictionary(x => x.Connection, x => ((Welcome)x.Message).PlayerId);
        var snapshots = recorder.Sent.Where(x => x.Message is Snapshot s && s.Players.Length == 2).ToList();
        Assert.True(snapshots.Count > 20);
        bool sawReload = false;
        foreach (var (connection, message) in snapshots)
        {
            var s = (Snapshot)message;
            int me = playerOf[connection];
            Assert.Equal(me, s.Self);
            var mine = Assert.Single(s.Players, p => p.PlayerId == me);
            var other = Assert.Single(s.Players, p => p.PlayerId != me);
            Assert.True(mine.Health > 0f && mine.Clip > 0 && mine.OtherClip > 0);
            Assert.Equal(other, Protocol.Slim(other));   // nothing beyond the slim fields
            Assert.Equal((0f, 0f, 0, 0, 0f, 0f), (other.Health, other.Shield, other.Clip, other.Reserve, other.ReloadLeft, other.Cooldown));
            Assert.Equal(h.Server.World.Players[other.PlayerId].Class, other.Class);
            if (mine.ReloadLeft > 0f) sawReload = true;
        }
        Assert.True(sawReload, "the reloading player never saw its own reload");
        var remote = h.Clients[1].RemotePlayers().Players.Single();
        Assert.Equal((Tactical, 0, 0f), (remote.Class, remote.Clip, remote.ReloadLeft));
    }

    [Fact]
    public void A_sixteen_player_snapshot_stays_within_1200_bytes()
    {
        var players = Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new PlayerState(i, new Vector3(1e4f), new Vector3(100f), 3f, true)
        {
            Alive = true, Health = 1e3f, Shield = 1e3f, Class = Protocol.MaxClasses - 1, Slot = WeaponSlot.Pistol, Clip = 65535, Reserve = 65535, OtherClip = 65535, OtherReserve = 65535,
            Cooldown = 1, ReloadLeft = 1, SinceDamage = 1, RespawnLeft = 1, SwitchLeft = 1, Pitch = 1, MeleeStep = Protocol.MaxMeleeStep, MeleeTime = 1,
            MeleeQueued = true, MeleeHeld = true, FireHeld = true, SwitchHeld = true,
        }).ToArray();
        var bytes = Protocol.Write(new Snapshot(1, 1, 1, players, Self: 7));
        output.WriteLine($"16-player snapshot: {bytes.Length} bytes");
        Assert.True(bytes.Length <= 1200, $"{bytes.Length} bytes");
        Assert.True(bytes.Length <= UdpServerTransport.MaxPacket);
        var back = Assert.IsType<Snapshot>(Protocol.Read(bytes));
        Assert.Equal(players.Select(p => p.PlayerId == 7 ? p : Protocol.Slim(p)), back.Players);

        // Malformed: two full records, an unknown slot.
        var two = Protocol.Write(new Snapshot(1, 1, 1, [players[0], players[0] with { PlayerId = 1 }], Self: 1));
        Assert.Null(Protocol.Read(two));
        var single = Protocol.Write(new Snapshot(1, 1, 1, [players[1]]));
        single[^31] = 2;   // slot byte of the only (slim) record (… flags, class, in hand, melee choice, SLOT, step, time 4, evade 1), before the match, kill, point and hit blocks
        Assert.Null(Protocol.Read(single));
    }

    // ---------------------------------------------------------------- the config hash

    [Fact]
    public void The_config_hash_changes_with_every_weapon_and_class_field()
    {
        ulong h = Config.Hash();
        // By value: copies of the lists hash the same.
        Assert.Equal(h, (Config with { Weapons = Config.Weapons.Select(w => w with { }).ToList(), Classes = Config.Classes.Select(c => c with { Melee = c.Melee.ToList() }).ToList() }).Hash());

        WeaponSpec[] variants =
        [
            Rifle with { Name = "x" }, Rifle with { Slot = WeaponSlot.Pistol }, Rifle with { Trigger = TriggerKind.SemiAuto },
            Rifle with { Damage = 11 }, Rifle with { ShotCooldown = 0.2f }, Rifle with { ClipSize = 21 }, Rifle with { ReserveSize = 61 },
            Rifle with { ReloadTime = 1.1f }, Rifle with { MaxRange = 99 }, Rifle with { FallOffStart = 50 }, Rifle with { FallOffEnd = 99 },
            Rifle with { FallOffEndMultiplier = 0.9f }, Rifle with { HeadMultiplier = 2.1f }, Rifle with { BodyMultiplier = 1.1f },
            Rifle with { ShieldHeadMultiplier = 1.1f }, Rifle with { ShieldBodyMultiplier = 0.9f },
        ];
        var seen = new HashSet<ulong> { h };
        foreach (var v in variants)
            Assert.True(seen.Add((Config with { Weapons = [v, Pistol, Heavy] }).Hash()), $"{v} left the hash unchanged");
        Assert.True(seen.Add((Config with { Weapons = [Rifle, Pistol, Heavy, Heavy] }).Hash()), "weapon count");

        var tactical = Config.Classes[0];
        ClassLoadout[] classVariants =
        [
            tactical with { Name = "y" }, tactical with { Primary = 2 }, tactical with { Pistol = -1 }, tactical with { Melee = [Knife] },
            tactical with { Melee = [Knife, Knife with { Duration = 1.1f }] }, tactical with { Melee = [Knife, Knife with { HitTime = 0.3f }] },
            tactical with { Melee = [Knife, Knife with { ShieldMultiplier = 0.5f }] },
        ];
        foreach (var v in classVariants)
            Assert.True(seen.Add((Config with { Classes = [v, Config.Classes[1], Config.Classes[2]] }).Hash()), $"{v.Name} {v.Primary} {v.Pistol} {v.Melee.Count}: hash unchanged");
        Assert.True(seen.Add((Config with { Classes = Config.Classes.Take(2).ToArray() }).Hash()), "class count");
        Assert.True(seen.Add((Config with { WeaponSwitchTime = 0.5f }).Hash()), "switch time");

        // Without lists, the defaults come from the single-weapon fields, which the hash follows.
        Assert.NotEqual(new SimConfig().Hash(), new SimConfig { Damage = 21 }.Hash());
        Assert.Equal(new SimConfig().Hash(), new SimConfig { Weapons = [new SimConfig().LegacyWeapon], Classes = [new ClassLoadout("default", 0, -1, SimConfig.PlaceholderMeleeSteps)] }.Hash());
    }

    // ---------------------------------------------------------------- prediction

    [Theory]
    [InlineData(51)]
    [InlineData(52)]
    public void Prediction_matches_the_server_while_switching_and_firing_under_150ms_rtt_and_5pct_loss(int seed)
    {
        // No damage (not predicted), small clips so reloads happen, and a quick knife for the tactical client.
        var config = Config with
        {
            Weapons = [Rifle with { Damage = 0f, ClipSize = 12, ReserveSize = 400 }, Pistol with { Damage = 0f, ReserveSize = 400 }, Heavy with { Damage = 0f, ReserveSize = 400 }],
            RunSpeed = 6.5f, StrafeSpeed = 6f, BackSpeed = 4.5f, SprintSpeed = 10f, GroundTimeToMaxSpeed = 0.2f, MaxAcceleration = 100f,
        };
        var link = new LinkConditions(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.05);
        var h = new Harness(link, seed, 0, config, new ServerOptions { PeerTimeout = TimeSpan.FromMinutes(5) });
        h.AddClient(nonce: 100, options: new ClientOptions { RequestedClass = Tactical });
        h.AddClient(nonce: 101, options: new ClientOptions { RequestedClass = Devastator });
        var truth = new SortedDictionary<uint, Dictionary<int, PlayerState>>();
        var predicted = new[] { new Dictionary<uint, PlayerState>(), new Dictionary<uint, PlayerState>() };
        PlayerInput Input(int i, uint t)
        {
            var move = Harness.Scripted(i, t);
            var buttons = move.Buttons & ~InputButtons.Jump;
            uint phase = (t + (uint)i * 11) % 150;
            if (phase is 0 or 1 || phase is 75 or 76) buttons |= InputButtons.Switch;   // held 2 ticks: one switch
            if (phase is (>= 20 and < 40) or (>= 90 and < 110) && phase % 3 == 0) buttons |= InputButtons.Fire;   // tapped
            if (phase is (>= 40 and < 60) or (>= 110 and < 130)) buttons |= InputButtons.Fire;                   // held
            if (phase == 135) buttons |= InputButtons.Reload;
            if (i == 0 && phase is 145 or 146) buttons |= InputButtons.Melee;
            return move with { Buttons = buttons, Pitch = 1.3f };
        }
        h.Run(TimeSpan.FromSeconds(20), Input, () =>
        {
            truth[h.Server.World.Tick] = new Dictionary<int, PlayerState>(h.Server.World.Players);
            for (int i = 0; i < 2; i++)
                if (h.Clients[i].Status == ClientStatus.Connected) predicted[i][h.Clients[i].Tick] = h.Clients[i].Predicted;
        });

        for (int i = 0; i < 2; i++)
        {
            var c = h.Clients[i];
            int id = c.PlayerId, compared = 0;
            var mismatches = new List<string>();
            uint from = truth.Keys.First() + 90;
            foreach (var (tick, pred) in predicted[i])
            {
                if (tick < from || !truth.TryGetValue(tick, out var world) || !world.TryGetValue(id, out var real)) continue;
                compared++;
                if (pred != real) mismatches.Add($"tick {tick}: predicted {pred}; server {real}");
            }
            var mine = truth.Values.Where(w => w.ContainsKey(id)).Select(w => w[id]).ToList();
            var pairs = mine.Zip(mine.Skip(1)).ToList();
            int switches = pairs.Count(p => p.First.Slot != p.Second.Slot);
            int pistolShots = pairs.Count(p => p.First.Slot == WeaponSlot.Pistol && p.Second.Slot == WeaponSlot.Pistol && p.Second.Clip == p.First.Clip - 1);
            int primaryShots = pairs.Count(p => p.First.Slot == WeaponSlot.Ranged && p.Second.Slot == WeaponSlot.Ranged && p.Second.Clip == p.First.Clip - 1);
            int reloads = pairs.Count(p => p.Second.ReloadLeft > 0f && p.First.ReloadLeft == 0f);
            output.WriteLine($"client {i}: compared {compared} ticks, {mismatches.Count} mismatches; {switches} switches, {primaryShots} primary and {pistolShots} pistol shots, {reloads} reloads started");
            foreach (var m in mismatches.Take(3)) output.WriteLine("  " + m);
            Assert.True(compared > 300, $"client {i}: only {compared} ticks compared");
            Assert.True(switches >= 3 && pistolShots > 5 && primaryShots > 10 && reloads > 2, $"client {i}: too little switching and firing");
            Assert.Empty(mismatches);
        }
    }
}
