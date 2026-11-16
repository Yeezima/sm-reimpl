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
/// The Bomb Squig's own look (expansion S2, Musou Mode only): the original <c>squig_bomb</c> on its rig (<see cref="SquigRig"/>), read on a worker and built as the Weirdboy's
/// (<see cref="WeirdboyLooks"/>), drawn at the size it was modelled (scale 1). When the install cannot give it, nothing is built and <see cref="MusouLooks"/> draws a labelled placeholder
/// (a Boy at <see cref="MusouLooks.SquigPlaceholderScale"/>). Read from the player's install at run time only (*original asset loaded*).
/// </summary>
public sealed class SquigLooks
{
    sealed record Read(RigAssets Rig, List<CharacterPiece> Pieces, HavokBoundAnimation?[] Clips, List<string> Notes);

    readonly string _gameDir;
    readonly Task<Read> _read;

    public List<string> Notes { get; } = new();
    /// <summary>The look once built (null before, or when it failed: see <see cref="Failed"/>).</summary>
    public MusouLooks.Look? Look { get; private set; }
    public bool Failed { get; private set; }
    public bool Done => Look is not null || Failed;
    /// <summary>The height of the built model (metres, game axes, Z up) at scale 1; 0 before.</summary>
    public float Height { get; private set; }

    public SquigLooks(string gameDir) { _gameDir = gameDir; _read = Task.Run(ReadAll); }

    Read ReadAll()
    {
        var notes = new List<string>();
        var rig = RigAssets.Load(_gameDir, SquigRig.Profile, notes);
        string archive = SquigRig.Profile.ArchivePath(_gameDir);
        var pieces = new List<CharacterPiece> { Piece(archive, SquigRig.BodyVisual, rig, notes) ?? throw new System.IO.FileNotFoundException($"no '{SquigRig.BodyVisual}' in {SquigRig.Archive}") };
        var names = new string?[MusouLooks.ClipCount];
        var clips = new HavokBoundAnimation?[MusouLooks.ClipCount];
        void Find(int clip, (string[] Names, string Contains) want)
        {
            names[clip] = rig.FindAnimation(want.Names, want.Contains);
            clips[clip] = names[clip] is { } n ? rig.Animation(n) : null;
        }
        Find(MusouLooks.Run, SquigRig.Run);
        Find(MusouLooks.Idle, SquigRig.Idle);
        Find(MusouLooks.Attack, SquigRig.Attack);
        Find(MusouLooks.Stagger, SquigRig.Stagger);
        Find(MusouLooks.Death, SquigRig.Death);
        var any = clips[MusouLooks.Idle] ?? clips[MusouLooks.Run];
        if (any is null && rig.Rig.AnimationNames.FirstOrDefault() is { } first) any = rig.Animation(first);
        clips[MusouLooks.Sprint] ??= clips[MusouLooks.Run];   // no sprint of its own: the run (2026-11-09)
        for (int i = 0; i < clips.Length; i++) clips[i] ??= any;
        notes.Add($"squig: rig {SquigRig.Entry}: {rig.Rig.Skeleton.BoneNames.Count} bones, {rig.Rig.AnimationNames.Count} clips; run {names[MusouLooks.Run] ?? "none"}, idle {names[MusouLooks.Idle] ?? "none"}, " +
                  $"attack {names[MusouLooks.Attack] ?? "none"}, stagger {names[MusouLooks.Stagger] ?? "none"}, death {names[MusouLooks.Death] ?? "none"}");
        return new Read(rig, pieces, clips, notes);
    }

    /// <summary>A mesh's nearest level of detail: its skinned blocks on the rig, its rigid blocks under their tree bones (as the Warboss's).</summary>
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
        var foreign = model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []).Select(b => b.Name).Where(b => !rig.BoneNames.Contains(b)).Distinct().ToList();
        if (foreign.Count > 0) notes.Add($"squig: {name} binds bones its rig lacks: {string.Join(", ", foreign)}");
        notes.Add($"squig: {name}: {model.Mesh.Blocks.Count} blocks, {model.Mesh.Blocks.Count - hidden.Count} drawn ({string.Join(", ", drawn.Select(v => v.RefNode))}), {rigid.Count} rigid");
        return new CharacterPiece("musou_" + name, model, null) { Rigid = rigid, Hidden = hidden };
    }

    /// <summary>Builds the look once its files are read (main thread, once); the look when built this call, else null.</summary>
    public MusouLooks.Look? Step()
    {
        if (Done || !_read.IsCompleted) return null;
        if (_read.IsFaulted)
        {
            Failed = true;
            Notes.Add($"squig: the Squig model could not be read ({_read.Exception?.GetBaseException().Message}); a placeholder is drawn instead");
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
            foreach (var piece in read.Pieces)
            {
                if (pieces.Build(piece.Name, piece, vertexColours: true) is not { } asset) { Notes.Add($"squig: {piece.Name} has nothing to draw"); continue; }
                list.Add(new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]));
                textured += asset.Surfaces.Count(s => s.HasDiffuse); surfaces += asset.Surfaces.Count;
                if (piece.Name.EndsWith(SquigRig.BodyVisual, StringComparison.Ordinal))
                {
                    var box = asset.Mesh.GetAabb();
                    top = MathF.Max(top, box.End.Z); bottom = MathF.Min(bottom, box.Position.Z);
                }
            }
            if (list.Count == 0 || read.Clips.All(c => c is null)) { Failed = true; Notes.Add("squig: no mesh or no clip to draw; a placeholder is drawn instead"); return null; }
            Height = top > bottom ? top - bottom : 0f;
            Notes.Add($"squig: drawn as {SquigRig.BodyVisual} x1 (model {Height:0.##} m tall)");
            return Look = new MusouLooks.Look { Name = "BombSquig:" + SquigRig.BodyVisual, Assets = new MarineAssets(read.Rig, list, textured, surfaces, pieces.Notes), Scale = 1f, Clips = read.Clips };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed = true;
            Notes.Add($"squig: the Squig model could not be built ({ex.GetType().Name}: {ex.Message}); a placeholder is drawn instead");
            return null;
        }
    }
}
