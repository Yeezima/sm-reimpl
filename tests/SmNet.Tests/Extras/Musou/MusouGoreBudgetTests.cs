using System.Numerics;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Tests.Extras.Musou;

/// <summary>
/// Gore by source (musou-net-budget, the owner 2026-11-09): the own hits and kills first, the allied Marines' second, the rest last, in the client's budget and effect priority and in
/// the server's event order.
/// </summary>
public class MusouGoreBudgetTests
{
    const int Own = 3;
    const ushort Ally = 4, Puppet = TfwContracts.PuppetIdFirst;

    [Fact]
    public void Sources_are_own_ally_and_other()
    {
        Assert.Equal(MusouGoreSource.Own, MusouGorePriority.SourceOf(Own, Own));
        Assert.Equal(MusouGoreSource.Ally, MusouGorePriority.SourceOf(Ally, Own));
        Assert.Equal(MusouGoreSource.Ally, MusouGorePriority.SourceOf(Puppet, Own));   // an AI Marine is an ally
        Assert.Equal(MusouGoreSource.Other, MusouGorePriority.SourceOf(0, Own));        // fire support
        Assert.True(MusouGorePriority.FxPriority(MusouGoreSource.Own) > MusouGorePriority.FxPriority(MusouGoreSource.Ally));
        Assert.True(MusouGorePriority.FxPriority(MusouGoreSource.Ally) > MusouGorePriority.CrowdFirePriority);   // the crowd's tracers go before any allied gore
        Assert.True(MusouGorePriority.FxPriority(MusouGoreSource.Other) >= MusouGorePriority.CrowdFirePriority);
        Assert.True(MusouGorePriority.HitMetres(MusouGoreSource.Own) > MusouGorePriority.HitMetres(MusouGoreSource.Ally));
        Assert.True(MusouGorePriority.HitMetres(MusouGoreSource.Ally) >= MusouGorePriority.HitMetres(MusouGoreSource.Other));
    }

    [Fact]
    public void A_flood_of_allied_and_other_hits_never_takes_the_own_hits_room()
    {
        var budget = new MusouGoreBudget();
        int own = 0, ownTried = 0;
        for (int frame = 0; frame < 600; frame++)   // 10 s at 60 fps
        {
            budget.Refill(1f / 60f);
            for (int k = 0; k < 20; k++) { budget.TakeHit(MusouGoreSource.Ally); budget.TakeHit(MusouGoreSource.Other); }   // 1,200 a second each
            if (frame % 3 == 0) { ownTried++; if (budget.TakeHit(MusouGoreSource.Own)) own++; }   // 20 own hits a second
        }
        Assert.Equal(ownTried, own);
        Assert.InRange(budget.HitsTaken[(int)MusouGoreSource.Ally], 190, 220);   // 20 a second, 8 saved
        Assert.InRange(budget.HitsTaken[(int)MusouGoreSource.Other], 95, 110);
    }

    [Fact]
    public void Under_the_old_budget_the_flood_starves_the_own_hits()
    {
        var budget = new MusouGoreBudget { Old = true };
        int own = 0, ownTried = 0;
        for (int frame = 0; frame < 600; frame++)
        {
            budget.Refill(1f / 60f);
            for (int k = 0; k < 20; k++) budget.TakeHit(MusouGoreSource.Ally);
            if (frame % 3 == 0) { ownTried++; if (budget.TakeHit(MusouGoreSource.Own)) own++; }
        }
        Assert.True(own < ownTried / 4, $"{own} of {ownTried}");   // the measured failure: one shared bucket of 16 a second
    }

    [Fact]
    public void Kills_a_frame_are_capped_per_source()
    {
        var budget = new MusouGoreBudget();
        budget.Refill(1f / 60f);
        for (int k = 0; k < 40; k++) { budget.TakeKill(MusouGoreSource.Other); budget.TakeKill(MusouGoreSource.Ally); }
        int own = 0;
        for (int k = 0; k < 40; k++) if (budget.TakeKill(MusouGoreSource.Own)) own++;
        Assert.Equal(MusouGorePriority.KillsPerFrame(MusouGoreSource.Own), own);
        Assert.Equal(MusouGorePriority.KillsPerFrame(MusouGoreSource.Ally), budget.KillsTaken[(int)MusouGoreSource.Ally]);
        Assert.Equal(MusouGorePriority.KillsPerFrame(MusouGoreSource.Other), budget.KillsTaken[(int)MusouGoreSource.Other]);
        budget.Refill(1f / 60f);
        Assert.True(budget.TakeKill(MusouGoreSource.Own));   // a new frame starts again
        var old = new MusouGoreBudget { Old = true };
        old.Refill(1f / 60f);
        for (int k = 0; k < 8; k++) Assert.True(old.TakeKill(MusouGoreSource.Other));
        Assert.False(old.TakeKill(MusouGoreSource.Own));   // before: 8 a frame for everyone, first come
    }

    static MusouEvent Hit(ushort player, ushort enemy, uint tick = 1) => new(MusouEventKind.Hit, tick, enemy, player, 1, Vector3.Zero);
    static MusouEvent Tracer(ushort enemy, uint tick = 1) => new(MusouEventKind.Tracer, tick, enemy, 0, 0, Vector3.Zero);

    [Fact]
    public void The_window_sends_a_humans_hit_then_an_ai_marines_before_the_crowds_tracers()
    {
        List<MusouEvent> Order(bool old)
        {
            var window = new MusouEventWindow { OldHitRank = old };
            return window.Step(1, [Tracer(9), Hit(0, 8), Hit(Puppet, 7), Hit(Ally, 6), new MusouEvent(MusouEventKind.Strike, 1, 5, Own, 0, Vector3.Zero),
                new MusouEvent(MusouEventKind.Telegraph, 1, 4, Own, 0, Vector3.Zero)], cap: 5).ToList();
        }
        var now = Order(old: false);
        Assert.Equal([MusouEventKind.Telegraph, MusouEventKind.Strike], now.Take(2).Select(e => e.Kind).ToArray());   // gameplay first
        Assert.Equal([Ally, Puppet], now.Skip(2).Take(2).Select(e => e.Player).ToArray());
        Assert.Equal(MusouEventKind.Tracer, now[4].Kind);
        var before = Order(old: true);
        Assert.Equal([MusouEventKind.Telegraph, MusouEventKind.Strike, MusouEventKind.Tracer, MusouEventKind.Hit], before.Take(4).Select(e => e.Kind).ToArray());   // before: hits after the tracers
        Assert.Equal(0, MusouEventWindow.Rank(new MusouEvent(MusouEventKind.Death, 1, 1, 0, 0, Vector3.Zero)));   // deaths stay first
    }

    [Fact]
    public void A_clients_own_hits_and_kills_go_first_in_its_stream_when_the_events_overflow()
    {
        var box = new MusouBox(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));
        int cap = MusouSendRules.EventsPerTick(30);
        var events = new List<MusouEvent>();
        for (int k = 0; k < cap + 40; k++) events.Add(Hit(Ally, (ushort)(k + 1)));   // more than a tick's share, the own ones last
        events.Add(Hit(Own, 900));
        events.Add(new MusouEvent(MusouEventKind.Death, 1, 901, Own, 0, Vector3.Zero));
        List<MusouEvent> Sent(int own)
        {
            var plan = new MusouInterest().Plan(1, 30, [], Vector3.Zero, box, null, events, own);
            return plan.SelectMany(m => m).Where(s => s.Tag == MusouWire.EventsTag).SelectMany(s => MusouWire.ReadEvents(s.Payload, box)!).ToList();
        }
        var mine = Sent(Own);
        Assert.Equal(cap, mine.Count);
        Assert.Contains(mine, e => e.Enemy == 900 && e.Player == Own);
        Assert.Contains(mine, e => e.Enemy == 901 && e.Kind == MusouEventKind.Death);
        var someoneElse = Sent(0);
        Assert.DoesNotContain(someoneElse, e => e.Enemy is 900 or 901);   // in the window's order they fell past the share
    }

    [Fact]
    public void Own_first_never_puts_an_own_hit_before_a_death_a_telegraph_or_a_strike_and_drops_nothing()
    {
        var box = new MusouBox(new Vector3(-100f, -100f, -10f), new Vector3(100f, 100f, 30f));
        var events = new List<MusouEvent>
        {
            Hit(Ally, 1), new MusouEvent(MusouEventKind.Death, 1, 2, Ally, 0, Vector3.Zero), Hit(Own, 3), new MusouEvent(MusouEventKind.Telegraph, 1, 4, Own, 0, Vector3.Zero),
            Tracer(5), new MusouEvent(MusouEventKind.Death, 1, 6, Own, 0, Vector3.Zero), new MusouEvent(MusouEventKind.Strike, 1, 7, Own, 0, Vector3.Zero), Hit(Own, 8),
        };
        var plan = new MusouInterest().Plan(1, 30, [], Vector3.Zero, box, null, events, Own);
        var sent = plan.SelectMany(m => m).Where(s => s.Tag == MusouWire.EventsTag).SelectMany(s => MusouWire.ReadEvents(s.Payload, box)!).Select(e => e.Enemy).ToArray();
        Assert.Equal(new ushort[] { 6, 2, 4, 7, 3, 8, 1, 5 }, sent);   // own kill; other deaths, telegraphs, strikes; own hits; the rest, each in the window's order
        var asGiven = new MusouInterest().Plan(1, 30, [], Vector3.Zero, box, null, events, 0);
        Assert.Equal(events.Select(e => e.Enemy).ToArray(), asGiven.SelectMany(m => m).Where(s => s.Tag == MusouWire.EventsTag).SelectMany(s => MusouWire.ReadEvents(s.Payload, box)!).Select(e => e.Enemy).ToArray());
    }
}
