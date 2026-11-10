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

    /// <summary>X1: the diagonal rake, from high behind the right shoulder down across to past the left hip (the sword's X1 at 1.25x: 1.28 s, hit 0.256 s).</summary>
    public static readonly WeaponPoseClip ClawX1 = Claw("claw_x1", 1.600f, 0.320f, 0.867f, "the sword's X1 (SM_MEL_CS_ATK_F_01_D_3.5m 1.600 s, hit 0.320 s)",
        "diagonal rake: the hips and chest coil hard right with the claw drawn up behind the right shoulder, then the whole body whips left and down as the talons rake across the target to past the left hip; the pistol arm swings back as a counterweight",
        antic: 0.086f, follow: 0.125f, recover: 0.80f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -28, 8, -8, PoseEase.In), T(h, 30, -16, 8, PoseEase.Snap), T(f, 38, -20, 10, PoseEase.Over), T(r, 10, -5, 2), Z(end)),
            Turn(SpineD, Z(0f), T(a, -34, 10, -8, PoseEase.In), T(h, 38, -20, 10, PoseEase.Snap), T(f, 46, -24, 12, PoseEase.Over), T(r, 12, -6, 3), Z(end)),
            Turn(Head, Z(0f), T(a, 30, -6, 0, PoseEase.In), T(h, -14, 8, 0, PoseEase.Snap), T(f, -18, 10, 0, PoseEase.Over), T(r, -4, 2, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -14, 18, -10, PoseEase.In), T(h, 14, 10, 10, PoseEase.Snap), T(f, 18, 6, 12, PoseEase.Over), T(r, 4, 1, 3), Z(end)),
            Turn(LClav, Z(0f), T(a, -8, -4, 4, PoseEase.In), T(h, 10, -8, -6, PoseEase.Snap), T(f, 12, -8, -8, PoseEase.Over), T(r, 3, -2, -2), Z(end)),
            Upper("R", A(0f, 45, 110, w: 0f), A(a, 45, 110, PoseEase.In), A(h, -20, -30, PoseEase.Snap), A(f, -40, -55, PoseEase.Over), A(r, -40, -55, w: 0.3f), A(end, -40, -55, w: 0f)),
            Fore("R", A(0f, 60, 20, w: 0f), A(a, 60, 20, PoseEase.In), A(h, -50, -60, PoseEase.Snap), A(f, -65, -100, PoseEase.Over), A(r, -65, -100, w: 0.3f), A(end, -65, -100, w: 0f)),
            Turn(RHand, Z(0f), T(0f, 0, 0, 0), T(a, 0, 30, 0, PoseEase.In), T(h, 0, -20, 15, PoseEase.Snap), T(f, 0, -16, 20, PoseEase.Over), T(r, 0, -5, 6), Z(end)),
            Upper("L", A(0f, -20, -110, w: 0f), A(a, -10, -80, PoseEase.In, 0.7f), A(h, -25, -150, PoseEase.Snap, 0.8f), A(f, -20, -160, PoseEase.Over, 0.8f), A(r, -20, -160, w: 0.25f), A(end, -20, -160, w: 0f)),
            Fore("L", A(0f, -40, -100, w: 0f), A(a, -30, -60, PoseEase.In, 0.7f), A(h, -40, -170, PoseEase.Snap, 0.8f), A(f, -30, 180, PoseEase.Over, 0.8f), A(r, -30, 180, w: 0.25f), A(end, -30, 180, w: 0f)),
        ]);

    /// <summary>X2: the low gutting rake, from past the left hip up and across to above the right shoulder (the sword's X2 at 1.25x: 1.146 s, hit 0.230 s).</summary>
    public static readonly WeaponPoseClip ClawX2 = Claw("claw_x2", 1.433f, 0.287f, 0.767f, "the sword's X2 (SM_MEL_PA_XATK_Combo2MsA_3.5m 1.433 s, hit 0.287 s)",
        "gutting rake: from the diagonal rake's end the body crouches further left over the low claw, then the hips and chest uncoil up and to the right as the talons tear upward through the belly to above the right shoulder; the pistol arm shoves forward low as a brace",
        antic: 0.08f, follow: 0.12f, recover: 0.75f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, 24, -12, 4), T(a, 30, -18, 6, PoseEase.In), T(h, -28, 10, -10, PoseEase.Snap), T(f, -36, 14, -12, PoseEase.Over), T(r, -10, 4, -3), Z(end)),
            Turn(SpineD, T(0f, 28, -14, 6), T(a, 36, -22, 8, PoseEase.In), T(h, -34, 12, -12, PoseEase.Snap), T(f, -44, 16, -14, PoseEase.Over), T(r, -12, 4, -4), Z(end)),
            Turn(Head, T(0f, -12, 6, 0), T(a, -20, 14, 0, PoseEase.In), T(h, 16, -10, 0, PoseEase.Snap), T(f, 20, -12, 0, PoseEase.Over), T(r, 5, -3, 0), Z(end)),
            Turn(RClav, T(0f, 10, 4, 8), T(a, 14, -6, 10, PoseEase.In), T(h, -14, 18, -10, PoseEase.Snap), T(f, -16, 20, -12, PoseEase.Over), T(r, -4, 5, -3), Z(end)),
            Turn(LClav, T(0f, 8, -6, -6), T(a, 10, -8, -8, PoseEase.In), T(h, -10, 6, 6, PoseEase.Snap), T(f, -12, 6, 8, PoseEase.Over), T(r, -3, 2, 2), Z(end)),
            Upper("R", A(0f, -40, -55, w: 0.85f), A(a, -55, -45, PoseEase.In), A(h, 25, 50, PoseEase.Snap), A(f, 55, 85, PoseEase.Over), A(r, 55, 85, w: 0.3f), A(end, 55, 85, w: 0f)),
            Fore("R", A(0f, -65, -100, w: 0.85f), A(a, -60, -90, PoseEase.In), A(h, 45, 70, PoseEase.Snap), A(f, 70, 110, PoseEase.Over), A(r, 70, 110, w: 0.3f), A(end, 70, 110, w: 0f)),
            Turn(RHand, T(0f, 0, -16, 20), T(a, 0, -25, 25, PoseEase.In), T(h, 0, 20, -15, PoseEase.Snap), T(f, 0, 25, -20, PoseEase.Over), T(r, 0, 6, -6), Z(end)),
            Upper("L", A(0f, -20, -160, w: 0.6f), A(a, -30, -150, PoseEase.In, 0.7f), A(h, -30, -15, PoseEase.Snap, 0.9f), A(f, -25, -5, PoseEase.Over, 0.9f), A(r, -25, -5, w: 0.25f), A(end, -25, -5, w: 0f)),
            Fore("L", A(0f, -30, 180, w: 0.6f), A(a, -40, -170, PoseEase.In, 0.7f), A(h, -5, 0, PoseEase.Snap, 0.9f), A(f, 0, 0, PoseEase.Over, 0.9f), A(r, 0, 0, w: 0.25f), A(end, 0, 0, w: 0f)),
        ]);

    /// <summary>X3: the punching stab, the talons driven straight into the target's chest from the ribs (the sword's X3 at 1.25x: 1.414 s, hit 0.346 s); the claw stays in until the next strike rips it out.</summary>
    public static readonly WeaponPoseClip ClawX3 = Claw("claw_x3", 1.767f, 0.433f, 0.733f, "the sword's X3 (SM_MEL_CS_XATK_Combo3_3.5 1.767 s, hit 0.433 s)",
        "punching stab: the chest coils far right with the claw drawn back beside the ribs, talons forward, and the pistol arm out as a guard, then the hips and shoulder drive the arm straight out and the talons punch into the target's chest; the claw stays buried, the body leaning in, until the rip",
        antic: 0.14f, follow: 0.115f, recover: 1.0f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, -10, 4, -3), T(a, -34, 8, -8, PoseEase.In), T(h, 24, -18, 6, PoseEase.Snap), T(f, 30, -22, 8, PoseEase.Over), T(0.62f, 30, -22, 8), T(r, 8, -6, 2), Z(end)),
            Turn(SpineD, T(0f, -12, 4, -4), T(a, -40, 10, -10, PoseEase.In), T(h, 30, -22, 8, PoseEase.Snap), T(f, 36, -26, 10, PoseEase.Over), T(0.62f, 36, -26, 10), T(r, 10, -7, 3), Z(end)),
            Turn(Head, T(0f, 6, 0, 0), T(a, 34, -4, 0, PoseEase.In), T(h, -14, 6, 0, PoseEase.Snap), T(f, -18, 8, 0, PoseEase.Over), T(0.62f, -18, 8, 0), T(r, -5, 2, 0), Z(end)),
            Turn(RClav, T(0f, -4, 0, -4), T(a, -16, -8, -10, PoseEase.In), T(h, 16, 12, 10, PoseEase.Snap), T(f, 20, 14, 12, PoseEase.Over), T(0.62f, 20, 14, 12), T(r, 5, 4, 3), Z(end)),
            Turn(LClav, T(0f, 4, 2, 2), T(a, 12, 8, 6, PoseEase.In), T(h, -12, -6, -6, PoseEase.Snap), T(f, -14, -6, -8, PoseEase.Over), T(0.62f, -14, -6, -8), T(r, -4, -2, -2), Z(end)),
            Upper("R", A(0f, 55, 85, w: 0.7f), A(a, -25, 165, PoseEase.In), A(h, 0, 5, PoseEase.Snap), A(f, -5, 0, PoseEase.Over), A(0.62f, -5, 0), A(r, -5, 0, w: 0.3f), A(end, -5, 0, w: 0f)),
            Fore("R", A(0f, 70, 110, w: 0.7f), A(a, 10, 60, PoseEase.In), A(h, 0, 0, PoseEase.Snap), A(f, -5, -5, PoseEase.Over), A(0.62f, -5, -5), A(r, -5, -5, w: 0.3f), A(end, -5, -5, w: 0f)),
            Turn(RHand, T(0f, 0, 25, -20), T(a, 0, 10, 0, PoseEase.In), T(h, 0, 0, 0, PoseEase.Snap), T(f, 0, -5, 0, PoseEase.Over), T(0.62f, 0, -5, 0), T(r, 0, -2, 0), Z(end)),
            Upper("L", A(0f, -25, -5, w: 0.6f), A(a, -10, -15, PoseEase.In, 0.9f), A(h, -35, -120, PoseEase.Snap, 0.9f), A(f, -30, -150, PoseEase.Over, 0.9f), A(0.62f, -30, -150, w: 0.9f), A(r, -30, -150, w: 0.25f), A(end, -30, -150, w: 0f)),
            Fore("L", A(0f, 0, 0, w: 0.6f), A(a, 10, -5, PoseEase.In, 0.9f), A(h, -40, -160, PoseEase.Snap, 0.9f), A(f, -35, 170, PoseEase.Over, 0.9f), A(0.62f, -35, 170, w: 0.9f), A(r, -35, 170, w: 0.25f), A(end, -35, 170, w: 0f)),
        ]);

    /// <summary>X4: the tearing rip that ends the string: the buried talons twisted, then torn back and up past the right shoulder, the off hand shoving the target away (the sword's X4 at 1.25x: 1.334 s, hit 0.426 s).</summary>
    public static readonly WeaponPoseClip ClawX4 = Claw("claw_x4", 1.667f, 0.533f, float.NaN, "the sword's X4 (SM_MEL_CS_XATK_Combo4_3.5m 1.667 s, hit 0.533 s; the string ends)",
        "tearing rip: with the talons buried, the body leans in and twists the claw deeper, the pistol hand planted on the target, then the hips and chest wrench back and right and the talons tear out and up past the right shoulder while the off hand shoves the target away; a long recovery",
        antic: 0.16f, follow: 0.14f, recover: 0.90f, (a, h, f, r, end) =>
        [
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

    /// <summary>Y: the heavy, an overhead downward rake, the claw raised straight up behind the head and brought down through the target with the whole body folding over it (the sword's Y backhand at 1.25x: 1.2 s, hit 0.264 s).</summary>
    public static readonly WeaponPoseClip ClawHeavy = Claw("claw_heavy", 1.5f, 0.33f, float.NaN, "the sword's Y backhand (SM_MEL_CS_XATK_Draw2_3.5m 1.5 s, hit 0.33 s)",
        "overhead downward rake: the body rears back with the claw raised straight up and cocked behind the head and the pistol arm thrown back, then hips, chest and shoulder fold forward together and the talons rake down through the target to the ground; a heavy settle",
        antic: 0.10f, follow: 0.16f, recover: 0.85f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -10, 16, -4, PoseEase.In), T(h, 8, -26, 2, PoseEase.Snap), T(f, 10, -34, 2, PoseEase.Over), T(r, 3, -10, 0), Z(end)),
            Turn(SpineD, Z(0f), T(a, -12, 20, -4, PoseEase.In), T(h, 10, -32, 2, PoseEase.Snap), T(f, 12, -40, 2, PoseEase.Over), T(r, 4, -12, 0), Z(end)),
            Turn(Head, Z(0f), T(a, 10, 10, 0, PoseEase.In), T(h, -6, -10, 0, PoseEase.Snap), T(f, -8, -8, 0, PoseEase.Over), T(r, -2, -2, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -6, 24, -8, PoseEase.In), T(h, 8, 14, 8, PoseEase.Snap), T(f, 10, 10, 10, PoseEase.Over), T(r, 3, 3, 3), Z(end)),
            Turn(LClav, Z(0f), T(a, 6, 10, 6, PoseEase.In), T(h, -6, -10, -6, PoseEase.Snap), T(f, -8, -12, -8, PoseEase.Over), T(r, -2, -4, -2), Z(end)),
            Upper("R", A(0f, 80, 60, w: 0f), A(a, 80, 60, PoseEase.In), A(h, -40, 10, PoseEase.Snap), A(f, -55, 5, PoseEase.Over), A(r, -55, 5, w: 0.3f), A(end, -55, 5, w: 0f)),
            Fore("R", A(0f, 55, 175, w: 0f), A(a, 55, 175, PoseEase.In), A(h, -65, 0, PoseEase.Snap), A(f, -80, -10, PoseEase.Over), A(r, -80, -10, w: 0.3f), A(end, -80, -10, w: 0f)),
            Turn(RHand, Z(0f), T(a, 0, 35, 0, PoseEase.In), T(h, 0, -35, 0, PoseEase.Snap), T(f, 0, -40, 0, PoseEase.Over), T(r, 0, -12, 0), Z(end)),
            Upper("L", A(0f, -20, -110, w: 0f), A(a, 10, -140, PoseEase.In, 0.8f), A(h, -40, -60, PoseEase.Snap, 0.8f), A(f, -50, -40, PoseEase.Over, 0.8f), A(r, -50, -40, w: 0.25f), A(end, -50, -40, w: 0f)),
            Fore("L", A(0f, -40, -100, w: 0f), A(a, 20, -170, PoseEase.In, 0.8f), A(h, -50, -70, PoseEase.Snap, 0.8f), A(f, -60, -60, PoseEase.Over, 0.8f), A(r, -60, -60, w: 0.25f), A(end, -60, -60, w: 0f)),
        ]);

    /// <summary>
    /// A claw clip from the sword strike's length, hit and combo opening (s, the sword's) at the claw's tempo; <paramref name="antic"/> and <paramref name="follow"/> are seconds before and after
    /// the hit, <paramref name="recover"/> the recovery key's time. The builder gets (anticipation, hit, follow-through, recovery, end) in seconds.
    /// </summary>
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

    // ---- the power spear (two hands on the shaft: the right hand rear, the left hand forward; the stand-in's blade is beyond the left hand) ----

    /// <summary>The rest: the spear upright at the right side, butt down and blade up (the right arm hanging with the shaft by the thigh, the left hand gripping it above at the chest; aims, el / az).</summary>
    const float RestRUpperEl = -75f, RestRUpperAz = 60f, RestRForeEl = -70f, RestRForeAz = 40f, RestLUpperEl = -45f, RestLUpperAz = 70f, RestLForeEl = 75f, RestLForeAz = 65f;
    static PoseKey RestRUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRUpperEl, RestRUpperAz, ease);
    static PoseKey RestRFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestRForeEl, RestRForeAz, ease);
    static PoseKey RestLUpper(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLUpperEl, RestLUpperAz, ease);
    static PoseKey RestLFore(float t, PoseEase ease = PoseEase.Smooth) => A(t, RestLForeEl, RestLForeAz, ease);

    /// <summary>The spear's rest pose (owner, 2026-11-10: "upright at his side"), held over the hammer's idle for as long as the spear is carried and not swung; every sweep starts from it and returns to it.</summary>
    public static readonly WeaponPoseClip SpearRest = new("spear_rest", "spear", SpearIdleLength, float.NaN, SpearIdle, 0f, 0f, 0f, float.NaN,
        "the hammer's idle (SM_2HM_NAV_TH_IdleL_S_01 1.5 s; the pose is held while it loops)",
        "the rest: the spear stands upright at the marine's right side, butt by the foot and blade above the head, the right hand on the shaft at the hip and the left gripping it at the chest",
        [
            Turn(SpineD, T(0f, 6, 0, 0), T(SpearIdleLength, 6, 0, 0)),
            Turn(Head, T(0f, -4, 0, 0), T(SpearIdleLength, -4, 0, 0)),
            Upper("R", RestRUpper(0f), RestRUpper(SpearIdleLength)),
            Fore("R", RestRFore(0f), RestRFore(SpearIdleLength)),
            Upper("L", RestLUpper(0f), RestLUpper(SpearIdleLength)),
            Fore("L", RestLFore(0f), RestLFore(SpearIdleLength)),
        ]);

    /// <summary>Forehand sweep, right to left (the hammer's X1 at 0.85x: 3.138 s, hit 0.711 s).</summary>
    public static readonly WeaponPoseClip SpearForehand = Spear("spear_forehand", 2.667f, 0.604f, 0.933f, SpearIdle, "the hammer's X1 (SM_MEL_TH_XATK_Combo2 2.667 s, hit 0.604 s)",
        "forehand sweep: from the upright rest the spear is levelled and drawn back over the right hip, blade forward-right, then a flat two-handed sweep across the front from right to left; the rear hand swings behind the right hip",
        antic: 0.19f, follow: 0.29f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -30, 4, -4, PoseEase.In), T(h, 30, -6, 4, PoseEase.Snap), T(f, 40, -9, 6, PoseEase.Over), T(r, 9, -2, 1), Z(end)),
            Turn(SpineD, Z(0f), T(a, -40, 6, -6, PoseEase.In), T(h, 40, -10, 6, PoseEase.Snap), T(f, 52, -14, 8, PoseEase.Over), T(r, 12, -3, 2), Z(end)),
            Turn(Head, Z(0f), T(a, 35, -2, 0, PoseEase.In), T(h, -20, 2, 0, PoseEase.Snap), T(f, -26, 3, 0, PoseEase.Over), T(r, -6, 0, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -10, 4, 0, PoseEase.In), T(h, 12, 6, 0, PoseEase.Snap), T(f, 14, 6, 0, PoseEase.Over), T(r, 3, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -10, 4, 0, PoseEase.In), T(h, 12, 6, 0, PoseEase.Snap), T(f, 14, 6, 0, PoseEase.Over), T(r, 3, 1, 0), Z(end)),
            // The rear (right) hand swings opposite to the blade: low at the left hip while the blade is wound forward-right, behind the right hip as the blade sweeps to the left.
            Upper("R", RestRUpper(0f), A(a, -40, -20, PoseEase.In), A(h, -30, 100, PoseEase.Snap), A(f, -10, 40, PoseEase.Over), A(r, -45, 45), RestRUpper(end)),
            Fore("R", RestRFore(0f), A(a, -20, -100, PoseEase.In), A(h, -10, 150, PoseEase.Snap), A(f, 0, 20, PoseEase.Over), A(r, -40, 30), RestRFore(end)),
            Upper("L", RestLUpper(0f), A(a, -10, 40, PoseEase.In), A(h, -30, -20, PoseEase.Snap), A(f, -10, -90, PoseEase.Over), A(r, -25, 10), RestLUpper(end)),
            Fore("L", RestLFore(0f), A(a, 10, 70, PoseEase.In), A(h, 0, -40, PoseEase.Snap), A(f, -10, -150, PoseEase.Over), A(r, 30, 30), RestLFore(end)),
        ]);

    /// <summary>Backhand sweep, left to right (the hammer's X2 at 0.85x: 2.745 s, hit 0.667 s).</summary>
    public static readonly WeaponPoseClip SpearBackhand = Spear("spear_backhand", 2.333f, 0.567f, 0.967f, SpearIdle, "the hammer's X2 (SM_MEL_TH_XATK_Combo3 2.333 s, hit 0.567 s)",
        "backhand sweep: from the forehand's end the spear is gathered over the left shoulder and swept back across the front from left to right",
        antic: 0.18f, follow: 0.28f, recover: 1.9f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, 14, -3, 2), T(a, 26, 3, 4, PoseEase.In), T(h, -30, -6, -4, PoseEase.Snap), T(f, -38, -9, -6, PoseEase.Over), T(r, -9, -2, -1), Z(end)),
            Turn(SpineD, T(0f, 18, -4, 3), T(a, 36, 4, 6, PoseEase.In), T(h, -40, -8, -6, PoseEase.Snap), T(f, -50, -12, -8, PoseEase.Over), T(r, -12, -3, -2), Z(end)),
            Turn(Head, T(0f, -10, 0, 0), T(a, -30, -2, 0, PoseEase.In), T(h, 25, 2, 0, PoseEase.Snap), T(f, 30, 3, 0, PoseEase.Over), T(r, 7, 0, 0), Z(end)),
            Turn(LClav, T(0f, 4, 2, 0), T(a, 10, 4, 0, PoseEase.In), T(h, -12, 6, 0, PoseEase.Snap), T(f, -14, 6, 0, PoseEase.Over), T(r, -3, 1, 0), Z(end)),
            Turn(RClav, T(0f, 4, 2, 0), T(a, 10, 4, 0, PoseEase.In), T(h, -12, 6, 0, PoseEase.Snap), T(f, -14, 6, 0, PoseEase.Over), T(r, -3, 1, 0), Z(end)),
            // From the forehand's end (blade left-back, the rear hand forward-right): the rear hand drops behind the left hip as the blade is gathered, then swings forward-left as the blade sweeps right.
            Upper("R", A(0f, -10, 40, w: 0.6f), A(a, -10, 30, PoseEase.In), A(h, -40, -60, PoseEase.Snap), A(f, -10, -30, PoseEase.Over), A(r, -45, 45), RestRUpper(end)),
            Fore("R", A(0f, 0, 20, w: 0.6f), A(a, 0, 10, PoseEase.In), A(h, -20, -150, PoseEase.Snap), A(f, 0, -20, PoseEase.Over), A(r, -40, 30), RestRFore(end)),
            Upper("L", A(0f, 0, -90, w: 0.6f), A(a, 0, -80, PoseEase.In), A(h, 0, 20, PoseEase.Snap), A(f, -10, 90, PoseEase.Over), A(r, -25, 10), RestLUpper(end)),
            Fore("L", A(0f, -10, -150, w: 0.6f), A(a, -10, -140, PoseEase.In), A(h, 10, 50, PoseEase.Snap), A(f, -10, 150, PoseEase.Over), A(r, 30, 30), RestLFore(end)),
        ]);

    /// <summary>
    /// The 240 degree spin sweep that ends the string (the hammer's X3 at 0.85x: 2.745 s, hit 0.824 s). The feet stay planted, so the chest winds about 50 degrees right and the arms carry
    /// the blade behind the right shoulder; the blade passes the front at the hit at a constant rate (<see cref="PoseEase.Linear"/>) and runs on past the left shoulder.
    /// </summary>
    public static readonly WeaponPoseClip SpearSpin = Spear("spear_spin", 2.333f, 0.700f, float.NaN, SpearIdle, "the hammer's X3 (SM_MEL_TH_HeavyATK02 2.333 s, hit 0.700 s; the string ends)",
        "spin sweep: the deepest wind-up, the blade behind the right shoulder and the eyes on the target, then one unbroken sweep through the front and on past the left shoulder, back to the upright rest",
        antic: 0.20f, follow: 0.18f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -38, 6, -6, PoseEase.In), T(h, 0, -8, 0, PoseEase.Linear), T(f, 40, -8, 6, PoseEase.Out), T(f + 0.25f, 44, -6, 6, PoseEase.Over), T(r, 12, -2, 2), Z(end)),
            Turn(SpineD, Z(0f), T(a, -48, 4, -8, PoseEase.In), T(h, 0, -10, 0, PoseEase.Linear), T(f, 52, -8, 8, PoseEase.Out), T(f + 0.25f, 56, -6, 8, PoseEase.Over), T(r, 15, -2, 2), Z(end)),
            Turn(Head, Z(0f), T(a, 45, -2, 0, PoseEase.In), T(h, 0, 0, 0, PoseEase.Linear), T(f, -40, 2, 0, PoseEase.Out), T(f + 0.25f, -42, 2, 0, PoseEase.Over), T(r, -10, 0, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -12, 4, 0, PoseEase.In), T(h, 0, 8, 0, PoseEase.Linear), T(f, 12, 6, 0, PoseEase.Out), T(f + 0.25f, 14, 6, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -12, 4, 0, PoseEase.In), T(h, 0, 8, 0, PoseEase.Linear), T(f, 12, 6, 0, PoseEase.Out), T(f + 0.25f, 14, 6, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            // The blade behind the right shoulder (the forward hand reaching right-back, the rear hand forward-left), through the front (the rear hand drawn behind the hips, the forward hand straight ahead), on to left-back.
            Upper("R", A(0f, -10, -30, w: 0.6f), A(a, -20, -20, PoseEase.In), A(h, -50, 150, PoseEase.Linear), A(f, -10, 40, PoseEase.Out), A(f + 0.25f, -10, 50, PoseEase.Over), A(r, -45, 45), RestRUpper(end)),
            Fore("R", A(0f, 0, -20, w: 0.6f), A(a, 0, -40, PoseEase.In), A(h, -30, 180, PoseEase.Linear), A(f, 0, 30, PoseEase.Out), A(f + 0.25f, 0, 40, PoseEase.Over), A(r, -40, 30), RestRFore(end)),
            Upper("L", A(0f, -10, 90, w: 0.6f), A(a, -10, 100, PoseEase.In), A(h, 0, 0, PoseEase.Linear), A(f, 0, -100, PoseEase.Out), A(f + 0.25f, 0, -110, PoseEase.Over), A(r, -25, 10), RestLUpper(end)),
            Fore("L", A(0f, -10, 150, w: 0.6f), A(a, -10, 150, PoseEase.In), A(h, 10, 0, PoseEase.Linear), A(f, -10, -150, PoseEase.Out), A(f + 0.25f, -10, -160, PoseEase.Over), A(r, 30, 30), RestLFore(end)),
        ]);

    /// <summary>The dive: both hands high on the shaft over the head, the left (blade) hand lower, so the spear hangs point-down and forward (held for the whole of the hammer's dive loop).</summary>
    public static readonly WeaponPoseClip SpearDive = new("spear_dive", "spear", SpearDiveLoopLength, float.NaN, SpearDiveLoop, 0f, 0f, 0f, float.NaN,
        "the hammer's dive loop (SM_JMP_TH_GP_Loop_01 1.767 s; the pose is held while it plays)",
        "the dive: the spear is turned point-down over the head, both hands high on the shaft, the body leaning into the fall",
        [
            Turn(SpineA, T(0f, 0, -4, 0), T(SpearDiveLoopLength, 0, -4, 0)),
            Turn(SpineD, T(0f, 0, -8, 0), T(SpearDiveLoopLength, 0, -8, 0)),
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
            Turn(SpineA, T(0f, 0, -4, 0), T(0.08f, 0, -14, 0, PoseEase.Snap), T(0.5f, 0, -16, 0), T(0.75f, 0, -6, 0), Z(SpearDiveImpactLength)),
            Turn(SpineD, T(0f, 0, -8, 0), T(0.08f, 0, -24, 0, PoseEase.Snap), T(0.5f, 0, -26, 0), T(0.75f, 0, -8, 0), Z(SpearDiveImpactLength)),
            Turn(Head, T(0f, 0, -10, 0), T(0.08f, 0, -12, 0, PoseEase.Snap), T(0.5f, 0, -8, 0), T(0.75f, 0, 2, 0), Z(SpearDiveImpactLength)),
            // The rear (right) hand stays high by the chest, the forward (left) hand drives straight down to knee height, so the blade stands in the ground a little ahead of the feet.
            Upper("R", A(0f, 60, 10), A(0.08f, -30, 60, PoseEase.Snap), A(0.5f, -35, 60), A(0.75f, -10, 60), A(SpearDiveImpactLength, -10, 60, w: 0f)),
            Fore("R", A(0f, 80, 0), A(0.08f, 40, -30, PoseEase.Snap), A(0.5f, 35, -30), A(0.75f, 60, -20), A(SpearDiveImpactLength, 60, -20, w: 0f)),
            Upper("L", A(0f, 20, -10), A(0.08f, -40, 10, PoseEase.Snap), A(0.5f, -45, 10), A(0.75f, 0, 10), A(SpearDiveImpactLength, 0, 10, w: 0f)),
            Fore("L", A(0f, -10, 0), A(0.08f, -80, 0, PoseEase.Snap), A(0.5f, -82, 0), A(0.75f, -30, 0), A(SpearDiveImpactLength, -30, 0, w: 0f)),
        ]);

    /// <summary>A spear clip from the hammer strike's length, hit and combo opening (s, the hammer's) at the spear's tempo.</summary>
    static WeaponPoseClip Spear(string name, float hammerLength, float hammerHit, float hammerCombo, string baseClip, string source, string description, float antic, float follow, float recover, Func<float, float, float, float, float, PoseChannel[]> build)
    {
        float d = hammerLength / SpearTempo, h = hammerHit / SpearTempo;
        float a = h - antic, f = h + follow;
        return new WeaponPoseClip(name, "spear", d, h, baseClip, a, f, Math.Min(d, recover + 0.4f), float.IsNaN(hammerCombo) ? float.NaN : hammerCombo / SpearTempo, source, description, build(a, h, f, recover, d));
    }

    /// <summary>Every clip, in the preview's order.</summary>
    public static readonly IReadOnlyList<WeaponPoseClip> All = [ClawX1, ClawX2, ClawX3, ClawX4, ClawHeavy, SpearRest, SpearForehand, SpearBackhand, SpearSpin, SpearDive, SpearImpale];

    /// <summary>The spear's sweeps (they start from and return to <see cref="SpearRest"/>).</summary>
    public static readonly IReadOnlyList<WeaponPoseClip> SpearSweeps = [SpearForehand, SpearBackhand, SpearSpin];

    /// <summary>A clip by name (any case), or null.</summary>
    public static WeaponPoseClip? ByName(string name) => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
