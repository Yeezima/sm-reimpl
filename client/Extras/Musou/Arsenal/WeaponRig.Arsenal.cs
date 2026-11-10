using SmContent;
using SmSim;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// Musou's arsenal on the character (W0, the base; docs/new-weapons-design.md section 8, hook 4): the one implementation of <see cref="ExtrasWeaponModel"/>, a dispatcher to each item's own
/// model, which its milestone implements in its own file: W1 the autocannon (<c>turret_aa</c> at the chest prop, stowed at the bolter's marker), W2 the claw (built in code, always worn on the
/// right hand), W3 the spear (built in code). Asked only for a rig under Musou's content (<see cref="Content"/>: the match's or the customizer page's profile) and only for the arsenal's names
/// (<see cref="ArsenalRules.IsArsenal"/>). Until then nothing is drawn for them, as for any name without a model.
/// </summary>
public sealed partial class WeaponRig
{
    partial void ExtrasWeaponModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset, ref bool alwaysHeld)
    {
        if (!Content.HasFlag(ContentExtras.Musou) || !ArsenalRules.IsArsenal(wargear)) return;
        AutocannonModel(role, wargear, assets, ref attachment, ref asset);
        LightningClawModel(role, wargear, assets, ref attachment, ref asset, ref alwaysHeld);
        PowerSpearModel(role, wargear, assets, ref attachment, ref asset);
    }

    /// <summary>W1 (<c>Arsenal/Autocannon/</c>): the autocannon's model.</summary>
    partial void AutocannonModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset);

    /// <summary>W2 (<c>Arsenal/Claw/</c>): the claw's model, and <paramref name="alwaysHeld"/> (it is never put away).</summary>
    partial void LightningClawModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset, ref bool alwaysHeld);

    /// <summary>W3 (<c>Arsenal/Spear/</c>): the spear's model.</summary>
    partial void PowerSpearModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset);
}
