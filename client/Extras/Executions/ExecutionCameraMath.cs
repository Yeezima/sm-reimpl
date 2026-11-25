using System;
using System.Numerics;

namespace SmClient;

/// <summary>
/// Executions X6: which parts of a player the overshield shell covers (owner 2026-11-25: every part of the model except the weapons: the jump pack, the iron halo and every armour piece;
/// not tabards, ornaments such as chains, cloth, leather belt pouches and similar objects). Engine-free, tested in <c>ExecutionPromptRuleTests</c>.
/// <para>How the install's data allows it (survey 2026-11-25, <c>--executions-x6-survey</c>): every MP armour piece is its own mesh (torso, arms, legs, shoulders, helmet, backpack) and the worn items
/// are separate attachments (<c>Worn_*</c>: the jump pack, the Devastator's pack); the materials are generic composites (<c>Standard_2</c>, <c>raptor_legr_realtime_composite</c>) that name no cloth,
/// leather or chain, and a piece's cloth or ornament shares its surface and texture with the armour around it. So the split is by mesh and surface name: a mesh or a surface whose name says
/// cloth, leather or ornament (<see cref="ExcludedWords"/>) is left out; everything else under the skeleton but the weapons is covered. Cloth or a chain modelled inside an armour piece's own
/// composite surface cannot be told apart in the data and is covered with it (a known limit).</para>
/// </summary>
public static class OvershieldCoverage
{
    /// <summary>Name parts that mark cloth, leather or ornaments (lower case; ours, from the owner's list). Includes every word of the armour-pieces paint mask's cloth list (<c>BakedArmourPaint.ClothWords</c>: cloth, cape, cloak, tabard, fur, scroll, parchment, purity, robe, pelt), so both features agree.</summary>
    public static readonly string[] ExcludedWords =
    [
        "tabard", "cloth", "robe", "cape", "cloak", "loincloth", "skirt", "sash", "banner", "flag", "ribbon", "tassel", "rope", "cord",
        "leather", "pouch", "belt", "strap", "holster", "satchel", "bag",
        "chain", "purity", "seal", "scroll", "parchment", "feather", "fur", "pelt", "trophy", "rosarius", "rosary", "ornament", "charm", "totem",
    ];

    /// <summary>Whether a mesh or surface named <paramref name="name"/> is cloth, leather or an ornament (not covered).</summary>
    public static bool Excluded(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name.ToLowerInvariant();
        foreach (var w in ExcludedWords) if (lower.Contains(w, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Whether a mesh or surface named <paramref name="name"/> is covered: an armour piece of the look (<paramref name="armourPiece"/>) always, whatever its name (X6 review X6-8: a pad or a
    /// helmet whose package name says seal, chain, trophy, fur, belt or cape is still armour); any other node (a worn item, an attachment) unless <see cref="Excluded"/>; never a weapon.
    /// </summary>
    public static bool Covered(string? name, bool armourPiece) => !(name is not null && Weapon(name)) && (armourPiece || !Excluded(name));

    /// <summary>Whether a node named <paramref name="name"/> under the skeleton is a weapon (never covered).</summary>
    public static bool Weapon(string name) => name.StartsWith("Weapon_", StringComparison.Ordinal);
}

/// <summary>
/// Executions X6: the overshield look's strength by how full the overshield is (owner 2026-11-25, decisions/2026-11-25-executions-x6-prompt-dot.md, picked from the 3 x 3 comparison grid:
/// full 0.6, half 0.6, low 1.0). It multiplies the shell's alpha and the armour tint's share, which already follow the fraction; engine-free, tested in <c>ExecutionPromptRuleTests</c>.
/// </summary>
public static class OvershieldLook
{
    /// <summary>The grid's fractions of the cap: half (0.5) and low (0.15).</summary>
    public const float HalfFraction = 0.5f, LowFraction = 0.15f;
    /// <summary>The owner's strengths: from full down to half, and at low and below.</summary>
    public const float HighStrength = 0.6f, LowStrength = 1.0f;

    /// <summary>0.6 from full down to half, rising smoothly (smoothstep) to 1.0 at the low fraction, 1.0 below it.</summary>
    public static float StrengthAt(float fraction)
    {
        float f = float.IsFinite(fraction) ? Math.Clamp(fraction, 0f, 1f) : 0f;
        if (f >= HalfFraction) return HighStrength;
        if (f <= LowFraction) return LowStrength;
        float t = (HalfFraction - f) / (HalfFraction - LowFraction);   // 0 at half, 1 at low
        t = t * t * (3f - 2f * t);
        return HighStrength + (LowStrength - HighStrength) * t;
    }
}

/// <summary>Executions X6: the camera's engine-free maths (tested in <c>ExecutionPromptRuleTests</c>).</summary>
public static class ExecutionCameraMath
{
    /// <summary>
    /// The view (sim yaw and pitch, radians) that looks along <paramref name="forward"/> (game space, Z up): the game's forward at yaw y is (-sin y, cos y), a positive pitch looks up. A forward with no
    /// horizontal part (straight up or down) keeps <paramref name="yawBefore"/>. The orbit's exit hands the view its heading with this.
    /// </summary>
    public static (float Yaw, float Pitch) ViewOf(Vector3 forward, float yawBefore)
    {
        if (!float.IsFinite(forward.X) || !float.IsFinite(forward.Y) || !float.IsFinite(forward.Z)) return (yawBefore, 0f);
        float flat = MathF.Sqrt(forward.X * forward.X + forward.Y * forward.Y);
        float yaw = flat > 1e-4f ? MathF.Atan2(-forward.X, forward.Y) : yawBefore;
        float pitch = MathF.Atan2(forward.Z, MathF.Max(flat, 1e-6f));
        return (yaw, pitch);
    }

    /// <summary>The share of the overshield's cap <paramref name="shield"/> holds above <paramref name="shieldMax"/> (0..1), the shell's and the tint's strength.</summary>
    public static float OvershieldFraction(float shield, float shieldMax)
    {
        float cap = SmSim.Extras.Executions.ExecutionRules.OvershieldCap(shieldMax);
        return cap > 0f && float.IsFinite(shield) ? Math.Clamp(SmSim.Extras.Executions.ExecutionRules.Overshield(shield, shieldMax) / cap, 0f, 1f) : 0f;
    }
}
