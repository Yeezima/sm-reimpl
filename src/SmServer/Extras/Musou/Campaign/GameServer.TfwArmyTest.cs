using System.Globalization;
using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aid (expansion S6, captures and the real-map profile): <c>SM_TFW_ARMY_TEST=clash:&lt;n&gt;</c> secures the position nearest the first human 2 s after each landing, stands the human 35 m short of it facing it, posts a
/// garrison of n there and sends a wave of n from the muster's stronghold at it; <c>SM_TFW_ARMY_TEST=rally</c> secures it, posts a garrison of 100 and sends it home at once with 60 lost, the human standing on its way
/// home (so it is rallied). Every 5 s a <c>TFWARMIES</c> line: each army's side, order, count, aggregate and real men. Off without the variable.
/// </summary>
public sealed partial class GameServer
{
    static readonly string? TfwArmyTestMode = Environment.GetEnvironmentVariable("SM_TFW_ARMY_TEST");
    TfwBattle? _armyTestBattle;
    uint _armyTestAt;
    bool _armyTestDone;

    void TfwArmyTest()
    {
        if (TfwArmyTestMode is null || World.Tfw is not { Battle: { } battle } run) return;
        int rate = Math.Max(1, World.Config.TickRate);
        if (!ReferenceEquals(battle, _armyTestBattle)) { _armyTestBattle = battle; _armyTestAt = World.Tick + (uint)(2 * rate); _armyTestDone = false; }
        if (World.Tick % (uint)(5 * rate) == 0)
        {
            string armies = string.Join("; ", battle.ArmyList.Select(a => FormattableString.Invariant($"{a.Id} {a.Side} {a.Order} goal {a.Goal} count {a.Count} far {a.FarCount} real {a.Real} coh {a.Cohesion:F2}")));
            Console.WriteLine(FormattableString.Invariant($"TFWARMIES tick {World.Tick}: {armies} | real army orks {battle.ArmyOrksAlive}, squads {battle.ArmySquads}, enemies {World.Musou?.Alive}, guards {World.TfwGuards?.Alive}"));
        }
        if (_armyTestDone || World.Tick < _armyTestAt) return;
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (human == 0) return;
        _armyTestDone = true;
        var me = World.Players[human].Position;
        int k = 0;
        for (int j = 1; j < battle.Layout.Positions.Length; j++)
            if (Vector3.Distance(battle.Layout.Positions[j], me) < Vector3.Distance(battle.Layout.Positions[k], me)) k = j;
        battle.ClearAndSecureForTests(k);
        var point = battle.Layout.Positions[k];
        if (TfwArmyTestMode.StartsWith("clash", StringComparison.Ordinal))
        {
            int n = int.TryParse(TfwArmyTestMode.AsSpan(TfwArmyTestMode.IndexOf(':') + 1), CultureInfo.InvariantCulture, out int size) ? size : 500;
            var (stand, way) = battle.RouteToBaseForTests(k, 0f);
            var (at, _) = battle.RouteToBaseForTests(k, 0.4f);
            if (Vector3.Distance(at, point) > 35f) at = point + Vector3.Normalize(at - point) * 35f;
            var look = point - at;
            World.TfwRespawnAt(human, at, MathF.Atan2(-look.X, look.Y));
            var g = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, k, n, point + new Vector3(Vector2.Normalize(new Vector2(look.X, look.Y)) * 6f, 0f), MathF.Atan2(-look.X, look.Y) + MathF.PI);
            var wave = battle.SendWaveForTests(n, k);
            Console.WriteLine($"TFWARMY tick {World.Tick}: clash at position {k}: garrison {g?.Count} and wave {wave?.Count} (from {wave?.From}); human at {at.X:F0},{at.Y:F0}");
        }
        else
        {
            var g = battle.AddArmyForTests(TfwArmySide.Guard, TfwArmyOrder.Hold, k, 100, point, 0f);
            if (g is null) return;
            TfwBattle.LoseForTests(g, 60);
            battle.RetreatForTests(g);
            var (at, way) = battle.RouteToBaseForTests(k, 0.2f);
            World.TfwRespawnAt(human, at, MathF.Atan2(way.X, -way.Y));   // facing back up the way, towards the point
            Console.WriteLine($"TFWARMY tick {World.Tick}: rally at position {k}: garrison of {g.Count} going home; human at {at.X:F0},{at.Y:F0}");
        }
    }
}
