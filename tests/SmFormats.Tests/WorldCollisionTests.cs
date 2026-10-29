using System.Numerics;
using SmFormats.Havok;
using static SmFormats.Havok.SyntheticHavokWriter;

namespace SmFormats.Tests;

public class WorldCollisionTests
{
    static readonly CollisionMesh Quad = new([new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)], [(0, 1, 2), (0, 2, 3)]);
    static readonly CollisionMesh Tri = new([new(0, 0, 0), new(0, 0, 1), new(0, 1, 0)], [(0, 1, 2)]);

    static IReadOnlyList<HavokCollisionLayer> Read(params CollisionVariant[] variants) =>
        HavokWorldCollision.Read(new HavokPackfile(BuildWorldCollision(variants)));

    [Fact]
    public void Placements_instance_shared_meshes_with_scale_rotation_and_translation()
    {
        var quarterTurn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2);
        var layers = Read(
            new CollisionVariant("2", [Quad, Tri],
            [
                new(0, new Vector3(10, 0, 0), Quaternion.Identity, Vector3.One),
                new(1, Vector3.Zero, Quaternion.Identity, Vector3.One),
                new(0, new Vector3(0, 0, 5), quarterTurn, new Vector3(2, 3, 1)),   // the quad again, reused
            ]),
            new CollisionVariant("8", [Tri], [new(0, new Vector3(0, 0, -1), Quaternion.Identity, Vector3.One)], InListShape: false));

        Assert.Equal(["2", "8"], layers.Select(l => l.Name));
        var world = layers[0];
        Assert.Equal(2 + 1 + 2, world.TriangleCount);
        Assert.Equal(4 + 3 + 4, world.Vertices.Length);
        Assert.Equal(new Vector3(11, 1, 0), world.Vertices[2]);
        // Scale first, then rotate a quarter turn about Z, then translate: local (1, 1, 0) -> (2, 3, 0) -> (-3, 2, 0) -> (-3, 2, 5).
        var placed = world.Vertices[7 + 2];
        Assert.True(Vector3.Distance(new Vector3(-3, 2, 5), placed) < 1e-5f, placed.ToString());
        // Indices point into the right placement's vertices.
        Assert.Equal([7, 8, 9, 7, 9, 10], world.Indices[9..]);
        Assert.Equal(-1f, layers[1].Vertices[0].Z);
    }

    [Fact]
    public void Inconsistent_storage_is_rejected()
    {
        IReadOnlyList<HavokCollisionLayer> With(float? storage) =>
            Read(new CollisionVariant("2", [Quad, Tri], [new(1, Vector3.Zero, Quaternion.Identity, Vector3.One, storage)]));
        Assert.Single(With(null));
        Assert.Throws<InvalidDataException>(() => With(0f));     // the quad's storage, while claiming the triangle's counts
        Assert.Throws<InvalidDataException>(() => With(7f));     // no such storage
        Assert.Throws<InvalidDataException>(() => With(0.5f));   // not an index
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x40000000)]   // 4 × this overflows to 0 in 32-bit arithmetic
    [InlineData(0x40000001)]   // ... and this to 4, the stored index count of one triangle
    [InlineData(int.MaxValue)]
    public void Corrupt_triangle_counts_are_rejected(int triangles) =>
        Assert.Throws<InvalidDataException>(() =>
            Read(new CollisionVariant("2", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One, TriangleField: triangles)])));

    [Fact]
    public void Layers_sharing_a_shape_are_rejected_rather_than_decoded_again()
    {
        var bytes = BuildWorldCollision([new CollisionVariant("2", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)])]);
        var pack = new HavokPackfile(bytes);
        Assert.Single(HavokWorldCollision.Read(pack));
        // Point a second root variant at the same shape by duplicating the variant entry: build two variants, then
        // compare with a file whose second variant reuses the first one's tree.
        var shared = BuildWorldCollision([new CollisionVariant("2", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)]),
                                          new CollisionVariant("8", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)], ShareTreeWithPrevious: true)]);
        var ex = Assert.Throws<InvalidDataException>(() => HavokWorldCollision.Read(new HavokPackfile(shared)));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Surface_materials_come_from_the_subpart_or_per_triangle_from_its_storage()
    {
        // M25a: a subpart's userData is its triangles' surface; with userData -1 and 8-bit material indices, each triangle takes
        // the indexed storage material's userData (an index beyond the materials reads as -1).
        var mixed = new CollisionMesh(Quad.Vertices, [(0, 1, 2), (0, 2, 3), (1, 2, 3)], MaterialIndices: [1, 0, 7], Materials: [4, 3]);
        var layer = Assert.Single(Read(new CollisionVariant("8", [Quad, mixed, Tri],
        [
            new(0, Vector3.Zero, Quaternion.Identity, Vector3.One, UserData: 0),
            new(1, Vector3.One, Quaternion.Identity, Vector3.One, UserData: -1),
            new(2, Vector3.Zero, Quaternion.Identity, Vector3.One, UserData: 3),
            new(1, Vector3.One, Quaternion.Identity, Vector3.One, UserData: 9),   // per-triangle storage, but the subpart names a surface: still per triangle
        ])));
        Assert.Equal(layer.TriangleCount, layer.Materials.Length);
        Assert.Equal([0, 0, 3, 4, -1, 3, 3, 4, -1], layer.Materials);
        // Without materials in the storage the subpart's value holds (the synthetic default is 4).
        Assert.Equal([4, 4], Assert.Single(Read(new CollisionVariant("2", [Quad], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)]))).Materials);
    }

    [Fact]
    public void A_shape_referenced_twice_is_rejected_rather_than_read_twice()
    {
        Assert.Single(Read(new CollisionVariant("2", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)])));
        var ex = Assert.Throws<InvalidDataException>(() =>
            Read(new CollisionVariant("2", [Tri], [new(0, Vector3.Zero, Quaternion.Identity, Vector3.One)], RepeatChild: true)));
        Assert.Contains("more than once", ex.Message);
    }
}
