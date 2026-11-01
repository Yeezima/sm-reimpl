using System.Numerics;

namespace SmSim;

/// <summary>
/// A hit reaction a player can be put in (protocol 17). The multiplayer reaction types the data triggers on players
/// (wargear and blueprint <c>trigger_reaction</c>, after the player blueprint's <c>multiplayer_reaction_mapping</c>):
/// <c>reaction_mp_melee</c>, <c>reaction_mp_stun</c> and <c>reaction_mp_stagger</c>. Other reaction types (flinches such as
/// <c>reaction_flinch</c>, <c>reaction_ranged_light</c>, and the campaign's <c>ck_x</c> / <c>cs_2xy</c> names) are not reactions
/// of a multiplayer player here: the first are upper-body flinches, the others are not mapped (spec/behavior.md).
/// </summary>
public enum ReactionKind : byte
{
    None = 0,
    /// <summary><c>reaction_mp_melee</c>: the light interrupt (UI "Light stun/interrupt"; the light-attack interrupt clips).</summary>
    Interrupt = 1,
    /// <summary><c>reaction_mp_stun</c>: the stun (the multiplayer grenade-reaction clips, four directions; interpretation).</summary>
    Stun = 2,
    /// <summary><c>reaction_mp_stagger</c>: the stagger (the stagger clips, front and back).</summary>
    Stagger = 3,
}

/// <summary>Where the hit came from, relative to the victim's facing at the hit (picks the reaction clip; sectors ours).</summary>
public enum ReactionDirection : byte { Front = 0, Right = 1, Back = 2, Left = 3 }

/// <summary>
/// One entry of a <c>trigger_reaction</c>'s <c>reaction_by_intensity</c>: from <see cref="MinIntensity"/> (a blast's intensity at
/// the target; 0 for direct hits) the reaction <see cref="Kind"/> with the entry's <c>reaction_distance</c> knockback: metres
/// back along the hit (<see cref="Backward"/>), to the right (<see cref="Right"/>) and up (<see cref="Up"/>) over
/// <see cref="Duration"/> seconds. Units as in the file (metres and seconds assumed).
/// </summary>
public readonly record struct ReactionEntry(float MinIntensity, ReactionKind Kind, float Backward = 0f, float Right = 0f, float Up = 0f, float Duration = 0f)
{
    internal void Write(BinaryWriter w)
    {
        w.Write(MinIntensity); w.Write((byte)Kind); w.Write(Backward); w.Write(Right); w.Write(Up); w.Write(Duration);
    }

    internal static void Write(BinaryWriter w, IReadOnlyList<ReactionEntry> entries)
    {
        w.Write(entries.Count);
        foreach (var e in entries) e.Write(w);
    }
}

/// <summary>
/// A reaction clip's timing (seconds from the reaction's start; from its sequencer in the marine rig's
/// <c>MP_SM_Reactions</c> graph: tag start × clip length).
/// </summary>
/// <param name="Animation">The clip (<c>SM_REC_…</c>).</param>
/// <param name="Duration">The clip's length.</param>
/// <param name="EndAt"><c>BRANCH_Reaction_End</c>: the reaction is over; the player acts normally (the clip's end without the tag).</param>
/// <param name="RunAt"><c>BRANCH_Run</c>: from here the stick ends the reaction (EndAt without the tag).</param>
/// <param name="EvadeAt"><c>BRANCH_Evade</c>: from here a Jump press ends it and evades (or jumps) in the same tick (EndAt without the tag).</param>
/// <param name="MeleeAt"><c>BRANCH_Melee_Combo</c>: from here a melee press ends it and attacks (EndAt without the tag).</param>
/// <param name="ReactAt"><c>BRANCH_HitReact</c>: from here another hit may start a new reaction (EndAt without the tag).</param>
public sealed record ReactionClip(string Animation, float Duration, float EndAt, float RunAt, float EvadeAt, float MeleeAt, float ReactAt)
{
    internal void Write(BinaryWriter w)
    {
        w.Write(Animation);
        foreach (float f in new[] { Duration, EndAt, RunAt, EvadeAt, MeleeAt, ReactAt }) w.Write(f);
    }
}

/// <summary>
/// A reaction kind's clips (front, right, back, left). <see cref="FourWay"/> false: only front and back exist (the pie
/// wedges of the data's stagger and light-interrupt states); the side clips are then the front's and never chosen.
/// </summary>
public sealed record ReactionSpec(ReactionKind Kind, bool FourWay, ReactionClip Front, ReactionClip Right, ReactionClip Back, ReactionClip Left)
{
    public ReactionClip Clip(ReactionDirection d) => d switch
    {
        ReactionDirection.Right => FourWay ? Right : Front,
        ReactionDirection.Back => Back,
        ReactionDirection.Left => FourWay ? Left : Front,
        _ => Front,
    };

    public IEnumerable<ReactionClip> Clips => FourWay ? [Front, Right, Back, Left] : [Front, Back];

    internal void Write(BinaryWriter w)
    {
        w.Write((byte)Kind); w.Write(FourWay);
        Front.Write(w); Right.Write(w); Back.Write(w); Left.Write(w);
    }
}

/// <summary>
/// Hit reactions (M28; rules **interpretation** of the data, spec/behavior.md "Reactions"). A reaction is applied by the world
/// after the victim's own tick (<see cref="World"/>: melee hits, blasts); from then on it runs in the victim's own tick
/// (<see cref="Combat.StepPlayer"/>), so client prediction reproduces it exactly from the snapshot.
/// <para>
/// While it lasts the player does nothing but turn: movement, firing, charging, reloading, switching, throwing, melee, aim,
/// jumping and evading are ignored (presses are lost). Its body moves only by the knockback (<see cref="Move"/>) and gravity.
/// It ends when <see cref="ReactionClip.EndAt"/> has played, or earlier by input: a Jump press from
/// <see cref="ReactionClip.EvadeAt"/> (the evade or jump then starts in that tick: the "stun break"), a melee or heavy-melee
/// press from <see cref="ReactionClip.MeleeAt"/>, a pushed stick from <see cref="ReactionClip.RunAt"/> (feel/locks-sprint-stop: the Jump and stick branches
/// × <see cref="SimConfig.ActionLockScale"/>, <see cref="EscapeAt"/>). A tick checks the time
/// played before it (as evades do).
/// </para>
/// </summary>
public static class Reaction
{
    const float Eps = 1e-5f;
    /// <summary>Largest <see cref="PlayerState.ReactionTicks"/> (one byte on the wire); the config refuses clips that end later.</summary>
    public const int MaxTicks = byte.MaxValue;
    /// <summary>Largest knockback distance a state carries: <see cref="PlayerState.PushDistance"/> is in 1/16 m in one byte.</summary>
    public const float MaxPushDistance = 255f / 16f;

    /// <summary>The data's multiplayer reaction names: <c>reaction_mp_melee</c>, <c>reaction_mp_stun</c>, <c>reaction_mp_stagger</c>.</summary>
    public const string InterruptName = "reaction_mp_melee", StunName = "reaction_mp_stun", StaggerName = "reaction_mp_stagger";

    /// <summary>The kind of a reaction type name (after the reaction mapping); None for any other name (flinches, campaign reactions).</summary>
    public static ReactionKind KindOf(string? name) => name switch
    {
        InterruptName => ReactionKind.Interrupt,
        StunName => ReactionKind.Stun,
        StaggerName => ReactionKind.Stagger,
        _ => ReactionKind.None,
    };

    /// <summary>The spec of a kind (<see cref="SimConfig.Reactions"/>), or null for None or a kind the config lacks.</summary>
    public static ReactionSpec? SpecOf(SimConfig c, ReactionKind kind)
    {
        foreach (var r in c.Reactions) if (r.Kind == kind) return r;
        return null;
    }

    /// <summary>The clip the player's reaction plays, or null when not reacting.</summary>
    public static ReactionClip? ClipOf(PlayerState s, SimConfig c) =>
        s.Reaction != ReactionKind.None && SpecOf(c, s.Reaction) is { } spec ? spec.Clip(s.ReactionDirection) : null;

    /// <summary>Seconds of the reaction clip played after the player's last tick.</summary>
    public static float Time(PlayerState s, SimConfig c) => Math.Max(0, s.ReactionTicks) * c.TickSeconds;

    /// <summary>A state with no reaction and no knockback (blindness is separate).</summary>
    public static PlayerState Cleared(PlayerState s) => s with
    {
        Reaction = ReactionKind.None, ReactionDirection = ReactionDirection.Front, ReactionTicks = 0, PushYaw = 0, PushDistance = 0, PushTicks = 0,
    };

    /// <summary>
    /// The start of a tick: whether the player's reaction holds it this tick (ticks counted on), or is over (cleared, the tick
    /// then acts normally on the real input). <paramref name="jumpPress"/>, <paramref name="meleePress"/> and
    /// <paramref name="stick"/> are this tick's real input.
    /// </summary>
    internal static (bool Active, PlayerState State) Begin(PlayerState s, SimConfig c, bool jumpPress, bool meleePress, bool stick, bool anyInput = false)
    {
        if (s.Reaction == ReactionKind.None) return (false, s);
        if (ClipOf(s, c) is not { } clip) return (false, Cleared(s));
        float played = Time(s, c);
        if (!Holds(s.Reaction))
        {
            // sim/combat-2 (item 36, the owner's decision of 2026-10-30): a light reaction (the interrupt) plays its clip but holds nobody: the first tick with any
            // input (the stick or any button) ends it and acts normally on that input; only the hit's knockback (a few ticks) runs before. Without input it runs on to its end.
            float pushEnd = s.PushDistance > 0 ? s.PushTicks * c.TickSeconds : 0f;
            if (played + Eps >= clip.EndAt || (anyInput && played + Eps >= pushEnd)) return (false, Cleared(s));
            return (true, s with { ReactionTicks = Math.Min(s.ReactionTicks + 1, MaxTicks) });
        }
        bool over = played + Eps >= clip.EndAt
            || (jumpPress && played + Eps >= EscapeAt(s, clip.EvadeAt, clip.EndAt, c))
            || (meleePress && played + Eps >= clip.MeleeAt)
            || (stick && played + Eps >= EscapeAt(s, clip.RunAt, clip.EndAt, c));
        if (over) return (false, Cleared(s));
        return (true, s with { ReactionTicks = Math.Min(s.ReactionTicks + 1, MaxTicks) });
    }

    /// <summary>
    /// sim/combat-2 (item 36): whether a reaction of this kind holds the victim (nothing but turning until it ends: the stun and the stagger); the light one (the light-attack interrupt)
    /// only interrupt the action and play their clip (<see cref="Begin"/>). Which hits are light and which heavy is the data's (the hit tables pick the kind).
    /// </summary>
    public static bool Holds(ReactionKind kind) => kind is ReactionKind.Stun or ReactionKind.Stagger;   // the owner (review of sim/combat-2): only the light-attack interrupt is escapable; stagger and stun hold

    /// <summary>
    /// feel/locks-sprint-stop: when a reaction's stick or Jump branch (<see cref="ReactionClip.RunAt"/>, <see cref="ReactionClip.EvadeAt"/>) opens:
    /// <paramref name="branch"/> × <see cref="SimConfig.ActionLockScale"/>, never before the knockback's last tick (<see cref="PlayerState.PushTicks"/>; ours:
    /// the push is the hit's, not a lock). Only a branch the data really has is scaled (one before the clip's end <paramref name="endAt"/>): a clip without
    /// it (its branch time falls back to the end: the stun's stick, every stagger branch) keeps its full length (decided in review, 2026-10-29).
    /// The end, the melee branch and the re-react branch are the data's.
    /// </summary>
    public static float EscapeAt(PlayerState s, float branch, float endAt, SimConfig c)
    {
        if (!(branch + Eps < endAt)) return branch;   // no such branch: the clip's end, unscaled
        float scaled = c.LockTime(branch);
        if (!(scaled < branch)) return scaled;   // the data's time (a scale of 1), or a longer one
        return MathF.Min(branch, MathF.Max(scaled, s.PushDistance > 0 ? s.PushTicks * c.TickSeconds : 0f));
    }

    /// <summary>
    /// The knockback velocity of a state's tick <paramref name="tick"/> (1-based): ticks 1..<see cref="PlayerState.PushTicks"/> move
    /// <see cref="PlayerState.PushDistance"/> / 16 m in all along <see cref="PlayerState.PushYaw"/> (2π/256 steps; yaw 0 faces +Y);
    /// zero afterwards. Integers in, so server and client compute the same floats.
    /// </summary>
    public static Vector2 PushVelocity(PlayerState s, int tick, SimConfig c)
    {
        if (s.PushTicks <= 0 || s.PushDistance <= 0 || tick < 1 || tick > s.PushTicks) return Vector2.Zero;
        float speed = s.PushDistance / 16f / (s.PushTicks * c.TickSeconds);
        float yaw = s.PushYaw * (2f * MathF.PI / 256f);
        return new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw)) * speed;
    }

    /// <summary>
    /// A reacting tick's movement (after <see cref="Begin"/> counted the tick): the knockback for this tick as a velocity
    /// through the capsule controller (walls stop it, ledges drop it, gravity applies), else standing still; the body turns
    /// with the view (as during a melee attack).
    /// </summary>
    internal static PlayerState Move(PlayerState s, float yaw, SimConfig c, CollisionWorld? world) =>
        Movement.Glide(s, yaw, PushVelocity(s, s.ReactionTicks, c), c, world);

    /// <summary>
    /// The entry of a <c>reaction_by_intensity</c> list for a hit of <paramref name="intensity"/>: the entry with the largest
    /// MinIntensity not above it (1e-4 tolerance); none below every entry (the ground pound reacts only from 0.8).
    /// </summary>
    public static ReactionEntry? Pick(IReadOnlyList<ReactionEntry> entries, float intensity)
    {
        ReactionEntry? best = null;
        foreach (var e in entries)
            if (e.Kind != ReactionKind.None && e.MinIntensity <= intensity + 1e-4f && (best is null || e.MinIntensity > best.Value.MinIntensity)) best = e;
        return best;
    }

    /// <summary>
    /// Which clip a hit picks: where <paramref name="from"/> (the attacker or the blast point) lies relative to the victim at
    /// <paramref name="victim"/> facing <paramref name="yaw"/>, in four 90° sectors centred on front, right, back and left
    /// (boundaries go to front and back); with two clips only, front or back by the half the source is in (ours). A source on
    /// the victim counts as in front.
    /// </summary>
    public static ReactionDirection DirectionOf(Vector3 victim, float yaw, Vector3 from, bool fourWay)
    {
        var to = new Vector2(from.X - victim.X, from.Y - victim.Y);
        if (to.LengthSquared() < 1e-10f || !float.IsFinite(to.X) || !float.IsFinite(to.Y)) return ReactionDirection.Front;
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var right = new Vector2(forward.Y, -forward.X);
        float f = Vector2.Dot(to, forward), r = Vector2.Dot(to, right);
        if (!fourWay || MathF.Abs(f) >= MathF.Abs(r)) return f >= 0f ? ReactionDirection.Front : ReactionDirection.Back;
        return r > 0f ? ReactionDirection.Right : ReactionDirection.Left;
    }

    /// <summary>A horizontal direction as a yaw byte (2π/256 steps, yaw = atan2(−x, y)); 0 for no direction.</summary>
    public static int YawByte(Vector2 direction)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() < 1e-12f) return 0;
        return (int)MathF.Round(MathF.Atan2(-direction.X, direction.Y) / (2f * MathF.PI) * 256f) & 255;
    }

    /// <summary>
    /// Whether a reaction may start on <paramref name="s"/> now (rules **interpretation**, spec/behavior.md): alive; not in the
    /// evade's hyper armour (<see cref="Evade.HyperArmour"/>, user's knowledge of the original) nor the charge's (M32,
    /// <see cref="Rush.HyperArmour"/>, user's knowledge of the original); not in jump-pack flight (take-off
    /// to dive: the reaction graph has no airborne clips); not in a reaction before its <see cref="ReactionClip.ReactAt"/>; not in
    /// a melee attack's window without <c>BRANCH_HitReact</c> (<see cref="MeleeStep.NoReactFrom"/> to <see cref="MeleeStep.NoReactTo"/>).
    /// </summary>
    public static bool CanReact(PlayerState s, SimConfig c)
    {
        if (!s.Alive || Evade.HyperArmour(s, c) || Rush.HyperArmour(s, c)) return false;
        if (Brace.ReactionImmune(s)) return false;   // fidelity gap 7: the heavy bolter's stance (owner's recollection: stuns and hits do not knock you out)
        if (Stims.ReactionImmune(c, s)) return false;   // perks/inert-2: combat stims (mp_reaction_immunity while they work)
        if (s.Jump is JumpPhase.Ascent or JumpPhase.Hang or JumpPhase.Fall or JumpPhase.Dive) return false;
        if (ClipOf(s, c) is { } clip && Time(s, c) + Eps < clip.ReactAt) return false;
        if (s.MeleeStep > 0 && Combat.MeleeStepOf(c, s) is { } m && m.NoReactTo > m.NoReactFrom
            && s.MeleeTime > m.NoReactFrom + Eps && s.MeleeTime < m.NoReactTo - Eps) return false;
        return true;
    }

    /// <summary>
    /// <paramref name="s"/> put into a reaction (the caller checked <see cref="CanReact"/>): whatever it was doing stops (a melee
    /// attack and its buffered press, a reload, a charge and a pending charged shot, a throw — before its release nothing is spent
    /// —, an evade (its spacing lock stays), a jump-pack take-off or landing (the power spent stays spent), aim mode); its
    /// horizontal velocity stops. The clip is <paramref name="direction"/>'s; the knockback goes along <paramref name="push"/>
    /// (horizontal part; a yaw byte) for the entry's Backward metres (1/16 m steps, at most <see cref="MaxPushDistance"/>) over its
    /// Duration rounded up to whole ticks (at least one). The entry's Right and Up distances are not applied (0 in every
    /// multiplayer entry).
    /// </summary>
    public static PlayerState Start(PlayerState s, SimConfig c, ReactionEntry entry, ReactionDirection direction, Vector2 push)
    {
        int distance = float.IsFinite(entry.Backward) && entry.Backward > 0f ? (int)MathF.Round(MathF.Min(entry.Backward, MaxPushDistance) * 16f) : 0;
        int pushYaw = distance > 0 ? YawByte(push) : 0;
        if (distance > 0 && push.LengthSquared() < 1e-12f) distance = 0;   // no direction, no knockback
        int ticks = 0;
        if (distance > 0)
        {
            float duration = float.IsFinite(entry.Duration) && entry.Duration > 0f ? entry.Duration : 0f;
            ticks = Math.Clamp((int)MathF.Ceiling(duration / c.TickSeconds - 1e-3f), 1, MaxTicks);
        }
        bool keepJump = s.Jump == JumpPhase.None;
        return s with
        {
            Reaction = entry.Kind, ReactionDirection = direction, ReactionTicks = 0,
            PushYaw = distance > 0 ? pushYaw : 0, PushDistance = distance, PushTicks = ticks,
            MeleeStep = 0, MeleeTime = 0f, MeleeQueued = false, MeleeHeavy = false, MeleeQueuedHeavy = false, MeleeDirection = EvadeDirection.Forward,
            ReloadLeft = 0f, Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f, Throwing = false, ThrowTime = 0f,
            EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLanded = false, JumpDashDirection = keepJump ? s.JumpDashDirection : EvadeDirection.Forward, Aiming = false, Rush = RushPhase.None, RushTicks = 0,
            Jump = keepJump ? s.Jump : JumpPhase.None, JumpTicks = keepJump ? s.JumpTicks : 0, JumpMark = keepJump ? s.JumpMark : 0,
            JumpHolding = false, JumpLandMoving = false, JumpCut = false, JumpSmall = false, JumpSlash = false,
            Velocity = new Vector3(0f, 0f, s.Velocity.Z), SprintStop = false,
        };
    }

    /// <summary>Blindness from a blast's <c>do_blindness</c>: the longer of the current and the new duration, in ticks (at most 255).</summary>
    public static PlayerState Blind(PlayerState s, SimConfig c, float seconds)
    {
        if (!s.Alive || !float.IsFinite(seconds) || seconds <= 0f) return s;
        int ticks = Math.Clamp((int)MathF.Round(seconds / c.TickSeconds), 1, MaxTicks);
        return ticks > s.BlindTicks ? s with { BlindTicks = ticks } : s;
    }

    /// <summary>
    /// The install's reaction clips as placeholders for tests without an install (values as read on buildid 7412325; the
    /// runtime reads them from the graph): light interrupt <c>SM_REC_Light_Itrpt_FHL_01</c> / <c>_BHR_01</c>, stun
    /// <c>SM_MP_REC_Grenade_F/R/B/L_01</c>, stagger <c>SM_REC_Stagger_F/B_01</c>.
    /// </summary>
    public static readonly IReadOnlyList<ReactionSpec> Placeholders = Array.AsReadOnly(new[]
    {
        new ReactionSpec(ReactionKind.Interrupt, false,
            Clip("SM_REC_Light_Itrpt_FHL_01", 1.16667f, 0.84f, 0.84f, 0.4f, 0.68f, 0.32f),
            Clip("SM_REC_Light_Itrpt_FHL_01", 1.16667f, 0.84f, 0.84f, 0.4f, 0.68f, 0.32f),
            Clip("SM_REC_Light_Itrpt_BHR_01", 1f, 0.92f, 0.92f, 0.2f, 0.72f, 0.4f),
            Clip("SM_REC_Light_Itrpt_BHR_01", 1f, 0.92f, 0.92f, 0.2f, 0.72f, 0.4f)),
        new ReactionSpec(ReactionKind.Stun, true,
            Clip("SM_MP_REC_Grenade_F_01", 1f, 0.5333f, 1f, 0.3333f, 1f, 1f),
            Clip("SM_MP_REC_Grenade_R_01", 1f, 0.8f, 1f, 0.3333f, 1f, 1f),
            Clip("SM_MP_REC_Grenade_B_01", 1f, 0.6667f, 1f, 0.3333f, 1f, 1f),
            Clip("SM_MP_REC_Grenade_L_01", 1f, 0.8f, 1f, 0.3333f, 1f, 1f)),
        new ReactionSpec(ReactionKind.Stagger, false,
            Clip("SM_REC_Stagger_F_01", 1.43333f, 0.3953f, 1f, 1f, 1f, 1f),
            Clip("SM_REC_Stagger_F_01", 1.43333f, 0.3953f, 1f, 1f, 1f, 1f),
            Clip("SM_REC_Stagger_B_01", 1.43333f, 0.7442f, 1f, 1f, 1f, 1f),
            Clip("SM_REC_Stagger_B_01", 1.43333f, 0.7442f, 1f, 1f, 1f, 1f)),
    });

    /// <summary>A clip from tag fractions; a branch at or after the end tag (1 = absent) falls back to the end.</summary>
    static ReactionClip Clip(string animation, float duration, float end, float run, float evade, float melee, float react)
    {
        float endAt = end * duration;
        float At(float f) => MathF.Min(f * duration, endAt);
        return new ReactionClip(animation, duration, endAt, At(run), At(evade), At(melee), At(react));
    }
}
