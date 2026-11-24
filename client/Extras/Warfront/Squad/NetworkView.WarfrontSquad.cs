using System;
using System.Collections.Generic;
using Godot;
using SmClient.Extras.Warfront.Squad;
using SmNet.Extras.Warfront;
using SmSim;
using Num = System.Numerics;

namespace SmClient;

/// <summary>
/// The squad in the networked view (slice W10; Phase B T5): the retainers are real World players, so the view draws, animates and sounds them as it does every player (every class and weapon, the owner's
/// rule of 2026-11-24). The squad's sections go into a <see cref="WarfrontSquadView"/> (model, downed markers, HUD, panel): the roster names each Marine's World id (tag 33), and the model reads that player in
/// the frame's remote players for its place and whether it is down. <see cref="WarfrontSquadReceive"/> takes tags 32 to 34 (32 retired), <see cref="WarfrontSquadTick"/> is the frame,
/// <see cref="WarfrontSquadKey"/> the keys. The ship's "aboard" flag (<see cref="WarfrontSquadAboard"/>) is set by the Warfront hub code. Added content (Warfront); no Musou or core line is touched.
/// </summary>
public partial class NetworkView
{
    WarfrontSquadView? _wfSquad;
    readonly Dictionary<int, WarfrontWorldBody> _wfBodies = new();
    double _wfBodiesAt = -1;

    /// <summary>Whether the player is aboard the ship (the squad panel opens there only; the revive prompt shows only in the field).</summary>
    internal bool WarfrontSquadAboard { get; set; }

    /// <summary>The own squad's view, for the report and the tests.</summary>
    internal WarfrontSquadView? WarfrontSquad => _wfSquad;

    /// <summary>Whether World player <paramref name="id"/> is a Warfront retainer (on a roster the client has); false outside Warfront.</summary>
    internal bool WarfrontIsRetainer(int id) => _wfSquad?.Model.IsRetainer(id) == true;

    /// <summary>A class's display name in capitals ("TACTICAL MARINE"), as the customizer names it; the config's key when the catalogue has no name.</summary>
    string SquadClassDisplay(ClassLoadout info) =>
        (_lookCatalog is { } catalog ? SmClient.Customizer.Model.LoadoutRows.ClassName(catalog, info.Role, info.Side == Team.Chaos ? SmContent.CustomizationCatalog.Chaos : SmContent.CustomizationCatalog.SpaceMarine) : info.Name).ToUpperInvariant();

    void WarfrontSquadEnsure()
    {
        if (_wfSquad is not null || Lean || _gameSpace is null || _hudView is null || _config is null) return;
        var config = _config;
        var view = new WarfrontSquadView(id => id < config.Classes.Count ? SquadClassDisplay(config.Classes[id]) : "?")
        {
            PlayerName = id => HudName(id),
            OwnHasJumpPack = () => _client is { } c && config.Classes.Count > c.Predicted.Class && c.Predicted.Class >= 0 && config.Classes[c.Predicted.Class].Role == "assault",
            MarkPoint = () => null,   // the fireteam mark's point: W5b's mark code supplies it (WarfrontSquad.MarkPoint)
            SendCommand = payload => _client?.SendExtrasCommand(SquadWire.CommandTag, payload),
            UseKeyLabel = ControlsInput.Prompt(GameAction.Use),
            UseHeld = () => !PickerBusy && (ControlsInput.Held(GameAction.Use) || _pad is not null && PadHost.Active && ControlsInput.PadHeld(_pad, GameAction.Use)),
        };
        view.Model.World = WarfrontBodyOf;
        _gameSpace.AddChild(view.Markers);
        var fit = WarfrontFitRoot(_hudView);   // the squad's HUD and panel are laid out in the 1920 by 1080 space: fitted to the window and centred (W5c second pass)
        fit.AddChild(view.Hud);
        fit.AddChild(view.Panel);
        AddChild(view);
        _wfSquad = view;
    }

    /// <summary>The envelope's squad sections (tags 32 to 34); true when one was taken. W5b's <c>WarfrontSquadSection</c> hook calls this.</summary>
    internal bool WarfrontSquadReceive(byte tag, ReadOnlySpan<byte> payload)
    {
        if (tag is < SquadWire.StateTag or > SquadWire.EventsTag) return false;
        WarfrontSquadEnsure();
        if (_wfSquad is null) return true;   // the view is not up yet: the next roster send carries it again
        _wfSquad.Model.OwnPlayer = _client?.PlayerId ?? -1;
        return _wfSquad.Section(tag, payload, _connectedFor);
    }

    /// <summary>The frame. W5b's <c>WarfrontSquadFrame</c> hook calls this.</summary>
    internal void WarfrontSquadTick(PlayerState me, double dt)
    {
        if (_wfSquad is null) return;
        _wfSquad.Aboard = WarfrontSquadAboard;
        _wfSquad.Model.OwnPlayer = _client?.PlayerId ?? -1;
        _wfSquad.Frame(_connectedFor, _camera, _gameSpace, me.Position);
        WarfrontRetainerShotsFrame(me);
    }

    /// <summary>The squad's keys (panel, jump-pound); true when used. Called from the networked view's key handling by W5b's hook.</summary>
    internal bool WarfrontSquadKey(InputEventKey key) => _wfSquad?.HandleKey(key) == true;

    /// <summary>A retainer's World player as this frame draws it (the frame's remote players, the same states the characters are posed from), sampled once a frame; null when it is not in the snapshot.</summary>
    WarfrontWorldBody? WarfrontBodyOf(int id)
    {
        if (_wfBodiesAt != _connectedFor)
        {
            _wfBodiesAt = _connectedFor;
            _wfBodies.Clear();
            foreach (var o in _lastOthers) _wfBodies[o.PlayerId] = new WarfrontWorldBody(o.Position, o.Yaw, o.Alive);
        }
        return _wfBodies.TryGetValue(id, out var b) ? b : null;
    }
}
