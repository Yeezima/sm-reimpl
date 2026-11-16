using System.Numerics;
using SmClient.Extras.Warfront.Squad;
using SmNet.Extras.Warfront;
using SmSim;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.SquadWireTests;

/// <summary>Warfront W10: the client's squad model (engine-free): interpolation, dropping, roster replacement, feed lines, the HUD line, the revive candidate, the look code.</summary>
public class WarfrontSquadModelTests
{
    static SquadMarineWire M(int i, Vector3 at, byte state = 1, byte aux = 0, bool revive = false, float yaw = 0f) =>
        new((byte)i, state, false, false, revive, 0, false, at, yaw, 255, aux, SquadMarineWire.NoTarget);

    static SquadStateSection S(params SquadMarineWire[] m) => new(0, m.Length > 0 ? m[0].Index : (byte)0, m);

    static SquadRosterWire R(int slot, int index, string name, byte cls = 1, bool own = true) =>
        new((byte)slot, (byte)index, name, cls, 10, 11, 12, own, new ushort[] { 5, 1, 0, 2, 3, 1, 4, 2 });

    static WarfrontSquadModel Model()
    {
        var m = new WarfrontSquadModel { OwnPlayer = 3 };
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Castor"), R(1, 1, "Pollux"), R(2, 2, "Brother Remus")]));
        m.ApplyRoster(new SquadRosterSection(200, 4, false, [R(0, 9, "Titus", 0, own: false)]));
        return m;
    }

    [Fact]
    public void A_marine_is_drawn_between_its_last_two_records_a_tenth_of_a_second_behind()
    {
        var m = Model();
        m.ApplyState(S(M(0, new Vector3(0, 0, 0))), 10.0);
        m.ApplyState(S(M(0, new Vector3(1, 0, 0))), 10.1);
        Assert.True(m.TrySample(0, 10.15, out var half));   // render time 10.05: halfway
        Assert.Equal(0.5f, half.Position.X, 0.01f);
        Assert.True(m.TrySample(0, 10.2, out var end));   // render time 10.1: at the newest record
        Assert.Equal(1f, end.Position.X, 0.01f);
        Assert.True(end.Speed > 9f && end.Speed < 11f, "10 m/s from the position delta");
        Assert.True(m.TrySample(0, 10.1, out var start));   // render time 10.0: the older record
        Assert.Equal(0f, start.Position.X, 0.01f);
        Assert.True(m.TrySample(0, 12.0, out var held));   // a long gap: held a little past the newest, never further
        Assert.True(held.Position.X <= 1.0f + 0.25f * 10f + 0.01f);
    }

    [Fact]
    public void Yaw_turns_the_short_way_and_a_far_move_snaps()
    {
        var m = Model();
        m.ApplyState(S(M(0, Vector3.Zero, yaw: 6.2f)), 1.0);
        m.ApplyState(S(M(0, Vector3.Zero, yaw: 0.2f)), 1.1);
        m.TrySample(0, 1.15, out var s);
        Assert.True(MathF.Abs(MathF.IEEERemainder(s.Yaw - 6.3f, MathF.PI * 2)) < 0.15f, $"yaw {s.Yaw}");
        m.ApplyState(S(M(1, Vector3.Zero)), 2.0);
        m.ApplyState(S(M(1, new Vector3(40, 0, 0))), 2.1);
        m.TrySample(1, 2.15, out var far);
        Assert.Equal(40f, far.Position.X, 0.01f);
    }

    [Fact]
    public void An_index_not_seen_for_a_second_is_dropped_and_the_others_stay()
    {
        var m = Model();
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.One)), 5.0);
        m.ApplyState(S(M(1, Vector3.One)), 5.8);
        m.Prune(5.9);
        Assert.Equal(2, m.Known);
        m.ApplyState(S(M(1, Vector3.One)), 6.3);
        m.Prune(6.3);
        Assert.Equal([1], m.Indices);
        Assert.Equal(1, m.Dropped);
    }

    [Fact]
    public void The_two_halves_of_a_split_list_make_all_of_them_known()
    {
        var m = new WarfrontSquadModel();
        m.ApplyState(S(Enumerable.Range(0, 18).Select(i => M(i, new Vector3(i, 0, 0))).ToArray()), 1.0);
        m.ApplyState(S(Enumerable.Range(18, 18).Select(i => M(i, new Vector3(i, 0, 0))).ToArray()), 1.1);
        m.Prune(1.15);
        Assert.Equal(36, m.Known);
    }

    [Fact]
    public void A_roster_section_replaces_only_its_owners()
    {
        var m = Model();
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.One), M(9, new Vector3(5, 5, 0))), 1.0);
        m.ApplyRoster(new SquadRosterSection(100, 3, false, [R(0, 0, "Cato"), R(1, 1, "Pollux"), R(2, 2, "Brother Remus")]));
        m.TrySample(0, 1.2, out var renamed);
        Assert.Equal("Cato", renamed.Name);
        m.TrySample(9, 1.2, out var other);
        Assert.Equal("Titus", other.Name);
        Assert.False(other.Own);
        Assert.True(renamed.Own);
        Assert.Equal(2, m.Rosters.Count);
        Assert.Equal(12, other.Melee0);
        Assert.Equal(0, other.Class);
    }

    [Fact]
    public void An_unrostered_marine_still_draws_with_a_placeholder_name()
    {
        var m = new WarfrontSquadModel();
        m.ApplyState(S(M(7, Vector3.Zero)), 1.0);
        Assert.True(m.TrySample(7, 1.0, out var s));
        Assert.Equal("Marine 8", s.Name);
        Assert.Equal(255, s.Class);
    }

    [Fact]
    public void Events_become_feed_lines_and_pounds_are_returned_separately()
    {
        var m = Model();
        var pounds = new List<Vector3>();
        var lines = m.ApplyEvents(
        [
            new SquadEventWire(SquadEventKind.Downed, 0, 255), new SquadEventWire(SquadEventKind.Revived, 0, 3), new SquadEventWire(SquadEventKind.Revived, 1, 202),
            new SquadEventWire(SquadEventKind.Out, 2, 255), new SquadEventWire(SquadEventKind.Recruited, 1, 255), new SquadEventWire(SquadEventKind.Pound, 0, 255, new Vector3(1, 2, 3)),
        ], id => id == 3 ? "Ash" : null, pounds);
        Assert.Equal(["Brother Castor is down", "Brother Castor revived by Ash", "Brother Pollux revived by Brother Remus", "Brother Remus is out", "Brother Pollux joins the squad"], lines);
        Assert.Equal([new Vector3(1, 2, 3)], pounds);
    }

    [Fact]
    public void The_hud_line_counts_standing_and_turns_to_down()
    {
        var m = Model();
        Assert.Null(m.HudLine(1.0));   // the squad is not in a battle yet
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.Zero), M(2, Vector3.Zero), M(9, Vector3.Zero)), 1.0);
        Assert.Equal("SQUAD 3/3", m.HudLine(1.0));   // another squad's Marine is not the viewer's
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.Zero, state: 2, aux: 150), M(2, Vector3.Zero, state: 2, aux: 100)), 1.1);
        Assert.Equal("2 DOWN", m.HudLine(1.1));
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.Zero), M(2, Vector3.Zero)), 1.2);
        Assert.Equal("SQUAD 3/3", m.HudLine(1.2));
    }

    [Fact]
    public void A_downed_marine_counts_its_window_down_and_a_revive_shows_its_share()
    {
        var m = Model();
        m.ApplyState(S(M(1, new Vector3(1, 0, 0), state: 2, aux: 150)), 1.0);
        m.TrySample(1, 1.0, out var a);
        Assert.True(a.Downed); Assert.Equal(15f, a.DownedLeft, 0.01f);
        m.TrySample(1, 3.0, out var b);
        Assert.Equal(13f, b.DownedLeft, 0.01f);
        m.ApplyState(S(M(1, new Vector3(1, 0, 0), state: 2, aux: 100, revive: true)), 3.1);
        m.TrySample(1, 3.2, out var c);
        Assert.True(c.BeingRevived); Assert.Equal(0.5f, c.ReviveShare, 0.01f);
        Assert.Equal(12.9f, c.DownedLeft, 0.2f);   // the countdown keeps running while a revive is under way
    }

    [Fact]
    public void The_revive_prompt_names_the_nearest_downed_marine_within_two_metres_only()
    {
        var m = Model();
        m.ApplyState(S(M(0, new Vector3(10, 0, 0)), M(1, new Vector3(1.5f, 0, 0), state: 2, aux: 150), M(2, new Vector3(1f, 1f, 0), state: 2, aux: 100, revive: true)), 1.0);
        var c = m.ReviveCandidate(Vector3.Zero, 1.0);
        Assert.NotNull(c);
        Assert.Equal(2, c!.Value.Index);   // 1.41 m beats 1.5 m
        Assert.Equal("Brother Remus", c.Value.Name);
        Assert.Equal(0.5f, c.Value.Share, 0.01f);
        Assert.Null(m.ReviveCandidate(new Vector3(30, 0, 0), 1.0));
        Assert.Null(m.ReviveCandidate(new Vector3(0, 3.5f, 0), 1.0));
    }

    [Fact]
    public void Name_and_detail_distances_are_the_planned_ones()
    {
        Assert.True(WarfrontSquadModel.NameVisible(true, 25f)); Assert.False(WarfrontSquadModel.NameVisible(true, 25.1f));
        Assert.True(WarfrontSquadModel.NameVisible(false, 8f)); Assert.False(WarfrontSquadModel.NameVisible(false, 8.1f));
        Assert.True(WarfrontSquadModel.FullDetail(40f)); Assert.False(WarfrontSquadModel.FullDetail(40.1f));
    }

    [Fact]
    public void The_panel_lists_the_own_roster_and_the_others_apart_with_their_state()
    {
        var m = Model();
        m.ApplyState(S(M(0, Vector3.Zero), M(1, Vector3.Zero, state: 2, aux: 80)), 1.0);
        var own = m.PanelRows(own: true);
        Assert.Equal(["Castor", "Pollux", "Brother Remus"], own.Select(r => r.Name));
        Assert.Equal(["FIGHTING", "DOWN", "READY"], own.Select(r => r.State));
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
    }
}
