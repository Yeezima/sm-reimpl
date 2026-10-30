namespace SmContent;

/// <summary>A reward the HUD shows on a reward line (ui/hud-2): its experience, the HUD movie's icon frame, and the name's locale id (with an English fallback for an install without the file).</summary>
public sealed record RewardSpec(string Key, string Name, int NameId, string Frame, int Experience);

/// <summary>One reward earned: <see cref="Spec"/> and, for a kill streak, the streak's length.</summary>
public sealed record RewardEarned(RewardSpec Spec, int Count = 0);

/// <summary>
/// The rewards of <c>xp_settings_pvp/rewards</c> (global attribute data) that a client can know from what is on the wire (ui/hud-2): the kill's own reward line, headshot, double to mega kill,
/// killstreak, revenge, avenger, grudge and victory in death. Values (experience, icon frame, name id) and criteria are the data's; the rest of the table (assists, captures, savior, variety,
/// long shot, death from below and above, reinforcement, round win, flag rewards) needs what the client does not receive (who damaged whom, who captured, distances, hit zones of others'
/// shots) and is not reproduced. The kill line's 50 is not in the table: measured on the reference footage.
/// </summary>
public static class RewardTable
{
    /// <summary><c>xp_settings_pvp/multi_kill_interval</c>: kills this close together (seconds) make a double, triple, quad or mega kill.</summary>
    public const double MultiKillInterval = 2.0;

    /// <summary>The avenger's window (<c>avenger</c>: <c>reward_criteria</c> 5 with <c>less_than_or_equal_to</c>: seconds since the opponent killed a teammate; the unit is <b>interpretation</b>).</summary>
    public const double AvengerWindow = 5.0;

    /// <summary>The grudge's kill count (<c>reward_criteria</c> 5, <c>equal_to</c>): the fifth kill of the same opponent in a round.</summary>
    public const int GrudgeKills = 5;

    public static readonly RewardSpec Kill = new("kill", "Kill", 121089, "mp_bonus_kill", 50);
    public static readonly RewardSpec Headshot = new("headshot", "Headshot", 105720, "mp_bonus_headshot", 35);
    /// <summary>
    /// A headshot kill's one line (owner, 2026-10-30): the headshot's icon and name instead of the plain kill line, with the experience of both (the data grants the kill's 50 and the headshot's 35;
    /// ours: shown as one line of 85).
    /// </summary>
    public static readonly RewardSpec HeadshotKill = new("headshot kill", "Headshot", 105720, "mp_bonus_headshot", Kill.Experience + Headshot.Experience);
    public static readonly RewardSpec DoubleKill = new("double kill", "Double Kill", 105730, "player_track_doublekill", 100);
    public static readonly RewardSpec TripleKill = new("triple kill", "Triple Kill", 105731, "mp_bonus_multikill", 300);
    public static readonly RewardSpec QuadKill = new("quad kill", "Quad Kill", 105732, "player_track_quadkill", 400);
    public static readonly RewardSpec MegaKill = new("mega kill", "Mega Kill", 105733, "player_track_megakill", 500);
    public static readonly RewardSpec Revenge = new("revenge", "Revenge", 105734, "player_track_revenge", 50);
    public static readonly RewardSpec Avenger = new("avenger", "Avenger", 105717, "mp_bonus_avenger", 25);
    public static readonly RewardSpec Grudge = new("grudge", "Grudge", 105719, "player_track_grudge", 25);
    public static readonly RewardSpec VictoryInDeath = new("victory in death", "Victory In Death", 121087, "mp_bonus_victory_in_death", 50);
    public static readonly RewardSpec MaxKillstreak = new("killmax", "Max Killstreak", 105729, "player_track_kill_max", 500);
    public static readonly RewardSpec KillstreakCap = new("killstreak cap", "Max Killstreak", 105729, "player_track_kill_max", 50);

    /// <summary>
    /// Experience of the kill streak's steps <c>killstreak x3</c> .. <c>x9</c> (50, 75, ... 200); the line shows the displayed entry's name "Killstreak" (<c>killstreak</c> is the only streak
    /// entry shown; <b>interpretation</b>: the step's own experience, the data's <c>cumulative</c> display type read as "per step").
    /// </summary>
    public static RewardSpec KillstreakStep(int n) => new("killstreak x" + n, "Killstreak", 121375, "mp_bonus_killstreak_" + n, 50 + 25 * (n - 3));

    /// <summary>Every spec the tracker can earn, the streak steps 3 to 9 included (for tests and for warming the names).</summary>
    public static IReadOnlyList<RewardSpec> All { get; } = BuildAll();

    static IReadOnlyList<RewardSpec> BuildAll()
    {
        var l = new List<RewardSpec> { Kill, Headshot, HeadshotKill, DoubleKill, TripleKill, QuadKill, MegaKill, Revenge, Avenger, Grudge, VictoryInDeath, MaxKillstreak, KillstreakCap };
        for (int n = 3; n <= 9; n++) l.Add(KillstreakStep(n));
        return l;
    }
}

/// <summary>
/// Works out the reward lines of the own player's kills from the kill feed (ui/hud-2; pure rules, the HUD feeds it). Everything it needs is on the wire: who killed whom (every client
/// sees every death), the own player's death, and (for the headshot) the own hit reports. The rules are the data's descriptions of <c>xp_settings_pvp/rewards</c>
/// (<see cref="RewardTable"/>): the order the lines are listed in and the streak's reset are ours.
/// </summary>
public sealed class RewardTracker
{
    string? _lastKiller;
    readonly Dictionary<string, double> _teamLossBy = new(StringComparer.Ordinal);
    readonly Dictionary<string, int> _killsOf = new(StringComparer.Ordinal);
    int _streak, _multi;
    double _lastKillAt = double.NegativeInfinity;

    /// <summary>The own kill streak: kills in a row since the last death.</summary>
    public int Streak => _streak;

    /// <summary>A new round or match: counts of grudges and streaks start afresh.</summary>
    public void Reset()
    {
        _lastKiller = null; _teamLossBy.Clear(); _killsOf.Clear(); _streak = 0; _multi = 0; _lastKillAt = double.NegativeInfinity;
    }

    /// <summary>The own player died (killed by <paramref name="killer"/>, null when unknown): the streak ends and the killer is the one to take revenge on.</summary>
    public void OwnDied(string? killer)
    {
        _streak = 0; _multi = 0;
        _lastKiller = string.IsNullOrEmpty(killer) ? null : killer;
    }

    /// <summary>An opponent killed a teammate (not the own player) at <paramref name="now"/>: killing that opponent soon after earns the avenger.</summary>
    public void TeammateKilledBy(string killer, double now)
    {
        if (!string.IsNullOrEmpty(killer)) _teamLossBy[killer] = now;
    }

    /// <summary>Someone died (any side): a killer who died before the own player could take revenge is no longer owed.</summary>
    public void Died(string victim)
    {
        if (_lastKiller == victim) _lastKiller = null;
    }

    /// <summary>
    /// The own player killed <paramref name="victim"/> at <paramref name="now"/>: the lines to show, the kill's own first. <paramref name="headshot"/>: the killing hit was on the head;
    /// <paramref name="ownAlive"/>: false for a kill made after the own death (a thrown grenade or a blast: victory in death).
    /// </summary>
    public IReadOnlyList<RewardEarned> OwnKill(string victim, double now, bool headshot, bool ownAlive)
    {
        var list = new List<RewardEarned> { new(headshot ? RewardTable.HeadshotKill : RewardTable.Kill) };
        if (_lastKiller == victim) { list.Add(new(RewardTable.Revenge)); _lastKiller = null; }
        if (_teamLossBy.TryGetValue(victim, out double at) && now - at <= RewardTable.AvengerWindow && now >= at) { list.Add(new(RewardTable.Avenger)); _teamLossBy.Remove(victim); }
        _killsOf[victim] = _killsOf.GetValueOrDefault(victim) + 1;
        if (_killsOf[victim] == RewardTable.GrudgeKills) list.Add(new(RewardTable.Grudge));
        if (!ownAlive) list.Add(new(RewardTable.VictoryInDeath));
        _multi = now - _lastKillAt <= RewardTable.MultiKillInterval ? _multi + 1 : 1;
        _lastKillAt = now;
        switch (_multi)
        {
            case 2: list.Add(new(RewardTable.DoubleKill)); break;
            case 3: list.Add(new(RewardTable.TripleKill)); break;
            case 4: list.Add(new(RewardTable.QuadKill)); break;
            case 5: list.Add(new(RewardTable.MegaKill)); break;
        }
        if (ownAlive) _streak++;
        if (_streak is >= 3 and <= 9) list.Add(new(RewardTable.KillstreakStep(_streak), _streak));
        else if (_streak == 10) list.Add(new(RewardTable.MaxKillstreak, _streak));
        else if (_streak > 10) list.Add(new(RewardTable.KillstreakCap, _streak));
        Died(victim);
        return list;
    }
}
