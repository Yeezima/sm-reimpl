using System;
using Godot;
using SmClient.Customizer;
using SmClient.Extras.Warfront.Hub;
using SmClient.Extras.Warfront.Stage;
using SmNet;
using SmSim;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmSim.Extras.Warfront.Results;

namespace SmClient;

/// <summary>
/// Warfront's stage flow on the client (slice W5b; plan sections 3, 4 and 8). The hooks of the dispatchers land here: <see cref="WarfrontFrame"/> once a frame, <see cref="WarfrontOpenWarMap"/> at the
/// Thunderhawk's destination station (the Warfront war map, <see cref="WarfrontHubPanel"/>), <see cref="WarfrontIsGeneratedMap"/> for the lane slots' names. The panels stand where the picker stands
/// (<c>_pickerLayer</c>, forced), so the view's rules for it hold unchanged: the mouse is free, the game takes no input. The travel screen (<see cref="WarfrontTravelPanel"/>) goes up with the server's
/// Preparing, stays through the Go and the rejoin (the same address, the patient connect of a map rotation: <see cref="RotateRequested"/>), and goes at the landing; the results panel opens when the
/// record has arrived. Bots show no panel, only log lines.
/// </summary>
public partial class NetworkView
{
    partial void WarfrontIsGeneratedMap(string name, ref bool yes) => yes |= name.StartsWith(WarfrontSlots.Prefix, StringComparison.Ordinal);

    WarfrontHubPanel? _wfHubPanel;
    WarfrontTravelPanel? _wfTravel;
    WarfrontResultsPanel? _wfResults;
    Label? _wfDownLabel;
    CanvasLayer? _wfDownLayer;
    WarfrontRunRecord? _wfPendingRecord;
    bool _wfRejoinLogged;

    /// <summary>Once a frame on a Warfront server (the dispatcher's hook): commands, the clock, the panels, the rejoin, the fan-out for the later slices, the bot.</summary>
    partial void WarfrontFrame(PlayerState me, double dt)
    {
        if (!WarfrontOn || _client is not { Status: ClientStatus.Connected } client || client.PlayerId < 0) return;
        double now = _connectedFor;
        var m = WarfrontModel;
        m.Tick(now);
        WarfrontSendCommands(now);
        if (!_bot)
        {
            WarfrontSyncPanels(me, now);
            WarfrontDownFrame(now);
        }
        if (m.RejoinDue && !RotateRequested)
        {
            RotateRequested = true;
            _nextMapSeen = true;
            if (!_wfRejoinLogged) { _wfRejoinLogged = true; GD.Print($"t {now:F1} s: warfront: the server moves to {m.Stage?.Map}; joining the same address again"); }
        }
        WarfrontBotFrame(me, now);
        WarfrontDemoFrame(now);
        WarfrontProgressionFrame(me, dt);
        WarfrontSquadFrame(me, dt);
        WarfrontCoopFrame(me, dt);
    }

    /// <summary>The Thunderhawk's destination station: the Warfront war map instead of Musou's (the hook is called only from Musou's station code, which a Warfront server shares).</summary>
    partial void WarfrontOpenWarMap(ref bool handled)
    {
        if (!WarfrontOn) return;
        handled = true;
        if (_bot) return;
        // the run is over and the results panel was closed: the station brings it back
        if (WarfrontModel.Hub.IsOver && WarfrontModel.Record is { } record) { _wfPendingRecord = record; return; }
        WarfrontOpenHubPanel();
    }

    // ---- the layer the panels stand on -------------------------------------------------------------------------------------------------------

    CanvasLayer? _wfLayerNow;

    /// <summary>A full-screen panel on the picker's layer (the mouse free, the game without input); null when another picker or menu is up (unless <paramref name="takeOver"/>: the travel screen closes it).</summary>
    T? WarfrontOpenLayer<T>(string name, Func<T> make, bool takeOver = false) where T : Control
    {
        if (takeOver && _pickerLayer is not null && !ReferenceEquals(_pickerLayer, _wfLayerNow)) ClosePicker();
        if (PickerBusy) return null;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiKit.EnterVirtualSpace(GetWindow());
        _pickerForced = true;
        _pickerLayer = new CanvasLayer { Name = name, Layer = 60 };
        _wfLayerNow = _pickerLayer;
        var root = new Control { Name = name + "Root", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var panel = make();
        root.AddChild(panel);
        _pickerLayer.AddChild(root);
        AddChild(_pickerLayer);
        UiSounds.Current?.Attach(_pickerLayer);
        return panel;
    }

    void WarfrontCloseLayer()
    {
        if (_pickerLayer is null || !ReferenceEquals(_pickerLayer, _wfLayerNow)) { _wfLayerNow = null; return; }
        _wfLayerNow = null;
        _pickerLayer.QueueFree();
        _pickerLayer = null;
        _pickerForced = false;
        PadMenuClosed();
        UiKit.LeaveVirtualSpace(GetWindow());
        RestoreMouse(leaving: false);
    }

    /// <summary>The view closed the layer itself (Esc, the connection going): forget the panels that stood on it.</summary>
    void WarfrontLayerGone()
    {
        if (_wfLayerNow is not null && _pickerLayer is not null && ReferenceEquals(_pickerLayer, _wfLayerNow)) return;
        _wfLayerNow = null;
        _wfHubPanel = null; _wfTravel = null; _wfResults = null;
    }

    // ---- the war map --------------------------------------------------------------------------------------------------------------------------

    void WarfrontOpenHubPanel()
    {
        if (_wfHubPanel is not null || PickerBusy) return;
        var m = WarfrontModel;
        m.Hub.DeployRequested -= WarfrontDeployPressed;
        m.Hub.DeployRequested += WarfrontDeployPressed;
        _wfHubPanel = WarfrontOpenLayer("WarfrontMap", () => new WarfrontHubPanel(m.Hub, WarfrontCloseHubPanel));
        _wfHubPanel?.Focus();
        if (_wfHubPanel is not null) GD.Print($"t {_connectedFor:F1} s: warfront: the war map opened (leader {m.Hub.View.ViewerIsLeader}, {m.Hub.View.Targets.Count} targets)");
    }

    void WarfrontCloseHubPanel()
    {
        if (_wfHubPanel is null) return;
        _wfHubPanel = null;
        WarfrontCloseLayer();
        GD.Print($"t {_connectedFor:F1} s: warfront: the war map closed");
    }

    /// <summary>A DEPLOY row (the leader on the ship): the command goes out, resent until the Hub or the Stage shows it was taken, and the map closes. The server decides.</summary>
    void WarfrontDeployPressed(WarfrontTarget target)
    {
        WarfrontModel.RequestDeploy(target);
        WarfrontCloseHubPanel();
    }

    // ---- once a frame: the panels follow the model -------------------------------------------------------------------------------------------

    void WarfrontSyncPanels(PlayerState me, double now)
    {
        var m = WarfrontModel;
        WarfrontLayerGone();
        // the travel screen over everything
        if (m.TravelVisible)
        {
            if (_wfTravel is null)
            {
                _wfHubPanel = null; _wfResults = null;
                if (_wfLayerNow is not null) WarfrontCloseLayer();
                _wfTravel = WarfrontOpenLayer("WarfrontTravel", () => new WarfrontTravelPanel(m.Hub), takeOver: true);
                if (_wfTravel is not null) GD.Print($"t {now:F1} s: warfront: the travel screen opened ({m.Stage?.State})");
            }
            if (_wfTravel is not null)
            {
                string destination = m.TravelDestinationText, status = m.TravelStatusText;
                if (m.NoteOverride(now) is { Length: > 0 } travelNote) status += "    " + travelNote;
                if (_wfTravel.DestinationLabel.Text != destination) _wfTravel.DestinationLabel.Text = destination;
                if (_wfTravel.StatusLabel.Text != status) _wfTravel.StatusLabel.Text = status;
                if (m.Stage is { State: SmNet.Extras.Warfront.WarfrontStageState.Preparing } s && Math.Abs(_wfTravel.Elapsed - s.ElapsedSeconds) > 1.5) _wfTravel.SetElapsed(s.ElapsedSeconds);
            }
            return;
        }
        if (_wfTravel is not null)
        {
            _wfTravel = null;
            WarfrontCloseLayer();
            GD.Print($"t {now:F1} s: warfront: the travel screen closed at the landing");
        }
        // the war map's note line carries what needs saying about this client (a throwaway key, no answer to Identify, a refused or unanswered deploy)
        if (_wfHubPanel is not null && m.NoteOverride(now) is { Length: > 0 } note && _wfHubPanel.NoteLabel.Text != note) _wfHubPanel.NoteLabel.Text = note;
        // the war map closes when the ship is left or the player falls
        if (_wfHubPanel is not null && (m.Hub.View.Phase != WarfrontPhase.Ship || !me.Alive)) WarfrontCloseHubPanel();
        // the results, once the record is complete and nothing else is up
        if (_wfPendingRecord is { } record && _wfResults is null && !PickerBusy)
        {
            _wfPendingRecord = null;
            string dir = WarfrontClientKey.Folder();
            _wfResults = WarfrontOpenLayer("WarfrontResults", () => WarfrontBuildResults(record, dir));
            if (_wfResults is not null) { _wfResults.Focus(); GD.Print($"t {now:F1} s: warfront: the results panel opened ({record.Result})"); }
        }
        if (_wfResults is not null && !WarfrontResultsDemo && m.HubSections > 0 && m.Hub.View.Phase is not (WarfrontPhase.Won or WarfrontPhase.Lost)) { _wfResults = null; WarfrontCloseLayer(); }
    }

    WarfrontResultsPanel WarfrontBuildResults(WarfrontRunRecord record, string dir)
    {
        var panel = new WarfrontResultsPanel(record, dir, WarfrontCloseResults, () => { WarfrontModel.RequestNewRun(); GD.Print($"t {_connectedFor:F1} s: warfront: new run pressed"); WarfrontCloseResults(); });
        if (panel.NewRunButton is { } newRun) newRun.Visible = WarfrontModel.Hub.View.ViewerIsLeader;
        return panel;
    }

    void WarfrontCloseResults()
    {
        if (_wfResults is null) return;
        _wfResults = null;
        WarfrontCloseLayer();
        GD.Print($"t {_connectedFor:F1} s: warfront: the results panel closed");
    }

    /// <summary>The record is complete (the server repeats it every few seconds; the model passes each new one once).</summary>
    void WarfrontRecordArrived(WarfrontRunRecord record)
    {
        GD.Print($"t {_connectedFor:F1} s: warfront: results record received: {record.Result}, {record.Battles.Count} battles");
        if (!_bot) _wfPendingRecord = record;
    }

    // ---- the down state -----------------------------------------------------------------------------------------------------------------------

    /// <summary>The seconds of the viewer's down state on a line of their own, top centre (W12's spectator camera reads <see cref="WarfrontStageModel.Down"/>).</summary>
    void WarfrontDownFrame(double now)
    {
        string line = WarfrontModel.DownLine(now);
        if (line.Length == 0)
        {
            if (_wfDownLayer is not null) _wfDownLayer.Visible = false;
            return;
        }
        if (_wfDownLayer is null)
        {
            _wfDownLayer = new CanvasLayer { Name = "WarfrontDown", Layer = 40 };
            _wfDownLabel = new Label { Name = "WarfrontDownLine", HorizontalAlignment = HorizontalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore };
            _wfDownLabel.AddThemeFontSizeOverride("font_size", 28);
            _wfDownLabel.AddThemeColorOverride("font_color", UiKit.White);
            _wfDownLabel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.CenterTop);
            _wfDownLabel.Position = new Vector2(-300f, 120f);
            _wfDownLabel.Size = new Vector2(600f, 40f);
            _wfDownLayer.AddChild(_wfDownLabel);
            AddChild(_wfDownLayer);
        }
        _wfDownLayer.Visible = true;
        if (_wfDownLabel!.Text != line) _wfDownLabel.Text = line;
    }
}
