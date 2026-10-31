using SmContent.Extras.Musou;
using SmSim;

namespace SmContent;

/// <summary>
/// The match tuning's added-content load (docs/added-content.md): the one implementation of <see cref="ExtrasLoad"/>, a dispatcher that reads each added
/// content's own tuning only under its own flag. The Ork is gated in <see cref="LoadForMatch"/> itself; gore and the loyalist barks read nothing here, so
/// the extended profile loads as before Musou existed. Musou: its tuning (<see cref="MusouTuning"/>) on <see cref="SimConfig.MusouRules"/>.
/// </summary>
public sealed partial record GameplayTuning
{
    static partial void ExtrasLoad(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Extras.HasFlag(ContentExtras.Musou))
            config = config with { MusouRules = MusouTuning.Load(gameDir, notes) };
    }
}
