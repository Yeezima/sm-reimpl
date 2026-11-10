using SmContent;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// Musou's arsenal in the networked view (W0, the base; docs/new-weapons-design.md section 8, hook 4): the one implementation of <see cref="ExtrasThrownVisual"/>, a dispatcher to the thrown
/// item's own model, which W4 implements in its own file (<c>Arsenal/Charge/</c>: the Remote Detonators drawn as the melta bomb, not the blueprint's Ork stikkbomb). Only the arsenal's names are
/// asked, and only Musou's config carries them. Until then the item is drawn with its blueprint's model, as before.
/// </summary>
public partial class NetworkView
{
    partial void ExtrasThrownVisual(ThrownSpec item, ref ProjectileVisual? visual)
    {
        if (!ArsenalRules.IsArsenal(item.Name)) return;
        RemoteDetonatorsVisual(item, ref visual);
    }

    /// <summary>W4 (<c>Arsenal/Charge/</c>): the charge's model in flight and at rest.</summary>
    partial void RemoteDetonatorsVisual(ThrownSpec item, ref ProjectileVisual? visual);
}
