using System.Numerics;

namespace SmSim;

/// <summary>
/// The direction of an evade relative to the view at the press. The first four are the four clips. The diagonals (M22, roll
/// only: <see cref="EvadeSpec.Diagonals"/>) play the forward clip (<see cref="ForwardRight"/>, <see cref="ForwardLeft"/>) or the
/// back clip (<see cref="BackRight"/>, <see cref="BackLeft"/>) with the body turned 45° (<see cref="Evade.YawOffset"/>), so
/// <see cref="PlayerState.Yaw"/> already holds the turn (see <see cref="Evade"/>).
/// </summary>
public enum EvadeDirection : byte { Forward = 0, Back = 1, Left = 2, Right = 3, ForwardRight = 4, BackRight = 5, BackLeft = 6, ForwardLeft = 7 }

/// <summary>
/// One directional evade (M21): the clip, its extracted motion and its branch timing, read by <c>SmContent.EvadeTuning</c>
/// from the marine rig (<c>SM_Base_MP</c> sequencers). Times in seconds from the press; distances in metres (assumed).
/// </summary>
/// <param name="Animation">The clip the roll plays (e.g. <c>SM_EVD_R2R_Roll_F_02_8m</c>); clients draw it at <see cref="Evade.Time"/>.</param>
/// <param name="Path">
/// The clip's extracted motion: displacement from the start in the character's frame (X right, Y forward), at
/// k × Duration / (Count − 1) for k = 0..Count − 1; linear between samples (interpretation), constant after the last.
/// </param>
/// <param name="Duration">The clip's length (its <c>BRANCH_Evade_End</c>): the evade ends when this much has played.</param>
/// <param name="MoveAt"><c>BRANCH_Run</c>: from here the stick ends the evade (movement returns).</param>
/// <param name="RangedAt"><c>BRANCH_ToRanged</c>: from here Fire, Aim or a Grenade press end it.</param>
/// <param name="SwitchAt"><c>BRANCH_To_Weapon_Switch</c>: from here a Switch press ends it (and switches).</param>
/// <param name="MeleeAt"><c>BRANCH_Melee_Combo</c>: from here a Melee press ends it (and attacks).</param>
/// <param name="NextAt"><c>BRANCH_Evade</c>: from here a Jump press starts the next evade (the spacing still applies).</param>
public sealed record EvadeMove(string Animation, IReadOnlyList<Vector2> Path, float Duration, float MoveAt, float RangedAt, float SwitchAt, float MeleeAt, float NextAt)
{
    /// <summary>The displacement after <paramref name="time"/> seconds (clamped to the clip; zero without samples).</summary>
    public Vector2 At(float time)
    {
        int n = Path.Count;
        if (n == 0) return Vector2.Zero;
        if (n == 1 || !(Duration > 0f)) return Path[^1];
        float u = Math.Clamp(time / Duration, 0f, 1f) * (n - 1);
        int i = Math.Min((int)u, n - 2);
        return Vector2.Lerp(Path[i], Path[i + 1], u - i);
    }

    /// <summary>The whole roll's displacement (the last sample).</summary>
    public Vector2 Distance => Path.Count > 0 ? Path[^1] : Vector2.Zero;

    /// <summary>
    /// The hop (M21, heavy evade): the feet's height above the take-off ground at the same sample times as <see cref="Path"/>
    /// (the clip's lowest foot or toe bone above its height in the first frame, below 2 cm counted as 0); empty for the roll,
    /// which stays on the ground. While it is above 0 the body leaves the ground and follows it (see <see cref="Evade"/>).
    /// </summary>
    public IReadOnlyList<float> Height { get; init; } = [];

    /// <summary>Whether this clip lifts the body off the ground (a hop).</summary>
    public bool Hops => Height.Any(h => h > 0f);

    /// <summary>The hop height after <paramref name="time"/> seconds (linear between samples, clamped; 0 without samples).</summary>
    public float HeightAt(float time)
    {
        int n = Height.Count;
        if (n == 0) return 0f;
        if (n == 1 || !(Duration > 0f)) return Height[^1];
        float u = Math.Clamp(time / Duration, 0f, 1f) * (n - 1);
        int i = Math.Min((int)u, n - 2);
        return Height[i] + (Height[i + 1] - Height[i]) * (u - i);
    }

    /// <summary>The highest point of the hop (0 for a roll).</summary>
    public float PeakHeight => Height.Count > 0 ? Height.Max() : 0f;

    /// <summary>
    /// M44: the flight of a jump-pack dash (<see cref="EvadeSpec.Dash"/>): <see cref="Height"/> is the clip's root-motion height
    /// (signed: it keeps falling below the take-off after the arc), which the body follows in the air; the clip's pose is in
    /// place, so clients do not lower the model by it (unlike the heavy hop, whose feet carry the lift). The flight lands on
    /// ground met on the way down (then <see cref="Landing"/> plays) and ends airborne at <see cref="Duration"/>.
    /// </summary>
    public bool Flight { get; init; }

    /// <summary>M44: the dash's landing part (its own clip, motion and branch times, from the landing tick); null otherwise.</summary>
    public EvadeMove? Landing { get; init; }

    /// <summary>M44: in a dash's landing part, the time from which a Jump press starts a take-off (<c>BRANCH_Evade_to_Jump</c>); unused otherwise.</summary>
    public float JumpAt { get; init; }

    /// <summary>
    /// Protocol 30, a dash's flight only: from this time (seconds of the flight played) a melee press starts the dash attack (the clip's
    /// <c>BRANCH_Dash_Attack</c>; 0.333 s on the install; <see cref="Rush.CanDashAttack"/>). Infinity: never (rolls, hops, landing parts).
    /// </summary>
    public float DashAttackAt { get; init; } = float.PositiveInfinity;

    /// <summary>Whether the clients lower the model by <see cref="Height"/> (the heavy hop: the clip's feet already carry the lift).</summary>
    public bool LiftsPose => Hops && !Flight;

    internal void Write(BinaryWriter w)
    {
        w.Write(Animation);
        w.Write(Path.Count); foreach (var p in Path) { w.Write(p.X); w.Write(p.Y); }
        foreach (float f in new[] { Duration, MoveAt, RangedAt, SwitchAt, MeleeAt, NextAt }) w.Write(f);
        w.Write(Height.Count); foreach (float h in Height) w.Write(h);
        w.Write(Flight); w.Write(JumpAt);
        w.Write(Landing is not null); Landing?.Write(w);
        if (float.IsFinite(DashAttackAt)) { w.Write("dash-attack"); w.Write(DashAttackAt); }   // protocol 30: only when read (older hashes unchanged)
    }
}

/// <summary>
/// A class's evade (M21; wargear <c>sm_mp_core_abil_evade</c> or <c>sm_mp_core_abil_heavy_evade</c>, the Jump input on
/// classes without a jump pack). The four directional clips and the ability's values; simulated by <see cref="Evade"/>.
/// </summary>
/// <param name="Name">The wargear file.</param>
public sealed record EvadeSpec(string Name, EvadeMove Forward, EvadeMove Back, EvadeMove Left, EvadeMove Right)
{
    /// <summary><c>special_nav_ability/min_time_between_activations</c>: seconds from one evade's press before the next may start (0.8).</summary>
    public float MinInterval { get; init; } = 0.8f;
    /// <summary>
    /// The forward sequencer aligns the character to the stick (<c>Align To Nav Direction</c> modifier): a forward roll
    /// turns the body to the stick's direction at the press and rolls along it.
    /// </summary>
    public bool AlignForward { get; init; }
    /// <summary>
    /// <c>special_nav_ability/max_horz_angle_for_wall_hit</c> (degrees, 25): a wall met within this angle of head-on ends the
    /// evade (interpretation of what a wall hit does; see <see cref="Evade"/>).
    /// </summary>
    public float WallHitAngle { get; init; } = 25f;
    /// <summary><c>max_turn_rate</c> (40; degrees per second assumed): recorded, not simulated (the body keeps its press-time facing).</summary>
    public float MaxTurnRate { get; init; } = 40f;
    /// <summary>
    /// Seconds the forward clip's <c>TargetableMode</c> entity property lasts from the press. With <see cref="CollisionModeTime"/>
    /// a window of hyper armour: stun effects do not apply (from the user's knowledge of the original; the data do not say
    /// which property does what). Not damage immunity.
    /// </summary>
    public float TargetableModeTime { get; init; }
    /// <summary>Seconds the forward clip's <c>CollisionMode</c> entity property lasts from the press (hyper armour, as above).</summary>
    public float CollisionModeTime { get; init; }
    /// <summary>Seconds of hyper armour from the press: the longer of the two property windows (<see cref="Evade.HyperArmour"/>).</summary>
    public float HyperArmourTime => MathF.Max(TargetableModeTime, CollisionModeTime);
    /// <summary>
    /// Whether two stick directions at once roll diagonally (M22; 8 sectors, <see cref="Evade.DirectionOf(float, float, bool)"/>):
    /// the Tactical roll yes, the heavy hop no (4 directions). From the user's knowledge of the original ("hold D and S and
    /// roll south-east"); the data have only the four clips. How a diagonal moves is an interpretation (<see cref="Evade"/>).
    /// </summary>
    public bool Diagonals { get; init; }
    /// <summary>The wargear's <c>animation_set</c> ("evade", "heavy_evade").</summary>
    public string AnimationSet { get; init; } = "";
    /// <summary>The ability's <c>camera_curve</c> (e.g. "evade_default_mp"; for the client).</summary>
    public string Camera { get; init; } = "";

    /// <summary>
    /// M44: the jump-pack classes' evade, the dash (<c>sm_mp_core_abil_jump_evade</c>; <c>SM_JMP_Dash_*</c> clips of SM_Base_MP's
    /// "Jumppack Evade" state): a tap of the Jump input (released before <see cref="ToJumpAt"/>) dashes, a hold jumps
    /// (<see cref="JumpPack"/>). Its moves are flights (<see cref="EvadeMove.Flight"/>) with landing parts.
    /// </summary>
    public bool Dash { get; init; }

    /// <summary>
    /// M44, dash only: seconds after the press at which a Jump input still held makes the press a jump instead of a dash
    /// (<c>BRANCH_Evade_to_Jump</c> with the "Holding Evade Condition Modifier" of the dash sequencers: 0.167 s on the install).
    /// </summary>
    public float ToJumpAt { get; init; }

    /// <summary>
    /// Ork O4 (design section 1.5): the evade has only side clips (the Ork Boy's dodge hop, <c>BOY_2HR_NAV_Evade4m_L/R</c>): every press
    /// hops left or right (<see cref="Evade.SideOf"/>), never forward, back or diagonally; <see cref="Forward"/> and <see cref="Back"/> are
    /// stand-ins (the right and left clips) that are never played.
    /// </summary>
    public bool SideOnly { get; init; }

    /// <summary>The clip of a direction (a diagonal plays the forward or back clip; <see cref="EvadeDirection"/>).</summary>
    public EvadeMove Move(EvadeDirection direction) => direction switch
    {
        EvadeDirection.Back or EvadeDirection.BackRight or EvadeDirection.BackLeft => Back,
        EvadeDirection.Left => Left,
        EvadeDirection.Right => Right,
        _ => Forward,
    };

    /// <summary>Writes every field for a config hash.</summary>
    internal void Write(BinaryWriter w)
    {
        w.Write(Name);
        Forward.Write(w); Back.Write(w); Left.Write(w); Right.Write(w);
        foreach (float f in new[] { MinInterval, WallHitAngle, MaxTurnRate, TargetableModeTime, CollisionModeTime }) w.Write(f);
        w.Write(AlignForward); w.Write(AnimationSet); w.Write(Camera); w.Write(Diagonals);
        w.Write(Dash); w.Write(ToJumpAt);
        if (SideOnly) w.Write("side-only");   // Ork O4: only for a side-only evade (older hashes unchanged)
    }
}

/// <summary>
/// The evade roll (M21), the Jump input of classes without a jump pack. Shared by the server and client prediction (pure;
/// integer tick counters so the state round-trips exactly). The clips' motion and branch times are the install's; how they
/// combine is an **interpretation** (spec/behavior.md):
/// <para>
/// Start: a Jump press edge while alive, on the ground, not evading (or at <see cref="EvadeMove.NextAt"/> of the evade in
/// progress), with no melee attack in progress or starting this tick, no throw in progress (feel/locks-sprint-stop: <see cref="Combat.ThrowHolds"/>), and at least
/// <see cref="EvadeSpec.MinInterval"/> since the previous evade's press. The direction comes from the stick at the press
/// (<see cref="DirectionOf(float, float, bool)"/>: the nearest of forward, back, left, right, or with
/// <see cref="EvadeSpec.Diagonals"/> of those and the four diagonals; no stick: forward). The body keeps the view's yaw,
/// except a forward roll with <see cref="EvadeSpec.AlignForward"/>, which turns it to the stick (<see cref="AlignedYaw"/>;
/// forward diagonals included), and a diagonal otherwise, which turns it by <see cref="YawOffset"/> (±45°) and plays the
/// forward or back clip along the turned body: the diagonal's path is that clip's path turned 45°, with its distance,
/// duration and branch times (M22, interpretation: the R2R set has no diagonal clips). The yaw then stays fixed for the
/// whole evade, so clients draw the model at <see cref="PlayerState.Yaw"/> playing <see cref="EvadeSpec.Move"/>'s clip. Starting cancels aim mode, a charge and a pending charged shot; a reload
/// carries on (its timer runs, nothing else of the weapon does).
/// </para>
/// <para>
/// Motion: the press tick is evade tick 1; tick n moves the body by the clip's displacement between (n − 1) × dt and n × dt
/// (<see cref="EvadeMove.At"/>), turned by the yaw, as a velocity through the capsule controller (walls block it, it slides
/// along shallow ones, steps and ledges work as for walking, gravity applies in the air). Meeting a wall within
/// <see cref="EvadeSpec.WallHitAngle"/> of head-on ends the evade. The Tactical classes' evade is a roll on the ground; the
/// heavy classes' is a small hop: their clips carry a <see cref="EvadeMove.Height"/> curve (the feet's clearance, about 0.3 m
/// for about 0.2 s) that lifts the body off the ground and sets it down again (see <see cref="Step"/>).
/// </para>
/// <para>
/// Hyper armour (<see cref="HyperArmour"/>): the first <see cref="EvadeSpec.HyperArmourTime"/> of an evade negates stun
/// effects (from the user's knowledge of the original); the hit reactions (M28, <see cref="Reaction.CanReact"/>) check it.
/// </para>
/// <para>
/// During it: no firing, charging, melee, throwing or switching; stick, Aim and the other presses are ignored (presses are
/// lost, not buffered) until the branch time that ends the evade on them (<see cref="EvadeMove.MoveAt"/>,
/// <see cref="EvadeMove.RangedAt"/>, <see cref="EvadeMove.SwitchAt"/>, <see cref="EvadeMove.MeleeAt"/>; the input then acts
/// normally in the same tick). Otherwise it ends once <see cref="EvadeMove.Duration"/> has played. Death ends it.
/// </para>
/// <para>
/// M44, the jump-pack dash (<see cref="EvadeSpec.Dash"/>; the Assault and Raptor "small hop"; owner's request 2026-10-28 to make it
/// mostly horizontal in eight directions; the clips, motion and branch times are the install's, the rules **interpretation**): it does
/// not start on the press but when <see cref="JumpPack"/> reports a take-off released before <see cref="EvadeSpec.ToJumpAt"/>
/// (<c>dashStart</c>); same start conditions and spacing as a roll otherwise, eight directions (diagonals turn the forward or back
/// clip 45°) from the stick **at the press** (<see cref="PlayerState.JumpDashDirection"/>, latched by <see cref="Combat"/> when the take-off
/// starts: a key let go a tick before Space still counts; ours), the body's yaw from the view at the release. The flight (<see cref="EvadeMove.Flight"/>) follows the clip's root motion in all three axes with no gravity (about 12 m
/// along and 1.7 m up at its highest on the install, back to the take-off height after 0.73 s) and lands on ground met on the way down;
/// no input ends it (ours: presses during it are lost); a wall within the hit angle ends it, and so does the flight's end
/// (<see cref="EvadeMove.Duration"/>; the body then falls under normal movement). The landing tick switches to the landing part
/// (<see cref="PlayerState.EvadeLanded"/>, evade tick 1 = landing clip time 0): its clip's motion on the ground and its branch times
/// as for a roll; a Jump press from <see cref="EvadeMove.JumpAt"/> ends it with a take-off (<see cref="JumpAllowed"/>). Hyper armour
/// covers the flight only (ours).
/// </para>
/// </summary>
public static class Evade
{
    const float Eps = 1e-5f;
    /// <summary>Stick deflection below which a press counts as "no direction" (ours).</summary>
    public const float DeadZone = 0.1f;
    /// <summary>Largest <see cref="PlayerState.EvadeTicks"/> and <see cref="PlayerState.EvadeLock"/> (u8 in the own record).</summary>
    public const int MaxTicks = byte.MaxValue;
    /// <summary>
    /// Largest <see cref="PlayerState.EvadeTicks"/> a slim record carries (6 bits): 2.1 s at 30 Hz, longer than any clip (1.17 s =
    /// 35 ticks). At 60 Hz and more the ranged roll (70 ticks) exceeds it, and remote players' rolls would read as saturated
    /// near their end (the own record carries the exact count up to <see cref="MaxTicks"/>, so prediction is unaffected).
    /// </summary>
    public const int MaxSlimTicks = 63;

    /// <summary>Whether the player is evading after its last tick.</summary>
    public static bool Evading(PlayerState s) => s.EvadeTicks > 0;

    /// <summary>
    /// Seconds of the evade clip played after the player's last tick (<see cref="PlayerState.EvadeTicks"/> × tick length; 0 when not
    /// evading); in a dash's landing part, of the landing clip ((EvadeTicks − 1) × tick length).
    /// </summary>
    public static float Time(PlayerState s, SimConfig c) => Played(Math.Max(0, s.EvadeTicks), s.EvadeLanded, c.TickSeconds);

    static float Played(int ticks, bool landed, float dt) => (landed ? Math.Max(0, ticks - 1) : ticks) * dt;

    /// <summary>
    /// The clip the player's evade plays (its class's spec, its direction; a dash's landing part once landed), or null when not
    /// evading or the class has no evade.
    /// </summary>
    public static EvadeMove? Move(PlayerState s, SimConfig c) =>
        s.EvadeTicks > 0 && Loadouts.Evade(c, s) is { } spec ? Part(spec.Move(s.EvadeDirection), s.EvadeLanded) : null;

    static EvadeMove Part(EvadeMove move, bool landed) => landed && move.Landing is { } landing ? landing : move;

    /// <summary>
    /// M44: whether a Jump press may start a jump-pack take-off given the evade before this tick: always when not evading or for a
    /// non-dash evade (those classes have no pack); never during a dash's flight; in its landing part from <see cref="EvadeMove.JumpAt"/>.
    /// </summary>
    public static bool JumpAllowed(PlayerState s, EvadeSpec? spec, SimConfig c)
    {
        if (s.EvadeTicks <= 0 || spec is not { Dash: true }) return true;
        if (!s.EvadeLanded) return false;
        var landing = Part(spec.Move(s.EvadeDirection), true);
        return Time(s, c) + Eps >= c.LockTime(landing.JumpAt);   // feel/locks-sprint-stop: × the lock scale
    }

    /// <summary>
    /// Fidelity gap 7: whether a Jump press this tick starts an evade from rest (no evade under way before it), as <see cref="Begin"/>'s
    /// start condition decides it: a non-dash evade, its spacing over by this tick, on the ground, no melee step or throw (feel/locks-sprint-stop: a throw
    /// past its scaled evade gate, <see cref="Combat.ThrowHolds"/>, no longer counts), no melee press. The brace's exit ends on Jump only then (<see cref="Brace.Begin"/>).
    /// </summary>
    public static bool StartsFromRest(PlayerState s, EvadeSpec? spec, bool meleePress, SimConfig c) =>
        spec is { Dash: false } && s.EvadeTicks == 0 && Math.Max(0, s.EvadeLock - 1) == 0 && s.Grounded && s.MeleeStep == 0 && !meleePress && !Combat.ThrowHolds(s, c);

    /// <summary>The fraction of the clip played (0..1): <see cref="Time"/> / the clip's duration; 0 when not evading.</summary>
    public static float Progress(PlayerState s, SimConfig c) =>
        Move(s, c) is { Duration: > 0f } m ? Math.Clamp(Time(s, c) / m.Duration, 0f, 1f) : 0f;

    /// <summary>
    /// Whether the player is in its evade's hyper-armour window after its last tick: evading and at most
    /// <see cref="EvadeSpec.HyperArmourTime"/> played (the press tick included). Stun, stagger, knockback and interrupt effects
    /// must not apply then (from the user's knowledge of the original); <see cref="Reaction.CanReact"/> checks it (M28). The
    /// damage still applies.
    /// </summary>
    public static bool HyperArmour(PlayerState s, SimConfig c) =>
        s.EvadeTicks > 0 && Loadouts.Evade(c, s) is { } spec && !(spec.Dash && s.EvadeLanded) && Time(s, c) <= spec.HyperArmourTime + Eps;

    /// <summary>
    /// The direction for a stick (X right, Y forward) with <paramref name="diagonals"/> (M22): within <see cref="DeadZone"/>,
    /// forward; else the nearest of eight directions, 45° sectors centred on the axes and the diagonals (so two keys at once,
    /// e.g. back and right, give the diagonal). Without: <see cref="DirectionOf(float, float)"/>. Sectors ours.
    /// </summary>
    public static EvadeDirection DirectionOf(float moveX, float moveY, bool diagonals)
    {
        if (!diagonals) return DirectionOf(moveX, moveY);
        if (!float.IsFinite(moveX) || !float.IsFinite(moveY) || moveX * moveX + moveY * moveY < DeadZone * DeadZone) return EvadeDirection.Forward;
        // Clockwise from forward in eighths of a turn: 0 forward, 1 forward-right, 2 right, … 7 forward-left.
        int sector = ((int)MathF.Floor(MathF.Atan2(moveX, moveY) / (MathF.PI / 4f) + 0.5f) % 8 + 8) % 8;
        return Sectors[sector];
    }

    static readonly EvadeDirection[] Sectors =
    [
        EvadeDirection.Forward, EvadeDirection.ForwardRight, EvadeDirection.Right, EvadeDirection.BackRight,
        EvadeDirection.Back, EvadeDirection.BackLeft, EvadeDirection.Left, EvadeDirection.ForwardLeft,
    ];

    /// <summary>
    /// How far a diagonal turns the body from the view at the press (radians; positive turns left, as yaw does): forward-right
    /// and back-left −π/4, forward-left and back-right +π/4 (the forward or back clip then travels along the diagonal); 0 for
    /// the four clip directions. A forward diagonal with <see cref="EvadeSpec.AlignForward"/> turns to the stick instead.
    /// </summary>
    public static float YawOffset(EvadeDirection direction) => direction switch
    {
        EvadeDirection.ForwardRight or EvadeDirection.BackLeft => -MathF.PI / 4f,
        EvadeDirection.ForwardLeft or EvadeDirection.BackRight => MathF.PI / 4f,
        _ => 0f,
    };

    /// <summary>Whether a direction plays the forward clip (forward and the forward diagonals).</summary>
    public static bool IsForward(EvadeDirection direction) => direction is EvadeDirection.Forward or EvadeDirection.ForwardRight or EvadeDirection.ForwardLeft;

    /// <summary>
    /// The clip for a stick (X right, Y forward), four directions: within <see cref="DeadZone"/>, forward; else the nearest axis (|Y| ≥ |X|:
    /// forward or back, so exact diagonals roll forward or back; else left or right). Interpretation: the sequencers are
    /// single clips (no blends), the sectors are ours.
    /// </summary>
    public static EvadeDirection DirectionOf(float moveX, float moveY)
    {
        if (!float.IsFinite(moveX) || !float.IsFinite(moveY) || moveX * moveX + moveY * moveY < DeadZone * DeadZone) return EvadeDirection.Forward;
        if (MathF.Abs(moveY) >= MathF.Abs(moveX)) return moveY >= 0f ? EvadeDirection.Forward : EvadeDirection.Back;
        return moveX > 0f ? EvadeDirection.Right : EvadeDirection.Left;
    }

    /// <summary>Speed (m/s) across the view below which the body's sideways motion does not pick a side-only hop's side (ours).</summary>
    public const float SideDrift = 0.5f;

    /// <summary>
    /// The side of a side-only hop (Ork O4, <see cref="EvadeSpec.SideOnly"/>; design section 1.5, rule **ours**): the stick's sideways
    /// sign when it is pushed sideways beyond <see cref="DeadZone"/> (so a diagonal press hops to its side); else (forward, back or no
    /// stick) the side the body is already moving to across the view (<paramref name="velocityX"/>, <paramref name="velocityY"/> in world
    /// space, faster than <see cref="SideDrift"/>: the "last strafe side" from the state the simulation keeps); else right.
    /// </summary>
    public static EvadeDirection SideOf(float moveX, float velocityX, float velocityY, float yaw)
    {
        if (float.IsFinite(moveX) && MathF.Abs(moveX) >= DeadZone) return moveX > 0f ? EvadeDirection.Right : EvadeDirection.Left;
        var right = new Vector2(MathF.Cos(yaw), MathF.Sin(yaw));   // the view's right (yaw 0 faces +Y, right is +X)
        float across = float.IsFinite(velocityX) && float.IsFinite(velocityY) && float.IsFinite(yaw) ? Vector2.Dot(new Vector2(velocityX, velocityY), right) : 0f;
        if (MathF.Abs(across) > SideDrift) return across > 0f ? EvadeDirection.Right : EvadeDirection.Left;
        return EvadeDirection.Right;
    }

    /// <summary>The yaw whose forward is the stick's direction in the view's frame (the view's yaw within <see cref="DeadZone"/>).</summary>
    public static float AlignedYaw(float yaw, float moveX, float moveY)
    {
        if (!float.IsFinite(moveX) || !float.IsFinite(moveY) || moveX * moveX + moveY * moveY < DeadZone * DeadZone) return yaw;
        return yaw + MathF.Atan2(-moveX, moveY);
    }

    /// <summary>
    /// Ticks of spacing after a press: the fewest whose length reaches <see cref="EvadeSpec.MinInterval"/> × <see cref="SimConfig.ActionLockScale"/>
    /// (feel/locks-sprint-stop; at most <see cref="MaxTicks"/>).
    /// </summary>
    public static int LockTicks(EvadeSpec spec, SimConfig c)
    {
        float ticks = c.LockTime(spec.MinInterval) / c.TickSeconds;
        if (!float.IsFinite(ticks) || ticks <= 0f) return 0;
        return (int)Math.Clamp(MathF.Ceiling(ticks - 1e-3f), 0f, MaxTicks);
    }

    /// <summary>The first part of a tick: end, chaining and start (before the weapon and melee).</summary>
    internal readonly record struct Tick(bool Active, bool Started, int Ticks, EvadeDirection Direction, int Lock, float Yaw, bool Landed = false);

    /// <param name="rangedDown">Fire or Aim held, or a Grenade press: input that ends the evade from <see cref="EvadeMove.RangedAt"/>.</param>
    /// <param name="dashStart">M44, dash only: the jump pack turned this tick's take-off into a dash (its start trigger instead of <paramref name="jumpPress"/>).</param>
    /// <param name="takeOff">M44, dash only: a take-off started this tick (in the landing part, from <see cref="EvadeMove.JumpAt"/>): it ends the dash.</param>
    /// <param name="dashDirection">M44, dash only: the direction latched at the take-off's press (<see cref="PlayerState.JumpDashDirection"/>); null: the stick now.</param>
    internal static Tick Begin(PlayerState s, EvadeSpec? spec, PlayerInput input, bool jumpPress, bool meleePress, bool switchPress, bool rangedDown, SimConfig c,
        bool dashStart = false, bool takeOff = false, EvadeDirection? dashDirection = null)
    {
        if (spec is null) return new Tick(false, false, 0, EvadeDirection.Forward, 0, s.Yaw);
        float dt = c.TickSeconds;
        int lockTicks = Math.Max(0, s.EvadeLock - 1);
        bool active = s.EvadeTicks > 0;
        bool landed = active && spec.Dash && s.EvadeLanded;
        int ticks = active ? s.EvadeTicks : 0;
        var direction = active ? s.EvadeDirection : EvadeDirection.Forward;
        float yaw = s.Yaw;
        bool stick = input.MoveX * input.MoveX + input.MoveY * input.MoveY >= DeadZone * DeadZone;
        bool chain = false;
        if (active)
        {
            var move = Part(spec.Move(direction), landed);
            float played = Played(ticks, landed, dt);
            bool At(float time) => played + Eps >= time;
            if (spec.Dash && !landed)
            {
                // The dash's flight: only its end (or a wall, in Step) ends it.
                if (At(move.Duration)) { active = false; ticks = 0; direction = EvadeDirection.Forward; }
            }
            else
            {
                // Protocol 30 (ours, with the owner's walk-off rule): an evade carried off a ledge is not ended by input while its body is in the air
                // (outside its own hop): its clip motion plays on to the end, then the body drops straight down (Movement.Step).
                bool inputs = s.Grounded || move.HeightAt(played) > 0f;
                // feel/locks-sprint-stop: the stick's and the next evade's branches × the lock scale (the ranged, switch and melee branches are the data's).
                chain = !spec.Dash && inputs && jumpPress && lockTicks == 0 && At(c.LockTime(move.NextAt));
                if (At(move.Duration) || (inputs && ((stick && At(c.LockTime(move.MoveAt))) || (rangedDown && At(move.RangedAt)) || (switchPress && At(move.SwitchAt))
                    || (meleePress && At(move.MeleeAt)))) || chain || (spec.Dash && takeOff))
                { active = false; ticks = 0; direction = EvadeDirection.Forward; landed = false; }
            }
        }
        bool started = false;
        // An evade that ended this tick for another reason (the stick, a ranged or switch input) lets the press start none:
        // a new one needs NextAt (the chain above) or no evade before this tick.
        bool trigger = spec.Dash ? dashStart : jumpPress;
        if (!active && trigger && lockTicks == 0 && (s.EvadeTicks == 0 || chain)
            && s.Grounded && s.MeleeStep == 0 && !meleePress && !Combat.ThrowHolds(s, c))
        {
            direction = spec.Dash && dashDirection is { } latched ? latched
                : spec.SideOnly ? SideOf(input.MoveX, s.Velocity.X, s.Velocity.Y, input.Yaw)
                : DirectionOf(input.MoveX, input.MoveY, spec.Diagonals);
            yaw = spec.AlignForward && IsForward(direction) ? AlignedYaw(input.Yaw, input.MoveX, input.MoveY) : input.Yaw + YawOffset(direction);
            active = true; started = true; ticks = 1; landed = false;
            lockTicks = LockTicks(spec, c);
        }
        else if (active) ticks = Math.Min(ticks + 1, MaxTicks);
        return new Tick(active, started, ticks, direction, lockTicks, yaw, active && landed);
    }

    /// <summary>
    /// M44: the evade fields after a tick: not evading (0, Forward, not landed); a dash that landed this tick starts its landing part
    /// (tick 1 = landing clip time 0); otherwise the tick's.
    /// </summary>
    internal static (int Ticks, EvadeDirection Direction, bool Landed) After(Tick t, bool evading, bool landedNow) =>
        !evading ? (0, EvadeDirection.Forward, false) : landedNow ? (1, t.Direction, true) : (t.Ticks, t.Direction, t.Landed);

    /// <summary>
    /// The evade's movement for this tick (<paramref name="t"/> active): the clip's displacement over the tick, turned by the
    /// evade's yaw, through the capsule controller. <paramref name="wallHit"/>: a wall within the spec's angle of head-on.
    /// </summary>
    internal static PlayerState Step(PlayerState s, EvadeSpec spec, Tick t, SimConfig c, CollisionWorld? world, out bool wallHit) =>
        Step(s, spec, t, c, world, out wallHit, out _);

    /// <param name="landed">M44: a dash's flight met the ground on its way down this tick (its landing part starts: <see cref="After"/>).</param>
    internal static PlayerState Step(PlayerState s, EvadeSpec spec, Tick t, SimConfig c, CollisionWorld? world, out bool wallHit, out bool landed)
    {
        float dt = c.TickSeconds;
        var move = Part(spec.Move(t.Direction), t.Landed);
        // Clip time at the start and end of this tick: tick n plays (n − 1, n] × dt; a landing part starts one tick later (tick 1 = time 0).
        float to = Played(t.Ticks, t.Landed, dt), from = MathF.Max(0f, to - dt);
        var local = move.At(to) - move.At(from);
        var forward = new Vector2(-MathF.Sin(t.Yaw), MathF.Cos(t.Yaw));   // yaw 0 faces +Y, as in movement
        var right = new Vector2(forward.Y, -forward.X);
        var delta = right * local.X + forward * local.Y;
        var horizontal = dt > 0f ? delta / dt : Vector2.Zero;
        PlayerState next;
        landed = false;
        float before = move.HeightAt(from), after = move.HeightAt(to);
        float vz = dt > 0f ? (after - before) / dt : 0f;
        // Protocol 30 (ours, the review of 2026-10-29: the owner's "drops straight down"): a roll, hop or a dash's landing part whose body is falling
        // (in the air past its first tick, outside its own hop curve) over a real drop (Movement.FallsStraight) stops travelling; its clip plays on.
        // Once stopped it stays stopped until it lands (its velocity is then zero), also through the last metre above the ground.
        if (!move.Flight && !s.Grounded && s.Velocity.Z != 0f && before <= 0f && after <= 0f && horizontal != Vector2.Zero
            && ((s.Velocity.X == 0f && s.Velocity.Y == 0f) || Movement.FallsStraight(s, c, world)))
        { horizontal = Vector2.Zero; delta = Vector2.Zero; }
        if (move.Flight)
        {
            // M44, the dash's flight: the root motion's height in the air with no gravity; ground met on the way down lands it.
            next = Movement.Fly(s with { Grounded = false }, t.Yaw, horizontal, vz, 0f, c, world);
            landed = next.Grounded && vz <= 0f;
        }
        else
        {
            // A hop (Height above 0 at either end of the tick) follows the clip's height curve: a rise lifts the body off the
            // ground, and in the air it moves by the curve's change with no gravity, landing where it meets ground on the way
            // down (early on a higher floor). On the ground on the way down it stays there. After the curve, a body still in the
            // air (off a ledge) falls with gravity from the curve's last vertical speed.
            bool hopTick = (before > 0f || after > 0f) && (vz > 0f || !s.Grounded);
            next = hopTick ? Movement.Fly(s with { Grounded = false }, t.Yaw, horizontal, vz, 0f, c, world) : Movement.Glide(s, t.Yaw, horizontal, c, world);
        }
        wallHit = false;
        float wished = delta.Length();
        if (world is not null && wished > 1e-4f)
        {
            // Sliding along a wall met at angle θ from head-on keeps sin²θ of the wished step along the wish; less than
            // sin²(WallHitAngle) of it is a wall within that angle.
            float angle = Math.Clamp(float.IsFinite(spec.WallHitAngle) ? spec.WallHitAngle : 0f, 0f, 90f) * (MathF.PI / 180f);
            float sin = MathF.Sin(angle);
            var achieved = new Vector2(next.Position.X - s.Position.X, next.Position.Y - s.Position.Y);
            wallHit = Vector2.Dot(achieved, delta / wished) <= wished * sin * sin + 1e-6f;
        }
        return next;
    }
}
