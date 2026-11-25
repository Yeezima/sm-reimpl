using System.Numerics;
using SmNet.Extras.Warfront;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmNet.Tests.Extras.Warfront.Chaos.Finale;

/// <summary>Chaos Warfront C5: the finale's kinds of the Chaos section (tag 51, kinds 16 and 17): round trips, C4's forward, and a decoder that never throws.</summary>
public class DuelWireTests
{
    [Fact]
    public void StateRoundTrip()
    {
        float health = 6234.5678f;
        var s = WarfrontChaosDuelWire.Of(WarfrontDuelPhase.Duel, 3, 5, 2, 1, 17, health, 15000f);
        byte[] b = WarfrontChaosDuelWire.Encode(s);
        Assert.Equal(WarfrontChaosDuelWire.StateLength, b.Length);
        Assert.True(WarfrontChaosWire.IsDuelKind(b));
        Assert.False(WarfrontChaosWire.TryDecode(b, out _));   // C4's decoder leaves it to this one
        Assert.True(WarfrontChaosDuelWire.TryDecode(b, out var d));
        Assert.Equal(WarfrontChaosDuelWire.KindState, d.Kind);
        Assert.Equal(s, d.Duel);
        Assert.Equal(BitConverter.SingleToInt32Bits(health), BitConverter.SingleToInt32Bits(d.Duel.Health));   // bit for bit
        Assert.Equal(416, d.Duel.Permille);   // 415.6 rounded up
        Assert.Equal(1, WarfrontChaosDuelWire.Of(WarfrontDuelPhase.Duel, 0, 0, 0, -1, 1, 0.01f, 15000f).Permille);   // a living Lord never shows 0
        Assert.Equal(255, WarfrontChaosDuelWire.Of(WarfrontDuelPhase.Handoff, 0, 0, 0, -1, 1, 1f, 1f).Attempt);
    }

    [Fact]
    public void TelegraphRoundTrip()
    {
        var t = WarfrontChaosDuelWire.Of(new WarfrontLordTelegraph(123456789u, WarfrontLordTelegraph.Leap, new Vector3(-12.34f, 56.78f, 3.21f), 1f));
        byte[] b = WarfrontChaosDuelWire.Encode(t);
        Assert.Equal(WarfrontChaosDuelWire.TelegraphLength, b.Length);
        Assert.True(WarfrontChaosWire.IsDuelKind(b));
        Assert.True(WarfrontChaosDuelWire.TryDecode(b, out var d));
        Assert.Equal(WarfrontChaosDuelWire.KindTelegraph, d.Kind);
        Assert.Equal(t, d.Told);
        Assert.Equal(-12.3f, d.Told.Position.X, 3);
        Assert.Equal(1f, d.Told.Seconds, 3);
    }

    [Fact]
    public void DecoderNeverThrowsFuzz()
    {
        var rng = new Random(51);
        int accepted = 0;
        for (int k = 0; k < 20000; k++)
        {
            var b = new byte[rng.Next(0, 24)];
            rng.NextBytes(b);
            if (b.Length > 0 && rng.Next(2) == 0) b[0] = (byte)rng.Next(15, 19);
            if (WarfrontChaosDuelWire.TryDecode(b, out var s))
            {
                accepted++;
                Assert.True(s.Kind is WarfrontChaosDuelWire.KindState or WarfrontChaosDuelWire.KindTelegraph);
            }
        }
        Assert.True(accepted > 0);
        Assert.False(WarfrontChaosDuelWire.TryDecode([], out _));
        var bad = WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(WarfrontDuelPhase.Duel, 1, 2, 0, 0, 1, 1f, 1f));
        bad[1] = 9;   // a phase above Lost
        Assert.False(WarfrontChaosDuelWire.TryDecode(bad, out _));
        var tel = WarfrontChaosDuelWire.Encode(WarfrontChaosDuelWire.Of(new WarfrontLordTelegraph(1, 1, Vector3.Zero, 1f)));
        tel[5] = 5;   // no such ability
        Assert.False(WarfrontChaosDuelWire.TryDecode(tel, out _));
    }
}
