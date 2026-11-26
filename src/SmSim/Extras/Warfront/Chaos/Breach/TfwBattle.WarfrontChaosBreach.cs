using System.Numerics;
using SmSim.Extras.Warfront.Chaos;

namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The warp breaches in the battle (Chaos Warfront C4): the battle holds its breaches (placed by <c>World.WarfrontChaosStep</c> on its first Chaos tick) and is the field they spawn into. A breach Daemon
/// is an unleashed spawn of the Gretchin row through <see cref="MusouSim.SpawnDaemon"/> (the Daemon in a Chaos battle), hunting the nearest target with the battle's usual seek, with the battle's
/// health scales (the level's and the humans', at the spawn). It is an ordinary enemy of the pool, so <see cref="Counted"/> counts it against the alive cap already (no hook needed there; a test
/// proves it). An Orks battle never has breaches.
/// </summary>
public sealed partial class TfwBattle
{
    /// <summary>This battle's warp breaches (null: none placed; always null in an Orks battle).</summary>
    public WarfrontBreaches? ChaosBreaches { get; private set; }

    /// <summary>Places the breaches once (a second call keeps the first's).</summary>
    internal WarfrontBreaches PlaceChaosBreaches(in WarfrontBreachSite site, Action<string>? log) =>
        ChaosBreaches ??= WarfrontBreaches.Place(_layout, _enemies.Nav, Roll, site, Math.Max(1, _enemies.TickRate), log);

    BreachField? _breachField;
    /// <summary>The battle as the breaches' field (public for the tests).</summary>
    public IWarfrontBreachField BreachSpawns => _breachField ??= new BreachField(this);

    /// <summary>The room the breaches may fill now: the crowds' alive cap less what counts against it (the trickle has its own cap), and the pool's room less the reserve.</summary>
    public int BreachRoom => Math.Max(0, Math.Min(AliveCapNow - (Counted() - _trickled.Count), TfwBattleRules.CrowdPoolRoom - PoolUsed));

    /// <summary>The enemies that count against the crowds' alive cap now (<see cref="Counted"/>; the tests prove a breach Daemon is one).</summary>
    public int AliveCounted => Counted();

    sealed class BreachField(TfwBattle battle) : IWarfrontBreachField
    {
        public int Room => battle.BreachRoom;

        public ushort Spawn(Vector3 spot, Vector3 breach)
        {
            var at = battle.Ground(spot, breach);
            return battle._enemies.SpawnDaemon(at, battle.FaceCentre(at), battle.Health(MusouEnemyType.Gretchin));
        }

        public bool IsDaemon(ushort id) => battle._enemies.TryGet(id, out var e) && e.Alive && e.Type == MusouEnemyType.Gretchin;
    }
}
