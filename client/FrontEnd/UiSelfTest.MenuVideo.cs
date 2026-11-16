using System;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;

namespace SmClient.FrontEnd;

// The main menu's video loop (the owner, 2026-11-10): the front end's background is the looping video beside the project, sized to cover the window at any aspect ratio
// without a letterbox, not played headless; --menu-still (and a missing file) gives the still picture.
public sealed partial class UiSelfTest
{
    async Task MenuVideoChecks()
    {
        _front.ShowMainMenu();
        await Frames(3);
        Check("menu video: the constant names one file", UiKit.MenuVideoPath == "res://assets/menu/menu-loop.ogv");
        Check("menu video: the switch's name", UiKit.MenuStillSwitch == "menu-still");
        string file = ProjectSettings.GlobalizePath(UiKit.MenuVideoPath);
        Check("menu video: the file is beside the project", System.IO.File.Exists(file) && UiKit.MenuVideoFile() == file, file);

        var background = _front.MenuBackground;
        if (UiKit.MenuStill)
            Check("menu video: --menu-still shows the still picture", background is TextureRect && background is not MenuVideoBackground && background.Name == "MenuBackground", background?.GetType().Name ?? "none");
        else
        {
            var video = background as MenuVideoBackground;
            Check("menu video: the front end's background is the video", video is { ClipContents: true } && video.Name == "MenuBackground" && _front.GetChild(0) == video, background?.GetType().Name ?? "none");
            if (video is not null)
            {
                var player = video.Player;
                Check("menu video: a looping Theora stream of the file", player.Stream is VideoStreamTheora theora && theora.File == file && player.Loop && player.Expand && !player.Autoplay,
                    (player.Stream as VideoStreamTheora)?.File ?? "no stream");
                Check("menu video: it takes no input", video.MouseFilter == Control.MouseFilterEnum.Ignore && player.MouseFilter == Control.MouseFilterEnum.Ignore);
                bool headless = DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase);
                Check("menu video: not played headless, played with a display", headless ? !player.IsPlaying() : player.IsPlaying() || video.HasNode("MenuStillFallback"));
                var shown = new Rect2(player.Position, player.Size);
                Check("menu video: the player covers the screen", video.Size.X > 0f && shown.Position.X <= 0.5f && shown.Position.Y <= 0.5f && shown.End.X >= video.Size.X - 0.5f && shown.End.Y >= video.Size.Y - 0.5f,
                    $"{shown} in {video.Size}");
                // The customizer's own dark background covers it: paused there, going on back on the main menu.
                _front.ShowCreator();
                await Frames(3);
                Check("menu video: paused under the customizer", video.Covered && (headless || player.Paused), $"covered {video.Covered}, paused {player.Paused}");
                _front.ShowMainMenu();
                await Frames(3);
                Check("menu video: going on back on the main menu", !video.Covered && !player.Paused, $"covered {video.Covered}, paused {player.Paused}");
            }
        }

        // Cover-fit at any aspect ratio: no letterbox, no stretch, centred, the overflow on one axis only.
        bool fits = true;
        string worst = "";
        foreach (var box in new[] { new Vector2(1920, 1080), new Vector2(2560, 1080), new Vector2(3440, 1440), new Vector2(1440, 1080), new Vector2(1280, 1024), new Vector2(1920, 1200), new Vector2(1080, 1920) })
        {
            var r = MenuVideoBackground.CoverRect(box, MenuVideoBackground.ConvertedSize);
            bool ok = r.Position.X <= 0.01f && r.Position.Y <= 0.01f && r.End.X >= box.X - 0.01f && r.End.Y >= box.Y - 0.01f
                      && Mathf.Abs(r.Size.X / r.Size.Y - 16f / 9f) < 0.001f
                      && (Mathf.Abs(r.Size.X - box.X) < 0.01f || Mathf.Abs(r.Size.Y - box.Y) < 0.01f)
                      && Mathf.Abs(r.Position.X + r.Size.X / 2f - box.X / 2f) < 0.01f && Mathf.Abs(r.Position.Y + r.Size.Y / 2f - box.Y / 2f) < 0.01f;
            if (!ok) { fits = false; worst = $"{box}: {r}"; }
        }
        Check("menu video: cover-fit at 16:9, 21:9, 4:3, 5:4, 16:10 and portrait", fits, worst);
        Check("menu video: an unknown video size fits as the converted one", MenuVideoBackground.CoverRect(new Vector2(2560, 1080), Vector2.Zero) == MenuVideoBackground.CoverRect(new Vector2(2560, 1080), MenuVideoBackground.ConvertedSize));

        // The still picture on request (the switch's path), built and freed here.
        var still = UiKit.MenuBackground(video: false);
        bool hasImage = UiKit.MenuBackgroundImage() is not null;
        Check("menu video: the still picture without the video", still is not MenuVideoBackground && (!hasImage || still is TextureRect { StretchMode: TextureRect.StretchModeEnum.KeepAspectCovered } && still.Name == "MenuBackground"), still.GetType().Name);
        still.Free();
    }
}
