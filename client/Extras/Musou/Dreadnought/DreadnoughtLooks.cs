using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;
using SmContent.Extras.Musou;

namespace SmClient;

/// <summary>
/// The Musou Dreadnought's model (D2): <c>sm_dreadnought_basic</c> (body, assault cannon and arm blocks, drawn by its object tree's near-level visuals) on its own rig <c>havok_sm_dreadnought_basic__hkxx</c>,
/// read on a worker like the Warboss's (<see cref="WarbossLooks"/>) and built on the main thread by <see cref="Step"/>. *Original asset loaded* at run time only. Null look = the install cannot give it:
/// the Tactical stand-in is drawn instead (never a T-pose).
/// </summary>
public sealed class DreadnoughtLooks
{
    sealed record Read(RigAssets Rig, CharacterPiece Body, List<string> Notes, Dictionary<string, (string Bone, System.Numerics.Matrix4x4 Local)> Markers);

    readonly string _gameDir;
    readonly Task<Read> _read;

    public List<string> Notes { get; } = new();
    /// <summary>The model's marker points (<c>fx_torso</c>, <c>fx_l_foot</c>, the lights ...) as the bone they sit under and the offset in it, read from the object tree (the effects' places; a marker the tree lacks is absent).</summary>
    public Dictionary<string, (string Bone, System.Numerics.Matrix4x4 Local)> Markers { get; private set; } = new();
    /// <summary>The model's assets once built (null before, or when it failed: <see cref="Failed"/>).</summary>
    public MarineAssets? Assets { get; private set; }
    public bool Failed { get; private set; }
    public bool Done => Assets is not null || Failed;
    /// <summary>Height of the built model at scale 1 (metres, Z up); 0 before.</summary>
    public float Height { get; private set; }

    readonly bool _chaos;
    public DreadnoughtLooks(string gameDir, bool chaos = false) { _gameDir = gameDir; _chaos = chaos; _read = Task.Run(ReadAll); }
    RigProfile Prof => _chaos ? DreadnoughtRig.ChaosProfile : DreadnoughtRig.Profile;
    string Visual => _chaos ? DreadnoughtRig.ChaosBodyVisual : DreadnoughtRig.BodyVisual;

    Read ReadAll()
    {
        var notes = new List<string>();
        var rig = RigAssets.Load(_gameDir, Prof, notes);
        string archive = Prof.ArchivePath(_gameDir);
        var model = StaticModel.Load(archive, Visual, notes);
        var tree = ObjectTree.Load(archive, Visual, notes) ?? throw new System.IO.FileNotFoundException($"no object tree '{Visual}.O3d'");
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count
                                            && !v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase)).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => model.Mesh.Blocks[v.MeshId].Skin is null))
            if (tree.UnderBone(v.RefNode, rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
        notes.Add($"dreadnought: rig {DreadnoughtRig.Entry}: {rig.Rig.Skeleton.BoneNames.Count} bones, {rig.Rig.AnimationNames.Count} clips; {model.Mesh.Blocks.Count} blocks, {drawn.Count} drawn ({string.Join(", ", drawn.Select(v => v.RefNode))}); {rigid.Count} rigid");
        var markers = new Dictionary<string, (string, System.Numerics.Matrix4x4)>();
        foreach (var name in DreadnoughtArm.AllMarkers()) if (tree.UnderBone(name, rig.BoneNames.Contains) is { } at) markers[name] = (at.Bone, at.Local);
        notes.Add($"dreadnought: {markers.Count} of {DreadnoughtArm.AllMarkers().Count()} marker points found in the object tree ({string.Join(", ", DreadnoughtArm.AllMarkers().Where(n => !markers.ContainsKey(n)))} missing)");
        return new Read(rig, new CharacterPiece("musou_" + Visual, model, null) { Rigid = rigid, Hidden = hidden }, notes, markers);
    }

    /// <summary>Builds the assets once the files are read (main thread, once); the assets when built this call, else null.</summary>
    public MarineAssets? Step()
    {
        if (Done || !_read.IsCompleted) return null;
        if (_read.IsFaulted)
        {
            Failed = true;
            Notes.Add($"dreadnought: the model could not be read ({_read.Exception?.GetBaseException().Message}); the Tactical is drawn instead");
            return null;
        }
        var read = _read.Result;
        Notes.AddRange(read.Notes);
        Markers = read.Markers;
        try
        {
            var pieces = new PieceCache(_gameDir, read.Rig, Notes);
            if (pieces.Build(read.Body.Name, read.Body, vertexColours: true) is not { } asset) { Failed = true; Notes.Add("dreadnought: nothing to draw"); return null; }
            var box = asset.Mesh.GetAabb();
            Height = box.End.Z - box.Position.Z;
            var list = new List<AssetPiece> { new(read.Body.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count]) };
            Notes.Add($"dreadnought: built, height {Height:F2} m, {asset.Surfaces.Count(s => s.HasDiffuse)} of {asset.Surfaces.Count} surfaces textured");
            return Assets = new MarineAssets(read.Rig, list, asset.Surfaces.Count(s => s.HasDiffuse), asset.Surfaces.Count, pieces.Notes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Failed = true;
            Notes.Add($"dreadnought: the model could not be built ({ex.GetType().Name}: {ex.Message}); the Tactical is drawn instead");
            return null;
        }
    }
}
