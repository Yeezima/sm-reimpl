using System.Runtime.InteropServices;

namespace SmServer;

/// <summary>
/// hotfix/musou-server-lag: the main loop's short wait between two passes, and its thread's priority. On Windows <c>Thread.Sleep(1)</c> sleeps a whole
/// timer period (15.6 ms unless the process raised its timer resolution, which this server never did), so the 30 Hz tick started 31 or 47 ms after the last
/// and the 20 Hz snapshots 47 or 63 ms apart; and with the PC busy (a build, a test sweep, the hosting client itself) a normal-priority sleeper woke tens to
/// hundreds of milliseconds late: ticks skipped, snapshots more than the client's 100 ms interpolation delay apart (measured, docs/milestones.md). On Windows the
/// wait is now a high-resolution waitable timer (Windows 10 1803 and later; else the timer resolution is raised to 1 ms for this process) and the server process
/// (its loop and the Musou route-search workers the loop waits for) (its loop thread; review 2026-11-08: not the whole process, so a client-hosted server never takes CPU from the client) runs above normal priority, so a busy PC no longer delays its ticks. Elsewhere (the Linux VM) nothing changes: <c>Thread.Sleep(1)</c> already sleeps about 1 ms there. Pacing only: what a tick
/// computes is unchanged (the tick length and the catch-up rule are the same).
/// </summary>
public static class LoopPacing
{
    static IntPtr _timer;
    static bool _resolutionRaised;

    /// <summary>What <see cref="Start"/> set up (one line for the server's log).</summary>
    public static string Describe { get; private set; } = "Thread.Sleep(1)";

    /// <summary>Called once by the main loop's thread before it starts.</summary>
    public static void Start()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (Environment.GetEnvironmentVariable(OldPacingVariable) == "1") { Describe = $"Thread.Sleep(1), normal priority ({OldPacingVariable}=1, test aid)"; return; }
        string priority = "normal priority";
        try
        {
            // Only the loop thread is raised, not the process (review 2026-11-08): a server hosted on the player's own PC must not take CPU from the client's render thread.
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            priority = "above normal loop-thread priority";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ThreadStateException or PlatformNotSupportedException or UnauthorizedAccessException) { }
        try
        {
            _timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
            if (_timer != IntPtr.Zero) { Describe = $"high-resolution waitable timer, {priority}"; return; }
            _resolutionRaised = timeBeginPeriod(1) == 0;
            Describe = (_resolutionRaised ? "Thread.Sleep(1) at 1 ms timer resolution, " : "Thread.Sleep(1), ") + priority;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { Describe = "Thread.Sleep(1), " + priority; }
    }

    /// <summary>Test aid: <c>SM_OLD_LOOP_PACING=1</c> keeps the loop as before (Thread.Sleep(1) at the default timer resolution, normal priority) for before/after runs.</summary>
    public const string OldPacingVariable = "SM_OLD_LOOP_PACING";

    /// <summary>Waits about one millisecond (the loop's pause between passes).</summary>
    public static void Wait()
    {
        if (_timer != IntPtr.Zero)
        {
            long due = -10_000;   // relative, in 100 ns units: 1 ms
            if (SetWaitableTimer(_timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false) && WaitForSingleObject(_timer, 100) == 0) return;
        }
        Thread.Sleep(1);
    }

    const uint CreateWaitableTimerHighResolution = 0x00000002, TimerAllAccess = 0x1F0003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr argument, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint period);
}
