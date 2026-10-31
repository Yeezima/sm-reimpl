using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace SmClient.Customizer.Model;

/// <summary>How the game window is shown: a normal window, borderless over the desktop, or exclusive fullscreen.</summary>
public enum WindowChoice { Windowed, Borderless, Fullscreen }

/// <summary>The anti-aliasing choice of the Options menu; <see cref="Default"/> leaves <c>--aa</c> (and its default) alone.</summary>
public enum AaChoice { Default, Off, Msaa, MsaaSmaa, Taa }

/// <summary>The shadow choice of the Options menu; <see cref="Default"/> keeps what the lighting preset asks for.</summary>
public enum ShadowChoice { Default, Low, Medium, High }

/// <summary>Which volume slider a sound follows.</summary>
public enum SoundCategory { Effects, Music, Voice }

/// <summary>
/// The Options menu's settings (ui/menu-rebuild, client only: ours; no simulation, protocol or config-hash meaning): saved in <c>frontend.json</c> under <c>"options"</c>. Every default is
/// the behaviour the client had without the menu, so a player who changes nothing plays as before. Engine-free; <c>OptionsHost</c> applies them to Godot.
/// </summary>
public sealed record GameOptions(
    int WindowWidth = 0, int WindowHeight = 0, WindowChoice Window = WindowChoice.Windowed, bool Vsync = true, int FpsCap = 0, float RenderScale = 1f,
    ShadowChoice Shadows = ShadowChoice.Default, AaChoice Aa = AaChoice.Default, float Brightness = 1f,
    float MasterVolume = 1f, float MusicVolume = 1f, float EffectsVolume = 1f, float VoiceVolume = 1f, bool MuteUnfocused = false,
    bool InvertY = false, float AimSensitivity = 1f, float PadLookSensitivity = 1f, bool InvertPadY = false)
{
    public static GameOptions Default => new();

    public static readonly int[] FpsCaps = [0, 30, 60, 90, 120, 144, 165, 240];
    public static readonly (int Width, int Height)[] Resolutions = [(1280, 720), (1366, 768), (1600, 900), (1920, 1080), (2560, 1440), (3840, 2160)];

    public const float MinRenderScale = 0.5f, MaxRenderScale = 1f, RenderScaleStep = 0.05f;
    public const float MinBrightness = 0.5f, MaxBrightness = 1.5f, BrightnessStep = 0.05f;
    public const float VolumeStep = 0.05f;
    public const float MinLook = 0.25f, MaxLook = 3f, LookStep = 0.05f;

    /// <summary>A value limited to its range and snapped to its step; a non-finite one is <paramref name="fallback"/>.</summary>
    public static float Snap(float value, float min, float max, float step, float fallback)
    {
        if (!float.IsFinite(value)) return fallback;
        float v = MathF.Round(Math.Clamp(value, min, max) / step) * step;
        return MathF.Round(Math.Clamp(v, min, max) * 1000f) / 1000f;
    }

    /// <summary>The options with every value in its range (what a damaged file or a hand edit gets).</summary>
    public GameOptions Clamped() => this with
    {
        WindowWidth = WindowWidth is >= 640 and <= 15360 ? WindowWidth : 0, WindowHeight = WindowHeight is >= 480 and <= 8640 ? WindowHeight : 0,
        FpsCap = FpsCaps.Contains(FpsCap) ? FpsCap : 0,
        RenderScale = Snap(RenderScale, MinRenderScale, MaxRenderScale, RenderScaleStep, 1f), Brightness = Snap(Brightness, MinBrightness, MaxBrightness, BrightnessStep, 1f),
        MasterVolume = Snap(MasterVolume, 0f, 1f, VolumeStep, 1f), MusicVolume = Snap(MusicVolume, 0f, 1f, VolumeStep, 1f),
        EffectsVolume = Snap(EffectsVolume, 0f, 1f, VolumeStep, 1f), VoiceVolume = Snap(VoiceVolume, 0f, 1f, VolumeStep, 1f),
        AimSensitivity = Snap(AimSensitivity, MinLook, MaxLook, LookStep, 1f), PadLookSensitivity = Snap(PadLookSensitivity, MinLook, MaxLook, LookStep, 1f),
    };

    /// <summary>Reads the <c>"options"</c> object of <c>frontend.json</c>; missing or wrong values are their defaults.</summary>
    public static GameOptions Read(JsonNode? node)
    {
        if (node is not JsonObject o) return Default;
        float F(string name, float fallback) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? (float)d : fallback;
        int I(string name) => o[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        bool B(string name, bool fallback) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : fallback;
        T E<T>(string name, T fallback) where T : struct, Enum => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && Enum.TryParse<T>(s, true, out var e) && Enum.IsDefined(e) ? e : fallback;
        return new GameOptions(I("windowWidth"), I("windowHeight"), E("window", WindowChoice.Windowed), B("vsync", true), I("fpsCap"), F("renderScale", 1f), E("shadows", ShadowChoice.Default), E("antiAliasing", AaChoice.Default),
            F("brightness", 1f), F("masterVolume", 1f), F("musicVolume", 1f), F("effectsVolume", 1f), F("voiceVolume", 1f), B("muteUnfocused", false),
            B("invertY", false), F("aimSensitivity", 1f), F("padLookSensitivity", 1f), B("invertPadY", false)).Clamped();
    }

    /// <summary>The object saved in <c>frontend.json</c>.</summary>
    public JsonObject Write() => new()
    {
        ["windowWidth"] = WindowWidth, ["windowHeight"] = WindowHeight, ["window"] = Window.ToString(), ["vsync"] = Vsync, ["fpsCap"] = FpsCap, ["renderScale"] = Math.Round(RenderScale, 3),
        ["shadows"] = Shadows.ToString(), ["antiAliasing"] = Aa.ToString(), ["brightness"] = Math.Round(Brightness, 3),
        ["masterVolume"] = Math.Round(MasterVolume, 3), ["musicVolume"] = Math.Round(MusicVolume, 3), ["effectsVolume"] = Math.Round(EffectsVolume, 3), ["voiceVolume"] = Math.Round(VoiceVolume, 3), ["muteUnfocused"] = MuteUnfocused,
        ["invertY"] = InvertY, ["aimSensitivity"] = Math.Round(AimSensitivity, 3), ["padLookSensitivity"] = Math.Round(PadLookSensitivity, 3), ["invertPadY"] = InvertPadY,
    };

    /// <summary>A slider value (0 to 1) as decibels: 1 is 0 dB, 0 is silence (-80 dB).</summary>
    public static float VolumeDb(float linear) => linear <= 0.0005f ? -80f : Math.Max(-80f, 20f * MathF.Log10(linear));

    /// <summary>The slider a posted sound event follows: speech is voice, the match's countdown and result stingers are the game's music (its only music in a match), the rest effects.</summary>
    public static SoundCategory CategoryOf(string soundEvent)
    {
        if (soundEvent.StartsWith("speech/", StringComparison.Ordinal)) return SoundCategory.Voice;
        return soundEvent.StartsWith("play_mp_", StringComparison.OrdinalIgnoreCase)
               && (soundEvent.Contains("victory", StringComparison.OrdinalIgnoreCase) || soundEvent.Contains("loss", StringComparison.OrdinalIgnoreCase) || soundEvent.Contains("countdown", StringComparison.OrdinalIgnoreCase))
            ? SoundCategory.Music : SoundCategory.Effects;
    }
}

/// <summary>What the running client's sound and look code read (set by <c>CustomizerServices</c> from the saved <see cref="GameOptions"/>); engine-free.</summary>
public static class LiveOptions
{
    public static GameOptions Current { get; set; } = GameOptions.Default;

    /// <summary>The gain in decibels of a sound's category (0 at the default).</summary>
    public static float GainDb(SoundCategory category) => GameOptions.VolumeDb(category switch
    {
        SoundCategory.Music => Current.MusicVolume, SoundCategory.Voice => Current.VoiceVolume, _ => Current.EffectsVolume,
    });

    /// <summary>The sign of a mouse look's vertical movement (-1 with Invert Y).</summary>
    public static float MouseVertical => Current.InvertY ? -1f : 1f;

    /// <summary>The factor on the mouse look: <see cref="GameOptions.AimSensitivity"/> while aiming.</summary>
    public static float MouseAim(bool aiming) => aiming ? Current.AimSensitivity : 1f;

    /// <summary>The factor on the right stick's turn rates.</summary>
    public static float PadLook => Current.PadLookSensitivity;

    /// <summary>The sign of the right stick's vertical turn (-1 with Invert gamepad Y).</summary>
    public static float PadVertical => Current.InvertPadY ? -1f : 1f;
}

/// <summary>The fixed join targets of the front end (ui/menu-rebuild, the owner's menu).</summary>
public static class JoinTargets
{
    /// <summary>JOIN SERVER connects here and nowhere else: the address cannot be changed in the UI.</summary>
    public const string PublicServer = "64.177.51.188:27015";
}
