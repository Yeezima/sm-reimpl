using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>Warfront W17 test aid: walks a lane battle's leader from objective to objective (the rules of the capture stay the game's), loopback-bound servers only.</summary>
public sealed partial class GameServer
{
    int _wfLaneWalkState;   // 0 untouched, -1 refused
    uint _wfLaneWalkNext;
    TfwBattle? _wfLaneWalkBattle;

    /// <summary>
    /// <c>SM_WF_LANE_WALK=&lt;seconds&gt;</c> (test aid; the server must be bound to 127.0.0.1): in a node battle the leader is put, every that many seconds, on the first objective that is open (A, then B, C, and D once it
    /// appears) when he is more than 8 m from it; each move is logged. Fighting, the clearing and securing times and the win are the battle's own: only the walk is aided. Use with <c>SM_TFW_WIN_TEST</c> and
    /// <c>SM_TFW_KEEP_HUMAN=1</c> when the fight is to be left out as well.
    /// </summary>
    void WarfrontLaneWalkAid(WarfrontServerState s)
    {
        WarfrontRosterAid(s);
        if (Environment.GetEnvironmentVariable("SM_WF_LANE_WALK") is not { Length: > 0 } v || !double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds <= 0) return;
        if (World.Tfw is not { Battle: { } battle } || battle.IsWarfrontFortress) return;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            if (_wfLaneWalkState == 0) { _wfLaneWalkState = -1; WfLog("test aid SM_WF_LANE_WALK refused: the server is not bound to 127.0.0.1 (--bind 127.0.0.1)"); }
            return;
        }
        if (_wfLaneWalkState < 0) return;
        int rate = WarfrontRate;
        if (!ReferenceEquals(battle, _wfLaneWalkBattle)) { _wfLaneWalkBattle = battle; _wfLaneWalkNext = World.Tick + (uint)(seconds * rate); }
        if (World.Tick < _wfLaneWalkNext) return;
        _wfLaneWalkNext = World.Tick + (uint)(seconds * rate);
        if (WarfrontLeaderPlayer() is not { } leader || !World.Players.TryGetValue(leader, out var p) || !p.Alive) return;
        foreach (var o in battle.Objectives)
        {
            if (o.State is TfwPositionState.Secured or TfwPositionState.Locked) continue;
            var where = new Vector3(o.Position.X, o.Position.Y, o.Position.Z);
            if (Vector2.Distance(new Vector2(p.Position.X, p.Position.Y), new Vector2(where.X, where.Y)) <= 8f) return;
            World.TfwRespawnAt(leader, where, 0f);
            WfLog($"test aid SM_WF_LANE_WALK: P{leader} put on objective {(char)('A' + o.Index)} ({where.X:0},{where.Y:0},{where.Z:0}), state {o.State}");
            return;
        }
    }

    bool _wfRosterRefused;

    /// <summary>
    /// <c>SM_WF_ROSTERS=&lt;n&gt;</c> (test aid, W17; the server must be bound to 127.0.0.1): every identified human's roster is recruited up to n Marines (at most 9) as soon as it is short, through the roster's own
    /// <c>Recruit</c> (an ineligible Dreadnought gets none), so the 36-retainer case can be measured without playing for the cards. Logged once per owner and size.
    /// </summary>
    void WarfrontRosterAid(WarfrontServerState s)
    {
        if (Environment.GetEnvironmentVariable("SM_WF_ROSTERS") is not { Length: > 0 } v || !int.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out int want) || want < 2 || _wfWiring is null) return;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            if (!_wfRosterRefused) { _wfRosterRefused = true; WfLog("test aid SM_WF_ROSTERS refused: the server is not bound to 127.0.0.1 (--bind 127.0.0.1)"); }
            return;
        }
        want = Math.Min(want, 9);
        foreach (var key in s.KeyOf.Values.Distinct().ToArray())
        {
            if (!_wfWiring.Rosters.Has(key)) continue;
            int before = _wfWiring.Rosters.Count(key);
            while (_wfWiring.Rosters.Count(key) < want && _wfWiring.Rosters.Recruit(key)) { }
            int after = _wfWiring.Rosters.Count(key);
            if (after != before) WfLog($"test aid SM_WF_ROSTERS: {KeyTail(key)} roster {before} -> {after}");
        }
    }
}
