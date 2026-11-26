using System.Numerics;
using SmSim.Extras.Executions;
using SmSim.Extras.Musou.Campaign;

namespace SmSim;

/// <summary>
/// Executions X3, Classic PvP Extended (owner-approved brief 2026-11-24, answers 2026-11-25): a confirmed lethal melee kill opens a window (<see cref="ExecutionRules.WindowTicks"/>, 0.8 s) in which
/// the killer may tap Execute to play a finisher on the corpse. The finisher is presentation and a lock only: no damage, no score, no kill event, no respawn delay (the kill already stood). One try
/// per corpse: the first press inside its window spends the offer, whether a move aligns or not (a corpse against a wall gives the normal kill only).
/// </summary>
public sealed partial class World
{
    /// <summary>The open offers, by victim (a later kill of the same victim replaces its offer).</summary>
    readonly SortedDictionary<int, ExecutionClassicOffer> _executionOffers = new();
    readonly List<ExecutionClassicOffer> _executionOffersNew = new();
    readonly List<ExecutionNoFit> _executionNoFits = new();

    /// <summary>The Classic presses of the last step whose corpse no move fitted, with the alignment's reasons (the server's log: why a finisher did not play).</summary>
    public IReadOnlyList<ExecutionNoFit> ExecutionsNoFitThisTick => _executionNoFits;

    /// <summary>The Classic offers this tick's kills opened (the server sends each to its killer alone).</summary>
    public IReadOnlyList<ExecutionClassicOffer> ExecutionsOfferedThisTick => _executionOffersNew;

    /// <summary>The Classic offers still open (by victim).</summary>
    public IReadOnlyCollection<ExecutionClassicOffer> ExecutionsOffersOpen => _executionOffers.Values;

    /// <summary>Bystanders are gathered within this distance of the executor (metres; ours): the longest slide, two capsule radii and 7 m of margin.</summary>
    public float ExecutionsBystanderReach => MathF.Max(ExecutionRules.ClassicMaxSlide, ExecutionAlign.MaxBackSlide) + 2f * MathF.Max(Config.CapsuleRadius, 0.05f) + 7f;

    void ExecutionsClassicOffers()
    {
        _executionOffersNew.Clear();
        if (Config.Extras.HasFlag(ContentExtras.Warfront)) return;
        foreach (var k in LastKills)
        {
            if (k.Cause != KillCause.Melee || k.Killer == 0 || k.Killer == k.Victim || TfwContracts.IsPuppet(k.Killer)) continue;
            if (!_players.TryGetValue(k.Killer, out var killer) || !_players.TryGetValue(k.Victim, out var victim)) continue;
            if (!killer.Alive) continue;   // a trade kill: the killer died in the same tick and cannot finish it
            if (Rules is not null && !Hostile(killer.Team, victim.Team)) continue;   // a teamkill never offers
            // The corpse as it fell: its feet, yaw, class and team; alive for the alignment's checks only (MeleeLock.Sees and the probes read a living body).
            var offer = new ExecutionClassicOffer(k.Killer, k.Victim, k.Tick, victim with { Alive = true, Velocity = Vector3.Zero });
            _executionOffers[k.Victim] = offer;
            _executionOffersNew.Add(offer);
        }
    }

    /// <summary>Drops the offers past their window (plus the rewind cap, so a late press with an early view tick still finds its offer) and those whose killer left.</summary>
    void ExecutionsClassicExpire()
    {
        if (_executionOffers.Count == 0) return;
        uint keep = (uint)(ExecutionRules.WindowTicks(Config) + Math.Max(0, Config.MaxRewindTicks));
        foreach (var (victim, offer) in _executionOffers.ToArray())
            if (Tick - offer.KillTick > keep || !_players.ContainsKey(offer.Killer)) _executionOffers.Remove(victim);
    }

    /// <summary>
    /// Classic: the press of <paramref name="id"/> at <paramref name="pressTick"/> takes its most recent offer whose window holds the press (none: refused) and spends it, then chooses the move
    /// (<see cref="ExecutionChoice.ChooseClassic"/> with the other living players as bystanders). False when nothing starts (the refusal recorded).
    /// </summary>
    bool ExecutionsClassicPick(int id, in PlayerState executor, uint pressTick, out ExecutionPick? pick)
    {
        pick = null;
        ExecutionClassicOffer? best = null;
        foreach (var offer in _executionOffers.Values)
            if (offer.Killer == id && ExecutionRules.InWindow(Config, offer.KillTick, pressTick) && (best is not { } b || offer.KillTick > b.KillTick)) best = offer;
        if (best is not { } chosen) { _executionRefusals.Add((id, ExecutionRefusal.NoOffer)); return false; }
        _executionOffers.Remove(chosen.Victim);   // one try per corpse
        var corpse = chosen.Corpse;
        var result = ExecutionChoice.ChooseClassic(executor, ExecutionsClassName(executor), ExecutionsMeleeWargear(executor) ?? "", corpse, Config.ExecutionRows, Config, Collision,
            ExecutionChoice.Seed(Tick, id, chosen.Victim), ExecutionsBystanders(id, chosen.Victim, executor.Position), ExecutionRules.ClassicMaxSlide);   // Classic's longer forward slide (lead 40)
        if (!result.Ok)
        {
            _executionRefusals.Add((id, ExecutionRefusal.NoFit));
            float gap = new Vector2(corpse.Position.X - executor.Position.X, corpse.Position.Y - executor.Position.Y).Length();
            _executionNoFits.Add(new ExecutionNoFit(id, chosen.Victim, result.Failure, gap, corpse.Position.Z - executor.Position.Z, result.Tried.ToArray()));
            return false;
        }
        pick = new ExecutionPick(ExecutionTargetKind.Player, chosen.Victim, result, result.VictimAt, result.VictimYaw, corpse.Class, corpse.Team);
        return true;
    }

    /// <summary>The feet of every other living player near <paramref name="at"/>, in id order (not the executor and not the corpse's own player, who may be respawning elsewhere).</summary>
    List<Vector3> ExecutionsBystanders(int executor, int victim, Vector3 at)
    {
        var list = new List<Vector3>();
        float reach2 = ExecutionsBystanderReach * ExecutionsBystanderReach;
        foreach (var (pid, p) in _players.OrderBy(kv => kv.Key))
            if (pid != executor && pid != victim && p.Alive && Vector3.DistanceSquared(p.Position, at) <= reach2) list.Add(p.Position);
        return list;
    }
}
