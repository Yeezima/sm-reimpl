using System.Numerics;

namespace SmSim;

/// <summary>
/// The sprint stop (feel/locks-sprint-stop; the owner's request of 2026-10-29: "a 'slowdown' when you stop sprinting where an animation plays of the
/// character model slowing down from the sprint back to a stop, if movement keys are let go of entirely"; a **deliberate change from the original's
/// simulation**, whose stop the ground acceleration rule made 0.16 to 0.25 s long; the curve is **ours**). Shared by the server and the client's
/// prediction (pure; the state carries one flag, <see cref="PlayerState.SprintStop"/>, and the velocity).
/// <para>
/// Start: a living player on the ground that was in plain locomotion after its last tick (no melee attack, reaction, charge, evade, jump-pack phase or
/// brace) and moving faster than <see cref="Threshold"/> (the sprint: run + 0.25 × (sprint − run), the client's sprint gate and M31's charge gate) lets go of
/// every movement key (<see cref="Cancels"/> false). Each such tick, and each later one while the stop runs, the horizontal velocity keeps its direction
/// and its speed (at most sprint speed: a faster body starts from it) falls as v² = v0² − <see cref="Rate"/> × t (a constant fall of v², so the body keeps most of its speed and stops quickly at the end,
/// the shape of the data's stop clip <c>SM_NAV_Bullrush2Idle_F_01</c>: 5.79 m over 1.2 s of root motion, 5.55 m by its <c>BRANCH_Sprint_Finished</c>
/// at 0.833 s); <see cref="Rate"/> = <see cref="SimConfig.SprintSpeed"/>² / <see cref="SimConfig.SprintStopTime"/>, so a stop from full sprint speed lasts
/// exactly <see cref="SimConfig.SprintStopTime"/> and covers 2/3 × sprint speed × that time (the install with the owner's speeds: 9 m/s, 0.833 s, 5.0 m).
/// The body moves through the capsule controller (walls and ledges as for walking) and turns with the view.
/// </para>
/// <para>
/// End: at rest, or cancelled into the normal movement (the ground acceleration rule from the speed it has) by any movement key, Jump (the evade or
/// take-off), Fire or Aim held (<see cref="Cancels"/>), by any action that moves the body (a melee attack, a reaction, a charge, an evade, a jump-pack
/// phase, the brace), by leaving the ground, by death or respawn. A throw, a reload or a switch let it run. Off when
/// <see cref="SimConfig.SprintStopTime"/> is 0 (synthetic configs; the content loader turns it on with the owner's feel changes).
/// </para>
/// </summary>
public static class SprintStop
{
    /// <summary>The fraction of the way from run to sprint speed the body must exceed to count as sprinting (M31's charge margin, superseded in protocol 39, and the client's M36 sprint gate).</summary>
    public const float SprintMargin = 0.25f;
    /// <summary>Speeds below this (m/s) count as at rest: the stop ends (ours).</summary>
    public const float RestSpeed = 0.01f;

    /// <summary>Whether the config has a sprint stop (<see cref="SimConfig.SprintStopTime"/> above 0 and a sprint speed).</summary>
    public static bool On(SimConfig c) => float.IsFinite(c.SprintStopTime) && c.SprintStopTime > 0f && float.IsFinite(c.SprintSpeed) && c.SprintSpeed > 0f;

    /// <summary>The ground speed above which a body counts as sprinting (m/s): RunSpeed + <see cref="SprintMargin"/> × (SprintSpeed − RunSpeed), at least RunSpeed.</summary>
    public static float Threshold(SimConfig c) => c.RunSpeed + SprintMargin * MathF.Max(0f, c.SprintSpeed - c.RunSpeed);

    /// <summary>How fast the squared speed falls (m²/s³): SprintSpeed² / SprintStopTime; 0 when off.</summary>
    public static float Rate(SimConfig c) => On(c) ? c.SprintSpeed * c.SprintSpeed / c.SprintStopTime : 0f;

    /// <summary>The speed after one tick of the stop from <paramref name="speed"/> (0 at rest: below <see cref="RestSpeed"/>).</summary>
    public static float Next(float speed, SimConfig c)
    {
        if (!(speed > 0f) || !float.IsFinite(speed)) return 0f;
        float next = MathF.Sqrt(MathF.Max(0f, speed * speed - Rate(c) * c.TickSeconds));
        return next < RestSpeed ? 0f : next;
    }

    /// <summary>Seconds a stop from <paramref name="speed"/> lasts (continuous: speed² / <see cref="Rate"/>); 0 when off.</summary>
    public static float Duration(float speed, SimConfig c) => On(c) && speed > 0f && float.IsFinite(speed) ? speed * speed / Rate(c) : 0f;

    /// <summary>Metres a stop from <paramref name="speed"/> covers (continuous: 2/3 × speed³ / <see cref="Rate"/>); 0 when off.</summary>
    public static float Distance(float speed, SimConfig c) => On(c) && speed > 0f && float.IsFinite(speed) ? 2f / 3f * speed * speed * speed / Rate(c) : 0f;

    /// <summary>
    /// For clients posing the stop clip: seconds into a stop from full sprint speed at which the body moves at <paramref name="speed"/>:
    /// <see cref="SimConfig.SprintStopTime"/> − <see cref="Duration"/>(speed), clamped to 0..SprintStopTime (0 at sprint speed and above, SprintStopTime at
    /// rest). A stop begun below sprint speed starts part-way in. 0 when off.
    /// </summary>
    public static float ClipTime(float speed, SimConfig c) => On(c) ? Math.Clamp(c.SprintStopTime - Duration(speed, c), 0f, c.SprintStopTime) : 0f;

    /// <summary>Whether this tick's input cancels a stop or keeps one from starting: the stick pushed (any movement key), or Jump, Fire or Aim held.</summary>
    public static bool Cancels(PlayerInput sanitized, InputButtons buttons) =>
        sanitized.MoveX != 0f || sanitized.MoveY != 0f || (buttons & (InputButtons.Jump | InputButtons.Fire | InputButtons.Aim)) != 0;

    /// <summary>Whether a state after a tick is plain locomotion: alive, no melee attack, reaction, charge, evade, jump-pack phase or brace.</summary>
    public static bool Plain(PlayerState s) =>
        s.Alive && s.MeleeStep == 0 && s.Reaction == ReactionKind.None && s.Rush == RushPhase.None && s.EvadeTicks == 0 && s.Jump == JumpPhase.None && s.Brace == BracePhase.None;

    /// <summary>
    /// Whether a tick that starts from <paramref name="start"/> (the state after the last tick) may be a stop tick by the state alone (the tick's input and
    /// actions are <see cref="Combat"/>'s): on, plain locomotion on the ground, and either a stop under way or the body faster than <see cref="Threshold"/>.
    /// </summary>
    public static bool Continues(PlayerState start, SimConfig c) =>
        On(c) && Plain(start) && start.Grounded && (start.SprintStop || new Vector2(start.Velocity.X, start.Velocity.Y).Length() > Threshold(c));

    /// <summary>The horizontal velocity after one stop tick from <paramref name="velocity"/> (the same direction, <see cref="Next"/>'s speed).</summary>
    public static Vector2 Slowed(Vector2 velocity, SimConfig c)
    {
        float speed = velocity.Length();
        if (!(speed > 0f) || !float.IsFinite(speed)) return Vector2.Zero;
        // The review of 2026-10-29: never from above sprint speed (a faster body, e.g. a roll ended by a branch, stops as if from a full sprint).
        return velocity * (Next(MathF.Min(speed, c.SprintSpeed), c) / speed);
    }
}
