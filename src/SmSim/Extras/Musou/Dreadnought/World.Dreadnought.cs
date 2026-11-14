using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the Dreadnought in the World (<see cref="DreadnoughtRules"/>; D1). Each Musou step (<see cref="DreadnoughtStep"/>) gives every living Dreadnought an entrance when he has none (he spawned, respawned or
/// took the class) and counts it down; while it runs everything he receives is multiplied by 0 (<see cref="DreadnoughtDamageTaken"/>, from <c>ExtrasDamageTaken</c>). Nothing of it is in the core snapshot. The entrance's
/// movement lock (4.833 s: he cannot move or fire while the clip plays, a guess) and its wire are D2's, with the clip.
/// </summary>
public sealed partial class World
{
    /// <summary>The time left of a living Dreadnought's entrance (seconds; 0: over) by player id; a dead one or one of another class has none.</summary>
    readonly Dictionary<int, float> _dreadnoughtEntry = new();
    readonly List<int> _dreadnoughtGone = new();

    /// <summary>How much of the entrance's invulnerability player <paramref name="id"/> has left (0: none, or not a living Dreadnought).</summary>
    public float DreadnoughtEntryLeft(int id) => _dreadnoughtEntry.GetValueOrDefault(id);

    /// <summary>The capsule radius a player's body has beyond a marine's (a big body); 0 for every other class and without class bodies. The Orks' ring, reach and hold use it (<see cref="MusouTarget.Body"/>).</summary>
    float DreadnoughtBodyRing(in PlayerState p) =>
        Config.Bodies.Count == 0 ? 0f : MathF.Max(0f, Config.For(p.Class).CapsuleRadius - Config.CapsuleRadius);

    void DreadnoughtStep()
    {
        float dt = 1f / Math.Max(1, Config.TickRate);
        _dreadnoughtGone.Clear();
        foreach (var id in _dreadnoughtEntry.Keys)
            if (!_players.TryGetValue(id, out var q) || !q.Alive || !DreadnoughtRules.IsDreadnought(Config, q.Class)) _dreadnoughtGone.Add(id);   // dead, left or changed class: the next spawn is a new entrance
        foreach (int id in _dreadnoughtGone) _dreadnoughtEntry.Remove(id);
        foreach (var (id, p) in _players)
        {
            if (!p.Alive || !DreadnoughtRules.IsDreadnought(Config, p.Class)) continue;
            if (!_dreadnoughtEntry.TryGetValue(id, out float left)) { _dreadnoughtEntry[id] = DreadnoughtRules.InvulnerableSeconds; continue; }   // enters this tick: the whole 5 s are still ahead
            if (left > 0f) _dreadnoughtEntry[id] = MathF.Max(0f, left - dt);
        }
    }

    /// <summary>Everything a Dreadnought receives is multiplied by 0 while his entrance runs (data <c>evt_spawn_in</c>); a Dreadnought with no entry yet (just spawned, before the step) is still entering.</summary>
    void DreadnoughtDamageTaken(int target, ref float scale)
    {
        if (!_players.TryGetValue(target, out var p) || !DreadnoughtRules.IsDreadnought(Config, p.Class)) return;
        if (!_dreadnoughtEntry.TryGetValue(target, out float left) || left > 0f) scale = 0f;
    }
}
