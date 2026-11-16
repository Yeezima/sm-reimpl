using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using SmMapmaker;

namespace SmMapmakerEditor;

/// <summary>The dock's tabs, in order (the index of each in the tab bar).</summary>
public enum DockTab { Start = 0, Build = 1, Object = 2, Library = 3, Tools = 4, Prefab = 5 }

/// <summary>
/// The Start and Build tabs of the Mapmaker dock (docs/mapmaker.md "How to make a map in 2 minutes"): the quick start, a new map from a layout, the live check with
/// one-click fixes, the Play test button and sharing; and the palette of pieces to place.
/// </summary>
public partial class MapmakerDock
{
    public void ShowTab(DockTab tab) => ShowTab((int)tab);

    // start
    readonly LineEdit _newName = new() { Text = "My Map", PlaceholderText = "Map name", TooltipText = "The map's name. It is shown in the game and names the file." };
    readonly OptionButton _template = new() { TooltipText = "The starting layout. You can change everything afterwards." };
    readonly Label _templateInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
    readonly Label _checkHead = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0), ThemeTypeVariation = "HeaderSmall" };
    readonly VBoxContainer _checkList = new();
    readonly Label _checkFacts = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
    readonly OptionButton _playMode = new() { TooltipText = "Which game mode to play. Seize Ground needs at least one capture point." };
    readonly SpinBox _playBots = new() { MinValue = 0, MaxValue = 6, Step = 1, Value = 3, TooltipText = "Bots on each side (0 to 6). They run in the background with no window." };
    readonly OptionButton _playSide = new() { TooltipText = "The side you start on." };
    readonly Label _playStatus = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
    readonly Label _shareInfo = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
    Button? _playButton;
    Label? _buildHint;

    public const string QuickStart =
        "How to make a map in 2 minutes\n" +
        "1. Pick a layout below and press New map (it is saved for you).\n" +
        "2. Move things with the gizmo (W move, E turn, R size), or open the Build tab and click a piece, then click in the 3D view to place it.\n" +
        "3. Fix anything red in the Check list. Fix buttons do it for you.\n" +
        "4. Press Play test: a server and bots start on this PC and the game opens on your map.\n" +
        "5. Send the .smmap file (Share). Anyone puts it in their maps folder and plays it.";

    ScrollContainer Scroll(Control page) => new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, Name = "Scroll", FollowFocus = true }.With(s => s.AddChild(page));

    Control StartTab()
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddChild(new Label { Text = QuickStart, AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) });
        v.AddChild(new HSeparator());
        v.AddChild(Head("New map"));
        v.AddChild(Row(new Label { Text = "Name" }, _newName));
        _newName.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        foreach (var t in MapTemplate.All) _template.AddItem(t.Label);
        _template.ItemSelected += i => _templateInfo.Text = MapTemplate.All[(int)i].Description;
        _templateInfo.Text = MapTemplate.All[0].Description;
        v.AddChild(Row(new Label { Text = "Layout" }, _template));
        _template.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(_templateInfo);
        v.AddChild(Row(Btn("New map", NewFromLayout, "Makes the map from the chosen layout, saves it and shows it."), Btn("Open map...", OpenMap, "Open a .smmap file."), _recentStart));
        _recentStart.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _recentStart.ItemSelected += i => { var p = _recentStart.GetItemMetadata((int)i).AsString(); if (p.Length > 0 && _plugin.EnsureSceneRoot()) _plugin.OpenMap(p); };
        v.AddChild(new HSeparator());
        v.AddChild(Head("Game modes this map is for"));
        _modeAnnihilation.Toggled += _ => ModesChanged(); _modeSeize.Toggled += _ => ModesChanged();
        v.AddChild(Row(_modeAnnihilation, _modeSeize));
        v.AddChild(Head("Check"));
        v.AddChild(_checkHead);
        v.AddChild(_checkList);
        v.AddChild(_checkFacts);
        v.AddChild(new HSeparator());
        v.AddChild(Head("Play test"));
        _playMode.AddItem("Annihilation"); _playMode.AddItem("Seize Ground");
        _playSide.AddItem("Space Marines"); _playSide.AddItem("Chaos");
        v.AddChild(Row(new Label { Text = "Mode" }, _playMode, new Label { Text = "Bots a side" }, _playBots));
        v.AddChild(Row(new Label { Text = "I start as" }, _playSide));
        _playMode.SizeFlagsHorizontal = SizeFlags.ExpandFill; _playSide.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _playButton = Btn("PLAY TEST", StartPlayTest, "Saves the map, starts a server on this PC only (127.0.0.1) with bots, and opens the game on your map. Close the game window to stop.");
        _playButton.CustomMinimumSize = new Vector2(0, 40);
        _playButton.AddThemeFontSizeOverride("font_size", 16);
        v.AddChild(Row(_playButton, Btn("Stop", () => _plugin.StopPlayTest(), "Stops the server and bots of the last play test (closing the game window does the same).")));
        _playButton.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(_playStatus);
        v.AddChild(new HSeparator());
        v.AddChild(Head("Share"));
        v.AddChild(Row(Btn("Save", () => SaveMap(false), "Saves the map to its file."), Btn("Save As...", () => SaveMap(true)),
            Btn("Show file", ShowFile, "Opens the folder with the map's .smmap file selected."), Btn("Copy path", CopyPath, "Copies the file's path.")));
        v.AddChild(_shareInfo);
        _plugin.CheckChanged += RefreshCheck;
        _plugin.PlayStatusChanged += () => _playStatus.Text = _plugin.PlayStatus;
        RefreshCheck();
        return Scroll(v);
    }

    readonly CheckBox _modeAnnihilation = new() { Text = "Annihilation", ButtonPressed = true, TooltipText = "Team deathmatch. Needs a spawn area for each side." };
    readonly CheckBox _modeSeize = new() { Text = "Seize Ground", ButtonPressed = true, TooltipText = "Capture points. Needs at least one capture point. Untick a mode your map is not made for; future modes will be listed here too." };
    readonly OptionButton _recentStart = new() { TooltipText = "Maps in your maps folder." };

    void NewFromLayout()
    {
        _ = NewFromLayoutAsync();
    }

    /// <summary>Test aid (the self test's flow): the same as typing the name, choosing the layout and pressing New map.</summary>
    public System.Threading.Tasks.Task TestNewFromLayout(string name, string templateId)
    {
        _newName.Text = name;
        _template.Select(Math.Max(0, MapTemplate.All.ToList().FindIndex(t => t.Id == templateId)));
        return NewFromLayoutAsync();
    }

    /// <summary>Test aid: the same as pressing a Build tab button.</summary>
    public void TestArmPreset(string id) { if (PiecePreset.Find(id) is { } p) ArmPreset(p); }

    /// <summary>Test aid: the same as pressing the Play test button with these choices.</summary>
    public string? TestPlay(string mode, int bots, string side, string[] extra) => _plugin.PlayTest(mode, bots, side, extra);

    async System.Threading.Tasks.Task NewFromLayoutAsync()
    {
        if (MapHost.StartingEmptyMap) return;
        MapHost.StartingEmptyMap = true;
        try
        {
            var s = _plugin.Session;
            if (s is not null && _plugin.Dirty)
            {
                // Nothing is lost: the open map is saved first (to its file, or to a new one in the maps folder).
                if (!_plugin.SaveMap(s.Path ?? _plugin.NewMapPath(s.Name))) { _status.Text = "The open map could not be saved, so the new map was not started."; return; }
            }
            _plugin.EnsureSceneRoot();
            for (int i = 0; i < 90 && !(EditorInterface.Singleton.GetEditedSceneRoot() is MapmakerRoot && MapHost.Root is not null); i++)
                await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (MapHost.Root is null) { _status.Text = "Could not open the Mapmaker scene."; return; }
            var t = MapTemplate.All[Math.Max(0, _template.Selected)];
            _backdrop.SetPressedNoSignal(false);
            _plugin.NewFromTemplate(_newName.Text, t.Id);
            _plugin.SetTool(Tool.Select);
        }
        finally { MapHost.StartingEmptyMap = false; }
    }

    void ModesChanged()
    {
        if (_refreshing || _plugin.Session is null) return;
        var modes = new List<string>();
        if (_modeAnnihilation.ButtonPressed) modes.Add("annihilation");
        if (_modeSeize.ButtonPressed) modes.Add("seize");
        // modes this version does not know stay as they are
        modes.AddRange(_plugin.Session.Document.Modes.Where(m => !MapDocument.KnownModes.Contains(m)));
        if (modes.Count == 0) { _status.Text = "A map needs at least one game mode."; RefreshCheck(); return; }
        _plugin.Do("Game modes", MapmakerPlugin.Op("set-modes", ("modes", MapmakerPlugin.Strs(modes))));
    }

    void StartPlayTest()
    {
        string mode = _playMode.Selected == 1 ? "seize" : "annihilation";
        _plugin.PlayTest(mode, (int)_playBots.Value, _playSide.Selected == 1 ? "chaos" : "marines");
        _playStatus.Text = _plugin.PlayStatus;
    }

    void ShowFile()
    {
        if (_plugin.ShareFile() is { } f) OS.ShellShowInFileManager(f);
    }

    void CopyPath()
    {
        if (_plugin.ShareFile() is { } f) { DisplayServer.ClipboardSet(f); _status.Text = $"Copied: {f}"; }
    }

    static Color SeverityColour(Severity s) => s switch { Severity.Error => new Color(1f, 0.45f, 0.4f), Severity.Warning => new Color(1f, 0.82f, 0.35f), _ => new Color(0.7f, 0.75f, 0.8f) };

    /// <summary>Redraws the Check list from the plugin's last check.</summary>
    public void RefreshCheck()
    {
        foreach (var c in _checkList.GetChildren()) { _checkList.RemoveChild(c); c.QueueFree(); }
        var r = _plugin.LastCheck;
        if (_plugin.Session is null || r is null)
        {
            _checkHead.Text = "No map yet. Pick a layout above and press New map.";
            _checkFacts.Text = "";
            _shareInfo.Text = "";
            return;
        }
        _modeAnnihilation.SetPressedNoSignal(r.Declared.Contains("annihilation")); _modeSeize.SetPressedNoSignal(r.Declared.Contains("seize"));
        _checkHead.Text = r.Headline + (_plugin.RouteChecking ? "  (checking the bots' route...)" : "");
        _checkHead.AddThemeColorOverride("font_color", r.AnnihilationReady || r.SeizeReady ? new Color(0.5f, 0.95f, 0.55f) : new Color(1f, 0.55f, 0.5f));
        foreach (var issue in r.Issues.Where(i => i.Code != "note").OrderBy(i => i.Severity))
        {
            var row = new HBoxContainer();
            var label = new Label { Text = (issue.Severity == Severity.Error ? "Fix: " : issue.Severity == Severity.Warning ? "Check: " : "Note: ") + issue.Message + (issue.Fix is null ? "" : "\n-> " + issue.Fix), AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 0) };
            label.AddThemeColorOverride("font_color", SeverityColour(issue.Severity));
            row.AddChild(label);
            if (issue.Action is not null)
            {
                var fix = Btn("Fix it", () => { _plugin.Fix(issue); }, "Adds what is missing, in a sensible place. Ctrl+Z undoes it.");
                row.AddChild(fix);
            }
            if (issue.Ids is { Count: > 0 })
                row.AddChild(Btn("Select", () => _plugin.Select(issue.Ids), "Selects the objects this is about."));
            _checkList.AddChild(row);
        }
        _checkFacts.Text = string.Join("\n", r.Facts);
        var s = _plugin.Session;
        _shareInfo.Text = s.Path is null ? "Not saved yet." : $"File: {s.Path}\nThis one file is the whole map. Send it; the other person puts it in {MapHost.Library.MapsDir} and picks the map in the match, or the server is started with --custom-map <file>.";
    }

    // ---- Build tab -------------------------------------------------------------------------------------------------------------------------------------------

    Control BuildTab()
    {
        var v = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        v.AddChild(new Label { Text = "Click a piece, then click in the 3D view to place it (right click or Esc stops). Click a block to stack on it. Placed pieces snap to the grid on the Tools tab.", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) });
        _buildHint = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) };
        void Section(string title, params string[] ids)
        {
            v.AddChild(Head(title));
            var grid = new GridContainer { Columns = 2 };
            foreach (var id in ids)
            {
                var p = PiecePreset.Find(id)!;
                var b = new Button { Text = p.Label, TooltipText = p.Tip, SizeFlagsHorizontal = SizeFlags.ExpandFill, ToggleMode = true };
                b.Pressed += () => ArmPreset(p);
                _presetButtons[p.Id] = b;
                grid.AddChild(b);
            }
            v.AddChild(grid);
        }
        Section("Ground and cover", "floor", "wall", "cover", "ramp", "column", "invisible");
        Section("Spawns", "spawn-marines", "spawn-chaos", "door-marines", "door-chaos");
        Section("Objectives and hazards", "point", "kill");
        v.AddChild(_buildHint);
        v.AddChild(new Label { Text = "Select a piece and use the Object tab to type its position, turn and size (metres). Rotation Z turns it; spawn areas and ramps face their green arrow (+Y).", AutowrapMode = TextServer.AutowrapMode.WordSmart, CustomMinimumSize = new Vector2(270, 0) });
        return Scroll(v);
    }

    readonly Dictionary<string, Button> _presetButtons = new();

    void ArmPreset(PiecePreset p)
    {
        if (_plugin.Session is null) { _status.Text = "Make a map first: Start tab, New map."; foreach (var b in _presetButtons.Values) b.SetPressedNoSignal(false); ShowTab(DockTab.Start); return; }
        Arm(new LibraryItem(LibraryKind.Piece, p.Id, p.Label));
        if (_buildHint is not null) _buildHint.Text = $"Placing: {p.Label}. {p.Tip}";
    }

    void RefreshBuild()
    {
        foreach (var (id, b) in _presetButtons) b.SetPressedNoSignal(MapHost.Armed is { Kind: LibraryKind.Piece } a && a.Id == id && MapHost.Tool == Tool.Place);
        if (_buildHint is not null && !(MapHost.Armed is { Kind: LibraryKind.Piece } && MapHost.Tool == Tool.Place)) _buildHint.Text = "";
    }
}

static class ControlExtensions
{
    public static T With<T>(this T c, Action<T> f) { f(c); return c; }
}

