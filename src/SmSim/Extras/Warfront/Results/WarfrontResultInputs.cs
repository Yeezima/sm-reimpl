namespace SmSim.Extras.Warfront.Results;

/// <summary>
/// What W9's squad sim fills for one retainer at the end of a run (Warfront plan section 6 "service record"): a small input record of ours, so this slice depends on no Squad type.
/// <see cref="Kills"/> counts every kill; <see cref="EliteKills"/> and <see cref="BossKills"/> are subsets of it. <see cref="Knockouts"/> is how often the Marine was downed.
/// </summary>
public sealed record WarfrontRetainerStats(string Name, string ClassName = "", int Slot = 0, int Kills = 0, int EliteKills = 0, int BossKills = 0, int Assists = 0,
    int Knockouts = 0, int Revives = 0, int Captures = 0, int BattlesSurvived = 0);

/// <summary>One human's own figures. <see cref="JoinIndex"/> is the join order (0 first); the builder overwrites it from the campaign. Kills include the elite and boss subsets.</summary>
public sealed record WarfrontPlayerStats(string Name, bool Connected = true, int JoinIndex = 0, int Kills = 0, int EliteKills = 0, int BossKills = 0, int Assists = 0,
    int Deaths = 0, int Revives = 0, int Captures = 0);

/// <summary>A player and the squad that served them (the squad as it stands, fallen Marines included).</summary>
public sealed record WarfrontContributor(WarfrontPlayerStats Player, IReadOnlyList<WarfrontRetainerStats> Squad)
{
    public static WarfrontContributor Solo(WarfrontPlayerStats player) => new(player, Array.Empty<WarfrontRetainerStats>());
}
