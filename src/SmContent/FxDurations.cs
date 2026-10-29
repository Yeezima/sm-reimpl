using SmFormats.Fx;

namespace SmContent;

/// <summary>
/// The engine-free rule behind <c>CSystem.From(sys, zeroDuration)</c> (F-D1, **interpretation**): an effect whose elements all have <c>Duration</c> 0 (the death effects
/// <c>plasma_limb_vanish</c>, <c>jumppack_dmg_burn_limb</c>, <c>jumppack_death_mp</c>, <c>jumppack_predeath</c>) is driven by the <c>length</c> its <c>play_fx</c> names, so each of its elements
/// lasts that long. A system with any element that has a duration of its own keeps its timing: the effects other weapons and hits share are never re-timed.
/// </summary>
public static class FxDurations
{
    /// <summary>
    /// The seconds an element of <paramref name="sys"/> with a duration of 0 lasts: <paramref name="length"/> when every element (collision descriptions included) has a duration of 0
    /// and <paramref name="length"/> is above 0, else 0 (no change).
    /// </summary>
    public static float LengthDriven(FxSystem sys, float length) =>
        length > 0f && !sys.ElementsWithCollisions.Any(e => e.Duration > 0f) ? length : 0f;

    /// <summary>The duration an element is compiled with: its own, or <paramref name="zero"/> when its own is 0 and <paramref name="zero"/> is above 0.</summary>
    public static float DurationOf(FxElement el, float zero) => el.Duration <= 0f && zero > 0f ? zero : el.Duration;

    /// <summary>How long an element shows with its children and collision content: its duration, its epsilon and the longest of what it holds (the compiled nodes' <c>Life</c>).</summary>
    public static float LifeOf(FxElement el, float zero) =>
        DurationOf(el, zero) + MathF.Max(0f, el.DurationEpsilon)
        + (el.Children.Count > 0 || el.OnCollision.Count > 0 ? el.Children.Concat(el.OnCollision).Max(k => LifeOf(k, zero)) : 0f);

    /// <summary>
    /// The system's lifetime when elements of duration 0 last <paramref name="zero"/>: the system's own (<see cref="FxSystem.Lifetime"/>) when <paramref name="zero"/> is 0 or the system loops (null),
    /// else the longest root element's <see cref="LifeOf"/>.
    /// </summary>
    public static float? Lifetime(FxSystem sys, float zero)
    {
        if (zero <= 0f || sys.Lifetime is null) return sys.Lifetime;
        var roots = sys.Emitters.Where(e => !e.Hide && !e.Screenspace).SelectMany(e => e.Elements).ToList();
        return roots.Count == 0 ? 0f : roots.Max(r => LifeOf(r, zero));
    }
}
