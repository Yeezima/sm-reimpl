using System;
using System.Collections.Generic;
using Godot;
using SmClient.Customizer;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// First run, no Space Marine install found: says so, lets the player Browse to the game folder (or paste it), explains what is wrong with a folder that does not
/// fit, and Retry looks again (Steam, the saved choice). A folder that validates is remembered (<see cref="GameInstall.Remember"/>, in the private data folder,
/// never in the install) and <paramref name="found"/> is called; the front end follows. The install is only ever read.
/// Test aids: <c>--setup-path &lt;dir&gt;</c> tries that folder as if it had been browsed to.
/// </summary>
public sealed partial class GameFolderSetup : Control
{
    readonly Action<GameLocation> _found;
    readonly string? _repoRoot;
    readonly string? _explicitDir;
    readonly string? _autoPath;
    LineEdit? _path;
    Label? _message;
    Button? _retry, _browse, _use;
    int _frames;
    bool _autoDone, _finished;

    /// <param name="explicitDir">The <c>--game-dir</c> the player passed (detection keeps honouring it on Retry).</param>
    public GameFolderSetup(Action<GameLocation> found, string? repoRoot, string? explicitDir, string? autoPath = null)
    {
        _found = found; _repoRoot = repoRoot; _explicitDir = explicitDir; _autoPath = autoPath;
        Name = "GameFolderSetup";
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = UiKit.Theme;
    }

    public override void _Ready()
    {
        UiKit.EnterVirtualSpace(GetWindow());
        GD.Print("game folder setup: Space Marine (2011, Steam) not found");
        AddChild(UiKit.MenuBackground().With(n => n.SetAnchorsPreset(LayoutPreset.FullRect)));

        var box = UiKit.VBox(16);
        box.CustomMinimumSize = new Vector2(1000, 0);
        var title = UiKit.HBox(16);
        title.AddChild(UiKit.Text("SPACE MARINE", UiKit.TitleSize, UiKit.Gold));
        title.AddChild(UiKit.Text("MULTIPLAYER", UiKit.TitleSize, UiKit.White));
        box.AddChild(title);
        box.AddChild(UiKit.Text("Space Marine (2011, Steam) not found", UiKit.HeaderSize, UiKit.LightRed));
        box.AddChild(UiKit.Text(
            "This game plays with the files of your own copy of Warhammer 40,000: Space Marine (the 2011 Steam version). Nothing from it is copied or changed. " +
            "Install it on Steam and press RETRY, or choose the folder that contains its \"data\" folder.",
            UiKit.BodySize, UiKit.White, HorizontalAlignment.Left, true).With(l => l.CustomMinimumSize = new Vector2(960, 0)));
        box.AddChild(UiKit.Text("Usually: ...\\steamapps\\common\\" + GameInstall.DefaultInstallDir, UiKit.CaptionSize, UiKit.Grey, HorizontalAlignment.Left, true));

        var row = UiKit.HBox(12);
        _path = new LineEdit { PlaceholderText = "game folder", CustomMinimumSize = new Vector2(640, UiKit.MinHit), FocusMode = FocusModeEnum.All, SelectAllOnFocus = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _path.TextSubmitted += TryText;
        row.AddChild(_path);
        _browse = UiKit.Button("BROWSE...", Browse, 200, "Choose the game folder");
        _use = UiKit.Button("USE THIS FOLDER", () => TryText(_path.Text), 280);
        row.AddChild(_browse);
        row.AddChild(_use);
        box.AddChild(row);

        _message = UiKit.Text("", UiKit.BodySize, UiKit.White, HorizontalAlignment.Left, true);
        _message.CustomMinimumSize = new Vector2(960, 64);
        box.AddChild(_message);

        var bar = UiKit.HBox(12);
        _retry = UiKit.Button("RETRY", Retry, 220, "Look for the game again");
        bar.AddChild(_retry);
        bar.AddChild(UiKit.Spacer());
        bar.AddChild(UiKit.Button("QUIT", () => GetTree().Quit(), 160));
        box.AddChild(bar);
        box.AddChild(new LinkButton { Text = "Help, builds and issues: " + ServerLaunch.ReleasesUrl, Uri = ServerLaunch.ReleasesUrl }.With(l => l.AddThemeFontSizeOverride("font_size", UiKit.CaptionSize)));

        var panel = UiKit.Panel(box, 28);
        panel.AddThemeStyleboxOverride("panel", UiKit.Box(UiKit.PanelSolid, UiKit.PanelSolid, 0, 28));
        var center = new CenterContainer();
        center.SetAnchorsPreset(LayoutPreset.FullRect);
        center.AddChild(panel);
        AddChild(center);
        _retry.FocusLater();
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_work is { IsCompleted: true } && _workDone is { } finished) { _work = null; _workDone = null; finished(); }
        if (!_autoDone && _autoPath is not null && _frames > 5) { _autoDone = true; _path!.Text = _autoPath; TryText(_autoPath); }
    }

    void Browse()
    {
        if (ModelViewer.TestWindow) { Say("No folder dialog in a test window (use --setup-path).", true); return; }
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir, Access = FileDialog.AccessEnum.Filesystem, UseNativeDialog = true,
            Title = "Choose the Space Marine game folder (the one that contains \"data\")",
        };
        AddChild(dialog);
        dialog.DirSelected += dir => { dialog.QueueFree(); _path!.Text = dir; TryText(dir); };
        dialog.Canceled += () => dialog.QueueFree();
        dialog.PopupCentered(new Vector2I(1000, 650));
    }

    /// <summary>Checks a folder the player chose or typed; on success it is remembered and the front end follows.</summary>
    void TryText(string text)
    {
        if (_finished || _work is not null) return;
        string dir = text.Trim().Trim('"');
        if (dir.Length == 0) { Say("Choose the game folder first.", true); return; }
        // The folder may be on a slow or unreachable drive: the checks run on a worker thread.
        Background("Checking the folder...", () =>
        {
            try
            {
                string fixedDir = GameInstall.Normalize(dir);
                return (Dir: fixedDir, Problem: GameInstall.Validate(fixedDir));
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return (Dir: dir, Problem: "That folder cannot be read: " + e.Message); }
        }, result =>
        {
            if (result.Problem is { } problem) { Say(problem, true); return; }
            if (GameInstall.Remember(result.Dir) is { } saveProblem)
                GD.PrintErr("game folder not remembered: " + saveProblem);   // the game still starts this time; the next start asks again
            Finish(new GameLocation(System.IO.Path.GetFullPath(result.Dir), "the folder you chose"));
        });
    }

    void Retry()
    {
        if (_finished || _work is not null) return;
        Background("Looking for the game...", () =>
        {
            try { return GameInstall.TryLocate(_explicitDir, _repoRoot); }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return null; }
        }, located =>
        {
            if (located is null) { Say("Still not found. Install Space Marine (2011) on Steam, or choose its folder.", true); return; }
            Finish(located);
        });
    }

    System.Threading.Tasks.Task? _work;
    Action? _workDone;

    /// <summary>Runs a file-system check off the main thread (the UI keeps drawing, the buttons wait) and calls <paramref name="done"/> back on it.</summary>
    void Background<T>(string message, Func<T> work, Action<T> done)
    {
        Say(message, false);
        SetBusy(true);
        var task = System.Threading.Tasks.Task.Run(work);
        _work = task;
        _workDone = () => { SetBusy(false); if (task.IsCompletedSuccessfully) done(task.Result); else Say("The check failed: " + task.Exception?.GetBaseException().Message, true); };
    }

    void SetBusy(bool busy)
    {
        foreach (var b in new[] { _browse, _use, _retry }) if (b is not null) b.Disabled = busy;
    }

    void Finish(GameLocation location)
    {
        _finished = true;
        Say("Found: " + location.Source, false);
        _found(location);
    }

    void Say(string text, bool problem)
    {
        if (_message is null) return;
        _message.Text = text;
        GD.Print("game folder setup: " + text);
        _message.AddThemeColorOverride("font_color", problem ? UiKit.LightRed : UiKit.White);
    }
}
