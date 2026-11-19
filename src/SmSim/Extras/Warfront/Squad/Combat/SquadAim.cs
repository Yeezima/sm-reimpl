namespace SmSim.Extras.Warfront.Squad;

/// <summary>
/// A retainer's aim (Warfront retainer AI, slice S1; owner 2026-11-19): the hit chance of a ranged shot is the weapon's own chance (its spread) times the aim's chance of landing on the body, worked out as the
/// Classic PvP bots do. The formula is a copy of <c>SmNet.BotAimModel</c> (SmSim cannot reference SmNet; <c>SquadAimParityTests</c> pins the two together): for an accuracy <c>a</c> the per-axis error sigma is
/// <c>r / 15 / sqrt(-2 ln(1 - a))</c> (r = <see cref="BodyRadius"/>, 15 m = <see cref="ReferenceDistance"/>), the error angle shrinks with the distance (<see cref="DistanceScale"/>), and a shot at distance d lands with chance
/// <c>1 - exp(-(r/d)^2 / 2 sigma^2)</c>. A retainer is stronger than a Classic bot: its misses are <see cref="MissShare"/> (40 %, 60 % fewer) of the Classic bot's, so its accuracy is 0.988 against Classic's 0.97.
/// Melee blows keep the weapon's own chance (a bot's aim error is a ranged one). The roll stays the sim's own deterministic hash.
/// </summary>
public static class SquadAim
{
    /// <summary>The Classic bots' default accuracy (<c>NetworkView.DefaultBotAccuracy</c>, client/NetworkView.BotAim.cs) [owner, 2026-11-19: "like the Classic bots"].</summary>
    public const float ClassicAccuracy = 0.97f;
    /// <summary>The share of a Classic bot's misses a retainer makes: 0.40, 60 % fewer [owner, 2026-11-19].</summary>
    public const float MissShare = 0.40f;
    /// <summary>A retainer's aim accuracy: 1 - (1 - <see cref="ClassicAccuracy"/>) x <see cref="MissShare"/> = 0.988.</summary>
    public const float RetainerAccuracy = 1f - (1f - ClassicAccuracy) * MissShare;

    /// <summary>The BotAimModel constants (copied): the distance at which the accuracy is the hit fraction (m), the body radius (m), and the limits of the distance scale.</summary>
    public const float ReferenceDistance = 15f, BodyRadius = 0.5f, MinScaleDistance = 3f, MinScale = 0.1f, MaxScale = 4f;
    const float Perfect = 0.999f, Floor = 0.01f;

    /// <summary>The per-axis sigma (radians) at the reference distance for <paramref name="accuracy"/> (BotAimModel.SigmaFor); 0 for a perfect aim.</summary>
    public static float SigmaFor(float accuracy)
    {
        if (!float.IsFinite(accuracy) || accuracy >= Perfect) return 0f;
        float a = Math.Clamp(accuracy, Floor, Perfect);
        return BodyRadius / ReferenceDistance / MathF.Sqrt(-2f * MathF.Log(1f - a));
    }

    /// <summary>The factor on the error angle at <paramref name="distance"/> metres (BotAimModel.DistanceScale).</summary>
    public static float DistanceScale(float distance) => Math.Clamp(ReferenceDistance / Math.Max(distance, MinScaleDistance), MinScale, MaxScale);

    /// <summary>The aim's chance of landing on the body at <paramref name="distance"/> for an accuracy: BotAimModel.HitChance of the distance-scaled sigma.</summary>
    public static float AimChance(float accuracy, float distance)
    {
        float sigma = SigmaFor(accuracy) * DistanceScale(distance);
        if (sigma <= 0f) return 1f;
        float theta = BodyRadius / Math.Max(distance, 0.1f);
        return 1f - MathF.Exp(-theta * theta / (2f * sigma * sigma));
    }

    /// <summary>A retainer's aim chance at <paramref name="distance"/>.</summary>
    public static float RetainerAimChance(float distance) => AimChance(RetainerAccuracy, distance);

    /// <summary>The hit chance of one round: the weapon's chance, times the retainer's aim chance for a ranged weapon (a blow keeps the weapon's own chance).</summary>
    public static float HitChance(float weaponHitChance, bool melee, float distance) => melee ? weaponHitChance : weaponHitChance * RetainerAimChance(distance);
}
