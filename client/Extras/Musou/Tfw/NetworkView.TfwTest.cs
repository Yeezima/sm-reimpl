using System;
using System.IO;
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
/// run instead: two battles won on one front through all three positions and the commander, each left by the free pickup, and the final assault to the Warboss's death. One <c>TFWTEST PASS</c> or <c>FAIL</c> line per step; quits with 0, or 1 when any failed.
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
        return true;
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
    }

    async Task TfwTestScript()
    {
        var client = _client!;
        if (!await TfwUntil("the war is on, in the hub", () => client.Match.State == MatchState.Playing && _tfwState.War is { Phase: TfwRunPhase.Hub } && client.Predicted.Alive, 60.0)) return;
        var start = TfwNow;
        TfwCheck("the reserves are the run's", start.Transport == TfwBattleRules.StartTransport && start.Supplies == TfwBattleRules.StartSupplies, $"{start.Transport}, {start.Supplies}");
        TfwCheck("the station prompt fits the spot", _tfwAt == TfwHudModel.StationAt(client.Predicted.Position, TfwHub!), $"{_tfwAt} at {client.Predicted.Position}");   // a respawn point is anywhere in the room

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
        await TfwShot("hub-after-extraction");
    }
}
