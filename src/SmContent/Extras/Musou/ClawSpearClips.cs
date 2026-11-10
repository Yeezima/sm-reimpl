using System;
using System.Collections.Generic;
using System.Linq;

namespace SmContent.Extras.Musou;

/// <summary>
/// The lightning claw's and the power spear's strike animations (added content, Musou Mode only; design <c>docs/new-weapons-design.md</c> sections 5 and 6, approach
/// <c>docs/claw-spear-animations.md</c>). **Ours**, authored as keys (<see cref="WeaponPoseClip"/>) over the install's stances; the timing is the design's:
/// <list type="bullet">
/// <item>Claw: the power sword's strikes at 1.25x, so each length and hit time is the sword's divided by <see cref="ClawTempo"/> (spec "Power sword X1..X4": hits 0.320 / 0.287 / 0.433 / 0.533 s,
/// lengths 1.600 / 1.433 / 1.767 / 1.667 s; the Y backhand <c>SM_MEL_CS_XATK_Draw2_3.5m</c> 1.5 s, hit 0.33 s). Laid over the one-handed melee idle (<see cref="ClawIdle"/>: the chainsword stance,
/// pistol in the left hand, the right arm hanging low and back). Second pass (owner's review 2026-11-10: "too stiff / small motions", "reads like a sword", "keep the diagonal rake"): the
/// whole upper body drives every strike (the lower spine as the hips, the chest, the shoulder), the arcs reach far behind and far past the target, the off hand braces and shoves, and
/// the moves are a claw's: a diagonal rake, a low gutting rake, a punching stab, a tearing rip back toward the body, an overhead downward rake.</item>
/// <item>Spear: the thunder hammer's at 0.85x, so each is the hammer's divided by <see cref="SpearTempo"/> (spec "Thunder hammer X1..X3": hits 0.604 / 0.567 / 0.700 s, lengths 2.667 / 2.333 / 2.333 s).
/// Laid over the hammer's two-handed idle (<see cref="SpearIdle"/>); at rest the spear stands upright at the marine's right side, butt down and blade up (<see cref="SpearRest"/>, the owner's
/// reference and his answer of 2026-11-10), and every sweep starts from and returns to that rest. The dive over the hammer's dive loop and landing (<see cref="SpearDiveLoop"/>,
/// <see cref="SpearDiveImpact"/>), whose impale lands with the landing (0 s of the impact clip) and releases at 0.5 s (owner, 2026-11-10).</item>
/// </list>
/// The arms are keyed as directions (<see cref="PoseChannelKind.Aim"/>: where the upper arm and the forearm point, elevation and azimuth), so a pose means the same over any stance; the spine,
/// head, clavicles and hands as turns. Every strike has a clear anticipation (the body coils, <see cref="PoseEase.In"/>), a snap on the strike (<see cref="PoseEase.Snap"/>, or
/// <see cref="PoseEase.Linear"/> through the hit of a sweep), a weighted follow-through (<see cref="PoseEase.Over"/>) and a settle; the legs are never keyed, so the feet stay where the stance
/// puts them. The spear's blade is beyond the left (forward) hand, the butt beyond the right, as a polearm is held.
/// </summary>
public static class ClawSpearClips
{
    /// <summary>The claw plays the sword's strikes 1.25x faster (design D13; the factor ours).</summary>
    public const float ClawTempo = 1.25f;
    /// <summary>The spear plays the hammer's strikes at 0.85x (design D14; the factor an estimate).</summary>
    public const float SpearTempo = 0.85f;

    /// <summary>The one-handed melee stance the claw's strikes start from (the chainsword's idle: pistol in the left hand, the melee weapon in the right).</summary>
    public const string ClawIdle = "SM_NAV_IdleL_CS_01";
    /// <summary>The hammer's two-handed idle (<c>SM_TH_NAV</c> "Idle"), the spear's base stance (the rest pose is laid over it).</summary>
    public const string SpearIdle = "SM_2HM_NAV_TH_IdleL_S_01";
    /// <summary>The hammer's jump-pack dive loop and its landing (design section 6.2: the spear turned point-down from the dive's start until the get-up).</summary>
    public const string SpearDiveLoop = "SM_JMP_TH_GP_Loop_01", SpearDiveImpact = "SM_JMP_TH_GP_Impact_01";
    /// <summary>The hammer's dive loop's, landing clip's and idle's lengths (s), from the install's clip list.</summary>
    public const float SpearDiveLoopLength = 1.767f, SpearDiveImpactLength = 1.067f, SpearIdleLength = 1.5f;

    // Bones (the multiplayer marine rig; docs/discovery.md "Havok skeleton").
    const string SpineA = "RIG_SpineA", SpineD = "RIG_SpineD", Head = "RIG_Head";
    const string LClav = "RIG_L_Clavicle", LUpper = "RIG_L_UpperArm", LFore = "RIG_L_Forearm", LHand = "RIG_L_Hand";
    const string RClav = "RIG_R_Clavicle", RUpper = "RIG_R_UpperArm", RFore = "RIG_R_Forearm", RHand = "RIG_R_Hand";

    static PoseKey T(float t, float yaw, float pitch, float roll, PoseEase ease = PoseEase.Smooth) => PoseKey.Turn(t, yaw, pitch, roll, ease);
    /// <summary>An aim key: elevation above the horizontal, azimuth from the front toward the right (degrees).</summary>
    static PoseKey A(float t, float el, float az, PoseEase ease = PoseEase.Smooth, float w = 1f) => PoseKey.Aim(t, el, az, ease, w);
    static PoseKey Z(float t, PoseEase ease = PoseEase.Smooth) => PoseKey.Zero(t, ease);
    static PoseChannel Turn(string bone, params PoseKey[] keys) => new(bone, keys);
    static PoseChannel Upper(string side, params PoseKey[] keys) => new($"RIG_{side}_UpperArm", keys, PoseChannelKind.Aim, $"RIG_{side}_Forearm");
    static PoseChannel Fore(string side, params PoseKey[] keys) => new($"RIG_{side}_Forearm", keys, PoseChannelKind.Aim, $"RIG_{side}_Hand");

    // ---- the lightning claw (right hand; the pistol stays in the left) ----
    // The claw idle's right arm hangs low and back (upper arm el -54 az 150, forearm el -57 az 92: --pose-log), so an aim key's weight 0 is that hanging arm.
    // Third pass (owner, 2026-11-10: "no part of the character's body should remain still through this.. he should be putting his entire body into the strokes", "add pelvis now"):
    // the pelvis turns, drops and shifts its weight on every strike (Turn and Move channels on RIG_Pelvis), a lead foot steps into the strike and replants (Foot channels: offsets from the
    // stance's spot, up > 0 lifts), the rear leg drives; the legs are solved onto the feet, so a planted foot never slides. Chained strikes start with the feet where the one before planted them.

    const string Pelvis = "RIG_Pelvis", LFoot = "RIG_L_Foot", RFoot = "RIG_R_Foot";
    /// <summary>A move or foot key: metres forward, right, up in the stance's frame.</summary>
    static PoseKey M(float t, float forward, float right, float up, PoseEase ease = PoseEase.Smooth) => PoseKey.Move(t, forward, right, up, ease);
    static PoseChannel PelvisMove(params PoseKey[] keys) => new(Pelvis, keys, PoseChannelKind.Move);
    static PoseChannel Foot(string side, params PoseKey[] keys) => new($"RIG_{side}_Foot", keys, PoseChannelKind.Foot);
    /// <summary>Where the string's steps leave the feet (metres forward, right from the stance's spots): after X1 the left foot, after X2 the right, after X3 the left lunged, after X4's rip the right stepped back.</summary>
    const float X1LeftF = 0.40f, X1LeftR = -0.12f, X2RightF = 0.35f, X2RightR = 0.18f, X3LeftF = 0.75f, X4RightF = -0.05f, X4RightR = 0.22f;

    /// <summary>X1: the diagonal rake, from high behind the right shoulder down across to past the left hip, the left foot stepping into it (the sword's X1 at 1.25x: 1.28 s, hit 0.256 s).</summary>
    public static readonly WeaponPoseClip ClawX1 = Claw("claw_x1", 1.600f, 0.320f, 0.867f, "the sword's X1 (SM_MEL_CS_ATK_F_01_D_3.5m 1.600 s, hit 0.320 s)",
        "diagonal rake: the hips and chest coil hard right over the rear leg with the claw drawn up behind the right shoulder and the left foot lifting, then the left foot stamps down ahead and the whole body whips left and down over it as the talons rake across the target to past the left hip; the pistol arm swings back as a counterweight",
        antic: 0.086f, follow: 0.125f, recover: 0.80f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, Z(0f), T(a, -18, 4, -6, PoseEase.In), T(h, 20, -10, 6, PoseEase.Snap), T(f, 24, -12, 8, PoseEase.Over), T(r, 8, -4, 2), Z(end)),
            PelvisMove(M(0f, 0, 0, 0), M(a, -0.06f, 0.05f, -0.06f, PoseEase.In), M(h, 0.18f, -0.06f, -0.14f, PoseEase.Snap), M(f, 0.22f, -0.08f, -0.16f, PoseEase.Over), M(r, 0.10f, -0.03f, -0.06f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0, 0, 0), M(a, 0.15f, -0.05f, 0.12f, PoseEase.In), M(h, X1LeftF, X1LeftR, 0, PoseEase.Snap), M(r, X1LeftF, X1LeftR, 0), M(r + 0.2f, X1LeftF * 0.5f, X1LeftR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, Z(0f), T(a, -28, 8, -8, PoseEase.In), T(h, 30, -16, 8, PoseEase.Snap), T(f, 38, -20, 10, PoseEase.Over), T(r, 10, -5, 2), Z(end)),
            Turn(SpineD, Z(0f), T(a, -34, 10, -8, PoseEase.In), T(h, 38, -20, 10, PoseEase.Snap), T(f, 46, -24, 12, PoseEase.Over), T(r, 12, -6, 3), Z(end)),
            Turn(Head, Z(0f), T(a, 30, -6, 0, PoseEase.In), T(h, -14, 8, 0, PoseEase.Snap), T(f, -18, 10, 0, PoseEase.Over), T(r, -4, 2, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -14, 18, -10, PoseEase.In), T(h, 14, 10, 10, PoseEase.Snap), T(f, 18, 6, 12, PoseEase.Over), T(r, 4, 1, 3), Z(end)),
            Turn(LClav, Z(0f), T(a, -8, -4, 4, PoseEase.In), T(h, 10, -8, -6, PoseEase.Snap), T(f, 12, -8, -8, PoseEase.Over), T(r, 3, -2, -2), Z(end)),
            Upper("R", A(0f, 45, 110, w: 0f), A(a, 50, 115, PoseEase.In), A(h, -20, -30, PoseEase.Snap), A(f, -40, -55, PoseEase.Over), A(r, -40, -55, w: 0.3f), A(end, -40, -55, w: 0f)),
            Fore("R", A(0f, 60, 20, w: 0f), A(a, 65, 10, PoseEase.In), A(h, -50, -60, PoseEase.Snap), A(f, -65, -100, PoseEase.Over), A(r, -65, -100, w: 0.3f), A(end, -65, -100, w: 0f)),
            Turn(RHand, Z(0f), T(a, 0, 30, 0, PoseEase.In), T(h, 0, -20, 15, PoseEase.Snap), T(f, 0, -16, 20, PoseEase.Over), T(r, 0, -5, 6), Z(end)),
            Upper("L", A(0f, -20, -110, w: 0f), A(a, -5, -70, PoseEase.In, 0.8f), A(h, -25, -150, PoseEase.Snap, 0.9f), A(f, -15, -165, PoseEase.Over, 0.9f), A(r, -15, -165, w: 0.3f), A(end, -15, -165, w: 0f)),
            Fore("L", A(0f, -40, -100, w: 0f), A(a, -25, -50, PoseEase.In, 0.8f), A(h, -40, -170, PoseEase.Snap, 0.9f), A(f, -25, 175, PoseEase.Over, 0.9f), A(r, -25, 175, w: 0.3f), A(end, -25, 175, w: 0f)),
        ]);

    /// <summary>X2: the low gutting rake, from past the left hip up and across to above the right shoulder, the right foot stepping through (the sword's X2 at 1.25x: 1.146 s, hit 0.230 s).</summary>
    public static readonly WeaponPoseClip ClawX2 = Claw("claw_x2", 1.433f, 0.287f, 0.767f, "the sword's X2 (SM_MEL_PA_XATK_Combo2MsA_3.5m 1.433 s, hit 0.287 s)",
        "gutting rake: from the diagonal rake's end the body sinks further left over the bent lead leg with the claw low, then the right foot steps through and the hips and chest uncoil up and to the right as the talons tear upward through the belly to above the right shoulder; the pistol arm shoves forward low as a brace",
        antic: 0.08f, follow: 0.12f, recover: 0.75f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 14, -6, 3), T(a, 18, -10, 4, PoseEase.In), T(h, -18, 6, -8, PoseEase.Snap), T(f, -22, 8, -10, PoseEase.Over), T(r, -6, 2, -3), Z(end)),
            PelvisMove(M(0f, 0.10f, -0.03f, -0.06f), M(a, 0.06f, -0.10f, -0.18f, PoseEase.In), M(h, 0.16f, 0.10f, -0.04f, PoseEase.Snap), M(f, 0.18f, 0.12f, 0.0f, PoseEase.Over), M(r, 0.10f, 0.05f, -0.05f), M(end, 0, 0, 0)),
            Foot("L", M(0f, X1LeftF, X1LeftR, 0), M(r, X1LeftF, X1LeftR, 0), M(r + 0.2f, X1LeftF * 0.5f, X1LeftR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Foot("R", M(0f, 0, 0, 0), M(a, 0.12f, 0.10f, 0.12f, PoseEase.In), M(h, X2RightF, X2RightR, 0, PoseEase.Snap), M(r, X2RightF, X2RightR, 0), M(r + 0.2f, X2RightF * 0.5f, X2RightR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 24, -12, 4), T(a, 30, -18, 6, PoseEase.In), T(h, -28, 10, -10, PoseEase.Snap), T(f, -36, 14, -12, PoseEase.Over), T(r, -10, 4, -3), Z(end)),
            Turn(SpineD, T(0f, 28, -14, 6), T(a, 36, -22, 8, PoseEase.In), T(h, -34, 12, -12, PoseEase.Snap), T(f, -44, 16, -14, PoseEase.Over), T(r, -12, 4, -4), Z(end)),
            Turn(Head, T(0f, -12, 6, 0), T(a, -20, 14, 0, PoseEase.In), T(h, 16, -10, 0, PoseEase.Snap), T(f, 20, -12, 0, PoseEase.Over), T(r, 5, -3, 0), Z(end)),
            Turn(RClav, T(0f, 10, 4, 8), T(a, 14, -6, 10, PoseEase.In), T(h, -14, 18, -10, PoseEase.Snap), T(f, -16, 20, -12, PoseEase.Over), T(r, -4, 5, -3), Z(end)),
            Turn(LClav, T(0f, 8, -6, -6), T(a, 10, -8, -8, PoseEase.In), T(h, -10, 6, 6, PoseEase.Snap), T(f, -12, 6, 8, PoseEase.Over), T(r, -3, 2, 2), Z(end)),
            Upper("R", A(0f, -40, -55, w: 0.85f), A(a, -55, -45, PoseEase.In), A(h, 25, 50, PoseEase.Snap), A(f, 55, 85, PoseEase.Over), A(r, 55, 85, w: 0.3f), A(end, 55, 85, w: 0f)),
            Fore("R", A(0f, -65, -100, w: 0.85f), A(a, -60, -90, PoseEase.In), A(h, 45, 70, PoseEase.Snap), A(f, 70, 110, PoseEase.Over), A(r, 70, 110, w: 0.3f), A(end, 70, 110, w: 0f)),
            Turn(RHand, T(0f, 0, -16, 20), T(a, 0, -25, 25, PoseEase.In), T(h, 0, 20, -15, PoseEase.Snap), T(f, 0, 25, -20, PoseEase.Over), T(r, 0, 6, -6), Z(end)),
            Upper("L", A(0f, -15, -165, w: 0.7f), A(a, -30, -150, PoseEase.In, 0.8f), A(h, -30, -15, PoseEase.Snap, 0.9f), A(f, -25, -5, PoseEase.Over, 0.9f), A(r, -25, -5, w: 0.3f), A(end, -25, -5, w: 0f)),
            Fore("L", A(0f, -25, 175, w: 0.7f), A(a, -40, -170, PoseEase.In, 0.8f), A(h, -5, 0, PoseEase.Snap, 0.9f), A(f, 0, 0, PoseEase.Over, 0.9f), A(r, 0, 0, w: 0.3f), A(end, 0, 0, w: 0f)),
        ]);

    /// <summary>X3: the punching stab, the talons driven straight into the target's chest from the ribs with a long lunge of the left foot (the sword's X3 at 1.25x: 1.414 s, hit 0.346 s); the claw stays in until the rip.</summary>
    public static readonly WeaponPoseClip ClawX3 = Claw("claw_x3", 1.767f, 0.433f, 0.733f, "the sword's X3 (SM_MEL_CS_XATK_Combo3_3.5 1.767 s, hit 0.433 s)",
        "punching stab: the weight sinks onto the rear leg, the chest coils far right with the claw drawn back beside the ribs, talons forward, the pistol arm out as a guard; then the left foot lunges a long step ahead and the hips drive the whole body forward behind the arm as the talons punch into the target's chest; the claw stays buried, the body leaning in, until the rip",
        antic: 0.14f, follow: 0.115f, recover: 1.0f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, -6, 2, -2), T(a, -22, 6, -6, PoseEase.In), T(h, 18, -12, 4, PoseEase.Snap), T(f, 22, -14, 5, PoseEase.Over), T(0.62f, 22, -14, 5), T(r, 6, -4, 1), Z(end)),
            PelvisMove(M(0f, 0.10f, 0.05f, -0.05f), M(a, -0.08f, 0.08f, -0.10f, PoseEase.In), M(h, 0.30f, -0.02f, -0.18f, PoseEase.Snap), M(f, 0.34f, -0.03f, -0.20f, PoseEase.Over), M(0.62f, 0.34f, -0.03f, -0.20f), M(r, 0.10f, 0, -0.06f), M(end, 0, 0, 0)),
            Foot("L", M(0f, X1LeftF, X1LeftR, 0), M(a, X1LeftF + 0.10f, X1LeftR, 0.14f, PoseEase.In), M(h, X3LeftF, X1LeftR, 0, PoseEase.Snap), M(r, X3LeftF, X1LeftR, 0), M(r + 0.2f, X3LeftF * 0.5f, X1LeftR * 0.5f, 0.10f), M(end, 0, 0, 0)),
            Foot("R", M(0f, X2RightF, X2RightR, 0), M(r, X2RightF, X2RightR, 0), M(r + 0.25f, X2RightF * 0.5f, X2RightR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, -10, 4, -3), T(a, -34, 8, -8, PoseEase.In), T(h, 24, -18, 6, PoseEase.Snap), T(f, 30, -22, 8, PoseEase.Over), T(0.62f, 30, -22, 8), T(r, 8, -6, 2), Z(end)),
            Turn(SpineD, T(0f, -12, 4, -4), T(a, -40, 10, -10, PoseEase.In), T(h, 30, -22, 8, PoseEase.Snap), T(f, 36, -26, 10, PoseEase.Over), T(0.62f, 36, -26, 10), T(r, 10, -7, 3), Z(end)),
            Turn(Head, T(0f, 6, 0, 0), T(a, 34, -4, 0, PoseEase.In), T(h, -14, 6, 0, PoseEase.Snap), T(f, -18, 8, 0, PoseEase.Over), T(0.62f, -18, 8, 0), T(r, -5, 2, 0), Z(end)),
            Turn(RClav, T(0f, -4, 0, -4), T(a, -16, -8, -10, PoseEase.In), T(h, 16, 12, 10, PoseEase.Snap), T(f, 20, 14, 12, PoseEase.Over), T(0.62f, 20, 14, 12), T(r, 5, 4, 3), Z(end)),
            Turn(LClav, T(0f, 4, 2, 2), T(a, 12, 8, 6, PoseEase.In), T(h, -12, -6, -6, PoseEase.Snap), T(f, -14, -6, -8, PoseEase.Over), T(0.62f, -14, -6, -8), T(r, -4, -2, -2), Z(end)),
            Upper("R", A(0f, 55, 85, w: 0.7f), A(a, -25, 165, PoseEase.In), A(h, 0, 5, PoseEase.Snap), A(f, -5, 0, PoseEase.Over), A(0.62f, -5, 0), A(r, -5, 0, w: 0.3f), A(end, -5, 0, w: 0f)),
            Fore("R", A(0f, 70, 110, w: 0.7f), A(a, 10, 60, PoseEase.In), A(h, 0, 0, PoseEase.Snap), A(f, -5, -5, PoseEase.Over), A(0.62f, -5, -5), A(r, -5, -5, w: 0.3f), A(end, -5, -5, w: 0f)),
            Turn(RHand, T(0f, 0, 25, -20), T(a, 0, 10, 0, PoseEase.In), T(h, 0, 0, 0, PoseEase.Snap), T(f, 0, -5, 0, PoseEase.Over), T(0.62f, 0, -5, 0), T(r, 0, -2, 0), Z(end)),
            Upper("L", A(0f, -25, -5, w: 0.6f), A(a, -10, -15, PoseEase.In, 0.9f), A(h, -35, -120, PoseEase.Snap, 0.9f), A(f, -30, -150, PoseEase.Over, 0.9f), A(0.62f, -30, -150, w: 0.9f), A(r, -30, -150, w: 0.3f), A(end, -30, -150, w: 0f)),
            Fore("L", A(0f, 0, 0, w: 0.6f), A(a, 10, -5, PoseEase.In, 0.9f), A(h, -40, -160, PoseEase.Snap, 0.9f), A(f, -35, 170, PoseEase.Over, 0.9f), A(0.62f, -35, 170, w: 0.9f), A(r, -35, 170, w: 0.3f), A(end, -35, 170, w: 0f)),
        ]);

    /// <summary>X4: the tearing rip that ends the string: the buried talons twisted, then torn back and up past the right shoulder as the right foot steps back and the off hand shoves the target away (the sword's X4 at 1.25x: 1.334 s, hit 0.426 s).</summary>
    public static readonly WeaponPoseClip ClawX4 = Claw("claw_x4", 1.667f, 0.533f, float.NaN, "the sword's X4 (SM_MEL_CS_XATK_Combo4_3.5m 1.667 s, hit 0.533 s; the string ends)",
        "tearing rip: with the talons buried, the body leans in further and twists the claw deeper, the pistol hand planted on the target, the rear foot lifting; then the right foot stamps back, the hips wrench back and right and the talons tear out and up past the right shoulder while the off hand shoves the target away; a long recovery as the feet come back under him",
        antic: 0.16f, follow: 0.14f, recover: 0.90f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 20, -12, 4), T(a, 24, -16, 5, PoseEase.In), T(h, -22, 10, -8, PoseEase.Snap), T(f, -28, 12, -10, PoseEase.Over), T(r, -10, 4, -4), Z(end)),
            PelvisMove(M(0f, 0.34f, -0.03f, -0.20f), M(a, 0.40f, -0.02f, -0.22f, PoseEase.In), M(h, -0.05f, 0.06f, -0.06f, PoseEase.Snap), M(f, -0.12f, 0.08f, -0.04f, PoseEase.Over), M(r, -0.02f, 0.02f, -0.03f), M(end, 0, 0, 0)),
            Foot("L", M(0f, X3LeftF, X1LeftR, 0), M(r, X3LeftF, X1LeftR, 0), M(r + 0.2f, X3LeftF * 0.5f, X1LeftR * 0.5f, 0.10f), M(end, 0, 0, 0)),
            Foot("R", M(0f, X2RightF, X2RightR, 0), M(a, 0.20f, 0.22f, 0.12f, PoseEase.In), M(h, X4RightF, X4RightR, 0, PoseEase.Snap), M(r, X4RightF, X4RightR, 0), M(r + 0.2f, X4RightF * 0.5f, X4RightR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 28, -20, 7), T(a, 32, -28, 8, PoseEase.In), T(h, -30, 14, -10, PoseEase.Snap), T(f, -38, 18, -12, PoseEase.Over), T(r, -16, 8, -5), Z(end)),
            Turn(SpineD, T(0f, 34, -24, 9), T(a, 38, -32, 10, PoseEase.In), T(h, -38, 18, -12, PoseEase.Snap), T(f, -48, 22, -14, PoseEase.Over), T(r, -20, 10, -6), Z(end)),
            Turn(Head, T(0f, -16, 8, 0), T(a, -18, 10, 0, PoseEase.In), T(h, 18, -10, 0, PoseEase.Snap), T(f, 22, -12, 0, PoseEase.Over), T(r, 8, -4, 0), Z(end)),
            Turn(RClav, T(0f, 18, 14, 10), T(a, 20, 16, 12, PoseEase.In), T(h, -16, 20, -12, PoseEase.Snap), T(f, -20, 22, -14, PoseEase.Over), T(r, -8, 10, -6), Z(end)),
            Turn(LClav, T(0f, -12, -6, -6), T(a, -14, 0, -6, PoseEase.In), T(h, 10, -14, 8, PoseEase.Snap), T(f, 12, -16, 10, PoseEase.Over), T(r, 4, -6, 4), Z(end)),
            Upper("R", A(0f, -5, 0, w: 0.95f), A(a, -10, -5, PoseEase.In), A(h, 20, 110, PoseEase.Snap), A(f, 35, 135, PoseEase.Over), A(r, 35, 135, w: 0.55f), A(end, 35, 135, w: 0f)),
            Fore("R", A(0f, -5, -5, w: 0.95f), A(a, 0, -10, PoseEase.In), A(h, 40, 170, PoseEase.Snap), A(f, 50, -160, PoseEase.Over), A(r, 50, -160, w: 0.55f), A(end, 50, -160, w: 0f)),
            Turn(RHand, T(0f, 0, -5, 0), T(a, 0, -10, 40, PoseEase.In), T(h, 0, 20, 10, PoseEase.Snap), T(f, 0, 25, 0, PoseEase.Over), T(r, 0, 12, 0), Z(end)),
            Upper("L", A(0f, -30, -150, w: 0.9f), A(a, -15, -10, PoseEase.In, 0.95f), A(h, -5, 0, PoseEase.Snap, 0.95f), A(f, 0, 5, PoseEase.Over, 0.95f), A(r, 0, 5, w: 0.4f), A(end, 0, 5, w: 0f)),
            Fore("L", A(0f, -35, 170, w: 0.9f), A(a, 5, 0, PoseEase.In, 0.95f), A(h, 10, 0, PoseEase.Snap, 0.95f), A(f, 10, 0, PoseEase.Over, 0.95f), A(r, 10, 0, w: 0.4f), A(end, 10, 0, w: 0f)),
        ]);

    /// <summary>Y: the heavy, an overhead downward rake, the claw raised straight up behind the head and brought down through the target with the whole body folding over a stamping lead foot (the sword's Y backhand at 1.25x: 1.2 s, hit 0.264 s).</summary>
    public static readonly WeaponPoseClip ClawHeavy = Claw("claw_heavy", 1.5f, 0.33f, float.NaN, "the sword's Y backhand (SM_MEL_CS_XATK_Draw2_3.5m 1.5 s, hit 0.33 s)",
        "overhead downward rake: the body rears back onto the rear leg with the claw raised straight up and cocked behind the head, the pistol arm thrown back and the left foot lifting, then the left foot stamps down ahead and hips, chest and shoulder fold forward over it together as the talons rake down through the target to the ground; a heavy settle",
        antic: 0.10f, follow: 0.16f, recover: 0.85f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, Z(0f), T(a, -8, 12, -3, PoseEase.In), T(h, 8, -18, 3, PoseEase.Snap), T(f, 10, -24, 3, PoseEase.Over), T(r, 3, -8, 0), Z(end)),
            PelvisMove(M(0f, 0, 0, 0), M(a, -0.10f, 0.03f, -0.04f, PoseEase.In), M(h, 0.20f, -0.04f, -0.22f, PoseEase.Snap), M(f, 0.24f, -0.05f, -0.26f, PoseEase.Over), M(r, 0.10f, -0.02f, -0.10f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0, 0, 0), M(a, 0.10f, -0.05f, 0.15f, PoseEase.In), M(h, 0.45f, -0.10f, 0, PoseEase.Snap), M(r, 0.45f, -0.10f, 0), M(r + 0.2f, 0.22f, -0.05f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, Z(0f), T(a, -10, 16, -4, PoseEase.In), T(h, 8, -26, 2, PoseEase.Snap), T(f, 10, -34, 2, PoseEase.Over), T(r, 3, -10, 0), Z(end)),
            Turn(SpineD, Z(0f), T(a, -12, 20, -4, PoseEase.In), T(h, 10, -32, 2, PoseEase.Snap), T(f, 12, -40, 2, PoseEase.Over), T(r, 4, -12, 0), Z(end)),
            Turn(Head, Z(0f), T(a, 10, 10, 0, PoseEase.In), T(h, -6, -10, 0, PoseEase.Snap), T(f, -8, -8, 0, PoseEase.Over), T(r, -2, -2, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -6, 24, -8, PoseEase.In), T(h, 8, 14, 8, PoseEase.Snap), T(f, 10, 10, 10, PoseEase.Over), T(r, 3, 3, 3), Z(end)),
            Turn(LClav, Z(0f), T(a, 6, 10, 6, PoseEase.In), T(h, -6, -10, -6, PoseEase.Snap), T(f, -8, -12, -8, PoseEase.Over), T(r, -2, -4, -2), Z(end)),
            Upper("R", A(0f, 80, 60, w: 0f), A(a, 82, 60, PoseEase.In), A(h, -40, 10, PoseEase.Snap), A(f, -55, 5, PoseEase.Over), A(r, -55, 5, w: 0.3f), A(end, -55, 5, w: 0f)),
            Fore("R", A(0f, 55, 175, w: 0f), A(a, 50, 175, PoseEase.In), A(h, -65, 0, PoseEase.Snap), A(f, -80, -10, PoseEase.Over), A(r, -80, -10, w: 0.3f), A(end, -80, -10, w: 0f)),
            Turn(RHand, Z(0f), T(a, 0, 35, 0, PoseEase.In), T(h, 0, -35, 0, PoseEase.Snap), T(f, 0, -40, 0, PoseEase.Over), T(r, 0, -12, 0), Z(end)),
            Upper("L", A(0f, -20, -110, w: 0f), A(a, 15, -145, PoseEase.In, 0.9f), A(h, -40, -60, PoseEase.Snap, 0.9f), A(f, -50, -40, PoseEase.Over, 0.9f), A(r, -50, -40, w: 0.3f), A(end, -50, -40, w: 0f)),
            Fore("L", A(0f, -40, -100, w: 0f), A(a, 25, -175, PoseEase.In, 0.9f), A(h, -50, -70, PoseEase.Snap, 0.9f), A(f, -60, -60, PoseEase.Over, 0.9f), A(r, -60, -60, w: 0.3f), A(end, -60, -60, w: 0f)),
        ]);

    static WeaponPoseClip Claw(string name, float swordLength, float swordHit, float swordCombo, string source, string description, float antic, float follow, float recover, Func<float, float, float, float, float, PoseChannel[]> build)
    {
        float d = swordLength / ClawTempo, h = swordHit / ClawTempo;
        float a = h - antic, f = h + follow;
        var channels = build(a, h, f, recover, d).Select(c => HeldAt(c, a, ClawApexHold)).ToArray();
        return new WeaponPoseClip(name, "claw", d, h, ClawIdle, a, f, Math.Min(d, recover + 0.35f), float.IsNaN(swordCombo) ? float.NaN : swordCombo / ClawTempo, source, description, channels);
    }

    /// <summary>The claw's wind-up sits at its apex this long before the strike snaps (s; ours: two frames, so the cocked pose reads before the arc).</summary>
    public const float ClawApexHold = 0.035f;

    /// <summary>The channel with its key at <paramref name="at"/> held for <paramref name="hold"/> seconds (a second key with the same values, <see cref="PoseEase.Hold"/>); unchanged when no key is there.</summary>
    static PoseChannel HeldAt(PoseChannel channel, float at, float hold)
    {
        int i = Array.FindIndex(channel.Keys, k => MathF.Abs(k.T - at) < 1e-4f);
        if (i < 0) return channel;
        var held = channel.Keys[i] with { T = at + hold, Ease = PoseEase.Hold };
        return channel with { Keys = [.. channel.Keys[..(i + 1)], held, .. channel.Keys[(i + 1)..]] };
    }

    // ---- the power spear (carried in the right hand; both hands swing it: the right hand rear, the left forward; the stand-in's blade is beyond the left hand) ----
    // Sixth pass (owner, 2026-11-10, on his reference clip of a halberd's "Wrath hew from Field Guard"): "the torso should remain intact and not bend much - straight back the entire time",
    // "doesn't match the reference, still too stiff / small", "the blade needs to face forward at rest". So the back stays straight: the spine's own keys are small (SpineLimitPitch,
    // SpineLimitRoll, pinned by the tests) and every lean and turn is the pelvis's, which carries the whole torso as one piece (CharacterViewer.WeaponPose: RIG_SpineA hangs beside
    // RIG_Pelvis under the root, so the overlay moves it with the pelvis; before, the torso stayed behind and the legs came away from the body). The power is the hips' turn, the deep
    // wide stance (the pelvis dropping to 0.3 m, a 0.6 to 0.85 m step) and the arms' full reach; the haft travels the reference's big overhead circle. Each light strike is a different
    // hewing cut; the F, F chain is the hammer's Y interrupt (a snap cut) then its slam (a massive lunging stab). The carry holds the shaft high in one hand and slides it low for melee.

    /// <summary>The spine's own keys stay within these (degrees) so the back reads straight; the lean and the turn come from the pelvis (owner, 2026-11-10).</summary>
    public const float SpineLimitPitch = 8f, SpineLimitRoll = 5f, SpineLimitYaw = 16f;

    /// <summary>A grip key: how far the left hand is on the shaft (0 off, 1 on).</summary>
    static PoseKey G(float t, float on, PoseEase ease = PoseEase.Smooth) => new(t, 0f, 0f, 0f, ease, on);
    /// <summary>A grip-point key: where the right hand holds the shaft, metres from the butt.</summary>
    static PoseKey P(float t, float fromButt, PoseEase ease = PoseEase.Smooth) => new(t, fromButt, 0f, 0f, ease, 1f);
    /// <summary>How long the shaft takes to slide through the hand to the melee grip at a string's start, and back to the carry at its end (s; ours: quick and visible).</summary>
    public const float GripSlide = 0.25f, GripSlideBack = 0.4f;

    /// <summary>The carry: the right arm hanging with the shaft at the right side (the shaft leans forward-up from this hand at the carry grip, <see cref="SpearHold.Place"/>, the blade's edge forward); the left arm hangs free (aims, el / az).</summary>
    const float RestRUpperEl = -75f, RestRUpperAz = 60f, RestRForeEl = -70f, RestRForeAz = 40f, RestLUpperEl = -70f, RestLUpperAz = -20f, RestLForeEl = -50f, RestLForeAz = -25f;
    static PoseKey RestRUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRUpperEl, RestRUpperAz, ease);
    static PoseKey RestRFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRForeEl, RestRForeAz, ease);
    static PoseKey RestLUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLUpperEl, RestLUpperAz, ease);
    static PoseKey RestLFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLForeEl, RestLForeAz, ease);
    /// <summary>The hands on their way back to the carry after a strike (the left hand leaving the shaft): the recovery key's aims.</summary>
    static PoseKey RecRUpper(float t) => A(t, -55, 50); static PoseKey RecRFore(float t) => A(t, -50, 35); static PoseKey RecLUpper(float t) => A(t, -45, -30); static PoseKey RecLFore(float t) => A(t, -25, -40);
    /// <summary>The field guard the hews end in (the reference's stance: wide and deep, the right hand at the right hip, the left hand forward and low, the blade forward and a little down): the hands' aims.</summary>
    static PoseKey GuardRUpper(float t, PoseEase e = PoseEase.Smooth) => A(t, -55, 35, e); static PoseKey GuardRFore(float t, PoseEase e = PoseEase.Smooth) => A(t, -30, 15, e);
    static PoseKey GuardLUpper(float t, PoseEase e = PoseEase.Smooth) => A(t, -35, -10, e); static PoseKey GuardLFore(float t, PoseEase e = PoseEase.Smooth) => A(t, -20, -5, e);
    /// <summary>The field guard's hips: leaning forward a little from the hips, dropped deep, the left foot a long step ahead (the reference's wide stance).</summary>
    const float GuardPelvisPitch = -10f, GuardDrop = -0.26f, GuardForward = 0.16f;
    /// <summary>Where the hews' steps leave the feet (metres forward, right from the stance's spots): after the first hew the left foot, after the second the right, the third's left pivot step.</summary>
    const float H1LeftF = 0.65f, H1LeftR = -0.15f, H2RightF = 0.60f, H2RightR = 0.22f, H3LeftF = 0.60f, H3LeftR = -0.30f;

    /// <summary>The spear's carry (owner, 2026-11-10: "carried 1 handed in the right hand when running or idling", the hand where the butt is just above the ground), held over the hammer's idle while the spear is not swung; every strike starts from it and the string's last returns to it.</summary>
    public static readonly WeaponPoseClip SpearRest = new("spear_rest", "spear", SpearIdleLength, float.NaN, SpearIdle, 0f, 0f, 0f, float.NaN,
        "the hammer's idle (SM_2HM_NAV_TH_IdleL_S_01 1.5 s; the pose is held while it loops)",
        "the carry: the spear in the right hand alone at the right side, the butt just above the ground and the blade up and forward over the shoulder, its edge facing the way he faces; the left arm hanging free",
        [
            Turn(SpineD, T(0f, 4, 0, 0), T(SpearIdleLength, 4, 0, 0)),
            Turn(Head, T(0f, -4, 0, 0), T(SpearIdleLength, -4, 0, 0)),
            Upper("R", RestRUpper(0f), RestRUpper(SpearIdleLength)),
            Fore("R", RestRFore(0f), RestRFore(SpearIdleLength)),
            Upper("L", RestLUpper(0f), RestLUpper(SpearIdleLength)),
            Fore("L", RestLFore(0f), RestLFore(SpearIdleLength)),
        ])
    { Grip = [G(0f, 0f), G(SpearIdleLength, 0f)], GripPoint = [P(0f, WeaponPoseClip.CarryGrip), P(SpearIdleLength, WeaponPoseClip.CarryGrip)] };

    /// <summary>Hew 1, the wrath hew (the reference): from the carry both hands rise into the steer, the haft upright overhead; the haft swings around behind the right shoulder as the hips coil; the cut comes down hard and diagonally from the right shoulder to low left, into the field guard (the hammer's X1 at 0.85x: 3.138 s, hit 0.711 s).</summary>
    public static readonly WeaponPoseClip SpearHew1 = Spear("spear_hew1", 2.667f, 0.604f, 0.933f, SpearIdle, "the hammer's X1 (SM_MEL_TH_XATK_Combo2 2.667 s, hit 0.604 s)",
        "wrath hew: the shaft slides low through the right hand as the left hand takes it and both hands rise into the steer, the haft upright overhead and the knees sinking; the haft swings around over the head and behind the right shoulder as the hips coil right and the left foot lifts; the left foot stamps a long step ahead and the hips drive the cut down hard and diagonally from the right shoulder to low left, the arms extended, the back straight, ending in the deep wide field guard",
        antic: 0.19f, follow: 0.29f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, Z(0f), T(a - 0.19f, -6, 4, 0), T(a, -34, 6, 0, PoseEase.In), T(h, 28, -16, 0, PoseEase.Snap), T(f, 14, GuardPelvisPitch, 0, PoseEase.Over), T(1.6f, 10, GuardPelvisPitch, 0), T(r, 3, -3, 0), Z(end)),
            PelvisMove(M(0f, 0, 0, 0), M(a - 0.19f, 0.02f, 0.02f, -0.10f), M(a, -0.10f, 0.10f, -0.14f, PoseEase.In), M(h, 0.22f, -0.06f, -0.30f, PoseEase.Snap), M(f, GuardForward, -0.06f, GuardDrop, PoseEase.Over), M(1.6f, GuardForward, -0.04f, GuardDrop), M(r, 0.06f, -0.02f, -0.08f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0, 0, 0), M(a, 0.15f, -0.10f, 0.16f, PoseEase.In), M(h, H1LeftF, H1LeftR, 0, PoseEase.Snap), M(r, H1LeftF, H1LeftR, 0), M(r + 0.3f, H1LeftF * 0.5f, H1LeftR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, Z(0f), T(a, -8, 2, 0, PoseEase.In), T(h, 8, -4, 0, PoseEase.Snap), T(f, 6, -4, 0, PoseEase.Over), T(r, 2, -1, 0), Z(end)),
            Turn(SpineD, Z(0f), T(a, -8, 2, 0, PoseEase.In), T(h, 8, -4, 0, PoseEase.Snap), T(f, 6, -4, 0, PoseEase.Over), T(r, 2, -1, 0), Z(end)),
            Turn(Head, Z(0f), T(a - 0.19f, 4, 8, 0), T(a, 46, 2, 0, PoseEase.In), T(h, -36, 6, 0, PoseEase.Snap), T(f, -20, 8, 0, PoseEase.Over), T(1.6f, -14, 6, 0), T(r, -4, 2, 0), Z(end)),
            Turn(LClav, Z(0f), T(a - 0.19f, 0, 16, 0), T(a, -10, 12, 0, PoseEase.In), T(h, 14, 4, 0, PoseEase.Snap), T(f, 14, 2, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a - 0.19f, 0, 16, 0), T(a, -10, 12, 0, PoseEase.In), T(h, 14, 4, 0, PoseEase.Snap), T(f, 14, 2, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            // The steer (a - 0.19: both hands straight up, the haft upright), the swing-around (a: the blade behind the right shoulder, high), the cut (h: high right to low left, arms long), the field guard (f, held to 1.6).
            Upper("R", RestRUpper(0f), A(a - 0.19f, 72, 35), A(a, 15, 95, PoseEase.In), A(h, -45, 25, PoseEase.Snap), GuardRUpper(f, PoseEase.Over), GuardRUpper(1.6f), RecRUpper(r), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(a - 0.19f, 82, 0), A(a, 45, 150, PoseEase.In), A(h, -35, -10, PoseEase.Snap), GuardRFore(f, PoseEase.Over), GuardRFore(1.6f), RecRFore(r), RestRFore(end)),
            Upper("L", RestLUpper(0f), A(a - 0.19f, 76, -20), A(a, 40, 140, PoseEase.In), A(h, -35, -45, PoseEase.Snap), GuardLUpper(f, PoseEase.Over), GuardLUpper(1.6f), RecLUpper(r), RestLUpper(end)),
            Fore("L", RestLFore(0f), A(a - 0.19f, 86, 0), A(a, 40, 175, PoseEase.In), A(h, -35, -50, PoseEase.Snap), GuardLFore(f, PoseEase.Over), GuardLFore(1.6f), RecLFore(r), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 0f), G(GripSlide, 1f, PoseEase.In), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.CarryGrip), P(GripSlide, WeaponPoseClip.LowGrip, PoseEase.Out), P(r, WeaponPoseClip.LowGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>Hew 2, the rising hew: from the field guard the blade is dropped low and left behind the hip, then torn up and across to high over the right shoulder as the right foot steps through (the hammer's X2 at 0.85x: 2.745 s, hit 0.667 s).</summary>
    public static readonly WeaponPoseClip SpearHew2 = Spear("spear_hew2", 2.333f, 0.567f, 0.967f, SpearIdle, "the hammer's X2 (SM_MEL_TH_XATK_Combo3 2.333 s, hit 0.567 s)",
        "rising hew: from the field guard the hips turn left and sink deeper over the bent lead leg as the blade drops low and left behind the hip, then the right foot steps through and the hips uncoil hard to the right as the blade is torn up and across to high over the right shoulder, the arms long, the back straight",
        antic: 0.18f, follow: 0.28f, recover: 1.9f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 10, GuardPelvisPitch, 0), T(a, 30, -12, 0, PoseEase.In), T(h, -28, -4, 0, PoseEase.Snap), T(f, -34, 0, 0, PoseEase.Over), T(r, -8, 0, 0), Z(end)),
            PelvisMove(M(0f, GuardForward, -0.04f, GuardDrop), M(a, 0.10f, -0.14f, -0.30f, PoseEase.In), M(h, 0.30f, 0.10f, -0.14f, PoseEase.Snap), M(f, 0.34f, 0.14f, -0.08f, PoseEase.Over), M(r, 0.10f, 0.03f, -0.06f), M(end, 0, 0, 0)),
            Foot("L", M(0f, H1LeftF, H1LeftR, 0), M(r, H1LeftF, H1LeftR, 0), M(r + 0.3f, H1LeftF * 0.5f, H1LeftR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Foot("R", M(0f, 0, 0, 0), M(a, 0.20f, 0.15f, 0.16f, PoseEase.In), M(h, H2RightF, H2RightR, 0, PoseEase.Snap), M(r, H2RightF, H2RightR, 0), M(r + 0.3f, H2RightF * 0.5f, H2RightR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 6, -4, 0), T(a, 8, -4, 0, PoseEase.In), T(h, -8, 2, 0, PoseEase.Snap), T(f, -8, 4, 0, PoseEase.Over), T(r, -2, 1, 0), Z(end)),
            Turn(SpineD, T(0f, 6, -4, 0), T(a, 8, -4, 0, PoseEase.In), T(h, -8, 2, 0, PoseEase.Snap), T(f, -8, 4, 0, PoseEase.Over), T(r, -2, 1, 0), Z(end)),
            Turn(Head, T(0f, -14, 6, 0), T(a, -38, 8, 0, PoseEase.In), T(h, 30, -6, 0, PoseEase.Snap), T(f, 36, -10, 0, PoseEase.Over), T(r, 8, -2, 0), Z(end)),
            Turn(LClav, T(0f, 4, 0, 0), T(a, 12, -4, 0, PoseEase.In), T(h, -14, 14, 0, PoseEase.Snap), T(f, -16, 16, 0, PoseEase.Over), T(r, -4, 3, 0), Z(end)),
            Turn(RClav, T(0f, 4, 0, 0), T(a, 12, -4, 0, PoseEase.In), T(h, -14, 14, 0, PoseEase.Snap), T(f, -16, 16, 0, PoseEase.Over), T(r, -4, 3, 0), Z(end)),
            // From the guard: the blade dropped low-left-back (a), the rising cut (h: the left hand high right, the right hand low left, the haft diagonal), ending high over the right shoulder (f).
            Upper("R", GuardRUpper(0f), A(a, -30, 30, PoseEase.In), A(h, -40, -15, PoseEase.Snap), A(f, -10, -35, PoseEase.Over), RecRUpper(r), RestRUpper(end)),
            Fore("R", GuardRFore(0f), A(a, -10, 0, PoseEase.In), A(h, -40, -45, PoseEase.Snap), A(f, -25, -65, PoseEase.Over), RecRFore(r), RestRFore(end)),
            Upper("L", GuardLUpper(0f), A(a, -50, -75, PoseEase.In), A(h, 30, 40, PoseEase.Snap), A(f, 45, 90, PoseEase.Over), RecLUpper(r), RestLUpper(end)),
            Fore("L", GuardLFore(0f), A(a, -45, -115, PoseEase.In), A(h, 50, 60, PoseEase.Snap), A(f, 55, 120, PoseEase.Over), RecLFore(r), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 1f), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.LowGrip), P(r, WeaponPoseClip.LowGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>Hew 3, the reverse hew that ends the string: the haft looped overhead from high right round to the left, then a flat hew at waist height from left to right with a pivot step, settling into the field guard and back to the carry (the hammer's X3 at 0.85x: 2.745 s, hit 0.824 s).</summary>
    public static readonly WeaponPoseClip SpearHew3 = Spear("spear_hew3", 2.333f, 0.700f, float.NaN, SpearIdle, "the hammer's X3 (SM_MEL_TH_HeavyATK02 2.333 s, hit 0.700 s; the string ends)",
        "reverse hew: from high over the right shoulder the haft loops over the head to the left as the hips coil left and the left foot lifts, then the left foot stamps down forward-left and the hips pivot hard right as a flat hew at waist height carries the blade from the left across the front and on to the right, the arms long and the back straight; it settles into the field guard, the feet come back under him and the shaft slides back up to the carry",
        antic: 0.20f, follow: 0.18f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, -10, 0, 0), T(a, 36, 2, 0, PoseEase.In), T(h, -12, -10, 0, PoseEase.Linear), T(f, -38, -10, 0, PoseEase.Out), T(f + 0.25f, -40, -8, 0, PoseEase.Over), T(1.6f, -12, GuardPelvisPitch, 0), T(r, -4, -3, 0), Z(end)),
            PelvisMove(M(0f, 0.10f, 0.03f, -0.06f), M(a, 0.14f, -0.16f, -0.16f, PoseEase.In), M(h, 0.34f, 0.04f, -0.28f, PoseEase.Linear), M(f, 0.38f, 0.14f, -0.26f, PoseEase.Out), M(f + 0.25f, 0.38f, 0.14f, -0.26f, PoseEase.Over), M(1.6f, GuardForward, 0.04f, GuardDrop), M(r, 0.08f, 0, -0.08f), M(end, 0, 0, 0)),
            Foot("L", M(0f, H1LeftF, H1LeftR, 0), M(a, H1LeftF + 0.05f, H1LeftR - 0.05f, 0.16f, PoseEase.In), M(h, H3LeftF, H3LeftR, 0, PoseEase.Linear), M(r, H3LeftF, H3LeftR, 0), M(r + 0.3f, H3LeftF * 0.5f, H3LeftR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Foot("R", M(0f, H2RightF, H2RightR, 0), M(r, H2RightF, H2RightR, 0), M(r + 0.35f, H2RightF * 0.5f, H2RightR * 0.5f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, -4, 0, 0), T(a, 8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Linear), T(f, -8, -4, 0, PoseEase.Out), T(f + 0.25f, -8, -4, 0, PoseEase.Over), T(1.6f, -4, -4, 0), T(r, -2, -1, 0), Z(end)),
            Turn(SpineD, T(0f, -4, 0, 0), T(a, 8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Linear), T(f, -8, -4, 0, PoseEase.Out), T(f + 0.25f, -8, -4, 0, PoseEase.Over), T(1.6f, -4, -4, 0), T(r, -2, -1, 0), Z(end)),
            Turn(Head, T(0f, 10, -4, 0), T(a, -46, 0, 0, PoseEase.In), T(h, 12, 4, 0, PoseEase.Linear), T(f, 44, 6, 0, PoseEase.Out), T(f + 0.25f, 46, 6, 0, PoseEase.Over), T(1.6f, 14, 6, 0), T(r, 4, 2, 0), Z(end)),
            Turn(LClav, T(0f, -4, 6, 0), T(a, 14, 10, 0, PoseEase.In), T(h, -6, 4, 0, PoseEase.Linear), T(f, -16, 4, 0, PoseEase.Out), T(f + 0.25f, -18, 4, 0, PoseEase.Over), T(r, -4, 1, 0), Z(end)),
            Turn(RClav, T(0f, -4, 6, 0), T(a, 14, 10, 0, PoseEase.In), T(h, -6, 4, 0, PoseEase.Linear), T(f, -16, 4, 0, PoseEase.Out), T(f + 0.25f, -18, 4, 0, PoseEase.Over), T(r, -4, 1, 0), Z(end)),
            // From high right (0): the overhead loop to the left (a: the blade left-back at shoulder height, the right hand up by the head), the waist-height hew through the front (h: both arms low and long, the blade forward-right), on to the right (f), the field guard (1.6).
            Upper("R", A(0f, -10, -35), A(a, 10, 30, PoseEase.In), A(h, -50, -5, PoseEase.Linear), A(f, -40, -20, PoseEase.Out), A(f + 0.25f, -40, -15, PoseEase.Over), GuardRUpper(1.6f), RecRUpper(r), RestRUpper(end)),
            Fore("R", A(0f, -25, -65), A(a, 30, 60, PoseEase.In), A(h, -30, -60, PoseEase.Linear), A(f, -20, -40, PoseEase.Out), A(f + 0.25f, -20, -35, PoseEase.Over), GuardRFore(1.6f), RecRFore(r), RestRFore(end)),
            Upper("L", A(0f, 45, 90), A(a, 20, -120, PoseEase.In), A(h, -40, 10, PoseEase.Linear), A(f, -30, 100, PoseEase.Out), A(f + 0.25f, -30, 105, PoseEase.Over), GuardLUpper(1.6f), RecLUpper(r), RestLUpper(end)),
            Fore("L", A(0f, 55, 120), A(a, 10, -150, PoseEase.In), A(h, -20, 20, PoseEase.Linear), A(f, -20, 140, PoseEase.Out), A(f + 0.25f, -20, 145, PoseEase.Over), GuardLFore(1.6f), RecLFore(r), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 1f), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.LowGrip), P(r, WeaponPoseClip.LowGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>The F stun strike: a quick snap cut at head height, right to left, from the carry (the hammer's Y "Interrupt Attack" SM_MEL_TH_Ytap_ATK at 0.85x: 1.961 s, hit 0.509 s; a second F in its window chains to the stab).</summary>
    public static readonly WeaponPoseClip SpearStun = Spear("spear_stun", 1.667f, 0.433f, 0.533f, SpearIdle, "the hammer's Y interrupt (SM_MEL_TH_Ytap_ATK 1.667 s, hit 0.433 s; chains to the slam at 0.533 s)",
        "stun strike: the shaft slides low through the hand as the left hand takes it and the haft is whipped up behind the right shoulder with a short coil of the hips, then a fast flat snap cut at head height from right to left with a half step of the left foot, the back straight; the hips stay coiled forward, ready for the stab",
        antic: 0.14f, follow: 0.12f, recover: 1.3f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, Z(0f), T(a, -28, 4, 0, PoseEase.In), T(h, 22, -8, 0, PoseEase.Snap), T(f, 30, -8, 0, PoseEase.Over), T(r, 8, -3, 0), Z(end)),
            PelvisMove(M(0f, 0, 0, 0), M(a, -0.06f, 0.08f, -0.12f, PoseEase.In), M(h, 0.14f, -0.04f, -0.18f, PoseEase.Snap), M(f, 0.16f, -0.05f, -0.18f, PoseEase.Over), M(r, 0.06f, -0.02f, -0.08f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0, 0, 0), M(a, 0.10f, -0.05f, 0.12f, PoseEase.In), M(h, 0.30f, -0.10f, 0, PoseEase.Snap), M(r, 0.30f, -0.10f, 0), M(r + 0.3f, 0.15f, -0.05f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, Z(0f), T(a, -8, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Snap), T(f, 8, -3, 0, PoseEase.Over), T(r, 2, -1, 0), Z(end)),
            Turn(SpineD, Z(0f), T(a, -8, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Snap), T(f, 8, -3, 0, PoseEase.Over), T(r, 2, -1, 0), Z(end)),
            Turn(Head, Z(0f), T(a, 40, -4, 0, PoseEase.In), T(h, -30, 4, 0, PoseEase.Snap), T(f, -36, 4, 0, PoseEase.Over), T(r, -8, 0, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -12, 10, 0, PoseEase.In), T(h, 14, 8, 0, PoseEase.Snap), T(f, 16, 8, 0, PoseEase.Over), T(r, 4, 2, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -12, 10, 0, PoseEase.In), T(h, 14, 8, 0, PoseEase.Snap), T(f, 16, 8, 0, PoseEase.Over), T(r, 4, 2, 0), Z(end)),
            // The blade behind the right shoulder at head height (a), the flat cut through the front (h: the blade forward-left at head height), on to the left (f).
            Upper("R", RestRUpper(0f), A(a, 20, -40, PoseEase.In), A(h, 0, 100, PoseEase.Snap), A(f, 10, 70, PoseEase.Over), RecRUpper(r), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(a, 30, -100, PoseEase.In), A(h, 10, 150, PoseEase.Snap), A(f, 20, 40, PoseEase.Over), RecRFore(r), RestRFore(end)),
            Upper("L", RestLUpper(0f), A(a, 25, 115, PoseEase.In), A(h, 5, -20, PoseEase.Snap), A(f, 10, -110, PoseEase.Over), RecLUpper(r), RestLUpper(end)),
            Fore("L", RestLFore(0f), A(a, 25, 165, PoseEase.In), A(h, 10, -40, PoseEase.Snap), A(f, 0, -160, PoseEase.Over), RecLFore(r), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 0f), G(GripSlide, 1f, PoseEase.In), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.CarryGrip), P(GripSlide, WeaponPoseClip.LowGrip, PoseEase.Out), P(r, WeaponPoseClip.LowGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>The F, F finisher: the massive stab, a whole-body lunging thrust with the hammer slam's timing (SM_MEL_TH_YATK_Displace2 at 0.85x: 3.529 s, hit 1.294 s); the hands at the butt for the full reach, the blade held in the target, then wrenched out.</summary>
    public static readonly WeaponPoseClip SpearStab = Spear("spear_stab", 3.0f, 1.1f, float.NaN, SpearIdle, "the hammer's Y,Y slam (SM_MEL_TH_YATK_Displace2 3.0 s, hit 1.1 s; the chain ends)",
        "massive stab: from the stun's end the spear is drawn far back along the right hip with the blade levelled at the target and both hands at the butt, the hips coiling back and sinking onto the rear leg with the left foot lifting, the head fixed on the target; then the left foot stamps a long stride ahead and the hips drive the whole body, back straight, behind the blade in one lunging thrust, the left arm fully extended; the blade is held in the target as the body settles over the lunge, then wrenched out and the shaft slid back up to the carry",
        antic: 0.20f, follow: 0.20f, recover: 2.6f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 22, -6, 0), T(0.6f, -30, 6, 0), T(a, -40, 8, 0, PoseEase.In), T(h, -8, -16, 0, PoseEase.Snap), T(f, -6, -18, 0, PoseEase.Over), T(1.9f, -6, -18, 0), T(r, -2, -6, 0), Z(end)),
            PelvisMove(M(0f, 0.14f, -0.04f, -0.16f), M(0.6f, -0.14f, 0.10f, -0.14f), M(a, -0.22f, 0.14f, -0.18f, PoseEase.In), M(h, 0.45f, -0.06f, -0.30f, PoseEase.Snap), M(f, 0.50f, -0.08f, -0.32f, PoseEase.Over), M(1.9f, 0.50f, -0.08f, -0.32f), M(r, 0.14f, -0.02f, -0.10f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0.30f, -0.10f, 0), M(0.6f, 0.20f, -0.10f, 0.10f), M(a, 0.15f, -0.12f, 0.18f, PoseEase.In), M(h, 0.85f, -0.15f, 0, PoseEase.Snap), M(1.9f, 0.85f, -0.15f, 0), M(r, 0.85f, -0.15f, 0), M(r + 0.3f, 0.42f, -0.07f, 0.10f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 6, -2, 0), T(0.6f, -8, 2, 0), T(a, -8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Snap), T(f, -4, -4, 0, PoseEase.Over), T(1.9f, -4, -4, 0), T(r, -1, -1, 0), Z(end)),
            Turn(SpineD, T(0f, 6, -2, 0), T(0.6f, -8, 2, 0), T(a, -8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Snap), T(f, -4, -4, 0, PoseEase.Over), T(1.9f, -4, -4, 0), T(r, -1, -1, 0), Z(end)),
            Turn(Head, T(0f, -26, 4, 0), T(0.6f, 40, -4, 0), T(a, 50, -6, 0, PoseEase.In), T(h, 12, 8, 0, PoseEase.Snap), T(f, 10, 10, 0, PoseEase.Over), T(1.9f, 10, 10, 0), T(r, 3, 2, 0), Z(end)),
            Turn(LClav, T(0f, 14, 8, 0), T(0.6f, -14, 6, 0), T(a, -18, 8, 0, PoseEase.In), T(h, 16, 12, 0, PoseEase.Snap), T(f, 18, 14, 0, PoseEase.Over), T(1.9f, 18, 14, 0), T(r, 4, 4, 0), Z(end)),
            Turn(RClav, T(0f, 14, 8, 0), T(0.6f, -14, 6, 0), T(a, -18, 8, 0, PoseEase.In), T(h, 16, 12, 0, PoseEase.Snap), T(f, 18, 14, 0, PoseEase.Over), T(1.9f, 18, 14, 0), T(r, 4, 4, 0), Z(end)),
            // From the stun's end: the spear drawn back along the right hip, blade forward (0.6, a), the thrust (h: the left arm straight ahead, the right hand at the chest, the haft level), held (1.9), out and back to the carry.
            Upper("R", A(0f, 10, 70), A(0.6f, -30, 120), A(a, -35, 135, PoseEase.In), A(h, -35, 40, PoseEase.Snap), A(f, -30, 40, PoseEase.Over), A(1.9f, -30, 40), RecRUpper(r), RestRUpper(end)),
            Fore("R", A(0f, 20, 40), A(0.6f, -10, 60), A(a, -10, 50, PoseEase.In), A(h, 5, -40, PoseEase.Snap), A(f, 5, -40, PoseEase.Over), A(1.9f, 5, -40), RecRFore(r), RestRFore(end)),
            Upper("L", A(0f, 10, -110), A(0.6f, -20, 60), A(a, -25, 70, PoseEase.In), A(h, 0, 5, PoseEase.Snap), A(f, 0, 5, PoseEase.Over), A(1.9f, 0, 5), RecLUpper(r), RestLUpper(end)),
            Fore("L", A(0f, 0, -160), A(0.6f, 0, 30), A(a, 0, 25, PoseEase.In), A(h, 0, 0, PoseEase.Snap), A(f, 0, 0, PoseEase.Over), A(1.9f, 0, 0), RecLFore(r), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 1f), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.LowGrip), P(0.6f, WeaponPoseClip.StabGrip), P(r, WeaponPoseClip.StabGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>The dive: both hands high on the shaft over the head, the left (blade) hand lower, so the spear hangs point-down and forward (held for the whole of the hammer's dive loop).</summary>
    public static readonly WeaponPoseClip SpearDive = new("spear_dive", "spear", SpearDiveLoopLength, float.NaN, SpearDiveLoop, 0f, 0f, 0f, float.NaN,
        "the hammer's dive loop (SM_JMP_TH_GP_Loop_01 1.767 s; the pose is held while it plays)",
        "the dive: the spear is turned point-down over the head, both hands high on the shaft, the body leaning into the fall",
        [
            Turn(Pelvis, T(0f, 0, -6, 0), T(SpearDiveLoopLength, 0, -6, 0)),
            Turn(SpineA, T(0f, 0, -3, 0), T(SpearDiveLoopLength, 0, -3, 0)),
            Turn(SpineD, T(0f, 0, -3, 0), T(SpearDiveLoopLength, 0, -3, 0)),
            Turn(Head, T(0f, 0, -10, 0), T(SpearDiveLoopLength, 0, -10, 0)),
            Upper("R", A(0f, 60, 10), A(SpearDiveLoopLength, 60, 10)),
            Fore("R", A(0f, 80, 0), A(SpearDiveLoopLength, 80, 0)),
            Upper("L", A(0f, 20, -10), A(SpearDiveLoopLength, 20, -10)),
            Fore("L", A(0f, -10, 0), A(SpearDiveLoopLength, -10, 0)),
        ]);

    /// <summary>
    /// The impaling landing (the hammer's landing clip, 1.067 s): the impale lands with the landing (hit 0 s; design section 6.2), the drive-down snaps over the first 0.08 s, the spear pins
    /// the target until 0.5 s (the owner kept the release at 0.5 s, 2026-11-10), is pulled out by 0.75 s and the pose is back on the get-up at the clip's end.
    /// </summary>
    public static readonly WeaponPoseClip SpearImpale = new("spear_impale", "spear", SpearDiveImpactLength, 0f, SpearDiveImpact, 0f, 0.08f, 1.0f, float.NaN,
        "the hammer's landing (SM_JMP_TH_GP_Impact_01 1.067 s; the impale at the landing, 0 s)",
        "impaling landing: the point-down spear is driven into the ground at the feet as the marine lands, held pinned, then wrenched free",
        [
            // The lean into the ground is the hips' (the whole torso as one piece, the back straight); the spine adds a little.
            Turn(Pelvis, T(0f, 0, -4, 0), T(0.08f, 0, -26, 0, PoseEase.Snap), T(0.5f, 0, -28, 0), T(0.75f, 0, -8, 0), Z(SpearDiveImpactLength)),
            Turn(SpineA, T(0f, 0, -2, 0), T(0.08f, 0, -6, 0, PoseEase.Snap), T(0.5f, 0, -7, 0), T(0.75f, 0, -3, 0), Z(SpearDiveImpactLength)),
            Turn(SpineD, T(0f, 0, -2, 0), T(0.08f, 0, -6, 0, PoseEase.Snap), T(0.5f, 0, -7, 0), T(0.75f, 0, -3, 0), Z(SpearDiveImpactLength)),
            Turn(Head, T(0f, 0, -10, 0), T(0.08f, 0, -12, 0, PoseEase.Snap), T(0.5f, 0, -8, 0), T(0.75f, 0, 2, 0), Z(SpearDiveImpactLength)),
            // The rear (right) hand stays high by the chest, the forward (left) hand drives straight down to knee height, so the blade stands in the ground a little ahead of the feet.
            Upper("R", A(0f, 60, 10), A(0.08f, -30, 60, PoseEase.Snap), A(0.5f, -35, 60), A(0.75f, -10, 60), A(SpearDiveImpactLength, -10, 60, w: 0f)),
            Fore("R", A(0f, 80, 0), A(0.08f, 40, -30, PoseEase.Snap), A(0.5f, 35, -30), A(0.75f, 60, -20), A(SpearDiveImpactLength, 60, -20, w: 0f)),
            Upper("L", A(0f, 20, -10), A(0.08f, -40, 10, PoseEase.Snap), A(0.5f, -45, 10), A(0.75f, 0, 10), A(SpearDiveImpactLength, 0, 10, w: 0f)),
            Fore("L", A(0f, -10, 0), A(0.08f, -80, 0, PoseEase.Snap), A(0.5f, -82, 0), A(0.75f, -30, 0), A(SpearDiveImpactLength, -30, 0, w: 0f)),
        ]);

    /// <summary>A spear clip from the hammer strike's length, hit and combo opening (s, the hammer's) at the spear's tempo; <paramref name="grip"/> builds the left hand's hold on the shaft over the same moments.</summary>
    static WeaponPoseClip Spear(string name, float hammerLength, float hammerHit, float hammerCombo, string baseClip, string source, string description, float antic, float follow, float recover,
        Func<float, float, float, float, float, PoseChannel[]> build, Func<float, float, float, float, float, PoseKey[]>? grip = null, Func<float, float, float, float, float, PoseKey[]>? gripPoint = null)
    {
        float d = hammerLength / SpearTempo, h = hammerHit / SpearTempo;
        float a = h - antic, f = h + follow;
        return new WeaponPoseClip(name, "spear", d, h, baseClip, a, f, Math.Min(d, recover + 0.4f), float.IsNaN(hammerCombo) ? float.NaN : hammerCombo / SpearTempo, source, description, build(a, h, f, recover, d))
        { Grip = grip?.Invoke(a, h, f, recover, d), GripPoint = gripPoint?.Invoke(a, h, f, recover, d) };
    }

    /// <summary>Every clip, in the preview's order.</summary>
    public static readonly IReadOnlyList<WeaponPoseClip> All = [ClawX1, ClawX2, ClawX3, ClawX4, ClawHeavy, SpearRest, SpearHew1, SpearHew2, SpearHew3, SpearStun, SpearStab, SpearDive, SpearImpale];

    /// <summary>The spear's strikes on the ground (the light hews and the F, F chain): each starts from <see cref="SpearRest"/> or the strike before it and ends back at the rest.</summary>
    public static readonly IReadOnlyList<WeaponPoseClip> SpearStrikes = [SpearHew1, SpearHew2, SpearHew3, SpearStun, SpearStab];

    /// <summary>
    /// The spear's clip for a simulated strike (W3: the thunder hammer's combo at 0.85, <c>SpearRules.FromHammer</c>, so its steps are the hammer's): light strikes 1..3 the three hews (each
    /// authored on that strike's timing); the heavy list is the hammer's (<c>MeleeTuning.ThunderHammer</c>: 1 the Y interrupt, 2 and 3 the X,Y and X,X,Y sweeping attacks, 4 and 5 the Y,Y slam
    /// after the interrupt or the X,Y sweep and after the X,X,Y sweep): the interrupt is the snap cut, the two sweeping attacks the snap cut as well (no clip of their own, ours; shown on
    /// their own hit, <see cref="WeaponPoseClip.TimeAt"/>), both slams the massive stab. Null for any other step (the hammer's clip stays as the stand-in).
    /// </summary>
    public static WeaponPoseClip? SpearStrike(bool heavy, int step) => heavy
        ? step switch { 1 or 2 or 3 => SpearStun, 4 or 5 => SpearStab, _ => null }
        : step switch { 1 => SpearHew1, 2 => SpearHew2, 3 => SpearHew3, _ => null };

    /// <summary>A clip by name (any case), or null.</summary>
    public static WeaponPoseClip? ByName(string name) => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
