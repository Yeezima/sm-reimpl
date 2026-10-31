using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the players' loadout in the World (owner, 2026-10-31; <see cref="MusouLoadout"/>). Every life starts with the class's pistol
/// alone: a player alive and not yet armed this life is stripped at the end of the tick's match step (<see cref="MusouArm"/>, from <c>ExtrasStepMatch</c>,
/// so a death-timer respawn, a round's respawn and a join are all armed before the next tick's moves; a join between ticks keeps its class loadout for the
/// one tick before). A weapon given later (<see cref="MusouGiveWeapon"/>, the Nobs' drops) stays for the rest of that life.
/// </summary>
public sealed partial class World
{
    readonly HashSet<int> _musouArmed = new();
    readonly List<int> _musouArmIds = new();
    Predicate<int>? _musouGone;

    /// <summary>
    /// Gives player <paramref name="playerId"/> weapon <paramref name="weapon"/> (an index into <see cref="SimConfig.Weapons"/>) in a Musou match, in hand
    /// with a full clip and reserve (<see cref="MusouLoadout.Give"/>: a primary its class may carry, else a pistol its class may carry). False, and nothing
    /// changed, outside Musou, for a player not alive or a weapon the class carries in neither slot.
    /// </summary>
    public bool MusouGiveWeapon(int playerId, int weapon)
    {
        if (_musou is null || !_players.TryGetValue(playerId, out var p) || MusouLoadout.Give(Config, p, weapon) is not { } given) return false;
        _players[playerId] = given;
        _musouArmed.Add(playerId);
        return true;
    }

    /// <summary>Strips every player alive and not yet armed this life (in id order); the dead are forgotten so their next life is armed again.</summary>
    void MusouArm()
    {
        if (!MusouLoadout.Prepared(Config)) return;   // a config the match tuning did not prepare: the core gives the class default primary anyway
        _musouArmIds.Clear();
        foreach (int id in _players.Keys) _musouArmIds.Add(id);
        _musouArmIds.Sort();
        foreach (int id in _musouArmIds)
        {
            var p = _players[id];
            if (!p.Alive) { _musouArmed.Remove(id); continue; }
            if (_musouArmed.Add(id)) _players[id] = MusouLoadout.Stripped(Config, p);
        }
        _musouArmed.RemoveWhere(_musouGone ??= id => !_players.ContainsKey(id));   // a leaver's id that joins again is armed again
    }

    /// <summary>A round's respawn (the core's StartRound gives the full class loadout): every player is armed afresh.</summary>
    void MusouRearm() => _musouArmed.Clear();
}
