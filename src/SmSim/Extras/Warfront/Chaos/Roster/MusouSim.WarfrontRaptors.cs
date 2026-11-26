using SmSim.Extras.Warfront.Chaos;

namespace SmSim.Extras.Musou;

/// <summary>
/// The Raptors' grounded state (Chaos Warfront C3; owner 2026-11-25, card "Raptors": "Yes, when grounded"): grounded Chaos Raptors may take the Chaos Marine executions, never in the air. Read only; the
/// executions themselves are later work. A Raptor is the Stormboy row of a Chaos battle (<see cref="WarfrontChaosRoster"/>), so its hop and fall are the Stormboy code's (<see cref="IsAirborne"/>,
/// <see cref="IsFalling"/>).
/// </summary>
public sealed partial class MusouSim
{
    /// <summary>
    /// Whether enemy <paramref name="id"/> is a Raptor of a Chaos battle standing on the ground: alive, not spawning or dying, not mid-hop (<see cref="MusouEnemyState.Airborne"/>) and not falling.
    /// False for every other kind, and in every Orks battle (a Stormboy there is an Ork).
    /// </summary>
    public bool RaptorGrounded(ushort id) =>
        ChaosRoster is not null && id >= 1 && id <= Capacity && _used[id - 1] && _type[id - 1] == MusouEnemyType.Stormboy
        && !IsAirborne(id) && !IsFalling(id) && _state[id - 1] is not (MusouEnemyState.Dying or MusouEnemyState.Spawning);
}
