using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

/// <summary>
/// The Ork Boy on its own rig (Ork O3, docs/ork-faction-design.md section 4): the boy's original skinned meshes and materials, joined with the
/// Exterminatus boy's skeleton and animations (<see cref="RigProfiles.OrkBoy"/>) into a <see cref="MarineAssets"/> (the class name stays; a
/// <see cref="CharacterViewer"/> shows it like a Space Marine). The rig and the piece cache are shared by every look.
/// <para>
/// Looks (**ours**: the install has no Ork look choice; these are the four boy meshes plus the armoured boy): the mesh name is the look. A look is
/// the near-detail visuals of the mesh's object tree (<c>.O3d</c>: <see cref="ObjectTreeVisual.NearLevel"/>, so the lower levels of detail are left out) without
/// the "wounded" visuals (the dismemberment stumps: neck, arms; with <c>wounded</c>, a test aid, only those are drawn). Skinned blocks bind to the bones by name through the
/// block's bone palette (<see cref="SmFormats.Dcm.DcmMesh"/>, O1); static blocks (the eye, the tankbusta's mortar and rocket back) hang from the nearest rig bone
/// of their object-tree node, as the Chaos armour's static blocks do.
/// </para>
/// <para>
/// Materials (**supported by evidence**): the near-detail blocks use the single-layer <c>diff_norm_spec_lp_lgchar</c> (<c>diff_emis_norm_spec_lp_lgchar</c> on
/// <c>boy_ranged</c>: an emissive colour, no emissive map) and the wounded blocks the two-layer <c>diff2_vtxblenduv01_norm_spec_lp</c> (the body texture and
/// <c>global_dismemberment_dif</c>, weighted by the vertex colour's alpha: 0 at the cut, 1 away from it: the map's two-layer path,
/// <see cref="MapMaterials"/>, now on skinned meshes). All DXT1; no paint mask: Orks are unpainted. The <c>LightPropMap</c> (<c>*_lp</c>) is not read (its use is
/// not known); the roughness and specular mapping is the map materials' **provisional approximation**.
/// </para>
/// <para>
/// Threads: the files (mesh, materials, textures, object tree) can be read on a worker with <see cref="Prefetch"/>; everything else, <see cref="Shared"/>,
/// <see cref="Build"/>, <see cref="IsReady"/> and the Godot objects, is **main thread only** (nothing here is locked).
/// </para>
/// </summary>
public sealed class OrkAssets
{
    /// <summary>The Ork looks in order (the order is the look index of a later protocol: <c>boy_ranged</c> first).</summary>
    public static readonly IReadOnlyList<string> Looks = OrkLookRules.Names;   // engine-free, so the choice by player id is tested (Ork O7a)

    readonly string _gameDir;
    // Main thread only (see the class remarks).
    readonly Dictionary<string, MarineAssets> _built = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Task<Loaded>> _reading = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a worker reads for a look: the model and its object tree (engine-free data), or the failure.</summary>
    sealed record Loaded(StaticModel? Model, ObjectTree? Tree, List<string> Notes, Exception? Failure);

    public RigAssets Rig { get; }
    public PieceCache Pieces { get; }
    public List<string> Notes { get; }

    public OrkAssets(string gameDir, List<string>? notes = null)
    {
        _gameDir = gameDir;
        Notes = notes ?? new List<string>();
        Rig = RigAssets.Load(gameDir, RigProfiles.OrkBoy, Notes);
        Pieces = new PieceCache(gameDir, Rig, Notes);
    }

    static OrkAssets? _shared;
    static string? _sharedFor;

    /// <summary>The caches every Ork of this process shares, made on first use for <paramref name="gameDir"/> (another folder replaces the old instance). Main thread only.</summary>
    public static OrkAssets Shared(string gameDir, List<string>? notes = null)
    {
        if (_shared is null || !string.Equals(_sharedFor, gameDir, StringComparison.OrdinalIgnoreCase)) { _shared = new OrkAssets(gameDir, notes); _sharedFor = gameDir; }
        return _shared;
    }

    /// <summary>The look's name as the mesh name (case ignored), or null for an unknown look.</summary>
    public static string? LookName(string? look) => Looks.FirstOrDefault(l => l.Equals(look, StringComparison.OrdinalIgnoreCase));

    static string KeyOf(string name, bool wounded) => wounded ? name + "+wounded" : name;

    /// <summary>
    /// Starts reading a look's files on a worker thread (no effect when it is built or being read), so <see cref="Build"/> does not wait for the disk.
    /// Unknown looks are ignored (<see cref="Build"/> reports them). Main thread only; the worker touches no Godot object.
    /// </summary>
    public void Prefetch(string look, bool wounded = false)
    {
        if (LookName(look) is not { } name) return;
        string key = KeyOf(name, wounded);
        if (_built.ContainsKey(key) || _reading.ContainsKey(key)) return;
        _reading[key] = Task.Run(() => Read(name));
    }

    /// <summary>Whether <see cref="Build"/> would not wait for a file: the look is built, or its read has finished.</summary>
    public bool IsReady(string look, bool wounded = false) =>
        LookName(look) is { } name && (_built.ContainsKey(KeyOf(name, wounded)) || (_reading.TryGetValue(KeyOf(name, wounded), out var t) && t.IsCompleted));

    /// <summary>The worker's job: nothing escapes it but an out-of-memory error (a failure is carried to <see cref="Build"/>).</summary>
    Loaded Read(string name)
    {
        var notes = new List<string>();
        try
        {
            string archive = RigProfiles.OrkBoy.ArchivePath(_gameDir);
            var model = StaticModel.Load(archive, name, notes);
            var tree = ObjectTree.Load(archive, name, notes) ?? throw new System.IO.FileNotFoundException($"Object tree '{name}.O3d' not found in {System.IO.Path.GetFileName(archive)}.");
            return new Loaded(model, tree, notes, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return new Loaded(null, null, notes, ex); }
    }

    /// <summary>The look's character model (built once per look and wounded setting). Waits for the files (not when <see cref="Prefetch"/> has finished). Throws when the mesh or its object tree is not in the install.</summary>
    /// <exception cref="ArgumentException">An unknown look.</exception>
    public MarineAssets Build(string look, bool wounded = false)
    {
        string name = LookName(look) ?? throw new ArgumentException($"unknown Ork look '{look}' (known: {string.Join(", ", Looks)})", nameof(look));
        string key = KeyOf(name, wounded);
        if (_built.TryGetValue(key, out var done)) return done;

        Prefetch(name, wounded);
        var read = _reading[key].GetAwaiter().GetResult();
        _reading.Remove(key);   // the raw bytes are not kept; a failure is thrown now and read again by a later call
        var notes = read.Notes;
        if (read.Failure is { } failure) throw new InvalidOperationException($"Ork look '{name}' could not be read: {failure.Message}", failure);
        var model = read.Model!;
        var tree = read.Tree!;

        // The visuals drawn: the nearest level of detail without the wounded stumps; with `wounded` (test aid) only the stumps, to see their blended material. MeshID is the block index.
        var drawn = tree.Visuals.Where(v => v.Renderable && v.NearLevel && v.MeshId >= 0 && v.MeshId < model.Mesh.Blocks.Count
                                            && v.RefNode.Contains("wounded", StringComparison.OrdinalIgnoreCase) == wounded).ToList();
        var hidden = Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => drawn.All(v => v.MeshId != b)).ToHashSet();
        var rigid = new Dictionary<int, RigidBlock>();
        foreach (var v in drawn.Where(v => !v.IsSkinned && model.Mesh.Blocks[v.MeshId].Skin is null))
        {
            if (tree.UnderBone(v.RefNode, Rig.BoneNames.Contains) is { } placed) rigid[v.MeshId] = new RigidBlock(placed.Bone, placed.Local);
            else notes.Add($"{name}: rigid block {v.MeshId} ('{v.RefNode}') hangs under no skeleton bone; left out");
        }
        foreach (var bone in model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []))
            if (!Rig.BoneNames.Contains(bone.Name)) notes.Add($"{name}: bone '{bone.Name}' is not in the Ork skeleton");

        var piece = new CharacterPiece("ork_" + name, model, null) { Rigid = rigid, Hidden = hidden };
        var asset = Pieces.Build(piece.Name, piece, vertexColours: true)
                    ?? throw new InvalidOperationException($"Ork look '{name}' has nothing to draw ({string.Join("; ", notes)})");
        foreach (var n in notes) Pieces.AddNote(n);
        var built = new MarineAssets(Rig, [new AssetPiece(piece.Name, asset.Mesh, asset.Skin, new Material?[asset.Surfaces.Count])],
            asset.Surfaces.Count(s => s.HasDiffuse), asset.Surfaces.Count, Pieces.Notes);
        _built[key] = built;
        return built;
    }

    /// <summary>A one-line description of what a look draws (for logs): visuals, blocks, vertices and materials.</summary>
    public static string Describe(MarineAssets assets)
    {
        var mesh = assets.Pieces.Count > 0 ? assets.Pieces[0].Mesh : null;
        if (mesh is null) return "nothing";
        int vertices = 0;
        for (int s = 0; s < mesh.GetSurfaceCount(); s++) vertices += mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Length;
        return $"{mesh.GetSurfaceCount()} surfaces, {vertices} vertices, {assets.TexturedSurfaces}/{assets.Surfaces} with original diffuse";
    }
}
