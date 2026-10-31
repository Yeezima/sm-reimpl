using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou" in the match's config: its tuning (MU1; <c>GameplayTuning.ExtrasLoad</c> sets it under <see cref="ContentExtras.Musou"/> only).
/// Null in every other profile and in a build without added content (this file is not compiled there). Not written into the hash itself: its values are
/// constants covered by <see cref="MusouSim.Version"/>.
/// </summary>
public sealed partial record SimConfig
{
    /// <summary>The Musou tuning (null: <see cref="MusouRules.Default"/> when a Musou match runs).</summary>
    public MusouRules? MusouRules { get; init; }
}
