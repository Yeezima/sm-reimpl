using SmContent;
using SmFormats.Fx;

namespace SmFormats.Tests;

/// <summary>F-D1: an effect whose elements all have a duration of 0 is read over its play's length (<see cref="FxDurations"/>); one with a duration of its own is left alone. Synthetic systems only.</summary>
public class FxDurationsTests
{
    static FxSystem Zero(float? ringDuration = null) => FxSystem.Read("test_zero", SyntheticFxWriter.ZeroDurationSystem(ringDuration));

    [Fact]
    public void A_system_whose_elements_all_have_no_duration_takes_the_plays_length()
    {
        var sys = Zero();
        Assert.All(sys.ElementsWithCollisions, e => Assert.Equal(0f, e.Duration));
        Assert.Equal(1f, FxDurations.LengthDriven(sys, 1f));
        Assert.Equal(0.6f, FxDurations.LengthDriven(sys, 0.6f));
        Assert.All(sys.ElementsWithCollisions, e => Assert.Equal(1f, FxDurations.DurationOf(e, 1f)));
    }

    [Fact]
    public void The_lifetime_is_recomputed_from_the_elements()
    {
        var sys = Zero();
        // A ring lasts 1 s + its 0.05 s epsilon; the spray adds that to its own: 2.1 s; the light 1.05 s.
        float eps = sys.Emitters[0].Elements[0].DurationEpsilon;
        Assert.Equal(0.05f, eps, 4);
        Assert.Equal(2f * (1f + eps), FxDurations.Lifetime(sys, 1f)!.Value, 4);
        Assert.Equal(1f + eps, FxDurations.LifeOf(sys.Emitters[0].Elements[1], 1f), 4);
        // Without a length the system's own lifetime stands.
        Assert.Equal(sys.Lifetime, FxDurations.Lifetime(sys, 0f));
    }

    [Fact]
    public void A_system_with_one_element_of_its_own_duration_stays_unchanged()
    {
        var sys = Zero(ringDuration: 0.3f);
        Assert.Equal(0f, FxDurations.LengthDriven(sys, 1f));
        var zero = FxDurations.LengthDriven(sys, 1f);
        // Every element keeps its duration (the zero ones stay zero) and the lifetime is the system's own.
        Assert.All(sys.ElementsWithCollisions, e => Assert.Equal(e.Duration, FxDurations.DurationOf(e, zero)));
        Assert.Equal(sys.Lifetime, FxDurations.Lifetime(sys, zero));
    }

    [Fact]
    public void A_system_with_durations_throughout_and_a_non_positive_length_are_never_re_timed()
    {
        var tracer = FxSystem.Read("test_tracer", SyntheticFxWriter.TracerSystem());
        Assert.Equal(0f, FxDurations.LengthDriven(tracer, 1f));
        Assert.Equal(0f, FxDurations.LengthDriven(Zero(), 0f));
        Assert.Equal(0f, FxDurations.LengthDriven(Zero(), -1f));
    }
}
