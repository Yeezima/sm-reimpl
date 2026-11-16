using Godot;
using SmNet.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;

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
            var target = view.Targets[0];
            _wfBotDeployAt = now;
            _wfBotDeploys++;
            m.RequestDeploy(target);
            GD.Print($"t {now:F1} s: WFBOT leads and deploys to {target.Kind} lane {target.Lane} (deploy {_wfBotDeploys})");
        }
        if (now - _wfBotLogAt < 5.0) return;
        _wfBotLogAt = now;
        GD.Print($"t {now:F0} s: WFBOT phase {view.Phase} deployments {view.Deployments} leader {view.ViewerIsLeader} identified {m.Identified} stage {m.Stage?.State.ToString() ?? "-"} fronts {string.Join(',', view.Fronts)} alive {me.Alive}");
    }
}
