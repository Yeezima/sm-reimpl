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
        // Musou only (review 2026-11-11): Classic's autocannon has no blast; its hit-scan path never reaches this hook, and the _musou check keeps it so.
        if (_musou is not null && weapon.Name.Equals(ArsenalRules.Autocannon, StringComparison.OrdinalIgnoreCase)) blast = AutocannonRules.Blast;
    }
}

/// <summary>
/// The autocannon's numbers (Musou Mode only; spec/behavior.md "Autocannon"). Metres, seconds, damage on the health scale. Classic PvP's autocannon (<see cref="ContentExtras.Autocannon"/>) shares
/// the rate and the clip but has no blast and the Stalker Bolter's shot (<c>SmContent.Extras.Musou.Arsenal.Arsenal.ClassicTwin</c>).
/// <para>
/// The autocannon pass (the owner, 2026-11-11: "it should kill essentially 1 shot any weak ork (adjust the damage value of the aoe blast radius of each shot to also 1 shot any fodder ork). initial
/// shot damage should go up as well to be the damage value of the aoe explosion effect + 30"): the blast is <see cref="BlastDamage"/> everywhere inside its 2.5 m (no fall-off), so with the war's
/// x1.2 on a human player's ranged damage (<c>TfwCombatRules.PlayerRangedDamageScale</c>) it deals 120 to every Ork in reach: the most health of any fodder Ork (<see cref="Fodder"/>: the
/// Stormboy's 120; the Boyz, Shoota Boyz and Rokkit Boyz 85, the Gretchin and the Bomb Squig 30). The round itself is <see cref="DirectDamage"/>, the blast's + 30 (156 after the x1.2).
/// </para>
/// </summary>
public static class AutocannonRules
{
    /// <summary>Seconds between two rounds (data: the install's <c>csm_mp_exotic_auto_cannon</c> shot cooldown, used between single rounds).</summary>
    public const float SecondsPerRound = 0.4f;
    /// <summary>The clip (data: the campaign gun's 400; Musou's ammunition is endless anyway).</summary>
    public const int Clip = 400;
    /// <summary>The blast's reach: twice the bolt blast's 1.25 m (owner's: D12).</summary>
    public const float BlastRadius = 2.5f;
    /// <summary>
    /// The blast's damage, the same anywhere in its reach (the owner, 2026-11-11: one blast kills any fodder Ork; 15, the bolt blast's, until then): 100 before the war's x1.2 on a human player's
    /// ranged damage, 120 after it, the most health of any fodder Ork (<see cref="FodderHealthMost"/>, the Stormboy's).
    /// </summary>
    public const float BlastDamage = 100f;
    /// <summary>The round's own damage (the owner, 2026-11-11: "the damage value of the aoe explosion effect + 30"): 130 before the war's x1.2, 156 after it (the file's 50 until then).</summary>
    public const float DirectDamage = BlastDamage + 30f;
    /// <summary>
    /// The blast's fall-off (distance in metres, share of the damage): full to its edge, so an Ork anywhere in reach takes all of <see cref="BlastDamage"/> (the owner's one-shot inside the radius,
    /// 2026-11-11; until then the bolt blast's shape at twice the size: full to 1.0 m, 30 % at the edge).
    /// </summary>
    public static readonly IReadOnlyList<DistancePoint> BlastFallOff = [new(BlastRadius, 1f)];

    /// <summary>
    /// The fodder Orks the owner's one-shot covers (2026-11-11, "any weak ork", "any fodder ork"): every Ork that is neither an elite nor a boss (<c>TfwRewardRules.Elite</c>, <c>MusouPowerupRules.Elite</c>,
    /// <c>MusouBossOrks</c>): the choppa and Shoota Boyz and the Gretchin (<c>MusouSim.Fodder</c>), the Rokkit Boyz (who stand in the crowds in a Boy's place), the Stormboyz and the Bomb Squigs. Not the
    /// 'Ard Boy (170, an elite), the Nob, the Weirdboy (his shield), the Scrap Mek, the Chaos Marine or a boss.
    /// </summary>
    public static readonly IReadOnlyList<SmSim.Extras.Musou.MusouEnemyType> Fodder =
    [
        SmSim.Extras.Musou.MusouEnemyType.Boy, SmSim.Extras.Musou.MusouEnemyType.ShootaBoy, SmSim.Extras.Musou.MusouEnemyType.Gretchin,
        SmSim.Extras.Musou.MusouEnemyType.RokkitBoy, SmSim.Extras.Musou.MusouEnemyType.Stormboy, SmSim.Extras.Musou.MusouEnemyType.BombSquig,
    ];

    /// <summary>The most health of a fodder Ork in Musou's tuning (the Stormboy's 120): what the blast must deal after the war's x1.2.</summary>
    public static float FodderHealthMost(SmSim.Extras.Musou.MusouRules rules) => Fodder.Max(t => rules.Of(t).Health);

    /// <summary>
    /// Whether <paramref name="weapon"/> keeps a full clip in Musou Mode (the owner, 2026-11-11: "the autocannon should also have infinite ammo in musou mode"): the autocannon never runs dry; its heat
    /// and overheat run as its data has them (<c>World.TfwEndlessAmmo</c>).
    /// </summary>
    public static bool EndlessClip(WeaponSpec weapon) => ArsenalRules.IsAutocannon(weapon.Name);

    /// <summary>The blast as a splash for the enemies' blast (<c>World.MusouBlast</c>: enemies only, in sight of its centre), as the bolt blast's.</summary>
    public static readonly SplashSpec Blast = new(BlastRadius, new DamageSpec(BlastDamage, 0f, 1f, 1f, 1f, 1f, false) { Type = DamageType.Explosive },
        BlastFallOff, LineOfSight: true, IgnoreSelf: true, TargetFilter: "target_enemy");

    /// <summary>The bolt blast's reach (<c>TfwCombatRules.BoltBlastRadius</c>), the size a blast is drawn at as it is.</summary>
    public const float BoltBlastReach = 1.25f;
    /// <summary>The wire's step for a point (<c>TfwWire.PointStep</c>, a decimetre): a blast's radius reaches the client rounded to it (1.25 m as 1.2, 2.5 m as 2.5).</summary>
    public const float WireStep = 0.1f;   // TfwWire.PointStep is this constant, so the two cannot drift

    /// <summary>
    /// How large a blast whose record says <paramref name="radius"/> metres is drawn against the bolt blast's. The record's radius is rounded to a decimetre on the wire, so a radius within one step of the bolt
    /// blast's reach is the bolt blast itself (scale 1: its drawing is what it was before the arsenal); a record with none (0 or less) or not finite is 1; any other is its reach over the bolt blast's
    /// (the autocannon's 2.5 m: 2).
    /// </summary>
    public static float BlastDrawScale(float radius) =>
        !float.IsFinite(radius) || radius <= 0f || MathF.Abs(radius - BoltBlastReach) <= WireStep + 1e-3f ? 1f : radius / BoltBlastReach;
}
