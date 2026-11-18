using SmMapmaker;

namespace SmMapmaker.Tests;

/// <summary>Which selected objects take a typed transform entry (owner, 2026-11-18): exactly the selection, a selected descendant of a selected object rides on it.</summary>
public class TransformTargetsTests
{
    static MapSession Scene(Lab lab)
    {
        var s = lab.Session();
        Assert.True(s.Apply([
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"parent","transform":{"position":[0,0,0]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"kid","transform":{"position":[0,5,0]}}"""),
            Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"grandkid","transform":{"position":[0,9,0]}}"""),
            Lab.Op("""{"op":"set-parent","id":"o2","parent":"o1"}"""),
            Lab.Op("""{"op":"set-parent","id":"o3","parent":"o2"}"""),
        ]).Ok);
        return s;
    }

    [Fact]
    public void ASelectedChildAloneIsItsOwnTarget()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.Equal(["o2"], TransformTargets.Roots(s.Document, ["o2"]));
        Assert.Equal(["o3"], TransformTargets.Roots(s.Document, ["o3"]));
    }

    [Fact]
    public void ASelectedDescendantRidesOnItsSelectedAncestor()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.Equal(["o1"], TransformTargets.Roots(s.Document, ["o1", "o2", "o3"]));
        Assert.Equal(["o1"], TransformTargets.Roots(s.Document, ["o2", "o1", "o3"]));
        Assert.Equal(["o1"], TransformTargets.Roots(s.Document, ["o1", "o3"]));   // the unselected middle does not break the chain
    }

    [Fact]
    public void SiblingsAndUnrelatedObjectsAreEachTargets()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        Assert.True(s.Apply(Lab.Op("""{"op":"place","asset":{"source":"sm1","map":"mp_fake","definition":"def_wall"},"name":"other","transform":{"position":[3,0,0]}}""")).Ok);
        Assert.Equal(["o2", "o4"], TransformTargets.Roots(s.Document, ["o2", "o4"]));
    }

    [Fact]
    public void RotatingOnlyTheChildLeavesTheParentAlone()
    {
        using var lab = new Lab();
        var s = Scene(lab);
        var parentBefore = s.Document.Get("o1").Transform;
        foreach (var id in TransformTargets.Roots(s.Document, ["o2"]))
            Assert.True(s.Apply(Lab.Op($$"""{"op":"transform","id":"{{id}}","rotation":[0,0,45]}""")).Ok);
        Assert.Equal(parentBefore, s.Document.Get("o1").Transform);
        Assert.Equal(45f, s.Document.Get("o2").Transform.Rotation.Z);
    }
}
