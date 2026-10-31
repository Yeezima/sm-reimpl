using System;
using Godot;
using SmSim;

namespace SmClient;

/// <summary>
/// anim/feel-pass (the owner, 2026-10-30: "the tactical roll is PERFECT. if you can make the rest of the animations/actions blend and transition that well and in the same style/way we
/// will be golden"; client presentation only, no simulation or protocol change). What the roll does, the pattern applied here:
/// <list type="number">
/// <item><b>One whole-body clip at the simulation's clock</b>: the roll clip is posed at the roll's own time, eased onto each snapshot, never jumping (<c>NetworkView.Evade.cs</c>); the
/// body travels the clip's own extracted root motion, so the feet and the ground agree.</item>
/// <item><b>In from a moving copy</b>: the graph's 'Nav to Evade' 0.1 s, at least 0.1875 s on the smoothstep curve, faded from a copy of the shown pose that keeps each bone's velocity
/// (dead blending, <c>CharacterViewer.Transitions.cs</c>).</item>
/// <item><b>Out over 'Standard Blend' 0.333 s</b>: at the clip's own run / idle branch the locomotion takes the whole body back over the base graph's 'Evade to Idle' / 'Evade to Run'
/// 10-frame blend, again from a moving copy, so the last of the roll flows into the stride.</item>
/// <item><b>The camera eases</b> to the roll's camera set and back on the same smooth curve.</item>
/// </list>
/// The roll owns the whole body from entry to exit: nothing is cut short and no layer fights it. What this pass changes so the other actions leave the same way:
/// <list type="bullet">
/// <item><b>Every whole-body action leaves through the navigation conduit's 'Standard Blend' 0.333 s</b> (<see cref="LeavesThroughNavConduit"/>). In <c>SM_Base_MP</c> the reactions
/// (<c>Reactions ( From Ranged )</c> → <c>TO Ranged Nav</c>), the jump pack (<c>Exotic - JumpPack</c> → <c>TO Ranged Nav</c>), the grenade toss (<c>Use Equipment ( From Ranged )</c> →
/// <c>TO Ranged Nav</c>) and the drawn melee attack (<c>FROM Melee Draw Attack</c> → <c>TO Ranged Nav</c>) all go back through the conduit <c>TO Ranged Nav</c> (or <c>TO Melee Nav</c>),
/// whose every way into a weapon's navigation state is 'Standard Blend' 0.333 s, the roll's 10 frames. The client had left a reaction or a knife strike over 0.2 s (ours, "conduits carry
/// no blend") and a jump-pack landing, get-up, pound or slash and the toss over the stance's own 0.15 s. That the conduit's onward blend is the one the hop uses is an
/// <b>interpretation</b> of the graph.</item>
/// <item><b>The air slash leaves like a strike handed over</b> (<see cref="SlashHandoff"/>): a marine that moves on 0.3 s after the slash's landing (the simulation's free time, ours)
/// had the whole clip cut to the run in the middle of its follow-through. Now the legs go to the locomotion as a melee strike's do at its move gate (<c>NetworkView.Handoff.cs</c>) and
/// the strike's follow-through stays on the upper body, fading out over the sword's or chainsword's move-on exit ('Standard Blend' 0.333 s) on the smooth curve.</item>
/// <item><b>The slash's wind-up never changes speed in a step</b> (<see cref="SmNet.SlashPose.SmoothDiveTime"/>): a Hermite time curve over the dive with the clip's own rate at both ends.</item>
/// </list>
/// <c>--no-feel</c> (test aid) switches every part off for before / after runs.
/// </summary>
public partial class NetworkView
{
    /// <summary>'Standard Blend' (<c>SM_Base_MP</c>: every edge out of the <c>TO Ranged Nav</c> / <c>TO Melee Nav</c> conduits into a weapon's navigation state): 10 frames at 30 fps.</summary>
    public const float NavConduitBlend = 10f / 30f;

    /// <summary>
    /// Whether a whole-body clip goes back to the locomotion through the base graph's navigation conduit (see the class comment): a reaction (<c>SM_REC_</c>), a melee strike
    /// (<c>SM_MEL_</c>, for a weapon whose own graph's exit is not read: the knife's draw attack), a jump-pack clip (<c>SM_JMP_</c>, <c>SM_DSH_</c>: landings, the pound's impact and
    /// get-up, the dash attack's impact) and the grenade toss.
    /// </summary>
    static bool LeavesThroughNavConduit(string clip) =>
        clip.StartsWith("SM_REC_", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("SM_MEL_", StringComparison.OrdinalIgnoreCase)
        || clip.StartsWith("SM_JMP_", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("SM_DSH_", StringComparison.OrdinalIgnoreCase)
        || clip.Contains("Toss", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The owner's decision (2026-10-30, ours): a hit reaction (the interrupt, the stun and stagger, their get-ups: <c>SM_REC_*</c>, <c>SM_MP_REC_*</c>) leaves for the locomotion over
    /// 0.2 s, snappier than the data's 0.333 s the other actions keep.
    /// </summary>
    public const float ReactionExitBlend = 0.2f;

    /// <summary>A hit reaction's clip (<c>SM_REC_</c>, <c>SM_MP_REC_</c>).</summary>
    static bool IsReactionClip(string clip) =>
        clip.StartsWith("SM_REC_", StringComparison.OrdinalIgnoreCase) || clip.StartsWith("SM_MP_REC_", StringComparison.OrdinalIgnoreCase);

    int _slashHandoffs;

    /// <summary>
    /// anim/feel-pass: the heavy bolter has no relaxed state in the graphs (<c>SM_Heavy_Bolter</c>: its navigation is the ready one), so a marine carrying it stays in the ready posture
    /// whenever it is not sprinting, as the ready hold after a shot shows it. Before, once the ready hold ran out the pose dropped to the rifle's relaxed clips (M36's stand-in), whose
    /// left hand is nowhere near the heavy bolter's grip: every lowering and raising swung the hand 40 to 50 cm and turned it up to 74 degrees a frame (the transition probe's worst
    /// move / shoot switches). The sprint, rolls, strikes and reactions keep their own clips. Data (no relaxed state); that it means "always ready" is an interpretation.
    /// </summary>
    bool HeavyStaysReady(PlayerState s)
    {
        if (CharacterViewer.NoFeel || !s.Alive || s.Aiming || s.Reaction != ReactionKind.None || s.MeleeStep != 0 || s.Throwing || s.SwitchLeft > 0f) return false;
        if (HipFamilyOf(s) != SmContent.HipFireWeaponClass.Heavy) return false;
        float speed = MathF.Sqrt(s.Velocity.X * s.Velocity.X + s.Velocity.Y * s.Velocity.Y);
        float forward = s.Velocity.X * -MathF.Sin(s.Yaw) + s.Velocity.Y * MathF.Cos(s.Yaw);
        return !LocoSprinting(s, LocoOf(s.PlayerId), speed, forward);
    }

    /// <summary>The slash wind-up's clip time in the dive (<see cref="SmNet.SlashPose.SmoothDiveTime"/>; <c>--no-feel</c>: the jump-pack-2 curve).</summary>
    static float SlashDiveTime(float sinceDive, float expectedDive, float flightEnd) =>
        CharacterViewer.NoFeel ? SmNet.SlashPose.DiveTime(sinceDive, expectedDive, flightEnd, CharacterViewer.MinEntryBlendSeconds) : SmNet.SlashPose.SmoothDiveTime(sinceDive, expectedDive, flightEnd);

    /// <summary>
    /// anim/air-slash-character: <c>--old-slash-windup</c> restores the feel pass's wind-up (the whole wind-up fitted into the dive, <see cref="SmNet.SlashPose.SmoothDiveTime"/>,
    /// and the 0.1 s catch-up at the landing) instead of the one curve from the dive to the strike at the hit (<see cref="SmNet.SlashPose.StrikeDiveTime"/>). <c>--no-feel</c> implies it.
    /// </summary>
    static readonly bool OldSlashWindup = OS.GetCmdlineUserArgs().Contains("--old-slash-windup");
    static bool StrikeCurve => !OldSlashWindup && !CharacterViewer.NoFeel;

    /// <summary>The slash clip's time <paramref name="sinceDive"/> s into <paramref name="s"/>'s dive (the strike curve, or <see cref="SlashDiveTime"/> with <c>--old-slash-windup</c>).</summary>
    float SlashDiveTimeOf(PlayerState s, float sinceDive, float expectedDive)
    {
        var slash = SlashOf(s);
        float flightEnd = slash?.FlightClipEnd ?? 0f;
        return StrikeCurve && slash is not null
            ? SmNet.SlashPose.StrikeDiveTime(sinceDive, expectedDive, flightEnd, slash.ImpactClipStart, slash.HitTime)
            : SlashDiveTime(sinceDive, expectedDive, flightEnd);
    }

    /// <summary>The rate of <see cref="SlashDiveTimeOf"/> at <paramref name="sinceDive"/> (NaN with <c>--old-slash-windup</c>: the old catch-up needs none).</summary>
    float SlashDiveRateOf(PlayerState s, float sinceDive, float expectedDive) =>
        StrikeCurve && SlashOf(s) is { } slash ? SmNet.SlashPose.StrikeDiveRate(sinceDive, expectedDive, slash.FlightClipEnd, slash.ImpactClipStart, slash.HitTime) : float.NaN;

    /// <summary>
    /// Called when the slash's get-up view ends because the marine moves on (<c>NetworkView.Jump.cs</c>): the follow-through is handed to the upper body from now, unless it is over.
    /// </summary>
    void SlashMovedOn(JumpTrack t, double now, double sinceImpact, double clipLeft)
    {
        if (CharacterViewer.NoFeel || sinceImpact >= clipLeft) return;
        t.SlashHandAt = now; t.SlashHandImpactAt = t.ImpactAt;
        _slashHandoffs++;
    }

    /// <summary>
    /// The air slash's follow-through over the moving marine's upper body (see the class comment): true when the legs were placed by the locomotion and the strike laid over the upper
    /// body (the action mask), false when no hand-over runs (none started, over, another action took the body, or no mask: the locomotion as before).
    /// </summary>
    bool SlashHandoff(CharacterViewer character, PlayerState s, bool own)
    {
        if (CharacterViewer.NoFeel || _slashClip is null || !_jumpTracks.TryGetValue(s.PlayerId, out var t) || double.IsNaN(t.SlashHandAt)) return false;
        float since = (float)(_connectedFor - t.SlashHandAt);
        float exit = MeleeExitBlendOf(s, _slashClip) ?? NavConduitBlend;
        float clipTime = SlashImpactTime(s, (float)(_connectedFor - t.SlashHandImpactAt));
        if (!s.Alive || t.View != JumpView.None || since < 0f || since >= exit || clipTime >= ClipLength(_slashClip, 1.733f) || ActionMask() is not { } mask)
        {
            t.SlashHandAt = double.NaN;
            return false;
        }
        PlaceHandedOver(character, s, own);
        character.SetPoseLayer(_slashClip, clipTime, 1f - CharacterViewer.Smooth(since / exit), additive: false, mask);
        return true;
    }
}
