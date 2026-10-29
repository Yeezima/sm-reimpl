using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// M21 (protocol 13): the evade roll in the networked view. The simulation moves the body (the clip's extracted motion,
/// <see cref="EvadeMove.Path"/>) and fixes its yaw; the client poses the original roll clip (<see cref="EvadeMove.Animation"/>:
/// <c>SM_EVD_R2R_Roll_*</c> for the Tactical classes, <c>SM_EVD_TB_Evade_*</c> for the heavy ones) at the played time
/// (<see cref="Evade.Time"/>). The clips are in place (the horizontal travel is not in the pose: checked on the install), so
/// no root motion has to be removed. The roll wins over aim and locomotion; melee and jumping stay as they were (the
/// simulation does not let them overlap). Remote players' ticks arrive at the snapshot rate (and capped at
/// <see cref="Evade.MaxSlimTicks"/>), so <see cref="TrackEvade"/> advances the time locally between snapshots and eases it
/// onto each new value. The own camera blends to the roll's camera set (<see cref="EvadeSpec.Camera"/>) in
/// <c>NetworkView.Camera.cs</c>. Original clips, motion and camera set; the rules are an interpretation (spec/behavior.md).
/// </summary>
public partial class NetworkView
{
    /// <summary>One player's roll as the client shows it.</summary>
    sealed class EvadeTrack
    {
        public bool Active;
        public int Ticks;                 // EvadeTicks last seen
        public EvadeDirection Direction;
        public float Time;                // clip seconds shown
        public int CorrectionsAtStart;    // own player: the number of correction samples when the roll began
    }

    /// <summary>Seconds the shown time may run ahead of the last snapshot's (about one snapshot interval).</summary>
    const float EvadeAhead = 0.15f;
    /// <summary>A shown time further than this from the snapshot's is replaced rather than eased (seconds).</summary>
    const float EvadeSnap = 0.2f;
    /// <summary>Bot test script: seconds between rolls (plus up to <see cref="BotEvadeJitter"/>); not original behaviour.</summary>
    const double BotEvadeEvery = 6.0, BotEvadeJitter = 2.0;

    readonly Dictionary<int, EvadeTrack> _evadeTracks = new();
    int _evadesOwn, _evadesSeen;
    double _botEvadeNext = 3.0;
    Random? _botRandom;

    /// <summary>
    /// Follows one player's roll for the pose and the log. The clip time starts at the snapshot's, runs on at real time, is
    /// eased half way to each new tick value (snapped when 0.2 s off, so a restart or chain shows at once) and never runs
    /// more than <see cref="EvadeAhead"/> past the last one nor past the clip.
    /// </summary>
    void TrackEvade(PlayerState s, bool own, float delta)
    {
        if (!_evadeTracks.TryGetValue(s.PlayerId, out var t)) _evadeTracks[s.PlayerId] = t = new EvadeTrack();
        var move = s.Alive ? Evade.Move(s, _config!) : null;
        if (move is null)
        {
            if (own && t.Active && _bot)
            {
                // Prediction check: the corrections sampled while the roll lasted (a respawn or a hit elsewhere also shows up as one).
                var errors = _client!.CorrectionErrors;
                int from = Math.Min(t.CorrectionsAtStart, errors.Count);
                GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} evade ended after {t.Time:F2} s; corrections during it: {errors.Count - from} sampled, max {(errors.Count > from ? errors.Skip(from).Max() : 0f):F3} m");
            }
            t.Active = false;
            return;
        }
        float target = Evade.Time(s, _config!);
        bool restart = !t.Active || s.EvadeDirection != t.Direction || s.EvadeTicks < t.Ticks;
        if (restart)
        {
            t.Time = target;
            if (own) t.CorrectionsAtStart = _client!.CorrectionErrors.Count;
            if (own) _evadesOwn++; else _evadesSeen++;
            if (_bot)
                GD.Print($"t {_connectedFor:F1} s: id {_client!.PlayerId}{(own ? "" : $" sees P{s.PlayerId}")} evade {s.EvadeDirection} ({move.Animation}, {move.Duration:F2} s, travel {move.Distance.X:F1},{move.Distance.Y:F1} m; " +
                         $"{(own ? $"rolls {_evadesOwn}" : $"seen {_evadesSeen}")}; prediction corrections {Corrections()})");
        }
        else
        {
            t.Time += delta;
            if (s.EvadeTicks != t.Ticks)
            {
                float error = target - t.Time;
                t.Time = MathF.Abs(error) > EvadeSnap ? target : t.Time + error * 0.5f;
            }
            t.Time = MathF.Min(t.Time, target + EvadeAhead);
        }
        t.Time = Math.Clamp(t.Time, 0f, move.Duration);
        t.Active = true; t.Ticks = s.EvadeTicks; t.Direction = s.EvadeDirection;
    }

    /// <summary>The roll clip and time to pose for a player (null when it is not rolling).</summary>
    (string Clip, float Time)? EvadePose(PlayerState s)
    {
        if (!s.Alive || !_evadeTracks.TryGetValue(s.PlayerId, out var t) || !t.Active || Evade.Move(s, _config!) is not { } move) return null;
        return (HopClipFor(s, move.Animation), t.Time);   // animation pass 3: the cannons' own hop clips (NetworkView.HeavyHold.cs)
    }

    /// <summary>Count and largest prediction correction of the own player so far, for the bot's log.</summary>
    string Corrections()
    {
        var errors = _client!.CorrectionErrors;
        return errors.Count == 0 ? "none sampled" : $"{errors.Count} sampled, max {errors.Max():F3} m";
    }

    /// <summary>
    /// The bot's roll (test script, only for classes with an evade): in a fight, grounded and free, a Jump press with the
    /// stick sideways (left or right at random) every <see cref="BotEvadeEvery"/> s or so; during a roll no stick (it would
    /// end the roll at the clip's run branch).
    /// </summary>
    PlayerInput? BotEvadeInput(PlayerState me, uint tick)
    {
        if (Loadouts.Evade(_config!, me) is null) return null;
        if (Evade.Evading(me)) return new PlayerInput(tick, 0f, 0f, _yaw, InputButtons.None, _pitch);
        if (_connectedFor < _botEvadeNext || !me.Grounded || me.EvadeLock > 0 || me.MeleeStep > 0 || me.Throwing || me.SwitchLeft > 0f || me.PendingShot > 0) return null;
        _botRandom ??= new Random(Math.Max(_client?.PlayerId ?? 0, 0) * 7919 + 1);
        _botEvadeNext = _connectedFor + BotEvadeEvery + _botRandom.NextDouble() * BotEvadeJitter;
        float side = _botRandom.Next(2) == 0 ? -1f : 1f;
        // Roll only onto walkable ground (navigation graph, NetworkView.Nav.cs): the other side, or no roll this time.
        if (!BotRollClear(me, side)) side = -side;
        if (!BotRollClear(me, side)) return null;
        return new PlayerInput(tick, side, 0f, _yaw, InputButtons.Jump, _pitch);
    }
}
