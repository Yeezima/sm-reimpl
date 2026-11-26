using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Progression;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.ProgressionWire;

/// <summary>Slice W8: the Progress and Offer records round-trip at their limits and every bad payload is refused without a throw.</summary>
public class WarfrontProgressionWireTests
{
    static WarfrontProgressWire Progress(int level = 7, uint xp = 12345, uint at = 9000, uint next = 15000, int pending = 2, int ret = 4, int speed = 300) =>
        new(level, xp, at, next, pending, new byte[] { 1, 2, 3, 4, 5 }, ret, speed);

    static WarfrontOfferWire Offer(bool open = true, int index = 3, int tenths = 150, bool aboard = false, params WarfrontCard[] cards) =>
        new(open, index, cards.Length > 0 ? cards : [WarfrontCard.Damage, WarfrontCard.MaxHp, WarfrontCard.AttackSpeed, WarfrontCard.CooldownReduction, WarfrontCard.MaxArmor, WarfrontCard.Retainer], tenths, aboard);

    static byte[] Mutated(byte[] b, int at, byte v) { var c = (byte[])b.Clone(); c[at] = v; return c; }

    [Fact]
    public void Tags_are_44_and_45_and_the_lengths_are_fixed()
    {
        Assert.Equal(44, WarfrontProgressionWire.ProgressTag);
        Assert.Equal(45, WarfrontProgressionWire.OfferTag);
        Assert.Equal(WarfrontProgressionWire.ProgressLength, WarfrontProgressionWire.Encode(Progress()).Length);
        Assert.Equal(WarfrontProgressionWire.OfferLength, WarfrontProgressionWire.Encode(Offer()).Length);
        Assert.True(WarfrontProgressionWire.ProgressLength + WarfrontProgressionWire.OfferLength < Protocol.MaxExtrasPayloadBytes / 4);
    }

    [Fact]
    public void The_pick_timeout_fits_the_u16_of_tenths()
    {
        // 40 s since owner 2026-11-23, card 2: the time left is a u16 of tenths (a u8 stopped at 25.5 s)
        Assert.True(WarfrontProgressionRules.PickTimeoutSeconds * 10 <= ushort.MaxValue, "a longer timeout would clamp silently on the wire");
        Assert.True(WarfrontProgressionRules.PickTimeoutSeconds * 10 > 255);
    }

    [Fact]
    public void Progress_round_trips_typical_and_at_its_limits()
    {
        foreach (var p in new[]
        {
            Progress(),
            Progress(1, 0, 0, 1500, 0, 1, 0),
            new WarfrontProgressWire(255, uint.MaxValue, uint.MaxValue - 1, uint.MaxValue, 255, new byte[] { 255, 255, 255, 255, 255 }, 255, 65535),
        })
        {
            Assert.True(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.Encode(p), out WarfrontProgressWire back));
            Assert.Equal(p.Level, back.Level); Assert.Equal(p.Xp, back.Xp); Assert.Equal(p.XpAtLevel, back.XpAtLevel); Assert.Equal(p.XpAtNextLevel, back.XpAtNextLevel);
            Assert.Equal(p.PendingPicks, back.PendingPicks); Assert.Equal(p.CardCounts, back.CardCounts); Assert.Equal(p.Retainers, back.Retainers); Assert.Equal(p.AttackSpeedMilli, back.AttackSpeedMilli);
        }
        Assert.Equal(0.3f, Progress().AttackSpeedBonus, 4);
    }

    [Fact]
    public void Offer_round_trips_open_closed_and_aboard()
    {
        var open = Offer(aboard: true, tenths: 255, index: 255);
        Assert.True(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.Encode(open), out WarfrontOfferWire a));
        Assert.True(a.Open); Assert.True(a.Aboard); Assert.Equal(255, a.PickIndex); Assert.Equal(25.5f, a.SecondsLeft, 3); Assert.Equal(open.Cards, a.Cards);
        Assert.True(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.EncodeClosed(9), out WarfrontOfferWire c));
        Assert.False(c.Open); Assert.Equal(9, c.PickIndex); Assert.Equal(0, c.TenthsLeft);
        // Five stat cards and no Retainer (the sixth slot empty) is a valid offer too.
        var stat = Offer(cards: [WarfrontCard.Damage, WarfrontCard.MaxHp, WarfrontCard.AttackSpeed, WarfrontCard.CooldownReduction, WarfrontCard.MaxArmor]);
        Assert.True(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.Encode(stat), out WarfrontOfferWire s));
        Assert.Equal([.. stat.Cards, WarfrontCard.None], s.Cards);
    }

    [Fact]
    public void Truncated_and_oversized_payloads_are_refused()
    {
        var p = WarfrontProgressionWire.Encode(Progress());
        var o = WarfrontProgressionWire.Encode(Offer());
        for (int n = 0; n < p.Length; n++) Assert.False(WarfrontProgressionWire.TryDecode(p.AsSpan(0, n), out WarfrontProgressWire _));
        for (int n = 0; n < o.Length; n++) Assert.False(WarfrontProgressionWire.TryDecode(o.AsSpan(0, n), out WarfrontOfferWire _));
        Assert.False(WarfrontProgressionWire.TryDecode(p.Append((byte)0).ToArray(), out WarfrontProgressWire _));
        Assert.False(WarfrontProgressionWire.TryDecode(o.Append((byte)0).ToArray(), out WarfrontOfferWire _));
        Assert.False(WarfrontProgressionWire.TryDecode(new byte[Protocol.MaxExtrasPayloadBytes + 1], out WarfrontProgressWire _));
        Assert.False(WarfrontProgressionWire.TryDecode(new byte[Protocol.MaxExtrasPayloadBytes + 1], out WarfrontOfferWire _));
    }

    [Fact]
    public void Out_of_range_values_are_refused()
    {
        var p = WarfrontProgressionWire.Encode(Progress());
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(p, 0, 0), out WarfrontProgressWire _));                                                           // level 0
        Assert.False(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.Encode(Progress(xp: 100, at: 200, next: 300)), out WarfrontProgressWire _));       // xp below its level's floor
        Assert.False(WarfrontProgressionWire.TryDecode(WarfrontProgressionWire.Encode(Progress(xp: 5000, at: 4000, next: 3000)), out WarfrontProgressWire _));    // next level below this one

        var o = WarfrontProgressionWire.Encode(Offer());
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 0, 2), out WarfrontOfferWire _));                          // open flag
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 10, 2), out WarfrontOfferWire _));                         // aboard flag (byte 10 since the u16 time)
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 2, 7), out WarfrontOfferWire _));                          // unknown card
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 2, 0), out WarfrontOfferWire _));                          // empty card in an open offer
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 3, o[2]), out WarfrontOfferWire _));                       // a duplicate
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 2, (byte)WarfrontCard.Retainer), out WarfrontOfferWire _)); // Retainer outside slot 6
        Assert.False(WarfrontProgressionWire.TryDecode(Mutated(o, 6, 0), out WarfrontOfferWire _));                          // an empty slot before the last
    }

    [Fact]
    public void Random_payloads_never_throw()
    {
        var rng = new Random(8);
        for (int i = 0; i < 20000; i++)
        {
            var buf = new byte[i % 3 == 0 ? WarfrontProgressionWire.ProgressLength : i % 3 == 1 ? WarfrontProgressionWire.OfferLength : rng.Next(0, 40)];
            rng.NextBytes(buf);
            WarfrontProgressionWire.TryDecode(buf, out WarfrontProgressWire _);
            WarfrontProgressionWire.TryDecode(buf, out WarfrontOfferWire _);
        }
    }
}
