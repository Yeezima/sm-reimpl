using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Warfront.Squad;
using SmServer;
using SmSim;
using SmSim.Extras.Warfront.Squad;
using Xunit;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.SquadWireTests;

sealed class FakeSquadHost : IWarfrontSquadHost
{
    public int Rate { get; init; } = 30;
    public bool InBattle { get; set; } = true;
    public List<(int Player, ulong Key)> ClientList { get; } = [];
    public IReadOnlyList<(int Player, ulong Key)> Clients => ClientList;
    public ulong Handle(ulong viewerKey, ulong ownerKey) => viewerKey == ownerKey ? ownerKey : ownerKey ^ 0xABCDEF0000000000UL;
    public int PlayerOf(ulong key) => ClientList.FirstOrDefault(c => c.Key == key) is { Key: not 0 } c ? c.Player : -1;
}

sealed class FakeCommandHost : IWarfrontSquadCommandHost
{
    public Dictionary<int, ulong> Keys { get; } = [];
    public bool IsAboard, IsFighting, Jump = true, Graph = true;
    public Vector3 Where;
    public double Now { get; set; }
    public int Orders; public byte? Option; public bool Refuse;
    public ulong? KeyOf(int player) => Keys.TryGetValue(player, out var k) ? k : null;
    public bool Aboard(int player) => IsAboard;
    public bool FightingAlive(int player) => IsFighting;
    public bool HasJumpPack(int player) => Jump;
    public Vector3 PositionOf(int player) => Where;
    public bool OnGraph(Vector3 point) => Graph;
    public bool OrderJumpPound(int player, Vector3 mark) { if (Refuse) return false; Orders++; return true; }
    public void SetSquadClass(ulong key, byte option) => Option = option;
}

/// <summary>Warfront W10: the squad sender (SquadState, roster signature and rotation, events) and the command handler (Rename, JumpPound, SquadClass) over fakes.</summary>
public class SquadSenderTests(ITestOutputHelper output)
{
    static (WarfrontSquadSim Sim, WarfrontSquadSender Sender, FakeSquadHost Host) Battle(int owners = 4, int marines = 9)
    {
        var rosters = new SquadRosters();
        var host = new FakeSquadHost();
        for (int o = 1; o <= owners; o++) { rosters.AddOwner((ulong)o * 1000, marines); host.ClientList.Add((o, (ulong)o * 1000)); }
        var sim = new WarfrontSquadSim(rosters, new FakeArena());
        sim.BeginBattle(host.ClientList.Select((c, i) => (c.Key, c.Player, new Vector3(i * 20f, 0, 0))).ToArray());
        return (sim, new WarfrontSquadSender(sim, new SimConfig(), host), host);
    }

    static List<SquadStateSection> States(IEnumerable<WarfrontSquadOutgoing> o) => o.Where(x => x.Tag == SquadWire.StateTag).Select(x => { Assert.True(SquadWire.TryDecodeState(x.Payload, out var s)); return s; }).ToList();

    [Fact]
    public void Thirty_six_marines_go_out_at_ten_hertz_and_a_client_reads_all_of_them()
    {
        var (sim, sender, host) = Battle();
        var sent = new List<WarfrontSquadOutgoing>();
        for (uint t = 1; t <= 30; t++) sent.AddRange(sender.Step(t));
        var states = States(sent);
        Assert.Equal(10, states.Count);
        Assert.All(states, s => Assert.Equal(36, s.Marines.Count));
        Assert.Equal(10, sent.Count(x => x.Tag == SquadWire.StateTag && x.Player == -1));
        output.WriteLine($"state section {sent.First(x => x.Tag == SquadWire.StateTag).Payload.Length} bytes; whole {sender.WholesSent}, halves {sender.HalvesSent}");
    }

    [Fact]
    public void In_the_hub_nothing_is_in_the_state_and_a_short_room_sends_halves()
    {
        var (sim, sender, host) = Battle();
        host.InBattle = false;
        var hub = new List<WarfrontSquadOutgoing>();
        for (uint t = 1; t <= 30; t++) hub.AddRange(sender.Step(t));
        Assert.Empty(States(hub));
        host.InBattle = true;
        sender.StateRoom = SquadWire.StateHeaderBytes + 20 * SquadWire.MarineBytes;
        var seen = new HashSet<int>();
        for (uint t = 31; t <= 60; t++) foreach (var s in States(sender.Step(t))) foreach (var m in s.Marines) seen.Add(m.Index);
        Assert.Equal(36, seen.Count);
        Assert.True(sender.HalvesSent > 0);
    }

    [Fact]
    public void Rosters_go_out_once_per_owner_per_client_and_again_only_on_change_or_rotation()
    {
        var (sim, sender, host) = Battle(owners: 2, marines: 3);
        var first = sender.Step(3);   // tick 3: the 10 Hz step
        var rosters = first.Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.Equal(2 * 2, rosters.Count);   // two owners to two clients
        Assert.True(SquadWire.TryDecodeRoster(rosters.First(r => r.Player == 1).Payload, out var own));
        Assert.Equal(1000UL, own.Handle);   // own roster: the viewer's key
        Assert.Equal(3, own.Marines.Count);
        Assert.True(SquadWire.TryDecodeRoster(rosters.First(r => r.Player == 2 && r.Payload.Length > 0 && SquadWire.TryDecodeRoster(r.Payload, out var o2) && o2.OwnerPlayer == 1).Payload, out var other));
        Assert.NotEqual(1000UL, other.Handle);   // another's: the pseudonym, never the key
        Assert.Empty(sender.Step(6).Where(x => x.Tag == SquadWire.RosterTag));   // nothing changed
        Assert.True(sim.Rosters.Rename(1000, 0, "Castor"));
        var changed = sender.Step(9).Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.Equal(2, changed.Count);   // one owner, to both clients
        Assert.True(SquadWire.TryDecodeRoster(changed[0].Payload, out var renamed));
        Assert.Equal("Castor", renamed.Marines[0].Name);
        var later = new List<WarfrontSquadOutgoing>();
        for (uint t = 12; t <= 90; t += 3) later.AddRange(sender.Step(t));
        Assert.True(later.Count(x => x.Tag == SquadWire.RosterTag) >= 2, "the rotation sends one owner every 2 s");
    }

    [Fact]
    public void Roster_marks_the_marines_in_the_battle_and_a_joining_client_gets_everything_again()
    {
        var (sim, sender, host) = Battle(owners: 1, marines: 4);
        var sections = sender.Step(3).Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.True(SquadWire.TryDecodeRoster(sections[0].Payload, out var r));
        Assert.All(r.Marines, m => Assert.NotEqual(SquadRosterWire.NotInBattle, m.Index));
        Assert.Empty(sender.Step(6).Where(x => x.Tag == SquadWire.RosterTag));
        sender.ClientJoined(9);
        Assert.NotEmpty(sender.Step(9).Where(x => x.Tag == SquadWire.RosterTag));
    }

    [Fact]
    public void Downed_revived_and_out_are_events_and_a_downed_marine_stays_in_the_state()
    {
        var (sim, sender, host) = Battle(owners: 1, marines: 3);
        sender.Step(3);
        Assert.True(sim.Hit(SquadRules.TargetId(0), 100000f, Vector3.Zero));
        sim.Step(4, []);
        var downed = sender.Step(6);
        Assert.True(SquadWire.TryDecodeEvents(downed.First(x => x.Tag == SquadWire.EventsTag).Payload, out var ev));
        Assert.Equal(SquadEventKind.Downed, ev[0].Kind);
        var st = States(downed)[0];
        Assert.Equal(2, st.Marines.First(m => m.Index == 0).State);
        Assert.True(st.Marines.First(m => m.Index == 0).Aux > 150, "about 20 s on the clock");
        Assert.True(sim.Revive(0, byHuman: 1));
        sender.Step(7); sim.Step(8, []);
        var revived = sender.Step(9);
        Assert.True(SquadWire.TryDecodeEvents(revived.First(x => x.Tag == SquadWire.EventsTag).Payload, out var ev2));
        Assert.Equal(SquadEventKind.Revived, ev2[0].Kind);
        Assert.Equal(1, ev2[0].By);
    }

    [Fact]
    public void The_sender_keeps_36_marines_under_the_budget_with_all_rosters()
    {
        var (sim, sender, host) = Battle();
        var all = new List<WarfrontSquadOutgoing>();
        for (uint t = 3; t <= 9; t += 3) all.AddRange(sender.Step(t));
        int biggest = all.Where(x => x.Player == 1 || x.Player == -1).Max(x => x.Payload.Length);
        Assert.True(biggest < Protocol.MaxExtrasPayloadBytes);
        output.WriteLine($"largest section {biggest} bytes (limit {Protocol.MaxExtrasPayloadBytes}); the state {SquadWire.MaxStateBytes}");
    }

    // ---- commands

    static (WarfrontSquadCommands Cmd, FakeCommandHost Host, SquadRosters Rosters) Commands()
    {
        var rosters = new SquadRosters();
        rosters.AddOwner(10, 3); rosters.AddOwner(20, 3);
        var host = new FakeCommandHost { IsAboard = true, IsFighting = false };
        host.Keys[1] = 10; host.Keys[2] = 20;
        return (new WarfrontSquadCommands(rosters, host), host, rosters);
    }

    [Fact]
    public void Rename_works_aboard_on_the_senders_own_roster_only()
    {
        var (cmd, host, rosters) = Commands();
        Assert.True(cmd.Handle(1, SquadWire.EncodeRename(1, "Brother Castor")!));
        Assert.Equal("Brother Castor", rosters.Marine(10, 1)!.Name);
        Assert.NotEqual("Brother Castor", rosters.Marine(20, 1)!.Name);   // the wire carries no owner: another player's roster cannot be reached
        Assert.True(cmd.Handle(2, SquadWire.EncodeRename(1, "Pollux")!));
        Assert.Equal("Brother Castor", rosters.Marine(10, 1)!.Name);
        Assert.Equal("Pollux", rosters.Marine(20, 1)!.Name);
    }

    [Fact]
    public void Rename_is_refused_from_the_field_for_an_empty_name_an_unknown_slot_and_an_unknown_player()
    {
        var (cmd, host, rosters) = Commands();
        string before = rosters.Marine(10, 0)!.Name;
        host.IsAboard = false;
        Assert.False(cmd.Handle(1, SquadWire.EncodeRename(0, "Castor")!));
        Assert.Equal(before, rosters.Marine(10, 0)!.Name);
        host.IsAboard = true;
        Assert.False(cmd.Handle(1, [(byte)SquadCommandKind.Rename, 0, 0]));   // empty
        Assert.False(cmd.Handle(1, SquadWire.EncodeRename(0, "\u0001\u0002 \u0003")!));   // nothing but control characters and blanks
        Assert.False(cmd.Handle(1, SquadWire.EncodeRename(5, "Castor")!));   // slot 5 of a roster of three
        Assert.False(cmd.Handle(99, SquadWire.EncodeRename(0, "Castor")!));   // no key
        Assert.Equal(before, rosters.Marine(10, 0)!.Name);
    }

    [Fact]
    public void Rename_removes_control_characters_and_trims()
    {
        var (cmd, host, rosters) = Commands();
        Assert.True(cmd.Handle(1, SquadWire.EncodeRename(0, "  Cas\u0007tor\n ")!));
        Assert.Equal("Castor", rosters.Marine(10, 0)!.Name);
        Assert.Equal("Castor", WarfrontSquadCommands.Clean("\tCas\u0000tor "));
    }

    [Fact]
    public void Jump_pound_needs_a_battle_a_pack_a_near_mark_on_the_graph_and_one_a_second()
    {
        var (cmd, host, _) = Commands();
        var mark = SquadWire.EncodeJumpPound(new Vector3(30, 0, 0));
        Assert.False(cmd.Handle(1, mark));   // aboard, not fighting
        host.IsFighting = true;
        host.Jump = false;
        Assert.False(cmd.Handle(1, mark));   // no jump pack
        host.Jump = true;
        Assert.False(cmd.Handle(1, SquadWire.EncodeJumpPound(new Vector3(80, 0, 0))));   // 80 m
        host.Graph = false;
        Assert.False(cmd.Handle(1, mark));
        host.Graph = true;
        Assert.True(cmd.Handle(1, mark)); Assert.Equal(1, host.Orders);
        host.Now = 0.5;
        Assert.False(cmd.Handle(1, mark));   // within a second
        host.Now = 1.2;
        Assert.True(cmd.Handle(1, mark)); Assert.Equal(2, host.Orders);
        host.Refuse = true; host.Now = 5;
        Assert.False(cmd.Handle(1, mark));
    }

    [Fact]
    public void Squad_class_is_stored_and_malformed_commands_do_nothing()
    {
        var (cmd, host, _) = Commands();
        Assert.True(cmd.Handle(1, SquadWire.EncodeSquadClass(2)));
        Assert.Equal((byte)2, host.Option);
        host.Option = null;
        Assert.False(cmd.Handle(1, [(byte)SquadCommandKind.SquadClass, 7]));
        Assert.False(cmd.Handle(1, []));
        Assert.False(cmd.Handle(1, [255, 1, 2, 3]));
        Assert.Null(host.Option);
    }
}
