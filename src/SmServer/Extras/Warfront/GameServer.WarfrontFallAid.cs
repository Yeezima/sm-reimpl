using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>Warfront fallen-watch test aid: makes the leader fall once per battle, loopback-bound servers only.</summary>
public sealed partial class GameServer
{
    TfwBattle? _wfFallBattle;
    uint _wfFallTick;
    bool _wfFallDone;

    /// <summary>
    /// <c>SM_WF_FALL_TEST=&lt;seconds&gt;</c> (test aid; the server must be bound to 127.0.0.1): that many seconds after a node or fortress battle starts, the leader is put far from his squad and falls (the sim's own test kill: the real down path follows, the 25 s revive window then the
    /// 60 s spectating then the respawn). Once per battle; logged.
    /// </summary>
    void WarfrontFallAid(WarfrontServerState s)
    {
        if (!double.TryParse(Environment.GetEnvironmentVariable("SM_WF_FALL_TEST"), System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds < 0) return;
        if (World.Tfw is not { Battle: { } battle })
        {
            _wfFallBattle = null;
            return;
        }
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            if (!_wfFallDone) { _wfFallDone = true; WfLog("test aid SM_WF_FALL_TEST refused: the server is not bound to 127.0.0.1 (--bind 127.0.0.1)"); }
            return;
        }
        if (!ReferenceEquals(battle, _wfFallBattle)) { _wfFallBattle = battle; _wfFallDone = false; _wfFallTick = World.Tick + (uint)(seconds * WarfrontRate); }
        if (_wfFallDone || World.Tick < _wfFallTick) return;
        if (WarfrontLeaderPlayer() is not { } leader || !World.Players.TryGetValue(leader, out var p) || !p.Alive) return;
        _wfFallDone = true;
        // the leader is first put on the objective farthest from the landing (the squad's Marines revive a body within 30 m at once), so the fall is a real one that nobody reaches in the 25 s window
        var layout = battle.Layout;
        var far = layout.Positions.Length > 0 ? layout.Positions.OrderByDescending(v => System.Numerics.Vector3.Distance(v, layout.LandingZone)).First() : layout.LandingZone;
        World.TfwRespawnAt(leader, far, 0f);
        World.TfwKillForTests(leader);
        WfLog($"test aid SM_WF_FALL_TEST: P{leader} falls at tick {World.Tick} at ({far.X:0},{far.Y:0},{far.Z:0}), {System.Numerics.Vector3.Distance(far, layout.LandingZone):0} m from the landing");
    }
}
