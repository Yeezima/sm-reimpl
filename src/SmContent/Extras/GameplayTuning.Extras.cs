using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>
/// The match tuning's added-content load (docs/added-content.md): the one implementation of <see cref="ExtrasLoad"/>, a dispatcher that reads each added
/// content's own tuning only under its own flag. The Ork is gated in <see cref="LoadForMatch"/> itself; gore and the loyalist barks read nothing here, so
/// the extended profile loads as before Musou existed. Musou: its tuning (<see cref="MusouTuning"/>) on <see cref="SimConfig.MusouRules"/>, and its classes
/// without a default primary (<see cref="MusouLoadout.Prepare"/>: the players spawn with the pistol alone).
/// </summary>
public sealed partial record GameplayTuning
{
    static partial void ExtrasLoad(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Extras.HasFlag(ContentExtras.Musou))
            config = MusouLoadout.Prepare(config with { MusouRules = MusouTuning.Load(gameDir, notes) });
    }
}
