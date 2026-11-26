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
            case WarfrontDuelPhase.Won when _winHoldUntil != 0:
                duel = true;
                if (!DuelWinHeld(w)) { _spectating.Clear(); Leave(w, left: false, TfwMapResult.Won); }   // the Lord's finisher played out (or its bound ran out): the win's exit
                return;
        }
    }

    uint _winHoldUntil;

    /// <summary>
    /// Executions X4b (lead 41, 2026-11-26: the Lord's finisher must play out): the fortress win's exit (<see cref="WarfrontBattleWon"/>: everyone back in the hub the same tick) waits while a finisher on the
    /// Lord plays, at most its move's length + <see cref="WarfrontDuelRules.WinHoldSlackSeconds"/> from its start; a run that ended for any reason (Done, a disconnect, an abort) lets the exit go at once.
    /// The duel is Won from the kill (its phase, its kind-16 state and the HUD outcome as before); the run's result is the same Won when the exit comes. True while it holds.
    /// </summary>
    bool DuelWinHeld(World w)
    {
        if (Battle is not { ChaosFortress: true } battle || !w.WarfrontDuelOwns(battle)) { _winHoldUntil = 0; return false; }
        ushort lord = w.WarfrontChaosDuel?.LordId ?? 0;   // the duel's own record: the battle forgets its boss id at his death
        uint? until = null;
        int rate = Math.Max(1, w.Config.TickRate);
        foreach (var r in w.ExecutionsLive)
            if (lord != 0 && r.Target == lord && w.Config.Executions.AtWire(r.Wire) is { } row)
                until = r.StartTick + (uint)MathF.Ceiling((row.Seconds + WarfrontDuelRules.WinHoldSlackSeconds) * rate);
        if (_winHoldUntil == 0)
        {
            if (until is not { } u || w.Tick >= u) return false;
            _winHoldUntil = u;
            w.WarfrontChaosLog?.Invoke($"chaos finale: the Lord's finisher plays out before the win's exit (at most until tick {u})");
            return true;
        }
        if (until is null || w.Tick >= _winHoldUntil)
        {
            w.WarfrontChaosLog?.Invoke(until is null ? "chaos finale: the Lord's finisher ended: the win's exit" : "chaos finale: the Lord's finisher ran past its bound: the win's exit");
            _winHoldUntil = 0;
            return false;
        }
        return true;
    }
}
