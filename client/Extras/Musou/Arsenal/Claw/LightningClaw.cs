using System.Collections.Generic;
using SmSim;
using SmContent;
using SmSim.Extras.Musou.Arsenal;

namespace SmClient;

/// <summary>W2, the lightning claw's model hook (<see cref="WeaponRig.ExtrasWeaponModel"/>'s dispatcher, <c>client/Extras/Musou/Arsenal/WeaponRig.Arsenal.cs</c>): <see cref="ClawModel"/> on the power sword's grip.</summary>
public sealed partial class WeaponRig
{
    internal static ClawHandFrame? _clawFrame;

    partial void LightningClawModel(HandRole role, string wargear, WeaponAssets assets, ref WeaponAttachment? attachment, ref WeaponAsset? asset, ref bool alwaysHeld)
    {
        if (role != HandRole.Melee || !wargear.Equals(ArsenalRules.LightningClaw, System.StringComparison.OrdinalIgnoreCase)) return;
        if (assets.Catalogue.Held(SmContent.Extras.Musou.Arsenal.Arsenal.ClawBase) is not { } sword || assets.Get(sword) is not { } swordAsset) return;   // the claw is held where the sword is
        ClawCrackle.Wanted = true; ClawCrackle.Ensure();   // a claw is worn: its power field is always on, so its glow driver runs from now (not only from a strike)
        // Owner 2026-11-12: the claw is part of the hand, blades out of the gauntlet, locked to the hand bone: the gauntlet's model is made in the hand bone's own space and drawn on that bone with no offset
        // (ClawGauntlet; `--old-claw-model` restores the held model below).
        if (!ClawGauntlet.Old && _skeleton is { } hand && (_clawFrame ??= ClawHandFrame.Read(hand)) is { } frame && ClawGauntlet.For(swordAsset, sword, frame) is { } gauntlet)
        {
            attachment = ClawGauntlet.OnHand(sword, wargear, frame);
            asset = gauntlet;
            alwaysHeld = true;
            return;
        }
        // The forearm's direction is the rig's (every marine's skeleton is the same): read once, when the claw's mesh is made.
        if ((ClawModel.Known(swordAsset) ? ClawModel.For(swordAsset, sword) : ClawModel.For(swordAsset, sword, ForearmInModelRoot(sword, swordAsset))) is not { } claw) return;
        attachment = sword with { Wargear = wargear, Name = "lightning_claw", HideOnStow = true };   // worn on the arm: a stim stows the sword on the hip, never the gauntlet
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
    /// <summary>
    /// W2: a claw strike is posed: this view starts driving the claws' glow (<see cref="ClawGlowFrame"/>; the claw's own clips, once approved, drop in here as well: <c>ExtrasMeleeClip</c> leaves the
    /// sword's stand-in clip when this gives none).
    /// </summary>
    partial void LightningClawClip(string wargear, bool heavy, int step, ref string? clip, ref float rate)
    {
        if (!wargear.Equals(ArsenalRules.LightningClaw, System.StringComparison.OrdinalIgnoreCase)) return;
        ClawGlowRegister();
    }

    /// <summary>This view drives the claws' glow (a new view after a rejoin takes over from the freed one: the driver drops a freed owner).</summary>
    internal void ClawGlowRegister()
    {
        if (ReferenceEquals(ClawCrackle.Owner, this)) return;
        ClawCrackle.Owner = this;   // a new view after a rejoin takes over from the freed one (the driver drops a freed owner)
        ClawCrackle.Frame = ClawGlowFrame;
        ClawCrackle.Ensure();
    }

    readonly Dictionary<int, float> _clawGlow = new();

    /// <summary>One worn claw: its mesh instance, the rig's own copies of the power-field materials (strike level, arc detail), the armour look it wears and its crackle sound.</summary>
    sealed class ClawRigState
    {
        public Godot.MeshInstance3D Mesh = null!;
        public readonly List<Godot.ShaderMaterial> Powered = new();
        public readonly List<(int Surface, bool Dark)> Housing = new();
        public Godot.ShaderMaterial? Arcs;
        public MarineAssets? Look;
        public bool LookDone;
        public int LookTries;
        public float Strike = -1f, Lod = -1f;
        public SoundEmitter? Sound;
        public bool Humming;
    }

    readonly Dictionary<int, ClawRigState> _clawMesh = new();
    readonly HashSet<int> _clawSeen = new();
    readonly List<int> _clawGone = new();

    /// <summary>Metres: a claw within this of the camera hums (its crackle loop plays), and stops past <c>HumStop</c>; one loop per claw, own first (ours: a handful of voices at most).</summary>
    const float HumStart = 16f, HumStop = 20f;

    /// <summary>Each claw's strike level (1 while its wearer's strike is shown, then faded out in 0.18 s), written to that claw's own mesh instance, with its arc level of detail and its armour look and crackle.</summary>
    void ClawGlowFrame(float delta)
    {
        if (_client is null || _config is null) return;
        _clawSeen.Clear();
        ClawGlowOf(_client.PlayerId, _local, _client.Predicted, delta);
        foreach (var o in _lastOthers) ClawGlowOf(o.PlayerId, _remotes.GetValueOrDefault(o.PlayerId), o, delta);
        if (_clawMesh.Count > _clawSeen.Count)   // a player who left, died into another weapon or changed melee
        {
            _clawGone.Clear();
            foreach (var id in _clawMesh.Keys) if (!_clawSeen.Contains(id)) _clawGone.Add(id);
            foreach (var id in _clawGone) { ClawHum(_clawMesh[id], false); _clawMesh.Remove(id); }
        }
    }

    void ClawGlowOf(int id, CharacterViewer? character, PlayerState state, float delta)
    {
        if (character is null || !Godot.GodotObject.IsInstanceValid(character) || id <= 0 || MeleeWargear(state) is not { } wargear || !wargear.Equals(ArsenalRules.LightningClaw, System.StringComparison.OrdinalIgnoreCase)) return;
        float before = _clawGlow.GetValueOrDefault(id);
        float level = ClawCrackle.GlowTest && id == _client!.PlayerId ? (Godot.Mathf.Sin(Godot.Time.GetTicksMsec() / 1000f * Godot.Mathf.Tau * 0.5f) > -0.2f ? 1f : 0f) : state.MeleeStep > 0 && state.Alive ? 1f : Godot.Mathf.Max(0f, before - delta / 0.18f);
        _clawGlow[id] = level;
        // The claw's mesh instance of this player, found once and kept while it lives (a rig rebuild frees it: then it is looked for again). Its power-field surfaces get copies of their materials of their own,
        // so only this claw's strike lights it and each claw's arcs have their own level of detail; its housing surfaces take the wearer's gauntlet armour (ClawArmourLook).
        if (!_clawMesh.TryGetValue(id, out var rig) || !Godot.GodotObject.IsInstanceValid(rig.Mesh) || !rig.Mesh.IsInsideTree())
        {
            if (rig is not null) ClawHum(rig, false);
            Godot.MeshInstance3D? mesh = null;
            if (character.Skeleton is { } skeleton && Godot.GodotObject.IsInstanceValid(skeleton))
                foreach (var child in skeleton.GetChildren())
                    if (child is Godot.BoneAttachment3D holder && holder.Name.ToString().StartsWith("Weapon_Melee_", System.StringComparison.Ordinal) && holder.GetNodeOrNull<Godot.MeshInstance3D>("Holder/Mesh") is { } m) { mesh = m; break; }
            if (mesh?.Mesh is not { } shape || shape.GetSurfaceCount() == 0) { _clawMesh.Remove(id); return; }
            rig = new ClawRigState { Mesh = mesh };
            for (int s = 0; s < shape.GetSurfaceCount(); s++)
            {
                var material = shape.SurfaceGetMaterial(s);
                if (ClawPowerField.IsPerRig(material))
                {
                    var own = (Godot.ShaderMaterial)material.Duplicate();
                    mesh.SetSurfaceOverrideMaterial(s, own);
                    rig.Powered.Add(own);
                    if (ReferenceEquals(material, ClawPowerField.ArcMaterial)) rig.Arcs = own;
                }
                else if (ReferenceEquals(material, ClawPowerField.HousingPlate)) rig.Housing.Add((s, false));
                else if (ReferenceEquals(material, ClawPowerField.HousingDark)) rig.Housing.Add((s, true));
            }
            _clawMesh[id] = rig;
        }
        _clawSeen.Add(id);
        if (rig.Housing.Count > 0 && (!ReferenceEquals(rig.Look, character.Assets) || (!rig.LookDone && rig.Look is not null)))
        {
            if (!ReferenceEquals(rig.Look, character.Assets)) rig.LookTries = 0;
            rig.Look = character.Assets;
            var look = ClawArmourLook.For(rig.Look);
            rig.LookTries++;
            rig.LookDone = rig.Look is null || look is not null || rig.LookTries >= 120;   // a look not yet built is asked again next frame (about 2 s at most)
            foreach (var (surface, dark) in rig.Housing)
                rig.Mesh.SetSurfaceOverrideMaterial(surface, look is null ? null : dark ? look.Dark : look.Plate);
        }
        float distance = rig.Mesh.GetViewport()?.GetCamera3D() is { } camera ? camera.GlobalPosition.DistanceTo(rig.Mesh.GlobalPosition) : 0f;
        if (level != rig.Strike)
        {
            rig.Strike = level;
            foreach (var m in rig.Powered) m.SetShaderParameter(ClawCrackle.StrikeName, level);
        }
        float lod = ClawPowerField.Lod(distance);
        if (rig.Arcs is not null && Godot.Mathf.Abs(lod - rig.Lod) > 0.01f) { rig.Lod = lod; rig.Arcs.SetShaderParameter(ClawPowerField.LodName, lod); }
        // The faint crackle loop (the power conduit's electric crackle, the Librarian's lightning loop): own claw and near ones while the wearer lives.
        bool near = distance < (rig.Humming ? HumStop : HumStart);
        ClawHum(rig, state.Alive && near && _sound is not null, id == _client!.PlayerId);
    }

    /// <summary>Starts or stops a claw's crackle loop (<see cref="ClawPowerField.HumEvent"/>), the emitter following the claw's mesh; only one post per start.</summary>
    void ClawHum(ClawRigState rig, bool on, bool own = false)
    {
        if (_sound is null) return;
        if (on && !rig.Humming)
        {
            var mesh = rig.Mesh;
            rig.Sound ??= new SoundEmitter
            {
                Label = "lightning claw", Own = own, DistanceScale = ClawPowerField.HumDistanceScale,
                Follow = () => Godot.GodotObject.IsInstanceValid(mesh) && mesh.IsInsideTree() ? mesh.GlobalPosition : null,
            };
            _sound.Post(ClawPowerField.HumEvent, rig.Sound);
            rig.Humming = true;
        }
        else if (!on && rig.Humming)
        {
            if (rig.Sound is not null) _sound.StopEmitter(rig.Sound);
            rig.Humming = false;
        }
    }
}
