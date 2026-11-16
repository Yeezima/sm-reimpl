using System.Numerics;
using SmNet.Extras.Warfront;
using Xunit;
using Xunit.Abstractions;

namespace SmNet.Tests.Extras.Warfront.SquadWireTests;

/// <summary>Warfront W10: the squad's wire (tags 32, 33, 34 and command 41): round trips at the limits, malformed payloads, the split in halves, the size budget.</summary>
public class SquadWireTests(ITestOutputHelper output)
{
    static SquadMarineWire M(int i, byte state = 1) =>
        SquadWire.Make(i, state, firing: i % 3 == 0, melee: i % 5 == 0, beingRevived: state == 2 && i % 2 == 0, order: (byte)(i % 3), airborne: i % 7 == 0,
            new Vector3(-1500f + i * 83.35f, 1600f - i * 41.7f, -300f + i * 3.01f), i * 0.37f, i / 36f, state == 2 ? 12.3f : 0f, 0.55f, i % 4 == 0 ? 1100 + i : -1);

    static List<SquadMarineWire> All(byte state = 1) => Enumerable.Range(0, SquadWire.MaxMarines).Select(i => M(i, state)).ToList();

    static SquadRosterWire R(int slot, bool full = true) =>
        new((byte)slot, (byte)(slot * 2), full ? new string((char)('A' + slot), SquadWire.NameMaxBytes) : "Cato", (byte)(slot % 3), (ushort)(1000 + slot), (ushort)(2000 + slot), (ushort)(3000 + slot), slot % 2 == 0,
            Enumerable.Range(0, 8).Select(f => (ushort)(60000 - slot * 100 - f)).ToArray());

    [Fact]
    public void State_round_trips_36_marines_and_a_marine_keeps_its_fields()
    {
        var sent = All(1).Select((m, i) => i % 4 == 1 ? M(i, 2) : m).ToList();
        var bytes = SquadWire.EncodeState(7, sent);
        Assert.Equal(SquadWire.StateHeaderBytes + 36 * SquadWire.MarineBytes, bytes.Length);
        Assert.True(SquadWire.TryDecodeState(bytes, out var s));
        Assert.Equal(7, s.Phase);
        Assert.Equal(36, s.Marines.Count);
        for (int i = 0; i < 36; i++)
        {
            var a = sent[i]; var b = s.Marines[i];
            Assert.Equal(a.Index, b.Index); Assert.Equal(a.State, b.State); Assert.Equal(a.Firing, b.Firing); Assert.Equal(a.Melee, b.Melee); Assert.Equal(a.BeingRevived, b.BeingRevived);
            Assert.Equal(a.Order, b.Order); Assert.Equal(a.Airborne, b.Airborne); Assert.Equal(a.Health, b.Health); Assert.Equal(a.Aux, b.Aux); Assert.Equal(a.Target, b.Target);
            Assert.Equal(SquadWire.Quantised(a.Position).X, b.Position.X, 4); Assert.Equal(SquadWire.Quantised(a.Position).Y, b.Position.Y, 4); Assert.Equal(SquadWire.Quantised(a.Position).Z, b.Position.Z, 4);
            Assert.True(MathF.Abs(a.Position.X - b.Position.X) <= SquadWire.XyStep / 2 + 1e-3f && MathF.Abs(a.Position.Z - b.Position.Z) <= SquadWire.ZStep / 2 + 1e-3f);
        }
    }

    [Fact]
    public void Yaw_wraps_and_stays_within_one_step()
    {
        foreach (float y in new[] { 0f, 1f, -1f, 3.14f, 6.28f, 100f, -100f })
        {
            float back = SquadWire.YawOf(SquadWire.YawByte(y));
            float d = MathF.Abs(MathF.IEEERemainder(back - y, MathF.PI * 2f));
            Assert.True(d <= MathF.PI / 256f + 1e-3f, $"{y} -> {back}");
        }
        Assert.Equal(0, SquadWire.YawByte(float.NaN));
    }

    [Fact]
    public void State_with_nothing_in_it_round_trips_and_a_nan_position_is_sent_as_zero()
    {
        Assert.True(SquadWire.TryDecodeState(SquadWire.EncodeState(0, []), out var empty));
        Assert.Empty(empty.Marines);
        var m = M(3) with { Position = new Vector3(float.NaN, float.PositiveInfinity, float.NegativeInfinity) };
        Assert.True(SquadWire.TryDecodeState(SquadWire.EncodeState(1, [m]), out var s));
        Assert.Equal(Vector3.Zero, s.Marines[0].Position);
    }

    [Fact]
    public void Position_limits_clamp_instead_of_wrapping()
    {
        var m = M(0) with { Position = new Vector3(5000f, -5000f, 5000f) };
        Assert.True(SquadWire.TryDecodeState(SquadWire.EncodeState(0, [m]), out var s));
        Assert.True(s.Marines[0].Position.X > 1600f && s.Marines[0].Position.Y < -1600f && s.Marines[0].Position.Z > 600f);
    }

    [Fact]
    public void State_malformed_payloads_return_false()
    {
        var good = SquadWire.EncodeState(1, All());
        Assert.False(SquadWire.TryDecodeState([], out _));
        Assert.False(SquadWire.TryDecodeState(good.AsSpan(0, good.Length - 1), out _));   // truncated
        Assert.False(SquadWire.TryDecodeState([.. good, 0], out _));   // oversized
        var bad = (byte[])good.Clone(); bad[SquadWire.StateHeaderBytes] = 36;   // index out of range
        Assert.False(SquadWire.TryDecodeState(bad, out _));
        bad = (byte[])good.Clone(); bad[SquadWire.StateHeaderBytes + SquadWire.MarineBytes] = 0;   // indices not ascending
        Assert.False(SquadWire.TryDecodeState(bad, out _));
        bad = (byte[])good.Clone(); bad[SquadWire.StateHeaderBytes + 1] &= 0xFC;   // state 0 (free) is never sent
        Assert.False(SquadWire.TryDecodeState(bad, out _));
        bad = (byte[])good.Clone(); bad[2] = 37;   // count over the limit
        Assert.False(SquadWire.TryDecodeState(bad, out _));
        bad = (byte[])good.Clone(); bad[SquadWire.StateHeaderBytes + 10] = 201;   // aux over its range
        Assert.False(SquadWire.TryDecodeState(bad, out _));
    }

    [Fact]
    public void The_split_in_halves_covers_all_36_when_two_alternating_sends_are_merged()
    {
        var all = All();
        int room = SquadWire.StateHeaderBytes + 20 * SquadWire.MarineBytes;   // less room than the full list
        var seen = new Dictionary<int, SquadMarineWire>();
        for (byte phase = 0; phase < 2; phase++)
        {
            var bytes = SquadWire.EncodeStateFitting(phase, all, room, out bool half);
            Assert.True(half);
            Assert.True(bytes.Length <= room);
            Assert.True(SquadWire.TryDecodeState(bytes, out var s));
            foreach (var m in s.Marines) seen[m.Index] = m;
        }
        Assert.Equal(36, seen.Count);
        for (int i = 0; i < 36; i++) Assert.Equal(all[i].Health, seen[i].Health);
        var whole = SquadWire.EncodeStateFitting(0, all, SquadWire.MaxStateBytes, out bool halved);
        Assert.False(halved);
        Assert.Equal(SquadWire.MaxStateBytes, whole.Length);
    }

    [Fact]
    public void Roster_round_trips_nine_full_marines_and_fits_a_section()
    {
        var sent = new SquadRosterSection(0xFEDCBA9876543210UL, 3, true, Enumerable.Range(0, 9).Select(i => R(i)).ToList());
        var bytes = SquadWire.EncodeRoster(sent);
        Assert.True(bytes.Length <= SquadWire.MaxRosterBytes);
        Assert.True(bytes.Length < Protocol.MaxExtrasPayloadBytes);
        Assert.True(SquadWire.TryDecodeRoster(bytes, out var r));
        Assert.Equal(sent.Handle, r.Handle); Assert.Equal(3, r.OwnerPlayer); Assert.True(r.Held);
        Assert.Equal(9, r.Marines.Count);
        for (int i = 0; i < 9; i++)
        {
            Assert.Equal(sent.Marines[i].Name, r.Marines[i].Name); Assert.Equal(24, r.Marines[i].Name.Length);
            Assert.Equal(sent.Marines[i].Index, r.Marines[i].Index); Assert.Equal(sent.Marines[i].Class, r.Marines[i].Class);
            Assert.Equal(sent.Marines[i].Primary, r.Marines[i].Primary); Assert.Equal(sent.Marines[i].Secondary, r.Marines[i].Secondary); Assert.Equal(sent.Marines[i].Melee, r.Marines[i].Melee);
            Assert.Equal(sent.Marines[i].OwnPieces, r.Marines[i].OwnPieces);
            Assert.Equal(sent.Marines[i].Record, r.Marines[i].Record);
        }
        output.WriteLine($"largest SquadState {SquadWire.MaxStateBytes} bytes, largest roster section {bytes.Length} bytes (limit {Protocol.MaxExtrasPayloadBytes}); together {SquadWire.MaxStateBytes + bytes.Length}; four owners' rosters {4 * bytes.Length}");
    }

    [Fact]
    public void Roster_name_over_24_bytes_is_cut_at_a_character_and_malformed_rosters_return_false()
    {
        var wide = new SquadRosterWire(0, 255, new string('é', 20), 0, 0, 0, 0, false, []);   // 40 bytes of UTF-8
        Assert.True(SquadWire.TryDecodeRoster(SquadWire.EncodeRoster(new SquadRosterSection(1, 255, false, [wide])), out var r));
        Assert.Equal(12, r.Marines[0].Name.Length);
        Assert.Equal(SquadRosterWire.NotInBattle, r.Marines[0].Index);

        var good = SquadWire.EncodeRoster(new SquadRosterSection(1, 2, false, Enumerable.Range(0, 3).Select(i => R(i, false)).ToList()));
        Assert.False(SquadWire.TryDecodeRoster([], out _));
        Assert.False(SquadWire.TryDecodeRoster(good.AsSpan(0, good.Length - 1), out _));
        Assert.False(SquadWire.TryDecodeRoster([.. good, 1], out _));
        var bad = (byte[])good.Clone(); bad[10] = 10;   // ten Marines
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
        bad = (byte[])good.Clone(); bad[9] = 2;   // held is a boolean
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
        bad = (byte[])good.Clone(); bad[11 + 2] = 25;   // name length over 24
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
        bad = (byte[])good.Clone(); bad[11] = 9;   // slot out of range
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
        bad = (byte[])good.Clone(); bad[11 + 1] = 36;   // battle index out of range
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
        bad = (byte[])good.Clone(); bad[11 + 3] = 0xFF; bad[11 + 4] = 0xFE;   // not UTF-8
        Assert.False(SquadWire.TryDecodeRoster(bad, out _));
    }

    [Fact]
    public void Events_round_trip_and_a_pound_carries_its_point()
    {
        var sent = new List<SquadEventWire>
        {
            new(SquadEventKind.Downed, 4, 255), new(SquadEventKind.Revived, 4, 2), new(SquadEventKind.Out, 30, 200 + 5), new(SquadEventKind.Recruited, 8, 255),
            new(SquadEventKind.Pound, 12, 255, new Vector3(-100.4f, 250.3f, 7.7f)),
        };
        Assert.True(SquadWire.TryDecodeEvents(SquadWire.EncodeEvents(sent), out var back));
        Assert.Equal(5, back.Count);
        for (int i = 0; i < 4; i++) Assert.Equal(sent[i], back[i]);
        Assert.Equal(-100.4f, back[4].Point.X, 0.06f); Assert.Equal(250.3f, back[4].Point.Y, 0.06f); Assert.Equal(7.7f, back[4].Point.Z, 0.06f);
        Assert.True(SquadWire.EncodeEvents(Enumerable.Repeat(sent[4], SquadWire.MaxEvents).ToList()).Length <= Protocol.MaxExtrasPayloadBytes);
    }

    [Fact]
    public void Events_malformed_return_false()
    {
        Assert.False(SquadWire.TryDecodeEvents([], out _));
        Assert.False(SquadWire.TryDecodeEvents([1, 1, 4], out _));   // truncated
        Assert.False(SquadWire.TryDecodeEvents([1, 0, 4, 255], out _));   // kind 0
        Assert.False(SquadWire.TryDecodeEvents([1, 6, 4, 255], out _));   // kind 6
        Assert.False(SquadWire.TryDecodeEvents([1, 1, 36, 255], out _));   // index
        Assert.False(SquadWire.TryDecodeEvents([1, 5, 4, 255, 0, 0], out _));   // a pound with half a point
        Assert.False(SquadWire.TryDecodeEvents([1, 1, 4, 255, 9], out _));   // trailing byte
        Assert.False(SquadWire.TryDecodeEvents([200, 1, 4, 255], out _));   // the count claims more
    }

    [Fact]
    public void Commands_round_trip_and_refuse_the_malformed()
    {
        var rename = SquadWire.EncodeRename(4, "  Brother Castor  ");
        Assert.NotNull(rename);
        Assert.True(SquadWire.TryDecodeCommand(rename, out var c));
        Assert.Equal(SquadCommandKind.Rename, c.Kind); Assert.Equal(4, c.Slot); Assert.Equal("Brother Castor", c.Name);
        Assert.Null(SquadWire.EncodeRename(0, "   "));
        Assert.Null(SquadWire.EncodeRename(0, new string('x', 25)));
        Assert.NotNull(SquadWire.EncodeRename(0, new string('x', 24)));
        Assert.True(SquadWire.EncodeRename(0, new string('x', 24))!.Length <= Protocol.MaxExtrasCommandPayload);

        Assert.True(SquadWire.TryDecodeCommand(SquadWire.EncodeJumpPound(new Vector3(-50.04f, 120.1f, 3f)), out var j));
        Assert.Equal(SquadCommandKind.JumpPound, j.Kind); Assert.Equal(-50.0f, j.Point.X, 0.06f); Assert.Equal(120.1f, j.Point.Y, 0.06f); Assert.Equal(3f, j.Point.Z, 0.06f);
        Assert.True(SquadWire.TryDecodeCommand(SquadWire.EncodeSquadClass(2), out var k));
        Assert.Equal(2, k.Option);

        Assert.False(SquadWire.TryDecodeCommand([], out _));
        Assert.False(SquadWire.TryDecodeCommand([0], out _));
        Assert.False(SquadWire.TryDecodeCommand([9, 1, 2], out _));
        Assert.False(SquadWire.TryDecodeCommand([1, 0, 0], out _));   // empty name
        Assert.False(SquadWire.TryDecodeCommand([1, 0, 3, 65], out _));   // short name
        Assert.False(SquadWire.TryDecodeCommand([1, 9, 1, 65], out _));   // slot 9
        Assert.False(SquadWire.TryDecodeCommand([1, 0, 2, 0xFF, 0xFE], out _));   // not UTF-8
        Assert.False(SquadWire.TryDecodeCommand([2, 0, 0], out _));
        Assert.False(SquadWire.TryDecodeCommand([3], out _));
        Assert.False(SquadWire.TryDecodeCommand([3, 3], out _));
    }

    [Fact]
    public void Random_payloads_never_throw_and_only_decode_to_a_valid_form()
    {
        var rng = new Random(20261117);
        for (int n = 0; n < 4000; n++)
        {
            var p = new byte[rng.Next(0, 160)];
            rng.NextBytes(p);
            if (p.Length > 2 && n % 2 == 0) p[2] = (byte)rng.Next(0, 5);
            if (SquadWire.TryDecodeState(p, out var s)) Assert.All(s.Marines, m => { Assert.InRange(m.Index, 0, 35); Assert.InRange(m.State, 1, 2); Assert.True(m.Aux <= 200); });
            if (SquadWire.TryDecodeRoster(p, out var r)) Assert.True(r.Marines.Count <= 9);
            if (SquadWire.TryDecodeEvents(p, out var e)) Assert.All(e, x => Assert.InRange(x.Index, 0, 35));
            if (SquadWire.TryDecodeCommand(p, out var c) && c.Kind == SquadCommandKind.Rename) Assert.InRange(c.Name.Length, 1, 24);
        }
    }

    [Fact]
    public void Tags_are_the_planned_ones_and_clear_of_the_neighbours()
    {
        Assert.Equal(32, SquadWire.StateTag); Assert.Equal(33, SquadWire.RosterTag); Assert.Equal(34, SquadWire.EventsTag); Assert.Equal(41, SquadWire.CommandTag);
        Assert.Equal(471, SquadWire.MaxStateBytes);
        Assert.True(SquadWire.MaxStateBytes + SquadWire.MaxRosterBytes < Protocol.MaxExtrasPayloadBytes);
    }
}
