using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// sim/hammer-180 (protocol 93; the owner 2026-11-25: "Add it for the hammer"): the thunder hammer's and the daemon maul's Dir 180 strikes (<see cref="MeleeStep.TurnsBack"/>; the clips
/// <c>SM_MEL_TH_XATK_180_MP</c> and <c>SM_MEL_TH_XATK_180_combo2</c>) play through the ordinary attack clip path (they are light strikes 4 and 5, their follow-ups 6 and 7, with their graph blends
/// from <c>MeleeFlowBlends</c>). The simulation strikes them along the turned facing for the whole strike; the clip starts facing the press-time way and its extracted root turns the half turn in one sample
/// (0.7 s of 3 s; the bones jump the opposite half turn in the same sample, so the body is continuous only with the root's turn applied). The drawn body therefore faces the heading less half a turn until
/// <see cref="MeleeStep.TurnAt"/>, then the heading. For the own, remote and spectated players alike (<c>Place</c>). <c>--no-hammer-180-turn</c> (test aid): the body faces the heading throughout.
/// </summary>
public partial class NetworkView
{
    static readonly bool NoHammer180Turn = OS.GetCmdlineUserArgs().Contains("--no-hammer-180-turn");

    /// <summary>The drawn body's yaw for a player in a Dir 180 strike before its root turn (<paramref name="aimYaw"/>: the heading as drawn), or null (not in one, past the turn, or no turn time read).</summary>
    float? Hammer180BodyYaw(PlayerState s, float aimYaw, SmoothClip? smooth)
    {
        if (NoHammer180Turn || _config is null || !s.Alive || s.MeleeStep <= 0 || s.MeleeHeavy || Combat.MeleeStepOf(_config, s) is not { TurnsBack: true } step || !float.IsFinite(step.TurnAt)) return null;
        float time = smooth is { Kind: ClipKind.Attack } sa ? sa.Time : s.MeleeTime;
        return time < step.TurnAt ? aimYaw - MathF.PI : null;
    }
}
