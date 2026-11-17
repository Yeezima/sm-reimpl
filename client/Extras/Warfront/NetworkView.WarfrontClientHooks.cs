using System;
using Godot;
using SmClient.Extras.Warfront;
using SmClient.Extras.Warfront.Hub;
using SmClient.Extras.Warfront.Progression;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront.Coop;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The bodies of Warfront's client hooks (slice W5c): the busy flag, the spectator view and its keys, the attack-speed carry, the held-M war map, the customizer's open and close on the ship, and the wiring
/// of the squad's and the co-op's sections that W5b left as no-op seams. Every body returns at once unless the match has the Warfront flag.
/// </summary>
public partial class NetworkView
{
    // ---- the picker and the squad panel take the game's input -----------------------------------------------------------------------------

    /// <summary>True while the card picker or the squad panel is up, or a text field has the focus: the weapon keys, the fire button and the rest of the game's input stand still (<see cref="PickerBusy"/>).</summary>
    partial void WarfrontPickerBusy(ref bool busy)
    {
        if (busy || !WarfrontOn) return;
        if (_wfProgression is { Picker.Visible: true }) { busy = true; return; }
        if (_wfSquad is { Panel.Visible: true }) { busy = true; return; }
        if (IsInsideTree() && GetViewport()?.GuiGetFocusOwner() is LineEdit) busy = true;
    }

    // ---- the attack-speed cards on the predicted player ------------------------------------------------------------------------------------

    partial void WarfrontCarry(ref PlayerState record)
    {
        if (_wfProgression is { } host) record = record with { AttackSpeedBonus = host.Model.AttackSpeedBonus };
    }

    // ---- M held: the Warfront war map, read only -------------------------------------------------------------------------------------------

    WarfrontHubPanel? _wfPeek;
    CanvasLayer? _wfPeekLayer;

    partial void WarfrontPeekWarMap(ref bool handled)
    {
        if (!WarfrontOn) return;
        handled = true;
        if (_bot) return;
        bool held = (Input.IsPhysicalKeyPressed(Key.M) || _wfhHoldM) && !PickerBusy;   // _wfhHoldM: the test aid's stand-in for the key (a synthetic event does not set the polled key state)
        _tfwMapHeld = held;   // Musou's HUD hides under the held map as it does under its own
        if (held && _wfPeek is null)
        {
            _wfPeekLayer = new CanvasLayer { Name = "WarfrontMapPeek", Layer = 50 };
            var root = new Control { Name = "WarfrontMapPeekRoot", Theme = SmClient.Customizer.UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
            _wfPeek = new WarfrontHubPanel(WarfrontModel.Hub, null) { MouseFilter = Control.MouseFilterEnum.Ignore };
            for (int row = 0; row < WarfrontHubModel.Rows; row++) { var b = _wfPeek.DeployRow(row); b.MouseFilter = Control.MouseFilterEnum.Ignore; b.FocusMode = Control.FocusModeEnum.None; b.Modulate = new Color(1f, 1f, 1f, 0f); }   // read only: no deploy rows drawn
            root.AddChild(_wfPeek);
            _wfPeekLayer.AddChild(root);
            AddChild(_wfPeekLayer);
        }
        if (_wfPeekLayer is null || _wfPeek is null) return;
        _wfPeekLayer.Visible = held;
        if (!held) return;
        var size = GetViewport().GetVisibleRect().Size;
        float scale = size.Y / 1080f;
        if (scale > 0f && _wfPeek.GetParent() is Control peekRoot)
        {
            peekRoot.Scale = new Vector2(scale, scale);
            peekRoot.Size = size / scale;
            _wfPeek.Size = peekRoot.Size;
        }
    }

    // ---- the customizer on the ship ----------------------------------------------------------------------------------------------------

    int _wfSquadClassSent = -1;

    partial void WarfrontCustomizerOpened()
    {
        if (WarfrontOn) WarfrontCoop.CustomizerOpened();
    }

    partial void WarfrontCustomizerClosed()
    {
        if (!WarfrontOn) return;
        WarfrontCoop.CustomizerClosed();   // ConfirmClass, when the joiner awaited its class
        WarfrontResendSquadClass();
    }

    /// <summary>A Librarian whose stored squad class differs from the one last sent sends it again (one SquadClass command).</summary>
    internal void WarfrontResendSquadClass()
    {
        if (_client is not { Status: ClientStatus.Connected } c || _config is null || _pickerServices?.Session?.Saved is not { } saved) return;
        if (!LibrarianRules.IsLibrarian(_config, c.Predicted.Class)) return;
        byte option = (byte)WarfrontSquadClassOption.SquadClassOf(saved);
        if (option == _wfSquadClassSent) return;
        _wfSquadClassSent = option;
        WarfrontSquadEnsure();
        _wfSquad?.SendSquadClass(option);
        GD.Print($"t {_connectedFor:F1} s: warfront: squad class {option} sent");
    }

    // ---- the co-op's section and frame; the squad's section, frame and keys (W5b's seams) ---------------------------------------------------------

    partial void WarfrontCoopSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (tag != WarfrontCoopWire.EnvelopeTag) return;
        handled = true;
        if (!WarfrontCoopWire.TryDecode(payload, out var s)) return;
        string own = _client is { } c ? HudName(c.PlayerId) : "";
        string watch = "";
        if (s.WatchKind == WarfrontWatchKind.Player) watch = HudName(s.WatchId);
        else if (s.WatchKind == WarfrontWatchKind.Marine && _wfSquad is { } squad) watch = squad.Model.NameOfIndex(s.WatchId);
        int level = _wfProgression?.Model.Level ?? 1;
        string leader = WarfrontModel.Hub.LeaderName;
        WarfrontCoopSection(payload, leader == "NOBODY" ? "" : leader, watch, own, level);
    }

    bool _wfWasSpectating;
    bool _wfFWas;

    partial void WarfrontCoopFrame(PlayerState me, double dt)
    {
        if (!WarfrontOn) return;
        if (_hudView is not null) WarfrontCoopBuildUi(_hudView);
        WarfrontCoop.Tick(dt);
        WarfrontKeyNodeEnsure();
        MusouEnsureCarry();   // the attack-speed carry stands from the first frame, buff lists or none
        WarfrontHooksTestFrame(me, dt);
        bool spectating = WarfrontSpectating && !_bot;
        if (spectating)
        {
            bool f = Input.IsPhysicalKeyPressed(Key.F);
            if (f && !_wfFWas) WarfrontCoop.StepWatch(Input.IsPhysicalKeyPressed(Key.Shift) ? -1 : 1);
            _wfFWas = f;
            // The rig and the death camera placed the camera first; the spectator follow (springs, level cast) places it again over them.
            if (_camera is not null && _gameSpace is not null) SpectateFollow(me, SpectateG(me.Position), (float)dt);
        }
        else if (_wfWasSpectating) _specInit = false;   // the next watch starts with a cut
        _wfWasSpectating = spectating;
    }

    partial void WarfrontSpectateView(PlayerState me, ref (Vector3 Eye, Vector3 Target)? view)
    {
        if (!WarfrontOn || !WarfrontSpectating || _client is not { } c || _gameSpace is null) return;
        var s = WarfrontCoop.Section;
        Num.Vector3? at = null;
        float yaw = 0f;
        if (s.WatchKind == WarfrontWatchKind.Player)
        {
            if (s.WatchId == c.PlayerId) { at = me.Position; yaw = me.Yaw; }
            else foreach (var p in c.RemotePlayers().Players) if (p.PlayerId == s.WatchId && p.Alive) { at = p.Position; yaw = p.Yaw; break; }
        }
        else if (s.WatchKind == WarfrontWatchKind.Marine && _wfSquad is { } squad && squad.Model.TrySample(s.WatchId, _connectedFor, out var shown)) { at = shown.Position; yaw = shown.Yaw; }
        if (at is { } position) view = WarfrontSpectatePose(SpectateG(position), yaw);
    }

    partial void WarfrontSquadSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (WarfrontSquadReceive(tag, payload)) handled = true;
    }

    bool _wfPanelWas;

    partial void WarfrontSquadFrame(PlayerState me, double dt)
    {
        if (!WarfrontOn) return;
        WarfrontSquadAboard = WarfrontModel.Hub.View.Phase == SmSim.Extras.Warfront.Campaign.WarfrontPhase.Ship;
        WarfrontSquadTick(me, dt);
        bool open = _wfSquad is { Panel.Visible: true };
        if (open && !_wfPanelWas) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (!open && _wfPanelWas) RestoreMouse(leaving: false);
        _wfPanelWas = open;
    }

    /// <summary>A child that offers the squad's keys (F4, J, Escape on its panel) before the view's own handling: children of the view see unhandled input first.</summary>
    sealed partial class WarfrontKeys(NetworkView view) : Node
    {
        public override void _UnhandledInput(InputEvent e)
        {
            if (view._bot || e is not InputEventKey key) return;
            if (view.WarfrontSquadKey(key)) GetViewport().SetInputAsHandled();
        }
    }

    WarfrontKeys? _wfKeys;

    void WarfrontKeyNodeEnsure()
    {
        if (_wfKeys is not null) return;
        _wfKeys = new WarfrontKeys(this) { Name = "WarfrontKeys" };
        AddChild(_wfKeys);
    }
}
