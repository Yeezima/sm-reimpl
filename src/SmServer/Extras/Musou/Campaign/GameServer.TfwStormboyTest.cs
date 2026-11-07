using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;

namespace SmServer;

/// <summary>
/// Test aid (expansion S3, a capture of a Stormboy's hop or crash): <c>SM_TFW_STORMBOY_TEST=1</c> makes every battle's Stormboy squads form at once 40 m in front of the first human and hunt him
/// (<see cref="TfwBattle.StormboyTest"/>); <c>SM_TFW_STORMBOY_TEST=crash</c> also shoots down one Stormboy in mid-hop every 6 s (credited to that human), so the crash is seen. Off without the variable.
/// </summary>
public sealed partial class GameServer
{
    static readonly string? TfwStormboyTestMode = Environment.GetEnvironmentVariable("SM_TFW_STORMBOY_TEST");
    uint _tfwStormDownAt;

    void TfwStormboyTest()
    {
        if (TfwStormboyTestMode is not ("1" or "crash") || World.Tfw is not { Battle: { } battle } run || World.Musou is not { } orks) return;
        battle.StormboyTest = true;
        int human = run.InField.FirstOrDefault(id => World.Players.TryGetValue(id, out var p) && p.Alive);
        if (human == 0) return;
        var me = World.Players[human];
        battle.StormTestFacing = new System.Numerics.Vector2(-MathF.Sin(me.Yaw), MathF.Cos(me.Yaw));
        if (TfwStormboyTestMode != "crash" || World.Tick < _tfwStormDownAt) return;
        foreach (var squad in battle.StormSquads())
            foreach (ushort id in squad.Ids)
            {
                if (!orks.IsAirborne(id) || !orks.TryGet(id, out var e) || e.Position.Z < me.Position.Z + 2.5f) continue;
                orks.Damage(id, human, e.Health, System.Numerics.Vector3.Zero, out _);
                Console.WriteLine($"TFWSTORM tick {World.Tick}: Stormboy {id} shot down at {e.Position.X:F1},{e.Position.Y:F1},{e.Position.Z:F1}");
                _tfwStormDownAt = World.Tick + (uint)(6 * World.Config.TickRate);
                return;
            }
    }
}
