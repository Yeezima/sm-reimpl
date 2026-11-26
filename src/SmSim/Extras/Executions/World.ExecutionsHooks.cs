namespace SmSim;

/// <summary>
/// The world's executions hooks (executions X1; docs/added-content.md section 13): the one implementation of each core <c>ExtrasExecution*</c> / <c>ExtrasReactionBlocked</c> hook, gated here on
/// <see cref="ContentExtras.Executions"/> and passed on to an <c>Executions*</c> partial that X3 (the state machine and Classic, <c>World.Executions.cs</c>, <c>World.ExecutionsClassic.cs</c>) and
/// X4 (Warfront) fill. Without the flag every hook returns at once; without an implementation every <c>Executions*</c> call compiles away, so X1 alone changes nothing.
/// </summary>
public sealed partial class World
{
    bool ExecutionsOn => Config.Extras.HasFlag(ContentExtras.Executions);

    partial void ExtrasExecutionInput(int id, ref PlayerInput input)
    {
        if (!ExecutionsOn) return;
        ExecutionsInput(id, ref input);   // EXECUTIONS-HOOK
    }

    partial void ExtrasExecutionPin(int id, PlayerState before, ref PlayerState next)
    {
        if (!ExecutionsOn) return;
        ExecutionsPin(id, before, ref next);   // EXECUTIONS-HOOK
    }

    partial void ExtrasExecutionStep()
    {
        if (!ExecutionsOn) return;
        ExecutionsStep();   // EXECUTIONS-HOOK
    }

    partial void ExtrasExecutionDamage(int target, ref float scale)
    {
        if (!ExecutionsOn) return;
        ExecutionsDamageTaken(target, ref scale);   // EXECUTIONS-HOOK
    }

    partial void ExtrasReactionBlocked(int target, ref bool blocked)
    {
        if (!ExecutionsOn) return;
        ExecutionsReactionBlocked(target, ref blocked);   // EXECUTIONS-HOOK
    }

    /// <summary>X3: reads player <paramref name="id"/>'s Execute press edge (its view tick through <c>ExecutionRules.PressTick</c>) and replaces a locked executor's input (<c>MatchFlow.Locked</c>, view kept).</summary>
    partial void ExecutionsInput(int id, ref PlayerInput input);
    /// <summary>X3: pins a locked executor's <paramref name="next"/> (position, yaw, velocity) to the alignment origin plus the clip's root track.</summary>
    partial void ExecutionsPin(int id, PlayerState before, ref PlayerState next);
    /// <summary>X3: the state machine's tick (requests, validation, contact and kill events, ends) and the Classic offers of this tick's hostile melee kills.</summary>
    partial void ExecutionsStep();
    /// <summary>X4 (Warfront): 0 for an executor during the move; Classic leaves <paramref name="scale"/> alone.</summary>
    partial void ExecutionsDamageTaken(int target, ref float scale);
    /// <summary>X3: true for a locked executor (nonlethal hits do not interrupt; a lethal one ends it through the normal death).</summary>
    partial void ExecutionsReactionBlocked(int target, ref bool blocked);
}
