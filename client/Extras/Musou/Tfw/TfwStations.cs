using System;
using Godot;
using SmClient.Customizer;
using SmNet;
using SmNet.Extras.Musou.Campaign;
using SmSim;
using SmSim.Extras.Musou.Campaign;

namespace SmClient;

/// <summary>
/// The Stormbird's stations and the war map (design section G). Client-side: near a station the prompt shows and the Use action (E, B on a controller) opens it; Destination the full-screen
/// <see cref="WarMapPanel"/>, Equipment the in-match loadout picker straight on its loadout screen, whose BACK returns to the game. M, held, shows the same map over the game for a look: nothing on it
/// can be pressed and the game keeps its input.
/// <para>
/// The open war map stands where the picker stands (<c>_pickerLayer</c>, forced), so the view's existing rules hold for it unchanged: the mouse is free, the game takes no input and Esc does not open the
/// menu. It closes with Esc or B, its CLOSE row, a Deploy, the end of the hub, or the player's death.
/// </para>
/// </summary>
public partial class NetworkView
{
    TfwStation _tfwAt;
    bool _tfwUseWas, _tfwEquipment, _tfwMapHeld;
    WarMapPanel? _tfwPanel, _tfwPeek;
    CanvasLayer? _tfwPeekLayer;

    void TfwStationsFrame(in TfwWarView war, PlayerState me)
    {
        if (_bot) return;
        // Equipment's picker opens on the Esc menu's loadout screen; its BACK would land on that menu: from a station it goes back to the game.
        if (_tfwEquipment && !PickerBusy) _tfwEquipment = false;
        if (_tfwEquipment && _pickerMenu is { } menu)
        {
            if (!_tfwEquipmentShown) { _tfwEquipmentShown = true; ShowPickerLoadout(); }
            else if (menu.Visible) { _tfwEquipment = false; ClosePicker(); }
        }

        if (_tfwPanel is not null)
        {
            if (_pickerLayer is null) _tfwPanel = null;   // the view closed it (the connection went)
            else if (war.Phase != TfwRunPhase.Hub || !me.Alive) TfwCloseWarMap();
            else _tfwPanel.Show(war, _client?.Match.State == MatchState.Playing);
        }
        TfwPeekFrame(war);

        _tfwAt = war.Phase == TfwRunPhase.Hub && me.Alive && !PickerBusy ? TfwHudModel.StationAt(me.Position) : TfwStation.None;
        bool use = ControlsInput.Held(GameAction.Use) || _pad is not null && PadHost.Active && !_padUseBlocked && ControlsInput.PadHeld(_pad, GameAction.Use);
        bool press = use && !_tfwUseWas;
        _tfwUseWas = use;
        if (!press) return;
        if (_tfwAt == TfwStation.Destination) TfwOpenWarMap(war);
        else if (_tfwAt == TfwStation.Equipment) TfwOpenEquipment();
    }

    bool _tfwEquipmentShown;

    void TfwOpenEquipment()
    {
        _tfwEquipment = true;
        _tfwEquipmentShown = false;
        if (!OpenPicker(forced: true)) _tfwEquipment = false;
        GD.Print($"t {_connectedFor:F1} s: war: Equipment opens the loadout picker");
    }

    void TfwOpenWarMap(in TfwWarView war)
    {
        if (PickerBusy) return;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        UiKit.EnterVirtualSpace(GetWindow());
        _pickerForced = true;
        _pickerLayer = new CanvasLayer { Name = "WarMap", Layer = 60 };
        var root = new Control { Name = "WarMapRoot", Theme = UiKit.Theme };
        root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _tfwPanel = new WarMapPanel(TfwDeploy, TfwCloseWarMap);
        root.AddChild(_tfwPanel);
        _pickerLayer.AddChild(root);
        AddChild(_pickerLayer);
        UiSounds.Current?.Attach(_pickerLayer);
        _tfwPanel.Show(war, _client?.Match.State == MatchState.Playing);
        _tfwPanel.Focus();
        GD.Print($"t {_connectedFor:F1} s: war map opened");
    }

    void TfwCloseWarMap()
    {
        if (_tfwPanel is null) return;
        _tfwPanel = null;
        _pickerLayer?.QueueFree();
        _pickerLayer = null;
        _pickerForced = false;
        PadMenuClosed();
        UiKit.LeaveVirtualSpace(GetWindow());
        RestoreMouse(leaving: false);
        GD.Print($"t {_connectedFor:F1} s: war map closed");
    }

    /// <summary>A DEPLOY row: the command goes out (resent until echoed) and the map closes. The server decides whether it stands.</summary>
    void TfwDeploy(TfwLane lane)
    {
        var sequence = _tfwState.Commands.Issue(TfwCommand.Deploy, (byte)lane);
        GD.Print($"t {_connectedFor:F1} s: war: deploy to front {lane} (sequence {sequence})");
        TfwCloseWarMap();
    }

    /// <summary>M held: the map over the game, in the 1080-high space the HUD draws in (the window has no virtual space while it plays).</summary>
    void TfwPeekFrame(in TfwWarView war)
    {
        _tfwMapHeld = Input.IsPhysicalKeyPressed(Key.M) && !PickerBusy && !_bot;
        if (_tfwMapHeld && _tfwPeek is null)
        {
            _tfwPeekLayer = new CanvasLayer { Name = "WarMapPeek", Layer = 50 };
            var root = new Control { Name = "WarMapPeekRoot", Theme = UiKit.Theme, MouseFilter = Control.MouseFilterEnum.Ignore };
            _tfwPeek = new WarMapPanel(null, null);
            root.AddChild(_tfwPeek);
            _tfwPeekLayer.AddChild(root);
            AddChild(_tfwPeekLayer);
        }
        if (_tfwPeekLayer is null || _tfwPeek is null) return;
        _tfwPeekLayer.Visible = _tfwMapHeld;
        if (!_tfwMapHeld) return;
        var size = GetViewport().GetVisibleRect().Size;
        float scale = size.Y / 1080f;
        if (scale > 0f && _tfwPeek.GetParent() is Control peekRoot)
        {
            peekRoot.Scale = new Vector2(scale, scale);
            peekRoot.Size = size / scale;
            _tfwPeek.Size = peekRoot.Size;
        }
        _tfwPeek.Show(war, _client?.Match.State == MatchState.Playing);
    }
}
