namespace SmSim.Extras.Musou.Campaign;

/// <summary>
/// The owner's melee and firepower changes for Musou Mode (pass 2, M5; items 7, 22, 24): the shove and the fall, the 20-enemy cleave, the piercing shots. They are set on
/// <see cref="MusouSim.War"/> by <see cref="World.StartTfw"/> alone, so the 5-wave test aid and every other mode never read them (<c>MusouSkirmishPinTests</c>, <c>PvpCombatIsolationTests</c>). Nothing
/// here changes a shared default: the core's frag (one frag, the data's blast: item 21's three big frags were taken out, owner 2026-11-08), melee and shot rules stay as they are, and the Musou Mode ones sit on top through the Musou hooks. Every value is ours (kind: ours, spec "Added
/// content: Musou Mode: melee and firepower"). Distances in metres, times in seconds. Hashed only through <see cref="TfwRules.Version"/>.
/// </summary>
public sealed record TfwCombatRules
{
    public static TfwCombatRules Default { get; } = new();

    // ---- the cleave (item 22)

    /// <summary>Most enemies one swing strikes, nearest first (the owner: "up to 20"; 2026-11-09: "raise the cleave cap up to 40 orks in one swing").</summary>
    public int CleaveTargets { get; init; } = 40;

    /// <summary>Most Guardsmen one Boss Ork swing strikes (20: the cleave cap before the players' 2026-11-09 raise to 40, which was for the players' swings only).</summary>
    public int BossCleaveTargets { get; init; } = 20;

    /// <summary>
    /// The players' melee volume in the war (owner, 2026-11-09: "extend the melee hitbox outward and forward of the player in a flat cone - they should reach out further for cleaving purposes"; ours,
    /// low confidence: about +30 % reach (+50 % until the owner's 2026-11-09 "reduce the latest melee cleave range to be 1.3x, not 1.5x"), 120 degrees to each side in place of 90): the cleave's reach (<see cref="MusouCombatRules.CleaveRange"/>) times <see cref="CleaveReachScale"/>, within
    /// <see cref="CleaveHalfAngleDegrees"/> of the heading. The height band stays the core's vertical reach (1.5 m: waist to head of an Ork, flat). The 5-wave skirmish keeps its old volume (pinned replay).
    /// </summary>
    public float CleaveReachScale { get; init; } = 1.3f;   // owner 2026-11-09: "reduce the latest melee cleave range to be 1.3x, not 1.5x"
    public float CleaveHalfAngleDegrees { get; init; } = 120f;

    /// <summary>
    /// The players' cleave reach in the war, on top of everything else (owner, 2026-11-11: "reduce melee attack cleave range by 40%"; scope "Musou, players only"): the whole flat reach of every
    /// human player's melee cleave (<see cref="ScalesPlayer"/>: not an AI Marine's; every melee weapon, Musou's arsenal included: the power spear's own 15 m sweep becomes 9 m) times this, applied after each weapon's own shape and before the Supercharged
    /// powerup's scale. The arc's angles, the target cap and the height band are unchanged; the Orks' melee (the Boss Orks' cleave on the Guardsmen) never reads it. 1 restores the reach before.
    /// Read by the server's cleave (<c>World.MusouCleave</c>), the cleave on the contraptions (<c>World.TfwMekMelee</c>) and the client's drawn swing arc and predicted pick
    /// (<see cref="MusouCleaveShape"/>), so the line and the hitbox stay together. The 5-wave run keeps its reach (pinned replay).
    /// </summary>
    public float PlayerCleaveReachScale { get; init; } = 0.72f;   // 0.6 (owner 2026-11-11) x 1.2 (owner 2026-11-12: "Increase all melee attacks cleave distance by 20%")

    // ---- the players' ranged damage (owner, 2026-11-11)

    /// <summary>
    /// The players' ranged damage on the enemies in the war (owner, 2026-11-11: "increase all ranged attack damage by 20%"; scope "Musou, players only"): every hit of a human player's gun (<see cref="ScalesPlayer"/>: not an AI Marine's),
    /// launcher and projectile, every splash of theirs (a launcher's or a plasma round's burst, the bolt and autocannon blasts, frag grenades, melta bombs) and the Remote Detonators' blasts times this, on
    /// the Orks and their contraptions; a plasma burn follows its hit. Our reading of "all ranged": the grenades and charges are in; not the Librarian's spells (their own numbers, LibrarianRules), not an
    /// Ork's own explosion, never the Orks' damage. The orbital strike a human player calls is in since the owner's "Add it to orbital strikes" (2026-11-11; on the enemies only, the shells' kills still
    /// credit nobody). 1 restores the damage before; the 5-wave run keeps its damage (pinned replay).
    /// </summary>
    public float PlayerRangedDamageScale { get; init; } = 1.2f;

    /// <summary>
    /// Whether a ranged hit on the enemies loses damage with range in the war (the weapon's own fall-off, <see cref="Combat.FallOff(float, WeaponSpec)"/>; a cone's distance fall-off, the melta's).
    /// The owner, 2026-11-11: "There should be no damage falloff for any ranged weapon in Musou mode": false, so every shot deals its full damage at any distance it reaches (its range, the cone's
    /// length), for every weapon a human player fires (AI Marines keep their fall-off: owner 2026-11-11), the arsenal's included, on the Orks and their contraptions (<c>World.MusouRangeFallOff</c>, the one place). A blast's fall-off from
    /// its centre is not a range fall-off and stays; so does a pierced shot's share for each enemy it has gone through (<see cref="PierceKeep"/>). True restores the fall-off; the 5-wave run keeps it
    /// (pinned replay).
    /// </summary>
    public bool RangeFallOff { get; init; }

    /// <summary>
    /// Whether the drop pod assault is on (owner, 2026-11-12; docs/drop-pod-assault.md): <see cref="TfwCommand.DropPod"/> calls a pod. False restores the artillery strike as the only support call:
    /// the server refuses the command (the client's <c>--no-drop-pod</c> keeps B on the artillery strike). The 5-wave run has no support call.
    /// </summary>
    public bool DropPods { get; init; } = true;

    /// <summary>
    /// Whether <see cref="PlayerCleaveReachScale"/> and <see cref="PlayerRangedDamageScale"/> apply to the attacks of player <paramref name="playerId"/> in the war: a human's character (the Librarian
    /// too when a human plays him), never an AI Marine (owner's scope, 2026-11-11: "Musou, players only"; the lead: AI allies keep their old reach and damage). The Guard squads never reach the
    /// players' paths. Only meaningful in a war, where the AI Marines hold <see cref="TfwContracts.IsPuppet"/>'s ids.
    /// </summary>
    public static bool ScalesPlayer(int playerId) => !TfwContracts.IsPuppet(playerId);

    /// <summary>The cosine of the cleave's half angle (<see cref="CleaveHalfAngleDegrees"/>).</summary>
    public float CleaveMinCos => MathF.Cos(MathF.Min(180f, CleaveHalfAngleDegrees) * MathF.PI / 180f);

    // ---- the fall (item 7; design section C)

    /// <summary>A fodder Ork or Guardsman lies down this long, then takes <see cref="GetUpSeconds"/> to stand; he neither moves nor attacks in that time (design: 1.8 and 0.7).</summary>
    public float DownSeconds { get; init; } = 1.8f;
    public float GetUpSeconds { get; init; } = 0.7f;
    public float KnockedSeconds => DownSeconds + GetUpSeconds;
    /// <summary>The time in a fall's clip (of <paramref name="clip"/> seconds) after <paramref name="seconds"/> on the ground: it falls in at most 0.8 s, lies at the end, and plays backwards over <see cref="GetUpSeconds"/>.</summary>
    public float FallClipTime(float seconds, float clip)
    {
        float rising = KnockedSeconds - seconds;
        if (rising < GetUpSeconds) return clip * MathF.Max(0f, rising) / GetUpSeconds;
        return MathF.Min(clip, seconds * MathF.Max(1f, clip / 0.8f));
    }
    /// <summary>How far each contact throws him (design: 1.5, 3, 4, 4; a melee hit is ours, the core's heavy knockback).</summary>
    public float WalkPush { get; init; } = 1.5f;
    public float SprintPush { get; init; } = 3f;
    public float ChargePush { get; init; } = 4f;
    public float LandingPush { get; init; } = 4f;
    public float MeleePush { get; init; } = 2f;
    /// <summary>The chance each contact kills outright (design: 3 %, 10 %, 25 %; landing as a charge, a melee hit as a sprint: ours).</summary>
    public float WalkDeath { get; init; } = 0.03f;
    public float SprintDeath { get; init; } = 0.10f;
    public float ChargeDeath { get; init; } = 0.25f;
    public float LandingDeath { get; init; } = 0.25f;
    public float MeleeDeath { get; init; } = 0.10f;
    /// <summary>A Marine counts as moving from this speed, flat, in m/s (under it nobody is shoved: standing still is no shove).</summary>
    public float MoveSpeed { get; init; } = 1.5f;
    /// <summary>Metres past the ring <see cref="MusouRules.PlayerRing"/> keeps enemies at that count as contact, in the front half of the move.</summary>
    public float ContactMargin { get; init; } = 0.25f;
    /// <summary>A landing shoves all round, this far, after a fall of at least <see cref="LandingFall"/> m/s (a step off a kerb is no landing).</summary>
    public float LandingReach { get; init; } = 2.5f;
    public float LandingFall { get; init; } = 4f;
    /// <summary>Most bodies one Marine tests a tick (design: 16).</summary>
    public int ShoveTests { get; init; } = 16;

    // ---- the enemies on the players (the owner, 2026-11-06)

    /// <summary>
    /// Whether an enemy's attack may put a player in a reaction (stagger, knockback). The owner, 2026-11-06: "remove all stun effects by orks in musou mode. they should never impede player movement or
    /// action": false, so an Ork's melee strike (a Nob, a Boss Ork, the Warboss), the Warboss's ground slam and his charge's knockback deal their damage and nothing else (<see cref="World"/>'s
    /// <c>MusouHit</c>). The barrage the players call (no enemy's) keeps its stagger. True restores the old staggers. The 5-wave test aid never reads it (it has no <see cref="MusouSim.War"/>).
    /// </summary>
    public bool EnemiesImpede { get; init; }

    // ---- piercing (item 24)

    /// <summary>An AI Marine's hit-scan shot that has gone through an enemy keeps this share of its damage for the next (owner 2026-11-11: a human player's keeps all of it).</summary>
    public float PierceKeep { get; init; } = 0.85f;
    /// <summary>The most enemies any shot goes through; a stack buffer's size.</summary>
    public const int MostPierced = 16;
    /// <summary>Most enemies one cone shot covers (the 5-wave run's 24 for the melta: a cone goes through what it covers).</summary>
    public int ConeTargets { get; init; } = 40;

    /// <summary>
    /// How many enemies a shot of <paramref name="weapon"/> goes through before the one that stops it (the owner: heavier weapons pierce more): the lascannon 8, the heavy bolter and the plasma cannon
    /// 5, the stalker bolter 4, the storm bolter and the melta 3, a pistol 1, any other ranged weapon 2. A projectile that bursts bursts on the one after them.
    /// </summary>
    public static int Pierced(WeaponSpec weapon)
    {
        string name = weapon.Name;
        bool Has(string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);
        if (Has("las_cannon")) return 8;
        if (Has("heavy_bolter") || Has("plasma_cannon") || Has("auto_cannon")) return 5;
        if (Has("sniper_rifle")) return 4;
        if (Has("storm_bolter") || Has("melta")) return 3;
        return weapon.Slot == WeaponSlot.Pistol ? 1 : 2;
    }

    // ---- the bolt blasts (the owner, 2026-11-07: "make all bolter weapons cause an explosion to trigger on every enemy the projectile passes through. the explosion is a small-medium explosion.")

    /// <summary>Whether a bolt weapon's shot sets off a blast at every enemy it strikes (false: the pierce of item 24 alone).</summary>
    public bool BoltBlasts { get; init; } = true;
    /// <summary>The blast's reach in metres ("small-medium": ours, an estimate; the data's frag reaches 10 m, the Musou Mode frag 20 m). Halved from 2.5 m (owner, 2026-11-08: "the explosion needs
    /// to be half its current size").</summary>
    public float BoltBlastRadius { get; init; } = 1.25f;
    /// <summary>The blast's damage at its centre (ours, an estimate: about a third of a Boy's 85, a bolter round is 22), halved from 30 (owner, 2026-11-08: "halve the damage as well yes").</summary>
    public float BoltBlastDamage { get; init; } = 15f;
    /// <summary>The blast's fall-off (distance in metres, share of <see cref="BoltBlastDamage"/>; linear between, nothing past the last): full to 0.5 m, 30 % at the edge (ours; the distances halved
    /// with the radius, owner 2026-11-08, so the same shares fall at the same fraction of the reach).</summary>
    public IReadOnlyList<DistancePoint> BoltBlastFallOff { get; init; } = [new(0.5f, 1f), new(1.25f, 0.3f)];
    /// <summary>
    /// What the blast does to an enemy (owner, 2026-11-08: "remove the ragdoll effect, it simply rips enemy corpses apart as soon as the target dies or knocks them down if it deals damage to them and
    /// they don't die"): one it kills dies as <see cref="MusouDeathKind.BoltBlast"/> (torn apart where he stands, never thrown); a fodder Ork it hurts and does not kill falls where he stands
    /// (<see cref="MusouSim.KnockDown"/> with no throw and no death roll), any other is staggered with no knockback. False: the blasts' old way (a blast death, thrown; a stagger with knockback).
    /// </summary>
    public bool BoltBlastKnocksDown { get; init; } = true;

    /// <summary>
    /// Whether <paramref name="weapon"/> is a bolt weapon: every multiplayer bolter, bolt pistol, storm bolter, heavy bolter and stalker bolter of either side (the names hold <c>bolt</c>, the stalker
    /// bolter's is <c>sniper_rifle</c>), gold variants and the perk's pistol bolter included.
    /// </summary>
    public static bool IsBolt(WeaponSpec weapon) =>
        weapon.Name.Contains("bolt", StringComparison.OrdinalIgnoreCase) || weapon.Name.Contains("sniper_rifle", StringComparison.OrdinalIgnoreCase);

    /// <summary>The blast as a splash for the enemies' blast (<c>World.MusouBlast</c>: enemies only, in sight of its centre).</summary>
    public SplashSpec BoltBlast() => new(BoltBlastRadius, new DamageSpec(BoltBlastDamage, 0f, 1f, 1f, 1f, 1f, false) { Type = DamageType.Explosive },
        BoltBlastFallOff, LineOfSight: true, IgnoreSelf: true, TargetFilter: "target_enemy");

    // ---- plasma pierces and burns (the owner, 2026-11-08: "in musou mode all plasma weapons should pierce all enemies the shot passes through + do burning lingering plasma damage over time to them.")

    /// <summary>Whether every plasma weapon's shot (light and charged; gun, pistol, cannon) goes through all the enemies on its path, each hit once at full damage (false: the plasma weapons pierce as <see cref="Pierced"/> says).</summary>
    public bool PlasmaPierces { get; init; } = true;
    /// <summary>The most enemies one plasma shot goes through (a buffer's size; no real path holds this many).</summary>
    public const int MostPlasmaPierced = 64;
    /// <summary>The burn's total as a share of the hit that set it (ours, low confidence: the owner gave no numbers).</summary>
    public float PlasmaBurnShare { get; init; } = 0.5f;
    /// <summary>How long a burn lasts after the latest hit, in seconds (ours); a new hit renews it, never stacks.</summary>
    public float PlasmaBurnSeconds { get; init; } = 3f;
    /// <summary>The time between two burn ticks, in seconds (ours).</summary>
    public float PlasmaBurnInterval { get; init; } = 0.5f;
    /// <summary>What a boss (the Warboss and the Boss Orks) takes of each burn tick (ours: they carry 10x the health, and a crowd of shooters would melt them with a flat share).</summary>
    public float PlasmaBurnBossShare { get; init; } = 0.25f;
    /// <summary>The burn ticks one hit leaves (3 s at 0.5 s: 6).</summary>
    public int PlasmaBurnTicks => Math.Max(1, (int)MathF.Round(PlasmaBurnSeconds / MathF.Max(0.05f, PlasmaBurnInterval)));

    /// <summary>Whether <paramref name="weapon"/> is a plasma weapon (the names hold <c>plasma</c>: the gun, the pistol and the cannon of either side).</summary>
    public static bool IsPlasma(WeaponSpec weapon) => weapon.Name.Contains("plasma", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="type"/> is a boss, which takes <see cref="PlasmaBurnBossShare"/> of each burn tick.</summary>
    public static bool IsBoss(MusouEnemyType type) => type is MusouEnemyType.Warboss or MusouEnemyType.BossBoy or MusouEnemyType.BossShootaBoy or MusouEnemyType.BossArdBoy or MusouEnemyType.BossNob;

    /// <summary>Whether a shot of <paramref name="weapon"/> pierces every enemy and burns them.</summary>
    public bool PlasmaShot(WeaponSpec weapon) => PlasmaPierces && IsPlasma(weapon);

    /// <summary>Whether a thrown item is a frag (a blind grenade, a melta bomb or stims are not).</summary>
    public static bool IsFrag(ThrownSpec item) => item.Stim is null && !item.IsBlindGrenade && item.Name.Contains("frag", StringComparison.OrdinalIgnoreCase);
}
