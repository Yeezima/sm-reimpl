using System.IO;
using SmClient.Customizer.Model;
using Xunit;

namespace SmFormats.Tests;

/// <summary>ui/menu-rebuild: the Options menu's saved settings, their limits, and the rules the sound and look code read. Engine-free.</summary>
public sealed class GameOptionsTests
{
    [Fact]
    public void Defaults_leave_the_game_as_it_was()
    {
        var o = GameOptions.Default;
        Assert.Equal(1f, o.RenderScale);
        Assert.Equal(1f, o.Brightness);
        Assert.Equal(0, o.FpsCap);
        Assert.True(o.Vsync);
        Assert.Equal(WindowChoice.Windowed, o.Window);
        Assert.Equal(AaChoice.Default, o.Aa);
        Assert.Equal(ShadowChoice.Default, o.Shadows);
        Assert.Equal(0f, GameOptions.VolumeDb(1f));
    }

    [Fact]
    public void F11_toggles_between_the_window_and_the_last_fullscreen_choice()
    {
        var window = GameOptions.Default with { WindowWidth = 1600, WindowHeight = 900, FpsCap = 60 };
        var full = window.WithFullscreenToggled(WindowChoice.Fullscreen);
        Assert.Equal(WindowChoice.Fullscreen, full.Window);
        Assert.Equal(window with { Window = WindowChoice.Fullscreen }, full);   // nothing but the mode changes
        Assert.Equal(window, full.WithFullscreenToggled(WindowChoice.Fullscreen));
        Assert.Equal(WindowChoice.Borderless, window.WithFullscreenToggled(WindowChoice.Borderless).Window);
        Assert.Equal(WindowChoice.Windowed, (window with { Window = WindowChoice.Borderless }).WithFullscreenToggled(WindowChoice.Fullscreen).Window);
        Assert.Equal(WindowChoice.Fullscreen, window.WithFullscreenToggled(WindowChoice.Windowed).Window);   // no usable memory: fullscreen
    }

    [Fact]
    public void Options_round_trip_through_frontend_json()
    {
        string dir = Directory.CreateTempSubdirectory("sm-options").FullName;
        try
        {
            var options = new GameOptions(1600, 900, WindowChoice.Borderless, false, 144, 0.75f, ShadowChoice.High, AaChoice.Taa, 1.25f, 0.5f, 0.25f, 0.75f, 0f, true, true, 0.5f, 2f, true);
            var settings = FrontEndSettings.Default with { Options = options };
            Assert.Null(settings.Save(dir));
            Assert.Equal(options, FrontEndSettings.Load(dir).Options);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void A_file_without_options_reads_the_defaults()
    {
        string dir = Directory.CreateTempSubdirectory("sm-options").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, FrontEndSettings.FileName), "{\"host\":\"127.0.0.1:27015\"}");
            Assert.Equal(GameOptions.Default, FrontEndSettings.Load(dir).Options);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Wrong_values_are_limited()
    {
        var o = new GameOptions(WindowWidth: 5, FpsCap: 7, RenderScale: 9f, Brightness: float.NaN, MasterVolume: -2f, AimSensitivity: 100f).Clamped();
        Assert.Equal(0, o.WindowWidth);
        Assert.Equal(0, o.FpsCap);
        Assert.Equal(1f, o.RenderScale);
        Assert.Equal(1f, o.Brightness);
        Assert.Equal(0f, o.MasterVolume);
        Assert.Equal(GameOptions.MaxLook, o.AimSensitivity);
    }

    [Fact]
    public void Sounds_follow_their_slider()
    {
        Assert.Equal(SoundCategory.Voice, GameOptions.CategoryOf("speech/combat/hit"));
        Assert.Equal(SoundCategory.Music, GameOptions.CategoryOf("play_mp_sm_victory"));
        Assert.Equal(SoundCategory.Music, GameOptions.CategoryOf("play_mp_chaos_loss"));
        Assert.Equal(SoundCategory.Music, GameOptions.CategoryOf("play_mp_countdown"));
        Assert.Equal(SoundCategory.Effects, GameOptions.CategoryOf("play_bolter_fire"));
        Assert.Equal(-80f, GameOptions.VolumeDb(0f));
    }

    [Fact]
    public void Join_server_has_one_fixed_address()
    {
        Assert.Equal("64.177.51.188:27015", JoinTargets.PublicServer);
        Assert.Null(FrontEndSettings.ValidateHost(JoinTargets.PublicServer));
    }
}
