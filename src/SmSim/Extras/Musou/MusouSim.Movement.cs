using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// Musou Ork movement feel (owner, 2026-11-09: "storm boys do a lot of walking in place while on the ground. reduce their movement floatiness - the same is true for all orks. make them fan out more and
/// move more intentionally and forcefully with full continuity and flow of movement"; every value ours, spec "Musou Mode: Ork movement feel"). Hashed into the config through <see cref="MusouSim.Version"/>;
/// the server's <c>SM_MUSOU_OLD_ORK_MOVE=1</c> restores the movement before (<see cref="MusouSim.OldOrkMovement"/>). Distances in metres, speeds in m/s, turns in degrees a second.
/// </summary>
public static class MusouMoveRules
{
    /// <summary>
    /// A running Ork faces the way he actually moves (his velocity smoothed by <see cref="HeadingBlend"/> a tick) once faster than <see cref="FaceMoveSpeed"/>, turning at most <see cref="FaceTurnDegrees"/>;
    /// slower he turns the same way toward where he wants to go. Before, he faced where he wanted to go at once, and the crowd's push slid him sideways or backwards under that facing (a pack's facing was
    /// 10 degrees off its move on average, 23 at the 90th percentile; a Stormboy's 104).
    /// </summary>
    public const float FaceMoveSpeed = 1.2f, FaceTurnDegrees = 540f, HeadingBlend = 0.35f;
    /// <summary>The crowd's push apart is eased by this share a tick (about two ticks to settle), so it does not flip an Ork's move from tick to tick; his own way is never eased.</summary>
    public const float PushBlend = 0.5f;
    /// <summary>
    /// A Stormboy under orders stands once within <see cref="OrderArrive"/> of his place and walks again only once pushed beyond <see cref="OrderLeave"/>. Before, he walked at full speed to within 0.8 m and
    /// the crowd and the landing shove pushed him straight back out: on the ground he stepped to and fro every tick (heading change 180 degrees a tick, 0.19 m/s on average, standing 38 % of the time).
    /// </summary>
    public const float OrderArrive = 1f, OrderLeave = 2.5f;
    /// <summary>Between hops a squad's Stormboys on the ground run on toward the squad's goal (a place this far ahead of each, again every tick) instead of standing at their landing places.</summary>
    public const float RunOnMetres = 4f;
    /// <summary>A melee Ork waiting for his blow stands this far outside a player's ring (the ring's push stops short of it, so he does not step in and out).</summary>
    public const float RingHoldMargin = 0.15f;

    /// <summary>Test bounds (ours): a running Ork's facing off his actual move on average (deg), his move's heading change a tick at the 95th percentile (deg), a pack's least width across its way (m).</summary>
    public const float MaxSlideDegrees = 8f, MaxTurnDegreesPerTick = 10f, LeastAcrossMetres = 7f;
}

public sealed partial class MusouSim
{
    /// <summary>Test aid (server <c>SM_MUSOU_OLD_ORK_MOVE=1</c>): the Ork movement before 2026-11-09 (facing, push, the Stormboys' orders and the crowd's spread numbers).</summary>
    public static bool OldOrkMovement { get; set; } = Environment.GetEnvironmentVariable("SM_MUSOU_OLD_ORK_MOVE") == "1";

    readonly Vector2[] _moveHeading = new Vector2[Capacity];
    readonly Vector2[] _pushEased = new Vector2[Capacity];
    readonly bool[] _orderSettled = new bool[Capacity];

    void SpawnedMovement(int i)
    {
        _moveHeading[i] = Vector2.Zero; _pushEased[i] = Vector2.Zero; _orderSettled[i] = false;
    }

    /// <summary>The crowd's push apart, eased (<see cref="MusouMoveRules.PushBlend"/>).</summary>
    Vector2 EasePush(int i, Vector2 push)
    {
        if (OldOrkMovement || Swarm is null) return push;   // the 5-wave run (no Swarm) as MusouSkirmishPinTests pins it
        _pushEased[i] += (push - _pushEased[i]) * MusouMoveRules.PushBlend;
        if (_pushEased[i].LengthSquared() < 1e-6f) _pushEased[i] = Vector2.Zero;
        return _pushEased[i];
    }

    /// <summary>After the move: an ordinary Ork faces his smoothed actual move when running, else turns toward <paramref name="desired"/>, at <see cref="MusouMoveRules.FaceTurnDegrees"/> either way.</summary>
    void FaceMove(int i, Vector2 desired)
    {
        _moveHeading[i] += (_velocity[i] - _moveHeading[i]) * MusouMoveRules.HeadingBlend;
        var v = _moveHeading[i];
        var want = v.LengthSquared() >= MusouMoveRules.FaceMoveSpeed * MusouMoveRules.FaceMoveSpeed ? v : desired;
        if (want.LengthSquared() < 1e-8f) return;
        float aim = MathF.Atan2(-want.X, want.Y), diff = aim - _yaw[i];
        while (diff > MathF.PI) diff -= MathF.Tau;
        while (diff < -MathF.PI) diff += MathF.Tau;
        float max = MusouMoveRules.FaceTurnDegrees * MathF.PI / 180f * Dt;
        _yaw[i] += Math.Clamp(diff, -max, max);
    }

    /// <summary>
    /// How near a melee Ork holding a token comes before he stands and waits for his blow: 0.8 of his reach, and in a war (2026-11-09) at least just outside a player's ring
    /// (<see cref="MusouRules.PlayerRing"/> plus his body and <see cref="MusouMoveRules.RingHoldMargin"/>). Before, a Boy waited only within 1.2 m and the ring pushed him back to 1.3 m: he stepped in and was pushed out every tick between blows.
    /// </summary>
    float MeleeHold(int i, MusouTypeSpec spec, in MusouTarget target)
    {
        float reach = spec.MeleeRange * 0.8f;
        if (OldOrkMovement || Swarm is null || target.Radius > 0f || target.Goal) return reach;
        return MathF.Max(reach, MathF.Min(spec.MeleeRange - 0.05f, Rules.PlayerRing + Body(_type[i]).Radius + MusouMoveRules.RingHoldMargin));   // never beyond his reach: he must still strike from where he stands
    }

    /// <summary>A Stormboy's way to his ordered place (<paramref name="way"/> from him to it) with the stand and walk-again of <see cref="MusouMoveRules.OrderArrive"/> / <see cref="MusouMoveRules.OrderLeave"/>.</summary>
    Vector2 OrderedWay(int i, Vector2 way, float speed)
    {
        float distance = way.Length();
        if (OldOrkMovement) return distance > 0.8f ? way / distance * speed : Vector2.Zero;
        if (_orderSettled[i] && distance > MusouMoveRules.OrderLeave) _orderSettled[i] = false;
        else if (!_orderSettled[i] && distance <= MusouMoveRules.OrderArrive) _orderSettled[i] = true;
        return _orderSettled[i] || distance < 1e-4f ? Vector2.Zero : way / distance * speed;
    }
}
