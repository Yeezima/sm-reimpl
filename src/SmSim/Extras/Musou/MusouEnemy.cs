using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The kinds of Musou enemy (design section B; 4 bits on the wire, so at most 16). Hit points and the rest come from the data where it has them, else
/// estimates (MU1's tuning, tagged kind: ours).
/// </summary>
public enum MusouEnemyType : byte
{
    /// <summary>Ork Boy with a choppa (boy_melee).</summary>
    Boy = 0,
    /// <summary>Ork Boy with a shoota (boy_ranged): holds 12-20 m.</summary>
    ShootaBoy = 1,
    /// <summary>'Ard Boy (boy_melee_pro): tougher, slower.</summary>
    ArdBoy = 2,
    /// <summary>Gretchin (gretchin_coop rig; only if its mesh loads): fast and frail.</summary>
    Gretchin = 3,
    /// <summary>Nob (nob_melee / nob_ranged): a heavy staggering swing.</summary>
    Nob = 4,
    /// <summary>Chaos Space Marine (the PvP model): melee and bolter.</summary>
    ChaosMarine = 5,
    /// <summary>The final boss: a scaled Nob (nob_melee_pro) with abilities of its own.</summary>
    Warboss = 6,
}

/// <summary>The state machine of one enemy (design section B; 4 bits on the wire).</summary>
public enum MusouEnemyState : byte
{
    Spawning = 0,
    Seek = 1,
    WindUp = 2,
    Strike = 3,
    Recover = 4,
    Stagger = 5,
    Dying = 6,
}

/// <summary>
/// One enemy as <see cref="MusouSim"/> hands it out (a copy of its slot in the pool's arrays; design section B, about 48 bytes a slot). <see cref="Id"/>
/// 1..<see cref="MusouSim.Capacity"/> (0: none); <see cref="Node"/> the nav node it stands on (-1 none); <see cref="TargetPlayer"/> the player it goes for
/// (0 none); <see cref="StateTicks"/> ticks in <see cref="State"/>; <see cref="AttackToken"/> the attack slot it holds on its target (0 none).
/// </summary>
public readonly record struct MusouEnemy(ushort Id, MusouEnemyType Type, MusouEnemyState State, Vector3 Position, Vector2 Velocity, float Yaw, float Health,
    int Node, ushort TargetPlayer, ushort StateTicks, byte AttackToken)
{
    /// <summary>Alive and in play (not dying).</summary>
    public bool Alive => Id != 0 && State != MusouEnemyState.Dying && Health > 0f;
}
