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

    /// <summary>
    /// Each class's default primary as <see cref="MusouLoadout.Prepare"/> found it, by class index (-1: none): the 5-wave run takes the defaults away, Musou Mode gives a player its full loadout (owner's item 25),
    /// so it reads them here. Derived from the classes, which the hash covers; not written into it.
    /// </summary>
    public IReadOnlyList<int> MusouDefaultPrimaries { get; init; } = [];
}
