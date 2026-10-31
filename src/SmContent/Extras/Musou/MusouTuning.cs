using SmSim;
using SmSim.Extras.Musou;

namespace SmContent.Extras.Musou;

/// <summary>
/// Added content "Musou": the mode's tuning for a match (MU1; design section B). It reads nothing from the install yet: the values are constants
/// (<see cref="MusouRules.Default"/>: the Boyz' 85 hit points as the design took them from the data, the rest the design's numbers or ours, kind: ours),
/// covered in the config hash by <see cref="MusouSim.Version"/>. Called from <c>GameplayTuning.ExtrasLoad</c> only under <see cref="ContentExtras.Musou"/>.
/// </summary>
public static class MusouTuning
{
    /// <summary>The tuning for a Musou match from <paramref name="gameDir"/> (unused today), with a note for the startup log.</summary>
    public static MusouRules Load(string gameDir, List<string> notes)
    {
        _ = gameDir;
        var rules = MusouRules.Default;
        notes.Add($"musou: {rules.Waves.Count} waves (" + string.Join(", ", rules.Waves.Select(w => $"{w.Total}/{w.MaxAlive}{(w.Nobs > 0 ? $"+{w.Nobs} Nobs" : "")}")) +
            $" at one player, +{rules.ScalePerExtraPlayer:0.##} a further player, cap {rules.HardCap}), {rules.BreakSeconds:0.#} s breaks, {rules.Gates} gates, " +
            $"lives {rules.Lives} + {rules.LivesPerExtraPlayer} a further player, then the Warboss ({rules.BossHealth:0} health times the scale); enemy numbers ours (constants, MU1)");
        return rules;
    }
}
