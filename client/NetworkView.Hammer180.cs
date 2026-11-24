using System;
using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/hammer-180 (protocol 93; the owner 2026-11-25: "Add it for the hammer"): the thunder hammer's and the daemon maul's Dir 180 strikes (<see cref="MeleeStep.TurnsBack"/>; the clips
/// <c>SM_MEL_TH_XATK_180_MP</c> and <c>SM_MEL_TH_XATK_180_combo2</c>) play through the ordinary attack clip path (they are light strikes 4 and 5, their follow-ups 6 and 7, with their graph blends
/// from <c>MeleeFlowBlends</c>). The simulation strikes them along the turned facing for the whole strike; the clip starts facing the press-time way and its extracted root turns the half turn in one sample
/// (0.7 s of 3 s; the bones jump the opposite half turn in the same sample, so the body is continuous only with the root's turn applied). The drawn body therefore faces the heading less half a turn until
/// the middle of that sample (<see cref="Hammer180Draw.FlipAt"/>), then the heading, and the clip is shown at the jump's two ends only (<see cref="Hammer180Draw.ClipTime"/>: the sampler would otherwise
/// spin the bones through the half turn in one frame). Both read the clip time as drawn (the hit-stop's hold included), so a hit-stop at the hit (0.6 s, just before the turn) holds root and bones together.
/// For the own, remote and spectated players alike (<c>Place</c>). <c>--no-hammer-180-turn</c> (test aid): the body faces the heading throughout; <c>--log-hammer-180</c>: a <c>HAMMER180</c> line per drawn
/// frame of such a strike, plus <c>HAMMER180 begin / hit / end</c> lines per player for every strike of the family (strike time of the hit, the time and the next strike at its end).
/// Known gap: a turn strike interrupted before its root turn (a stagger, a roll, a death) leaves the simulation's facing turned, so the drawn body turns half round at once (logged as
/// <c>HAMMER180 interrupted</c>); the clip's own end state there is the press-time facing.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoHammer180Turn = OS.GetCmdlineUserArgs().Contains("--no-hammer-180-turn");
    static readonly bool LogHammer180 = OS.GetCmdlineUserArgs().Contains("--log-hammer-180");

    /// <summary>Per player, for <c>--log-hammer-180</c>: the family strike last drawn, its last drawn time, whether its hit was logged and whether it was drawn unturned.</summary>
    readonly Dictionary<int, (int Step, bool Heavy, float Time, bool Hit, bool Unturned)> _hammer180Seen = new();

    /// <summary>The clip time a strike is drawn at (the smoothed remote clock or the state's, less the hit-stop's hold), as <c>Place</c> poses it.</summary>
    float Hammer180ClipTime(PlayerState s, SmoothClip? smooth) => MathF.Max(0f, (smooth is { Kind: ClipKind.Attack } sa ? sa.Time : s.MeleeTime) - HitStopHold(s.PlayerId));

    /// <summary>The yaw to draw a player at: <paramref name="bodyYaw"/> (the turn-in-place result), or for a Dir 180 strike before its root turn the heading as drawn (<paramref name="aimYaw"/>) less half a turn.</summary>
    float Hammer180Yaw(PlayerState s, float aimYaw, float bodyYaw, SmoothClip? smooth)
    {
        var step = _config is null || !s.Alive || s.MeleeStep <= 0 ? null : Combat.MeleeStepOf(_config, s);
        if (LogHammer180) NoteHammer180(s, step, smooth);
        if (s.MeleeHeavy || step is not { TurnsBack: true } m) return bodyYaw;
        float time = Hammer180ClipTime(s, smooth);
        float drawn = !NoHammer180Turn && Hammer180Draw.Unturned(m, time) ? aimYaw - MathF.PI : bodyYaw;
        if (LogHammer180) GD.Print($"HAMMER180 t {_connectedFor:F3} P{s.PlayerId} step {s.MeleeStep} time {time:F3} turnAt {m.TurnAt:F3} state yaw {s.Yaw:F2} aim {aimYaw:F2} body {bodyYaw:F2} drawn {drawn:F2}");
        return drawn;
    }

    /// <summary>The clip time to pose a strike at: <see cref="Hammer180Draw.ClipTime"/> for a Dir 180 strike (not with <c>--no-hammer-180-turn</c>), else <paramref name="drawnTime"/>.</summary>
    float Hammer180PoseTime(PlayerState s, float drawnTime) =>
        NoHammer180Turn || _config is null || s.MeleeHeavy || Combat.MeleeStepOf(_config, s) is not { TurnsBack: true } m ? drawnTime : Hammer180Draw.ClipTime(m, drawnTime);

    void NoteHammer180(PlayerState s, MeleeStep? step, SmoothClip? smooth)
    {
        bool family = step is { Dir180: true } && !s.MeleeHeavy;
        bool had = _hammer180Seen.TryGetValue(s.PlayerId, out var seen);
        float time = Hammer180ClipTime(s, smooth);
        if (had && (seen.Step != s.MeleeStep || seen.Heavy != s.MeleeHeavy || (family && time + 0.25f < seen.Time)))
        {
            string next = s.MeleeStep <= 0 ? "rest" : $"{(s.MeleeHeavy ? "heavy" : "light")} {s.MeleeStep}";
            GD.Print($"HAMMER180 end t {_connectedFor:F3} P{s.PlayerId} step {seen.Step} at {seen.Time:F3} -> {next}{(s.Alive ? "" : " (dead)")}");
            if (seen.Unturned && !(family && step is { TurnsBack: true })) GD.Print($"HAMMER180 interrupted t {_connectedFor:F3} P{s.PlayerId} step {seen.Step} before its root turn at {seen.Time:F3}: the drawn body turns half round");
            _hammer180Seen.Remove(s.PlayerId);
            had = false;
        }
        if (!family) return;
        var m = step!.Value;
        if (!had) { GD.Print($"HAMMER180 begin t {_connectedFor:F3} P{s.PlayerId} step {s.MeleeStep} at {time:F3} (hit {m.HitTime:F3}, combo {m.ComboOpen:F3}-{m.ComboClose:F3}, reset {m.StringResetAt:F3}, end {m.Duration:F3}, yaw {s.Yaw:F2})"); seen = (s.MeleeStep, false, time, false, false); }
        bool hit = seen.Hit;
        if (!hit && time + 1e-4f >= m.HitTime) { GD.Print($"HAMMER180 hit t {_connectedFor:F3} P{s.PlayerId} step {s.MeleeStep} at {time:F3} (data {m.HitTime:F3})"); hit = true; }
        _hammer180Seen[s.PlayerId] = (s.MeleeStep, false, time, hit, Hammer180Draw.Unturned(m, time));
    }
}
