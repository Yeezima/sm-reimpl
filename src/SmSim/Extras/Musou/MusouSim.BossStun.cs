using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The bosses' melee stun (owner, 2026-11-12: "bosses should remain unstunnable in any form" and "they should be susceptible to melee attack stuns though"). The Warboss and the Boss Orks
/// (<see cref="MusouPowerupRules.Boss"/>) stay immune to every stun and stagger (<see cref="MusouSim.Stagger"/> is false for them: grenades, blasts, charges of demolition, plasma, artillery,
/// the drop pod, the Librarian's powers) except a player's melee strike that carries one (<see cref="MusouSim.StaggerMelee"/>). The health-threshold stun windows (80, 60, 40, 20 %) of the
/// earlier change are gone. Spec "Musou Mode: bosses and melee stuns".
/// </summary>
public sealed partial class MusouSim
{
    readonly ushort[] _stunLeft = new ushort[Capacity];

    void SpawnedBossStun(int i) { _stunLeft[i] = 0; }

    /// <summary>Whether boss <paramref name="id"/> stands in a melee stun now (the Warboss's abilities read it).</summary>
    public bool BossStunned(ushort id) => id >= 1 && id <= Capacity && _used[id - 1] && _stunLeft[id - 1] > 0;

    /// <summary>Melee stuns that landed on a boss so far (the log and the tests).</summary>
    public int BossStuns { get; private set; }

    /// <summary>
    /// A human player's melee strike (not an AI Marine's) that carries a stagger (a heavy swing, a charge, a ground pound): an ordinary enemy staggers as <see cref="Stagger"/> says; the Warboss and a Boss Ork, which no
    /// other stagger moves, stand stunned for the same <see cref="MusouCombatRules.StaggerSeconds"/> (no knockback; each strike restarts the window, none is added on top). False for anything dying,
    /// in a hop or unknown. The only door through which a boss is stunned.
    /// </summary>
    public bool StaggerMelee(ushort id, Vector2 push, float distance, int attacker)
    {
        int i = id - 1;
        if (i < 0 || i >= Capacity || !_used[i] || !(_type[i] == MusouEnemyType.Warboss || MusouBossOrks.Is(_type[i])) || Campaign.TfwContracts.IsPuppet(attacker)) return Stagger(id, push, distance);   // an AI Marine's melee never stuns a boss (review of 2026-11-12): only a human player's
        if (War is null || _state[i] is MusouEnemyState.Dying or MusouEnemyState.Airborne or MusouEnemyState.Spawning) return false;
        if (_stunLeft[i] == 0)
        {
            _scripted[i] = false; _drive[i] = Vector2.Zero; _driveTicks[i] = 0;
            _charging[i] = false;
            SetState(i, MusouEnemyState.Stunned);
            BossStuns++;
        }
        _token[i] = 0; _burst[i] = 0; _velocity[i] = Vector2.Zero;
        _stunLeft[i] = Math.Max((ushort)1, Ticks(Rules.Combat.StaggerSeconds));
        return true;
    }

    /// <summary>One tick of a stunned boss (from <see cref="Step"/>): it stands, then goes back to its own decisions. True while it stood.</summary>
    bool StepBossStun(int i)
    {
        _headwayTick[i] = Tick; _anchor[i] = _position[i];
        _token[i] = 0; _burst[i] = 0; _velocity[i] = Vector2.Zero;
        if (--_stunLeft[i] == 0) SetState(i, MusouEnemyState.Seek);
        else if (_state[i] != MusouEnemyState.Stunned) SetState(i, MusouEnemyState.Stunned);
        return true;
    }
}
