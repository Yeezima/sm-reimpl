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
/// Laid over the hammer's two-handed idle (<see cref="SpearIdle"/>); at rest the spear is carried in the right hand held out wide of the hip, the shaft leaning forward and the blade's edge facing out and
/// forward (<see cref="SpearRest"/>; the owner's notes of 2026-11-10, pass 2), and every sweep starts from and returns to that carry. The dive over the hammer's dive loop and landing (<see cref="SpearDiveLoop"/>,
/// <see cref="SpearDiveImpact"/>), whose impale drives the blade point-down into the Ork at his feet by 0.14 s, holds to 0.85 s and is free by 0.95 s (owner, 2026-11-10: "Point-down").</item>
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

    /// <summary>The carry: the right arm hanging with the shaft at the right side (the shaft leans forward-up from this hand at the carry grip, <see cref="SpearHold.Place"/>, the blade's edge facing out and forward, from the hips' facing); the left arm hangs free (aims, el / az).</summary>
    /// <remarks>Second follow-up pass (owner, 2026-11-11: "for the running and idle animation pull the spear and arm closer in to the body"; and the blade out of the crosshair's column): the carry
    /// is read in the facing frame (<see cref="WeaponPoseClip.FacingFrame"/>), so it keeps its place against the camera in every stance; the right upper arm hangs down and a little forward and
    /// out (-35 degrees elevation, 45 azimuth from the facing), the forearm comes up to the chest (20, 55), the fist about 0.43 m from the spine at idle (0.77 m before), and the shaft leans from it
    /// in at the top (<see cref="SpearHold.CarryElevation"/>), the blade above and forward of the right shoulder, left of the crosshair's column (<c>SpearClearanceTests</c>: 5.9 degrees at idle;
    /// a wider field of view moves the camera right, away from it). The left arm hangs as before. The strikes, laid over the hammer idle, start and end on the same arms read in the idle's stance
    /// frame (<see cref="IdleHipsTurn"/>).</remarks>
    const float RestRUpperEl = -35f, RestRUpperAz = 45f, RestRForeEl = 0f, RestRForeAz = 40f, RestLUpperEl = -70f, RestLUpperAz = 51f, RestLForeEl = -50f, RestLForeAz = 46f;
    /// <summary>
    /// The hammer idle's hips turn: its stance's front stands this many degrees right of the way the player faces (measured over <see cref="SpearIdle"/>; the spear's strikes are laid over it, so a
    /// facing-frame azimuth A is A minus this in their stance frame).
    /// </summary>
    public const float IdleHipsTurn = 71f;
    /// <summary>The carry's arms (facing frame) as a strike over the hammer idle reads them (its stance frame): where every strike starts from and returns to.</summary>
    static PoseKey RestRUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRUpperEl, RestRUpperAz - IdleHipsTurn, ease);
    static PoseKey RestRFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRForeEl, RestRForeAz - IdleHipsTurn, ease);
    static PoseKey RestLUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLUpperEl, RestLUpperAz - IdleHipsTurn, ease);
    static PoseKey RestLFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLForeEl, RestLForeAz - IdleHipsTurn, ease);
    // The recovery (the left hand forward, the right out, before the left lets go) and the field guard the hews end in were shared aims until the second follow-up pass (2026-11-11); since
    // then each clip's keys are solved for the palms on the shaft, so they are written out per clip (the same shaft lines: SpearClearanceTests).
    // The steps: the planted feet reach their spots on every frame (SpearClearanceTests.The_spears_steps_plant_the_feet_without_floating_or_sliding): since the second follow-up pass the hips
    // stay low (0.20 to 0.22 m down) at the recovery key until the stepped foot has come back, and hew 3 starts with the hips down over hew 2's step (before, the leg could not reach the
    // forward foot and it floated up to 0.11 m off the ground at the recovery).
    /// <summary>The field guard's hips: leaning forward a little from the hips, dropped deep, the left foot a long step ahead (the reference's wide stance).</summary>
    const float GuardPelvisPitch = -10f, GuardDrop = -0.26f, GuardForward = 0.16f;
    /// <summary>Where the hews' steps leave the feet (metres forward, right from the stance's spots): after the first hew the left foot, after the second the right, the third's left pivot step.</summary>
    const float H1LeftF = 0.65f, H1LeftR = -0.15f, H2RightF = 0.60f, H2RightR = 0.22f, H3LeftF = 0.60f, H3LeftR = -0.30f;

    /// <summary>
    /// Where the carry's fist is held against the shoulders while he walks, runs and sprints (m right, forward, up from the middle of the shoulders in the facing frame; pass 3, 2026-11-11, the owner's
    /// "pull the spear and arm closer in to the body" for the running): the keys' absolute aims put the fist 0.7 to 1.0 m out to the right in the moving stances (their shoulders face the way he runs,
    /// the idle's stand side-on), and the blade crossed the crosshair's column; solved onto this point (<see cref="WeaponPoseClip.FistTarget"/>, laid with the speed) the fist stays 0.36 m right of the
    /// shoulders' middle and in front of the right pad, the blade 3.6 to 7 degrees left of the column in every walk and run (<c>SpearClearanceTests</c>). Standing, the keys hold it 0.39 right,
    /// 0.19 forward (ours, chosen with the clearance and column checks).
    /// </summary>
    public static readonly System.Numerics.Vector3 CarryFist = new(0.36f, 0.62f, -0.02f);

    /// <summary>The spear's carry (owner, 2026-11-10: "carried 1 handed in the right hand when running or idling", the hand where the butt is just above the ground), held over the hammer's idle while the spear is not swung; every strike starts from it and the string's last returns to it.</summary>
    public static readonly WeaponPoseClip SpearRest = new("spear_rest", "spear", SpearIdleLength, float.NaN, SpearIdle, 0f, 0f, 0f, float.NaN,
        "the hammer's idle (SM_2HM_NAV_TH_IdleL_S_01 1.5 s; the pose is held while it loops)",
        "the carry: the spear in the right hand alone at the right side, the butt just above the ground and the blade up and forward over the shoulder, its edge facing out and forward from the hips; the left arm hanging free",
        [
            Turn(SpineD, T(0f, 4, 0, 0), T(SpearIdleLength, 4, 0, 0)),
            Turn(Head, T(0f, -4, 0, 0), T(SpearIdleLength, -4, 0, 0)),
            Upper("R", A(0f, RestRUpperEl, RestRUpperAz), A(SpearIdleLength, RestRUpperEl, RestRUpperAz)),   // the facing frame: the constants as they are
            Fore("R", A(0f, RestRForeEl, RestRForeAz), A(SpearIdleLength, RestRForeEl, RestRForeAz)),
            Upper("L", A(0f, RestLUpperEl, RestLUpperAz), A(SpearIdleLength, RestLUpperEl, RestLUpperAz)),
            Fore("L", A(0f, RestLForeEl, RestLForeAz), A(SpearIdleLength, RestLForeEl, RestLForeAz)),
        ])
    { Grip = [G(0f, 0f), G(SpearIdleLength, 0f)], GripPoint = [P(0f, WeaponPoseClip.CarryGrip), P(SpearIdleLength, WeaponPoseClip.CarryGrip)], FacingFrame = true, FistTarget = CarryFist };

    /// <summary>
    /// Hew 1, the wide descending hew (pass 3, 2026-11-11, the owner's "swinging out wide"; the hammer's X1 at 0.85x: 3.138 s, hit 0.711 s): from the carry the shaft slides low and is drawn
    /// across the chest with the blade back past his right side and high; the hips turn from the idle's coil through the target to past square on the left as the left foot steps toward the
    /// target, the arms long, and the blade sweeps round the front, cutting down across the target, on to low left; the approved field guard and recovery follow. The arms are solved per key
    /// (every 1/30 s through the swing) so the palms hold the authored shaft line clear of the body (<c>SpearClearanceTests</c>); the keys are written in the hammer idle's stance frame, the
    /// steps and the lean toward the target converted into it.
    /// </summary>
    public static readonly WeaponPoseClip SpearHew1 = Spear("spear_hew1", 2.667f, 0.604f, 0.933f, SpearIdle, "the hammer's X1 (SM_MEL_TH_XATK_Combo2 2.667 s, hit 0.604 s)",
        "wide descending hew: the shaft slides low through the right hand as the left hand takes it and is drawn across the chest, the blade back past his right side and high; the left foot steps toward the target and the hips turn through it, the arms long, as the blade sweeps round the front and cuts down across the target and on to low left; then the deep wide field guard",
        antic: 0.19f, follow: 0.29f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, Z(0f), T(a - 0.19f, -2, 0.65f, 1.89f), T(a, 0, 1.3f, 3.78f, PoseEase.In), T(h, 62, -3.91f, -11.35f, PoseEase.Linear), T(f, 95, -3.26f, -9.46f, PoseEase.Out), T(1.6f, 40, -2.61f, -7.56f), T(r, 3, -3, 0), Z(end)),
            PelvisMove(M(0f, 0, 0, 0), M(a - 0.19f, 0.019f, 0.007f, -0.1f), M(a, 0.024f, 0.114f, -0.16f, PoseEase.In), M(h, 0.04f, -0.24f, -0.3f, PoseEase.Linear), M(f, 0.016f, -0.291f, -0.28f, PoseEase.Out), M(1.6f, 0.034f, -0.221f, -0.26f), M(r, 0.065f, -0.189f, -0.2f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0, 0, 0), M(a, 0.039f, -0.113f, 0.16f, PoseEase.In), M(h, 0.16f, -0.527f, 0, PoseEase.Snap), M(r, 0.16f, -0.527f, 0), M(r + 0.3f, 0.08f, -0.263f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, Z(0f), T(a, -4, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Linear), T(f, 12, -4, 0, PoseEase.Out), T(1.6f, 6, -4, 0), T(r, 2, -1, 0), Z(end)),
            Turn(SpineD, Z(0f), T(a, -4, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Linear), T(f, 12, -4, 0, PoseEase.Out), T(1.6f, 6, -4, 0), T(r, 2, -1, 0), Z(end)),
            Turn(Head, Z(0f), T(a - 0.19f, 4, 4, 0), T(a, 24, 2, 0, PoseEase.In), T(h, -50, 4, 0, PoseEase.Linear), T(f, -90, 6, 0, PoseEase.Out), T(1.6f, -40, 6, 0), T(r, -4, 2, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -6, 10, 0, PoseEase.In), T(h, 10, 2, 0, PoseEase.Linear), T(f, 14, 2, 0, PoseEase.Out), T(r, 4, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -6, 10, 0, PoseEase.In), T(h, 10, 2, 0, PoseEase.Linear), T(f, 14, 2, 0, PoseEase.Out), T(r, 4, 1, 0), Z(end)),
            Upper("R", RestRUpper(0f), A(0.17f, -54, 22), A(a - 0.19f, -40, 4), A(0.364f, -32, 11, PoseEase.Linear), A(0.397f, -28, 13, PoseEase.Linear), A(0.431f, -38, 10, PoseEase.Linear), A(0.464f, -25, 20, PoseEase.Linear), A(0.497f, -20, 23, PoseEase.Linear), A(a, -17, 24, PoseEase.In), A(0.554f, -20, 10, PoseEase.Linear), A(0.587f, -35, -11, PoseEase.Linear), A(0.600f, -42, -27, PoseEase.Linear), A((a + h) * 0.5f, 3, -50, PoseEase.Linear), A(0.633f, 2, -65, PoseEase.Linear), A(0.649f, -28, -24, PoseEase.Linear), A(0.667f, -7, -34, PoseEase.Linear), A(0.682f, -4, -44, PoseEase.Linear), A(h, -20, -4, PoseEase.Linear), A(0.744f, -38, -48, PoseEase.Linear), A(0.777f, -33, -72, PoseEase.Linear), A(0.811f, -35, -80, PoseEase.Linear), A(0.844f, -38, -87, PoseEase.Linear), A(0.877f, -39, -91, PoseEase.Linear), A(0.911f, -43, -97, PoseEase.Linear), A(0.944f, -41, -96, PoseEase.Linear), A(0.977f, -42, -97, PoseEase.Linear), A(f, -42, -98, PoseEase.Linear), A(1.034f, -41, -95, PoseEase.Linear), A(1.067f, -42, -92, PoseEase.Linear), A(1.101f, -41, -88, PoseEase.Linear), A(1.134f, -42, -82, PoseEase.Linear), A(1.167f, -42, -75, PoseEase.Linear), A(1.201f, -42, -67, PoseEase.Linear), A(1.234f, -40, -61, PoseEase.Linear), A(1.267f, -43, -53, PoseEase.Linear), A(1.301f, -43, -44, PoseEase.Linear), A(1.334f, -48, -34, PoseEase.Linear), A(1.367f, -40, -27, PoseEase.Linear), A(1.401f, -40, -19, PoseEase.Linear), A(1.434f, -42, -8, PoseEase.Linear), A(1.467f, -45, 23, PoseEase.Linear), A(1.501f, -30, 20, PoseEase.Linear), A(1.534f, -33, 36, PoseEase.Linear), A(1.567f, -14, 26, PoseEase.Linear), A(1.6f, -47, 70), A(r, 5, 104), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(0.17f, -33, 4), A(a - 0.19f, -19, -11), A(0.364f, -25, -15, PoseEase.Linear), A(0.397f, -26, -14, PoseEase.Linear), A(0.431f, -15, -10, PoseEase.Linear), A(0.464f, -19, -16, PoseEase.Linear), A(0.497f, -15, -16, PoseEase.Linear), A(a, -7, -15, PoseEase.In), A(0.554f, -7, -33, PoseEase.Linear), A(0.587f, -10, -45, PoseEase.Linear), A(0.600f, -12, -53, PoseEase.Linear), A((a + h) * 0.5f, -5, -59, PoseEase.Linear), A(0.633f, 4, -68, PoseEase.Linear), A(0.649f, -16, -78, PoseEase.Linear), A(0.667f, -10, -81, PoseEase.Linear), A(0.682f, -6, -80, PoseEase.Linear), A(h, -46, -86, PoseEase.Linear), A(0.744f, -29, -107, PoseEase.Linear), A(0.777f, -29, -115, PoseEase.Linear), A(0.811f, -29, -125, PoseEase.Linear), A(0.844f, -30, -132, PoseEase.Linear), A(0.877f, -32, -136, PoseEase.Linear), A(0.911f, -33, -137, PoseEase.Linear), A(0.944f, -34, -140, PoseEase.Linear), A(0.977f, -34, -140, PoseEase.Linear), A(f, -34, -140, PoseEase.Linear), A(1.034f, -34, -140, PoseEase.Linear), A(1.067f, -33, -138, PoseEase.Linear), A(1.101f, -33, -135, PoseEase.Linear), A(1.134f, -33, -132, PoseEase.Linear), A(1.167f, -33, -128, PoseEase.Linear), A(1.201f, -33, -123, PoseEase.Linear), A(1.234f, -35, -118, PoseEase.Linear), A(1.267f, -34, -110, PoseEase.Linear), A(1.301f, -36, -105, PoseEase.Linear), A(1.334f, -32, -94, PoseEase.Linear), A(1.367f, -39, -94, PoseEase.Linear), A(1.401f, -39, -87, PoseEase.Linear), A(1.434f, -38, -81, PoseEase.Linear), A(1.467f, -35, -71, PoseEase.Linear), A(1.501f, -51, -77, PoseEase.Linear), A(1.534f, -45, -69, PoseEase.Linear), A(1.567f, -54, -74, PoseEase.Linear), A(1.6f, -32, -47), A(r, -40, 38), RestRFore(end)),
            Upper("L", RestLUpper(0f), A(0.17f, 0, 70), A(a - 0.19f, 10, 74), A(0.364f, 9, 76, PoseEase.Linear), A(0.397f, 0, 76, PoseEase.Linear), A(0.431f, 2, 67, PoseEase.Linear), A(0.464f, 9, 81, PoseEase.Linear), A(0.497f, 10, 83, PoseEase.Linear), A(a, 12, 83, PoseEase.In), A(0.554f, 11, 64, PoseEase.Linear), A(0.587f, 7, 50, PoseEase.Linear), A(0.600f, 6, 62, PoseEase.Linear), A((a + h) * 0.5f, 25, 20, PoseEase.Linear), A(0.633f, 33, 2, PoseEase.Linear), A(0.649f, 18, -5, PoseEase.Linear), A(0.667f, 11, -6, PoseEase.Linear), A(0.682f, 15, -24, PoseEase.Linear), A(h, -7, -17, PoseEase.Linear), A(0.744f, -11, -46, PoseEase.Linear), A(0.777f, -14, -64, PoseEase.Linear), A(0.811f, -19, -94, PoseEase.Linear), A(0.844f, -23, -110, PoseEase.Linear), A(0.877f, -26, -119, PoseEase.Linear), A(0.911f, -25, -122, PoseEase.Linear), A(0.944f, -30, -126, PoseEase.Linear), A(0.977f, -30, -127, PoseEase.Linear), A(f, -30, -127, PoseEase.Linear), A(1.034f, -30, -126, PoseEase.Linear), A(1.067f, -29, -123, PoseEase.Linear), A(1.101f, -29, -119, PoseEase.Linear), A(1.134f, -28, -113, PoseEase.Linear), A(1.167f, -28, -106, PoseEase.Linear), A(1.201f, -28, -97, PoseEase.Linear), A(1.234f, -27, -86, PoseEase.Linear), A(1.267f, -26, -76, PoseEase.Linear), A(1.301f, -26, -66, PoseEase.Linear), A(1.334f, -25, -50, PoseEase.Linear), A(1.367f, -37, -27, PoseEase.Linear), A(1.401f, -22, -27, PoseEase.Linear), A(1.434f, -22, -13, PoseEase.Linear), A(1.467f, -20, -4, PoseEase.Linear), A(1.501f, -21, 4, PoseEase.Linear), A(1.534f, -20, 9, PoseEase.Linear), A(1.567f, -15, 13, PoseEase.Linear), A(1.6f, -19, 15), A(r, -57, 34), RestLUpper(end)),
            Fore("L", RestLFore(0f), A(0.17f, 8, 76), A(a - 0.19f, 2, 76), A(0.364f, 2, 76, PoseEase.Linear), A(0.397f, 9, 77, PoseEase.Linear), A(0.431f, 8, 90, PoseEase.Linear), A(0.464f, 4, 83, PoseEase.Linear), A(0.497f, 4, 86, PoseEase.Linear), A(a, 8, 91, PoseEase.In), A(0.554f, 17, 76, PoseEase.Linear), A(0.587f, 15, 50, PoseEase.Linear), A(0.600f, 9, 15, PoseEase.Linear), A((a + h) * 0.5f, 34, 22, PoseEase.Linear), A(0.633f, 36, 10, PoseEase.Linear), A(0.649f, -2, 21, PoseEase.Linear), A(0.667f, 27, -6, PoseEase.Linear), A(0.682f, 26, -3, PoseEase.Linear), A(h, 0, -17, PoseEase.Linear), A(0.744f, -3, -44, PoseEase.Linear), A(0.777f, -4, -58, PoseEase.Linear), A(0.811f, -10, -53, PoseEase.Linear), A(0.844f, -14, -53, PoseEase.Linear), A(0.877f, -16, -53, PoseEase.Linear), A(0.911f, -20, -49, PoseEase.Linear), A(0.944f, -18, -55, PoseEase.Linear), A(0.977f, -19, -55, PoseEase.Linear), A(f, -19, -55, PoseEase.Linear), A(1.034f, -18, -54, PoseEase.Linear), A(1.067f, -18, -52, PoseEase.Linear), A(1.101f, -18, -48, PoseEase.Linear), A(1.134f, -18, -45, PoseEase.Linear), A(1.167f, -17, -41, PoseEase.Linear), A(1.201f, -16, -36, PoseEase.Linear), A(1.234f, -18, -35, PoseEase.Linear), A(1.267f, -16, -23, PoseEase.Linear), A(1.301f, -17, -20, PoseEase.Linear), A(1.334f, -16, -20, PoseEase.Linear), A(1.367f, -5, -31, PoseEase.Linear), A(1.401f, -15, -13, PoseEase.Linear), A(1.434f, -13, -12, PoseEase.Linear), A(1.467f, -14, 3, PoseEase.Linear), A(1.501f, -15, 10, PoseEase.Linear), A(1.534f, -12, 16, PoseEase.Linear), A(1.567f, -8, 18, PoseEase.Linear), A(1.6f, -11, 21), A(r, 20, 15), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 0f), G(GripSlide, 1f, PoseEase.In), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.CarryGrip), P(GripSlide, WeaponPoseClip.HewGrip, PoseEase.Out), P(r, WeaponPoseClip.HewGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>
    /// Hew 2, the wide rising hew (pass 3; the hammer's X2 at 0.85x: 2.745 s, hit 0.667 s): from hew 1's low-left follow-through the hips coil further left and the blade drops low behind
    /// his left side; the right foot passes through toward the target as the hips uncoil to the right and the blade is torn up round the front, through the target, to high behind his right
    /// shoulder; the approved recovery follows. Arms solved per key as hew 1's.
    /// </summary>
    public static readonly WeaponPoseClip SpearHew2 = Spear("spear_hew2", 2.333f, 0.567f, 0.967f, SpearIdle, "the hammer's X2 (SM_MEL_TH_XATK_Combo3 2.333 s, hit 0.567 s)",
        "wide rising hew: from the low-left follow-through the hips coil further left and sink as the blade drops low behind his left side, then the right foot passes through toward the target and the hips uncoil hard to the right as the blade is torn up round the front, through the target, to high behind his right shoulder, the arms long, the back straight",
        antic: 0.18f, follow: 0.28f, recover: 1.9f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 91.14f, -3.21f, -9.32f), T(a, 125, -3.26f, -9.46f, PoseEase.In), T(h, 105, -3.91f, -11.35f, PoseEase.Linear), T(f, 40, -1.95f, -5.67f, PoseEase.Out), T(r, -8, 0, 0), Z(end)),
            PelvisMove(M(0f, 0.017f, -0.286f, -0.279f), M(a, -0.035f, -0.266f, -0.3f, PoseEase.In), M(h, 0.194f, -0.409f, -0.22f, PoseEase.Linear), M(f, 0.276f, -0.434f, -0.16f, PoseEase.Out), M(r, 0.251f, -0.421f, -0.2f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0.16f, -0.527f, 0), M(r, 0.16f, -0.527f, 0), M(r + 0.3f, 0.08f, -0.263f, 0.08f), M(end, 0, 0, 0)),
            Foot("R", M(0f, 0, 0, 0), M(a, 0.195f, -0.197f, 0.16f, PoseEase.In), M(h, 0.464f, -0.58f, 0, PoseEase.Snap), M(r, 0.464f, -0.58f, 0), M(r + 0.3f, 0.232f, -0.29f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 11.58f, -4, 0), T(a, 12, -3, 0, PoseEase.In), T(h, 4, 2, 0, PoseEase.Linear), T(f, -10, 4, 0, PoseEase.Out), T(r, -2, 1, 0), Z(end)),
            Turn(SpineD, T(0f, 11.58f, -4, 0), T(a, 12, -3, 0, PoseEase.In), T(h, 4, 2, 0, PoseEase.Linear), T(f, -10, 4, 0, PoseEase.Out), T(r, -2, 1, 0), Z(end)),
            Turn(Head, T(0f, -86.49f, 6, 0), T(a, -110, 8, 0, PoseEase.In), T(h, -100, -2, 0, PoseEase.Linear), T(f, -20, -10, 0, PoseEase.Out), T(r, 8, -2, 0), Z(end)),
            Turn(LClav, T(0f, 13.74f, 1.97f, 0), T(a, 12, -4, 0, PoseEase.In), T(h, -6, 8, 0, PoseEase.Linear), T(f, -16, 16, 0, PoseEase.Out), T(r, -4, 3, 0), Z(end)),
            Turn(RClav, T(0f, 13.74f, 1.97f, 0), T(a, 12, -4, 0, PoseEase.In), T(h, -6, 8, 0, PoseEase.Linear), T(f, -16, 16, 0, PoseEase.Out), T(r, -4, 3, 0), Z(end)),
            Upper("R", A(0f, -41, -88), A(a, -26, -154, PoseEase.In), A(0.520f, -30, -148, PoseEase.Linear), A(0.554f, -33, -137, PoseEase.Linear), A((a + h) * 0.5f, -42, -139, PoseEase.Linear), A(0.610f, -18, -121, PoseEase.Linear), A(0.644f, -2, -135, PoseEase.Linear), A(h, -15, -111, PoseEase.Linear), A(0.700f, 4, -68, PoseEase.Linear), A(0.734f, -3, -31, PoseEase.Linear), A(0.750f, 12, -25, PoseEase.Linear), A(0.767f, -25, -23, PoseEase.Linear), A(0.800f, -10, -11, PoseEase.Linear), A(0.834f, -3, -2, PoseEase.Linear), A(0.867f, 3, 2, PoseEase.Linear), A(0.900f, -3, 8, PoseEase.Linear), A(0.934f, -13, 8, PoseEase.Linear), A(f, -21, 4, PoseEase.Linear), A(1.217f, -3, 13, PoseEase.Linear), A(1.400f, -6, 19, PoseEase.Linear), A(1.533f, -22, 37, PoseEase.Linear), A(1.650f, 1, 47, PoseEase.Linear), A(1.717f, -6, 49, PoseEase.Linear), A(1.767f, -9, 34, PoseEase.Linear), A(1.800f, -15, 48, PoseEase.Linear), A(1.833f, 12, 50, PoseEase.Linear), A(1.850f, -14, 8, PoseEase.Linear), A(r, -9, 104), RestRUpper(end)),
            Fore("R", A(0f, -33, -135), A(a, -31, -149, PoseEase.In), A(0.520f, -37, -153, PoseEase.Linear), A(0.554f, -35, -131, PoseEase.Linear), A((a + h) * 0.5f, -23, -103, PoseEase.Linear), A(0.610f, -6, -116, PoseEase.Linear), A(0.644f, -4, -127, PoseEase.Linear), A(h, -11, -137, PoseEase.Linear), A(0.700f, -24, -105, PoseEase.Linear), A(0.734f, -2, -92, PoseEase.Linear), A(0.750f, -6, -83, PoseEase.Linear), A(0.767f, -22, -47, PoseEase.Linear), A(0.800f, -21, -38, PoseEase.Linear), A(0.834f, -18, -33, PoseEase.Linear), A(0.867f, -18, -30, PoseEase.Linear), A(0.900f, -12, -31, PoseEase.Linear), A(0.934f, -4, -30, PoseEase.Linear), A(f, 1, -27, PoseEase.Linear), A(1.217f, -12, -28, PoseEase.Linear), A(1.400f, -11, -25, PoseEase.Linear), A(1.533f, 18, -29, PoseEase.Linear), A(1.650f, 2, -33, PoseEase.Linear), A(1.717f, 6, -32, PoseEase.Linear), A(1.767f, -10, -18, PoseEase.Linear), A(1.800f, 11, -27, PoseEase.Linear), A(1.833f, -7, -30, PoseEase.Linear), A(1.850f, -14, 5, PoseEase.Linear), A(r, -37, 29), RestRFore(end)),
            Upper("L", A(0f, -29, -119), A(a, -23, -157, PoseEase.In), A(0.520f, -31, -164, PoseEase.Linear), A(0.554f, -32, -117, PoseEase.Linear), A((a + h) * 0.5f, -5, -76, PoseEase.Linear), A(0.610f, 15, -73, PoseEase.Linear), A(0.644f, 31, -77, PoseEase.Linear), A(h, 23, -62, PoseEase.Linear), A(0.700f, 25, -14, PoseEase.Linear), A(0.734f, 36, 13, PoseEase.Linear), A(0.750f, 39, 24, PoseEase.Linear), A(0.767f, 16, 37, PoseEase.Linear), A(0.800f, 22, 44, PoseEase.Linear), A(0.834f, 24, 54, PoseEase.Linear), A(0.867f, 19, 63, PoseEase.Linear), A(0.900f, 26, 63, PoseEase.Linear), A(0.934f, 29, 63, PoseEase.Linear), A(f, 22, 64, PoseEase.Linear), A(1.217f, 28, 67, PoseEase.Linear), A(1.400f, 31, 71, PoseEase.Linear), A(1.533f, 26, 93, PoseEase.Linear), A(1.650f, 29, 101, PoseEase.Linear), A(1.717f, 26, 104, PoseEase.Linear), A(1.767f, 18, 83, PoseEase.Linear), A(1.800f, 27, 101, PoseEase.Linear), A(1.833f, 27, 105, PoseEase.Linear), A(1.850f, 14, 86, PoseEase.Linear), A(r, 9, 54), RestLUpper(end)),
            Fore("L", A(0f, -18, -48), A(a, -40, -144, PoseEase.In), A(0.520f, -32, -137, PoseEase.Linear), A(0.554f, 2, -103, PoseEase.Linear), A((a + h) * 0.5f, 3, -74, PoseEase.Linear), A(0.610f, 28, -71, PoseEase.Linear), A(0.644f, 38, -71, PoseEase.Linear), A(h, 35, -61, PoseEase.Linear), A(0.700f, 37, -21, PoseEase.Linear), A(0.734f, 43, 27, PoseEase.Linear), A(0.750f, 47, 40, PoseEase.Linear), A(0.767f, 22, 38, PoseEase.Linear), A(0.800f, 25, 58, PoseEase.Linear), A(0.834f, 30, 66, PoseEase.Linear), A(0.867f, 38, 66, PoseEase.Linear), A(0.900f, 31, 73, PoseEase.Linear), A(0.934f, 28, 73, PoseEase.Linear), A(f, 35, 71, PoseEase.Linear), A(1.217f, 24, 79, PoseEase.Linear), A(1.400f, 13, 85, PoseEase.Linear), A(1.533f, 34, 87, PoseEase.Linear), A(1.650f, 31, 91, PoseEase.Linear), A(1.717f, 34, 90, PoseEase.Linear), A(1.767f, 23, 96, PoseEase.Linear), A(1.800f, 31, 95, PoseEase.Linear), A(1.833f, 33, 94, PoseEase.Linear), A(1.850f, 22, 80, PoseEase.Linear), A(r, -36, -14), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 1f), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.HewGrip), P(r, WeaponPoseClip.HewGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>
    /// Hew 3, the wide flat hew that ends the string (pass 3; the hammer's X3 at 0.85x: 2.745 s, hit 0.824 s): from hew 2's high right the blade comes down behind his right side
    /// to waist height as the hips coil right; the left foot pivots forward-left toward the target and the hips swing left through it as a flat hew at waist height carries the blade from behind
    /// his right round the front, through the target, to far left; a field guard and the approved recovery to the carry follow. Before pass 3 it looped overhead to the left and hewed left to
    /// right; the overhead loop could not be kept clear of the head and the pads in the wide version (ours). It starts as hew 1 does, from the carry (the right arm, the shaft sliding down from the
    /// carry grip as the left hand takes it), with the left hand where hew 2 leaves it, so the hand-overs from the carry, hew 1 and hew 2 (Musou picks any) stay clear.
    /// </summary>
    public static readonly WeaponPoseClip SpearHew3 = Spear("spear_hew3", 2.333f, 0.700f, float.NaN, SpearIdle, "the hammer's X3 (SM_MEL_TH_HeavyATK02 2.333 s, hit 0.700 s; the string ends)",
        "wide flat hew: from high over the right shoulder the blade comes down behind his right side to waist height as the hips coil right and the left foot lifts, then the left foot pivots forward-left toward the target and the hips swing hard left as a flat hew at waist height carries the blade from behind his right round the front and on to far left, the arms long and the back straight; it settles into the field guard, the feet come back under him and the shaft slides back up to the carry",
        antic: 0.20f, follow: 0.18f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 35.01f, -1.75f, -5.08f), T(a - 0.22f, 28, -1.3f, -3.78f), T(a, 10, -1.95f, -5.67f, PoseEase.In), T(h, 72, -3.91f, -11.35f, PoseEase.Linear), T(f, 100, -3.26f, -9.46f, PoseEase.Out), T(f + 0.25f, 105, -2.61f, -7.56f, PoseEase.Over), T(1.6f, 40, -2.61f, -7.56f), T(r, -4, -3, 0), Z(end)),
            PelvisMove(M(0f, 0.274f, -0.432f, -0.164f), M(a, 0.149f, -0.372f, -0.26f, PoseEase.In), M(h, 0.126f, -0.612f, -0.3f, PoseEase.Linear), M(f, 0.083f, -0.67f, -0.28f, PoseEase.Out), M(f + 0.25f, 0.076f, -0.651f, -0.27f, PoseEase.Over), M(1.6f, 0.122f, -0.54f, -0.24f), M(r, 0.163f, -0.473f, -0.2f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0.16f, -0.527f, 0), M(a, 0.111f, -0.628f, 0.16f, PoseEase.In), M(h, 0.008f, -0.791f, 0, PoseEase.Snap), M(r, 0.008f, -0.791f, 0), M(r + 0.3f, 0.004f, -0.395f, 0.08f), M(end, 0, 0, 0)),
            Foot("R", M(0f, 0.464f, -0.58f, 0), M(r, 0.464f, -0.58f, 0), M(r + 0.35f, 0.232f, -0.29f, 0.08f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, -9.17f, 3.69f, 0), T(a, -6, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Linear), T(f, 12, -4, 0, PoseEase.Out), T(f + 0.25f, 12, -4, 0, PoseEase.Over), T(1.6f, -4, -4, 0), T(r, -2, -1, 0), Z(end)),
            Turn(SpineD, T(0f, -9.17f, 3.69f, 0), T(a, -6, 2, 0, PoseEase.In), T(h, 8, -3, 0, PoseEase.Linear), T(f, 12, -4, 0, PoseEase.Out), T(f + 0.25f, 12, -4, 0, PoseEase.Over), T(1.6f, -4, -4, 0), T(r, -2, -1, 0), Z(end)),
            Turn(Head, T(0f, -17.09f, -9.17f, 0), T(a, 20, 2, 0, PoseEase.In), T(h, -55, 4, 0, PoseEase.Linear), T(f, -90, 6, 0, PoseEase.Out), T(f + 0.25f, -95, 6, 0, PoseEase.Over), T(1.6f, -40, 6, 0), T(r, 4, 2, 0), Z(end)),
            Turn(LClav, T(0f, -14.75f, 14.65f, 0), T(a, -6, 10, 0, PoseEase.In), T(h, 10, 2, 0, PoseEase.Linear), T(f, 14, 2, 0, PoseEase.Out), T(f + 0.25f, 14, 2, 0, PoseEase.Over), T(r, -4, 1, 0), Z(end)),
            Turn(RClav, T(0f, -14.75f, 14.65f, 0), T(a, -6, 10, 0, PoseEase.In), T(h, 10, 2, 0, PoseEase.Linear), T(f, 14, 2, 0, PoseEase.Out), T(f + 0.25f, 14, 2, 0, PoseEase.Over), T(r, -4, 1, 0), Z(end)),
            Upper("R", RestRUpper(0f), A(0.200f, -11, 9, PoseEase.Linear), A(a - 0.22f, -11, 4), A(0.437f, -27, 3, PoseEase.Linear), A(0.470f, -17, 7, PoseEase.Linear), A(0.504f, -31, -0, PoseEase.Linear), A(0.537f, -1, -2, PoseEase.Linear), A(0.570f, -18, 8, PoseEase.Linear), A(0.604f, -18, 6, PoseEase.Linear), A(a, -24, -1, PoseEase.In), A(0.657f, -7, 4, PoseEase.Linear), A(0.690f, -12, -2, PoseEase.Linear), A((a + h) * 0.5f, -5, -50, PoseEase.Linear), A(0.757f, 0, -76, PoseEase.Linear), A(0.790f, -10, -44, PoseEase.Linear), A(h, -15, -20, PoseEase.Linear), A(0.857f, -34, -68, PoseEase.Linear), A(0.890f, -34, -86, PoseEase.Linear), A(0.924f, -36, -91, PoseEase.Linear), A(0.957f, -36, -95, PoseEase.Linear), A(0.990f, -37, -98, PoseEase.Linear), A(f, -35, -96, PoseEase.Linear), A(1.037f, -40, -100, PoseEase.Linear), A(1.070f, -38, -101, PoseEase.Linear), A(1.104f, -39, -100, PoseEase.Linear), A(1.137f, -38, -101, PoseEase.Linear), A(1.170f, -38, -101, PoseEase.Linear), A(1.204f, -38, -101, PoseEase.Linear), A(1.237f, -39, -102, PoseEase.Linear), A(f + 0.25f, -39, -101, PoseEase.Over), A(1.433f, -39, -32, PoseEase.Linear), A(1.6f, -4, 37), A(r, -2, 100), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(0.200f, -11, -24, PoseEase.Linear), A(a - 0.22f, -23, -23), A(0.437f, -9, -22, PoseEase.Linear), A(0.470f, -17, -26, PoseEase.Linear), A(0.504f, -9, -21, PoseEase.Linear), A(0.537f, -34, -19, PoseEase.Linear), A(0.570f, -25, -22, PoseEase.Linear), A(0.604f, -31, -16, PoseEase.Linear), A(a, -31, 3, PoseEase.In), A(0.657f, 6, -41, PoseEase.Linear), A(0.690f, -3, -63, PoseEase.Linear), A((a + h) * 0.5f, -5, -68, PoseEase.Linear), A(0.757f, -1, -72, PoseEase.Linear), A(0.790f, -48, -71, PoseEase.Linear), A(h, -50, -97, PoseEase.Linear), A(0.857f, -28, -122, PoseEase.Linear), A(0.890f, -27, -135, PoseEase.Linear), A(0.924f, -27, -143, PoseEase.Linear), A(0.957f, -28, -147, PoseEase.Linear), A(0.990f, -28, -147, PoseEase.Linear), A(f, -28, -148, PoseEase.Linear), A(1.037f, -28, -149, PoseEase.Linear), A(1.070f, -28, -152, PoseEase.Linear), A(1.104f, -27, -153, PoseEase.Linear), A(1.137f, -28, -154, PoseEase.Linear), A(1.170f, -28, -154, PoseEase.Linear), A(1.204f, -27, -154, PoseEase.Linear), A(1.237f, -28, -153, PoseEase.Linear), A(f + 0.25f, -28, -153, PoseEase.Over), A(1.433f, -33, -99, PoseEase.Linear), A(1.6f, -66, -55), A(r, -36, 36), RestRFore(end)),
            Upper("L", A(0f, 26, 66), A(0.200f, 17, 68, PoseEase.Linear), A(a - 0.22f, -5, 65), A(0.437f, 5, 65, PoseEase.Linear), A(0.470f, 8, 65, PoseEase.Linear), A(0.504f, 2, 67, PoseEase.Linear), A(0.537f, 7, 69, PoseEase.Linear), A(0.570f, -0, 71, PoseEase.Linear), A(0.604f, 1, 72, PoseEase.Linear), A(a, -8, 77, PoseEase.In), A(0.657f, 24, 61, PoseEase.Linear), A(0.690f, 11, 44, PoseEase.Linear), A((a + h) * 0.5f, 21, 11, PoseEase.Linear), A(0.757f, 21, -13, PoseEase.Linear), A(0.790f, -3, -18, PoseEase.Linear), A(h, -8, -28, PoseEase.Linear), A(0.857f, -6, -64, PoseEase.Linear), A(0.890f, -12, -110, PoseEase.Linear), A(0.924f, -14, -129, PoseEase.Linear), A(0.957f, -16, -138, PoseEase.Linear), A(0.990f, -17, -140, PoseEase.Linear), A(f, -18, -140, PoseEase.Linear), A(1.037f, -13, -138, PoseEase.Linear), A(1.070f, -17, -147, PoseEase.Linear), A(1.104f, -19, -149, PoseEase.Linear), A(1.137f, -19, -149, PoseEase.Linear), A(1.170f, -19, -149, PoseEase.Linear), A(1.204f, -19, -149, PoseEase.Linear), A(1.237f, -19, -149, PoseEase.Linear), A(f + 0.25f, -20, -149, PoseEase.Over), A(1.433f, -20, -65, PoseEase.Linear), A(1.6f, -15, 36), A(r, -5, 69), RestLUpper(end)),
            Fore("L", A(0f, 27, 77), A(0.200f, 23, 75, PoseEase.Linear), A(a - 0.22f, 25, 73), A(0.437f, 20, 73, PoseEase.Linear), A(0.470f, 15, 73, PoseEase.Linear), A(0.504f, 20, 70, PoseEase.Linear), A(0.537f, 11, 73, PoseEase.Linear), A(0.570f, 11, 75, PoseEase.Linear), A(0.604f, -1, 85, PoseEase.Linear), A(a, 1, 81, PoseEase.In), A(0.657f, 25, 70, PoseEase.Linear), A(0.690f, 29, 51, PoseEase.Linear), A((a + h) * 0.5f, 29, 15, PoseEase.Linear), A(0.757f, 36, -13, PoseEase.Linear), A(0.790f, 5, -15, PoseEase.Linear), A(h, 0, -27, PoseEase.Linear), A(0.857f, -4, -62, PoseEase.Linear), A(0.890f, -7, -56, PoseEase.Linear), A(0.924f, -10, -56, PoseEase.Linear), A(0.957f, -10, -60, PoseEase.Linear), A(0.990f, -11, -62, PoseEase.Linear), A(f, -10, -63, PoseEase.Linear), A(1.037f, -16, -51, PoseEase.Linear), A(1.070f, -14, -59, PoseEase.Linear), A(1.104f, -13, -59, PoseEase.Linear), A(1.137f, -13, -59, PoseEase.Linear), A(1.170f, -13, -58, PoseEase.Linear), A(1.204f, -13, -59, PoseEase.Linear), A(1.237f, -13, -60, PoseEase.Linear), A(f + 0.25f, -13, -59, PoseEase.Over), A(1.433f, -15, -9, PoseEase.Linear), A(1.6f, -6, 40), A(r, -29, -33), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 0f), G(GripSlide, 1f, PoseEase.In), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.CarryGrip), P(GripSlide, WeaponPoseClip.HewGrip, PoseEase.Out), P(r, WeaponPoseClip.HewGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

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
            // The blade raised by the right shoulder (a), the flat cut through the front at shoulder height (h), on to the left (f). No-clipping pass (owner, 2026-11-10): the hands placed in the
            // chest's frame, in front of it and below the shoulder pads; before, the arms spread to either side of the body and the shaft between the hands ran through the pads and the chest.
            // Second follow-up pass: every key re-solved so the palms hold the shaft (the snap cut's line as approved).
            Upper("R", RestRUpper(0f), A(a, 45, 72, PoseEase.In), A(h, -60, -81, PoseEase.Snap), A(f, -53, -4, PoseEase.Over), A(r, -58, 91), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(a, -60, 79, PoseEase.In), A(h, 9, -8, PoseEase.Snap), A(f, 10, -71, PoseEase.Over), A(r, -19, 42), RestRFore(end)),
            Upper("L", RestLUpper(0f), A(a, -5, 144, PoseEase.In), A(h, 37, 28, PoseEase.Snap), A(f, -33, -62, PoseEase.Over), A(r, -65, 1), RestLUpper(end)),
            Fore("L", RestLFore(0f), A(a, 49, 79, PoseEase.In), A(h, -2, -21, PoseEase.Snap), A(f, 49, 25, PoseEase.Over), A(r, 6, 15), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 0f), G(GripSlide, 1f, PoseEase.In), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.CarryGrip), P(GripSlide, WeaponPoseClip.LowGrip, PoseEase.Out), P(r, WeaponPoseClip.LowGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>The F, F finisher: the massive stab, a whole-body lunging thrust with the hammer slam's timing (SM_MEL_TH_YATK_Displace2 at 0.85x: 3.529 s, hit 1.294 s); the hands at the butt for the full reach, the blade held in the target, then wrenched out.</summary>
    public static readonly WeaponPoseClip SpearStab = Spear("spear_stab", 3.0f, 1.1f, float.NaN, SpearIdle, "the hammer's Y,Y slam (SM_MEL_TH_YATK_Displace2 3.0 s, hit 1.1 s; the chain ends)",
        "massive stab: from the stun's end the spear is drawn far back along the right hip with the blade levelled at the target and both hands at the butt, the hips coiling back and sinking onto the rear leg with the left foot lifting, the head fixed on the target; then the left foot stamps a long stride ahead and the hips drive the whole body, back straight, behind the blade in one lunging thrust, the left arm fully extended; the blade is held in the target as the body settles over the lunge, then wrenched out and the shaft slid back up to the carry",
        antic: 0.20f, follow: 0.20f, recover: 2.6f, (a, h, f, r, end) =>
        [
            Turn(Pelvis, T(0f, 22, -6, 0), T(0.6f, -30, 6, 0), T(a, -40, 8, 0, PoseEase.In), T(h, -8, -16, 0, PoseEase.Snap), T(f, -6, -18, 0, PoseEase.Over), T(1.9f, -6, -18, 0), T(r, -2, -6, 0), Z(end)),
            PelvisMove(M(0f, 0.14f, -0.04f, -0.16f), M(0.6f, -0.14f, 0.10f, -0.14f), M(a, -0.22f, 0.14f, -0.18f, PoseEase.In), M(h, 0.45f, -0.06f, -0.30f, PoseEase.Snap), M(f, 0.50f, -0.08f, -0.32f, PoseEase.Over), M(1.9f, 0.50f, -0.08f, -0.32f), M(r, 0.30f, -0.04f, -0.22f), M(end, 0, 0, 0)),
            Foot("L", M(0f, 0.30f, -0.10f, 0), M(0.6f, 0.20f, -0.10f, 0.10f), M(a, 0.15f, -0.12f, 0.18f, PoseEase.In), M(h, 0.85f, -0.15f, 0, PoseEase.Snap), M(1.9f, 0.85f, -0.15f, 0), M(r, 0.85f, -0.15f, 0), M(r + 0.3f, 0.42f, -0.07f, 0.10f), M(end, 0, 0, 0)),
            Turn(SpineA, T(0f, 6, -2, 0), T(0.6f, -8, 2, 0), T(a, -8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Snap), T(f, -4, -4, 0, PoseEase.Over), T(1.9f, -4, -4, 0), T(r, -1, -1, 0), Z(end)),
            Turn(SpineD, T(0f, 6, -2, 0), T(0.6f, -8, 2, 0), T(a, -8, 2, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Snap), T(f, -4, -4, 0, PoseEase.Over), T(1.9f, -4, -4, 0), T(r, -1, -1, 0), Z(end)),
            Turn(Head, T(0f, -26, 4, 0), T(0.6f, 40, -4, 0), T(a, 50, -6, 0, PoseEase.In), T(h, 12, 8, 0, PoseEase.Snap), T(f, 10, 10, 0, PoseEase.Over), T(1.9f, 10, 10, 0), T(r, 3, 2, 0), Z(end)),
            Turn(LClav, T(0f, 14, 8, 0), T(0.6f, -14, 6, 0), T(a, -18, 8, 0, PoseEase.In), T(h, 16, 12, 0, PoseEase.Snap), T(f, 18, 14, 0, PoseEase.Over), T(1.9f, 18, 14, 0), T(r, 4, 4, 0), Z(end)),
            Turn(RClav, T(0f, 14, 8, 0), T(0.6f, -14, 6, 0), T(a, -18, 8, 0, PoseEase.In), T(h, 16, 12, 0, PoseEase.Snap), T(f, 18, 14, 0, PoseEase.Over), T(1.9f, 18, 14, 0), T(r, 4, 4, 0), Z(end)),
            // From the stun's end: the spear drawn back along the right hip, blade forward (0.6, a), the thrust (h: the left arm straight ahead, the right hand before the belly), held (1.9), out and
            // back to the carry. No-clipping pass (owner, 2026-11-10): the start, the drawn-back apex and the thrust placed in the chest's frame (IK); before, the butt end of the shaft held at the
            // butt rose into the right shoulder pad through the hold, and the start drifted into the chest.
            // Second follow-up pass: every key re-solved so the palms hold the shaft (the stab's line as approved).
            Upper("R", A(0f, -52, 15), A(0.6f, -29, 143), A(a, -65, 97, PoseEase.In), A(h, -55, 46, PoseEase.Snap), A(f, -55, 40, PoseEase.Over), A(1.9f, -55, 44), A(r, -1, 100), RestRUpper(end)),
            Fore("R", A(0f, 8, -53), A(0.6f, -10, 62), A(a, 7, 102, PoseEase.In), A(h, -7, -12, PoseEase.Snap), A(f, -7, -16, PoseEase.Over), A(1.9f, -7, -17), A(r, -48, 37), RestRFore(end)),
            Upper("L", A(0f, -34, -46), A(0.6f, -19, 86), A(a, -63, 117, PoseEase.In), A(h, 13, 35, PoseEase.Snap), A(f, -46, 21, PoseEase.Over), A(1.9f, -48, -1), A(r, -68, 75), RestLUpper(end)),
            Fore("L", A(0f, 45, 34), A(0.6f, 3, 9), A(a, 24, 102, PoseEase.In), A(h, -21, -10, PoseEase.Snap), A(f, 22, -9, PoseEase.Over), A(1.9f, 24, 8), A(r, 18, 2), RestLFore(end)),
        ],
        grip: (a, h, f, r, end) => [G(0f, 1f), G(r, 1f), G(r + GripSlideBack, 0f), G(end, 0f)],
        gripPoint: (a, h, f, r, end) => [P(0f, WeaponPoseClip.LowGrip), P(0.6f, WeaponPoseClip.StabGrip), P(r, WeaponPoseClip.StabGrip), P(r + GripSlideBack, WeaponPoseClip.CarryGrip), P(end, WeaponPoseClip.CarryGrip)]);

    /// <summary>
    /// The dive: both hands high on the shaft over the head, the left (blade) hand lower, so the spear hangs point-down and forward (held for the whole of the hammer's dive loop). No-clipping pass
    /// (owner, 2026-11-10): the right hand before the face rather than straight overhead, the left lower and forward (IK in the chest's frame), so the shaft between the hands stays in front of
    /// the jump pack's thrusters as the dive leans the body forward.
    /// </summary>
    public static readonly WeaponPoseClip SpearDive = new("spear_dive", "spear", SpearDiveLoopLength, float.NaN, SpearDiveLoop, 0f, 0f, 0f, float.NaN,
        "the hammer's dive loop (SM_JMP_TH_GP_Loop_01 1.767 s; the pose is held while it plays)",
        "the dive: the spear is turned point-down over the head, both hands high on the shaft, the body leaning into the fall",
        [
            Turn(Pelvis, T(0f, 0, -6, 0), T(SpearDiveLoopLength, 0, -6, 0)),
            Turn(SpineA, T(0f, 0, -3, 0), T(SpearDiveLoopLength, 0, -3, 0)),
            Turn(SpineD, T(0f, 0, -3, 0), T(SpearDiveLoopLength, 0, -3, 0)),
            Turn(Head, T(0f, 0, -10, 0), T(SpearDiveLoopLength, 0, -10, 0)),
            Upper("R", DiveRUpper(0f), DiveRUpper(SpearDiveLoopLength)),
            Fore("R", DiveRFore(0f), DiveRFore(SpearDiveLoopLength)),
            Upper("L", DiveLUpper(0f), DiveLUpper(SpearDiveLoopLength)),
            Fore("L", DiveLFore(0f), DiveLFore(SpearDiveLoopLength)),
        ]);

    /// <summary>The dive's hands (aims, el / az): the right hand before the face, the left lower and forward, the shaft point-down between them.</summary>
    /// <remarks>Second follow-up pass (2026-11-11): re-solved so the palms' grip sockets hold the shaft where the wrists did.</remarks>
    static PoseKey DiveRUpper(float t) => A(t, 9, 14); static PoseKey DiveRFore(float t) => A(t, 46, -27); static PoseKey DiveLUpper(float t) => A(t, -30, 41); static PoseKey DiveLFore(float t) => A(t, 33, 76);

    /// <summary>
    /// The impaling landing (the hammer's landing clip, 1.067 s): the impale lands with the landing (hit 0 s; design section 6.2) and the drive-down snaps home by <see cref="ImpaleDrive"/>. Owner's review of
    /// 2026-11-10 ("Point-down"): the blade stays driven point-down into the impaled Ork, as at the end of the dive, until the pound ends (<see cref="ImpaleHeld"/>), then is wrenched free by
    /// <see cref="ImpaleFree"/> and the pose is back on the get-up at the clip's end. The hands slide up the shaft as it is driven down (<see cref="ImpaleGrip"/>), so the blade sinks into the
    /// Ork at the feet from the deep crouch of the landing while the butt end stays clear of the head and the jump pack; the landing's torso keeps moving under the held spear, so the hands
    /// are keyed every 0.15 s in the chest's frame (IK; <c>SpearClearanceTests</c>). Before, the held keys laid the shaft flat at knee height toward his left (0.12 to 0.5 s) and released at 0.5 s.
    /// </summary>
    public static readonly WeaponPoseClip SpearImpale = new("spear_impale", "spear", SpearDiveImpactLength, 0f, SpearDiveImpact, 0f, ImpaleDrive, 1.0f, float.NaN,
        "the hammer's landing (SM_JMP_TH_GP_Impact_01 1.067 s; the impale at the landing, 0 s)",
        "impaling landing: the point-down spear is driven down into the impaled Ork at his feet as the marine lands, the hands sliding up the shaft, and held there, point-down, until the pound ends; then wrenched free",
        [
            // The landing clip folds the body far forward over the hammer (the spine about 80 degrees from upright, the jump pack on top); the hips lift the straight back to a lean of 35 to 45
            // degrees over the impaled Ork, so the hands can drive the spear down in front of the chest. Held to ImpaleHeld.
            Turn(Pelvis, T(0f, 0, -4, 0), T(ImpaleDrive, 0, 30, 0, PoseEase.Snap), T(ImpaleHeld, 0, 30, 0), T(ImpaleFree, 0, 10, 0), Z(SpearDiveImpactLength)),
            Turn(SpineA, T(0f, 0, -2, 0), T(ImpaleDrive, 0, 0, 0, PoseEase.Snap), T(ImpaleHeld, 0, 0, 0), T(ImpaleFree, 0, -2, 0), Z(SpearDiveImpactLength)),
            Turn(SpineD, T(0f, 0, -2, 0), T(ImpaleDrive, 0, 0, 0, PoseEase.Snap), T(ImpaleHeld, 0, 0, 0), T(ImpaleFree, 0, -2, 0), Z(SpearDiveImpactLength)),
            Turn(Head, T(0f, 0, -10, 0), T(ImpaleDrive, 0, -12, 0, PoseEase.Snap), T(ImpaleHeld, 0, -12, 0), T(ImpaleFree, 0, 2, 0), Z(SpearDiveImpactLength)),
            // Both hands high on the shaft over the impaled Ork, the right (upper) hand before the face, the left lower; keyed every 0.15 s while held (the landing's torso rises under them).
            // Second follow-up pass (2026-11-11): re-solved so the palms hold the shaft where the wrists did.
            Upper("R", DiveRUpper(0f), A(ImpaleDrive, 46, -31, PoseEase.Snap), A(0.2f, 4, -70), A(0.35f, 5, -64), A(0.5f, 6, -62), A(0.65f, 5, -60), A(ImpaleHeld, 4, -64), A(ImpaleFree, 27, -65), RestRUpper(SpearDiveImpactLength)),
            Fore("R", DiveRFore(0f), A(ImpaleDrive, 61, -62, PoseEase.Snap), A(0.2f, 60, -84), A(0.35f, 45, -88), A(0.5f, 42, -87), A(0.65f, 43, -89), A(ImpaleHeld, 45, -84), A(ImpaleFree, 43, -44), RestRFore(SpearDiveImpactLength)),
            Upper("L", DiveLUpper(0f), A(ImpaleDrive, -25, -36, PoseEase.Snap), A(0.2f, -2, -9), A(0.35f, -15, -40), A(0.5f, -14, -41), A(0.65f, -15, -39), A(ImpaleHeld, -11, -38), A(ImpaleFree, -1, -56), RestLUpper(SpearDiveImpactLength)),
            Fore("L", DiveLFore(0f), A(ImpaleDrive, -31, -78, PoseEase.Snap), A(0.2f, -25, -3), A(0.35f, 11, 23), A(0.5f, 11, 25), A(0.65f, 12, 24), A(ImpaleHeld, 8, 27), A(ImpaleFree, 19, 19), RestLFore(SpearDiveImpactLength)),
        ])
    {
        // Both hands on the shaft through the dive and the hold; the left lets go as he rises and the clip ends on the carry's arms with the shaft upright from the right hand at the low grip
        // (the landing still crouches him, too low for the carry's long butt end), so the carry that follows the pound takes over from it with the grip sliding up (the 0.14 s hand-over).
        Grip = [G(0f, 1f), G(ImpaleFree, 1f), G(SpearDiveImpactLength, 0f)],
        GripPoint = [P(0f, WeaponPoseClip.LowGrip), P(ImpaleDrive, ImpaleGrip, PoseEase.Snap), P(ImpaleHeld, ImpaleGrip), P(ImpaleFree, WeaponPoseClip.LowGrip), P(SpearDiveImpactLength, WeaponPoseClip.LowGrip)],
    };

    /// <summary>The impale's hold (s into the landing clip; owner, 2026-11-10: held point-down "until the pound ends") and when the spear is free of the Ork again (ours; the clip's last 0.12 s bring the hands back to the carry).</summary>
    public const float ImpaleHeld = 0.85f, ImpaleFree = 0.95f;
    /// <summary>When the drive-down is home (s; ours): the hand-over from the dive (<c>WeaponPoseState.BlendSeconds</c>, 0.14 s) and the snap end together, so the blend of the two never swings the blade into the legs (0.08 s before the no-clipping pass).</summary>
    public const float ImpaleDrive = 0.14f;
    /// <summary>Where the right hand holds the shaft while the impale is held (m from the butt; ours): high toward the blade, so the point reaches the Ork at his feet from the landing's deep crouch (the shoulders about 0.8 m up).</summary>
    public const float ImpaleGrip = 1.5f;

    /// <summary>
    /// The pound's landing when it impaled no Ork (owner via the lead, 2026-11-11: the point-down impale "only when the landing actually impaled/hit an Ork"; **ours**, composed of the impale's
    /// own keys, no pose of its own): the same touch-down as the impale (the dive's hands, the point dipping toward the ground by <see cref="ImpaleDrive"/> as the hands slide up the shaft; nothing
    /// is there, and the tip stays above the ground), but no hold: the impale's release pose by <see cref="LandFree"/>, the carry's arms with the left hand off the shaft by <see cref="LandRest"/>
    /// (the impale's own 0.12 s release), the hips easing out of the landing's lean by <see cref="LandRelax"/> before the shaft turns up (with the lean held, the release swung the blade through the
    /// knees: <c>SpearClearanceTests</c>), then the carry's arms held as the impale ends. Before 2026-11-11 every landing held the spear point-down in the ground to 0.85 s. Shown when the
    /// landing impaled nothing (<see cref="SpearLanding"/>).
    /// </summary>
    public static readonly WeaponPoseClip SpearLand = LandFrom(SpearImpale);

    /// <summary>The plain landing built from an impale clip's keys (<see cref="SpearLand"/> is it for <see cref="SpearImpale"/>).</summary>
    public static WeaponPoseClip LandFrom(WeaponPoseClip impale) => impale with
    {
        Name = "spear_land",
        HitTime = float.NaN,
        Settle = LandRest,
        Source = "the hammer's landing (SM_JMP_TH_GP_Impact_01 1.067 s; no impale: nothing was hit)",
        Description = "landing with no Ork impaled: the point dips toward the ground as the marine lands, finds nothing and comes straight back up, the left hand letting go, the spear upright beside him in the right hand as he rises",
        Channels = [.. impale.Channels.Select(LandChannel)],
        Grip = [G(0f, 1f), G(LandFree, 1f), G(LandRest, 0f), G(SpearDiveImpactLength, 0f)],
        GripPoint = [P(0f, WeaponPoseClip.LowGrip), P(ImpaleDrive, ImpaleGrip, PoseEase.Snap), P(LandFree, WeaponPoseClip.LowGrip), P(SpearDiveImpactLength, WeaponPoseClip.LowGrip)],
    };

    /// <summary>The plain landing: when the spear is free (the impale's release pose; s into the landing clip), when the carry's arms have it (the impale's own release length later), and when the hips are out of the lean (ours).</summary>
    public const float LandFree = 0.30f, LandRest = LandFree + 0.12f, LandRelax = LandFree + 0.10f;

    /// <summary>One of the impale's channels for the plain landing: the arms from its touch-down (its first two keys) to its release pose at <see cref="LandFree"/> and the carry's arms from <see cref="LandRest"/>; the pelvis's lean held to half way and eased to the release's by <see cref="LandRelax"/>; the back and head as they are.</summary>
    static PoseChannel LandChannel(PoseChannel c)
    {
        static PoseKey At(PoseChannel ch, float t) => ch.Keys.First(k => MathF.Abs(k.T - t) < 1e-4f);
        if (c.Kind == PoseChannelKind.Aim)
        {
            var rest = c.Keys[^1];   // the impale's own end: the carry's arms as its stance reads them
            return c with { Keys = [c.Keys[0], At(c, ImpaleDrive), At(c, ImpaleFree) with { T = LandFree, Ease = PoseEase.Smooth }, rest with { T = LandRest }, rest with { T = SpearDiveImpactLength }] };
        }
        if (c.Bone != "RIG_Pelvis") return c;
        var drive = At(c, ImpaleDrive);
        return c with { Keys = [c.Keys[0], drive, drive with { T = LandRelax * 0.5f, Ease = PoseEase.Smooth }, At(c, ImpaleFree) with { T = LandRelax }, c.Keys[^1]] };
    }

    /// <summary>The pound's landing clip: <see cref="SpearImpale"/> when the landing impaled an Ork (the server's impale), else <see cref="SpearLand"/> (owner via the lead, 2026-11-11).</summary>
    public static WeaponPoseClip SpearLanding(bool impaled) => impaled ? SpearImpale : SpearLand;

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
    public static readonly IReadOnlyList<WeaponPoseClip> All = [ClawX1, ClawX2, ClawX3, ClawX4, ClawHeavy, SpearRest, SpearHew1, SpearHew2, SpearHew3, SpearStun, SpearStab, SpearDive, SpearImpale, SpearLand];

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
