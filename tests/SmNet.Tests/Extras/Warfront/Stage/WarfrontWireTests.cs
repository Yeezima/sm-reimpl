using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;

namespace SmNet.Tests.Extras.Warfront.Stage;

/// <summary>W5a: the Warfront wire contract (round trips, bounds, fuzz). A malformed payload is dropped, never thrown.</summary>
public class WarfrontWireTests
{
    static WarfrontCampaign Campaign()
    {
        var c = new WarfrontCampaign(77, new ForcedRolls(true, false));
        c.Join(111, "Alpha", out _); c.Join(222, "Beta", out _);
        c.Embark();
        return c;
    }

    [Fact]
    public void Hub_round_trips_and_hides_other_keys()
    {
        var c = Campaign();
        var hub = WarfrontWire.HubFor(c, 222);
        var bytes = WarfrontWire.Encode(hub);
        Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontHubWire back));
        Assert.Equal(bytes, WarfrontWire.Encode(back));
        Assert.Equal(222UL, back.ViewerKey);
        Assert.DoesNotContain(back.Players, p => p.Key == 111UL);
        Assert.Contains(back.Players, p => p.Key == 222UL);
        Assert.Equal(WarfrontWire.Pseudonym(111, 77), back.LeaderKey);
        Assert.Equal(c.AvailableTargets(), back.Targets);
    }

    [Fact]
    public void Hub_carries_travel_target_and_last_results()
    {
        var c = Campaign();
        Assert.True(c.TryDeploy(111, WarfrontTarget.Node(1), out _));
        c.BeginBattle(); c.Resolve(WarfrontOutcome.Win);
        Assert.True(c.TryDeploy(111, WarfrontTarget.Node(0), out _));
        var hub = WarfrontWire.HubFor(c, 111);
        Assert.True(WarfrontWire.TryDecode(WarfrontWire.Encode(hub), out WarfrontHubWire back));
        Assert.Equal(WarfrontPhase.Travel, back.Phase);
        Assert.Equal(WarfrontTarget.Node(0), back.CurrentTarget);
        Assert.Equal(1, back.Last[1]);
        Assert.Equal(new[] { 1, 2, 1 }, back.Fronts);
        Assert.True(back.Advanced[1]);
    }

    [Fact]
    public void Stage_and_down_round_trip()
    {
        var s = new WarfrontStageWire(WarfrontStageState.Preparing, WarfrontTargetKind.Node, 2, "wf_c1", 42, 61, 1, 2, "building the walking graph");
        Assert.True(WarfrontWire.TryDecode(WarfrontWire.Encode(s), out WarfrontStageWire b));
        Assert.Equal(s, b);
        var d = new WarfrontDownWire(WarfrontDownPhase.Spectator, 37.5f);
        Assert.True(WarfrontWire.TryDecode(WarfrontWire.Encode(d), out WarfrontDownWire db));
        Assert.Equal(d, db);
    }

    [Fact]
    public void Commands_round_trip()
    {
        foreach (var c in new[]
        {
            new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 0xFEEDFACECAFEBEEFUL, Name: "Brother Test"),
            new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Fortress(1)),
            new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: WarfrontTarget.Hq),
            new WarfrontClientCommand(WarfrontClientCommandKind.Pick, Slot: 2, Pick: 3),
            new WarfrontClientCommand(WarfrontClientCommandKind.NewRun),
        })
        {
            var bytes = WarfrontWire.Encode(c);
            Assert.InRange(bytes.Length, 1, Protocol.MaxExtrasCommandPayload);
            Assert.True(WarfrontWire.TryDecode(bytes, out WarfrontClientCommand back));
            Assert.Equal(c, back);
        }
    }

    [Fact]
    public void Long_names_are_cut_not_refused()
    {
        var c = new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 5, Name: new string('x', 200));
        Assert.True(WarfrontWire.TryDecode(WarfrontWire.Encode(c), out WarfrontClientCommand back));
        Assert.Equal(WarfrontWire.MaxName, back.Name.Length);
    }

    [Fact]
    public void Bad_commands_are_dropped()
    {
        Assert.False(WarfrontWire.TryDecode(new byte[] { }, out WarfrontClientCommand _));
        Assert.False(WarfrontWire.TryDecode(new byte[] { 9 }, out WarfrontClientCommand _));
        Assert.False(WarfrontWire.TryDecode(new byte[] { 2, 1, 7 }, out WarfrontClientCommand _));   // lane 7
        Assert.False(WarfrontWire.TryDecode(new byte[] { 2, 0, 255 }, out WarfrontClientCommand _));   // a node with no lane
        Assert.False(WarfrontWire.TryDecode(new byte[] { 2, 1, 0, 0 }, out WarfrontClientCommand _));   // trailing byte
        Assert.False(WarfrontWire.TryDecode(new byte[9].Prepend((byte)1).ToArray(), out WarfrontClientCommand _));   // identify with key 0
    }

    [Fact]
    public void Fuzz_never_throws()
    {
        var rng = new Random(5);
        var seeds = new[]
        {
            WarfrontWire.Encode(WarfrontWire.HubFor(Campaign(), 111)),
            WarfrontWire.Encode(new WarfrontStageWire(WarfrontStageState.Go, WarfrontTargetKind.Hq, -1, "wf_hq", 100, 5, 0, 0, "go")),
            WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: 9, Name: "n")),
            WarfrontWire.Encode(new WarfrontDownWire(WarfrontDownPhase.Downed, 3f)),
        };
        for (int i = 0; i < 20000; i++)
        {
            var b = (byte[])seeds[i % seeds.Length].Clone();
            int edits = rng.Next(0, 6);
            for (int e = 0; e < edits && b.Length > 0; e++) b[rng.Next(b.Length)] = (byte)rng.Next(256);
            if (rng.Next(4) == 0) b = b[..rng.Next(b.Length + 1)];
            if (rng.Next(6) == 0) b = new byte[rng.Next(0, 200)].Select(_ => (byte)rng.Next(256)).ToArray();
            WarfrontWire.TryDecode(b, out WarfrontHubWire _);
            WarfrontWire.TryDecode(b, out WarfrontStageWire _);
            WarfrontWire.TryDecode(b, out WarfrontDownWire _);
            WarfrontWire.TryDecode(b, out WarfrontClientCommand _);
        }
    }

    [Fact]
    public void Full_hub_fits_one_section()
    {
        var players = Enumerable.Range(0, WarfrontWire.MaxPlayers).Select(i => new WarfrontHubWirePlayer((ulong)i + 1, new string('n', 60), true)).ToList();
        var hub = new WarfrontHubWire(WarfrontPhase.Ship, new[] { 1, 2, 3 }, new[] { true, true, true }, false, 99, Enumerable.Repeat(WarfrontTarget.Node(0), 20).ToList(), null, 1, 2, players, new byte[] { 1, 2, 3 });
        Assert.InRange(WarfrontWire.Encode(hub).Length, 1, Protocol.MaxExtrasPayloadBytes);
    }
}
