using SmSim.Extras.Executions;

namespace SmSim;

/// <summary>
/// Executions on the match config (executions X1; docs/added-content.md section 13): the tunable Classic window and the overshield hook. Everything here does nothing without
/// <see cref="ContentExtras.Executions"/>; the hash block is written by <see cref="ExecutionRules.Hash"/> from <c>SimConfig.Extras.cs</c> under the same flag.
/// </summary>
public sealed partial record SimConfig
{
    /// <summary>
    /// Classic PvP Extended: the finisher window after a lethal melee kill, in seconds (<see cref="ExecutionRules.ClassicWindowSeconds"/>, 0.8, ours), and the death pose's hold
    /// (<see cref="ExecutionRules.DeathPoseHoldSeconds"/>). Hashed in the executions block, so a server and its clients agree on it; read only under <see cref="ContentExtras.Executions"/>.
    /// </summary>
    public float ExecutionWindowSeconds { get; init; } = ExecutionRules.ClassicWindowSeconds;

    // executions X1: the overshield's no-decay rule (Combat.StepPlayer's regeneration line). X4 (Warfront) fills ExecutionsKeepsOvershield; until then nothing keeps armour above its maximum.
    partial void ExtrasKeepsOvershield(ref bool keeps)
    {
        if (!Extras.HasFlag(ContentExtras.Executions)) return;
        ExecutionsKeepsOvershield(ref keeps);   // EXECUTIONS-HOOK
    }

    /// <summary>Executions (X4, Warfront): sets <paramref name="keeps"/> when the match keeps an execution's overshield (Warfront only; never Classic PvP).</summary>
    partial void ExecutionsKeepsOvershield(ref bool keeps);
}
