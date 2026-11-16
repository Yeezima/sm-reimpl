using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aid (the Big Shoota capture, 2026-11-09): <c>SM_TFW_BIGSHOOTA_TEST=1</c> posts one extra Big Shoota Nob 7 m ahead and 5 m to the left of the first human in a battle (once per battle), facing him,
/// so his gun, muzzle flash and tracers can be filmed (use with <c>SM_TFW_KEEP_HUMAN=1</c>). Off without the variable; Musou Mode only.
/// </summary>
public sealed partial class GameServer
{
    static readonly bool TfwBigShootaTestOn = Environment.GetEnvironmentVariable("SM_TFW_BIGSHOOTA_TEST") == "1";
    const float BigShootaTestDistance = 7f, BigShootaTestSide = -5f;   // ahead and to the left, in the open and in the player's view
    object? _tfwBigShootaTestBattle;

    void TfwBigShootaTest()
    {
        if (!TfwBigShootaTestOn || World.Tfw is not { Battle: { } battle } run || World.Musou is not { } orks || ReferenceEquals(_tfwBigShootaTestBattle, battle)) return;
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (human == 0) return;
        var me = World.Players[human];
        var at = me.Position + new System.Numerics.Vector3(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw), 0f) * BigShootaTestDistance + new System.Numerics.Vector3(MathF.Cos(me.Yaw), MathF.Sin(me.Yaw), 0f) * BigShootaTestSide;
        ushort id = orks.Spawn(MusouEnemyType.BigShootaNob, at, me.Yaw + MathF.PI, 1_000_000f);   // outlasts the Guardsmen's fire: the capture needs him alive
        if (id == 0) return;
        orks.Post(id, at);
        _tfwBigShootaTestBattle = battle;
        Console.WriteLine($"TFWBIGSHOOTA tick {World.Tick}: Big Shoota Nob {id} posted at {at.X:F1},{at.Y:F1},{at.Z:F1}");
    }
}
