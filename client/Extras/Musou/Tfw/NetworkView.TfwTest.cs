using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The war's test aids (TF7b, client only; docs/commands.md).
/// <para>
/// <c>--tfw-test [dir]</c>: a scripted player (not <c>--bot</c>) plays through the real keys, mouse and pad events on a real server and checks the HUD's data: E at Equipment opens the picker (needs
/// <c>--profile</c>), M shows the map, E at Destination opens it, Esc, B and the CLOSE row close it, Enter on DEPLOY sends the command and the server echoes it, the travel, the landing, X, V and Z
/// change the order the server reports, X and V no longer switch or strike, P held calls a pickup, held again calls it off, and the pickup brings the player home. With a server started with
/// <c>SM_TFW_DOWN_TEST=&lt;s&gt;</c> it plays on: a second battle with the revive, a third with the extraction. With <c>--tfw-test-win</c> and a server started with <c>SM_TFW_WIN_TEST=&lt;m&gt;</c> it walks a whole
/// run instead: two battles won on one front through all three positions and the commander, each left by the free pickup, and the final assault to the Warboss's death; on the way it reads each map's score panel, picks a reward card, opens a crate, equips slots with keys 3 and
/// 2, reads the run's panel and copies the result, and with <c>--tfw-server-log &lt;file&gt;</c> (the server's output) holds every shown score against the server's <c>TFWSCORE</c> lines. One <c>TFWTEST PASS</c> or <c>FAIL</c> line per step; quits with 0, or 1 when any failed.
/// In a windowed run <c>dir</c> gets a PNG of the Stormbird's prompt, the war map, the battle's HUD, the revive and the extraction.
/// </para>
/// <para>
/// <c>--bot --tfw-bot</c>: a lean bot deploys, calls the pickup 5 s after the landing (again every 5 s, as a death calls it off) and quits with 0 once it is home (1 after 120 s). It has no keys or screens: it issues the same commands.
/// </para>
/// </summary>
public partial class NetworkView
{
    static readonly bool TfwTestOn = CmdFlag("tfw-test"), TfwBotOn = CmdFlag("tfw-bot");
    bool _tfwTestStarted;
    int _tfwTestPassed, _tfwTestFailed;
    string? _tfwShots;

    // ---------------------------------------------------------------- the lean bot

    double _tfwBotSent = double.NegativeInfinity, _tfwBotLanded = -1, _tfwBotStart = -1, _tfwBotCalled = double.NegativeInfinity;

    void TfwBotFrame(in TfwWarView war, PlayerState me)
    {
        if (!_bot || !TfwBotOn || _client is not { } client) return;
        if (_tfwBotStart < 0) _tfwBotStart = _connectedFor;
        if (_connectedFor - _tfwBotStart > 120.0) { GD.PrintErr($"TFWBOT FAIL: timed out in {war.Phase}"); GetTree().Quit(1); return; }
        if (war.Phase == TfwRunPhase.Hub && _tfwBotLanded >= 0)
        {
            GD.Print($"TFWBOT PASS: home in the hub, transport {war.Transport}, supplies {war.Supplies}, clock {war.Clock:F0}");
            GetTree().Quit(0);
            return;
        }
        if (war.Phase == TfwRunPhase.Hub && client.Match.State == MatchState.Playing && me.Alive && _connectedFor - _tfwBotSent > 2.0)
        {
            _tfwBotSent = _connectedFor;
            _tfwState.Commands.Issue(TfwCommand.Deploy, (byte)TfwLane.B);
            GD.Print($"t {_connectedFor:F1} s: TFWBOT deploys to front B");
        }
        if (war.Phase is not (TfwRunPhase.Battle or TfwRunPhase.Pickup)) return;
        if (_tfwBotLanded < 0)
        {
            _tfwBotLanded = _connectedFor;
            GD.Print($"t {_connectedFor:F1} s: TFWBOT landed at ({me.Position.X:F0}, {me.Position.Y:F0}); {_tfwState.Objectives.Count} objectives, guards {war.Own.Guards}, retainer {war.Own.Retainer}");
        }
        // It walks into the fight, and a death calls a pickup off: it asks again.
        if (war.Phase == TfwRunPhase.Battle && _connectedFor - _tfwBotLanded > 5.0 && _connectedFor - _tfwBotCalled > 5.0)
        {
            _tfwBotCalled = _connectedFor;
            _tfwState.Commands.Issue(TfwCommand.RequestPickup);
            GD.Print($"t {_connectedFor:F1} s: TFWBOT calls the pickup");
        }
    }

    // ---------------------------------------------------------------- the scripted player

    void TfwTestFrame()
    {
        if (!TfwTestOn || _tfwTestStarted || _bot) return;
        _tfwTestStarted = true;
        _tfwShots = CmdArg("tfw-test");
        if (_tfwShots is { Length: > 0 } dir) Directory.CreateDirectory(dir); else _tfwShots = null;
        _ = TfwTestRun();
    }

    async Task TfwTestRun()
    {
        try { await TfwTestScript(); }
        catch (Exception ex) { TfwCheck("exception", false, ex.ToString()); }
        foreach (var key in new[] { Key.W, Key.P, Key.E }) TfwKey(key, false);
        GD.Print($"TFWTEST done: {_tfwTestPassed} passed, {_tfwTestFailed} failed");
        GetTree().Quit(_tfwTestFailed == 0 ? 0 : 1);
    }

    void TfwCheck(string name, bool ok, string detail = "")
    {
        if (ok) _tfwTestPassed++; else _tfwTestFailed++;
        GD.Print($"TFWTEST {(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 && !ok ? " - " + detail : "")}");
    }

    async Task TfwFrames(int count = 1) { for (int i = 0; i < count; i++) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }

    async Task TfwWait(double seconds) { double end = _connectedFor + seconds; while (_connectedFor < end) await TfwFrames(); }

    async Task<bool> TfwUntil(string what, Func<bool> done, double seconds)
    {
        double end = _connectedFor + seconds;
        while (!done())
        {
            if (_connectedFor > end) { TfwCheck(what, false, $"not within {seconds:F0} s (phase {_tfwState.War?.Phase}, at {_client?.Predicted.Position})"); return false; }
            await TfwFrames();
        }
        TfwCheck(what, true);
        return true;
    }

    static void TfwKey(Key key, bool down) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = down });

    async Task TfwTap(Key key) { TfwKey(key, true); await TfwFrames(4); TfwKey(key, false); await TfwFrames(4); }

    /// <summary>Walks with the W key, looking at <paramref name="at"/>, onto its pad.</summary>
    async Task<bool> TfwWalkTo(Num.Vector3 at, string what, double seconds = 20.0, Func<bool>? over = null)
    {
        var client = _client!;
        double end = _connectedFor + seconds;
        TfwKey(Key.W, true);
        await TfwFrames(2);
        while (new Vector2(client.Predicted.Position.X - at.X, client.Predicted.Position.Y - at.Y).Length() > 0.7f && _connectedFor < end && over?.Invoke() != true)
        {
            _yaw = MathF.Atan2(-(at.X - client.Predicted.Position.X), at.Y - client.Predicted.Position.Y);
            await TfwFrames();
        }
        TfwKey(Key.W, false);
        await TfwWait(0.5);
        bool there = _connectedFor < end;
        TfwCheck(what, there, $"stopped at {client.Predicted.Position}");
        return there;
    }

    SmSim.NavGraph? _tfwTestNav;

    /// <summary>Walks to <paramref name="at"/> along the battlefield's walking graph, as a player finds the way: it proves the place can be reached on foot. The graph is the server's own (the same cache file).</summary>
    async Task<bool> TfwPathTo(Num.Vector3 at, string what, double seconds = 60.0, Func<bool>? over = null)
    {
        var client = _client!;
        if (_tfwTestNav is null)
        {
            var data = SmContent.Extras.Musou.TfwWorld.Load(_gameDir, out string? problem);
            if (data is null || _config is null) { TfwCheck(what, false, problem ?? "no config"); return false; }
            _tfwTestNav = SmContent.Extras.Musou.TfwWorld.Nav(data.Collision, data.Field, _config, out _);
        }
        double end = _connectedFor + seconds;
        var path = _tfwTestNav.FindPath(client.Predicted.Position, at);
        if (path is null || path.Partial) { TfwCheck(what, false, $"no way from {client.Predicted.Position} to {at}"); return false; }
        var points = path.Waypoints;
        TfwKey(Key.W, true);
        await TfwFrames(2);
        int next = 0, stuckFrames = 0;
        var last = client.Predicted.Position;
        while (next < points.Count && _connectedFor < end && over?.Invoke() != true)
        {
            var me = client.Predicted.Position;
            // The farthest waypoint within 3 m ahead: corners are cut as a player cuts them.
            while (next + 1 < points.Count && new Vector2(points[next + 1].Position.X - me.X, points[next + 1].Position.Y - me.Y).Length() < 3f) next++;
            var to = points[next].Position;
            if (new Vector2(to.X - me.X, to.Y - me.Y).Length() < 0.7f) next++;
            _yaw = MathF.Atan2(-(to.X - me.X), to.Y - me.Y);
            stuckFrames = Num.Vector3.Distance(me, last) < 0.001f ? stuckFrames + 1 : 0;
            last = me;
            if (stuckFrames == 120) GD.Print($"TFWTEST note: held up at {me} on the way to {to}");
            await TfwFrames();
        }
        TfwKey(Key.W, false);
        await TfwWait(0.3);
        bool there = _connectedFor < end;
        TfwCheck(what, there, $"stopped at {client.Predicted.Position}");
        return there;
    }

    async Task TfwShot(string name)
    {
        if (_tfwShots is null || DisplayServer.GetName() == "headless") return;
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string path = Path.Combine(_tfwShots, name + ".png");
        GD.Print(GetViewport().GetTexture().GetImage().SavePng(path) == Error.Ok ? $"tfw shot -> {path}" : $"tfw shot failed: {path}");
    }

    TfwWarView TfwNow => _tfwState.War!.Value;

    string TfwPromptNow => _tfwState.War is { } war ? TfwHudModel.Prompt(war, _client!.Predicted.Alive) : "";

    /// <summary>An order key: the order the server reports follows, the key's old action does not.</summary>
    async Task TfwOrder(Key key, MarineOrder order, GameAction? old)
    {
        TfwKey(key, true);
        await TfwFrames(3);
        if (old is { } action) TfwCheck($"{key} no longer does '{action}' in the war", !ControlsInput.Held(action));
        TfwKey(key, false);
        await TfwUntil($"{key}: the server reports the order {order}", () => TfwNow.Own.Order == order, 5.0);
    }

    async Task<bool> TfwTestDeploy(TfwLane lane, bool final = false)
    {
        await TfwTestPanelsAway();
        if (!await TfwWalkTo(TfwHub!.DestinationStation, "walked to Destination")) return false;
        await TfwTap(Key.E);
        if (!await TfwUntil("E at Destination opens the war map", () => _tfwPanel is not null, 3.0)) return false;
        await TfwFrames(4);
        _tfwPanel!.DeployButton(lane)!.GrabFocus();
        await TfwFrames(2);
        int waiting = _tfwState.Commands.Pending;
        await TfwTap(Key.Enter);
        TfwCheck("Enter on DEPLOY sends a command", _tfwPanel is null && (_tfwState.Commands.Pending > waiting || TfwNow.Phase != TfwRunPhase.Hub), $"pending {_tfwState.Commands.Pending}");
        if (!await TfwUntil("the server echoes it: travel", () => TfwNow.Phase == TfwRunPhase.Travel, 5.0)) return false;
        var battle = final ? TfwRunPhase.FinalAssault : TfwRunPhase.Battle;
        return await TfwUntil($"the Stormbird lands: {battle}", () => TfwNow.Phase == battle && _tfwState.Objectives.Count == TfwBattle.CommanderObjective + 1, 10.0);
    }

    /// <summary>One battle won on <paramref name="lane"/>: the player walks to each position in turn, a real squad secures it, the commander comes and falls (<c>SM_TFW_WIN_TEST</c>), and the free pickup brings it home.</summary>
    async Task<bool> TfwTestWinBattle(TfwLane lane, int number)
    {
        var client = _client!;
        var before = TfwNow;
        sbyte front = TfwHudModel.Lane(before, lane).Front;
        if (!await TfwTestDeploy(lane)) return false;
        await TfwTestLoadout();
        if (number == 2 && !await TfwTestCrateAndSlot()) return false;
        for (int k = 0; k < TfwBattle.CommanderObjective; k++)
        {
            int index = k;
            string name = $"battle {number}: {TfwHudModel.ObjectiveName(k, false)}";
            if (!await TfwPathTo(_tfwState.Objectives[k].Position, $"{name}: walked there", 90.0)) return false;
            if (!await TfwUntil($"{name}: cleared, the HUD says SECURING", () => TfwHudModel.ObjectiveState(_tfwState.Objectives[index]).StartsWith("SECURING", StringComparison.Ordinal) || _tfwState.Objectives[index].State == TfwPositionState.Secured, 30.0)) return false;
            if (!await TfwUntil($"{name}: secured by the Guardsmen", () => _tfwState.Objectives[index].State == TfwPositionState.Secured, 120.0)) return false;
            if (k == 1)
            {
                await TfwWait(0.3);
                var depot = TfwNow;
                TfwCheck($"{name}: the depot's transport and supply", depot.Transport >= before.Transport - TfwBattleRules.DeployCost + TfwBattleRules.DepotTransport && depot.Supplies == before.Supplies + TfwBattleRules.DepotSupplies, $"{depot.Transport}, {depot.Supplies}");
            }
        }
        if (!await TfwUntil($"battle {number}: the commander stands at the HQ", () => _tfwState.Objectives[TfwBattle.CommanderObjective].State >= TfwPositionState.Held, 5.0)) return false;
        await TfwFrames(3);
        TfwCheck($"battle {number}: the boss bar names the commander", TfwHudModel.BossName(TfwNow, _tfwState.Objectives) == "ORK COMMANDER");
        await TfwShot($"commander-{number}");
        if (!await TfwPathTo(_tfwState.Objectives[TfwBattle.CommanderObjective].Position, $"battle {number}: walked to the HQ", 90.0)) return false;
        if (!await TfwUntil($"battle {number}: the commander falls and the battle is won", () => TfwNow.Own.BattleWon, 15.0)) return false;
        await TfwUntil($"battle {number}: the prompt offers the free pickup", () => TfwPromptNow == "BATTLE WON: HOLD P FOR A FREE PICKUP", 3.0);
        await TfwShot($"battle-won-{number}");
        var won = TfwNow;
        TfwCheck($"battle {number}: the front moved up", TfwHudModel.Lane(won, lane).Front == front + 1, TfwHudModel.FrontText(TfwHudModel.Lane(won, lane).Front));
        TfwKey(Key.P, true);
        await TfwUntil($"battle {number}: P held calls the free pickup", () => TfwNow.Phase == TfwRunPhase.Pickup, 4.0);
        TfwKey(Key.P, false);
        await TfwUntil($"battle {number}: the countdown shows", () => TfwPromptNow.StartsWith("PICKUP IN", StringComparison.Ordinal), 3.0);
        if (!await TfwUntil($"battle {number}: the free pickup brings the player home", () => TfwNow.Phase == TfwRunPhase.Hub, 25.0)) return false;
        await TfwWait(0.5);
        var home = TfwNow;
        TfwCheck($"battle {number}: the pickup was free", home.Transport >= won.Transport, $"{won.Transport} to {home.Transport}");
        TfwCheck($"battle {number}: the player is in the Stormbird", TfwHub!.Contains(client.Predicted.Position), client.Predicted.Position.ToString());
        return await TfwTestMapPanel(number);
    }

    /// <summary>A whole run: from front 1 two wins reach the stronghold, then the final assault to the Warboss's death.</summary>
    async Task TfwTestWinRun()
    {
        const TfwLane lane = TfwLane.A;
        for (int number = 1; TfwHudModel.Lane(TfwNow, lane).Front < WarRules.FrontStronghold; number++)
            if (number > 3 || !await TfwTestWinBattle(lane, number)) { TfwCheck("the stronghold is reached", false, TfwHudModel.FrontText(TfwHudModel.Lane(TfwNow, lane).Front)); return; }
        TfwCheck("the front stands at the stronghold: the final assault is open", TfwHudModel.IsFinal(TfwHudModel.Lane(TfwNow, lane)));
        if (!await TfwTestDeploy(lane, final: true)) return;
        TfwCheck("all three positions stand secured", _tfwState.Objectives[0].State == TfwPositionState.Secured && _tfwState.Objectives[2].State == TfwPositionState.Secured);
        TfwCheck("the tracker names the Warboss", TfwHudModel.ObjectiveName(TfwBattle.CommanderObjective, TfwNow.Phase == TfwRunPhase.FinalAssault) == "WARBOSS");
        await TfwWait(1.0);
        TfwCheck("the boss bar names the Warboss", TfwHudModel.BossName(TfwNow, _tfwState.Objectives) == "ORK WARBOSS");
        await TfwShot("final-assault");
        // The Warboss has no leash: he comes to meet the player and may fall on the way.
        await TfwPathTo(_tfwState.Objectives[TfwBattle.CommanderObjective].Position, "walked towards the HQ, or the Warboss fell first", 90.0, () => TfwNow.Phase == TfwRunPhase.Won);
        if (!await TfwUntil("the Warboss falls: the war is won", () => TfwNow.Phase == TfwRunPhase.Won, 30.0)) return;
        await TfwUntil("the prompt says the war is won", () => TfwPromptNow == "THE WARBOSS IS DEAD. THE WAR IS WON", 3.0);
        await TfwShot("war-won");
        await TfwTestRunPanel();
    }

    /// <summary>Panels a scripted step does not read go away as a player sends them: Enter on CONTINUE; a card is picked.</summary>
    async Task TfwTestPanelsAway()
    {
        await TfwWait(0.4);
        for (int k = 0; k < 8 && _tfwModal is not null; k++)
        {
            await TfwTap(Key.Enter);
            await TfwWait(0.3);
        }
    }

    /// <summary>Every <c>TFWSCORE</c> line of the server's output for this player (<c>--tfw-server-log</c>); empty without it.</summary>
    string[] TfwServerScores()
    {
        if (CmdArg("tfw-server-log") is not { Length: > 0 } path || _client is null) return [];
        using var stream = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        string prefix = $"TFWSCORE player {_client.PlayerId} ";
        return reader.ReadToEnd().Split('\n').Select(l => l.Trim()).Where(l => l.Contains(prefix, StringComparison.Ordinal)).ToArray();
    }

    static long TfwServerFigure(string line, string word)
    {
        int at = line.LastIndexOf(word + " ", StringComparison.Ordinal);
        if (at < 0) return -1;
        var digits = new string(line[(at + word.Length + 1)..].TakeWhile(char.IsDigit).ToArray());
        return long.TryParse(digits, out long v) ? v : -1;
    }

    static string TfwThousands(long v) => v.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The map's panel: every figure as tag 12 gave it and, with the server's log, as the server banked it; Enter goes on to the cards, one is picked, tag 15 confirms a weapon.</summary>
    async Task<bool> TfwTestMapPanel(int number)
    {
        if (!await TfwUntil($"battle {number}: the end-of-map panel shows", () => _tfwModal is TfwScorePanel, 5.0)) return false;
        var panel = (TfwScorePanel)_tfwModal!;
        var row = _tfwState.ScoreHistory[^1];
        TfwCheck($"battle {number}: the panel shows tag 12's map points", panel.MapPoints == TfwThousands(row.Points), $"{panel.MapPoints} against {row.Points}");
        TfwCheck($"battle {number}: the panel shows tag 12's run total", panel.RunTotal == TfwThousands(_tfwState.ScoreOwn?.RunTotal ?? 0), panel.RunTotal);
        TfwCheck($"battle {number}: the map was won", row.Result == TfwMapResult.Won, row.Result.ToString());
        await TfwWait(0.3);
        if (TfwServerScores() is { Length: > 0 } server)
        {
            var line = server.Last(l => l.Contains(" map ", StringComparison.Ordinal));
            GD.Print($"TFWTEST note: server says: {line}");
            TfwCheck($"battle {number}: shown map points equal the server's", TfwThousands(TfwServerFigure(line, "points")) == panel.MapPoints, line);
            TfwCheck($"battle {number}: shown run total equals the server's", TfwThousands(TfwServerFigure(line, "total")) == panel.RunTotal, line);
        }
        await TfwShot($"map-score-{number}");
        await TfwTap(Key.Enter);
        if (!await TfwUntil($"battle {number}: Enter goes on to the reward cards", () => _tfwModal is TfwRewardPanel, 5.0)) return false;
        var cards = (TfwRewardPanel)_tfwModal!;
        await TfwFrames(4);
        await TfwShot($"reward-cards-{number}");
        var offer = _tfwState.Reward!.Value;
        TfwCheck($"battle {number}: the cards offer a weapon, an equipment or a perk", cards.Card(0) is not null || cards.Card(1) is not null || cards.Card(2) is not null);
        // The first battle takes the weapon card, for slot 3; the second a perk or the equipment, if offered.
        int pick = number == 1 ? 0 : cards.Card(2) is not null ? 2 : cards.Card(1) is not null ? 1 : 0;
        if (cards.Card(pick) is null) pick = cards.Card(1) is not null ? 1 : cards.Card(2) is not null ? 2 : 0;
        cards.Card(pick)?.GrabFocus();
        await TfwFrames(2);
        var chosen = pick switch { 0 => offer.First, 1 => offer.Second, _ => offer.Third };
        int before = _tfwState.Commands.Pending;
        await TfwTap(Key.Enter);
        TfwCheck($"battle {number}: Enter picks a card, and the panel closes", _tfwModal is null, _tfwModal?.Name.ToString() ?? "");
        if (chosen.Kind == TfwRewardKind.Weapon && !SmSim.Extras.Musou.MusouDropItem.IsMelee(chosen.Item))
            await TfwUntil($"battle {number}: the picked weapon stands in an extra slot", () => Enumerable.Range(3, 3).Any(k => _tfwState.Arsenal.Weapon(k) == chosen.Item), 3.0);
        else await TfwWait(0.5);
        await TfwWait(0.3);
        TfwCheck($"battle {number}: the offer is answered", _tfwModal is null && !PickerBusy);
        return true;
    }

    /// <summary>Owner's item 25: the landing gives the full chosen loadout, not the pistol alone.</summary>
    async Task TfwTestLoadout()
    {
        await TfwWait(0.3);
        var me = _client!.Predicted;
        bool hasPrimary = _config is { } c && SmSim.Extras.Musou.MusouLoadout.DefaultPrimary(c, me.Class) >= 0;
        TfwCheck("the landing gives the full loadout, not the pistol alone", !hasPrimary || Loadouts.Weapon(_config!, me, WeaponSlot.Ranged) is not null, $"primary {me.Primary}, pistol {me.Pistol}, class {me.Class}");
    }

    int _tfwTestUseTicks;

    /// <summary>The Interact bit for the scripted player's next inputs (<see cref="TfwTestCrateAndSlot"/>).</summary>
    void TfwTestInput(ref PlayerInput input)
    {
        if (_tfwTestUseTicks <= 0) return;
        _tfwTestUseTicks--;
        input = input with { Buttons = input.Buttons | InputButtons.Interact };
    }

    /// <summary>A crate opened on foot, then slot 3 equipped with its key, and key 2 back on the own primary.</summary>
    async Task<bool> TfwTestCrateAndSlot()
    {
        var client = _client!;
        await TfwUntil("the crates are listed for this client", () => _musouDropLive.Values.Any(d => d.Crate), 5.0);
        var crates = _musouDropLive.Values.Where(d => d.Crate).ToList();
        TfwCheck("six crates at most, one per site", crates.Count is > 0 and <= TfwSites.CrateCount, crates.Count.ToString());
        var near = crates.OrderBy(d => Num.Vector3.DistanceSquared(d.Position, client.Predicted.Position)).First();
        await TfwShot("slots-before");
        if (!await TfwPathTo(near.Position, "walked to the nearest crate", 120.0)) return false;
        TfwKey(Key.S, true);   // a step back: a player opens it from beside it, in reach (1.5 m)
        await TfwWait(0.15);
        TfwKey(Key.S, false);
        await TfwWait(0.5);
        await TfwShot("crate");
        var before = _tfwState.Arsenal;
        int primaryBefore = client.Predicted.Primary, pistolBefore = client.Predicted.Pistol;
        _tfwTestUseTicks = 4;   // the script's mouse is not captured, which the core's E asks for: the same input bit, sent by hand
        await TfwFrames(8);
        bool taken = await TfwUntil("E opens the crate: the weapon stands in an extra slot", () => _tfwState.Arsenal != before || !_musouDropLive.ContainsKey(near.Id), 3.0);
        await TfwWait(0.4);
        TfwCheck("the crate left the primary alone", client.Predicted.Primary == primaryBefore && client.Predicted.Pistol == pistolBefore, $"{client.Predicted.Primary}, {client.Predicted.Pistol}");
        int key = Enumerable.Range(3, 3).FirstOrDefault(k => _tfwState.Arsenal.Weapon(k) >= 0);
        TfwCheck("an extra slot is filled", key >= 3, _tfwState.Arsenal.ToString());
        if (key < 3) return taken;
        int weapon = _tfwState.Arsenal.Weapon(key);
        await TfwTap(key == 3 ? Key.Key3 : key == 4 ? Key.Key4 : Key.Key5);
        await TfwUntil($"key {key} equips the extra slot", () => _tfwState.Arsenal.LoadedPrimary == key || _tfwState.Arsenal.LoadedPistol == key, 3.0);
        await TfwUntil($"the slot's weapon is in hand", () => Loadouts.WeaponIndex(_config!, client.Predicted, client.Predicted.Slot) == weapon, 3.0);
        await TfwFrames(6);
        await TfwShot("slot-in-hand");
        bool primary = _tfwState.Arsenal.LoadedPrimary == key;
        await TfwTap(primary ? Key.Key2 : Key.Key1);
        await TfwUntil("the own weapon comes back with its key", () => _tfwState.Arsenal.LoadedPrimary == 0 && _tfwState.Arsenal.LoadedPistol == 0, 3.0);
        await TfwUntil("the original weapon is in its field again", () => client.Predicted.Primary == primaryBefore && client.Predicted.Pistol == pistolBefore, 3.0);
        return true;
    }

    /// <summary>The run's panel: the rows and the total against tag 12 and the server, the local best, and COPY RESULT.</summary>
    async Task TfwTestRunPanel()
    {
        if (!await TfwUntil("the end-of-run panel shows", () => _tfwModal is TfwRunPanel, 6.0)) return;
        var panel = (TfwRunPanel)_tfwModal!;
        long sum = _tfwState.ScoreHistory.Sum(m => (long)m.Points);
        TfwCheck("the run's total is the sum of its maps", panel.Total == TfwThousands(sum), $"{panel.Total} against {sum}");
        TfwCheck("one row per map", panel.Rows.Count == _tfwState.ScoreHistory.Count, panel.Rows.Count.ToString());
        await TfwWait(0.5);
        if (TfwServerScores() is { Length: > 0 } server)
        {
            var line = server.Last();
            GD.Print($"TFWTEST note: server says: {line}");
            TfwCheck("shown run total equals the server's", TfwThousands(TfwServerFigure(line, "total")) == panel.Total, line);
            var maps = server.Where(l => l.Contains(" map ", StringComparison.Ordinal)).ToArray();
            TfwCheck("one server map line per shown row", maps.Length == panel.Rows.Count, $"{maps.Length} against {panel.Rows.Count}");
            for (int k = 0; k < Math.Min(maps.Length, panel.Rows.Count); k++)
                TfwCheck($"map {k + 1}'s row equals the server's", panel.Rows[k].EndsWith(TfwThousands(TfwServerFigure(maps[k], "points")), StringComparison.Ordinal), $"{panel.Rows[k]} against {maps[k]}");
        }
        else GD.Print("TFWTEST note: no --tfw-server-log: the server's figures were not compared");
        await TfwFrames(4);
        await TfwShot("run-score");
        var best = TfwBest.Load(TfwBestDirectory());
        TfwCheck("the local best holds the run", best is not null && TfwThousands(best.Total) is var saved && (saved == panel.Total || best.Total >= sum), best?.ToString() ?? "none");
        panel.CopyButton.GrabFocus();
        await TfwFrames(2);
        await TfwTap(Key.Enter);
        TfwCheck("COPY RESULT makes the line", panel.Result.Contains(panel.Total, StringComparison.Ordinal) && panel.Result.Contains("VICTORY", StringComparison.Ordinal), panel.Result);
        if (DisplayServer.GetName() != "headless") TfwCheck("the line is on the clipboard", DisplayServer.ClipboardGet() == panel.Result, DisplayServer.ClipboardGet());
        await TfwShot("run-score-copied");
        panel.CloseButton.GrabFocus();
        await TfwFrames(2);
        await TfwTap(Key.Enter);
        TfwCheck("CLOSE closes the panel", _tfwModal is null);
    }

    /// <summary>
    /// The hub's look and its walls (item 29): shots from the spawn looking out the ramp and forward, a walk up the ramp to its end (it must stop there, aboard, with the hub's own environment on the camera
    /// and the map's sun off), shots there looking out and back, then a push against each side wall of the corridor and the end wall (the player must not leave the ship).
    /// </summary>
    async Task<bool> TfwHubTour()
    {
        var client = _client!;
        var hub = TfwHub!;
        await TfwFrames(20);
        // A headless run draws nothing (TfwMarkersFrame), so there is no sky to check there; the walls below are checked either way.
        TfwCheck("the camera is aboard: the hub's sky is on and the map's sun is off", DisplayServer.GetName() == "headless" || (_tfwAboard && _camera!.Environment is not null && _sun is { Visible: false }));
        _yaw = TfwShip.RampYaw; _pitch = 0f;
        await TfwFrames(15);
        await TfwShot("hub-spawn-out");
        _yaw = 0f;
        await TfwFrames(20);
        await TfwShot("hub-spawn-forward");
        _yaw = TfwShip.RampYaw;
        // Down the ramp, to the wall at its end (model Y is the world's -Y from the centre's row).
        double end = _connectedFor + 25.0;
        TfwKey(Key.W, true);
        while (_connectedFor < end && client.Predicted.Position.Y - hub.ToWorld(new Num.Vector3(0f, TfwShip.RampEndY, 0f)).Y > 1.0f) await TfwFrames();
        await TfwShot("hub-ramp-out");
        await TfwWait(2.0);
        TfwKey(Key.W, false);
        var at = client.Predicted.Position;
        float along = hub.Centre.Y + TfwShip.MidY - at.Y;   // the model's Y
        TfwCheck("W up the ramp stops at its end wall, aboard", hub.Contains(at) && along > TfwShip.RampEndY - 1.2f && along < TfwShip.RampEndY, at.ToString());
        TfwCheck("the ramp is the floor's height", MathF.Abs(at.Z - hub.Centre.Z) < 0.15f, at.ToString());
        _pitch = -0.35f;
        await TfwFrames(20);
        await TfwShot("hub-ramp-end-down");
        _pitch = 0f; _yaw = 0f;
        await TfwFrames(20);
        await TfwShot("hub-ramp-back");
        // Push against the walls: the side walls from the middle of the corridor, then the end wall again.
        bool aboard = true;
        foreach (var (name, key, yaw) in new[] { ("east", Key.W, -MathF.PI / 2f), ("west", Key.W, MathF.PI / 2f), ("cockpit", Key.W, 0f), ("ramp", Key.W, TfwShip.RampYaw) })
        {
            await TfwWalkTo(hub.OnFloor(new Num.Vector2(0f, TfwShip.MidY + 3f)), "back in the corridor", 20.0);
            _yaw = yaw;
            TfwKey(key, true);
            for (int i = 0; i < 90; i++) { await TfwFrames(); aboard &= hub.Contains(client.Predicted.Position); }
            TfwKey(key, false);
            await TfwWait(0.3);
            TfwCheck($"walking {name} into the wall stops inside the ship", aboard && hub.Contains(client.Predicted.Position), client.Predicted.Position.ToString());
        }
        _yaw = 0f;
        return true;
    }

    async Task TfwTestScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the hub", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 60.0)) return;
        var start = TfwNow;
        TfwCheck("the reserves are the run's", start.Transport == TfwBattleRules.StartTransport && start.Supplies == TfwBattleRules.StartSupplies, $"{start.Transport}, {start.Supplies}");
        TfwCheck("the station prompt fits the spot", _tfwAt == TfwHudModel.StationAt(client.Predicted.Position, TfwHub!), $"{_tfwAt} at {client.Predicted.Position}");   // a respawn point is anywhere in the room

        if (!await TfwHubTour()) return;

        // Equipment
        if (!await TfwWalkTo(TfwHub!.EquipmentStation, "walked to Equipment")) return;
        await TfwFrames(3);
        TfwCheck("the Equipment prompt shows", _tfwHud is { Data.Station: "E: EQUIPMENT" }, _tfwHud?.Data.Station ?? "no HUD");
        await TfwShot("stormbird-prompt");
        await TfwTap(Key.E);
        if (!await TfwUntil("E at Equipment opens the loadout screen", () => _pickerCreator is { Visible: true } && _tfwEquipmentShown, 90.0)) return;
        await TfwShot("equipment");
        _pickerCreator!.Back();
        await TfwUntil("BACK returns to the game", () => !PickerBusy, 3.0);

        // Destination, the overlay and the ways out
        if (!await TfwWalkTo(TfwHub!.DestinationStation, "walked to Destination")) return;
        await TfwFrames(3);
        TfwCheck("the Destination prompt shows", _tfwHud is { Data.Station: "E: DESTINATION" }, _tfwHud?.Data.Station ?? "no HUD");
        await TfwShot("destination-prompt");
        TfwKey(Key.M, true);
        await TfwFrames(4);
        TfwCheck("M shows the war map", _tfwMapHeld && _tfwPeek is { Interactive: false } && _tfwPeekLayer!.Visible);
        TfwCheck("the game keeps its input under M", !PickerBusy);
        await TfwShot("war-map-held");
        TfwKey(Key.M, false);
        await TfwFrames(4);
        TfwCheck("M let go hides it", !_tfwMapHeld && !_tfwPeekLayer!.Visible);

        await TfwTap(Key.E);
        if (!await TfwUntil("E at Destination opens the war map", () => _tfwPanel is not null, 3.0)) return;
        await TfwFrames(3);
        TfwCheck("the map takes the game's input", PickerBusy);
        TfwCheck("DEPLOY can be pressed", _tfwPanel!.DeployButton(TfwLane.A) is { Disabled: false }, _tfwPanel.Note);
        await TfwShot("war-map");
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Keycode = Key.Escape, Pressed = true });
        await TfwFrames(2);
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = Key.Escape, Keycode = Key.Escape, Pressed = false });
        await TfwFrames(3);
        TfwCheck("Esc closes the map, and opens no menu", _tfwPanel is null && !PickerBusy, $"panel {_tfwPanel is not null}, busy {PickerBusy}");

        await TfwTap(Key.E);
        await TfwUntil("E opens it again", () => _tfwPanel is not null, 3.0);
        await TfwFrames(3);
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.B, Pressed = true });
        await TfwFrames(2);
        Input.ParseInputEvent(new InputEventJoypadButton { ButtonIndex = JoyButton.B, Pressed = false });
        await TfwFrames(3);
        TfwCheck("B on a controller closes it", _tfwPanel is null && !PickerBusy);
        TfwKey(Key.W, true); TfwKey(Key.W, false);   // the keyboard takes the scheme back from the pad
        await TfwFrames(3);

        await TfwTap(Key.E);
        await TfwUntil("E opens it a third time", () => _tfwPanel is not null, 3.0);
        await TfwFrames(6);
        var close = _tfwPanel?.CloseButton;
        if (close is not null)
        {
            var at = GetViewport().GetFinalTransform() * (close.GetGlobalTransformWithCanvas() * (close.Size / 2f));
            Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
            await TfwFrames(2);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
            await TfwFrames(2);
            Input.ParseInputEvent(new InputEventMouseButton { Position = at, GlobalPosition = at, ButtonIndex = MouseButton.Left, Pressed = false });
            await TfwFrames(4);
        }
        TfwCheck("a mouse click on CLOSE closes it", _tfwPanel is null);
        if (_tfwPanel is not null) return;

        // The first battle: orders and the pickup
        if (!await TfwTestDeploy(TfwLane.B)) return;
        var landed = TfwNow;
        TfwCheck("Deploy took one transport", landed.Transport == start.Transport - TfwBattleRules.DeployCost, landed.Transport.ToString());
        await TfwWait(0.5);
        var me = client.Predicted.Position;
        TfwCheck("the player stands at the landing zone", _tfwState.Sites.Count > 0 && Num.Vector2.Distance(new(me.X, me.Y), new(_tfwState.Sites[0].X, _tfwState.Sites[0].Y)) < TfwMap.LandingRadius, $"{me}");
        TfwCheck("the HUD shows the Guardsmen and the retainer", landed.Own.Guards > 0 && landed.Own.Squads > 0 && landed.Own.Retainer == TfwRetainerState.Ready, TfwHudModel.GuardsText(landed.Own) + "; " + TfwHudModel.RetainerText(landed.Own));
        TfwCheck("the first position is held", _tfwState.Objectives[0].State >= TfwPositionState.Held);
        await TfwWait(1.5);
        await TfwShot("battle-hud");
        await TfwOrder(Key.X, MarineOrder.Hold, GameAction.NextWeapon);
        await TfwOrder(Key.V, MarineOrder.Attack, GameAction.Melee);
        await TfwOrder(Key.Z, MarineOrder.Follow, null);

        TfwKey(Key.P, true);
        await TfwFrames(10);
        TfwCheck("P held fills the bar", _tfwKeys.PickupHold > 0f && TfwNow.Phase == TfwRunPhase.Battle, $"{_tfwKeys.PickupHold}");
        await TfwUntil("P held 1 s calls the pickup", () => TfwNow.Phase == TfwRunPhase.Pickup, 4.0);
        TfwKey(Key.P, false);
        await TfwUntil("the countdown shows", () => TfwNow.Own.PickupSeconds > 0 && TfwPromptNow.StartsWith("PICKUP IN", StringComparison.Ordinal), 3.0);
        await TfwShot("pickup-countdown");
        await TfwFrames(4);
        TfwKey(Key.P, true);
        await TfwUntil("P held again calls it off", () => TfwNow.Phase == TfwRunPhase.Battle, 4.0);
        TfwKey(Key.P, false);
        await TfwFrames(4);
        TfwKey(Key.P, true);
        await TfwUntil("P held once more calls it again", () => TfwNow.Phase == TfwRunPhase.Pickup, 4.0);
        TfwKey(Key.P, false);
        if (!await TfwUntil("the pickup brings the player home", () => TfwNow.Phase == TfwRunPhase.Hub, 25.0)) return;
        await TfwWait(0.5);
        await TfwTestPanelsAway();   // the map's score: a left map shows it too
        var home = TfwNow;
        TfwCheck("the pickup took one transport", home.Transport == landed.Transport - TfwBattleRules.PickupCost, home.Transport.ToString());
        TfwCheck("the player is in the Stormbird", TfwHub!.Contains(client.Predicted.Position), client.Predicted.Position.ToString());
        TfwCheck("the objectives are gone", _tfwState.Objectives.Count == 0 || TfwNow.Own.Guards == 0);
        if (CmdFlag("tfw-test-win")) { await TfwTestWinRun(); return; }
        if (System.Environment.GetEnvironmentVariable("SM_TFW_DOWN_TEST") is null && !CmdFlag("tfw-test-downs")) return;

        // The second battle: down alone, the retainer revives
        if (!await TfwTestDeploy(TfwLane.A)) return;
        if (await TfwUntil("the player falls (SM_TFW_DOWN_TEST)", () => !client.Predicted.Alive, 60.0))
        {
            await TfwWait(0.3);
            TfwCheck("the revive window shows", TfwNow.Own.ReviveSeconds > 0 && TfwPromptNow.Contains("RETAINER", StringComparison.Ordinal), TfwPromptNow);
            await TfwShot("revive");
            await TfwUntil("the retainer revives the player", () => client.Predicted.Alive, 30.0);
        }
        TfwKey(Key.P, true);
        await TfwUntil("P calls the pickup again", () => TfwNow.Phase == TfwRunPhase.Pickup, 4.0);
        TfwKey(Key.P, false);
        if (!await TfwUntil("home again", () => TfwNow.Phase == TfwRunPhase.Hub, 25.0)) return;

        // The third: down with the retainer, the extraction
        await TfwWait(0.5);
        byte supplies = TfwNow.Supplies;
        if (!await TfwTestDeploy(TfwLane.C)) return;
        if (!await TfwUntil("the player falls with its retainer", () => !client.Predicted.Alive, 60.0)) return;
        await TfwUntil("the extraction's prompt shows", () => TfwNow.Own.ExtractionSeconds > 0 && TfwPromptNow.Contains("EXTRACTION", StringComparison.Ordinal), 5.0);
        await TfwShot("extraction-prompt");
        await TfwUntil("the extraction brings the player home", () => TfwNow.Phase == TfwRunPhase.Hub, 8.0);
        await TfwWait(0.5);
        var last = TfwNow;
        TfwCheck("it cost the supplies", last.Supplies == supplies - TfwBattleRules.ExtractionSupplies, last.Supplies.ToString());
        TfwCheck("the retainer recovers", last.Own.Retainer == TfwRetainerState.Recovering && last.Own.RecoverySeconds > 0, TfwHudModel.RetainerText(last.Own));
        await TfwTestPanelsAway();
        await TfwShot("hub-after-extraction");
    }
}
