using SmSim.Extras.Musou;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;

namespace SmSim;

/// <summary>
/// Warfront's melee cleave (owner 2026-11-18): every melee weapon's reach x1.2, the Dreadnought's x1.4 in place of it, for human players and their retainers only (not the allied Marines, Guardsmen or contraptions); no cap on the enemies one swing strikes; an Ork is struck whenever his collision
/// overlaps the cleave volume (his centre need not be inside the reach). Applied from <c>MusouCleave</c> through the <c>WarfrontCleave</c> hook, so it acts only in a Warfront run
/// (<c>_tfw.Warfront</c> set) and Classic PvP and Musou Mode keep their own numbers. Server authoritative; the client's drawn arc reads the same scale (<c>MusouCleaveShape.Of</c>).
/// </summary>
public sealed partial class World
{
    /// <summary>Whether <paramref name="attacker"/> gets Warfront's cleave bonus: a human player or a human's retainer (owner 2026-11-18: "players and their retainers only"; not the allied pair, the drop pod Marines or the Guardsmen).</summary>
    public bool WarfrontCleaveBonus(int attacker) => !TfwContracts.IsPuppet(attacker) || _tfw?.MarineOf(attacker) is { Role: MarineRole.Retainer } || (SmSim.Extras.Warfront.Retainers.WarfrontRetainerRules.CleaveBonus && _tfw?.MarineOf(attacker) is { Role: MarineRole.Squad });   // Squad (Warfront's retainers as World players, Phase B T2): off by the owner's "Base player damage only" (2026-11-25)

    partial void WarfrontCleave(int attacker, in PlayerState state, ref float range, ref int most, ref float bodySlack)
    {
        if (_tfw?.Warfront is null || !WarfrontCleaveBonus(attacker)) return;
        bool dreadnought = DreadnoughtRules.IsDreadnought(Config, state.Class);
        if (dreadnought) range /= DreadnoughtRules.CleaveReachScale;   // main's x1.5 (2026-11-17) is replaced by Warfront's x1.4, not stacked on it
        range *= WarfrontBattleRules.CleaveScale(dreadnought);
        most = MusouSim.Capacity;
        bodySlack = WarfrontBattleRules.CleaveBodySlack;
    }
}
