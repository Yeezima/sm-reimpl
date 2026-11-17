using SmNet.Extras.Warfront;
using SmServer;
using SmSim.Extras.Warfront.Progression;
using Xunit;

namespace SmNet.Tests.Extras.Warfront.ProgressionWire;

public class WarfrontProgressionSenderTests
{
    const int Rate = 30;

    static WarfrontProgressionSnapshot Snap(int level = 3, long xp = 500, int pending = 0, bool open = false, int pickIndex = 4, double left = 20, bool aboard = false) =>
        new(level, xp, 100, 900, pending, new[] { 1, 1, 3 }, 1, 0.2f, open, pickIndex,
            new[] { WarfrontCard.AttackSpeed, WarfrontCard.MaxHp, WarfrontCard.Damage, WarfrontCard.CooldownReduction, WarfrontCard.MaxArmor, WarfrontCard.Retainer }, left, aboard);

    static int Count(List<(byte Tag, byte[] Payload)> l, byte tag) => l.Count(s => s.Tag == tag);

    [Fact]
    public void Progress_IsSentFirst_NotAgainUnchanged_ThenOnChange()
    {
        var s = new WarfrontProgressionSender();
        Assert.Equal(1, Count(s.Step(1, Snap(), 100, Rate), WarfrontProgressionWire.ProgressTag));
        Assert.Empty(s.Step(1, Snap(), 101, Rate));
        var changed = s.Step(1, Snap(xp: 600), 102, Rate);
        var section = Assert.Single(changed);
        Assert.Equal(WarfrontProgressionWire.ProgressTag, section.Tag);
        Assert.True(WarfrontProgressionWire.TryDecode(section.Payload, out WarfrontProgressWire p));
        Assert.Equal(600u, p.Xp);
        Assert.Equal(200, p.AttackSpeedMilli);
        Assert.Equal(1, p.Retainers);
    }

    [Fact]
    public void Progress_RepeatsEveryTwoSeconds()
    {
        var s = new WarfrontProgressionSender();
        s.Step(1, Snap(), 100, Rate);
        Assert.Empty(s.Step(1, Snap(), 100 + 2 * Rate - 1, Rate));
        Assert.Equal(1, Count(s.Step(1, Snap(), 100 + 2 * Rate, Rate), WarfrontProgressionWire.ProgressTag));
        Assert.Empty(s.Step(1, Snap(), 100 + 2 * Rate + 1, Rate));
    }

    [Fact]
    public void Card_Counts_AreInWireOrder()
    {
        var b = WarfrontProgressionSender.ProgressBytes(Snap());
        Assert.True(WarfrontProgressionWire.TryDecode(b, out WarfrontProgressWire p));
        Assert.Equal(new byte[] { 2, 0, 1, 0, 0 }, p.CardCounts.ToArray());
    }

    [Fact]
    public void Offer_RepeatsWhileOpen_ThenCloseIsSentThreeTimes()
    {
        var s = new WarfrontProgressionSender();
        uint t = 100;
        for (int i = 0; i < 4; i++)
        {
            var open = s.Step(1, Snap(open: true, left: 20 - i), t++, Rate);
            var o = Assert.Single(open, x => x.Tag == WarfrontProgressionWire.OfferTag);
            Assert.True(WarfrontProgressionWire.TryDecode(o.Payload, out WarfrontOfferWire w));
            Assert.True(w.Open); Assert.Equal(4, w.PickIndex); Assert.Equal((20 - i) * 10, w.TenthsLeft);
        }
        for (int i = 0; i < 3; i++)
        {
            var close = Assert.Single(s.Step(1, Snap(), t++, Rate), x => x.Tag == WarfrontProgressionWire.OfferTag);
            Assert.True(WarfrontProgressionWire.TryDecode(close.Payload, out WarfrontOfferWire w));
            Assert.False(w.Open);
        }
        Assert.Equal(0, Count(s.Step(1, Snap(), t++, Rate), WarfrontProgressionWire.OfferTag));
    }

    [Fact]
    public void ANewOffer_DuringTheClose_CancelsIt()
    {
        var s = new WarfrontProgressionSender();
        s.Step(1, Snap(open: true), 100, Rate);
        s.Step(1, Snap(), 101, Rate);   // close 1 of 3
        s.Step(1, Snap(open: true, pickIndex: 5), 102, Rate);
        var after = s.Step(1, Snap(), 103, Rate);
        Assert.True(WarfrontProgressionWire.TryDecode(Assert.Single(after, x => x.Tag == WarfrontProgressionWire.OfferTag).Payload, out WarfrontOfferWire w));
        Assert.False(w.Open);
        Assert.Equal(5, w.PickIndex);   // a fresh run of three for the new offer
    }

    [Fact]
    public void Players_AreIndependent_AndNeverCrossed()
    {
        var s = new WarfrontProgressionSender();
        var a = s.Step(1, Snap(open: true), 100, Rate);
        var b = s.Step(2, Snap(), 100, Rate);
        Assert.Equal(1, Count(a, WarfrontProgressionWire.OfferTag));
        Assert.Equal(0, Count(b, WarfrontProgressionWire.OfferTag));
        Assert.Empty(s.Step(2, Snap(), 101, Rate));   // player 1's offer does not make player 2 send
        s.Keep(new[] { 2 });
        Assert.Equal(1, Count(s.Step(1, Snap(), 102, Rate), WarfrontProgressionWire.ProgressTag));   // forgotten: starts over
    }

    [Fact]
    public void Aboard_AndTimeClamp()
    {
        var o = WarfrontProgressionSender.OfferBytes(Snap(open: true, left: 99, aboard: true));
        Assert.True(WarfrontProgressionWire.TryDecode(o, out WarfrontOfferWire w));
        Assert.True(w.Aboard);
        Assert.Equal(255, w.TenthsLeft);
        Assert.True(WarfrontProgressionRules.PickTimeoutSeconds * 10 <= 255);
    }
}
