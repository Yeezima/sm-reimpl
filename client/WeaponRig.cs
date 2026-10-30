using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmContent;
using SmContent.Customization;
using SmSim;
using Num = System.Numerics;
using Role = SmContent.HandRole;

namespace SmClient;

/// <summary>
/// M24: the weapon models (original meshes and diffuse textures from the weapons' packages), loaded on demand and shared by every
/// character. A model's visible parts are baked into one <see cref="ArrayMesh"/> in the weapon's root space (vertex × the part's node
/// transform, as <see cref="WeaponModel"/> describes), so a weapon costs one mesh instance per character. Worn models (jump packs, the
/// Devastator backpack) are baked per rig bone instead. Label: original asset loaded; placement from the wargear's own mount fields
/// (supported by evidence, docs/discovery.md "Weapon models"); belts, cables and chain teeth are drawn rigid in the bind pose (placeholder).
/// </summary>
public sealed class WeaponAssets
{
    readonly string _gameDir;
    readonly HashSet<string> _bones;
    readonly Dictionary<string, string> _canonicalBone;
    readonly Dictionary<string, Texture2D?> _textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, WeaponAsset?> _cache = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _notes;
    readonly MapMaterials _materials = new();   // painted backpacks and jump packs (wargear materials with a paint mask)
    readonly PaintedMaterialCache _packMaterials = new();   // a worn jump pack's materials for a look's backpack paint, shared by every character in that paint

    public WeaponModels Catalogue { get; }
    public int ModelsLoaded => _cache.Values.Count(a => a is not null);
    public double LoadMilliseconds { get; private set; }
    public double CatalogueMilliseconds { get; }

    WeaponAssets(string gameDir, WeaponModels catalogue, IReadOnlyList<string> bones, List<string> notes, double catalogueMs)
    {
        _gameDir = gameDir; Catalogue = catalogue; _notes = notes; CatalogueMilliseconds = catalogueMs;
        _bones = bones.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _canonicalBone = bones.GroupBy(b => b, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="wargear">The wargear files of every weapon, melee choice and jump pack the simulation knows.</param>
    public static WeaponAssets Load(string gameDir, MarineAssets marine, IEnumerable<string> wargear, List<string> notes) =>
        Load(gameDir, marine, RigProfiles.Marine, wargear, notes);

    /// <summary>The weapons of a character on another rig (Ork O6: the character's bones, the profile's body visual and weapon mounts).</summary>
    public static WeaponAssets Load(string gameDir, MarineAssets character, RigProfile rig, IEnumerable<string> wargear, List<string> notes)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var bones = character.Model.Rig.Skeleton.BoneNames;
        var names = wargear.Where(n => n.Length > 0 && !n.Equals("default", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var catalogue = WeaponModels.Load(gameDir, rig, notes, names, bones);
        return new WeaponAssets(gameDir, catalogue, bones, notes, watch.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// The weapons of a rig from a catalogue that was read elsewhere (Ork O7a: <see cref="WeaponModels.Load(string, RigProfile, List{string}?, IEnumerable{string}?, IReadOnlyList{string}?)"/>
    /// is engine-free, so the networked view reads it on a worker and only this wrapper, with its Godot caches, is made on the main thread).
    /// </summary>
    public static WeaponAssets FromCatalogue(string gameDir, IReadOnlyList<string> bones, WeaponModels catalogue, List<string> notes, double catalogueMs) =>
        new(gameDir, catalogue, bones, notes, catalogueMs);

    public string? CanonicalBone(string name) => _canonicalBone.GetValueOrDefault(name);

    readonly Dictionary<(WeaponAsset, string), (string Bone, Transform3D Local)?> _wornLocators = new();

    /// <summary>
    /// M44: a locator of a worn model (the jump pack's <c>fx_l_jet</c> ...): the skeleton bone it hangs under and its transform in that bone's space (as the
    /// worn meshes are placed: the object tree's transforms up to the bone); null when the tree has no such node or no bone above it. Cached.
    /// </summary>
    public (string Bone, Transform3D Local)? WornLocator(WeaponAsset asset, string locator)
    {
        if (_wornLocators.TryGetValue((asset, locator), out var known)) return known;
        (string, Transform3D)? found = null;
        if (asset.Model.Tree.UnderBone(locator, _bones.Contains) is { } placed && CanonicalBone(placed.Bone) is { } bone)
            found = (bone, MarineAssets.ToGodot(placed.Local));
        return _wornLocators[(asset, locator)] = found;
    }

    /// <summary>The attachment's model and meshes (read once; null, with a note, when it cannot be read).</summary>
    public WeaponAsset? Get(WeaponAttachment attachment)
    {
        string key = attachment.VisualPackage + "|" + attachment.VisualName;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        WeaponAsset? asset = null;
        try
        {
            var model = WeaponModel.Load(_gameDir, attachment, _notes);
            asset = Build(model, attachment);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            // Any failure of one package (truncated data, bad index, overflow ...) leaves that weapon undrawn; the null is cached so it is read and noted once.
            _notes.Add($"weapon model {key}: {ex.GetType().Name}: {ex.Message}; not drawn");
        }
        watch.Stop();
        LoadMilliseconds += watch.Elapsed.TotalMilliseconds;
        _cache[key] = asset;
        GD.Print(asset is null ? $"  weapon model {key}: not available" :
            $"  weapon model {key}: {asset.Triangles} triangles, {asset.Parts} parts drawn ({asset.Hidden} hidden, {asset.Rigid} rigid placeholders), {(asset.Muzzle is null ? "no muzzle" : "muzzle")}{(asset.Worn.Count > 0 ? $", worn on {string.Join("/", asset.Worn.Keys)}" : "")}; {watch.Elapsed.TotalMilliseconds:F1} ms");
        return asset;
    }

    PaintScheme? _marineScheme, _chaosScheme;
    bool _marineResolved, _chaosResolved;

    /// <summary>The side's paint scheme, resolved once per <see cref="WeaponAssets"/> (null when it is absent: nothing is looked up again).</summary>
    PaintScheme? SchemeOf(bool chaos)
    {
        if (chaos)
        {
            if (!_chaosResolved) { _chaosScheme = MarineAssets.SchemeFor(_gameDir, _notes, chaos: true); _chaosResolved = true; }
            return _chaosScheme;
        }
        if (!_marineResolved) { _marineScheme = MarineAssets.SchemeFor(_gameDir, _notes, chaos: false); _marineResolved = true; }
        return _marineScheme;
    }

    /// <summary>
    /// Backpack weapons and jump packs whose material names a paint mask are painted with the side's default paint scheme (Chaos packages are
    /// named "csm_*"), slot "backpack"; null for every other surface. **Provisional approximation**, see <see cref="MapMaterials"/>.
    /// </summary>
    Material? PaintedMaterial(WeaponModel model, StaticModelSurface surface) =>
        PreparePaint(model, surface) is { } prepared && DefaultSet(model) is { } set ? _materials.MakePainted(prepared, MapMaterials.PaintColours.From(set), null, null) : null;

    /// <summary>The colour-independent part of a surface whose material names a paint mask (null for every other surface, or when the mask or diffuse cannot be read).</summary>
    MapMaterials.PaintedSurface? PreparePaint(WeaponModel model, StaticModelSurface surface) =>
        surface.Material is { } bmat && bmat.Textures.ContainsKey("PaintMask") ? _materials.PreparePainted(bmat, model.Model.Textures, _notes) : null;

    /// <summary>The side's default paint of a model (the scheme's slot of its package name; Chaos packages are named "csm_*"), or null without a scheme.</summary>
    PaintSet? DefaultSet(WeaponModel model) =>
        SchemeOf(model.Package.StartsWith("csm_", StringComparison.OrdinalIgnoreCase)) is { } scheme ? PaintSchemes.For(scheme, model.Package) : null;

    /// <summary>
    /// The material of a worn jump pack's surface in a look's pack paint (<see cref="WornPackPaint"/>: the backpack's), shared by every character wearing that paint
    /// (<see cref="PaintedMaterialKey"/> over the surface and the colours; bounded like the armour's cache). Cheap: only uniforms differ.
    /// </summary>
    public Material PackMaterial(WornPaintSurface surface, ResolvedPaint paint) =>
        _packMaterials.Get(PaintedMaterialKey.Of(surface.Key, paint, null, null), () => _materials.MakePainted(surface.Surface, MapMaterials.PaintColours.From(paint), null, null));

    WeaponAsset Build(WeaponModel model, WeaponAttachment attachment)
    {
        var rigidMesh = new ArrayMesh();
        var magazineMesh = new ArrayMesh();
        Vector3 magazineCentre = Vector3.Zero, magazineSize = Vector3.Zero;
        string? magazineNode = null;
        var wornMeshes = new Dictionary<string, ArrayMesh>(StringComparer.Ordinal);
        var wornPaint = new Dictionary<ArrayMesh, List<WornPaintSurface?>>();   // per worn mesh, one entry per surface in the order added (null: not a painted surface)
        int triangles = 0, drawn = 0, hidden = 0, rigid = 0;
        foreach (var part in model.Parts)
        {
            if (!part.Visible) { hidden++; continue; }
            ArrayMesh target;
            Num.Matrix4x4 matrix;
            if (attachment.IsWorn)
            {
                if (model.WornPlacement(part, _bones.Contains) is not { } placed || CanonicalBone(placed.Bone) is not { } bone)
                {
                    _notes.Add($"{model.Package}: worn part '{part.Node}' hangs under no skeleton bone{(part.Skinned ? " (skinned)" : "")}; not drawn");
                    continue;
                }
                if (!wornMeshes.TryGetValue(bone, out target!)) wornMeshes[bone] = target = new ArrayMesh();
                matrix = placed.PartToBone;
            }
            else { target = rigidMesh; matrix = part.ToRoot; }
            var block0 = model.Model.Mesh.Blocks[part.BlockIndex];

            // M35: the detachable magazine is a mesh of its own around its own centre (so a copy can fall as a body of its own): a part of its own, or
            // (Chaos bolter and bolt pistol) the shells of the body block that lie where the magazine is.
            bool wholeMagazine = !attachment.IsWorn && model.Magazine is { } magazinePart && ReferenceEquals(magazinePart, part);
            bool[]? inMagazine = null;
            if (!attachment.IsWorn && model.EmbeddedMagazine is { } embedded && ReferenceEquals(embedded.Part, part))
            {
                var (picked, shells) = MeshShells.PickInBox(block0.Positions, block0.Indices, matrix, embedded.Min, embedded.Max, embedded.MaxShells);
                if (shells > 0) { inMagazine = picked; _notes.Add($"{model.Package}: magazine = {shells} shell(s) of '{part.Node}' in the magazine box (interpretation)"); }
                else _notes.Add($"{model.Package}: no shell of '{part.Node}' lies in the magazine box; no magazine drop");
            }
            var magazineShift = Vector3.Zero;
            if (wholeMagazine || inMagazine is not null)
            {
                var low = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var high = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int v = 0; v < block0.Positions.Length; v++)
                {
                    if (inMagazine is not null && !inMagazine[v]) continue;
                    var q = Num.Vector3.Transform(block0.Positions[v], matrix);
                    low = new Vector3(Math.Min(low.X, q.X), Math.Min(low.Y, q.Y), Math.Min(low.Z, q.Z));
                    high = new Vector3(Math.Max(high.X, q.X), Math.Max(high.Y, q.Y), Math.Max(high.Z, q.Z));
                }
                if (low.X <= high.X) { magazineShift = magazineCentre = (low + high) * 0.5f; magazineSize = high - low; magazineNode = inMagazine is null ? part.Node : part.Node + " (magazine shells)"; }
                else { wholeMagazine = false; inMagazine = null; }
            }

            bool flip = matrix.GetDeterminant() < 0f;
            bool any = false;
            // Adds a surface of this part to a mesh: the triangles whose first vertex passes keep, positions moved by shift.
            int Emit(StaticModelSurface surface, ArrayMesh into, Vector3 shift, Func<int, bool> keep)
            {
                var block = model.Model.Mesh.Blocks[surface.BlockIndex];
                int n = block.Positions.Length;
                if (n == 0 || surface.Range.IndexCount < 3) return 0;
                var kept = new List<int>(surface.Range.IndexCount);
                for (int i = 0; i + 2 < surface.Range.IndexCount; i += 3)
                {
                    int a = block.Indices[surface.Range.StartIndex + i], b = block.Indices[surface.Range.StartIndex + i + 1], c = block.Indices[surface.Range.StartIndex + i + 2];
                    if (!keep(a)) continue;
                    if (flip) (b, c) = (c, b);
                    kept.Add(a); kept.Add(b); kept.Add(c);
                }
                if (kept.Count == 0) return 0;
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                var vertices = new Vector3[n];
                var normals = new Vector3[n];
                var tangents = new float[4 * n];
                var uv = new Vector2[n];
                var uv2 = new Vector2[n];
                for (int v = 0; v < n; v++)
                {
                    var p = Num.Vector3.Transform(block.Positions[v], matrix);
                    vertices[v] = new Vector3(p.X, p.Y, p.Z) - shift;
                    var nrm = Num.Vector3.TransformNormal(block.Normals[v], matrix);
                    nrm = nrm.LengthSquared() > 1e-12f ? Num.Vector3.Normalize(nrm) : Num.Vector3.UnitZ;
                    normals[v] = new Vector3(nrm.X, nrm.Y, nrm.Z);
                    var t = block.Tangents[v];
                    var tv = Num.Vector3.TransformNormal(new Num.Vector3(t.X, t.Y, t.Z), matrix);
                    tv = tv.LengthSquared() > 1e-12f ? Num.Vector3.Normalize(tv) : Num.Vector3.UnitX;
                    (tangents[4 * v], tangents[4 * v + 1], tangents[4 * v + 2], tangents[4 * v + 3]) = (tv.X, tv.Y, tv.Z, flip ? -t.W : t.W);
                    uv[v] = new Vector2(block.Uv0[v].X, block.Uv0[v].Y);
                    uv2[v] = new Vector2(block.Uv1[v].X, block.Uv1[v].Y);
                }
                arrays[(int)Mesh.ArrayType.Vertex] = vertices;
                arrays[(int)Mesh.ArrayType.Normal] = normals;
                arrays[(int)Mesh.ArrayType.Tangent] = tangents;
                arrays[(int)Mesh.ArrayType.TexUV] = uv;
                arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
                arrays[(int)Mesh.ArrayType.Index] = kept.ToArray();
                into.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
                Material? baked;
                if (attachment.IsWorn)
                {
                    // A worn model keeps its paintable surfaces so a look can repaint them (WeaponRig.SetPackPaint); the baked material is the side's default paint.
                    var prepared = PreparePaint(model, surface);
                    if (!wornPaint.TryGetValue(into, out var list)) wornPaint[into] = list = new List<WornPaintSurface?>();
                    list.Add(prepared is null ? null : new WornPaintSurface(prepared, model.Package + "|" + surface.Material?.Name));
                    baked = prepared is not null && DefaultSet(model) is { } set ? _materials.MakePainted(prepared, MapMaterials.PaintColours.From(set), null, null) : null;
                }
                else baked = PaintedMaterial(model, surface);
                into.SurfaceSetMaterial(into.GetSurfaceCount() - 1, baked ?? ModelViewer.MaterialFor(surface, _textures, _notes));
                return kept.Count / 3;
            }
            foreach (var surface in model.Model.Surfaces.Where(s => s.BlockIndex == part.BlockIndex))
            {
                int made;
                if (inMagazine is not null)
                    made = Emit(surface, rigidMesh, Vector3.Zero, v => !inMagazine[v]) + Emit(surface, magazineMesh, magazineShift, v => inMagazine[v]);
                else if (wholeMagazine) made = Emit(surface, magazineMesh, magazineShift, _ => true);
                else made = Emit(surface, target, Vector3.Zero, _ => true);
                triangles += made;
                any |= made > 0;
            }
            if (any) { drawn++; if (part.Simulated || part.Skinned) rigid++; }
        }
        var magazine = magazineNode is not null && magazineMesh.GetSurfaceCount() > 0 ? new WeaponMagazine(magazineNode, magazineMesh, magazineCentre, magazineSize) : null;
        var paintable = wornMeshes.Where(w => wornPaint.TryGetValue(w.Value, out var l) && l.Any(s => s is not null)).ToDictionary(w => w.Key, w => wornPaint[w.Value].ToArray(), StringComparer.Ordinal);
        return new WeaponAsset(model, attachment.IsWorn ? null : rigidMesh, wornMeshes, triangles, drawn, hidden, rigid, magazine) { WornPaint = paintable };
    }
}

/// <summary>A paintable surface of a worn model: its colour-independent material data and the key of the surface (package and material) for the material cache.</summary>
public sealed record WornPaintSurface(MapMaterials.PaintedSurface Surface, string Key);

/// <summary>A weapon's detachable magazine (M35): its mesh around <see cref="Centre"/>, which is in the weapon's root space (the model's own axes, Z up), and its extent along those axes.</summary>
public sealed record WeaponMagazine(string Node, ArrayMesh Mesh, Vector3 Centre, Vector3 Size);

/// <summary>A weapon model with its baked meshes: <see cref="Mesh"/> (held/stowed weapons, in the model's root space) or <see cref="Worn"/> (per rig bone, in that bone's space).</summary>
public sealed class WeaponAsset
{
    public WeaponModel Model { get; }
    public ArrayMesh? Mesh { get; }
    public IReadOnlyDictionary<string, ArrayMesh> Worn { get; }
    public int Triangles { get; }
    public int Parts { get; }
    public int Hidden { get; }
    public int Rigid { get; }
    public WeaponLocator? Muzzle => Model.Muzzle;
    /// <summary>The detachable magazine drawn as a mesh of its own (M35), or null; <see cref="Mesh"/> does not contain it.</summary>
    public WeaponMagazine? Magazine { get; }
    /// <summary>For a worn model: per rig bone (as <see cref="Worn"/>), the paintable surface at each surface index of that mesh (null: unpainted); bones with none are absent.</summary>
    public IReadOnlyDictionary<string, WornPaintSurface?[]> WornPaint { get; init; } = new Dictionary<string, WornPaintSurface?[]>();

    public WeaponAsset(WeaponModel model, ArrayMesh? mesh, IReadOnlyDictionary<string, ArrayMesh> worn, int triangles, int parts, int hidden, int rigid, WeaponMagazine? magazine = null)
    {
        Model = model; Mesh = mesh; Worn = worn; Triangles = triangles; Parts = parts; Hidden = hidden; Rigid = rigid; Magazine = magazine;
    }
}

/// <summary>
/// The weapons one character carries (M24): the primary and the pistol in hand or stowed, the melee weapon, and the worn models
/// (jump pack, Devastator backpack), attached with <see cref="BoneAttachment3D"/> nodes under the character's skeleton so the
/// animations carry them. Where each weapon hangs comes from its wargear (<c>wield_info_by_upgrade</c>, <c>stow_info</c>).
/// <para>
/// **Interpretation** (when the original draws what is not decoded): the weapon in hand is the simulation's current slot; in a switch the old ranged weapon
/// goes onto its stow marker and then the new one comes into the hand at the moments <see cref="SmContent.SwitchPlan"/> gives (fix/stow-right; before it, one swap at the
/// switch clip's tag; pass 1: half the switch time); the Assault and Raptor classes (those with a jump pack) hold
/// their melee weapon in the right hand at all times; the other classes draw it only during a melee attack and stow it (or, for the
/// combat knife, hide it) otherwise; a melee weapon wielded where the ranged one is (the chest prop of two-handed weapons) puts the
/// ranged weapon away while it is out.
/// </para>
/// </summary>
public sealed class WeaponRig
{
    // Roles are SmContent.HandRole; Grenade (Ork O6) is a thrown item held in hand only while SetGrenadeHeld says so (hidden otherwise), not a ranged weapon (no muzzle or magazine queries).

    sealed class Item
    {
        public Role Role;
        public WeaponAttachment Attachment = null!;
        public WeaponAsset Asset = null!;
        public BoneAttachment3D Node = null!;
        public Node3D Holder = null!;
        public MeshInstance3D? Magazine;   // M35: the detachable magazine, a mesh of its own under the holder (null: the model has none)
        public Transform3D Wield, Stow;
        public string? Bone;
        public int Mode = -1;          // 0 stowed, 1 in hand, 2 hidden
        public bool InHand;
    }

    readonly Skeleton3D _skeleton;
    readonly WeaponAssets _assets;
    readonly List<Item> _items = new();
    readonly List<Node> _worn = new();
    readonly List<WeaponAsset> _wornAssets = new();   // M44: for the worn models' locators (TryWornMarker)
    readonly List<(MeshInstance3D Mesh, WornPaintSurface?[] Surfaces)> _packMeshes = new();   // the worn jump pack's meshes that take a look's paint
    ResolvedPaint? _packPaint;
    (int Class, int Primary, int Pistol, string? Melee, string? Pack) _key = (-1, -1, -1, null, null);
    bool _alive = true;
    /// <summary>Set when building or updating the nodes threw: the rig stays empty and is not touched again (one bad weapon must not stop the frame).</summary>
    public bool Faulted { get; private set; }
    bool _first = true;
    WeaponSlot _shown, _target;
    float _timer;

    public bool AlwaysMelee { get; private set; }
    /// <summary>The thrown item's wargear the rig was configured with (Ork O6), or null.</summary>
    public string? GrenadeWargear { get; private set; }
    bool _grenadeHeld;

    /// <summary>
    /// Ork O6: shows the thrown item in hand (its wield mount) or hides it; takes effect at the next <see cref="Update"/>. Nothing happens without a
    /// <c>grenade</c> in <see cref="Configure"/>. The caller decides when: from the throw's start to its release (none of the Space Marine path calls this).
    /// </summary>
    public void SetGrenadeHeld(bool held) => _grenadeHeld = held;
    /// <summary>The model nodes drawn (weapons held or stowed, worn models) for the info line.</summary>
    public int WeaponCount => _items.Count;
    public int WornCount => _worn.Count;
    public IEnumerable<string> Description => _items.Select(i => $"{i.Role}:{i.Attachment.VisualName}({(i.Mode == 1 ? "hand" : i.Mode == 0 ? "stowed" : "hidden")})");

    public WeaponRig(Skeleton3D skeleton, WeaponAssets assets) { _skeleton = skeleton; _assets = assets; }

    /// <summary>
    /// (Re)builds the nodes for a loadout when <paramref name="key"/> (class, primary, pistol, melee wargear, pack wargear) differs from
    /// the last call; false when nothing changed. Compared field by field: no allocation per frame.
    /// </summary>
    /// <param name="grenade">Ork O6: the thrown item's wargear whose model is drawn in hand while <see cref="SetGrenadeHeld"/> is true (null: none, as every Space Marine class).</param>
    public bool Configure((int Class, int Primary, int Pistol, string? Melee, string? Pack) key, string? primary, string? pistol, string? melee, IEnumerable<string?> wornWargear, bool alwaysMelee,
        string? grenade = null)
    {
        if (key == _key && string.Equals(grenade, GrenadeWargear, StringComparison.OrdinalIgnoreCase)) return false;
        _key = key;
        GrenadeWargear = grenade;
        var wornList = wornWargear.ToArray();
        Names = (primary, pistol, melee, wornList);
        AlwaysMelee = alwaysMelee;
        Clear();
        _first = true;
        AddItem(Role.Primary, primary);
        AddItem(Role.Pistol, pistol);
        AddItem(Role.Melee, melee);
        AddItem(Role.Grenade, grenade);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { primary, pistol, melee }.Concat(wornList))
        {
            if (name is null || !seen.Add(name)) continue;
            bool pack = wornList.Contains(name, StringComparer.OrdinalIgnoreCase);   // the jump pack (the worn wargear), not a weapon's own worn part
            foreach (var attachment in _assets.Catalogue.Worn(name))
            {
                if (_assets.Get(attachment) is not { } asset) continue;
                if (!_wornAssets.Contains(asset)) _wornAssets.Add(asset);
                foreach (var (bone, mesh) in asset.Worn)
                {
                    var node = new BoneAttachment3D { Name = "Worn_" + attachment.VisualName + "_" + bone };
                    _skeleton.AddChild(node);
                    node.BoneName = bone;
                    var instance = new MeshInstance3D { Name = "Mesh", Mesh = mesh };
                    node.AddChild(instance);
                    _worn.Add(node);
                    if (pack && asset.WornPaint.TryGetValue(bone, out var surfaces)) _packMeshes.Add((instance, surfaces));
                }
            }
        }
        ApplyPackPaint();
        return true;
    }

    /// <summary>
    /// Paints the worn jump pack with a look's pack paint (<see cref="WornPackPaint"/>: the backpack slot's colours; the owner's rule) instead of the side's default scheme;
    /// null returns it to the default. Cheap when nothing changed (a reference comparison per call), so the view may call it every frame; a pack configured later gets
    /// it too. The materials are shared by every character in the same paint.
    /// </summary>
    public void SetPackPaint(ResolvedPaint? paint)
    {
        if (ReferenceEquals(paint, _packPaint)) return;
        _packPaint = paint;
        ApplyPackPaint();
    }

    /// <summary>The pack paint now applied (null: the side's default).</summary>
    public ResolvedPaint? PackPaint => _packPaint;

    void ApplyPackPaint()
    {
        foreach (var (mesh, surfaces) in _packMeshes)
        {
            if (!GodotObject.IsInstanceValid(mesh)) continue;
            for (int i = 0; i < surfaces.Length; i++)
                if (surfaces[i] is { } surface) mesh.SetSurfaceOverrideMaterial(i, _packPaint is null ? null : _assets.PackMaterial(surface, _packPaint));
        }
    }

    /// <summary>The primary colour the worn jump pack's first painted surface is drawn with now (the shader's value), or null without a painted pack. Test aid.</summary>
    public Color? PackPrimaryColour
    {
        get
        {
            foreach (var (mesh, surfaces) in _packMeshes)
            {
                if (!GodotObject.IsInstanceValid(mesh)) continue;
                for (int i = 0; i < surfaces.Length; i++)
                    if (surfaces[i] is not null && mesh.GetActiveMaterial(i) is ShaderMaterial m) return m.GetShaderParameter("primary").AsColor();
            }
            return null;
        }
    }

    void AddItem(Role role, string? wargear)
    {
        if (wargear is null || _assets.Catalogue.Held(wargear) is not { } attachment || _assets.Get(attachment) is not { Mesh: { } mesh } asset) return;
        var item = new Item { Role = role, Attachment = attachment, Asset = asset };
        if (attachment.Wield is { Bone: not null } wield) item.Wield = MarineAssets.ToGodot(asset.Model.RootToBone(wield));
        if (attachment.Stow is { Bone: not null } stow) item.Stow = MarineAssets.ToGodot(asset.Model.RootToBone(stow));
        item.Node = new BoneAttachment3D { Name = $"Weapon_{role}_{attachment.VisualName}", Visible = false };
        _skeleton.AddChild(item.Node);
        item.Holder = new Node3D { Name = "Holder" };
        item.Holder.AddChild(new MeshInstance3D { Name = "Mesh", Mesh = mesh });
        if (asset.Magazine is { } magazine)
            item.Holder.AddChild(item.Magazine = new MeshInstance3D { Name = "Magazine", Mesh = magazine.Mesh, Position = magazine.Centre });
        item.Node.AddChild(item.Holder);
        _items.Add(item);
    }

    void Clear()
    {
        // Out of the tree first: a queued node would still draw this frame and keep its name, so the new nodes would be renamed.
        static void Drop(Node n) { if (GodotObject.IsInstanceValid(n)) { n.GetParent()?.RemoveChild(n); n.QueueFree(); } }
        foreach (var i in _items) Drop(i.Node);
        foreach (var n in _worn) Drop(n);
        _items.Clear();
        _worn.Clear();
        _wornAssets.Clear();
        _packMeshes.Clear();
    }

    /// <summary>
    /// M44: a named locator of a worn model (the jump pack's <c>fx_l_jet</c>, <c>fx_r_jet_inside_center</c> ...; matched ignoring case) in Godot world space from
    /// the skeleton's current pose (its +Y axis is the transform's Y column); false without a worn model that has it. As <see cref="TryMarker"/>.
    /// </summary>
    public bool TryWornMarker(string name, out Transform3D world) => TryWornMarker(name, false, out world);

    /// <summary>
    /// As <see cref="TryWornMarker(string, out Transform3D)"/>; with <paramref name="exhaust"/> (fx/impact-pass) the origin is moved along the locator's +Y to where the drawn model
    /// ends around that axis (<see cref="NozzleExit"/>: a jump pack's nozzle exit; 0.28 m for the Chaos pack's inside-centre markers), measured once per model and locator.
    /// </summary>
    public bool TryWornMarker(string name, bool exhaust, out Transform3D world)
    {
        world = default;
        if (!_alive || Faulted) return false;
        foreach (var asset in _wornAssets)
        {
            if (asset.Model.Locator(name) is not { } locator || _assets.WornLocator(asset, locator.Name) is not { } placed) continue;
            int bone = _skeleton.FindBone(placed.Bone);
            if (bone < 0) continue;
            world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) * placed.Local;
            if (exhaust)
            {
                var key = (asset.Model, locator.Name);
                if (!ExitCache.TryGetValue(key, out float exit)) ExitCache[key] = exit = NozzleExit.Along(asset.Model, locator);
                if (exit > 0f) world.Origin += world.Basis.Y.Normalized() * (exit * world.Basis.Y.Length());
            }
            world.Basis = world.Basis.Orthonormalized();
            return true;
        }
        return false;
    }

    static readonly Dictionary<(WeaponModel, string), float> ExitCache = new();

    /// <summary>Gives up on this rig after a failure: its nodes are removed and <see cref="Update"/> does nothing from now on.</summary>
    public void Fault() { Faulted = true; Clear(); }

    /// <summary>Removes every node (the rig is dropped with its character).</summary>
    public void Dispose() => Clear();

    /// <summary>
    /// Per frame: puts each weapon in hand or on its stow marker.
    /// </summary>
    /// <param name="slot">The simulation's current ranged slot (the weapon in hand).</param>
    /// <param name="meleeOut">A melee attack is shown.</param>
    /// <param name="alive">False: the player is dead; no swap delay and <see cref="TryMuzzle"/> returns false.</param>
    /// <param name="switchTime">The weapon switch time (s); the old weapon leaves the hand <paramref name="swapFraction"/> of it after the slot changed.</param>
    /// <param name="swapFraction">Animation pass 2: where in the switch the old weapon goes onto its stow marker (fix/stow-right: <see cref="SwitchPlan.Release"/>; the single clip's weapon tag: 0.5 for a
    /// rifle, 0.6 for the pistol).</param>
    /// <param name="takeFraction">fix/stow-right: where in the switch the new weapon comes into the hand (<see cref="SwitchPlan.Take"/>); between the two both ranged weapons are stowed. Negative: the same
    /// moment as <paramref name="swapFraction"/> (one swap, the behaviour before).</param>
    public void Update(WeaponSlot slot, bool meleeOut, bool alive, float delta, float switchTime, float swapFraction = 0.5f, float takeFraction = -1f)
    {
        if (Faulted) return;
        _alive = alive;
        if (_first || !alive) { _shown = _target = slot; _timer = 0f; _empty = false; _first = false; }
        else
        {
            // _timer counts the seconds since the slot changed. A change during a switch starts again from the weapon now in hand (or from none, both stowed).
            if (slot != _target) { _target = slot; _timer = 0f; }
            if (_shown != _target || _empty)
            {
                _timer += delta;
                float release = switchTime * Math.Clamp(swapFraction, 0f, 1f);
                float take = takeFraction < 0f ? release : Math.Max(release, switchTime * Math.Min(takeFraction, 1f));
                if (_timer >= take) { _shown = _target; _empty = false; }
                else if (_timer >= release) _empty = true;
            }
        }
        bool melee = AlwaysMelee || meleeOut;
        string? meleeBone = null;
        foreach (var i in _items) if (i.Role == Role.Melee && melee) meleeBone = i.Attachment.Wield?.Bone;
        foreach (var i in _items)
        {
            bool inHand = (!_empty || i.Role is not (Role.Primary or Role.Pistol))
                          && WeaponHands.InHand(i.Role, _shown, melee, _grenadeHeld, i.Attachment.TwoHanded, i.Attachment.Wield?.Bone, meleeBone);
            if (StowPistol && i.Role == Role.Pistol) inHand = false;   // anim/melee-flow: both hands on the hammer
            Apply(i, inHand);
        }
    }

    /// <summary>anim/melee-flow: the pistol goes to its holster (the marine holds the thunder hammer or the daemon maul in both hands); set by the networked view before <see cref="Update"/>.</summary>
    public bool StowPistol { get; set; }

    /// <summary>fix/stow-right: between the old weapon's release and the new one's take both ranged weapons are on their stow markers.</summary>
    bool _empty;

    /// <summary>
    /// <c>--old-stow</c> (test aid, fix/stow-right before / after): a switch plays the new weapon's clip alone and swaps the models at its tag (<see cref="SwitchPlan.For"/>'s old rule), so the
    /// primary flies round the left side to the back on the pistol clip's chest prop, as before the fix.
    /// </summary>
    public static readonly bool OldStow = OS.GetCmdlineUserArgs().Contains("--old-stow");

    void Apply(Item i, bool inHand)
    {
        i.InHand = inHand;
        var mount = inHand ? i.Attachment.Wield : i.Attachment.Stow;
        int mode = mount?.Bone is null || (!inHand && i.Attachment.HideOnStow) ? 2 : inHand ? 1 : 0;
        if (mode == i.Mode) return;
        i.Mode = mode;
        i.Node.Visible = mode != 2;
        if (mode == 2) return;
        if (i.Bone != mount!.Bone) { i.Bone = mount.Bone; i.Node.BoneName = mount.Bone; }
        i.Holder.Transform = inHand ? i.Wield : i.Stow;
    }

    /// <summary>
    /// The muzzle (<c>fx_Muzzle</c>) of the ranged weapon in hand in Godot world space and the direction out of the barrel
    /// (the locator's +Y); false without a ranged weapon in hand, for a dead player, or when its model has no muzzle. Reads the skeleton's
    /// current bone pose: <see cref="CharacterViewer"/> processes before <see cref="NetworkView"/> (negative process priority), so
    /// called from the view's <c>_Process</c> or later in the frame it reflects this frame's animation step (the pose chosen in this
    /// frame's <c>Place</c> shows from the next one). A hidden own character (camera at the head) still has its muzzle.
    /// </summary>
    public bool TryMuzzle(out Vector3 position, out Vector3 direction)
    {
        position = default; direction = default;
        if (!_alive || Faulted) return false;
        foreach (var i in _items)
        {
            if (i.Role is Role.Melee or Role.Grenade || !i.InHand || i.Mode != 1 || i.Asset.Muzzle is not { } muzzle || i.Bone is null) continue;
            int bone = _skeleton.FindBone(i.Bone);
            if (bone < 0) continue;
            var world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) * i.Wield * MarineAssets.ToGodot(muzzle.ToRoot);
            position = world.Origin;
            direction = world.Basis.Y.Normalized();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Any named locator (<c>fx_shells_eject</c>, <c>fx_muzzleflash</c>, <c>fx_trail</c> ...; matched ignoring case) of the ranged weapon in hand in Godot world
    /// space (its +Y axis is the transform's Y column); false when there is no such weapon or locator. As <see cref="TryMuzzle"/>.
    /// </summary>
    public bool TryMarker(string name, out Transform3D world)
    {
        world = default;
        if (!_alive || Faulted) return false;
        foreach (var i in _items)
        {
            if (i.Role is Role.Melee or Role.Grenade || !i.InHand || i.Mode != 1 || i.Bone is null || i.Asset.Model.Locator(name) is not { } locator) continue;
            int bone = _skeleton.FindBone(i.Bone);
            if (bone < 0) continue;
            world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) * i.Wield * MarineAssets.ToGodot(locator.ToRoot);
            world.Basis = world.Basis.Orthonormalized();
            return true;
        }
        return false;
    }

    /// <summary>
    /// fx/impact-pass: a named locator (<c>fx_powersword_swing</c>, <c>fx_swing</c> ...) of the melee weapon while it is in hand, in Godot world space from the skeleton's current
    /// pose; false when the melee weapon is not in hand, for a dead player, or when its model has no such locator. As <see cref="TryMarker"/>.
    /// </summary>
    public bool TryMeleeMarker(string name, out Transform3D world)
    {
        world = default;
        if (!_alive || Faulted || MeleeInHand() is not { } i || i.Asset.Model.Locator(name) is not { } locator) return false;
        int bone = _skeleton.FindBone(i.Bone!);
        if (bone < 0) return false;
        world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) * i.Wield * MarineAssets.ToGodot(locator.ToRoot);
        world.Basis = world.Basis.Orthonormalized();
        return true;
    }

    /// <summary>fx/impact-pass: the melee weapon's drawn mesh and its model while it is in hand (null otherwise: stowed, hidden, dead, no melee weapon).</summary>
    public (MeshInstance3D Mesh, WeaponModel Model)? MeleeMeshInHand =>
        _alive && !Faulted && MeleeInHand() is { } i && i.Holder.GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh ? (mesh, i.Asset.Model) : null;

    Item? MeleeInHand()
    {
        foreach (var i in _items)
            if (i.Role == Role.Melee && i.InHand && i.Mode == 1 && i.Bone is not null && GodotObject.IsInstanceValid(i.Holder)) return i;
        return null;
    }

    /// <summary>
    /// The detachable magazine of the ranged weapon in hand (M35): its asset and its transform in Godot world space (the node's origin is
    /// the magazine's centre), or false without such a weapon, for a dead player or when the model has no separate magazine part.
    /// </summary>
    public bool TryMagazine(out WeaponMagazine magazine, out Transform3D world)
    {
        magazine = null!; world = default;
        if (Faulted) return false;
        foreach (var i in _items)
        {
            if (i.Role is Role.Melee or Role.Grenade || !i.InHand || i.Mode != 1 || i.Magazine is null || i.Asset.Magazine is not { } m || !GodotObject.IsInstanceValid(i.Magazine)) continue;
            int bone = i.Bone is null ? -1 : _skeleton.FindBone(i.Bone);
            if (bone < 0) continue;
            // From the skeleton's current pose, like TryMuzzle (the attachment node follows the bone only after the skeleton's update).
            magazine = m; world = _skeleton.GlobalTransform * _skeleton.GetBoneGlobalPose(bone) * i.Wield * new Transform3D(Basis.Identity, m.Centre);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Where the in-hand ranged weapon's magazine hangs (M35): the rig bone the weapon is mounted on, the magazine's centre in that bone's
    /// space and the weapon model's name (the key of anything measured on it); false without such a weapon.
    /// </summary>
    public bool TryMagazineMount(out string bone, out Vector3 centreInBone, out string model)
    {
        bone = model = ""; centreInBone = default;
        if (Faulted) return false;
        foreach (var i in _items)
        {
            if (i.Role is Role.Melee or Role.Grenade || !i.InHand || i.Mode != 1 || i.Bone is null || i.Asset.Magazine is not { } m) continue;
            bone = i.Bone; centreInBone = i.Wield * m.Centre; model = i.Attachment.VisualPackage + "/" + i.Attachment.VisualName;
            return true;
        }
        return false;
    }

    /// <summary>Shows or hides the magazine of the ranged weapon in hand (M35); returns whether there is one.</summary>
    public bool SetMagazineVisible(bool visible)
    {
        if (Faulted) return false;
        foreach (var i in _items)
        {
            if (i.Role is Role.Melee or Role.Grenade || !i.InHand || i.Mode != 1 || i.Magazine is null || !GodotObject.IsInstanceValid(i.Magazine)) continue;
            i.Magazine.Visible = visible;
            return true;
        }
        return false;
    }

    /// <summary>Shows every weapon's magazine again (M35: a reload ended or was cancelled, the weapon was put away).</summary>
    public void ShowMagazines()
    {
        if (Faulted) return;
        foreach (var i in _items) if (i.Magazine is not null && GodotObject.IsInstanceValid(i.Magazine)) i.Magazine.Visible = true;
    }

    /// <summary>
    /// Test aid (<c>--switch-test --switch-probe</c>): each drawn weapon's role, where it is ("hand", "stowed") and its handle point (the mount's handle node; the model's root
    /// without one) in the skeleton's own space, from the skeleton's current pose.
    /// </summary>
    public IEnumerable<(Role Role, string Mode, Vector3 Origin)> Placements()
    {
        foreach (var i in _items)
        {
            if (i.Mode is not (0 or 1) || i.Bone is null || _skeleton.FindBone(i.Bone) is not (var bone and >= 0)) continue;
            var mount = i.Mode == 1 ? i.Attachment.Wield : i.Attachment.Stow;
            var handle = mount is not null && i.Asset.Model.Tree.Contains(mount.HandleNode) ? MarineAssets.ToGodot(i.Asset.Model.Tree.ModelSpace(mount.HandleNode)).Origin : Vector3.Zero;
            yield return (i.Role, i.Mode == 1 ? "hand" : "stowed", _skeleton.GetBoneGlobalPose(bone) * i.Holder.Transform * handle);
        }
    }

    /// <summary>A rig for another character of the same skeleton (the corpse) with the same loadout and the weapons where they are now.</summary>
    public WeaponRig CloneTo(Skeleton3D skeleton)
    {
        var copy = new WeaponRig(skeleton, _assets);
        copy.Configure(_key, Names.Primary, Names.Pistol, Names.Melee, Names.Worn, AlwaysMelee, GrenadeWargear);
        copy._grenadeHeld = _grenadeHeld;
        copy.SetPackPaint(_packPaint);   // the corpse keeps the pack's paint
        copy._first = false; copy._shown = copy._target = _shown; copy._alive = _alive; copy._empty = _empty;
        foreach (var mine in _items)
        {
            var theirs = copy._items.FirstOrDefault(i => i.Role == mine.Role);
            if (theirs is not null && mine.Mode >= 0) copy.Apply(theirs, mine.InHand);
        }
        return copy;
    }

    /// <summary>The names the rig was configured with.</summary>
    public (string? Primary, string? Pistol, string? Melee, string?[] Worn) Names { get; private set; }
}
