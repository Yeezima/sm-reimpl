using System.Numerics;

namespace SmSim;

/// <summary>
/// Musou's arsenal in the World (W0, the base; docs/new-weapons-design.md section 8, "Musou call sites"): one partial method per call site in Musou's own files, each a no-op until the
/// milestone that owns it implements it in its own file (one implementer each, so W1 to W4 share no file). Every call site sits in Musou code, which runs only with
/// <see cref="ContentExtras.Musou"/>; Annihilation and Seize Ground never reach one. Nothing here changes a rule, so neither Musou's version nor any config hash moves.
/// <list type="bullet">
/// <item><see cref="ArsenalCleave"/> (W3, the spear's sweeps): the cleave's reach, cone, target cap and damage per weapon (<c>World.MusouCombat.cs</c>, <c>MusouCleave</c>).</item>
/// <item><see cref="ArsenalShotBlast"/> (W1, the autocannon's explosive round): a weapon's own blast at every enemy a round strikes (<c>Campaign/World.TfwMelee.cs</c>, the bolt blasts' path).</item>
/// <item><see cref="ArsenalJump"/> (W3, the impaling dive): the jump pack's events, the pound's Impact among them (<c>World.Musou.cs</c>, <c>ExtrasJump</c>).</item>
/// <item><see cref="ArsenalProjectileMeets"/> (W4, the sticking charge): a projectile meeting an enemy (<c>World.MusouCombat.cs</c>, <c>MusouProjectiles</c>) or a contraption
/// (<c>Campaign/World.TfwMeks.cs</c>, <c>ExtrasProjectileStop</c>).</item>
/// <item><see cref="ArsenalStep"/> (W4, the fuse and the rolling ring): once a tick, after the core's projectiles and detonations of the tick (<c>World.Musou.cs</c>, <c>ExtrasStep</c>).</item>
/// <item><see cref="ArsenalRefillSeconds"/> (W4, one charge back every 30 s): the per-item refill period (<c>Campaign/World.TfwSupply.cs</c>, <c>TfwGrenadeRefill</c>).</item>
/// </list>
/// </summary>
public sealed partial class World
{
    /// <summary>W3: the cleave of a strike by <paramref name="attacker"/> with this weapon: its reach in metres, the cone's least cosine, the most enemies struck and the damage each takes.</summary>
    partial void ArsenalCleave(int attacker, in PlayerState state, MeleeStep step, ref float range, ref float minCos, ref int most, ref float damage);

    /// <summary>W1: the blast a round of <paramref name="weapon"/> sets off at every enemy it strikes (the bolt blasts' path, with this splash); null: none of its own.</summary>
    partial void ArsenalShotBlast(WeaponSpec weapon, ref SplashSpec? blast);

    /// <summary>W3: a jump pack event of <paramref name="player"/> (the pound's <see cref="JumpEventKind.Impact"/> among them), after the core resolved it.</summary>
    partial void ArsenalJump(int player, JumpEventKind kind);

    /// <summary>
    /// W4: projectile <paramref name="projectile"/> (its id) met Musou enemy <paramref name="enemy"/> (0: none) or contraption <paramref name="contraption"/> (0: none) at <paramref name="point"/>,
    /// before Musou applies its own rule (a sticky projectile stops where it struck). <paramref name="stays"/>: set by the arsenal for a contraption, so the core's stop (the detonation at it) does not
    /// happen and the arsenal itself holds the projectile there (W4's charge sticks to it).
    /// </summary>
    partial void ArsenalProjectileMeets(int projectile, ushort enemy, byte contraption, Vector3 point, ref bool stays);

    /// <summary>W4: once a tick in a Musou match, after the core's projectiles and detonations of the tick.</summary>
    partial void ArsenalStep();

    /// <summary>W4: seconds between two of <paramref name="item"/> given back to a player carrying fewer than its most (Musou Mode's supply rule; 15 for every item until W4).</summary>
    partial void ArsenalRefillSeconds(ThrownSpec item, ref float seconds);
}
