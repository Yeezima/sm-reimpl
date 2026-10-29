using System.Numerics;
using SmContent;

namespace SmFormats.Tests;

/// <summary>fx/impact-pass: where a thrust leaves its nozzle (<see cref="NozzleExit"/>), on a synthetic nozzle: a tube of points around an axis.</summary>
public class NozzleExitTests
{
    /// <summary>A ring of 8 points every 5 cm from <paramref name="from"/> to <paramref name="to"/> along -Z, radius <paramref name="radius"/>, around (x, y).</summary>
    static IEnumerable<Vector3> Tube(float x, float y, float from, float to, float radius)
    {
        for (float z = from; z >= to - 1e-4f; z -= 0.05f)
            for (int i = 0; i < 8; i++)
                yield return new Vector3(x + radius * MathF.Cos(i * MathF.PI / 4f), y + radius * MathF.Sin(i * MathF.PI / 4f), z);
    }

    [Fact]
    public void A_marker_inside_its_nozzle_is_moved_to_the_exit()
    {
        // The nozzle runs from z 1.6 down to z 1.3; the marker sits at z 1.55 and points down (-Z): the exit is 0.25 m ahead.
        var model = Tube(0f, 0f, 1.6f, 1.3f, 0.12f).ToList();
        Assert.Equal(0.25f, NozzleExit.Along(model, new Vector3(0, 0, 1.55f), -Vector3.UnitZ), 3);
    }

    [Fact]
    public void A_marker_at_the_exit_stays_and_geometry_off_the_axis_or_behind_does_not_count()
    {
        var model = Tube(0f, 0f, 1.6f, 1.3f, 0.12f).Concat(Tube(0.5f, 0f, 1.2f, 0.4f, 0.1f)).ToList();   // a second tube 0.5 m to the side reaches lower
        Assert.Equal(0f, NozzleExit.Along(model, new Vector3(0, 0, 1.3f), -Vector3.UnitZ), 3);
        Assert.Equal(0f, NozzleExit.Along(model, new Vector3(0, 0, 1.55f), Vector3.Zero));   // a degenerate axis
        Assert.Equal(0.25f, NozzleExit.Along(model, new Vector3(0, 0, 1.55f), new Vector3(0, 0, -3f)), 3);   // the axis need not be unit length
    }
}
