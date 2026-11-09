using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aid (the Weirdboy bolt capture, 2026-11-09): <c>SM_TFW_WEIRDBOY_TEST=1</c> brings one extra Weirdboy in 30 m ahead of the first human in a battle (once per battle; on the graph when it has one),
/// facing him, so his cast and bolt can be filmed without waiting 45 to 90 s for the battle's own (use with <c>SM_TFW_KEEP_HUMAN=1</c>). Off without the variable; Musou Mode only.
/// </summary>
public sealed partial class GameServer
{
    static readonly bool TfwWeirdboyTestOn = Environment.GetEnvironmentVariable("SM_TFW_WEIRDBOY_TEST") == "1";
    const float WeirdboyTestDistance = 30f;   // inside his 20 to 50 m band
    object? _tfwWeirdboyTestBattle;
    ushort _tfwWeirdboyTestId;
    MusouEnemyState _tfwWeirdboyTestState;

    void TfwWeirdboyTest()
    {
        if (TfwWeirdboyTestOn && _tfwWeirdboyTestId != 0 && World.Musou is { } sim)
        {
            if (sim.TryGet(_tfwWeirdboyTestId, out var e) && e.State != _tfwWeirdboyTestState) { _tfwWeirdboyTestState = e.State; Console.WriteLine($"TFWWEIRDBOY tick {World.Tick}: state {e.State}"); }
            foreach (var ev in sim.Events)
                if (ev.Enemy == _tfwWeirdboyTestId && ev.Kind == MusouEventKind.Telegraph && ev.Value == MusouWeirdboy.BoltCue)
                    Console.WriteLine($"TFWWEIRDBOY tick {World.Tick}: bolt at target {ev.Player} ({ev.Vector.X:F1},{ev.Vector.Y:F1})");
        }
        if (!TfwWeirdboyTestOn || World.Tfw is not { Battle: { } battle } run || World.Musou is not { } orks || ReferenceEquals(_tfwWeirdboyTestBattle, battle)) return;
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (human == 0) return;
        var me = World.Players[human];
        var at = me.Position + new System.Numerics.Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f) * WeirdboyTestDistance;
        int node = -1;
        if (orks.Nav is { } nav && nav.Nearest(at, 6f, 6f) is var n and >= 0) { node = n; at = nav.Position(n); }
        ushort id = orks.Spawn(MusouEnemyType.Weirdboy, at, me.Yaw + MathF.PI, 1_000_000f, node);   // outlasts the Guardsmen's and the allies' fire: the capture needs him alive (his shield still breaks)
        if (id == 0) return;
        _tfwWeirdboyTestBattle = battle; _tfwWeirdboyTestId = id;
        Console.WriteLine($"TFWWEIRDBOY tick {World.Tick}: Weirdboy {id} at {at.X:F1},{at.Y:F1},{at.Z:F1}, player {human} at {me.Position.X:F1},{me.Position.Y:F1},{me.Position.Z:F1}");
    }
}
