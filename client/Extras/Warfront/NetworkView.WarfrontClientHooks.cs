using System;
using Godot;
using SmClient.Extras.Warfront;
using SmClient.Extras.Warfront.Coop;
using SmClient.Extras.Warfront.Hub;
using SmClient.Extras.Warfront.Progression;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;
using SmSim.Extras.Musou;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Coop;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The bodies of Warfront's client hooks (slice W5c): the busy flag, the spectator view and its keys, the attack-speed carry, the held-M war map, the customizer's open and close on the ship, and the wiring
/// of the squad's and the co-op's sections that W5b left as no-op seams. Every body returns at once unless the match has the Warfront flag.
/// </summary>
public partial class NetworkView
{
    /// <summary>
    /// Warfront's melee reach (owner 2026-11-18): x1.2, the Dreadnought x1.4, as the server's <c>World.WarfrontCleave</c>; the drawn arc and the predicted targets use it. Humans and their retainers (Phase B T5: the
    /// server's <c>WarfrontCleaveBonus</c> gives it to <c>MarineRole.Squad</c>, so a retainer's arc is drawn at the reach it strikes); the allied pair and a pod's Marines keep Musou Mode's reach.
    /// </summary>
    partial void WarfrontCleaveScaleFor(int player, ref float scale)
    {
        if (_config is null || !_config.Extras.HasFlag(ContentExtras.Warfront) || _tfwState.War is null) return;
        if (SmSim.Extras.Musou.Campaign.TfwContracts.IsPuppet(player) && !WarfrontIsRetainer(player)) return;
        scale = WarfrontBattleRules.CleaveScale(ClassOf(player) is int c && DreadnoughtRules.IsDreadnought(_config, c));
    }

    /// <summary>A retainer's arc takes the human-only x0.6 only when the server gives it the human-only scales (<c>WarfrontRetainerRules.HumanOnlyScales</c>; today never: base player damage only, owner 2026-11-25).</summary>
    partial void WarfrontArcHuman(int player, ref bool human)
    {
        if (!human && SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.HumanOnlyScales && WarfrontOn && WarfrontIsRetainer(player)) human = true;
    }

    /// <summary>A retainer standing by a landing drop pod is not one of the pod's Marines: it does not give their battle cry (a human player would not).</summary>
    partial void WarfrontNotPodMarine(int id, ref bool not)
    {
        if (WarfrontOn && WarfrontIsRetainer(id)) not = true;
    }

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
    bool _wfPeekHeld;
    CanvasLayer? _wfPeekLayer;

    partial void WarfrontPeekWarMap(ref bool handled)
    {
        if (!WarfrontOn) return;
        handled = true;
        if (_bot) return;
        bool held = (Input.IsPhysicalKeyPressed(Key.M) || _wfhHoldM) && !PickerBusy;   // _wfhHoldM: the test aid's stand-in for the key (a synthetic event does not set the polled key state)
        _wfPeekHeld = held;
        _tfwMapHeld = held || WarfrontFallen;   // Musou's HUD (its prompts: KILLED, the deploy line; the station and capture bar) hides under the held map as it does under its own, and for a fallen player (second pass)
        if (held && _wfPeek is null)
        {
            _wfPeekLayer = new CanvasLayer { Name = "WarfrontMapPeek", Layer = 50 };
            var root = new WarfrontUiFit { Name = "WarfrontMapPeekRoot", Theme = SmClient.Customizer.UiKit.Theme };
            _wfPeek = new WarfrontHubPanel(WarfrontModel.Hub, null) { MouseFilter = Control.MouseFilterEnum.Ignore };
            for (int row = 0; row < WarfrontHubModel.Rows; row++) { var b = _wfPeek.DeployRow(row); b.MouseFilter = Control.MouseFilterEnum.Ignore; b.FocusMode = Control.FocusModeEnum.None; b.Modulate = new Color(1f, 1f, 1f, 0f); }   // read only: no deploy rows drawn
            _wfPeek.NoteSuppressed = true; _wfPeek.NoteLabel.Visible = false;   // the leader's line ("YOU LEAD: CHOOSE THE DEPLOYMENT") is not true under a read-only look
            root.AddChild(_wfPeek);
            _wfPeekLayer.AddChild(root);
            AddChild(_wfPeekLayer);
        }
        if (_wfPeekLayer is null || _wfPeek is null) return;
        _wfPeekLayer.Visible = held;
        if (!held) return;
        _wfPeek.Size = new Vector2(WarfrontUiFit.DesignWidth, WarfrontUiFit.DesignHeight);   // the fit root scales and centres it
    }

    /// <summary>The viewer is down or spectating as a fallen player (the stage model's fresh Down section): the death texts of Classic and Musou are not true for him (second pass).</summary>
    internal bool WarfrontFallen => WarfrontOn && WarfrontModel.DownLine(_connectedFor).Length > 0 && WarfrontModel.Down.Phase != WarfrontDownPhase.None;

    // ---- the customizer on the ship ----------------------------------------------------------------------------------------------------

    int _wfSquadClassSent = -1;
    GameClient? _wfSquadClassClient;

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
        if (!ReferenceEquals(_wfSquadClassClient, c)) { _wfSquadClassClient = c; _wfSquadClassSent = -1; }   // a new connection (each stage's rejoin) has not been told
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
        string leader = WarfrontModel.Hub.View.Leader is null ? "" : WarfrontModel.Hub.LeaderName;
        WarfrontCoop.OwnName = own;   // the decoded section goes straight in: the payload is decoded once
        WarfrontCoop.Apply(s, leader, watch, level, WarfrontCoopModel.PicksLeftFor(s, own));
    }

    bool _wfWasSpectating;
    bool _wfFWas;
    bool _wfCreatorWas;

    partial void WarfrontCoopFrame(PlayerState me, double dt)
    {
        if (!WarfrontOn) return;
        if (_hudView is not null) WarfrontCoopBuildUi(WarfrontFitRoot(_hudView));
        WarfrontCoop.Tick(dt);
        WarfrontKeyNodeEnsure();
        MusouEnsureCarry();   // the attack-speed carry stands from the first frame, buff lists or none
        if (WarfrontFallen && _loadoutHint is not null) _loadoutHint.Visible = false;   // "[ESC] CHANGE LOADOUT": the loadout is changed aboard the ship, not here
        WarfrontHooksTestFrame(me, dt);
        WfdDownWatchFrame(me);
        // The fallen player's state (W5a's Down section, kept by the stage model; stale after 2 s: the server sends one last None) drives the spectator view and its HUD line.
        bool downFresh = WarfrontModel.DownLine(_connectedFor).Length > 0;
        WarfrontCoopDown(downFresh ? WarfrontModel.Down.Phase : WarfrontDownPhase.None, downFresh ? WarfrontModel.Down.SecondsLeft : 0f);
        // The customizer's own state, whichever way it was opened (the Equipment station or the Esc menu): shown, then gone, is the confirmation.
        bool creator = _pickerCreator is { } cv && cv.IsVisibleInTree();
        if (creator && !_wfCreatorWas) WarfrontCustomizerOpened();
        else if (!creator && _wfCreatorWas) WarfrontCustomizerClosed();
        _wfCreatorWas = creator;
        bool spectating = WarfrontSpectating && !_bot;
        if (spectating)
        {
            bool f = (Input.IsPhysicalKeyPressed(Key.F) || _wfhHoldF) && !PickerBusy;   // _wfhHoldF: the down-watch aid's stand-in for the key
            if (f && !_wfFWas) WarfrontCoop.StepWatch(Input.IsPhysicalKeyPressed(Key.Shift) ? -1 : 1);
            _wfFWas = f;
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
        else if (s.WatchKind == WarfrontWatchKind.Marine && _wfSquad is { } squad && squad.Model.WorldIdOf(s.WatchId) is var marine and > 0)   // Phase B T5: the Marine is its World player
            foreach (var p in c.RemotePlayers().Players) if (p.PlayerId == marine) { at = p.Position; yaw = p.Yaw; break; }
        if (at is null) { at = me.Position; yaw = me.Yaw; }   // nobody to watch yet (the server names the watch on the first F): the own body's place
        if (at is { } position) view = WarfrontSpectatePose(SpectateG(position), yaw);
    }

    partial void WarfrontSquadSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (WarfrontSquadReceive(tag, payload)) handled = true;
    }

    bool _wfPanelWas;
    double _wfResendClock;

    partial void WarfrontSquadFrame(PlayerState me, double dt)
    {
        if (!WarfrontOn) return;
        WarfrontSquadAboard = WarfrontModel.Hub.View.Phase == SmSim.Extras.Warfront.Campaign.WarfrontPhase.Ship;
        WarfrontSquadTick(me, dt);
        _wfResendClock += dt;
        if (_wfResendClock >= 1.0) { _wfResendClock = 0.0; WarfrontResendSquadClass(); }   // a Librarian's stored option reaches the server on every connection, once a second at most
        bool open = _wfSquad is { Panel.Visible: true };
        if (open && !_wfPanelWas) Input.MouseMode = Input.MouseModeEnum.Visible;
        else if (!open && _wfPanelWas) RestoreMouse(leaving: false);
        _wfPanelWas = open;
        if (_wfProgression is { } host) host.Hud.Modulate = open || _wfPeekHeld ? new Color(1f, 1f, 1f, 0f) : Colors.White;   // the XP HUD stays out of the way of the squad panel and the held map
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

    WarfrontUiFit? _wfFit;

    /// <summary>The one 1920 by 1080 root of the squad's HUD and panel, the join panel and the spectator HUD, in the HUD layer (<see cref="WarfrontUiFit"/>).</summary>
    internal WarfrontUiFit WarfrontFitRoot(Control parent)
    {
        if (_wfFit is { } fit && IsInstanceValid(fit)) return fit;
        _wfFit = new WarfrontUiFit { Name = "WarfrontFit" };
        parent.AddChild(_wfFit);
        return _wfFit;
    }

    void WarfrontKeyNodeEnsure()
    {
        if (_wfKeys is not null) return;
        _wfKeys = new WarfrontKeys(this) { Name = "WarfrontKeys" };
        AddChild(_wfKeys);
    }
}
