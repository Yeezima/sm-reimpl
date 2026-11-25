using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using SmClient;
using SmNet.Extras.Executions;
using SmSim;
using Xunit.Abstractions;
using static SmNet.Tests.Extras.Executions.Acceptance.ExecutionFixtures;

namespace SmNet.Tests.Extras.Executions.Acceptance;

/// <summary>
/// Executions X8 drop 1, isolation: without <see cref="ContentExtras.Executions"/> nothing changes. The faithful and Musou hashes and 900-tick replays are pinned to values captured at main
/// d4db1299 (before any executions code existed); the replay is the one of <c>MusouIsolationTests</c> (its fixture, read by reflection so this file shares no copy of it). A faithful or Musou
/// server never sends tag 50; the Execute bit does nothing without the flag; an old controls file loads unchanged.
/// </summary>
public class ExecutionIsolationAcceptanceTests(ITestOutputHelper output)
{
    // Captured at main d4db1299 (no executions code in the tree) with the same probe: the pinned fixture of MusouIsolationTests.
    const ulong DefaultFaithfulHash = 0x6F2F6B2B1A7A41F0UL, FixtureFaithfulHash = 0x47117A94D7F3520AUL;
    const string FaithfulReplay = "8A5833FDFB6C629D0E09BB2F4B84F8CECFE4C9E51142F31118C7D1F8CB1D5EBB";
    const ulong DefaultMusouHash = 0x4F982DFE8D7DFB35UL, FixtureMusouHash = 0x9E052C715804C5F2UL;
    const string MusouReplay = "4A58613E868E6664C82942237D50AAA40E3117A367016AD2417FC7849FFAC882";

    static SimConfig IsolationFixture()
    {
        var field = typeof(MusouIsolationTests).GetField("Fixture", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new InvalidOperationException("MusouIsolationTests.Fixture moved");
        return (SimConfig)field.GetValue(null)!;
    }

    static (string Hash, MusouIsolationTests.ReplayStats Stats) Replay(SimConfig c) => MusouIsolationTests.Replay(c with { Cancel = CancelLive.Of(CancelData.Baseline) });

    [Fact]
    public void FaithfulHash_And900TickReplay_Unchanged()
    {
        var fixture = IsolationFixture();
        Assert.Equal(DefaultFaithfulHash, new SimConfig().Hash());
        Assert.Equal(FixtureFaithfulHash, fixture.Hash());
        Assert.Equal(ContentExtras.None, fixture.Extras);
        var (hash, stats) = Replay(fixture);
        output.WriteLine($"faithful replay {hash}: {stats}");
        Assert.True(stats.Shots > 0 && stats.Throws > 0 && stats.MeleeHits > 0 && stats.Rushes > 0 && stats.Kills > 0, stats.ToString());   // the script exercises the sim
        Assert.Equal(FaithfulReplay, hash);
        // rows on a faithful config are never read: the hash and the replay do not move
        var withRows = fixture with { Executions = ClassicSet() };
        Assert.Equal(FixtureFaithfulHash, withRows.Hash());
        Assert.Equal(FaithfulReplay, Replay(withRows).Hash);
    }

    [Fact]
    public void MusouHash_And900TickReplay_Unchanged()
    {
        var fixture = IsolationFixture() with { Extras = ContentExtras.Musou };
        Assert.Equal(DefaultMusouHash, new SimConfig { Extras = ContentExtras.Musou }.Hash());
        Assert.Equal(FixtureMusouHash, fixture.Hash());
        Assert.False(ContentExtras.Musou.HasFlag(ContentExtras.Executions));
        var (hash, stats) = Replay(fixture);
        output.WriteLine($"musou replay {hash}: {stats}");
        Assert.True(stats.Shots > 0 && stats.MeleeHits > 0, stats.ToString());
        Assert.Equal(MusouReplay, hash);
        var withRows = fixture with { Executions = ClassicSet() };
        Assert.Equal(FixtureMusouHash, withRows.Hash());
        Assert.Equal(MusouReplay, Replay(withRows).Hash);
    }

    // ---------------------------------------------------------------- the wire

    sealed class Recording(IServerTransport inner, List<byte[]> sent) : IServerTransport
    {
        public void Send(int connection, byte[] packet) { sent.Add(packet); inner.Send(connection, packet); }
        public bool TryReceive(out int connection, out byte[] packet) => inner.TryReceive(out connection, out packet);
        public void Disconnect(int connection) => inner.Disconnect(connection);
        public void Pin(int connection) => inner.Pin(connection);
    }

    /// <summary>Runs a two-client server of <paramref name="extras"/> for 3 s with both clients pressing Execute with Melee every few ticks, and returns every tag-50 section any packet carried.</summary>
    List<byte[]> Tag50Sections(ContentExtras extras)
    {
        var sent = new List<byte[]>();
        var h = new Harness(LinkConditions.Perfect, 7, 2, Config(extras: extras), wrapServer: inner => new Recording(inner, sent));
        h.Run(TimeSpan.FromSeconds(3), (i, t) => new PlayerInput(t, 0, 0, 0, t % 4 == 0 ? InputButtons.Execute | InputButtons.Melee : InputButtons.None));
        Assert.All(h.Clients, c => Assert.Equal(ClientStatus.Connected, c.Status));
        Assert.True(sent.Count > 100, "the server sent almost nothing; the check would be vacuous");
        Assert.Empty(h.Server.World.ExecutionsLive);
        Assert.Equal(0, h.Server.ExecutionMessagesSent);
        var sections = sent.Select(p => Protocol.Read(p)).OfType<ExtrasUpdate>().SelectMany(e => e.Sections).Where(s => s.Tag == ExecutionWire.Tag).Select(s => s.Payload).ToList();
        output.WriteLine($"{extras}: {sent.Count} packets, {sections.Count} tag-50 sections");
        return sections;
    }

    [Fact]
    public void FaithfulServer_NeverSendsTag50() => Assert.Empty(Tag50Sections(ContentExtras.None));

    [Fact]
    public void MusouServer_NeverSendsTag50() => Assert.Empty(Tag50Sections(ContentExtras.Musou));

    [Fact]
    public void ExecutionsServer_DoesSendTag50_AsTheControl()
    {
        // the same run on a server with the flag: its overshield list and refusals prove the pipe is live (so the two "never" tests above are not vacuous)
        var sent = new List<byte[]>();
        var h = new Harness(LinkConditions.Perfect, 7, 2, Config(extras: ContentProfiles.WarfrontProfile), wrapServer: inner => new Recording(inner, sent));   // X3 repair: the overshield list (sent from the first tick) is Warfront-only, so the control is the Warfront profile
        bool sawRefusal = false;
        h.Run(TimeSpan.FromSeconds(3), (i, t) => new PlayerInput(t, 0, 0, 0, t % 4 == 0 ? InputButtons.Execute | InputButtons.Melee : InputButtons.None), () => sawRefusal |= h.Server.World.ExecutionsRefusedThisTick.Count > 0);
        var sections = sent.Select(p => Protocol.Read(p)).OfType<ExtrasUpdate>().SelectMany(e => e.Sections).Where(s => s.Tag == ExecutionWire.Tag).ToList();
        Assert.NotEmpty(sections);
        Assert.True(sawRefusal, "an Execute press never reached the executions server's world");
    }

    // ---------------------------------------------------------------- the Execute bit

    /// <summary>Two scripted worlds of <paramref name="extras"/>, one fed Execute presses and one not: every player's state is identical every tick and no execution state ever fills.</summary>
    static void AssertExecuteIsInertWithout(ContentExtras extras)
    {
        var plain = Arena(Config(extras: extras), (Tactical, Vector3.Zero), (Chaos, Near), (Chaos, new Vector3(3f, 0f, 0f)));
        var pressed = Arena(Config(extras: extras), (Tactical, Vector3.Zero), (Chaos, Near), (Chaos, new Vector3(3f, 0f, 0f)));
        int kills = 0;
        for (int k = 0; k < 400; k++)
        {
            Step(plain, id => id == 1 && k % 40 == 0 ? InputButtons.Melee : InputButtons.None);
            Step(pressed, id => (id == 1 && k % 40 == 0 ? InputButtons.Melee : InputButtons.None) | (k % 3 == 0 ? InputButtons.Execute : InputButtons.None));
            foreach (int id in plain.Players.Keys) Assert.Equal(plain.Players[id], pressed.Players[id]);
            Assert.Empty(pressed.ExecutionsLive);
            Assert.Empty(pressed.ExecutionsStartedThisTick);
            Assert.Empty(pressed.ExecutionsEndedThisTick);
            Assert.Empty(pressed.ExecutionsRefusedThisTick);
            Assert.Empty(pressed.ExecutionsOfferedThisTick);
            Assert.Empty(pressed.ExecutionsOffersOpen);
            kills += pressed.LastKills.Count;
        }
        Assert.True(kills > 0, "no kill happened in the script (a window never opened, so the check would be vacuous)");
        Assert.Equal(plain.Scores[1], pressed.Scores[1]);
    }

    [Fact]
    public void ExecuteBit_DroppedWithoutFlag()
    {
        AssertExecuteIsInertWithout(ContentExtras.None);
        AssertExecuteIsInertWithout(ContentExtras.Musou);
        // and with only the other extras (Classic Extended minus executions), still inert
        AssertExecuteIsInertWithout(ContentExtras.All & ~ContentExtras.Executions);
    }

    // ---------------------------------------------------------------- an old controls file

    static JsonObject DefaultsJson() => JsonNode.Parse(BindingSet.Defaults().ToJson())!.AsObject();

    [Fact]
    public void OldControlsJson_Loads_QLeftFreeOnly()
    {
        // A file saved before executions: no Execute entry on either device.
        var old = DefaultsJson();
        old["keyboard"]!.AsObject().Remove("Execute");
        old["controller"]!.AsObject().Remove("Execute");
        var loaded = BindingSet.FromJson(old.ToJsonString(), out var notes);
        // Q was free in that file, so Execute takes it; every other action loads exactly as saved
        Assert.Equal("Key:Q", loaded.Get(BindDevice.Keyboard, GameAction.Execute, 0));
        Assert.Null(loaded.Get(BindDevice.Controller, GameAction.Execute, 0));
        var defaults = BindingSet.Defaults();
        foreach (var device in new[] { BindDevice.Keyboard, BindDevice.Controller })
            foreach (var action in Enum.GetValues<GameAction>().Where(a => a != GameAction.Execute))
                for (int slot = 0; slot < BindingSet.Slots(device); slot++)
                    Assert.Equal(defaults.Get(device, action, slot), loaded.Get(device, action, slot));
        Assert.Empty(notes);

        // A file saved before executions that had put Q on another action: Q stays with that action and Execute is left unbound
        var taken = DefaultsJson();
        taken["keyboard"]!.AsObject().Remove("Execute");
        taken["controller"]!.AsObject().Remove("Execute");
        taken["keyboard"]!.AsObject()["Reload"] = new JsonArray("Key:Q", null);
        var reloaded = BindingSet.FromJson(taken.ToJsonString(), out var notes2);
        Assert.Equal("Key:Q", reloaded.Get(BindDevice.Keyboard, GameAction.Reload, 0));
        Assert.Null(reloaded.Get(BindDevice.Keyboard, GameAction.Execute, 0));
        Assert.Null(reloaded.Get(BindDevice.Keyboard, GameAction.Execute, 1));
        Assert.NotEmpty(notes2);   // the player is told why Execute is unbound
        // Q is free again in Classic's own sampling: pressing Q drives Reload and never the Execute bit
        var down = new HashSet<string> { "Key:Q" };
        var sampled = BindingSet.Sample(a => reloaded.Inputs(BindDevice.Keyboard, a).Any(down.Contains), true, includeMove: true);
        Assert.Equal(InputButtons.None, sampled.Buttons & InputButtons.Execute);
    }
}
