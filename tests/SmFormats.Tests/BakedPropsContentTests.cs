using System.Numerics;
using SmContent;
using SmFormats.Obpk;
using SmSim;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

/// <summary>
/// The movable cover baked into Havok layer "3" (<see cref="BakedProps"/>, classic-maps-fidelity gameplay 2026-11-18) from a
/// synthetic install: one prop per connected set of triangles, solid for characters and shots, none without the layer.
/// </summary>
public class BakedPropsContentTests
{
    static readonly CollisionMesh Floor = new([new(-10, -10, 0), new(10, -10, 0), new(10, 10, 0), new(-10, 10, 0)], [(0, 1, 2), (0, 2, 3)]);
    /// <summary>A 1.2 x 3 x 1.5 m crate on the floor: 8 vertices, 12 triangles.</summary>
    static readonly CollisionMesh Crate = new(
        [new(-0.6f, -1.5f, 0), new(0.6f, -1.5f, 0), new(0.6f, 1.5f, 0), new(-0.6f, 1.5f, 0), new(-0.6f, -1.5f, 1.5f), new(0.6f, -1.5f, 1.5f), new(0.6f, 1.5f, 1.5f), new(-0.6f, 1.5f, 1.5f)],
        [(0, 2, 1), (0, 3, 2), (4, 5, 6), (4, 6, 7), (0, 1, 5), (0, 5, 4), (1, 2, 6), (1, 6, 5), (2, 3, 7), (2, 7, 6), (3, 0, 4), (3, 4, 7)]);
    static readonly CollisionPlacement At0 = new(0, Vector3.Zero, Quaternion.Identity, Vector3.One);

    /// <summary>The same crate as a soup: every triangle has its own three vertices (nothing shared by index).</summary>
    static readonly CollisionMesh SoupCrate = new(
        Crate.Triangles.SelectMany(t => new[] { Crate.Vertices[t.A], Crate.Vertices[t.B], Crate.Vertices[t.C] }).ToArray(),
        Enumerable.Range(0, 12).Select(i => (3 * i, 3 * i + 1, 3 * i + 2)).ToArray());

    static string InstallCrates(CollisionMesh mesh, params float[] xs)
    {
        var variants = new List<CollisionVariant> { new("2", [Floor], [At0]), new("8", [Floor], [At0]),
            new("3", [mesh], xs.Select(x => new CollisionPlacement(0, new Vector3(x, 0, 0), Quaternion.Identity, Vector3.One)).ToArray()) };
        string dir = Path.Combine(Path.GetTempPath(), "sm-baked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build([new("hkt", "wrld", BuildWorldCollision([.. variants]))]));
        return dir;
    }

    [Fact]
    public void An_unwelded_soup_of_one_crate_is_one_prop_and_crates_that_share_corners_are_one_prop()
    {
        // 1.2 m wide crates: at x -3 and -1.8 the second one's -X face is the first one's +X face (corners coincide).
        string soup = InstallCrates(SoupCrate, 0f), apart = InstallCrates(SoupCrate, -5f, 5f), touching = InstallCrates(Crate, -3f, -1.8f);
        try
        {
            Assert.Equal(12, Assert.Single(MapContent.Load(soup, "mp_test").Props).Indices.Length / 3);
            Assert.Equal(2, MapContent.Load(apart, "mp_test").Props.Count);
            Assert.Equal(24, Assert.Single(MapContent.Load(touching, "mp_test").Props).Indices.Length / 3);
        }
        finally { foreach (var x in new[] { soup, apart, touching }) Directory.Delete(x, recursive: true); }
    }

    static string Install(bool withCrates, float secondCrateX = -5f)
    {
        var variants = new List<CollisionVariant> { new("2", [Floor], [At0]), new("8", [Floor], [At0]) };
        if (withCrates) variants.Add(new("3", [Crate], [new(0, new Vector3(4, 0, 0), Quaternion.Identity, Vector3.One), new(0, new Vector3(secondCrateX, 0, 0), Quaternion.Identity, Vector3.One)]));
        string dir = Path.Combine(Path.GetTempPath(), "sm-baked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "data", "chunks"));
        File.WriteAllBytes(Path.Combine(dir, "data", "chunks", "mp_test__wrld.oppc"), SyntheticObpkWriter.Build([new("hkt", "wrld", BuildWorldCollision([.. variants]))]));
        return dir;
    }

    [Fact]
    public void Every_connected_set_of_layer_3_triangles_is_a_prop_solid_for_characters_and_shots()
    {
        string dir = Install(withCrates: true);
        try
        {
            var map = MapContent.Load(dir, "mp_test");
            Assert.Equal(2, map.Props.Count);
            Assert.All(map.Props, p => { Assert.Equal(12, p.Indices.Length / 3); Assert.Equal(8, p.Vertices.Length); Assert.Equal("hkt layer 3", p.Definition); });
            Assert.Equal(["layer3_01", "layer3_02"], map.Props.Select(p => p.Name));
            Assert.True(map.Props[0].Vertices.Min(v => v.X) < map.Props[1].Vertices.Min(v => v.X));   // ordered by the lowest corner: Z, then X
            var world = map.ToCollisionWorld();
            Assert.Equal(2, world.Props.Count);
            foreach (var team in new[] { Team.SpaceMarines, Team.Chaos, Team.None })
                Assert.NotNull(world.CharacterFor(team).Raycast(new Vector3(0, 0, 0.7f), Vector3.UnitX, 10f));   // the crate at x 4 stops a walker
            Assert.NotNull(world.Weapons.Raycast(new Vector3(0, 0, 0.7f), Vector3.UnitX, 10f));               // and a shot
            Assert.Equal(24, world.Character.TriangleCount - 4);   // the floors are 2 + 2 triangles; 2 crates x 12
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_map_without_layer_3_has_no_baked_props_and_the_hash_covers_the_layer()
    {
        string none = Install(withCrates: false), a = Install(withCrates: true), b = Install(withCrates: true), c = Install(withCrates: true, secondCrateX: -5.5f);
        try
        {
            Assert.Empty(MapContent.Load(none, "mp_test").Props);
            ulong h = MapContent.Load(a, "mp_test").Hash;
            Assert.Equal(h, MapContent.Load(b, "mp_test").Hash);
            Assert.NotEqual(h, MapContent.Load(c, "mp_test").Hash);
            Assert.NotEqual(h, MapContent.Load(none, "mp_test").Hash);
        }
        finally { foreach (var x in new[] { none, a, b, c }) Directory.Delete(x, recursive: true); }
    }
}
