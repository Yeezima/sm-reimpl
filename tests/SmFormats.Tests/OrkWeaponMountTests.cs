using System.Numerics;
using SmContent;
using SmFormats.Baf;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmSim;
using Xunit;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>Ork O6: weapon mounts a rig supplies where the wargear names no nodes, our stow spots, the held stikkbomb and the low-detail rule (synthetic fixtures only).</summary>
public class OrkWeaponMountTests
{
    static (string, object)[] NoNodes(string package) =>
    [
        ("visual_name", package), ("visual_package", package),
        ("wield_info_by_upgrade", new (string, object)[] { ("entry", new (string, object)[]
            { ("handle_node", ""), ("body_node", ""), ("scale", 1f), ("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) }) }) }),
        ("stow_info", new (string, object)[] { ("handle_node", ""), ("body_node", ""), ("scale", 1f), ("hide_on_stow", false) }),
        ("upgrades", new (string, object)[] { ("upgrades", Array.Empty<(string, object)>()) }),
    ];

    static (string, object)[] Named(string package, string node) =>
    [
        ("visual_name", package), ("visual_package", package),
        ("wield_info_by_upgrade", new (string, object)[] { ("entry", new (string, object)[]
            { ("handle_node", node), ("body_node", node), ("scale", 1f), ("required_upgrade_name", new (string, object)[] { ("required_list", Array.Empty<(string, object)>()) }) }) }),
        ("stow_info", new (string, object)[] { ("handle_node", ""), ("body_node", ""), ("scale", 1f), ("hide_on_stow", false) }),
        ("upgrades", new (string, object)[] { ("upgrades", Array.Empty<(string, object)>()) }),
    ];

    static BafDocument Wargear(string slot, params (string Name, object Value)[] attachments) => BafDocument.Read(SyntheticBafWriter.Build(
        ("visual_data", new (string, object)[] { ("attachments", attachments) }),
        ("common_data", new (string, object)[] { ("slot", slot) })));

    static readonly string[] Bones = ["RIG_Root", "RIG_Spine3", "RIG_Pelvis", "RIG_Prop_RHand", "RIG_Prop_M_Chest"];

    static readonly RigWeaponMount Gun = new("RIG_Prop_M_Chest", "RIG_Prop_M_Chest",
        new RigStowSpot("RIG_Spine3", new Vector3(0.1f, -0.45f, 0), new Vector3(0.7f, 0, 0.7f), new Vector3(0.7f, 0, -0.7f), 1f, "ours:test back"), TwoHanded: true);

    static Dictionary<string, BafDocument> Docs() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ork_gun"] = Wargear("ranged", ("gun", NoNodes("ork_gun_mp"))),
        ["ork_nade"] = Wargear("equipment"),
        ["sm_gun"] = Wargear("ranged", ("gun", Named("sm_gun_mp", "rig_prop_m_chest"))),
    };

    [Fact]
    public void A_rig_supplies_the_mounts_for_wargear_that_names_no_nodes()
    {
        var mounts = new Dictionary<string, RigWeaponMount>(StringComparer.OrdinalIgnoreCase)
        {
            ["ork_gun"] = Gun,
            ["ork_nade"] = new("RIG_Prop_RHand", "RIG_Prop_RHand", null, HeldModel: new RigHeldModel("nade_pkg", "nade_vis")),
            ["sm_gun"] = new("RIG_Prop_RHand", "RIG_Prop_RHand", null),   // the wargear names its nodes: they win
        };

        // Without the rig's table a wargear without nodes is a worn model, as before.
        var plain = WeaponModels.Build(Docs(), null, Bones, []);
        Assert.True(plain.ByWargear["ork_gun"].Single().IsWorn);
        Assert.Null(plain.Held("ork_gun"));
        Assert.Empty(plain.ByWargear["ork_nade"]);

        var notes = new List<string>();
        var models = WeaponModels.Build(Docs(), null, Bones, notes, rigMounts: mounts);
        var gun = models.Held("ORK_GUN")!;
        Assert.False(gun.IsWorn);
        Assert.True(gun.TwoHanded);
        Assert.Equal(("RIG_Prop_M_Chest", "RIG_Prop_M_Chest", Matrix4x4.Identity, 1f, false), (gun.Wield!.HandleNode, gun.Wield.Bone, gun.Wield.BodyLocal, gun.Wield.Scale, gun.Wield.ModelAxes));
        Assert.Equal(("ours:test back", "RIG_Spine3", true), (gun.Stow!.BodyNode, gun.Stow.Bone, gun.Stow.ModelAxes));
        Assert.Equal(new Vector3(0.1f, -0.45f, 0), gun.Stow.BodyLocal.Translation);
        Assert.False(gun.HideOnStow);
        Assert.Empty(notes);

        // A wargear with no attachment at all draws the rig's held model, in hand only.
        var nade = models.Held("ork_nade")!;
        Assert.Equal(("nade_pkg", "nade_vis", "equipment"), (nade.VisualPackage, nade.VisualName, nade.Slot));
        Assert.Equal("RIG_Prop_RHand", nade.Wield!.Bone);
        Assert.Null(nade.Stow);
        Assert.True(nade.HideOnStow);
        Assert.False(nade.IsWorn);
        Assert.False(nade.TwoHanded);

        // Named nodes win over the rig's table.
        var sm = models.Held("sm_gun")!;
        Assert.Equal(("rig_prop_m_chest", "RIG_Prop_M_Chest", false), (sm.Wield!.HandleNode, sm.Wield.Bone, sm.TwoHanded));
        Assert.Null(sm.Stow);
    }

    [Fact]
    public void In_hand_rules_follow_the_slot_the_melee_weapon_and_the_throw()
    {
        const string chest = "RIG_Prop_M_Chest", left = "RIG_Prop_LHand", right = "RIG_Prop_RHand";
        bool Hand(HandRole role, WeaponSlot shown, bool melee, bool nade, bool two, string? bone, string? meleeBone) =>
            WeaponHands.InHand(role, shown, melee, nade, two, bone, meleeBone);

        // Nothing out: the slot decides; the thrown item and the melee weapon are away.
        Assert.True(Hand(HandRole.Primary, WeaponSlot.Ranged, false, false, true, chest, null));
        Assert.False(Hand(HandRole.Pistol, WeaponSlot.Ranged, false, false, false, left, null));
        Assert.True(Hand(HandRole.Pistol, WeaponSlot.Pistol, false, false, false, left, null));
        Assert.False(Hand(HandRole.Melee, WeaponSlot.Ranged, false, false, false, right, null));
        Assert.False(Hand(HandRole.Grenade, WeaponSlot.Ranged, false, false, false, right, null));
        // The thrown item is in hand while thrown, whatever slot.
        Assert.True(Hand(HandRole.Grenade, WeaponSlot.Pistol, false, true, false, right, null));
        // The melee weapon out: in hand itself; the two-handed shoota is put away, the slugga stays (other bone, one hand).
        Assert.True(Hand(HandRole.Melee, WeaponSlot.Ranged, true, false, false, right, right));
        Assert.False(Hand(HandRole.Primary, WeaponSlot.Ranged, true, false, true, chest, right));
        Assert.True(Hand(HandRole.Pistol, WeaponSlot.Pistol, true, false, false, left, right));
        // The marine rule: a melee weapon on the same bone puts the other away; with a different bone and no two-handed flag both stay.
        Assert.False(Hand(HandRole.Primary, WeaponSlot.Ranged, true, false, false, chest, "rig_prop_m_chest"));
        Assert.True(Hand(HandRole.Primary, WeaponSlot.Ranged, true, false, false, chest, right));
        // A thrown item in the melee hand is put away by the melee weapon too.
        Assert.False(Hand(HandRole.Grenade, WeaponSlot.Ranged, true, true, false, right, right));
    }

    [Fact]
    public void A_mount_on_a_bone_the_skeleton_lacks_is_noted_and_unresolved()
    {
        var notes = new List<string>();
        var models = WeaponModels.Build(Docs(), null, ["RIG_Root"], notes, rigMounts: new Dictionary<string, RigWeaponMount> { ["ork_gun"] = Gun });
        var gun = models.Held("ork_gun")!;
        Assert.Null(gun.Wield!.Bone);
        Assert.Null(gun.Stow!.Bone);
        Assert.Contains(notes, n => n.Contains("ork_gun") && n.Contains("RIG_Prop_M_Chest"));
        Assert.Contains(notes, n => n.Contains("ork_gun") && n.Contains("RIG_Spine3"));
    }

    [Fact]
    public void A_stow_spot_turns_the_weapon_by_its_own_axes_about_its_handle()
    {
        // A weapon whose handle node sits at (1,2,3) and is turned: the handle's own frame must not matter for a stow spot.
        var tree = ObjectTreeOf("gun", [Bone3("RIG_Prop_M_Chest", [1, 2, 3], [10, 20, 30])]);
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([Tri()]));
        var model = WeaponModel.From("gun", new StaticModel("gun.oppc", "gun", mesh, []), tree);
        var spot = new RigStowSpot("RIG_Spine3", new Vector3(0.5f, -0.4f, 0.1f), new Vector3(0, 0, -1), new Vector3(1, 0, 0), 2f, "ours:test");
        var mount = new WeaponMount("rig_prop_m_chest", "ours:test", "RIG_Spine3", spot.BoneLocal, spot.Scale, ModelAxes: true);
        var m = model.RootToBone(mount);

        // The handle point lands on the offset; the model's +Y (barrel) points along Length and +Z along Up (scaled by 2).
        Assert.True(Vector3.Distance(spot.Offset, Vector3.Transform(new Vector3(1, 2, 3), m)) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(0, 0, -2), Vector3.TransformNormal(Vector3.UnitY, m)) < 1e-5f);
        Assert.True(Vector3.Distance(new Vector3(2, 0, 0), Vector3.TransformNormal(Vector3.UnitZ, m)) < 1e-5f);
        // The scale is about the handle: a point 1 m from it along the barrel ends 2 m from the offset.
        Assert.True(Vector3.Distance(spot.Offset + new Vector3(0, 0, -2), Vector3.Transform(new Vector3(1, 3, 3), m)) < 1e-5f);

        // The wield mount (not model axes) still puts the handle's whole frame on the bone: the handle node maps to the identity.
        var wield = model.RootToBone(new WeaponMount("rig_prop_m_chest", "RIG_Prop_M_Chest", "RIG_Prop_M_Chest", Matrix4x4.Identity, 1f));
        var handle = Matrix4x4.Multiply(tree.ModelSpace("rig_prop_m_chest"), wield);
        Assert.True(Vector3.Distance(Vector3.Zero, handle.Translation) < 1e-4f);
        Assert.True(Math.Abs(handle.M11 - 1) < 1e-4f && Math.Abs(handle.M22 - 1) < 1e-4f && Math.Abs(handle.M33 - 1) < 1e-4f);
    }

    [Theory]
    [InlineData(0, 0, 1, 1, 0, 0)]       // across the length
    [InlineData(0, 0, 1, 0, 0, 1)]       // up along the length: only the projection counts, a fallback up is picked
    [InlineData(0.7f, 0, 0.7f, 0.7f, 0, -0.7f)]
    [InlineData(-1, 0, 0, 0, 1, 0)]
    public void A_stow_spot_makes_a_right_handed_rotation(float lx, float ly, float lz, float ux, float uy, float uz)
    {
        var spot = new RigStowSpot("B", new Vector3(1, 2, 3), new Vector3(lx, ly, lz), new Vector3(ux, uy, uz));
        var m = spot.BoneLocal;
        var x = new Vector3(m.M11, m.M12, m.M13); var y = new Vector3(m.M21, m.M22, m.M23); var z = new Vector3(m.M31, m.M32, m.M33);
        Assert.True(Math.Abs(x.Length() - 1) < 1e-5f && Math.Abs(y.Length() - 1) < 1e-5f && Math.Abs(z.Length() - 1) < 1e-5f);
        Assert.True(Math.Abs(Vector3.Dot(x, y)) < 1e-5f && Math.Abs(Vector3.Dot(y, z)) < 1e-5f && Math.Abs(Vector3.Dot(x, z)) < 1e-5f);
        Assert.True(Vector3.Distance(Vector3.Cross(x, y), z) < 1e-5f);   // right-handed: no mirrored weapon
        Assert.True(Vector3.Distance(Vector3.Normalize(new Vector3(lx, ly, lz)), y) < 1e-5f);
        Assert.Equal(new Vector3(1, 2, 3), m.Translation);
    }

    [Fact]
    public void A_node_named_like_a_low_detail_copy_is_not_drawn_even_when_its_level_says_all()
    {
        var mesh = DcmMesh.Read(SyntheticDcmWriter.Build([Tri(), Tri(), Tri()]));
        var tree = ObjectTreeOf("gun", [Bone3("GEO_Gun", [0, 0, 0], [0, 0, 0]), Bone3("geo_gun_lodl", [0, 0, 0], [0, 0, 0]), Bone3("GEO_Other_LODL", [0, 0, 0], [0, 0, 0])],
            [Visual("StaticMeshVisual", "GEO_Gun", "gun", 0, -1), Visual("StaticMeshVisual", "geo_gun_lodl", "gun", 1, -1), Visual("StaticMeshVisual", "GEO_Other_LODL", "gun", 2, 16)]);
        var model = WeaponModel.From("gun", new StaticModel("gun.oppc", "gun", mesh, []), tree);
        Assert.Equal([true, false, false], model.Parts.Select(p => p.Visible));
        Assert.Equal("low-detail copy (node name)", model.Parts[1].HiddenBecause);
        Assert.Contains("far detail level", model.Parts[2].HiddenBecause);   // the Space Marine models' copies: hidden by their level, as before
        Assert.True(WeaponModel.IsLowDetailCopy("geo_shoota_LODL"));
        Assert.False(WeaponModel.IsLowDetailCopy("GEO_body"));
        Assert.False(WeaponModel.IsLowDetailCopy("lodl_marker"));
    }

    [Fact]
    public void The_marine_profile_has_no_mounts_and_the_ork_table_names_its_prop_bones_and_stow_spots()
    {
        Assert.Empty(RigProfiles.Marine.WeaponMounts);
        var table = RigProfiles.OrkBoy.WeaponMounts;
        Assert.Equal(["ork_coop_boy_2hand_shoota_rapid", "ork_coop_boy_choppa", "ork_coop_boy_stikkbomb", "slugga"], table.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var (wargear, mount) in table)
        {
            Assert.StartsWith("RIG_Prop_", mount.HandleNode, StringComparison.Ordinal);
            Assert.Equal(mount.HandleNode, mount.WieldBone);
            if (mount.Stow is not { } spot) continue;
            Assert.True(spot.Length.LengthSquared() > 0 && spot.Up.LengthSquared() > 0, wargear);
            Assert.True(float.IsFinite(spot.Offset.X + spot.Offset.Y + spot.Offset.Z), wargear);
            Assert.StartsWith("ours:", spot.Label, StringComparison.Ordinal);   // every stow spot is ours and says so
        }
        Assert.Equal(("RIG_Prop_LHand", "RIG_Prop_M_Chest", "RIG_Prop_RHand"),
            (table["slugga"].WieldBone, table["ork_coop_boy_2hand_shoota_rapid"].WieldBone, table["ork_coop_boy_choppa"].WieldBone));
        Assert.True(table["ork_coop_boy_2hand_shoota_rapid"].TwoHanded);
        Assert.False(table["slugga"].TwoHanded);
        var stikk = table["ork_coop_boy_stikkbomb"];
        Assert.Null(stikk.Stow);
        Assert.NotNull(stikk.HeldModel);
        // Every wargear the profile lists as the Ork's own has a mount.
        Assert.All(RigProfiles.OrkBoy.DefaultWargear, w => Assert.True(table.ContainsKey(w), w));
    }

    // ---- fixtures ----

    static SyntheticDcmWriter.Block Tri() => new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("gun", 0, 3, 0, 3)]);

    static Struct Bone3(string name, float[] position, float[] rotation) => new("Node3D",
        ("Name", new S(name)), ("Position", new Vec(position)), ("Rotation", new Vec(rotation)), ("Scale", new Vec(1, 1, 1)), ("Children", new List()));

    static Struct Visual(string cls, string node, string mesh, int id, int lod) => new(cls,
        ("RefNode", new S(node)), ("MeshName", new S(mesh)), ("MeshID", new I(id)), ("LODLevel", new I(lod)), ("Renderable", new B(true)));

    static ObjectTree ObjectTreeOf(string name, Struct[] nodes, Struct[]? visuals = null) =>
        ObjectTree.From(name, BodDocument.Read(Document(new Struct("Object3D",
            ("Skeleton", new Ref(new Struct("Skeleton3D", ("Name", new S(name)), ("Children", new List(nodes.Cast<Node>().ToArray()))))),
            ("Visuals", new List((visuals ?? []).Cast<Node>().ToArray()))))).Root);
}
