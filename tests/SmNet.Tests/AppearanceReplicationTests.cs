using System.Numerics;
using SmNet;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>Shared synthetic content for the custom-class integration tests (no game data).</summary>
static class CustomFixture
{
    public const int SmTac = 0, SmAssault = 1, CsmTac = 2, CsmRaptor = 3;
    public const int Rifle = 0, Sidearm = 1, RifleB = 2, PistolBolter = 3, PlasmaPistol = 4;
    public const int Homer = 0, TrueGrit = 1, Charge = 2, Targeter = 3;

    static WeaponSpec Gun(string name, WeaponSlot slot) => new(name, slot, TriggerKind.SemiAuto, 20f, 0.1f, 30, 90, 1f, 250f, 250f, 250f, 1f, 1f, 1f, 1f, 1f);

    static ClassLoadout Tactical(string name, Team side) => new(name, Rifle, Sidearm, SimConfig.PlaceholderMeleeSteps)
    { PrimaryChoices = [Rifle, RifleB], PistolChoices = [Sidearm], Side = side, Role = "tactical", PerkChoices = [Homer, Targeter], DefaultPerk = Homer };
    static ClassLoadout Assault(string name, Team side) => new(name, -1, Sidearm, SimConfig.PlaceholderMeleeSteps)
    { PistolChoices = [Sidearm, PlasmaPistol, PistolBolter], Side = side, Role = "assault", PerkChoices = [Charge, TrueGrit], DefaultPerk = Charge };

    /// <summary>One rifle hit kills (no shield); a quick respawn.</summary>
    public static readonly SimConfig Config = new()
    {
        Weapons = [Gun("test_rifle", WeaponSlot.Ranged), Gun("test_sidearm", WeaponSlot.Pistol), Gun("test_rifle_b", WeaponSlot.Ranged),
                   Gun("sm_mp_pistol_bolter", WeaponSlot.Pistol), Gun("test_plasma_pistol", WeaponSlot.Pistol)],
        Classes = [Tactical("sm - tactical", Team.SpaceMarines), Assault("sm - assault marine", Team.SpaceMarines),
                   Tactical("csm - tactical", Team.Chaos), Assault("csm - raptor", Team.Chaos)],
        Perks =
        [
            new PerkSpec("mp_perk_friendly_spawn_target", "mp_perk_friendly_spawn_target", PerkKind.Class, 1),
            new PerkSpec(Loadouts.TrueGritPerk, Loadouts.TrueGritPerk, PerkKind.Class, 25),
            new PerkSpec("mp_perk_improved_bullrush", "mp_perk_improved_bullrush", PerkKind.Class, 1),
            new PerkSpec("mp_perk_weap_bolter_accuracy", "mp_perk_weap_bolter_accuracy", PerkKind.Weapon, 1),
        ],
        HealthMax = 20f, ShieldMax = 0f, HealthRegenDelay = 1e6f, RespawnTime = 0.5f,
    };

    public static readonly AppearanceLimits Limits = new(4, [10, 10, 10, 10, 10, 10, 10, 10, 10], 60, 20);
    public static readonly ServerOptions Options = new()
    {
        Match = new MatchRules { TimeLimit = 0f, ScoreLimit = 1000 },
        AppearanceRules = new CatalogAppearanceRules(Limits, Limits),
    };
    /// <summary>150 ms round trip with 10 ms jitter and 10 % loss each way (the architecture's acceptance link).</summary>
    public static readonly LinkConditions Lossy = new(TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(10), 0.10);

    public static AppearanceCode Look(byte head) => AppearanceCode.Default.WithPiece(ArmourSlot.Head, head).WithPalette(ArmourSlot.Torso, 1, 2, 3).WithDecals(5, 6);

    /// <summary>Runs the harness until <paramref name="done"/> or the time runs out; returns whether it happened.</summary>
    public static bool RunUntil(Harness h, Func<bool> done, TimeSpan limit, Func<int, uint, PlayerInput>? input = null)
    {
        int steps = (int)limit.TotalMilliseconds;
        for (int i = 0; i < steps; i++) { if (done()) return true; h.Step(input); }
        return done();
    }

    /// <summary>A raw endpoint joined with <paramref name="hello"/> (its cookie filled in); returns its player id and session.</summary>
    public static (int Id, uint Session) Join(Harness h, IClientTransport raw, Hello hello)
    {
        raw.Send(Protocol.Write(hello));
        for (int i = 0; i < 1000; i++)
        {
            h.Step();
            while (raw.TryReceive(out var p))
                switch (Protocol.Read(p))
                {
                    case Challenge c: raw.Send(Protocol.Write(hello with { Cookie = c.Cookie })); break;
                    case Welcome w: return (w.PlayerId, w.Session);
                }
        }
        throw new Xunit.Sdk.XunitException("raw client got no Welcome");
    }

    public static Hello HelloFor(Harness h, uint nonce, byte playerClass = Protocol.ServerDefaultClass, AppearanceCode sm = default, AppearanceCode chaos = default) =>
        new(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce, RequestedClass: playerClass, SpaceMarineAppearance: sm, ChaosAppearance: chaos);

    /// <summary>Everything a raw endpoint received, read.</summary>
    public static List<Message> Drain(IClientTransport raw)
    {
        var list = new List<Message>();
        while (raw.TryReceive(out var p)) if (Protocol.Read(p) is { } m) list.Add(m);
        return list;
    }
}

/// <summary>
/// Custom classes over the network (T3b, protocol 22): the Hello's perks and appearance, appearances reaching other clients with the same
/// version over a 150 ms / 10 % loss link, Setup applied at the respawn and acknowledged, idempotent and rate-limited Setups, budgeted
/// appearance queries, the version counter, the True Grit log, and a hostile server's scoreboard and codes. Synthetic data only.
/// </summary>
public class AppearanceReplicationTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = CustomFixture.Config;

    [Fact]
    public void An_appearance_reaches_another_client_with_the_same_version_over_a_lossy_link()
    {
        var h = new Harness(CustomFixture.Lossy, seed: 7, clients: 0, Config, CustomFixture.Options);
        var lookSm = CustomFixture.Look(3).WithLens((9, 8, 7));
        var lookChaos = CustomFixture.Look(4).WithPiece(ArmourSlot.LeftArm, 200);   // out of range: the rules default it
        var a = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmTac, SpaceMarineAppearance = lookSm, ChaosAppearance = lookChaos }, nonce: 1);
        var b = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac, SpaceMarineLimits = CustomFixture.Limits, ChaosLimits = CustomFixture.Limits }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected && b.Status == ClientStatus.Connected && b.AppearanceOf(a.PlayerId) is not null, TimeSpan.FromSeconds(10)));
        var (version, race, code) = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.NotEqual(0, version);
        Assert.Equal((Race.SpaceMarines, lookSm), (race, code));   // the Space Marine look: the player is a Space Marine
        var seen = b.AppearanceOf(a.PlayerId)!;
        Assert.Equal(new RemoteAppearance(version, Race.SpaceMarines, lookSm), seen);
        Assert.Contains(a.PlayerId, b.TakeAppearanceChanges());
        Assert.Equal(version, b.Scoreboard!.Players.Single(r => r.PlayerId == a.PlayerId).AppearanceVersion);
        // B kept the default look: version 0, nothing to ask for.
        Assert.Equal(0, h.Server.AppearanceOf(b.PlayerId)!.Value.Version);
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromSeconds(2));
        Assert.Null(a.AppearanceOf(b.PlayerId));
        output.WriteLine($"appearance version {version} reached the other client; packets {h.Net.PacketsSent}, dropped {h.Net.PacketsDropped}");
    }

    [Fact]
    public void The_hello_passes_perks_and_the_server_logs_a_pistol_defaulted_by_the_True_Grit_rule()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var log = new List<string>();
        h.Server.Log += log.Add;
        var plain = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmAssault, RequestedPistol = 2 }, nonce: 1);
        var gritty = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmAssault, RequestedPistol = 2, RequestedPerk2 = 1 }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => plain.Status == ClientStatus.Connected && gritty.Status == ClientStatus.Connected, TimeSpan.FromSeconds(2)));
        var p = h.Server.World.Players[plain.PlayerId];
        var g = h.Server.World.Players[gritty.PlayerId];
        Assert.Equal((CustomFixture.Sidearm, CustomFixture.Charge, -1), (p.Pistol, p.Perk1, p.Perk2));
        Assert.Equal((CustomFixture.PistolBolter, CustomFixture.Charge, CustomFixture.TrueGrit), (g.Pistol, g.Perk1, g.Perk2));
        var line = Assert.Single(log);
        Assert.Contains($"player {plain.PlayerId}", line);
        Assert.Contains("True Grit", line);
    }

    [Fact]
    public void A_setup_applies_at_the_respawn_and_its_ack_names_the_coming_version()
    {
        var h = new Harness(CustomFixture.Lossy, seed: 11, clients: 0, Config, CustomFixture.Options);
        var a = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmTac }, nonce: 1);
        var b = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected && b.Status == ClientStatus.Connected && a.Scoreboard is not null, TimeSpan.FromSeconds(5)));
        var look = CustomFixture.Look(7);
        a.ChangeSetup(new LoadoutChoice(Primary: 1, Perk1: 1), look, AppearanceCode.Default);
        Assert.True(a.SetupPending);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is not null, TimeSpan.FromSeconds(5)));
        var ack = a.LastSetupAck!;
        Assert.False(a.SetupPending);
        Assert.Equal(SetupFlags.Pending, ack.Flags);
        Assert.Equal(new LoadoutChoice(CustomFixture.SmTac, 1, -1, -1, -1, 1, -1), ack.Loadout);
        Assert.NotEqual(0, ack.AppearanceVersion);
        // Nothing changes while A lives.
        Assert.Equal(0, h.Server.AppearanceOf(a.PlayerId)!.Value.Version);
        Assert.Equal(CustomFixture.Rifle, h.Server.World.Players[a.PlayerId].Primary);
        // B shoots A; A respawns with the new loadout and look.
        PlayerInput Shoot(int client, uint tick)
        {
            if (client != 1 || !h.Server.World.Players.TryGetValue(a.PlayerId, out var target)) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
            var d = target.Position - b.Predicted.Position;
            return new PlayerInput(tick, 0, 0, MathF.Atan2(-d.X, d.Y), tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
        }
        Assert.True(CustomFixture.RunUntil(h, () => h.Server.AppearanceOf(a.PlayerId)!.Value.Version == ack.AppearanceVersion, TimeSpan.FromSeconds(10), Shoot));
        var after = h.Server.World.Players[a.PlayerId];
        Assert.Equal((CustomFixture.RifleB, CustomFixture.Targeter), (after.Primary, after.Perk1));
        Assert.Equal(look, h.Server.AppearanceOf(a.PlayerId)!.Value.Code);
        Assert.True(CustomFixture.RunUntil(h, () => b.AppearanceOf(a.PlayerId)?.Version == ack.AppearanceVersion, TimeSpan.FromSeconds(5)));
        Assert.Equal(look, b.AppearanceOf(a.PlayerId)!.Code);
    }

    /// <summary>Client <paramref name="shooter"/>'s input: aims at player <paramref name="target"/> (the server's position) and fires every other tick.</summary>
    static Func<int, uint, PlayerInput> ShootAt(Harness h, GameClient shooter, int shooterIndex, Func<int> target) => (client, tick) =>
    {
        if (client != shooterIndex || !h.Server.World.Players.TryGetValue(target(), out var t)) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
        var d = t.Position - shooter.Predicted.Position;
        return new PlayerInput(tick, 0, 0, MathF.Atan2(-d.X, d.Y), tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
    };

    [Fact]
    public void A_pending_look_equal_to_the_shown_one_keeps_the_version_the_emblem_moved_it_to()
    {
        var blob = System.Security.Cryptography.RandomNumberGenerator.GetBytes(EmblemHash.BlobBytes);
        var hash = EmblemHash.Of(blob);
        var look = CustomFixture.Look(2).WithDecals(5, AppearanceCode.DecalEmblem, 2).WithEmblem(hash);
        var h = new Harness(LinkConditions.Perfect, seed: 3, clients: 0, Config, CustomFixture.Options);
        var shooter = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 9);
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: look, chaos: look));
        ushort v1 = h.Server.AppearanceOf(id)!.Value.Version;
        // The same look again in a Setup before the emblem arrived: its pending version is the shown one.
        raw.Send(Protocol.Write(new Setup(session, 1, LoadoutChoice.Default, look, look)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        Assert.Equal(v1, CustomFixture.Drain(raw).OfType<SetupAck>().Single().AppearanceVersion);
        bool respawned = false;
        h.Server.Stepped += w => respawned |= w.LastRespawns.Contains(id);
        int stepped = 0;
        void Step(Func<int, uint, PlayerInput>? input = null)
        {
            h.Step(input);
            if (stepped++ % 500 == 0) raw.Send(Protocol.Write(new InputBatch([], session)));
            foreach (var r in CustomFixture.Drain(raw).OfType<BlobRequest>()) raw.Send(Protocol.Write(BlobTransfer.Chunk(r.Hash, blob, r.Chunk)));
        }
        for (int i = 0; i < 3000 && !h.Server.HasEmblem(hash); i++) Step();
        ushort v2 = h.Server.AppearanceOf(id)!.Value.Version;
        Assert.NotEqual(v1, v2);   // the arrival: a new version (clients cached v1 with the fallback badge)
        var shoot = ShootAt(h, shooter, 0, () => id);
        for (int i = 0; i < 10000 && !respawned; i++) Step(shoot);
        Assert.True(respawned);
        var after = h.Server.AppearanceOf(id)!.Value;
        Assert.Equal(v2, after.Version);   // not back to v1
        Assert.True(after.Code.UsesEmblem);
    }

    [Fact]
    public void A_match_restart_promotes_a_pending_look()
    {
        var rules = new MatchRules { TimeLimit = 0f, ScoreLimit = 1, RestartDelay = 0.3f };
        var h = new Harness(LinkConditions.Perfect, seed: 5, clients: 0, Config, CustomFixture.Options with { Match = rules });
        var victim = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmTac }, nonce: 1);
        var shooter = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => victim.Status == ClientStatus.Connected && shooter.Status == ClientStatus.Connected, TimeSpan.FromSeconds(2)));
        var look = CustomFixture.Look(8);
        shooter.ChangeSetup(LoadoutChoice.Default, AppearanceCode.Default, look);
        Assert.True(CustomFixture.RunUntil(h, () => shooter.LastSetupAck is not null, TimeSpan.FromSeconds(2)));
        ushort pending = shooter.LastSetupAck!.AppearanceVersion;
        Assert.Equal(0, h.Server.AppearanceOf(shooter.PlayerId)!.Value.Version);
        bool ended = false, shooterDied = false;
        h.Server.Stepped += w => { ended |= w.State == MatchState.Ended; shooterDied |= w.LastKills.Any(k => k.Victim == shooter.PlayerId); };
        var shoot = ShootAt(h, shooter, 1, () => victim.PlayerId);
        Assert.True(CustomFixture.RunUntil(h, () => ended && h.Server.World.State == MatchState.Playing, TimeSpan.FromSeconds(10), shoot));
        Assert.False(shooterDied);   // the shooter respawned only by the restart
        var now = h.Server.AppearanceOf(shooter.PlayerId)!.Value;
        Assert.Equal((pending, look), (now.Version, now.Code));
    }

    [Fact]
    public void The_client_reads_each_own_emblem_once_and_keeps_serving_the_shown_one_after_a_setup_change()
    {
        var blobs = Enumerable.Range(0, 2).Select(_ => System.Security.Cryptography.RandomNumberGenerator.GetBytes(EmblemHash.BlobBytes)).ToArray();
        var hashes = blobs.Select(b => EmblemHash.Of(b)).ToArray();
        int reads = 0;
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        AppearanceCode With(EmblemHash x) => AppearanceCode.Default.WithDecals(AppearanceCode.DecalEmblem, 1).WithEmblem(x);
        var client = new GameClient(Config, Harness.Fingerprint, net.Connect(), clock, new ClientOptions
        {
            SpaceMarineAppearance = With(hashes[0]), EmblemSource = x => { reads++; return blobs.FirstOrDefault(b => EmblemHash.Of(b) == x); },
        }, nonce: 3);
        Assert.Equal(1, reads);   // read and verified once, at the start
        var server = net.Server;
        void Step(int ms = 2) { for (int i = 0; i < ms; i++) { clock.Advance(TimeSpan.FromMilliseconds(1)); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); } }
        List<Message> FromClient() { var l = new List<Message>(); while (server.TryReceive(out _, out var p)) if (Protocol.Read(p) is { } m) l.Add(m); return l; }
        Step();
        var hello = Assert.IsType<Hello>(FromClient().First());
        server.Send(0, Protocol.Write(new Welcome(4, 100, Config.TickRate, hello.Nonce, hello.SentMs, 0x5E55)));
        Step();
        int Answers(EmblemHash x)
        {
            FromClient();
            for (int i = 0; i < 3; i++) server.Send(0, Protocol.Write(new BlobRequest(0, x, i)));
            Step();
            return FromClient().OfType<BlobChunk>().Count(c => c.Hash == x);
        }
        Assert.Equal(3, Answers(hashes[0]));
        ushort seq = client.ChangeSetup(LoadoutChoice.Default, With(hashes[1]), AppearanceCode.Default);
        Step();
        Assert.Equal(2, reads);
        Assert.Equal(3, Answers(hashes[1]));
        server.Send(0, Protocol.Write(new SetupAck(seq, LoadoutChoice.Default, SetupFlags.Pending, 9)));
        Step();
        Assert.False(client.SetupPending);
        Assert.Equal(3, Answers(hashes[0]));   // still shown until the respawn: still served
        Assert.Equal(0, Answers(new EmblemHash(5, 6)));
        for (int i = 0; i < 20; i++) Answers(hashes[0]);
        Assert.Equal(2, reads);                // no read or hash per request
    }

    [Fact]
    public void Setups_need_the_session_are_idempotent_per_sequence_and_limited_to_one_a_second()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 5, CustomFixture.SmTac));
        CustomFixture.Drain(raw);
        List<SetupAck> Send(Setup s, int ms = 50)
        {
            raw.Send(Protocol.Write(s));
            CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(ms));
            return CustomFixture.Drain(raw).OfType<SetupAck>().ToList();
        }
        var setup = new Setup(session, 5, new LoadoutChoice(Primary: 1, Perk2: 7), CustomFixture.Look(2), AppearanceCode.Default);
        Assert.Empty(Send(setup with { Session = session ^ 0x55 }));          // no session: ignored
        Assert.Null(h.Server.World.NextLoadout(id));
        var first = Assert.Single(Send(setup));
        Assert.Equal(SetupFlags.Pending | SetupFlags.Defaulted, first.Flags);   // perk choice 7 does not exist
        Assert.Equal(new LoadoutChoice(CustomFixture.SmTac, 1, -1, -1, -1, -1, -1), first.Loadout);
        ushort counter = first.AppearanceVersion;
        var again = Assert.Single(Send(setup));                                // a resend: the same answer, nothing new
        Assert.Equal(first, again);
        Assert.Equal(first.Loadout, h.Server.World.NextLoadout(id));
        Assert.Empty(Send(setup with { Sequence = 6, Loadout = new LoadoutChoice(Primary: 0) }));   // a new one too soon: dropped (the client resends)
        Assert.Empty(Send(setup with { Sequence = 4 }, 1100));                                   // an older one: ignored
        var later = Assert.Single(Send(setup with { Sequence = 6, Loadout = new LoadoutChoice(Primary: 0) }));
        Assert.Equal((ushort)6, later.Sequence);
        Assert.Equal(0, h.Server.World.NextLoadout(id)!.Value.Primary);
        Assert.Equal(counter, later.AppearanceVersion);   // the same pending look keeps its version
    }

    [Fact]
    public void Appearance_queries_need_the_session_and_are_budgeted_per_peer()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var a = h.AddClient(options: new ClientOptions { SpaceMarineAppearance = CustomFixture.Look(1), ChaosAppearance = CustomFixture.Look(1) }, nonce: 1);
        var raw = h.Net.Connect();
        var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 9));
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected, TimeSpan.FromSeconds(1)));
        CustomFixture.Drain(raw);
        int Answers(uint s, int[] ids)
        {
            raw.Send(Protocol.Write(new AppearanceQuery(s, ids)));
            CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
            return CustomFixture.Drain(raw).OfType<Appearance>().Count(x => x.PlayerId == a.PlayerId);
        }
        int[] sixteen = [.. Enumerable.Range(1000, 15), a.PlayerId];
        Assert.Equal(0, Answers(session + 1, sixteen));   // no session
        Assert.Equal(1, Answers(session, sixteen));
        Assert.Equal(1, Answers(session, sixteen));       // 32 ids in the burst
        Assert.Equal(0, Answers(session, sixteen));       // over the budget (32 ids per second)
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromSeconds(1));
        Assert.Equal(1, Answers(session, [a.PlayerId]));
    }

    [Fact]
    public void The_version_counter_skips_zero_on_the_wrap()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        h.Server.AppearanceCounterForTests = ushort.MaxValue - 1;
        var raw = h.Net.Connect();
        var (first, _) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, sm: CustomFixture.Look(1), chaos: CustomFixture.Look(1)));
        var raw2 = h.Net.Connect();
        var (second, _) = CustomFixture.Join(h, raw2, CustomFixture.HelloFor(h, 2, sm: CustomFixture.Look(2), chaos: CustomFixture.Look(2)));
        Assert.Equal((ushort)65535, h.Server.AppearanceOf(first)!.Value.Version);
        Assert.Equal((ushort)1, h.Server.AppearanceOf(second)!.Value.Version);
    }

    [Fact]
    public void An_old_protocol_hello_is_refused_instead_of_ignored()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raw = h.Net.Connect();
        // A protocol-21 Hello: the 49-byte layout this protocol cannot read.
        var old = Protocol.Write(new Hello(21, Harness.Fingerprint, 1, 77)).AsSpan(0, 49).ToArray();
        Assert.Null(Protocol.Read(old));
        raw.Send(old);
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(10));
        var reject = Assert.IsType<Reject>(Assert.Single(CustomFixture.Drain(raw)));
        Assert.Equal((RejectReason.ProtocolMismatch, 77u), (reject.Reason, reject.Nonce));
        Assert.True(Protocol.Write(reject).Length <= old.Length);   // no amplification
        raw.Send(old.AsSpan(0, 20).ToArray());                      // too short to name a nonce: silence
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(10));
        Assert.Empty(CustomFixture.Drain(raw));
    }

    [Fact]
    public void A_hostile_server_cannot_break_the_client_with_bad_ids_or_codes()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        var client = new GameClient(Config, Harness.Fingerprint, net.Connect(), clock, new ClientOptions { ChaosLimits = CustomFixture.Limits, SpaceMarineLimits = CustomFixture.Limits }, nonce: 3);
        var server = net.Server;
        void Step(int ms = 1)
        {
            for (int i = 0; i < ms; i++) { clock.Advance(TimeSpan.FromMilliseconds(1)); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); }
        }
        List<Message> FromClient() { var l = new List<Message>(); while (server.TryReceive(out _, out var p)) if (Protocol.Read(p) is { } m) l.Add(m); return l; }
        Step();
        var hello = Assert.IsType<Hello>(FromClient().First());
        server.Send(0, Protocol.Write(new Welcome(4, 100, Config.TickRate, hello.Nonce, hello.SentMs, 0x5E55)));
        Step(5);
        Assert.Equal(ClientStatus.Connected, client.Status);
        FromClient();
        // Ids 0 and 70000 travel (the scoreboard carries 32-bit ids); the client must not put them in a query.
        server.Send(0, Protocol.Write(new Scoreboard(101, true, 1, 0f, false, [new ScoreEntry(0, Team.Chaos, 0, 0, 0, 5), new ScoreEntry(70000, Team.Chaos, 0, 0, 0, 5),
            new ScoreEntry(9, Team.Chaos, 2, 0, 0, 5)])));
        Step(5);
        var query = Assert.IsType<AppearanceQuery>(Assert.Single(FromClient()));
        Assert.Equal(new[] { 9 }, query.PlayerIds);
        // A code with out-of-range indices is re-sanitized with the client's own limits.
        var hostileCode = AppearanceCode.Default.WithPiece(ArmourSlot.Head, 200).WithPalette(ArmourSlot.Head, 90, 1, 2);
        server.Send(0, Protocol.Write(new Appearance(9, 5, Race.Chaos, hostileCode)));
        Step(5);
        var seen = client.AppearanceOf(9)!;
        Assert.Equal((255, 255, 1), (seen.Code.Piece(ArmourSlot.Head), seen.Code.Palette(ArmourSlot.Head, 0), seen.Code.Palette(ArmourSlot.Head, 1)));
        // A chunk for a blob the client is not pulling is ignored.
        server.Send(0, Protocol.Write(new BlobChunk(new EmblemHash(1, 2), 0, 16, new byte[1024])));
        Step(5);
        Assert.Null(client.Emblem(new EmblemHash(1, 2)));
        // A server that answers an emblem pull with a tampered blob: one retry from nothing, then the client stops asking.
        var blob = System.Security.Cryptography.RandomNumberGenerator.GetBytes(EmblemHash.BlobBytes);
        var hash = EmblemHash.Of(blob);
        server.Send(0, Protocol.Write(new Appearance(9, 6, Race.Chaos, AppearanceCode.Default.WithDecals(AppearanceCode.DecalEmblem, 1, 2).WithEmblem(hash))));
        var bad = (byte[])blob.Clone(); bad[0] ^= 1;
        int requests = 0;
        for (int t = 0; t < 8000; t++)
        {
            Step();
            foreach (var r in FromClient().OfType<BlobRequest>()) { requests++; server.Send(0, Protocol.Write(BlobTransfer.Chunk(hash, bad, r.Chunk))); }
            if (t % 1000 == 0) server.Send(0, Protocol.Write(new Snapshot(200 + (uint)t, 0, Protocol.NoLead, [])));   // keep the connection
        }
        Assert.Null(client.Emblem(hash));
        Assert.Equal(32, requests);   // two pulls of 16 chunks, then the back-off
        Assert.Equal(ClientStatus.Connected, client.Status);
    }
}
