using SmContent;
using SmContent.Extras.Musou.Arsenal;
using SmSim;

namespace SmClient;

/// <summary>
/// W0b, Musou's arsenal on the HUD: the one implementation of <see cref="ExtrasReticle"/> and <see cref="ExtrasWeaponBox"/>. Under a content that carries it (<see cref="Hud.Content"/>, the match's
/// profile: Musou's, or Classic PvP's <see cref="ContentExtras.Autocannon"/>), the autocannon shows the movie's own autocannon reticle and its ammunition display (<c>Arsenal.Reticle</c>,
/// <c>Arsenal.WeaponBox</c>: in Musou Mode the endless sign, its clip never spent; in Classic PvP the rounds left); every other weapon keeps the rules' choice.
/// </summary>
public sealed partial class Hud
{
    partial void ExtrasReticle(string wargear, ref ReticleSpec? spec)
    {
        if (Arsenal.Reticle(Content, wargear) is not { } own) return;
        spec = own;
    }

    partial void ExtrasWeaponBox(string wargear, bool chaos, ref WeaponBoxSpec? spec)
    {
        if (Arsenal.WeaponBox(Content, wargear, chaos) is not { } own) return;
        spec = own;
    }
}
