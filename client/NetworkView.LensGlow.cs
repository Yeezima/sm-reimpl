using System.Collections.Generic;
using Godot;

namespace SmClient;

/// <summary>
/// fx/character-visuals (owner, 2026-10-30, item 17; client only): the eye glow of every Space Marine and Chaos character in the colour of the look's lens (<see cref="LensGlow"/>); the
/// own character's too (seen when the camera is back), the Ork's none. Called once per frame from <see cref="UpdateFx"/>. Test aid <c>--no-lens-glow</c> (before / after).
/// </summary>
public partial class NetworkView
{
    readonly Dictionary<int, LensGlow> _lensGlows = new();
    readonly HashSet<int> _lensSeen = new();

    void LensGlowFrame()
    {
        if (_client is null || _config is null || LensGlow.Off || _client.Status != SmNet.ClientStatus.Connected) return;
        _lensSeen.Clear();
        LensGlowFor(_client.PlayerId, _local, _client.Predicted.Alive, _client.Predicted.Team == SmSim.Team.Chaos);
        foreach (var state in _lastOthers) LensGlowFor(state.PlayerId, _remotes.GetValueOrDefault(state.PlayerId), state.Alive, state.Team == SmSim.Team.Chaos);
        if (_lensGlows.Count > _lensSeen.Count)
            foreach (var id in new List<int>(_lensGlows.Keys)) if (!_lensSeen.Contains(id)) { _lensGlows[id].Hide(); _lensGlows.Remove(id); }
    }

    void LensGlowFor(int id, CharacterViewer? character, bool alive, bool chaos)
    {
        if (character is null || id <= 0 || IsOrkCharacter(character)) return;
        _lensSeen.Add(id);
        if (!_lensGlows.TryGetValue(id, out var glow)) _lensGlows[id] = glow = new LensGlow();
        var lens = _playerLooks.TryGetValue(id, out var look) ? look.Entry.Look.Lens : null;
        glow.Update(character, LensGlow.ColourFor(lens is { } l ? (l.R, l.G, l.B) : null, chaos), alive && character.Visible);
    }
}
