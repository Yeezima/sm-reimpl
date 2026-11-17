using System;
using System.IO;
using Godot;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront.Coop;

namespace SmClient;

/// <summary>
/// <c>--warfront-hooks-test</c> (slice W5c test aid, windowed human client, never in play): walks the five hooks once on a real server and prints <c>WFHOOK</c> lines (and stills to
/// <c>--warfront-hooks-shots &lt;dir&gt;</c>). Needs the server's <c>SM_WF_XP</c> so a card offer opens aboard the ship. Steps: the picker open and a weapon key held (the hand must not change), the picker taken
/// and the same key held (the hand changes), the squad panel, the held-M war map, the spectator camera on the own body and the camera's distance, the customizer opened and closed while awaiting a class (one ConfirmClass).
/// </summary>
public partial class NetworkView
{
    static readonly bool WarfrontHooksTest = CmdFlag("warfront-hooks-test");
    int _wfhStep;
    double _wfhAt, _wfhStarted = -1;
    string? _wfhHand;
    int _wfhConfirms;
    bool _wfhWired, _wfhHoldM;

    string WfhHand(PlayerState me) => Loadouts.InHand(_config, me)?.Name ?? "-";

    void WfhShot(string name)
    {
        if (CmdArg("warfront-hooks-shots") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"WFHOOK shot {name}: {err}");
    }

    void WfhKey(string action, bool down)
    {
        if (down) Input.ActionPress(action); else Input.ActionRelease(action);
    }

    void WfhNext(double now, int step) { _wfhStep = step; _wfhAt = now; }

    void WarfrontHooksTestFrame(PlayerState me, double dt)
    {
        if (!WarfrontHooksTest || _bot || _client is not { Status: ClientStatus.Connected } || !me.Alive) return;
        double now = _connectedFor;
        if (_wfhStarted < 0) { _wfhStarted = now; WfhNext(now, 0); }
        if (!_wfhWired) { _wfhWired = true; WarfrontCoop.ConfirmRequested += () => { _wfhConfirms++; GD.Print($"WFHOOK ConfirmClass requested ({_wfhConfirms})"); }; }
        double since = now - _wfhAt;
        bool pickerUp = _wfProgression is { Picker.Visible: true };
        switch (_wfhStep)
        {
            case 0:   // wait for the offer
                if (now - _wfhStarted > 40) { GD.Print("WFHOOK FAIL no card offer opened (is SM_WF_XP set on the server?)"); WfhNext(now, 99); }
                else if (pickerUp && since > 1.0)
                {
                    _wfhHand = WfhHand(me);
                    GD.Print($"WFHOOK picker open: PickerBusy={PickerBusy} hand={_wfhHand}");
                    WfhShot("1-picker-open");
                    WfhKey("sm_weapon3", true); WfhKey("sm_weapon2", true);
                    WfhNext(now, 1);
                }
                break;
            case 1:   // weapon keys held 1.2 s under the open picker
                if (since > 1.2)
                {
                    WfhKey("sm_weapon3", false); WfhKey("sm_weapon2", false);
                    string after = WfhHand(me);
                    GD.Print($"WFHOOK {(after == _wfhHand ? "PASS" : "FAIL")} weapon keys under the picker: hand {_wfhHand} -> {after}; picker still up={_wfProgression is { Picker.Visible: true }}");
                    if (_wfProgression is { } host) { int slot = Array.IndexOf(host.Model.Cards, SmSim.Extras.Warfront.Progression.WarfrontCard.AttackSpeed); host.Model.Pick(slot < 0 ? 0 : slot); GD.Print($"WFHOOK picked slot {Math.Max(slot, 0)} (attack speed card at {slot})"); }
                    WfhNext(now, 2);
                }
                break;
            case 2:   // picker closes after the server answers
                if (!pickerUp && since > 0.5 || since > 8)
                {
                    GD.Print($"WFHOOK picker closed: PickerBusy={PickerBusy} (expect False)");
                    _wfhHand = WfhHand(me);
                    WfhKey("sm_weapon1", true);
                    WfhNext(now, 3);
                }
                break;
            case 3:
                if (since > 1.2)
                {
                    WfhKey("sm_weapon1", false);
                    GD.Print($"WFHOOK carry: predicted AttackSpeedBonus={_client.Predicted.AttackSpeedBonus} model={_wfProgression?.Model.AttackSpeedBonus} ({(_client.Predicted.AttackSpeedBonus > 0f && Math.Abs(_client.Predicted.AttackSpeedBonus - (_wfProgression?.Model.AttackSpeedBonus ?? 0f)) < 0.001f ? "PASS" : "FAIL")})");
                    GD.Print($"WFHOOK weapon key with the picker closed: hand {_wfhHand} -> {WfhHand(me)} (informational: a change shows the gate lifts)");
                    WfhNext(now, 4);
                }
                break;
            case 4:   // the squad panel
                WarfrontSquadEnsure();
                if (_wfSquad is null) { if (since > 15) { GD.Print("WFHOOK squad view not ready (looks); skipping"); WfhNext(now, 6); } break; }
                _wfSquad.Panel.Open();
                WfhNext(now, 5);
                break;
            case 5:
                if (since > 0.6)
                {
                    GD.Print($"WFHOOK squad panel open: PickerBusy={PickerBusy} mouse={Input.MouseMode} (expect True, Visible)");
                    WfhShot("2-squad-panel");
                    _wfSquad!.Panel.Close();
                    WfhNext(now, 6);
                }
                break;
            case 6:   // held M
                _wfhHoldM = true;
                if (since > 0.8)
                {
                    GD.Print($"WFHOOK held M: peek layer visible={_wfPeekLayer?.Visible} ({(_wfPeekLayer?.Visible == true ? "PASS" : "FAIL")}); busy={PickerBusy} war={_tfwState.War is not null} bot={_bot}");
                    WfhShot("3-held-m-war-map");
                    _wfhHoldM = false;
                    WfhNext(now, 7);
                }
                break;
            case 7:   // spectator camera on the own body
                if (since < 0.1) _wfhHand = null;
                WarfrontCoop.Apply(new WarfrontCoopWire.Section(WarfrontCoopRole.SpectatingJoiner, WarfrontWatchKind.Player, (byte)_client.PlayerId, 0, 0, Array.Empty<WarfrontAwaited>()), "", "SELF", 1);
                if (since > 1.5 && _camera is not null && _gameSpace is not null)
                {
                    var body = SpectateG(me.Position);
                    float d = _camera.GlobalPosition.DistanceTo(body);
                    GD.Print($"WFHOOK spectate: spectating={WarfrontSpectating} camera {d:F1} m from the watched body ({(d > 4f && d < 10f ? "PASS" : "FAIL")}, the pose is about 6.9 m behind and above)");
                    WfhShot("4-spectate-follow");
                    WarfrontCoop.Apply(new WarfrontCoopWire.Section(WarfrontCoopRole.Player, WarfrontWatchKind.None, 0, 0, 0, Array.Empty<WarfrontAwaited>()), "", "", 1);
                    WfhNext(now, 8);
                }
                break;
            case 8:   // back to the own view
                if (since > 1.5)
                {
                    var body = SpectateG(me.Position);
                    GD.Print($"WFHOOK own view back: spectating={WarfrontSpectating}, camera {_camera?.GlobalPosition.DistanceTo(body):F1} m from the body");
                    WfhNext(now, 9);
                }
                break;
            case 9:   // the customizer awaited as a class: open, then close
                WarfrontCoop.Apply(new WarfrontCoopWire.Section(WarfrontCoopRole.AwaitClass, WarfrontWatchKind.None, 0, 0, 120, Array.Empty<WarfrontAwaited>()), "", "", 1);
                if (since < 0.3) { _wfhConfirms = 0; TfwOpenEquipment(); }
                else if (since > 6 && _pickerLayer is not null)
                {
                    GD.Print($"WFHOOK customizer open: PickerBusy={PickerBusy}");
                    WfhShot("5-customizer");
                    _pickerCreator?.WarfrontTestShowLibrarianSupport();
                    WfhNext(now, 11);
                }
                else if (since > 25) { GD.Print("WFHOOK FAIL the customizer did not open"); WfhNext(now, 10); }
                break;
            case 11:   // the Librarian's SUPPORT step with the squad-class rows
                if (since > 1.0)
                {
                    WfhShot("6-librarian-support-squad-rows");
                    GD.Print($"WFHOOK squad class stored after the Assault row: {_pickerCreator?.WarfrontTestPress(SmSim.Extras.Warfront.Squad.SquadClass.Assault)}");
                    WfhNext(now, 12);
                }
                break;
            case 12:
                if (since > 0.8)
                {
                    WfhShot("7-squad-class-chosen");
                    WarfrontCoop.Apply(new WarfrontCoopWire.Section(WarfrontCoopRole.AwaitClass, WarfrontWatchKind.None, 0, 0, 120, Array.Empty<WarfrontAwaited>()), "", "", 1);
                    WarfrontCoop.CustomizerOpened();   // the server's own Player section reset the opened mark in between; the aid sets it as the open did
                    ClosePicker();
                    WfhNext(now, 10);
                }
                break;
            case 10:
                WarfrontCoop.Apply(new WarfrontCoopWire.Section(WarfrontCoopRole.AwaitClass, WarfrontWatchKind.None, 0, 0, 120, Array.Empty<WarfrontAwaited>()), "", "", 1);
                if (since > 1.5)
                {
                    GD.Print($"WFHOOK {(_wfhConfirms == 1 ? "PASS" : "FAIL")} customizer closed in AwaitClass: ConfirmClass requests {_wfhConfirms} (expect 1)");
                    WfhNext(now, 99);
                }
                break;
            case 99:
                GD.Print("WFHOOK done");
                WfhNext(now, 100);
                break;
        }
    }
}
