using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Dcm;
using SmFormats.Havok;
using SmFormats.Obpk;
using SmContent.Customization;
using SmSim;
using Xunit;

namespace SmFormats.Tests;

/// <summary>
/// The engine-free parts of the customizer's armour rendering (T6): reading pieces one by one (what the client's piece cache does on worker threads), the
/// eye-lens table and rule, and the painted-material cache keys. Synthetic data only.
/// </summary>
public class AppearanceRenderingTests
{
    // ---- pieces one by one -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pieces_read_one_by_one_match_the_whole_load_even_from_several_threads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-pieces-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var rig = SyntheticHavokWriter.Build(
                [new("RIG_Root", -1, Vector3.Zero, Quaternion.Identity), new("RIG_L_ShoulderPad", 0, new Vector3(0, 0, 2), Quaternion.Identity)],
                [new("Idle", 1f, 31, [new(Vector3.Zero, Quaternion.Identity), new(new Vector3(0, 0, 2), Quaternion.Identity)])]);
            File.WriteAllBytes(Path.Combine(dir, "rig.oppc"), SyntheticObpkWriter.Build([new("hkxx", "havok_test__hkxx", rig)]));
            var skinned = SyntheticDcmWriter.Build([new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [0, 1, 2], [new("arm", 0, 3, 0, 3)],
                Skinning: new([new("RIG_Root", Matrix4x4.Identity)], [Vector4.UnitX, Vector4.UnitX, Vector4.UnitX], new byte[12]))]);
            File.WriteAllBytes(Path.Combine(dir, "test_arm.oppc"), SyntheticObpkWriter.Build([new("dcm", "test_arm", skinned)]));
            var pad = SyntheticDcmWriter.Build([new([new(0, 0, 2), new(1, 0, 2), new(0, 1, 2)], [0, 1, 2], [new("pad", 0, 3, 0, 3)])]);
            var o3d = SyntheticBodWriter.Material("GEO_L_ShoulderPad", "RIG_L_ShoulderPad", []);
            File.WriteAllBytes(Path.Combine(dir, "test_pad.oppc"), SyntheticObpkWriter.Build([new("dcm", "test_pad", pad), new("O3d", "test_pad", o3d)]));

            var rigFile = CharacterModel.LoadRig(Path.Combine(dir, "rig.oppc"), "havok_test__hkxx");
            Assert.Equal(["RIG_Root", "RIG_L_ShoulderPad"], rigFile.Skeleton.BoneNames);
            Assert.Throws<FileNotFoundException>(() => CharacterModel.LoadRig(Path.Combine(dir, "rig.oppc"), "nope"));

            var bones = new HashSet<string>(rigFile.Skeleton.BoneNames, StringComparer.Ordinal);
            var whole = CharacterModel.Load(Path.Combine(dir, "rig.oppc"), "havok_test__hkxx", [Path.Combine(dir, "test_arm.oppc"), Path.Combine(dir, "test_pad.oppc")]);

            // Several workers reading the same pieces at once (the piece cache's background reads) each get a complete, equal piece.
            var results = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
                CharacterModel.LoadPiece(bones, Path.Combine(dir, i % 2 == 0 ? "test_arm.oppc" : "test_pad.oppc"), new List<string>()))).ToArray();
            await Task.WhenAll(results);
            for (int i = 0; i < results.Length; i++)
            {
                var piece = await results[i];
                var expected = whole.Pieces[i % 2];
                Assert.Equal(expected.Name, piece.Name);
                Assert.Equal(expected.RigidBone, piece.RigidBone);
                Assert.Equal(expected.Model.Mesh.Blocks.Count, piece.Model.Mesh.Blocks.Count);
                Assert.Equal(expected.Model.Mesh.Blocks[0].Positions, piece.Model.Mesh.Blocks[0].Positions);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("sm_armour_mp_basic_helmet", true)]
    [InlineData("csm_armour_mp_havoc_01_helmet", true)]
    [InlineData("", false)]
    [InlineData("..\\evil", false)]
    [InlineData("a/b", false)]
    [InlineData("c:evil", false)]
    [InlineData("a..b", false)]
    public void Package_paths_stay_inside_the_packages_folder(string package, bool ok)
    {
        string? path = CharacterModel.PackagePath("game", package);
        if (!ok) { Assert.Null(path); return; }
        Assert.Equal(Path.Combine("game", "data", "packages", package + ".oppc"), path);
    }

    // ---- the lens table and rule -------------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_lens_rectangle_lies_inside_the_texture_and_has_an_area()
    {
        foreach (var package in LensRegions.Packages)
        {
            var rects = LensRegions.For(package);
            Assert.InRange(rects.Count, 1, LensRegions.MaxRects);
            foreach (var r in rects)
            {
                Assert.InRange(r.X0, 0, LensRegions.TextureSize - 1);
                Assert.InRange(r.Y0, 0, LensRegions.TextureSize - 1);
                Assert.InRange(r.X1, r.X0 + 1, LensRegions.TextureSize);
                Assert.InRange(r.Y1, r.Y0 + 1, LensRegions.TextureSize);
                Assert.True(r.Width < 100 && r.Height < 100, $"{package}: a lens is small, not {r}");
                var (u0, v0, u1, v1) = LensRegions.Uv(r);
                Assert.True(u0 < u1 && v0 < v1 && u1 <= 1f && v1 <= 1f);
            }
        }
    }

    [Theory]
    [InlineData("sm_armour_mp_basic_helmet", 2)]
    [InlineData("SM_ARMOUR_MP_MK4_HELMET", 2)]
    [InlineData("csm_armour_mp_basic_helmet", 3)]
    [InlineData("csm_armour_mp_havoc_01_helmet", 3)]
    [InlineData("csm_armour_mp_berserk_01_helmet", 0)]   // a black visor slit: no lens texels
    [InlineData("csm_armour_mp_berserk_02_helmet", 0)]
    [InlineData("sm_armour_mp_basic_torso", 0)]
    [InlineData("nothing", 0)]
    public void Lens_rectangles_per_helmet(string package, int count) => Assert.Equal(count, LensRegions.For(package).Count);

    [Theory]
    [InlineData("sm_armour_mp_mk6_helmet", 8f)]   // the Mk6's lens texels are very dark: its recoloured lens is scaled up so the chosen colour reads
    [InlineData("sm_armour_mp_basic_helmet", 1f)]
    [InlineData("csm_armour_mp_havoc_02_helmet", 1f)]
    [InlineData("nothing", 1f)]
    public void The_recoloured_lens_is_scaled_only_where_the_texels_are_dark(string package, float gain) => Assert.Equal(gain, LensRegions.Gain(package));

    [Fact]
    public void Every_lens_helmet_has_a_usable_gain()
    {
        foreach (var package in LensRegions.Packages) Assert.InRange(LensRegions.Gain(package), 1f, 16f);
    }

    [Fact]
    public void The_slots_are_padded_to_the_shaders_four_rectangles()
    {
        var slots = LensRegions.Slots("sm_armour_mp_basic_helmet");
        Assert.Equal(LensRegions.MaxRects, slots.Length);
        Assert.Equal(LensRegions.Uv(LensRegions.For("sm_armour_mp_basic_helmet")[0]), slots[0]);
        Assert.Equal((0f, 0f, 0f, 0f), slots[2]);
        Assert.All(LensRegions.Slots("nothing"), s => Assert.Equal((0f, 0f, 0f, 0f), s));
    }

    [Fact]
    public void The_lens_rule_picks_saturated_unmasked_texels()
    {
        Assert.True(LensRegions.IsLensTexel(200, 20, 20, 0, 0, 0));      // a red lens
        Assert.True(LensRegions.IsLensTexel(20, 160, 30, 2, 3, 4));      // a green lens, mask noise
        Assert.True(LensRegions.IsLensTexel(68, 6, 6, 0, 0, 0));         // dark red (the Mk6 lens)
        Assert.False(LensRegions.IsLensTexel(200, 20, 20, 100, 100, 0)); // painted armour (mask about 100 / 100 / 0)
        Assert.False(LensRegions.IsLensTexel(90, 90, 100, 0, 0, 0));     // grey metal: not saturated
        Assert.False(LensRegions.IsLensTexel(25, 2, 2, 0, 0, 0));        // too dark
        Assert.False(LensRegions.IsLensTexel(200, 130, 120, 0, 0, 0));   // a warm gold: saturation 0.4 or less
    }

    // ---- material keys -----------------------------------------------------------------------------------------------------------------

    static ResolvedColour Colour(string name, float r, float g, float b) => new(name, [r, g, b], [r * 0.5f, g * 0.5f, b * 0.5f]);
    static ResolvedPaint Paint(float r = 0.2f, bool pattern = true) => new(Colour("a", r, 0.3f, 0.4f), Colour("b", 0.5f, 0.6f, 0.7f), Colour("c", 0.1f, 0.1f, 0.1f), pattern);

    [Fact]
    public void Equal_looks_share_a_key_and_every_difference_changes_it()
    {
        string baseKey = PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:x", null);
        Assert.Equal(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:x", null));   // separate but equal objects
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#1:mat", Paint(), "b:x", null));            // the surface
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint(0.25f), "b:x", null));       // a colour
        Assert.Equal(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint(pattern: false), "b:x", null));    // the flag itself is not in the key: the colours are all the shader reads
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:y", null));            // the decal
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:x", (1, 2, 3)));       // the lens
        Assert.NotEqual(PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:x", (1, 2, 3)), PaintedMaterialKey.Of("pkg#0:mat", Paint(), "b:x", (1, 2, 4)));
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#0:mat", null, "b:x", null));               // no paint at all
        // A colour the look leaves undefined differs from a defined one.
        Assert.NotEqual(baseKey, PaintedMaterialKey.Of("pkg#0:mat", Paint() with { Tertiary = null }, "b:x", null));
    }

    [Fact]
    public void Only_the_specular_of_a_colour_can_differ_and_still_changes_the_key()
    {
        var a = Paint();
        var b = a with { Primary = a.Primary! with { Specular = [0.9f, 0.9f, 0.9f] } };
        Assert.NotEqual(PaintedMaterialKey.Of("s", a, "", null), PaintedMaterialKey.Of("s", b, "", null));
    }

    [Fact]
    public void Decal_keys_name_the_texture_or_the_emblem_that_is_drawn()
    {
        var badge = new PaintDecal("sm_badge", "badge_tex", "badge_pkg", "space_marine");
        var blank = new PaintDecal("blank", "blank", "blank", null);
        var hash = EmblemHash.Of(new byte[EmblemHash.BlobBytes]);
        Assert.Equal("", PaintedMaterialKey.DecalKeyOf(ResolvedDecal.None, false));
        Assert.Equal("b:badge_tex", PaintedMaterialKey.DecalKeyOf(new ResolvedDecal(DecalKind.Badge, badge, EmblemHash.None), false));
        Assert.Equal("", PaintedMaterialKey.DecalKeyOf(new ResolvedDecal(DecalKind.Badge, blank, EmblemHash.None), false));
        // An emblem that has arrived is its hash; before that (or without bytes) the pad shows the fallback badge, or nothing when there is none.
        Assert.Equal("e:" + hash.ToHex(), PaintedMaterialKey.DecalKeyOf(new ResolvedDecal(DecalKind.Emblem, badge, hash), true));
        Assert.Equal("b:badge_tex", PaintedMaterialKey.DecalKeyOf(new ResolvedDecal(DecalKind.Emblem, badge, hash), false));
        Assert.Equal("", PaintedMaterialKey.DecalKeyOf(new ResolvedDecal(DecalKind.Emblem, null, hash), false));
    }
}
