using System.Numerics;

namespace SmSim.Extras.Musou.Arsenal;

/// <summary>
/// Musou's power spear (W3; docs/new-weapons-design.md section 6; the owner approved the design 2026-11-10: "slower than hammer but much wider reach", "impaling dive plus wide sweeping swings"). Added content,
/// Musou only. Every number is ours (kind: estimate or ours, confidence low), to check in a playtest; they are in the refine register.
/// <para>The spear is the thunder hammer's combo slowed to <see cref="Tempo"/> (<see cref="FromHammer"/>): every timing of a step divided by it, the damage of the swings times <see cref="DamageScale"/>, the slam
/// and the charge as the hammer's. Its sweeps are a wide, long flat cone (<see cref="ReachMetres"/>, <see cref="HalfAngleDegrees"/> each side, at most <see cref="MostOrks"/> Orks, through <c>World.ArsenalCleave</c>), and its
/// jump-pack dive ends in an impale (<see cref="ImpaleDamage"/> to the enemy nearest the landing within <see cref="ImpaleReach"/>, <c>World.ArsenalJump</c>) on top of the pack's own blast. It takes the hammer's
/// jump-pack rules (<see cref="ArsenalRules.WieldsHammer"/>).</para>
/// </summary>
public static class SpearRules
{
    /// <summary>The hammer's clips at this rate (D14: slower than the hammer; 0.85 an estimate): every step's timing is the hammer's divided by it. Also the client's clip rate (<c>WeaponMap.ClipSpeed</c>).</summary>
    public const float Tempo = 0.85f;

    /// <summary>Reach of a sweep, metres from the feet (D14: much wider reach; the owner 2026-11-10: "Wider and longer", 15 m, was 12; the hammer's war cleave is about 8.8 m).</summary>
    public const float ReachMetres = 15f;

    /// <summary>A sweep covers this many degrees either side of the facing (300 in all, nearly a full circle; owner 2026-11-10: "Wider and longer", was 120 each side; the hammer's war cleave is 120 each side).</summary>
    public const float HalfAngleDegrees = 150f;

    /// <summary>The most Orks one sweep strikes, nearest first (the hammer's is the war's 40; the spear's is a crowd cap of its own, estimate).</summary>
    public const int MostOrks = 12;

    /// <summary>The swings' damage as a share of the hammer's (150 against 200 for a light swing; the heavy sweeps the same share; the slam is the hammer's): an estimate.</summary>
    public const float DamageScale = 0.75f;

    /// <summary>The impale: damage to the enemy nearest the landing (a Nob's whole health; an estimate, D18) and how near it must be (metres).</summary>
    public const float ImpaleDamage = 600f, ImpaleReach = 2f;

    /// <summary>The cone's least cosine (<see cref="HalfAngleDegrees"/>).</summary>
    public static float MinCos => MathF.Cos(HalfAngleDegrees * MathF.PI / 180f);

    /// <summary>Whether <paramref name="state"/> has the power spear chosen in its melee slot (<paramref name="config"/>'s class lists carry it only in a Musou match).</summary>
    public static bool Wields(SimConfig config, PlayerState state)
    {
        int i = Loadouts.MeleeChoiceOf(config, state);
        return i >= 0 && Loadouts.ClassOf(config, state.Class)!.MeleeChoices[i].Wargear is { } w && ArsenalRules.PowerSpear.Equals(w, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The spear's combo (named <paramref name="wargear"/>) from the hammer's: slowed to <see cref="Tempo"/>, the swings' damage times <see cref="DamageScale"/>. Perk variants follow.</summary>
    public static MeleeCombo FromHammer(MeleeCombo hammer, string wargear) => Convert(hammer) with { Wargear = wargear };

    static MeleeCombo Convert(MeleeCombo c) => c with
    {
        Steps = c.Steps.Select(s => Slow(s, light: true)).ToArray(),
        Heavy = c.Heavy.Select(s => Slow(s, light: false)).ToArray(),
        Variants = c.Variants.Select(v => v with { Spec = Convert(v.Spec) }).ToArray(),
    };

    /// <summary>One step of the hammer at the spear's tempo (every time in seconds divided by <see cref="Tempo"/>; a slam keeps the hammer's damage and blast).</summary>
    public static MeleeStep Slow(MeleeStep s, bool light)
    {
        static float T(float t) => float.IsFinite(t) ? t / Tempo : t;
        float scale = s.Stomp is not null ? 1f : DamageScale;   // a step that sets off a blast is the slam: the hammer's
        var slow = s with
        {
            Duration = T(s.Duration), HitTime = T(s.HitTime), ComboOpen = T(s.ComboOpen), ComboClose = T(s.ComboClose), RangedAt = T(s.RangedAt), MoveAt = T(s.MoveAt),
            Damage = s.Damage * scale, SecondaryDamage = s.SecondaryDamage * scale, ShieldDamage = s.ShieldDamage * scale, SecondaryShieldDamage = s.SecondaryShieldDamage * scale,
            NoReactFrom = T(s.NoReactFrom), NoReactTo = T(s.NoReactTo),
            YComboOpen = T(s.YComboOpen), YComboClose = T(s.YComboClose),
            StringResetAt = T(s.StringResetAt), EvadeAt = T(s.EvadeAt),
        };
        if (s.Steers)
            // The turn is the same 185 degrees over the longer window: the rate slows with the clip.
            slow = slow with { SteerFrom = T(s.SteerFrom), SteerTo = T(s.SteerTo), SteerRate = s.SteerRate * Tempo };
        if (s.Reach is { } r)
            slow = slow with { Reach = r with { AlignFrom = T(r.AlignFrom), AlignTo = T(r.AlignTo), DistanceFrom = T(r.DistanceFrom), DistanceTo = T(r.DistanceTo) } };
        return slow;
    }
}

/// <summary>W3's part of the arsenal's name rules.</summary>
public static partial class ArsenalRules
{
    static partial void SpearWieldsHammer(string wargear, ref bool hammer)
    {
        if (PowerSpear.Equals(wargear, StringComparison.OrdinalIgnoreCase)) hammer = true;
    }
}
