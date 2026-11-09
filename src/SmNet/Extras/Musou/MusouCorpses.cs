using System;

namespace SmNet.Extras.Musou;

/// <summary>
/// The corpse floor in Musou Mode (the owner, 2026-11-09: "ork corpses should never immediately disappear, same as guardsmen and allied space marines. they should always leave a corpse
/// for at least 10 seconds before disappearing."). Engine-free, so the client's body pools (<c>CrowdView</c> for the Orks and the Guardsmen, <c>MusouRagdolls</c>, the Marines' ragdolls)
/// and the tests share one rule. Every Ork, Guardsman and Marine that dies lies whole for at least <see cref="FloorSeconds"/> at any distance, then sinks; a pool full of corpses younger
/// than that grows (up to its hard ceiling, which is only a safety: <see cref="MakeRoom"/> reports a broken floor) instead of dropping one. Every number is ours (spec "Musou Mode: the corpse floor").
/// The client's <c>--musou-old-corpses</c> restores the rules before (3 s lying, the oldest first out of a 100-body pool, no body for an unseen death past 90 m, a lying ragdoll taken for a new throw).
/// </summary>
public static class MusouCorpseRules
{
    /// <summary>The least a corpse stays whole and in place (s, from the death; the owner's 10 s).</summary>
    public const float FloorSeconds = 10f;
    /// <summary>The seconds a body lies before it sinks: the floor, and before it (<c>--musou-old-corpses</c>) 3 s.</summary>
    public const float OldLieSeconds = 3f;
    /// <summary>Most bodies a crowd keeps whatever their age (a safety, never reached in the measured fights): the Orks' and the Guardsmen's.</summary>
    public const int OrkHardBodies = 3000, GuardHardBodies = 1000;
    /// <summary>Most bodies an aggregate army's loss gave before the floor (one section's): now every slot that went leaves one.</summary>
    public const int OldImpostorDeathsPerSection = 6;

    /// <summary>The seconds a body lies whole before it sinks.</summary>
    public static float LieSeconds(bool old) => old ? OldLieSeconds : FloorSeconds;

    /// <summary>
    /// Room for one more body in a pool of <paramref name="count"/> (in the order they were added, oldest first; <paramref name="ageOf"/>: seconds since that one's death): the index of the
    /// body to drop, or -1 to keep them all. Before the floor (<paramref name="old"/>): the oldest once <paramref name="soft"/> are kept. Now: under <paramref name="soft"/> none; past it the
    /// oldest one past the floor; with every one younger the pool grows up to <paramref name="hard"/>, and only there the oldest goes (<paramref name="floorBroken"/>).
    /// </summary>
    public static int MakeRoom(int count, Func<int, float> ageOf, int soft, int hard, bool old, out bool floorBroken)
    {
        floorBroken = false;
        if (old) return count >= soft ? 0 : -1;
        if (count < soft) return -1;
        for (int i = 0; i < count; i++) if (ageOf(i) >= FloorSeconds) return i;
        if (count < Math.Max(soft, hard)) return -1;
        floorBroken = true;
        return 0;
    }

    /// <summary>
    /// Whether a death leaves a whole body. A gibbed death (torn apart by the gore: the owner's rules of 2026-11-02 and 2026-11-08) leaves its pieces instead, but only when its burst is
    /// drawn (<paramref name="burstDrawn"/>); one too far for the gore keeps its body. Before the floor (<paramref name="old"/>) a death past 90 m outside the view (<paramref name="farUnseen"/>)
    /// left nothing, and a gibbed one nothing whether drawn or not.
    /// </summary>
    public static bool LeavesBody(bool gibbed, bool burstDrawn, bool farUnseen, bool old) =>
        old ? !farUnseen && !gibbed : !gibbed || !burstDrawn;

    /// <summary>Whether a death is handled at all past 90 m outside the view (its body, gore and ragdoll): now always; before the floor not.</summary>
    public static bool HandlesUnseenFar(bool old) => !old;

    /// <summary>Whether a lying ragdoll <paramref name="age"/> s after its throw may be taken for a new throw: only once past the floor (before it: any lying one).</summary>
    public static bool MayRetake(float age, bool old) => old || age >= FloorSeconds;

    /// <summary>Whether a Marine's corpse <paramref name="age"/> s old is kept although its player left the snapshot or the ragdoll cap is full: until it is past the floor.</summary>
    public static bool Hold(double age, bool old) => !old && age < FloorSeconds;

    /// <summary>The bodies an aggregate's loss of <paramref name="lost"/> slots in one section leaves: every one (before the floor at most 6).</summary>
    public static int ImpostorBodies(int lost, bool old) => Math.Max(0, old ? Math.Min(lost, OldImpostorDeathsPerSection) : lost);

    /// <summary>The distance within which an aggregate's loss leaves bodies: any now; before the floor the brief's 90 m (<paramref name="coreMetres"/>).</summary>
    public static float ImpostorBodiesWithin(float coreMetres, bool old) => old ? coreMetres : float.PositiveInfinity;
}
