using SmClient.Bullets;

namespace SmNet.Tests;

/// <summary>plasma-soft-visuals (owner, 2026-11-14): plasma ribbons fade out across their width and at both ends, glow cards toward their rim; the chambers' arcs stay on the gun (client/Bullets/PlasmaSoftEdges.cs, engine-free).</summary>
public class PlasmaSoftEdgesTests
{
    [Fact]
    public void TheCoreStaysAndTheEdgeFadesToNothing()
    {
        Assert.Equal(1f, PlasmaSoftEdges.Lateral(0f));
        Assert.Equal(1f, PlasmaSoftEdges.Lateral(PlasmaSoftEdges.CoreHalfWidth));
        Assert.Equal(0f, PlasmaSoftEdges.Lateral(1f), 5);
        float last = 1f;
        for (float x = 0f; x <= 1f; x += 0.05f) { float v = PlasmaSoftEdges.Lateral(x); Assert.InRange(v, 0f, last + 1e-6f); last = v; }   // a smooth, never rising gradient: no step
    }

    [Fact]
    public void ARibbonFadesInAtTheHeadAndOutAtTheTail()
    {
        Assert.Equal(0f, PlasmaSoftEdges.Ribbon(0f), 5);
        Assert.Equal(0f, PlasmaSoftEdges.Ribbon(1f), 5);
        Assert.Equal(1f, PlasmaSoftEdges.Ribbon(0.35f), 5);
        for (float a = 0f; a < 1f; a += 0.02f) Assert.InRange(MathF.Abs(PlasmaSoftEdges.Ribbon(a + 0.02f) - PlasmaSoftEdges.Ribbon(a)), 0f, 0.2f);   // no jump between neighbouring segments
    }

    [Fact]
    public void ARibbonSeenEndOnFadesOut() { Assert.Equal(1f, PlasmaSoftEdges.EndOn(0f)); Assert.Equal(1f, PlasmaSoftEdges.EndOn(0.6f)); Assert.Equal(0f, PlasmaSoftEdges.EndOn(1f), 5); }

    [Fact]
    public void AGlowCardHasNoStraightEdge() { Assert.Equal(1f, PlasmaSoftEdges.Radial(0.5f)); Assert.Equal(0f, PlasmaSoftEdges.Radial(1f), 5); }

    [Theory]
    [InlineData("plasma_gun_tracer", true)]
    [InlineData("chaos_plasma_gun_trail", true)]
    [InlineData("csm_plasma_cannon_secondary_trail", true)]
    [InlineData("plasma_pistol_charged", true)]
    [InlineData("bolter_tracer", false)]
    [InlineData("", false)]
    public void OnlyPlasmaEffectsAreSoftened(string name, bool expected) => Assert.Equal(expected, PlasmaSoftEdges.Applies(name));

    [Fact]
    public void TheChamberArcsAreLeashedToTheGun() => Assert.InRange(PlasmaSoftEdges.ArcLeash, 0.05f, 0.6f);   // the data's offset reaches 2.5 m (a blue light over the carrier's head)
}
