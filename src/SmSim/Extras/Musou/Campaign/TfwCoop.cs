using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// Musou Mode co-op (the owner, 2026-11-06: "add support for 4 players to host on a locally hosted server that works the same way as the locally hosted server of classic pvp mode";
/// kind: ours). Up to <see cref="MaxHumans"/> humans play one run together as one side (Space Marines) against the Orks: the server admits no more players than that under Musou
/// (<see cref="PlayerCap"/>), a Deploy takes everyone, each lands at its own place by the landing zone (<see cref="LandingOffset"/>) (the two allied Marines land once, whatever the number of humans), and a human who joins while a
/// battle is under way is flown in at the landing zone (<see cref="TfwRun"/>'s late join). The rest was already the team's: the fallen wait while another fights, the Ork cap grows with each
/// human (<see cref="TfwBattleRules.AliveCap"/>), scores, drops and reward cards are per human, the Stormbird has four spawn places (<see cref="TfwShip.Spawns"/>).
/// </summary>
public static class TfwCoop
{
    /// <summary>Most humans in one Musou match (the owner's 4; the Stormbird's four spawn places, <see cref="TfwShip.Spawns"/>). Spectators are not counted.</summary>
    public const int MaxHumans = 4;

    /// <summary>The player cap of a match of <paramref name="extras"/> on a server that admits <paramref name="cap"/>: Musou lowers it to <see cref="MaxHumans"/>; any other profile keeps it.</summary>
    public static int PlayerCap(ContentExtras extras, int cap) => extras.HasFlag(ContentExtras.Musou) ? Math.Min(cap, MaxHumans) : cap;

    /// <summary>
    /// Where the <paramref name="k"/>-th human (0-based, in id order; a late joiner takes the next) stands by the landing zone, in metres: two abreast, 3 m apart, each pair 3 m behind
    /// the one before (the landing's own layout since TF4, kept for every human).
    /// </summary>
    public static Vector3 LandingOffset(int k) => new(k % 2 * 3f - 1.5f, k / 2 * -3f, 0f);
}
