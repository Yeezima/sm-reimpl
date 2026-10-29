using SmNet;
using SmServer;
using SmSim;

namespace SmNet.Tests;

/// <summary>
/// Added content E2 (docs/added-content.md): what a content profile lets a player ask for. A faithful server refuses the Ork class (a Hello's, a Setup's),
/// ignores the Hello's and Setup's Ork look and never draws anyone as race 3; a faithful client drops an Appearance of race 3; the Join's rules
/// (<see cref="ContentGate"/>) keep the Ork class and look out of a faithful request. The same config with the Orks flag is the control. Synthetic data only.
/// </summary>
public class ContentGateTests
{
    const int SmTac = CustomFixture.SmTac, CsmTac = CustomFixture.CsmTac, Ork = 4;

    /// <summary>The custom-class fixture with an Ork Boy (class 4: no side, race Orks) and the given profile.</summary>
    static SimConfig ConfigFor(ContentExtras extras) => CustomFixture.Config with
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
        Extras = extras,
    };

    static Harness NewHarness(ContentExtras extras) => new(LinkConditions.Perfect, 5, clients: 0, ConfigFor(extras), CustomFixture.Options);

    static Hello HelloFor(Harness h, uint nonce, byte playerClass, byte orkLook = Races.OrkLookAutomatic) =>
        new(Protocol.Version, Harness.Fingerprint, h.Config.Hash(), nonce, RequestedClass: playerClass, OrkLook: orkLook);

    // ---------------------------------------------------------------- the rules

    [Fact]
    public void The_gate_offers_the_ork_only_with_the_orks()
    {
        Assert.False(ContentGate.OffersOrks(ContentExtras.None));
        Assert.False(ContentGate.OffersOrks(ContentExtras.Gore));
        Assert.True(ContentGate.OffersOrks(ContentExtras.Orks));
        Assert.Equal([Race.SpaceMarines, Race.Chaos], ContentGate.Factions(ContentExtras.None));
        Assert.Equal([Race.SpaceMarines, Race.Chaos, Race.Orks], ContentGate.Factions(ContentExtras.All));
        Assert.True(ContentGate.OffersRace(ContentExtras.None, Race.Chaos));
        Assert.False(ContentGate.OffersRace(ContentExtras.None, Race.Orks));

        var faithful = ConfigFor(ContentExtras.None);
        Assert.True(ContentGate.ClassOffered(faithful, SmTac));
        Assert.False(ContentGate.ClassOffered(faithful, Ork));      // an Ork class in a config without the flag
        Assert.False(ContentGate.ClassOffered(faithful, 99));       // beyond the config
        Assert.False(ContentGate.ClassOffered(faithful, -1));
        Assert.True(ContentGate.ClassOffered(ConfigFor(ContentExtras.Orks), Ork));
        Assert.True(ContentGate.ClassOffered(faithful, Ork, ContentExtras.Orks));   // the profile given explicitly

        Assert.Equal(Races.OrkLookAutomatic, ContentGate.OrkLook(2, ContentExtras.None));
        Assert.Equal(2, ContentGate.OrkLook(2, ContentExtras.Orks));
    }

    [Fact]
    public void A_join_never_holds_an_ork_class_in_a_profile_without_the_orks()
    {
        var config = ConfigFor(ContentExtras.None);
        var fallback = new LoadoutChoice(Class: SmTac);
        var ork = new LoadoutChoice(Class: Ork, Primary: 0, Pistol: 0, Perk1: 1);
        Assert.Equal(fallback, ContentGate.ForJoin(ork, config, ContentExtras.None, fallback));
        Assert.Equal(fallback, ContentGate.ForJoin(new LoadoutChoice(Class: 6), config, ContentExtras.None, fallback));   // a class the profile does not have
        Assert.Equal(ork, ContentGate.ForJoin(ork, config, ContentExtras.Orks, fallback));
        Assert.Equal(new LoadoutChoice(Class: 6), ContentGate.ForJoin(new LoadoutChoice(Class: 6), config with { Classes = CustomFixture.Config.Classes }, ContentExtras.Orks, fallback));   // the front end's own six classes: the match's config decides
        var tactical = new LoadoutChoice(Class: SmTac, Primary: 0);
        Assert.Equal(tactical, ContentGate.ForJoin(tactical, config, ContentExtras.None, fallback));
        Assert.Equal(LoadoutChoice.Default, ContentGate.ForJoin(LoadoutChoice.Default, config, ContentExtras.None, fallback));   // none asked: the server's default
    }

    // ---------------------------------------------------------------- the server

    [Fact]
    public void A_faithful_server_plays_the_default_class_for_an_ork_hello_and_ignores_its_look()
    {
        var h = NewHarness(ContentExtras.None);
        var (id, _) = CustomFixture.Join(h, h.Net.Connect(), HelloFor(h, 1, Ork, orkLook: 2));
        var player = h.Server.World.Players[id];
        Assert.NotEqual(Ork, player.Class);
        Assert.Equal(Race.SpaceMarines, Appearances.RaceOf(h.Server.World.Config, player));
        var (version, race, code) = h.Server.AppearanceOf(id)!.Value;
        Assert.NotEqual(Race.Orks, race);
        Assert.NotEqual(Races.OrkCode(2), code);
        Assert.Equal((ushort)0, version);   // the default look: the Ork look byte named nothing here

        // The control: the same Hello with the Orks is an Ork with look 2.
        var ho = NewHarness(ContentExtras.Orks);
        var (orkId, _) = CustomFixture.Join(ho, ho.Net.Connect(), HelloFor(ho, 1, Ork, orkLook: 2));
        Assert.Equal(Ork, ho.Server.World.Players[orkId].Class);
        Assert.Equal((Race.Orks, Races.OrkCode(2)), (ho.Server.AppearanceOf(orkId)!.Value.Race, ho.Server.AppearanceOf(orkId)!.Value.Code));
    }

    [Fact]
    public void A_faithful_server_refuses_a_setup_to_the_ork_and_says_it_defaulted()
    {
        var h = NewHarness(ContentExtras.None);
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, HelloFor(h, 1, SmTac));
        CustomFixture.Drain(raw);
        raw.Send(Protocol.Write(new Setup(session, 1, new LoadoutChoice(Class: Ork, Primary: 0, Perk1: 1, Perk2: 0), CustomFixture.Look(1), AppearanceCode.Default, OrkLook: 3)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        var ack = CustomFixture.Drain(raw).OfType<SetupAck>().Single();
        Assert.NotEqual(Ork, ack.Loadout.Class);
        // The other indices were for the refused class's lists: they are reset, so the result is the player's own class with its defaults.
        Assert.Equal(h.Server.World.Players[id].Class, ack.Loadout.Class);
        Assert.Equal(Loadouts.Resolve(h.Server.World.Config, ack.Loadout.Class, LoadoutChoice.Default), ack.Loadout);
        Assert.NotEqual(Ork, h.Server.World.NextLoadout(id)!.Value.Class);
        Assert.True(ack.Flags.HasFlag(SetupFlags.Defaulted));   // the request was not honoured
        Assert.Equal(Race.SpaceMarines, h.Server.AppearanceOf(id)!.Value.Race);
    }

    [Fact]
    public void A_setup_for_class_6_of_a_config_without_the_ork_also_says_it_defaulted()
    {
        var h = new Harness(LinkConditions.Perfect, 5, clients: 0, CustomFixture.Config with { Extras = ContentExtras.None }, CustomFixture.Options);   // no Ork class at all
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, HelloFor(h, 1, SmTac));
        CustomFixture.Drain(raw);
        raw.Send(Protocol.Write(new Setup(session, 1, new LoadoutChoice(Class: 6, Primary: 0, Perk1: 1), AppearanceCode.Default, AppearanceCode.Default)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        var ack = CustomFixture.Drain(raw).OfType<SetupAck>().Single();
        Assert.True(ack.Flags.HasFlag(SetupFlags.Defaulted));
        Assert.Equal(h.Server.World.Players[id].Class, ack.Loadout.Class);
        Assert.Equal(Loadouts.Resolve(h.Server.World.Config, ack.Loadout.Class, LoadoutChoice.Default), ack.Loadout);
    }

    [Fact]
    public void A_refused_class_in_a_hello_takes_none_of_its_item_choices()
    {
        var h = NewHarness(ContentExtras.None);
        var raw = h.Net.Connect();
        var hello = HelloFor(h, 1, Ork) with { RequestedPrimary = 0, RequestedPerk1 = 1, RequestedPerk2 = 0 };
        var (id, _) = CustomFixture.Join(h, raw, hello);
        var player = h.Server.World.Players[id];
        var plain = NewHarness(ContentExtras.None);
        var (plainId, _) = CustomFixture.Join(plain, plain.Net.Connect(), HelloFor(plain, 1, Protocol.ServerDefaultClass));
        var expected = plain.Server.World.Players[plainId];
        Assert.Equal((expected.Class, expected.Perk1, expected.Perk2), (player.Class, player.Perk1, player.Perk2));
    }

    // ---------------------------------------------------------------- the client

    [Fact]
    public void A_faithful_client_drops_an_appearance_of_race_3()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect, 1);
        var config = ConfigFor(ContentExtras.None);
        var client = new GameClient(config, Harness.Fingerprint, net.Connect(), clock, new ClientOptions { SpaceMarineLimits = CustomFixture.Limits, ChaosLimits = CustomFixture.Limits }, nonce: 3);
        var server = net.Server;
        void Step(int ms = 1) { for (int i = 0; i < ms; i++) { clock.Advance(TimeSpan.FromMilliseconds(1)); client.Update(t => new PlayerInput(t, 0, 0, 0, InputButtons.None)); } }
        Step();
        Hello? hello = null;
        while (server.TryReceive(out _, out var p)) hello ??= Protocol.Read(p) as Hello;
        server.Send(0, Protocol.Write(new Welcome(4, 100, config.TickRate, hello!.Nonce, hello.SentMs, 0x5E55)));
        Step(5);
        Assert.Equal(ClientStatus.Connected, client.Status);
        server.Send(0, Protocol.Write(new Appearance(77, 9, Race.Orks, Races.OrkCode(1))));
        server.Send(0, Protocol.Write(new Appearance(78, 9, Race.SpaceMarines, CustomFixture.Look(2))));
        Step(5);
        Assert.Null(client.AppearanceOf(77));                                // race 3 means nothing in this profile
        Assert.Equal(Race.SpaceMarines, client.AppearanceOf(78)?.Race);      // the rest is as before
    }
}
