using System.Numerics;
using SmNet.Extras.Warfront;
using SmNet.Tests.Extras.Musou.Campaign;
using SmNet.Tests.Extras.Warfront.Squad;
using SmNet.Tests.Extras.Warfront.Wiring;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront.Retainers;
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
    public bool Librarian = true;
    public bool IsLibrarian(int player) => Librarian;
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

/// <summary>Warfront W10 and Phase B T2: the squad sender (roster signature, rotation and World ids, events; SquadState retired) over real retainers, and the command handler (Rename, JumpPound, SquadClass) over fakes.</summary>
public class SquadSenderTests(ITestOutputHelper output)
{
    /// <summary><paramref name="owners"/> humans (ids 1 up) on a Warfront World, each with a roster of <paramref name="marines"/> landed as World players.</summary>
    static (World W, WarfrontRetainers Retainers, SquadRosters Rosters, WarfrontSquadSender Sender, FakeSquadHost Host) Battle(int owners = 4, int marines = 2)
    {
        var w = TfwRig.War(null, WarfrontSquadWiringTests.Cfg());
        w.Tfw!.Guardsmen = false; w.Tfw.Allies = false;
        for (int id = 2; id <= owners; id++) w.Spawn(id, w.AssignClass(0));
        TfwRig.Steps(w, 5);
        var rosters = new SquadRosters();
        var host = new FakeSquadHost();
        for (int o = 1; o <= owners; o++) { rosters.AddOwner((ulong)o * 1000, marines); host.ClientList.Add((o, (ulong)o * 1000)); }
        var retainers = new WarfrontRetainers(w, rosters);
        w.Tfw.Warfront!.Retainers = retainers;
        retainers.BeginBattle();
        for (int o = 1; o <= owners; o++) Assert.Equal(marines, retainers.Land((ulong)o * 1000, o, w.Players[o].Position));
        return (w, retainers, rosters, new WarfrontSquadSender(retainers, rosters, w.Config, host), host);
    }

    [Fact]
    public void SquadState_is_no_longer_sent_the_retainers_are_World_players()
    {
        var (w, retainers, _, sender, _) = Battle();
        var sent = new List<WarfrontSquadOutgoing>();
        for (uint t = 1; t <= 30; t++) sent.AddRange(sender.Step(t));
        Assert.DoesNotContain(sent, x => x.Tag == SquadWire.StateTag);
        Assert.Equal(8, retainers.All.Count(r => r.WorldId > 0 && w.Players.ContainsKey(r.WorldId)));
    }

    [Fact]
    public void Rosters_go_out_once_per_owner_per_client_and_again_only_on_change_or_rotation()
    {
        var (_, _, rosters, sender, _) = Battle(owners: 2, marines: 2);
        var first = sender.Step(3);   // tick 3: the 10 Hz step
        var sections = first.Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.Equal(2 * 2, sections.Count);   // two owners to two clients
        Assert.True(SquadWire.TryDecodeRoster(sections.First(r => r.Player == 1).Payload, out var own));
        Assert.Equal(1000UL, own.Handle);   // own roster: the viewer's key
        Assert.Equal(2, own.Marines.Count);
        Assert.True(SquadWire.TryDecodeRoster(sections.First(r => r.Player == 2 && SquadWire.TryDecodeRoster(r.Payload, out var o2) && o2.OwnerPlayer == 1).Payload, out var other));
        Assert.NotEqual(1000UL, other.Handle);   // another's: the pseudonym, never the key
        Assert.Empty(sender.Step(6).Where(x => x.Tag == SquadWire.RosterTag));   // nothing changed
        Assert.True(rosters.Rename(1000, 0, "Castor"));
        var changed = sender.Step(18).Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.Equal(2, changed.Count);   // one owner, to both clients
        Assert.True(SquadWire.TryDecodeRoster(changed[0].Payload, out var renamed));
        Assert.Equal("Castor", renamed.Marines[0].Name);
        var later = new List<WarfrontSquadOutgoing>();
        for (uint t = 21; t <= 90; t += 3) later.AddRange(sender.Step(t));
        Assert.True(later.Count(x => x.Tag == SquadWire.RosterTag) >= 2, "the rotation sends one owner every 2 s");
    }

    [Fact]
    public void The_roster_carries_each_Marines_World_id_and_drops_it_once_out_and_a_joining_client_gets_everything_again()
    {
        var (w, retainers, _, sender, host) = Battle(owners: 1, marines: 2);
        var sections = sender.Step(3).Where(x => x.Tag == SquadWire.RosterTag).ToList();
        Assert.True(SquadWire.TryDecodeRoster(sections[0].Payload, out var r));
        Assert.All(r.Marines, m => Assert.NotEqual(SquadRosterWire.NotInBattle, m.Index));
        Assert.Equal(retainers.All.Select(x => (ushort)x.WorldId).Order(), r.Marines.Select(m => m.WorldId).Order());
        Assert.All(r.Marines, m => Assert.True(TfwContracts.IsPuppet(m.WorldId) && w.Players.ContainsKey(m.WorldId)));
        Assert.Empty(sender.Step(6).Where(x => x.Tag == SquadWire.RosterTag));
        sender.ClientJoined(9);
        Assert.NotEmpty(sender.Step(9).Where(x => x.Tag == SquadWire.RosterTag));
        // out: the downed window runs to its end, the World player goes and the roster says so
        var first = retainers.All[0];
        Assert.True(w.TfwKillForTests(first.WorldId));
        for (int k = 0; k < (int)(WarfrontRetainerRules.DownedSeconds * w.Config.TickRate) + 2; k++) retainers.Step();
        Assert.Equal(SquadState.Out, first.State);
        sender.ClientJoined(9);
        Assert.True(SquadWire.TryDecodeRoster(sender.Step(12).First(x => x.Tag == SquadWire.RosterTag && x.Player == 1).Payload, out var after));
        Assert.Equal(SquadRosterWire.NoWorldId, after.Marines[0].WorldId);
        _ = host;
    }

    /// <summary>T5 review F2: a leaver's landed Marines (his key no longer has a player id; or he is back but not yet re-bound) keep their World ids in the roster the others see.</summary>
    [Fact]
    public void A_leavers_landed_Marines_keep_their_World_ids_on_the_wire_until_and_after_he_returns()
    {
        var (w, retainers, _, sender, host) = Battle(owners: 2, marines: 2);
        var ids = retainers.All.Where(x => x.Key == 1000).Select(x => (ushort)x.WorldId).Order().ToList();
        host.ClientList.RemoveAll(c => c.Key == 1000);   // player 1 leaves; his squad fights on
        sender.ClientJoined(2);
        var gone = sender.Step(3).Where(x => x.Tag == SquadWire.RosterTag && x.Player == 2).Select(x => SquadWire.TryDecodeRoster(x.Payload, out var r) ? r : null).First(r => r is not null && r.Marines.Count == 2 && r.Marines.Any(m => ids.Contains(m.WorldId)))!;
        Assert.Equal(ids, gone.Marines.Select(m => m.WorldId).Order());
        Assert.All(gone.Marines, m => Assert.NotEqual(SquadRosterWire.NotInBattle, m.Index));
        // he identifies again as player 5 (the Identify) before the squad is re-bound (the Rebind): still found by key
        host.ClientList.Add((5, 1000));
        sender.ClientJoined(5);
        var back = sender.Step(6).Where(x => x.Tag == SquadWire.RosterTag && x.Player == 5).Select(x => SquadWire.TryDecodeRoster(x.Payload, out var r) ? r : null).First(r => r is not null && r.Handle == 1000)!;
        Assert.Equal(ids, back.Marines.Select(m => m.WorldId).Order());
        Assert.True(retainers.Rebind(1000, 5));
        Assert.Same(retainers.OfKey(1000, 0), retainers.Of(5, 0));
        _ = w;
    }

    [Fact]
    public void Downed_and_revived_are_events_with_the_reviver()
    {
        var (w, retainers, _, sender, _) = Battle(owners: 1, marines: 2);
        sender.Step(3);
        var first = retainers.All[0];
        Assert.True(w.TfwKillForTests(first.WorldId));
        retainers.Step();
        var downed = sender.Step(6);
        Assert.True(SquadWire.TryDecodeEvents(downed.First(x => x.Tag == SquadWire.EventsTag).Payload, out var ev));
        Assert.Equal(SquadEventKind.Downed, ev[0].Kind);
        Assert.True(retainers.View(first.Index)!.Value.DownedLeft > 19f, "about 20 s on the clock");
        Assert.True(retainers.Revive(first, byHuman: 1));
        sender.Step(7); retainers.Step();
        var revived = sender.Step(9);
        Assert.True(SquadWire.TryDecodeEvents(revived.First(x => x.Tag == SquadWire.EventsTag).Payload, out var ev2));
        Assert.Equal(SquadEventKind.Revived, ev2[0].Kind);
        Assert.Equal(1, ev2[0].By);
    }

    [Fact]
    public void The_sender_keeps_every_roster_under_the_budget()
    {
        var (_, _, rosters, sender, _) = Battle();
        for (int o = 1; o <= 4; o++) while (rosters.Count((ulong)o * 1000) < SquadRules.MaxPerPlayer) Assert.True(rosters.Recruit((ulong)o * 1000));
        var all = new List<WarfrontSquadOutgoing>();
        for (uint t = 3; t <= 9; t += 3) all.AddRange(sender.Step(t));
        int biggest = all.Where(x => x.Player == 1 || x.Player == -1).Max(x => x.Payload.Length);
        Assert.True(biggest < Protocol.MaxExtrasPayloadBytes);
        Assert.True(SquadWire.MaxRosterBytes < Protocol.MaxExtrasPayloadBytes);
        output.WriteLine($"largest section {biggest} bytes (limit {Protocol.MaxExtrasPayloadBytes}); a full roster {SquadWire.MaxRosterBytes}");
    }

    [Fact]
    public void The_roster_is_looked_at_twice_a_second_and_a_recruit_forces_it()
    {
        var (_, _, rosters, sender, _) = Battle(owners: 1, marines: 2);
        sender.Step(3);
        Assert.DoesNotContain(sender.Step(6), x => x.Tag == SquadWire.EventsTag);
        // A rename is seen at the next check (15 ticks at 30 Hz), not at every 10 Hz send.
        Assert.True(rosters.Rename(1000, 0, "Cato"));
        Assert.Empty(sender.Step(9).Where(x => x.Tag == SquadWire.RosterTag));
        Assert.NotEmpty(sender.Step(21).Where(x => x.Tag == SquadWire.RosterTag));
        // A recruit forces the next check and is an event.
        Assert.True(rosters.Recruit(1000));
        sender.NoteRecruited(2);
        var recruited = sender.Step(24);
        Assert.NotEmpty(recruited.Where(x => x.Tag == SquadWire.RosterTag));
        Assert.True(SquadWire.TryDecodeEvents(recruited.First(x => x.Tag == SquadWire.EventsTag).Payload, out var ev));
        Assert.Equal(SquadEventKind.Recruited, ev[0].Kind);
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
        host.IsAboard = false;
        Assert.False(cmd.Handle(1, SquadWire.EncodeSquadClass(2)));   // aboard only
        host.IsAboard = true; host.Librarian = false;
        Assert.False(cmd.Handle(1, SquadWire.EncodeSquadClass(2)));   // a Librarian only
        host.Librarian = true;
        Assert.True(cmd.Handle(1, SquadWire.EncodeSquadClass(2)));
        Assert.Equal((byte)2, host.Option);
        host.Option = null;
        Assert.False(cmd.Handle(1, [(byte)SquadCommandKind.SquadClass, 7]));
        Assert.False(cmd.Handle(1, []));
        Assert.False(cmd.Handle(1, [255, 1, 2, 3]));
        Assert.Null(host.Option);
    }
}
