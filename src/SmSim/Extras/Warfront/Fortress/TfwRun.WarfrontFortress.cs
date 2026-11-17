using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>Warfront's fortress battle in the run (slice W14b): feeds the living humans' feet to the battle's stage flow each tick. The feet are the run's own list of this tick (<see cref="TfwRun.Fight"/> fills it just before the step).</summary>
public sealed partial class TfwRun
{
    Vector3[] _fortressHumans = new Vector3[8];

    /// <summary>Called from <c>WarfrontBattleChange</c> right after the battle's step: the humans (not puppets) among the feet go to the fortress's stages. No-op in any other battle.</summary>
    void WarfrontFortressObserve(TfwBattle battle)
    {
        if (!battle.IsWarfrontFortress) return;
        int n = Math.Min(battle.FortressPlayerCount, _feet.Length);
        if (_fortressHumans.Length < n) _fortressHumans = new Vector3[n * 2];
        int humans = 0;
        for (int k = 0; k < n; k++)
            if (!TfwContracts.IsPuppet(battle.FortressPlayerId(k))) _fortressHumans[humans++] = _feet[k];
        battle.FortressObserve(_fortressHumans.AsSpan(0, humans));
    }
}
