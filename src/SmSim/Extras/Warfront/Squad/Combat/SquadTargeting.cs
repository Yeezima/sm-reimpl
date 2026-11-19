using System.Numerics;

namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// A retainer's target choice with memory (Warfront retainer AI, slice S1; owner 2026-11-19: fight like the Classic bots, not flip to whichever Ork is nearest every three ticks). The pieces are pure so a test can pin them;
/// <c>WarfrontSquadSim.PickTarget</c> applies them. All values are ours (estimates).
/// <list type="bullet">
/// <item>An Ork that threatens the owner or a downed squad-mate (within <see cref="ThreatRadius"/> of one) is preferred over any other.</item>
/// <item>A held target is kept for <see cref="HoldSeconds"/> unless a new one threatens the owner (and the held one does not) or is clearly nearer (under <see cref="HoldNearerMargin"/> of the held distance); after that a new one needs only to be under <see cref="NearerMargin"/> of it.</item>
/// </list>
/// </summary>
public static class SquadTargeting
{
    /// <summary>An Ork this close to the owner or to a downed Marine threatens him (metres) [ours, estimate].</summary>
    public const float ThreatRadius = 6f;
    /// <summary>A target is held at least this long unless a threat or a much nearer one appears (seconds) [ours, estimate; the owner's brief: about 1 s].</summary>
    public const float HoldSeconds = 1f;
    /// <summary>The share of the held target's distance a new one must be under to replace it: within the hold, and after it [ours, estimate].</summary>
    public const float HoldNearerMargin = 0.5f, NearerMargin = 0.75f;

    /// <summary>Whether <paramref name="enemy"/> is within <see cref="ThreatRadius"/> (flat) of any of the guarded places (the owner, the downed squad-mates).</summary>
    public static bool Threatens(Vector3 enemy, ReadOnlySpan<Vector3> guarded)
    {
        foreach (var g in guarded)
        {
            float dx = enemy.X - g.X, dy = enemy.Y - g.Y;
            if (dx * dx + dy * dy <= ThreatRadius * ThreatRadius) return true;
        }
        return false;
    }

    /// <summary>Whether a new candidate replaces the held target (see the class).</summary>
    public static bool PreferNew(float heldSeconds, float heldDistance, bool heldThreatens, float newDistance, bool newThreatens)
    {
        if (newThreatens && !heldThreatens) return true;
        if (heldThreatens && !newThreatens) return false;
        float margin = heldSeconds < HoldSeconds ? HoldNearerMargin : NearerMargin;
        return newDistance < heldDistance * margin;
    }
}
