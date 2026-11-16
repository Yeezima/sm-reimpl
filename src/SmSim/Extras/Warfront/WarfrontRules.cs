namespace SmSim.Extras.Warfront;

/// <summary>
/// Warfront (docs/added-content.md section 11): the persistent campaign on Musou Mode's battle. The one version of its rules, simulation and wire sections;
/// hashed into the config hash only under <see cref="ContentExtras.Warfront"/> (<c>SimConfig.ExtrasHash</c>), so raising it never touches another profile.
/// </summary>
public static class WarfrontRules
{
    /// <summary>2: the node battle rules and hook seams (slice W3: D as a capture point, no Warboss, the fallen human's spectator time and wipe rule, +20 % Ork health per extra human). 1: the switch and the skeleton (slice W1).</summary>
    public const uint Version = 2;
}
