using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The main menu's moving background (the owner, 2026-11-10: "use this video loop as the main menu background in the next shipped release"): his own cinematic of our Ashfall
/// Citadel map (owner-made, not an original file), converted by <c>tools/menu-video.ps1</c> to <see cref="MenuVideoPath"/> (Ogg Theora, 1920 x 1080, a constant 30 fps, no audio,
/// the loop's seam crossfaded in the file). It plays looping behind every screen that showed the still picture (the front end in Classic PvP and Musou alike, the game folder
/// setup); the loading screen keeps its own picture. <c>--menu-still</c> restores the still picture; so does a missing or unplayable file. Ours (spec "Main menu video").
/// </summary>
public static partial class UiKit
{
    /// <summary>Where the video is read from: beside the project in a development run, under <c>assets/menu</c> beside the game in an exported one (a folder Godot does not import).</summary>
    public const string MenuVideoPath = "res://assets/menu/menu-loop.ogv";

    /// <summary>The switch that restores the still picture behind the menus (<c>--menu-still</c>).</summary>
    public const string MenuStillSwitch = "menu-still";

    /// <summary>The switch that brings the looping video back behind the menus (<c>--menu-video</c>; the default from 2026-11-10 to 2026-11-14).</summary>
    public const string MenuVideoSwitch = "menu-video";

    /// <summary>
    /// The menus show the still picture: the default again since 2026-11-14 (owner: "yes the orange forgle-world city is the art to go back to", the picture of 1 to 9 November); <c>--menu-video</c> restores the video loop.
    /// <c>--menu-still</c> is still accepted and changes nothing.
    /// </summary>
    public static bool MenuStill { get; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--" + MenuVideoSwitch) < 0;

    /// <summary>The video's file, or null when it is missing (development run: beside the project; exported game: beside the exe).</summary>
    public static string? MenuVideoFile() => MenuFile(MenuVideoPath);

    /// <summary>
    /// The background of the menus: the still picture; the looping video only with <c>--menu-video</c> (and the file present) (then the still picture, or the plain dark one without it).
    /// <paramref name="video"/> overrides the choice (the self-test builds both). The video is not played under the headless display server (nothing is drawn there).
    /// </summary>
    public static Control MenuBackground(bool? video = null)
    {
        if ((video ?? !MenuStill) && MenuVideoFile() is { } file)
            return new MenuVideoBackground(file, play: !DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase));
        return MenuStillBackground();
    }

    /// <summary>The still picture covering the window without stretching, or the plain dark background without it.</summary>
    public static Control MenuStillBackground()
    {
        if (MenuBackgroundImage() is not { } image) return Background();
        return new TextureRect { Name = "MenuBackground", Texture = image, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, MouseFilter = Control.MouseFilterEnum.Ignore };
    }

    /// <summary>A file of <c>assets/menu</c>: beside the project in a development run, beside the game in an exported one; null when neither exists.</summary>
    static string? MenuFile(string resPath)
    {
        try
        {
            string exeDir = System.IO.Path.GetDirectoryName(OS.GetExecutablePath()) ?? "";
            foreach (string path in new[] { ProjectSettings.GlobalizePath(resPath), System.IO.Path.Combine(exeDir, "assets", "menu", System.IO.Path.GetFileName(resPath)) })
                if (System.IO.File.Exists(path)) return path;
        }
        catch (Exception e) when (e is ArgumentException or System.IO.IOException or NotSupportedException) { GD.PrintErr("menu file " + resPath + ": " + e.Message); }
        return null;
    }
}

/// <summary>
/// The looping video behind the menus (<see cref="UiKit.MenuBackground"/>): a <see cref="VideoStreamPlayer"/> sized to cover the window without stretching (centred, the overflow
/// clipped, so no letterbox at any aspect ratio), over the plain dark background until the first frame. Paused while a screen with its own background covers it (the customizer:
/// <see cref="Covered"/>). A video that has not started after <see cref="StartSeconds"/> gives way to the still picture, so a file Godot cannot play never leaves the menus dark.
/// </summary>
public sealed partial class MenuVideoBackground : Control
{
    /// <summary>The video's frame size as converted (tools/menu-video.ps1); the decoded texture's own size replaces it once known.</summary>
    public static readonly Vector2 ConvertedSize = new(1920, 1080);

    /// <summary>Seconds of drawn frames after which a video that shows no frame is replaced by the still picture (ours).</summary>
    public const double StartSeconds = 2.0;

    public VideoStreamPlayer Player { get; }
    readonly bool _play;
    bool _covered, _gaveUp;
    double _waited;
    Vector2 _videoSize = ConvertedSize;

    public MenuVideoBackground(string file, bool play)
    {
        _play = play;
        Name = "MenuBackground";
        ClipContents = true;
        MouseFilter = MouseFilterEnum.Ignore;
        AddChild(UiKit.Background().With(n => { n.Name = "MenuVideoUnder"; n.SetAnchorsPreset(LayoutPreset.FullRect); }));
        Player = new VideoStreamPlayer
        {
            Name = "MenuVideo", Stream = new VideoStreamTheora { File = file }, Loop = true, Expand = true, Autoplay = false, MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(Player);
        Resized += Fit;
    }

    /// <summary>A screen with its own opaque background is over the video (the customizer): it pauses there and goes on where it was when uncovered.</summary>
    public bool Covered
    {
        get => _covered;
        set
        {
            if (_covered == value) return;
            _covered = value;
            if (_play && !_gaveUp) Player.Paused = value;
        }
    }

    /// <summary>The player's rect for a window of <paramref name="box"/>: the video scaled to cover it, keeping its aspect, centred (the self-test checks it).</summary>
    public static Rect2 CoverRect(Vector2 box, Vector2 video)
    {
        if (video.X <= 0f || video.Y <= 0f) video = ConvertedSize;
        float scale = Mathf.Max(box.X / video.X, box.Y / video.Y);
        var size = video * scale;
        return new Rect2((box - size) / 2f, size);
    }

    public override void _Ready()
    {
        Fit();
        if (_play) Player.Play();
    }

    public override void _Process(double delta)
    {
        if (!_play || _gaveUp || _waited < 0) return;   // _waited < 0: a frame came, the size is known and the video never gives up later
        if (Player.GetVideoTexture() is { } texture && texture.GetSize() is { X: > 0f, Y: > 0f } size)
        {
            if (size != _videoSize) { _videoSize = size; Fit(); }
            _waited = -1;
            return;
        }
        if (_covered) return;
        _waited += Math.Min(delta, 0.1);
        if (_waited < StartSeconds) return;
        _gaveUp = true;
        GD.PrintErr("menu video: no frame after " + StartSeconds + " s; the still picture instead");
        Player.Stop();
        Player.Visible = false;
        AddChild(UiKit.MenuStillBackground().With(n => { n.Name = "MenuStillFallback"; n.SetAnchorsPreset(LayoutPreset.FullRect); }));
    }

    void Fit()
    {
        var r = CoverRect(Size, _videoSize);
        Player.Position = r.Position;
        Player.Size = r.Size;
    }
}
