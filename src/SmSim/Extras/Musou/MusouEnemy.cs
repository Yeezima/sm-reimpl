using System.Numerics;

namespace SmSim.Extras.Musou;

/// <summary>
/// The kinds of Musou enemy (design section B; a full byte on the wire since Musou version 13). Hit points and the rest come from the data where it has them, else
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
    /// <summary>The roaming Boss Orks (owner, 2026-11-06; <see cref="MusouBossOrks"/>): a Boy, Shoota Boy, 'Ard Boy or Nob at 3x its size, 10x its health (at least 3000) and 2x its damage.</summary>
    BossBoy = 7,
    BossShootaBoy = 8,
    BossArdBoy = 9,
    BossNob = 10,
    /// <summary>The Weirdboy (Exterminatus's <c>ork_coop_weirdboy</c>; Musou Mode only, <see cref="MusouWeirdboy"/>): a frail psyker behind an energy shield that bullets cannot pierce,
    /// who throws bursting bolts from range, teleports away from anyone who closes in and calls Boyz.</summary>
    Weirdboy = 11,
    /// <summary>Expansion S1 (docs/musou-expansion.md; placeholder rows, nothing spawns them until S2 to S4): the Bomb Squig, a fused runner that bursts as an Ork blast.</summary>
    BombSquig = 12,
    /// <summary>The Rokkit Boy (S2): holds 25-45 m and fires a slow rocket.</summary>
    RokkitBoy = 13,
    /// <summary>The Stormboy (S3): jump-pack squads.</summary>
    Stormboy = 14,
    /// <summary>The Big Shoota Nob (S3): an elite with a heavy gun.</summary>
    BigShootaNob = 15,
    /// <summary>The Scrap Mek (S4, <see cref="MusouMeks"/>): runs from players, hides and builds contraptions; never attacks.</summary>
    ScrapMek = 16,
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
    /// <summary>Thrown down by a Marine's shove (Musou pass 2; M0 is the contract alone, nothing enters it yet). The 4 bits on the wire hold 16 states.</summary>
    Knocked = 7,
    /// <summary>The Weirdboy's shield broke: he stands stunned, unshielded (<see cref="MusouWeirdboy"/>).</summary>
    Stunned = 8,
    /// <summary>The Weirdboy calls Boyz: he stands casting until they come.</summary>
    Casting = 9,
    /// <summary>The Weirdboy teleports away: he stands fading, then appears elsewhere (in <see cref="Spawning"/>).</summary>
    Vanishing = 10,
    /// <summary>Expansion S4: a Scrap Mek at work on a contraption (<see cref="MusouMeks"/>): he stands, facing it.</summary>
    Building = 11,
    // 12 left free.
    /// <summary>Expansion S3: a Stormboy in a jump-pack hop (<see cref="MusouStormboy"/>): his position follows the arc (the z on the wire is the arc's), nothing he does until he lands.</summary>
    Airborne = 13,
    /// <summary>Expansion S3: the Big Shoota Nob's gun overheated or jammed after a burst (<see cref="MusouBigShoota"/>): venting steam, not firing.</summary>
    Overheated = 14,
}

/// <summary>
/// One enemy as <see cref="MusouSim"/> hands it out (a copy of its slot in the pool's arrays; design section B, about 48 bytes a slot). <see cref="Id"/>
/// 1..<see cref="MusouSim.Capacity"/> (0: none); <see cref="Node"/> the nav node it stands on (-1 none); <see cref="TargetPlayer"/> the player it goes for
/// (0 none); <see cref="StateTicks"/> ticks in <see cref="State"/>; <see cref="AttackToken"/> the attack token it holds on its target (0 none, <see cref="MusouSim.MeleeToken"/>, <see cref="MusouSim.ShooterToken"/>).
/// </summary>
public readonly record struct MusouEnemy(ushort Id, MusouEnemyType Type, MusouEnemyState State, Vector3 Position, Vector2 Velocity, float Yaw, float Health,
    int Node, ushort TargetPlayer, ushort StateTicks, byte AttackToken)
{
    /// <summary>Alive and in play (not dying).</summary>
    public bool Alive => Id != 0 && State != MusouEnemyState.Dying && Health > 0f;
}
