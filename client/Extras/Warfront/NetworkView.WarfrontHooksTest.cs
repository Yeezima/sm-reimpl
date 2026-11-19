using System;
using System.IO;
using Godot;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Warfront;
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
    bool _wfhWired, _wfhHoldM, _wfhHoldF, _wfhPickWired;

    // ---- --warfront-down-watch: log (and stills) of a real fall on a real server: the Down state, the spectator view on the watched body, F stepping the watch, the own view back ----
    static readonly bool WarfrontDownWatch = CmdFlag("warfront-down-watch");
    double _wfdLogAt, _wfdSince = -1, _wfdFUntil;
    int _wfdStage;
    double _wfdWalkFrom = -1, _wfdForgedAt = -1, _wfdBattleForgedAt = -1;
    bool _wfdBannerShot1, _wfdBannerShot2;
    bool _wfdWasSpectating;

    /// <summary>Test aid: prints a <c>WFDOWN</c> line every 2 s (phase, spectating, watched body, camera distance), taps F once 6 s into a watch, and saves stills at the watch's start, after the step and on the return.</summary>
    void WfdDownWatchFrame(PlayerState me)
    {
        if (!WarfrontDownWatch || _bot || _client is not { Status: ClientStatus.Connected } c) return;
        double now = _connectedFor;
        bool spec = WarfrontSpectating;
        var s = WarfrontCoop.Section;
        Vector3? body = null;
        if (s.WatchKind == WarfrontWatchKind.Player) { foreach (var p in c.RemotePlayers().Players) if (p.PlayerId == s.WatchId) body = SpectateG(p.Position); if (s.WatchId == c.PlayerId) body = SpectateG(me.Position); }
        else if (s.WatchKind == WarfrontWatchKind.Marine && _wfSquad is { } squad && squad.Model.TrySample(s.WatchId, now, out var shown)) body = SpectateG(shown.Position);
        else body = SpectateG(me.Position);
        // --warfront-down-walk <seconds>: in a battle (not aboard) the player walks forward and holds the trigger for that long, to meet the Orks and fall (test aid).
        if (double.TryParse(CmdArg("warfront-down-walk"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double walk) && walk > 0 && me.Alive && !WarfrontSquadAboard && WarfrontModel.Hub.View.Phase == SmSim.Extras.Warfront.Campaign.WarfrontPhase.Battle)
        {
            if (_wfdWalkFrom < 0) _wfdWalkFrom = now + 6.0;
            bool go = now >= _wfdWalkFrom && now < _wfdWalkFrom + walk;
            WfhKey("sm_moveforward", go); WfhKey("sm_fire", go);
        }
        // --warfront-down-forge: a Down section (Spectator, 14 s left) is fed to the stage model from 10 s on, as the server's would be, for a client that cannot be made to fall (test aid): the path under test is the stage model -> WarfrontCoopDown -> the spectator view and its HUD, and back.
        if (CmdFlag("warfront-down-forge") && now >= 10.0 && now < 24.0 && now - _wfdForgedAt >= 0.5)
        {
            _wfdForgedAt = now;
            WarfrontModel.ApplyDown(WarfrontWire.Encode(new WarfrontDownWire(WarfrontDownPhase.Spectator, (float)(24.0 - now))), now);
        }
        // --warfront-down-battle-line (with --warfront-down-forge): a Battle section of an HQ defence is fed alongside, so the Battle HUD's line and clock are up under the spectator overlay (test aid; the layout under test is the overlay's place)
        if (CmdFlag("warfront-down-battle-line") && now >= 5.0 && now < 24.0 && now - _wfdBattleForgedAt >= 0.5)
        {
            _wfdBattleForgedAt = now;
            // a banner message once before the spectating (the banner at 250) and once at its start (under the strip: 390), then none
            byte message = now is >= 6.0 and < 6.6 || now is >= 11.8 and < 12.4 ? SmSim.Extras.Warfront.Hq.WarfrontHqBattleRules.MessageWallFell : (byte)0;
            WarfrontBattleReceive(WarfrontBattleWire.EnvelopeTag, WarfrontBattleWire.Encode(new WarfrontBattleWire.Section(1, 0, 1, 3, 40, 372, message, 7, 100)));
        }
        if (CmdFlag("warfront-down-battle-line") && !_wfdBannerShot1 && now >= 7.0) { _wfdBannerShot1 = true; WfhShot("down-banner-250"); }
        if (CmdFlag("warfront-down-battle-line") && !_wfdBannerShot2 && now >= 13.0) { _wfdBannerShot2 = true; WfhShot("down-banner-390"); }
        if (now - _wfdLogAt >= 2.0)
        {
            _wfdLogAt = now;
            string d = _camera is not null && body is { } b ? _camera.GlobalPosition.DistanceTo(b).ToString("F1") : "-";
            GD.Print($"WFDOWN t={now:F1} alive={me.Alive} down={WarfrontCoop.DownPhase} spectating={spec} role={WarfrontCoop.Role} watch={s.WatchKind}:{s.WatchId} cameraToBody={d} m picker={PickerBusy} fallLabel={_wfDownLabel?.Visible == true && _wfDownLayer?.Visible == true} spectateHud={_wfSpectateHud?.Visible == true} \"{WarfrontCoop.WatchingText}\" / \"{WarfrontCoop.SpectateSubText}\"");
        }
        if (spec && !_wfdWasSpectating) { _wfdSince = now; _wfdStage = 0; GD.Print("WFDOWN spectating begins"); }
        if (!spec && _wfdWasSpectating) { _wfdSince = now; _wfdStage = 10; GD.Print($"WFDOWN spectating ends; alive={me.Alive}"); }
        _wfdWasSpectating = spec;
        _wfhHoldF = now < _wfdFUntil;
        double since = now - _wfdSince;
        if (_wfdSince < 0) return;
        if (_wfdStage == 0 && since > 3) { WfhShot("down-1-spectating"); _wfdStage = 1; }
        else if (_wfdStage == 1 && since > 6) { _wfdFUntil = now + 0.2; GD.Print($"WFDOWN F tapped (watch before {s.WatchKind}:{s.WatchId})"); _wfdStage = 2; }
        else if (_wfdStage == 2 && since > 9) { WfhShot("down-2-after-f"); GD.Print($"WFDOWN after F: watch {s.WatchKind}:{s.WatchId}"); _wfdStage = 3; }
        else if (_wfdStage == 10 && since > 2) { WfhShot("down-3-own-view-back"); GD.Print("WFDOWN own view back"); _wfdStage = 11; }
    }

    string WfhHand(PlayerState me) => Loadouts.InHand(_config, me)?.Name ?? "-";

    void WfhShot(string name)
    {
        if (CmdArg("warfront-hooks-shots") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var size = GetViewport().GetVisibleRect().Size;
        string path = Path.Combine(dir, $"{name}-{(int)size.X}x{(int)size.Y}.png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"WFHOOK shot {name}: {err}");
    }

    void WfhKey(string action, bool down)
    {
        if (down) Input.ActionPress(action); else Input.ActionRelease(action);
    }

    /// <summary>A real key event through the input pipeline (press, then release next frame by the caller's timing); <paramref name="unicode"/> is what a text field would take.</summary>
    static void WfhRealKey(Key key, bool pressed, int unicode = 0) =>
        Input.ParseInputEvent(new InputEventKey { PhysicalKeycode = key, Keycode = key, Pressed = pressed, Unicode = unicode });

    int _wfhPicks, _wfhPickedSlot = -1;
    string _wfhTyped = "";

    void WfhNext(double now, int step) { _wfhStep = step; _wfhAt = now; }

    void WarfrontHooksTestFrame(PlayerState me, double dt)
    {
        if (!WarfrontHooksTest || _bot || _client is not { Status: ClientStatus.Connected } || !me.Alive) return;
        double now = _connectedFor;
        if (_wfhStarted < 0) { _wfhStarted = now; WfhNext(now, 0); }
        if (!_wfhWired) { _wfhWired = true; WarfrontCoop.ConfirmRequested += () => { _wfhConfirms++; GD.Print($"WFHOOK ConfirmClass requested ({_wfhConfirms})"); }; }
        if (_wfProgression is { } wired && !_wfhPickWired) { _wfhPickWired = true; wired.PickRequested += (slot, index) => { _wfhPicks++; _wfhPickedSlot = slot; GD.Print($"WFHOOK pick sent: slot {slot + 1}, index {index}"); }; }
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
                    WfhRealKey(Key.Escape, true); WfhRealKey(Key.Escape, false);   // Esc with the card picker open (item 6f): what stands?
                    WfhNext(now, 20);
                }
                break;
            case 20:
                if (since > 0.8)
                {
                    GD.Print($"WFHOOK Esc over the card picker: Esc menu up={PickerOpen}, card picker still visible={_wfProgression is { Picker.Visible: true }}, PickerBusy={PickerBusy}, mouse={Input.MouseMode}");
                    WfhShot("1b-esc-over-picker");
                    if (PickerOpen) { WfhRealKey(Key.Escape, true); WfhRealKey(Key.Escape, false); }
                    WfhNext(now, 21);
                }
                break;
            case 21:
                if (since > 1.0)
                {
                    GD.Print($"WFHOOK Esc again: Esc menu up={PickerOpen}, card picker visible={_wfProgression is { Picker.Visible: true }}");
                    _wfhHand = WfhHand(me);
                    WfhKey("sm_weapon2", true);   // the weapon-2 action held under the open picker ...
                    WfhRealKey(Key.Key3, true, '3'); WfhRealKey(Key.Key3, false, '3');   // ... and the real key 3 (the picker's own: card 3) through the input pipeline
                    WfhNext(now, 1);
                }
                break;
            case 1:   // weapon keys held 1.2 s under the open picker
                if (since > 1.2)
                {
                    WfhKey("sm_weapon2", false);
                    string after = WfhHand(me);
                    GD.Print($"WFHOOK {(after == _wfhHand ? "PASS" : "FAIL")} weapon keys under the picker: hand {_wfhHand} -> {after}; picker still up={_wfProgression is { Picker.Visible: true }}");
                    GD.Print($"WFHOOK {(_wfhPicks == 1 && _wfhPickedSlot == 2 ? "PASS" : "FAIL")} the real key 3 picked card 3 once: picks sent {_wfhPicks}, slot {_wfhPickedSlot + 1}");
                    if (_wfhPicks == 0 && _wfProgression is { } host) { int slot = Array.IndexOf(host.Model.Cards, SmSim.Extras.Warfront.Progression.WarfrontCard.AttackSpeed); host.Model.Pick(slot < 0 ? 0 : slot); GD.Print($"WFHOOK picked slot {Math.Max(slot, 0)} (attack speed card at {slot})"); }
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
                    if (_wfSquad!.Panel.NameEdits.Count > 0)
                    {
                        var edit = _wfSquad.Panel.NameEdits[0];
                        edit.GrabFocus();
                        _wfhHand = WfhHand(me);
                        _wfhTyped = edit.Text;
                        foreach (char ch in "2f1") { var key = ch == 'f' ? Key.F : ch == '1' ? Key.Key1 : Key.Key2; WfhRealKey(key, true, ch); WfhRealKey(key, false, ch); }
                        WfhNext(now, 13);
                    }
                    else { _wfSquad.Panel.Close(); WfhNext(now, 6); }
                }
                break;
            case 13:   // typing a name: the keys go to the text field, not the game
                if (since > 0.6)
                {
                    var edit = _wfSquad!.Panel.NameEdits[0];
                    string hand = WfhHand(me);
                    GD.Print($"WFHOOK {(hand == _wfhHand && !WarfrontSpectating ? "PASS" : "FAIL")} typing in the squad name field: hand {_wfhHand} -> {hand}; field \"{_wfhTyped}\" -> \"{edit.Text}\"; PickerBusy={PickerBusy}");
                    edit.ReleaseFocus();
                    _wfSquad.Panel.Close();
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
