using System.Numerics;
using SmContent.Extras.Musou;
using Xunit;

namespace SmFormats.Tests;

/// <summary>The Dreadnought's torrent of visual tracers (owner 2026-11-18): a ring around the aim axis that turns each shot, with a small spread; the camera shake is x0.2. Presentation only.</summary>
public class DreadnoughtTorrentTests
{
    static Func<float> Rng(int seed) { var r = new Random(seed); return () => (float)r.NextDouble(); }

    [Fact]
    public void A_shot_makes_the_default_count_of_streaks_on_the_ring_across_the_axis()
    {
        var axis = Vector3.Normalize(new Vector3(0.3f, 1f, 0.1f));
        var fan = DreadnoughtTorrent.Fan(axis, 0, DreadnoughtTorrent.Settings.Default, Rng(1));
        Assert.Equal(DreadnoughtTorrent.PerShot, fan.Length);
        foreach (var (offset, dir) in fan)
        {
            Assert.InRange(Vector3.Dot(offset, axis), -1e-4f, 1e-4f);   // on the plane across the axis
            Assert.InRange(offset.Length(), DreadnoughtTorrent.RingRadius * 0.6f - 1e-4f, DreadnoughtTorrent.RingRadius + 1e-4f);
            Assert.InRange(dir.Length(), 0.999f, 1.001f);
            float angle = MathF.Acos(Math.Clamp(Vector3.Dot(dir, axis), -1f, 1f)) * 180f / MathF.PI;
            Assert.InRange(angle, 0f, DreadnoughtTorrent.ConeDegrees + DreadnoughtTorrent.JitterDegrees * 1.5f);   // a small spread, never a wide fan
        }
    }

    [Fact]
    public void The_ring_turns_between_shots_and_the_streaks_differ()
    {
        var axis = Vector3.UnitY;
        var s = DreadnoughtTorrent.Settings.Default with { JitterDeg = 0f };
        var a = DreadnoughtTorrent.Fan(axis, 0, s, () => 1f);
        var b = DreadnoughtTorrent.Fan(axis, 1, s, () => 1f);
        Assert.True(Vector3.Distance(a[0].Offset, b[0].Offset) > 0.1f);
        Assert.True(Vector3.Distance(a[0].Offset, a[1].Offset) > 0.1f);
    }

    [Fact]
    public void Zero_count_or_a_zero_axis_makes_none_and_every_other_aid_setting_is_safe()
    {
        Assert.Empty(DreadnoughtTorrent.Fan(Vector3.UnitY, 0, DreadnoughtTorrent.Settings.Default with { Count = 0 }, Rng(2)));
        Assert.All(DreadnoughtTorrent.Fan(Vector3.Zero, 0, DreadnoughtTorrent.Settings.Default, Rng(2)), x => Assert.Equal(default, x));
        Assert.All(DreadnoughtTorrent.Fan(Vector3.UnitZ, 3, DreadnoughtTorrent.Settings.Default, Rng(3)), x => Assert.False(float.IsNaN(x.Direction.X)));   // an axis straight up
    }

    [Fact]
    public void The_shake_scale_is_one_fifth()
    {
        Assert.Equal(0.2f, DreadnoughtTorrent.ShakeScale);
        Assert.True(DreadnoughtTorrent.MaxPerFrame >= DreadnoughtTorrent.PerShot);
    }
}
