using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>
/// feel/melee-lock, client presentation only (the simulation is <see cref="MeleeLock"/>): the rules the client draws melee with, engine-free so they are
/// tested. All values are ours (spec/behavior.md "Melee target lock": the presentation rows).
/// <list type="bullet">
/// <item>Hit-stop: when a melee hit lands, the attacker's and the victim's clips hold for <see cref="StopSeconds"/> and then catch up with the
/// simulation's time over <see cref="CatchUpSeconds"/> (<see cref="Hold"/>); nothing of the simulation waits.</item>
/// <item>Distance clips: an attack is posed as the blend of its distance variants at the distance its target had when it began
/// (<see cref="StartDistance"/>), the blend the lunge's root motion follows, so the feet match the body's travel.</item>
/// <item>Root-driven motion: after an attack's <see cref="MeleeStep.MoveAt"/> the body may still be moving by the clip's own root motion (the stick idle);
/// then the clip keeps the legs (<see cref="RootDriven"/>) instead of handing them to the locomotion (M40-T4).</item>
/// <item>Camera assist: while the own attack aligns to its locked target the view turns toward it at most <see cref="AssistRate"/> (<see cref="Assist"/>),
/// so the camera lines up with the body.</item>
/// </list>
/// </summary>
public static class MeleePresentation
{
    /// <summary>Ours: how long a landed melee hit holds the attacker's and the victim's clips (about four frames at 60 fps).</summary>
    public const float StopSeconds = 0.07f;
    /// <summary>Ours: how long the held clips take to catch up with the simulation's time afterwards.</summary>
    public const float CatchUpSeconds = 0.12f;
    /// <summary>Ours: the camera assist's turn rate toward the locked target, degrees a second.</summary>
    public const float AssistRate = 150f;
    /// <summary>Ours: the assist runs from the attack's start to this long after its alignment window, and only for targets within this angle of the view.</summary>
    public const float AssistAfterWindow = 0.1f, AssistMaxDegrees = 75f;
    /// <summary>Ours: the camera shake a landed own melee hit adds (radians; the knife's <c>camera_effect</c> shakes yaw 0.5 and pitch 0.45, read as degrees).</summary>
    public const float HitShake = 0.5f * MathF.PI / 180f;

    /// <summary>Seconds a clip is held back <paramref name="since"/> seconds after a hit-stop began: the time since (frozen) during the stop, then fading to 0.</summary>
    public static float Hold(float since)
    {
        if (!(since >= 0f)) return 0f;
        if (since <= StopSeconds) return since;
        return MathF.Max(0f, StopSeconds * (1f - (since - StopSeconds) / CatchUpSeconds));
    }

    /// <summary>Whether a hit-stop that began <paramref name="since"/> seconds ago still changes anything.</summary>
    public static bool Active(float since) => since >= 0f && since < StopSeconds + CatchUpSeconds;

    /// <summary>
    /// The distance the attack's target had when the attack began, seen <paramref name="time"/> into it with the target now <paramref name="distanceNow"/>
    /// away (null: no target, the graph's start blend): the attacker has already closed the blended clip's root motion to that time, so
    /// D0 = distanceNow + F(D0, time) (<see cref="MeleeReach.StartDistance"/>, the blend the simulation's lunge follows).
    /// </summary>
    public static float StartDistance(MeleeReach reach, float duration, float time, float? distanceNow)
    {
        var (low, high) = reach.Range;
        return distanceNow is float d && float.IsFinite(d) ? reach.StartDistance(time, duration, d) : Math.Clamp(reach.DefaultDistance, low, high);
    }

    /// <summary>
    /// Whether a body moving at <paramref name="velocity"/> (flat) facing <paramref name="yaw"/> at <paramref name="time"/> into <paramref name="step"/> moves by the shown clip's root
    /// motion (the blend at <paramref name="blendDistance"/>, the one the client poses): no faster than its root speed there (± a tick) plus 0.75 m/s, and along
    /// the heading (forward or back) or nearly still.
    /// </summary>
    public static bool RootDriven(MeleeStep step, float time, Vector2 velocity, float yaw, float tick, float blendDistance)
    {
        if (step.Reach is not { Variants.Count: > 0 } reach) return false;
        float h = MathF.Max(tick, 1e-3f);
        float root = MathF.Abs(reach.ForwardAt(time + h, step.Duration, blendDistance) - reach.ForwardAt(time - h, step.Duration, blendDistance)) / (2f * h);
        float speed = velocity.Length();
        if (speed > root + 0.75f) return false;
        if (speed < 0.3f) return true;
        return MathF.Abs(Vector2.Dot(velocity / speed, MeleeLock.Forward(yaw))) >= 0.8f;
    }

    /// <summary>
    /// The view yaw after <paramref name="delta"/> seconds of the camera assist toward <paramref name="targetYaw"/>: turned by at most <see cref="AssistRate"/>,
    /// never past it; unchanged when the target is more than <see cref="AssistMaxDegrees"/> off the view.
    /// </summary>
    public static float Assist(float viewYaw, float targetYaw, float delta)
    {
        float d = MeleeLock.Delta(viewYaw, targetYaw);
        if (MathF.Abs(d) > AssistMaxDegrees * MathF.PI / 180f || !(delta > 0f)) return viewYaw;
        return MeleeLock.TurnToward(viewYaw, targetYaw, AssistRate * MathF.PI / 180f * delta);
    }

    /// <summary>Whether the camera assist runs <paramref name="time"/> into an attack (from its start to <see cref="AssistAfterWindow"/> after its alignment window).</summary>
    public static bool AssistWindow(MeleeStep step, float time) =>
        step.Reach is { } r && r.AlignTo > r.AlignFrom && time <= r.AlignTo + AssistAfterWindow;

    /// <summary>
    /// The player a landed melee hit most likely struck, for presentation (the server decides): among <paramref name="others"/>, the attacker's
    /// <paramref name="preferred"/> lock when it is in reach, else the nearest enemy in reach (<see cref="SimConfig.MeleeRange"/> + 0.5 m) inside the melee cone
    /// of the attacker's heading. 0 when none.
    /// </summary>
    public static int Victim(in PlayerState attacker, IEnumerable<PlayerState> others, SimConfig c, int preferred = 0)
    {
        float minCos = MathF.Cos(c.MeleeConeDegrees * MathF.PI / 180f);
        var forward = MeleeLock.Forward(attacker.Yaw);
        int best = 0; float bestDistance = float.MaxValue;
        foreach (var o in others)
        {
            if (!MeleeLock.IsEnemy(attacker, o) || MathF.Abs(o.Position.Z - attacker.Position.Z) > c.MeleeVerticalReach) continue;
            var flat = MeleeLock.Offset(attacker, o);
            float distance = flat.Length();
            if (distance > c.MeleeRange + 0.5f) continue;
            if (distance > 1e-4f && Vector2.Dot(flat / distance, forward) < minCos) continue;
            if (o.PlayerId == preferred) return o.PlayerId;
            if (distance < bestDistance) { best = o.PlayerId; bestDistance = distance; }
        }
        return best;
    }
}
