namespace SmSim;

/// <summary>
/// Combat stimulants (perks/inert-2; the equipment <c>sm_mp_eq_stimpack</c>, shared by both sides; read by <c>SmContent.StimTuning</c>).
/// Used like a thrown item (the Grenade button, <see cref="ThrownSpec.Count"/> carried, the item's cooldown) but nothing leaves the hand:
/// at the use's release (<see cref="UseRelease"/>, the graph's <c>StartAction</c> event) the stim's <c>actn_stims_activated</c> applies
/// its modifiers to the user for <see cref="Lifetime"/> seconds (<see cref="PlayerState.StimLeft"/>):
/// <list type="bullet">
/// <item><c>received_*_damage_health_only</c> multiplications: the part of a hit that reaches health is scaled by the multiplier of its
/// damage type (<see cref="HealthTaken"/>); the armour shield part is not.</item>
/// <item><c>melee_damage</c> multiplication: the user's melee hits (attacks, the charge, the dash attack, the slash) deal that much more
/// (<see cref="MeleeDealt"/>; interpretation: blasts a melee step sets off are not melee hits).</item>
/// <item><c>apply_upgrade_string mp_reaction_immunity</c>: no hit reaction while it lasts (<see cref="ReactionImmune"/>; read as for the slams' ability, Weapons C).</item>
/// </list>
/// Master-Crafted Wargear's entry (its variant) is longer and stronger; Favor of the Armory's carries two.
/// </summary>
/// <param name="Lifetime">The modifiers' <c>lifetime</c> (s; 5, Master-Crafted 7). The ability's own <c>duration</c> (6) is not used (its meaning beside the lifetime is unknown).</param>
/// <param name="UseRelease">Seconds from the press to the effect (the "Equip - StimPack" sequencer's <c>StartAction</c>: 0.2 of the 1.5 s clip).</param>
/// <param name="UseDuration">Seconds the use blocks the weapon (its <c>BRANCH_UseEquipment_End</c>: 0.6 of the clip).</param>
public sealed record StimSpec(float Lifetime, float UseRelease, float UseDuration)
{
    /// <summary><c>received_world_damage_health_only</c> (no world damage is simulated but the kill volumes, which kill outright).</summary>
    public float ReceivedWorld { get; init; } = 1f;
    /// <summary><c>received_ranged_damage_health_only</c>.</summary>
    public float ReceivedRanged { get; init; } = 1f;
    /// <summary><c>received_melee_damage_health_only</c>.</summary>
    public float ReceivedMelee { get; init; } = 1f;
    /// <summary><c>received_explosive_damage_health_only</c>.</summary>
    public float ReceivedExplosive { get; init; } = 1f;
    /// <summary><c>melee_damage</c> (multiplication; the Master-Crafted one is <c>exclusive</c>: there is no other melee-damage modifier to exclude).</summary>
    public float MeleeDamage { get; init; } = 1f;
    /// <summary>Whether the stim applies <c>mp_reaction_immunity</c> for its lifetime.</summary>
    public bool ReactionImmune { get; init; }
    /// <summary>The use's clip (client only; not hashed): the "Equip - StimPack" sequencer's.</summary>
    public string Animation { get; init; } = "";

    /// <summary>The health-only multiplier for damage of <paramref name="type"/> (unknown types: the ranged one, as a shot's).</summary>
    public float Received(DamageType type) => type switch
    {
        DamageType.Melee => ReceivedMelee,
        DamageType.Explosive => ReceivedExplosive,
        _ => ReceivedRanged,
    };

    /// <summary>Whether the values can be simulated (finite, a positive lifetime, multipliers not negative, release within the use).</summary>
    public bool Valid =>
        float.IsFinite(Lifetime) && Lifetime > 0f && float.IsFinite(UseRelease) && UseRelease >= 0f && float.IsFinite(UseDuration) && UseDuration >= UseRelease
        && new[] { ReceivedWorld, ReceivedRanged, ReceivedMelee, ReceivedExplosive, MeleeDamage }.All(f => float.IsFinite(f) && f >= 0f);

    internal static void Write(BinaryWriter w, StimSpec s)
    {
        foreach (float f in new[] { s.Lifetime, s.UseRelease, s.UseDuration, s.ReceivedWorld, s.ReceivedRanged, s.ReceivedMelee, s.ReceivedExplosive, s.MeleeDamage }) w.Write(f);
        w.Write(s.ReactionImmune);
    }
}

/// <summary>The stim's effect on a player (see <see cref="StimSpec"/>).</summary>
public static class Stims
{
    /// <summary>The stim working on <paramref name="s"/> (alive, <see cref="PlayerState.StimLeft"/> above 0, its equipment a stim), or null.</summary>
    public static StimSpec? Active(SimConfig c, PlayerState s) =>
        s.Alive && s.StimLeft > 0f && Loadouts.Equipment(c, s)?.Stim is { } stim ? stim : null;

    /// <summary>The multiplier on the health part of a hit of <paramref name="type"/> that <paramref name="victim"/> takes (1 without a stim).</summary>
    public static float HealthTaken(SimConfig c, PlayerState victim, DamageType type) => Active(c, victim)?.Received(type) ?? 1f;

    /// <summary>The multiplier on the melee damage <paramref name="attacker"/> deals (1 without a stim).</summary>
    public static float MeleeDealt(SimConfig c, PlayerState attacker) => Active(c, attacker)?.MeleeDamage ?? 1f;

    /// <summary>Whether a stim keeps <paramref name="s"/> out of hit reactions now.</summary>
    public static bool ReactionImmune(SimConfig c, PlayerState s) => Active(c, s)?.ReactionImmune == true;

    /// <summary>
    /// The own record to replay from at a reconcile. The stim's time left is not on the wire (the snapshot budget is full), so it is derived from
    /// what is (review, perks/inert-2): the server starts the item's cooldown and the stim in the same release tick and counts both down by the tick,
    /// so while the record's <see cref="PlayerState.GrenadeCooldown"/> runs the stim has <c>Lifetime − (Cooldown − GrenadeCooldown)</c> left
    /// (a use the server interrupted before its release, or never got, starts no cooldown and so no stim; a stall cannot lose a real one). Past the
    /// cooldown (Master-Crafted's 7 s outlast its 6 s) only the prediction knows: its value is kept when it agrees with the record on life, class,
    /// item, count and a finished cooldown, never above what can remain then (<c>Lifetime − Cooldown</c>); else 0.
    /// </summary>
    public static PlayerState Carry(SimConfig c, PlayerState record, PlayerState? predicted)
    {
        if (!record.Alive || Loadouts.Equipment(c, record) is not { Stim: { } stim } item) return record with { StimLeft = 0f };
        if (record.GrenadeCooldown > 0f)
        {
            float left = stim.Lifetime - (item.Cooldown - record.GrenadeCooldown);
            return record with { StimLeft = left > 1e-4f ? MathF.Min(left, stim.Lifetime) : 0f };
        }
        bool agrees = predicted is { } p && p.Alive && p.Class == record.Class && p.Equipment == record.Equipment
            && p.Grenades == record.Grenades && p.GrenadeCooldown <= 0f;
        float tail = MathF.Max(0f, stim.Lifetime - item.Cooldown);
        return record with { StimLeft = agrees ? Math.Clamp(predicted!.Value.StimLeft, 0f, tail) : 0f };
    }
}
