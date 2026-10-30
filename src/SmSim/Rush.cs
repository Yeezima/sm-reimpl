using System.Numerics;

namespace SmSim;

/// <summary>Where a player is in a charge (M29; <see cref="PlayerState.Rush"/>).</summary>
public enum RushPhase : byte
{
    None = 0,
    /// <summary>The rush: the bull-rush attack clip's motion along the body's facing until it connects, meets a wall or ends.</summary>
    Rush = 1,
    /// <summary>The stop after connecting with a player or a wall: the collision clip.</summary>
    Collision = 2,
    /// <summary>Protocol 30: the dash attack's lunge (<see cref="DashAttackSpec"/>; <c>SM_JMP_DashAtk_F</c>) until it connects, meets a wall or reaches its <c>BRANCH_toIdle</c>.</summary>
    DashAttack = 3,
    /// <summary>Protocol 30: the dash attack's hit state after connecting with a player (<see cref="DashAttackSpec.Hit"/>).</summary>
    DashHit = 4,
    /// <summary>Protocol 30: the run-out after the lunge (<see cref="DashAttackSpec.Stop"/>, "Dash to Idle").</summary>
    DashStop = 5,
    /// <summary>Protocol 30: the knocked-back stop after the lunge met a wall (the dash attack's <see cref="RushSpec.Collision"/>).</summary>
    DashCollision = 6,
}

/// <summary>
/// One clip of the charge (M29): its extracted motion and branch times (seconds from the phase's start), read by
/// <c>SmContent.MeleeTuning</c> from the marine rig's sprint graph <c>MP_SM_Bolter_NuSprint</c>.
/// </summary>
/// <param name="Animation">The clip (rush: <c>SM_BUL_Hit_M_5m</c>; stop: <c>SM_NAV_Bullrush2Idle_F_Collision_01</c>).</param>
/// <param name="Path">Extracted motion: displacement in the character's frame (X right, Y forward), evenly over <paramref name="ClipLength"/>.</param>
/// <param name="ClipLength">The clip's length (the path's time span).</param>
/// <param name="End">The phase ends (<c>BRANCH_Sprint_Finished</c>).</param>
/// <param name="MoveAt">From here the stick, Fire, Aim or a Grenade press end it (rush: <c>BRANCH_Attack2Idle</c>; End without one).</param>
/// <param name="EvadeAt">From here a Jump press ends it and evades or jumps (<c>BRANCH_Evade</c> / <c>BRANCH_Sprint_Evade</c>).</param>
/// <param name="MeleeAt">From here a melee or heavy press ends it and attacks (<c>BRANCH_To_Melee</c>; End without one).</param>
public sealed record RushMove(string Animation, IReadOnlyList<Vector2> Path, float ClipLength, float End, float MoveAt, float EvadeAt, float MeleeAt)
{
    /// <summary>The displacement after <paramref name="time"/> seconds (linear between samples, clamped to the clip).</summary>
    public Vector2 At(float time)
    {
        int n = Path.Count;
        if (n == 0) return Vector2.Zero;
        if (n == 1 || !(ClipLength > 0f)) return Path[^1];
        float u = Math.Clamp(time / ClipLength, 0f, 1f) * (n - 1);
        int i = Math.Min((int)u, n - 2);
        return Vector2.Lerp(Path[i], Path[i + 1], u - i);
    }

    internal void Write(BinaryWriter w)
    {
        w.Write(Animation);
        w.Write(Path.Count); foreach (var p in Path) { w.Write(p.X); w.Write(p.Y); }
        foreach (float f in new[] { ClipLength, End, MoveAt, EvadeAt, MeleeAt }) w.Write(f);
    }
}

/// <summary>
/// A melee weapon's charge (M29; the bull rush): its ability's hits (wargear <c>ability_bullrush_01</c>, default entries) and
/// the two clips. Rules in <see cref="Charge"/>.
/// </summary>
/// <param name="Primary">The connected player's damage (<c>primary_target_actions</c> <c>do_damage</c>: damage and the shield part).</param>
/// <param name="PrimaryReaction">… and reaction (<c>reaction_mp_stun</c>).</param>
/// <param name="Secondary">Every other target in reach in front at the contact.</param>
/// <param name="Range"><c>ability_metadata/generic_metadata/max_range</c> (4): the secondary targets' reach.</param>
/// <param name="MinInterval"><c>min_time_between_activations</c> (1 s): from one charge's start to the next.</param>
public sealed record RushSpec(string Ability, RushMove Rush, RushMove Collision, DamageSpec Primary, ReactionEntry PrimaryReaction,
    DamageSpec Secondary, ReactionEntry SecondaryReaction, float Range, float MinInterval)
{
    /// <summary>Contacts count until here in the rush (seconds; its <c>BRANCH_Attack2Idle</c>, interpretation).</summary>
    public float ContactUntil { get; init; } = 0.667f;
    /// <summary>A wall ends the rush from here (seconds; the clip's <c>WallCollisionTest</c> tag start); before, walls only block.</summary>
    public float WallFrom { get; init; } = 0.333f;
    /// <summary>A wall within this angle of head-on (degrees; the sprint's <c>max_horz_angle_for_wall_hit</c>, 25) ends the rush.</summary>
    public float WallHitAngle { get; init; } = 25f;
    /// <summary>
    /// Ours: the stick's forward part that counts as W held (0.7 of full; a keyboard's W, alone or with A or D, gives 1). feel/charge-directions
    /// (protocol 39, the owner's rule of 2026-10-29): W held at the F press starts the charge (<see cref="Rush.CanStart"/>); no sprint is needed.
    /// </summary>
    public float MinForwardStick { get; init; } = DefaultMinForwardStick;
    /// <summary>The install-independent default of <see cref="MinForwardStick"/> (the controller rule reads it too).</summary>
    public const float DefaultMinForwardStick = 0.7f;
    /// <summary>
    /// feel/charge-directions (protocol 39; the owner's rule of 2026-10-29: "you should be able to hold A or D while holding W and F to charge a bit to
    /// the left or right"): the most the stick turns the charge's heading from the view at its start (degrees; the angle ours, 25). The share is the
    /// stick's angle from straight ahead over 45 degrees (W + A or W + D on a keyboard: all of it). Replaces M31's sprint margin. A charge that locks
    /// onto a target starts along the view and steers to it (<see cref="MeleeLock.Steer"/>), as before.
    /// </summary>
    public float SideSteer { get; init; } = 25f;
    /// <summary>Ours: a player counts as connected when the rush passes within the two capsule radii plus this (metres).</summary>
    public float ContactMargin { get; init; } = 0.3f;
    /// <summary>Ours: … and lies within this half-angle of the rush's heading (degrees).</summary>
    public float ContactHalfAngle { get; init; } = 45f;
    /// <summary>
    /// M32: the charger's hyper armour lasts while the rush has played at most this long (seconds; <see cref="Rush.HyperArmour"/>).
    /// The owner (user's knowledge of the original, 2026-10-28, approximate): "hyper armour during the initial phase of the charge;
    /// the hyper armour that blocks stuns wears off roughly halfway through the charge". The graph has no window for it (the rush
    /// clip's only entity property, <c>CollisionMode</c>, covers the whole clip), so the reader takes half of the rush's attack part,
    /// up to its <c>BRANCH_Attack2Idle</c> (<see cref="RushMove.MoveAt"/>; install 0.667 s, so 0.333 s; ours).
    /// </summary>
    public float HyperArmourUntil { get; init; } = 0.3334f;   // just over 10 ticks at 30 Hz, as the install value (0.33334)
    /// <summary>
    /// feel/melee-charge-2 (protocol 42; the owner's request of 2026-10-29: "landing after ledge charging there is always an additional shoulder bash ... you
    /// should in a blended fluid way transition to a running or standing animation"): a bull rush held in the air (<see cref="Rush.Held"/>) ends when it lands
    /// after a real fall: its vertical speed at the start of the landing tick at least that of a free fall of this many metres (√(2 g h); ours, 0.5 m). A
    /// shallower drop (a curb, a step the controller does not step down) keeps the M29 rule: the clip resumes. The dash attack held at its end ends on any landing.
    /// </summary>
    public float LandEndDrop { get; init; } = DefaultLandEndDrop;
    /// <summary>The default of <see cref="LandEndDrop"/> (metres; ours).</summary>
    public const float DefaultLandEndDrop = 0.5f;

    internal void Write(BinaryWriter w)
    {
        w.Write(Ability); Rush.Write(w); Collision.Write(w);
        DamageSpec.Write(w, Primary); PrimaryReaction.Write(w); DamageSpec.Write(w, Secondary); SecondaryReaction.Write(w);
        foreach (float f in new[] { Range, MinInterval, ContactUntil, WallFrom, WallHitAngle, MinForwardStick, SideSteer, ContactMargin, ContactHalfAngle, HyperArmourUntil }) w.Write(f);
        if (LandEndDrop != DefaultLandEndDrop) { w.Write((byte)0x4C); w.Write(LandEndDrop); }   // feel/melee-charge-2: only when not the default
    }

    internal static void Write(BinaryWriter w, RushSpec? c)
    {
        w.Write(c is not null);
        c?.Write(w);
    }
}

/// <summary>
/// The charge (M29; rules **interpretation**, spec/behavior.md "Charge"). The owner's description of the original (user's
/// knowledge, 2026-10-28): sprinting forward with W and pressing F is a straight-line rush that stuns and damages an enemy it
/// connects with; an ally only stops it; hyper armour negates the stun; the jump-pack classes charge too, on the ground only; the charger has
/// hyper armour in the charge's first part (M32). M31's sprint rule is superseded (feel/charge-directions, protocol 39, the owner's rule of
/// 2026-10-29): "charge shouldn't require you to actually sprint to initiate charge, just be holding w when you press f"; without W, F is the stun
/// strike. The data: the melee weapons' <c>ability_bullrush_01</c>
/// (75 + 25 shield, <c>reaction_mp_stun</c>; others in reach 30 and an interrupt; 1 s between uses) and the sprint graph's
/// rush clip (<c>SM_BUL_Hit_M_5m</c>, 14.9 m of extracted motion) and its collision stop.
/// <para>
/// Start (protocol 39): a <see cref="InputButtons.MeleeHeavy"/> press edge with W held (the stick's forward part at least
/// <see cref="RushSpec.MinForwardStick"/>; standing, walking, running or sprinting alike, aim mode and the trigger too), on the ground, with no melee attack, evade, jump-pack phase (take-off, flight, dive, landing, recovery), throw or reaction under
/// way and the spacing over. The press then is the charge, not a heavy strike. It cancels a reload,
/// a charge of the weapon and a pending charged shot, and aim mode. The body faces the view's heading at the press turned by the stick's sideways
/// part (<see cref="StartYaw"/>: A or D with W, up to <see cref="RushSpec.SideSteer"/>) and keeps it; a locked charge starts along the view and
/// steers to its target. The dash attack's lunge goes the stick's way of eight (<see cref="DashYaw"/>).
/// </para>
/// <para>
/// During it the player only turns the view: other input is ignored (presses lost) until the phase's branch times; the body
/// follows the clip's extracted motion through the capsule controller. The world ends the rush on contact (see
/// <c>World.ResolveRush</c>); a wall within <see cref="RushSpec.WallHitAngle"/> of head-on ends it from
/// <see cref="RushSpec.WallFrom"/>; both start the collision stop. A tick checks the time played before it (as evades do).
/// </para>
/// <para>
/// Hyper armour (M32; <see cref="HyperArmour"/>; the owner's description, approximate): the rush's first
/// <see cref="RushSpec.HyperArmourUntil"/> negates hit reactions (stun, stagger, interrupt, knockback) exactly as the evade's does:
/// <see cref="Reaction.CanReact"/> checks it, the damage still applies, and the charge runs on. Later in the rush, and in the
/// collision stop, a reaction ends the charge (<see cref="Reaction.Start"/>).
/// </para>
/// </summary>
public static class Rush
{
    const float Eps = 1e-5f;
    /// <summary>Largest <see cref="PlayerState.RushTicks"/> and <see cref="PlayerState.RushLock"/> (one byte on the wire).</summary>
    public const int MaxTicks = byte.MaxValue;

    /// <summary>The clip of a phase, or null.</summary>
    public static RushMove? MoveOf(RushSpec? spec, RushPhase phase) => spec is null ? null : phase switch
    {
        RushPhase.Rush => spec.Rush,
        RushPhase.Collision => spec.Collision,
        _ => null,
    };

    /// <summary>Protocol 30: the dash attack's phases (<see cref="RushPhase.DashAttack"/> to <see cref="RushPhase.DashCollision"/>).</summary>
    public static bool IsDash(RushPhase phase) => phase is RushPhase.DashAttack or RushPhase.DashHit or RushPhase.DashStop or RushPhase.DashCollision;

    /// <summary>
    /// BR-02 (owner's recollection, 2026-10-30): a Tactical (SM or CSM: the class role "tactical") cannot fire from the charge's start to its end, and Fire does
    /// not end the charge. Every other class keeps its behaviour (Fire ends their charge from its move-on time and the weapon fires that tick).
    /// </summary>
    public static bool NoFire(SimConfig c, PlayerState s) => Loadouts.ClassOf(c, s.Class) is { } l && l.Role.Equals("tactical", StringComparison.OrdinalIgnoreCase);

    /// <summary>The charging phases: the clip moves the body along its facing, it connects with players and walls stop it (the rush, the dash attack's lunge).</summary>
    public static bool Charging(RushPhase phase) => phase is RushPhase.Rush or RushPhase.DashAttack;

    /// <summary>The charge spec of the player's phase: the dash attack's (<see cref="DashAttackSpec.Charge"/>, the jump pack's) or the melee weapon's bull rush, or null.</summary>
    public static RushSpec? SpecOf(SimConfig c, PlayerState s) => IsDash(s.Rush) ? Loadouts.JumpPack(c, s)?.DashAttack?.Charge : Loadouts.Rush(c, s);

    /// <summary>The clip of the player's phase (the bull rush's or the dash attack's), or null.</summary>
    public static RushMove? MoveOf(SimConfig c, PlayerState s)
    {
        if (!IsDash(s.Rush)) return MoveOf(Loadouts.Rush(c, s), s.Rush);
        if (Loadouts.JumpPack(c, s)?.DashAttack is not { } dash) return null;
        return s.Rush switch
        {
            RushPhase.DashAttack => dash.Charge.Rush,
            RushPhase.DashHit => dash.Hit,
            RushPhase.DashStop => dash.Stop,
            RushPhase.DashCollision => dash.Charge.Collision,
            _ => null,
        };
    }

    /// <summary>Seconds of the phase's clip played after the player's last tick.</summary>
    public static float Time(PlayerState s, SimConfig c) => Math.Max(0, s.RushTicks) * c.TickSeconds;

    /// <summary>The state with no charge (the spacing lock stays).</summary>
    public static PlayerState Cleared(PlayerState s) => s with { Rush = RushPhase.None, RushTicks = 0 };

    /// <summary>
    /// The state put into the stop (the world on contact; the tick on a wall): the bull rush's collision stop either way; the dash attack's hit
    /// state on a player (<c>BOOL_Dash_Hit</c>), its knocked-back stop on a <paramref name="wall"/> (<c>BOOL_Dash_Wall_Hit</c>). Horizontal velocity stops.
    /// </summary>
    public static PlayerState Stopped(PlayerState s, bool wall = false) => s with
    {
        Rush = s.Rush == RushPhase.DashAttack ? (wall ? RushPhase.DashCollision : RushPhase.DashHit) : RushPhase.Collision,
        RushTicks = 0, Velocity = new Vector3(0f, 0f, s.Velocity.Z),
    };

    /// <summary>
    /// Whether the player is in its charge's hyper armour after its last tick (M32): in the rush phase with at most
    /// <see cref="RushSpec.HyperArmourUntil"/> played (the press tick included, as the evade's <see cref="Evade.HyperArmour"/>).
    /// Not in the collision stop.
    /// </summary>
    public static bool HyperArmour(PlayerState s, SimConfig c) =>
        Charging(s.Rush) && s.RushTicks > 0 && SpecOf(c, s) is { } spec && Time(s, c) <= spec.HyperArmourUntil + Eps;

    /// <summary>
    /// feel/jump-pack-2 (the review): the charging state the player moved in this tick when that move ended in a wall stop (<see cref="Move"/> put
    /// <paramref name="next"/> into the bull rush's collision stop or the dash attack's wall stop with 0 ticks), else null. The world tests this tick's
    /// contacts with it, so an enemy between the charger and the wall is still reached; a hit wins over the wall (<c>World.StopRushes</c>).
    /// </summary>
    public static PlayerState? WallStoppedFrom(PlayerState before, PlayerState next, SimConfig c)
    {
        if (next.RushTicks != 0 || next.Rush is not (RushPhase.Collision or RushPhase.DashCollision)) return null;
        var phase = next.Rush == RushPhase.DashCollision ? RushPhase.DashAttack : RushPhase.Rush;
        int ticks = before.Rush != phase ? 1 : MoveOf(c, before) is { } move && Held(before, move, c) ? before.RushTicks : Math.Min(before.RushTicks + 1, MaxTicks);
        return next with { Rush = phase, RushTicks = ticks };
    }

    /// <summary>
    /// feel/melee-charge-2 (protocol 42): the charging state a charger moved in this tick when that move landed it and the landing ended its charge
    /// (<see cref="Move"/>: <see cref="EndsOnLanding"/>), else null. The world tests this tick's contacts with it (the airborne part's contacts still work);
    /// a contact then puts the charger into the stop as any other (<c>World.StopRushes</c>).
    /// </summary>
    public static PlayerState? LandedFrom(PlayerState before, PlayerState next)
    {
        if (!Charging(before.Rush) || before.Grounded || !next.Alive || !next.Grounded || next.Rush != RushPhase.None || next.Reaction != ReactionKind.None) return null;
        return next with { Rush = before.Rush, RushTicks = before.RushTicks };
    }

    /// <summary>
    /// feel/melee-charge-2 (protocol 42; the owner's request): whether a charging phase held in the air (<see cref="Held"/>) that lands this tick ends there:
    /// the dash attack (held only at its end) always; the bull rush after a real fall, its vertical speed at the tick's start (<paramref name="s"/>) at least
    /// that of a free fall of <see cref="RushSpec.LandEndDrop"/> (ours).
    /// </summary>
    public static bool EndsOnLanding(PlayerState s, RushSpec? spec, SimConfig c)
    {
        if (s.Rush == RushPhase.DashAttack) return true;
        if (s.Rush != RushPhase.Rush) return false;
        float drop = spec is not null && float.IsFinite(spec.LandEndDrop) ? MathF.Max(0f, spec.LandEndDrop) : RushSpec.DefaultLandEndDrop;
        return -s.Velocity.Z + Eps >= MathF.Sqrt(2f * MathF.Max(0f, c.Gravity) * drop);
    }

    /// <summary>
    /// feel/melee-charge-2: the state after a landing that ended the charge (<see cref="EndsOnLanding"/>): no charge (the spacing lock stays), normal movement
    /// from the next tick (a movement key: the run; none: the stand, through the sprint stop's slowdown), the horizontal speed kept for that slowdown but at
    /// most <see cref="SimConfig.SprintSpeed"/> (ours: a charge held at its clip's 30 to 40 m/s burst would otherwise slide for metres).
    /// </summary>
    public static PlayerState Landed(PlayerState s, SimConfig c)
    {
        var flat = new Vector2(s.Velocity.X, s.Velocity.Y);
        float speed = flat.Length(), cap = float.IsFinite(c.SprintSpeed) ? MathF.Max(0f, c.SprintSpeed) : 0f;
        if (speed > cap && speed > 1e-6f) flat *= cap / speed;
        return Cleared(s) with { Velocity = new Vector3(flat.X, flat.Y, s.Velocity.Z) };
    }

    /// <summary>The phase a charger stopped by a wall this tick was charging in (<see cref="WallStoppedFrom"/>), else its own phase.</summary>
    public static RushPhase ChargingPhase(PlayerState s) =>
        s.RushTicks == 0 && s.Rush == RushPhase.DashCollision ? RushPhase.DashAttack : s.RushTicks == 0 && s.Rush == RushPhase.Collision ? RushPhase.Rush : s.Rush;

    /// <summary>Whether the rush (or the dash attack's lunge) still connects with players after its last tick.</summary>
    public static bool Connecting(PlayerState s, SimConfig c) =>
        Charging(s.Rush) && SpecOf(c, s) is { } spec && (s.RushTicks - 1) * c.TickSeconds < spec.ContactUntil - Eps;

    /// <summary>
    /// The start of a tick (<see cref="Begin"/>): whether the tick is the charge's, whether it started one, whether its body is held in the air
    /// (<see cref="Held"/>: the clip waits, the horizontal velocity is kept) and the state.
    /// </summary>
    internal readonly record struct Tick(bool Active, bool Started, bool Held, PlayerState State);

    /// <summary>
    /// The start of a tick: a charge under way runs on (ticks counted) or ends (by time or by input from its branch times; the
    /// tick then acts normally), or a new one starts: the bull rush on a heavy press with W held (<see cref="CanStart"/>), the dash attack
    /// on a melee press in the dash's window (<see cref="CanDashAttack"/>). Protocol 30: the dash attack's lunge that reaches its end goes on into
    /// its run-out (<see cref="RushPhase.DashStop"/>); a charging phase that starts the tick in the air is not ended by time or input (the owner's
    /// rule: the charge keeps its momentum until it lands; <see cref="Held"/>). Returns whether the tick is the charge's.
    /// </summary>
    internal static Tick Begin(PlayerState s, PlayerInput input, SimConfig c, bool heavyPress, bool jumpPress, bool meleePress, bool otherInput,
        CollisionWorld? world = null)
    {
        int lockTicks = s.RushLock;   // counted down by the caller every living tick (reacting too)
        if (s.Rush != RushPhase.None)
        {
            if (MoveOf(c, s) is not { } move) return new(false, false, false, Cleared(s));
            float played = Time(s, c);
            bool At(float t) => played + Eps >= t;
            if (Held(s, move, c)) return new(true, false, true, s);   // in the air: the clip waits for the ground (no end by time or input)
            if (Charging(s.Rush) && !s.Grounded) return new(true, false, false, s with { RushTicks = Math.Min(s.RushTicks + 1, MaxTicks) });   // a lunge begun in the air runs on
            // feel/locks-sprint-stop: the bull rush's and its collision stop's Jump and stick branches × the lock scale. feel/jump-pack-2 (the owner's request,
            // an estimate): the dash attack's states after the lunge (hit, wall stop, run-out) too, their melee branch included; the lunge (no input ends it) not.
            bool scaled = s.Rush is RushPhase.Rush or RushPhase.Collision or RushPhase.DashHit or RushPhase.DashCollision or RushPhase.DashStop;
            bool dashAfter = s.Rush is RushPhase.DashHit or RushPhase.DashCollision or RushPhase.DashStop;
            float evadeAt = scaled ? c.LockTime(move.EvadeAt) : move.EvadeAt, moveAt = scaled ? c.LockTime(move.MoveAt) : move.MoveAt;
            float meleeAt = dashAfter ? c.LockTime(move.MeleeAt) : move.MeleeAt;
            bool over = At(move.End) || (jumpPress && At(evadeAt)) || ((meleePress || heavyPress) && At(meleeAt)) || (otherInput && At(moveAt));
            if (!over) return new(true, false, false, s with { RushTicks = Math.Min(s.RushTicks + 1, MaxTicks) });
            // The dash attack's lunge ends into its run-out ("Dash to Idle" on BRANCH_toIdle), which plays from this tick.
            if (s.Rush == RushPhase.DashAttack && At(move.End) && Loadouts.JumpPack(c, s)?.DashAttack is not null)
                return new(true, false, false, s with { Rush = RushPhase.DashStop, RushTicks = 1 });
            return new(false, false, false, Cleared(s));
        }
        if (meleePress && lockTicks == 0 && CanDashAttack(s, c, world) && Loadouts.JumpPack(c, s)?.DashAttack is { } dash)
        {
            int spacing = Math.Clamp((int)MathF.Ceiling(dash.Charge.MinInterval / c.TickSeconds - 1e-3f), 0, MaxTicks);
            return new(true, true, false, s with
            {
                Rush = RushPhase.DashAttack, RushTicks = 1, RushLock = spacing, Yaw = DashYaw(input),   // feel/charge-directions: the stick's way of eight
                EvadeTicks = 0, EvadeDirection = EvadeDirection.Forward, EvadeLanded = false,   // the dash ends (its spacing lock stays)
                ReloadLeft = 0f, Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f, Aiming = false,
            });
        }
        var spec = Loadouts.Rush(c, s);
        if (spec is null || !heavyPress || lockTicks > 0 || !CanStart(s, input, spec, c)) return new(false, false, false, s);
        int interval = Math.Clamp((int)MathF.Ceiling(spec.MinInterval / c.TickSeconds - 1e-3f), 0, MaxTicks);
        var yaw = StartYaw(input, spec);   // feel/charge-directions: W + A / W + D turn it (a locked charge is put back on the view by Combat)
        return new(true, true, false, s with
        {
            Rush = RushPhase.Rush, RushTicks = 1, RushLock = interval, Yaw = yaw,
            ReloadLeft = 0f, Charging = false, ChargeTime = 0f, PendingShot = 0, PendingLeft = 0f, Aiming = false,
        });
    }

    /// <summary>
    /// Protocol 30, the owner's rule (2026-10-28: "the player should maintain their charge acceleration/momentum forward until they hit the
    /// ground"): whether a charging phase's body is held in the air this tick: the bull rush whenever the tick starts in the air (it left the
    /// ground: a ledge), the dash attack's lunge (which may begin in the air) once its clip has reached its end in the air. Held: the clip
    /// time waits, the horizontal velocity of the last tick is kept, gravity pulls, contacts and wall stops go on, nothing ends it but a
    /// landing, a reaction or death. feel/melee-charge-2 (protocol 42, the owner's request of 2026-10-29): the landing after a real fall ends the charge
    /// (<see cref="EndsOnLanding"/>, <see cref="Landed"/>: no resumed attack, no second contact window; the dash attack skips its run-out); after a shallow
    /// one the bull rush's clip resumes at its time (M29's rule, ours).
    /// </summary>
    /// <remarks>
    /// The held speed is exactly the horizontal speed of the tick it left the ground on, uncapped (the owner's decision, 2026-10-29: "no a ledge charge
    /// should not be capped"), even in the clip's opening burst (the install's bull rush moves 28 to 41 m/s for ticks 2 to 5). Because the clip time
    /// waits, the time-based windows wait too: the charger's hyper armour (<see cref="HyperArmour"/>) and the contact window
    /// (<see cref="Connecting"/>) stay as they were when it left the ground until it lands (ours).
    /// </remarks>
    public static bool Held(PlayerState s, RushMove move, SimConfig c) =>
        !s.Grounded && (s.Rush == RushPhase.Rush || (s.Rush == RushPhase.DashAttack && Time(s, c) + Eps >= move.End));

    /// <summary>
    /// Protocol 30: whether a melee press this tick starts the dash attack (the "Jumppack Evade" state's <c>BTN_Melee_Attack</c> transition):
    /// alive, a jump-pack dash in its flight (not its landing part) with at least the direction's <see cref="EvadeMove.DashAttackAt"/>
    /// (<c>BRANCH_Dash_Attack</c>) played, the feet at most <see cref="DashAttackSpec.MaxHeight"/> above the ground of <paramref name="world"/>, no
    /// reaction or charge under way. The spacing lock is the caller's (<see cref="PlayerState.RushLock"/>, shared with the bull rush; ours).
    /// </summary>
    public static bool CanDashAttack(PlayerState s, SimConfig c, CollisionWorld? world)
    {
        if (!s.Alive || s.Reaction != ReactionKind.None || s.Rush != RushPhase.None || s.EvadeTicks <= 0 || s.EvadeLanded) return false;
        if (Loadouts.JumpPack(c, s)?.DashAttack is not { } dash || Loadouts.Evade(c, s) is not { Dash: true } evade) return false;
        if (!(Evade.Time(s, c) + Eps >= evade.Move(s.EvadeDirection).DashAttackAt)) return false;
        return JumpPack.AboveGround(s, world) <= dash.MaxHeight + Eps;
    }

    /// <summary>
    /// The start gate (feel/charge-directions, protocol 39; the owner's rule of 2026-10-29, superseding M31's sprint gate): alive, on the ground and
    /// not in any jump-pack phase (so the Assault and Raptor charge only from the ground, M31), nothing else under way, and W held: the stick's
    /// forward part at least <see cref="RushSpec.MinForwardStick"/> (A or D may be held with it: <see cref="StartYaw"/>). No sprint and no speed
    /// are needed; aim mode and the held trigger do not stop it (the charge cancels both).
    /// </summary>
    public static bool CanStart(PlayerState s, PlayerInput input, RushSpec spec, SimConfig c)
    {
        if (!s.Alive || !s.Grounded || s.MeleeStep > 0 || s.EvadeTicks > 0 || s.Jump != JumpPhase.None || s.Throwing || s.Reaction != ReactionKind.None) return false;
        return WHeld(input, spec);
    }

    /// <summary>Whether the input holds W for the charge: the stick's forward part at least <see cref="RushSpec.MinForwardStick"/>.</summary>
    public static bool WHeld(PlayerInput input, RushSpec spec)
    {
        input = input.Sanitized();
        return input.MoveY >= spec.MinForwardStick - Eps;
    }

    /// <summary>
    /// The charge's heading at its start (feel/charge-directions; the owner's rule, the angle ours): the view's yaw turned toward the stick's side by
    /// <see cref="RushSpec.SideSteer"/> × (the stick's angle from straight ahead / 45 degrees, at most 1): W alone straight along the view, W + A
    /// 25 degrees left, W + D 25 degrees right (keyboard). Not wrapped (as the evade's yaw).
    /// </summary>
    public static float StartYaw(PlayerInput input, RushSpec spec)
    {
        input = input.Sanitized();
        float steer = float.IsFinite(spec.SideSteer) ? Math.Clamp(spec.SideSteer, 0f, 90f) * (MathF.PI / 180f) : 0f;
        if (steer <= 0f || input.MoveY <= 0f) return input.Yaw;
        // The share grows continuously from 0 at the dead zone's edge (no step there): the sideways part beyond the dead zone, as an angle against the forward
        // part, over 45 degrees, at most 1 (a keyboard's W + A / W + D: 1).
        float side = MathF.Sign(input.MoveX) * MathF.Max(0f, MathF.Abs(input.MoveX) - Evade.DeadZone) / (1f - Evade.DeadZone);
        float share = Math.Clamp(MathF.Atan2(side, input.MoveY) / (MathF.PI / 4f), -1f, 1f);
        return input.Yaw - steer * share;   // yaw grows to the left: the stick's right turns it down
    }

    /// <summary>
    /// The dash attack's heading at its start (feel/charge-directions, protocol 39; the owner's rule of 2026-10-29: "you should be able to hold A or D
    /// or S and go in other directions, as well as the usual combinations"): the stick's nearest of eight directions around the view
    /// (<see cref="Evade.DirectionOf(float, float, bool)"/>: forward, the four sides, the diagonals); no stick: along the view (as before). The body is
    /// turned to it and plays the data's only clip, <c>SM_JMP_DashAtk_F</c>, forward along it (ours: the data has no other direction's clip).
    /// </summary>
    public static float DashYaw(PlayerInput input)
    {
        input = input.Sanitized();
        if (!(input.MoveX * input.MoveX + input.MoveY * input.MoveY >= Evade.DeadZone * Evade.DeadZone)) return input.Yaw;
        return input.Yaw + DashOffset(Evade.DirectionOf(input.MoveX, input.MoveY, diagonals: true));
    }

    /// <summary>The yaw offset of each of the eight directions from the view (radians; yaw grows to the left).</summary>
    public static float DashOffset(EvadeDirection direction) => direction switch
    {
        EvadeDirection.ForwardLeft => MathF.PI / 4f,
        EvadeDirection.Left => MathF.PI / 2f,
        EvadeDirection.BackLeft => 3f * MathF.PI / 4f,
        EvadeDirection.Back => MathF.PI,
        EvadeDirection.BackRight => -3f * MathF.PI / 4f,
        EvadeDirection.Right => -MathF.PI / 2f,
        EvadeDirection.ForwardRight => -MathF.PI / 4f,
        _ => 0f,
    };

    /// <summary>
    /// A charge tick's movement (after <see cref="Begin"/> counted it): the clip's displacement over the tick along the body's
    /// fixed facing, as a velocity through the capsule controller. A wall within the wall-hit angle in the rush from
    /// <see cref="RushSpec.WallFrom"/> starts the collision stop.
    /// </summary>
    /// <param name="held">Protocol 30 (<see cref="Held"/>): the body keeps its horizontal velocity (the clip waits) and falls; protocol 42: a landing after a
    /// real fall ends the charge (<see cref="EndsOnLanding"/>).</param>
    internal static PlayerState Move(PlayerState s, SimConfig c, CollisionWorld? world, bool held = false)
    {
        float dt = c.TickSeconds;
        var spec = SpecOf(c, s);
        if (MoveOf(c, s) is not { } move) return Movement.Glide(s, s.Yaw, Vector2.Zero, c, world);
        Vector2 delta;
        if (held) delta = new Vector2(s.Velocity.X, s.Velocity.Y) * dt;   // the speed it left the ground with, uncapped (owner, 2026-10-29)
        else
        {
            var local = move.At(s.RushTicks * dt) - move.At((s.RushTicks - 1) * dt);
            var forward = new Vector2(-MathF.Sin(s.Yaw), MathF.Cos(s.Yaw));
            var right = new Vector2(forward.Y, -forward.X);
            delta = right * local.X + forward * local.Y;
        }
        var next = Movement.Glide(s, s.Yaw, dt > 0f ? delta / dt : Vector2.Zero, c, world);
        // feel/melee-charge-2 (protocol 42): a held charge that lands after a real fall ends here (no resumed clip, no wall stop on the landing tick).
        if (held && next.Grounded && EndsOnLanding(s, spec, c)) return Landed(next, c);
        float wished = delta.Length();
        if (Charging(s.Rush) && spec is not null && world is not null && wished > 1e-4f && (s.RushTicks - 1) * dt + Eps >= spec.WallFrom)
        {
            float angle = Math.Clamp(float.IsFinite(spec.WallHitAngle) ? spec.WallHitAngle : 0f, 0f, 90f) * (MathF.PI / 180f);
            float sin = MathF.Sin(angle);
            var achieved = new Vector2(next.Position.X - s.Position.X, next.Position.Y - s.Position.Y);
            bool blocked = Vector2.Dot(achieved, delta / wished) <= wished * sin * sin + 1e-6f;
            // sim/player-collision: another player's capsule stops the body but is not a wall: when the move was cut, the wall test is the level's
            // move alone (the contact, World.ResolveRush, decides what meeting a player does).
            if (blocked && PlayerCollision.Current is not null)
            {
                PlayerState levelMove;
                using (PlayerCollision.Use(null)) levelMove = Movement.Glide(s, s.Yaw, dt > 0f ? delta / dt : Vector2.Zero, c, world);
                var level = new Vector2(levelMove.Position.X - s.Position.X, levelMove.Position.Y - s.Position.Y);
                blocked = Vector2.Dot(level, delta / wished) <= wished * sin * sin + 1e-6f;
            }
            if (blocked) next = Stopped(next, wall: true);
        }
        return next;
    }

    /// <summary>
    /// Whether a rush moving its body from <paramref name="from"/> to <paramref name="to"/> this tick (heading
    /// <paramref name="yaw"/>) connects with a body at <paramref name="target"/>, and how far along the move (0..1): the
    /// closest approach of the horizontal move to the target within the two capsule radii plus the margin, the target ahead:
    /// within the half-angle of the heading as seen from the move's start (so a player beside or behind, even overlapping,
    /// is not reached; one exactly at the start counts as ahead), the feet within the melee vertical reach.
    /// </summary>
    public static float? Contact(Vector3 from, Vector3 to, float yaw, Vector3 target, RushSpec spec, SimConfig c)
    {
        if (MathF.Abs(target.Z - to.Z) > c.MeleeVerticalReach) return null;
        var a = new Vector2(from.X, from.Y); var b = new Vector2(to.X, to.Y); var p = new Vector2(target.X, target.Y);
        var ab = b - a;
        float len2 = ab.LengthSquared();
        float t = len2 > 1e-10f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        var closest = a + ab * t;
        float reach = 2f * c.CapsuleRadius + spec.ContactMargin;
        float distance = Vector2.Distance(closest, p);
        if (distance > reach) return null;
        var forward = new Vector2(-MathF.Sin(yaw), MathF.Cos(yaw));
        var offset = p - a;
        float l = offset.Length();
        if (l > 1e-4f && Vector2.Dot(offset / l, forward) < MathF.Cos(spec.ContactHalfAngle * MathF.PI / 180f)) return null;
        return t;
    }
}
