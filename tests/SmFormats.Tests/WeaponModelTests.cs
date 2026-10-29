using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Obpk;
using Xunit;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>M24: type-2 mesh blocks, object trees, wargear attachments and weapon models (synthetic fixtures only).</summary>
public class WeaponModelTests
{
    static SyntheticDcmWriter.Block Tri(float z, string material = "gun", int? simulation = null) =>
        new([new(0, 0, z), new(1, 0, z), new(0, 1, z)], [0, 1, 2], [new(material, 0, 3, 0, 3)], SimulationBytes: simulation);

    [Fact]
    public void Type_2_blocks_read_as_static_geometry_with_a_trailing_range()
    {
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([Tri(0), Tri(5, "cable", simulation: 37)]));
        Assert.Null(mesh.Blocks[0].Simulation);
        var cable = mesh.Blocks[1];
        Assert.Equal(2, cable.Flags);
        Assert.Equal(new Vector3(1, 0, 5), cable.Positions[1]);
        Assert.Equal("cable", cable.Materials.Single().Name);
        var sim = Assert.IsType<DcmSimulationData>(cable.Simulation);
        Assert.Equal((0u, 1u, 37), (sim.Unknown, sim.Count, sim.Size));
        Assert.Equal(0, sim.Offset % 16);
    }

    [Fact]
    public void Other_type_2_layouts_stay_not_supported()
    {
        var bytes = SyntheticDcmWriter.Build([Tri(0, simulation: 20)]);
        int headerSize = BitConverter.ToInt32(bytes, 0);
        // The trailer's offset field (last 8 bytes of the header: offset, size) moved away from the attribute stream.
        BitConverter.GetBytes(BitConverter.ToInt32(bytes, headerSize - 8) + 64).CopyTo(bytes, headerSize - 8);
        Assert.Throws<NotSupportedException>(() => DcmMesh.Read(bytes));
    }

    static Struct Node3D(string name, float[] position, float[] rotation, params Node[] children) => new("Node3D",
        ("Name", new S(name)), ("Position", new Vec(position)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(1, 1, 1)), ("Children", new List(children)));

    static Struct Visual(string cls, string node, string mesh, int id, int lod = -1) => new(cls,
        ("RefNode", new S(node)), ("MeshName", new S(mesh)), ("MeshID", new I(id)), ("LODLevel", new I(lod)), ("Renderable", new B(true)));

    static ObjectTree Tree(string name, Struct skeleton, params Node[] visuals) =>
        ObjectTree.From(name, BodDocument.Read(Document(new Struct("Object3D", ("Skeleton", new Ref(skeleton)), ("Visuals", new List(visuals))))).Root);

    [Fact]
    public void Object_tree_composes_nodes_child_first_and_finds_the_bone_above_a_marker()
    {
        var tree = Tree("body", new Struct("Skeleton3D", ("Name", new S("body")), ("Children", new List(
            Node3D("RIG_SpineA", [0, 0, 1], [0, 0, 90],
                Node3D("MKR_Stow_Bolter", [1, 0, 0], [0, 0, 0]))))));
        Assert.Equal(["body", "RIG_SpineA", "MKR_Stow_Bolter"], tree.Order);
        // (0,0,0) in the marker: 1 m along the spine's X, which the spine turns to +Y, then up 1 m.
        var p = Vector3.Transform(Vector3.Zero, tree.ModelSpace("mkr_stow_bolter"));
        Assert.True(Vector3.Distance(new Vector3(0, 1, 1), p) < 1e-5f, p.ToString());
        var under = tree.UnderBone("mkr_stow_bolter", n => n.StartsWith("RIG_"));
        Assert.Equal("RIG_SpineA", under!.Value.Bone);
        Assert.Equal(new Vector3(1, 0, 0), under.Value.Local.Translation);
        Assert.Null(tree.UnderBone("no_such_node", _ => true));
    }

    static (string, object)[] Mount(string handle, string body, float scale = 1f) => [("handle_node", handle), ("body_node", body), ("scale", scale)];

    static BafDocument Wargear(string slot, params (string Name, object Value)[] attachments) => BafDocument.Read(SyntheticBafWriter.Build(
        ("visual_data", new (string, object)[] { ("attachments", attachments) }),
        ("common_data", new (string, object)[] { ("slot", slot) }),
        ("fx", new (string, object)[] { ("play_fx", new (string, object)[] { ("marker", new (string, object)[] { ("marker", "fx_muzzle") }) }) })));

    static (string, object)[] Attachment(string package, (string, object)[]? wield, (string, object)[]? stow, bool hideOnStow = false,
        (string, object)[]? gatedWield = null, params (string, object)[] upgrades)
    {
        var wields = new List<(string, object)>();
        if (gatedWield is not null) wields.Add(("entry", gatedWield.Append(("required_upgrade_name", (object)new (string, object)[] {
            ("required_list", new (string, object)[] { ("upgrade_name", "mp_perk_x") }) })).ToArray()));
        wields.Add(("entry", (wield ?? Mount("", "")).Append(("required_upgrade_name", (object)new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) })).ToArray()));
        return
        [
            ("visual_name", package), ("visual_package", package),
            ("wield_info_by_upgrade", wields.ToArray()),
            ("stow_info", (stow ?? Mount("", "")).Append(("hide_on_stow", (object)hideOnStow)).ToArray()),
            ("upgrades", new (string, object)[] { ("upgrades", upgrades) }),
        ];
    }

    static (string, object) Upgrade(string show, string? hide, string perk) => ("entry", new (string, object)[]
    {
        ("visuals_to_show", new (string, object)[] { ("name", show) }),
        ("visuals_to_hide", hide is null ? Array.Empty<(string, object)>() : new (string, object)[] { ("name", hide) }),
        ("required_upgrade_name", new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", perk) }) }),
    });

    static ObjectTree Body() => Tree("spacemarine_body_default", new Struct("Skeleton3D", ("Name", new S("spacemarine_body_default")), ("Children", new List(
        Node3D("RIG_SpineA", [0, 0, 1.4f], [0, 0, 0],
            Node3D("MKR_Stow_Bolter", [0, -0.3f, 0], [0, 90, 0]),
            Node3D("RIG_Prop_M_Chest", [0, 0.4f, 0.3f], [0, 0, 0]))))));

    [Fact]
    public void Wargear_attachments_resolve_hand_bones_stow_markers_upgrades_and_worn_models()
    {
        var notes = new List<string>();
        var wargear = new Dictionary<string, BafDocument>
        {
            ["sm_mp_ranged_gun"] = Wargear("ranged", ("gun", Attachment("gun_mp", Mount("rig_prop_m_chest", "rig_prop_m_chest"), Mount("rig_prop_m_chest", "mkr_stow_bolter", 0.9f),
                gatedWield: Mount("rig_prop_m_chest", "rig_prop_lhand"), upgrades: Upgrade("geo_upg_barrel", "geo_barrel", "mp_perk_barrel")))),
            ["sm_mp_abil_pack"] = Wargear("coreability", ("wargear\\attachment_entry", Attachment("pack_mp", null, null))),
            ["sm_mp_melee_odd"] = Wargear("melee", ("blade", Attachment("blade", Mount("rig_prop_rhand", "rig_prop_rhand"), Mount("rig_prop_rhand", "mkr_nowhere"), hideOnStow: true))),
            ["sm_mp_eq_grenade"] = Wargear("equipment"),
            ["sm_mp_pistol_gun"] = Wargear("pistol", ("gun", Attachment("gun_mp", Mount("rig_prop_lhand", "rig_prop_lhand"), null))),
            // Only a perk-gated wield entry and no stow_info: held (not worn), but with no default mount.
            ["sm_mp_ranged_perk_only"] = Wargear("ranged", ("gun", new (string, object)[]
            {
                ("visual_name", "gun_mp"), ("visual_package", "gun_mp"),
                ("wield_info_by_upgrade", new (string, object)[] { ("entry", Mount("rig_prop_m_chest", "rig_prop_m_chest").Append(("required_upgrade_name",
                    (object)new (string, object)[] { ("required_list", new (string, object)[] { ("upgrade_name", "mp_perk_y") }) })).ToArray()) }),
            })),
        };
        var models = WeaponModels.Build(wargear, Body(), ["RIG_SpineA", "RIG_Prop_M_Chest", "RIG_Prop_RHand"], notes);

        var gun = models.Held("SM_MP_RANGED_GUN")!;
        Assert.Equal(("gun", "ranged", "gun_mp", "gun_mp"), (gun.Name, gun.Slot, gun.VisualPackage, gun.VisualName));
        // In hand: the wield entry without requirements; the bone keeps the skeleton's case.
        Assert.Equal(("rig_prop_m_chest", "RIG_Prop_M_Chest", Matrix4x4.Identity, 1f), (gun.Wield!.HandleNode, gun.Wield.Bone, gun.Wield.BodyLocal, gun.Wield.Scale));
        Assert.Contains(notes, n => n.Contains("sm_mp_ranged_gun") && n.Contains("mp_perk_x"));
        // Stowed: the body marker hangs from RIG_SpineA with its own offset; the scale is kept.
        Assert.Equal(("mkr_stow_bolter", "RIG_SpineA", 0.9f), (gun.Stow!.BodyNode, gun.Stow.Bone, gun.Stow.Scale));
        Assert.Equal(new Vector3(0, -0.3f, 0), gun.Stow.BodyLocal.Translation);
        Assert.Equal("RIG_SpineA", models.BodyMarkers["MKR_STOW_BOLTER"].Bone);
        Assert.False(models.BodyMarkers.ContainsKey("RIG_Prop_M_Chest"));   // bones are not markers
        // Upgrade geometry is hidden by default; the fx markers are collected.
        Assert.Equal(["geo_upg_barrel"], gun.HiddenByDefault);
        Assert.Equal(["mp_perk_barrel"], gun.Upgrades.Single().RequiredUpgrades);
        Assert.Equal(["fx_muzzle"], gun.FxMarkers);
        // Another wargear drawing the same model without upgrades hides the same upgrade parts.
        var pistol = models.Held("sm_mp_pistol_gun")!;
        Assert.Empty(pistol.Upgrades);
        Assert.Equal(["geo_upg_barrel"], pistol.HiddenByDefault);
        Assert.Null(pistol.Stow);

        var perkOnly = Assert.Single(models.ByWargear["sm_mp_ranged_perk_only"]);
        Assert.Equal((null, null, false), (perkOnly.Wield, perkOnly.Stow, perkOnly.IsWorn));
        Assert.Empty(models.Worn("sm_mp_ranged_perk_only"));
        var pack = Assert.Single(models.Worn("sm_mp_abil_pack"));
        Assert.True(pack.IsWorn);
        Assert.Null(models.Held("sm_mp_abil_pack"));

        var blade = models.Held("sm_mp_melee_odd")!;
        Assert.True(blade.HideOnStow);
        Assert.Null(blade.Stow!.Bone);
        Assert.Contains(notes, n => n.Contains("mkr_nowhere"));
        Assert.Empty(models.ByWargear["sm_mp_eq_grenade"]);
        Assert.Equal(Path.Combine("g", "data", "packages", "gun_mp.oppc"), gun.PackagePath("g"));
    }

    [Fact]
    public void Weapon_model_places_blocks_on_their_nodes_and_finds_the_muzzle()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-weapon-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var mesh = SyntheticDcmWriter.Build(
            [
                Tri(0),                                         // 0 body, on GEO_Body
                Tri(0),                                         // 1 upgrade barrel, hidden by default
                Tri(0),                                         // 2 far detail level
                new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("gun", 0, 3, 0, 3)],
                    Skinning: new([new("RIG_Teeth", Matrix4x4.Identity)], [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX], new byte[12])),   // 3 skinned
                Tri(0, simulation: 16),                         // 4 cable
                Tri(0),                                         // 5 no visual
            ]);
            var skeleton = new Struct("Skeleton3D", ("Name", new S("gun_mp")), ("Children", new List(
                Node3D("RIG_Prop_M_Chest", [0, 0, 0], [0, 0, 90],
                    Node3D("GEO_Body", [1, 0, 0], [0, 0, 0]),
                    Node3D("GEO_upg_barrel", [2, 0, 0], [0, 0, 0]),
                    Node3D("GEO_Body_LODL", [1, 0, 0], [0, 0, 0]),
                    Node3D("fx_Muzzle", [3, 0, 0], [0, 0, -90]),
                    Node3D("fx_Muzzle01", [3, 0.1f, 0], [0, 0, -90]),
                    Node3D("fx_muzzleflash", [3, 0, 0], [0, 0, -90])),
                Node3D("GEO_Teeth", [5, 5, 5], [0, 0, 0]),
                Node3D("GEO_cable", [0, 0, 0], [0, 0, 0]))));
            var o3d = Document(new Struct("Object3D", ("Skeleton", new Ref(skeleton)), ("Visuals", new List(
                Visual("StaticMeshVisual", "GEO_Body", "gun_mp", 0, 31),
                Visual("StaticMeshVisual", "GEO_upg_barrel", "gun_mp", 1, 15),
                Visual("StaticMeshVisual", "GEO_Body_LODL", "gun_mp", 2, 16),
                Visual("SkinMeshVisual", "GEO_Teeth", "gun_mp", 3),
                Visual("StaticMeshVisual", "GEO_cable", "gun_mp", 4),
                Visual("StaticMeshVisual", "GEO_Body", "gun_mp", 9)))));
            string path = Path.Combine(dir, "gun_mp.oppc");
            File.WriteAllBytes(path, SyntheticObpkWriter.Build(
            [
                new("dcm", "gun_mp", mesh), new("O3d", "gun_mp", o3d),
                new("bmat", "gun", SyntheticBodWriter.Material("gun", "diff", [("DiffuseMap", "gun_dif")])),
                new("dds", "gun_dif", [0x44, 0x44, 0x53, 0x20]),
            ]));

            var notes = new List<string>();
            var model = WeaponModel.Load(path, "gun_mp", new HashSet<string>(["geo_upg_barrel"], StringComparer.OrdinalIgnoreCase), notes);
            Assert.Equal("gun_mp", model.Package);
            Assert.Equal([0, 1, 2, 3, 4], model.Parts.Select(p => p.BlockIndex));
            Assert.Equal([true, false, false, true, true], model.Parts.Select(p => p.Visible));
            Assert.Equal("upgrade geometry", model.Parts[1].HiddenBecause);
            Assert.Contains("far detail", model.Parts[2].HiddenBecause);
            Assert.Contains(notes, n => n.Contains("block 9"));
            Assert.Contains(notes, n => n.Contains("block 5 has no visual"));
            // Static blocks sit in their node's space: (0,0,0) on GEO_Body is 1 m along the prop's X, turned to +Y.
            var body = Vector3.Transform(Vector3.Zero, model.Parts[0].ToRoot);
            Assert.True(Vector3.Distance(new Vector3(0, 1, 0), body) < 1e-5f, body.ToString());
            // Skinned blocks are already in root space; type-2 blocks are flagged.
            Assert.True(model.Parts[3].Skinned);
            Assert.Equal(Matrix4x4.Identity, model.Parts[3].ToRoot);
            Assert.True(model.Parts[4].Simulated);
            Assert.True(model.Model.Surfaces.All(s => s.DiffuseDds is not null));

            // The muzzle: 3 m along the prop's X (+Y in root); its +Y axis (turned -90° then +90°) points along root +Y.
            var muzzle = model.Muzzle!;
            Assert.True(Vector3.Distance(new Vector3(0, 3, 0), muzzle.Position) < 1e-5f, muzzle.Position.ToString());
            Assert.True(Vector3.Distance(Vector3.UnitY, muzzle.Forward) < 1e-5f, muzzle.Forward.ToString());
            Assert.Equal(["fx_Muzzle", "fx_Muzzle01"], model.Muzzles.Select(m => m.Name));
            Assert.DoesNotContain(model.Locators, l => l.Name.StartsWith("GEO_Body", StringComparison.Ordinal));
            Assert.NotNull(model.Locator("FX_MUZZLEFLASH"));

            // Mounted: the handle node lands on the body node; the scale applies about the handle.
            var mount = new WeaponMount("rig_prop_m_chest", "mkr_stow_bolter", "RIG_SpineA", Matrix4x4.CreateTranslation(0, -0.3f, 0), 0.5f);
            var inBone = Vector3.Transform(muzzle.Position, model.RootToBone(mount));
            Assert.True(Vector3.Distance(new Vector3(1.5f, -0.3f, 0), inBone) < 1e-5f, inBone.ToString());
            // Worn placement: a part's transform up to the nearest skeleton bone above its node.
            var worn = model.WornPlacement(model.Parts[0], n => n == "RIG_Prop_M_Chest")!.Value;
            Assert.Equal(("RIG_Prop_M_Chest", new Vector3(1, 0, 0)), (worn.Bone, worn.PartToBone.Translation));
            Assert.Null(model.WornPlacement(model.Parts[3], _ => true));
            var missing = new List<string>();
            model.RootToBone(mount with { HandleNode = "rig_prop_rhand" }, missing);
            Assert.Contains(missing, n => n.Contains("rig_prop_rhand"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Singular_handle_transform_falls_back_to_the_root_with_a_note()
    {
        var tree = Tree("flat_gun", new Struct("Skeleton3D", ("Name", new S("flat_gun")), ("Children", new List(
            new Struct("Node3D", ("Name", new S("RIG_Prop_RHand")), ("Position", new Vec(1, 2, 3)), ("Rotation", new Vec(0, 0, 0)), ("Scale", new Vec(0, 0, 0)),
                ("Children", new List()))))));
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([Tri(0)]));
        var model = WeaponModel.From("flat_gun", new StaticModel("flat_gun.oppc", "flat_gun", mesh, []), tree);
        var notes = new List<string>();
        var m = model.RootToBone(new WeaponMount("rig_prop_rhand", "rig_prop_rhand", "RIG_Prop_RHand", Matrix4x4.CreateTranslation(0, 0, 1), 2f), notes);
        Assert.Equal(Matrix4x4.CreateScale(2f) * Matrix4x4.CreateTranslation(0, 0, 1), m);
        Assert.Contains(notes, n => n.Contains("singular"));
        // A non-finite body offset never reaches the caller either.
        var nan = model.RootToBone(new WeaponMount("rig_prop_rhand", "x", "B", Matrix4x4.CreateTranslation(float.NaN, 0, 0), 1f), notes);
        Assert.Equal(Matrix4x4.Identity, nan);
        Assert.Contains(notes, n => n.Contains("non-finite"));
    }

    [Fact]
    public void Object_tree_accepts_integer_vectors_and_notes_malformed_ones()
    {
        var notes = new List<string>();
        var tree = ObjectTree.From("t", BodDocument.Read(Document(new Struct("Object3D", ("Skeleton", new Ref(new Struct("Skeleton3D", ("Name", new S("t")),
            ("Children", new List(
                new Struct("Node3D", ("Name", new S("ints")), ("Position", new List(new I(1), new I(2), new I(3))), ("Children", new List())),
                new Struct("Node3D", ("Name", new S("short")), ("Position", new Vec(4, 5)), ("Rotation", new Vec(0, 0, 0)), ("Children", new List()))))))),
            ("Visuals", new List())))).Root, notes);
        Assert.Equal(new Vector3(1, 2, 3), tree.ModelSpace("ints").Translation);
        Assert.Equal(Vector3.Zero, tree.ModelSpace("short").Translation);
        var note = Assert.Single(notes);
        Assert.Contains("'short' Position", note);
    }

    [Fact]
    public void Projectile_visual_comes_from_the_renderable_extension()
    {
        var doc = BafDocument.Read(SyntheticBafWriter.Build(("extensions", new (string, object)[]
        {
            ("renderable_ext", new (string, object)[] { ("visual_package", "frag_grenade"), ("visual", "geo_frag_grenade") }),
        })));
        var v = WeaponModels.ProjectileVisualOf("pp_grenade_multiplayer", doc)!;
        Assert.Equal(("frag_grenade", "geo_frag_grenade", false), (v.VisualPackage, v.VisualName, v.IsInvisible));
        Assert.True(new ProjectileVisual("p", "invisible_projectile", "invisible_projectile").IsInvisible);
        Assert.Null(WeaponModels.ProjectileVisualOf("x", BafDocument.Read(SyntheticBafWriter.Build(("extensions", Array.Empty<(string, object)>())))));
    }
}
