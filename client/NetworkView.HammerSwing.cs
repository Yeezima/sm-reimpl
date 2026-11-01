using System;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/hammer-swing (the owner, 2026-11-01: on the thunder hammer, "the legs are doing really crazy things when you are doing the regular swings, in particular, continuing the combo. Your
/// upper body moves forward with the step forward ... your legs are still mostly rooted to the ground"; client presentation only, no simulation, wire or config-hash change of its own).
/// <para>
/// Cause (measured with <c>--anim-probe</c>, <c>--swing-test stand</c>): the hammer's light strikes carry 4.5 to 6 m of root motion (the clip's feet planted in an authored world that moves
/// at up to 19 m/s under them) and the simulation moves the body 1.625 times that (<see cref="SimConfig.MeleeLungeScale"/>, the lunge every melee weapon has). The clip foot lock held each planted foot where
/// it was put down in the world while the body went on, so the leg was pulled out straight within a few frames and the foot was then dragged along the ground on the edge of the reach
/// (1.24 m behind the body, the leg at its 0.995 limit in 58 % of the strikes' frames), and the next strike's blend let that foot go over up to 0.35 s from where it lay.
/// </para>
/// <para>
/// Now the clip's feet are stretched along the heading by the body's extra travel (<see cref="HammerStrideScale"/>; <c>CharacterViewer.ClipStrideScale</c>): the same step in a world
/// stretched as far as the body's lunge, so the foot put down is held where the body will pass it. <c>--old-hammer-legs</c> (test aid) restores the old legs.
/// </para>
/// </summary>
public partial class NetworkView
{
    /// <summary>The stride factor of a hammer (thunder hammer, daemon maul) light strike shown now: how many times the clip's root motion the body travels; 1 for anything else.</summary>
    float HammerStrideScale(PlayerState s)
    {
        if (CharacterViewer.OldHammerLegs || NoMeleeFlow || _config is null || s.MeleeHeavy || s.MeleeStep <= 0 || MeleeWargear(s) is not { } wargear) return 1f;
        if (!wargear.Contains("thunder_hammer", StringComparison.OrdinalIgnoreCase) && !wargear.Contains("power_mace", StringComparison.OrdinalIgnoreCase)) return 1f;
        if (Combat.MeleeStepOf(_config, s) is not { Reach.Variants.Count: > 0 } step) return 1f;
        float k = _config.LungeScale * step.LungeFactorApplied;
        return float.IsFinite(k) && k > 1f ? k : 1f;
    }

    /// <summary>The hammer's own air slash clip when the rig has and decodes it (<see cref="HammerSlashPose.Clip"/>; from <see cref="SetUpDashAttack"/>).</summary>
    string? _hammerSlashClip;
    /// <summary><c>--old-hammer-slash</c> (test aid): the hammer marine's air slash shows the sword's clip as before anim/hammer-swing.</summary>
    static readonly bool OldHammerSlash = Godot.OS.GetCmdlineUserArgs().Contains("--old-hammer-slash");
    /// <summary>Whether <paramref name="s"/>'s air slash shows the hammer's own clip (the thunder hammer or the daemon maul in hand).</summary>
    bool HammerSlash(PlayerState s) => !OldHammerSlash && _hammerSlashClip is not null && HammerSlashPose.IsHammer(MeleeWargear(s));
    /// <summary>The clip <paramref name="s"/>'s air slash shows: the hammer's, or the sword's (null: none; the pound's clips).</summary>
    string? SlashClipOf(PlayerState s) => HammerSlash(s) ? _hammerSlashClip : _slashClip;
}
