using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/hammer-180 (protocol 93; the owner 2026-11-25: "Add it for the hammer"): the thunder hammer's and the daemon maul's Dir 180 strikes (<see cref="MeleeStep.TurnsBack"/>; the clips
/// <c>SM_MEL_TH_XATK_180_MP</c> and <c>SM_MEL_TH_XATK_180_combo2</c>) play through the ordinary attack clip path (they are light strikes 4 and 5, their follow-ups 6 and 7, with their graph blends
/// from <c>MeleeFlowBlends</c>). The simulation strikes them along the turned facing for the whole strike; the clip starts facing the press-time way and its extracted root turns the half turn in one sample
/// (0.7 s of 3 s; the bones jump the opposite half turn in the same sample, so the body is continuous only with the root's turn applied). The drawn body therefore faces the heading less half a turn until
/// <see cref="MeleeStep.TurnAt"/>, then the heading. For the own, remote and spectated players alike (<c>Place</c>). <c>--no-hammer-180-turn</c> (test aid): the body faces the heading throughout;
/// <c>--log-hammer-180</c>: a <c>HAMMER180</c> line per drawn frame of such a strike.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoHammer180Turn = OS.GetCmdlineUserArgs().Contains("--no-hammer-180-turn");
    static readonly bool LogHammer180 = OS.GetCmdlineUserArgs().Contains("--log-hammer-180");

    /// <summary>The yaw to draw a player at: <paramref name="bodyYaw"/> (the turn-in-place result), or for a Dir 180 strike before its root turn the heading as drawn (<paramref name="aimYaw"/>) less half a turn.</summary>
    float Hammer180Yaw(PlayerState s, float aimYaw, float bodyYaw, SmoothClip? smooth)
    {
        if (_config is null || !s.Alive || s.MeleeStep <= 0 || s.MeleeHeavy || Combat.MeleeStepOf(_config, s) is not { TurnsBack: true } step) return bodyYaw;
        float time = smooth is { Kind: ClipKind.Attack } sa ? sa.Time : s.MeleeTime;
        float drawn = !NoHammer180Turn && float.IsFinite(step.TurnAt) && time < step.TurnAt ? aimYaw - MathF.PI : bodyYaw;
        if (LogHammer180) GD.Print($"HAMMER180 t {_connectedFor:F3} P{s.PlayerId} step {s.MeleeStep} time {time:F3} turnAt {step.TurnAt:F3} state yaw {s.Yaw:F2} aim {aimYaw:F2} body {bodyYaw:F2} drawn {drawn:F2}");
        return drawn;
    }
}
