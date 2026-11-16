using System;
using System.Numerics;

namespace SmNet.Extras.Musou;

/// <summary>How a flinching Ork moves: straight away from the hit (most), to the side, or away and back.</summary>
public enum MusouFlinchMode : byte { Away, SideLeft, SideRight, BackAndForth }

/// <summary>One Ork's flinch (constants of the Ork, from <see cref="MusouOrkVariety.Flinch"/>): the share of the kick's size, of its duration, the seconds before it starts, and its way.</summary>
public readonly record struct MusouFlinchVariant(float Strength, float Duration, float Delay, MusouFlinchMode Mode)
{
    /// <summary>The unvaried flinch (the original: straight away, full size, normal length, at once).</summary>
    public static readonly MusouFlinchVariant None = new(1f, 1f, 0f, MusouFlinchMode.Away);
}

/// <summary>
/// Musou Mode, Ork variety (owner order 2026-11-16: a blast that hits or kills a wave of Orks made them all flinch and die in the same pose at the same instant, mirroring each other).
/// Client presentation only: no simulation, wire, protocol or hash change, and nothing here runs outside Musou Mode (Classic PvP never reaches it). Every Ork's variation is deterministic from
/// its entity id: the same Ork reacts the same way on every client and every run. Every number is ours (kind: ours; confidence: low until a playtest; spec "Musou Mode: Ork variety").
/// <para>
/// The flinch's strength, duration and delay are low-discrepancy sequences over the id (a Weyl sequence with a fixed step), not dice: ids next to each other (the Orks of one wave get ids
/// in a row) are at least <see cref="NeighbourSpread"/> of the range apart, so no two neighbours can roll alike, which a random roll cannot promise.
/// </para>
/// </summary>
public static class MusouOrkVariety
{
    // ---- the flinch
    /// <summary>The range of the share of the kick's size an Ork takes (ours; a stronger one is held at <see cref="MusouOrkFlinchRules.MaxStrength"/>).</summary>
    public const float StrengthMin = 0.55f, StrengthMax = 1.2f;
    /// <summary>The range of the share of the reaction's duration (ours).</summary>
    public const float DurationMin = 0.65f, DurationMax = 1.45f;
    /// <summary>The longest wait (s) before an Ork's flinch starts: the phase (ours; the reference's rise is 42 ms, so up to two frames of 24 fps).</summary>
    public const float DelayMax = 0.08f;
    /// <summary>The share of Orks that flinch sideways (half to each side) or away and back instead of straight away from the hit (ours, an estimate: the owner asked for "a few").</summary>
    public const float SideShare = 0.14f, BackAndForthShare = 0.09f;
    /// <summary>How far sideways a side flinch turns from straight away from the hit (degrees; ours).</summary>
    public const float SideDegrees = 90f;
    /// <summary>Where a back-and-forth flinch comes back: the reaction's share where it starts to rebound, where it is done, and the rebound's depth against the first swing (ours).</summary>
    public const float ReboundStart = 0.35f, ReboundEnd = 0.85f, ReboundDepth = 0.85f;
    /// <summary>The smallest gap between two ids in a row, as a share of the range (the Weyl steps below make it at least this).</summary>
    public const float NeighbourSpread = 0.38f;

    // The steps: the distance to the nearest whole number is the least gap between two consecutive ids (0.4641 for strength, 0.4142 for duration, 0.382 for the delay).
    const double StrengthStep = 0.4641016151, DurationStep = 0.4142135624, DelayStep = 0.3819660113;

    /// <summary>The seed of an Ork (a well-mixed 32 bits from its id).</summary>
    public static uint Seed(int id)
    {
        uint x = unchecked((uint)id * 2654435761u + 0x9E3779B9u);
        x ^= x >> 16; x *= 0x7FEB352Du; x ^= x >> 15; x *= 0x846CA68Bu; x ^= x >> 16;
        return x;
    }

    /// <summary>A repeatable roll in [0, 1) for an Ork and a purpose (<paramref name="salt"/>).</summary>
    public static float Roll(int id, int salt)
    {
        uint x = Seed(id) ^ unchecked((uint)salt * 0x85EBCA6Bu);
        x ^= x >> 13; x *= 0xC2B2AE35u; x ^= x >> 16;
        return (x >> 8) / 16777216f;
    }

    static float Weyl(int id, double step, int salt)
    {
        double u = id * step + Roll(0, salt);
        return (float)(u - Math.Floor(u));
    }

    /// <summary>This Ork's flinch.</summary>
    public static MusouFlinchVariant Flinch(int id)
    {
        float strength = StrengthMin + (StrengthMax - StrengthMin) * Weyl(id, StrengthStep, 11);
        float duration = DurationMin + (DurationMax - DurationMin) * Weyl(id, DurationStep, 12);
        float delay = DelayMax * Weyl(id, DelayStep, 13);
        float pick = Roll(id, 14);
        var mode = pick < SideShare * 0.5f ? MusouFlinchMode.SideLeft
            : pick < SideShare ? MusouFlinchMode.SideRight
            : pick < SideShare + BackAndForthShare ? MusouFlinchMode.BackAndForth
            : MusouFlinchMode.Away;
        return new MusouFlinchVariant(strength, duration, delay, mode);
    }

    /// <summary>The direction this Ork's flinch starts in: <paramref name="away"/> (a unit direction away from the hit) for most, turned a quarter to a side for the side flinchers.</summary>
    public static Vector2 FlinchDirection(MusouFlinchVariant v, Vector2 away)
    {
        if (v.Mode is not (MusouFlinchMode.SideLeft or MusouFlinchMode.SideRight)) return away;
        float a = SideDegrees * MathF.PI / 180f * (v.Mode == MusouFlinchMode.SideLeft ? 1f : -1f), c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(away.X * c - away.Y * s, away.X * s + away.Y * c);
    }

    /// <summary>The seconds a flinch of this variant lasts after its delay.</summary>
    public static float FlinchSeconds(MusouFlinchVariant v) => MusouOrkFlinchRules.Seconds * v.Duration;

    /// <summary>The share of the kick's displacement at <paramref name="age"/> seconds after the kick (signed): the envelope after the variant's delay, stretched by its duration, and for a back-and-forth flinch the rebound the other way.</summary>
    public static float FlinchShare(MusouFlinchVariant v, float age)
    {
        float t = (age - v.Delay) / v.Duration;   // seconds on the unvaried clock
        if (t <= 0f) return 0f;
        float share = MusouOrkFlinchRules.Envelope(t);
        if (v.Mode == MusouFlinchMode.BackAndForth)
        {
            float n = t / MusouOrkFlinchRules.Seconds;
            if (n > ReboundStart && n < ReboundEnd) share -= ReboundDepth * MathF.Sin(MathF.PI * (n - ReboundStart) / (ReboundEnd - ReboundStart));
        }
        return share;
    }

    // ---- the death throw
    /// <summary>The longest an Ork's throw is held back (s): the owner's "up to 100 ms" (the crowd's own death hand-out waits at most the same, <c>MusouDeathRules.MaxWaitSeconds</c>).</summary>
    public const float ThrowDelayMax = 0.1f;
    /// <summary>The share of the throw's impulse an Ork gets: 1 plus or minus this (30 %; ours).</summary>
    public const float ThrowImpulseSpread = 0.3f;
    /// <summary>How far the throw's flat direction turns from away from the blast, each way (degrees): about 10 % of a quarter turn beyond what <c>MusouGoreRules.ThrowSpreadDegrees</c> already gives (ours).</summary>
    public const float ThrowTurnDegrees = 5f;

    /// <summary>The seconds this Ork's throw waits after the blast's other deaths (0 to <see cref="ThrowDelayMax"/>).</summary>
    public static float ThrowDelay(int id) => ThrowDelayMax * Roll(id, 21);

    /// <summary>The factor on this Ork's throw velocity (1 +/- <see cref="ThrowImpulseSpread"/>).</summary>
    public static float ThrowImpulse(int id) => 1f + ThrowImpulseSpread * (Roll(id, 22) * 2f - 1f);

    /// <summary>The turn (radians, about the vertical) of this Ork's throw from straight away from the blast (+/- <see cref="ThrowTurnDegrees"/>).</summary>
    public static float ThrowTurn(int id) => ThrowTurnDegrees * MathF.PI / 180f * (Roll(id, 23) * 2f - 1f);

    /// <summary>The throw velocity of this Ork: <paramref name="velocity"/> turned about the vertical and scaled by its impulse; the up part is scaled too, the direction stays near the blast's.</summary>
    public static Vector3 Throw(int id, Vector3 velocity)
    {
        float turn = ThrowTurn(id), c = MathF.Cos(turn), s = MathF.Sin(turn), k = ThrowImpulse(id);
        return new Vector3((velocity.X * c - velocity.Y * s) * k, (velocity.X * s + velocity.Y * c) * k, velocity.Z * k);
    }

    // ---- the death clip
    /// <summary>How many death clips an Ork's rig has to choose from at most (the rig's own, see the look's clips).</summary>
    public const int DeathClips = 3;

    /// <summary>Which of the <paramref name="available"/> death clips (the first is the usual) this Ork dies in: repeatable from its id, every clip equally likely.</summary>
    public static int DeathClip(int id, int available) => available <= 1 ? 0 : Math.Min(available - 1, (int)(Roll(id, 31) * available));
}
