using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Tests.Extras.Musou.Arsenal;

/// <summary>
/// The deferred death events (owner, 2026-11-10, "spread deaths over ticks"; <see cref="MusouSim.AddEvent"/>, <see cref="MusouSim.BeginTick"/>): a Death over the tick's 1,024 waits in a queue and goes out over the
/// next ticks, first in first out, up to 512 a tick and on top of the tick's own room (never in place of another event); the queue holds 4,096, a Death past that is dropped and counted; Clear forgets it.
/// </summary>
public class DeathQueueTests
{
    static MusouEvent Death(ushort enemy, uint tick = 1) => new(MusouEventKind.Death, tick, enemy, 1, (byte)MusouDeathKind.Blast, Vector3.Zero);
    static MusouEvent Hit(ushort enemy, uint tick = 1) => new(MusouEventKind.Hit, tick, enemy, 1, 1, Vector3.Zero);

    static MusouSim Full(out uint tick)
    {
        var sim = new MusouSim(null);
        tick = 1;
        sim.BeginTick(tick);
        for (ushort k = 1; k <= MusouSim.MaxEventsPerTick; k++) sim.AddEvent(Hit(k));
        Assert.Equal(MusouSim.MaxEventsPerTick, sim.Events.Count);
        return sim;
    }

    [Fact]
    public void A_death_over_the_cap_waits_and_comes_out_first_in_first_out_on_the_next_ticks()
    {
        var sim = Full(out uint tick);
        for (ushort k = 1; k <= 700; k++) sim.AddEvent(Death(k));
        Assert.Equal(MusouSim.MaxEventsPerTick, sim.Events.Count);   // nothing more this tick
        Assert.Equal(700, sim.DeathsWaiting);
        Assert.Equal(700, sim.DeathsDeferred);
        Assert.Equal(0, sim.EventsDropped);
        sim.BeginTick(++tick);
        Assert.Equal(MusouSim.DeathDrainPerTick, sim.Events.Count);   // the 512 drain
        Assert.All(sim.Events.Select((e, i) => (e, i)), x => { Assert.Equal(MusouEventKind.Death, x.e.Kind); Assert.Equal((ushort)(x.i + 1), x.e.Enemy); Assert.Equal(tick, x.e.Tick); });
        Assert.Equal(188, sim.DeathsWaiting);
        sim.BeginTick(++tick);
        Assert.Equal(188, sim.Events.Count);
        Assert.Equal((ushort)513, sim.Events[0].Enemy);
        Assert.Equal((ushort)700, sim.Events[^1].Enemy);
        Assert.Equal(0, sim.DeathsWaiting);
    }

    [Fact]
    public void The_drain_does_not_push_out_the_ticks_own_events()
    {
        var sim = Full(out uint tick);
        for (ushort k = 1; k <= 600; k++) sim.AddEvent(Death(k));
        sim.BeginTick(++tick);
        Assert.Equal(MusouSim.DeathDrainPerTick, sim.Events.Count);
        for (ushort k = 1; k <= MusouSim.MaxEventsPerTick; k++) sim.AddEvent(new MusouEvent(MusouEventKind.Telegraph, tick, k, 1, 0, Vector3.Zero));
        Assert.Equal(MusouSim.DeathDrainPerTick + MusouSim.MaxEventsPerTick, sim.Events.Count);   // all its own events fit
        Assert.Equal(0, sim.EventsDropped);
        sim.AddEvent(Hit(1, tick));   // past the tick's own room: dropped as before
        Assert.Equal(1, sim.EventsDropped);
    }

    [Fact]
    public void Only_a_death_waits_every_other_kind_is_dropped_over_the_cap()
    {
        var sim = Full(out _);
        sim.AddEvent(Hit(9));
        sim.AddEvent(new MusouEvent(MusouEventKind.Strike, 1, 9, 1, 0, Vector3.Zero));
        Assert.Equal(2, sim.EventsDropped);
        Assert.Equal(0, sim.DeathsWaiting);
    }

    [Fact]
    public void The_queue_holds_4096_and_a_death_past_that_is_dropped_and_counted()
    {
        var sim = Full(out _);
        for (int k = 1; k <= MusouSim.DeferredDeathsMax + 904; k++) sim.AddEvent(Death((ushort)(k % 2000 + 1)));
        Assert.Equal(MusouSim.DeferredDeathsMax, sim.DeathsWaiting);
        Assert.Equal(MusouSim.DeferredDeathsMax, sim.DeathsDeferred);
        Assert.Equal(904, sim.EventsDropped);
    }

    [Fact]
    public void Clear_forgets_the_queue_and_the_counts()
    {
        var sim = Full(out uint tick);
        for (ushort k = 1; k <= 300; k++) sim.AddEvent(Death(k));
        sim.Clear();
        Assert.Equal(0, sim.DeathsWaiting);
        Assert.Equal(0, sim.DeathsDeferred);
        Assert.Equal(0, sim.EventsDropped);
        sim.BeginTick(tick + 1);
        Assert.Empty(sim.Events);
    }
}
