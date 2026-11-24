using System.Numerics;
using SmClient.Extras.Warfront.Squad;
using SmNet.Extras.Warfront;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.SquadWireTests;

/// <summary>
/// Warfront W10 and Phase B T5: the client's squad model (engine-free). The retainers are World players: the roster names each one's World id, the model reads that player's place and life from the frame's
/// players (a table here), and the HUD line, the panel, the revive candidate and the countdown come from there. Roster replacement, feed lines, the look code.
/// </summary>
public class WarfrontSquadModelTests
{
    static SquadRosterWire R(int slot, int index, string name, byte cls = 1, bool own = true, ushort world = 0) =>
        new((byte)slot, (byte)index, name, cls, 10, 11, 12, own, new ushort[] { 5, 1, 0, 2, 3, 1, 4, 2 }, world);

    /// <summary>Owner 3's squad (World ids 21, 22, 23) and owner 4's Titus (World id 30); the frame's players in <paramref name="bodies"/>.</summary>
    static WarfrontSquadModel Model(Dictionary<int, WarfrontWorldBody> bodies)
    {
        var m = new WarfrontSquadModel { OwnPlayer = 3, World = id => bodies.TryGetValue(id, out var b) ? b : null };
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Castor", world: 21), R(1, 1, "Pollux", world: 22), R(2, 2, "Brother Remus", world: 23)]));
        m.ApplyRoster(new SquadRosterSection(200, 4, false, [R(0, 9, "Titus", 0, own: false, world: 30)]));
        return m;
    }

    static WarfrontWorldBody Up(float x = 0, float y = 0) => new(new Vector3(x, y, 0), 0.5f, true);
    static WarfrontWorldBody Fallen(float x = 0, float y = 0) => new(new Vector3(x, y, 0), 0.5f, false);

    [Fact]
    public void A_marine_stands_where_its_world_player_stands()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody> { [21] = Up(4, 5), [30] = Up(9, 9) };
        var m = Model(bodies);
        Assert.True(m.TrySample(0, 1.0, out var s));
        Assert.Equal(new Vector3(4, 5, 0), s.Position); Assert.Equal(0.5f, s.Yaw); Assert.Equal(21, s.WorldId); Assert.False(s.Downed);
        Assert.True(s.Own); Assert.Equal("Castor", s.Name);
        Assert.False(m.TrySample(1, 1.0, out _));   // not in the snapshot yet
        Assert.True(m.TrySample(9, 1.0, out var other));
        Assert.False(other.Own); Assert.Equal(0, other.Class); Assert.Equal(4, other.OwnerPlayer);
    }

    [Fact]
    public void World_ids_map_to_battle_indices_and_owners_and_follow_a_new_roster()
    {
        var m = Model([]);
        Assert.True(m.IsRetainer(22)); Assert.Equal(1, m.IndexOfWorld(22)); Assert.Equal(22, m.WorldIdOf(1)); Assert.Equal(3, m.OwnerPlayerOf(22));
        Assert.False(m.IsRetainer(5)); Assert.Equal(-1, m.IndexOfWorld(5)); Assert.False(m.IsRetainer(0));
        Assert.Equal([0, 1, 2, 9], m.Indices);
        // Pollux is out (no World id); the owner came back as player 6 (the server re-bound the squad)
        m.ApplyRoster(new SquadRosterSection(100, 6, false, [R(0, 0, "Castor", world: 21), R(1, 1, "Pollux"), R(2, 2, "Brother Remus", world: 23)]));
        Assert.False(m.IsRetainer(22)); Assert.Equal(0, m.WorldIdOf(1));
        Assert.Equal(6, m.OwnerPlayerOf(21));
        Assert.Equal([0, 2, 9], m.Indices);
    }

    [Fact]
    public void A_roster_section_replaces_only_its_owners()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody> { [21] = Up(), [30] = Up() };
        var m = Model(bodies);
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Cato", world: 21), R(1, 1, "Pollux", world: 22), R(2, 2, "Brother Remus", world: 23)]));
        m.TrySample(0, 1.2, out var renamed);
        Assert.Equal("Cato", renamed.Name);
        m.TrySample(9, 1.2, out var other);
        Assert.Equal("Titus", other.Name);
        Assert.Equal(2, m.Rosters.Count);
        Assert.True(m.IsRetainer(30));
    }

    [Fact]
    public void Events_become_feed_lines_and_pounds_are_returned_separately()
    {
        var m = Model([]);
        var pounds = new List<Vector3>();
        var lines = m.ApplyEvents(
        [
            new SquadEventWire(SquadEventKind.Downed, 0, 255), new SquadEventWire(SquadEventKind.Revived, 0, 3), new SquadEventWire(SquadEventKind.Revived, 1, 202),
            new SquadEventWire(SquadEventKind.Out, 2, 255), new SquadEventWire(SquadEventKind.Recruited, 1, 255), new SquadEventWire(SquadEventKind.Pound, 0, 255, new Vector3(1, 2, 3)),
        ], id => id == 3 ? "Ash" : null, pounds, now: 10.0);
        Assert.Equal(["Brother Castor IS DOWN", "Brother Castor REVIVED BY Ash", "Brother Pollux REVIVED BY Brother Remus", "Brother Remus IS OUT", "Brother Pollux JOINS THE SQUAD"], lines);
        Assert.Equal([new Vector3(1, 2, 3)], pounds);
    }

    [Fact]
    public void The_hud_line_counts_standing_and_turns_to_down()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody>();
        var m = new WarfrontSquadModel { OwnPlayer = 3, World = id => bodies.TryGetValue(id, out var b) ? b : null };
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 255, "Castor"), R(1, 255, "Pollux"), R(2, 255, "Brother Remus")]));
        Assert.Null(m.HudLine(1.0));   // the squad is not in a battle yet
        m = Model(bodies);
        bodies[21] = Up(); bodies[22] = Up(); bodies[23] = Up(); bodies[30] = Fallen();
        Assert.Equal("SQUAD 3/3", m.HudLine(1.0));   // another squad's Marine is not the viewer's
        bodies[22] = Fallen(); bodies[23] = Fallen();
        Assert.Equal("2 DOWN", m.HudLine(1.1));
        bodies[22] = Up(); bodies[23] = Up();
        Assert.Equal("SQUAD 3/3", m.HudLine(1.2));
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Castor", world: 21), R(1, 1, "Pollux"), R(2, 2, "Brother Remus", world: 23)]));   // Pollux is out
        Assert.Equal("SQUAD 2/3", m.HudLine(1.3));
    }

    [Fact]
    public void A_downed_marine_counts_its_window_down_from_the_event_or_from_when_it_was_first_seen_down()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody> { [21] = Up(), [22] = Fallen(1, 0), [23] = Up() };
        var m = Model(bodies);
        m.ApplyEvents([new SquadEventWire(SquadEventKind.Downed, 1, 255)], _ => null, now: 1.0);
        m.Frame(3.0);
        m.TrySample(1, 3.0, out var a);
        Assert.True(a.Downed); Assert.Equal(WarfrontSquadModel.DownedSeconds - 2f, a.DownedLeft, 0.01f);
        bodies[23] = Fallen();   // no event seen (a client that joined late): counted from now
        m.Frame(5.0);
        m.TrySample(2, 6.0, out var b);
        Assert.Equal(WarfrontSquadModel.DownedSeconds - 1f, b.DownedLeft, 0.01f);
        bodies[22] = Up();   // revived
        m.Frame(7.0);
        m.TrySample(1, 7.0, out var c);
        Assert.False(c.Downed); Assert.Equal(0f, c.DownedLeft);
    }

    [Fact]
    public void The_revive_prompt_names_the_nearest_downed_marine_within_reach_and_the_own_hold_fills_its_ring()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody> { [21] = Up(10, 0), [22] = Fallen(1.5f, 0), [23] = Fallen(1f, 1f) };
        var m = Model(bodies);
        var c = m.ReviveCandidate(Vector3.Zero, 1.0);
        Assert.NotNull(c);
        Assert.Equal(2, c!.Value.Index);   // 1.41 m beats 1.5 m
        Assert.Equal("Brother Remus", c.Value.Name);
        Assert.Equal(0f, c.Value.Share);   // not held
        m.ReviveCandidate(Vector3.Zero, 2.0, useHeld: true);
        var held = m.ReviveCandidate(Vector3.Zero, 2.0 + WarfrontSquadModel.ReviveSeconds / 2, useHeld: true);
        Assert.Equal(0.5f, held!.Value.Share, 0.01f);
        Assert.Equal(0f, m.ReviveCandidate(Vector3.Zero, 3.0, useHeld: false)!.Value.Share);   // released: the hold starts again
        Assert.Equal(0f, m.ReviveCandidate(Vector3.Zero, 3.1, useHeld: true)!.Value.Share, 0.01f);
        Assert.Null(m.ReviveCandidate(new Vector3(30, 0, 0), 1.0));
        Assert.Null(m.ReviveCandidate(new Vector3(0, WarfrontSquadModel.ReviveReach + 2.5f, 0), 1.0));
    }

    [Fact]
    public void A_lost_recruit_event_changes_nothing_the_roster_section_adds_the_marine()
    {
        var m = new WarfrontSquadModel { OwnPlayer = 3 };
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "A"), R(1, 1, "B")]));
        Assert.Equal(2, m.PanelRows(true).Count);
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "A"), R(1, 1, "B"), R(2, 255, "C")]));   // the event was lost; the section arrives
        Assert.Equal(3, m.PanelRows(true).Count);
    }

    [Fact]
    public void The_panel_lists_the_own_roster_and_the_others_apart_with_their_state()
    {
        var bodies = new Dictionary<int, WarfrontWorldBody> { [21] = Up(), [22] = Fallen() };
        var m = Model(bodies);
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Castor", world: 21), R(1, 1, "Pollux", world: 22), R(2, 2, "Brother Remus"), R(3, 255, "Cato")]));
        var own = m.PanelRows(own: true);
        Assert.Equal(["Castor", "Pollux", "Brother Remus", "Cato"], own.Select(r => r.Name));
        Assert.Equal(["FIGHTING", "DOWN", "OUT", "READY"], own.Select(r => r.State));
        Assert.Equal(8, own[0].Record.Count);
        Assert.Single(m.PanelRows(own: false));
        m.ApplyRoster(new SquadRosterSection(100, 3, true, [R(0, 255, "Castor")]));
        Assert.Equal("ON HOLD", m.PanelRows(own: true)[0].State);
    }

    [Fact]
    public void A_standard_retainer_wears_the_owners_look_and_a_librarians_squad_only_his_colours()
    {
        var owner = AppearanceCode.Default.WithPiece(ArmourSlot.Head, 3).WithPalette(ArmourSlot.Torso, 10, 20, 30).WithPattern(ArmourSlot.Torso, true).WithPredefinedSet(2);
        Assert.Equal(owner, WarfrontSquadModel.LookCode(owner, true));
        var coloursOnly = WarfrontSquadModel.LookCode(owner, false);
        Assert.Equal(AppearanceCode.DefaultIndex, coloursOnly.Piece(ArmourSlot.Head));
        Assert.False(coloursOnly.HasPredefinedSet);
        Assert.Equal((byte)10, coloursOnly.Palette(ArmourSlot.Torso, 0)); Assert.Equal((byte)30, coloursOnly.Palette(ArmourSlot.Torso, 2));
        Assert.True(coloursOnly.PatternOn(ArmourSlot.Torso));
        Assert.Equal(SmSim.Extras.Warfront.Retainers.RetainerLook.CodeOf(owner, false), coloursOnly);   // the server's look path applies the same rule
    }

    /// <summary>T5 follow-up: the drawn arc follows the server's <c>WarfrontCleaveBonus</c> through the same switch: a human keeps Warfront's x1.2, a retainer gets it exactly when <c>WarfrontRetainerRules.CleaveBonus</c> is on, other puppets never.</summary>
    [Fact]
    public void The_drawn_arc_gives_Warfronts_cleave_reach_as_the_servers_switch_says()
    {
        Assert.True(WarfrontSquadModel.CleaveBonus(puppet: false, retainer: false));   // a human: unchanged
        Assert.Equal(SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.CleaveBonus, WarfrontSquadModel.CleaveBonus(puppet: true, retainer: true));   // a retainer: the server's switch, not a constant of the client
        Assert.False(WarfrontSquadModel.CleaveBonus(puppet: true, retainer: false));   // the allied pair, a pod's Marines: Musou's reach
    }
}
