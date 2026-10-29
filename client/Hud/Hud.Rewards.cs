using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Godot;
using SmContent;

namespace SmClient;

// ui/hud-2: the reward lines beyond "Kill". What the client can know of the original's reward table (xp_settings_pvp/rewards, RewardTable) it works out here from what the HUD is
// already told: every kill in the feed (names and sides), the own kills (AddEvent), the own hit reports (a killing head hit) and the own player's death (the frame data). The tracker's
// rules are in SmContent.RewardTracker; this part feeds it.
public sealed partial class Hud
{
    /// <summary>Own-side deaths by an opponent seen in the feed, waiting 0.5 s to be told apart: the own death (revenge) or a teammate's (avenger).</summary>
    readonly List<(string Killer, double At)> _pendingOwnSide = new();

    /// <summary>Deaths seen in the feed, waiting 0.3 s (the own kill's call comes after the feed's): whoever was owed revenge and died is no longer.</summary>
    readonly List<(string Victim, double At)> _pendingDied = new();

    double _ownDiedAt = double.NegativeInfinity;
    bool _rewardAliveKnown, _rewardAlive;
    int _rewardScore = -1;

    /// <summary>A kill the feed shows: an opponent's kill of the own side is remembered (the own player's death, or a teammate's), and every death is told to the tracker a moment later.</summary>
    void NoteKillForRewards(Kill k)
    {
        if (k.VictimRel == Rel.Own && k.KillerRel == Rel.Enemy && k.Killer.Length > 0) _pendingOwnSide.Add((k.Killer, _time));
        if (k.Victim.Length > 0) _pendingDied.Add((k.Victim, _time));
    }

    /// <summary>Per frame: the own death and score resets, and the settled pending feed entries.</summary>
    void TrackRewards()
    {
        var d = Data;
        if (d.InGame)
        {
            if (_rewardAliveKnown && _rewardAlive && !d.Alive) _ownDiedAt = _time;
            _rewardAliveKnown = true; _rewardAlive = d.Alive;
            // A new round or match (the scores start over): grudges and streaks start afresh.
            int score = d.OwnScore + d.EnemyScore;
            if (_rewardScore > score) { _rewards.Reset(); _pendingOwnSide.Clear(); _pendingDied.Clear(); }
            _rewardScore = score;
        }
        for (int i = _pendingOwnSide.Count - 1; i >= 0; i--)
        {
            var (killer, at) = _pendingOwnSide[i];
            if (_time - at < 0.5) continue;
            _pendingOwnSide.RemoveAt(i);
            if (Math.Abs(at - _ownDiedAt) <= 0.6) _rewards.OwnDied(killer); else _rewards.TeammateKilledBy(killer, at);
        }
        for (int i = _pendingDied.Count - 1; i >= 0; i--)
        {
            var (victim, at) = _pendingDied[i];
            if (_time - at < 0.3) continue;
            _pendingDied.RemoveAt(i);
            _rewards.Died(victim);
        }
    }

    // ---- showcase aid

    bool? _rewardsDemo;
    double _rewardsDemoAt = -1;
    int _rewardsDemoStep;

    /// <summary>
    /// <c>--hud-rewards-demo</c> (screenshot aid): from 3 s in game every 10 s a scripted run of own kills: a head-shot kill, then two kills within 2 s (double kill), a third (triple kill and the
    /// first of the killstreak steps follow with more kills): the reward lines they earn.
    /// </summary>
    void RunRewardsDemo()
    {
        _rewardsDemo ??= Array.IndexOf(OS.GetCmdlineUserArgs(), "--hud-rewards-demo") >= 0;
        if (_rewardsDemo != true || !Data.InGame) return;
        if (_rewardsDemoAt < 0) _rewardsDemoAt = _time + 3.0;
        if (_time < _rewardsDemoAt) return;
        double since = _time - _rewardsDemoAt;
        string[] victims = ["P4", "P5", "P6"];
        double[] at = [0.0, 0.8, 1.6];
        if (_rewardsDemoStep < victims.Length && since >= at[_rewardsDemoStep])
        {
            if (_rewardsDemoStep == 0) ShowHitMarker(true, true);
            AddEvent("+ Kill " + victims[_rewardsDemoStep]);
            _rewardsDemoStep++;
        }
        else if (since >= 10.0) { _rewardsDemoAt = _time + 3.0; _rewardsDemoStep = 0; _rewards.Reset(); }
    }

    // ---- the names (the install's locale, read once in the background; English from the code until then and without it)

    Task<Dictionary<int, string>>? _rewardNamesLoad;
    Dictionary<int, string>? _rewardNames;

    /// <summary>The reward's name in the install's English locale (<see cref="RewardSpec.NameId"/>), or the code's own when the locale is not read (yet).</summary>
    string RewardName(RewardSpec spec)
    {
        if (_rewardNames is null)
        {
            if (_rewardNamesLoad is null && GameDir is { } dir) _rewardNamesLoad = Task.Run(() => ReadRewardNames(dir));
            if (_rewardNamesLoad is { IsCompletedSuccessfully: true }) _rewardNames = _rewardNamesLoad.Result;
        }
        return _rewardNames is not null && _rewardNames.TryGetValue(spec.NameId, out var name) && name.Length > 0 ? name : spec.Name;
    }

    static Dictionary<int, string> ReadRewardNames(string gameDir)
    {
        var names = new Dictionary<int, string>();
        try
        {
            var locale = SmFormats.Locale.LocaleTable.Open(Path.Combine(gameDir, "data", "locale", "english", "ucs.pc"));
            foreach (var spec in RewardTable.All) if (locale.Get(spec.NameId) is { Length: > 0 } text) names[spec.NameId] = text;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { GD.PrintErr("reward names: locale not read (" + e.Message + "); the code's English names are used"); }
        return names;
    }
}
