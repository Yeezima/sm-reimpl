using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>Warfront's part of Musou's stranded-enemy failsafe (slice W13e): an Ork standing inside the ring it was sent to has arrived.</summary>
public sealed partial class MusouSim
{
    /// <summary>
    /// Musou counts an Ork as near (arrived) within 12 m of a target; Warfront's capture rings are 24 m (K) and 40 m (V), so an Ork holding its place in the ring was listed as stranded and carried off.
    /// Near when any alive goal target with a ring has it within that ring. Returns at once without the Warfront object. An Ork truly wedged inside a ring is no longer lifted out; it still counts in the ring.
    /// </summary>
    partial void WarfrontHeadwayNear(Vector3 position, ReadOnlySpan<MusouTarget> targets, ref bool near)
    {
        if (near || Warfront is null) return;
        foreach (var t in targets)
            if (t.Alive && t.Goal && t.Ring > 0f && Vector3.Distance(t.Position, position) <= t.Ring) { near = true; return; }
    }
}
