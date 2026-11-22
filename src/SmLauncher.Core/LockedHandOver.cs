namespace SmLauncher.Core;

/// <summary>What happened to an install folder's lock around a hand-over to another launcher.</summary>
public enum HandOverOutcome
{
    /// <summary>The other launcher was started; this one lets go of the lock and closes.</summary>
    HandedOver,
    /// <summary>The start failed or threw; the lock is held again and this window carries on.</summary>
    StayedLocked,
    /// <summary>The start failed and the lock could not be taken back: another launcher has the folder, so this window must close.</summary>
    LockLost,
}

/// <summary>
/// Hands the install folder over to another launcher. The lock is released before the start (the new launcher needs it), and taken back when the
/// start fails or throws, so a window that carries on never runs without the lock (one launcher per folder).
/// </summary>
public static class LockedHandOver
{
    public static HandOverOutcome Run(ref RootLock? held, InstallLayout layout, Func<bool> start, TimeSpan reacquireWait, Action<string>? log = null)
    {
        held?.Dispose();
        held = null;
        bool started = false;
        try { started = start(); }
        catch (Exception e) { log?.Invoke("launcher: hand-over failed: " + e.Message); }
        if (started) return HandOverOutcome.HandedOver;
        held = RootLock.TryAcquire(layout, reacquireWait);
        return held is null ? HandOverOutcome.LockLost : HandOverOutcome.StayedLocked;
    }
}
