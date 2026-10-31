using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Remote players' clip-driven poses at the frame rate (client presentation, ours). The interpolated remote view carries each
/// player's reaction ticks (M28), rush ticks (M29) and melee time from the older of the two snapshots, so they step at the
/// snapshot rate; posing the clip at that value froze the pose between snapshots and jumped at the next. Each remote player now
/// has a <see cref="ClipClock"/> that runs on at real time and eases onto each new snapshot value (restarting when the clip's
/// kind, step or phase changes). Evade rolls have their own, older version of this in <c>NetworkView.Evade.cs</c>. The own
/// player (predicted every tick) is untouched. The bot logs how often the raw and the smoothed time stood still or jumped.
/// </summary>
public partial class NetworkView
{
    enum ClipKind { None, Reaction, Rush, Attack }

    /// <summary>The clip a remote player's pose is smoothed for: which of Place's clip branches it feeds, and the time to pose.</summary>
    readonly record struct SmoothClip(ClipKind Kind, float Time);

    readonly Dictionary<int, ClipClock> _clipClocks = new();

    /// <summary>Per-frame stand-still and jump counts of one remote player's clip time (bot log only).</summary>
    sealed class ClipStats
    {
        public long Key = -1;
        public float RawTime, SmoothTime;
        public long Frames, RawStill, RawJumpy, SmoothStill, SmoothJumpy;
        public float RawMaxStep, SmoothMaxStep;
    }
    readonly Dictionary<int, ClipStats> _clipStats = new();
    double _clipStatsNext = 15.0;

    /// <summary>
    /// Steps one remote player's clip clock for this frame and returns the smoothed time of the clip <see cref="Place"/> will pose
    /// (reaction, else charge, else attack: the same order), or null when none applies.
    /// </summary>
    SmoothClip? SmoothRemoteClip(PlayerState s, float delta)
    {
        if (!_clipClocks.TryGetValue(s.PlayerId, out var clock)) _clipClocks[s.PlayerId] = clock = new ClipClock();
        var (kind, key, target, length) = ClipTargetOf(s);
        if (kind == ClipKind.None) { clock.Stop(); NoteShownAttack(s.PlayerId, kind, 0f); NoteClipStats(s.PlayerId, -1, 0f, 0f, 0f, 0f); return null; }
        float shown = clock.Step(key, target, delta, length);
        NoteShownAttack(s.PlayerId, kind, shown);
        NoteClipStats(s.PlayerId, key, target, shown, delta, length);
        return new SmoothClip(kind, shown);
    }

    /// <summary>
    /// The clip <see cref="Place"/> will pose for a player (reaction, else charge, else attack: the same order) as the state carries it: its
    /// kind (<see cref="ClipKind.None"/>: none), identity, time and length. Shared by the remote clip clocks and the own clip clock (feel/controls).
    /// </summary>
    (ClipKind Kind, long Key, float Target, float Length) ClipTargetOf(PlayerState s)
    {
        ClipKind kind = ClipKind.None;
        long key = 0;
        float target = 0f, length = 0f;
        if (IsOrkPlayer(s)) OrkClipTarget(s, ref kind, ref key, ref target, ref length);   // Ork O7b / O7c: the boy's clips (NetworkView.OrkActions.cs), decided from the state alone
        else if (ReactionPose(s) is { } reaction && Reaction.ClipOf(s, _config!) is { } clip)
        {
            kind = ClipKind.Reaction; target = reaction.Time; length = clip.Duration;
            key = ClipClock.Key((int)kind, (int)s.Reaction, (int)s.ReactionDirection);
        }
        else if (RushPose(s) is { } rush && Rush.MoveOf(_config!, s) is { } move)   // protocol 30: the dash attack's phases too
        {
            kind = ClipKind.Rush; target = rush.Time; length = move.ClipLength;
            key = ClipClock.Key((int)kind, (int)s.Rush);
        }
        else if (AttackClip(s) is not null)
        {
            kind = ClipKind.Attack; target = s.MeleeTime; length = float.MaxValue;   // the pose clamps to the clip's own length
            key = ClipClock.Key((int)kind, s.MeleeStep, s.MeleeHeavy ? 1 : 0, s.MeleeChoice);
        }
        return (kind, key, target, length);
    }

    /// <summary>Counts, for the bot's log, how the raw (snapshot-stepped) and the smoothed clip time moved from frame to frame.</summary>
    void NoteClipStats(int id, long key, float raw, float smooth, float delta, float length)
    {
        if (!_bot) return;
        if (!_clipStats.TryGetValue(id, out var st)) _clipStats[id] = st = new ClipStats();
        bool same = key >= 0 && key == st.Key && delta > 0f;
        if (same && smooth < length - 1e-3f && st.SmoothTime < length - 1e-3f)
        {
            st.Frames++;
            float rawStep = MathF.Abs(raw - st.RawTime), smoothStep = MathF.Abs(smooth - st.SmoothTime);
            // Still: the time did not move though a frame passed; jumpy: it moved more than twice a frame plus a tick's worth.
            float jumpy = 2f * delta + 0.02f;
            if (rawStep < 1e-5f) st.RawStill++; else if (rawStep > jumpy) st.RawJumpy++;
            if (smoothStep < 1e-5f) st.SmoothStill++; else if (smoothStep > jumpy) st.SmoothJumpy++;
            st.RawMaxStep = MathF.Max(st.RawMaxStep, rawStep); st.SmoothMaxStep = MathF.Max(st.SmoothMaxStep, smoothStep);
        }
        st.Key = key; st.RawTime = raw; st.SmoothTime = smooth;
    }

    /// <summary>The bot's periodic summary of <see cref="NoteClipStats"/> (cumulative over all remote players).</summary>
    void LogClipStats()
    {
        if (!_bot || _connectedFor < _clipStatsNext) return;
        _clipStatsNext = _connectedFor + 15.0;
        long frames = _clipStats.Values.Sum(s => s.Frames);
        if (frames == 0) return;
        GD.Print($"t {_connectedFor:F1} s: remote clip time over {frames} frames (clip kind unchanged from the last frame): " +
                 $"raw (snapshot value) still {_clipStats.Values.Sum(s => s.RawStill)}, jumpy {_clipStats.Values.Sum(s => s.RawJumpy)}, max step {_clipStats.Values.Max(s => s.RawMaxStep):F3} s; " +
                 $"smoothed still {_clipStats.Values.Sum(s => s.SmoothStill)}, jumpy {_clipStats.Values.Sum(s => s.SmoothJumpy)}, max step {_clipStats.Values.Max(s => s.SmoothMaxStep):F3} s");
    }
}
