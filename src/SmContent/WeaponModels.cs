using System.Numerics;
using SmFormats.Baf;
using SmFormats.Havok;
using SmFormats.Obpk;

namespace SmContent;

/// <summary>
/// Where a weapon hangs on the character, from a wargear attachment's <c>wield_info_by_upgrade</c> (in hand) or <c>stow_info</c>
/// (stowed): the weapon's own <see cref="HandleNode"/> is placed on the character's <see cref="BodyNode"/> (**supported by
/// evidence**: the field names, and every readable model's handle node is the same rig prop node the wield names). The body
/// node is a skeleton bone or a marker of the character's object tree (<c>spacemarine_body_default</c>) under a bone.
/// </summary>
/// <param name="HandleNode">Node of the weapon's object tree that is put on the body node (e.g. "rig_prop_m_chest").</param>
/// <param name="BodyNode">As the wargear names it (e.g. "rig_prop_m_chest", "mkr_stow_bolter").</param>
/// <param name="Bone">The skeleton bone the body node is or hangs from (canonical case); null when it could not be resolved.</param>
/// <param name="BodyLocal">The body node in <see cref="Bone"/>'s space (row-vector; identity when the body node is the bone).</param>
/// <param name="Scale">The wargear's scale (stowed swords 0.8–0.9). **Interpretation**: uniform, about the handle node.</param>
/// <param name="ModelAxes">Ork O6 stow spots: <see cref="BodyLocal"/> turns the weapon about its handle's position by the model's own axes (not by the handle node's
/// frame): the handle point is moved to the origin, then <see cref="BodyLocal"/> applies. False for every wargear-named mount.</param>
public sealed record WeaponMount(string HandleNode, string BodyNode, string? Bone, Matrix4x4 BodyLocal, float Scale, bool ModelAxes = false);

/// <summary>
/// Where a rig carries a weapon whose wargear file names no nodes (Ork O6: the three Ork weapons' <c>wield_info_by_upgrade</c> and <c>stow_info</c> have empty
/// handle and body nodes, unlike the Space Marine wargear). The weapon's own object tree holds the handle: every readable Ork weapon model has a
/// <c>RIG_Prop_*</c> locator and the Ork rig a bone of the same name (**supported by evidence**, <c>smtool weapon-parts --rig ork</c>).
/// </summary>
/// <param name="HandleNode">The node of the weapon's tree put on the bone (e.g. "RIG_Prop_RHand").</param>
/// <param name="WieldBone">The rig bone the handle is put on in hand (the same name).</param>
/// <param name="Stow">Where the weapon hangs when put away; null: it is hidden then. **Ours**: the Ork rig has no <c>MKR_Stow_*</c> markers.</param>
/// <param name="TwoHanded">The weapon needs both hands: it is put away while the melee weapon is out (**ours**, design section 5).</param>
/// <param name="HeldModel">For wargear without any attachment (the Ork's stikkbomb: no <c>visual_data/attachments</c> entry): the model drawn in hand while it is thrown.</param>
public sealed record RigWeaponMount(string HandleNode, string WieldBone, RigStowSpot? Stow, bool TwoHanded = false, RigHeldModel? HeldModel = null);

/// <summary>A model in <c>data/packages/&lt;Package&gt;.oppc</c> (object tree and mesh named <paramref name="Visual"/>) that a rig mount draws.</summary>
public sealed record RigHeldModel(string Package, string Visual);

/// <summary>
/// A stow spot of our own (the rig has no stow markers): the weapon's handle point is put at <paramref name="Offset"/> (metres) in <paramref name="Bone"/>'s space and
/// the weapon turned so that its own +Y axis (the barrel or shaft: every Ork weapon is longest along it) points along <paramref name="Length"/> and its own +Z axis (up
/// when held: sights, blade width) along <paramref name="Up"/>, both directions in the bone's frame (the spine and pelvis: X up the body, Y forward, Z to the left;
/// measured from the rig's markers), scaled by <paramref name="Scale"/>.
/// </summary>
public sealed record RigStowSpot(string Bone, Vector3 Offset, Vector3 Length, Vector3 Up, float Scale = 1f, string Label = "")
{
    /// <summary>The spot in the bone's space (row-vector: the model's axes become <see cref="Length"/>, <see cref="Up"/> and their cross product; then move).</summary>
    public Matrix4x4 BoneLocal
    {
        get
        {
            var y = Vector3.Normalize(Length);
            var z = Up - Vector3.Dot(Up, y) * y;   // the part of Up across the length (Gram-Schmidt)
            z = z.LengthSquared() > 1e-8f ? Vector3.Normalize(z) : Vector3.Normalize(Vector3.Cross(y, Math.Abs(y.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY));
            var x = Vector3.Cross(y, z);
            return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, Offset.X, Offset.Y, Offset.Z, 1);
        }
    }
}

/// <summary>A visual upgrade of an attachment: nodes shown and hidden while the listed upgrades (perks) are owned.</summary>
public sealed record WeaponVisualUpgrade(IReadOnlyList<string> Show, IReadOnlyList<string> Hide, IReadOnlyList<string> RequiredUpgrades);

/// <summary>
/// One <c>visual_data/attachments</c> entry of a wargear file: the model it shows (<see cref="VisualName"/> in the package
/// <c>data/packages/&lt;VisualPackage&gt;.oppc</c>) and where it hangs. Wargear that is worn rather than held (jump packs, the
/// Devastator's backpack) has empty wield and stow nodes: <see cref="Wield"/> and <see cref="Stow"/> are null and the model's
/// object tree names the rig bones it follows (as the armour pieces do).
/// </summary>
/// <param name="Name">The attachment's key ("bolter", "bolt_pistol", or the template name "wargear\attachment_entry").</param>
/// <param name="Slot">The wargear's <c>common_data/slot</c> ("ranged", "pistol", "melee", "equipment", "coreability").</param>
/// <param name="Upgrades">Visual upgrades; nodes any of them shows are hidden by default (<see cref="WeaponModel"/>).</param>
/// <param name="FxMarkers">Every <c>marker</c> the wargear's effects name (e.g. "fx_muzzle", "fx_shells_eject"); evidence for locators.</param>
public sealed record WeaponAttachment(string Wargear, string Name, string Slot, string VisualPackage, string VisualName,
    WeaponMount? Wield, WeaponMount? Stow, bool HideOnStow, IReadOnlyList<WeaponVisualUpgrade> Upgrades, IReadOnlyList<string> FxMarkers)
{
    /// <summary>
    /// Worn rather than held: no wield entry (gated or not) and no stow info names a handle or body node (jump packs, the
    /// Devastator backpack). <see cref="WeaponModels.Build"/> decides it from the data; constructed directly it defaults to
    /// having neither mount. A held weapon whose only wield entries need upgrades is not worn (its <see cref="Wield"/> is null).
    /// </summary>
    public bool IsWorn { get; init; } = Wield is null && Stow is null;
    /// <summary>Needs both hands (<see cref="RigWeaponMount.TwoHanded"/>, Ork O6); false for every Space Marine weapon.</summary>
    public bool TwoHanded { get; init; }
    public string PackagePath(string gameDir) => Path.Combine(gameDir, "data", "packages", VisualPackage + ".oppc");
    /// <summary>
    /// Node names hidden in the default look: every node an upgrade shows (upgrade geometry needs perks). <see cref="WeaponModels.Build"/>
    /// widens it to the upgrades of every attachment with the same model (**interpretation**: the Devastator's bolt pistol lists no
    /// upgrades but is the Tactical's pistol model, whose upgrade parts would otherwise all show).
    /// </summary>
    public IReadOnlySet<string> HiddenByDefault { get; init; } = Upgrades.SelectMany(u => u.Show).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The models of the multiplayer wargear (M24): for every wargear file a class may carry (base and startup wargear in
/// <c>global</c>), its attachments with model package, in-hand and stowed mounts and visual upgrades; plus the character's
/// stow markers. Cheap: reads the wargear attributes, the character's object tree and the rig's bone names; the meshes are
/// read per attachment on demand by <see cref="WeaponModel.Load(string, WeaponAttachment, List{string}?)"/>.
/// Both sides use the same character (<c>sm_space_marine_player</c> and <c>csm_chaos_marine_player</c> both name the visual
/// <c>spacemarine_body_default</c>) and the same rig.
/// </summary>
public sealed record WeaponModels(IReadOnlyDictionary<string, IReadOnlyList<WeaponAttachment>> ByWargear,
    IReadOnlyDictionary<string, BodyMarker> BodyMarkers, IReadOnlyList<string> Notes)
{
    /// <summary>The character visual both multiplayer player blueprints name (renderable_ext/visual), in the rig's chunk.</summary>
    public const string BodyVisual = "spacemarine_body_default";

    /// <summary>The attachment a weapon is drawn with in hand: the first with a wield mount (null for wargear without a held model).</summary>
    public WeaponAttachment? Held(string wargear) =>
        ByWargear.TryGetValue(wargear, out var list) ? list.FirstOrDefault(a => a.Wield is not null) : null;

    /// <summary>Attachments worn on the body (jump packs, the Devastator backpack) of a wargear file.</summary>
    public IEnumerable<WeaponAttachment> Worn(string wargear) =>
        ByWargear.TryGetValue(wargear, out var list) ? list.Where(a => a.IsWorn) : [];

    /// <param name="wargearNames">Wargear files to read; null: every base and startup wargear of the multiplayer classes.</param>
    /// <param name="skeletonBones">The rig's bone names (e.g. <c>MarineAssets.Model.Rig.Skeleton.BoneNames</c>); null: read from the rig.</param>
    /// <exception cref="FileNotFoundException">A required archive or entry is missing.</exception>
    /// <exception cref="InvalidDataException">An archive, attribute file, object tree or mesh is malformed.</exception>
    /// <exception cref="NotSupportedException">A mesh or document uses a layout not decoded yet (e.g. other DCM type-2 blocks).</exception>
    /// <exception cref="IOException">The file cannot be read (also <see cref="EndOfStreamException"/> on a truncated archive).</exception>
    public static WeaponModels Load(string gameDir, List<string>? notes = null, IEnumerable<string>? wargearNames = null,
        IReadOnlyList<string>? skeletonBones = null) => Load(gameDir, RigProfiles.Marine, notes, wargearNames, skeletonBones);

    /// <summary>
    /// The models of wargear on a profile's character (Ork O2): the rig's bones and its <see cref="RigProfile.BodyVisual"/>'s stow markers,
    /// both from <see cref="RigProfile.Archive"/>. Without names: the profile's <see cref="RigProfile.DefaultWargear"/>, or (empty, the marine)
    /// every base and startup wargear of the multiplayer classes. Exceptions as <see cref="Load(string, List{string}?, IEnumerable{string}?, IReadOnlyList{string}?)"/>.
    /// </summary>
    public static WeaponModels Load(string gameDir, RigProfile rig, List<string>? notes = null, IEnumerable<string>? wargearNames = null,
        IReadOnlyList<string>? skeletonBones = null)
    {
        notes ??= [];
        var names = (wargearNames ?? (rig.DefaultWargear.Count > 0 ? rig.DefaultWargear : null))?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names is null)
        {
            BafDocument? global = null;
            using (var archive = ObpkArchive.Open(Path.Combine(gameDir, MpClasses.GlobalPackage)))
                archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && e.FileName == "global", (_, d) => global = BafDocument.Read(d));
            if (global is null) throw new FileNotFoundException($"global not found in {MpClasses.GlobalPackage}.");
            names = MpClasses.ReadClasses(global).SelectMany(c => c.BaseWargear.Concat(c.StartupWargear)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => docs[e.FileName] = BafDocument.Read(d));
        foreach (var n in names.Where(n => !docs.ContainsKey(n)).Order()) notes.Add($"{n}: wargear file not found");

        // The character's object tree and (unless given) the rig's bones, both in the rig's chunk: one pass.
        ObjectTree? body = null;
        IReadOnlyList<string>? bones = skeletonBones;
        using (var archive = ObpkArchive.Open(rig.ArchivePath(gameDir)))
            archive.ReadEntries(e =>
                    (body is null && archive.GroupName(e) == "O3d" && e.FileName.Equals(rig.BodyVisual, StringComparison.OrdinalIgnoreCase))
                    || (bones is null && e.FileName.Equals(rig.Entry, StringComparison.OrdinalIgnoreCase)),
                (e, d) =>
                {
                    if (archive.GroupName(e) == "O3d") body = ObjectTree.From(e.FileName, SmFormats.Bod.BodDocument.Read(d).Root);
                    else bones = new HavokCharacterFile(d.ToArray()).Skeleton.BoneNames;
                });
        if (body is null) notes.Add($"{rig.BodyVisual}.O3d not found in {rig.Archive}; stow markers unresolved");
        if (bones is null) throw new FileNotFoundException($"'{rig.Entry}' not found.");
        return Build(docs, body, bones, notes, rig.BodyVisual, rig.WeaponMounts);
    }

    /// <summary>The attachments from already-read data (testable without an install).</summary>
    /// <param name="body">The character's object tree (stow markers); null: only bone body nodes resolve.</param>
    /// <param name="bodyVisual">The character visual's name, for notes.</param>
    /// <param name="rigMounts">The rig's own mounts for wargear that names no nodes (<see cref="RigProfile.WeaponMounts"/>); null or empty (the marine): none, and the output is as before.</param>
    public static WeaponModels Build(IReadOnlyDictionary<string, BafDocument> wargear, ObjectTree? body, IReadOnlyList<string> skeletonBones, List<string> notes,
        string bodyVisual = BodyVisual, IReadOnlyDictionary<string, RigWeaponMount>? rigMounts = null)
    {
        var bones = skeletonBones.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var canonical = skeletonBones.GroupBy(b => b, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var markers = new Dictionary<string, BodyMarker>(StringComparer.OrdinalIgnoreCase);
        if (body is not null)
            foreach (var n in body.Order)
                if (!bones.Contains(n) && body.UnderBone(n, bones.Contains) is { } under)
                    markers[n] = new BodyMarker(n, canonical[under.Bone], under.Local);

        WeaponMount? Mount(string wargearName, string what, BafTable? t)
        {
            if (t is not { } table) return null;
            string handle = table.String("handle_node") ?? "", node = table.String("body_node") ?? "";
            if (handle.Length == 0 && node.Length == 0) return null;
            float scale = table.Float("scale") is { } s && float.IsFinite(s) && s > 0f ? s : 1f;
            if (canonical.TryGetValue(node, out var bone)) return new WeaponMount(handle, node, bone, Matrix4x4.Identity, scale);
            if (markers.TryGetValue(node, out var marker)) return new WeaponMount(handle, node, marker.Bone, marker.Local, scale);
            notes.Add($"{wargearName}: {what} body node '{node}' is neither a rig bone nor a marker of {bodyVisual}");
            return new WeaponMount(handle, node, null, Matrix4x4.Identity, scale);
        }

        WeaponMount RigMount(string wargearName, string what, string handle, string bone, Matrix4x4 local, float scale, string label = "", bool modelAxes = false)
        {
            if (canonical.TryGetValue(bone, out var found)) return new WeaponMount(handle, label.Length > 0 ? label : bone, found, local, scale, modelAxes);
            notes.Add($"{wargearName}: the rig's {what} bone '{bone}' is not in the skeleton");
            return new WeaponMount(handle, bone, null, Matrix4x4.Identity, scale);
        }

        var result = new Dictionary<string, IReadOnlyList<WeaponAttachment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, doc) in wargear.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            var list = new List<WeaponAttachment>();
            string slot = doc.Root.Table("common_data")?.String("slot") ?? "";
            var fx = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectMarkers(doc.Root, fx, 0);
            foreach (var e in doc.Root.Table("visual_data/attachments")?.Entries ?? [])
            {
                if (e.Type != BafType.Table) continue;
                var t = e.AsTable();
                string package = t.String("visual_package") ?? "", visual = t.String("visual_name") ?? "";
                if (package.Length == 0 || visual.Length == 0) { notes.Add($"{name}: attachment '{e.Name}' names no visual"); continue; }
                // In hand: the wield entry without upgrade requirements (the default look); others are noted, not applied.
                BafTable? wield = null;
                bool namesNodes = false;
                foreach (var w in t.Table("wield_info_by_upgrade")?.Entries ?? [])
                {
                    if (w.Type != BafType.Table) continue;
                    var wt = w.AsTable();
                    namesNodes |= NamesNodes(wt);
                    if (Required(wt).Count == 0) wield ??= wt;
                    else notes.Add($"{name}: a wield entry needing [{string.Join(", ", Required(wt))}] is not applied");
                }
                var stow = t.Table("stow_info");
                namesNodes |= stow is { } st && NamesNodes(st);
                bool hideOnStow = stow?["hide_on_stow"] is { Type: BafType.Bool } h && h.AsBool();
                var upgrades = new List<WeaponVisualUpgrade>();
                foreach (var u in t.Table("upgrades/upgrades")?.Entries ?? [])
                {
                    if (u.Type != BafType.Table) continue;
                    var ut = u.AsTable();
                    upgrades.Add(new WeaponVisualUpgrade(Names(ut.Table("visuals_to_show")), Names(ut.Table("visuals_to_hide")), Required(ut)));
                }
                var wieldMount = Mount(name, "wield", wield);
                var stowMount = Mount(name, "stow", stow);
                bool isWorn = !namesNodes, twoHanded = false;
                // A rig that carries a wargear the data gives no nodes for (the Ork's weapons) supplies the mounts itself.
                if (!namesNodes && rigMounts is not null && rigMounts.TryGetValue(name, out var own))
                {
                    wieldMount = RigMount(name, "wield", own.HandleNode, own.WieldBone, Matrix4x4.Identity, 1f);
                    stowMount = own.Stow is { } spot ? RigMount(name, "stow", own.HandleNode, spot.Bone, spot.BoneLocal, spot.Scale, spot.Label, modelAxes: true) : null;
                    isWorn = false; twoHanded = own.TwoHanded;
                    hideOnStow = stowMount is null;
                }
                list.Add(new WeaponAttachment(name, e.Name, slot, package, visual, wieldMount, stowMount,
                    hideOnStow, upgrades, fx.ToList()) { IsWorn = isWorn, TwoHanded = twoHanded });
            }
            // A wargear without any model of its own that the rig draws anyway (the Ork's stikkbomb: the boy's thrown grenade, the mesh the Stormboy holds).
            if (list.Count == 0 && rigMounts is not null && rigMounts.TryGetValue(name, out var heldOnly) && heldOnly.HeldModel is { } held)
                list.Add(new WeaponAttachment(name, "ours:held model", slot, held.Package, held.Visual,
                    RigMount(name, "wield", heldOnly.HandleNode, heldOnly.WieldBone, Matrix4x4.Identity, 1f), null, true, [], fx.ToList()) { IsWorn = false });
            result[name] = list;
        }
        // Upgrade parts of a model are hidden for every attachment drawing it.
        var hiddenByModel = result.Values.SelectMany(l => l).GroupBy(a => (a.VisualPackage.ToLowerInvariant(), a.VisualName.ToLowerInvariant()))
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.SelectMany(a => a.HiddenByDefault).ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var name in result.Keys.ToList())
            result[name] = result[name].Select(a => a with { HiddenByDefault = hiddenByModel[(a.VisualPackage.ToLowerInvariant(), a.VisualName.ToLowerInvariant())] }).ToList();
        return new WeaponModels(result, markers, notes);
    }

    /// <summary>
    /// The models of projectile blueprints (thrown grenades, launcher grenades; <see cref="SmSim.ThrownSpec"/> and
    /// <see cref="SmSim.ProjectileSpec"/> name them) from their <c>renderable_ext</c> in <c>data/packages/ebps.oppc</c>, by blueprint
    /// file name. Load a model with <see cref="WeaponModel.Load(string, string, IReadOnlySet{string}?, List{string}?)"/>.
    /// </summary>
    /// <param name="blueprints">Blueprint names or paths (e.g. "ebps\projectiles\pp_grenade_multiplayer"); only the file name is used.</param>
    /// <exception cref="FileNotFoundException">A required archive or entry is missing.</exception>
    /// <exception cref="InvalidDataException">An archive, attribute file, object tree or mesh is malformed.</exception>
    /// <exception cref="NotSupportedException">A mesh or document uses a layout not decoded yet (e.g. other DCM type-2 blocks).</exception>
    /// <exception cref="IOException">The file cannot be read (also <see cref="EndOfStreamException"/> on a truncated archive).</exception>
    public static IReadOnlyDictionary<string, ProjectileVisual> ReadProjectileVisuals(string gameDir, IEnumerable<string> blueprints, List<string>? notes = null)
    {
        var wanted = blueprints.Select(Leaf).Where(b => b.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ProjectileVisual>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.BlueprintPackage)))
            archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && wanted.Contains(e.FileName) && !result.ContainsKey(e.FileName), (e, d) =>
            {
                if (ProjectileVisualOf(e.FileName, BafDocument.Read(d)) is { } v) result[e.FileName] = v;
                else notes?.Add($"{e.FileName}: no renderable_ext visual");
            });
        foreach (var b in wanted.Where(b => !result.ContainsKey(b)).Order()) notes?.Add($"{b}: projectile blueprint without a model");
        return result;
    }

    /// <summary>A blueprint's <c>extensions/renderable_ext</c> visual (package and object name), or null.</summary>
    public static ProjectileVisual? ProjectileVisualOf(string blueprint, BafDocument doc)
    {
        var r = doc.Root.Table("extensions/renderable_ext");
        string package = r?.String("visual_package") ?? "", visual = r?.String("visual") ?? "";
        return package.Length == 0 || visual.Length == 0 ? null : new ProjectileVisual(blueprint, package, visual);
    }

    static string Leaf(string path) => path.Split('\\', '/')[^1];

    static bool NamesNodes(BafTable t) => (t.String("handle_node") ?? "").Length > 0 || (t.String("body_node") ?? "").Length > 0;

    static IReadOnlyList<string> Required(BafTable t) =>
        t.Table("required_upgrade_name/required_list")?.Entries.Where(x => x.Type == BafType.String).Select(x => x.AsString()).Where(s => s.Length > 0).ToList() ?? [];

    static IReadOnlyList<string> Names(BafTable? t) =>
        t?.Entries.Where(x => x.Type == BafType.String && x.Name == "name").Select(x => x.AsString()).Where(s => s.Length > 0).ToList() ?? [];

    static void CollectMarkers(BafTable t, SortedSet<string> into, int depth)
    {
        if (depth > 32) return;
        foreach (var e in t.Entries)
        {
            if (e.Type == BafType.Table) CollectMarkers(e.AsTable(), into, depth + 1);
            else if (e.Type == BafType.String && e.Name == "marker" && e.AsString().Length > 0) into.Add(e.AsString());
        }
    }
}

/// <summary>
/// The Ork Boy's weapon mounts (Ork O6, docs/ork-faction-design.md section 4). In hand: the weapon model's own <c>RIG_Prop_*</c> locator on the rig's bone of
/// that name (slugga <c>RIG_Prop_LHand</c>, shoota <c>RIG_Prop_M_Chest</c>, choppa <c>RIG_Prop_RHand</c>; **supported by evidence**: the wargear files name no nodes,
/// each model's tree names exactly one prop node, and the marine uses the same scheme). Stowed: spots of our own on the pelvis and spine (**ours**,
/// tuned against screenshots; the boy has no <c>MKR_Stow_*</c> markers). The stikkbomb's wargear has no model: the held one is the Stormboy's
/// <c>stikkbomb_stormboy</c> (**interpretation**: its tree names <c>RIG_Prop_RHand</c> like the choppa's, and its texture set is the Ork stikkbomb's), shown only
/// while it is thrown (it has no stow spot: hidden otherwise).
/// </summary>
public static class OrkWeaponMounts
{
    public const string Slugga = "slugga", Shoota = "ork_coop_boy_2hand_shoota_rapid", Choppa = "ork_coop_boy_choppa", Stikkbomb = "ork_coop_boy_stikkbomb";

    public static readonly IReadOnlyDictionary<string, RigWeaponMount> Table = new Dictionary<string, RigWeaponMount>(StringComparer.OrdinalIgnoreCase)
    {
        // Slugga: holstered on the left hip, barrel down, flat against the hip.
        [Slugga] = new("RIG_Prop_LHand", "RIG_Prop_LHand", new RigStowSpot("RIG_Pelvis", new Vector3(0f, 0f, 0.32f), new Vector3(-1, 0, 0), new Vector3(0, 1, 0), 1f, "ours:slugga left hip")),
        // Shoota: slung across the upper back, barrel up and to the left, flat against the back.
        [Shoota] = new("RIG_Prop_M_Chest", "RIG_Prop_M_Chest", new RigStowSpot("RIG_Spine3", new Vector3(0.1f, -0.45f, 0f), new Vector3(0.7f, 0, 0.7f), new Vector3(0.7f, 0, -0.7f), 1f, "ours:shoota upper back"), TwoHanded: true),
        // Choppa: across the lower back, grip on the left, blade out to the right.
        [Choppa] = new("RIG_Prop_RHand", "RIG_Prop_RHand", new RigStowSpot("RIG_Spine1", new Vector3(0f, -0.4f, 0.35f), new Vector3(0, 0, -1), new Vector3(1, 0, 0), 1f, "ours:choppa lower back")),
        // Stikkbomb: in the right hand while thrown, not drawn otherwise.
        [Stikkbomb] = new("RIG_Prop_RHand", "RIG_Prop_RHand", null, HeldModel: new RigHeldModel("stikkbomb_stormboy", "stikkbomb_stormboy")),
    };
}

/// <summary>A projectile blueprint's model: <see cref="VisualName"/> in <c>data/packages/&lt;VisualPackage&gt;.oppc</c>.</summary>
public sealed record ProjectileVisual(string Blueprint, string VisualPackage, string VisualName)
{
    public string PackagePath(string gameDir) => Path.Combine(gameDir, "data", "packages", VisualPackage + ".oppc");
    /// <summary>The plasma bolts name "invisible_projectile", an object tree without a mesh (drawn by effects in the original).</summary>
    public bool IsInvisible => VisualName.Equals("invisible_projectile", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A marker of the character's object tree (e.g. MKR_Stow_Bolter) and where it hangs: a rig bone and the offset from it (row-vector).</summary>
public sealed record BodyMarker(string Name, string Bone, Matrix4x4 Local);

/// <summary>One mesh block of a weapon model and how it is placed in the model's root space.</summary>
/// <param name="Node">The object-tree node the visual names.</param>
/// <param name="ToRoot">Vertex → model root (row-vector). Static blocks: the node's transform (vertices are in the node's space,
/// as for the Chaos armour; **supported by evidence**: the parts of every readable weapon fit together only so). Skinned
/// blocks (chain teeth): identity, the vertices are in the bind pose in root space.</param>
/// <param name="Skinned">Skinned to the weapon's own bones (chainsword teeth); drawn static in the bind pose (the chain's motion is not reproduced).</param>
/// <param name="Simulated">A type-2 block (cables, ammunition belts) the original simulates; drawn rigid in the bind pose (**placeholder**).</param>
/// <param name="Visible">Drawn in the default look: near detail level, renderable, and not upgrade geometry.</param>
public sealed record WeaponPart(int BlockIndex, string Node, Matrix4x4 ToRoot, bool Skinned, bool Simulated, bool Visible, string? HiddenBecause);

/// <summary>A named node of a weapon's object tree that is not geometry (e.g. fx_Muzzle, fx_shells_eject, marker_collision).</summary>
public sealed record WeaponLocator(string Name, Matrix4x4 ToRoot)
{
    public Vector3 Position => new(ToRoot.M41, ToRoot.M42, ToRoot.M43);
    /// <summary>
    /// The locator's +Y axis. For muzzles this points out of the barrel (**supported by evidence**: on every readable ranged model
    /// the muzzle's +Y points from the model's centre to the muzzle, cosine 0.95 or more).
    /// </summary>
    public Vector3 Forward => Vector3.Normalize(new Vector3(ToRoot.M21, ToRoot.M22, ToRoot.M23));
}

/// <summary>
/// A weapon's geometry from its package: the mesh with materials and diffuse textures (<see cref="StaticModel"/>), the
/// object tree, how each block sits in the model's root space, and the locators (muzzles, shell ejection, effects).
/// Coordinates stay as in the files (Z up, metres).
/// </summary>
public sealed record WeaponModel(string Package, StaticModel Model, ObjectTree Tree, IReadOnlyList<WeaponPart> Parts, IReadOnlyList<WeaponLocator> Locators)
{
    /// <summary>The muzzle (node "fx_muzzle", any case), the marker the wargear's muzzle effects name; null for melee weapons.</summary>
    public WeaponLocator? Muzzle => Locators.FirstOrDefault(l => l.Name.Equals("fx_muzzle", StringComparison.OrdinalIgnoreCase));

    /// <summary>Every barrel muzzle: "fx_muzzle" and numbered ones ("fx_Muzzle01": the storm bolter's second barrel).</summary>
    public IReadOnlyList<WeaponLocator> Muzzles => Locators.Where(l => l.Name.StartsWith("fx_muzzle", StringComparison.OrdinalIgnoreCase)
        && l.Name[9..].All(char.IsDigit)).ToList();

    public WeaponLocator? Locator(string name) => Locators.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The weapon's detachable magazine (M35): the first part drawn in the default look whose object-tree node is named like one
    /// (<see cref="IsMagazineNode"/>; the bolter's <c>GEO_Clip_Plasma</c>, the bolt pistol's <c>GEO_Bolt_Pistol_AmmoClip</c>, the stalker's
    /// <c>GEO_clip</c>, the storm bolter's <c>GEO_Ammo_clip</c> / <c>GEO_drum</c>, the grenade launcher's <c>GEO_Grenade_Drum</c>), a rigid
    /// static block; null when the model has none (the Chaos bolter and bolt pistol draw theirs inside the body block; lascannon, heavy
    /// bolter, plasma cannon and melta have no separate one). **Supported by evidence** for the names, **interpretation** that these parts are the
    /// magazines the reload takes out.
    /// </summary>
    public WeaponPart? Magazine => Parts.FirstOrDefault(p => p.Visible && !p.Skinned && !p.Simulated && IsMagazineNode(p.Node));

    /// <summary>
    /// Magazines the Chaos models draw inside their body block (M35: <c>chaos_bolter_mp</c>, <c>chaos_unholy_bolter_mp</c>, <c>chaos_boltpistol_mp</c>
    /// have no part of their own): a box in the model's root space; the shells of the body block whose bounds centre is in it are the magazine
    /// (<see cref="MeshShells.PickInBox"/>). Measured with <c>smtool weapon-parts &lt;wargear&gt; --components</c> against the Space Marine
    /// models' own magazine parts (the bolter's 0.001..0.175, -0.039..0.028, -0.382..-0.105; the bolt pistol's -0.635..-0.540, -0.011..0.217, 0.845..1.043),
    /// widened by about 0.03 m: the Chaos bolter's shells at 0.010..0.174, -0.034..0.026, -0.390..-0.123 (150 triangles) and six small ones around it;
    /// the Chaos pistol's at -0.637..-0.555, 0.007..0.173, 0.824..0.999 (28) and -0.656..-0.558, -0.029..0.143, 0.770..0.940 (20).
    /// **Interpretation**: that these shells are the magazines (they sit where the Space Marine models' are, in the same mount).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (Vector3 Min, Vector3 Max)> EmbeddedMagazineBoxes = new Dictionary<string, (Vector3, Vector3)>(StringComparer.OrdinalIgnoreCase)
    {
        ["chaos_bolter_mp"] = (new Vector3(-0.01f, -0.06f, -0.42f), new Vector3(0.21f, 0.05f, -0.10f)),
        ["chaos_unholy_bolter_mp"] = (new Vector3(-0.01f, -0.06f, -0.42f), new Vector3(0.21f, 0.05f, -0.10f)),
        ["chaos_boltpistol_mp"] = (new Vector3(-0.665f, -0.04f, 0.80f), new Vector3(-0.51f, 0.25f, 1.07f)),
    };

    /// <summary>
    /// How many shells of the box are the magazine, for models where the box also holds loose pieces: the Chaos bolt pistol's box holds the
    /// magazine (28 triangles) and a plate below the grip (20), seen to be no magazine when dropped, so only the biggest shell is taken; the bolters take every shell in the box.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, int> EmbeddedMagazineMaxShells = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["chaos_boltpistol_mp"] = 1,
    };

    /// <summary>
    /// For a model without a magazine part whose body block holds the magazine (<see cref="EmbeddedMagazineBoxes"/>): the body part (the visible
    /// rigid part with the most triangles) and the magazine's box; null otherwise.
    /// </summary>
    public (WeaponPart Part, Vector3 Min, Vector3 Max, int MaxShells)? EmbeddedMagazine
    {
        get
        {
            if (Magazine is not null || !EmbeddedMagazineBoxes.TryGetValue(Package, out var box)) return null;
            var body = Parts.Where(p => p.Visible && !p.Skinned && !p.Simulated).MaxBy(p => Model.Mesh.Blocks[p.BlockIndex].Indices.Length);
            return body is null ? null : (body, box.Min, box.Max, EmbeddedMagazineMaxShells.GetValueOrDefault(Package));
        }
    }

    /// <summary>Whether a node name is a magazine: it names a clip, drum or magazine and is not upgrade geometry (<c>GEO_Upg_clip</c>).</summary>
    public static bool IsMagazineNode(string node) =>
        !node.Contains("upg", StringComparison.OrdinalIgnoreCase)
        && (node.Contains("clip", StringComparison.OrdinalIgnoreCase) || node.Contains("drum", StringComparison.OrdinalIgnoreCase) || node.Contains("magazine", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The model's root in the mount bone's space (row-vector): the inverse of the handle node, the mount's scale, then the body
    /// node's offset from its bone. Multiply a part's <see cref="WeaponPart.ToRoot"/> (or a locator) by this, then by the bone's
    /// pose, to place it on the character. The handle node's absence, or a singular (non-invertible) handle transform, is noted
    /// and treated as the root; the result is always finite (identity, with a note, otherwise).
    /// </summary>
    public Matrix4x4 RootToBone(WeaponMount mount, List<string>? notes = null)
    {
        if (!Tree.Contains(mount.HandleNode)) notes?.Add($"{Package}: handle node '{mount.HandleNode}' not in its object tree; the root is used");
        Matrix4x4 inverse;
        if (mount.ModelAxes)
        {
            // Ork O6 stow spots: only the handle's position is put at the origin; the weapon then turns by its own axes (BodyLocal).
            inverse = Matrix4x4.CreateTranslation(-Tree.ModelSpace(mount.HandleNode).Translation);
            if (!Finite(inverse)) { notes?.Add($"{Package}: handle node '{mount.HandleNode}' has a non-finite position; the root is used"); inverse = Matrix4x4.Identity; }
        }
        else if (!Matrix4x4.Invert(Tree.ModelSpace(mount.HandleNode), out inverse) || !Finite(inverse))
        {
            notes?.Add($"{Package}: handle node '{mount.HandleNode}' has a singular transform; the root is used");
            inverse = Matrix4x4.Identity;
        }
        var result = inverse * Matrix4x4.CreateScale(mount.Scale) * mount.BodyLocal;
        if (Finite(result)) return result;
        notes?.Add($"{Package}: mount '{mount.BodyNode}' gives a non-finite transform; identity is used");
        return Matrix4x4.Identity;
    }

    static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);


    /// <summary>
    /// For worn models (<see cref="WeaponAttachment.IsWorn"/>: jump packs, the Devastator backpack, whose trees copy the rig): the
    /// skeleton bone a static part hangs under and the part's vertex → bone transform (row-vector), as for the Chaos armour's rigid
    /// blocks (vertex × node's transforms up to the bone, then the bone's pose). Null for skinned parts or when no ancestor is a bone.
    /// </summary>
    public (string Bone, Matrix4x4 PartToBone)? WornPlacement(WeaponPart part, Func<string, bool> isSkeletonBone) =>
        part.Skinned ? null : Tree.UnderBone(part.Node, isSkeletonBone);

    /// <summary>Reads the attachment's model from its package (on demand; one package read).</summary>
    /// <exception cref="FileNotFoundException">A required archive or entry is missing.</exception>
    /// <exception cref="InvalidDataException">An archive, attribute file, object tree or mesh is malformed.</exception>
    /// <exception cref="NotSupportedException">A mesh or document uses a layout not decoded yet (e.g. other DCM type-2 blocks).</exception>
    /// <exception cref="IOException">The file cannot be read (also <see cref="EndOfStreamException"/> on a truncated archive).</exception>
    public static WeaponModel Load(string gameDir, WeaponAttachment attachment, List<string>? notes = null) =>
        Load(attachment.PackagePath(gameDir), attachment.VisualName, attachment.HiddenByDefault, notes);

    /// <summary>Reads the model <paramref name="visualName"/> (mesh and object tree of that name) from an archive.</summary>
    /// <param name="hiddenNodes">Nodes not drawn in the default look (upgrade geometry).</param>
    /// <exception cref="FileNotFoundException">A required archive or entry is missing.</exception>
    /// <exception cref="InvalidDataException">An archive, attribute file, object tree or mesh is malformed.</exception>
    /// <exception cref="NotSupportedException">A mesh or document uses a layout not decoded yet (e.g. other DCM type-2 blocks).</exception>
    /// <exception cref="IOException">The file cannot be read (also <see cref="EndOfStreamException"/> on a truncated archive).</exception>
    public static WeaponModel Load(string archivePath, string visualName, IReadOnlySet<string>? hiddenNodes = null, List<string>? notes = null)
    {
        var model = StaticModel.Load(archivePath, visualName, notes);
        var tree = ObjectTree.Load(archivePath, visualName, notes) ?? throw new FileNotFoundException($"'{visualName}.O3d' not found in {Path.GetFileName(archivePath)}.");
        return From(Path.GetFileNameWithoutExtension(archivePath), model, tree, hiddenNodes, notes);
    }

    /// <summary>
    /// Ork O6: a node named <c>*_lodl</c> is the low-detail copy of a part. Every Space Marine model's copies carry LODLevel 16 and are hidden by it; the Ork shoota's
    /// <c>geo_shoota_lodl</c> (526 triangles over the same bounds as <c>GEO_Shoota</c>'s 718) says -1 (all levels) and would be drawn over the full mesh. **Interpretation**:
    /// the name says what it is, so it is not drawn (no change to any Space Marine model).
    /// </summary>
    public static bool IsLowDetailCopy(string node) => node.EndsWith("_lodl", StringComparison.OrdinalIgnoreCase);

    /// <summary>Places the mesh blocks by the object tree's visuals (testable without an install).</summary>
    public static WeaponModel From(string package, StaticModel model, ObjectTree tree, IReadOnlySet<string>? hiddenNodes = null, List<string>? notes = null)
    {
        var parts = new List<WeaponPart>();
        var geometry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in tree.Visuals)
        {
            if (!v.MeshName.Equals(model.MeshName, StringComparison.OrdinalIgnoreCase))
            { notes?.Add($"{package}: visual '{v.RefNode}' names mesh '{v.MeshName}', not '{model.MeshName}'; skipped"); continue; }
            if (v.MeshId < 0 || v.MeshId >= model.Mesh.Blocks.Count)
            { notes?.Add($"{package}: visual '{v.RefNode}' names block {v.MeshId} of {model.Mesh.Blocks.Count}; skipped"); continue; }
            geometry.Add(v.RefNode);
            var block = model.Mesh.Blocks[v.MeshId];
            bool skinned = block.Skin is not null;
            if (!skinned && !tree.Contains(v.RefNode)) notes?.Add($"{package}: visual node '{v.RefNode}' not in the tree; block {v.MeshId} placed at the root");
            string? hidden = !v.Renderable ? "not renderable"
                : !v.NearLevel ? $"far detail level (LODLevel {v.LodLevel})"
                : IsLowDetailCopy(v.RefNode) ? "low-detail copy (node name)"
                : hiddenNodes?.Contains(v.RefNode) == true ? "upgrade geometry" : null;
            parts.Add(new WeaponPart(v.MeshId, v.RefNode, skinned ? Matrix4x4.Identity : tree.ModelSpace(v.RefNode), skinned,
                block.Simulation is not null, hidden is null, hidden));
        }
        foreach (int b in Enumerable.Range(0, model.Mesh.Blocks.Count).Where(b => parts.All(p => p.BlockIndex != b)))
            notes?.Add($"{package}: block {b} has no visual; not drawn");
        var magazines = parts.Where(p => p.Visible && !p.Skinned && !p.Simulated && IsMagazineNode(p.Node)).Select(p => p.Node).ToList();
        if (magazines.Count > 1) notes?.Add($"{package}: {magazines.Count} parts are named like a magazine ({string.Join(", ", magazines)}); the first one drops in a reload");
        var locators = tree.Order.Where(n => !geometry.Contains(n)).Select(n => new WeaponLocator(n, tree.ModelSpace(n))).ToList();
        return new WeaponModel(package, model, tree, parts, locators);
    }
}
