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
/// The Warboss's own look (owner, 2026-11-06: "the final boss should use a warboss model"): the original <c>warboss_regular</c> body and <c>warboss_head_regular</c> head on their own rig
/// (<see cref="WarbossRig"/>), read on a worker like the Guardsman's (<see cref="GuardLooks"/>), built on the main thread by <see cref="Step"/>. Its scale is set by <see cref="MusouLooks"/> so
/// he stands as tall as the old Nob body did at <c>MusouRules.BossScale</c> (the owner: "as tall as today"). When the install cannot give it, nothing is built and the Nob body stays.
/// Read from the player's install at run time only (*original asset loaded*).
/// </summary>
public sealed class WarbossLooks
{
    sealed record Read(RigAssets Rig, CharacterPiece Body, CharacterPiece? Head, HavokBoundAnimation?[] Clips, List<string> Notes);

    readonly string _gameDir;
    readonly Task<Read> _read;

    public List<string> Notes { get; } = new();
    /// <summary>The look once built (null before, or when it failed: see <see cref="Failed"/>).</summary>
    public MusouLooks.Look? Look { get; private set; }
    /// <summary>The model could not be had: the Nob body is drawn instead.</summary>
    public bool Failed { get; private set; }
    /// <summary>Whether <see cref="Step"/> has finished, either way.</summary>
    public bool Done => Look is not null || Failed;
    /// <summary>The height of the built model (metres, game axes, Z up) at scale 1; 0 before.</summary>
    public float Height { get; private set; }

    public WarbossLooks(string gameDir) { _gameDir = gameDir; _read = Task.Run(ReadAll); }

    Read ReadAll()
    {
        var notes = new List<string>();
        var rig = RigAssets.Load(_gameDir, WarbossRig.Profile, notes);
        string archive = WarbossRig.Profile.ArchivePath(_gameDir);
        var body = Piece(archive, WarbossRig.BodyVisual, rig, notes) ?? throw new System.IO.FileNotFoundException($"no '{WarbossRig.BodyVisual}' in {WarbossRig.Archive}");
        CharacterPiece? head = null;
        try { head = Piece(archive, WarbossRig.Head, rig, notes); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { notes.Add($"warboss: head {WarbossRig.Head}: {ex.Message}"); }
        var names = new string?[MusouLooks.ClipCount];
        var clips = new HavokBoundAnimation?[MusouLooks.ClipCount];
        void Find(int clip, (string[] Names, string Contains) want)
        {
            names[clip] = rig.FindAnimation(want.Names, want.Contains);
            clips[clip] = names[clip] is { } n ? rig.Animation(n) : null;
        }
        Find(MusouLooks.Run, WarbossRig.Run);
        Find(MusouLooks.Idle, WarbossRig.Idle);
        Find(MusouLooks.Attack, WarbossRig.Attack);
        Find(MusouLooks.Stagger, WarbossRig.Stagger);
        Find(MusouLooks.Death, WarbossRig.Death);
        var any = clips[MusouLooks.Idle] ?? clips[MusouLooks.Run];
        if (any is null && rig.Rig.AnimationNames.FirstOrDefault() is { } first) any = rig.Animation(first);   // a still pose rather than none
        for (int i = 0; i < clips.Length; i++) clips[i] ??= any;
        notes.Add($"warboss: rig {WarbossRig.Entry}: {rig.Rig.Skeleton.BoneNames.Count} bones, {rig.Rig.AnimationNames.Count} clips; run {names[MusouLooks.Run] ?? "none"}, idle {names[MusouLooks.Idle] ?? "none"}, " +
                  $"attack {names[MusouLooks.Attack] ?? "none"}, stagger {names[MusouLooks.Stagger] ?? "none"}, death {names[MusouLooks.Death] ?? "none"}");
        return new Read(rig, body, head, clips, notes);
    }

    /// <summary>A mesh's nearest level of detail, its rigid blocks under their tree bones (as the Guardsman's); null when the mesh binds no bone of this rig.</summary>
    static CharacterPiece? Piece(string archive, string name, RigAssets rig, List<string> notes)
    {
        var model = StaticModel.Load(archive, name, notes);
        var tree = ObjectTree.Load(archive, name, notes) ?? throw new System.IO.FileNotFoundException($"no object tree '{name}.O3d'");
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count
                                            && !v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase)).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => model.Mesh.Blocks[v.MeshId].Skin is null))
            if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        int foreign = model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []).Count(b => !rig.BoneNames.Contains(b.Name));
        if (foreign > 0) notes.Add($"warboss: {name} binds {foreign} bones its rig lacks");
        return new CharacterPiece("musou_" + name, model, null) { Rigid = rigid, Hidden = hidden };
    }

    /// <summary>Builds the look once its files are read (main thread, once); the look when built this call, else null.</summary>
    public MusouLooks.Look? Step()
    {
        if (Done || !_read.IsCompleted) return null;
        if (_read.IsFaulted)
        {
            Failed = true;
            Notes.Add($"warboss: the Warboss model could not be read ({_read.Exception?.GetBaseException().Message}); the Nob body is drawn instead");
            return null;
        }
        var read = _read.Result;
        Notes.AddRange(read.Notes);
        try
        {
            var pieces = new PieceCache(_gameDir, read.Rig, Notes);
            var list = new List<AssetPiece>();
            int textured = 0, surfaces = 0;
            float top = float.NegativeInfinity, bottom = float.PositiveInfinity;
            foreach (var piece in new[] { read.Body, read.Head })
            {
                if (piece is null) continue;
                if (pieces.Build(piece.Name, piece, vertexColours: true) is not { } asset) { Notes.Add($"warboss: {piece.Name} has nothing to draw"); continue; }
                list.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]));
                textured += asset.Surfaces.Count(s => s.HasDiffuse); surfaces += asset.Surfaces.Count;
                var box = asset.Mesh.GetAabb();
                top = MathF.Max(top, box.End.Z); bottom = MathF.Min(bottom, box.Position.Z);
            }
            if (list.Count == 0 || read.Clips.All(c => c is null)) { Failed = true; Notes.Add("warboss: no mesh or no clip to draw; the Nob body is drawn instead"); return null; }
            Height = top > bottom ? top - bottom : 0f;
            return Look = new MusouLooks.Look { Name = "Warboss:" + WarbossRig.BodyVisual, Assets = new MarineAssets(read.Rig, list, textured, surfaces, pieces.Notes), Scale = 1f, Clips = read.Clips };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed = true;
            Notes.Add($"warboss: the Warboss model could not be built ({ex.GetType().Name}: {ex.Message}); the Nob body is drawn instead");
            return null;
        }
    }
}
