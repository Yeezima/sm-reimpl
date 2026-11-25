using System.Numerics;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Chaos;

namespace SmSim;

/// <summary>
/// The warp breaches in the World (Chaos Warfront C4; design "Warp breaches"). Every hook returns at once unless the run's Warfront rules carry the Chaos roster and a battle is under way, so an Orks
/// stage, Musou Mode and Classic never place, step or close a breach. Each tick (<see cref="WarfrontChaosStep"/>, the last line of <c>WarfrontStep</c>): the battle's breaches are placed on its first
/// Chaos tick; every detonation not yet judged (<see cref="RecentDetonations"/>, so those raised late in the last tick, a launcher round bursting on a Daemon, are judged too, each exactly once)
/// that a connected human's qualifying explosive made closes the nearest breach within reach; then the breaches step (open, squads, refills). And a qualifying projectile of a human that flies
/// into a breach's sphere detonates there (<see cref="ExtrasProjectileBreach"/>, the core seam in <c>Projectiles.cs</c>), so a rocket or a plasma ball through it closes it.
/// </summary>
public sealed partial class World
{
    readonly WarfrontBreachFilter _breachFilter = new();
    readonly HashSet<(uint Tick, int Projectile)> _breachJudged = new();
    readonly List<(uint Tick, int Projectile)> _breachStale = new();

    /// <summary>Where the breaches' lines go (the server sets its log; null: nowhere).</summary>
    public Action<string>? WarfrontChaosLog
    {
        get => _chaosLog;
        set { _chaosLog = value; _breachFilter.Log = value; if (_tfw?.Battle?.ChaosBreaches is { } b) b.Log = value; }
    }
    Action<string>? _chaosLog;

    /// <summary>The breaches of the Chaos battle under way (null: none, or not a Chaos battle).</summary>
    public WarfrontBreaches? WarfrontChaosBreaches => _tfw?.Warfront?.ChaosRoster is not null ? _tfw.Battle?.ChaosBreaches : null;

    /// <summary>The breach filter's verdict on a detonation (the tests' and the log's).</summary>
    public BreachHit WarfrontBreachVerdict(in DetonationEvent e) => _breachFilter.Classify(e, Config);

    /// <summary>C4: the breaches' tick (the last line of <c>WarfrontStep</c>).</summary>
    partial void WarfrontChaosStep();

    /// <summary>C5's finale step, after the breaches' (declared for C5; a no-op until it lands).</summary>
    partial void WarfrontChaosDuelStep();

    partial void WarfrontChaosStep()
    {
        if (_tfw?.Warfront is not { ChaosRoster: not null } rules || _tfw.Battle is not { } battle)
        {
            if (_breachJudged.Count > 0) _breachJudged.Clear();
            return;
        }
        var breaches = battle.ChaosBreaches ?? battle.PlaceChaosBreaches(BreachSite(rules, battle), _chaosLog);
        // the detonations of the last ticks not yet judged, each once
        foreach (var d in RecentDetonations)
        {
            if (!_breachJudged.Add((d.Tick, d.Projectile))) continue;
            if (!breaches.AnyStanding || !BreachHuman(d.Owner)) continue;
            if (_breachFilter.Classify(d, Config).Counts) breaches.TryClose(d.Position, d.Owner, Tick);
        }
        if (_breachJudged.Count > 0)
        {
            _breachStale.Clear();
            foreach (var k in _breachJudged) if (Tick - k.Tick > DetonationMemoryTicks) _breachStale.Add(k);
            foreach (var k in _breachStale) _breachJudged.Remove(k);
        }
        breaches.Step(battle.BattleTicks, battle.BreachSpawns);
        WarfrontChaosDuelStep();
    }

    /// <summary>The battle the breaches are placed for: the fortress (its plaza kept clear), the HQ defence (none), or a lane node.</summary>
    static WarfrontBreachSite BreachSite(WarfrontBattleRules rules, TfwBattle battle)
    {
        if (rules.Fortress is { } f) return new(WarfrontBreachBattle.Fortress, rules.Node, f.Plan.BossSpot, f.Plan.ArenaRadius, f.Plan.SourceStage, WarfrontBreachRules.FortressPlazaStage);
        if (battle.WarfrontHq || rules.Target == WarfrontTargetKind.Hq) return new(WarfrontBreachBattle.Hq, rules.Node);
        return new(WarfrontBreachBattle.Lane, rules.Node);
    }

    /// <summary>Whether <paramref name="owner"/> is a connected human (a player of the World, not a retainer or an AI Marine).</summary>
    bool BreachHuman(int owner) => owner > 0 && !TfwContracts.IsPuppet(owner) && _players.ContainsKey(owner);

    /// <summary>
    /// The core seam (<c>Projectiles.cs</c> <c>Fly</c>, EXTRAS-HOOK): a qualifying explosive of a connected human whose step meets a standing breach's sphere stops there and detonates (it then closes
    /// that breach through the step above). Every other projectile, and every run without standing breaches, is untouched.
    /// </summary>
    partial void ExtrasProjectileBreach(in ProjectileState p, Vector3 direction, float max, ref float stop)
    {
        if (_tfw?.Warfront?.ChaosRoster is null || _tfw.Battle?.ChaosBreaches is not { AnyStanding: true } breaches) return;
        if (!BreachHuman(p.Owner) || !_breachFilter.Classify(p.Owner, p.Weapon, p.Thrown, Config).Counts) return;
        float at = breaches.SphereEntry(p.Position, direction, max);
        if (at < stop) stop = at;
    }
}
