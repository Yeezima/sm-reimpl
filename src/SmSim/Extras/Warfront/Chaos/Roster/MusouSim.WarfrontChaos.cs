using System.Numerics;
using SmSim.Extras.Warfront.Chaos;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Chaos army on the enemy pool (Chaos Warfront C2): the kind map at the spawn chokepoint, the damage and stagger gate, and the breach's Daemon spawn. Every hook returns at once unless the run's
/// Warfront rules carry the Chaos roster (<see cref="WarfrontChaosRoster"/>; an Orks stage never does).
/// </summary>
public sealed partial class MusouSim
{
    WarfrontChaosRoster? ChaosRoster => Warfront?.ChaosRoster;

    partial void WarfrontKind(ref MusouEnemyType type, ref float health)
    {
        if (ChaosRoster is not { } chaos) return;
        var to = WarfrontChaosRoster.Alias(type, chaos.DaemonPending);
        chaos.DaemonPending = false;
        if (to == type) return;
        if (to == WarfrontChaosRoster.Refused) { chaos.Refusals++; type = to; return; }   // outside the table: Spawn returns 0
        // the caller asked for the health of the kind it named (its row in the Chaos table); the unit that comes has its own, with the same scale on it
        float from = Rules.Of(type).Health, into = Rules.Of(to).Health;
        if (from > 0f && float.IsFinite(into)) health *= into / from;
        chaos.Substituted++;
        type = to;
    }

    partial void WarfrontDamageGate(ushort enemy, int attacker, MusouDeathKind cause, ref float amount, ref bool barred)
    {
        if (ChaosRoster is { } chaos && !chaos.Admits(enemy, attacker)) barred = true;
    }

    partial void WarfrontStaggerGate(ushort enemy, int attacker, ref bool barred)
    {
        if (ChaosRoster is { } chaos && !chaos.Admits(enemy, attacker)) barred = true;
    }

    /// <summary>
    /// A warp breach's Daemon (C4): <see cref="Spawn"/> of the Gretchin row as the Daemon (Bloodletter) it stands for in a Chaos battle; 0 outside a Chaos battle (no Daemon there), when the pool
    /// is full, or for a value that is not finite. The only door through which the Gretchin row spawns in a Chaos battle (any other Gretchin comes as a Cultist).
    /// </summary>
    public ushort SpawnDaemon(Vector3 position, float yaw, float health, int node = -1, bool keepYaw = false)
    {
        if (ChaosRoster is not { } chaos) return 0;
        chaos.DaemonPending = true;
        ushort id = Spawn(MusouEnemyType.Gretchin, position, yaw, health, node, keepYaw);
        chaos.DaemonPending = false;
        return id;
    }
}
