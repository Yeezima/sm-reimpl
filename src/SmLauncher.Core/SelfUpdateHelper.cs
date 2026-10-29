using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SmLauncher.Core;

/// <summary>A process the helper started (the new launcher's window, or the old one put back).</summary>
public interface IStartedProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
}

/// <summary>The helper's view of processes (tests replace it with a fake).</summary>
public interface ILauncherProcesses
{
    /// <summary>
    /// Waits for the old launcher to exit. True when it is gone, or the id now belongs to another process (its start time differs) or cannot be read;
    /// false only when that very process is still running after <paramref name="wait"/>.
    /// </summary>
    bool WaitForExit(int pid, long? startFileTimeUtc, TimeSpan wait);
    /// <summary>Runs a program headless and returns its exit code (-1 when it could not start or did not finish in time; it is then stopped).</summary>
    int RunHeadless(string exe, IReadOnlyList<string> args, TimeSpan wait);
    IStartedProcess? Start(string exe, IReadOnlyList<string> args);
}

public sealed class SystemProcesses : ILauncherProcesses
{
    public static readonly SystemProcesses Instance = new();

    public bool WaitForExit(int pid, long? startFileTimeUtc, TimeSpan wait)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (startFileTimeUtc is long expected)
            {
                long actual;
                try { actual = p.StartTime.ToFileTimeUtc(); }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException) { return true; }   // cannot tell: not verifiably ours
                if (Math.Abs(actual - expected) > TimeSpan.TicksPerSecond) return true;                                                   // the id was reused
            }
            return p.WaitForExit(wait);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return true; }
    }

    public int RunHeadless(string exe, IReadOnlyList<string> args, TimeSpan wait)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p is null) return -1;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(wait)) { try { p.Kill(); } catch (Exception) { } return -1; }
            return p.ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return -1; }
    }

    public IStartedProcess? Start(string exe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi);
        return p is null ? null : new Started(p);
    }

    sealed class Started(Process p) : IStartedProcess
    {
        public bool HasExited => p.HasExited;
        public int ExitCode => p.ExitCode;
        public void Dispose() => p.Dispose();
    }
}

/// <summary>What the helper is asked to do (from its command line: --install-self &lt;target&gt; --root &lt;root&gt; --wait-pid --wait-start [--window]).</summary>
public sealed class HelperRequest
{
    public required string Target { get; init; }
    public required string Root { get; init; }
    /// <summary>The helper's own exe: the new launcher inside the verified build folder.</summary>
    public required string OwnPath { get; init; }
    public int? OldPid { get; init; }
    public long? OldStartFileTimeUtc { get; init; }
    /// <summary>Start the launcher's window when done (and the old one when it fails). Without it (the headless --update-launcher) nothing is started.</summary>
    public bool Window { get; init; }
    /// <summary>Pass --root on to the window (only when the old window had it; a launcher in its install folder finds the folder itself).</summary>
    public bool ForwardRoot { get; init; }
    public IReadOnlyList<string> Forward { get; init; } = [];
    public TimeSpan OldExitWait { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HandshakeWait { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan LockWait { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);
}

public enum HelperOutcome { Updated = 0, PutBack = 1, Refused = 2, OldStillRunning = 3, NotNewer = 4 }

/// <summary>
/// Replaces the launcher in the install folder with the verified new one, which runs this as a helper once the old launcher has exited (a running
/// single-file launcher is never renamed: it still loads parts of itself from its file). Rules: every way out with --window leaves a working launcher
/// started (the new one, or the old one put back with a notice), except when the old launcher is verifiably still running (nothing is changed then), a
/// second helper is already at work, or another launcher holds the install folder. The new window proves itself by writing &lt;exe&gt;.ok after its first
/// update check; a window that closes normally (exit 0) before that is not a failure; a crash (non-zero exit or &lt;exe&gt;.crashed) puts the old one back.
/// </summary>
public static class SelfUpdateHelper
{
    public const string FailedFlag = "--launcher-update-failed";
    public static string OkMarker(string exe) => exe + ".ok";
    public static string CrashMarker(string exe) => exe + ".crashed";

    public static string MutexName(string root) =>
        @"Local\SmReimplLauncherHelper-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(root).TrimEnd('\\').ToUpperInvariant())))[..16];

    /// <summary>The helper's own exe is the launcher of an installed build: &lt;root&gt;\versions\&lt;tag&gt;\0 - SM Reimpl Launcher.exe, matching that build's file list.</summary>
    public static bool IsVerifiedBundled(string ownPath, InstallLayout layout)
    {
        string own = Path.GetFullPath(ownPath);
        if (!string.Equals(Path.GetFileName(own), PackageLayout.BundledLauncher, StringComparison.OrdinalIgnoreCase)) return false;
        string versionDir = Path.GetDirectoryName(own)!;
        string? tag = Path.GetFileName(versionDir);
        if (!Names.IsTag(tag) || !string.Equals(Path.GetDirectoryName(versionDir), layout.VersionsDir, StringComparison.OrdinalIgnoreCase)) return false;
        return Manifest.FileMatches(versionDir, PackageLayout.BundledLauncher);
    }

    public static HelperOutcome Run(HelperRequest r, ILauncherProcesses procs, Action<string> log,
        Func<string, string, string?>? place = null, Func<string, string, bool>? isNewer = null, TimeSpan? selfTestWait = null)
    {
        place ??= LauncherVersions.Place;
        isNewer ??= (source, target) => LauncherVersions.ShouldReplace(LauncherVersions.Of(source), LauncherVersions.Of(target), File.Exists(target));
        var layout = new InstallLayout(r.Root);
        string target = Path.GetFullPath(r.Target);
        if (!string.Equals(target, layout.LauncherExe, StringComparison.OrdinalIgnoreCase))
        {
            log($"helper: refused: {target} is not the launcher of the install folder {layout.Root}");
            return HelperOutcome.Refused;                                      // never start or replace anything else
        }

        void StartTarget(bool failed)
        {
            if (!r.Window) return;
            if (!File.Exists(target)) { log("helper: no launcher to start at " + target); return; }
            var args = new List<string>();
            if (r.ForwardRoot) args.AddRange(["--root", layout.Root]);
            args.AddRange(r.Forward);
            if (failed) args.Add(FailedFlag);
            try { procs.Start(target, args)?.Dispose(); log($"helper: started {(failed ? "the previous launcher again" : "the launcher")}"); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { log("helper: could not start the launcher: " + e.Message); }
        }

        if (!IsVerifiedBundled(r.OwnPath, layout))
        {
            log("helper: refused: it is not the verified launcher of an installed build");
            StartTarget(failed: true);
            return HelperOutcome.Refused;
        }

        using var mutex = new Mutex(false, MutexName(layout.Root));
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) { log("helper: another helper is replacing the launcher already"); return HelperOutcome.Refused; }
        RootLock? rootLock = null;
        string? backup = null;

        HelperOutcome PutBack(string why)
        {
            log($"helper: {why}; putting the previous launcher back");
            rootLock?.Dispose();
            rootLock = null;
            if (backup is not null && File.Exists(backup))
            {
                try { LauncherVersions.Restore(backup, target); log("helper: the previous launcher is back"); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("helper: could not put the previous launcher back: " + e.Message); }
            }
            try { File.Delete(CrashMarker(target)); } catch (Exception) { }
            StartTarget(failed: true);
            return HelperOutcome.PutBack;
        }

        try
        {
            if (r.OldPid is int pid && !procs.WaitForExit(pid, r.OldStartFileTimeUtc, r.OldExitWait))
            {
                log("helper: the old launcher is still running; nothing changed");
                return HelperOutcome.OldStillRunning;
            }
            rootLock = RootLock.TryAcquire(layout, r.LockWait);
            if (rootLock is null) { log("helper: another launcher has the install folder open; nothing changed"); return HelperOutcome.Refused; }
            if (!isNewer(r.OwnPath, target))
            {
                log("helper: the launcher there is as new or newer; nothing changed");
                rootLock.Dispose();
                rootLock = null;
                StartTarget(failed: false);
                return HelperOutcome.NotNewer;
            }
            try { backup = place(r.OwnPath, target); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Place rolls its own steps back; if the target is still missing, the newest .old copy is the old launcher.
                if (!File.Exists(target))
                {
                    var old = Directory.EnumerateFiles(Path.GetDirectoryName(target)!, Path.GetFileName(target) + "*.old").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                    backup = old;
                }
                return PutBack("the new launcher could not be put in place (" + e.Message + ")");
            }
            log($"helper: placed the new launcher at {target}{(backup is null ? "" : $", the previous one kept as {Path.GetFileName(backup)}")}");
            int check = procs.RunHeadless(target, ["--selftest", "--rules"], selfTestWait ?? TimeSpan.FromSeconds(60));
            if (check != 0) return PutBack($"the new launcher failed its self-test (exit {check})");
            if (!r.Window)
            {
                if (backup is not null) { try { File.Delete(backup); } catch (Exception) { } }
                log("helper: done");
                return HelperOutcome.Updated;
            }
            string ok = OkMarker(target), crash = CrashMarker(target);
            foreach (var f in new[] { ok, crash }) { try { File.Delete(f); } catch (Exception) { } }
            var args = new List<string>();
            if (backup is not null) args.AddRange(["--self-updated", backup]);
            if (r.ForwardRoot) args.AddRange(["--root", layout.Root]);
            args.AddRange(r.Forward);
            IStartedProcess? p;
            try { p = procs.Start(target, args); }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException) { return PutBack("the new launcher could not be started (" + e.Message + ")"); }
            if (p is null) return PutBack("the new launcher could not be started");
            using (p)
            {
                rootLock.Dispose();                                            // the new window takes the install folder now
                rootLock = null;
                var until = DateTime.UtcNow + r.HandshakeWait;
                while (DateTime.UtcNow < until && !File.Exists(ok) && !p.HasExited) Thread.Sleep(r.Poll);
                if (File.Exists(ok)) { try { File.Delete(ok); } catch (Exception) { } log("helper: the new launcher started"); return HelperOutcome.Updated; }
                if (!p.HasExited) { log("helper: the new launcher is still starting; left running"); return HelperOutcome.Updated; }
                bool crashed = p.ExitCode != 0 || File.Exists(crash);
                if (!crashed)
                {
                    log("helper: the new launcher was closed before its first update check (exit 0): kept");
                    if (backup is not null) { try { File.Delete(backup); } catch (Exception) { } }
                    return HelperOutcome.Updated;
                }
                return PutBack($"the new launcher stopped before its first update check (exit {p.ExitCode}{(File.Exists(crash) ? ", crashed" : "")})");
            }
        }
        finally
        {
            rootLock?.Dispose();
            if (owned) mutex.ReleaseMutex();
        }
    }
}
