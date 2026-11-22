using SmLauncher.Core;

namespace SmLauncher.Tests;

public class LockedHandOverTests
{
    static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(300);

    [Fact]
    public void A_successful_hand_over_leaves_the_lock_free_for_the_new_launcher()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        var held = RootLock.TryAcquire(l, TimeSpan.Zero);
        var outcome = LockedHandOver.Run(ref held, l, () => true, Wait);
        Assert.Equal(HandOverOutcome.HandedOver, outcome);
        Assert.Null(held);
        using var next = RootLock.TryAcquire(l, TimeSpan.Zero);
        Assert.NotNull(next);
    }

    [Fact]
    public void A_failed_hand_over_takes_the_lock_back()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        var held = RootLock.TryAcquire(l, TimeSpan.Zero);
        var outcome = LockedHandOver.Run(ref held, l, () => false, Wait);
        Assert.Equal(HandOverOutcome.StayedLocked, outcome);
        Assert.NotNull(held);
        Assert.Null(RootLock.TryAcquire(l, Wait));          // a second launcher cannot open the folder
        held!.Dispose();
    }

    [Fact]
    public void A_throwing_hand_over_takes_the_lock_back_and_logs()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        var held = RootLock.TryAcquire(l, TimeSpan.Zero);
        var log = new List<string>();
        var outcome = LockedHandOver.Run(ref held, l, () => throw new IOException("boom"), Wait, log.Add);
        Assert.Equal(HandOverOutcome.StayedLocked, outcome);
        Assert.NotNull(held);
        Assert.Null(RootLock.TryAcquire(l, Wait));
        Assert.Contains(log, m => m.Contains("boom"));
        held!.Dispose();
    }

    [Fact]
    public void A_failed_hand_over_whose_lock_was_taken_meanwhile_reports_the_loss()
    {
        using var t = new TempDir();
        var l = new InstallLayout(t.Path);
        var held = RootLock.TryAcquire(l, TimeSpan.Zero);
        RootLock? other = null;
        var outcome = LockedHandOver.Run(ref held, l, () => { other = RootLock.TryAcquire(l, TimeSpan.Zero); return false; }, Wait);
        using (other)
        {
            Assert.NotNull(other);
            Assert.Equal(HandOverOutcome.LockLost, outcome);
            Assert.Null(held);
        }
    }
}
