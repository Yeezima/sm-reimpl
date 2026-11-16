namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront (docs/added-content.md section 11): the persistent campaign on Musou Mode's battle. The one version of its rules, simulation and wire sections;
/// hashed into the config hash only under <see cref="ContentExtras.Warfront"/> (<c>SimConfig.ExtrasHash</c>), so raising it never touches another profile.
/// </summary>
public static class WarfrontRules
{
    /// <summary>1: the switch and the skeleton (slice W1); no rules yet.</summary>
    public const uint Version = 1;
}
