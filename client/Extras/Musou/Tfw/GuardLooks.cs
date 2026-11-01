using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Musou;
using SmFormats.Havok;

namespace SmClient;

/// <summary>
/// The Guardsman's look (TF7a): the original body, head, lasgun and clips of <c>ig_coop_guardsman</c> joined into a <see cref="MusouLooks.Look"/> (one mesh set, its clips <see cref="GuardRig.ClipNames"/>)
/// for <see cref="GuardCrowd"/>, with the clips only the skinned tier uses (aim pitch, recoil) and where the muzzle is in the aim pose. Files and clips are read on a worker; <see cref="Step"/>
/// builds the Godot objects on the main thread. Read from the player's install at run time only.
/// </summary>
public sealed class GuardLooks
{
    sealed record Read(RigAssets Rig, CharacterPiece Body, CharacterPiece Head, CharacterPiece Lasgun, GuardLasgun Mount, HavokBoundAnimation?[] Clips, HavokBoundAnimation? AimDown, HavokBoundAnimation? AimUp,
        HavokBoundAnimation? Recoil, bool RecoilAdditive, Vector3 Muzzle, List<string> Notes);

    readonly string _gameDir;
    readonly Task<Read> _read;
    bool _done;

    public List<string> Notes { get; } = new();
    /// <summary>The look, once built (null before, or when the install cannot give it: no Guardsman is drawn then).</summary>
    public MusouLooks.Look? Look { get; private set; }
    public HavokBoundAnimation? AimDown { get; private set; }
    public HavokBoundAnimation? AimUp { get; private set; }
    public HavokBoundAnimation? Recoil { get; private set; }
    /// <summary>The recoil's tracks are offsets (Havok's blend hint); otherwise a whole pose, laid as its difference from its first frame.</summary>
    public bool RecoilAdditive { get; private set; }
    /// <summary>The muzzle in a man's own space (game axes, feet at the origin, facing +Y) in the level aim.</summary>
    public Vector3 Muzzle { get; private set; }
    /// <summary>The bone the lasgun follows and the muzzle's place on it.</summary>
    public int MuzzleBone { get; private set; } = -1;
    public Transform3D MuzzleOnBone { get; private set; } = Transform3D.Identity;

    public GuardLooks(string gameDir) { _gameDir = gameDir; _read = Task.Run(ReadAll); }

    Read ReadAll()
    {
        var notes = new List<string>();
        var rig = RigAssets.Load(_gameDir, GuardRig.Profile, notes);
        string archive = GuardRig.Profile.ArchivePath(_gameDir);
        var body = Piece(archive, GuardRig.BodyVisual, rig, notes);
        var head = Piece(archive, GuardRig.Head, rig, notes);
        var mount = GuardLasgun.Load(_gameDir, rig.Rig.Skeleton.BoneNames, notes);
        var lasgun = new CharacterPiece("ig_lasgun", mount.Model.Model, null) { Rigid = mount.Rigid, Hidden = mount.Hidden };
        var clips = GuardRig.ClipNames.Select(rig.Animation).ToArray();
        for (int i = 0; i < clips.Length; i++) clips[i] ??= rig.Animation(GuardRig.ClipNames[GuardRig.Idle]);
        var recoil = rig.Animation(GuardRig.Recoil);
        bool additive = recoil is not null && rig.Rig.LoadBlendHint(GuardRig.Recoil) == AnimationBlendHint.Additive;
        return new Read(rig, body, head, lasgun, mount, clips, rig.Animation(GuardRig.AimDown), rig.Animation(GuardRig.AimUp), recoil, additive, MuzzleInAim(rig, mount, clips[GuardRig.AimIdle]), notes);
    }

    /// <summary>The muzzle in model space in the first frame of the level aim.</summary>
    static Vector3 MuzzleInAim(RigAssets rig, GuardLasgun mount, HavokBoundAnimation? aim)
    {
        var skeleton = rig.Rig.Skeleton;
        int bone = skeleton.BoneNames.ToList().IndexOf(mount.Bone);
        if (bone < 0 || mount.MuzzleToBone is not { } muzzle) return new Vector3(0f, 0.8f, 1.4f);   // ours: only for a damaged install
        var pose = skeleton.ReferencePose.ToArray();
        if (aim is not null)
        {
            var sample = new HavokTransform[aim.Animation.TrackCount];
            aim.Animation.SampleInto(0f, sample);
            for (int k = 0; k < sample.Length; k++) if ((uint)aim.TrackToBone[k] < (uint)pose.Length) pose[aim.TrackToBone[k]] = sample[k];
        }
        var at = (muzzle * skeleton.ModelSpace(pose)[bone]).Translation;
        return new Vector3(at.X, at.Y, at.Z);
    }

    /// <summary>A mesh's nearest level of detail, its rigid blocks under their tree bones.</summary>
    static CharacterPiece Piece(string archive, string name, RigAssets rig, List<string> notes)
    {
        var model = StaticModel.Load(archive, name, notes);
        var tree = ObjectTree.Load(archive, name, notes) ?? throw new System.IO.FileNotFoundException($"no object tree '{name}.O3d'");
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => model.Mesh.Blocks[v.MeshId].Skin is null))
            if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        return new CharacterPiece("ig_" + name, model, null) { Rigid = rigid, Hidden = hidden };
    }

    /// <summary>Builds the look once its files are read (main thread); the look when built this call, else null.</summary>
    public MusouLooks.Look? Step()
    {
        if (_done || !_read.IsCompleted) return null;
        _done = true;
        if (_read.IsFaulted) { Notes.Add($"guards: the Guardsman could not be read: {_read.Exception?.GetBaseException().Message}"); return null; }
        var read = _read.Result;
        Notes.AddRange(read.Notes);
        try
        {
            var pieces = new PieceCache(_gameDir, read.Rig, Notes);
            var list = new List<AssetPiece>();
            int textured = 0, surfaces = 0;
            foreach (var piece in new[] { read.Body, read.Head, read.Lasgun })
            {
                if (pieces.Build(piece.Name, piece, vertexColours: true) is not { } asset) { Notes.Add($"guards: {piece.Name} has nothing to draw"); continue; }
                list.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]));
                textured += asset.Surfaces.Count(s => s.HasDiffuse); surfaces += asset.Surfaces.Count;
            }
            AimDown = read.AimDown; AimUp = read.AimUp; Recoil = read.Recoil; RecoilAdditive = read.RecoilAdditive; Muzzle = read.Muzzle;
            MuzzleBone = read.Rig.Rig.Skeleton.BoneNames.ToList().IndexOf(read.Mount.Bone);
            if (read.Mount.MuzzleToBone is { } muzzle) MuzzleOnBone = MarineAssets.ToGodot(muzzle);
            return Look = new MusouLooks.Look { Name = GuardRig.BodyVisual, Assets = new MarineAssets(read.Rig, list, textured, surfaces, pieces.Notes), Scale = 1f, Clips = read.Clips };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Notes.Add($"guards: the Guardsman could not be built: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
