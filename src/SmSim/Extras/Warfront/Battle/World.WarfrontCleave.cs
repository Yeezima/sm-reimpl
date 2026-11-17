using SmSim.Extras.Musou;
using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>
/// Warfront's melee cleave (owner 2026-11-18): every melee weapon's reach x1.2, the Dreadnought's x1.4 in place of it; no cap on the enemies one swing strikes; an Ork is struck whenever his collision
/// overlaps the cleave volume (his centre need not be inside the reach). Applied from <c>MusouCleave</c> through the <c>WarfrontCleave</c> hook, so it acts only in a Warfront run
/// (<c>_tfw.Warfront</c> set) and Classic PvP and Musou Mode keep their own numbers. Server authoritative; the client's drawn arc reads the same scale (<c>MusouCleaveShape.Of</c>).
/// </summary>
public sealed partial class World
{
    partial void WarfrontCleave(in PlayerState state, ref float range, ref int most, ref float bodySlack)
    {
        if (_tfw?.Warfront is null) return;
        range *= WarfrontBattleRules.CleaveScale(DreadnoughtRules.IsDreadnought(Config, state.Class));
        most = MusouSim.Capacity;
        bodySlack = WarfrontBattleRules.CleaveBodySlack;
    }
}
