using SmSim;

namespace SmNet;

/// <summary>An ordered count per key: the best key is the one with the most, the one seen first winning a tie.</summary>
public sealed class Tally<T> where T : notnull
{
    readonly List<(T Key, int Count)> _items = new();

    public IReadOnlyList<(T Key, int Count)> Items => _items;

    public void Add(T key)
    {
        int i = _items.FindIndex(x => EqualityComparer<T>.Default.Equals(x.Key, key));
        if (i < 0) _items.Add((key, 1)); else _items[i] = (key, _items[i].Count + 1);
    }

    public int Count(T key)
    {
        int i = _items.FindIndex(x => EqualityComparer<T>.Default.Equals(x.Key, key));
        return i < 0 ? 0 : _items[i].Count;
    }

    /// <summary>The key with the highest count (first seen on a tie), or null when empty.</summary>
    public (T Key, int Count)? Best
    {
        get
        {
            (T Key, int Count)? best = null;
            foreach (var item in _items) if (best is null || item.Count > best.Value.Count) best = item;
            return best;
        }
    }
}

/// <summary>What a client saw of one player's kills and deaths this match (<see cref="MatchStats"/>).</summary>
public sealed class PlayerRecord
{
    /// <summary>Kills of opponents, deaths of any cause, kills of one's own side (not counted as kills), suicides and falls (counted as deaths).</summary>
    public int Kills, Deaths, TeamKills, Suicides;
    /// <summary>Opponents killed in a row without dying, now and at best.</summary>
    public int Streak, BestStreak;
    /// <summary>Weapon names of this player's kills, the classes it played them in, the opponents it killed, and the opponents who killed it.</summary>
    public Tally<string> Weapons { get; } = new();
    public Tally<int> Classes { get; } = new();
    public Tally<int> Killed { get; } = new();
    public Tally<int> KilledBy { get; } = new();
}

/// <summary>
/// Per-player kill statistics built from the kill feed a client sees (fidelity gap 4, the summary screen; engine-free, ours). The server
/// sends every death to every client, so each player's kills, deaths, streaks, weapons and opponents can be followed without a new message;
/// what the feed does not carry (assists, score, who captured, defends, level) is not here. A client that joined mid-match only knows the
/// kills since it arrived, so the numbers the summary shows as totals (kills, deaths) come from the scoreboard and these only fill in the
/// rest.
/// </summary>
public sealed class MatchStats
{
    readonly Dictionary<int, PlayerRecord> _players = new();
    int _marineCaptures, _chaosCaptures;

    /// <summary>The record of a player (created empty when unseen).</summary>
    public PlayerRecord Of(int playerId)
    {
        if (!_players.TryGetValue(playerId, out var record)) _players[playerId] = record = new PlayerRecord();
        return record;
    }

    public bool Has(int playerId) => _players.ContainsKey(playerId);

    /// <summary>
    /// One death. <paramref name="killer"/> 0 or equal to the victim: a fall, a kill by the level or a suicide (a death, no kill, the streak ends).
    /// <paramref name="hostile"/> false: a kill of the killer's own side (a death for the victim, no kill for the killer, whose streak is left
    /// alone; the server does not count it either). <paramref name="killerClass"/> -1: unknown.
    /// </summary>
    public void RecordKill(int killer, int victim, bool hostile, string weapon, int killerClass)
    {
        var v = Of(victim);
        v.Deaths++;
        v.Streak = 0;
        if (killer <= 0 || killer == victim) { v.Suicides++; return; }
        var k = Of(killer);
        if (!hostile) { k.TeamKills++; return; }
        k.Kills++;
        k.Streak++;
        k.BestStreak = Math.Max(k.BestStreak, k.Streak);
        if (!string.IsNullOrEmpty(weapon)) k.Weapons.Add(weapon);
        if (killerClass >= 0) k.Classes.Add(killerClass);
        k.Killed.Add(victim);
        v.KilledBy.Add(killer);
    }

    /// <summary>A control point that <paramref name="side"/> completed (Seize Ground: the wire says which side holds a point, not who captured it).</summary>
    public void RecordCapture(Team side)
    {
        if (side == Team.SpaceMarines) _marineCaptures++;
        else if (side == Team.Chaos) _chaosCaptures++;
    }

    public int Captures(Team side) => side == Team.SpaceMarines ? _marineCaptures : side == Team.Chaos ? _chaosCaptures : 0;

    public void Reset()
    {
        _players.Clear();
        _marineCaptures = _chaosCaptures = 0;
    }
}

/// <summary>
/// The viewer's battle record, the rows of the original's summary (locale 106085 BEST CLASS, 106086 BEST WEAPON, 106087 ENEMY KILLS, 106088 PLAYER DEATHS,
/// 106089 KILL RATIO, 106090 TEAM ASSISTS, 106091 BEST KILLSTREAK, 106094 NEMESIS). Assists are not tracked (null).
/// </summary>
public sealed record BattleRecord(
    int EnemyKills, int Deaths, double KillRatio, int BestStreak,
    string? BestWeapon, int BestWeaponKills, int? BestClass, int BestClassKills,
    int? Nemesis, int NemesisKills, int KillsOfNemesis, int TeamCaptures)
{
    /// <summary>
    /// The record of <paramref name="playerId"/>. <paramref name="kills"/> and <paramref name="deaths"/>: the authoritative totals (the scoreboard's) when
    /// known, else what the feed showed. The ratio is kills per death, and the kills themselves when there was no death (ours; the original's formula
    /// is not in the data). The nemesis is the opponent who killed the player most (the first seen on a tie).
    /// </summary>
    public static BattleRecord For(MatchStats stats, int playerId, int? kills, int? deaths, Team side)
    {
        var r = stats.Of(playerId);
        int k = kills ?? r.Kills, d = deaths ?? r.Deaths;
        var weapon = r.Weapons.Best;
        var cls = r.Classes.Best;
        var nemesis = r.KilledBy.Best;
        return new BattleRecord(k, d, Ratio(k, d), r.BestStreak,
            weapon?.Key, weapon?.Count ?? 0, cls?.Key, cls?.Count ?? 0,
            nemesis?.Key, nemesis?.Count ?? 0, nemesis is { } n ? r.Killed.Count(n.Key) : 0,
            stats.Captures(side));
    }

    public static double Ratio(int kills, int deaths) => deaths <= 0 ? kills : kills / (double)deaths;
}

/// <summary>Where the end of a match is: the splash, the count-up, the summary, then the scoreboard until the restart.</summary>
public enum MatchEndPhase { Splash, CountUp, Summary, Scoreboard }

/// <summary>
/// The screens between the end of a match and the restart (fidelity gap 4; engine-free). The 6.5 s splash is the original's clip
/// (<see cref="MatchFlow.SplashSeconds"/>); the original's end-of-arena count-up and summary screen are movies whose lengths are set by
/// script timers (g_scoreStartDelay, g_scoreTimeDelay, g_scoreTimeInterval; the values are not in the movie's constant pools), so the split of the
/// remaining time is ours: the whole sequence has to fit the server's 15 s restart (<see cref="SmSim.MatchRules.RestartDelay"/>), leaving the scoreboard
/// with the "NEXT MAP" timer 2.5 s at least.
/// </summary>
public static class MatchEndTimeline
{
    public const double SplashEnd = MatchFlow.SplashSeconds;
    /// <summary>The count-up: two stages of <see cref="CountUp.StageSeconds"/>.</summary>
    public const double CountUpSeconds = CountUp.Stages * CountUp.StageSeconds;
    public const double SummarySeconds = 3.0;
    public const double CountUpEnd = SplashEnd + CountUpSeconds;
    public const double SummaryEnd = CountUpEnd + SummarySeconds;

    /// <summary>The phase at <paramref name="secondsSinceEnd"/> seconds after the end was seen (negative: the splash).</summary>
    public static MatchEndPhase PhaseAt(double secondsSinceEnd) =>
        secondsSinceEnd < SplashEnd ? MatchEndPhase.Splash
        : secondsSinceEnd < CountUpEnd ? MatchEndPhase.CountUp
        : secondsSinceEnd < SummaryEnd ? MatchEndPhase.Summary
        : MatchEndPhase.Scoreboard;

    /// <summary>Seconds into the phase of <paramref name="secondsSinceEnd"/>.</summary>
    public static double TimeInPhase(double secondsSinceEnd) => PhaseAt(secondsSinceEnd) switch
    {
        MatchEndPhase.Splash => Math.Max(0.0, secondsSinceEnd),
        MatchEndPhase.CountUp => secondsSinceEnd - SplashEnd,
        MatchEndPhase.Summary => secondsSinceEnd - CountUpEnd,
        _ => secondsSinceEnd - SummaryEnd,
    };

    /// <summary>Seconds the scoreboard has before a restart <paramref name="restartDelay"/> seconds after the end.</summary>
    public static double ScoreboardSeconds(double restartDelay) => restartDelay - SummaryEnd;
}

/// <summary>
/// The score count-up (fidelity gap 4; ours). The original's <c>EndOfArena.gfx</c> main block runs three stages on frame labels <c>teamscore</c> (0),
/// <c>playerscore</c> (25) and <c>xpearned</c> (51) of a 76-frame clip, each stopping until the script has counted its number up; without per-player
/// scores and experience here there are two stages: the team score, then the viewer's kills.
/// </summary>
public static class CountUp
{
    public const int Stages = 2;
    public const double StageSeconds = 1.5;
    /// <summary>Within a stage the panel eases in for this long, then the number counts for <see cref="CountSeconds"/>, then it holds (all ours).</summary>
    public const double IntroSeconds = 0.3, CountSeconds = 0.9;

    /// <summary>The stage (0-based, clamped) and the seconds into it at <paramref name="t"/> seconds from the start of the count-up.</summary>
    public static (int Stage, double Time) StageAt(double t)
    {
        t = Math.Max(0.0, t);
        int stage = Math.Min(Stages - 1, (int)(t / StageSeconds));
        return (stage, t - stage * StageSeconds);
    }

    /// <summary>0 to 1 over a stage's counting window, eased out (fast first, slow near the target).</summary>
    public static double Progress(double stageTime)
    {
        double x = Math.Clamp((stageTime - IntroSeconds) / CountSeconds, 0.0, 1.0);
        return 1.0 - Math.Pow(1.0 - x, 3.0);
    }

    /// <summary>The number shown while counting toward <paramref name="target"/> (whole numbers, reaching the target exactly at the end).</summary>
    public static int Value(int target, double stageTime) => target <= 0 ? Math.Max(0, target) : (int)Math.Round(target * Progress(stageTime));

    /// <summary>Alpha of a stage's panel (1 once in).</summary>
    public static double Intro(double stageTime) => Math.Clamp(stageTime / IntroSeconds, 0.0, 1.0);
}

/// <summary>Which players the count-up lists.</summary>
public static class CountUpRows
{
    /// <summary>
    /// Up to <paramref name="count"/> players of <paramref name="side"/> (all players when <paramref name="teams"/> is false), best first (kills, then fewer
    /// deaths, then id), always including <paramref name="me"/> when present (it replaces the last row). The original's block lists four rows
    /// (<c>player_1_mc</c> .. <c>player_4_mc</c>); which players is not in the movie (ours).
    /// </summary>
    public static List<ScoreEntry> Pick(IEnumerable<ScoreEntry> players, bool teams, Team side, int me, int count = 4)
    {
        var ranked = players.Where(e => !teams || e.Team == side)
            .OrderByDescending(e => e.Kills).ThenBy(e => e.Deaths).ThenBy(e => e.PlayerId).ToList();
        var rows = ranked.Take(count).ToList();
        int at = ranked.FindIndex(e => e.PlayerId == me);
        if (at >= count) rows[count - 1] = ranked[at];
        return rows;
    }
}
