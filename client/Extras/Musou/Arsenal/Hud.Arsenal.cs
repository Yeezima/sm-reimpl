using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;

namespace SmClient;

/// <summary>
/// W0b, Musou's arsenal on the HUD: the one implementation of <see cref="ExtrasReticle"/> and <see cref="ExtrasWeaponBox"/>. Under Musou's content only (<see cref="Hud.Content"/>, the match's
/// profile), the autocannon shows the movie's own autocannon reticle and its ammunition display (<c>Arsenal.Reticle</c>, <c>Arsenal.WeaponBox</c>); every other weapon keeps the rules' choice.
/// </summary>
public sealed partial class Hud
{
    partial void ExtrasReticle(string wargear, ref ReticleSpec? spec)
    {
        if (!Content.HasFlag(ContentExtras.Musou) || Arsenal.Reticle(wargear) is not { } own) return;
        spec = own;
    }

    partial void ExtrasWeaponBox(string wargear, bool chaos, ref WeaponBoxSpec? spec)
    {
        if (!Content.HasFlag(ContentExtras.Musou) || Arsenal.WeaponBox(wargear, chaos) is not { } own) return;
        spec = own;
    }
}
