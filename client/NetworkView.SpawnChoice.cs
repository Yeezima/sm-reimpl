using System.Collections.Generic;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// fix/pvp-deploy-spawn (protocol 57; the owner, 2026-11-06; classic PvP only): the death screen's respawn choice. While dead, Next weapon (X or the wheel; a controller's
/// D-pad left / right) cycles the spawn room, each control point the side holds (Seize Ground) and each ally the scoreboard marks as a living Teleport Homer carrier; the
/// line under the loadout hint says where the respawn goes. The choice is resent twice a second while dead (unacknowledged, idempotent); the server checks it again at the
/// respawn and uses the spawn room when it no longer holds (<see cref="World.SetSpawnChoice"/>). Rules: <see cref="SpawnChoiceRules"/>. Musou Mode shows none of it.
/// </summary>
public partial class NetworkView
{
    SpawnChoice _spawnWanted = SpawnChoice.SpawnRoom;
    double _spawnSentAt = double.MinValue;
    IReadOnlyList<SpawnChoice> _spawnOptions = [];

    /// <summary>The choices offered now (none outside classic PvP or before the scoreboard).</summary>
    IReadOnlyList<SpawnChoice> CurrentSpawnOptions(PlayerState me) =>
        _client is null || _config is null ? [] : SpawnChoiceRules.Options(_client.Scoreboard, _config.Extras, _client.PlayerId, me.Team, _client.Points);

    /// <summary>Called by <c>_UnhandledInput</c> for humans: on the death screen Next weapon or the D-pad cycles the respawn choice.</summary>
    bool SpawnChoiceInput(InputEvent e)
    {
        if (_client is null || !OwnDead || PickerBusy || _spawnOptions.Count < 2) return false;
        int step = 0;
        if (e is InputEventJoypadButton { Pressed: true } pad)
            step = pad.ButtonIndex == JoyButton.DpadRight ? 1 : pad.ButtonIndex == JoyButton.DpadLeft ? -1 : 0;
        else if (e.IsPressed() && !e.IsEcho() && ControlsInput.Is(e, GameAction.NextWeapon))
            step = e is InputEventMouseButton { ButtonIndex: MouseButton.WheelDown } ? -1 : 1;
        if (step == 0) return false;
        var current = SpawnChoiceRules.Effective(_spawnOptions, _spawnWanted);
        _spawnWanted = SpawnChoiceRules.Next(_spawnOptions, current, step);
        _spawnSentAt = double.MinValue;   // send at once
        GD.Print($"t {_connectedFor:F1} s: id {_client.PlayerId} respawn choice: {SpawnChoiceRules.Label(_spawnWanted, _client.Scoreboard)}");
        return true;
    }

    /// <summary>Per frame (from <c>LoadoutFrame</c>): the options, and while dead the current choice resent.</summary>
    void SpawnChoiceFrame(PlayerState me)
    {
        if (_client is null) return;
        _spawnOptions = CurrentSpawnOptions(me);
        if (_spawnOptions.Count == 0 || me.PlayerId <= 0 || me.Alive || _bot) return;
        // A choice that is no longer offered (the point lost, the carrier dead) reads as the spawn room; it comes back if offered again before the respawn.
        if (_connectedFor - _spawnSentAt < SpawnChoiceRules.ResendSeconds) return;
        _spawnSentAt = _connectedFor;
        _client.SendSpawnChoice(SpawnChoiceRules.Effective(_spawnOptions, _spawnWanted));
    }

    /// <summary>The death screen's line ("" when there is no choice to show).</summary>
    string SpawnChoiceLine(PlayerState me)
    {
        if (_spawnOptions.Count == 0 || UiAlive(me) || _bot) return "";
        string at = "SPAWN AT: " + SpawnChoiceRules.Label(SpawnChoiceRules.Effective(_spawnOptions, _spawnWanted), _client?.Scoreboard);
        return _spawnOptions.Count > 1 ? $"{at}   [{PadHost.Hint("X", "D-PAD")}] CHANGE" : at;
    }
}
