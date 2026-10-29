using System.Numerics;
using SmContent;
using SmFormats.Bod;
using SmFormats.Havok;
using SmFormats.Obpk;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>Map loading from a synthetic install: region archive, collision layers, spawn markers, hash, and refusals.</summary>
public class MapContentTests
{
    static readonly CollisionMesh Floor = new([new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)], [(0, 1, 2), (0, 2, 3)]);

    static byte[] Collision(params string[] layers) =>
        BuildWorldCollision(layers.Select(l => new CollisionVariant(l, [Floor], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)])).ToArray());

    static byte[] Spawns(params (string Name, float[] Position, float[] Rotation)[] markers) => SyntheticBodWriter.Layer(markers);

    static string Install(string map, string region, byte[] collision, byte[]? layer)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-map-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        var files = new List<SyntheticObpkWriter.File> { new("hkt", region, collision) };
        if (layer is not null) files.Add(new("Layer", "d_tdm", layer, $"\\mp\\{map}\\{region}\\"));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", $"{map}__{region}.oppc"), SyntheticObpkWriter.Build(files));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", $"{map}__{map}.oppc"), SyntheticObpkWriter.Build([new("world", map, [1, 2, 3])]));
        return dir;
    }

    [Fact]
    public void Loads_layers_and_enabled_spawn_markers_in_name_order_with_yaw_in_radians()
    {
        string dir = Install("mp_test", "r000_main", Collision("2", "7", "8"), Spawns(
            ("tdm_mkr_spawn_02", [5f, 6f, 1f], [0f, 0f, -90f]),
            ("tdm_mkr_spawn_01", [1f, 2f, 3f], [0f, 0f, 180f]),
            ("tdm_mkr_flag", [9f, 9f, 9f], [0f, 0f, 0f])));
        try
        {
            Assert.Equal(["mp_test"], MapContent.Available(dir));
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal("r000_main", map.Region);
            Assert.Equal(["2", "7", "8"], map.Layers.Select(l => l.Name));
            Assert.Equal(["tdm_mkr_spawn_01", "tdm_mkr_spawn_02"], map.Spawns.Select(s => s.Name));
            Assert.Equal(new Vector3(1, 2, 3), map.Spawns[0].Position);
            Assert.Equal(MathF.PI, map.Spawns[0].Yaw, 5);
            Assert.Equal(-MathF.PI / 2, map.Spawns[1].Yaw, 5);
            var world = map.ToCollisionWorld();
            Assert.Equal((4, 2), (world.Character.TriangleCount, world.Weapons.TriangleCount));   // characters: layers 2 and 8 (M18)
            Assert.Equal((4, 4, false), (world.Weapons.MaterialOf(0), world.Weapons.MaterialOf(1), world.Character.HasMaterials));   // M25a: shots name surfaces
            Assert.Equal(2, map.SpawnPoints().Count);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void The_hash_follows_the_source_bytes()
    {
        string a = Install("mp_test", "wrld", Collision("2", "8"), Spawns(("tdm_mkr_spawn_01", [1f, 2f, 3f], [0f, 0f, 0f])));
        string b = Install("mp_test", "wrld", Collision("2", "8"), Spawns(("tdm_mkr_spawn_01", [1f, 2f, 3f], [0f, 0f, 0f])));
        string c = Install("mp_test", "wrld", Collision("2", "8"), Spawns(("tdm_mkr_spawn_01", [1f, 2f, 4f], [0f, 0f, 0f])));
        try
        {
            ulong ha = MapContent.Load(a, "mp_test").Hash;
            Assert.Equal(ha, MapContent.Load(b, "mp_test").Hash);
            Assert.NotEqual(ha, MapContent.Load(c, "mp_test").Hash);   // a moved spawn changes it
            Assert.NotEqual(0UL, ha);
        }
        finally { foreach (var d in new[] { a, b, c }) Directory.Delete(d, recursive: true); }
    }

    [Fact]
    public void Missing_layers_unknown_maps_and_bad_names_are_refused()
    {
        string dir = Install("mp_test", "wrld", Collision("2", "7"), null);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => MapContent.Load(dir, "mp_test"));
            Assert.Contains("'8'", ex.Message);
            Assert.Throws<FileNotFoundException>(() => MapContent.Load(dir, "mp_other"));
            Assert.Throws<ArgumentException>(() => MapContent.Load(dir, "../mp_test"));
            Assert.Throws<ArgumentException>(() => MapContent.Load(dir, ""));
            Assert.Empty(MapContent.Available(Path.Combine(dir, "nowhere")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
