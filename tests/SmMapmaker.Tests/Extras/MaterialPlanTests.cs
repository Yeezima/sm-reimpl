using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SmClient;
using SmMapmaker;
using Xunit;
using static SmMapmaker.ModelImport;

namespace SmMapmaker.Tests;

/// <summary>
/// The engine-free half of the material builder (<see cref="MaterialPlan"/>, client/Extras/Mapmaker; docs/mapmaker.md "Materials (editor)"): the channel each input reads, the DirectX
/// green flip, and wiring standalone pictures into a material by their file names. Synthetic headers only.
/// </summary>
public sealed class MaterialPlanTests
{
    static string Png(Lab lab, string name, int seed)
    {
        var d = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(d, 0);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(16), 64);
        BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(20), 64);
        d[40] = (byte)seed;
        var path = Path.Combine(lab.Dir, name);
        File.WriteAllBytes(path, d);
        return path;
    }

    static OpResult Run(MapSession s, JsonObject op)
    {
        var r = s.Apply(new JsonArray(op.DeepClone()).ToJsonString());
        Assert.True(r.Ok, string.Join("; ", r.Errors.Select(e => e.Code + ": " + e.Message)));
        return r;
    }

    /// <summary>A map with one material and these pictures added; returns the session, the material id and (id, file name) per picture.</summary>
    static (MapSession S, string Material, List<(string Id, string File)> Images) Setup(Lab lab, params string[] files)
    {
        var s = lab.Session(withSm1: false);
        var mid = Run(s, new JsonObject { ["op"] = "material-add", ["name"] = "Wall" }).CreatedMaterials[0];
        var imgs = new List<(string, string)>();
        foreach (var (f, i) in files.Select((f, i) => (f, i)))
            imgs.Add((Run(s, new JsonObject { ["op"] = "image-add", ["path"] = Png(lab, f, i + 1) }).CreatedImages[0], f));
        return (s, mid, imgs);
    }

    [Fact]
    public void Channels_map_to_Godot_texture_channels()
    {
        Assert.Equal(0, MaterialPlan.GodotChannel(TextureChannel.R));
        Assert.Equal(1, MaterialPlan.GodotChannel(TextureChannel.G));
        Assert.Equal(2, MaterialPlan.GodotChannel(TextureChannel.B));
        Assert.Equal(3, MaterialPlan.GodotChannel(TextureChannel.A));
        Assert.Equal(0, MaterialPlan.GodotChannel(TextureChannel.Rgb));   // a colour read as one value: its red channel, as Godot does
    }

    [Fact]
    public void Green_flip_inverts_only_the_green_byte()
    {
        byte[] px = [10, 20, 30, 40, 200, 0, 255, 255];
        MaterialPlan.FlipGreenRgba8(px);
        Assert.Equal(new byte[] { 10, 235, 30, 40, 200, 255, 255, 255 }, px);
        MaterialPlan.FlipGreenRgba8(px);
        Assert.Equal(new byte[] { 10, 20, 30, 40, 200, 0, 255, 255 }, px);   // twice is the original
    }

    [Fact]
    public void An_orm_picture_feeds_ao_roughness_and_metallic_from_red_green_blue()
    {
        Assert.Equal([(MaterialPlan.Feature.Ao, TextureChannel.R), (MaterialPlan.Feature.Roughness, TextureChannel.G), (MaterialPlan.Feature.Metallic, TextureChannel.B)], MaterialPlan.Uses(TextureRole.Orm));
        Assert.Equal([(MaterialPlan.Feature.Albedo, TextureChannel.Rgb)], MaterialPlan.Uses(TextureRole.Albedo));
        Assert.Empty(MaterialPlan.Uses(TextureRole.Displacement));
    }

    [Theory]
    [InlineData("brick_Normal_DX.png", true)]
    [InlineData("brick_normal_directx.png", true)]
    [InlineData("brick_Normal_GL.png", false)]
    [InlineData("brick_Normal.png", false)]
    [InlineData("rock_4K_Normal.jpg", true)]   // unmarked Megascans-style: read as DirectX (owner's note 2026-11-17; ours, a refine item), as the model import does
    public void Normal_convention_from_the_name(string file, bool directX) => Assert.Equal(directX, MaterialPlan.IsDirectXNormal(ClassifyTexture(file)!));

    [Fact]
    public void Five_pictures_wired_by_name_resolve_to_five_channels_and_only_the_normal_is_flipped()
    {
        using var lab = new Lab();
        var (s, mid, imgs) = Setup(lab, "brick_BaseColor.png", "brick_Normal_DX.png", "brick_Roughness.png", "brick_Metallic.png", "brick_AO.png");
        var g = s.Document.FindMaterial(mid)!;
        var op = MaterialPlan.WireByName(mid, "surface", g.Nodes.Select(n => n.Id).ToList(), imgs)!;
        Run(s, op);
        var m = s.Document.FindMaterial(mid)!.Resolve();
        Assert.Equal(new TextureSlot(imgs[0].Id, TextureChannel.Rgb), m.BaseColorTexture);
        Assert.Equal(new TextureSlot(imgs[1].Id, TextureChannel.Rgb), m.NormalTexture);
        Assert.Equal(new TextureSlot(imgs[2].Id, TextureChannel.R), m.RoughnessTexture);
        Assert.Equal(new TextureSlot(imgs[3].Id, TextureChannel.R), m.MetallicTexture);
        Assert.Equal(new TextureSlot(imgs[4].Id, TextureChannel.R), m.AoTexture);
        Assert.True(m.NormalDirectX);
        var plan = MaterialPlan.Of(m);
        Assert.Equal(5, plan.Textures.Count);
        Assert.Equal([MaterialPlan.Feature.Normal], plan.Textures.Where(t => t.FlipGreen).Select(t => t.Feature));
        Assert.Equal(1f, plan.Metallic); Assert.Equal(1f, plan.Roughness);   // scalars multiply their texture: 1 when linked
        Assert.False(plan.ConstantAo);
    }

    [Fact]
    public void An_orm_picture_is_split_into_three_channels_and_an_unknown_name_stays_unlinked()
    {
        using var lab = new Lab();
        var (s, mid, imgs) = Setup(lab, "T_Rock_ORM.png", "holiday-photo.png");
        var g = s.Document.FindMaterial(mid)!;
        Run(s, MaterialPlan.WireByName(mid, "surface", g.Nodes.Select(n => n.Id).ToList(), imgs)!);
        var g2 = s.Document.FindMaterial(mid)!;
        var m = g2.Resolve();
        Assert.Equal(TextureChannel.R, m.AoTexture!.Channel);
        Assert.Equal(TextureChannel.G, m.RoughnessTexture!.Channel);
        Assert.Equal(TextureChannel.B, m.MetallicTexture!.Channel);
        Assert.Null(m.BaseColorTexture);
        Assert.Single(g2.Nodes, n => n.Type == NodeTypes.SeparateRgb);
        Assert.Contains(g2.Nodes, n => n.Type == NodeTypes.ImageTexture && n.Params["image"]?.ToString() == imgs[1].Id);   // added, not linked
        Assert.DoesNotContain(g2.Links, l => g2.Node(l.From)?.Params["image"]?.ToString() == imgs[1].Id);
    }

    [Fact]
    public void Pictures_added_later_share_the_materials_mapping_and_replace_an_input_already_linked()
    {
        using var lab = new Lab();
        var (s, mid, imgs) = Setup(lab, "a_BaseColor.png", "b_BaseColor.png", "a_Roughness.png");
        Run(s, MaterialPlan.WireByName(mid, "surface", ["surface", "output"], imgs.Take(1))!);
        // a mapping at 0.5 per metre into the first picture
        var g = s.Document.FindMaterial(mid)!;
        var tex = g.Nodes.First(n => n.Type == NodeTypes.ImageTexture).Id;
        Run(s, new JsonObject { ["op"] = "material-edit", ["id"] = mid, ["addNodes"] = new JsonArray(new JsonObject { ["id"] = "map", ["type"] = "mapping", ["params"] = new JsonObject { ["scale"] = new JsonArray(0.5, 0.5) } }),
            ["link"] = new JsonArray(new JsonObject { ["from"] = "map", ["out"] = "vector", ["to"] = tex, ["in"] = "vector" }) });
        g = s.Document.FindMaterial(mid)!;
        // without the mapping a new picture would be refused (one mapping per material: unsupported-graph); with it both resolve, and the new base colour replaces the old link
        var plain = MaterialPlan.WireByName(mid, "surface", g.Nodes.Select(n => n.Id).ToList(), imgs.Skip(2))!;
        var refused = s.Apply(new JsonArray(plain).ToJsonString());
        Assert.False(refused.Ok);
        Assert.Equal("unsupported-graph", refused.Errors[0].Code);
        var op = MaterialPlan.WireByName(mid, "surface", g.Nodes.Select(n => n.Id).ToList(), imgs.Skip(1), mappingId: "map")!;
        Run(s, op);
        var m = s.Document.FindMaterial(mid)!.Resolve();
        Assert.Equal(imgs[1].Id, m.BaseColorTexture!.ImageId);
        Assert.Equal(imgs[2].Id, m.RoughnessTexture!.ImageId);
        Assert.Equal(new System.Numerics.Vector2(0.5f, 0.5f), m.UvScale);
    }
    [Fact]
    public void A_constant_ao_below_one_is_planned_as_a_constant_texture_and_a_plain_material_has_none()
    {
        Assert.False(MaterialPlan.Of(PbrMaterial.Plain).ConstantAo);
        Assert.Empty(MaterialPlan.Of(PbrMaterial.Plain).Textures);
        Assert.True(MaterialPlan.Of(PbrMaterial.Plain with { Ao = 0.6f }).ConstantAo);
    }

    [Fact]
    public void Nothing_to_wire_gives_no_operation()
    {
        Assert.Null(MaterialPlan.WireByName("m1", "surface", ["surface"], []));
    }
}
