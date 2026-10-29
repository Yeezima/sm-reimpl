using SmContent;

namespace SmFormats.Tests;

/// <summary>sound/shield-regen: the own shield's sound triggers (<see cref="ShieldSoundTracker"/>), frame by frame as the client sees them.</summary>
public class ShieldSoundTests
{
    const float Max = 100f;

    static List<ShieldSoundEvents> Run(ShieldSoundTracker t, params (float Shield, bool Alive)[] frames)
    {
        var list = new List<ShieldSoundEvents>();
        for (int i = 1; i < frames.Length; i++) list.Add(t.Step(frames[i - 1].Shield, frames[i].Shield, Max, frames[i - 1].Alive, frames[i].Alive));
        return list;
    }

    [Fact]
    public void A_regeneration_starts_once_even_when_the_frame_rate_repeats_values()
    {
        var t = new ShieldSoundTracker();
        // Broken, then the 7 s delay (flat), then rising at the 30 Hz tick shown at 60 fps: every other frame repeats the value.
        var events = Run(t, (40f, true), (0f, true), (0f, true), (0f, true), (1.1f, true), (1.1f, true), (2.2f, true), (2.2f, true), (3.3f, true));
        Assert.Equal(ShieldSoundEvents.Broken, events[0]);
        Assert.Equal(1, events.Count(e => e.HasFlag(ShieldSoundEvents.RegenStart)));
        Assert.Equal(ShieldSoundEvents.RegenStart, events[3]);
        Assert.True(t.Regenerating);
    }

    [Fact]
    public void A_regeneration_from_a_damaged_shield_starts_too_and_ends_when_full()
    {
        var t = new ShieldSoundTracker();
        var events = Run(t, (100f, true), (61.5f, true), (61.5f, true), (62.6f, true), (80f, true), (99.995f, true), (100f, true), (100f, true));
        Assert.Equal(ShieldSoundEvents.Hit, events[0]);
        Assert.Equal(ShieldSoundEvents.RegenStart, events[2]);
        Assert.Equal(ShieldSoundEvents.Restored, events[4]);       // 99.995 is within the 0.01 margin of full
        Assert.Equal(ShieldSoundEvents.None, events[5]);           // already full: no second restore
        Assert.Equal(1, events.Count(e => e.HasFlag(ShieldSoundEvents.RegenStart)));
        Assert.False(t.Regenerating);
    }

    [Fact]
    public void Damage_interrupts_a_regeneration_and_the_next_rise_is_a_new_one()
    {
        var t = new ShieldSoundTracker();
        var events = Run(t, (0f, true), (1f, true), (5f, true), (0f, true), (0f, true), (1f, true), (2f, true));
        Assert.Equal(ShieldSoundEvents.RegenStart, events[0]);
        Assert.Equal(ShieldSoundEvents.Broken | ShieldSoundEvents.RegenInterrupted, events[2]);
        Assert.Equal(ShieldSoundEvents.RegenStart, events[4]);
        Assert.Equal(2, events.Count(e => e.HasFlag(ShieldSoundEvents.RegenStart)));
    }

    [Fact]
    public void Death_and_spawn_are_life_changes_not_regenerations()
    {
        var t = new ShieldSoundTracker();
        var events = Run(t, (40f, true), (0f, true), (0f, false), (0f, false), (100f, true), (100f, true));
        Assert.Equal(ShieldSoundEvents.Broken, events[0]);
        Assert.Equal(ShieldSoundEvents.LifeChanged, events[1]);   // died with the shield down: the client ends the warning loop
        Assert.Equal(ShieldSoundEvents.None, events[2]);
        Assert.Equal(ShieldSoundEvents.LifeChanged, events[3]);   // the spawn's full shield is no regeneration and no restore
        Assert.Equal(ShieldSoundEvents.None, events[4]);
        Assert.False(t.Regenerating);
    }

    [Fact]
    public void A_full_shield_never_starts_a_regeneration()
    {
        var t = new ShieldSoundTracker();
        Assert.All(Run(t, (100f, true), (100f, true), (100f, true)), e => Assert.Equal(ShieldSoundEvents.None, e));
        // An Iron Halo maximum of 150: 100 is not full.
        Assert.Equal(ShieldSoundEvents.RegenStart, t.Step(100f, 101f, 150f, true, true));
        Assert.Equal(ShieldSoundEvents.Restored, t.Step(149f, 150f, 150f, true, true));
    }
}
