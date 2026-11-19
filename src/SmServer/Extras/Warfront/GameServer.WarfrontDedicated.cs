using SmSim.Extras.Warfront.Campaign;

namespace SmServer;

/// <summary>
/// The dedicated server's own decisions (S2; only with <c>--warfront-dedicated</c>, player-hosted play is unchanged): a default deployment when the leader sits idle on the ship, and a new run
/// when the results screen has stood long enough. Rules and timings: docs/warfront.md "Dedicated mode"; all kind=ours in spec/behavior.md "Dedicated server operation".
/// </summary>
public sealed partial class GameServer
{
    /// <summary>The ship's idle time after which the server deploys for the leader (an estimate, ours).</summary>
    internal static double WarfrontDeployFallbackSeconds = 90;

    /// <summary>The results screen's time after which the server starts a new run when the leader has not (an estimate, ours).</summary>
    internal static double WarfrontAutoNewRunSeconds = 120;

    /// <summary>With nobody connected the new run waits this long after the results first stood (clients of a restarted stage reconnect in that time; an estimate, ours).</summary>
    internal static double WarfrontEmptyGraceSeconds = 10;

    /// <summary>How often a deployment that did not start (refused for another reason) is asked again.</summary>
    const double WarfrontFallbackRetrySeconds = 5;

    // The ship clock: since when this leader has had an idle ship (null: not counting). The results clock: since when the results stand with someone to read them.
    ulong? _wfShipLeader;
    uint _wfShipSince, _wfShipNextTry;
    uint? _wfResultsSince;

    /// <summary>
    /// The deployment the server picks for an idle leader: the HQ defence when it is mandatory, else the node of the lane with the lowest front (ties: lane A, B, C), else the fortress through the lowest
    /// cleared lane. Only from <see cref="WarfrontCampaign.AvailableTargets"/>; null when there is none (not on the ship).
    /// </summary>
    internal static WarfrontTarget? WarfrontDefaultTarget(WarfrontCampaign c)
    {
        var list = c.AvailableTargets();
        if (list.Count == 0) return null;
        foreach (var t in list) if (t.Kind == WarfrontTargetKind.Hq) return t;
        var nodes = list.Where(t => t.Kind == WarfrontTargetKind.Node).OrderBy(t => c.Front(t.Lane)).ThenBy(t => t.Lane).ToList();
        if (nodes.Count > 0) return nodes[0];
        var forts = list.Where(t => t.Kind == WarfrontTargetKind.Fortress).OrderBy(t => t.Lane).ToList();
        return forts.Count > 0 ? forts[0] : null;
    }

    void WarfrontDedicatedStep(WarfrontServerState s)
    {
        if (!WarfrontRunFiles.Dedicated) return;
        WarfrontDeployFallbackStep(s);
        WarfrontAutoNewRunStep(s);
    }

    /// <summary>
    /// Rule 1 and 3. The clock runs while the campaign is on the ship with nothing under way (no preparation, no Go, no landing, no arrival), the run is not over and a leader is connected and identified; it
    /// restarts at zero whenever any of that stops holding, or the leader changes (a new leader gets the full time). With nobody connected there is no leader, so it never fires on an empty server.
    /// </summary>
    void WarfrontDeployFallbackStep(WarfrontServerState s)
    {
        var c = s.Campaign;
        bool idle = c.Phase == WarfrontPhase.Ship && !s.Finished && !s.Leaving && s.Prep is null && s.Go is null && !s.Landing && !s.Arriving && !s.RestartDue;
        if (!idle || c.Leader is not { } leader || WarfrontPlayerOf(leader) is not { } player) { _wfShipLeader = null; return; }
        if (_wfShipLeader != leader) { _wfShipLeader = leader; _wfShipSince = World.Tick; _wfShipNextTry = 0; return; }
        uint due = _wfShipSince + (uint)(WarfrontDeployFallbackSeconds * WarfrontRate);
        if (World.Tick < due || World.Tick < _wfShipNextTry) return;
        _wfShipNextTry = World.Tick + (uint)(WarfrontFallbackRetrySeconds * WarfrontRate);
        if (WarfrontDefaultTarget(c) is not { } target) return;
        string what = target.Kind switch
        {
            WarfrontTargetKind.Hq => "the HQ defence",
            WarfrontTargetKind.Fortress => $"the Ork fortress through lane {(char)('A' + target.Lane)}",
            _ => $"lane {(char)('A' + target.Lane)}",
        };
        WfLog($"dedicated: no deployment in {(int)WarfrontDeployFallbackSeconds} s; the server deploys to {what}");
        s.GateRefusal = $"No deployment chosen: the server sends the squad to {what}";   // the Stage None text, shown for 5 s (WarfrontStageSection)
        s.GateRefusalTick = World.Tick;
        WarfrontDeploy(player, target, serverFallback: true);   // the normal path, except the co-op gate: whoever is still choosing a class or cards had 90 s
    }

    /// <summary>
    /// Rule 2. With the results standing: <see cref="WarfrontAutoNewRunSeconds"/> after the server first saw them, or, when no human is connected, as soon as the first <see cref="WarfrontEmptyGraceSeconds"/> have passed
    /// (a stage restarted on a finished run gives the clients that were reading it time to reconnect and read it).
    /// </summary>
    void WarfrontAutoNewRunStep(WarfrontServerState s)
    {
        if (!s.Finished || s.Leaving || s.Go is not null || !s.Campaign.IsOver) { _wfResultsSince = null; return; }
        _wfResultsSince ??= World.Tick;
        double elapsed = (double)(World.Tick - _wfResultsSince.Value) / WarfrontRate;
        bool nobody = s.KeyOf.Count == 0 && s.Waiting.Count == 0;
        if (nobody && elapsed >= WarfrontEmptyGraceSeconds) { WarfrontBeginNewRun(s, "dedicated: nobody is connected"); return; }
        if (elapsed >= WarfrontAutoNewRunSeconds) WarfrontBeginNewRun(s, $"dedicated: the leader did not start one in {(int)WarfrontAutoNewRunSeconds} s");
    }
}
