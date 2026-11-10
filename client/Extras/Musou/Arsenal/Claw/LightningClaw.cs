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
        if (ClawModel.For(swordAsset, sword, ForearmInModelRoot(sword, swordAsset)) is not { } claw) return;
        attachment = sword with { Wargear = wargear, Name = "lightning_claw" };
        asset = claw;
        alwaysHeld = true;   // a claw cannot be put away (design section 5, ours)
    }
}

public sealed partial class WeaponRig
{
    /// <summary>
    /// The direction from the elbow to the hand in the sword's model root space (the space the claw's mesh is built in), read from the rig's rest pose: the mount bone's ancestor named like a forearm
    /// gives the elbow, the mount's own transform (<c>Item.Wield</c>) carries the bone's frame into the model's. Null when the bones cannot be found (the claw then follows the sword's axes).
    /// </summary>
    System.Numerics.Vector3? ForearmInModelRoot(WeaponAttachment sword, WeaponAsset swordAsset)
    {
        if (sword.Wield is not { Bone: { } boneName } wield) return null;
        int bone = _skeleton.FindBone(boneName);
        if (bone < 0) return null;
        int elbow = -1;
        var chain = new System.Collections.Generic.List<string>();
        for (int a = _skeleton.GetBoneParent(bone), depth = 0; a >= 0 && depth < 5; a = _skeleton.GetBoneParent(a), depth++)
        {
            chain.Add(_skeleton.GetBoneName(a));
            if (elbow < 0 && _skeleton.GetBoneName(a).Contains("forearm", System.StringComparison.OrdinalIgnoreCase)) elbow = a;
        }
        if (elbow < 0) elbow = _skeleton.GetBoneParent(_skeleton.GetBoneParent(bone));   // the hand's parent's parent
        if (elbow < 0) return null;
        var rest = _skeleton.GetBoneGlobalRest(bone);
        var towardElbow = rest.Basis.Inverse() * (_skeleton.GetBoneGlobalRest(elbow).Origin - rest.Origin);
        if (towardElbow.LengthSquared() < 1e-8f) return null;
        var wieldTransform = MarineAssets.ToGodot(swordAsset.Model.RootToBone(wield));
        var root = wieldTransform.Basis.Inverse() * (-towardElbow.Normalized());
        Godot.GD.Print($"lightning claw: forearm from bone '{boneName}' up {string.Join(" > ", chain)} (elbow '{_skeleton.GetBoneName(elbow)}'), direction in the model's root {root}");
        return new System.Numerics.Vector3(root.X, root.Y, root.Z);
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
