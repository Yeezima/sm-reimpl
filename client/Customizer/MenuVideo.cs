using System;
using Godot;

namespace SmClient.Customizer;

/// <summary>
/// The main menu's moving background (the owner, 2026-11-10: "use this video loop as the main menu background in the next shipped release"): his own cinematic of our Ashfall
/// Citadel map (owner-made, not an original file), converted by <c>tools/menu-video.ps1</c> to <see cref="MenuVideoPath"/> (Ogg Theora, 1920 x 1080, a constant 30 fps, no audio,
/// the loop's seam crossfaded in the file). It plays looping behind every screen that showed the still picture (the front end in Classic PvP and Musou alike, the game folder
/// setup); the loading screen keeps its own picture. <c>--menu-still</c> restores the still picture; so does a missing or unplayable file. Since 2026-11-15 the default is the concept's 8 s video
/// (<see cref="MenuConceptVideoPath"/>) and <c>--menu-video</c> the older loop. Ours (spec "Main menu video").
/// </summary>
public static partial class UiKit
{
    /// <summary>
    /// The main menu's video since 2026-11-15 (the owner's UI concept, "make the referenced video image the background of the main menu"): his 8 s clip of the Ashfall courtyard with the Librarian, converted by
    /// <c>tools/menu-video.ps1</c> (Ogg Theora, 2560 x 1440, 24 fps, no audio, 7.5 s, the seam crossfaded, 11.5 MB). Read from beside the project in a development run, under <c>assets/menu</c> beside the game
    /// in an exported one (a folder Godot does not import); shipped with the release.
    /// </summary>
    public const string MenuConceptVideoPath = "res://assets/menu/menu-concept-loop.ogv";

    /// <summary>Where the older video loop (the owner's 31 s cinematic of 2026-11-10) is read from: a developer aid, not shipped (the release leaves it out, 20 MB), so outside a checkout MenuVideoFile() is null with <c>--menu-video</c>.</summary>
    public const string MenuVideoPath = "res://assets/menu/menu-loop.ogv";

    /// <summary>The switch that shows the still picture behind the menus (<c>--menu-still</c>; the default of 2026-11-14, restored by this switch since 2026-11-15).</summary>
    public const string MenuStillSwitch = "menu-still";

    /// <summary>The switch that brings the older 31 s video loop back behind the menus (<c>--menu-video</c>; the default from 2026-11-10 to 2026-11-14).</summary>
    public const string MenuVideoSwitch = "menu-video";

    /// <summary>
    /// The menus show the still picture only with <c>--menu-still</c> (owner, 2026-11-15: the concept's video is the main menu's background; the 2026-11-14 forge-world city picture is what the switch keeps),
    /// or without a playable video file.
    /// </summary>
    public static bool MenuStill { get; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--" + MenuStillSwitch) >= 0 && Array.IndexOf(OS.GetCmdlineUserArgs(), "--" + MenuVideoSwitch) < 0;

    /// <summary><c>--menu-video</c>: the older 31 s loop instead of the concept's video.</summary>
    public static bool MenuOldVideo { get; } = Array.IndexOf(OS.GetCmdlineUserArgs(), "--" + MenuVideoSwitch) >= 0;

    /// <summary>The video's file, or null when it is missing: the older loop with <c>--menu-video</c>, else the concept's video (development run: beside the project; exported game: beside the exe).</summary>
    public static string? MenuVideoFile() => MenuFile(MenuOldVideo ? MenuVideoPath : MenuConceptVideoPath);

    /// <summary>
    /// The background of the menus: the concept's looping video by default (then the still picture when the file is missing, or the plain dark one without that); the still picture with <c>--menu-still</c>;
    /// the older loop with <c>--menu-video</c>. <paramref name="video"/> overrides the choice (the self-test builds both). The video is not played under the headless display server (nothing is drawn there).
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
    public static readonly Vector2 ConvertedSize = new(2560, 1440);

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

    /// <summary>
    /// The main menu's framing (ui/concept-redesign, 2026-11-15): the concept's overlay covers the left half of the screen, so the video is enlarged by <see cref="MainMenuZoom"/> (1.2x the covering size since 2026-11-20, the least that lets the pan put the Marine's body, at 0.42 of the picture's width, in the middle of the window; 1.6x before) and
    /// panned so that its focus point (<see cref="FocusU"/>, <see cref="FocusV"/>: the middle of the Marine and the tower with its god rays, measured on the video's frames: the Marine at about 0.37 to 0.46 of its
    /// width, the tower at 0.50 to 0.63) lands at <see cref="FocusX"/>, <see cref="FocusY"/> of the window, centred in the part right of the overlay. The picture always covers the window (no black edge at any aspect ratio).
    /// Other screens show the picture as it was made.
    /// </summary>
    public bool MainMenuFraming { get => _framing; set { if (_framing == value) return; _framing = value; Fit(); } }
    public const float DefaultMainMenuZoom = 1.2f, FocusU = 0.42f, FocusV = 0.5f, FocusX = 0.5f, FocusY = 0.5f;

    /// <summary>The main menu's zoom: <see cref="DefaultMainMenuZoom"/>, or <c>--menu-zoom &lt;f&gt;</c> (1.0 = the whole frame on a 16:9 window, 1.6 = the 2026-11-15 framing; at 16:9 the zoom 1.33 pans to the left edge, so the focus point is only reached from about 1.6; at least 1, so the picture covers the window).</summary>
    public static float MainMenuZoom { get; } = ParseZoom(OS.GetCmdlineUserArgs());

    static float ParseZoom(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string value = args[i] == "--menu-zoom" && i + 1 < args.Length ? args[i + 1] : args[i].StartsWith("--menu-zoom=", StringComparison.Ordinal) ? args[i]["--menu-zoom=".Length..] : "";
            if (value.Length > 0 && float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)) return Mathf.Clamp(f, 1f, 4f);
        }
        return DefaultMainMenuZoom;
    }
    bool _framing;

    /// <summary>The player's rect for a window of <paramref name="box"/> with the main menu's framing (the self-test checks that it covers the window).</summary>
    public static Rect2 FramedRect(Vector2 box, Vector2 video)
    {
        var r = CoverRect(box, video);
        // On a wider window than 16:9 the covering size already crops the picture's height, so the zoom shrinks with the aspect to keep the same part of the picture in view (the Marine's feet and the tower's top).
        float zoom = Mathf.Max(1f, MainMenuZoom * Mathf.Min(1f, (16f / 9f) / (box.X / box.Y)));
        var size = r.Size * zoom;
        float focusX = FocusX;   // the Marine stays in the middle (the menu is a small group in the bottom left corner)
        var pos = new Vector2(focusX * box.X - size.X * FocusU, FocusY * box.Y - size.Y * FocusV);
        pos.X = Mathf.Clamp(pos.X, box.X - size.X, 0f);
        pos.Y = Mathf.Clamp(pos.Y, box.Y - size.Y, 0f);
        return new Rect2(pos, size);
    }

    void Fit()
    {
        var r = _framing ? FramedRect(Size, _videoSize) : CoverRect(Size, _videoSize);
        Player.Position = r.Position;
        Player.Size = r.Size;
    }
}
