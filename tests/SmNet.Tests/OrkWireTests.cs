using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Ork O5 (protocol 28; docs/ork-faction-design.md section 3): the Ork look in the Hello and the Setup, race 3 in the Appearance message, the
/// Ork's rules (a look only, never an emblem), the server putting an Ork on the side it asks for under the balance rule, a class change to or
/// from the Ork changing the race at the respawn, and the victim bar filled from the victim's own body. Synthetic data only.
/// </summary>
public class OrkWireTests
{
    const int SmTac = CustomFixture.SmTac, CsmTac = CustomFixture.CsmTac, Ork = 4;
    static readonly EmblemHash SomeHash = new(0x1111UL, 0x2222UL);

    /// <summary>The custom-class fixture with an Ork Boy (class 4: no side, race Orks, a body of its own with twice the health).</summary>
    static readonly SimConfig Config = CustomFixture.Config with
    {
        Classes =
        [
            .. CustomFixture.Config.Classes,
            new ClassLoadout("ork - boy", CustomFixture.Rifle, CustomFixture.Sidearm, SimConfig.PlaceholderMeleeSteps)
            {
                PrimaryChoices = [CustomFixture.Rifle], PistolChoices = [CustomFixture.Sidearm], Side = Team.None, Role = "ork",
                PerkChoices = [CustomFixture.Homer, CustomFixture.Targeter], DefaultPerk = CustomFixture.Homer, Body = 0, Race = Race.Orks,
            },
        ],
        Bodies = [BodySpec.Of(CustomFixture.Config, "ork - boy (test)") with { HealthMax = 40f }],
        Extras = ContentExtras.Orks,   // added content E2: the Ork class is played only in a profile that has the Orks
    };

    static Hello HelloFor(Harness h, uint nonce, byte playerClass, byte side = 0, AppearanceCode sm = default, byte orkLook = Races.OrkLookAutomatic) =>
        new(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce, RequestedClass: playerClass, RequestedSide: side, SpaceMarineAppearance: sm, OrkLook: orkLook);

    static Harness NewHarness(int seed = 5) => new(LinkConditions.Perfect, seed, clients: 0, Config, CustomFixture.Options);

    // ---------------------------------------------------------------- the wire

    [Fact]
    public void The_hello_and_the_setup_carry_the_ork_look()
    {
        Assert.Equal((177, 139), (Protocol.HelloBytes, Protocol.SetupBytes));   // protocol 37: + the flags byte
        var hello = new Hello(Protocol.Version, 1, 2, 3, RequestedClass: 6, RequestedSide: 2, SpaceMarineAppearance: CustomFixture.Look(3), OrkLook: 3);
        var bytes = Protocol.Write(hello);
        Assert.Equal(Protocol.HelloBytes, bytes.Length);
        Assert.Equal(3, bytes[^2]);   // the Ork look: the last byte but the flags (protocol 37)
        Assert.Equal(0, bytes[^1]);
        var back = Assert.IsType<Hello>(Protocol.Read(bytes));
        Assert.Equal(hello, back);
        Assert.Equal(Races.OrkCode(3), back.AppearanceFor(Race.Orks));
        Assert.Equal(CustomFixture.Look(3), back.AppearanceFor(Race.SpaceMarines));
        Assert.Equal(AppearanceCode.Default, back.AppearanceFor(Race.Chaos));
        Assert.Null(Protocol.Read(bytes.AsSpan(0, bytes.Length - 1)));   // a protocol-27-sized Hello is refused
        Assert.Equal(Races.OrkLookAutomatic, Assert.IsType<Hello>(Protocol.Read(Protocol.Write(new Hello(Protocol.Version, 1, 2, 3)))).OrkLook);
        // Any byte travels; one beyond the looks reads as automatic.
        Assert.Equal(AppearanceCode.Default, Assert.IsType<Hello>(Protocol.Read(Protocol.Write(hello with { OrkLook = 9 }))).AppearanceFor(Race.Orks));

        var setup = new Setup(7, 2, new LoadoutChoice(Class: 6), AppearanceCode.Default, CustomFixture.Look(1), OrkLook: 4);
        var setupBytes = Protocol.Write(setup);
        Assert.Equal(Protocol.SetupBytes, setupBytes.Length);
        Assert.Equal(setup, Assert.IsType<Setup>(Protocol.Read(setupBytes)));
        Assert.Null(Protocol.Read(setupBytes.AsSpan(0, setupBytes.Length - 1)));
        Assert.Null(Protocol.Read([.. setupBytes, 0]));
        Assert.Equal(Races.OrkCode(4), setup.AppearanceFor(Race.Orks));
        Assert.Equal(Races.OrkLookAutomatic, Assert.IsType<Setup>(Protocol.Read(Protocol.Write(new Setup(7, 2, LoadoutChoice.Default, default, default)))).OrkLook);
    }

    [Fact]
    public void An_appearance_of_race_3_round_trips_and_other_races_are_refused()
    {
        var ork = new Appearance(12, 300, Race.Orks, Races.OrkCode(2));
        var bytes = Protocol.Write(ork);
        Assert.Equal(Protocol.AppearanceBytes, bytes.Length);
        Assert.Equal(3, bytes[5]);
        Assert.Equal(ork, Assert.IsType<Appearance>(Protocol.Read(bytes)));
        foreach (byte bad in new byte[] { 0, 4, 128, 255 })
        {
            var copy = (byte[])bytes.Clone();
            copy[5] = bad;
            Assert.Null(Protocol.Read(copy));
        }
        Assert.Throws<ArgumentException>(() => Protocol.Write(ork with { Race = Race.None }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(ork with { Race = (Race)4 }));
    }

    [Fact]
    public void The_ork_rules_keep_only_the_look()
    {
        var busy = CustomFixture.Look(3).WithPredefinedSet(2).WithLens((1, 2, 3)).WithDecals(AppearanceCode.DecalEmblem, 4).WithEmblem(SomeHash);
        Assert.Equal(Races.OrkCode(2), OrkAppearanceRules.Apply(busy));
        Assert.False(OrkAppearanceRules.Apply(busy).UsesEmblem);
        Assert.Equal(AppearanceCode.Default, OrkAppearanceRules.Apply(CustomFixture.Look(3)));                                   // no look named: automatic
        Assert.Equal(AppearanceCode.Default, OrkAppearanceRules.Apply(AppearanceCode.Default.WithPredefinedSet(Races.OrkLooks)));   // beyond the looks
        for (byte look = 0; look < Races.OrkLooks; look++)
        {
            Assert.Equal(look, Races.OrkLookOf(Races.OrkCode(look)));
            Assert.Equal(Races.OrkCode(look), OrkAppearanceRules.Apply(Races.OrkCode(look)));   // a client's own code passes unchanged
        }
        Assert.Equal(Races.OrkLookAutomatic, Races.OrkLookOf(Races.OrkCode(Races.OrkLookAutomatic)));
        var rules = new CatalogAppearanceRules(CustomFixture.Limits, CustomFixture.Limits);
        Assert.Equal(Races.OrkCode(2), rules.Apply(busy, Race.Orks, Ork));
        Assert.Equal(busy.Sanitized(CustomFixture.Limits).WithPiece(ArmourSlot.Head, 255).WithPalette(ArmourSlot.Torso, 255, 255, 255), rules.Apply(busy, Race.SpaceMarines, SmTac));
        Assert.Equal(rules.Apply(busy, Race.Chaos, CsmTac), rules.Apply(busy, Team.Chaos, CsmTac));   // the side form is the side's race
    }

    // ---------------------------------------------------------------- the server

    [Fact]
    public void An_ork_joins_the_side_it_asks_for_under_the_balance_rule_and_keeps_its_class()
    {
        var h = NewHarness();
        var raws = new List<(int Id, uint Session)>();
        int Join(uint nonce, byte cls, Team side = Team.None)
        {
            var raw = h.Net.Connect();
            var joined = CustomFixture.Join(h, raw, HelloFor(h, nonce, cls, (byte)side));
            raws.Add(joined);
            return joined.Id;
        }
        PlayerState P(int id) => h.Server.World.Players[id];
        int m1 = Join(1, SmTac), m2 = Join(2, SmTac);
        Assert.Equal((SmTac, Team.SpaceMarines, CsmTac, Team.Chaos), (P(m1).Class, P(m1).Team, P(m2).Class, P(m2).Team));   // the marines balance as before
        int o1 = Join(3, Ork, Team.Chaos);
        Assert.Equal((Ork, Team.Chaos), (P(o1).Class, P(o1).Team));          // 1 against 1: granted
        int o2 = Join(4, Ork, Team.Chaos);
        Assert.Equal((Ork, Team.SpaceMarines), (P(o2).Class, P(o2).Team));   // asked for the side with more players (2 against 1): the other one, still an Ork
        int o3 = Join(5, Ork, Team.SpaceMarines);
        Assert.Equal((Ork, Team.SpaceMarines), (P(o3).Class, P(o3).Team));   // 2 against 2: granted
        int o4 = Join(6, Ork);
        Assert.Equal((Ork, Team.Chaos), (P(o4).Class, P(o4).Team));          // no request: the smaller side (3 against 2)
        // The race follows the class, not the side.
        Assert.Equal(Race.Orks, Appearances.RaceOf(Config, P(o1)));
        Assert.Equal(Race.Orks, Appearances.RaceOf(Config, P(o3)));
        Assert.Equal(Race.SpaceMarines, Appearances.RaceOf(Config, P(m1)));
        Assert.Equal(Race.Orks, h.Server.AppearanceOf(o3)!.Value.Race);
    }

    [Fact]
    public void An_orks_appearance_is_its_look_reaches_others_as_race_orks_and_never_pulls_an_emblem()
    {
        var h = NewHarness();
        var watcher = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac, SpaceMarineLimits = CustomFixture.Limits, ChaosLimits = CustomFixture.Limits }, nonce: 50);
        var raw = h.Net.Connect();
        // Its Space Marine look names a custom emblem: an Ork never wears it, so the server never asks for it.
        var smLook = CustomFixture.Look(2).WithDecals(AppearanceCode.DecalEmblem, 1, 2).WithEmblem(SomeHash);
        var (id, session) = CustomFixture.Join(h, raw, HelloFor(h, 1, Ork, sm: smLook, orkLook: 2));
        var (version, race, code) = h.Server.AppearanceOf(id)!.Value;
        Assert.NotEqual(0, version);
        Assert.Equal((Race.Orks, Races.OrkCode(2)), (race, code));
        var seenRequests = new List<BlobRequest>();
        Assert.True(CustomFixture.RunUntil(h, () =>
        {
            seenRequests.AddRange(CustomFixture.Drain(raw).OfType<BlobRequest>());
            if (h.Clock.Now.TotalMilliseconds % 400 < 1) raw.Send(Protocol.Write(new InputBatch([], session)));
            return watcher.AppearanceOf(id) is not null;
        }, TimeSpan.FromSeconds(5)));
        Assert.Equal(new RemoteAppearance(version, Race.Orks, Races.OrkCode(2)), watcher.AppearanceOf(id));
        CustomFixture.RunUntil(h, () => { seenRequests.AddRange(CustomFixture.Drain(raw).OfType<BlobRequest>()); return false; }, TimeSpan.FromSeconds(3));
        Assert.Empty(seenRequests);
        Assert.False(h.Server.HasEmblem(SomeHash));
        Assert.Equal(0, h.Server.EmblemUploads);

        // An automatic look is the default look: version 0, nothing to ask for.
        var raw2 = h.Net.Connect();
        var (auto, _) = CustomFixture.Join(h, raw2, HelloFor(h, 2, Ork, sm: smLook));
        Assert.Equal(((ushort)0, Race.Orks, AppearanceCode.Default), h.Server.AppearanceOf(auto)!.Value);
    }

    [Fact]
    public void A_client_re_sanitizes_an_ork_code_from_the_server()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect, 1);
        var client = new GameClient(Config, Harness.Fingerprint, net.Connect(), clock, new ClientOptions { SpaceMarineLimits = CustomFixture.Limits, ChaosLimits = CustomFixture.Limits }, nonce: 3);
        var server = net.Server;
        void Step(int ms = 1)
        {
            for (int i = 0; i < ms; i++) { clock.Advance(TimeSpan.FromMilliseconds(1)); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); }
        }
        Step();
        Hello? hello = null;
        while (server.TryReceive(out _, out var p)) hello ??= Protocol.Read(p) as Hello;
        server.Send(0, Protocol.Write(new Welcome(4, 100, Config.TickRate, hello!.Nonce, hello.SentMs, 0x5E55)));
        Step(5);
        Assert.Equal(ClientStatus.Connected, client.Status);
        // A hostile server sends an Ork code with pieces, a lens and an emblem: the client keeps only the look.
        var hostile = CustomFixture.Look(4).WithPredefinedSet(1).WithLens((5, 5, 5)).WithDecals(AppearanceCode.DecalEmblem, 1, 2).WithEmblem(SomeHash);
        server.Send(0, Protocol.Write(new Appearance(77, 9, Race.Orks, hostile)));
        Step(5);
        Assert.Equal(new RemoteAppearance(9, Race.Orks, Races.OrkCode(1)), client.AppearanceOf(77));
    }

    [Fact]
    public void A_class_change_to_and_from_the_ork_changes_the_race_at_the_respawn()
    {
        var h = NewHarness(seed: 9);
        var look = CustomFixture.Look(6);
        var a = h.AddClient(options: new ClientOptions { RequestedClass = SmTac, SpaceMarineAppearance = look }, nonce: 1);
        var shooter = h.AddClient(options: new ClientOptions { RequestedClass = CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected && shooter.Status == ClientStatus.Connected && a.Scoreboard is not null, TimeSpan.FromSeconds(5)));
        var before = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.Equal((Race.SpaceMarines, look), (before.Race, before.Code));
        Func<int, uint, PlayerInput> shoot = (client, tick) =>
        {
            if (client != 1 || !h.Server.World.Players.TryGetValue(a.PlayerId, out var t)) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
            var d = t.Position - shooter.Predicted.Position;
            return new PlayerInput(tick, 0, 0, MathF.Atan2(-d.X, d.Y), tick % 2 == 0 ? InputButtons.Fire : InputButtons.None);
        };
        bool respawned = false;
        h.Server.Stepped += w => respawned |= w.LastRespawns.Contains(a.PlayerId);

        // To the Ork, look 1.
        a.ChangeSetup(new LoadoutChoice(Class: Ork), look, AppearanceCode.Default, orkLook: 1);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is not null, TimeSpan.FromSeconds(5)));
        var ack = a.LastSetupAck!;
        Assert.Equal((SetupFlags.Pending, Ork), (ack.Flags, ack.Loadout.Class));
        Assert.NotEqual(before.Version, ack.AppearanceVersion);
        Assert.Equal(before, h.Server.AppearanceOf(a.PlayerId)!.Value);   // nothing changes while A lives
        Assert.Equal(SmTac, h.Server.World.Players[a.PlayerId].Class);
        Assert.True(CustomFixture.RunUntil(h, () => respawned, TimeSpan.FromSeconds(15), shoot));
        var ork = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.Equal((ack.AppearanceVersion, Race.Orks, Races.OrkCode(1)), (ork.Version, ork.Race, ork.Code));
        Assert.Equal((Ork, Team.SpaceMarines), (h.Server.World.Players[a.PlayerId].Class, h.Server.World.Players[a.PlayerId].Team));   // the side stays
        Assert.True(CustomFixture.RunUntil(h, () => shooter.AppearanceOf(a.PlayerId)?.Race == Race.Orks, TimeSpan.FromSeconds(5)));
        Assert.Equal(Races.OrkCode(1), shooter.AppearanceOf(a.PlayerId)!.Code);

        // Back to the Tactical: the Space Marine look again, at the next respawn (a new version: the race changed).
        h.Clock.Advance(TimeSpan.FromSeconds(2));   // past the Setup interval
        a.ChangeSetup(new LoadoutChoice(Class: SmTac), look, AppearanceCode.Default, orkLook: 1);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is { } x && x.Loadout.Class == SmTac, TimeSpan.FromSeconds(5)));
        var back = a.LastSetupAck!;
        Assert.NotEqual(ork.Version, back.AppearanceVersion);
        Assert.Equal(Race.Orks, h.Server.AppearanceOf(a.PlayerId)!.Value.Race);
        respawned = false;
        Assert.True(CustomFixture.RunUntil(h, () => respawned, TimeSpan.FromSeconds(15), shoot));
        var marine = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.Equal((back.AppearanceVersion, Race.SpaceMarines, look), (marine.Version, marine.Race, marine.Code));
        Assert.True(CustomFixture.RunUntil(h, () => shooter.AppearanceOf(a.PlayerId)?.Race == Race.SpaceMarines, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_look_beyond_the_ork_looks_is_defaulted_to_automatic()
    {
        var h = NewHarness();
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, HelloFor(h, 1, Ork, orkLook: 3));
        Assert.Equal(Races.OrkCode(3), h.Server.AppearanceOf(id)!.Value.Code);
        CustomFixture.Drain(raw);
        raw.Send(Protocol.Write(new Setup(session, 1, LoadoutChoice.Default, CustomFixture.Look(1), AppearanceCode.Default, OrkLook: 7)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        var ack = CustomFixture.Drain(raw).OfType<SetupAck>().Single();
        Assert.Equal(SetupFlags.Pending | SetupFlags.Defaulted, ack.Flags);
        Assert.Equal(Ork, ack.Loadout.Class);
        Assert.Equal(0, ack.AppearanceVersion);   // automatic: the default look
    }

    // ---------------------------------------------------------------- the client

    [Fact]
    public void The_victim_bar_is_filled_from_the_victims_own_body()
    {
        var h = NewHarness(seed: 13);
        var ork = h.AddClient(options: new ClientOptions { RequestedClass = Ork, RequestedSide = Team.Chaos }, nonce: 1);
        var shooter = h.AddClient(options: new ClientOptions { RequestedClass = SmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => ork.Status == ClientStatus.Connected && shooter.Status == ClientStatus.Connected && shooter.Predicted.Alive
            && h.Server.World.Players.Count == 2, TimeSpan.FromSeconds(5)));
        Assert.Equal(Team.Chaos, h.Server.World.Players[ork.PlayerId].Team);
        HitReport? first = null;
        bool fired = false;
        Func<int, uint, PlayerInput> shootOnce = (client, tick) =>
        {
            if (client != 1 || fired || !h.Server.World.Players.TryGetValue(ork.PlayerId, out var t)) return new PlayerInput(tick, 0, 0, 0, InputButtons.None);
            var d = t.Position - shooter.Predicted.Position;
            fired = tick % 15 == 0;   // aim first, then one shot
            return new PlayerInput(tick, 0, 0, MathF.Atan2(-d.X, d.Y), fired ? InputButtons.Fire : InputButtons.None);
        };
        Assert.True(CustomFixture.RunUntil(h, () => (first ??= shooter.TakeHits()) is not null, TimeSpan.FromSeconds(5), shootOnce));
        Assert.Equal(ork.PlayerId, first!.LastVictim);
        Assert.False(first.LastKilled);
        var victim = h.Server.World.Players[ork.PlayerId];
        Assert.Equal(40f, Config.For(Ork).HealthMax);
        Assert.Equal(victim.Health, first.VictimHealth, 0.5f);   // 20 of 40 (the config's 20 would have shown 10)
        Assert.Equal(20f, first.VictimHealth, 0.5f);
    }

    [Fact]
    public void A_client_with_another_ork_balance_is_refused()
    {
        var h = NewHarness();
        var other = Config with { Bodies = [Config.Bodies[0] with { HealthMax = 85f }] };   // as a server and a client that differ in --ork-data
        Assert.NotEqual(Config.Hash(), other.Hash());
        var refused = h.AddClient(config: other, nonce: 1);
        var joined = h.AddClient(nonce: 2);
        CustomFixture.RunUntil(h, () => refused.Status == ClientStatus.Rejected && joined.Status == ClientStatus.Connected, TimeSpan.FromSeconds(5));
        Assert.Equal(ClientStatus.Rejected, refused.Status);
        Assert.Equal(RejectReason.ConfigMismatch, refused.RejectedBecause);
        Assert.Contains("--ork-data", refused.RejectDetail);   // O5 review: the usual cause, named in the refusal
        Assert.Equal(ClientStatus.Connected, joined.Status);
    }
}
