namespace SmContent;

/// <summary>
/// musou/autocannon-flash (owner 2026-11-15: "leave only the bright yellow explosive flash"): which sprites of the autocannon's hit and blast effects are kept when only the bright flash is drawn: an additive
/// sprite named flash or streaks (the data's "1 frame flash", "ground flash", "flash", "streaks"). The fire ("fire", "flare", "hot cloud"), the fireball sprays, the smoke and dust and the debris are not.
/// </summary>
public static class BrightFlash
{
    public static bool Keeps(bool additive, bool distortion, string name) =>
        additive && !distortion && (name.Contains("flash", StringComparison.OrdinalIgnoreCase) || name.Contains("streaks", StringComparison.OrdinalIgnoreCase));
}
