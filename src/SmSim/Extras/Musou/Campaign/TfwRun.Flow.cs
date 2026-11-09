using System.Numerics;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The late batch's run flow (the owner, 2026-11-06, stream D): a fallen human may deploy again at the landing zone for a supply (<see cref="TfwCommand.Redeploy"/>), and the host may call the
/// Stormbird back (<see cref="TfwCommand.ReturnToShip"/>): the team survives <see cref="TfwBattleRules.RecallSeconds"/>, then the run returns to the Stormbird.
/// </summary>
public sealed partial class TfwRun
{
    bool _recall;
    readonly Dictionary<int, int> _landingSlots = new();   // each human's place of the landing zone (TfwCoop.LandingOffset), kept through others leaving

    /// <summary>Whether the pickup under way is the host's recall (free, and no death or other player calls it off).</summary>
    public bool Recalling => _recall && Phase == TfwRunPhase.Pickup;

    /// <summary>Redeploys made this run (tests and the server's log).</summary>
    public int Redeploys { get; private set; }

    /// <summary>
    /// The host of a Musou match (ours, the owner's "host button", 2026-11-06): the human with the lowest player id in the match, the first to join (a local host's own client connects
    /// first). The server decides by this, the client shows the button by the same rule over the scoreboard. -1: no human.
    /// </summary>
    public static int HostOf(IEnumerable<int> players)
    {
        int host = -1;
        foreach (int id in players)
            if (!TfwContracts.IsPuppet(id) && (host < 0 || id < host)) host = id;
        return host;
    }

    /// <summary>
    /// The owner, 2026-11-06: "choosing deploy after dying respawns the player at the original start spawn and costs 1 supply". A fallen human of the battle under way (its revive window
    /// open or not, the extraction's prompt running or not) comes back at once at its own place of the landing zone (<see cref="TfwCoop.LandingOffset"/>, as at the landing), facing the HQ,
    /// for <see cref="TfwBattleRules.RedeployCost"/> supplies. Refused while alive, outside a battle, for one not in the field, or short of supplies.
    /// </summary>
    void Redeploy(World w, int player)
    {
        if (Phase is not (TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup) || Battle is not { } battle) return;
        int k = _deployed.IndexOf(player);
        if (k < 0 || !w.Players.TryGetValue(player, out var p) || p.Alive || Supplies < TfwBattleRules.RedeployCost) return;
        Supplies -= TfwBattleRules.RedeployCost;
        var layout = battle.Layout;
        w.TfwRespawnAt(player, Ashore(w, layout, TfwCoop.LandingOffset(_landingSlots.GetValueOrDefault(player, k))), layout.FaceCentre(layout.LandingZone));   // owner 2026-11-09: faces the map's centre
        _reviveTicks.Remove(player);
        ExtractionTicks = 0;
        Redeploys++;
    }

    /// <summary>
    /// The host's "back to Stormbird" (the owner, 2026-11-06: "players must survive 20 s, then the run returns to the Stormbird"): from a battle or the final assault, or over a pickup under
    /// way, the Stormbird comes in <see cref="TfwBattleRules.RecallSeconds"/>. Ours: it is free (no transport, as a won battle's pickup), a death does not call it off (the fallen are flown out
    /// with the team; with nobody left standing and no revive window the extraction's rule decides as ever), only the host can call it off (P, as a pickup), and a battle not won counts as left.
    /// </summary>
    void ReturnToShip(World w, int player, int rate)
    {
        if (Phase is not (TfwRunPhase.Battle or TfwRunPhase.FinalAssault or TfwRunPhase.Pickup) || Battle is null || !_deployed.Contains(player) || player != HostOf(w.Players.Keys)) return;
        if (Recalling) return;   // already on its way: a second press does not restart the 20 s
        if (Phase != TfwRunPhase.Pickup) _fighting = Phase;
        _recall = true;
        PickupTicks = Math.Max(1, (int)MathF.Round(TfwBattleRules.RecallSeconds * rate));
        War.Enter(TfwRunPhase.Pickup, War.BattleLane);
    }
}
