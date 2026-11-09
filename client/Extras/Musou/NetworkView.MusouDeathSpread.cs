using System;
using System.Collections.Generic;
using Godot;
using SmNet.Extras.Musou;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>The client's hand-out of the Musou deaths (night batch 2026-11-06; ours, spec "Musou Mode: a burst of deaths spread over frames").</summary>
public static class MusouDeathRules
{
    /// <summary>
    /// Deaths given their body, ragdoll, gore and sound in one frame, the nearest to the camera first; of them at most <see cref="DollsPerFrame"/> thrown as ragdolls; and the
    /// longest a death waits (s) before it is handed out whatever the counts. The owner, 2026-11-06: "when a grenade explodes many orks die instantaneously ... remove the moment
    /// of extreme frame time"; the deaths may start up to about 100 ms apart. A waiting death keeps its place in the crowd, already playing its death clip, so nothing blinks.
    /// </summary>
    public const int PerFrame = 24, DollsPerFrame = 5;
    public const float MaxWaitSeconds = 0.1f;
    /// <summary>
    /// The most a frame hands out even of deaths that have waited their most, and of them thrown as ragdolls (late batch 2026-11-06, stream D: "a grenade that kills many Orks still hitches").
    /// Without it a burst bigger than <see cref="MaxWaitSeconds"/> of <see cref="PerFrame"/> (three big frags in a crowd of 400 kill about 280, measured on the server) came out in one frame once
    /// due, and at a slow frame rate the whole burst did (200 deaths over 2 frames at about 10 fps); now 280 deaths take at least 6 frames. <c>--musou-death-deadline-all</c> restores the old rule.
    /// </summary>
    public const int DuePerFrame = 48, DueDollsPerFrame = 8;
}

public partial class NetworkView
{
    /// <summary>Test aid: <c>--musou-old-deaths</c> hands every death out in the frame it arrives, as before the night batch of 2026-11-06 (the death-spike test's A/B run switches it per burst).</summary>
    static readonly bool MusouOldDeathsFlag = CmdFlag("musou-old-deaths");
    /// <summary>Test aid: <c>--musou-death-deadline-all</c> hands out every death that has waited its most in that frame, whatever the count (the night batch's rule, before stream D's <see cref="MusouDeathRules.DuePerFrame"/>).</summary>
    static readonly bool MusouDeathDeadlineAllFlag = CmdFlag("musou-death-deadline-all");
    bool _musouDeathDeadlineAll = MusouDeathDeadlineAllFlag;
    bool _musouOldDeaths = MusouOldDeathsFlag;

    readonly record struct WaitingDeath(MusouDeathShown Death, double Arrived);
    readonly List<WaitingDeath> _musouDeathQueue = new();
    readonly List<(WaitingDeath W, float Metres)> _musouDeathOrder = new();
    readonly Dictionary<ushort, float> _musouDeathAge = new();
    /// <summary>The deaths handed out this frame, for their sounds (cleared by the sounds' frame).</summary>
    readonly List<MusouDeathShown> _musouDeathsHanded = new();
    double _musouDeathNow;

    /// <summary>The deaths the client was told of this frame join the queue (with <c>--musou-old-deaths</c>: straight to this frame's list).</summary>
    void MusouDeathsArrive(IReadOnlyList<MusouDeathShown> deaths, double now)
    {
        _musouDeathNow = now;
        if (_musouOldDeaths) foreach (var d in deaths) _musouDeaths.Add(d);
        else foreach (var d in deaths) _musouDeathQueue.Add(new WaitingDeath(d, now));
        // The corpse floor (2026-11-09): the deaths waiting are bounded by the crowd's hard ceiling, not its 100 bodies (a grenade's 280 deaths lost all but the last 100: no body at all);
        // the queue hands out at least 48 a frame, so it drains within a few frames. --musou-old-corpses: the 100.
        int most = CrowdView.OldCorpses ? MusouCrowd.MaxBodies : MusouCrowd.HardBodies;
        if (_musouDeaths.Count > most) _musouDeaths.RemoveRange(0, _musouDeaths.Count - most);
        if (_musouDeathQueue.Count > most) _musouDeathQueue.RemoveRange(0, _musouDeathQueue.Count - most);
    }

    /// <summary>Whether this death would be thrown as a ragdoll (<see cref="MusouThrow"/>'s test): the costly kind, a pool doll woken into the physics.</summary>
    bool MusouDeathIsDoll(in MusouDeathShown d, float metres) =>
        _musouRagdolls is not null && MusouGoreRules.Thrown(d.Cause) && metres <= MusouRagdolls.Metres && MusouRagdolls.Fits(d.Type);

    /// <summary>
    /// This frame's share of the queue into <see cref="_musouDeaths"/>, the nearest first: up to <see cref="MusouDeathRules.PerFrame"/>, of them up to
    /// <see cref="MusouDeathRules.DollsPerFrame"/> ragdolls, and every one that has waited its most.
    /// </summary>
    void MusouDeathsTake(Vector3 camera)
    {
        if (_musouDeathQueue.Count == 0) return;
        _musouDeathOrder.Clear();
        foreach (var w in _musouDeathQueue) _musouDeathOrder.Add((w, new Vector3(w.Death.Position.X, w.Death.Position.Y, w.Death.Position.Z).DistanceTo(camera)));
        _musouDeathOrder.Sort(static (a, b) => a.Metres.CompareTo(b.Metres));
        _musouDeathQueue.Clear();
        int given = 0, dolls = 0, candidates = 0, available = _musouRagdolls?.Available ?? 0;
        foreach (var (w, metres) in _musouDeathOrder)
        {
            // Only as many as the pool can take become dolls (the nearest); the rest tumble as crowd bodies, which cost little and go out at once.
            bool doll = MusouDeathIsDoll(w.Death, metres) && ++candidates <= available;
            bool due = _musouDeathNow - w.Arrived >= MusouDeathRules.MaxWaitSeconds;
            bool room = due
                ? _musouDeathDeadlineAll || (given < MusouDeathRules.DuePerFrame && (!doll || dolls < MusouDeathRules.DueDollsPerFrame))   // stream D: even the overdue come a frame's worth at a time
                : given < MusouDeathRules.PerFrame && (!doll || dolls < MusouDeathRules.DollsPerFrame);
            if (room)
            {
                _musouDeaths.Add(w.Death);
                _musouDeathAge[w.Death.Id] = (float)(_musouDeathNow - w.Arrived);
                given++;
                if (doll) dolls++;
            }
            else _musouDeathQueue.Add(w);
        }
    }

    /// <summary>The deaths still waiting are drawn where they fell, already dying (their clip from their arrival), so the crowd shows every death at once.</summary>
    void MusouDeathsWaitingItems()
    {
        if (_musouCrowd is not { } crowd) return;
        crowd.Dying.Clear();
        foreach (var w in _musouDeathQueue) crowd.Dying.Add((w.Death, (float)(_musouDeathNow - w.Arrived)));
    }

    bool _musouDollsWarm;

    /// <summary>One doll a frame dressed ahead of need in a thrown look (<see cref="MusouRagdolls.Prewarm"/>), until every doll wears every built look of the boy's rig; <c>--musou-old-deaths</c>: not done.</summary>
    void MusouRagdollsPrewarm(MusouRagdolls dolls)
    {
        if (_musouDollsWarm || MusouOldDeathsFlag || _musouLooks is null) return;
        bool pending = false;
        foreach (var type in Enum.GetValues<MusouEnemyType>())
        {
            if (!MusouRagdolls.Fits(type)) continue;
            if (_musouLooks.For(type) is not { } look) { pending = true; continue; }   // not built yet
            if (dolls.Prewarm(look)) return;
        }
        if (!pending && dolls.Built == MusouRagdolls.Dolls) { _musouDollsWarm = true; GD.Print("musou: ragdoll pool dressed in every thrown look"); }
    }

    /// <summary>The seconds a death handed out this frame waited (its body starts its clip that far in); zero for one not delayed.</summary>
    float MusouDeathWaited(ushort id) => _musouDeathAge.TryGetValue(id, out float age) ? age : 0f;
}
