using SmLauncher.Core;
using Xunit;

namespace SmLauncher.Tests;

public class LauncherHandoffTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "smlh-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _installed, _zip, _bundled;

    public LauncherHandoffTests()
    {
        Directory.CreateDirectory(_dir);
        _installed = Path.Combine(_dir, "SM Reimpl Launcher.exe");
        _zip = Path.Combine(_dir, "0 - SM Reimpl Launcher.exe");
        _bundled = Path.Combine(_dir, "bundled.exe");
        foreach (var f in new[] { _installed, _zip, _bundled }) File.WriteAllText(f, "x");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    static Version V(string s) => Version.Parse(s);

    [Fact]
    public void OlderRunningCopyHandsOverToTheInstalledLauncher() =>
        Assert.Equal(_installed, LauncherHandoff.Decide(_zip, V("2026.1030.991"), false, _installed, V("2026.1031.129"), null, null));

    [Fact]
    public void SameOrNewerRunningCopyStays()
    {
        Assert.Null(LauncherHandoff.Decide(_zip, V("2026.1031.129"), false, _installed, V("2026.1031.129"), null, null));
        Assert.Null(LauncherHandoff.Decide(_zip, V("2026.1031.200"), false, _installed, V("2026.1031.129"), null, null));
    }

    [Fact]
    public void TheInstalledLauncherNeverHandsOverToItself() =>
        Assert.Null(LauncherHandoff.Decide(_installed, V("2026.1031.129"), false, _installed, V("2026.1031.129"), null, null));

    [Fact]
    public void TheNewestOfInstalledAndBundledWins()
    {
        Assert.Equal(_bundled, LauncherHandoff.Decide(_zip, V("1.0.0"), false, _installed, V("2.0.0"), _bundled, V("3.0.0")));
        Assert.Equal(_installed, LauncherHandoff.Decide(_zip, V("1.0.0"), false, _installed, V("3.0.0"), _bundled, V("2.0.0")));
    }

    [Fact]
    public void MissingInstalledFileOrUnknownVersionsMeanNoHandOver()
    {
        Assert.Null(LauncherHandoff.Decide(_zip, V("1.0.0"), false, Path.Combine(_dir, "none.exe"), V("9.0.0"), null, null));
        Assert.Null(LauncherHandoff.Decide(_zip, V("1.0.0"), false, _installed, null, null, null));
        Assert.Null(LauncherHandoff.Decide(_zip, null, false, _installed, V("9.0.0"), null, null));
    }

    [Theory]
    [InlineData("selftest")]
    [InlineData("layout-check")]
    [InlineData("root")]
    [InlineData("screenshot")]
    [InlineData("install-self")]
    [InlineData("self-updated")]
    [InlineData("handed-off")]
    public void TestHelperAndAlreadyHandedOffRunsNeverHandOver(string flag)
    {
        Assert.True(LauncherHandoff.IsTestOrHelperRun([flag]));
        Assert.Null(LauncherHandoff.Decide(_zip, V("1.0.0"), LauncherHandoff.IsTestOrHelperRun([flag]), _installed, V("9.0.0"), null, null));
    }

    [Fact]
    public void PlainRunIsNotATestRun() => Assert.False(LauncherHandoff.IsTestOrHelperRun(["no-shortcuts"]));

    [Fact]
    public void TheStartedLauncherCarriesTheLoopGuard()
    {
        var args = LauncherHandoff.ArgumentsFor(["--foo", "bar"]);
        Assert.Equal(["--foo", "bar", "--handed-off"], args);
        Assert.True(LauncherHandoff.IsTestOrHelperRun(args.Where(a => a.StartsWith("--")).Select(a => a[2..])));
    }

    [Fact]
    public void AfterAnUpdateInstalledANewerLauncherTheOldCopyRelaunches()
    {
        // an old zip copy installed a newer launcher into the install folder: the decision flips from "stay" to "hand over"
        Assert.Null(LauncherHandoff.Decide(_zip, V("2026.1030.991"), false, _installed, V("2026.1030.991"), null, null));
        Assert.Equal(_installed, LauncherHandoff.Decide(_zip, V("2026.1030.991"), false, _installed, V("2026.1031.129"), null, null));
    }
}
