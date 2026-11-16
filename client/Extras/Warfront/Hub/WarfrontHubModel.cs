using System;
using System.Linq;
using SmSim.Extras.Warfront.Campaign;

namespace SmClient.Extras.Warfront.Hub;

/// <summary>
/// The Warfront hub's view-model (plan section 8; no Godot, no networking): holds the current <see cref="WarfrontHubView"/>, answers every text the war map and the travel screen
/// show, and turns a press on a deployment row into <see cref="DeployRequested"/>. W5's network feed calls <see cref="Apply"/> with each campaign state it receives (and
/// <see cref="SetTravelProgress"/> while the next battlefield loads) and sends the deploy command from <see cref="DeployRequested"/>; nothing here is wired to a socket.
/// All text is capitals, ours.
/// </summary>
public sealed class WarfrontHubModel
{
    public static readonly string[] NodeNames = { "WASTELAND", "OUTSKIRTS", "URBAN CORE" };

    WarfrontHubView _view = new();

    public WarfrontHubView View => _view;
    /// <summary>Something the panels draw changed.</summary>
    public event Action? Changed;
    /// <summary>The leader pressed a deployment row (only raised when <see cref="CanDeploy"/> holds). W5 sends the command.</summary>
    public event Action<WarfrontTarget>? DeployRequested;

    public int TravelJoined { get; private set; }
    public int TravelTotal { get; private set; }

    /// <summary>Feed: the campaign as it stands now. Nothing is raised unless something drawn changed.</summary>
    public void Apply(WarfrontHubView view)
    {
        if (_view.Signature == view.Signature) return;
        _view = view;
        if (view.Phase != WarfrontPhase.Travel) { TravelJoined = 0; TravelTotal = 0; }
        Changed?.Invoke();
    }

    /// <summary>Feed: while travelling, how many of the run's players have reached the new battlefield.</summary>
    public void SetTravelProgress(int joined, int total)
    {
        if (joined == TravelJoined && total == TravelTotal) return;
        TravelJoined = joined;
        TravelTotal = total;
        Changed?.Invoke();
    }

    // ---- deploying --------------------------------------------------------------------------------------------------------------------------

    public bool InShip => _view.Phase == WarfrontPhase.Ship;
    public bool IsOver => _view.Phase is WarfrontPhase.Won or WarfrontPhase.Lost;
    public bool IsTravelling => _view.Phase == WarfrontPhase.Travel;

    /// <summary>The leader on the ship, and the target is one the campaign offers (HQ defense alone when it is mandatory).</summary>
    public bool CanDeploy(WarfrontTarget target) => InShip && _view.ViewerIsLeader && _view.Targets.Contains(target);

    public bool RequestDeploy(WarfrontTarget target)
    {
        if (!CanDeploy(target)) return false;
        DeployRequested?.Invoke(target);
        return true;
    }

    /// <summary>The target a row stands for: rows 0 to 2 the lane's node (or the fortress through it when cleared), row 3 the HQ defense. Null when the lane has no target (broken through).</summary>
    public WarfrontTarget? RowTarget(int row)
    {
        if (row == 3) return WarfrontTarget.Hq;
        int f = _view.Fronts[row];
        return f is >= 1 and <= 3 ? WarfrontTarget.Node(row) : f == 4 ? WarfrontTarget.Fortress(row) : null;
    }

    // ---- text -------------------------------------------------------------------------------------------------------------------------------

    public static string LaneName(int lane) => "LANE " + (char)('A' + lane);

    /// <summary>The node a lane fights at, "NODE 2, OUTSKIRTS".</summary>
    public static string NodeText(int front) => $"NODE {front}, {NodeNames[Math.Clamp(front, 1, 3) - 1]}";

    public string DeployRowText(int row)
    {
        if (row == 3) return "DEPLOY: IMPERIAL HQ DEFENSE";
        int f = _view.Fronts[row];
        if (f is >= 1 and <= 3) return $"DEPLOY: {LaneName(row)}, {NodeText(f)}";
        if (f == 4) return $"DEPLOY: ORK FORTRESS, THROUGH {LaneName(row)}";
        return $"{LaneName(row)}: BROKEN THROUGH";
    }

    /// <summary>The text in lane <paramref name="lane"/>'s node box <paramref name="node"/> (1 to 3): its name and its state.</summary>
    public string NodeLabel(int lane, int node) => $"{node} {NodeNames[node - 1]}\n{NodeStateText(lane, node)}";

    public string NodeStateText(int lane, int node)
    {
        int f = _view.Fronts[lane];
        return node < f ? "CLEARED" : node == f ? "FRONT" : "ORK-HELD";
    }

    public string LaneStatus(int lane) => _view.Fronts[lane] switch
    {
        0 => "BROKEN THROUGH TO THE HQ",
        4 => "CLEARED, THE FORTRESS IS OPEN",
        var f => $"FIGHTING AT {NodeText(f)}",
    };

    public string LastText(int lane) => _view.Last[lane] switch
    {
        HubLastKind.PlayedWon => "LAST DEPLOYMENT: WON",
        HubLastKind.PlayedWiped => "LAST DEPLOYMENT: WIPED",
        HubLastKind.PlayedWipedHeldAtFloor => "LAST DEPLOYMENT: WIPED, HELD AT FLOOR",
        HubLastKind.RollWon => "LAST ROLL: WON, LANE HELD",
        HubLastKind.RollLost => "LAST ROLL: LOST, ONE NODE BACK",
        HubLastKind.RollLostHeldAtFloor => "LAST ROLL: LOST, HELD AT FLOOR",
        _ => "LAST ROLL: NONE YET",
    };

    /// <summary>The floor: a lane that was never advanced cannot fall below node 1.</summary>
    public string FloorText(int lane) => _view.Fronts[lane] == 1 && !_view.Advanced[lane] ? "FLOOR: NODE 1 CANNOT FALL" : "";

    public string FortressState => _view.FortressUnlocked ? "UNLOCKED" : "LOCKED";
    public string HqState => _view.HqMandatory ? "MUST BE HELD" : _view.Fronts.Any(f => f == 0) ? "BROKEN THROUGH" : "SECURE";

    public string Header => _view.Phase switch
    {
        WarfrontPhase.Won => "THE WAR IS WON",
        WarfrontPhase.Lost => "THE WAR IS LOST",
        WarfrontPhase.Travel => "TRAVELLING",
        _ => $"THUNDERHAWK    DEPLOYMENT {_view.Deployments + 1}",
    };

    public string LeaderName => _view.Leader is { } l ? NameOf(l, IndexOf(l.Key)) : "NOBODY";

    /// <summary>The line under the rows: the viewer's role, and the reason when only one choice is open.</summary>
    public string Note
    {
        get
        {
            if (IsOver) return _view.Phase == WarfrontPhase.Won ? "THE ORK WARBOSS IS DEAD" : "THE IMPERIAL HQ HAS FALLEN";
            if (!InShip) return "";
            if (_view.Leader is null) return "NO LEADER IS CONNECTED";
            if (!_view.ViewerIsLeader) return $"{LeaderName} IS CHOOSING THE DEPLOYMENT";
            return _view.HqMandatory ? "HQ DEFENSE IS MANDATORY: A LANE BROKE THROUGH" : "YOU LEAD: CHOOSE THE DEPLOYMENT";
        }
    }

    public string PartyLine(int i)
    {
        if (i >= _view.Players.Count) return "";
        var p = _view.Players[i];
        var line = NameOf(p, i);
        if (_view.LeaderKey == p.Key) line += "    LEADER";
        if (p.Key == _view.ViewerKey) line += "    YOU";
        if (!p.Connected) line += "    AWAY";
        return line;
    }

    int IndexOf(ulong key)
    {
        for (int i = 0; i < _view.Players.Count; i++) if (_view.Players[i].Key == key) return i;
        return 0;
    }

    static string NameOf(HubPlayer p, int index) => (string.IsNullOrWhiteSpace(p.Name) ? "PLAYER " + (index + 1) : p.Name).ToUpperInvariant();

    // ---- travel screen ----------------------------------------------------------------------------------------------------------------------

    public string TravelDestination
    {
        get
        {
            if (_view.CurrentTarget is not { } t) return "TO THE NEXT BATTLEFIELD";
            return t.Kind switch
            {
                WarfrontTargetKind.Hq => "TO THE IMPERIAL HQ",
                WarfrontTargetKind.Fortress => "TO THE ORK FORTRESS, THROUGH " + LaneName(t.Lane),
                _ => $"TO {LaneName(t.Lane)}, {NodeText(_view.Fronts[t.Lane])}",
            };
        }
    }

    public string TravelStatus => TravelTotal > 0 ? $"LOADING THE BATTLEFIELD    {TravelJoined} OF {TravelTotal} PLAYERS ARRIVED" : "LOADING THE BATTLEFIELD";
}
