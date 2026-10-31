namespace SmLauncher.Core;

/// <summary>
/// Which launcher should be the one on screen. A launcher started from an old unpacked zip (or any copy outside the install folder) hands over to the
/// install folder's launcher, or to the verified launcher of the active build, when that one is newer; it starts it with the same arguments and exits.
/// Never in the headless and test modes, never twice (the started launcher carries <see cref="LoopGuardFlag"/>), never to an older or equal launcher.
/// </summary>
public static class LauncherHandoff
{
    /// <summary>Added to the arguments of the launcher that was started by a hand-over: it never hands over again.</summary>
    public const string LoopGuardFlag = "--handed-off";

    /// <summary>Command-line flags that make a run a test or a helper run: it stays where it is.</summary>
    static readonly string[] NeverFlags =
    [
        "selftest", "layout-check", "root", "screenshot", "ui-state", "ui-scale", "ui-workarea", "ui-check", "offline", "auto-update", "auto-update-launcher",
        "install-self", "self-updated", "launcher-update-failed", "handed-off", "adopt",
    ];

    public static bool IsTestOrHelperRun(IEnumerable<string> flagNames) =>
        flagNames.Any(f => NeverFlags.Contains(f, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The launcher to hand over to, or null to stay. <paramref name="installed"/> is the install folder's launcher and <paramref name="bundled"/> the
    /// active build's verified launcher (null when absent, unverified or not newer than the installed one).
    /// </summary>
    public static string? Decide(string? runningPath, Version? runningVersion, bool testOrHelperRun, string? installedPath, Version? installedVersion, string? bundledPath, Version? bundledVersion)
    {
        if (testOrHelperRun || runningPath is null || runningVersion is null) return null;
        string? best = null;
        Version bestVersion = LauncherVersions.Normalise(runningVersion);
        foreach (var (path, version) in new[] { (installedPath, installedVersion), (bundledPath, bundledVersion) })
        {
            if (path is null || version is null || !File.Exists(path) || SamePath(path, runningPath)) continue;
            if (LauncherVersions.Normalise(version) > bestVersion) { best = path; bestVersion = LauncherVersions.Normalise(version); }
        }
        return best;
    }

    /// <summary>Same arguments for the launcher that takes over, plus the loop guard.</summary>
    public static IReadOnlyList<string> ArgumentsFor(IEnumerable<string> original) => [.. original, LoopGuardFlag];

    static bool SamePath(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }
}
