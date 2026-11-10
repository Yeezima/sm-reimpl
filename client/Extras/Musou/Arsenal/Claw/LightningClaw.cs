using SmContent;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>W2, the lightning claw's model hook (<see cref="WeaponRig.ExtrasWeaponModel"/>'s dispatcher, <c>client/Extras/Musou/Arsenal/WeaponRig.Arsenal.cs</c>): <see cref="ClawModel"/> on the power sword's grip.</summary>
public sealed partial class WeaponRig
{
    partial void LightningClawModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset, ref bool alwaysHeld)
    {
        if (role != HandRole.Melee || !wargear.Equals(ArsenalRules.LightningClaw, System.StringComparison.OrdinalIgnoreCase)) return;
        if (assets.Catalogue.Held(SmContent.Extras.Musou.Arsenal.Arsenal.ClawBase) is not { } sword || assets.Get(sword) is not { } swordAsset) return;   // the claw is held where the sword is
        if (ClawModel.For(swordAsset, sword) is not { } claw) return;
        attachment = sword with { Wargear = wargear, Name = "lightning_claw" };
        asset = claw;
        alwaysHeld = true;   // a claw cannot be put away (design section 5, ours)
    }
}

public partial class NetworkView
{
    /// <summary>W2: a claw strike is posed this frame: the blue crackle (the claw's own clips, once approved, drop in here as well: <c>ExtrasMeleeClip</c> leaves the sword's stand-in clip when this gives none).</summary>
    partial void LightningClawClip(string wargear, bool heavy, int step, ref string? clip, ref float rate)
    {
        if (!wargear.Equals(ArsenalRules.LightningClaw, System.StringComparison.OrdinalIgnoreCase)) return;
        ClawCrackle.Strike();
    }
}
