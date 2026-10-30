using SmContent;

namespace SmFormats.Tests;

/// <summary>ui/hud-camera-2 (owner, 2026-10-30): exactly one sound set per reload; R during a reload plays nothing; a new reload after a cancel plays normally.</summary>
public class ReloadSoundGuardTests
{
    [Fact]
    public void A_start_seen_again_during_the_reload_schedules_nothing()
    {
        var g = new ReloadSoundGuard();
        Assert.NotNull(g.Start(1, 10.0, 2.2f));
        Assert.Null(g.Start(1, 10.5, 2.2f));
        Assert.Null(g.Start(1, 12.1, 2.2f));
        Assert.NotNull(g.Start(1, 12.3, 2.2f));   // a later reload
    }

    [Fact]
    public void A_cancelled_reload_lets_the_next_one_play_at_once_and_drops_the_old_sounds()
    {
        var g = new ReloadSoundGuard();
        int first = g.Start(1, 10.0, 2.2f)!.Value;
        g.Cancel(1);
        Assert.False(g.Current(1, first));
        int second = g.Start(1, 10.4, 2.2f)!.Value;   // R again within the old reload's length
        Assert.True(g.Current(1, second));
        Assert.False(g.Current(1, first));
    }

    [Fact]
    public void Slots_do_not_block_each_other()
    {
        var g = new ReloadSoundGuard();
        Assert.NotNull(g.Start(1, 10.0, 2.2f));
        Assert.NotNull(g.Start(2, 10.5, 2.2f));   // another weapon's reload after a switch
    }
}
