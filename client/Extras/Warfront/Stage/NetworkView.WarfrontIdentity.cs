using System;
using Godot;
using SmClient.Extras.Warfront.Stage;
using SmNet;

namespace SmClient;

/// <summary>
/// Who this client is on a Warfront server (slice W5b; plan section 3): the campaign key (<see cref="WarfrontClientKey"/>) and the display name go out in Identify right after the join, are resent every second
/// until the Hub names this client (the Hub's viewer handle is the key itself), and go out again after every rejoin (a new view reads the same key file). Test clients name their key file with
/// <c>--warfront-key-file &lt;path&gt;</c>, so several run on one PC.
/// </summary>
public partial class NetworkView
{
    ulong WarfrontIdentityKey()
    {
        string? file = CmdArg("warfront-key-file");
        if (file is { Length: > 0 } && !file.StartsWith("--")) return WarfrontClientKey.LoadOrCreate(file);
        if (_bot) GD.Print("warfront: a bot without --warfront-key-file shares this PC's key file with every other such client (the server keeps one seat per key)");
        return WarfrontClientKey.LoadOrCreate(WarfrontClientKey.DefaultPath());
    }

    WarfrontStageModel MakeWarfrontModel()
    {
        ulong key = WarfrontIdentityKey();
        var model = new WarfrontStageModel(key, WarfrontDisplayName());
        // this PC's progression (codebase fix 1): read once per connection; the Progress command sends it after Identify, tag 49 may rewrite the file
        _wfStore = new WarfrontProgressStore(WarfrontProgressStore.PathFor(CmdArg("warfront-key-file")), key) { Log = GD.Print };
        _wfStore.Load();
        model.SetProgress(_wfStore.Current, _wfStore.Newer);
        if (WarfrontClientKey.LastProblem.Length > 0) { model.KeyNote = WarfrontClientKey.LastProblem; GD.Print("warfront: " + WarfrontClientKey.LastProblem); }
        return model;
    }

    WarfrontProgressStore? _wfStore;

    /// <summary>ProgressSave (tag 49) from the host: its copy becomes this PC's file only when it is at least as good as the file's own (never lowers it, never touches a newer file). Main thread; one small file write, and only on a change.</summary>
    void WarfrontProgressReceive(ReadOnlySpan<byte> payload)
    {
        if (WarfrontModel.ApplyProgressSave(payload) is not { } record) { WarfrontDropped++; return; }
        if (record.Absent || record.State is null || _wfStore is null) return;
        if (_wfStore.ApplyHostCopy(record.State)) GD.Print($"t {_connectedFor:F1} s: warfront: progress file updated (level {_wfStore.Current?.Level}, {_wfStore.Current?.Xp} xp)");
    }

    string WarfrontDisplayName()
    {
        string? name = _client is { PlayerId: >= 0 } c ? c.NameOf(c.PlayerId) : null;
        if (string.IsNullOrWhiteSpace(name)) name = _bot ? "BOT" : "PLAYER";
        return name!.Length > SmNet.Extras.Warfront.WarfrontWire.MaxName ? name[..SmNet.Extras.Warfront.WarfrontWire.MaxName] : name;
    }

    int _wfIdentifyLogged, _wfProgressLogged;
    double _wfHeldSince = -1;
    bool _wfFirstConnectedLogged;

    /// <summary>Once a frame: Identify while the server does not know the key, the leader's Deploy and a NewRun while they are pending.</summary>
    void WarfrontSendCommands(double now)
    {
        if (_client is not { Status: ClientStatus.Connected } client) return;
        var m = WarfrontModel;
        m.Name = WarfrontDisplayName();   // read at send time: the name may not have been known when the model was made
        if (!_wfFirstConnectedLogged)
        {
            _wfFirstConnectedLogged = true;
            long sinceMs = _warfrontLastStepUs == 0 ? -1 : ((long)Time.GetTicksUsec() - _warfrontLastStepUs) / 1000;
            GD.Print($"t {now:F1} s: warfront: first connected frame: {(WarfrontBandBusy ? "the band's drawing steps are still running (the Identify is held)" : sinceMs >= 0 ? $"the band's last drawing step ended {sinceMs} ms ago" : "no band was drawn on this view")}");
        }
        // W5d second pass: no Identify while the band's drawing steps are still queued, so the server cannot land a player who still sees the drawing happen; the "DRAWING THE BATTLEFIELD" screen stands meanwhile (WarfrontSyncPanels)
        if (WarfrontBandBusy)
        {
            if (_wfHeldSince < 0) { _wfHeldSince = now; GD.Print($"t {now:F1} s: warfront: identify held until the band's drawing steps are done"); }
        }
        else if (_wfHeldSince >= 0)
        {
            GD.Print($"t {now:F1} s: warfront: the band's drawing steps are done; identify released after {now - _wfHeldSince:F2} s held");
            _wfHeldSince = -1;
        }
        if (!WarfrontBandBusy && m.IdentifyDue(now) is { } identify)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, identify);
            if (_wfIdentifyLogged++ == 0) GD.Print($"t {now:F1} s: warfront: identify sent (key {WarfrontClientKey.Tail(m.Key)}, name {m.Name})");
        }
        if (m.ProgressDue(now) is { } chunks)
        {
            foreach (var chunk in chunks) client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, chunk);
            if (_wfProgressLogged++ == 0) GD.Print($"t {now:F1} s: warfront: progress sent ({chunks.Count} chunk(s), {(m.ProgressRecord()?.Length ?? 0)} bytes)");
        }
        if (m.DeployDue(now) is { } deploy)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, deploy);
            GD.Print($"t {now:F1} s: warfront: deploy to {m.PendingDeploy?.Kind} lane {m.PendingDeploy?.Lane} sent");
        }
        if (m.NewRunDue(now) is { } newRun)
        {
            client.SendExtrasCommand(SmNet.Extras.Warfront.WarfrontWire.CommandTag, newRun);
            GD.Print($"t {now:F1} s: warfront: new run requested");
        }
    }
}
