using System.Numerics;
using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>One spin edit changes exactly one component (owner 2026-11-19): the others stay bit for bit.</summary>
public class ComponentEditTests
{
    static readonly MapTransform Odd = new(new Vector3(12.345f, -164.983f, 0.1f), new Vector3(33.3f, -12.34f, 179.99f), new Vector3(1.001f, 2.345f, 0.333f));

    static void Same(float a, float b) => Assert.Equal(BitConverter.SingleToInt32Bits(a), BitConverter.SingleToInt32Bits(b));

    [Theory]
    [InlineData(TransformProp.Position)]
    [InlineData(TransformProp.Rotation)]
    [InlineData(TransformProp.Scale)]
    public void LocalEditChangesOnlyTheEditedAxis(TransformProp p)
    {
        for (int axis = 0; axis < 3; axis++)
        {
            var r = ComponentEdit.Local(Odd, p, axis, 7.125f);
            foreach (var q in Enum.GetValues<TransformProp>())
                for (int a = 0; a < 3; a++)
                {
                    float got = ComponentEdit.Get(ComponentEdit.Of(r, q), a), was = ComponentEdit.Get(ComponentEdit.Of(Odd, q), a);
                    if (q == p && a == axis) Same(7.125f, got); else Same(was, got);
                }
        }
    }

    [Fact]
    public void RoundedLookingValuesSurviveAnXEdit()
    {
        var r = ComponentEdit.Local(Odd, TransformProp.Position, 0, 50f);
        Same(-164.983f, r.Position.Y); Same(0.1f, r.Position.Z); Same(1.001f, r.Scale.X);
    }

    [Fact]
    public void WorldPositionEditUnderARotatedParentKeepsLocalRotationAndScale()
    {
        var parent = new MapTransform(new Vector3(5, -3, 2), new Vector3(10, 20, 75), new Vector3(2, 2, 2)).ToMatrix();
        var world = TransformMath.Decompose(Odd.ToMatrix() * parent);
        var r = ComponentEdit.World(Odd, parent, TransformProp.Position, 0, world.Position.X + 4f);
        Assert.Equal(Odd.Rotation, r.Rotation); Assert.Equal(Odd.Scale, r.Scale);
        var after = TransformMath.Decompose(r.ToMatrix() * parent);
        Assert.Equal(world.Position.X + 4f, after.Position.X, 3);
        Assert.Equal(world.Position.Y, after.Position.Y, 3);
        Assert.Equal(world.Position.Z, after.Position.Z, 3);
    }

    [Fact]
    public void WorldRotationEditKeepsPositionAndScaleExactly()
    {
        var parent = new MapTransform(new Vector3(5, -3, 2), new Vector3(0, 0, 40), Vector3.One).ToMatrix();
        var small = new MapTransform(new Vector3(1.234f, -164.983f, 3), new Vector3(0, 0, 10), new Vector3(1.001f, 1.001f, 1.001f));
        var world = TransformMath.Decompose(small.ToMatrix() * parent);
        var r = ComponentEdit.World(small, parent, TransformProp.Rotation, 2, world.Rotation.Z + 15f);
        Assert.Equal(small.Position, r.Position); Assert.Equal(small.Scale, r.Scale);
        Assert.Equal(25f, r.Rotation.Z, 2);
    }

    [Fact]
    public void WorldEditWithNoParentIsLocal() =>
        Assert.Equal(ComponentEdit.Local(Odd, TransformProp.Scale, 1, 4f), ComponentEdit.World(Odd, null, TransformProp.Scale, 1, 4f));

    [Fact]
    public void MultiSelectXEditLeavesYAndZIdentical()
    {
        var a = Odd; var b = new MapTransform(new Vector3(1, 2.001f, -164.983f), new Vector3(0, 0.1f, 0), new Vector3(1.001f, 1, 1));
        var ra = ComponentEdit.Delta(a, TransformProp.Position, 0, a.Position.X, 20f);
        var rb = ComponentEdit.Delta(b, TransformProp.Position, 0, a.Position.X, 20f);
        Same(a.Position.Y, ra.Position.Y); Same(a.Position.Z, ra.Position.Z);
        Same(b.Position.Y, rb.Position.Y); Same(b.Position.Z, rb.Position.Z);
        Assert.Equal(b.Position.X + (20f - a.Position.X), rb.Position.X);
        Assert.Equal(a.Rotation, ra.Rotation); Assert.Equal(b.Scale, rb.Scale);
    }

    [Fact]
    public void MultiSelectScaleEditUsesARatioOnThatAxisOnly()
    {
        var r = ComponentEdit.Delta(Odd, TransformProp.Scale, 0, 1f, 2f);
        Same(Odd.Scale.X * 2f, r.Scale.X); Same(Odd.Scale.Y, r.Scale.Y); Same(Odd.Scale.Z, r.Scale.Z);
    }
}
