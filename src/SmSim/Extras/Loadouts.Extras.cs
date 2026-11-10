using SmSim.Extras.Musou.Arsenal;

namespace SmSim;

/// <summary>
/// The loadout rules' added-content part (docs/added-content.md): the one implementation of <see cref="ExtrasWieldsHammer"/>, a dispatcher that asks each added content under its own flag.
/// Musou: its arsenal's melee weapons (<see cref="ArsenalRules.WieldsHammer"/>; W3's power spear takes the hammer's rules). No other profile carries those names.
/// </summary>
public static partial class Loadouts
{
    static partial void ExtrasWieldsHammer(SimConfig c, string wargear, ref bool hammer)
    {
        if (c.Extras.HasFlag(ContentExtras.Musou) && ArsenalRules.WieldsHammer(wargear)) hammer = true;
    }
}
