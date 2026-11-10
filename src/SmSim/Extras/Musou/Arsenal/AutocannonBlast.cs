using SmSim.Extras.Musou.Arsenal;

namespace SmSim;

/// <summary>
/// W1, the Musou autocannon's explosive round (docs/new-weapons-design.md section 4; the owner, 2026-11-10: "have 2x the HE explosion size of the bolter and bolt pistol"): every round that strikes an Ork
/// sets off a blast at the point it struck him, twice the bolt blast's reach, with the bolt blast's rules (a kill is torn apart where he stands, a hurt fodder Ork falls where he stands, any other staggers,
/// <c>TfwCombatRules.BoltBlastKnocksDown</c>). It rides the bolt blasts' path (<c>World.TfwBoltBlasts</c>, through <see cref="ArsenalShotBlast"/>), so a round that pierces five Orks blasts at each of the
/// six it strikes. The numbers are <see cref="AutocannonRules"/>'s.
/// </summary>
public sealed partial class World
{
    partial void ArsenalShotBlast(WeaponSpec weapon, ref SplashSpec? blast)
    {
        if (weapon.Name.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) blast = AutocannonRules.Blast;
    }
}

/// <summary>The autocannon's numbers (Musou Mode only; spec/behavior.md "Autocannon"). Metres, seconds, damage on the health scale.</summary>
public static class AutocannonRules
{
    /// <summary>Seconds between two rounds (data: the install's <c>csm_mp_exotic_auto_cannon</c> shot cooldown, used between single rounds).</summary>
    public const float SecondsPerRound = 0.4f;
    /// <summary>The clip (data: the campaign gun's 400; Musou's ammunition is endless anyway).</summary>
    public const int Clip = 400;
    /// <summary>The blast's reach: twice the bolt blast's 1.25 m (owner's: D12).</summary>
    public const float BlastRadius = 2.5f;
    /// <summary>The blast's damage at its centre: the bolt blast's (the owner asked for the size only; an estimate, ours).</summary>
    public const float BlastDamage = 15f;
    /// <summary>The blast's fall-off (distance in metres, share of the damage): full to 1.0 m, 30 % at the edge (the bolt blast's shape at twice the size).</summary>
    public static readonly IReadOnlyList<DistancePoint> BlastFallOff = [new(1.0f, 1f), new(BlastRadius, 0.3f)];

    /// <summary>The blast as a splash for the enemies' blast (<c>World.MusouBlast</c>: enemies only, in sight of its centre), as the bolt blast's.</summary>
    public static readonly SplashSpec Blast = new(BlastRadius, new DamageSpec(BlastDamage, 0f, 1f, 1f, 1f, 1f, false) { Type = DamageType.Explosive },
        BlastFallOff, LineOfSight: true, IgnoreSelf: true, TargetFilter: "target_enemy");
}
