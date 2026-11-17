using System;
using Godot;
using SmClient.Extras.Warfront.Progression;
using SmNet;
using SmNet.Extras.Warfront;
using SmSim;

namespace SmClient;

/// <summary>
/// Warfront's card picker and XP HUD wired into the network view (slice W8s): implements W5b's two fan-out hooks. The <see cref="WarfrontProgressionHost"/> exists only on a Warfront server (made on the first section
/// or frame), takes tags 44 and 45, sends the Pick command from <c>PickRequested</c> and resets on a stage change.
/// </summary>
public partial class NetworkView
{
    /// <summary>
    /// Whether a human sees the card picker on a live server: <b>true</b> (flipped by W5c once <c>PickerBusy</c> kept the weapon keys and the fire button out of the game under the open picker; pinned in
    /// <c>WarfrontBoundaryTests</c>). The HUD, the banner and the bot's picks never depended on it: a hidden picker would leave the server's 20 s timeout to take card 1.
    /// </summary>
    internal static bool WarfrontPickerLive = true;

    WarfrontProgressionHost? _wfProgression;
    WarfrontStageState _wfProgressionStage;
    bool _wfProgressionRejoin;

    WarfrontProgressionHost? WarfrontProgressionNode()
    {
        if (!WarfrontOn) return null;
        if (_wfProgression is not null) return _wfProgression;
        bool bot = _bot && WarfrontBotFlag;
        var host = new WarfrontProgressionHost(bot);
        host.Picker.Enabled = WarfrontPickerLive || bot;
        host.PickRequested += (slot, index) =>
        {
            if (_bot) GD.Print($"t {_connectedFor:F1} s: warfront: card pick sent: slot {slot + 1}, pick index {index}");
            if (_client is { Status: ClientStatus.Connected } c)
                c.SendExtrasCommand(WarfrontWire.CommandTag, WarfrontWire.Encode(new WarfrontClientCommand(WarfrontClientCommandKind.Pick, Slot: slot, Pick: index)));
        };
        AddChild(host);
        return _wfProgression = host;
    }

    partial void WarfrontProgressionSection(byte tag, ReadOnlySpan<byte> payload, ref bool handled)
    {
        if (WarfrontProgressionNode() is { } host && host.Section(tag, payload)) handled = true;
    }

    partial void WarfrontProgressionFrame(PlayerState me, double dt)
    {
        if (WarfrontProgressionNode() is not { } host) return;
        var stage = WarfrontModel.Stage?.State ?? WarfrontStageState.None;
        bool rejoin = WarfrontModel.RejoinDue;
        if (stage != _wfProgressionStage && stage == WarfrontStageState.Go || rejoin && !_wfProgressionRejoin) host.Model.Reset();   // the stage moves on: nothing shown carries over
        _wfProgressionStage = stage;
        _wfProgressionRejoin = rejoin;
        host.Picker.Enabled = WarfrontPickerLive || (_bot && WarfrontBotFlag);
        host.Model.PickerHidden = !host.Picker.Enabled;
        host.Board.PlayerRows = Math.Max(1, _client?.Scoreboard?.Players.Length ?? 1);
        host.Board.Shown = _boardView is { Visible: true };   // the XP section belongs to the Tab scoreboard
        host.Frame((float)dt);
        WarfrontXpShotFrame();
    }
}

public partial class NetworkView
{
    // ---- --warfront-xp-shots <dir>: test aid (windowed): takes the cards offered with key 1, then saves "xp-in-play" (no XP panel on the field) and, with the scoreboard action held, "xp-scoreboard" ----
    static readonly string? WarfrontXpShots = CmdArg("warfront-xp-shots");
    double _wfxClear = -1, _wfxHeldAt = -1, _wfxPressAt;
    int _wfxStage;

    void WarfrontXpShotFrame()
    {
        if (WarfrontXpShots is not { Length: > 0 } dir || _wfProgression is not { } host || !host.Model.HasProgress) return;
        double now = _connectedFor;
        void Shot(string name)
        {
            System.IO.Directory.CreateDirectory(dir);
            var size = GetViewport().GetVisibleRect().Size;
            var err = GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"{name}-{(int)size.X}x{(int)size.Y}.png"));
            GD.Print($"WFXP shot {name}: {err}");
        }
        string board = ControlsInput.Name(GameAction.Scoreboard);
        if (_wfxStage == 0)
        {
            if (host.Model.OfferOpen)
            {
                _wfxClear = -1;
                if (now - _wfxPressAt > 0.8) { _wfxPressAt = now; WfhRealKey(Key.Key1, true); WfhRealKey(Key.Key1, false); }
            }
            else if (_wfxClear < 0) _wfxClear = now;
            else if (now - _wfxClear > 3.0) { Shot("xp-in-play"); GD.Print($"WFXP model: {host.Model.LevelText}, {host.Model.XpText}, pending {host.Model.Pending}, board visible {host.Board.Visible}"); Input.ActionPress(board); _wfxHeldAt = now; _wfxStage = 1; }
        }
        else if (_wfxStage == 1 && now - _wfxHeldAt > 1.0)
        {
            Shot("xp-scoreboard"); GD.Print($"WFXP board visible {host.Board.Visible}");
            Input.ActionRelease(board); _wfxStage = 2;
        }
    }
}
