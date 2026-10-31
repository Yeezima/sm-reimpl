using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// The config hash's added-content block (docs/added-content.md): the one implementation of <see cref="ExtrasHash"/>, a dispatcher that writes each added
/// content's own block only under its own flag. Nothing is written for <see cref="ContentExtras.Orks"/>, <see cref="ContentExtras.Gore"/> or
/// <see cref="ContentExtras.LoyalistBarks"/> (they ride on <see cref="ContentExtras.WireVersion"/>), so the extended profile hashes as before Musou existed
/// (pinned in <c>MusouIsolationTests</c>).
/// </summary>
public sealed partial record SimConfig
{
    partial void ExtrasHash(BinaryWriter w)
    {
        // Musou: its own version (simulation and wire sections), so a Musou-only change never touches ContentExtras.WireVersion or the extended hash.
        // The Three-Front War is Musou's war: its rules' version sits in the same block.
        if (Extras.HasFlag(ContentExtras.Musou)) { w.Write("musou"); w.Write(MusouSim.Version); w.Write(SmSim.Extras.Musou.Campaign.TfwRules.Version); }
    }
}
