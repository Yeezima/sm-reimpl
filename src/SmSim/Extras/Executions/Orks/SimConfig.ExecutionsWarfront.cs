namespace SmSim;

/// <summary>
/// Executions X4 (Warfront): the overshield's no-decay rule. A Warfront match keeps the armour above its maximum that an execution's reward gave (<c>Combat.StepPlayer</c>'s regeneration line
/// through <see cref="KeepsOvershield"/>); damage alone takes it (<c>Combat.ApplyDamage</c>, armour first). Classic PvP Extended never gets an overshield, so it never keeps one.
/// </summary>
public sealed partial record SimConfig
{
    partial void ExecutionsKeepsOvershield(ref bool keeps)
    {
        if (Extras.HasFlag(ContentExtras.Warfront)) keeps = true;
    }
}
