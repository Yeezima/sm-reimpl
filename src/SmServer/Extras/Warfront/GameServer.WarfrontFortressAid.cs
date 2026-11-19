using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>Warfront W14b test aid: walks the fortress's human through its three stages without a fight (for stills and the client's HUD line), loopback-bound servers only.</summary>
public sealed partial class GameServer
{
    int _wfWalkStep;
    uint _wfWalkNext;
    TfwBattle? _wfWalkBattle;

    /// <summary>
    /// <c>SM_WF_FORTRESS_WALK=&lt;seconds&gt;</c> (test aid; the server must be bound to 127.0.0.1): in a fortress battle the leader is put, every that many seconds, at the gate (stage 1), then 30 m south of the Warboss
    /// in the plaza (stage 2), then at the gate again, then back at the landing zone; each move is logged. Use with <c>SM_TFW_KEEP_HUMAN=1</c> so the human stays alive between moves.
    /// </summary>
    void WarfrontFortressWalkAid(WarfrontServerState s)
    {
        if (Environment.GetEnvironmentVariable("SM_WF_FORTRESS_WALK") is not { Length: > 0 } v || !double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || seconds <= 0) return;
        if (World.Tfw is not { Battle: { IsWarfrontFortress: true } battle, Warfront.Fortress: { } setup }) return;
        var args = Environment.GetCommandLineArgs();
        int at = Array.IndexOf(args, "--bind");
        if (at < 0 || at + 1 >= args.Length || !System.Net.IPAddress.TryParse(args[at + 1], out var bound) || !System.Net.IPAddress.IsLoopback(bound))
        {
            if (_wfWalkStep == 0) { _wfWalkStep = -1; WfLog("test aid SM_WF_FORTRESS_WALK refused: the server is not bound to 127.0.0.1 (--bind 127.0.0.1)"); }
            return;
        }
        if (_wfWalkStep < 0) return;
        int rate = WarfrontRate;
        if (!ReferenceEquals(battle, _wfWalkBattle)) { _wfWalkBattle = battle; _wfWalkStep = 0; _wfWalkNext = World.Tick + (uint)(seconds * rate); }
        if (World.Tick < _wfWalkNext) return;
        if (WarfrontLeaderPlayer() is not { } leader || !World.Players.TryGetValue(leader, out var p) || !p.Alive) return;
        var plan = setup.Plan;
        var (where, name) = (_wfWalkStep % 4) switch
        {
            0 => (plan.Gate, "the gate"),
            1 => (plan.BossSpot + new Vector3(0f, -30f, 0f), "30 m south of the Warboss in the plaza"),
            2 => (plan.Gate, "the gate again"),
            _ => (plan.Layout.LandingZone, "the landing zone"),
        };
        World.TfwRespawnAt(leader, where, 0f);
        WfLog($"test aid SM_WF_FORTRESS_WALK: P{leader} put at {name} ({where.X:0},{where.Y:0},{where.Z:0}); stage now {battle.WarfrontFortressFlow?.Stage}");
        _wfWalkStep++;
        _wfWalkNext = World.Tick + (uint)(seconds * rate);
    }
}
