using SmSim.Extras.Musou;

namespace SmSim;

/// <summary>
/// Added content "Musou": the players' loadout in the World (owner, 2026-10-31; <see cref="MusouLoadout"/>). Every life starts with the class's pistol
/// alone: a player alive and not yet armed this life is stripped at the end of the tick's match step (<see cref="MusouArm"/>, from <c>ExtrasStepMatch</c>,
/// so a death-timer respawn, a round's respawn and a join are all armed before the next tick's moves), again at the top of the Musou step, and at once for a
/// join (<see cref="MusouJoined"/>: the server calls it when a player joins between ticks, so no tick runs with the class loadout). A weapon given later
/// (<see cref="MusouGiveWeapon"/>, <see cref="MusouGiveItem"/>: the Nobs' drops) stays for the rest of that life; a player not armed yet is stripped first.
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
    public bool MusouGiveWeapon(int playerId, int weapon) =>
        weapon is >= 0 and < MusouDropItem.MeleeFlag && MusouGiveItem(playerId, (ushort)weapon);

    /// <summary>
    /// Gives player <paramref name="playerId"/> drop item <paramref name="item"/> (<see cref="MusouDropItem"/>: a weapon, or a melee weapon of its class) in
    /// a Musou match (<see cref="MusouLoadout.GiveItem"/>). A player alive and not armed yet this life is stripped first (the pistol alone), so the gift
    /// is never added to the class loadout. False, and nothing changed, when it cannot be given.
    /// </summary>
    public bool MusouGiveItem(int playerId, ushort item)
    {
        if (_musou is null || !_players.TryGetValue(playerId, out var p) || !p.Alive) return false;
        var from = !_musouArmed.Contains(playerId) && MusouLoadout.Prepared(Config) ? MusouLoadout.Stripped(Config, p) : p;
        if (MusouLoadout.GiveItem(Config, from, item) is not { } given) return false;
        _players[playerId] = given;
        _musouArmed.Add(playerId);
        return true;
    }

    /// <summary>
    /// A player who joined a Musou match between ticks (the server, as it joins): stripped at once to the pistol alone, so it never moves or fires a tick
    /// with its class loadout (a fresh join: armed whatever an earlier player of the id had). Nothing outside Musou or for a player not alive.
    /// </summary>
    public void MusouJoined(int playerId)
    {
        if (_musou is null || !MusouLoadout.Prepared(Config) || !_players.TryGetValue(playerId, out var p) || !p.Alive) return;
        _musouArmed.Add(playerId);
        _players[playerId] = MusouLoadout.Stripped(Config, p);
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
