namespace SmContent;

/// <summary>
/// The stop events of the sounds a behaviour tag started (sound/regen-loop-2). A <c>post_sound_event</c> command names a start and a stop event; the chainsword's
/// <c>swing_x1</c> starts <c>play_sm_chainsword_swing_x1</c>, whose motor layer is an endless continuous container, and names <c>stop_sm_chainsword_swing</c>. The stop
/// belongs at the end of the action. Before this tracker the client never posted it: every swing added an endless chain that only a death ended (a headless run
/// showed 40 continuations a second and the voice cap reached). The client records each stop when it posts the start and releases them when the clip that fired
/// the tag ends, restarts or changes, and at the latest after <see cref="MaxAgeSeconds"/>.
/// </summary>
public sealed class TagStopTracker
{
    /// <summary>No tag-started sound outlives this (ours: the longest melee clip is about 1.5 s).</summary>
    public const double MaxAgeSeconds = 3.0;

    readonly List<(string Stop, double At)> _open = new();

    public int Count => _open.Count;

    /// <summary>A start was posted; its stop event is owed. The same stop already owed is refreshed, not duplicated.</summary>
    public void Started(string stopEvent, double now)
    {
        if (stopEvent.Length == 0) return;
        for (int i = 0; i < _open.Count; i++)
            if (_open[i].Stop == stopEvent) { _open[i] = (stopEvent, now); return; }
        _open.Add((stopEvent, now));
    }

    /// <summary>The action ended: every owed stop, once each.</summary>
    public List<string> TakeAll()
    {
        var list = _open.Select(o => o.Stop).ToList();
        _open.Clear();
        return list;
    }

    /// <summary>Stops older than <see cref="MaxAgeSeconds"/>.</summary>
    public List<string> TakeExpired(double now)
    {
        var list = new List<string>();
        for (int i = _open.Count - 1; i >= 0; i--)
            if (now - _open[i].At >= MaxAgeSeconds) { list.Insert(0, _open[i].Stop); _open.RemoveAt(i); }
        return list;
    }

    /// <summary>A death or a leave: the sounds are ended another way, nothing is owed.</summary>
    public void Clear() => _open.Clear();
}
