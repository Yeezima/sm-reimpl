using System;
using Godot;
using SmNet.Extras.Warfront;
using SmNet.Extras.Musou.Campaign;
using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Results;

namespace SmClient;

/// <summary>
/// The Warfront test bot (slice W5b; a test script, not an AI of the original): <c>--warfront-bot</c> on a <c>--bot</c> client. It identifies like any client; when it is the leader on the ship it deploys to the
/// first target the campaign offers after <c>--warfront-deploy-after</c> seconds (default 3), again after 10 s if the deploy was not taken; every other moment it plays as Musou Mode's co-op bot does (it
/// stands in the hub and fights in a battle). It never sends Musou's own Deploy (which would be a command of another mode). A <c>WFBOT</c> line every 5 s names the phase, the stage and the leader.
/// </summary>
public partial class NetworkView
{
    static readonly bool WarfrontBotFlag = CmdFlag("warfront-bot");
    double _wfBotShipSince = -1, _wfBotLogAt, _wfBotDeployAt = double.NegativeInfinity;
    int _wfBotDeploys;
    double? _wfBotAfter;

    double WarfrontBotAfter => _wfBotAfter ??= double.TryParse(CmdArg("warfront-deploy-after"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) && s >= 0 ? s : 3.0;

    void WarfrontBotFrame(SmSim.PlayerState me, double now)
    {
        if (!_bot || !WarfrontBotFlag) return;
        _coopDeployAfter = 1e9;   // Musou's own Deploy is another mode's command
        var m = WarfrontModel;
        var view = m.Hub.View;
        bool onShip = view.Phase == WarfrontPhase.Ship && m.Identified && m.Stage is not { State: not WarfrontStageState.None };
        if (!onShip) _wfBotShipSince = -1;
        else if (_wfBotShipSince < 0) _wfBotShipSince = now;
        if (onShip && view.ViewerIsLeader && view.Targets.Count > 0 && m.PendingDeploy is null && now - _wfBotShipSince >= WarfrontBotAfter && now - _wfBotDeployAt >= 10.0)
        {
            var target = CmdArg("warfront-bot-pick") == "last" ? view.Targets[^1] : view.Targets[0];   // W17 test aid: last = the fortress / the HQ when offered after the lanes
            _wfBotDeployAt = now;
            _wfBotDeploys++;
            m.RequestDeploy(target);
            GD.Print($"t {now:F1} s: WFBOT leads and deploys to {target.Kind} lane {target.Lane} (deploy {_wfBotDeploys})");
        }
        if (now - _wfBotLogAt < 5.0) return;
        _wfBotLogAt = now;
        GD.Print($"t {now:F0} s: WFBOT phase {view.Phase} deployments {view.Deployments} leader {view.ViewerIsLeader} identified {m.Identified} stage {m.Stage?.State.ToString() ?? "-"} fronts {string.Join(',', view.Fronts)} alive {me.Alive}");
    }

    // ---- capture aids for a windowed, human-like client (screenshots of the stage flow; test aids, not game rules) ----------------------------------------------------------

    static readonly bool WarfrontUiDemo = CmdFlag("warfront-ui-demo"), WarfrontResultsDemo = CmdFlag("warfront-results-demo");
    bool _wfDemoMap, _wfDemoDeployed, _wfDemoResults;

    /// <summary>
    /// <c>--warfront-ui-demo</c> (windowed, not a bot): 10 s after the join the war map opens as if the player had pressed Use at the station; 18 s after, when this client leads on the ship, the first deployment row is pressed.
    /// <c>--warfront-results-demo</c>: 10 s after the join a synthetic record (made here, no real run) goes through the Results chunks' own path, so the panel opens as it would at the end of a run.
    /// </summary>
    void WarfrontDemoFrame(double now)
    {
        if (_bot) return;
        var m = WarfrontModel;
        if (WarfrontUiDemo && !_wfDemoMap && now >= 10.0 && m.HubSections > 0 && m.TravelVisible == false) { _wfDemoMap = true; WarfrontOpenHubPanel(); }
        if (WarfrontUiDemo && !_wfDemoDeployed && now >= 18.0 && m.Hub.View is { Phase: WarfrontPhase.Ship, ViewerIsLeader: true } v && v.Targets.Count > 0)
        {
            _wfDemoDeployed = true;
            GD.Print($"t {now:F1} s: warfront demo: the first deployment row is pressed");
            m.Hub.RequestDeploy(v.Targets[0]);
        }
        WarfrontStationTest(now);
        if (WarfrontResultsDemo && !_wfDemoResults && now >= 10.0)
        {
            _wfDemoResults = true;
            var c = new WarfrontCampaign(7);
            c.Join(m.Key, m.Name, out _);
            c.Join(0x51, "Brann", out _);
            var record = WarfrontResults.Build(c, null, System.TimeSpan.FromSeconds(2710), new System.DateTime(2026, 11, 17, 12, 0, 0, System.DateTimeKind.Utc));
            foreach (var chunk in WarfrontResultsWire.Split(WarfrontResultsWire.Encode(record), 1))
                if (m.ApplyResults(chunk) is { } done) WarfrontRecordArrived(done);
        }
    }

    static readonly bool WarfrontStationTestOn = CmdFlag("warfront-station-test");
    int _wfStationStep;   // 0 walking, 1 use pressed, 2 done
    double _wfStationAt, _wfStationLog;

    static void WarfrontKey(Key key, bool pressed) => Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed });

    /// <summary>
    /// <c>--warfront-station-test</c> (windowed, not a bot; a capture aid): the player walks to the Thunderhawk's destination station with the forward key and presses the Use key (real key events through the input
    /// system, not a call of the panel), so the one path a player will use is the one tested. It logs the distance every 2 s and the press; the war map then opens through the hook.
    /// </summary>
    void WarfrontStationTest(double now)
    {
        if (!WarfrontStationTestOn || _wfStationStep >= 2 || TfwHub is not { } hub || _client is null) return;
        var me = _client.Predicted;
        var goal = hub.DestinationStation;
        float dx = goal.X - me.Position.X, dy = goal.Y - me.Position.Y;
        float flat = MathF.Sqrt(dx * dx + dy * dy);
        if (now - _wfStationLog >= 2.0) { _wfStationLog = now; GD.Print($"t {now:F1} s: warfront station test: {flat:F1} m from the destination station, station under the player: {_tfwAt}, step {_wfStationStep}"); }
        if (_wfStationStep == 0)
        {
            if (now < 6.0 || PickerBusy) return;
            _yaw = MathF.Atan2(-dx, dy);
            if (_tfwAt == TfwStation.Destination) { WarfrontKey(Key.W, false); WarfrontKey(Key.E, true); _wfStationStep = 1; _wfStationAt = now; GD.Print($"t {now:F1} s: warfront station test: Use pressed at {flat:F1} m"); }
            else WarfrontKey(Key.W, true);
        }
        else if (now - _wfStationAt > 0.3) { WarfrontKey(Key.E, false); _wfStationStep = 2; }
    }
}
