namespace SmSim.Extras.Warfront.Progression;

/// <summary>The factors applied to ONE Ork at the moment it spawns.</summary>
public readonly record struct WarfrontOrkScale(float Health, float Damage)
{
    public static readonly WarfrontOrkScale One = new(1f, 1f);
}

/// <summary>
/// Ork scaling (plan section 5; owner brief and answer 4): off the highest player's level, HP x (1 + 0.08 (L-1)), damage x (1 + 0.05 (L-1)), then HP x (1 + 0.20 (humans-1)).
/// Fixed at spawn: the battle's spawn hooks multiply an Ork's health once and remember its damage factor in W3's per-Ork store (<c>WarfrontBattleRules.DamageScale</c>); nothing rewrites a living Ork. In a battle the level part is
/// <see cref="LevelScale"/> (read through <c>WarfrontBattleRules.OrkLevelScale</c>) and the humans' part stays W3's (<c>WarfrontBattleRules.PlayerScale</c>); <see cref="For"/> is both together (the tuning table).
/// </summary>
public static class WarfrontOrkScaling
{
    /// <summary>The level part alone: health x (1 + 0.08 (L-1)) and damage x (1 + 0.05 (L-1)), times the base knobs (level 1: exactly 1, 1).</summary>
    public static WarfrontOrkScale LevelScale(int highestLevel)
    {
        int l = Math.Max(1, highestLevel);
        return new((1f + WarfrontProgressionRules.OrkHealthPerLevel * (l - 1)) * WarfrontProgressionRules.OrkHealthBase,
            (1f + WarfrontProgressionRules.OrkDamagePerLevel * (l - 1)) * WarfrontProgressionRules.OrkDamageBase);
    }

    public static WarfrontOrkScale For(int highestLevel, int humans)
    {
        int h = Math.Max(1, humans);
        var level = LevelScale(highestLevel);
        return new(level.Health * (1f + WarfrontProgressionRules.OrkHealthPerExtraHuman * (h - 1)), level.Damage);
    }
}
