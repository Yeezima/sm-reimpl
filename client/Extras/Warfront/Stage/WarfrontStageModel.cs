using System;
using System.Collections.Generic;
using System.Linq;
using SmClient.Extras.Warfront.Hub;
using SmNet.Extras.Warfront;
using SmNet.Extras.Warfront.Results;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Progression;
using SmSim.Extras.Warfront.Results;

namespace SmClient.Extras.Warfront.Stage;

/// <summary>
/// The client's Warfront stage flow without Godot or sockets (slice W5b; plan sections 3, 4 and 8): what the server's sections (<see cref="WarfrontWire"/>) do to the hub model, the travel screen, the
/// results record and the down state, and what the client sends back (Identify until the Hub shows the server knows the key, Deploy until the Hub or the Stage shows it was taken, NewRun until the
/// run is not over). The networked view feeds it bytes and the clock and acts on its answers; every decoder drops a malformed payload whole.
/// </summary>
public sealed class WarfrontStageModel
{
    /// <summary>Seconds between resends of a command the server has not acknowledged (the transport is unreliable).</summary>
    public const double ResendSeconds = 1.0;
    /// <summary>A command is sent at most this often in all (a refusal or a lost server must not make a stream of it).</summary>
    public const int MaxSends = 10;
    /// <summary>A Preparing section that stops coming for this long means the server is gone: the travel screen goes.</summary>
    public const double PreparingStaleSeconds = 8.0;
    /// <summary>An Arrived section that stops coming for this long means the landing happened (the server stops sending it).</summary>
    public const double ArrivedStaleSeconds = 4.0;
    /// <summary>After Go the client waits this long before it rejoins: the old process keeps answering for 2 s and a join then would reach it.</summary>
    public const double RejoinDelaySeconds = 3.0;

    public WarfrontHubModel Hub { get; } = new();
    public ulong Key { get; }
    /// <summary>The display name Identify carries (read again at send time: it may not be known when the model is made).</summary>
    public string Name { get; set; }
    /// <summary>Why this client's key is a throwaway for the session (the key file could not be read or written), shown on the hub note.</summary>
    public string KeyNote { get; set; } = "";

    public WarfrontStageModel(ulong key, string name) { Key = key; Name = name; }

    // ---- Hub -------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The first Hub section whose viewer handle is this client's key arrived: the server knows who this is.</summary>
    public bool Identified { get; private set; }
    public int HubSections { get; private set; }

    public bool ApplyHub(ReadOnlySpan<byte> payload)
    {
        if (!WarfrontWire.TryDecode(payload, out WarfrontHubWire wire)) return false;
        var view = ToView(wire);
        HubSections++;
        if (view.ViewerKey == Key) Identified = true;
        Hub.Apply(view);
        UpdateTravelFromHub(view);
        if (_deploy is not null && (view.Phase is WarfrontPhase.Travel or WarfrontPhase.Battle || view.CurrentTarget is not null)) ClearDeploy("the Hub shows it was taken");
        if (_newRun && view.Phase is not (WarfrontPhase.Won or WarfrontPhase.Lost)) _newRun = false;
        return true;
    }

    /// <summary>The wire's Hub as the hub model's view (the wire's fields are that type's fields; <see cref="WarfrontHubView.Last"/> is the wire's byte as <see cref="HubLastKind"/>).</summary>
    public static WarfrontHubView ToView(WarfrontHubWire w) => new()
    {
        Phase = w.Phase, Fronts = w.Fronts.ToArray(), Advanced = w.Advanced.ToArray(), HqMandatory = w.HqMandatory, Deployments = w.Deployments,
        Targets = w.Targets.ToArray(), CurrentTarget = w.CurrentTarget, ViewerKey = w.ViewerKey, LeaderKey = w.LeaderKey,
        Players = w.Players.Select(p => new HubPlayer(p.Key, p.Name, p.Connected)).ToArray(),
        Last = w.Last.Select(b => (HubLastKind)b).ToArray(), Forecast = w.Forecast?.ToArray(),
    };

    /// <summary>The inverse of <see cref="ToView"/> (the model tests prove the bytes round-trip with the server's encoder).</summary>
    public static WarfrontHubWire ToWire(WarfrontHubView v) => new(
        v.Phase, v.Fronts.ToArray(), v.Advanced.ToArray(), v.HqMandatory, v.Deployments, v.Targets.ToArray(), v.CurrentTarget, v.ViewerKey, v.LeaderKey,
        v.Players.Select(p => new WarfrontHubWirePlayer(p.Key, p.Name, p.Connected)).ToArray(), v.Last.Select(k => (byte)k).ToArray(), v.Forecast?.ToArray());

    // ---- identify, deploy, new run: the commands ------------------------------------------------------------------------------------------

    int _identifySends;
    double _identifyFirst;
    double _identifyAt = double.NegativeInfinity;

    /// <summary>The Identify to send now (null when the server knows the key, or one went out less than <see cref="ResendSeconds"/> ago). Right after a join, and again until the Hub names this client.</summary>
    public byte[]? IdentifyDue(double now)
    {
        // no cap: a human is held aboard until the server has the key, and one small datagram a second costs nothing
        if (Identified || now - _identifyAt < ResendSeconds) return null;
        if (_identifySends == 0) _identifyFirst = now;
        _identifyAt = now; _identifySends++;
        return WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Identify, Key: Key, Name: Name));
    }

    // ---- progress: this PC's progression to the host ------------------------------------------------------------------------------------

    /// <summary>A progress record is sent at most this many times (a server of an older build never acknowledges it).</summary>
    public const int MaxProgressSends = 30;

    WarfrontProgressFile? _progress;
    bool _progressNewer;
    int _progressSends;
    byte _progressTransfer;
    double _progressAt = double.NegativeInfinity;

    /// <summary>The first ProgressSave (tag 49) arrived: the host has this client's progress and the Progress command stops. With a reset pending, only a ProgressSave that shows level 1 counts: the host has heard the reset.</summary>
    public bool ProgressAcked { get; private set; }

    /// <summary>This PC's progress file as read at connect (null: no file, so flag 1 absent goes out). <paramref name="newer"/>: the file is of a newer build and nothing is sent, so the host keeps its copy and the file is never touched.</summary>
    public void SetProgress(WarfrontProgressFile? own, bool newer = false) { _progress = own; _progressNewer = newer; _progressSends = 0; _progressAt = double.NegativeInfinity; ProgressAcked = false; }

    /// <summary>The record the Progress command carries: flag 1 (absent) with no file, flag 2 (reset) for a fresh level-1 file with a resetAt, else the file's progression. Null for a newer file.</summary>
    public byte[]? ProgressRecord()
    {
        if (_progressNewer) return null;
        if (_progress is null) return WarfrontProgressSaveWire.Encode(null, WarfrontProgressSaveWire.FlagAbsent);
        return WarfrontProgressSaveWire.Encode(_progress, WarfrontProgressStore.IsPendingReset(_progress) ? WarfrontProgressSaveWire.FlagReset : (byte)0);
    }

    /// <summary>
    /// The Progress command's chunks to send now (null: nothing due). Sent after the first Identify, resent every <see cref="ResendSeconds"/> until the first ProgressSave arrives (at most <see cref="MaxProgressSends"/> times),
    /// and never for a newer file.
    /// </summary>
    public IReadOnlyList<byte[]>? ProgressDue(double now)
    {
        bool resetPending = WarfrontProgressStore.IsPendingReset(_progress);   // the reset is repeated until the host shows level 1 (no send cap: the player's reset must reach it)
        if (ProgressAcked || _progressNewer || _identifySends == 0 || (!resetPending && _progressSends >= MaxProgressSends) || now - _progressAt < ResendSeconds) return null;
        if (ProgressRecord() is not { } record) return null;
        if (_progressSends == 0) _progressTransfer++;
        _progressAt = now; _progressSends++;
        return WarfrontProgressSaveWire.CommandChunks(record, _progressTransfer);
    }

    /// <summary>A ProgressSave section (tag 49): the first one is the acknowledgement. Null when it does not decode.</summary>
    public WarfrontProgressRecord? ApplyProgressSave(ReadOnlySpan<byte> payload)
    {
        if (!WarfrontProgressSaveWire.TryDecode(payload, out var record)) return null;
        bool showsReset = record.State is { } st && WarfrontProgressStore.IsFresh(WarfrontProgressFile.Sanitize(st));
        if (!WarfrontProgressStore.IsPendingReset(_progress) || showsReset) ProgressAcked = true;
        return record;
    }

    WarfrontTarget? _deploy;
    int _deploySends;
    double _deployAt = double.NegativeInfinity;

    public WarfrontTarget? PendingDeploy => _deploy;
    /// <summary>The last refusal the server's Stage None section carried, shown on the war map's note.</summary>
    public string Refusal { get; private set; } = "";

    /// <summary>The leader pressed a deployment row: it is sent now and resent until the Hub or the Stage shows it was taken.</summary>
    public void RequestDeploy(WarfrontTarget target)
    {
        _deployGaveUp = false;
        _deploy = target; _deploySends = 0; _deployAt = double.NegativeInfinity; Refusal = "";
    }

    void ClearDeploy(string why) { _deploy = null; LastDeployNote = why; }
    bool _deployGaveUp;
    public string LastDeployNote { get; private set; } = "";

    public byte[]? DeployDue(double now)
    {
        if (_deploy is not { } t) return null;
        if (_deploySends >= MaxSends) { ClearDeploy("gave up after " + MaxSends + " sends"); _deployGaveUp = true; return null; }
        if (now - _deployAt < ResendSeconds) return null;
        _deployAt = now; _deploySends++;
        return WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Deploy, Target: t));
    }

    bool _newRun;
    int _newRunSends;
    double _newRunAt = double.NegativeInfinity;

    public void RequestNewRun() { if (Hub.IsOver && Hub.View.ViewerIsLeader) { _newRun = true; _newRunSends = 0; _newRunAt = double.NegativeInfinity; } }
    public bool NewRunPending => _newRun;

    public byte[]? NewRunDue(double now)
    {
        if (!_newRun) return null;
        if (_newRunSends >= MaxSends) { _newRun = false; return null; }
        if (now - _newRunAt < ResendSeconds) return null;
        _newRunAt = now; _newRunSends++;
        return WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.NewRun));
    }

    /// <summary>Seconds after the first Identify before the screens say the server does not know this client yet.</summary>
    public const double IdentifyNoteSeconds = 5.0;

    /// <summary>The one line the hub's note (and the travel screen's status) shows instead of its own when something about this client needs saying: a throwaway key, a server that has not answered Identify, a refused or unanswered deploy. Empty when all is well.</summary>
    public string NoteOverride(double now)
    {
        if (KeyNote.Length > 0) return KeyNote;
        if (!Identified && _identifySends > 0 && now - _identifyFirst >= IdentifyNoteSeconds) return "WAITING FOR THE SERVER TO KNOW YOU";
        if (Refusal.Length > 0) return Refusal.ToUpperInvariant();
        if (_deployGaveUp) return "THE DEPLOYMENT WAS NOT ANSWERED";
        return "";
    }

    // ---- Stage and the travel screen ------------------------------------------------------------------------------------------------------

    public WarfrontStageWire? Stage { get; private set; }
    double _stageAt = double.NegativeInfinity, _goAt = double.NegativeInfinity;
    bool _goSeen;

    /// <summary>The travel screen is up.</summary>
    public bool TravelVisible { get; private set; }
    /// <summary>The client should leave this connection and join the same address again (set <see cref="RejoinDelaySeconds"/> after the Go).</summary>
    public bool RejoinDue { get; private set; }
    /// <summary>Preparing, Go or Arrived has been seen on this connection.</summary>
    public WarfrontStageState Last { get; private set; }

    public bool ApplyStage(ReadOnlySpan<byte> payload, double now)
    {
        if (!WarfrontWire.TryDecode(payload, out WarfrontStageWire stage)) return false;
        Stage = stage;
        _stageAt = now;
        switch (stage.State)
        {
            case WarfrontStageState.None:
                if (stage.Status.Length > 0) { Refusal = stage.Status; ClearDeploy("refused: " + stage.Status); }
                break;
            case WarfrontStageState.Preparing:
                Last = WarfrontStageState.Preparing; TravelVisible = true; ClearDeploy("taken: preparing");
                break;
            case WarfrontStageState.Go:
                Last = WarfrontStageState.Go; TravelVisible = true; ClearDeploy("taken: go");
                if (!_goSeen) { _goSeen = true; _goAt = now; }
                break;
            case WarfrontStageState.Arrived:
                Last = WarfrontStageState.Arrived; ClearDeploy("taken: arrived");
                // the server repeats Arrived for a moment after the landing: once the Hub says the battle began (or the ship is back) it is not shown again
                TravelVisible = HubSections == 0 || Hub.View.Phase == WarfrontPhase.Travel;
                Hub.SetTravelProgress(stage.Arrived, Math.Max(stage.Expected, 1));
                break;
        }
        return true;
    }

    void UpdateTravelFromHub(WarfrontHubView view)
    {
        // an Arrived stage that the Hub has moved past is history (the server stops sending it): the model does not keep showing it
        if (Stage is { State: WarfrontStageState.Arrived } && view.Phase is WarfrontPhase.Battle or WarfrontPhase.Ship or WarfrontPhase.Won or WarfrontPhase.Lost) Stage = null;
        // The landing: the battle began (or the ship is back after it). The Go screen stays: the rejoin is coming.
        if (TravelVisible && Last == WarfrontStageState.Arrived && view.Phase is WarfrontPhase.Battle or WarfrontPhase.Ship or WarfrontPhase.Won or WarfrontPhase.Lost) TravelVisible = false;
    }

    /// <summary>The clock: a stale Preparing or Arrived section ends the travel screen, and the rejoin falls due after the Go.</summary>
    public void Tick(double now)
    {
        if (_goSeen && !RejoinDue && now - _goAt >= RejoinDelaySeconds) RejoinDue = true;
        if (!TravelVisible || _goSeen) return;
        double age = now - _stageAt;
        if (Last == WarfrontStageState.Preparing && age > PreparingStaleSeconds) TravelVisible = false;
        if (Last == WarfrontStageState.Arrived && age > ArrivedStaleSeconds) TravelVisible = false;
    }

    /// <summary>"BUILDING THE BATTLEFIELD 42 PERCENT" and the server's own status line; after the Go the rejoin; on the new ship the players arrived.</summary>
    public string TravelStatusText
    {
        get
        {
            if (Stage is not { } s) return Hub.TravelStatus;
            switch (s.State)
            {
                case WarfrontStageState.Preparing:
                    return $"BUILDING THE BATTLEFIELD    {s.Percent} PERCENT" + (s.Status.Length > 0 ? "    " + s.Status.ToUpperInvariant() : "");
                case WarfrontStageState.Go:
                    return "THE BATTLEFIELD IS READY    JOINING THE THUNDERHAWK";
                case WarfrontStageState.Arrived:
                    return $"WAITING FOR PLAYERS    {s.Arrived} OF {Math.Max(s.Expected, 1)} PLAYERS ARRIVED";
                default: return Hub.TravelStatus;
            }
        }
    }

    /// <summary>Where the Thunderhawk goes, from the Stage's target (the hub's own text waits for the saved Travel phase, which comes only when the build is done).</summary>
    public string TravelDestinationText
    {
        get
        {
            if (Stage is not { State: not WarfrontStageState.None } s) return Hub.TravelDestination;
            var fronts = Hub.View.Fronts;
            return s.Kind switch
            {
                WarfrontTargetKind.Hq => "TO THE IMPERIAL HQ",
                WarfrontTargetKind.Fortress when s.Lane >= 0 => "TO THE ORK FORTRESS, THROUGH " + WarfrontHubModel.LaneName(s.Lane),
                _ when s.Lane is >= 0 and < 3 && fronts[s.Lane] is >= 1 and <= 3 && Hub.View.Phase != WarfrontPhase.Travel => $"TO {WarfrontHubModel.LaneName(s.Lane)}, {WarfrontHubModel.NodeText(fronts[s.Lane])}",
                _ => Hub.TravelDestination,
            };
        }
    }

    // ---- Results ---------------------------------------------------------------------------------------------------------------------------

    readonly WarfrontResultsWire.Reassembler _results = new();
    byte[]? _lastRecord;

    /// <summary>The record that was last completed (opened once; the server repeats the transfer every few seconds).</summary>
    public WarfrontRunRecord? Record { get; private set; }

    /// <summary>One Results chunk. Returns the record when this chunk completed a record that is new (not the one already opened); a malformed or undecodable one is dropped whole.</summary>
    public WarfrontRunRecord? ApplyResults(ReadOnlySpan<byte> chunk)
    {
        var whole = _results.Add(chunk);
        if (whole is null) return null;
        if (_lastRecord is not null && _lastRecord.AsSpan().SequenceEqual(whole)) return null;
        WarfrontRunRecord record;
        try { record = WarfrontResultsWire.Decode(whole); }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AccessViolationException)) { return null; }   // untrusted bytes: any decode failure drops the record whole
        _lastRecord = whole;
        Record = record;
        return record;
    }

    // ---- Down ------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The viewer's fallen state (W12's spectator camera reads it; here it only shows the seconds).</summary>
    public WarfrontDownWire Down { get; private set; } = new(WarfrontDownPhase.None, 0f);
    double _downAt = double.NegativeInfinity;

    public bool ApplyDown(ReadOnlySpan<byte> payload, double now)
    {
        if (!WarfrontWire.TryDecode(payload, out WarfrontDownWire d)) return false;
        Down = d; _downAt = now;
        return true;
    }

    /// <summary>The seconds line of the down state ("DOWN 12 S", "SPECTATING 30 S"), empty when not down (or the sections stopped for 2 s: the server sends one last None).</summary>
    public string DownLine(double now)
    {
        if (Down.Phase == WarfrontDownPhase.None || now - _downAt > 2.0) return "";
        int s = (int)MathF.Ceiling(Down.SecondsLeft);
        return Down.Phase == WarfrontDownPhase.Downed ? $"DOWN    {s} S" : $"SPECTATING    {s} S";
    }
}
