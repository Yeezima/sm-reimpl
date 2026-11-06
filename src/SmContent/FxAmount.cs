namespace SmContent;

/// <summary>
/// gore-original (ours): the effect player's amount factor (<c>FxEffect.Amount</c>), engine-free. Musou Mode's blood sets it (the owner's 1.5x); every other effect, classic PvP's
/// included, keeps 1 and so the data's own particle counts.
/// </summary>
public static class FxAmount
{
    /// <summary>The most copies of one particle (a bound on the cost of a large factor).</summary>
    public const int MaxCopies = 8;

    /// <summary>
    /// How many copies of each particle a spray draws for an amount factor: all of the whole part, the fractional part by the seeded roll (0..1). 1 for a factor of 1 or less, or
    /// not finite (the data's own count).
    /// </summary>
    public static int Copies(float factor, float roll)
    {
        if (!(factor > 1f) || !float.IsFinite(factor)) return 1;
        int whole = (int)MathF.Floor(factor);
        return Math.Min(MaxCopies, whole + (roll < factor - whole ? 1 : 0));
    }
}
