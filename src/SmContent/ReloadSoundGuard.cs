using System.Collections.Generic;

namespace SmContent;

/// <summary>
/// One reload, one set of sounds (owner, 2026-10-30; ui/hud-camera-2): a reload start seen again while the same weapon slot's reload is still running schedules nothing; a reload that ended early
/// (a switch, a melee attack, a death) is <see cref="Cancel"/>led, so the next reload of that slot or of another weapon plays normally and the cancelled reload's pending sounds are dropped
/// (<see cref="Current"/>). Keyed by weapon slot; <see cref="Start"/> returns the reload's instance number.
/// </summary>
public sealed class ReloadSoundGuard
{
    readonly Dictionary<int, (double Until, int Instance)> _slots = new();
    int _next;

    /// <summary>The instance number of the new reload, or null when this slot's reload (started less than <paramref name="length"/> ago) already has its sounds.</summary>
    public int? Start(int slot, double now, float length)
    {
        if (_slots.TryGetValue(slot, out var s) && now < s.Until) return null;
        _slots[slot] = (now + length, ++_next);
        return _next;
    }

    /// <summary>The slot's reload ended early: its sounds stop and a new reload may start at once.</summary>
    public void Cancel(int slot) => _slots.Remove(slot);

    /// <summary>
    /// The reload's sounds still to play when its start is seen <paramref name="elapsed"/> seconds into a reload of <paramref name="length"/> seconds (sound/mix-and-reloads): each tag
    /// (fraction of the reload) with its delay from now; tags already passed (more than <paramref name="tolerance"/> ago) are left out, so a reload picked up again after a
    /// prediction correction plays its remaining sounds once instead of starting them all over on top of the ones already playing.
    /// </summary>
    public static List<(string Tag, double Delay)> Remaining(IReadOnlyList<(string Tag, float At)> tags, float length, float elapsed, double tolerance)
    {
        var due = new List<(string, double)>();
        foreach (var (tag, at) in tags)
        {
            double delay = at * length - Math.Max(0f, elapsed);
            if (delay < -tolerance) continue;
            due.Add((tag, Math.Max(0.0, delay)));
        }
        return due;
    }

    /// <summary>Whether <paramref name="instance"/> is still the slot's running reload (not cancelled, not replaced).</summary>
    public bool Current(int slot, int instance) => _slots.TryGetValue(slot, out var s) && s.Instance == instance;
}
