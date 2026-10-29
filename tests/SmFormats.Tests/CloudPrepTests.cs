using SmContent;
using SmFormats;

namespace SmFormats.Tests;

/// <summary>Server preparation (Linux and containers): SM_DATA_DIR and the OS-aware case rule of InstallPaths.IsWithin.</summary>
public class CloudPrepTests
{
    [Fact]
    public void DataRoot_defaults_to_the_per_user_application_data_folder()
    {
        string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl");
        Assert.Equal(expected, GameInstall.DataRootFor(_ => null));
        Assert.Equal(expected, GameInstall.DataRootFor(_ => ""));
        Assert.Equal(expected, GameInstall.DataRootFor(_ => "   "));
    }

    [Fact]
    public void SM_DATA_DIR_moves_the_data_root_and_everything_below_it()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-data-" + Guid.NewGuid().ToString("N"));
        string full = Path.GetFullPath(dir);
        string root = GameInstall.DataRootFor(k => k == "SM_DATA_DIR" ? dir : null);
        Assert.Equal(full, root);
        Assert.NotEqual(GameInstall.DataRootFor(_ => null), root);

        // The caches are built from DataRoot, so they move with it (the process environment is not changed here: other tests run in parallel).
        string now = GameInstall.DataRoot;
        Assert.Equal(Path.Combine(now, "cache"), AudioCache.Root);
        Assert.Equal(Path.Combine(now, "cache", "audio"), AudioCache.DecodedDir);
        Assert.Equal(Path.Combine(now, "cache", "audio-codebooks"), AudioCache.CodebookDir);
        Assert.Equal(Path.Combine(now, "cache", "nav"), BotNavigation.CacheDirectory);
        Assert.False(Directory.Exists(dir));                                                   // reading the path creates nothing
    }

    [Fact]
    public void A_relative_SM_DATA_DIR_is_made_absolute()
    {
        string root = GameInstall.DataRootFor(k => k == "SM_DATA_DIR" ? "relative-data" : null);
        Assert.True(Path.IsPathRooted(root));
        Assert.Equal("relative-data", Path.GetFileName(root));
    }

    [Fact]
    public void IsWithin_ignores_case_with_the_Windows_and_macOS_rule_and_not_with_the_Linux_rule()
    {
        string tmp = Path.GetTempPath();
        string dir = Path.Combine(tmp, "SmGame");
        string below = Path.Combine(tmp, "smgame", "Data", "x.oppc");
        Assert.True(InstallPaths.IsWithin(below, dir, StringComparison.OrdinalIgnoreCase));
        Assert.False(InstallPaths.IsWithin(below, dir, StringComparison.Ordinal));
        Assert.True(InstallPaths.IsWithin(Path.Combine(dir, "Data"), dir, StringComparison.Ordinal));
        Assert.True(InstallPaths.IsWithin(dir + Path.DirectorySeparatorChar, dir, StringComparison.Ordinal));
        Assert.False(InstallPaths.IsWithin(dir + "X", dir, StringComparison.OrdinalIgnoreCase));     // separator-aware under both rules
        Assert.False(InstallPaths.IsWithin(dir + "X", dir, StringComparison.Ordinal));
    }

    [Fact]
    public void IsWithinOsRule_uses_the_rule_of_the_running_OS()
    {
        bool insensitive = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        Assert.Equal(insensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal, InstallPaths.PathComparison);
        string tmp = Path.GetTempPath();
        string dir = Path.Combine(tmp, "SmGame");
        string below = Path.Combine(tmp, "smgame", "x");
        Assert.Equal(insensitive, InstallPaths.IsWithinOsRule(below, dir));
        Assert.True(InstallPaths.IsWithinOsRule(Path.Combine(dir, "x"), dir));
    }

    [Fact]
    public void The_protective_guards_ignore_case_on_every_OS_so_they_fail_closed()
    {
        string tmp = Path.GetTempPath();
        string install = Path.Combine(tmp, "SmGame");
        string sameFolderOtherCase = Path.Combine(tmp, "smgame", "Audio");
        Assert.True(InstallPaths.IsWithin(sameFolderOtherCase, install));                     // a case-insensitive mount on Linux
        Assert.True(InstallPaths.Overlaps(sameFolderOtherCase, install));
        Assert.True(InstallPaths.Overlaps(install, sameFolderOtherCase));
        Assert.False(InstallPaths.Overlaps(install + "X", install));                           // still separator-aware

        // the decoded-audio guard (AudioCache.RefuseOutputDir) refuses the install and the repository in any case spelling
        Assert.NotNull(AudioCache.RefuseOutputDir(sameFolderOtherCase, install, null));
        Assert.NotNull(AudioCache.RefuseOutputDir(sameFolderOtherCase, null, install));
        Assert.Null(AudioCache.RefuseOutputDir(Path.Combine(tmp, "elsewhere"), install, install));
    }
}
