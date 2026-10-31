using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Customization;
using SmSim;

namespace SmClient.FrontEnd;

/// <summary>
/// <c>--ui-selftest</c> (test aid, run headless): drives the real screens through their own buttons and dialogs (the signals a click or Enter sends) and checks the
/// profile session afterwards: create a class in an empty slot, choose items and perks (the True Grit rule), rename, copy, save, clear, revert, the unsaved prompt,
/// the appearance edits and the emblem library. Prints one PASS / FAIL line per step and quits with a non-zero code if any failed. It never writes outside the
/// profile directory the run was given (<c>--profile</c>); the default profile is refused. The run starts from a default profile in memory, so the result does not depend on what
/// an earlier run left in that file (which its own saves then overwrite).
/// </summary>
public sealed partial class UiSelfTest : Node
{
    readonly FrontEndView _front;
    readonly CustomizerServices _services;
    int _failed, _passed;

    public UiSelfTest(FrontEndView front, CustomizerServices services) { _front = front; _services = services; Name = "UiSelfTest"; }

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int count = 3) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 && !ok ? " - " + detail : "")}");
    }

    static Button? FindButton(Node root, Func<Button, bool> match)
    {
        if (root is Button b && b.IsVisibleInTree() && match(b)) return b;
        foreach (var child in root.GetChildren()) if (FindButton(child, match) is { } found) return found;
        return null;
    }

    static LineEdit? FindEdit(Node root)
    {
        if (root is LineEdit e && e.IsVisibleInTree()) return e;
        foreach (var child in root.GetChildren()) if (FindEdit(child) is { } found) return found;
        return null;
    }

    /// <summary>The open dialog (a sibling of the screens), or null.</summary>
    UiKit.Dialog? OpenDialog() => _front.GetChildren().OfType<UiKit.Dialog>().FirstOrDefault(d => !d.IsQueuedForDeletion());

    Button? ClickDialog(string text)
    {
        var d = OpenDialog();
        var b = d is null ? null : FindButton(d, x => x.Text == text);
        b?.EmitSignal(BaseButton.SignalName.Pressed);
        return b;
    }

    Button? Click(Func<Button, bool> match, Node? root = null)
    {
        var b = FindButton(root ?? _front, match);
        if (b is null) return null;
        if (b.Disabled) return b;
        if (b.ToggleMode) b.ButtonPressed = true;   // a toggle sends Toggled
        else b.EmitSignal(BaseButton.SignalName.Pressed);
        return b;
    }

    /// <summary>Where a control's centre is in window coordinates (what a mouse event carries).</summary>
    Vector2 WindowPoint(Control c) => GetViewport().GetFinalTransform() * (c.GetGlobalTransformWithCanvas() * (c.Size / 2f));

    /// <summary>A real left click: the mouse moves onto the control and presses and releases there, through the viewport's own input path (what a player's mouse does).</summary>
    async Task MouseClick(Control c)
    {
        var at = WindowPoint(c);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
        await Frames(2);
    }

    /// <summary>One mouse-wheel notch down over a control.</summary>
    async Task MouseWheel(Control c, MouseButton direction = MouseButton.WheelDown)
    {
        var at = WindowPoint(c);
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        await Frames(2);
        Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = direction, Pressed = true, Factor = 1f });
        Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = direction, Pressed = false, Factor = 1f });
        await Frames(3);
    }

    async Task RunAsync()
    {
        try { await Run(); }
        catch (Exception ex) { _failed++; GD.Print($"SELFTEST FAIL exception {ex}"); }
        GD.Print($"SELFTEST done: {_passed} passed, {_failed} failed");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    async Task Run()
    {
        for (int i = 0; i < 600 && !_services.Ready; i++) await Frames(1);
        Check("install read", _services.Ready, _services.Error ?? "");
        if (!_services.Ready) return;
        if (!_services.UsesProfileFile) { Check("a --profile file is required", false, "the self-test never touches the default profile"); return; }
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        // The run starts from the default profile, whatever the --profile file holds: a second run on the same file (the first one saves slots 4 and 7 into it) must
        // see the same empty slots, or every step that starts from "slot 4 is empty" fails. The file is only replaced by the run's own saves.
        session.Seed(CustomizationProfile.Default(catalog));
        await Frames(10);
        var creator = _front.Creator!;
        _front.ShowCreator();
        await Frames(5);

        // 1. an empty slot becomes an Assault class through the class rows.
        creator.OpenSelection(Selection.CustomSlot(4));
        await Frames(3);
        Check("slot 4 starts empty", session.Working.ClassAt(4) is null);
        var assaultRow = FindButton(creator, b => b is UiKit.Row && b.Text.Contains("Assault", StringComparison.OrdinalIgnoreCase));
        Check("class rows are listed for an empty slot", assaultRow is not null);
        assaultRow?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check("choosing a class creates it", session.Working.ClassAt(4)?.Role == "assault");
        Check("the session is dirty and Save is enabled", session.Dirty && FindButton(creator, b => b.Text.StartsWith("SAVE"))?.Disabled == false);

        // 2. the pistol-slot bolter needs True Grit.
        creator.OpenStep("weapons");
        await Frames(3);
        var pistolBolter = FindButton(creator, b => b is UiKit.Row && b.Text.StartsWith("Bolter", StringComparison.OrdinalIgnoreCase) && !b.Text.Contains("Relic") && b.Text.Length < 12);
        Check("the pistol-slot Bolter is listed", pistolBolter is not null);
        pistolBolter?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check("it cannot be chosen without True Grit", session.Working.ClassAt(4)?.Pistol is null);
        creator.OpenStep("perks");
        await Frames(3);
        var grit = FindButton(creator, b => b is UiKit.Row && b.Text.Contains("True Grit", StringComparison.OrdinalIgnoreCase));
        Check("True Grit is listed as a perk", grit is not null);
        // Perk 2 first (any perk in either slot), then back to the weapons.
        Click(b => b.ToggleMode && b.Text.StartsWith("Perk 2"));
        await Frames(4);
        FindButton(creator, b => b is UiKit.Row && b.Text.Contains("True Grit", StringComparison.OrdinalIgnoreCase))?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check("True Grit goes into perk slot 2", session.Working.ClassAt(4)?.Perk(1) == "mp_perk_bolter_in_place_of_pistol", string.Join(",", session.Working.ClassAt(4)?.Perks ?? []));
        creator.OpenStep("weapons");
        await Frames(3);
        FindButton(creator, b => b is UiKit.Row && b.Text.StartsWith("Bolter", StringComparison.OrdinalIgnoreCase) && b.Text.Length < 12)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check("with True Grit the pistol Bolter is chosen", session.Working.ClassAt(4)?.Pistol is { } p && p.Contains("pistol_bolter"), session.Working.ClassAt(4)?.Pistol ?? "null");
        // The same perk in both slots is refused.
        creator.OpenStep("perks");
        await Frames(3);
        Click(b => b.ToggleMode && b.Text.StartsWith("Perk 1"));
        await Frames(4);
        FindButton(creator, b => b is UiKit.Row && b.Text.Contains("True Grit", StringComparison.OrdinalIgnoreCase))?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        var perksNow = session.Working.ClassAt(4)!.Perks;
        Check("a perk never sits in both slots", perksNow.Count(x => x == "mp_perk_bolter_in_place_of_pistol") <= 1, string.Join(",", perksNow));
        // Choosing a perk that is already in a slot clears that slot; True Grit going takes the pistol Bolter with it.
        Check("choosing a chosen perk again clears it and resets the pistol", session.Working.ClassAt(4)!.Perk(1) is null && session.Working.ClassAt(4)!.Pistol is null);
        Click(b => b.ToggleMode && b.Text.StartsWith("Perk 2"));
        await Frames(4);
        FindButton(creator, b => b is UiKit.Row && b.Text.Contains("True Grit", StringComparison.OrdinalIgnoreCase))?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        creator.OpenStep("weapons");
        await Frames(3);
        FindButton(creator, b => b is UiKit.Row && b.Text.StartsWith("Bolter", StringComparison.OrdinalIgnoreCase) && b.Text.Length < 12)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(3);
        Check("the pistol Bolter is back", session.Working.ClassAt(4)?.Pistol is not null);

        // 3. rename through the prompt.
        Click(b => b.Text.StartsWith("Rename"));
        await Frames(3);
        var edit = FindEdit(_front);
        Check("the rename prompt opens", edit is not null);
        if (edit is not null) { edit.Text = "  Zed the Bold  "; edit.EmitSignal(LineEdit.SignalName.TextSubmitted, edit.Text); }
        await Frames(4);
        Check("the loadout is renamed (trimmed)", session.Working.ClassAt(4)?.Name == "Zed the Bold", session.Working.ClassAt(4)?.Name ?? "null");

        // 4. copy to slot 7.
        Click(b => b.Text.StartsWith("Copy to"));
        await Frames(3);
        edit = FindEdit(_front);
        if (edit is not null) { edit.Text = "7"; edit.EmitSignal(LineEdit.SignalName.TextSubmitted, "7"); }
        await Frames(4);
        Check("copy to slot 7", session.Working.ClassAt(7)?.Name == "Zed the Bold (copy)", session.Working.ClassAt(7)?.Name ?? "null");

        // 5. save writes the file and clears the unsaved mark.
        Click(b => b.Text.StartsWith("SAVE"));
        await Frames(3);
        Check("save writes the profile file", System.IO.File.Exists(_services.Store.ProfilePath) && !session.Dirty);
        var reloaded = ProfileSession.Open(catalog, _services.Store);
        Check("the saved profile loads back", reloaded.Working.ClassAt(4)?.Name == "Zed the Bold" && reloaded.Working.ClassAt(7) is not null);

        // 6. set active and clear.
        creator.OpenSelection(Selection.CustomSlot(4));
        await Frames(3);
        Click(b => b.Text == "SET ACTIVE");
        await Frames(3);
        Check("set active selects slot 4", session.Working.Selected == Selection.CustomSlot(4));
        creator.OpenSelection(Selection.CustomSlot(7));
        await Frames(3);
        Click(b => b.Text == "Clear");
        await Frames(3);
        Check("clear asks first", OpenDialog() is not null);
        ClickDialog("Clear");
        await Frames(4);
        Check("clear empties the slot after the confirm", session.Working.ClassAt(7) is null);

        // 7. revert.
        session.Save();
        creator.OpenSelection(Selection.CustomSlot(4));
        await Frames(3);
        var resetButton = Click(b => b.Text == "Reset");
        await Frames(3);
        Check("reset asks first", resetButton is { Disabled: false } && OpenDialog() is not null, $"disabled={resetButton?.Disabled}");
        ClickDialog("Reset");
        await Frames(4);
        Check("reset makes the slot dirty", session.Dirty);
        Click(b => b.Text == "Revert");
        await Frames(3);
        ClickDialog("Revert");
        await Frames(4);
        Check("revert drops the edit", !session.Dirty && session.Working.ClassAt(4)?.Pistol is not null);

        // 8. the unsaved prompt on Back.
        session.Edit(p => LoadoutEdits.Rename(p, 4, "Unsaved").Profile);
        await Frames(3);
        creator.Back();
        await Frames(4);
        var dialog = OpenDialog();
        Check("leaving with edits asks Save / Discard / Cancel", dialog is not null && new[] { "Save", "Discard", "Cancel" }.All(t => FindButton(dialog, b => b.Text == t) is not null));
        ClickDialog("Discard");
        await Frames(4);
        Check("Discard reverts and shows the Join screen", !session.Dirty && session.Working.ClassAt(4)?.Name == "Zed the Bold" && _front.ShowsJoin);

        // 9. the appearance editor: a preset, a colour, a badge, then undo.
        _front.ShowCreator();
        creator.OpenSelection(Selection.CustomSlot(4));
        await Frames(3);
        _front.ShowAppearance();
        await Frames(8);
        var appearance = _front.Appearance!;
        appearance.OpenSection("chapter");
        await Frames(5);
        var preset = FindButton(appearance, b => b is UiKit.Row && b.Text.Contains("Ultramarines", StringComparison.OrdinalIgnoreCase));
        preset?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        var look = session.Working.Appearance(session.Working.ClassAt(4)?.Appearance.SpaceMarine);
        Check("choosing a chapter creates a look with its paint", look is not null && look.PresetApplied is not null && look.Paint.Count > 0, look?.PresetApplied ?? "null");
        appearance.OpenSection("colours");
        await Frames(5);
        FindButton(appearance, b => b is UiKit.ColourButton)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        look = session.Working.Appearance(session.Working.ClassAt(4)?.Appearance.SpaceMarine);
        Check("a palette colour applies to all parts", look is not null && look.Paint.Count == 9 && look.PresetApplied is null);
        await PackPaintChecks(appearance);
        appearance.OpenSection("badges");
        await Frames(5);
        Click(b => b.Text == "None" && b is UiKit.Row);
        await Frames(4);
        look = session.Working.Appearance(session.Working.ClassAt(4)?.Appearance.SpaceMarine);
        Check("None clears the badge", look?.Decals.GetValueOrDefault("leftshoulder")?.None == true);
        Click(b => b.Text == "Undo");
        await Frames(4);
        look = session.Working.Appearance(session.Working.ClassAt(4)?.Appearance.SpaceMarine);
        Check("undo steps back", look?.Decals.GetValueOrDefault("leftshoulder")?.None != true);
        // The wire form of the look: the join plan carries it.
        var plan = _services.PlanFor(Selection.CustomSlot(4));
        Check("the join plan carries the look and the class", plan is not null && !plan.SpaceMarineLook.IsDefault && plan.ClassName is not null, plan?.ClassName ?? "null");
        // An emblem on a look: one hash per look on the wire, the same emblem on both pads; saved so a later --connect --profile run can show it reaching the Hello.
        var emblem = EmblemPipeline.Add(_services.Store, EmblemPipeline.ToBc3(Enumerable.Repeat((byte)200, 128 * 128 * 4).ToArray())).Hash;
        Check("an emblem is stored in the library", !emblem.IsNone && EmblemPipeline.Library(_services.Store).Contains(emblem));
        var id = session.Working.ClassAt(4)!.Appearance.SpaceMarine!;
        session.Edit(p => AppearanceEdits.ReplaceAppearance(p, AppearanceEdits.ApplyEmblem(AppearanceEdits.ApplyEmblem(p.Appearance(id)!, ["leftshoulder"], "11111111111111111111111111111111").Look, ["rightshoulder"], emblem.ToHex()).Look));
        var withEmblem = _services.PlanFor(Selection.CustomSlot(4));
        Check("both pads show the newest emblem and the plan carries it", withEmblem is { UsesEmblem: true } && withEmblem.SpaceMarineLook.Emblem == emblem && withEmblem.SpaceMarineLook.LeftDecal == withEmblem.SpaceMarineLook.RightDecal);
        Check("the profile saves with the look and the emblem", session.Save() is null);

        await LoadoutChecks();
        await MouseChecks();
        await DefaultsChecks();
        await ContentChecks();
        await PreviewPoseChecks();
        await OrkChecks();
        await HostChecks();
        await ControlsChecks();   // ui/keybinds (UiSelfTest.Controls.cs)
        await FitChecks();
        await ScoreBoxChecks();   // ui/score-box-center: the HUD score box holds its block (UiSelfTest.ScoreBox.cs)
    }

    /// <summary>
    /// fix/connect-timeout, fix/hosting-bots-logs: the HOST panel's BOTS stepper (no bots by default in every reach, never remembered, 0 to 15, - / + at the ends
    /// disabled), the Easy default, STOP SERVER disabled while nothing of this game runs, no live + BOT / - BOT on the running row, and an old saved 4v4 fill that
    /// brings back no bots. Needs a server executable (<c>--server-exe</c> in a dev run); starts nothing. Leaves the saved choices at the defaults.
    /// </summary>
    async Task HostChecks()
    {
        _front.ShowJoin();
        await Frames(4);
        var panel = FindNode<HostPanel>(_front);
        if (panel is null) { GD.Print("SELFTEST SKIP host panel: no server executable next to the client (pass --server-exe <SmServer> to include it)"); return; }
        var original = _services.Settings;
        Check("host: This PC only starts no bots by default, on Easy", panel.BotCount == 0 && panel.Difficulty == BotDifficulty.Easy && HostBots.Plan(panel.BotCount, false).Count == 0,
              $"{panel.BotCount} {panel.Difficulty}");
        Check("host: the BOTS stepper shows No bots", panel.BotsShown == ("No bots", "Easy"), panel.BotsShown.ToString());
        Check("host: - is disabled at no bots, + is enabled", FindButton(panel, b => b.Name == "BotsFewer") is { Disabled: true } && FindButton(panel, b => b.Name == "BotsMore") is { Disabled: false });
        Check("host: STOP SERVER is disabled with no server of this game running", FindButton(panel, b => b.Text == "STOP SERVER")?.Disabled == true || HostedServer.Running);
        Check("host: no live + BOT / - BOT buttons", FindButton(panel, b => b.Text is "+ BOT" or "- BOT") is null);
        panel.ShowRunningForTest("Hosting Annihilation on port 27015, this PC only · no bots");
        await Frames(3);
        Check("host: the running row has JOIN YOUR SERVER and STOP SERVER and no bot buttons", FindButton(panel.RunningActions, b => b.Text == "STOP SERVER") is not null
              && FindButton(panel.RunningActions, b => b.Text == "JOIN YOUR SERVER") is not null && FindButton(panel.RunningActions, b => b.Text is "+ BOT" or "- BOT") is null);
        panel.ShowRunningForTest(null);
        await Frames(2);

        panel.ChooseForTest(1, null);
        await Frames(2);
        Check("host: + gives 1 bot", panel.BotCount == 1 && panel.BotsShown.Count == "1 bot", panel.BotsShown.ToString());
        panel.ChooseForTest(20, null);
        await Frames(2);
        Check("host: the stepper stops at 15, + disabled there", panel.BotCount == HostBots.MaxBots && panel.BotsShown.Count == "15 bots" && FindButton(panel, b => b.Name == "BotsMore") is { Disabled: true });
        Press(panel, "LAN / Internet");
        await Frames(2);
        Check("host: the number stays when the reach changes", panel.BotCount == HostBots.MaxBots);
        Press(panel, "This PC only");
        panel.ChooseForTest(-20, BotDifficulty.Normal);
        await Frames(2);
        Check("host: - goes back to No bots", panel.BotCount == 0 && panel.BotsShown.Count == "No bots");
        Check("host: the difficulty is remembered", FrontEndSettings.Load(_services.SettingsDirectory).BotDifficulty == BotDifficulty.Normal);
        // An earlier build's saved fill ("hostBotsLocal": "FourVsFour" = 7 bots) is ignored and dropped at the next save.
        string file = System.IO.Path.Combine(_services.SettingsDirectory, FrontEndSettings.FileName);
        try
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(file))!.AsObject();
            json["hostBotsLocal"] = "FourVsFour";
            json["hostBotsLan"] = "EightVsEight";
            System.IO.File.WriteAllText(file, json.ToJsonString());
            Check("host: an old saved 4v4 fill is not read back", FrontEndSettings.Load(_services.SettingsDirectory).Save(_services.SettingsDirectory) is null
                  && !System.IO.File.ReadAllText(file).Contains("hostBots", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Check("host: an old saved 4v4 fill is not read back", false, e.Message);
        }

        // How the panel fits the window (setup with the BOTS row, running row) is checked at every size by FitChecks.
        _services.UpdateSettings(_ => original with { BotDifficulty = BotDifficulty.Easy });
        panel.ChooseForTest(0, BotDifficulty.Easy);
        await Frames(2);
        Check("host: the choices are back at the defaults", FrontEndSettings.Load(_services.SettingsDirectory) is { BotDifficulty: BotDifficulty.Easy } && panel.BotCount == 0);
    }

    /// <summary>A toggle button pressed as a click does it (pressed state, then the Pressed signal the panel listens to).</summary>
    static void Press(Node root, string text)
    {
        if (FindButton(root, b => b.Text == text) is not { Disabled: false } b) return;
        if (b.ToggleMode) b.SetPressedNoSignal(true);
        b.EmitSignal(BaseButton.SignalName.Pressed);
    }

    static T? FindNode<T>(Node root) where T : Node
    {
        if (root is T t) return t;
        foreach (var child in root.GetChildren()) if (FindNode<T>(child) is { } found) return found;
        return null;
    }

    /// <summary>
    /// Window sizes the front end must fit (fix/frontend-fit, release issue 1: the Join screen's bottom row was cut off at about 1600 x 930): the common 16:9 sizes, the
    /// owner's window, an ultrawide, a 5:4 and 4K. The UI is a 1920 x 1080 space stretched to the window with the aspect kept (more room on the long side), so these
    /// cover what Windows display scaling changes too: it changes only the window's size in pixels.
    /// </summary>
    static readonly Vector2I[] FitSizes = [new(1280, 720), new(1600, 900), new(1600, 930), new(1920, 1080), new(2560, 1080), new(1280, 1024), new(3840, 2160)];

    /// <summary>
    /// Lays out the Join screen, the creator (also with the in-match picker's banner, DEPLOY, RESUME and LEAVE MATCH) and the appearance editor at each of
    /// <see cref="FitSizes"/> and checks that every button, field and slider is inside the window, or inside a scroll list that is itself inside the window.
    /// A loadout with the longest possible name is active, so the ACTIVE lines are as long as they get.
    /// </summary>
    async Task FitChecks()
    {
        var window = GetWindow();
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        var creator = _front.Creator!;
        var appearance = _front.Appearance!;
        var kept = session.Working;
        var original = window.Size;
        string longName = new('W', CustomizationProfile.MaxNameLength);
        var withLong = LoadoutEdits.Create(kept.ClassAt(10) is null ? kept : LoadoutEdits.Clear(kept, 10).Profile, 10, "tactical", catalog).Profile;
        withLong = LoadoutEdits.Select(LoadoutEdits.Rename(withLong, 10, longName).Profile, Selection.CustomSlot(10));
        session.Seed(withLong);
        try
        {
            foreach (var size in FitSizes)
            {
                window.Size = size;
                await Frames(4);
                var view = GetViewport().GetVisibleRect();
                string at = $"{size.X}x{size.Y} (UI {view.Size.X:F0}x{view.Size.Y:F0})";
                Check($"fit {at}: the window takes the size", window.Size == size, window.Size.ToString());

                _front.ShowJoin();
                _front.ShowMessage("");
                await Frames(4);
                Check($"fit {at}: the Join screen has the Host panel (forced on in the self-test)", _front.HasHostPanel);
                CheckJoinFits($"fit {at}: Join screen with the Host panel", view);
                if (Mathf.IsEqualApprox(view.Size.Y, UiKit.VirtualHeight) && _front.JoinMiddle is { } middle && _front.JoinMiddleContent is { } content)
                    Check($"fit {at}: a 16:9-or-wider window shows the whole Join screen without scrolling", !middle.GetVScrollBar().Visible,
                        $"content {content.GetCombinedMinimumSize().Y:F0} in {middle.Size.Y:F0}");
                // A long message (a failed join's reason) takes room above the bar: the middle gives it up and scrolls, the bar stays.
                _front.ShowMessage("Could not connect to the server. " + string.Concat(Enumerable.Repeat("The server did not answer in time; check the address and that it runs the same content profile. ", 3)), true);
                await Frames(4);
                CheckJoinFits($"fit {at}: Join screen with a three-line message", view);
                _front.ShowMessage("");
                // The Host panel with a server running: the longest status line and four more buttons in its action row (what later server controls add).
                if (_front.JoinHostPanel is { } host)
                {
                    host.ShowRunningForTest("Server running: Seize Ground, UDP 27015, all interfaces, content extended (starting...) - waiting for the game files", 4);
                    await Frames(4);
                    CheckJoinFits($"fit {at}: Join screen with a running server and 4 extra server buttons", view);
                    host.ShowRunningForTest(null);
                    await Frames(2);
                }

                _front.ShowCreator();
                creator.OpenSelection(Selection.CustomSlot(10));
                await Frames(4);
                CheckFits($"fit {at}: class creator", creator, view);
                await PerkCardFitChecks(view, at);   // ui/hud-2: every perk card of every class fits (UiSelfTest.PerkCards.cs)
                _front.ShowCreator();
                creator.OpenSelection(Selection.CustomSlot(10));
                await Frames(3);
                if (_services.OffersOrks)
                {
                    // ui/customizer-ork: the Ork Boy's creator (four class tabs, the Orks faction, his fifth step LOOK).
                    creator.OpenSelection(Selection.DefaultFor(OrkLoadout.Role));
                    creator.OpenStep("LOOK");
                    await Frames(4);
                    CheckFits($"fit {at}: class creator with the Ork Boy's LOOK step", creator, view);
                    creator.OpenSelection(Selection.CustomSlot(10));
                }
                creator.Banner = "Applies when you respawn - RESPAWN IN 4";
                creator.Deploy = _ => { };
                creator.Leave = () => { };
                creator.BackText = "RESUME (Esc)";
                creator.OpenSelection(Selection.CustomSlot(10));
                await Frames(4);
                CheckFits($"fit {at}: in-match loadout picker", creator, view);
                creator.Banner = "";
                creator.Deploy = null;
                creator.Leave = null;
                creator.BackText = "BACK (Esc)";
                creator.OpenSelection(Selection.CustomSlot(10));

                _front.ShowAppearance();
                await Frames(6);
                CheckFits($"fit {at}: appearance editor", appearance, view);
                await ControlsFitChecks(view, at);   // ui/keybinds
            }
        }
        finally
        {
            window.Size = original;
            _front.JoinHostPanel?.ShowRunningForTest(null);
            session.Seed(kept);
            await Frames(3);
            _front.ShowJoin();
        }
    }

    /// <summary>The Join screen's controls fit, JOIN is in the window outside the scrolling part, and the scrolling part keeps a usable height.</summary>
    void CheckJoinFits(string name, Rect2 view)
    {
        CheckFits(name, _front.JoinScreen!, view);
        var join = FindButton(_front.JoinScreen!, b => b.Text == "JOIN");
        Check($"{name}: JOIN is in the window and outside the scrolling part", join is not null && Encloses(view, join.GetGlobalRect()) && !IsInside(join, _front.JoinMiddle));
        Check($"{name}: the scrolling part is at least 300 high", _front.JoinMiddle is { } m && m.Size.Y >= 300f, $"{_front.JoinMiddle?.Size.Y:F0}");
    }

    static bool Encloses(Rect2 outer, Rect2 inner) =>
        inner.Position.X >= outer.Position.X - 0.5f && inner.Position.Y >= outer.Position.Y - 0.5f && inner.End.X <= outer.End.X + 0.5f && inner.End.Y <= outer.End.Y + 0.5f;

    static bool IsInside(Node node, Node? ancestor)
    {
        for (var n = node.GetParent(); n is not null; n = n.GetParent()) if (n == ancestor) return true;
        return false;
    }

    /// <summary>
    /// Every visible control a player uses (buttons, fields, sliders) of <paramref name="screen"/> is inside the window; one in a scroll list is inside the list across
    /// (the lists never scroll sideways) and the list is inside the window (rows below its fold are reached by scrolling).
    /// </summary>
    void CheckFits(string name, Control screen, Rect2 view)
    {
        var problems = new List<string>();
        int count = 0;
        void Walk(Node node)
        {
            if (node is CanvasItem { Visible: false }) return;
            if (node is BaseButton or LineEdit or Godot.Range and not ScrollBar && node is Control c)
            {
                count++;
                var rect = c.GetGlobalRect();
                ScrollContainer? scroll = null;
                for (var n = c.GetParent(); n is not null && scroll is null; n = n.GetParent()) scroll = n as ScrollContainer;
                bool ok = scroll is null
                    ? Encloses(view, rect)
                    : Encloses(view, scroll.GetGlobalRect()) && rect.Position.X >= scroll.GlobalPosition.X - 0.5f && rect.End.X <= scroll.GetGlobalRect().End.X + 0.5f;
                ok &= rect.Size.X > 0f && rect.Size.Y > 0f;   // a control squeezed to nothing cannot be clicked
                if (!ok) problems.Add($"{c.GetType().Name} \"{(c as Button)?.Text.Replace('\n', ' ') ?? c.Name}\" at {rect.Position.X:F0},{rect.Position.Y:F0} size {rect.Size.X:F0}x{rect.Size.Y:F0}");
                // The class creator's role tabs (one line each, clipped) must show their whole label (ui/hud-fidelity review: "Tactical Marine / Chaos Space Marine" was cut off).
                else if (c is Button { ClipText: true } clipped && clipped.Name.ToString().StartsWith("ClassTab", StringComparison.Ordinal) && UiKit.TextNeed(clipped) > rect.Size.X + 1f)
                    problems.Add($"Button \"{clipped.Text.Replace('\n', ' ')}\" cut off: needs {UiKit.TextNeed(clipped):F0} px, has {rect.Size.X:F0}");
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }
        Walk(screen);
        Check($"{name}: all {count} controls in the window", count > 0 && problems.Count == 0, $"view {view.Size.X:F0}x{view.Size.Y:F0}; outside: {string.Join("; ", problems.Take(6))}");
    }

    static void FindButtons(Node root, List<Button> into)
    {
        if (root is Button b && b.IsVisibleInTree()) into.Add(b);
        foreach (var child in root.GetChildren()) FindButtons(child, into);
    }

    /// <summary>The swatch colour of a palette button (its normal style's fill).</summary>
    static Color SwatchOf(Button b) => b.GetThemeStylebox("normal") is StyleBoxFlat flat ? flat.BgColor : Colors.Transparent;

    /// <summary>
    /// The owner's rule of 2026-10-30: the worn jump pack takes the backpack slot's colours (SmContent.Customization.WornPackPaint). Slot 4 is an Assault class, so the appearance editor's
    /// preview wears a jump pack: choosing a colour for all parts must set the pack's material colour to that swatch, and a second, different swatch must change it again.
    /// </summary>
    async Task PackPaintChecks(AppearanceView appearance)
    {
        var preview = appearance.Preview;
        async Task<Color?> PackAfter(Color? previous)
        {
            for (int i = 0; i < 900; i++)
            {
                await Frames(1);
                if (preview.Loaded && preview.PackPrimaryColour is { } now && (previous is null || now != previous)) return now;
            }
            return preview.PackPrimaryColour;
        }
        appearance.OpenSection("colours");
        await Frames(5);
        var swatches = new List<Button>();
        FindButtons(appearance, swatches);
        swatches = swatches.Where(b => b is UiKit.ColourButton).ToList();
        Check("the colour section lists swatches", swatches.Count > 20, swatches.Count.ToString());
        if (swatches.Count < 21) return;
        var first = swatches[0];
        var second = swatches[Math.Min(swatches.Count - 1, 17)];
        Color firstColour = SwatchOf(first), secondColour = SwatchOf(second);
        first.EmitSignal(BaseButton.SignalName.Pressed);
        var packFirst = await PackAfter(null);
        Check("the Assault preview wears a painted jump pack", packFirst is not null);
        Check("the pack is drawn in the backpack colour that was chosen", packFirst is { } a && Near(a, firstColour), $"{packFirst} vs {firstColour}");
        Check("the pack paint is the backpack's paint of the look", preview.PackPaint is { Primary: { } chest } && Near(new Color(chest.Diffuse[0], chest.Diffuse[1], chest.Diffuse[2]), firstColour));
        swatches.Clear();
        FindButtons(appearance, swatches);
        var again = swatches.Where(b => b is UiKit.ColourButton && Near(SwatchOf(b), secondColour)).FirstOrDefault();
        again?.EmitSignal(BaseButton.SignalName.Pressed);
        var packSecond = await PackAfter(packFirst);
        Check("changing the backpack colour changes the pack's material colour", packSecond is { } b2 && packFirst is { } b1 && !Near(b1, b2) && Near(b2, secondColour), $"{packFirst} -> {packSecond} (wanted {secondColour})");
    }

    static bool Near(Color a, Color b) => Math.Abs(a.R - b.R) < 0.004f && Math.Abs(a.G - b.G) < 0.004f && Math.Abs(a.B - b.B) < 0.004f;

    /// <summary>
    /// The creator opens on a DEFAULTS loadout (the active one of a new profile). Choosing a weapon or perk there used to be refused with a one-line "Defaults can't be changed",
    /// which is how the owner met it ("i can't select the power sword, or perks"): it now makes an editable copy in the first empty slot (the active loadout when these defaults
    /// were it) and applies the choice. Also the perks' simulation state is the simulation's (34 active), not a blanket NOT ACTIVE YET.
    /// </summary>
    async Task DefaultsChecks()
    {
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        var creator = _front.Creator!;
        var before = session.Working;
        _front.ShowCreator();
        creator.SetPreviewRace(CustomizationCatalog.SpaceMarine);
        session.Edit(p => LoadoutEdits.Select(p, Selection.DefaultFor("assault")));
        creator.OpenSelection(Selection.DefaultFor("assault"));
        creator.OpenStep("weapons");
        await Frames(4);
        int slot = LoadoutEdits.FirstEmptySlot(session.Working);
        Check("a slot is free for the copy", slot > 0);
        var sword = FindButton(creator, b => b is UiKit.Row && b.Text == "Power Sword");
        Check("DEFAULTS Assault lists the Power Sword", sword is not null);
        sword?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        var made = session.Working.ClassAt(slot);
        Check("choosing the Power Sword on DEFAULTS makes an editable copy with it", made is { Role: "assault", Melee: "mp_melee_powersword" }, made?.Melee ?? "null");
        Check("the copy is the open slot and, the defaults being active, the active loadout", creator.Open == Selection.CustomSlot(slot) && session.Working.Selected == Selection.CustomSlot(slot));
        var plan = _services.PlanFor();
        var served = plan is null ? [] : LoadoutIndices.Names(_services.Config!, plan.Choice.Class, "melee");
        Check("the join plan carries the Power Sword", plan is not null && plan.Choice.Melee >= 0 && plan.Choice.Melee < served.Count && ProfileWire.Neutral(served[plan.Choice.Melee]) == "mp_melee_powersword", plan?.Summary ?? "null");

        // A perk on DEFAULTS Tactical: the same copy, the perk in slot 1.
        session.Edit(p => LoadoutEdits.Select(p, Selection.DefaultFor("tactical")));
        creator.OpenSelection(Selection.DefaultFor("tactical"));
        creator.OpenStep("perks");
        await Frames(4);
        int second = LoadoutEdits.FirstEmptySlot(session.Working);
        var perkRow = FindButton(creator, b => b is UiKit.Row && b.Text == "Larraman's Blessing");
        Check("DEFAULTS Tactical lists Larraman's Blessing", perkRow is not null);
        perkRow?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
        Check("choosing a perk on DEFAULTS makes an editable copy with it", session.Working.ClassAt(second) is { Role: "tactical" } t && t.Perk(0) == "mp_perk_shield_recharge", string.Join(",", session.Working.ClassAt(second)?.Perks ?? []));

        // Reverting the copy goes back to the DEFAULTS it came from, not to an empty slot.
        Click(b => b.Text == "Revert");
        await Frames(3);
        ClickDialog("Revert");
        await Frames(4);
        Check("Revert drops the copy and returns to the DEFAULTS it came from", !session.Dirty && session.Working.ClassAt(second) is null && creator.Open == Selection.DefaultFor("tactical"), creator.Open.ToString());

        // The perks' states come from the simulation.
        var rows = LoadoutRows.Perks(catalog, "assault", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, _services.PerkActivities);
        int active = rows.Count(r => r.Status.Activity == PerkActivity.Active), inert = rows.Count(r => r.Status.Activity == PerkActivity.Inert);
        Check("most assault perks are active, not a blanket NOT ACTIVE YET", active >= 10 && inert <= 2, $"active {active}, inert {inert} of {rows.Count}");
        Check("Furious Charge is active", rows.Single(r => r.Name == "mp_perk_improved_bullrush").Status.Activity == PerkActivity.Active);
        var tactical = LoadoutRows.Perks(catalog, "tactical", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, _services.PerkActivities);
        Check("Rapid Deployment, Weapon Versatility, Teleport Homer and Tactical Readout are active (protocol 44)", new[] { "mp_perk_respawn_timer", "mp_perk_secondary_weapon", "mp_perk_friendly_spawn_target", "mp_perk_enemy_info" }.All(n => tactical.Single(r => r.Name == n).Active), string.Join(", ", tactical.Where(r => !r.Active).Select(r => r.Name)));
        Check("Signum Link is active (heavy)", LoadoutRows.Perks(catalog, "heavy", CustomizationCatalog.SpaceMarine, null, UnlockPolicy.All, _services.PerkActivities).Single(r => r.Name == "mp_perk_snap_to_increase").Active);

        // The list's scroll bar has a width: a list longer than its panel shows that it goes on.
        creator.OpenStep("weapons");
        creator.OpenSelection(Selection.DefaultFor("assault"));
        await Frames(4);
        var bar = FindScroll(creator)?.GetVScrollBar();
        Check("the list's scroll bar is wide enough to see", bar is not null && bar.GetCombinedMinimumSize().X >= 12, bar?.GetCombinedMinimumSize().ToString() ?? "null");

        session.Seed(before);
        creator.OpenSelection(Selection.CustomSlot(4));
        await Frames(2);
    }

    static string? stored0(ProfileSession session, int slot, string key) =>
        session.Working.ClassAt(slot) is { } c ? key switch { "primary" => c.Primary, "pistol" => c.Pistol, "melee" => c.Melee, _ => c.Equipment } : null;

    static ScrollContainer? FindScroll(Node root)
    {
        if (root is ScrollContainer s && s.IsVisibleInTree()) return s;
        foreach (var child in root.GetChildren()) if (FindScroll(child) is { } found) return found;
        return null;
    }

    /// <summary>
    /// The same choices with the real mouse (events through the viewport, not the button's signal): the wheel scrolls the list with the pointer over a row, a row
    /// below the fold is reached by scrolling, a click on the power sword and on a perk stores them.
    /// </summary>
    async Task MouseChecks()
    {
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        var creator = _front.Creator!;
        const int slot = 2;
        creator.SetPreviewRace(CustomizationCatalog.SpaceMarine);
        session.Edit(p => LoadoutEdits.Clear(p, slot).Profile);
        session.Edit(p => LoadoutEdits.Create(p, slot, "assault", catalog).Profile);
        creator.OpenSelection(Selection.CustomSlot(slot));
        creator.OpenStep("weapons");
        await Frames(4);
        var scroll = FindScroll(creator);
        Check("the list is a scroll container", scroll is not null);
        if (scroll is null) return;
        var sword = FindButton(creator, b => b is UiKit.Row && b.Text == "Power Sword");
        Check("the Power Sword row exists", sword is not null);
        if (sword is null) return;
        var plasma = FindButton(creator, b => b is UiKit.Row && b.Text == "Plasma Pistol")!;
        int before = scroll.ScrollVertical;
        bool visibleAtStart = scroll.GetGlobalRect().Encloses(sword.GetGlobalRect());
        for (int i = 0; i < 6 && !scroll.GetGlobalRect().Encloses(sword.GetGlobalRect()); i++) await MouseWheel(plasma);
        Check("the mouse wheel over a row scrolls the list", scroll.ScrollVertical > before || visibleAtStart, $"scroll {before} -> {scroll.ScrollVertical}");
        Check("the Power Sword row is fully in view after scrolling", scroll.GetGlobalRect().Encloses(sword.GetGlobalRect()));
        await MouseClick(sword);
        Check("a mouse click on Power Sword stores it", session.Working.ClassAt(slot)?.Melee == "mp_melee_powersword", session.Working.ClassAt(slot)?.Melee ?? "null");

        creator.OpenStep("perks");
        await Frames(4);
        var slotTwo = FindButton(creator, b => b.ToggleMode && b.Text.StartsWith("Perk 2"));
        if (slotTwo is not null) await MouseClick(slotTwo);
        bool Power(Button b) => b is UiKit.Row && b.Text.Contains("Power Sword", StringComparison.OrdinalIgnoreCase);
        bool FirstRow(Button b) => b is UiKit.Row && b.IsVisibleInTree() && scroll!.GetGlobalRect().Encloses(b.GetGlobalRect());
        scroll = FindScroll(creator)!;
        for (int i = 0; i < 20; i++)
        {
            var target = FindButton(creator, Power);
            if (target is not null && scroll.GetGlobalRect().Encloses(target.GetGlobalRect())) break;
            if (FindButton(creator, FirstRow) is { } hover) await MouseWheel(hover); else await Frames(2);
        }
        var perk = FindButton(creator, Power);
        Check("a weapon perk row can be scrolled into view", perk is not null && scroll.GetGlobalRect().Encloses(perk.GetGlobalRect()));
        if (perk is not null)
        {
            await MouseClick(perk);
            Check("a mouse click on a weapon perk stores it in perk slot 2", session.Working.ClassAt(slot)?.Perk(1) is { } p && p.Contains("power_sword"), string.Join(",", session.Working.ClassAt(slot)?.Perks ?? []));
        }
        session.Edit(p => LoadoutEdits.Clear(p, slot).Profile);
        await Frames(2);
    }

    /// <summary>
    /// Every weapon, grenade and perk of every class, on both sides, chosen through the creator's own rows (fix/customizer-weapons-perks, the owner's report of
    /// 2026-10-29: "i can't select the power sword, or perks"): the rows the creator lists for a class equal the choices the server's list for that class holds
    /// (the simulated ones), each row is a real button that can be pressed and stores the item, the join plan's wire index names that same item in the server's
    /// list, both perk slots take any perk of the class, and the True Grit rule holds. Slot 2 is used and cleared again.
    /// </summary>
    async Task LoadoutChecks()
    {
        var session = _services.Session!;
        var catalog = _services.Catalog!;
        var config = _services.Config!;
        var creator = _front.Creator!;
        const int slot = 2;
        _front.ShowCreator();
        await Frames(4);
        static string N(string name) => ProfileWire.Neutral(name);
        var stepOfKey = new Dictionary<string, string> { ["primary"] = "weapons", ["pistol"] = "weapons", ["melee"] = "weapons", ["equipment"] = "equipment" };
        foreach (string race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            creator.SetPreviewRace(race);
            await Frames(3);
            var side = ProfileWire.SideOf(race);
            foreach (string role in ProfileStore.Roles(catalog))
            {
                string tag = $"{race}/{role}";
                var options = catalog.Perks.ForRole(role, race);
                int cls = ProfileWire.ClassIndex(config, side, role);
                Check($"{tag}: the server has the class", options is not null && cls >= 0, $"cls={cls}");
                if (options is null || cls < 0) continue;
                session.Edit(p => LoadoutEdits.Clear(p, slot).Profile);
                session.Edit(p => LoadoutEdits.Create(p, slot, role, catalog).Profile);
                creator.OpenSelection(Selection.CustomSlot(slot));
                await Frames(3);

                // 1. what the creator lists equals what the server accepts.
                foreach (var key in LoadoutRows.SlotKeys)
                {
                    var listed = options.For(key).Items.Where(i => i.Simulated).Select(i => N(i.Name)).ToList();
                    var served = LoadoutIndices.Names(config, cls, key).Select(N).ToList();
                    if (key == "melee" && listed.Count == 0) served = served.Where(n => !n.Contains("core_abil", StringComparison.OrdinalIgnoreCase)).ToList();   // the Devastator's heavy evade is not a melee weapon
                    Check($"{tag}: {key} rows equal the server's choices", listed.SequenceEqual(served), $"listed [{string.Join(",", listed)}] served [{string.Join(",", served)}]");
                }

                // 2. every selectable item is a real, enabled row that stores the item and reaches the wire as the same item.
                foreach (var key in LoadoutRows.SlotKeys)
                {
                    var choices = options.For(key);
                    if (!choices.Selectable) continue;
                    foreach (var item in choices.Items.Where(i => i.Simulated))
                    {
                        string neutral = N(item.Name);
                        if (item.RequiredPerk is { } need)
                        {
                            session.Edit(p => LoadoutEdits.SetPerk(p, slot, 1, need, catalog).Profile);
                            await Frames(2);
                        }
                        creator.OpenStep(stepOfKey[key]);
                        await Frames(3);
                        string title = key == "equipment" ? LoadoutRows.EquipmentTitle(item) : item.DisplayName;
                        var row = FindButton(creator, b => b is UiKit.Row && b.Text == title);
                        Check($"{tag}: {key} {neutral} has a row", row is not null, title);
                        if (row is null) continue;
                        Check($"{tag}: {key} {neutral} row can be pressed", !row.Disabled);
                        row.EmitSignal(BaseButton.SignalName.Pressed);
                        await Frames(3);
                        var custom = session.Working.ClassAt(slot)!;
                        string? stored = key switch { "primary" => custom.Primary, "pistol" => custom.Pistol, "melee" => custom.Melee, _ => custom.Equipment };
                        // The row of the item already in effect is the selected one: pressing it changes nothing (the class default stays stored as none, which the wire sends as the default).
                        bool inEffect = LoadoutRows.Effective(catalog, options, key, session.Working.ClassAt(slot)) is { } now && now.Name == item.Name && stored0(session, slot, key) is null;
                        Check($"{tag}: pressing {neutral} stores it", stored == neutral || (inEffect && stored is null), stored ?? "null");
                        var wire = ProfileWire.ToLoadout(custom, config, side);
                        int index = key switch { "primary" => wire.Primary, "pistol" => wire.Pistol, "melee" => wire.Melee, _ => wire.Equipment };
                        var served = LoadoutIndices.Names(config, cls, key);
                        Check($"{tag}: {neutral} reaches the wire as that item", (index >= 0 && index < served.Count && N(served[index]) == neutral) || (index < 0 && stored is null && inEffect && served.Count > 0 && N(served[0]) == neutral), $"index {index}");
                        var resolved = Loadouts.Resolve(config, cls, wire);
                        int kept = key switch { "primary" => resolved.Primary, "pistol" => resolved.Pistol, "melee" => resolved.Melee, _ => resolved.Equipment };
                        Check($"{tag}: the server keeps {neutral}", kept == index, $"sent {index} kept {kept}");
                    }
                }

                // 3. both perk slots take any perk of the class; the wire and the server keep them.
                session.Edit(p => LoadoutEdits.Reset(p, slot).Profile);
                creator.OpenStep("perks");
                await Frames(3);
                var perks = options.Perks.Select(p => p.Name).ToList();
                int tried = 0;
                foreach (string perk in perks)
                {
                    foreach (int perkSlot in new[] { 0, 1 })
                    {
                        session.Edit(p => LoadoutEdits.Reset(p, slot).Profile);
                        await Frames(2);
                        Click(b => b.ToggleMode && b.Text.StartsWith($"Perk {perkSlot + 1}"));
                        await Frames(3);
                        string title = options.Perks.First(p => p.Name == perk).DisplayName;
                        var row = FindButton(creator, b => b is UiKit.Row && b.Text == title);
                        if (row is null || row.Disabled) { Check($"{tag}: perk {perk} slot {perkSlot + 1} has a row", false, title); continue; }
                        row.EmitSignal(BaseButton.SignalName.Pressed);
                        await Frames(3);
                        var custom = session.Working.ClassAt(slot)!;
                        var wire = ProfileWire.ToLoadout(custom, config, side);
                        var resolved = Loadouts.Resolve(config, cls, wire);
                        var served = LoadoutIndices.Names(config, cls, "perk");
                        int index = perkSlot == 0 ? resolved.Perk1 : resolved.Perk2;
                        // The class default perk shown in slot 1 is the server's default (-1) when the player did not pick it; the same perk in slot 2 is refused as a duplicate.
                        bool isDefault = options.DefaultPerk?.Name == perk;
                        bool sitsInSlot = custom.Perk(perkSlot) == perk || (isDefault && perkSlot == 0 && custom.Perk(0) is null);
                        bool onWire = (isDefault && perkSlot == 0 && resolved.Perk1 < 0) || (index >= 0 && index < served.Count && served[index] == perk);
                        if (isDefault && perkSlot == 1) { tried++; continue; }   // the default perk already sits in slot 1: not a second-slot choice
                        if (!sitsInSlot || !onWire) Check($"{tag}: perk {perk} in slot {perkSlot + 1}", false, $"stored [{string.Join(",", custom.Perks)}] wire {wire.Perk1}/{wire.Perk2} resolved {resolved.Perk1}/{resolved.Perk2}");
                        tried++;
                    }
                }
                Check($"{tag}: all {perks.Count} perks go into both slots", tried == perks.Count * 2, $"{tried} of {perks.Count * 2} taken");
            }
        }

        // 4. the True Grit rule at the server: the pistol Bolter is kept with the perk and dropped without it.
        int assault = ProfileWire.ClassIndex(config, Team.SpaceMarines, "assault");
        var gritChoice = LoadoutIndices.ToChoice(config, config.Classes[assault].Name, null, "mp_pistol_bolter", null, null, Loadouts.TrueGritPerk, null);
        var gritless = LoadoutIndices.ToChoice(config, config.Classes[assault].Name, null, "mp_pistol_bolter", null, null, null, null);
        Check("True Grit: the server keeps the pistol Bolter with the perk", gritChoice.Pistol >= 0 && Loadouts.Resolve(config, assault, gritChoice).Pistol == gritChoice.Pistol);
        Check("True Grit: the server drops the pistol Bolter without the perk", Loadouts.Resolve(config, assault, gritless).Pistol == -1);

        session.Edit(p => LoadoutEdits.Clear(p, slot).Profile);
        await Frames(2);
    }

    /// <summary>
    /// Added content E2 (docs/added-content.md): the content profile on the Join screen and what it hides. Runs last and leaves the saved choice at the default.
    /// </summary>
    async Task ContentChecks()
    {
        var session = _services.Session!;
        var kept = session.Working;
        string originalName = _services.Settings.Content;   // restored at the end: the run must not leave its own choice in the settings file
        var originalContent = _services.Content;
        _front.ShowJoin();
        await Frames(4);
        var faithfulButton = FindButton(_front, b => b.Name == "ContentFaithful");
        var extendedButton = FindButton(_front, b => b.Name == "ContentExtended");
        Check("the Join screen has the content picker", faithfulButton is not null && extendedButton is not null);

        // Extended is the default and the one that adds content; Faithful removes it, and the choice is remembered next to the profile.
        if (ContentExtras.Compiled)
        {
            extendedButton?.EmitSignal(BaseButton.SignalName.Pressed);
            await Frames(2);
            Check("Extended plays every added content and is remembered", _services.Content == ContentExtras.All && FrontEndSettings.Load(_services.SettingsDirectory).Content == ContentProfiles.Extended, _services.Content.ToString());
            Check("the default profile is extended", ContentProfiles.DefaultName == ContentProfiles.Extended && FrontEndSettings.Default.Content == ContentProfiles.Extended);
        }
        else Check("a build without added content cannot pick Extended", extendedButton?.Disabled == true);
        faithfulButton?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(2);
        Check("Faithful plays no added content and is remembered", _services.Content == ContentExtras.None && FrontEndSettings.Load(_services.SettingsDirectory).Content == ContentProfiles.Faithful);
        Check("the picker shows the choice", faithfulButton?.ButtonPressed == true && extendedButton?.ButtonPressed == false);

        // The customizer's faction step and the class creator do not offer the Ork in the faithful profile.
        var factions = _services.Factions();
        Check("faithful: the customizer offers the Space Marines and Chaos only", factions.Select(f => f.Race).SequenceEqual([CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos]), string.Join(",", factions.Select(f => f.Race)));
        Check("the rule offers the Ork only with the Orks", !ContentGate.Factions(ContentExtras.None).Contains(Race.Orks) && ContentGate.Factions(ContentExtras.All).Contains(Race.Orks));
        _front.ShowCreator();
        await Frames(5);
        Check("faithful: the class creator shows no Ork", FindButton(_front.Creator!, b => b.Text.Contains("Ork", StringComparison.OrdinalIgnoreCase)) is null
            && FindButton(_front.Creator!, b => b.Text == "Space Marines") is not null && FindButton(_front.Creator!, b => b.Text == "Chaos") is not null);
        _front.ShowAppearance();
        await Frames(6);
        Check("faithful: the appearance editor shows no Ork", FindButton(_front.Appearance!, b => b.Text.Contains("Ork", StringComparison.OrdinalIgnoreCase)) is null);

        // A saved Ork loadout is kept, shown as unavailable and joined as the side's default class.
        var ork = new CustomClass(9, "Boyz", SmContent.OrkClass.Role, null, null, null, null, [null, null], AppearanceRefs.None);
        var withOrk = new CustomizationProfile(kept.Schema, Selection.CustomSlot(9), kept.Classes.Where(c => c.Slot != 9).Append(ork).ToList(), kept.Appearances);
        session.Seed(withOrk);
        _front.ShowJoin();
        await Frames(4);
        Check("the Ork slot stays in the profile", session.Working.ClassAt(9)?.Role == SmContent.OrkClass.Role);
        Check("the Ork slot is shown as unavailable in the faithful profile", FindButton(_front, b => b.TooltipText.Contains("added content", StringComparison.OrdinalIgnoreCase)) is not null);
        var faithfulPlan = _services.PlanFor(Selection.CustomSlot(9));
        Check("it joins as the side's default class", faithfulPlan is { ClassName: not null } && faithfulPlan.Choice.Class is >= 0 and < 6 && faithfulPlan.Choice.Primary == -1, faithfulPlan?.ClassName ?? "null");
        Check("the plan says why", faithfulPlan?.Summary.Contains("added content", StringComparison.OrdinalIgnoreCase) == true, faithfulPlan?.Summary ?? "null");
        session.Seed(kept);

        // The Join never sends an Ork class or an Ork look in the faithful profile, and does with the Orks.
        var orkConfig = (await Task.Run(() => GameplayTuning.LoadForMatch(_services.GameDir, ContentExtras.Orks))).Config;
        int orkClass = orkConfig.Classes.ToList().FindIndex(c => c.Race == Race.Orks);
        Check("the Ork Boy is class 6 of an Orks match", orkClass == 6, orkClass.ToString());
        var basePlan = _services.PlanFor(Selection.CustomSlot(4))!;
        var orkPlan = basePlan with { Choice = new LoadoutChoice(orkClass, 0, 0, 0, 0, 0, 0) };
        var asked = new SmNet.ClientOptions { OrkLook = 2, RequestedClass = (byte)orkClass };
        var faithful = FrontEndLaunch.Apply(asked, new JoinRequest("127.0.0.1:27360", orkPlan, _services, ContentExtras.None));
        Check("faithful: the Hello asks for the server's default class", faithful.RequestedClass == SmNet.Protocol.ServerDefaultClass && faithful.RequestedPrimary == SmNet.Protocol.DefaultChoice, faithful.RequestedClass.ToString());
        Check("faithful: the Hello carries no Ork look", faithful.OrkLook == Races.OrkLookAutomatic, faithful.OrkLook.ToString());
        var extended = FrontEndLaunch.Apply(asked, new JoinRequest("127.0.0.1:27360", orkPlan, _services, ContentExtras.All));
        Check("extended: the Hello keeps the Ork class and its look", extended.RequestedClass == orkClass && extended.OrkLook == 2, $"{extended.RequestedClass}/{extended.OrkLook}");

        // Put the saved choice back as the run found it.
        _services.Content = originalContent;
        _services.UpdateSettings(x => x with { Content = originalName });
        await Frames(2);
        Check("the saved content choice is restored", FrontEndSettings.Load(_services.SettingsDirectory).Content == originalName && _services.Content == originalContent, FrontEndSettings.Load(_services.SettingsDirectory).Content);
        _front.ShowJoin();
    }
}
