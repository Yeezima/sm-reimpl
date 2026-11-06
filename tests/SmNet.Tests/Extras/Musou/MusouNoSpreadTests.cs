using SmSim;

namespace SmNet.Tests.Extras.Musou;

/// <summary>musou/no-spread (owner, 2026-11-06: "in musou mode only completely remove bullet spread"): Musou shots go exactly along the aim; Classic PvP keeps the data's spread.</summary>
public class MusouNoSpreadTests
{
    static readonly SimConfig Musou = SpreadTests.Config with { Extras = ContentExtras.Musou };

    static List<PlayerState> Shots(SimConfig c)
    {
        var s = SpreadTests.Fresh();
        var shots = new List<PlayerState>();
        for (uint t = 1; t <= 60; t++)
        {
            s = Combat.StepPlayer(s, new PlayerInput(t, 0f, 0f, 0f, InputButtons.Fire), c, out var ev);
            if (ev.Fired) shots.Add(s);
        }
        return shots;
    }

    [Fact]
    public void Only_musou_removes_the_spread()
    {
        Assert.True(Musou.NoBulletSpread);
        Assert.False(SpreadTests.Config.NoBulletSpread);
        Assert.False((SpreadTests.Config with { Extras = ContentExtras.Orks | ContentExtras.Gore | ContentExtras.LoyalistBarks }).NoBulletSpread);   // extended: Classic PvP with extras
    }

    [Fact]
    public void In_musou_every_shot_goes_along_the_aim_and_nothing_blooms()
    {
        var shots = Shots(Musou);
        Assert.True(shots.Count >= 10);
        Assert.All(shots, s => { Assert.Equal(0f, s.SpreadYaw); Assert.Equal(0f, s.SpreadPitch); Assert.Equal(0f, s.SpreadBloom); });
        Assert.Equal((0f, 0f), Spread.HalfWidths(SpreadTests.Bolter, shots[^1], 0, 1f, Musou));
    }

    [Fact]
    public void Classic_still_scatters_and_blooms()
    {
        var shots = Shots(SpreadTests.Config);
        Assert.Contains(shots, s => s.SpreadYaw != 0f || s.SpreadPitch != 0f);
        Assert.True(shots[^1].SpreadBloom > 0f);
    }
}
