using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmClient.Customizer;
using SmClient.Customizer.Model;
using SmContent;
using SmContent.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;

namespace SmClient.FrontEnd;

/// <summary>
/// Self-test of the Warfront menu (slice W16), headless: <c>godot.cmd -- --warfront-selftest --server-exe &lt;any existing file&gt; --profile &lt;temp file&gt;</c> (the server is never started: the press stops at
/// <see cref="FrontEndView.WarfrontOverride"/>, the join at <see cref="FrontEndView.JoinOverride"/>; the run folder is a temp folder, never the owner's save). Drives the real rows through their own
/// buttons: the main menu's rows (Warfront first, Musou Mode gone, Classic's unchanged), the page in its three cases (no run, a run, a finished run), the New confirmation, a server already running, the preparing screen and
/// Esc. Add <c>--warfront-shots &lt;dir&gt;</c> in a windowed run to save a still of each. One SELFTEST PASS or FAIL line per check, then WARFRONT SELFTEST RESULT; quits non-zero on a failure.
/// </summary>
public sealed partial class WarfrontLocalSelfTest : Node
{
    readonly FrontEndView _front;
    int _passed, _failed, _shot;
    string? _shotDir;

    public WarfrontLocalSelfTest(FrontEndView front) { _front = front; Name = "WarfrontLocalSelfTest"; }

    public override void _Ready() => _ = RunAsync();

    async Task Frames(int n = 3) { for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    void Check(string name, bool ok, string detail = "")
    {
        if (ok) _passed++; else _failed++;
        GD.Print($"SELFTEST {(ok ? "PASS" : "FAIL")} {name}{(!ok && detail.Length > 0 ? " - " + detail : "")}");
    }

    static void Buttons(Node n, List<Button> into)
    {
        if (n is Button b && b.IsVisibleInTree()) into.Add(b);
        foreach (var c in n.GetChildren()) Buttons(c, into);
    }

    static void Labels(Node n, List<Label> into)
    {
        if (n is Label l && l.IsVisibleInTree()) into.Add(l);
        foreach (var c in n.GetChildren()) Labels(c, into);
    }

    static List<string> Rows(UiKit.MenuPage? page)
    {
        var all = new List<Button>();
        if (page is not null) Buttons(page.Rows, all);
        return all.Select(b => b.Text).ToList();
    }

    static List<string> Texts(UiKit.MenuPage? page)
    {
        var all = new List<Label>();
        if (page is not null) Labels(page.Rows, all);
        if (page is WarfrontPage wp) all.AddRange(wp.OwnLabels().Where(l => l.Visible));   // the strip's cells and the status line are the page's own labels, outside the rows
        return all.Select(l => l.Text).Where(t => t.Length > 0).ToList();
    }

    static Button? Find(Node root, string text)
    {
        var all = new List<Button>();
        Buttons(root, all);
        return all.FirstOrDefault(b => string.Equals(b.Text, text, StringComparison.OrdinalIgnoreCase));
    }

    async Task Press(Node root, string text)
    {
        Find(root, text)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Frames(4);
    }

    async Task Still(string name)
    {
        if (_shotDir is null) return;
        await Frames(6);
        var image = GetViewport().GetTexture().GetImage();
        string size = $"{image.GetWidth()}x{image.GetHeight()}";
        image.SavePng(Path.Combine(_shotDir, $"{++_shot:00}-{name}-{size}.png"));
    }

    async Task RunAsync()
    {
        WarfrontOnline.HideForTest = true;   // the row lists below are checked without ONLINE WARFRONT; its own section forces it on
        var args = OS.GetCmdlineUserArgs();
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--warfront-shots") _shotDir = args[i + 1];
        if (_shotDir is not null) Directory.CreateDirectory(_shotDir);
        string dir = Path.Combine(Path.GetTempPath(), "sm-wf16-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try { await Checks(dir); }
        catch (Exception e) { Check("no exception", false, e.ToString()); }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        GD.Print($"WARFRONT SELFTEST RESULT passed {_passed} failed {_failed}");
        GetTree().Quit(_failed == 0 ? 0 : 1);
    }

    static WarfrontCampaign Run(string phase = "Ship")
    {
        var c = new WarfrontCampaign(7);
        c.Embark();
        c.Join(11, "Owner", out _);
        if (phase == "Ship") return c;
        return WarfrontSave.FromJson(c.ToJson().Replace("\"Phase\":\"Ship\"", "\"Phase\":\"" + phase + "\""));
    }

    async Task Checks(string dir)
    {
        await Frames(5);   // this node is added while the pages are built; the front end finishes building in that same frame
        for (int i = 0; i < 600 && !_front.Built; i++) await Frames(1);
        Check("the front end is built", _front.Built);
        if (!_front.Built) return;
        _front.WarfrontRunDirForTest = dir;
        JoinRequest? joined = null;
        _front.JoinOverride = r => joined = r;
        bool? started = null;
        _front.WarfrontOverride = isNew => started = isNew;
        _front.ShowMainMenu();
        await Frames(4);

        // ---- the main menu: the row, in Musou Mode's old place; Classic's rows as before ----
        var main = _front.MainPage!;
        bool added = ContentExtras.Compiled && ContentExtras.WarfrontCompiled;
        var wanted = new List<string>();
        if (added) wanted.Add("WARFRONT");
        wanted.AddRange(["CLASSIC PVP", "ARMOURY", "OPTIONS", "EXIT"]);
        Check("main menu: WARFRONT first in Musou Mode's old place, no Musou Mode row", Rows(main).SequenceEqual(wanted) && Find(main, "MUSOU MODE") is null, string.Join(", ", Rows(main)));
        Check("main menu: the row exists only with added content and the Warfront build", (_front.WarfrontRow is not null) == added);
        if (!added) { Check("no Warfront row in this build", Find(main, "WARFRONT") is null); return; }
        Check("main menu: the row's tooltip", _front.WarfrontRow!.TooltipText == WarfrontLocal.RowTooltip, _front.WarfrontRow.TooltipText);
        Check("main menu: the row is a steel plate like its neighbours", _front.WarfrontRow.ThemeTypeVariation == Find(main, "CLASSIC PVP")!.ThemeTypeVariation && _front.WarfrontRow.Name.ToString().Contains("MenuWarfront"), _front.WarfrontRow.Name);
        await Still("main-menu");

        await Press(main, "CLASSIC PVP");
        Check("Classic's lobby page is unchanged", Rows(_front.LobbyPage).Take(3).SequenceEqual(["HOST LOCAL LOBBY", "JOIN SERVER", "JOIN LOCAL LOBBY"]));
        _front.ShowMainMenu();
        await Frames(3);

        // ---- no run: NEW CAMPAIGN, Customizer, Back ----
        await Press(main, "WARFRONT");
        var page = _front.WarfrontPage;
        Check("page: opens and the main menu is hidden", page is { Visible: true } && !_front.ShowsMainMenu);
        Check("page: no run gives New campaign, Host for friends, Join a friend, Customizer, Back", Rows(page).SequenceEqual(["NEW CAMPAIGN", "HOST FOR FRIENDS", "JOIN A FRIEND", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]), string.Join(", ", Rows(page)));
        Check("page: no run, the status strip shows dashes", page is WarfrontPage { } none && none.Cells.Select(c => c.Value).SequenceEqual(["-", "-", "-"]) && none.Cells.Select(c => c.Label).SequenceEqual(["DEPLOYMENTS", "FRONTS", "LEVEL"]));
        Check("page: the first row has the focus", GetViewport().GuiGetFocusOwner() is Button { Text: "NEW CAMPAIGN" });
        Check("page: text audit, no explainer text", SmClient.FrontEnd.UiSelfTest.AuditText(page!).Count == 0, string.Join(" | ", SmClient.FrontEnd.UiSelfTest.AuditText(page!)));
        // ---- codebase fix 1: RESET PROGRESSION and the loader's notice (SM_WF_DIR names a temp folder in this run) ----
        Check("progress: no key on this PC: the reset plate is listed and greyed", Find(page!, "RESET PROGRESSION") is { Disabled: true });
        string wfKeyDir = SmClient.Extras.Warfront.Stage.WarfrontClientKey.Folder();
        Check("progress: the self-test runs in a temp folder (SM_WF_DIR), never the owner's", System.Environment.GetEnvironmentVariable("SM_WF_DIR") is { Length: > 0 }, wfKeyDir);
        if (System.Environment.GetEnvironmentVariable("SM_WF_DIR") is { Length: > 0 })
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "notice.json"), System.Text.Json.JsonSerializer.Serialize(new { kind = "unreadable", utc = "2026-11-22T10:00:00Z", text = "Your campaign file was damaged and kept. A new campaign started." + (char)10 + "Second line." }));
            SmClient.Extras.Warfront.Stage.WarfrontClientKey.LoadOrCreate(SmClient.Extras.Warfront.Stage.WarfrontClientKey.DefaultPath());
            _front.ShowWarfrontPageForTest();
            await Frames(3);
            page = _front.WarfrontPage;
            Check("progress: the notice shows as one upper-case line and its file is deleted", Texts(page).Contains("YOUR CAMPAIGN FILE WAS DAMAGED AND KEPT. A NEW CAMPAIGN STARTED.") && !System.IO.File.Exists(System.IO.Path.Combine(dir, "notice.json")), string.Join(" | ", Texts(page)));
            Check("progress: with a key the reset plate is enabled", Find(page!, "RESET PROGRESSION") is { Disabled: false });
            await Press(page!, "RESET PROGRESSION");
            await Frames(3);
            Check("progress: the plate opens a confirmation that names what is cleared", _front.WarfrontConfirmOpen);
            Check("progress: nothing is written before Yes", _front.WarfrontStoreForTest is { State: SmClient.Extras.Warfront.Stage.WarfrontProgressLoad.Absent });
            await Press(_front, "No");
            await Frames(3);
            Check("progress: reset writes a level-1 file", _front.WarfrontResetNow() && _front.WarfrontStoreForTest is { State: SmClient.Extras.Warfront.Stage.WarfrontProgressLoad.Ok, Current.Level: 1 });
            await Frames(3);
            page = _front.WarfrontPage;
        }
        await Still("page-no-run");
        await Press(page!, "NEW CAMPAIGN");
        Check("new without a run starts at once (no question)", started == true && !_front.WarfrontConfirmOpen, $"started {started}");
        started = null;

        // ---- ONLINE WARFRONT: off by default (the server is not live yet); forced on, one more row that joins the public host's Warfront port with no typing ----
        Check("online: the gate is on by default (the 2026-11-20 ship); hidden here by the test switch", WarfrontOnline.EnabledByDefault && !Rows(page).Contains("ONLINE WARFRONT"));
        WarfrontOnline.ForceForTest = true;
        _front.ShowWarfrontPageForTest();
        await Frames(4);
        page = _front.WarfrontPage;
        Check("online: forced on, the page lists it after Join a friend", Rows(page).SequenceEqual(["NEW CAMPAIGN", "HOST FOR FRIENDS", "JOIN A FRIEND", "ONLINE WARFRONT", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]), string.Join(", ", Rows(page)));
        Check("online: the row takes focus (keyboard and pad)", Find(page!, "ONLINE WARFRONT") is { Disabled: false, FocusMode: not Control.FocusModeEnum.None });
        joined = null;
        for (int i = 0; i < 300 && joined is null; i++) await Press(page!, "ONLINE WARFRONT");   // the game data's plan loads in the background: press until it is ready
        string onlineHost = JoinTargets.PublicServer[..JoinTargets.PublicServer.LastIndexOf(':')] + ":27016";
        Check("online: joins the public host on port 27016 with Warfront content", joined?.Host == onlineHost && joined.Host == WarfrontOnline.Address && joined.Content == ContentProfiles.WarfrontProfile, (joined?.Host ?? "null") + " / " + _front.MessageText);
        Check("online: the join does not remember the address", joined?.Services.Settings.Host != onlineHost);
        Check("online: no answer reads as not reachable", WarfrontOnline.Message(SmNet.ConnectFailure.NoAnswerText(onlineHost)) == WarfrontOnline.NotReachableText);
        Check("online: a refusal keeps its plain words", WarfrontOnline.Message(SmNet.ConnectFailure.RejectText(SmNet.RejectReason.ProtocolMismatch, null)) == SmNet.ConnectFailure.RejectText(SmNet.RejectReason.ProtocolMismatch, null));
        WarfrontOnline.ForceForTest = false;
        _front.ShowWarfrontPageForTest();
        await Frames(4);
        page = _front.WarfrontPage;
        joined = null;

        // Esc steps back to the main menu (not to Player versus Player)
        Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = true });
        await Frames(4);
        Check("Esc on the page returns to the main menu", _front.ShowsMainMenu && _front.WarfrontPage is { Visible: false });
        await Press(main, "WARFRONT");
        await Press(_front.WarfrontPage!, "BACK");
        Check("Back returns to the main menu", _front.ShowsMainMenu && _front.WarfrontPage is { Visible: false });

        // ---- a run: CONTINUE with its line, NEW asks first ----
        WarfrontSave.Write(dir, Run());
        string runFile = Path.Combine(dir, WarfrontSave.RunFile);
        var before = File.ReadAllBytes(runFile);
        await Press(main, "WARFRONT");
        page = _front.WarfrontPage;
        Check("page: a run gives Continue, New, Customizer, Back", Rows(page).SequenceEqual(["CONTINUE CAMPAIGN", "NEW CAMPAIGN", "HOST FOR FRIENDS", "JOIN A FRIEND", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]), string.Join(", ", Rows(page)));
        Check("page: the run's three figures are in the status strip", page is WarfrontPage { } cells && cells.Cells.Select(c => c.Value).SequenceEqual(["0", "1 / 1 / 1", "1"]), string.Join(" | ", Texts(page)));
        Check("page: Continue has the focus", GetViewport().GuiGetFocusOwner() is Button { Text: "CONTINUE CAMPAIGN" });
        await Still("page-with-run");
        await Press(page!, "CONTINUE CAMPAIGN");
        Check("Continue starts the server for the same run", started == false, $"started {started}");
        started = null;
        await Press(page!, "NEW CAMPAIGN");
        Check("New with a run asks first", _front.WarfrontConfirmOpen && started is null);
        var dialogTexts = new List<Label>();
        Labels(_front, dialogTexts);
        Check("confirmation: the question and what happens to the current run", dialogTexts.Any(l => l.Text == WarfrontLocal.ConfirmTitle) && dialogTexts.Any(l => l.Text == WarfrontLocal.ConfirmBody));
        Check("confirmation: No has the focus", GetViewport().GuiGetFocusOwner() is Button nb && nb.Text.Equals("No", StringComparison.OrdinalIgnoreCase));
        await Still("confirm-new");
        await Press(_front, "No");
        Check("No leaves everything as it was", !_front.WarfrontConfirmOpen && started is null && File.ReadAllBytes(runFile).SequenceEqual(before));
        await Press(page!, "NEW CAMPAIGN");
        await Press(_front, "Yes");
        Check("Yes starts a new campaign", started == true);
        started = null;
        await Press(page!, "NEW CAMPAIGN");
        Input.ParseInputEvent(new InputEventAction { Action = "ui_cancel", Pressed = true });
        await Frames(4);
        Check("Esc on the confirmation answers No", !_front.WarfrontConfirmOpen && started is null && _front.WarfrontPage is { Visible: true });
        Check("the page never wrote the run file", File.ReadAllBytes(runFile).SequenceEqual(before));

        // ---- a server already running ----
        _front.WarfrontStateForTest = (true, true, false);
        _front.ShowWarfrontPageForTest();
        await Frames(3);
        page = _front.WarfrontPage;
        Check("another server running: the page says so and offers Stop server alone", Rows(page).SequenceEqual(["STOP SERVER", "HOST FOR FRIENDS", "JOIN A FRIEND", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]) && Texts(page).Contains("A SERVER IS RUNNING. STOP IT FIRST."), string.Join(" | ", Rows(page)) + " / " + string.Join(" | ", Texts(page)));
        _front.WarfrontStateForTest = (true, true, true);
        _front.ShowWarfrontPageForTest();
        await Frames(3);
        page = _front.WarfrontPage;
        Check("this game's Warfront server running: only Rejoin and Stop (no New, no Continue)", Rows(page).SequenceEqual(["REJOIN CAMPAIGN", "STOP SERVER", "HOST FOR FRIENDS", "JOIN A FRIEND", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]) && Find(page!, "NEW CAMPAIGN") is null && Find(page!, "CONTINUE CAMPAIGN") is null, string.Join(", ", Rows(page)));
        Check("this game's Warfront server running: the page says so", page is WarfrontPage { } running && running.NoteLabel.Text == "THE WARFRONT SERVER IS RUNNING.", string.Join(" | ", Texts(page)));
        await Press(page!, "REJOIN CAMPAIGN");   // sets the Warfront profile; its configuration is read in the background (as while a real server starts)
        for (int i = 0; i < 1200 && !_front.ServicesReadyForTest; i++) await Frames(1);
        joined = null;
        await Press(page!, "REJOIN CAMPAIGN");
        Check("a ready Warfront server is rejoined, never a second one, never a new campaign", started is null && joined is { Content: { } c } && c == ContentProfiles.WarfrontProfile && joined.Host.StartsWith("127.0.0.1:", StringComparison.Ordinal), (joined?.Host ?? "null") + " / " + _front.MessageText);
        joined = null;
        _front.WarfrontStateForTest = (true, false, true);
        await Press(page!, "REJOIN CAMPAIGN");
        Check("a starting Warfront server is waited for: the preparing screen", _front.WarfrontPreparing is { Visible: true } && joined is null && started is null);
        _front.WarfrontStateForTest = null;

        // ---- the preparing screen ----
        _front.WarfrontStateForTest = (true, false, true);
        _front.WarfrontWaitForTest(startedSecondsAgo: 83);
        await Frames(6);
        var prep = _front.WarfrontPreparing;
        Check("preparing: the title, the status and the elapsed time", prep is ConceptPanelPage { } prepPage && prepPage.PlateTitle == WarfrontLocal.PreparingTitle.ToUpperInvariant() && Texts(prep).Any(t => t.StartsWith("1:2", StringComparison.Ordinal)), string.Join(" | ", Texts(prep)));
        Check("preparing: Cancel is the one row", Rows(prep).SequenceEqual(["CANCEL"]));
        await Still("preparing");
        await Press(prep!, "CANCEL");
        Check("Cancel stops watching and returns to the page", !_front.WarfrontWaiting && _front.WarfrontPage is { Visible: true } && _front.WarfrontPreparing is { Visible: false });
        _front.WarfrontStateForTest = null;

        // ---- back at the menu after a match: the server stops and the lobby's profile comes back; a failed join keeps the server once ----
        _front.WarfrontStateForTest = (true, true, true);
        _front.ContentForTest = ContentProfiles.WarfrontProfile;
        _front.WarfrontJoinedForTest = true;
        _front.WarfrontMessageForTest = WarfrontLocal.LeftMatchText;
        _front.WarfrontAtMenuForTest();
        Check("back at the menu after a match: the profile is the remembered one again", _front.ContentForTest == _front.SettingsContentForTest && !_front.WarfrontJoinedForTest, _front.ContentForTest.ToString());
        _front.ContentForTest = ContentProfiles.WarfrontProfile;
        _front.WarfrontJoinedForTest = true;
        _front.WarfrontMessageForTest = "Rejected: no room.";
        _front.WarfrontAtMenuForTest();
        Check("a failed join keeps the server and the Warfront profile for one retry", _front.ContentForTest == ContentProfiles.WarfrontProfile && WarfrontMenuState.Retries == 1);
        _front.WarfrontJoinedForTest = true;
        _front.WarfrontAtMenuForTest();
        Check("a second failure stops it and puts the profile back", _front.ContentForTest == _front.SettingsContentForTest && WarfrontMenuState.Retries == 0);
        _front.WarfrontMessageForTest = null;
        _front.WarfrontStateForTest = null;

        // ---- a finished run: only New campaign, and the honor line ----
        WarfrontSave.Write(dir, Run("Won"));
        Directory.CreateDirectory(Path.Combine(dir, "records"));
        var rec = new WarfrontRunRecord("won", "2026-11-17T00:00:00Z", 2480, WarfrontScoreWeights.Default, Array.Empty<string>(), 0, 0, 0, Array.Empty<WarfrontLeaderRow>());
        File.WriteAllText(Path.Combine(dir, "records", "20261101T000000Z-won" + WarfrontResultsArchive.RecordSuffix), rec.ToJson());
        _front.ShowWarfrontPageForTest();
        await Frames(3);
        page = _front.WarfrontPage;
        Check("page: a finished run gives only New campaign", Rows(page).SequenceEqual(["NEW CAMPAIGN", "HOST FOR FRIENDS", "JOIN A FRIEND", "CUSTOMIZER", "RESET PROGRESSION", "BACK"]), string.Join(", ", Rows(page)));
        Check("page: the honor roll line", Texts(page).Contains("RUNS 1, VICTORIES 1, FASTEST 41:20"), string.Join(" | ", Texts(page)));
        await Still("page-finished");
        await Press(page!, "NEW CAMPAIGN");
        Check("a finished run needs no question", started == true && !_front.WarfrontConfirmOpen);
        started = null;
        File.WriteAllText(Path.Combine(dir, WarfrontSave.RunFile), "not json");
        _front.ShowWarfrontPageForTest();
        await Frames(3);
        Check("page: an unreadable run gives no Continue", !Rows(_front.WarfrontPage).Contains("CONTINUE CAMPAIGN") && Rows(_front.WarfrontPage).Contains("NEW CAMPAIGN"));

        _front.ShowMainMenu();
        await Frames(3);
        Check("the front end is back on the main menu", _front.ShowsMainMenu && _front.WarfrontPage is { Visible: false });
    }
}
