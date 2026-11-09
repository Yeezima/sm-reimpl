using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The Librarian's sounds (Musou Mode only): original install events only, by name (the shared <c>In_Game_Default</c> bank, posted through the client's sound system so the data's mix, distance curves
/// and instance limits apply). *Original asset loaded*; which event plays which power is ours (the original has no playable psyker).
/// </summary>
public static class LibrarianSounds
{
    /// <summary>While the lightning is held: the power conduit's electric crackle (an ambience loop of the multiplayer and campaign maps; its start burst, then two looping layers).</summary>
    public const string Loop = "play_power_conduit_elec";
    /// <summary>The release tail: the same conduit's stop event, which plays its own discharge one-shot and stops the loop's layers 250 ms later.</summary>
    public const string Tail = "stop_power_conduit_elec";
    /// <summary>Each newly chained target: the psyker bolt's hit (the campaign Chaos psyker's; its two layers' own limits 2 and 3 apply too).</summary>
    public const string Zap = "play_psyker_bolt_hit";
    /// <summary>The Psychic Shockwave: the multiplayer reactive blast (the stomp's displacement layers with a blast; max 6 instances).</summary>
    public const string Shockwave = "play_reactive_blast";
    /// <summary>Psychic Stress reaching 100 (all powers locked): the Space Marine plasma weapons' overheat vent.</summary>
    public const string Locked = "play_sm_plasma_venting_overheat";

    /// <summary>Zaps one section may start, and at most this many a second in all (ours): a 40-target chain must not take 40 voices.</summary>
    public const int ZapsPerSection = 3;
    public const float ZapsPerSecond = 8f;
    /// <summary>A link's end within this of an end of the last section is the same target, not a new one (metres; ours: the Orks move under a metre between two sections).</summary>
    public const float SameTarget = 0.9f;

    /// <summary>
    /// The links of <paramref name="now"/> whose end is new (no end of <paramref name="before"/> within <see cref="SameTarget"/>), first ones first, at most <paramref name="max"/>, written to
    /// <paramref name="into"/>; returns how many. The first link of a chain that just started counts too (its first hit).
    /// </summary>
    public static int NewHits(IReadOnlyList<LibrarianLink> before, IReadOnlyList<LibrarianLink> now, Span<int> into, int max)
    {
        int n = 0;
        for (int k = 0; k < now.Count && n < Math.Min(max, into.Length); k++)
        {
            bool seen = false;
            foreach (var b in before)
                if (Vector3.DistanceSquared(b.To, now[k].To) <= SameTarget * SameTarget) { seen = true; break; }
            if (!seen) into[n++] = k;
        }
        return n;
    }
}

/// <summary>A token bucket for the zaps (<see cref="LibrarianSounds.ZapsPerSecond"/>, a burst of <see cref="LibrarianSounds.ZapsPerSection"/>): the voice limit across every Librarian.</summary>
public sealed class LibrarianZapBudget
{
    double _tokens = LibrarianSounds.ZapsPerSection, _at = double.NaN;

    /// <summary>Takes one zap at time <paramref name="now"/> (seconds) if the budget allows it.</summary>
    public bool Take(double now)
    {
        if (double.IsNaN(_at)) _at = now;
        _tokens = Math.Min(LibrarianSounds.ZapsPerSection, _tokens + Math.Max(0.0, now - _at) * LibrarianSounds.ZapsPerSecond);
        _at = now;
        if (_tokens < 1.0) return false;
        _tokens -= 1.0;
        return true;
    }
}

/// <summary>
/// The Librarian's gauge in the HUD (Musou Mode only), laid out as the weapon box's heat bar (he carries no gun, so the box is his): the Psychic Stress fills the bar as heat does, and while every power is
/// locked it blinks as an overheated weapon's bar (the same 14 rad/s pulse between 0.55 and 1); the Shockwave is the cell beside it, its cooldown as a draining bar (the Warp Pull's cell went with the power, owner 2026-11-10).
/// Layout and words are ours.
/// </summary>
public static class LibrarianHudModel
{
    /// <summary>The bar's fill, 0..1.</summary>
    public static float StressFill(float stress) => Math.Clamp(stress / LibrarianRules.StressMax, 0f, 1f);

    /// <summary>A power's cell: 1 when ready, else the part of its cooldown already done.</summary>
    public static float CooldownFill(float left, float cooldown) => left <= 0f ? 1f : Math.Clamp(1f - left / MathF.Max(cooldown, 1e-3f), 0f, 1f);

    /// <summary>The overheat blink's brightness at <paramref name="time"/> while locked (Hud.Panels' heat bar), else 1.</summary>
    public static float Blink(bool locked, double time) => locked ? 0.55f + 0.45f * MathF.Sin((float)time * 14f) : 1f;

    /// <summary>A power's line: its name, then READY, LOCKED or the seconds left.</summary>
    public static string CellText(string name, float left, bool locked) => locked ? $"{name}  LOCKED" : left > 0f ? $"{name}  {left.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}" :$"{name}  READY";
}
