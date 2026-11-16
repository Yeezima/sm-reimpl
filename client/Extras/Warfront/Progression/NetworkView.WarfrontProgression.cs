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
    /// Whether a human sees the card picker on a live server. False until W5c's <c>PickerBusy</c> hook exists (keys 1 to 4 and the pad would still switch weapons under the panel); W5c flips it to true. The HUD, the banner and the bot's
    /// picks do not depend on it: a hidden picker leaves the server's 20 s timeout to take card 1.
    /// </summary>
    internal static bool WarfrontPickerLive = false;

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
        host.Frame((float)dt);
    }
}
