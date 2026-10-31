using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using SmLauncher.Core;

namespace SmLauncher;

/// <summary>
/// SM Reimpl Launcher. Without arguments: the window (update check, Update, Play / Host / Join, Check files, Roll back, ...).
///   --root &lt;dir&gt;           the install folder to use (default: the folder this launcher lives in when it is an install, else the remembered one,
///                           else %LOCALAPPDATA%\SM Reimpl; the first run asks)
///   --selftest              headless: checks the build this launcher came with (or the active version of --root) against its file list, no network,
///                           writes nothing; exit 0 = ok (the release smoke test runs it)
///   headless commands (need --root; for tests and support): --status, --check, --install-latest, --resume, --verify, --rollback, --adopt &lt;dir&gt;,
///   --update-launcher (puts the active build's launcher in the install folder when it is newer than this one)
///   test aids of the window: --screenshot &lt;png&gt; [--screenshot-after &lt;s&gt;] (the window is drawn off-screen into the file once the update check
///   is done, or after s seconds, then it closes), --auto-update (presses Update once the check is done), --no-shortcuts (the first install
///   creates no desktop or Start menu shortcut), --offline (no update check: the release smoke test), --auto-update-launcher (takes the active
///   build's newer launcher at once; with --screenshot the new launcher draws &lt;png&gt;.after-update.png)
///   internal: --install-self &lt;target&gt; --root &lt;root&gt; --wait-pid &lt;pid&gt; --wait-start &lt;filetime&gt; [--window] [--forward-root] (the verified new
///   launcher replacing the old one once it has exited; SmLauncher.Core.SelfUpdateHelper), --self-updated &lt;backup&gt; (the new window: signals once
///   its first update check is done, then removes the backup if it is "&lt;exe name&gt;*.old" beside it), --launcher-update-failed (the old launcher,
///   put back: says so), --selftest --rules (the light self-test the helper runs: rule checks only)
/// Network: only api.github.com and GitHub's download hosts, over HTTPS. Never needs administrator rights, binds no port.
/// </summary>
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var a = Args.Parse(args);
        // A crash leaves a sign beside the exe, so a self-update helper waiting for this launcher's first update check knows to put the old one back.
        AppDomain.CurrentDomain.UnhandledException += (_, _) =>
        {
            try { if (LauncherInfo.ExePath is { } exe) File.WriteAllText(SelfUpdateHelper.CrashMarker(exe), DateTime.UtcNow.ToString("O")); } catch (Exception) { }
        };
        // Test aid of the self-update's failure path: a new launcher that dies at once after an update (exit 3), as a broken build would.
        if (a.Has("self-updated") && Environment.GetEnvironmentVariable("SM_LAUNCHER_TEST_FAIL_AFTER_UPDATE") == "1") return 3;
        if (a.IsHeadless)
        {
            ConsoleOut.Attach();
            try { return Cli.Run(a); }
            catch (Exception e) { Console.Error.WriteLine("error: " + e.Message); return 1; }
        }
        if (TryHandOff(a, args)) return 0;
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => DarkBox.Show(e.Exception.Message, "SM Reimpl Launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm(a));
        return Environment.ExitCode;
    }

    /// <summary>
    /// A launcher that is not the install folder's one (an old unpacked zip) starts the installed launcher, or the verified one of the active build, when
    /// that is newer, with the same arguments, and exits (<see cref="LauncherHandoff"/>). True when it did.
    /// </summary>
    static bool TryHandOff(Args a, string[] args)
    {
        try
        {
            if (LauncherHandoff.IsTestOrHelperRun(a.Values.Keys)) return false;
            string? root = RootFinder.Find(a);
            if (root is null || LauncherInfo.ExePath is not { } me) return false;
            var layout = new InstallLayout(root);
            string installed = layout.LauncherExe;
            string? bundled = null;
            if (!SelfUpdate.IsRunning(installed)) try { bundled = LauncherVersions.NewerBundled(new Updater(layout, null).CurrentDir(), LauncherVersions.Of(installed) ?? new Version(0, 0)); } catch (Exception) { }
            string? target = LauncherHandoff.Decide(me, LauncherInfo.Version, false, installed, LauncherVersions.Of(installed), bundled, bundled is null ? null : LauncherVersions.Of(bundled));
            if (target is null) return false;
            return SelfUpdate.StartInstalled(target, args);
        }
        catch (Exception) { return false; }
    }
}

/// <summary>The launcher's own version: release.ps1 sets it from the build commit's time (yyyy.MMdd.minute); a local build is 1.0.0.</summary>
static class LauncherInfo
{
    public static Version Version => typeof(Program).Assembly.GetName().Version ?? new Version(1, 0, 0, 0);
    public static string VersionText
    {
        get
        {
            string? info = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            string v = $"{Version.Major}.{Version.Minor}.{Version.Build}";
            return info is not null && info.Contains('|') ? $"{v} ({info.Split('|')[0]})" : v;
        }
    }
    public static string? ExePath => Environment.ProcessPath;
    public static string ExeDir => AppContext.BaseDirectory.TrimEnd('\\');
}

sealed class Args
{
    public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> WithValue = new(StringComparer.OrdinalIgnoreCase) { "root", "adopt", "screenshot", "screenshot-after", "self-updated", "install-self", "wait-pid", "wait-start", "ui-scale", "ui-workarea", "ui-state", "layout-check", "row-check" };
    static readonly string[] Commands = ["status", "check", "install-latest", "resume", "verify", "rollback", "adopt", "recover", "update-launcher", "install-self"];

    public static Args Parse(string[] args)
    {
        var a = new Args();
        for (int i = 0; i < args.Length; i++)
        {
            string s = args[i];
            if (!s.StartsWith("--", StringComparison.Ordinal)) continue;
            string key = s[2..];
            string? value = null;
            if (WithValue.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) value = args[++i];   // a flag is never a value
            a.Values[key] = value;
        }
        return a;
    }

    public bool Has(string key) => Values.ContainsKey(key);
    public string? this[string key] => Values.GetValueOrDefault(key);
    public string? Command => Commands.FirstOrDefault(Has);
    public bool IsHeadless => Has("selftest") || Has("layout-check") || Has("row-check") || Command is not null;
}

/// <summary>Where the install folder is (see <see cref="Program"/>).</summary>
static class RootFinder
{
    const string RegistryKey = @"Software\SM Reimpl Launcher";

    public static string? Find(Args a)
    {
        if (a["root"] is { } r) return Path.GetFullPath(r);
        string exeDir = LauncherInfo.ExeDir;
        if (InstallLayout.LooksLikeRoot(exeDir)) return exeDir;
        // a launcher started from inside an installed version (versions\<tag>\0 - SM Reimpl Launcher.exe)
        var parent = Directory.GetParent(exeDir);
        if (parent is { Name: "versions" } && parent.Parent is { } grand && InstallLayout.LooksLikeRoot(grand.FullName)) return grand.FullName;
        if (Remembered() is { } remembered && InstallLayout.LooksLikeRoot(remembered)) return remembered;
        if (InstallLayout.LooksLikeRoot(InstallLayout.DefaultRoot)) return InstallLayout.DefaultRoot;
        return null;
    }

    static string? Remembered()
    {
        try { using var k = Registry.CurrentUser.OpenSubKey(RegistryKey); return k?.GetValue("InstallRoot") as string; }
        catch (Exception) { return null; }
    }

    /// <summary>Remembers a chosen install folder other than the default (HKCU only; never with --root).</summary>
    public static void Remember(string root)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(root), InstallLayout.DefaultRoot, StringComparison.OrdinalIgnoreCase)) { Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, false); return; }
            using var k = Registry.CurrentUser.CreateSubKey(RegistryKey);
            k.SetValue("InstallRoot", Path.GetFullPath(root));
        }
        catch (Exception) { }
    }

    /// <summary>The unpacked playtest folder this launcher was started from (it has notes\VERSION.txt and the game), unless that is an installed version.</summary>
    public static string? AdoptCandidate()
    {
        string exeDir = LauncherInfo.ExeDir;
        if (!File.Exists(Path.Combine(exeDir, PackageLayout.VersionPath)) || !File.Exists(Path.Combine(exeDir, PackageLayout.GameExe))) return null;
        var parent = Directory.GetParent(exeDir);
        if (parent is { Name: "versions" } && parent.Parent is { } grand && InstallLayout.LooksLikeRoot(grand.FullName)) return null;
        return exeDir;
    }
}

/// <summary>A GUI program's output for the headless modes: the console it was started from, or the redirected handles the caller gave it.</summary>
static class ConsoleOut
{
    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern nint GetStdHandle(int which);

    public static void Attach()
    {
        nint h = GetStdHandle(-11);
        if (h == 0 || h == -1) AttachConsole(-1);
    }
}

/// <summary>The launcher's log (install folder\logs\launcher.log), kept under 1 MB.</summary>
sealed class AppLog(InstallLayout layout)
{
    readonly object _gate = new();

    public void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(layout.LogFile)!);
                var fi = new FileInfo(layout.LogFile);
                if (fi.Exists && fi.Length > 1 << 20) File.Move(layout.LogFile, layout.LogFile + ".1", overwrite: true);
                File.AppendAllText(layout.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }
}
