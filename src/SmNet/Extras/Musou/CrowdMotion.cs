using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>
/// How a crowd member's drawn motion is kept smooth between sparse records (crowd-smooth, 2026-11-08; the owner: "stormboyz look extremely choppy while jumping and flying ... do the same for all guardsmen,
/// orks"). Every value is ours (spec "Musou Mode: smooth crowd motion"). Used by <see cref="MusouClientState"/> (the Orks) and <see cref="Campaign.TfwClientState"/> (the Guardsmen).
/// </summary>
public static class CrowdMotionRules
{
    /// <summary>The drawn place closes on the sampled one with this time constant (ticks; 0.2 s, as the far runners' glide of 2026-11-06).</summary>
    public const float GlideTicks = 6f;
    /// <summary>A sampled place further than this from where the drawn one was heading is drawn at once (a teleport, a respawn on a reused id).</summary>
    public const float SnapMetres = 4f;
    /// <summary>Two samples further apart than this (render ticks) are not glided between (a stalled view or a render clock that jumped).</summary>
    public const float MaxStepTicks = 2f;
    /// <summary>The drawn facing closes on the sampled one with this time constant (ticks; ours, 67 ms): a far member's facing comes in steps, one a record.</summary>
    public const float YawTicks = 2f;
    /// <summary>
    /// A moving member past its newest record is carried on along its last step for that step's span plus <see cref="CarryMarginTicks"/>, at least <see cref="CarryTicks"/>, at most
    /// <see cref="MaxCarryTicks"/> (the Orks' rule of 2026-11-06, now the Guardsmen's too).
    /// </summary>
    public const int CarryTicks = 6, CarryMarginTicks = 3, MaxCarryTicks = 30;

    /// <summary>How far past its newest record (ticks) a member whose last step took <paramref name="stepTicks"/> is carried on; 0 for a step longer than <see cref="MaxCarryTicks"/>.</summary>
    public static int CarryFor(uint stepTicks) => stepTicks == 0 || stepTicks > MaxCarryTicks ? 0 : Math.Clamp((int)stepTicks + CarryMarginTicks, CarryTicks, MaxCarryTicks);
}

/// <summary>Where a crowd member was drawn last frame (place, facing, render tick) and the step to this frame's.</summary>
public struct DrawnMotion
{
    public bool Has;
    public Vector3 Place;
    public float Yaw;
    public double Tick;

    /// <summary>
    /// The place and facing to draw at <paramref name="renderTick"/>, given the sampled ones moving at <paramref name="velocity"/> (per tick): with <paramref name="glide"/> and a sample last frame, the last drawn
    /// place moved on at that speed and pulled toward the sampled one (<see cref="CrowdMotionRules.GlideTicks"/>) and the facing turned toward the sampled one (<see cref="CrowdMotionRules.YawTicks"/>);
    /// else (or beyond <see cref="CrowdMotionRules.SnapMetres"/>, after <see cref="CrowdMotionRules.MaxStepTicks"/>, a render clock gone back) the sampled ones.
    /// </summary>
    public (Vector3 Place, float Yaw) Step(Vector3 sampled, Vector3 velocity, float sampledYaw, double renderTick, bool glide)
    {
        var place = sampled;
        float yaw = sampledYaw;
        if (Has && glide)
        {
            double step = renderTick - Tick;
            if (step == 0) return (Place, Yaw);   // a second sample at the same render tick draws the same
            if (step > 0 && step <= CrowdMotionRules.MaxStepTicks)
            {
                var guess = Place + velocity * (float)step;
                var off = sampled - guess;
                if (off.LengthSquared() <= CrowdMotionRules.SnapMetres * CrowdMotionRules.SnapMetres)
                {
                    place = guess + off * (float)(1.0 - Math.Exp(-step / CrowdMotionRules.GlideTicks));
                    float turn = MathF.IEEERemainder(sampledYaw - Yaw, 2f * MathF.PI);
                    yaw = MathF.IEEERemainder(Yaw + turn * (float)(1.0 - Math.Exp(-step / CrowdMotionRules.YawTicks)), 2f * MathF.PI);
                }
            }
        }
        Has = true; Place = place; Yaw = yaw; Tick = renderTick;
        return (place, yaw);
    }
}
