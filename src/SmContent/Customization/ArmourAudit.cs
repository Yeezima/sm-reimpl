using SmSim;

namespace SmContent.Customization;

/// <summary>One look the audit resolved: what it is (a whole set, a slot's offered piece, a class's default look) and what went wrong with it.</summary>
/// <param name="Subject">"set &lt;name&gt; (&lt;display&gt;)", "piece &lt;slot&gt; &lt;name&gt;" or "default".</param>
/// <param name="Problems">Empty when every slot the look should show has a piece whose packages load and draw.</param>
/// <param name="Gaps">Explained gaps that leave the look whole: an extra attachment of a piece (a torso's belt) whose package the install does not have at
/// all, while the piece's other packages draw.</param>
public sealed record ArmourAuditCase(string Race, string ClassKey, string Subject, IReadOnlyList<string> Problems, IReadOnlyList<string> Gaps);

/// <summary>What one armour package gave: whether it loaded and how many of its surfaces a renderer would draw.</summary>
/// <param name="Problem">Why nothing (or not everything) is drawn, or null.</param>
public sealed record ArmourPackageCheck(string Package, int Surfaces, int Drawn, int WithoutDiffuse, string? Problem)
{
    /// <summary>The package file is not in the install.</summary>
    public bool Missing { get; init; }
    /// <summary>Drawn blocks that hang under no bone of the object tree and follow the bone nearest their top ("block 0 ('GEO_cloth') on RIG_Pelvis"; <see cref="RigidBlock.ModelSpace"/>).</summary>
    public IReadOnlyList<string> FreeHanging { get; init; } = [];
}

/// <summary>
/// The armour audit (fix/armour-pieces, the tester's report of 2026-10-29: a whole set drew no torso, another no backpack): for every class of both races it
/// resolves the class's default look, every set of the race worn whole, and every offered piece of every slot, the way the customizer and the match do
/// (<see cref="AppearanceResolver"/> with a <see cref="ResolveOptions.PieceLookup"/>), then loads every package the look names with
/// <see cref="CharacterModel.LoadPiece"/> on the multiplayer rig and counts the surfaces the client's piece builder (<c>client/PieceCache.cs</c>) would draw: a
/// block that is not a "_lod" node, has triangles and is skinned or hangs from a bone of the skeleton. A slot the look should show that ends with no drawn surface
/// is a problem. Read-only (the install is opened with read access only); engine-free so smtool and an install-gated test share it.
/// </summary>
public sealed class ArmourAudit
{
    readonly string _gameDir;
    readonly IReadOnlySet<string> _bones;
    readonly IReadOnlyDictionary<string, System.Numerics.Vector3>? _bonePositions;
    readonly Dictionary<string, ArmourPackageCheck> _packages = new(StringComparer.OrdinalIgnoreCase);

    public ArmourAudit(string gameDir, IReadOnlySet<string> boneNames, IReadOnlyDictionary<string, System.Numerics.Vector3>? bonePositions = null)
    { _gameDir = gameDir; _bones = boneNames; _bonePositions = bonePositions; }

    /// <summary>Every package checked so far, by name.</summary>
    public IReadOnlyDictionary<string, ArmourPackageCheck> Packages => _packages;

    /// <summary>The audit with the multiplayer marine rig's bones (both races share the rig).</summary>
    public static ArmourAudit ForInstall(string gameDir)
    {
        var skeleton = CharacterModel.LoadMarineRig(gameDir).Skeleton;
        return new(gameDir, new HashSet<string>(skeleton.BoneNames, StringComparer.Ordinal), CharacterModel.BindPositions(skeleton));
    }

    /// <summary>Runs every case of every class (one class per race and role); see the type's summary.</summary>
    public List<ArmourAuditCase> Run(CustomizationCatalog catalog, Func<string, ArmourPiece?>? pieceLookup = null)
    {
        var cases = new List<ArmourAuditCase>();
        var options = new ResolveOptions { PieceLookup = pieceLookup };
        foreach (var cls in catalog.Classes)
        {
            if (catalog.Race(cls.Race) is not { } race) continue;
            cases.Add(Check(race, cls, "default", AppearanceCode.Default, catalog, options));
            var sets = CustomizationCatalog.CodeSets(race);
            for (int i = 0; i < sets.Count; i++)
                cases.Add(Check(race, cls, $"set {sets[i].Name} ({sets[i].DisplayName}){(sets[i].Customizable ? "" : " [whole]")}",
                    AppearanceCode.Default.WithPredefinedSet((byte)i), catalog, options, sets[i]));
            for (int s = 0; s < AppearanceCode.Slots; s++)
            {
                var slot = (ArmourSlot)s;
                var offered = race.PiecesFor(ProfileWire.SlotName(slot));
                for (int i = 0; i < offered.Count; i++)
                    cases.Add(Check(race, cls, $"piece {ProfileWire.SlotName(slot)} {offered[i].Name}", AppearanceCode.Default.WithPiece(slot, (byte)i), catalog, options));
            }
        }
        return cases;
    }

    ArmourAuditCase Check(RaceCatalog race, ClassCustomization cls, string subject, AppearanceCode code, CustomizationCatalog catalog, ResolveOptions options, ArmourSet? set = null)
    {
        var problems = new List<string>();
        var gaps = new List<string>();
        ResolvedAppearance look;
        try { look = AppearanceResolver.Resolve(code, cls.Key, catalog, options); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            return new ArmourAuditCase(race.Race, cls.Key, subject, [$"does not resolve ({ex.Message})"], []);
        }
        problems.AddRange(look.Notes);
        foreach (var slot in CustomizationCatalog.ArmourSlots)
        {
            if (look.HiddenSlots.Contains(slot)) continue;
            if (look.Piece(slot) is not { } piece)
            {
                problems.Add(set is null ? $"{slot}: no piece" : $"{slot}: the set names no piece for it (items: {string.Join(", ", set.Items.Values)})");
                continue;
            }
            if (piece.Packages.Count == 0) { problems.Add($"{slot}: piece {piece.Name} has no visual package"); continue; }
            int drawn = piece.Packages.Sum(p => Package(p).Drawn);
            foreach (var package in piece.Packages)
            {
                var r = Package(package);
                if (r.Problem is null) continue;
                // A missing extra attachment (the DLC torsos' belts) leaves the piece whole when another package draws: an explained gap.
                if (drawn > 0 && r.Missing) gaps.Add($"{slot}: {piece.Name} package {package}: {r.Problem}");
                else problems.Add($"{slot}: {piece.Name} package {package}: {r.Problem}");
            }
            if (drawn == 0 && piece.Packages.All(p => Package(p).Problem is null)) problems.Add($"{slot}: piece {piece.Name} draws no surface");
        }
        foreach (var extra in look.Pieces.Where(p => !CustomizationCatalog.ArmourSlots.Contains(p.Slot)))
            foreach (var package in extra.Packages)
                if (Package(package).Problem is { } why) problems.Add($"{extra.Slot}: {extra.Name} package {package}: {why}");
        return new ArmourAuditCase(race.Race, cls.Key, subject, problems, gaps);
    }

    /// <summary>Loads one package (once) and counts what would be drawn.</summary>
    public ArmourPackageCheck Package(string package)
    {
        if (_packages.TryGetValue(package, out var known)) return known;
        ArmourPackageCheck result;
        string? path = CharacterModel.PackagePath(_gameDir, package);
        if (path is null || !File.Exists(path)) result = new(package, 0, 0, 0, "not in the install") { Missing = true };
        else
        {
            try
            {
                var notes = new List<string>();
                var piece = CharacterModel.LoadPiece(_bones, path, notes, CharacterModel.ChaosTexturePackages, objectTreePlacement: true, _bonePositions);
                int surfaces = 0, drawn = 0, noDiffuse = 0, unattached = 0;
                var freeHanging = new List<string>();
                var why = new List<string>();
                foreach (var s in piece.Model.Surfaces)
                {
                    surfaces++;
                    var block = piece.Model.Mesh.Blocks[s.BlockIndex];
                    if (piece.Hidden?.Contains(s.BlockIndex) == true) continue;
                    if (block.Positions.Length == 0 || s.Range.IndexCount < 3) continue;
                    bool attached = block.Skin is not null
                        || (piece.Rigid is not null && piece.Rigid.TryGetValue(s.BlockIndex, out var rb) && _bones.Contains(rb.Bone))
                        || (piece.RigidBone is { } b && _bones.Contains(b));
                    if (!attached) { why.Add($"block {s.BlockIndex} not attached"); unattached++; continue; }
                    drawn++;
                    if (piece.Rigid?.GetValueOrDefault(s.BlockIndex) is { ModelSpace: true } free) freeHanging.Add($"block {s.BlockIndex} ('{free.Node}') on {free.Bone}");
                    if (s.DiffuseDds is null) noDiffuse++;
                }
                piece.Model.ReleaseTextures();
                string? problem = drawn == 0 ? $"no drawable surface of {surfaces} ({string.Join("; ", why.Concat(notes).Distinct().Take(6))})"
                    : unattached > 0 ? $"{unattached} surface(s) not attached ({string.Join("; ", why.Concat(notes).Distinct().Take(6))})" : null;
                result = new(package, surfaces, drawn, noDiffuse, problem) { FreeHanging = freeHanging.Distinct().ToList() };
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
            {
                result = new(package, 0, 0, 0, $"unreadable ({ex.GetType().Name}: {ex.Message})");
            }
        }
        _packages[package] = result;
        return result;
    }
}
