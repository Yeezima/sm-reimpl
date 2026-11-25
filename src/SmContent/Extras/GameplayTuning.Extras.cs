using SmContent.Extras.Autocannon;
using SmContent.Extras.Musou;
using SmContent.Extras.Musou.Arsenal;
using SmSim;
using SmSim.Extras.Musou;

namespace SmContent;

/// <summary>
/// The match tuning's added-content load (docs/added-content.md): the one implementation of <see cref="ExtrasLoad"/>, a dispatcher that reads each added
/// content's own tuning only under its own flag. The Ork is gated in <see cref="LoadForMatch"/> itself; gore and the loyalist barks read nothing here, so
/// the extended profile loads as before Musou existed. Musou: its tuning (<see cref="MusouTuning"/>) on <see cref="SimConfig.MusouRules"/>, and its classes
/// without a default primary (<see cref="MusouLoadout.Prepare"/>: the players spawn with the pistol alone), after its arsenal's items (<see cref="Arsenal.Append"/>). Classic PvP's autocannon
/// (<see cref="ContentExtras.Autocannon"/>, in the extended profile; never with Musou): <see cref="ClassicAutocannon.Append"/>.
/// </summary>
public sealed partial record GameplayTuning
{
    static partial void ExtrasLoad(ref SimConfig config, string gameDir, List<string> notes)
    {
        if (config.Extras.HasFlag(ContentExtras.Musou))
        {
            // The Librarian's restore switches (LibrarianVariant, SM_LIBRARIAN_OLD): read here, on the server and the clients alike, so a class change stays in the config hash.
            var librarian = LibrarianVariant.Parse(Environment.GetEnvironmentVariable(LibrarianVariant.EnvironmentVariable));
            if (librarian.Any) notes.Add($"musou: the Librarian's restore switches {librarian} ({LibrarianVariant.EnvironmentVariable})");
            config = Arsenal.Append(config, gameDir, notes);   // Musou's arsenal (docs/new-weapons-design.md, W0): its items before Prepare, so the Musou lists see them; the stubs add nothing
            config = DreadnoughtWeapons.Append(config, gameDir, notes);   // the Dreadnought's cannon and melta (D3, D4): after the arsenal (his cannon is the autocannon's numbers, the 2026-11-15 buff) and before Prepare, which gives him them
            config = MusouLoadout.Prepare(config with { MusouRules = MusouTuning.Load(gameDir, notes), LibrarianVariant = librarian });
        }
        else if (config.Extras.HasFlag(ContentExtras.Autocannon)) config = ClassicAutocannon.Append(config, gameDir, notes);   // Classic PvP's autocannon (owner 2026-11-11); Musou is never combined with it
        if (config.Extras.HasFlag(ContentExtras.Executions)) ExecutionsLoad(ref config, gameDir, notes);   // EXECUTIONS-HOOK: executions X2/X3 (the catalogue on the config), after every other load so it sees the final classes and weapons
    }

    /// <summary>Executions (docs/added-content.md section 13): the catalogue and its tuning on the match config, implemented in <c>Extras/Executions/</c> (X2, X3); only under the executions flag.</summary>
    static partial void ExecutionsLoad(ref SimConfig config, string gameDir, List<string> notes);
}
