using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/regen-loop-2: the stop events a behaviour tag owes (<see cref="TagStopTracker"/>), so a chainsword swing's endless motor layer ends with the swing.</summary>
public class TagStopTests
{
    const string Stop = "stop_sm_chainsword_swing";

    [Fact]
    public void A_start_owes_its_stop_once_when_the_action_ends()
    {
        var t = new TagStopTracker();
        t.Started(Stop, 1.0);
        t.Started(Stop, 1.5);   // a second swing before the first stop: still one stop owed
        Assert.Equal(1, t.Count);
        Assert.Equal([Stop], t.TakeAll());
        Assert.Empty(t.TakeAll());
    }

    [Fact]
    public void A_start_without_a_stop_event_owes_nothing()
    {
        var t = new TagStopTracker();
        t.Started("", 1.0);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void A_stop_not_released_by_the_clip_is_released_by_age()
    {
        var t = new TagStopTracker();
        t.Started(Stop, 10.0);
        Assert.Empty(t.TakeExpired(10.0 + TagStopTracker.MaxAgeSeconds - 0.1));
        Assert.Equal([Stop], t.TakeExpired(10.0 + TagStopTracker.MaxAgeSeconds));
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void A_death_clears_what_is_owed()
    {
        var t = new TagStopTracker();
        t.Started(Stop, 1.0);
        t.Clear();
        Assert.Empty(t.TakeAll());
    }
}
