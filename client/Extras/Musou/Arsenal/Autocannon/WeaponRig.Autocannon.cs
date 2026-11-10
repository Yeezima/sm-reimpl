using SmContent;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>
/// W1, the autocannon's model (docs/new-weapons-design.md section 4, Presentation): the install's <c>turret_aa</c> (every autocannon wargear names it; 1,883 triangles, about 2.8 m long, a
/// <c>RIG_Prop_M_Chest</c> locator at its grip), held and stowed exactly as the heavy bolter: the heavy bolter's attachment with this model in its place (its wield mount names the chest prop, its stow
/// mount the bolter marker, both by node name, so the mounts fit the new model). Original asset loaded; the placement is a stand-in for the Chaos file's own mounts (supported by evidence, to check in a
/// still). Nothing without the heavy bolter in the catalogue (it is in every multiplayer class set).
/// </summary>
public sealed partial class WeaponRig
{
    /// <summary>The model package and visual the autocannon wears (the install's, <c>data/packages/turret_aa.oppc</c>).</summary>
    internal const string AutocannonPackage = "turret_aa", AutocannonVisual = "turret_aa";

    partial void AutocannonModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset)
    {
        if (!wargear.Equals(ArsenalRules.Autocannon, System.StringComparison.OrdinalIgnoreCase)) return;
        attachment = AutocannonAttachment(assets.Catalogue.Held(SmContent.Extras.Musou.Arsenal.Arsenal.HeavyBolterFile) ?? assets.Catalogue.Held("csm_mp_exotic_heavy_bolter"));
    }

    /// <summary>The heavy bolter's attachment with the autocannon's model, without the heavy bolter's visual upgrades; null without a heavy bolter.</summary>
    internal static WeaponAttachment? AutocannonAttachment(WeaponAttachment? heavy) => heavy is null ? null : heavy with
    {
        Wargear = ArsenalRules.Autocannon, Name = "auto_cannon", VisualPackage = AutocannonPackage, VisualName = AutocannonVisual,
        Upgrades = [], HiddenByDefault = new System.Collections.Generic.HashSet<string>(),
    };
}
