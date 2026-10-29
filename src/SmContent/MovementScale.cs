using System.Numerics;
using SmSim;

namespace SmContent;

/// <summary>
/// feel/buffer-speed, the owner's request of 2026-10-29 (a **deliberate change from the original**): "Movement feels a bit too fast. Can you slow
/// movement including flying and jumping hopping speed by a small degree? Don't change charge or lunge attack speed." <see cref="Apply"/> scales,
/// by one factor (<see cref="OwnerFactor"/>, 0.9: **ours**, the owner's "small degree"):
/// <list type="bullet">
/// <item>the ground speeds (walk, run, strafe, back, sprint, and the aiming table) of the config and of every class body (the Ork's), and the
/// acceleration cap <see cref="SimConfig.MaxAcceleration"/> (the rate below the cap is speed / <see cref="SimConfig.GroundTimeToMaxSpeed"/>, so it
/// follows by itself: starts and stops take the same time as before);</item>
/// <item>the jump packs' horizontal flight: <see cref="JumpPackSpec.ForwardSpeed"/> (the lift-off and the released-stick drift target) and air
/// control's <see cref="JumpPackSpec.AirSpeed"/> and <see cref="JumpPackSpec.AirAcceleration"/>, perk variants included (heights, gravities and
/// times unchanged, so a jump is as high and as long in time, and covers 0.9 of the ground);</item>
/// <item>the travel of the jump-pack dash (its flight and landing parts) and of the heavy evade's hop (Devastator, Havoc): the clips' horizontal
/// root motion (<see cref="EvadeMove.Path"/>) × the factor over the same time; their heights unchanged.</item>
/// </list>
/// Not scaled (the owner's exceptions, and what he did not name): the charge (bull rush), the dash attack's lunge, the slash's dive and landing
/// lunge, the ground pound's dive, melee lunges, knockback, and the Tactical classes' roll (and the Ork's side hop, the Tactical's counterpart:
/// ours). The client's sprint threshold (run + 0.25 × (sprint − run); M31's charge gate, superseded in protocol 39: the charge needs only W held) is
/// computed from the scaled speeds, so it scales with them (7.375 → 6.6375 m/s on the install).
/// </summary>
public static class MovementScale
{
    /// <summary>The owner's "small degree" (ours): every speed above × 0.9.</summary>
    public const float OwnerFactor = 0.9f;

    /// <summary>The config with its movement scaled by <paramref name="factor"/> (see the class summary); <see cref="SimConfig.MoveSpeedScale"/> records it. 1: unchanged.</summary>
    public static SimConfig Apply(SimConfig c, float factor)
    {
        if (!float.IsFinite(factor) || factor <= 0f) throw new ArgumentOutOfRangeException(nameof(factor));
        if (factor == 1f) return c;
        float f = factor;
        return c with
        {
            MoveSpeedScale = c.MoveSpeedScale * f,
            RunSpeed = c.RunSpeed * f, StrafeSpeed = c.StrafeSpeed * f, BackSpeed = c.BackSpeed * f, SprintSpeed = c.SprintSpeed * f, WalkSpeed = c.WalkSpeed * f,
            AimRunSpeed = c.AimRunSpeed * f, AimStrafeSpeed = c.AimStrafeSpeed * f, AimBackSpeed = c.AimBackSpeed * f,
            MaxAcceleration = c.MaxAcceleration * f,
            Bodies = c.Bodies.Select(b => b with
            {
                RunSpeed = b.RunSpeed * f, StrafeSpeed = b.StrafeSpeed * f, BackSpeed = b.BackSpeed * f, SprintSpeed = b.SprintSpeed * f, WalkSpeed = b.WalkSpeed * f,
                AimRunSpeed = b.AimRunSpeed * f, AimStrafeSpeed = b.AimStrafeSpeed * f, AimBackSpeed = b.AimBackSpeed * f,
            }).ToArray(),
            JumpPacks = c.JumpPacks.Select(p => Pack(p, f)).ToArray(),
            Evades = c.Evades.Select(e => Scaled(e) ? Evade(e, f) : e).ToArray(),
        };
    }

    /// <summary>Whether an evade's travel scales: the jump-pack dash and the heavy hop (not the roll, not the Ork's side hop).</summary>
    public static bool Scaled(EvadeSpec e) => !e.SideOnly && (e.Dash || EvadeTuning.HopSets.Contains(e.AnimationSet));

    static JumpPackSpec Pack(JumpPackSpec p, float f) => p with
    {
        ForwardSpeed = p.ForwardSpeed * f, AirSpeed = p.AirSpeed * f, AirAcceleration = p.AirAcceleration * f,
        Variants = p.Variants.Select(v => new UpgradeVariant<JumpPackSpec>(v.Upgrades, Pack(v.Spec, f))).ToArray(),
    };

    static EvadeSpec Evade(EvadeSpec e, float f) => e with { Forward = Move(e.Forward, f), Back = Move(e.Back, f), Left = Move(e.Left, f), Right = Move(e.Right, f) };

    static EvadeMove Move(EvadeMove m, float f) => m with
    {
        Path = m.Path.Select(p => p * f).ToArray(),
        Landing = m.Landing is { } landing ? Move(landing, f) : null,
    };
}
