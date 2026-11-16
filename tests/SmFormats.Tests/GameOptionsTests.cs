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
        Assert.Equal(1f, o.CameraShake);     // combat feel (2026-11-09): the game's own shake and flashes by default
        Assert.Equal(1f, o.ScreenFlashes);
    }

    [Fact]
    public void Camera_shake_and_screen_flashes_save_snap_and_clamp()
    {
        string dir = Directory.CreateTempSubdirectory("sm-options").FullName;
        try
        {
            var options = GameOptions.Default with { CameraShake = 0.35f, ScreenFlashes = 0f };
            var settings = FrontEndSettings.Default with { Options = options };
            Assert.Null(settings.Save(dir));
            var back = FrontEndSettings.Load(dir).Options;
            Assert.Equal(0.35f, back.CameraShake, 3);
            Assert.Equal(0f, back.ScreenFlashes);
        }
        finally { Directory.Delete(dir, true); }
        var wild = (GameOptions.Default with { CameraShake = 7f, ScreenFlashes = float.NaN }).Clamped();
        Assert.Equal(1f, wild.CameraShake);
        Assert.Equal(1f, wild.ScreenFlashes);
        Assert.Equal(0.35f, (GameOptions.Default with { CameraShake = 0.36f }).Clamped().CameraShake, 3);
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

    [Fact]
    public void Performance_readout_is_off_by_default_saves_and_rejects_an_unknown_choice()
    {
        // polish pass P1: F3 cycles the readout (no Options row); the choice is saved as performanceReadout and a damaged file falls back to OFF.
        Assert.Equal(ReadoutChoice.Off, GameOptions.Default.Readout);
        Assert.Equal(ReadoutChoice.Off, GameOptions.Read(null).Readout);
        var o = GameOptions.Default with { Readout = ReadoutChoice.Network };
        Assert.Equal("Network", (string?)o.Write()["performanceReadout"]);
        Assert.Equal(ReadoutChoice.Network, GameOptions.Read(o.Write()).Readout);
        Assert.Equal(ReadoutChoice.Off, GameOptions.Read(new System.Text.Json.Nodes.JsonObject { ["performanceReadout"] = "Sideways" }).Readout);
        Assert.Equal(ReadoutChoice.Off, (GameOptions.Default with { Readout = (ReadoutChoice)9 }).Clamped().Readout);
    }

    [Fact]
    public void Pad_aim_sensitivity_defaults_to_the_old_look_saves_clamps_and_applies_only_while_aiming()
    {
        Assert.Equal(1f, GameOptions.Default.PadAimSensitivity);
        Assert.Equal(1f, GameOptions.Read(null).PadAimSensitivity);
        var o = GameOptions.Default with { PadLookSensitivity = 2f, PadAimSensitivity = 0.5f };
        Assert.Equal(0.5f, GameOptions.Read(o.Write()).PadAimSensitivity, 3);
        Assert.Equal(GameOptions.MaxLook, (GameOptions.Default with { PadAimSensitivity = 99f }).Clamped().PadAimSensitivity);
        var before = LiveOptions.Current;
        try
        {
            LiveOptions.Current = o;
            Assert.Equal(2f, LiveOptions.PadLookAim(false), 3);
            Assert.Equal(1f, LiveOptions.PadLookAim(true), 3);
            LiveOptions.Current = GameOptions.Default;
            Assert.Equal(LiveOptions.PadLook, LiveOptions.PadLookAim(true), 3);   // the default turns as before
        }
        finally { LiveOptions.Current = before; }
    }
}
