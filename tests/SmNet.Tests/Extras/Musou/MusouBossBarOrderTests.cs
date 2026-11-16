using SmNet.Extras.Musou;
using Xunit;

namespace SmNet.Tests.Extras.Musou;

/// <summary>The Musou HUD's boss column order (owner, 2026-11-10): the bosses the own player is damaging first, then the commander, then by distance; held a second between changes.</summary>
public class MusouBossBarOrderTests
{
    static readonly MusouBossBar Warboss = new(10, "ORK WARBOSS", 0.6f, true);
    static readonly MusouBossBar Nob = new(20, "BOSS NOB", 0.7f, false);
    static readonly MusouBossBar Mek = new(30, "BOSS BIG MEK", 0.3f, false);
    static readonly MusouBossBar Shoota = new(40, "BOSS SHOOTA BOY", 1f, false);

    readonly Dictionary<ushort, double> _hits = new();
    readonly Dictionary<ushort, float> _far = new() { [10] = 80f, [20] = 30f, [30] = 10f, [40] = 50f };

    double Hit(ushort id) => _hits.TryGetValue(id, out double t) ? t : double.NegativeInfinity;
    float Far(ushort id) => _far.TryGetValue(id, out float d) ? d : float.MaxValue;

    [Fact]
    public void CommanderThenNearestWhenNothingIsHit()
    {
        var order = new MusouBossBarOrder();
        order.Update([Nob, Mek, Warboss, Shoota], Hit, Far, 0);
        Assert.Equal([10, 30, 20, 40], order.Order);
        Assert.Equal(1, order.Hidden);
    }

    [Fact]
    public void BossesBeingHitComeFirstMostRecentFirst()
    {
        var order = new MusouBossBarOrder();
        _hits[40] = 9.0;
        _hits[20] = 9.5;
        order.Update([Nob, Mek, Warboss, Shoota], Hit, Far, 10);
        Assert.Equal([20, 40, 10, 30], order.Order);
        // past the window they fall back to the commander-then-distance order (after the hold)
        order.Update([Nob, Mek, Warboss, Shoota], Hit, Far, 16);
        Assert.Equal([10, 30, 20, 40], order.Order);
    }

    [Fact]
    public void OrderHoldsASecondButTheDeadLeaveAtOnce()
    {
        var order = new MusouBossBarOrder();
        order.Update([Nob, Mek, Warboss], Hit, Far, 0);
        Assert.Equal([10, 30, 20], order.Order);
        _hits[20] = 0.3;
        order.Update([Nob, Mek, Warboss], Hit, Far, 0.4);
        Assert.Equal([10, 30, 20], order.Order);   // held
        order.Update([Nob, Mek, Warboss], Hit, Far, 1.1);
        Assert.Equal([20, 10, 30], order.Order);   // changed after the hold
        _hits[30] = 1.3;
        order.Update([Nob, Warboss], Hit, Far, 1.4);   // the Mek died: it leaves at once, nothing else moves
        Assert.Equal([20, 10], order.Order);
        order.Update([Nob, Warboss, Shoota], Hit, Far, 1.5);   // a new boss joins at the end until the next change
        Assert.Equal([20, 10, 40], order.Order);
    }
}
