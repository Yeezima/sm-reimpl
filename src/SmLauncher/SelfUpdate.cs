using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using SmLauncher.Core;

namespace SmLauncher;

/// <summary>
/// The launcher ships inside every playtest zip ("0 - SM Reimpl Launcher.exe"). Once a build is installed and verified, its launcher (listed in the build's
/// file list, hash matching) is compared with the launcher in the install folder (file version, set by release.ps1 from the build commit's time); only a
/// newer one replaces it. A launcher that is not running is replaced directly; the running one through the new launcher acting as a helper
/// (<see cref="SelfUpdateHelper"/>), because a running single-file program must not be renamed.
/// </summary>
static class SelfUpdate
{
    public static Version? FileVersionOf(string exe) => LauncherVersions.Of(exe);

    /// <summary>The verified build's launcher (listed in its file list, hash matching) when it is newer than the one at <paramref name="target"/>.</summary>
    public static string? NewerBundled(string? versionDir, string target)
    {
        var there = File.Exists(target) ? LauncherVersions.Of(target) ?? new Version(0, 0) : new Version(0, 0);
        return LauncherVersions.NewerBundled(versionDir, there);
    }

    public enum Outcome { NotNewer, Installed, NeedsHelper }

    public static bool IsRunning(string target) =>
        string.Equals(Path.GetFullPath(target), Path.GetFullPath(LauncherInfo.ExePath ?? ""), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="target"/> unless the launcher already there is as new or newer (never a downgrade). The running
    /// launcher is never replaced here: <see cref="Outcome.NeedsHelper"/> says to use <see cref="StartHelper"/> and exit.
    /// </summary>
    public static Outcome Install(string source, string target)
    {
        if (!LauncherVersions.ShouldReplace(LauncherVersions.Of(source), LauncherVersions.Of(target), File.Exists(target))) return Outcome.NotNewer;
        if (IsRunning(target)) return Outcome.NeedsHelper;
        string? backup = LauncherVersions.Place(source, target);
        if (backup is not null) { try { File.Delete(backup); } catch (Exception) { } }
        return Outcome.Installed;
    }

    /// <summary>
    /// Replacing the running launcher: starts the verified new launcher (in the build's folder) as a helper: --install-self &lt;target&gt; --root &lt;root&gt;
    /// --wait-pid &lt;this process&gt; --wait-start &lt;its start time&gt; [--window] [--forward-root] (see <see cref="SelfUpdateHelper.Run"/>). The caller
    /// releases the install folder's lock and exits right after.
    /// </summary>
    public static void StartHelper(string helperExe, string target, string root, bool window, bool forwardRoot, IEnumerable<string> forward)
    {
        var psi = new ProcessStartInfo(helperExe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(helperExe)!, CreateNoWindow = true };
        using var me = Process.GetCurrentProcess();
        psi.ArgumentList.Add("--install-self");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add("--root");
        psi.ArgumentList.Add(root);
        psi.ArgumentList.Add("--wait-pid");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        psi.ArgumentList.Add("--wait-start");
        psi.ArgumentList.Add(me.StartTime.ToFileTimeUtc().ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (window) psi.ArgumentList.Add("--window");
        if (forwardRoot) psi.ArgumentList.Add("--forward-root");
        foreach (var a in forward) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
    }

    /// <summary>The helper (the --install-self command): its request from the command line, run by <see cref="SelfUpdateHelper.Run"/>.</summary>
    public static int RunHelper(Args a, Action<string> log)
    {
        if (a["install-self"] is not { } target || a["root"] is not { } root) { log("helper: refused: --install-self needs the target and --root"); return (int)HelperOutcome.Refused; }
        var forward = new List<string>();
        if (a.Has("offline")) forward.Add("--offline");
        if (a.Has("no-shortcuts")) forward.Add("--no-shortcuts");
        if (a["screenshot"] is { } png) { forward.Add("--screenshot"); forward.Add(png); }
        var request = new HelperRequest
        {
            Target = target,
            Root = root,
            OwnPath = LauncherInfo.ExePath ?? "",
            OldPid = int.TryParse(a["wait-pid"], out int pid) ? pid : null,
            OldStartFileTimeUtc = long.TryParse(a["wait-start"], out long start) ? start : null,
            Window = a.Has("window"),
            ForwardRoot = a.Has("forward-root"),
            Forward = forward,
        };
        return (int)SelfUpdateHelper.Run(request, SystemProcesses.Instance, log);
    }

    /// <summary>
    /// The new window's sign that it works (after its first update check): &lt;exe&gt;.ok for the helper, then the backup it was told about is removed
    /// (only "&lt;exe name&gt;*.old" beside it; retried for a while). The .ok sign itself is left for the helper.
    /// </summary>
    public static void ConfirmStarted(string? backup)
    {
        if (LauncherInfo.ExePath is not { } exe) return;
        try { File.WriteAllText(SelfUpdateHelper.OkMarker(exe), ""); } catch (Exception) { }
        bool accept = LauncherVersions.IsOwnBackup(exe, backup);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 120; i++)
            {
                bool gone = !accept || !File.Exists(backup!);
                if (accept && !gone) { try { File.Delete(backup!); gone = true; } catch (Exception) { } }
                if (gone && LauncherVersions.CleanOld(exe, markers: false)) return;
                await Task.Delay(500);
            }
        });
    }

    /// <summary>On a normal start: the leftovers of an earlier self-update (.old, .new and the hand-over signs).</summary>
    public static void CleanOld()
    {
        if (LauncherInfo.ExePath is { } exe) LauncherVersions.CleanOld(exe, markers: true);
    }
}

/// <summary>Desktop and Start menu shortcuts to the installed launcher (IShellLink; created only when the tester leaves the box ticked).</summary>
static class Shortcuts
{
    public static void Create(string target)
    {
        foreach (var folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) continue;
            var link = (IShellLinkW)new CShellLink();
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("SM Reimpl: keeps the playtest build up to date and starts it");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(Path.Combine(folder, "SM Reimpl Launcher.lnk"), true);
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class CShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, nint fd, uint flags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

/// <summary>Starts the active version's numbered launchers, or the game itself for Join.</summary>
static class GameStart
{
    /// <summary>"2 - Play.cmd": it starts the game and returns, so it runs without a console window.</summary>
    public static void Play(string versionDir) => RunCmd(versionDir, 2, visible: false);
    /// <summary>"3 - Host a match on this PC.cmd": the server's console window stays open while it runs.</summary>
    public static void Host(string versionDir) => RunCmd(versionDir, 3, visible: true);
    /// <summary>"5 - Host a match for friends (LAN).cmd".</summary>
    public static void HostLan(string versionDir) => RunCmd(versionDir, 5, visible: true);

    /// <summary>Join: the game with --connect, as "4 - Join a server by address.cmd" starts it (that script asks in a console; the launcher asks in its window).</summary>
    public static void Join(string versionDir, string address)
    {
        string game = Path.Combine(versionDir, PackageLayout.GameExe);
        if (!File.Exists(game)) throw new FileNotFoundException("The game is missing from the installed build; use Check files.");
        var psi = new ProcessStartInfo(game) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(game)! };
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--connect");
        psi.ArgumentList.Add(address);
        Process.Start(psi)?.Dispose();
    }

    static void RunCmd(string versionDir, int number, bool visible)
    {
        string script = PackageLayout.EntryPoint(versionDir, number) ?? throw new FileNotFoundException($"The installed build has no \"{number} - ...cmd\"; use Check files.");
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = !visible,
            WorkingDirectory = versionDir,
            Arguments = $"/d /c \"\"{script}\"\"",
        };
        Process.Start(psi)?.Dispose();
    }

    /// <summary>The game or a server of this install is running (used before clearing caches).</summary>
    public static bool AnythingRunning(string root)
    {
        foreach (var name in new[] { "sm-reimpl", "sm-reimpl.console", "SmServer" })
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    try { if (p.MainModule?.FileName is { } f && Updater.IsInsideFolder(f, root)) return true; }
                    catch (Exception) { return true; }      // cannot tell: assume it is ours
                }
            }
        return false;
    }

    public static void OpenUrl(string url)
    {
        // Only GitHub pages are opened (the release page, the issues tab).
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host != "github.com") return;
        Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose();
    }

    public static void OpenFolder(string folder)
    {
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder }, UseShellExecute = false })?.Dispose();
    }
}
