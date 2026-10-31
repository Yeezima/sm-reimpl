using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using SmNet;
using SmSim;

namespace SmClient;

/// <summary>
/// Match flow in the networked view (ours; spec/behavior.md "Match flow"): the "MATCH BEGINS" countdown, the victory / defeat / draw
/// splash and the "NEXT MAP" timer, drawn by <see cref="MatchFlowView"/> above the HUD, with the original's sounds: the riser
/// <c>play_mp_countdown</c> (a 2.6 s swell peaking 1.15 s in, started so that the peak falls on the start; start time ours) and the music
/// stingers <c>play_mp_sm_victory</c> / <c>_sm_loss</c> / <c>_chaos_victory</c> / <c>_chaos_loss</c> (music segments of the in-game bank)
/// at the end. The countdown is the server's (<see cref="MatchState.Countdown"/>, carried as warm-up with ticks left); the splash runs
/// <see cref="MatchFlow.SplashSeconds"/> from the moment the end is seen. Then (fidelity gap 4, the rest) the score count-up, the battle record and
/// the scoreboard follow in turn (<see cref="MatchEndTimeline"/>: 6.5 s splash, 3 s count-up, 3 s record, the scoreboard for the rest of the 15 s
/// restart; <see cref="MatchSummaryView"/>, statistics from the kill feed in <see cref="MatchStats"/>). Lines in the log mark each step.
/// </summary>
public partial class NetworkView
{
    /// <summary>Seconds before the start at which the riser begins, so that its peak (1.15 s into the sound) falls on the start (ours).</summary>
    const double CountdownRiserLead = 1.15;

    MatchFlowView? _flowView;
    MatchFlowText _flowText = new();
    MatchState _flowPrev = MatchState.None;
    bool _flowRiser;
    int _flowShown = -1;
    double _splashStart = -1;
    /// <summary>Seconds of the restart left when the end was seen: the end screens run on the server's restart clock (re-synced by every snapshot), so they stay in step with the NEXT MAP timer even when this client's frames stall.</summary>
    double _endRestartTotal;
    MatchResult _splashResult;
    Team _splashWinner;
    SoundEmitter? _matchEmitter;

    // The end-of-match screens after the splash (fidelity gap 4).
    MatchSummaryView? _summaryView;
    MatchSummaryText _summaryText = new();
    readonly MatchStats _stats = new();
    MatchState _statsState = MatchState.None;
    bool _pointsSoundOn;
    double _endSinceMax;
    Team[] _pointOwners = [];
    MatchEndPhase _endPhase = MatchEndPhase.Splash;
    CountUpData? _countUpData;
    SummaryData? _summaryData;

    /// <summary>Adds the match flow's view on the HUD layer (after the HUD, so it draws above it).</summary>
    void AddMatchFlowUi(CanvasLayer layer)
    {
        _flowText = MatchFlowText.Load(_gameDir);
        _summaryText = MatchSummaryText.Load(_gameDir);
        _summaryView = new MatchSummaryView();
        _summaryView.SetText(_summaryText);
        layer.AddChild(_summaryView);   // under the flow view: the NEXT MAP line and the splash draw over it
        _flowView = new MatchFlowView();
        _flowView.SetText(_flowText);
        layer.AddChild(_flowView);
    }

    /// <summary>Seconds since the end was seen (when it was seen after play), else -1.</summary>
    double SecondsSinceEnd
    {
        get
        {
            if (_splashStart < 0 || _client is null || _config is null) return -1.0;
            // Monotonic within one match end (a late snapshot may carry an older ticks-left value).
            return _endSinceMax = Math.Max(_endSinceMax, Math.Max(0.0, _endRestartTotal - SecondsLeft(_client.Match)));
        }
    }

    /// <summary>The splash is playing: the other screens wait for it.</summary>
    bool SplashActive => _splashStart >= 0 && MatchEndTimeline.PhaseAt(SecondsSinceEnd) == MatchEndPhase.Splash;

    /// <summary>The end's scoreboard is up: after the count-up and the battle record, or at once when the end was not seen from play (a late join).</summary>
    bool EndBoardUp => _splashStart < 0 || MatchEndTimeline.PhaseAt(SecondsSinceEnd) == MatchEndPhase.Scoreboard;

    void MatchFlowLog(string text) => GD.Print($"t {_connectedFor:F1} s: id {_client?.PlayerId} match flow: {text}");

    /// <summary>Per frame, after the match block is noted: steps (log, sounds) on state changes, then the frame to draw.</summary>
    void UpdateMatchFlow(MatchView match)
    {
        if (_client is null || _config is null || _flowView is null) return;
        if (!MatchFlowGate.HasMatchState(match))
        {
            // fix/connect-timeout: no snapshot with a match state yet (or a server without match rules): no countdown, splash or NEXT MAP.
            _flowView.SetFrame(MatchFlowFrame.None);
            _summaryView?.SetScreen(null, null, 0);
            return;
        }
        var state = match.Effective();
        if (state != _flowPrev)
        {
            var before = _flowPrev;
            _flowPrev = state;
            switch (state)
            {
                case MatchState.Countdown:
                    _flowRiser = false; _flowShown = -1; _splashStart = -1;
                    MatchFlowLog($"countdown begins, {SecondsLeft(match):F1} s");
                    break;
                case MatchState.Playing when before == MatchState.Countdown:
                    MatchFlowLog("match begins (the countdown ran out; attacks work)");
                    break;
                case MatchState.Warmup when before == MatchState.Countdown:
                    MatchFlowLog("countdown cancelled (a side emptied): back to warm-up");
                    PostMatchSound("stop_mp_countdown");
                    break;
                case MatchState.Ended when before == MatchState.Playing:
                    StartSplash(match);
                    break;
                case MatchState.Ended:
                    break;
                default:
                    if (before == MatchState.Ended)
                    {
                        _splashStart = -1; _countUpData = null; _summaryData = null; _endPhase = MatchEndPhase.Splash; StopPointsSound();
                        MatchFlowLog($"next round: {state}");
                    }
                    break;
            }
        }

        double countdown = -1, splash = -1, nextMap = -1;
        if (state == MatchState.Countdown)
        {
            countdown = Math.Max(0.0, SecondsLeft(match));
            int shown = MatchFlow.DisplaySeconds(countdown);
            if (shown != _flowShown) { _flowShown = shown; if (_bot) MatchFlowLog($"countdown shows {shown}"); }
            if (!_flowRiser && countdown <= CountdownRiserLead)
            {
                _flowRiser = true;
                PostMatchSound("play_mp_countdown", once: true);   // sound-hunt: an endless container; once = the swell plays through, no chain that loops for the whole match
            }
        }
        else if (state == MatchState.Ended)
        {
            if (SplashActive) splash = SecondsSinceEnd;
            else nextMap = Math.Max(0.0, SecondsLeft(match));
        }
        _flowView.SetFrame(new MatchFlowFrame(countdown, splash, _splashResult, _client.Predicted.Team, _splashWinner, nextMap));
        UpdateEndScreens(state);
    }

    void StartSplash(MatchView match)
    {
        if (_client is null) return;
        var own = _client.Predicted.Team;
        _splashWinner = match.Winner;
        _splashResult = MatchFlow.ResultFor(own, match.Winner);
        _splashStart = _connectedFor;
        _endRestartTotal = SecondsLeft(match);
        _endSinceMax = 0;
        _endPhase = MatchEndPhase.Splash;
        MatchFlowLog($"match over: {_splashResult} for {SideName(own)} (winner {(match.Winner == Team.None ? "none" : SideName(match.Winner))}, " +
                     $"marines {match.MarineScore} - chaos {match.ChaosScore}); splash {MatchFlow.SplashSeconds:F1} s, then the count-up ({MatchEndTimeline.CountUpSeconds:F1} s), the battle record ({MatchEndTimeline.SummarySeconds:F1} s) and the scoreboard");
        if (MatchFlow.StingerFor(own, _splashResult) is { } stinger) PostMatchSound(stinger);
    }

    // ------------------------------------------------------------------ the end-of-match screens (count-up, battle record)

    /// <summary>
    /// A death from the feed, recorded for the battle record (called once per kill by <c>UpdateMatchUi</c>). The sides come from what the client knows now;
    /// without teams (free-for-all) every kill is hostile, as on the server.
    /// </summary>
    void NoteKillForStats(KillView k, MatchState state)
    {
        bool hostile = state == MatchState.None || World.Hostile(TeamOf(k.Killer), TeamOf(k.Victim));
        _stats.RecordKill(k.Killer, k.Victim, hostile, k.Cause == KillCause.World ? "" : CauseLabel(k), ClassOf(k.Killer) ?? -1);
    }

    /// <summary>
    /// Seize Ground captures seen: a control point's owner changing to a side during play (the wire carries who holds a point, not who captured it, so
    /// the record counts the side's). The first sight of the points only remembers them.
    /// </summary>
    void NoteCapturesForStats(MatchState state)
    {
        if (_client is null) return;
        var points = _client.Points;
        if (_pointOwners.Length != points.Count)
        {
            _pointOwners = new Team[points.Count];
            for (int i = 0; i < points.Count; i++) _pointOwners[i] = points[i].Owner;
            return;
        }
        for (int i = 0; i < points.Count; i++)
        {
            var owner = points[i].Owner;
            if (owner == _pointOwners[i]) continue;
            _pointOwners[i] = owner;
            if (state == MatchState.Playing && owner is Team.SpaceMarines or Team.Chaos) _stats.RecordCapture(owner);
        }
    }

    /// <summary>
    /// The statistics start afresh with each round: forgotten when the ended match is left and when a round begins (the countdown, or play starting
    /// without one), so a warm-up or a late round start never carries kills over.
    /// </summary>
    void RoundStatsGate(MatchState state)
    {
        var before = _statsState;
        _statsState = state;
        if (state == before) return;
        if (before == MatchState.Ended || state == MatchState.Countdown || (state == MatchState.Playing && before != MatchState.Countdown))
            _stats.Reset();
    }

    /// <summary>Stops the tally sound if this screen sequence started it (once).</summary>
    void StopPointsSound()
    {
        if (!_pointsSoundOn) return;
        _pointsSoundOn = false;
        PostMatchSound("stop_mp_points_total");
    }

    /// <summary>
    /// Per frame while a match has ended: the count-up, the battle record and the scoreboard in turn (none during the splash, and the view is hidden while Tab is
    /// held). The phase only moves forward within one match end, so a late or reordered snapshot cannot start the count-up (or its sound) again; the sound is
    /// stopped when the record or the scoreboard begins and when the ended match is left, even if Tab hid the screens.
    /// </summary>
    void UpdateEndScreens(MatchState state)
    {
        if (_summaryView is null || _client is null) return;
        if (state != MatchState.Ended || _splashStart < 0)
        {
            _summaryView.SetScreen(null, null, 0);
            if (state != MatchState.Ended) { StopPointsSound(); _endPhase = MatchEndPhase.Splash; }
            return;
        }
        bool tab = !_bot && ControlsInput.Held(GameAction.Scoreboard) || PadScoreboardHeld;
        double since = SecondsSinceEnd;
        var phase = MatchEndTimeline.PhaseAt(since);
        double inPhase = MatchEndTimeline.TimeInPhase(since);
        if (phase > _endPhase)
        {
            _endPhase = phase;
            switch (phase)
            {
                case MatchEndPhase.CountUp:
                    _countUpData = BuildCountUp();
                    MatchFlowLog($"score count-up begins (team score {_countUpData?.TeamScore}, own kills {_countUpData?.ViewerKills}, {_countUpData?.Rows.Count} rows; next map in {SecondsLeft(_client.Match):F1} s)");
                    _pointsSoundOn = true;
                    PostMatchSound("play_mp_points_total");   // the movie posts it for the tally (EndOfArena.gfx: Play_MP_Points_Total); where it stops is ours
                    break;
                case MatchEndPhase.Summary:
                    _summaryData = BuildSummary();
                    MatchFlowLog("battle record: " + string.Join("; ", _summaryData.Lines.Select(l => $"{l.Label} {l.Value ?? "-"}")));
                    StopPointsSound();
                    break;
                case MatchEndPhase.Scoreboard:
                    MatchFlowLog($"scoreboard (the rest of the restart; next map in {SecondsLeft(_client.Match):F1} s)");
                    StopPointsSound();
                    break;
            }
        }
        if (tab) { _summaryView.SetScreen(null, null, 0); return; }
        switch (phase)
        {
            case MatchEndPhase.CountUp: _summaryView.SetScreen(_countUpData ??= BuildCountUp(), null, inPhase); break;
            case MatchEndPhase.Summary: _summaryView.SetScreen(null, _summaryData ??= BuildSummary(), inPhase); break;
            default: _summaryView.SetScreen(null, null, 0); break;
        }
    }

    ScoreEntry? OwnScoreEntry() => _client?.Scoreboard is { } b ? b.Players.Where(e => e.PlayerId == _client.PlayerId).Select(e => (ScoreEntry?)e).FirstOrDefault() : null;

    CountUpData BuildCountUp()
    {
        var match = _client!.Match;
        var board = _client.Scoreboard;
        var own = _client.Predicted.Team;
        bool teams = board?.Teams ?? own != Team.None;
        var side = own == Team.None ? Team.SpaceMarines : own;
        int teamScore = side == Team.Chaos ? match.ChaosScore : match.MarineScore;
        int otherScore = side == Team.Chaos ? match.MarineScore : match.ChaosScore;
        string? otherLabel = teams ? (side == Team.Chaos ? _flowText.MarinesName : _flowText.ChaosName) : null;
        var entries = board?.Players ?? [];
        var rows = CountUpRows.Pick(entries, teams, side, _client.PlayerId)
            .Select(e => new CountUpRow(HudName(e.PlayerId), ClassLabel(e.Class), e.Kills, e.Deaths, e.PlayerId == _client.PlayerId))
            .ToList();
        int kills = OwnScoreEntry()?.Kills ?? _stats.Of(_client.PlayerId).Kills;
        return new CountUpData(_summaryText.TeamScore, teamScore, otherLabel, otherScore, kills, rows, new Color(1f, 0.8f, 0f));
    }

    SummaryData BuildSummary()
    {
        int me = _client!.PlayerId;
        var entry = OwnScoreEntry();
        var side = _client.Predicted.Team;
        var r = BattleRecord.For(_stats, me, entry?.Kills, entry?.Deaths, side);
        var t = _summaryText;
        var lines = new List<SummaryLine>
        {
            new(t.BestClass, r.BestClass is { } c ? ClassLabel(c) : null),
            new(t.BestWeapon, r.BestWeapon is { } w ? $"{w}  ({r.BestWeaponKills})" : null),
            new(t.EnemyKills, r.EnemyKills.ToString()),
            new(t.PlayerDeaths, r.Deaths.ToString()),
            new(t.KillRatio, r.KillRatio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)),
            new(t.TeamAssists, null),   // assists are not on the wire
            new(t.BestKillstreak, r.BestStreak.ToString()),
            new(t.Nemesis, r.Nemesis is { } n ? $"{HudName(n)}  ({r.NemesisKills} : {r.KillsOfNemesis})" : null, new Color(1f, 0.2f, 0f)),
        };
        if (_client.Scoreboard?.Seize is not null && side != Team.None) lines.Add(new SummaryLine(t.Seized, $"{r.TeamCaptures}  ({_flowText.NameOf(side)})"));
        return new SummaryData(lines, "Counted from the kill feed this client saw. Assists are not tracked yet.");
    }

    /// <summary>Posts one of the match flow's events on its own emitter (not positioned); a note in the log says what the catalogue made of it.</summary>
    void PostMatchSound(string eventName, bool once = false)
    {
        if (!SoundReady() || _sound is null) return;
        _matchEmitter ??= new SoundEmitter { Label = "match" };
        var play = _sound.Post(eventName, _matchEmitter, once: once);
        if (play is null) return;
        MatchFlowLog($"sound {eventName}: " + (!play.Found ? "event not in the banks" : $"{play.Voices.Count} voice(s)" +
            (play.Voices.Count > 0 ? $" (3D {play.Voices[0].Is3D}, {play.Voices[0].VolumeDb:F1} dB)" : "") + (play.MissingMedia > 0 ? $", {play.MissingMedia} without media" : "") +
            (play.Stops.Count > 0 ? $", {play.Stops.Count} stop(s)" : "")));
    }
}
