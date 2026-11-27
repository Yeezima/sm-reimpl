using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront;

namespace SmNet.Tests.Extras.Warfront.Vox;

/// <summary>Guardsmen G2: the vox section's wire (tag 52, 5 bytes).</summary>
public class WarfrontVoxWireTests
{
    [Fact]
    public void A_line_round_trips_in_five_bytes()
    {
        var line = new WarfrontVoxLine(0x1234, WarfrontVoxKind.FallingBack, 1, WarfrontVox.LandingZone, 999);
        var bytes = WarfrontVoxWire.Encode(line);
        Assert.Equal(WarfrontVoxWire.Length, bytes.Length);
        Assert.Equal(new byte[] { 0x34, 0x12, 2, 1, 3 }, bytes);
        Assert.True(WarfrontVoxWire.TryDecode(bytes, out var back));
        Assert.Equal(new WarfrontVoxWire.Line(0x1234, WarfrontVoxKind.FallingBack, 1, 3), back);
    }

    [Theory]
    [InlineData(new byte[] { 1, 0, 6, 0, 255 })]     // kind above 5
    [InlineData(new byte[] { 1, 0, 0, 0, 255 })]     // kind 0
    [InlineData(new byte[] { 0, 0, 1, 0, 255 })]     // sequence 0
    [InlineData(new byte[] { 1, 0, 1, 4, 255 })]     // a point above 3 that is not 255
    [InlineData(new byte[] { 1, 0, 2, 0, 7 })]
    [InlineData(new byte[] { 1, 0, 1, 0 })]           // short
    [InlineData(new byte[] { 1, 0, 1, 0, 255, 0 })]   // long
    public void Bad_payloads_are_refused(byte[] payload) => Assert.False(WarfrontVoxWire.TryDecode(payload, out _));

    [Fact]
    public void The_decoder_never_throws_on_random_bytes()
    {
        var rng = new Random(52);
        int ok = 0;
        for (int i = 0; i < 20_000; i++)
        {
            var b = new byte[rng.Next(0, 9)];
            rng.NextBytes(b);
            if (WarfrontVoxWire.TryDecode(b, out var line))
            {
                ok++;
                Assert.InRange((int)line.Kind, 1, 5);
                Assert.True(line.From <= 3 || line.From == 255);
                Assert.Equal(b, WarfrontVoxWire.Encode(line));
            }
        }
        Assert.True(ok >= 0);   // random bytes are almost never a valid line; the point is that none throws
    }

    [Fact]
    public void The_tag_is_its_own()
    {
        Assert.Equal(52, WarfrontWire.VoxTag);
        Assert.NotEqual(WarfrontChaosWire.Tag, WarfrontWire.VoxTag);
        Assert.NotEqual(50, WarfrontWire.VoxTag);   // executions X1's tag (its namespace stays inside the executions folders)
        Assert.NotEqual(WarfrontBattleWire.EnvelopeTag, WarfrontWire.VoxTag);
        Assert.NotEqual(Protocol.WeatherStateTag, WarfrontWire.VoxTag);
    }
}
