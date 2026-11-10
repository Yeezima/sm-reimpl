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
/// pistol in the left hand, the right arm hanging low and back).</item>
/// <item>Spear: the thunder hammer's at 0.85x, so each is the hammer's divided by <see cref="SpearTempo"/> (spec "Thunder hammer X1..X3": hits 0.604 / 0.567 / 0.700 s, lengths 2.667 / 2.333 / 2.333 s).
/// Laid over the hammer's two-handed idle (<see cref="SpearIdle"/>, both hands low in front); the dive over the hammer's dive loop and landing (<see cref="SpearDiveLoop"/>,
/// <see cref="SpearDiveImpact"/>), whose impale lands with the landing (0 s of the impact clip).</item>
/// </list>
/// The arms are keyed as directions (<see cref="PoseChannelKind.Aim"/>: where the upper arm and the forearm point, elevation and azimuth), so a pose means the same over any stance; the spine,
/// head, clavicles and hands as turns. Every strike has a clear anticipation (the body coils, <see cref="PoseEase.In"/>), a snap on the strike (<see cref="PoseEase.Snap"/>, or
/// <see cref="PoseEase.Linear"/> through the hit of a sweep), a weighted follow-through (<see cref="PoseEase.Over"/>) and a settle back to the stance; the legs are never keyed, so the feet
/// stay where the stance puts them. The spear's blade is beyond the left (forward) hand, the butt beyond the right, as a polearm is held.
/// </summary>
public static class ClawSpearClips
{
    /// <summary>The claw plays the sword's strikes 1.25x faster (design D13; the factor ours).</summary>
    public const float ClawTempo = 1.25f;
    /// <summary>The spear plays the hammer's strikes at 0.85x (design D14; the factor an estimate).</summary>
    public const float SpearTempo = 0.85f;

    /// <summary>The one-handed melee stance the claw's strikes start from (the chainsword's idle: pistol in the left hand, the melee weapon in the right).</summary>
    public const string ClawIdle = "SM_NAV_IdleL_CS_01";
    /// <summary>The hammer's two-handed idle (<c>SM_TH_NAV</c> "Idle"), the spear's stance.</summary>
    public const string SpearIdle = "SM_2HM_NAV_TH_IdleL_S_01";
    /// <summary>The hammer's jump-pack dive loop and its landing (design section 6.2: the spear turned point-down from the dive's start until the get-up).</summary>
    public const string SpearDiveLoop = "SM_JMP_TH_GP_Loop_01", SpearDiveImpact = "SM_JMP_TH_GP_Impact_01";
    /// <summary>The hammer's dive loop's and landing clip's lengths (s), from the install's clip list.</summary>
    public const float SpearDiveLoopLength = 1.767f, SpearDiveImpactLength = 1.067f;

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

    /// <summary>X1: a cross slash, high right to low left (the sword's X1 at 1.25x: 1.28 s, hit 0.256 s).</summary>
    public static readonly WeaponPoseClip ClawX1 = Claw("claw_x1", 1.600f, 0.320f, 0.867f, "the sword's X1 (SM_MEL_CS_ATK_F_01_D_3.5m 1.600 s, hit 0.320 s)",
        "cross slash: the body coils right with the claw cocked high by the ear, then rakes down across to the left",
        antic: 0.086f, follow: 0.115f, recover: 0.80f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -14, 4, -2, PoseEase.In), T(h, 22, -8, 4, PoseEase.Snap), T(f, 26, -9, 5, PoseEase.Over), T(r, 7, -3, 1), Z(end)),
            Turn(SpineD, Z(0f), T(a, -18, 5, -4, PoseEase.In), T(h, 28, -12, 6, PoseEase.Snap), T(f, 34, -14, 7, PoseEase.Over), T(r, 10, -4, 2), Z(end)),
            Turn(Head, Z(0f), T(a, 16, -3, 0, PoseEase.In), T(h, -10, 4, 0, PoseEase.Snap), T(f, -12, 5, 0, PoseEase.Over), T(r, -3, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -6, 10, -6, PoseEase.In), T(h, 8, 6, 6, PoseEase.Snap), T(f, 10, 4, 8, PoseEase.Over), T(r, 3, 1, 2), Z(end)),
            Upper("R", A(0f, 25, 75, w: 0f), A(a, 25, 75, PoseEase.In), A(h, -10, 10, PoseEase.Snap), A(f, -25, -10, PoseEase.Over), A(r, -25, -10, w: 0.3f), A(end, -25, -10, w: 0f)),
            Fore("R", A(0f, 50, -30, w: 0f), A(a, 50, -30, PoseEase.In), A(h, -30, -45, PoseEase.Snap), A(f, -45, -60, PoseEase.Over), A(r, -45, -60, w: 0.3f), A(end, -45, -60, w: 0f)),
            Turn(RHand, Z(0f), T(a, 0, 20, 0, PoseEase.In), T(h, 0, -10, 10, PoseEase.Snap), T(f, 0, -8, 12, PoseEase.Over), T(r, 0, -3, 4), Z(end)),
            Upper("L", A(0f, -20, -120, w: 0f), A(a, -20, -120, PoseEase.In, 0.5f), A(h, -35, -130, PoseEase.Snap, 0.5f), A(f, -40, -130, PoseEase.Over, 0.5f), A(r, -40, -130, w: 0.15f), A(end, -40, -130, w: 0f)),
        ]);

    /// <summary>X2: a backhand rake, left to right (the sword's X2 at 1.25x: 1.146 s, hit 0.230 s).</summary>
    public static readonly WeaponPoseClip ClawX2 = Claw("claw_x2", 1.433f, 0.287f, 0.767f, "the sword's X2 (SM_MEL_PA_XATK_Combo2MsA_3.5m 1.433 s, hit 0.287 s)",
        "backhand rake: from the cross slash's end the claw is drawn across the chest and whipped out flat to the right",
        antic: 0.08f, follow: 0.11f, recover: 0.75f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, 12, -3, 2), T(a, 14, -4, 2, PoseEase.In), T(h, -20, -6, -4, PoseEase.Snap), T(f, -26, -7, -5, PoseEase.Over), T(r, -8, -2, -1), Z(end)),
            Turn(SpineD, T(0f, 18, -4, 3), T(a, 24, -6, 3, PoseEase.In), T(h, -26, -8, -5, PoseEase.Snap), T(f, -32, -9, -6, PoseEase.Over), T(r, -10, -3, -2), Z(end)),
            Turn(Head, T(0f, -8, 2, 0), T(a, -12, 2, 0, PoseEase.In), T(h, 12, 0, 0, PoseEase.Snap), T(f, 14, 0, 0, PoseEase.Over), T(r, 4, 0, 0), Z(end)),
            Turn(RClav, T(0f, 6, 4, 4), T(a, 8, 8, 6, PoseEase.In), T(h, -8, 8, -6, PoseEase.Snap), T(f, -10, 6, -8, PoseEase.Over), T(r, -3, 2, -2), Z(end)),
            Upper("R", A(0f, -20, -10, w: 0.8f), A(a, 10, -20, PoseEase.In), A(h, 5, 70, PoseEase.Snap), A(f, 0, 95, PoseEase.Over), A(r, 0, 95, w: 0.3f), A(end, 0, 95, w: 0f)),
            Fore("R", A(0f, -40, -55, w: 0.8f), A(a, 30, -80, PoseEase.In), A(h, 0, 95, PoseEase.Snap), A(f, -10, 120, PoseEase.Over), A(r, -10, 120, w: 0.3f), A(end, -10, 120, w: 0f)),
            Turn(RHand, T(0f, 0, -5, 8), T(a, 0, 5, 20, PoseEase.In), T(h, 0, 0, -12, PoseEase.Snap), T(f, 0, 0, -16, PoseEase.Over), T(r, 0, 0, -5), Z(end)),
            Upper("L", A(0f, -40, -130, w: 0.4f), A(a, -35, -125, PoseEase.In, 0.5f), A(h, -25, -110, PoseEase.Snap, 0.5f), A(f, -25, -105, PoseEase.Over, 0.5f), A(r, -25, -105, w: 0.15f), A(end, -25, -105, w: 0f)),
        ]);

    /// <summary>X3: a rising rake, low behind the hip to high in front (the sword's X3 at 1.25x: 1.414 s, hit 0.346 s).</summary>
    public static readonly WeaponPoseClip ClawX3 = Claw("claw_x3", 1.767f, 0.433f, 0.733f, "the sword's X3 (SM_MEL_CS_XATK_Combo3_3.5 1.767 s, hit 0.433 s)",
        "rising rake: a crouch with the claw drawn back behind the hip, then the talons tear upward through the target's chest to above the head",
        antic: 0.106f, follow: 0.115f, recover: 0.95f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, -6, -3, -1), T(a, -10, -12, -3, PoseEase.In), T(h, 6, 6, 2, PoseEase.Snap), T(f, 8, 8, 2, PoseEase.Over), T(r, 2, 2, 0), Z(end)),
            Turn(SpineD, T(0f, -8, -4, -2), T(a, -12, -16, -3, PoseEase.In), T(h, 8, 10, 3, PoseEase.Snap), T(f, 10, 14, 3, PoseEase.Over), T(r, 3, 4, 1), Z(end)),
            Turn(Head, T(0f, 4, 2, 0), T(a, 8, 10, 0, PoseEase.In), T(h, -4, -4, 0, PoseEase.Snap), T(f, -6, -6, 0, PoseEase.Over), T(r, -2, -2, 0), Z(end)),
            Turn(RClav, T(0f, -2, -2, -2), T(a, -4, -6, -4, PoseEase.In), T(h, 2, 14, 2, PoseEase.Snap), T(f, 2, 16, 2, PoseEase.Over), T(r, 1, 5, 1), Z(end)),
            Upper("R", A(0f, 0, 95, w: 0.8f), A(a, -60, 160, PoseEase.In), A(h, 10, 15, PoseEase.Snap), A(f, 45, 10, PoseEase.Over), A(r, 45, 10, w: 0.3f), A(end, 45, 10, w: 0f)),
            Fore("R", A(0f, -10, 120, w: 0.8f), A(a, -50, 170, PoseEase.In), A(h, 55, 10, PoseEase.Snap), A(f, 80, 0, PoseEase.Over), A(r, 80, 0, w: 0.3f), A(end, 80, 0, w: 0f)),
            Turn(RHand, T(0f, 0, -8, 0), T(a, 0, -20, 0, PoseEase.In), T(h, 0, 15, 0, PoseEase.Snap), T(f, 0, 20, 0, PoseEase.Over), T(r, 0, 6, 0), Z(end)),
            Upper("L", A(0f, -25, -105, w: 0.4f), A(a, -10, -90, PoseEase.In, 0.5f), A(h, -30, -120, PoseEase.Snap, 0.5f), A(f, -35, -125, PoseEase.Over, 0.5f), A(r, -35, -125, w: 0.15f), A(end, -35, -125, w: 0f)),
        ]);

    /// <summary>X4: the overhead stab that ends the string (the sword's X4 at 1.25x: 1.334 s, hit 0.426 s).</summary>
    public static readonly WeaponPoseClip ClawX4 = Claw("claw_x4", 1.667f, 0.533f, float.NaN, "the sword's X4 (SM_MEL_CS_XATK_Combo4_3.5m 1.667 s, hit 0.533 s; the string ends)",
        "overhead stab: the claw is raised straight up and cocked behind the head, then driven down into the target with the whole upper body; a long recovery",
        antic: 0.126f, follow: 0.13f, recover: 0.90f, (a, h, f, r, end) =>
        [
            Turn(SpineA, T(0f, 2, 3, 1), T(a, -4, 8, 0, PoseEase.In), T(h, 2, -14, 0, PoseEase.Snap), T(f, 2, -17, 0, PoseEase.Over), T(r, 1, -9, 0), Z(end)),
            Turn(SpineD, T(0f, 3, 5, 1), T(a, -6, 12, 0, PoseEase.In), T(h, 4, -22, 0, PoseEase.Snap), T(f, 4, -26, 0, PoseEase.Over), T(r, 2, -14, 0), Z(end)),
            Turn(Head, T(0f, -2, -2, 0), T(a, 4, 4, 0, PoseEase.In), T(h, -2, -8, 0, PoseEase.Snap), T(f, -2, -6, 0, PoseEase.Over), T(r, -1, -2, 0), Z(end)),
            Turn(RClav, T(0f, 1, 6, 0), T(a, 0, 14, -2, PoseEase.In), T(h, 2, 10, 2, PoseEase.Snap), T(f, 2, 8, 2, PoseEase.Over), T(r, 1, 4, 1), Z(end)),
            Upper("R", A(0f, 45, 10, w: 0.8f), A(a, 70, 30, PoseEase.In), A(h, -20, 5, PoseEase.Snap), A(f, -30, 0, PoseEase.Over), A(r, -30, 0, w: 0.55f), A(end, -30, 0, w: 0f)),
            Fore("R", A(0f, 80, 0, w: 0.8f), A(a, 40, 150, PoseEase.In), A(h, -50, 0, PoseEase.Snap), A(f, -60, -5, PoseEase.Over), A(r, -60, -5, w: 0.55f), A(end, -60, -5, w: 0f)),
            Turn(RHand, T(0f, 0, 6, 0), T(a, 0, 25, 0, PoseEase.In), T(h, 0, -30, 0, PoseEase.Snap), T(f, 0, -32, 0, PoseEase.Over), T(r, 0, -16, 0), Z(end)),
            Upper("L", A(0f, -35, -125, w: 0.4f), A(a, -5, -110, PoseEase.In, 0.5f), A(h, -40, -120, PoseEase.Snap, 0.5f), A(f, -45, -120, PoseEase.Over, 0.5f), A(r, -45, -120, w: 0.25f), A(end, -45, -120, w: 0f)),
        ]);

    /// <summary>Y: the heavy backhand, a wide stunning sweep from the left across to the right (the sword's Y backhand at 1.25x: 1.2 s, hit 0.264 s).</summary>
    public static readonly WeaponPoseClip ClawHeavy = Claw("claw_heavy", 1.5f, 0.33f, float.NaN, "the sword's Y backhand (SM_MEL_CS_XATK_Draw2_3.5m 1.5 s, hit 0.33 s)",
        "heavy backhand: the claw is wound far across the left shoulder, the chest turned with it, then the whole upper body unwinds into a wide flat sweep to the right",
        antic: 0.10f, follow: 0.14f, recover: 0.85f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, 18, -2, 3, PoseEase.In), T(h, -26, -6, -5, PoseEase.Snap), T(f, -32, -7, -6, PoseEase.Over), T(r, -10, -2, -2), Z(end)),
            Turn(SpineD, Z(0f), T(a, 30, -4, 4, PoseEase.In), T(h, -36, -6, -6, PoseEase.Snap), T(f, -44, -8, -7, PoseEase.Over), T(r, -13, -2, -2), Z(end)),
            Turn(Head, Z(0f), T(a, -20, 0, 0, PoseEase.In), T(h, 4, 0, 0, PoseEase.Snap), T(f, 10, 0, 0, PoseEase.Over), T(r, 3, 0, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, 10, 8, 8, PoseEase.In), T(h, -10, 10, -8, PoseEase.Snap), T(f, -12, 8, -10, PoseEase.Over), T(r, -4, 2, -3), Z(end)),
            Upper("R", A(0f, 15, -35, w: 0f), A(a, 15, -35, PoseEase.In), A(h, 5, 60, PoseEase.Snap), A(f, 0, 110, PoseEase.Over), A(r, 0, 110, w: 0.3f), A(end, 0, 110, w: 0f)),
            Fore("R", A(0f, 10, -110, w: 0f), A(a, 10, -110, PoseEase.In), A(h, 0, 80, PoseEase.Snap), A(f, -10, 140, PoseEase.Over), A(r, -10, 140, w: 0.3f), A(end, -10, 140, w: 0f)),
            Turn(RHand, Z(0f), T(a, 0, 0, 25, PoseEase.In), T(h, 0, 0, -20, PoseEase.Snap), T(f, 0, 0, -24, PoseEase.Over), T(r, 0, 0, -7), Z(end)),
            Upper("L", A(0f, -40, -130, w: 0f), A(a, -30, -150, PoseEase.In, 0.5f), A(h, -30, -110, PoseEase.Snap, 0.5f), A(f, -30, -100, PoseEase.Over, 0.5f), A(r, -30, -100, w: 0.15f), A(end, -30, -100, w: 0f)),
        ]);

    /// <summary>
    /// A claw clip from the sword strike's length, hit and combo opening (s, the sword's) at the claw's tempo; <paramref name="antic"/> and <paramref name="follow"/> are seconds before and after
    /// the hit, <paramref name="recover"/> the recovery key's time. The builder gets (anticipation, hit, follow-through, recovery, end) in seconds.
    /// </summary>
    static WeaponPoseClip Claw(string name, float swordLength, float swordHit, float swordCombo, string source, string description, float antic, float follow, float recover, Func<float, float, float, float, float, PoseChannel[]> build)
    {
        float d = swordLength / ClawTempo, h = swordHit / ClawTempo;
        float a = h - antic, f = h + follow;
        return new WeaponPoseClip(name, "claw", d, h, ClawIdle, a, f, Math.Min(d, recover + 0.35f), float.IsNaN(swordCombo) ? float.NaN : swordCombo / ClawTempo, source, description, build(a, h, f, recover, d));
    }

    // ---- the power spear (two hands on the shaft: the right hand rear, the left hand forward; the stand-in's blade is beyond the left hand) ----

    /// <summary>Forehand sweep, right to left (the hammer's X1 at 0.85x: 3.138 s, hit 0.711 s).</summary>
    public static readonly WeaponPoseClip SpearForehand = Spear("spear_forehand", 2.667f, 0.604f, 0.933f, SpearIdle, "the hammer's X1 (SM_MEL_TH_XATK_Combo2 2.667 s, hit 0.604 s)",
        "forehand sweep: a long wind-up with the spear drawn back over the right hip, blade forward-right, then a flat two-handed sweep across the front from right to left",
        antic: 0.19f, follow: 0.29f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -30, 4, -4, PoseEase.In), T(h, 30, -6, 4, PoseEase.Snap), T(f, 40, -9, 6, PoseEase.Over), T(r, 9, -2, 1), Z(end)),
            Turn(SpineD, Z(0f), T(a, -40, 6, -6, PoseEase.In), T(h, 40, -10, 6, PoseEase.Snap), T(f, 52, -14, 8, PoseEase.Over), T(r, 12, -3, 2), Z(end)),
            Turn(Head, Z(0f), T(a, 35, -2, 0, PoseEase.In), T(h, -20, 2, 0, PoseEase.Snap), T(f, -26, 3, 0, PoseEase.Over), T(r, -6, 0, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -10, 4, 0, PoseEase.In), T(h, 12, 6, 0, PoseEase.Snap), T(f, 14, 6, 0, PoseEase.Over), T(r, 3, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -10, 4, 0, PoseEase.In), T(h, 12, 6, 0, PoseEase.Snap), T(f, 14, 6, 0, PoseEase.Over), T(r, 3, 1, 0), Z(end)),
            // The rear (right) hand swings opposite to the blade: low at the left hip while the blade is wound forward-right, behind the right hip as the blade sweeps to the left.
            Upper("R", A(0f, -40, -20, w: 0f), A(a, -40, -20, PoseEase.In), A(h, -30, 100, PoseEase.Snap), A(f, -10, 40, PoseEase.Over), A(r, -10, 40, w: 0.25f), A(end, -10, 40, w: 0f)),
            Fore("R", A(0f, -20, -100, w: 0f), A(a, -20, -100, PoseEase.In), A(h, -10, 150, PoseEase.Snap), A(f, 0, 20, PoseEase.Over), A(r, 0, 20, w: 0.25f), A(end, 0, 20, w: 0f)),
            Upper("L", A(0f, -10, 40, w: 0f), A(a, -10, 40, PoseEase.In), A(h, -30, -20, PoseEase.Snap), A(f, -10, -90, PoseEase.Over), A(r, -10, -90, w: 0.25f), A(end, -10, -90, w: 0f)),
            Fore("L", A(0f, 10, 70, w: 0f), A(a, 10, 70, PoseEase.In), A(h, 0, -40, PoseEase.Snap), A(f, -10, -150, PoseEase.Over), A(r, -10, -150, w: 0.25f), A(end, -10, -150, w: 0f)),
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
            Upper("R", A(0f, -10, 40, w: 0.6f), A(a, -10, 30, PoseEase.In), A(h, -40, -60, PoseEase.Snap), A(f, -10, -30, PoseEase.Over), A(r, -10, -30, w: 0.25f), A(end, -10, -30, w: 0f)),
            Fore("R", A(0f, 0, 20, w: 0.6f), A(a, 0, 10, PoseEase.In), A(h, -20, -150, PoseEase.Snap), A(f, 0, -20, PoseEase.Over), A(r, 0, -20, w: 0.25f), A(end, 0, -20, w: 0f)),
            Upper("L", A(0f, 0, -90, w: 0.6f), A(a, 0, -80, PoseEase.In), A(h, 0, 20, PoseEase.Snap), A(f, -10, 90, PoseEase.Over), A(r, -10, 90, w: 0.25f), A(end, -10, 90, w: 0f)),
            Fore("L", A(0f, -10, -150, w: 0.6f), A(a, -10, -140, PoseEase.In), A(h, 10, 50, PoseEase.Snap), A(f, -10, 150, PoseEase.Over), A(r, -10, 150, w: 0.25f), A(end, -10, 150, w: 0f)),
        ]);

    /// <summary>
    /// The 240 degree spin sweep that ends the string (the hammer's X3 at 0.85x: 2.745 s, hit 0.824 s). The feet stay planted, so the chest winds about 50 degrees right and the arms carry
    /// the blade behind the right shoulder; the blade passes the front at the hit at a constant rate (<see cref="PoseEase.Linear"/>) and runs on past the left shoulder.
    /// </summary>
    public static readonly WeaponPoseClip SpearSpin = Spear("spear_spin", 2.333f, 0.700f, float.NaN, SpearIdle, "the hammer's X3 (SM_MEL_TH_HeavyATK02 2.333 s, hit 0.700 s; the string ends)",
        "spin sweep: the deepest wind-up, the blade behind the right shoulder and the eyes on the target, then one unbroken sweep through the front and on past the left shoulder",
        antic: 0.20f, follow: 0.18f, recover: 2.0f, (a, h, f, r, end) =>
        [
            Turn(SpineA, Z(0f), T(a, -38, 6, -6, PoseEase.In), T(h, 0, -8, 0, PoseEase.Linear), T(f, 40, -8, 6, PoseEase.Out), T(f + 0.25f, 44, -6, 6, PoseEase.Over), T(r, 12, -2, 2), Z(end)),
            Turn(SpineD, Z(0f), T(a, -48, 4, -8, PoseEase.In), T(h, 0, -10, 0, PoseEase.Linear), T(f, 52, -8, 8, PoseEase.Out), T(f + 0.25f, 56, -6, 8, PoseEase.Over), T(r, 15, -2, 2), Z(end)),
            Turn(Head, Z(0f), T(a, 45, -2, 0, PoseEase.In), T(h, 0, 0, 0, PoseEase.Linear), T(f, -40, 2, 0, PoseEase.Out), T(f + 0.25f, -42, 2, 0, PoseEase.Over), T(r, -10, 0, 0), Z(end)),
            Turn(LClav, Z(0f), T(a, -12, 4, 0, PoseEase.In), T(h, 0, 8, 0, PoseEase.Linear), T(f, 12, 6, 0, PoseEase.Out), T(f + 0.25f, 14, 6, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            Turn(RClav, Z(0f), T(a, -12, 4, 0, PoseEase.In), T(h, 0, 8, 0, PoseEase.Linear), T(f, 12, 6, 0, PoseEase.Out), T(f + 0.25f, 14, 6, 0, PoseEase.Over), T(r, 4, 1, 0), Z(end)),
            // The blade behind the right shoulder (the forward hand reaching right-back, the rear hand forward-left), through the front (the rear hand drawn behind the hips, the forward hand straight ahead), on to left-back.
            Upper("R", A(0f, -20, -20, w: 0f), A(a, -20, -20, PoseEase.In), A(h, -50, 150, PoseEase.Linear), A(f, -10, 40, PoseEase.Out), A(f + 0.25f, -10, 50, PoseEase.Over), A(r, -10, 50, w: 0.3f), A(end, -10, 50, w: 0f)),
            Fore("R", A(0f, 0, -40, w: 0f), A(a, 0, -40, PoseEase.In), A(h, -30, 180, PoseEase.Linear), A(f, 0, 30, PoseEase.Out), A(f + 0.25f, 0, 40, PoseEase.Over), A(r, 0, 40, w: 0.3f), A(end, 0, 40, w: 0f)),
            Upper("L", A(0f, -10, 100, w: 0f), A(a, -10, 100, PoseEase.In), A(h, 0, 0, PoseEase.Linear), A(f, 0, -100, PoseEase.Out), A(f + 0.25f, 0, -110, PoseEase.Over), A(r, 0, -110, w: 0.3f), A(end, 0, -110, w: 0f)),
            Fore("L", A(0f, -10, 150, w: 0f), A(a, -10, 150, PoseEase.In), A(h, 10, 0, PoseEase.Linear), A(f, -10, -150, PoseEase.Out), A(f + 0.25f, -10, -160, PoseEase.Over), A(r, -10, -160, w: 0.3f), A(end, -10, -160, w: 0f)),
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
    /// the target until 0.5 s, is pulled out by 0.75 s and the pose is back on the get-up at the clip's end.
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
    public static readonly IReadOnlyList<WeaponPoseClip> All = [ClawX1, ClawX2, ClawX3, ClawX4, ClawHeavy, SpearForehand, SpearBackhand, SpearSpin, SpearDive, SpearImpale];

    /// <summary>A clip by name (any case), or null.</summary>
    public static WeaponPoseClip? ByName(string name) => All.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
