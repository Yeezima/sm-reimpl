namespace SmSim.Extras.Musou;

/// <summary>Warfront's part of the enemy pool (slice W3): the health multiplier at spawn and each Ork's remembered damage multiplier. Returns at once without the Warfront object.</summary>
public sealed partial class MusouSim
{
    /// <summary>The run's Warfront rules (null: not a Warfront run).</summary>
    internal SmSim.Extras.Warfront.WarfrontBattleRules? Warfront { get; set; }

    partial void WarfrontSpawn(MusouEnemyType type, ref float health)
    {
        if (Warfront is not { } rules) return;
        rules.PendingDamage = 1f;
        if (!rules.InNode) return;   // the level's scale is a node battle's: not the hub's (the Firing Range's targets)
        var (hp, damage) = rules.OrkLevelScale();
        rules.PendingDamage = damage;
        float scale = rules.PlayerScaleNow * hp;
        if (scale != 1f) health *= scale;
    }

    partial void WarfrontSpawned(ushort id)
    {
        if (Warfront is not { } rules) return;
        rules.Progression.Assists.Clear(id);
        rules.DamageScale[id] = rules.PendingDamage;
        rules.PendingDamage = 1f;
    }

    /// <summary>The assists' record: the last two human attackers of the Ork and when (slice W3b).</summary>
    partial void WarfrontDamaged(ushort enemy, int attacker)
    {
        if (Warfront is not { } rules || attacker <= 0) return;
        rules.Retainers?.Hit(enemy, attacker, Tick);   // Phase B T2: a retainer's hit, for its assists
        if (!rules.Progression.TryKey(attacker, out _)) return;   // humans only (not the AI Marines)
        rules.Progression.Assists.Hit(enemy, attacker, Tick);
    }

    /// <summary>Owner 2026-11-25 (b): a Warfront retainer's melee stuns a boss as a human's (<see cref="SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.StunsBosses"/>).</summary>
    partial void WarfrontBossStunner(int attacker, ref bool aiMarine)
    {
        if (SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.StunsBosses && aiMarine && Warfront?.Retainers is { } retainers && retainers.IsRetainer(attacker)) aiMarine = false;
    }
}
