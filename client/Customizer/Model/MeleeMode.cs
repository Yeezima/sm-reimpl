using System;
using SmSim;

namespace SmClient.Customizer.Model;

/// <summary>
/// camera/melee-orbit (owner 2026-11-25; decision melee-camera-orbit): the own player's melee mode, in which the camera orbits free (<see cref="MeleeOrbitSteer"/>).
/// The field-of-view widening (<see cref="MeleeFov"/>) keeps its own rules of 2026-11-15 (owner 2026-11-25: "keep the old widen rules but only those moves free the camera").
/// <para>
/// Enter (owner 2026-11-25: "knife, melee weapon strike, lunge also free the camera"): a swing or heavy strike with a melee weapon or the combat knife, or the
/// jump pack's dash attack lunge; not the charge (bull rush), the ground pound or the Devastator's and Havoc's kick and stomp. Leave: a ranged shot (a fire press, or fire held once the strike is over),
/// aiming, a reload press (the graph's exits of melee nav), death and respawn. A grenade throw and sprinting keep it (the graph returns them to melee nav).
/// No timer: the mode lasts until one of those (owner; the data's camera <c>melee_mode_timeout</c> 3 s is not used).
/// </para>
/// Engine-free; the client steps it once per input tick with that tick's own buttons, so the tick that leaves the mode already carries the camera's yaw.
/// </summary>
public sealed class MeleeMode
{
    /// <summary>What this tick says.</summary>
    /// <param name="Alive">The player is alive (death ends the mode, the first tick alive again is a respawn).</param>
    /// <param name="Strike">A move that frees the camera is in progress: a knife or melee-weapon strike, or the dash attack's lunge.</param>
    /// <param name="Fire">The fire button is held in this tick's input.</param>
    /// <param name="Aim">The aim button is held in this tick's input.</param>
    /// <param name="Reload">The reload button is held in this tick's input (a press edge leaves the mode).</param>
    /// <param name="Excluded">Added content's body that has no melee mode (Musou: the Dreadnought).</param>
    public readonly record struct Inputs(bool Alive, bool Strike, bool Fire, bool Aim, bool Reload, bool Excluded);

    public enum Change { None, Entered, Shot, Aimed, Reloaded, Died, Respawned, Excluded }

    bool _wasStrike, _wasFire, _wasReload, _wasAlive;

    /// <summary>In melee mode: the field of view is widened and the camera orbits freely.</summary>
    public bool Active { get; private set; }

    /// <summary>Advances one tick; what began or ended the mode, if anything.</summary>
    public Change Step(Inputs i)
    {
        var change = Change.None;
        bool strikeStart = i.Strike && !_wasStrike;
        bool fireEdge = i.Fire && !_wasFire;
        bool reloadEdge = i.Reload && !_wasReload;
        bool respawn = i.Alive && !_wasAlive;
        if (i.Excluded) change = Leave(Change.Excluded);
        else if (!i.Alive) change = Leave(Change.Died);
        else if (respawn) change = Leave(Change.Respawned);
        else if (i.Aim) change = Leave(Change.Aimed);
        else if (reloadEdge) change = Leave(Change.Reloaded);
        else if (strikeStart) { if (!Active) change = Change.Entered; Active = true; }   // a strike start beats a fire press on the same tick
        else if (fireEdge || (i.Fire && !i.Strike)) change = Leave(Change.Shot);
        _wasStrike = i.Strike; _wasFire = i.Fire; _wasReload = i.Reload; _wasAlive = i.Alive;
        return change;
    }

    Change Leave(Change why)
    {
        bool was = Active;
        Active = false;
        return was ? why : Change.None;
    }

    /// <summary>
    /// Whether this predicted state is a move that frees the camera (owner 2026-11-25): a knife or melee-weapon strike (<paramref name="meleeStep"/> above 0 with
    /// <see cref="EntersMeleeMode"/>'s wargear) or the jump pack's dash attack lunge (<see cref="Rush.IsDash"/>). The charge (bull rush), the ground pound and the
    /// kick / stomp do not.
    /// </summary>
    public static bool FreesCamera(int meleeStep, string? meleeWargear, RushPhase rush) =>
        (meleeStep > 0 && EntersMeleeMode(meleeWargear)) || Rush.IsDash(rush);

    /// <summary>
    /// Whether a strike with this melee wargear enters melee mode: a melee weapon or the combat knife (a wargear named <c>..._melee_...</c>, or the Ork boy's
    /// choppa); not the Devastator's and Havoc's kick and stomp (their melee is the heavy evade ability, no weapon) nor a class without a melee choice (null).
    /// </summary>
    public static bool EntersMeleeMode(string? meleeWargear) =>
        !string.IsNullOrEmpty(meleeWargear)
        && (meleeWargear.Contains("melee", StringComparison.OrdinalIgnoreCase) || meleeWargear.Contains("choppa", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// camera/melee-orbit, "run where you steer" (owner 2026-11-25): in melee mode the camera's yaw is free (the mouse or right stick orbit it all the way round)
/// and the body is steered separately. The movement keys or left stick, read relative to the camera, give the direction to run; the client sends that as the
/// input's yaw with the stick as pure forward (magnitude kept), so the simulation runs the marine forward along it and his body faces his run. The sent yaw turns
/// toward it at the data's body turn rate (<see cref="MaxTurnDegreesPerSecond"/>, reached after <see cref="RampSeconds"/>); a strike, an evade or a jump press
/// turns it at once. With no key held the body holds its facing, except a strike with no key held, which goes the way the camera looks.
/// Engine-free; yaw in radians, 0 facing +Y, growing to the left (the simulation's convention); stick X right, Y forward.
/// </summary>
public sealed class MeleeOrbitSteer
{
    /// <summary>Original data: <c>ebps sm_space_marine_player / biped_locomotion_ext / rotation_max_degrees_per_second</c>.</summary>
    public const float MaxTurnDegreesPerSecond = 1080f;
    /// <summary>Original data: <c>biped_locomotion_ext / rotation_time_till_max_speed</c> (seconds; read as a linear ramp from rest, ours).</summary>
    public const float RampSeconds = 0.15f;
    /// <summary>A stick shorter than this is idle (the pad's own dead zone is applied before; the keyboard sends 0 or 1).</summary>
    public const float IdleStick = 1e-3f;

    float _turning;   // seconds the current turn has lasted (the ramp)

    /// <summary>What to send this tick.</summary>
    public readonly record struct Result(float Yaw, float MoveX, float MoveY);

    /// <summary>The world yaw of a camera-relative stick.</summary>
    public static float StickYaw(float cameraYaw, float moveX, float moveY) => cameraYaw + MathF.Atan2(-moveX, moveY);

    /// <summary>
    /// One tick: <paramref name="bodyYaw"/> is the body's current heading (the predicted state's), <paramref name="snap"/> a press that turns the body to the
    /// stick at once (strike, evade, jump), <paramref name="strikePress"/> a strike press (with no key held it goes the way the camera looks).
    /// </summary>
    public Result Step(float cameraYaw, float bodyYaw, float moveX, float moveY, bool snap, bool strikePress, float dt)
    {
        float m = MathF.Min(1f, MathF.Sqrt(moveX * moveX + moveY * moveY));
        if (!(m >= IdleStick))
        {
            _turning = 0f;
            return new Result(strikePress ? Wrap(cameraYaw) : bodyYaw, 0f, 0f);
        }
        float target = StickYaw(cameraYaw, moveX, moveY);
        if (snap) { _turning = 0f; return new Result(Wrap(target), 0f, m); }
        float diff = Wrap(target - bodyYaw);
        _turning += MathF.Max(0f, dt);
        float rate = MaxTurnDegreesPerSecond * MathF.PI / 180f * (RampSeconds > 0f ? MathF.Min(1f, _turning / RampSeconds) : 1f);
        float step = rate * MathF.Max(0f, dt);
        if (MathF.Abs(diff) <= step) { _turning = 0f; return new Result(Wrap(target), 0f, m); }
        return new Result(Wrap(bodyYaw + MathF.Sign(diff) * step), 0f, m);
    }

    /// <summary>Forgets a turn in progress (the mode ended, or a throw or jump flight sent the camera's yaw).</summary>
    public void Reset() => _turning = 0f;

    /// <summary>An angle wrapped to (-pi, pi].</summary>
    public static float Wrap(float a)
    {
        a = MathF.IEEERemainder(a, 2f * MathF.PI);
        return a <= -MathF.PI ? a + 2f * MathF.PI : a;
    }
}
