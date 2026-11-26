using SmSim.Extras.Executions;
using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Executions X4b, Warfront on Chaos (owner 2026-11-25 "All with originals"; 2026-11-26 "Chosen can be executed", "Lord finisher at low health", "Finishers run into the duel"). A Chaos battle plays the
/// same press, hold, kill frame, reward and cooldown as an Orks battle (<c>World.ExecutionsOrks.cs</c>); only the kind of each enemy type byte differs: Warfront's roster key of the byte (Chaos C2's
/// aliasing, asked through <see cref="WarfrontChaosUnit"/>) through <see cref="ExecutionTargets.ChaosRun"/> (<see cref="ExecutionKindMap.Chaos"/>). The Chaos finale's duel (C5) is asked through
/// <see cref="WarfrontDuelGate"/>: from the snapshot to the end of the relay nobody executes but the duelist, and he only the Lord while the duel's gate stands (his health under a tenth); that
/// finisher's kill frame is the Lord's death through the normal damage path, so the duel's own win path ends it. Both hooks are Warfront's (pinned in <c>WarfrontBoundaryTests</c>); without Warfront
/// compiled both are no-ops and a battle is never Chaos (<c>WarfrontChaosArmy</c>), so nothing here then changes a battle.
/// </summary>
public sealed partial class World
{
    ExecutionKindMap? _executionChaosKinds, _executionChaosKindsLord;

    /// <summary>The kind map of the battle under way: a Chaos battle's (the Lord's key mapped only when <paramref name="lord"/>), else the Orks'.</summary>
    ExecutionKindMap ExecutionsKinds(bool lord)
    {
        if (!ExecutionsChaosStage) return ExecutionKindMap.Orks;
        if (lord) return _executionChaosKindsLord ??= ExecutionKindMap.Chaos(ExecutionsChaosUnitKey, lord: true);
        return _executionChaosKinds ??= ExecutionKindMap.Chaos(ExecutionsChaosUnitKey);
    }

    /// <summary>The roster key of a Chaos battle's enemy of <paramref name="type"/> (Warfront's; null without Warfront or for an Ork-only kind).</summary>
    string? ExecutionsChaosUnitKey(MusouEnemyType type)
    {
        string? key = null;
        WarfrontChaosUnit(type, ref key);   // WARFRONT-HOOK: the Chaos roster's key of the type byte (Extras/Warfront/Chaos/Roster/World.WarfrontChaosArmy.cs)
        return key;
    }

    /// <summary>The finale duel's gate for <paramref name="executor"/>: whether the duel holds the battle's presses now, and the Lord he may execute now (0: none).</summary>
    (bool Duel, ushort Lord) ExecutionsDuelGate(int executor)
    {
        bool duel = false;
        ushort lord = 0;
        WarfrontDuelGate(executor, ref duel, ref lord);   // WARFRONT-HOOK: the Chaos finale (Extras/Warfront/Chaos/Finale/World.WarfrontChaosFinale.cs)
        return (duel, lord);
    }

    /// <summary>Hook for Warfront (Chaos C2's roster): the roster key of an enemy of <paramref name="type"/> in a Chaos battle (<c>WarfrontChaosRoster.ExecutionKey</c>: "cultist", "chosen", "chaos_lord" ...; null: an Ork-only kind).</summary>
    partial void WarfrontChaosUnit(MusouEnemyType type, ref string? key);

    /// <summary>
    /// Hook for Warfront (Chaos C5's finale): <paramref name="duel"/> when the duel relay holds the battle under way (from the snapshot to its end), and <paramref name="lord"/> the Lord's id when
    /// <paramref name="executor"/> is the duelist on the plaza and the Lord is alive under the duel's execution gate (his health under a tenth of his maximum); else 0.
    /// </summary>
    partial void WarfrontDuelGate(int executor, ref bool duel, ref ushort lord);
}
