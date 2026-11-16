using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.Coop;

/// <summary>Slice W12: the Coop section and command (tags 46 and 42): round trips, bounds, and a fuzz that never throws.</summary>
public class WarfrontCoopWireTests
{
    static WarfrontCoopWire.Section Sample() => new(
        WarfrontCoopRole.AwaitPicks, WarfrontWatchKind.Marine, 9, WarfrontCoopWire.FlagYouLead | WarfrontCoopWire.FlagGrantApplied, 72,
        [new WarfrontAwaited("Cato", WarfrontAwaitReason.Class, 2), new WarfrontAwaited("Dorn", WarfrontAwaitReason.Picks, 1)]);

    [Fact]
    public void The_tags_are_the_ones_the_brief_gives_and_do_not_clash_with_W5a_or_W10()
    {
        Assert.Equal(46, WarfrontCoopWire.EnvelopeTag);
        Assert.Equal(42, WarfrontCoopWire.CommandTag);
    }

    [Fact]
    public void A_section_round_trips()
    {
        var s = Sample();
        var bytes = WarfrontCoopWire.Encode(s);
        Assert.True(WarfrontCoopWire.TryDecode(bytes, out var back));
        Assert.Equal(s.Role, back.Role);
        Assert.Equal(s.WatchKind, back.WatchKind);
        Assert.Equal(s.WatchId, back.WatchId);
        Assert.Equal(s.Flags, back.Flags);
        Assert.Equal(s.GateSecondsLeft, back.GateSecondsLeft);
        Assert.Equal(s.Awaited, back.Awaited);
        Assert.True(back.YouLead && back.GrantApplied && !back.LeaderChanged);
    }

    [Fact]
    public void An_empty_section_is_six_bytes()
    {
        var bytes = WarfrontCoopWire.Encode(new WarfrontCoopWire.Section(WarfrontCoopRole.Player, WarfrontWatchKind.None, 0, 0, 0, []));
        Assert.Equal(6, bytes.Length);
        Assert.True(WarfrontCoopWire.TryDecode(bytes, out var back));
        Assert.Empty(back.Awaited);
    }

    [Fact]
    public void Long_names_and_too_many_rows_are_cut_never_thrown()
    {
        var many = Enumerable.Range(0, 9).Select(i => new WarfrontAwaited(new string('x', 60) + i, WarfrontAwaitReason.Class, 0)).ToArray();
        var bytes = WarfrontCoopWire.Encode(new WarfrontCoopWire.Section(WarfrontCoopRole.AwaitClass, 0, 0, 0, 0, many));
        Assert.True(WarfrontCoopWire.TryDecode(bytes, out var back));
        Assert.Equal(WarfrontCoopWire.MaxAwaited, back.Awaited.Length);
        Assert.All(back.Awaited, a => Assert.Equal(WarfrontCoopWire.MaxName, a.Name.Length));
        // a multi-byte name is never cut inside a character
        var wide = WarfrontCoopWire.Encode(new WarfrontCoopWire.Section(WarfrontCoopRole.AwaitClass, 0, 0, 0, 0, [new WarfrontAwaited(new string('é', 20), WarfrontAwaitReason.Class, 0)]));
        Assert.True(WarfrontCoopWire.TryDecode(wide, out var w2));
        Assert.DoesNotContain('�', w2.Awaited[0].Name);
    }

    [Fact]
    public void FromView_clamps_and_maps_the_flags()
    {
        var v = new WarfrontCoopView(WarfrontCoopRole.SpectatingJoiner, new WarfrontWatch(WarfrontWatchKind.Player, 300), true, true, false, 999, "Ash", 4, 0, []);
        var s = WarfrontCoopWire.FromView(v);
        Assert.Equal(255, s.WatchId);
        Assert.Equal(255, s.GateSecondsLeft);
        Assert.True(s.YouLead && s.LeaderChanged && !s.GrantApplied);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0, 0, 0, 0, 0 })]                          // too short
    [InlineData(new byte[] { 9, 0, 0, 0, 0, 0 })]                       // bad role
    [InlineData(new byte[] { 0, 9, 0, 0, 0, 0 })]                       // bad watch kind
    [InlineData(new byte[] { 0, 0, 0, 0x80, 0, 0 })]                    // unknown flag
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 5 })]                       // too many awaited
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1 })]                       // awaited row missing
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 25, 0, 0 })]             // name too long
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 1, 65, 7, 0 })]          // bad reason
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 1 })]                    // trailing byte
    public void Malformed_sections_are_a_false(byte[] data)
    {
        Assert.False(WarfrontCoopWire.TryDecode(data, out _));
    }

    [Fact]
    public void Fuzz_never_throws_and_whatever_decodes_encodes_back_to_the_same_bytes()
    {
        var rng = new Random(46);
        int decoded = 0;
        for (int i = 0; i < 20000; i++)
        {
            var b = new byte[rng.Next(0, 40)];
            rng.NextBytes(b);
            if (b.Length >= 6 && rng.Next(3) > 0) { b[0] %= 4; b[1] %= 3; b[3] &= 7; b[5] %= 5; }   // bias towards the valid header
            if (WarfrontCoopWire.TryDecode(b, out var s))
            {
                decoded++;
                Assert.True(WarfrontCoopWire.TryDecode(WarfrontCoopWire.Encode(s), out var again));
                Assert.Equal(s.Awaited, again.Awaited);
            }
            WarfrontCoopWire.TryDecodeCommand(b, out _);
        }
        Assert.True(decoded > 0);
    }

    [Fact]
    public void Commands_round_trip_and_bad_ones_are_refused()
    {
        Assert.True(WarfrontCoopWire.TryDecodeCommand(WarfrontCoopWire.EncodeCommand(new(WarfrontCoopWire.CommandKind.ConfirmClass, 0)), out var c));
        Assert.Equal(WarfrontCoopWire.CommandKind.ConfirmClass, c.Kind);
        foreach (int step in new[] { -1, 1 })
        {
            Assert.True(WarfrontCoopWire.TryDecodeCommand(WarfrontCoopWire.EncodeCommand(new(WarfrontCoopWire.CommandKind.Watch, step)), out var w));
            Assert.Equal(step, w.Step);
        }
        Assert.False(WarfrontCoopWire.TryDecodeCommand(new byte[] { 1, 0 }, out _));      // a step of 0
        Assert.False(WarfrontCoopWire.TryDecodeCommand(new byte[] { 1, 5 }, out _));      // a step of 5
        Assert.False(WarfrontCoopWire.TryDecodeCommand(new byte[] { 0, 0 }, out _));      // ConfirmClass carries nothing more
        Assert.False(WarfrontCoopWire.TryDecodeCommand(new byte[] { 2 }, out _));         // unknown kind
        Assert.False(WarfrontCoopWire.TryDecodeCommand(ReadOnlySpan<byte>.Empty, out _));
    }
}
