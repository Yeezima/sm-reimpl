namespace SmSim.Extras.Musou;

/// <summary>Warfront's part of the enemy pool (slice W3): the health multiplier at spawn and each Ork's remembered damage multiplier. Returns at once without the Warfront object.</summary>
public sealed partial class MusouSim
{
    /// <summary>The run's Warfront rules (null: not a Warfront run).</summary>
    internal SmSim.Extras.Warfront.WarfrontBattleRules? Warfront { get; set; }

    partial void WarfrontSpawn(MusouEnemyType type, ref float health)
    {
        if (Warfront is not { } rules) return;
        var (hp, damage) = rules.OrkLevelScale();
        rules.PendingDamage = damage;
        float scale = rules.PlayerScaleNow * hp;
        if (scale != 1f) health *= scale;
    }

    partial void WarfrontSpawned(ushort id)
    {
        if (Warfront is not { } rules) return;
        rules.DamageScale[id] = rules.PendingDamage;
        rules.PendingDamage = 1f;
    }
}
