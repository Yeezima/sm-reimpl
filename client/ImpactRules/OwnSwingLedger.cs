using System;
using System.Collections.Generic;

namespace SmClient.ImpactRules;

/// <summary>
/// sound/original-hits: which of the own player's melee hit sounds a server hit report still owes. The own swing sounds at its hit time (<see cref="Strike"/>); the server's report of that
/// swing arrives a round trip later (it names only the last victim: <c>HitReport.LastVictim</c>). Wall-time windows cannot tell the report of swing 1 from swing 2's when the round trip is as long
/// as a combo step, so each swing keeps its own sounded victims, and a report is matched to a swing, never to a clock:
/// <list type="number">
/// <item>A report for a victim a swing sounded and no report has confirmed yet is that swing's confirmation: silent. The oldest such swing at or after the open one wins; swings before it are closed.</item>
/// <item>Any other report is a victim the swing did not sound for (a target the strike did not pick, or a swing that found nobody): it belongs to the open swing (the oldest not closed). Sounded there
/// as the swing's first target (primary) when it has sounded nobody, else as a further target (secondary). A victim the swing has already sounded is never sounded twice.</item>
/// <item>With no swing in memory (the entries age out after <see cref="Memory"/> seconds, at most <see cref="Capacity"/> are kept) the report starts a swing of the strike the caller names.</item>
/// </list>
/// Interpretation (ours): the wire carries no swing id, so a report whose victim no swing sounded goes to the open swing.
/// </summary>
public sealed class OwnSwingLedger
{
    /// <summary>Seconds a swing stays in the ledger (ours; far past any round trip).</summary>
    public const double Memory = 6.0;
    public const int Capacity = 8;

    /// <summary>What to do with a report: <see cref="Sound"/> false: nothing (already sounded at the strike).</summary>
    public readonly record struct Verdict(bool Sound, bool Secondary, bool Heavy, int Step);

    sealed class Swing
    {
        public double At;
        public bool Heavy;
        public int Step;
        public readonly HashSet<int> Sounded = new();
        public readonly HashSet<int> Unconfirmed = new();
    }

    readonly List<Swing> _swings = new();
    int _open;   // index of the oldest swing not closed

    public int Count => _swings.Count;

    /// <summary>
    /// The own swing's hit time has passed. <paramref name="sounded"/>: the strike posted a sound for <paramref name="victim"/> (an enemy in reach); false: nobody was in reach (the swing still
    /// takes the next report). The strike is what <see cref="Verdict"/> will report back for this swing's secondary targets.
    /// </summary>
    public void Strike(double now, bool heavy, int step, bool sounded, int victim)
    {
        Prune(now);
        var s = new Swing { At = now, Heavy = heavy, Step = step };
        if (sounded) { s.Sounded.Add(victim); s.Unconfirmed.Add(victim); }
        _swings.Add(s);
        while (_swings.Count > Capacity) { _swings.RemoveAt(0); if (_open > 0) _open--; }
    }

    /// <summary>A server hit report for <paramref name="victim"/> arrived at <paramref name="now"/>; <paramref name="heavy"/> and <paramref name="step"/> are the strike to use when no swing is in memory.</summary>
    public Verdict Report(double now, int victim, bool heavy, int step)
    {
        Prune(now);
        for (int i = _open; i < _swings.Count; i++)
            if (_swings[i].Unconfirmed.Remove(victim)) { _open = i; return new Verdict(false, false, _swings[i].Heavy, _swings[i].Step); }
        if (_swings.Count == 0 || _open >= _swings.Count)
        {
            var fresh = new Swing { At = now, Heavy = heavy, Step = step };
            fresh.Sounded.Add(victim);
            _swings.Add(fresh);
            _open = _swings.Count - 1;
            return new Verdict(true, false, heavy, step);
        }
        // The swing whose report is outstanding (the oldest from the open one that still awaits a confirmation), else the open one itself.
        int at = _open;
        for (int i = _open; i < _swings.Count; i++) if (_swings[i].Unconfirmed.Count > 0) { at = i; break; }
        _open = at;
        var open = _swings[at];
        if (!open.Sounded.Add(victim)) return new Verdict(false, false, open.Heavy, open.Step);   // already sounded in this swing
        return new Verdict(true, open.Sounded.Count > 1, open.Heavy, open.Step);
    }

    void Prune(double now)
    {
        int drop = 0;
        while (drop < _swings.Count && now - _swings[drop].At > Memory) drop++;
        if (drop == 0) return;
        _swings.RemoveRange(0, drop);
        _open = Math.Max(0, _open - drop);
    }
}
