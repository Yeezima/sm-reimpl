using SmSim.Extras.Warfront.Chaos.Finale;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The duel relay in the run (Chaos Warfront C5): while the relay of this battle is on (a challenger on the plaza or the pause between two), its rules replace the fallen's (no revive window, no spectator
/// respawn, no wipe: a downed duelist is defeated, the World's step hands over); when the relay is lost the battle is left as lost the same tick, which the server maps to <c>WarfrontOutcome.FinaleLost</c>.
/// Before the snapshot (the bodyguard fight) and in every other battle the normal rules stand.
/// </summary>
public sealed partial class TfwRun
{
    /// <summary>C5's seam at the top of <c>WarfrontDowned</c>: <paramref name="duel"/> true takes the tick's fallen rules.</summary>
    partial void WarfrontDuelDowned(World w, int rate, ref bool duel);

    partial void WarfrontDuelDowned(World w, int rate, ref bool duel)
    {
        if (Battle is not { } battle || !w.WarfrontDuelOwns(battle) || w.WarfrontChaosDuel is not { } d) return;
        switch (d.Phase)
        {
            case WarfrontDuelPhase.Snapshot or WarfrontDuelPhase.Duel or WarfrontDuelPhase.Handoff:
                duel = true;
                return;
            case WarfrontDuelPhase.Lost:
                duel = true;
                _spectating.Clear();
                Leave(w, left: false, TfwMapResult.Lost);   // the relay ran out: FinaleLost
                return;
        }
    }
}
