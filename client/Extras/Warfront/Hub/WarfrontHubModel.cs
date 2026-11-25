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
        if (row == 4) return null;
        if (row == 3) return WarfrontTarget.Hq;
        int f = _view.Fronts[row];
        return f is >= 1 and <= 3 ? WarfrontTarget.Node(row) : f == 4 ? WarfrontTarget.Fortress(row) : null;
    }

    // ---- text -------------------------------------------------------------------------------------------------------------------------------

    public static string LaneName(int lane) => "LANE " + (char)('A' + lane);

    /// <summary>The node a lane fights at, "NODE 2, OUTSKIRTS".</summary>
    public static string NodeText(int front) => $"NODE {front}, {NodeNames[Math.Clamp(front, 1, 3) - 1]}";

    /// <summary>Rows 0 to 2 the lanes, 3 the HQ defense, 4 the locked fortress.</summary>
    public const int Rows = 5;

    /// <summary>The campaign offers this row's target (to anyone, whoever leads).</summary>
    public bool RowOpen(int row) => RowTarget(row) is { } t && InShip && _view.Targets.Contains(t);

    /// <summary>Rows 0 to 3 stand while the war goes on; the fortress row (4) only while the fortress is locked.</summary>
    public bool RowVisible(int row) => !IsOver && (row < 4 || !_view.FortressUnlocked);

    /// <summary>Chaos Warfront C3: the run fights Chaos (the Hub's faction byte), so the labels name the Chaos stronghold, its Lord and the Chaos-held nodes (design "Faction": the labels).</summary>
    public bool Chaos => _view.Faction == SmSim.Extras.Warfront.Faction.WarfrontFaction.Chaos;
    /// <summary>The enemy's last stand: "ORK FORTRESS", or "CHAOS STRONGHOLD" in a Chaos run.</summary>
    public string FortressName => Chaos ? "CHAOS STRONGHOLD" : "ORK FORTRESS";
    /// <summary>The enemy commander's box on the war map: "WARBOSS", or "CHAOS LORD".</summary>
    public string CommanderName => Chaos ? "CHAOS LORD" : "WARBOSS";

    public string DeployRowText(int row)
    {
        if (row == 4) return $"{FortressName}: LOCKED";
        if (!RowOpen(row)) return row == 3 ? "IMPERIAL HQ DEFENSE: NOT AVAILABLE" : $"{LaneName(row)}: NOT AVAILABLE";
        if (row == 3) return "DEPLOY: IMPERIAL HQ DEFENSE";
        int f = _view.Fronts[row];
        return f == 4 ? $"DEPLOY: {FortressName}, THROUGH {LaneName(row)}" : $"DEPLOY: {LaneName(row)}, {NodeText(f)}";
    }

    /// <summary>The text in lane <paramref name="lane"/>'s node box <paramref name="node"/> (1 to 3): its name and its state.</summary>
    public string NodeLabel(int lane, int node) => $"{node} {NodeNames[node - 1]}\n{NodeStateText(lane, node)}{WeatherTag(lane, node)}";

    /// <summary>Weather W1: the forecast's short name after the node's state (" - RAIN"), on every node whatever its state, or nothing when the server sent no forecast. The default look reads "STORM" (the Ashfall storm).</summary>
    public string WeatherTag(int lane, int node) => _view.ForecastOf(lane, node) is { } id && SmSim.Extras.Weather.WeatherCatalog.Of(id) is { } def ? " - " + def.Short : "";

    /// <summary>The forecast's full name for a node ("Low fog"), or "".</summary>
    public string WeatherName(int lane, int node) => _view.ForecastOf(lane, node) is { } id && SmSim.Extras.Weather.WeatherCatalog.Of(id) is { } def ? def.Name : "";

    public string NodeStateText(int lane, int node)
    {
        int f = _view.Fronts[lane];
        return node < f ? "CLEARED" : node == f ? "FRONT" : Chaos ? "CHAOS-HELD" : "ORK-HELD";
    }

    public string LaneStatus(int lane) => _view.Fronts[lane] switch
    {
        0 => "BROKEN THROUGH TO THE HQ",
        4 => "CLEARED, THE FORTRESS IS OPEN",
        var f => $"FIGHTING AT {NodeText(f)}",
    };

    public string LastText(int lane) => IsOver ? "" : _view.Last[lane] switch
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
    public string FloorText(int lane) => !IsOver && _view.Fronts[lane] == 1 && !_view.Advanced[lane] ? "FLOOR: NODE 1 CANNOT FALL" : "";

    public string FortressState => _view.Phase == WarfrontPhase.Won ? (Chaos ? "CHAOS LORD SLAIN" : "WARBOSS SLAIN") : _view.FortressUnlocked ? "UNLOCKED" : "LOCKED";
    /// <summary>Chaos Warfront C5c (lead 40): the campaign was lost at the fortress's duel relay (FinaleLost), not at the HQ: lost with no lane broken through (the HQ falls only through a broken lane's mandatory defence).</summary>
    public bool LostAtFinale => _view.Phase == WarfrontPhase.Lost && !_view.Fronts.Any(f => f == 0);

    public string HqState => _view.Phase == WarfrontPhase.Lost ? (LostAtFinale ? "STANDING" : "FALLEN") : _view.Phase == WarfrontPhase.Won ? "HELD" : _view.HqMandatory ? "MUST BE HELD" : _view.Fronts.Any(f => f == 0) ? "BROKEN THROUGH" : "SECURE";

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
            if (IsOver) return _view.Phase == WarfrontPhase.Won ? (Chaos ? "THE CHAOS LORD IS DEAD" : "THE ORK WARBOSS IS DEAD") : LostAtFinale ? (Chaos ? "THE CHAOS LORD STANDS: NO CHALLENGER LEFT" : "THE WARBOSS STANDS: NO CHALLENGER LEFT") : "THE IMPERIAL HQ HAS FALLEN";
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
                WarfrontTargetKind.Fortress => $"TO THE {FortressName}, THROUGH " + LaneName(t.Lane),
                _ => $"TO {LaneName(t.Lane)}, {NodeText(_view.Fronts[t.Lane])}",
            };
        }
    }

    public string TravelStatus => TravelTotal > 0 ? $"WAITING FOR PLAYERS    {TravelJoined} OF {TravelTotal} PLAYERS ARRIVED" : "BUILDING THE BATTLEFIELD, FIRST VISIT CAN TAKE A FEW MINUTES";

    /// <summary>"ELAPSED 1:05" (the first visit to a battlefield can take 100 to 180 seconds).</summary>
    public static string ElapsedText(double seconds) => $"ELAPSED {(int)seconds / 60}:{(int)seconds % 60:00}";
}
