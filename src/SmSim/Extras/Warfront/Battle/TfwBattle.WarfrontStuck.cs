using System.Numerics;
using SmSim.Extras.Warfront;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>Warfront's stuck-unit record (slice W3b, from the W2 review): a failsafe that removes or relocates an Ork of a node battle reports it to the rules' <see cref="WarfrontBattleRules.StuckSink"/> first.</summary>
public sealed partial class TfwBattle
{
    partial void WarfrontStuck(ushort id, MusouEnemyType type, Vector3 at, byte why)
    {
        if (why == 1 && id == BossId && _fortress is not null) _fortressBossCarried = true;   // W14e: Musou's failsafe moved the fortress Warboss (the arena-boss rule puts him back; Fortress/TfwBattle.WarfrontFortress.cs)
        if (!WarfrontNode || _enemies.Warfront is not { StuckSink: { } sink } rules) return;
        sink(new WarfrontStuckReport(rules.Lane, rules.Node, id, type, at, (WarfrontStuckWhy)why));
    }
}
