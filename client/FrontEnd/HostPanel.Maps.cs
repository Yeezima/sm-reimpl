using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmClient.Customizer;
using SmContent;

namespace SmClient.FrontEnd;

/// <summary>
/// classic-maps-all (2026-11-15): the Host panel's MAP row, the install's own competitive map list (<see cref="MapCatalog"/>) with the default Manufactorum first and ROTATION last
/// (<see cref="HostMapChoices"/>). Test aid <c>--host-map mp_tdm_17|rotation</c> picks one (not saved). Musou Mode keeps its own map (the row is not used there).
/// </summary>
public sealed partial class HostPanel
{
    UiKit.MenuChoice? _mapRow;
    IReadOnlyList<HostMapChoices.Choice> _mapChoices = [];
    string _map = HostMapChoices.DefaultMap;

    UiKit.MenuChoice? BuildMapRow()
    {
        try { _mapChoices = HostMapChoices.For(MapCatalog.Read(_gameDir)); }
        catch (Exception e) when (e is System.IO.IOException or InvalidOperationException or System.IO.InvalidDataException or UnauthorizedAccessException)
        { GD.Print($"host: the map list could not be read ({e.Message}); the server's default map is used"); _mapChoices = []; }
        if (_mapChoices.Count < 2) return null;   // nothing to choose (no install data): the server's default
        if (_args.GetValueOrDefault("host-map") is { } wanted && _mapChoices.Any(c => c.Value.Equals(wanted, StringComparison.OrdinalIgnoreCase)))
            _map = _mapChoices.First(c => c.Value.Equals(wanted, StringComparison.OrdinalIgnoreCase)).Value;
        int index = Math.Max(0, _mapChoices.ToList().FindIndex(c => c.Value == _map));
        var row = new UiKit.MenuChoice("Map", _mapChoices.Select(c => c.Label).ToArray(), index, i => _map = _mapChoices[i].Value,
            "The map the server plays. ROTATION plays every map in turn, one match each (everyone is moved on automatically after the scoreboard).");
        row.Name = "ChoiceMap";
        return row;
    }

    /// <summary>The map and rotation arguments for the server (Manufactorum, the default, sends none).</summary>
    (string? Map, string? Maps) ChosenMap() => _mapRow is null || Musou ? (null, null) : HostMapChoices.ServerOptions(_map);

    /// <summary>The running line's map text (null: the default map, no row).</summary>
    string? MapRunningText(HostOptions o) =>
        o.Maps is not null ? "rotation of all maps" : o.Map is { } m && _mapChoices.FirstOrDefault(c => c.Value == m).Label is { } label ? label.ToLowerInvariant() : null;
}
