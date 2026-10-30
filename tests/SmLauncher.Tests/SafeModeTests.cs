using SmLauncher.Core;

namespace SmLauncher.Tests;

/// <summary>hotfix/old-gpu: the launcher's "Old GPU / safe mode" rules and its saved setting.</summary>
public class SafeModeTests
{
    [Fact]
    public void Safe_mode_adds_the_compatibility_renderer_before_the_separator_and_the_flag_after()
    {
        Assert.Equal(["--rendering-method", "gl_compatibility", "--rendering-driver", "opengl3"], SafeModePolicy.EngineArgs);
        Assert.Equal(["--safe-mode"], SafeModePolicy.ExtraGameArgs(true));
        Assert.Empty(SafeModePolicy.ExtraGameArgs(false));
    }

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("--headless", false, "--headless")]
    [InlineData(null, true, "--rendering-method gl_compatibility --rendering-driver opengl3")]
    [InlineData(" --headless ", true, "--headless --rendering-method gl_compatibility --rendering-driver opengl3")]
    public void The_engine_variable_keeps_a_testers_own_options(string? existing, bool safe, string? expected) =>
        Assert.Equal(expected, SafeModePolicy.EngineArgsText(existing, safe));

    [Theory]
    [InlineData(false, false, 1, 2, true)]       // failed after 2 s: retry
    [InlineData(false, false, -1, 8, true)]      // a crash code at the edge of the window
    [InlineData(false, false, 0, 2, false)]      // a clean exit is not a failure
    [InlineData(false, false, 1, 9, false)]       // it ran long enough to be a different problem
    [InlineData(true, false, 1, 2, false)]       // already in safe mode
    [InlineData(false, true, 1, 2, false)]       // already retried once: no loop
    public void An_early_failure_retries_once_in_safe_mode(bool safeWas, bool retried, int exit, double seconds, bool expected) =>
        Assert.Equal(expected, SafeModePolicy.ShouldRetryInSafeMode(safeWas, retried, exit, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_choice_is_saved_with_the_launcher_settings_and_defaults_to_off()
    {
        using var t = new TempDir();
        var layout = new InstallLayout(t.Path);
        Assert.False(Store.ReadSettings(layout).SafeMode);
        var s = Store.ReadSettings(layout);
        s.LastServer = "127.0.0.1:27025";
        s.SafeMode = true;
        Store.WriteSettings(layout, s);
        var back = Store.ReadSettings(layout);
        Assert.True(back.SafeMode);
        Assert.Equal("127.0.0.1:27025", back.LastServer);
    }

    [Fact]
    public void Settings_written_before_the_option_existed_still_load()
    {
        using var t = new TempDir();
        var layout = new InstallLayout(t.Path);
        Directory.CreateDirectory(t.Path);
        File.WriteAllText(layout.SettingsFile, "{ \"lastServer\": \"1.2.3.4:5\" }");
        var s = Store.ReadSettings(layout);
        Assert.False(s.SafeMode);
        Assert.Equal("1.2.3.4:5", s.LastServer);
    }
}
