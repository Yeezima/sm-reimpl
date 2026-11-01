using System.Numerics;
using SmSim.Extras.Musou;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The Firing Range's run state (the owner, 2026-11-02): from the Stormbird a player presses the range's button (<see cref="TfwCommand.RangeGo"/>) and is moved to <see cref="TfwRange"/>; while
/// anybody is there the enemies are topped up to <see cref="RangeOrks"/> (Musou's own spawning, <see cref="World.TfwRangeTopUp"/>), the fire support runs, and <see cref="TfwCommand.RangeBack"/> brings
/// the player back to the Stormbird. The war itself (phase, reserves) is untouched: the range is a hub-phase side trip. A Deploy ends it (the field is cleared); the last one to leave or disconnect ends it too.
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>Enemies the range keeps alive, the most spawned in a tick, and the seconds a fallen player waits before the range stands them up again (ours, spec "The Firing Range").</summary>
    public const int RangeOrks = 200, RangeReleasesPerTick = 6;
    public const float RangeRespawnSeconds = 3f;

    readonly List<int> _range = new();
    readonly Dictionary<int, int> _rangeDown = new();

    /// <summary>Whether anybody is at the range, and who.</summary>
    public bool RangeOn => _range.Count > 0;
    public IReadOnlyList<int> InRange => _range;

    void RangeGo(World w, int player)
    {
        if (Field.Range is not { } range || Phase != TfwRunPhase.Hub || _range.Contains(player)) return;
        if (TfwContracts.IsPuppet(player) || !w.Players.TryGetValue(player, out var p) || !p.Alive || !Field.Hub.Contains(p.Position)) return;
        var spawn = range.Spawn;
        w.TfwRespawnAt(player, spawn.Position + new Vector3(_range.Count % 3 * 2f - 2f, 0f, 0f), spawn.Yaw);
        _range.Add(player);
    }

    void RangeBack(World w, int player)
    {
        if (!_range.Remove(player)) return;
        _rangeDown.Remove(player);
        var spawn = Field.Hub.Spawns[player % Field.Hub.Spawns.Count];
        if (w.Players.ContainsKey(player)) w.TfwRespawnAt(player, spawn.Position, spawn.Yaw);
        if (_range.Count == 0) w.TfwClearField();
    }

    /// <summary>The range ends: whoever is there goes back to the Stormbird (a Deploy, which then takes everybody, does not wait for this), the field is cleared.</summary>
    void RangeEnd(World w)
    {
        if (_range.Count == 0) return;
        foreach (int id in _range.ToArray()) RangeBack(w, id);
    }

    /// <summary>One tick: players gone are dropped, the fallen stand up again at the range after a wait, the enemies are topped up.</summary>
    internal void RangeStep(World w, int rate)
    {
        if (_range.Count == 0) return;
        if (Phase != TfwRunPhase.Hub) { RangeEnd(w); return; }
        _range.RemoveAll(id => !w.Players.ContainsKey(id));
        if (_range.Count == 0) { w.TfwClearField(); return; }
        var spawn = Field.Range!.Spawn;
        foreach (int id in _range)
        {
            if (w.Players[id].Alive) { _rangeDown.Remove(id); continue; }
            w.TfwHoldRespawn(id);
            _rangeDown.TryGetValue(id, out int ticks);
            if (++ticks >= (int)(RangeRespawnSeconds * rate)) { _rangeDown.Remove(id); w.TfwRespawnAt(id, spawn.Position, spawn.Yaw); }
            else _rangeDown[id] = ticks;
        }
        w.TfwRangeTopUp(Field.Range!, _range, RangeOrks, RangeReleasesPerTick);
    }
}
