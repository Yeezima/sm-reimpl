using SmFormats.Bod;
using SmFormats.Havok;
using System.Numerics;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// An engine-neutral animated character: the Havok rig (skeleton and animations) plus armour pieces, each an
/// original ".dcm" with its materials and diffuse textures. Skinned pieces bind to the skeleton by bone name;
/// rigid pieces (backpack, shoulder pads) follow the single bone their ".O3d" names. Coordinates stay as in the
/// files (Z up). Everything is read into memory; nothing is written.
/// </summary>
public sealed record CharacterModel(HavokCharacterFile Rig, IReadOnlyList<CharacterPiece> Pieces)
{
    /// <summary>The multiplayer Space Marine rig, and the "basic_001" armour set from the packages.</summary>
    public const string MarineRigArchive = "data/chunks/ebp_sets__mp_versus_set.oppc";
    public const string MarineRigEntry = "havok_sm_base_mp__hkxx";
    public static readonly string[] MarineBasicArmour =
        ["torso", "legl", "legr", "arml", "armr", "helmet", "shldrl", "shldrr", "backpack"];

    /// <summary>
    /// The multiplayer Space Marine armour with a paint mask (<c>sm_armour_mp_basic_&lt;part&gt;</c>, wargear shader like the Chaos sets;
    /// its diffuse is unpainted grey, see <see cref="PaintSchemes"/>). <see cref="LoadMarine"/> uses it when the install has it,
    /// else the pre-painted singleplayer-style set (<c>sm_armour_basic_001</c>, M3).
    /// </summary>
    public const string MarineMpArmour = "sm_armour_mp_basic";

    public static IReadOnlyList<string> MarineMpArmourFiles(string gameDir) =>
        MarineBasicArmour.Select(p => Path.Combine(gameDir, "data", "packages", $"{MarineMpArmour}_{p}.oppc")).Where(File.Exists).ToList();

    /// <summary>
    /// The armour files <see cref="LoadMarine"/> loads: the multiplayer set when all nine parts exist (<paramref name="paintable"/> true),
    /// else the singleplayer-style set (one missing part is enough to fall back, so a character is never half one set, half the other);
    /// always the old set when <paramref name="legacyArmour"/>.
    /// </summary>
    public static IReadOnlyList<string> MarineArmourFiles(string gameDir, bool legacyArmour, out bool paintable)
    {
        var mp = legacyArmour ? [] : MarineMpArmourFiles(gameDir);
        paintable = mp.Count == MarineBasicArmour.Length;
        return paintable ? mp
            : MarineBasicArmour.Select(p => Path.Combine(gameDir, "data", "packages", $"sm_armour_basic_001_{p}.oppc")).ToList();
    }

    public static CharacterModel LoadMarine(string gameDir, List<string>? notes = null, bool legacyArmour = false)
    {
        var files = MarineArmourFiles(gameDir, legacyArmour, out bool paintable);
        return paintable
            ? Load(Path.Combine(gameDir, MarineRigArchive), MarineRigEntry, files, notes, ChaosTexturePackages, objectTreePlacement: true)
            : Load(Path.Combine(gameDir, MarineRigArchive), MarineRigEntry, files, notes);
    }

    /// <summary>
    /// The Chaos multiplayer armour sets (<c>csm_armour_mp_&lt;set&gt;_&lt;part&gt;</c> in the packages). Chaos marines share the
    /// Space Marine multiplayer rig (same Havok packfile; every piece binds by bone name). Raptor sets have no
    /// backpack piece (the jump pack is wargear).
    /// </summary>
    public static readonly string[] ChaosParts = ["torso", "legl", "legr", "arml", "armr", "helmet", "shldrl", "shldrr", "backpack"];

    // M17 chose a set by the class name ("csm - havoc" -> havoc_01, "csm - raptor" -> raptor_01: an interpretation). Removed on the owner's decision of 2026-10-28
    // ("havoc default armor yes"): a class wears what the data says, its default_armor_set (chaos_default) plus its class overrides, resolved by
    // SmContent.Customization.AppearanceResolver (spec "Players' looks in the match").

    /// <summary>The package files of a Chaos armour set that exist, for a set named explicitly (<c>--chaos &lt;set&gt;</c> of the model viewer; a played class's look comes from the resolver).</summary>
    public static IReadOnlyList<string> ChaosArmourFiles(string gameDir, string set) =>
        ChaosParts.Select(p => Path.Combine(gameDir, "data", "packages", $"csm_armour_mp_{set}_{p}.oppc")).Where(File.Exists).ToList();

    public static CharacterModel LoadChaos(string gameDir, string set, List<string>? notes = null)
    {
        var files = ChaosArmourFiles(gameDir, set);
        if (files.Count == 0) throw new FileNotFoundException($"No Chaos armour pieces for set '{set}' in the install.");
        return Load(Path.Combine(gameDir, MarineRigArchive), MarineRigEntry, files, notes, ChaosTexturePackages, objectTreePlacement: true);
    }

    /// <summary>The texture packages of a Chaos piece, best first (the diffuse maps are not in the mesh's own package).</summary>
    public static IEnumerable<string> ChaosTexturePackages(string pieceArchive) =>
        new[] { "wg", "tt1", "tt2" }.Select(v => Path.Combine(Path.GetDirectoryName(pieceArchive) ?? "", Path.GetFileNameWithoutExtension(pieceArchive) + $"_{v}.oppc"));

    public static CharacterModel Load(string rigArchive, string rigEntry, IEnumerable<string> pieceArchives, List<string>? notes = null,
        Func<string, IEnumerable<string>>? texturePackages = null, bool objectTreePlacement = false)
    {
        var rig = LoadRig(rigArchive, rigEntry);
        var boneNames = new HashSet<string>(rig.Skeleton.BoneNames, StringComparer.Ordinal);
        var bonePositions = BindPositions(rig.Skeleton);   // the viewer paths draw root cloth as the piece cache does
        var pieces = new List<CharacterPiece>();
        foreach (var path in pieceArchives)
            pieces.Add(LoadPiece(boneNames, path, notes, texturePackages, objectTreePlacement, bonePositions));
        return new CharacterModel(rig, pieces);
    }

    /// <summary>The Havok character packfile <paramref name="rigEntry"/> of <paramref name="rigArchive"/> (skeleton and animations).</summary>
    /// <exception cref="FileNotFoundException">The entry is not in the archive.</exception>
    public static HavokCharacterFile LoadRig(string rigArchive, string rigEntry)
    {
        HavokCharacterFile? rig = null;
        using (var archive = ObpkArchive.Open(rigArchive))
            archive.ReadEntries(e => e.FileName.Equals(rigEntry, StringComparison.OrdinalIgnoreCase) && rig is null,
                (_, data) => rig = new HavokCharacterFile(data.ToArray()));
        return rig ?? throw new FileNotFoundException($"Rig '{rigEntry}' not found in {Path.GetFileName(rigArchive)}.");
    }

    /// <summary>The multiplayer Space Marine rig (shared by both sides' armour).</summary>
    public static HavokCharacterFile LoadMarineRig(string gameDir) => LoadRig(gameDir, RigProfiles.Marine);

    /// <summary>A profile's rig (skeleton and animations) from a game folder (Ork O2).</summary>
    /// <exception cref="FileNotFoundException">The entry is not in the archive.</exception>
    public static HavokCharacterFile LoadRig(string gameDir, RigProfile rig) => LoadRig(rig.ArchivePath(gameDir), rig.Entry);

    /// <summary>The file of an armour package (<c>data/packages/&lt;package&gt;.oppc</c>); null when the name has a path separator, a drive colon or "..".</summary>
    public static string? PackagePath(string gameDir, string package) =>
        package.Length == 0 || package.IndexOfAny(['\\', '/', ':']) >= 0 || package.Contains("..", StringComparison.Ordinal)
            ? null : Path.Combine(gameDir, "data", "packages", package + ".oppc");

    /// <summary>
    /// One armour piece from its package file, bound to a rig with the bones <paramref name="boneNames"/> (the body of <see cref="Load"/>). Thread-safe: it
    /// only reads files, so a renderer may read pieces on a worker thread and build its meshes on the main one. <paramref name="notes"/> is not synchronised.
    /// </summary>
    /// <param name="bonePositions">The skeleton's bones' model-space origins in the bind pose (<see cref="BindPositions"/>). With object-tree placement it lets a
    /// rigid block whose node hangs under no bone (the armour's cloth, belts and fur at the tree's root) follow <see cref="ClothBone"/>
    /// (<see cref="RigidBlock.ModelSpace"/>); null leaves such a block out with a note, as before.</param>
    public static CharacterPiece LoadPiece(IReadOnlySet<string> boneNames, string path, List<string>? notes = null,
        Func<string, IEnumerable<string>>? texturePackages = null, bool objectTreePlacement = false, IReadOnlyDictionary<string, Vector3>? bonePositions = null)
    {
        string name = MeshNameOf(path);
        var model = StaticModel.Load(path, name, notes, texturePackages?.Invoke(path));
        string? rigidBone = null;
        Dictionary<int, RigidBlock>? rigid = null;
        HashSet<int>? hidden = null;
        if (objectTreePlacement)
        {
            // Chaos pieces: every mesh block has a StaticMeshVisual (MeshID = block index) naming a GEO node; the node hangs
            // under the RIG_ bone node it follows, with its own local transform. Interpretation: vertices are in the GEO
            // node's space, so placed = vertex x GEO local x the bone's model-space bind pose; "_lod" nodes are not drawn.
            (rigid, hidden) = ObjectTreePlacement(path, name, boneNames, model.Mesh.Blocks, notes, bonePositions);
        }
        else if (model.Mesh.Blocks.Any(bl => bl.Skin is null))
        {
            // The object tree's node names include the bone the rigid mesh hangs from (e.g. RIG_L_ShoulderPad).
            var bones = ObjectTreeStrings(path, name).Where(boneNames.Contains).Distinct().ToList();
            if (bones.Count == 1) rigidBone = bones[0];
            else notes?.Add($"{name}: rigid mesh names {bones.Count} skeleton bones in its .O3d; left unattached");
        }
        foreach (var bone in model.Mesh.Blocks.SelectMany(b => b.Skin?.Bones ?? []))
            if (!boneNames.Contains(bone.Name)) notes?.Add($"{name}: bone '{bone.Name}' is not in the skeleton");
        return new CharacterPiece(name, model, rigidBone) { Rigid = rigid, Hidden = hidden };
    }

    /// <summary>
    /// The mesh a piece package holds: the <c>dcm</c> named as the package (every piece but one), else the package's only <c>dcm</c>. The Emperor's Children
    /// backpack <c>csm_armour_mp_children_bp</c> holds <c>csm_armour_mp_children_backpack</c> (with its <c>O3d</c> of the same name), the one package of the
    /// install's armour whose mesh is named otherwise (fix/armour-pieces; <c>smtool armour-audit</c>). Several meshes and none named as the package: the
    /// package's name (the read then reports the missing mesh).
    /// </summary>
    public static string MeshNameOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        using var archive = ObpkArchive.Open(path);
        var meshes = archive.Entries.Where(e => archive.GroupName(e).Equals("dcm", StringComparison.OrdinalIgnoreCase)).Select(e => e.FileName).ToList();
        return meshes.Any(m => m.Equals(name, StringComparison.OrdinalIgnoreCase)) || meshes.Count != 1 ? name : meshes[0];
    }

    /// <summary>The bind-pose model-space origin of every bone of <paramref name="skeleton"/>, by name (for <see cref="LoadPiece"/>'s cloth placement).</summary>
    public static IReadOnlyDictionary<string, Vector3> BindPositions(HavokSkeleton skeleton)
    {
        var model = skeleton.ModelSpace(skeleton.ReferencePose);
        var result = new Dictionary<string, Vector3>(StringComparer.Ordinal);
        for (int i = 0; i < skeleton.BoneNames.Count; i++) result.TryAdd(skeleton.BoneNames[i], model[i].Translation);
        return result;
    }

    /// <summary>The bone free-hanging cloth follows when the skeleton has it (<see cref="ClothBone"/>).</summary>
    public const string ClothHangBone = "RIG_Pelvis";

    /// <summary>
    /// The bone a free-hanging rigid block follows (fix/armour-pieces): <see cref="ClothHangBone"/> when the skeleton has it, else <see cref="NearestBoneToTop"/>.
    /// **Interpretation**: the original simulates these blocks as cloth (their type-2 trailer, not decoded, and the "_sim" sibling node). Every such block of the
    /// install's armour (eight: the belts' tabards and loincloths, the Space Wolves fur, the Salamanders Veteran's cloth) hangs from the belt line (model-space
    /// heights 0.56 to 1.44 m, the pelvis bone at 1.22 m), so drawn rigid on the pelvis it moves with the hips the way the belt does.
    /// </summary>
    public static string? ClothBone(IReadOnlyList<Vector3> modelSpaceVertices, IReadOnlyDictionary<string, Vector3> bonePositions) =>
        bonePositions.ContainsKey(ClothHangBone) ? ClothHangBone : NearestBoneToTop(modelSpaceVertices, bonePositions);

    /// <summary>
    /// The body bone whose bind-pose origin is nearest the mean of the block's top tenth of vertices (by height, in model space), where cloth hangs from; the
    /// root, interaction, prop and look-at helpers are not candidates. Null without vertices or candidates.
    /// </summary>
    public static string? NearestBoneToTop(IReadOnlyList<Vector3> modelSpaceVertices, IReadOnlyDictionary<string, Vector3> bonePositions)
    {
        bonePositions = bonePositions.Where(kv => !IsHelperBone(kv.Key)).ToDictionary();
        if (modelSpaceVertices.Count == 0 || bonePositions.Count == 0) return null;
        float minZ = modelSpaceVertices.Min(v => v.Z), maxZ = modelSpaceVertices.Max(v => v.Z);
        float cut = maxZ - 0.1f * (maxZ - minZ);
        var top = modelSpaceVertices.Where(v => v.Z >= cut).ToList();
        var centre = top.Aggregate(Vector3.Zero, (a, v) => a + v) / top.Count;
        return bonePositions.OrderBy(kv => Vector3.DistanceSquared(kv.Value, centre)).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
    }

    static bool IsHelperBone(string bone) =>
        bone.Equals("RIG_Root", StringComparison.OrdinalIgnoreCase) || bone.Contains("Interaction", StringComparison.OrdinalIgnoreCase)
        || bone.Contains("_Prop", StringComparison.OrdinalIgnoreCase) || bone.Contains("LookAt", StringComparison.OrdinalIgnoreCase);

    static (Dictionary<int, RigidBlock>, HashSet<int>) ObjectTreePlacement(string archivePath, string name, IReadOnlySet<string> boneNames, IReadOnlyList<SmFormats.Dcm.DcmBlock> blocks,
        List<string>? notes, IReadOnlyDictionary<string, Vector3>? bonePositions)
    {
        var isRigid = blocks.Select(bl => bl.Skin is null).ToList();
        BodValue? root = null;
        using (var archive = ObpkArchive.Open(archivePath))
            archive.ReadEntries(e => archive.GroupName(e).Equals("O3d", StringComparison.OrdinalIgnoreCase) && e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase),
                (_, data) => root = BodDocument.Read(data).Root);
        var nodes = new Dictionary<string, (string? Parent, Matrix4x4 Local)>(StringComparer.Ordinal);
        static IEnumerable<BodValue> Items(BodValue? v) => v switch
        {
            BodList l => l.Items.Select(i => Unref(i.Value)), BodArray a => a.Items.Select(Unref), _ => [],
        };
        static BodValue Unref(BodValue v) { while (v is BodRef r) v = r.Value; return v; }
        static float[] Floats(BodValue? v, float fallback) { var f = Items(v).OfType<BodFloat>().Select(x => x.Value).ToArray(); return f.Length == 3 ? f : [fallback, fallback, fallback]; }
        void Walk(BodValue? node, string? parent)
        {
            if (Unref(node!) is not BodStruct s || s["Name"] is not BodString { Text: { } n }) return;
            var p = Floats(s["Position"], 0); var r = Floats(s["Rotation"], 0); var sc = Floats(s["Scale"], 1);
            const float Deg = MathF.PI / 180f;
            var local = Matrix4x4.CreateScale(sc[0], sc[1], sc[2]) * Matrix4x4.CreateRotationX(r[0] * Deg) * Matrix4x4.CreateRotationY(r[1] * Deg)
                        * Matrix4x4.CreateRotationZ(r[2] * Deg) * Matrix4x4.CreateTranslation(p[0], p[1], p[2]);
            nodes[n] = (parent, local);
            foreach (var c in Items(Unref(s["Children"] ?? BodNull.Instance))) Walk(c, n);
        }
        var rigid = new Dictionary<int, RigidBlock>();
        var hidden = new HashSet<int>();
        if (root is null || Unref(root) is not BodStruct o) return (rigid, hidden);
        foreach (var c in Items(Unref(o["Skeleton"] ?? BodNull.Instance) is BodStruct sk ? sk["Children"] : null)) Walk(c, null);
        foreach (var v in Items(o["Visuals"]))
        {
            if (v is not BodStruct { Class.Text: "StaticMeshVisual" } vs || vs["RefNode"] is not BodString { Text: { } refNode } || vs["MeshID"] is not BodInt { Value: var id }) continue;
            if (id < 0 || id >= isRigid.Count) continue;
            if (refNode.Contains("_lod", StringComparison.OrdinalIgnoreCase)) { hidden.Add(id); continue; }
            if (!isRigid[id]) continue;
            // Walk up to the nearest skeleton bone, composing local transforms (child first).
            var m = Matrix4x4.Identity; string? at = refNode; string? bone = null;
            while (at is not null && nodes.TryGetValue(at, out var info))
            {
                if (boneNames.Contains(at)) { bone = at; break; }
                m *= info.Local; at = info.Parent;
            }
            // The cloth fallback only for a type-2 (simulated) block whose node is in the tree (it then reached the tree's root); anything else is left out as before.
            if (bone is null && bonePositions is not null && nodes.ContainsKey(refNode) && blocks[id].Simulation is not null
                && ClothBone(blocks[id].Positions.Select(p => Vector3.Transform(p, m)).ToList(), bonePositions.Where(kv => boneNames.Contains(kv.Key)).ToDictionary()) is { } nearest)
                rigid[id] = new RigidBlock(nearest, m) { ModelSpace = true, Node = refNode };   // m: the node chain up to the root, so vertex x m is model space
            else if (bone is null) notes?.Add($"{name}: rigid block {id} ('{refNode}') hangs under no skeleton bone; left out");
            else rigid[id] = new RigidBlock(bone, m);
        }
        return (rigid, hidden);
    }

    static IReadOnlyList<string> ObjectTreeStrings(string archivePath, string name)
    {
        IReadOnlyList<string> strings = [];
        using var archive = ObpkArchive.Open(archivePath);
        archive.ReadEntries(e => archive.GroupName(e).Equals("O3d", StringComparison.OrdinalIgnoreCase) && e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase),
            (_, data) => strings = BodDocument.ReadStrings(data));
        return strings;
    }
}

/// <param name="RigidBone">For meshes without skinning: the bone they follow; their vertices are in the bind pose.</param>
public sealed record CharacterPiece(string Name, StaticModel Model, string? RigidBone)
{
    /// <summary>Chaos pieces: per un-skinned block index, the bone it follows and its bone-local transform (row-vector) from the object tree.</summary>
    public IReadOnlyDictionary<int, RigidBlock>? Rigid { get; init; }
    /// <summary>Chaos pieces: block indices not drawn (the "_lod" nodes).</summary>
    public IReadOnlySet<int>? Hidden { get; init; }
}

/// <param name="Local">The block's vertices to the bone's bind space (row-vector): placed = vertex x <see cref="Local"/> x the bone's model-space bind pose; with
/// <see cref="ModelSpace"/> it maps the vertices to model space directly (placed = vertex x <see cref="Local"/>), the bone is the one they follow.</param>
public sealed record RigidBlock(string Bone, Matrix4x4 Local)
{
    /// <summary>
    /// The block hangs under no bone of the object tree (the armour's cloth, belts and fur sit at its root: fix/armour-pieces); <see cref="Bone"/> is the one
    /// it follows (<see cref="CharacterModel.ClothBone"/>, an interpretation) and <see cref="Local"/> already gives model space.
    /// </summary>
    public bool ModelSpace { get; init; }
    /// <summary>The object-tree node the block's visual names (set for <see cref="ModelSpace"/> blocks; reported by the armour audit), or null.</summary>
    public string? Node { get; init; }
}
