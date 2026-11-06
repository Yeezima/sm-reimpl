using System;
using System.Collections.Generic;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// The toss follow-through (stream B of the owner's batch of 2026-11-06 late: "let the grenade throw animation play out a little longer for both sides"; client presentation only,
/// no simulation, wire or config-hash change). Since feel/grenades-quickdraw the simulation ends a throw at its release (0.1 s), so the toss clip was cut at the hand opening
/// (clip time 0.2 s of its 0.667 s) and the arm blended out at once. Now, for every player (own and others', Space Marines and Chaos), the toss clip plays on after the throw ends
/// at its own speed, to the toss graph's <c>BRANCH_UseEquipment_End</c> (<see cref="TossClipEnd"/>, 0.667 s: the 0.467 s after the release that the data's throw had), as the
/// original's throw state played it out. It is presentation only: the player is free from the release as before, and the follow-through gives way at once to anything that shows
/// an action of its own (<see cref="ThrowFollowBusy"/>: a shot, a charge, a melee attack, aim, a switch, a reload, a jump, an evade, a reaction, the charge, a brace, a vent, a
/// sprint, death, a new throw). While the body moves the legs keep the locomotion and the toss is laid over the upper body (<see cref="HandoffThrow"/>), as during the throw.
/// The length is the data's; that the original played the rest of the clip after a release this early is an interpretation. <c>--old-throw-end</c> (test aid): the cut at the release.
/// </summary>
public partial class NetworkView
{
    bool? _oldThrowEnd;
    /// <summary><c>--old-throw-end</c> (test aid): no follow-through, the toss ends with the simulation's throw (before stream B).</summary>
    bool OldThrowEnd => _oldThrowEnd ??= CmdFlag("old-throw-end");

    sealed class ThrowFollowTrack
    {
        public bool Throwing;            // the last frame showed a throw under way
        public float LastTime;           // its throw time then
        public WeaponSlot Slot;          // the slot in hand during the throw
        public bool Active;              // the follow-through runs
        public double EndedAt;           // connected time the throw ended
        public float EndTime;            // the throw time it ended at
    }

    readonly Dictionary<int, ThrowFollowTrack> _throwFollow = new();
    int _throwFollowsStarted, _throwFollowsCut;

    /// <summary>
    /// The throw time to pose the toss at this frame: <paramref name="simThrowTime"/> while the throw is under way, then the follow-through's time (the throw's last time plus
    /// the real time since it ended) until the clip's end or until <see cref="ThrowFollowBusy"/>; null when neither.
    /// </summary>
    float? ThrowShown(CharacterViewer? character, PlayerState s, float? simThrowTime)
    {
        if (OldThrowEnd || _tossAnimation is null || _config is null || (character is not null && IsOrkCharacter(character))) return simThrowTime;
        if (!_throwFollow.TryGetValue(s.PlayerId, out var t)) _throwFollow[s.PlayerId] = t = new ThrowFollowTrack();
        if (simThrowTime is { } tt && s.Alive)
        {
            t.Active = false; t.Throwing = true; t.LastTime = tt; t.Slot = s.Slot;
            return tt;
        }
        if (t.Throwing)
        {
            t.Throwing = false;
            if (s.Alive && TossShown(s) && t.LastTime + 1e-4f >= _config.ThrowRelease) { t.Active = true; t.EndedAt = _connectedFor; t.EndTime = t.LastTime; _throwFollowsStarted++; }
        }
        if (!t.Active) return null;
        float time = t.EndTime + (float)Math.Max(0.0, _connectedFor - t.EndedAt);
        bool busy = ThrowFollowBusy(s, t);
        if (!ActionHandoff.ThrowFollowContinues(TossClipTime(time), TossClipEnd, busy))
        {
            t.Active = false;
            if (busy) _throwFollowsCut++;
            return null;
        }
        return time;
    }

    /// <summary>Whether something with a look of its own has started since the throw ended, so the follow-through gives way to it.</summary>
    bool ThrowFollowBusy(PlayerState s, ThrowFollowTrack t)
    {
        if (!s.Alive || s.MeleeStep > 0 || s.Aiming || s.Charging || s.PendingShot > 0 || s.RaiseTicks > 0 || s.SwitchLeft > 0f || s.Slot != t.Slot
            || s.Jump != JumpPhase.None || s.EvadeTicks > 0 || s.Reaction != ReactionKind.None || s.Rush != RushPhase.None || s.Brace != BracePhase.None || s.Venting)
            return true;
        if (HipOf(s.PlayerId).ShotAt > t.EndedAt) return true;   // a shot seen since (own: the prediction's; others': their shot records)
        if (_reloads.TryGetValue(s.PlayerId, out var r) && r.Player.Shown) return true;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        return _config is { } c && speed > c.RunSpeed * 1.1f;   // a sprint
    }

    /// <summary>The F1 / bot log line about the follow-through.</summary>
    string ThrowFollowStats() => $"toss follow-throughs started {_throwFollowsStarted}, cut short by another action {_throwFollowsCut}{(OldThrowEnd ? " (--old-throw-end)" : "")}";
}
