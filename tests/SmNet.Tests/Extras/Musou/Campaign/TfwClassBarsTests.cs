using System.Numerics;
using SmNet.Extras.Musou;
using SmNet.Extras.Musou.Campaign;
using SmServer;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode pass 2, M7 and M11: a retainer wears its owner's look (item 1), the class change in the Stormbird (item 2) and the elites' health bars (item 14). The sim parts run through the real
/// World and the war's rig; the wire parts through a real server and clients on the loopback.
/// </summary>
public class TfwClassBarsTests(ITestOutputHelper output)
{
    const int Human = TfwRig.Human;

    static readonly WeaponSpec Rifle = new("rifle", WeaponSlot.Ranged, TriggerKind.FullAuto, 9f, 0.12f, 30, 270, 1.2f, 250f, 45f, 70f, 0.8f, 2.5f, 1f, 0.5f, 0.5f);
    static readonly WeaponSpec Pistol = new("pistol", WeaponSlot.Pistol, TriggerKind.SemiAuto, 20f, 0.3f, 8, 64, 1.2f, 120f, 30f, 60f, 0.5f, 3f, 1f, 0.6f, 0.6f);
    static readonly MeleeStep Light = new(1f, 0.2f, 0.4f, 0.6f, 0.5f, 0.7f, 25f, 10f, 3.5f);
    static ClassLoadout Class(string name, Team side, string role) => new(name, -1, 1, [Light]) { PrimaryChoices = [0], PistolChoices = [1], Side = side, Role = role };

    /// <summary>Two Space Marine classes (tactical 0, devastator 1) and a Chaos one (2).</summary>
    static readonly SimConfig Config = new()
    {
        Weapons = [Rifle, Pistol], Extras = ContentExtras.Musou, RespawnTime = 2f,
        Classes = [Class("sm - tactical", Team.SpaceMarines, "tactical"), Class("sm - devastator", Team.SpaceMarines, "heavy"), Class("csm - tactical", Team.Chaos, "tactical")],
    };

    static MatchRules Rules => new() { CountdownTime = 1f, TimeLimit = 0f, ScoreLimit = int.MaxValue / 2 };

    static AppearanceCode Look(byte head) => AppearanceCode.Default.WithPiece(ArmourSlot.Head, head).WithPalette(ArmourSlot.Torso, 1, 2, 3).WithDecals(5, 6);

    static Harness WarServer(out GameClient client, AppearanceCode sm = default)
    {
        var map = TfwTestArena.ServerMap();
        var h = new Harness(LinkConditions.Perfect, 5, 0, Config, new ServerOptions { Match = Rules }, map: map,
            clientOptions: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash), SpaceMarineAppearance = sm });
        client = h.AddClient(options: new ClientOptions { LoadMap = _ => new LoadedMap(map.Collision, map.Hash), SpaceMarineAppearance = sm, RequestedClass = 0 }, nonce: 1);
        return h;
    }

    static bool Settled(Harness h, GameClient c) => c.Status == ClientStatus.Connected && h.Server.World.State == MatchState.Playing && h.Server.World.Players.TryGetValue(c.PlayerId, out var p) && p.Alive;

    // ---------------------------------------------------------------- item 1: a retainer wears its owner's look

    [Fact]
    public void A_retainer_reports_its_owners_look_and_a_client_learns_it()
    {
        var look = Look(3);
        var h = WarServer(out var client, look);
        Assert.True(CustomFixture.RunUntil(h, () => Settled(h, client), TimeSpan.FromSeconds(10)));
        var owner = h.Server.AppearanceOf(client.PlayerId)!.Value;
        Assert.NotEqual(0, owner.Version);
        int retainer = h.Server.World.TfwSpawnRetainer(client.PlayerId, h.Server.World.Players[client.PlayerId].Position) ?? throw new Xunit.Sdk.XunitException("no retainer");
        var lord = h.Server.World.TfwSpawnLord(TfwLane.A, h.Server.World.Players[client.PlayerId].Position) ?? throw new Xunit.Sdk.XunitException("no lord");
        Assert.Equal(owner, h.Server.AppearanceOf(retainer));               // the server's answer: the owner's version, race and code
        Assert.Null(h.Server.AppearanceOf(lord));                           // a lord keeps the default look
        Assert.True(CustomFixture.RunUntil(h, () => client.AppearanceOf(retainer) is not null, TimeSpan.FromSeconds(10)));
        Assert.Equal(new RemoteAppearance(owner.Version, Race.SpaceMarines, look), client.AppearanceOf(retainer));
        Assert.Equal(owner.Version, client.Scoreboard!.Players.Single(r => r.PlayerId == retainer).AppearanceVersion);   // the snapshot path: the scoreboard row names the version
        Assert.Null(client.AppearanceOf(lord));
        output.WriteLine($"retainer {retainer} wears version {owner.Version}");
    }

    [Fact]
    public void A_retainer_of_a_player_with_the_default_look_has_the_default_look()
    {
        var h = WarServer(out var client);
        Assert.True(CustomFixture.RunUntil(h, () => Settled(h, client), TimeSpan.FromSeconds(10)));
        int retainer = h.Server.World.TfwSpawnRetainer(client.PlayerId, h.Server.World.Players[client.PlayerId].Position)!.Value;
        Assert.Null(h.Server.AppearanceOf(retainer));
        h.Run(TimeSpan.FromSeconds(1));
        Assert.Null(client.AppearanceOf(retainer));
    }

    // ---------------------------------------------------------------- item 2: the class change

    [Fact]
    public void A_class_change_in_the_hub_is_a_fresh_life_on_the_spot_with_no_death_and_leaves_smoke()
    {
        var w = TfwRig.War(config: Config);
        var run = w.Tfw!;
        var before = w.Players[Human];
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
        Assert.NotNull(w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 }));
        Assert.Equal(0, before.Class);
        run.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 1);
        var after = w.Players[Human];
        Assert.True(after.Alive);
        Assert.Equal(1, after.Class);                                       // the devastator, at once
        Assert.True(Vector3.Distance(before.Position, after.Position) < 0.01f, $"{before.Position} -> {after.Position}");   // where it stood
        Assert.Null(w.NextLoadout(Human));                                  // the Setup is consumed
        Assert.Equal(0, w.Scores.GetValueOrDefault(Human).Deaths);           // no death
        var smoke = Assert.Single(run.Smoke);
        Assert.Equal((TfwFireKind.Smoke, (ushort)Human), (smoke.Kind, smoke.Subject));
        Assert.True(Vector3.Distance(smoke.At, before.Position) < 0.01f);
        Assert.Equal(TfwRunPhase.Hub, run.Phase);
    }

    [Fact]
    public void A_class_change_reaches_last_respawns_so_the_server_promotes_the_waiting_look()
    {
        var w = TfwRig.War(config: Config);
        w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 });
        w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.ChangeClass, 0));
        bool seen = false;
        for (int k = 0; k < 3; k++) { w.Step(_ => default); seen |= w.LastRespawns.Contains(Human); }
        Assert.True(seen);
    }

    [Fact]
    public void A_class_change_needs_a_waiting_setup_the_hub_a_living_player_and_the_stormbird()
    {
        // No Setup waiting: a free refill and a stripped weapon, so nothing.
        var w = TfwRig.War(config: Config);
        w.Tfw!.Enqueue(Human, new TfwCommandRecord(1, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 2);
        Assert.Empty(w.Tfw.Smoke);

        // Outside the Stormbird (a fresh position in the arena).
        w = TfwRig.War(config: Config);
        Assert.True(w.TfwRespawnAt(Human, TfwTestArena.LandingZone, 0f));
        w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 });
        w.Tfw!.ClearSmoke();
        w.Tfw.Enqueue(Human, new TfwCommandRecord(2, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 2);
        Assert.Empty(w.Tfw.Smoke);
        Assert.Equal(0, w.Players[Human].Class);
        Assert.NotNull(w.NextLoadout(Human));                               // still waiting for the next real life

        // In the travel and in a battle.
        w = TfwRig.War(config: Config);
        w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 });
        TfwRig.Command(w, TfwCommand.Deploy);
        TfwRig.Steps(w, 3);
        Assert.Equal(TfwRunPhase.Travel, w.Tfw!.Phase);
        w.Tfw.Enqueue(Human, new TfwCommandRecord(3, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 2);
        Assert.Empty(w.Tfw.Smoke);
        TfwRig.Seconds(w, TfwBattleRules.TravelSeconds);
        Assert.Equal(TfwRunPhase.Battle, w.Tfw.Phase);
        w.SetNextLoadout(Human, LoadoutChoice.Default with { Class = 1 });
        w.Tfw.Enqueue(Human, new TfwCommandRecord(4, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 2);
        Assert.Empty(w.Tfw.Smoke);
    }

    [Fact]
    public void A_puppet_cannot_change_class()
    {
        var w = TfwRig.War(config: Config);
        int retainer = w.TfwSpawnRetainer(Human, w.Players[Human].Position)!.Value;
        w.SetNextLoadout(retainer, LoadoutChoice.Default with { Class = 1 });
        w.Tfw!.Enqueue(retainer, new TfwCommandRecord(1, TfwCommand.ChangeClass, 0));
        TfwRig.Steps(w, 2);
        Assert.Empty(w.Tfw.Smoke);
    }

    [Fact]
    public void A_class_change_is_ordinary_for_a_pvp_world()
    {
        // The command exists only in the war's queue; a world that is no war has no TfwRun to take it (the isolation tests cover the servers).
        var w = new World(Config with { Extras = ContentExtras.None }, TfwTestArena.Level(), TfwTestArena.Spawns);
        Assert.Null(w.Tfw);
    }

    [Fact]
    public void The_smoke_and_the_new_class_reach_every_client_over_the_wire()
    {
        var h = WarServer(out var a, Look(3));
        var b = h.AddClient(options: new ClientOptions { LoadMap = _ => new LoadedMap(TfwTestArena.ServerMap().Collision, TfwTestArena.ServerMap().Hash), RequestedClass = 0 }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => Settled(h, a) && Settled(h, b), TimeSpan.FromSeconds(10)));
        var fire = new List<TfwFireEvent>();
        void Collect() { foreach (var s in b.TakeExtras()) if (s.Tag == TfwWire.FireTag && TfwWire.ReadFire(s.Payload) is { } records) fire.AddRange(records); }
        int retainer = h.Server.World.TfwSpawnRetainer(a.PlayerId, h.Server.World.Players[a.PlayerId].Position)!.Value;
        Assert.True(CustomFixture.RunUntil(h, () => b.AppearanceOf(retainer) is not null, TimeSpan.FromSeconds(10)));
        var oldVersion = h.Server.AppearanceOf(a.PlayerId)!.Value.Version;

        // A sends its Setup (a devastator in another look), waits for the acknowledgement, then the command.
        ushort sequence = a.ChangeSetup(LoadoutChoice.Default with { Class = 1 }, Look(4), default);
        Assert.True(CustomFixture.RunUntil(h, () => a.LastSetupAck is { } ack && ack.Sequence == sequence && !a.SetupPending, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, h.Server.World.Players[a.PlayerId].Class);          // the change waits for a life
        Assert.True(a.SendExtrasCommand(TfwWire.CommandTag, TfwWire.WriteCommand(new TfwCommandRecord(1, TfwCommand.ChangeClass, 0))));
        h.Run(TimeSpan.FromSeconds(1), after: Collect);

        var player = h.Server.World.Players[a.PlayerId];
        Assert.Equal((true, 1), (player.Alive, player.Class));
        var smoke = Assert.Single(fire.DistinctBy(f => (f.Kind, f.Tick, f.Subject)));
        Assert.Equal((TfwFireKind.Smoke, (ushort)a.PlayerId), (smoke.Kind, smoke.Subject));
        Assert.True(Vector3.Distance(smoke.At, player.Position) < 0.2f, $"{smoke.At} {player.Position}");
        var now = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.NotEqual(oldVersion, now.Version);                           // the waiting look was promoted at once, no death needed
        Assert.Equal(Look(4), now.Code);
        Assert.Equal(now, h.Server.AppearanceOf(retainer));                 // and the retainer follows it
        Assert.True(CustomFixture.RunUntil(h, () => b.AppearanceOf(retainer)?.Version == now.Version, TimeSpan.FromSeconds(10)));
        output.WriteLine($"smoke records seen {fire.Count} (one cloud); look {oldVersion} -> {now.Version}");
    }

    // ---------------------------------------------------------------- item 14: the elites' bars

    [Theory]
    [InlineData(MusouEnemyType.Boy, false)]
    [InlineData(MusouEnemyType.ShootaBoy, false)]
    [InlineData(MusouEnemyType.Gretchin, false)]
    [InlineData(MusouEnemyType.ArdBoy, true)]
    [InlineData(MusouEnemyType.Nob, true)]
    [InlineData(MusouEnemyType.Warboss, true)]
    [InlineData(MusouEnemyType.ChaosMarine, false)]
    public void Only_elites_wear_a_bar(MusouEnemyType type, bool elite) => Assert.Equal(elite, TfwBars.IsElite(type));

    [Theory]
    [InlineData(100f, 100f, 255)]
    [InlineData(50f, 100f, 128)]
    [InlineData(0.01f, 100f, 1)]       // alive never shows empty
    [InlineData(0f, 100f, 0)]
    [InlineData(150f, 100f, 255)]
    [InlineData(10f, 0f, 0)]
    public void Health_becomes_a_byte_rounded_up(float health, float max, int expected) => Assert.Equal((byte)expected, TfwBars.Quantise(health, max));

    [Fact]
    public void Up_to_120_elites_a_client_is_sent_the_64_nearest_in_pool_order_and_the_section_fits()
    {
        var elites = Enumerable.Range(1, 120).Select(i => new TfwElite((ushort)i, new Vector3(i * 2f, 0f, 0f), 255)).ToList();
        var into = new List<TfwBar>();
        TfwBars.Select(elites, new Vector3(0f, 0f, 0f), into);
        Assert.Equal(TfwBars.PerClient, into.Count);
        Assert.Equal(Enumerable.Range(1, 64).Select(i => (ushort)i), into.Select(b => b.Enemy));
        TfwBars.Select(elites, new Vector3(240f, 0f, 0f), into);             // from the far end: the far ones
        Assert.Equal(Enumerable.Range(57, 64).Select(i => (ushort)i), into.Select(b => b.Enemy));
        var payload = TfwWire.WriteBars(into);
        Assert.Equal(1 + 64 * TfwWire.BarBytes, payload.Length);
        Assert.True(payload.Length <= Protocol.MaxExtrasPayloadBytes);
        Assert.Equal(into, TfwWire.ReadBars(payload));
        TfwBars.Select(elites.Take(10).ToList(), null, into);                // few: all of them, with or without a viewer
        Assert.Equal(10, into.Count);
        TfwBars.Select(elites, null, into);                                   // nobody alive: the first ones, still bounded
        Assert.Equal(64, into.Count);
        // The worst a client costs: 10 sections a second of 64 records.
        double bytesPerSecond = TfwBars.PerSecond * (payload.Length + Protocol.ExtrasHeaderBytes + Protocol.ExtrasSectionHeaderBytes);
        Assert.True(bytesPerSecond <= 2200, $"{bytesPerSecond} B/s");
    }

    [Fact]
    public void A_list_is_due_when_it_changed_after_the_period_or_after_the_keep_alive_second()
    {
        Assert.False(TfwBars.Due(true, 10, 9, 30));                           // a tick after the last: too soon (3-tick period)
        Assert.True(TfwBars.Due(true, 12, 9, 30));
        Assert.False(TfwBars.Due(false, 12, 9, 30));
        Assert.True(TfwBars.Due(false, 39, 9, 30));                           // a second without a change
        Assert.True(TfwBars.Due(false, 5, 100, 30));                          // the clock went back: a new match
    }

    [Fact]
    public void Real_elites_in_a_war_get_their_bars_and_the_fodder_none_and_the_list_clears_when_they_fall()
    {
        var h = WarServer(out var client);
        Assert.True(CustomFixture.RunUntil(h, () => Settled(h, client), TimeSpan.FromSeconds(10)));
        var sim = h.Server.World.Musou!;
        var at = h.Server.World.Players[client.PlayerId].Position + new Vector3(0f, 6f, 0f);
        ushort ard = sim.Spawn(MusouEnemyType.ArdBoy, at, 0f, sim.Rules.Of(MusouEnemyType.ArdBoy).Health);
        ushort nob = sim.Spawn(MusouEnemyType.Nob, at + new Vector3(2f, 0f, 0f), 0f, sim.Rules.Of(MusouEnemyType.Nob).Health);
        ushort boy = sim.Spawn(MusouEnemyType.Boy, at + new Vector3(-2f, 0f, 0f), 0f, sim.Rules.Of(MusouEnemyType.Boy).Health);
        Assert.NotEqual(0, ard * nob * boy);
        TfwBar[] latest = [];
        int sections = 0;
        void Collect() { foreach (var s in client.TakeExtras()) if (s.Tag == TfwWire.BarsTag && TfwWire.ReadBars(s.Payload) is { } bars) { latest = bars; sections++; } }
        h.Run(TimeSpan.FromSeconds(1), after: Collect);
        Assert.Equal(new ushort[] { ard, nob }.Order(), latest.Select(b => b.Enemy).Order());
        Assert.All(latest, b => Assert.Equal((byte)255, b.Health));
        // A hit lowers it; a full second without change is a keep-alive at most.
        sim.Damage(ard, client.PlayerId, sim.Rules.Of(MusouEnemyType.ArdBoy).Health / 2f, Vector3.Zero, out _);
        h.Run(TimeSpan.FromSeconds(1), after: Collect);
        Assert.InRange(latest.Single(b => b.Enemy == ard).Health, 126, 129);
        Assert.Equal((byte)255, latest.Single(b => b.Enemy == nob).Health);
        // Both fall: an empty list tells the client to clear.
        sim.Damage(ard, client.PlayerId, 1e6f, Vector3.Zero, out _);
        sim.Damage(nob, client.PlayerId, 1e6f, Vector3.Zero, out _);
        h.Run(TimeSpan.FromSeconds(1), after: Collect);
        Assert.Empty(latest);
        int before = sections;
        h.Run(TimeSpan.FromSeconds(3), after: Collect);
        Assert.True(sections - before <= 1, $"{sections - before} empty sections in 3 s");   // an empty list is not kept alive
        output.WriteLine($"{sections} bar sections, largest payload {h.Server.TfwBarLargest} B");
    }

    [Fact]
    public void A_swarm_of_120_elites_costs_a_client_no_more_than_the_budget()
    {
        var h = WarServer(out var client);
        Assert.True(CustomFixture.RunUntil(h, () => Settled(h, client), TimeSpan.FromSeconds(10)));
        var sim = h.Server.World.Musou!;
        var origin = h.Server.World.Players[client.PlayerId].Position;
        for (int i = 0; i < 120; i++)
            Assert.NotEqual(0, sim.Spawn(i % 4 == 0 ? MusouEnemyType.Nob : MusouEnemyType.ArdBoy, origin + new Vector3(4f + i % 12 * 2f, 4f + i / 12 * 2f, 0f), 0f, 500f));
        long bytes = 0;
        int largest = 0;
        TfwBar[] latest = [];
        h.Run(TimeSpan.FromSeconds(3), after: () =>
        {
            foreach (var s in client.TakeExtras())
                if (s.Tag == TfwWire.BarsTag) { bytes += s.Payload.Length + Protocol.ExtrasHeaderBytes + Protocol.ExtrasSectionHeaderBytes; largest = Math.Max(largest, s.Payload.Length); latest = TfwWire.ReadBars(s.Payload)!; }
        });
        Assert.Equal(TfwBars.PerClient, latest.Length);
        Assert.Equal(1 + TfwBars.PerClient * TfwWire.BarBytes, largest);
        Assert.True(bytes / 3.0 <= 2200, $"{bytes / 3.0:F0} B/s");
        output.WriteLine($"120 elites: {bytes / 3.0:F0} B/s to one client, {latest.Length} bars each");
    }
}
