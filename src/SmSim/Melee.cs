namespace SmSim;

/// <summary>
/// One attack of a melee combo, in seconds from the attack's start. Read from the install by
/// <c>SmContent.MeleeTuning</c> (behavior graph tags and wargear); see spec/behavior.md.
/// </summary>
/// <param name="Duration">Length of the attack's animation.</param>
/// <param name="HitTime">When the hit lands (the graph's <c>ACTN_Hit_Targets_Multiplayer</c> event).</param>
/// <param name="ComboOpen">A melee press (buffered) moves on to the next attack from here …</param>
/// <param name="ComboClose">… until here (<c>BRANCH_Melee_Combo</c>).</param>
/// <param name="RangedAt">From here firing cancels the attack (<c>BRANCH_ToRanged</c>).</param>
/// <param name="MoveAt">From here movement input applies again (<c>BRANCH_Melee_Draw_Run</c> / <c>BRANCH_Melee_Run</c>).</param>
/// <param name="Damage">To the primary target (wargear <c>do_damage/damage_general</c>, default entry).</param>
/// <param name="SecondaryDamage">To other targets in reach (wargear <c>secondary_target_actions</c>).</param>
/// <param name="Lunge">The attack's <c>AttackDistance</c> tag, metres (recorded; the lunge itself is <see cref="Reach"/>'s root motion, feel/melee-lock).</param>
/// <param name="HealthMultiplier">The weapon's own body multiplier for health damage (wargear <c>damage_material_multipliers/default_body</c>).</param>
/// <param name="ShieldMultiplier">… and for armour shield damage (<c>armourshield_damage_material_multipliers/default_body</c>).</param>
public readonly record struct MeleeStep(
    float Duration, float HitTime, float ComboOpen, float ComboClose, float RangedAt, float MoveAt,
    float Damage, float SecondaryDamage, float Lunge, float HealthMultiplier = 1f, float ShieldMultiplier = 1f)
{
    /// <summary>
    /// The reaction the hit puts the primary target in (the ability's <c>primary_target_actions</c> <c>trigger_reaction</c>,
    /// mapped by the player blueprint's <c>multiplayer_reaction_mapping</c>; M28). Kind None: none.
    /// </summary>
    public ReactionEntry PrimaryReaction { get; init; }
    /// <summary>The reaction of every other target in reach (<c>secondary_target_actions</c>).</summary>
    public ReactionEntry SecondaryReaction { get; init; }
    /// <summary>
    /// A window of the attack (seconds from its start) without <c>BRANCH_HitReact</c> in its sequencer: a hit then puts the
    /// attacker in no reaction (the attack goes on). NoReactTo ≤ NoReactFrom: none (always interruptible).
    /// </summary>
    public float NoReactFrom { get; init; }
    /// <summary>End of the window without <c>BRANCH_HitReact</c>.</summary>
    public float NoReactTo { get; init; }
    /// <summary>
    /// A blast the attack sets off at <see cref="HitTime"/> around the attacker (the heavy combo's foot stomp: the wargear's
    /// <c>displace_footstomp</c> triggered by the sequencer's <c>DISPLACE_FootStomp</c> action), or null.
    /// </summary>
    public SplashSpec? Stomp { get; init; }
    /// <summary>
    /// Health the attacker gets back for the primary target the hit lands on while it is below full health (M43: the heal-on-hit
    /// perks, e.g. Swordsman's Zeal; the hit's <c>target_self_action</c> <c>do_heal/health</c> behind a <c>prerequisites\health</c>
    /// of at most 0.9999 of the maximum). 0: none.
    /// </summary>
    public float Heal { get; init; }
    /// <summary>As <see cref="Heal"/> for each other target the hit lands on (<c>secondary_target_actions</c>).</summary>
    public float SecondaryHeal { get; init; }
    /// <summary>The hit's damage type (<c>damagetype</c>; M43, the received-damage perks); melee unless the file says otherwise.</summary>
    public DamageType DamageType { get; init; } = DamageType.Melee;

    /// <summary>
    /// Weapons C: the primary target's armour-shield damage (the wargear's separate <c>damage_armourshield</c>; the power axe's 250, Killing
    /// Blow's 2000). As a blast's and the charge's: it comes off the armour shield only, before <see cref="Damage"/> is applied.
    /// </summary>
    public float ShieldDamage { get; init; }
    /// <summary>As <see cref="ShieldDamage"/> for every other target in reach.</summary>
    public float SecondaryShieldDamage { get; init; }
    /// <summary>
    /// Weapons C, light attacks: a melee press does not continue the combo from this attack (its graph state has no light transition out:
    /// the power sword's X4, the hammer's X3, the Devastator's kick). False (the default): the press goes on to the next attack, from the
    /// last one to attack 1 (the knife and chainsword graphs).
    /// </summary>
    public bool StringEnd { get; init; }
    /// <summary>
    /// Weapons C, light attacks: the heavy strike (1-based) a heavy press in the combo window chains to; 0 (the default): strike n + 1 after
    /// attack n when the weapon has it; -1: none (the hammer's X3, the sword's and axe's X4). The Devastator's kick chains to strike 1, the stomp.
    /// </summary>
    public int HeavyNext { get; init; }
    /// <summary>
    /// Weapons C, heavy strikes: the strike (1-based, the heavy list) a heavy press in this strike's Y-chain window
    /// (<see cref="YComboOpen"/> to <see cref="YComboClose"/>; the graph's <c>BRANCH_Melee_Y_Combo</c> or the hammer's <c>BRANCH_To_Slam</c>) chains
    /// to: the Y,Y finisher. 0 (the default): a heavy press during this strike does nothing.
    /// </summary>
    public int YNext { get; init; }
    /// <summary>Start of the Y-chain window, seconds from the strike's start.</summary>
    public float YComboOpen { get; init; }
    /// <summary>End of the Y-chain window.</summary>
    public float YComboClose { get; init; }

    /// <summary>
    /// feel/melee-lock (protocol 32): the attack's alignment window, distance variants and root motion (<see cref="MeleeLock"/>), or null (tests,
    /// a read without the rig's motion: no lunge, and the view turns the attack as before).
    /// </summary>
    public MeleeReach? Reach { get; init; }

    /// <summary>
    /// sim/stomp-in-place (protocol 49; owner, 2026-10-31): the strike keeps the body exactly where it stands until <see cref="MoveAt"/> (no lunge, no
    /// burst, no coasting on the speed it began with): the Devastator's and the Havoc's stomp. False (the default): every other strike.
    /// </summary>
    public bool InPlace { get; init; }

    /// <summary>
    /// anim/hammer-swing (owner, 2026-11-01: the thunder hammer's standard swings "step forward" too far): a factor on this strike's forward lunge on top of
    /// <see cref="SimConfig.MeleeLungeScale"/> (<see cref="MeleeLock.LungeStep"/>); 1 (the default): none. Set by the content loader for the hammer's light strikes
    /// (<c>MeleeTuning.WeaponMap.LightLungeFactor</c>); hashed only when it is not 1.
    /// </summary>
    public float LungeFactor { get; init; } = 1f;
    /// <summary><see cref="LungeFactor"/> as applied (a factor that is not finite or not above 0 counts as 1).</summary>
    public float LungeFactorApplied => float.IsFinite(LungeFactor) && LungeFactor > 0f ? LungeFactor : 1f;

    /// <summary>
    /// feel/locks-sprint-stop: the attack's <c>BRANCH_Evade</c> (the start of its first window, seconds from the attack's start): from here (scaled by
    /// <see cref="SimConfig.ActionLockScale"/>, never before <see cref="HitTime"/>) a Jump press ends the attack and evades or takes off in the same tick
    /// (<see cref="Combat"/>). Infinity (the default: tests, a sequencer without the tag): no Jump during the attack, as before.
    /// </summary>
    public float EvadeAt { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// sim/light-melee-flinch (protocol 52; the owner, 2026-11-01: "only stun strikes, charges, stomps, and kicks should heavy stagger/stun"): the strike is the heavy
    /// classes' kick (the Devastator's and the Havoc's light attack, <c>ability_displace_kick</c>; set by the content reader): though a light press, its hit keeps the
    /// data's reaction (the stagger). False (the default): a light attack of the normal combo, which only flinches (<see cref="Spread.Flinch"/>).
    /// </summary>
    public bool Kick { get; init; }

    /// <summary>Writes the fields added in M28 (and M43, Weapons C) for a config hash.</summary>
    internal void WriteExtras(BinaryWriter w)
    {
        PrimaryReaction.Write(w); SecondaryReaction.Write(w);
        w.Write(NoReactFrom); w.Write(NoReactTo);
        ProjectileSpec.WriteSplash(w, Stomp);
        w.Write(Heal); w.Write(SecondaryHeal); w.Write((byte)DamageType);
        // Weapons C: only when set, so that a step without them hashes as before (the pinned config hashes stay).
        if (ShieldDamage != 0f || SecondaryShieldDamage != 0f || StringEnd || HeavyNext != 0 || YNext != 0 || YComboOpen != 0f || YComboClose != 0f)
        {
            w.Write((byte)0xC3);
            w.Write(ShieldDamage); w.Write(SecondaryShieldDamage); w.Write(StringEnd); w.Write(HeavyNext); w.Write(YNext); w.Write(YComboOpen); w.Write(YComboClose);
        }
        // feel/melee-lock: only when set, so a step without it hashes as before.
        if (Reach is { } reach) { w.Write((byte)0xA1); reach.Write(w); }
        if (InPlace) w.Write((byte)0xA2);   // sim/stomp-in-place: only when set
        if (LungeFactorApplied != 1f) { w.Write((byte)0xA3); w.Write(LungeFactorApplied); }   // anim/hammer-swing: only when not 1
        if (Kick) w.Write((byte)0xA4);   // sim/light-melee-flinch: only when set
        // feel/locks-sprint-stop: only when read, so a step without it hashes as before.
        if (float.IsFinite(EvadeAt)) { w.Write((byte)0xE5); w.Write(EvadeAt); }
    }
}

/// <summary>Weapons C: where a buffered heavy press leads (the light-to-heavy rule and the Y,Y chain; <see cref="Combat"/>).</summary>
public static class MeleeChain
{
    /// <summary>
    /// The heavy strike (1-based) a heavy press during attack <paramref name="step"/> (<paramref name="heavy"/>: a heavy strike) of step
    /// <paramref name="m"/> leads to, or 0 when none: after light attack n, <see cref="MeleeStep.HeavyNext"/> or else strike n + 1; after a
    /// heavy strike, its <see cref="MeleeStep.YNext"/>. Strikes beyond <paramref name="heavyCount"/> are none.
    /// </summary>
    public static int HeavyTarget(MeleeStep m, int step, bool heavy, int heavyCount)
    {
        int next = heavy ? m.YNext : m.HeavyNext > 0 ? m.HeavyNext : m.HeavyNext < 0 ? 0 : step + 1;
        return next >= 1 && next <= heavyCount ? next : 0;
    }

    /// <summary>The window in which a buffered press of the given kind moves on from step <paramref name="m"/>: the Y-chain window for a heavy press during a heavy strike, else the combo window.</summary>
    public static (float Open, float Close) Window(MeleeStep m, bool heavy, bool queuedHeavy) =>
        heavy && queuedHeavy ? (m.YComboOpen, m.YComboClose) : (m.ComboOpen, m.ComboClose);
}
