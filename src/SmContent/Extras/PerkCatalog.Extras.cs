using SmContent.Extras.Autocannon;
using SmContent.Extras.Musou.Arsenal;
using SmSim;

namespace SmContent;

/// <summary>
/// The customizer catalogue's added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasItems"/>, a dispatcher that adds each added content's own items only
/// for a content profile with that content. Musou: its arsenal's rows (<see cref="Arsenal.Catalog"/>, W1 to W4); Classic PvP's autocannon (<see cref="ContentExtras.Autocannon"/>): its row
/// for both heavy classes (<see cref="ClassicAutocannon.Catalog"/>); every other profile gets the catalogue as the data makes it.
/// </summary>
public sealed partial class PerkCatalog
{
    partial void ExtrasItems(ContentExtras content, ref PerkCatalog catalog)
    {
        if (content.HasFlag(ContentExtras.Musou)) catalog = Arsenal.Catalog(catalog);
        else if (content.HasFlag(ContentExtras.Autocannon)) catalog = ClassicAutocannon.Catalog(catalog);   // Classic PvP's autocannon: the Devastator's and the Havoc's row (owner 2026-11-11)
    }
}
