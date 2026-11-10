using System.Numerics;
using SmContent.Extras.Musou;
using SmSim;
using SmSim.Extras.Musou.Ashfall;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>Polish pass P2: the Ashfall layout cache's file format (a synthetic level: the real one takes 23 s to generate and is not what these check).</summary>
public class AshfallLayoutCacheTests
{
    static (AshfallLevel, AshfallDressing) Synthetic()
    {
        var parts = new[]
        {
            new AshfallPart(AshfallKind.Ground, [new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 0, 0), new Vector3(10, 10, 0), new Vector3(0, 10, 0)]),
            new AshfallPart(AshfallKind.Wall, [new Vector3(1, 1, 0), new Vector3(2, 1, 0), new Vector3(2, 1, 3)]),
            new AshfallPart(AshfallKind.Objective, []),
        };
        var points = new[] { new ControlPoint("A", new Vector3(5, 5, 0), 4f, 2f), new ControlPoint("B", new Vector3(-5, 5, 0), 4f, 2f) };
        var areas = new[] { new StartArea(Team.SpaceMarines, new Vector3(0, -20, 0), new Vector3(10, 10, 4), 0.5f), new StartArea(Team.Chaos, new Vector3(0, 20, 0), new Vector3(10, 10, 4), 0f, AreaShape.Cylinder) };
        var spawns = new[] { new Vector3(1, 2, 3), new Vector3(-1, -2, 0.5f) };
        var level = new AshfallLevel(parts, points, areas, spawns, 0xDEADBEEF12345678UL) { Layout = AshfallLayout.City };
        var items = new[] { new AshfallDressItem("wall_a", new Vector3(1, 1, 0), 1.5707f, new Vector3(4, 1, 3), true, AshfallDressRole.Prop, 1), new AshfallDressItem("tile", Vector3.Zero, 0f, new Vector3(10, 10, 0.1f), false, AshfallDressRole.Surface, -1) };
        var dressing = new AshfallDressing(items, [1], AshfallMap.DressVersion, [new AshfallStructure([1], new Vector3(1, 1, 0), new Vector3(2, 1, 3))]) { ItemParts = [(1, 1), (0, 0)], Flags = ["gap: test", "mass: test"] };
        return (level, dressing);
    }

    [Fact]
    public void A_layout_round_trips_through_the_cache_file()
    {
        var (level, dressing) = Synthetic();
        using var stream = new MemoryStream();
        AshfallLayoutCache.Write(stream, AshfallLayout.City, level, dressing);
        stream.Position = 0;
        var read = AshfallLayoutCache.Read(stream, AshfallLayout.City);
        Assert.NotNull(read);
        var (l, d) = read.Value;
        Assert.Equal(level.Hash, l.Hash);
        Assert.Equal(AshfallLayout.City, l.Layout);
        Assert.Equal(level.Parts.Count, l.Parts.Count);
        for (int i = 0; i < level.Parts.Count; i++) { Assert.Equal(level.Parts[i].Kind, l.Parts[i].Kind); Assert.Equal(level.Parts[i].Corners, l.Parts[i].Corners); }
        Assert.Equal(level.Points, l.Points);
        Assert.Equal(level.Areas, l.Areas);
        Assert.Equal(level.Spawns, l.Spawns);
        Assert.Equal(dressing.Version, d.Version);
        Assert.Equal(dressing.Items, d.Items);
        Assert.Equal(dressing.HiddenParts, d.HiddenParts);
        Assert.NotNull(d.Structures);
        Assert.Equal(dressing.Structures![0].Parts, d.Structures![0].Parts);
        Assert.Equal(dressing.Structures[0].Min, d.Structures[0].Min);
        Assert.Equal(dressing.ItemParts, d.ItemParts);
        Assert.Equal(dressing.Flags, d.Flags);
        Assert.Equal(stream.Length, stream.Position);   // the end mark was the last thing read
    }

    [Fact]
    public void Another_layout_or_a_cut_file_is_refused()
    {
        var (level, dressing) = Synthetic();
        using var stream = new MemoryStream();
        AshfallLayoutCache.Write(stream, AshfallLayout.City, level, dressing);
        stream.Position = 0;
        Assert.Null(AshfallLayoutCache.Read(stream, AshfallLayout.Field));   // the file names City: a Field join must generate
        var cut = new MemoryStream(stream.ToArray(), 0, (int)stream.Length - 7);
        Assert.ThrowsAny<Exception>(() => AshfallLayoutCache.Read(cut, AshfallLayout.City));   // a cut file throws (the caller generates and rewrites)
    }

    [Fact]
    public void The_key_names_the_generator_versions_and_the_build()
    {
        string key = AshfallLayoutCache.Key;
        Assert.StartsWith($"v{AshfallMap.Version}-g{AshfallMap.GrayboxVersion}-d{AshfallMap.DressVersion}-", key);
        Assert.Equal(16, key.Split('-')[^1].Length);   // 8 bytes of the module version id
        Assert.EndsWith($"city-{key}.bin", AshfallLayoutCache.PathOf(AshfallLayout.City));
    }
}
