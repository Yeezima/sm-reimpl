using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Havok;
using SmSim.Extras.Musou;

namespace SmClient;

/// <summary>
/// The guns in the ranged Orks' hands (added content, Musou only; <see cref="MusouOrkGunRules"/>): each gun's wargear, model and wield mount are read on a worker as the playable Ork's are
/// (<see cref="WeaponModels"/> on the boy's rig, <see cref="OrkWeaponMounts"/>), and <see cref="Arm"/> joins the model to its type's look as one more rigid piece on its prop bone, so the
/// skinned tier and the vertex-animation bake both carry it (the bake takes every piece of the look). The look the Ork class shares is never changed: an armed look is a new
/// <see cref="MarineAssets"/> over the same pieces plus the gun. Also where the muzzle is: on its bone, and in an Ork's own space in his aiming stance (for the far tier). Read from the install only.
/// </summary>
public sealed class MusouGunModels
{
    /// <summary>A look's gun: the bone it follows, the muzzle on that bone, and the muzzle in the Ork's own space (game axes, feet at the origin, facing +Y) in the aiming stance's first frame.</summary>
    public sealed record Gun(string Wargear, int MuzzleBone, Transform3D MuzzleOnBone, Vector3 Muzzle);

    sealed record Read(GuardLasgun Mount, CharacterPiece Piece, List<string> Notes);

    readonly Dictionary<MusouEnemyType, Task<Read>> _reads = new();
    readonly Dictionary<MusouLooks.Look, Gun> _guns = new(ReferenceEqualityComparer.Instance);

    public MusouGunModels(string gameDir, IReadOnlyList<string> bones)
    {
        foreach (var g in MusouOrkGunRules.Guns)
        {
            string wargear = g.Wargear;
            _reads[g.Type] = Task.Run(() =>
            {
                var notes = new List<string>();
                var catalogue = WeaponModels.Load(gameDir, RigProfiles.OrkBoy, notes, [wargear], bones);
                var attachment = catalogue.Held(wargear) ?? OwnMount(catalogue, wargear, bones, notes) ?? throw new System.IO.InvalidDataException($"{wargear}: no wield entry");
                var mount = GuardLasgun.From(WeaponModel.Load(gameDir, attachment, notes), attachment.Wield!, notes);
                return new Read(mount, new CharacterPiece("musou_gun_" + wargear, mount.Model.Model, null) { Rigid = mount.Rigid, Hidden = mount.Hidden }, notes);
            });
        }
    }

    /// <summary>
    /// A gun the data gives no wield mount (<see cref="MusouOrkGunRules.OwnMounts"/>: the Rokkit Boy's launcha): its first attachment, held by our handle node on the rig bone of that name; null when
    /// none is listed, the wargear has no attachment or the rig lacks the bone.
    /// </summary>
    static WeaponAttachment? OwnMount(WeaponModels catalogue, string wargear, IReadOnlyList<string> bones, List<string> notes)
    {
        if (!MusouOrkGunRules.OwnMounts.TryGetValue(wargear, out var node) || !catalogue.ByWargear.TryGetValue(wargear, out var list) || list.Count == 0) return null;
        var bone = bones.FirstOrDefault(b => b.Equals(node, StringComparison.OrdinalIgnoreCase));
        if (bone is null) { notes.Add($"{wargear}: our mount bone '{node}' is not in the rig"); return null; }
        notes.Add($"{wargear}: no wield entry in the data; held by our mount, its {node} on the bone {bone} (as the Shoota)");
        return list[0] with { Wield = new WeaponMount(node, node, bone, System.Numerics.Matrix4x4.Identity, 1f), Stow = null, IsWorn = false, TwoHanded = true };
    }

    /// <summary>Whether <paramref name="type"/>'s gun has been read (or it carries none, or the read failed): its look need not wait.</summary>
    public bool IsReady(MusouEnemyType type) => !_reads.TryGetValue(type, out var t) || t.IsCompleted;

    /// <summary>The gun of a look (null: an unarmed look, or not yet armed).</summary>
    public Gun? Of(MusouLooks.Look? look) => look is not null && _guns.TryGetValue(look, out var g) ? g : null;

    /// <summary>Puts <paramref name="type"/>'s gun in <paramref name="look"/>'s hands (main thread, once its read is done); a failure is noted and the look stays unarmed.</summary>
    public void Arm(MusouLooks.Look look, MusouEnemyType type, PieceCache pieces, List<string> notes)
    {
        if (!_reads.TryGetValue(type, out var task) || !task.IsCompleted) return;
        if (task.IsFaulted) { notes.Add($"musou guns: {type}'s gun could not be read: {task.Exception?.GetBaseException().Message}"); return; }
        var read = task.Result;
        foreach (var n in read.Notes) notes.Add("musou guns: " + n);
        try
        {
            if (pieces.Build(read.Piece.Name, read.Piece, vertexColours: true) is not { } asset) { notes.Add($"musou guns: {read.Piece.Name} has nothing to draw"); return; }
            var old = look.Assets;
            var list = old.Pieces.Append(new AssetPiece(read.Piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count])).ToList();
            look.Assets = new MarineAssets(old.Rig, list, old.TexturedSurfaces + asset.Surfaces.Count(s => s.HasDiffuse), old.Surfaces + asset.Surfaces.Count, old.Notes);
            var skeleton = old.Model.Rig.Skeleton;
            int bone = skeleton.BoneNames.ToList().IndexOf(read.Mount.Bone);
            var onBone = read.Mount.MuzzleToBone is { } m ? MarineAssets.ToGodot(m) : Transform3D.Identity;
            _guns[look] = new Gun(read.Mount.Model.Package, bone, onBone, MuzzleInStance(skeleton, bone, read.Mount, look.Clips[MusouLooks.Idle]));
            notes.Add($"musou guns: {type} holds {read.Mount.Model.Package} on {read.Mount.Bone}{(read.Mount.MuzzleToBone is null ? " (no fx_muzzle: flashes from the bone)" : "")}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            notes.Add($"musou guns: {type}'s gun could not be built: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The muzzle in model space in the first frame of the stance clip (as the Guardsman's, <c>GuardLooks</c>).</summary>
    static Vector3 MuzzleInStance(HavokSkeleton skeleton, int bone, GuardLasgun mount, HavokBoundAnimation? stance)
    {
        if (bone < 0) return new Vector3(0.2f, 0.7f, 1.2f);   // ours: only for a damaged install
        var pose = skeleton.ReferencePose.ToArray();
        if (stance is not null)
        {
            var sample = new HavokTransform[stance.Animation.TrackCount];
            stance.Animation.SampleInto(0f, sample);
            for (int k = 0; k < sample.Length; k++) if ((uint)stance.TrackToBone[k] < (uint)pose.Length) pose[stance.TrackToBone[k]] = sample[k];
        }
        var at = ((mount.MuzzleToBone ?? System.Numerics.Matrix4x4.Identity) * skeleton.ModelSpace(pose)[bone]).Translation;
        return new Vector3(at.X, at.Y, at.Z);
    }
}
