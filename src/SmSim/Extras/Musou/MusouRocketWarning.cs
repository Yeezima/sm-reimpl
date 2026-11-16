using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>A rocket as a client draws it (expansion S2): its serial, where it is and its velocity.</summary>
public readonly record struct MusouRocketTrack(ushort Serial, Vector3 Position, Vector3 Velocity);

/// <summary>
/// The client's "incoming" warning for the Rokkit Boyz' rockets (expansion S2; ours, owner brief 2026-11-07): one subtle directional cue for the single most threatening rocket, starting
/// <see cref="MusouRokkitRules.WarningLeadSeconds"/> before it would pass nearest the listener within <see cref="MusouRokkitRules.WarningMissDistance"/>. Never stacked: while a warning
/// plays, no other starts, and every rocket that was threatening meanwhile counts as warned (it never warns later). Pure logic (no engine), so it is tested on its own.
/// </summary>
public sealed class MusouRocketWarner
{
    readonly HashSet<ushort> _warned = new();
    double _until = double.NegativeInfinity;

    /// <summary>The rocket the warning plays for now (0: none).</summary>
    public ushort Active { get; private set; }
    /// <summary>Warnings started since the start.</summary>
    public int Started { get; private set; }

    /// <summary>
    /// When a rocket of <paramref name="rockets"/> would pass within the miss distance of <paramref name="listener"/>, and how near (seconds, metres); null when it never comes nearer than now
    /// or misses by more.
    /// </summary>
    public static (float Seconds, float Miss)? Threat(Vector3 listener, in MusouRocketTrack rocket)
    {
        float speed2 = rocket.Velocity.LengthSquared();
        if (!(speed2 > 1e-6f)) return null;
        var to = listener - rocket.Position;
        float t = Vector3.Dot(to, rocket.Velocity) / speed2;
        if (t <= 0f) return null;
        float miss = Vector3.Distance(rocket.Position + rocket.Velocity * t, listener);
        return miss <= MusouRokkitRules.WarningMissDistance ? (t, miss) : null;
    }

    /// <summary>
    /// One frame at <paramref name="now"/> (seconds): the serial of a rocket whose warning starts this frame (0: none). The warning lasts until that rocket's nearest pass plus a short tail.
    /// </summary>
    public ushort Update(double now, Vector3 listener, ReadOnlySpan<MusouRocketTrack> rockets)
    {
        bool busy = now < _until;
        if (!busy) Active = 0;
        ushort pick = 0; float soonest = float.MaxValue;
        foreach (var r in rockets)
        {
            if (Threat(listener, r) is not { } threat || threat.Seconds > MusouRokkitRules.WarningLeadSeconds) continue;
            if (busy) { _warned.Add(r.Serial); continue; }   // swallowed by the warning playing now
            if (_warned.Contains(r.Serial)) continue;
            if (threat.Seconds < soonest) { soonest = threat.Seconds; pick = r.Serial; }
        }
        if (pick == 0) return 0;
        // The others threatening now are swallowed by this one.
        foreach (var r in rockets) if (Threat(listener, r) is { } t && t.Seconds <= MusouRokkitRules.WarningLeadSeconds) _warned.Add(r.Serial);
        Active = pick;
        Started++;
        _until = now + soonest + 0.3;
        if (_warned.Count > 512) _warned.Clear();
        return pick;
    }
}
