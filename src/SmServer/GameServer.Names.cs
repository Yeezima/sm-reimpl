using SmNet;
using SmSim;

namespace SmServer;

public sealed partial record ServerOptions
{
    /// <summary>
    /// Display names (names/bot-lore, protocol 41): the seed of the bots' name choice (<see cref="PlayerNames.Pick"/>, with each race's join order); the
    /// same seed and join order give the same names. Null (default): a random seed when the server starts (<see cref="GameServer.NameSeed"/>,
    /// printed by the console server; <c>--name-seed</c> fixes it).
    /// </summary>
    public uint? NameSeed { get; init; }
}

/// <summary>
/// Display names (names/bot-lore, protocol 41; ours): a test bot (its Hello's <see cref="Hello.BotFlag"/>) gets a name from its race's pool when it joins,
/// unique among the connected players; a bot whose race changes (a class change to or from the Ork Boy at a respawn) gets a name of its new race when
/// the next scoreboard is built. Humans have no name source yet (no profile name): they keep "P&lt;id&gt;" (an empty name on the wire);
/// <see cref="SetDisplayName"/> is the place a future name source sets one. Names travel in the scoreboard rows.
/// </summary>
public sealed partial class GameServer
{
    uint? _nameSeed;
    /// <summary>Names given so far per race (the join order that, with the seed, picks the next one).</summary>
    readonly Dictionary<Race, int> _namesGiven = new();

    /// <summary>The seed of the bots' names (<see cref="ServerOptions.NameSeed"/>, else chosen at random once).</summary>
    public uint NameSeed => _nameSeed ??= _options.NameSeed ?? BitConverter.ToUInt32(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    /// <summary>The display name of a connected player (sanitised), or null when it has none (shown as "P&lt;id&gt;").</summary>
    public string? DisplayNameOf(int playerId) => PeerOf(playerId)?.Name is { Length: > 0 } name ? name : null;

    /// <summary>"player 3 (Brother Aurex)" or "player 3": how the server's log names a player.</summary>
    public string Describe(int playerId) => DisplayNameOf(playerId) is { } name ? $"player {playerId} ({name})" : $"player {playerId}";

    /// <summary>
    /// Sets (or with null or empty clears) a connected player's name after <see cref="PlayerNames.Sanitise"/>; refused (false) when another connected
    /// player has it (compared without case) or the player is unknown. For a future name source (a profile name); bots are named by the server.
    /// </summary>
    public bool SetDisplayName(int playerId, string? name)
    {
        if (PeerOf(playerId) is not { } peer) return false;
        string clean = PlayerNames.Sanitise(name);
        if (clean.Length > 0 && NameTaken(clean, except: peer)) return false;
        peer.Name = clean;
        peer.NameRace = Race.None;   // not a pool name: never renamed by race
        return true;
    }

    bool NameTaken(string name, Peer? except) =>
        _peers.Values.Any(p => !ReferenceEquals(p, except) && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A bot's name from its race's pool, unique among the connected players (the join and a race change).</summary>
    void NameBot(Peer peer)
    {
        var race = peer.Appearance.Race is Race.None ? Race.SpaceMarines : peer.Appearance.Race;
        int order = _namesGiven.GetValueOrDefault(race);
        _namesGiven[race] = order + 1;
        string old = peer.Name;
        peer.Name = PlayerNames.Pick(race, NameSeed, order, name => NameTaken(name, except: peer));
        peer.NameRace = race;
        Log?.Invoke(old.Length == 0
            ? $"player {peer.PlayerId} is a test bot: named \"{peer.Name}\" ({race})"
            : $"player {peer.PlayerId} ({old}) now plays {race}: renamed \"{peer.Name}\"");
    }

    /// <summary>Before a scoreboard: a bot whose race changed since it was named takes a name of the new race.</summary>
    void RefreshBotNames()
    {
        foreach (var peer in _peers.Values)
            if (peer.Bot && peer.NameRace != Race.None && peer.Appearance.Race is not Race.None && peer.Appearance.Race != peer.NameRace) NameBot(peer);
    }
}
