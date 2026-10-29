using System.Numerics;
using SmFormats.Bod;
using SmFormats.Fx;
using static SmFormats.Bod.SyntheticBodWriter;

namespace SmFormats.Tests;

/// <summary>The effects model (M25a): PSystem trees, curves, colours, materials, and the atlas texture page. Synthetic fixtures only.</summary>
public class FxTests
{
    [Fact]
    public void A_particle_system_reads_as_typed_emitters_and_descriptions()
    {
        var bytes = SyntheticFxWriter.TracerSystem();
        var fx = FxSystem.Read("test_tracer", bytes);
        Assert.Equal("test_tracer", fx.Name);   // the entry's name when the system's own is empty
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), fx.BackgroundColor);
        Assert.Equal(-1, fx.GlobalSortOverride);
        var emitter = Assert.Single(fx.Emitters);
        Assert.Equal(("main", "fx_test", 40f), (emitter.Name, emitter.RefNode, emitter.LodDistanceFar));
        Assert.Equal([FxElementKind.Spray, FxElementKind.Combo, FxElementKind.Decal, FxElementKind.Audio, FxElementKind.Mesh, FxElementKind.Streamer, FxElementKind.Unknown],
            emitter.Elements.Select(e => e.Kind));

        // The spray: one burst of one particle; its particle shows two beams.
        var spray = Assert.IsType<FxSpray>(emitter.Elements[0]);
        Assert.Equal([new FxBurst(1, 0f)], spray.Bursts);
        Assert.Equal((0.5f, 2, 1, 1.5f), (spray.InheritVelocity, spray.EmitterShape, spray.Direction, spray.Deviation.Constant!.Value));
        Assert.Equal(1f, spray.InitialSpeed.Evaluate(0.5f), 5);
        var streak = Assert.IsType<FxBeam>(spray.Children[0]);
        Assert.Equal(("streak", 700f, 30f, 1, true), (streak.Name, streak.USpeed, streak.MeterPerU, streak.NumSegments, streak.Billboard));
        Assert.Equal((0.4f, 0.05f, false, 3), (streak.Duration, streak.DurationEpsilon, streak.IsLooping, streak.Lod));
        Assert.Equal(1f, streak.Length.Constant);
        Assert.Equal(("test_streak_00", SyntheticBodWriter.Hash("test_streak_00"), 1, 1, 1f, 3f, true),
            (streak.Material.Name, streak.Material.Hash, streak.Material.AdditiveBlend, streak.Material.SoftParticle, streak.Material.TileX, streak.Material.TileY, streak.Material.RandomTile));
        Assert.Equal(new FxColour(250, 240, 230, 255), streak.Colour.Keys[0].Colour);
        var mid = streak.Colour.Evaluate(0.5f);
        Assert.Equal(new Vector4(175 / 255f, 165 / 255f, 155 / 255f, 127.5f / 255f), mid);
        Assert.Equal(0.15f, streak.Width.Evaluate(0.5f), 5);
        Assert.Equal(12, Assert.IsType<FxBeam>(spray.Children[1]).NumSegments);

        // The combo plays one child at random: a ring and a light.
        var combo = Assert.IsType<FxCombo>(emitter.Elements[1]);
        Assert.True(combo.OneAtRandom);
        Assert.Equal(2f, combo.Scale.Constant);
        var ring = Assert.IsType<FxRing>(combo.Children[0]);
        Assert.Equal((true, 0.4f, new Vector3(0, 0, 90), (false, false, true)), (ring.Billboard, ring.EpsilonRadius, ring.StartAngle, ring.UseStartAngle));
        Assert.Equal((0.1f, 0.3f, 0.25f, 0.2f), (ring.Radius.Evaluate(-1f), ring.Radius.Evaluate(0.5f), ring.Radius.Evaluate(0.75f), ring.Radius.Evaluate(2f)));
        var light = Assert.IsType<FxLight>(combo.Children[1]);
        Assert.Equal((2f, 1.5f, 5f, false), (light.Intensity.Constant!.Value, light.AttenStart.Constant!.Value, light.AttenEnd.Constant!.Value, light.IsSpotLight));
        Assert.Equal(new FxColour(255, 200, 130, 255), light.Colour.Keys[0].Colour);

        var decal = Assert.IsType<FxDecal>(emitter.Elements[2]);
        Assert.Equal(("test_decal_dif", "test_decal_nrm", "", 0.6f, 0.6f, 2f, 60f), (decal.DiffuseMap, decal.NormalMap, decal.SpecularMap, decal.Width, decal.Height, decal.Depth, decal.NormalThreshold));
        Assert.Equal(0f, decal.Colour.Evaluate(1f).W);
        Assert.Equal("play_test_event", Assert.IsType<FxAudio>(emitter.Elements[3]).EventName);
        var mesh = Assert.IsType<FxMesh>(emitter.Elements[4]);
        Assert.Equal(("test_casing", "test_fx", 1f), (mesh.Mesh, mesh.PackageName, mesh.Scale.Constant!.Value));
        var trail = Assert.IsType<FxStreamer>(emitter.Elements[5]);
        Assert.Equal((0.5f, true, 3f, -1f), (trail.Length.Constant!.Value, trail.LengthAsSeconds, trail.Speed, trail.Gravity));

        // An unknown class is kept with its name and fields, and noted.
        var unknown = Assert.IsType<FxUnknownElement>(emitter.Elements[6]);
        Assert.Equal(("FXFutureDescription", "mystery", 7f), (unknown.Class, unknown.Name, unknown.Float("Strange")!.Value));
        Assert.Contains("Strange", unknown.FieldNames);
        Assert.Contains(fx.Notes, n => n.Contains("FXFutureDescription"));

        // Whole-system views: every element depth first, distinct materials, the lifetime approximation.
        Assert.Equal(11, fx.Elements.Count());
        Assert.Equal(["test_streak_00", "test_smoke"], fx.Materials.Select(m => m.Name));
        Assert.Equal(4f + 0.05f, fx.Lifetime!.Value, 4);   // the decal, the longest top-level element
    }

    [Fact]
    public void Curves_and_colours_tolerate_odd_values_and_missing_fields()
    {
        Assert.Equal(0f, FxCurve.Empty.Evaluate(0.5f));
        Assert.Equal(Vector4.One, FxColourCurve.Empty.Evaluate(0.5f));
        var step = new FxCurve([new(0.5f, 1f), new(0.5f, 3f), new(1f, 5f)]);
        // Before and at the first key: the first value; between keys: linear; after the last: the last; NaN: the first.
        Assert.Equal((1f, 1f, 4f, 5f, 1f), (step.Evaluate(0.25f), step.Evaluate(0.5f), step.Evaluate(0.75f), step.Evaluate(2f), step.Evaluate(float.NaN)));
        Assert.Null(step.Constant);
        Assert.Equal((1f, 5f), (step.Min, step.Max));
        // Ints where floats are expected, keys that are not [t, v], colour components out of range.
        var oddRing = new Struct("FXRingDescription", ("Name", new S("odd")), ("Duration", new I(2)),
            ("FrameRadius", new Values(new Arr(new I(0), new I(4)), new Arr(new F(1f)), new F(3f))),
            ("FrameColour", new Values(new Arr(new F(0f), new Arr(new U(300), new F(-4f), new I(7), new U(255))))));
        var emitterNode = new Struct("FXEmitter", ("FXDescriptions", new List(oddRing)));
        var doc = BodDocument.Read(Document(new Struct("PSystem", ("Emitters", new List(emitterNode)))));
        var ring = Assert.IsType<FxRing>(Assert.Single(FxSystem.Read("odd", doc).Elements));
        Assert.Equal(2f, ring.Duration);
        Assert.Equal([new FxKey(0f, 4f)], ring.Radius.Keys);
        Assert.Equal(new FxColour(255, 0, 7, 255), ring.Colour.Keys[0].Colour);
        Assert.Equal((0f, false, true), (ring.SpinZ.Evaluate(0f), ring.IsLooping, ring.Material.IsEmpty));
        Assert.Equal(2f, FxSystem.Read("odd", doc).Lifetime);
    }

    [Fact]
    public void A_document_that_is_not_a_particle_system_is_refused_and_deep_trees_are_cut()
    {
        Assert.Throws<InvalidDataException>(() => FxSystem.Read("x", Document(new Struct("Material", ("Name", new S("m"))))));
        Node nested = new Struct("FXRingDescription", ("Name", new S("leaf")));
        for (int i = 0; i < FxSystem.MaxDepth + 2; i++) nested = new Struct("FXComboDescription", ("Name", new S($"c{i}")), ("FXDescriptions", new List(nested)));
        var fx = FxSystem.Read("deep", Document(new Struct("PSystem", ("Emitters", new List(new Struct("FXEmitter", ("FXDescriptions", new List(nested))))))));
        Assert.Equal(FxSystem.MaxDepth, fx.Elements.Count());
        Assert.Contains(fx.Notes, n => n.Contains("deeper"));
    }

    [Fact]
    public void The_texture_page_maps_material_hashes_to_atlas_rects()
    {
        ulong streak = SyntheticBodWriter.Hash("test_streak_00"), smoke = SyntheticBodWriter.Hash("test_smoke");
        var bytes = SyntheticFxWriter.TexturePage((streak, 0.5f, 0.25f, 0.0625f, 0.125f, 1), (smoke, 0f, 0f, 0.25f, 0.25f, 0), (99, 0.75f, 0.75f, 0.25f, 0.25f, 0));
        Assert.Equal(8 + 3 * 32, bytes.Length);
        var page = FxTexturePage.Read(bytes);
        Assert.Equal(("master_fx", 0u, 3), (page.AtlasTexture, page.Reserved, page.Rects.Count));
        var rect = page.Find(streak)!;
        Assert.Equal((0.5f, 0.25f, 0.0625f, 0.125f, true), (rect.X, rect.Y, rect.Width, rect.Height, rect.Rotated));
        Assert.Equal((new Vector2(0.5f, 0.25f), new Vector2(0.5625f, 0.375f)), rect.Uv);
        Assert.False(page.Find(smoke)!.Rotated);
        Assert.Null(page.Find(12345));
        // Through a system's materials and its string table.
        var doc = BodDocument.Read(SyntheticFxWriter.TracerSystem());
        var fx = FxSystem.Read("t", doc);
        Assert.All(fx.Materials, m => Assert.NotNull(page.Find(m)));
        Assert.Null(page.Find(fx.Materials[0] with { Name = "", Hash = 0 }));
        var named = page.WithNames(doc.Strings);
        Assert.Equal(["test_streak_00", "test_smoke", ""], named.Rects.Select(r => r.Name));
        // Refusals: a count that does not match the length, a non-finite rect, a short file.
        Assert.Throws<InvalidDataException>(() => FxTexturePage.Read(bytes.AsSpan(0, bytes.Length - 1)));
        Assert.Throws<InvalidDataException>(() => FxTexturePage.Read([.. bytes, 0]));
        Assert.Throws<InvalidDataException>(() => FxTexturePage.Read(new byte[4]));
        var nan = (byte[])bytes.Clone();
        BitConverter.TryWriteBytes(nan.AsSpan(8 + 8), float.NaN);
        Assert.Throws<InvalidDataException>(() => FxTexturePage.Read(nan));
        Assert.Empty(FxTexturePage.Read(new byte[8]).Rects);
    }
}
