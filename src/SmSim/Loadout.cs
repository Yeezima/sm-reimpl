using System.Numerics;

namespace SmSim;

/// <summary>
/// A weapon slot: <see cref="Ranged"/> the primary (key 2), <see cref="Pistol"/> (key 1) and, with the Weapon Versatility perk (Tactical only; protocol 44),
/// <see cref="Second"/> the second primary (key 3).
/// </summary>
public enum WeaponSlot : byte { Ranged = 0, Pistol = 1, Second = 2 }
public enum TriggerKind : byte { FullAuto = 0, SemiAuto = 1 }

/// <summary>
/// A hit-scan firearm as the simulation uses it, read from its wargear file by <c>SmContent.WeaponTuning</c> (default
/// entries; see spec/behavior.md). Units: seconds, metres (assumed), damage on the health scale.
/// </summary>
/// <param name="Name">Wargear file name, e.g. "sm_mp_ranged_bolter".</param>
/// <param name="Trigger">Full auto fires while held; semi auto fires once per press (wargear trigger policy).</param>
public sealed record WeaponSpec(
    string Name, WeaponSlot Slot, TriggerKind Trigger,
    float Damage, float ShotCooldown, int ClipSize, int ReserveSize, float ReloadTime, float MaxRange,
    float FallOffStart, float FallOffEnd, float FallOffEndMultiplier,
    float HeadMultiplier, float BodyMultiplier, float ShieldHeadMultiplier, float ShieldBodyMultiplier)
{
    /// <summary>Heat (wargear <c>common_data/heat</c> and the primary ability's <c>apply_heat</c>); null: the weapon does not heat up.</summary>
    public HeatSpec? Heat { get; init; }

    /// <summary>
    /// How the basic shot travels. <see cref="DeliveryKind.Raycast"/>: the hit-scan fields above apply. <see cref="DeliveryKind.Projectile"/>:
    /// the shot is <see cref="Projectile"/>; <see cref="Damage"/> and the multipliers are then its direct-hit damage
    /// (the delivery's <c>hit_sim</c>, e.g. the grenade launcher's 10) and the fall-off fields are unused.
    /// A simulation that only knows hit-scan must not treat a projectile weapon as one.
    /// </summary>
    public DeliveryKind Delivery { get; init; } = DeliveryKind.Raycast;

    /// <summary>The basic shot's projectile when <see cref="Delivery"/> is <see cref="DeliveryKind.Projectile"/>, else null.</summary>
    public ProjectileSpec? Projectile { get; init; }

    /// <summary>Ammunition one basic shot uses (<c>delivery/ammo_used/…/amount</c>).</summary>
    public int AmmoPerShot { get; init; } = 1;

    /// <summary>
    /// The weapon's charged fire (a <c>charge_ranged</c> ability), or null. Simulated by <c>Combat.StepSelf</c> on Fire whichever
    /// ability holds it (M31, owner's decision 2026-10-28): a press starts the charge, a release before the first stage with a
    /// shot fires the basic shot (the other ability), a later release the charged shot.
    /// </summary>
    public ChargeSpec? Charge { get; init; }

    /// <summary>
    /// Seconds from the trigger to the shot when the weapon is down (M31; hip fire): the weapon family's "Set Weapon Ready"
    /// <c>BRANCH_to_Fire</c> time (rifle 0.2, pistol 0.167, heavy bolter 0.333 s on the install; <c>SmContent.HipFireAnimations</c>).
    /// 0: the shot never waits (no data, tests).
    /// </summary>
    public float RaiseDelay { get; init; }

    /// <summary>
    /// The weapon is always up and never waits for a raise (M31): the heavy bolter's navigation graph has no relaxed posture, and
    /// the rifle graph enters its ready posture whenever a grenade launcher, melta, lascannon or plasma cannon is carried.
    /// </summary>
    public bool AlwaysReady { get; init; }

    /// <summary>Whether a press on this weapon waits for the raise when it is down (<see cref="RaiseDelay"/> &gt; 0, not <see cref="AlwaysReady"/>).</summary>
    public bool WaitsToRaise => !AlwaysReady && RaiseDelay > 0f;

    /// <summary>
    /// Delays, in seconds, after which the weapon's remotely detonated projectiles go off, oldest first (the grenade
    /// launcher's <c>ability_utility</c> <c>detonate_projectile</c> delays 0 / 0.25 / 0.32 on the install); a projectile
    /// beyond the list uses the last delay; empty: all at once. Remote detonation applies to a projectile weapon whose
    /// projectile has a <see cref="ProjectileSpec.MaxActive"/> limit (<see cref="Shots.RemoteDetonated"/>).
    /// </summary>
    public IReadOnlyList<float> DetonationDelays { get; init; } = [];

    /// <summary>The basic shot's cone when <see cref="Delivery"/> is <see cref="DeliveryKind.Cone"/> (the melta's shapecast), else null.</summary>
    public ConeSpec? Cone { get; init; }

    /// <summary>
    /// A clip that refills itself from the reserve (the melta: <c>recharge_delay</c>, <c>ammo_recharged_per_second</c>), or
    /// null. Such a weapon has no reload input: <c>Combat.StepPlayer</c> starts a recharge whenever the clip is below its
    /// capacity and reserve is left; each one takes Delay + 1 / PerSecond seconds and moves one round (**interpretation**).
    /// </summary>
    public RechargeSpec? Recharge { get; init; }

    /// <summary>The basic hit-scan shot's penetration through props (M39; the lascannon and stalker bolter), or null: it stops at the first surface.</summary>
    public PierceSpec? Pierce { get; init; }

    /// <summary>The basic shot's penetration with a perk (M39; the bolter's Kraken Bolts), in file order; <see cref="Pierces.Of"/> picks.</summary>
    public IReadOnlyList<UpgradePierce> PerkPierces { get; init; } = [];

    /// <summary>The basic shot's damage type (its <c>hit_sim</c> <c>do_damage</c> <c>damagetype</c>; M43: the received-damage perks key on it).</summary>
    public DamageType HitType { get; init; }

    /// <summary>
    /// Rounds one trigger pull fires (the basic shot's <c>basic_weapon_tuning</c> <c>burst_length</c>; M43, the stalker bolter's Burst
    /// Fire perk: 2); 0 or 1: one. The later rounds leave <see cref="BurstInterval"/> apart (<c>time_between_shots</c>);
    /// <see cref="ShotCooldown"/> counts from the first (interpretation; <see cref="Combat.StepPlayer"/>).
    /// </summary>
    public int BurstLength { get; init; }
    /// <summary>Seconds between a burst's rounds (<c>time_between_shots</c>); used only with <see cref="BurstLength"/> &gt; 1.</summary>
    public float BurstInterval { get; init; }

    /// <summary>
    /// The scatter and handling sets per stance (weapons/spread: wargear <c>kick_back_and_scatter_values</c>, <c>climb_sway_scatter</c>;
    /// <see cref="SmSim.Spread"/>), or null: the shot goes exactly along its aim.
    /// </summary>
    public SpreadSpec? Spread { get; init; }

    /// <summary>Longest burst a config may have (the own record carries the rounds left in 2 bits, protocol 23).</summary>
    public const int MaxBurstLength = 4;

    /// <summary>Whether a trigger pull fires a burst (<see cref="BurstLength"/> &gt; 1).</summary>
    public bool Bursts => BurstLength > 1;

    /// <summary>The heavy bolter's braced fire on Reload (fidelity gap 7; <see cref="SmSim.Brace"/>), or null.</summary>
    public BraceSpec? Brace { get; init; }

    /// <summary>The plasma weapons' heat vent on Reload (fidelity gap 7; <see cref="SmSim.Vent"/>), or null.</summary>
    public VentSpec? Vent { get; init; }

    /// <summary>
    /// The weapon as perks change it (M43): read again with the upgrade strings granted (<see cref="UpgradeVariant{T}"/>); only
    /// variants that differ from this spec. <see cref="With"/> picks one; a variant has no variants of its own.
    /// </summary>
    public IReadOnlyList<UpgradeVariant<WeaponSpec>> Variants { get; init; } = [];

    /// <summary>The weapon for a player with <paramref name="upgrades"/> (<see cref="SmSim.Variants.Pick"/>); this spec without a matching variant.</summary>
    public WeaponSpec With(PerkUpgrades upgrades) => SmSim.Variants.Pick(this, Variants, upgrades);

    /// <summary>The basic shot's own damage as a <see cref="DamageSpec"/> (hit-scan damage, or a projectile's direct hit).</summary>
    public DamageSpec DirectDamage => new(Damage, 0f, HeadMultiplier, BodyMultiplier, ShieldHeadMultiplier, ShieldBodyMultiplier, false) { Type = HitType };

    /// <summary>Writes every field (the variants included) for a config hash.</summary>
    internal void WriteHash(BinaryWriter w)
    {
        w.Write(Name); w.Write((byte)Slot); w.Write((byte)Trigger);
        w.Write(Damage); w.Write(ShotCooldown); w.Write(ClipSize); w.Write(ReserveSize); w.Write(ReloadTime); w.Write(MaxRange);
        w.Write(FallOffStart); w.Write(FallOffEnd); w.Write(FallOffEndMultiplier);
        w.Write(HeadMultiplier); w.Write(BodyMultiplier); w.Write(ShieldHeadMultiplier); w.Write(ShieldBodyMultiplier);
        w.Write(Heat is not null);
        if (Heat is { } heat)
        {
            w.Write(heat.PerShot); w.Write(heat.Overheat); w.Write(heat.HighHeat); w.Write(heat.Recovery);
            w.Write(heat.CoolRate); w.Write(heat.CoolDelay); w.Write(heat.CoolDelayAfterOverheat);
        }
        WriteHashExtras(w);
        w.Write((byte)HitType); w.Write(BurstLength); w.Write(BurstInterval);
        SpreadSpec.Write(w, Spread);   // weapons/spread
        SmSim.Variants.Write(w, Variants, (b, v) => v.WriteHash(b));
    }

    /// <summary>
    /// Writes <see cref="Delivery"/>, <see cref="Projectile"/>, <see cref="AmmoPerShot"/>, <see cref="Charge"/> and
    /// <see cref="DetonationDelays"/> for a config hash (little-endian; strings length-prefixed UTF-8, as
    /// <see cref="SimConfig.Hash"/> writes the other fields).
    /// </summary>
    public void WriteHashExtras(BinaryWriter w)
    {
        w.Write((byte)Delivery); w.Write(AmmoPerShot);
        w.Write(RaiseDelay); w.Write(AlwaysReady);
        ProjectileSpec.Write(w, Projectile);
        w.Write(DetonationDelays.Count); foreach (float d in DetonationDelays) w.Write(d);
        ConeSpec.Write(w, Cone);
        w.Write(Recharge is not null);
        if (Recharge is { } r) { w.Write(r.Delay); w.Write(r.PerSecond); }
        PierceSpec.Write(w, Pierce);
        w.Write(PerkPierces.Count); foreach (var p in PerkPierces) { w.Write(p.Upgrade); PierceSpec.Write(w, p.Spec); }
        // Fidelity gap 7: the brace, the vent and the overheat's speed modifier, behind a marker byte only when the weapon has one (a weapon
        // without them hashes as before).
        if (Brace is not null || Vent is not null || Heat is { OverheatSpeedTime: > 0f } || Heat is { OverheatSpeed: not 1f })
        {
            w.Write((byte)0xB7);
            BraceSpec.Write(w, Brace); VentSpec.Write(w, Vent);
            w.Write(Heat?.OverheatSpeed ?? 1f); w.Write(Heat?.OverheatSpeedTime ?? 0f);
        }
        w.Write(Charge is not null);
        if (Charge is not { } c) return;
        w.Write(c.Ability); w.Write(c.MaxChargeTime); w.Write(c.MinTimeMultiplier); w.Write(c.ReloadCancels); w.Write(c.AmmoToStart);
        w.Write(c.Stages.Count);
        foreach (var s in c.Stages)
        {
            w.Write(s.Name); w.Write(s.MinTime); w.Write(s.HeatPerSecond); w.Write(s.MoveSpeedMultiplier);
            w.Write(s.Shot is not null);
            if (s.Shot is not { } shot) continue;
            w.Write((byte)shot.Delivery); DamageSpec.Write(w, shot.Damage); w.Write(shot.MaxRange); w.Write(shot.ShotCooldown);
            w.Write(shot.AmmoCost); w.Write(shot.HeatPerShot); w.Write(shot.FireDelay);
            ProjectileSpec.Write(w, shot.Projectile);
            PierceSpec.Write(w, shot.Pierce);
        }
    }
}

/// <summary>How a shot reaches its target.</summary>
public enum DeliveryKind : byte { Raycast = 0, Projectile = 1, Cone = 2 }

/// <summary>A self-refilling clip (wargear <c>ammunition_info</c>): seconds before the first round and rounds per second.</summary>
public sealed record RechargeSpec(float Delay, float PerSecond)
{
    /// <summary>Seconds one recharged round takes (Delay + 1 / PerSecond).</summary>
    public float RoundTime => Delay + 1f / PerSecond;
}

/// <summary>
/// A shapecast cone (<c>ranged_delivery_type/…/shapecast</c> with <c>shape\cone</c>; the melta). Units as in the file:
/// metres (assumed), degrees. **Interpretation** (spec/behavior.md): <see cref="Angle"/> is the full apex angle (another cone
/// in the files has 135, which as a half angle would be no cone); the apex sits <see cref="StartLength"/> behind the muzzle
/// (the files always have <see cref="Offset"/> = −StartLength); the volume reaches <see cref="Length"/> ahead of the muzzle.
/// <see cref="FallOff"/>: (distance, damage multiplier) from <c>damage_fall_off</c>; <see cref="CoverageFallOff"/>:
/// (coverage, damage multiplier) from <c>coverage_fall_off</c>, ascending coverage.
/// </summary>
public sealed record ConeSpec(float Length, float Angle, float StartLength, float Offset, DamageSpec Damage,
    IReadOnlyList<DistancePoint> FallOff, IReadOnlyList<DistancePoint> CoverageFallOff)
{
    /// <summary>Half the apex angle, radians.</summary>
    public float HalfAngle => Angle * 0.5f * (MathF.PI / 180f);

    internal static void Write(BinaryWriter w, ConeSpec? c)
    {
        w.Write(c is not null);
        if (c is null) return;
        w.Write(c.Length); w.Write(c.Angle); w.Write(c.StartLength); w.Write(c.Offset);
        DamageSpec.Write(w, c.Damage);
        ProjectileSpec.WritePoints(w, c.FallOff); ProjectileSpec.WritePoints(w, c.CoverageFallOff);
    }
}

/// <summary>
/// A rigid-body projectile's contact rules (<c>projectile_ext/rigid_info</c>; read only when <c>collision_type</c> is
/// <c>rigidbody</c>). "Reductions" are read as the fraction of each velocity part kept at a contact (**interpretation**:
/// a player hit keeps 5 %, so a grenade drops at the feet of whoever it hits; the ground keeps 30 % across and 40 % up).
/// Wall reductions are (angle in degrees between the incoming direction and the wall's surface — 0 grazing, 90 head-on —,
/// fraction kept) points: a grazing hit keeps all, a head-on one 20 % (**interpretation**). <see cref="GroundAngle"/>: a
/// surface whose normal is within this many degrees of up is ground, anything steeper is a wall (**interpretation**).
/// </summary>
public sealed record RigidSpec(float GroundAngle, float GroundHorizontalReduction, float GroundVerticalReduction,
    float EntityHorizontalReduction, float EntityVerticalReduction,
    IReadOnlyList<DistancePoint> WallHorizontalReduction, IReadOnlyList<DistancePoint> WallVerticalReduction,
    float DelayBeforeCollision, float DelayBetweenCollision)
{
    /// <summary>The blueprint's Havok body material (<c>rigid_body_options_ext/friction_override</c>, frag 2); NaN when missing. Used only with <see cref="GroundMaterial"/>.</summary>
    public float BodyFriction { get; init; } = float.NaN;
    /// <summary>The blueprint's Havok body material (<c>rigid_body_options_ext/restituation_override</c>, frag 0.65); NaN when missing. Used only with <see cref="GroundMaterial"/>.</summary>
    public float BodyRestitution { get; init; } = float.NaN;
    /// <summary>
    /// fix/grenades (protocol 37; the owner's change, 2026-10-29: "grenades shouldn't bounce so far when thrown"; ours): a ground bounce first loses speed
    /// to the body's own material, then keeps the data's fractions: the speed along the normal comes back × <see cref="BodyRestitution"/>, the speed along the
    /// surface loses up to <see cref="BodyFriction"/> × (1 + restitution) × the normal speed (a Coulomb friction impulse), then the ground reductions apply
    /// as before. False (the data's rule as read): the reductions alone. Hashed only when on.
    /// </summary>
    public bool GroundMaterial { get; init; }

    /// <summary>Whether <see cref="GroundMaterial"/> can apply: both material values finite and not negative.</summary>
    public bool HasBodyMaterial => float.IsFinite(BodyFriction) && BodyFriction >= 0f && float.IsFinite(BodyRestitution) && BodyRestitution >= 0f;

    /// <summary>
    /// A ground bounce's velocity before the reductions with <see cref="GroundMaterial"/> (and a usable material): the incoming <paramref name="velocity"/>
    /// on a surface of unit <paramref name="normal"/>, its normal part reflected × restitution (clamped to [0, 1]), its tangential part shortened by the
    /// friction impulse (never reversed). Otherwise the plain reflection.
    /// </summary>
    public Vector3 Bounce(Vector3 velocity, Vector3 normal)
    {
        float vn = Vector3.Dot(velocity, normal);
        var reflected = velocity - 2f * vn * normal;
        if (!GroundMaterial || !HasBodyMaterial || vn >= 0f) return reflected;
        float e = Math.Clamp(BodyRestitution, 0f, 1f);
        var tangent = velocity - vn * normal;
        float along = tangent.Length();
        float kept = MathF.Max(0f, along - BodyFriction * (1f + e) * -vn);
        return (along > 1e-6f ? tangent * (kept / along) : Vector3.Zero) + normal * (e * -vn);
    }

    internal static void Write(BinaryWriter w, RigidSpec? r)
    {
        w.Write(r is not null);
        if (r is null) return;
        w.Write(r.GroundAngle); w.Write(r.GroundHorizontalReduction); w.Write(r.GroundVerticalReduction);
        w.Write(r.EntityHorizontalReduction); w.Write(r.EntityVerticalReduction);
        ProjectileSpec.WritePoints(w, r.WallHorizontalReduction); ProjectileSpec.WritePoints(w, r.WallVerticalReduction);
        w.Write(r.DelayBeforeCollision); w.Write(r.DelayBetweenCollision);
        if (r.GroundMaterial) { w.Write("ground-material"); w.Write(r.BodyFriction); w.Write(r.BodyRestitution); }   // fix/grenades: only when on (older hashes unchanged)
    }
}

/// <summary>
/// One <c>do_damage</c> command (units as in the file: damage on the health scale). <see cref="ShieldDamage"/> is the file's
/// separate <c>damage_armourshield</c> (e.g. the plasma gun's charged shot: 75 + 75); how the original combines it with
/// <see cref="Damage"/> is **not established**. Multipliers: <c>damage_material_multipliers</c> (health) and
/// <c>armourshield_damage_material_multipliers</c> (shield), <c>default_head</c> / <c>default_body</c>.
/// </summary>
/// <param name="ShieldBreaker">The file's <c>armourshield_breaker</c> (meaning not established).</param>
public sealed record DamageSpec(float Damage, float ShieldDamage, float HeadMultiplier, float BodyMultiplier,
    float ShieldHeadMultiplier, float ShieldBodyMultiplier, bool ShieldBreaker)
{
    /// <summary>The file's <c>damagetype</c> (M43: the received-damage perks key on it); Unknown when not read.</summary>
    public DamageType Type { get; init; }

    internal static void Write(BinaryWriter w, DamageSpec? d)
    {
        w.Write(d is not null);
        if (d is null) return;
        w.Write(d.Damage); w.Write(d.ShieldDamage); w.Write(d.HeadMultiplier); w.Write(d.BodyMultiplier);
        w.Write(d.ShieldHeadMultiplier); w.Write(d.ShieldBodyMultiplier); w.Write(d.ShieldBreaker); w.Write((byte)d.Type);
    }
}

/// <summary>A point of a piecewise table keyed by distance (fall-off intensity, gravity factor).</summary>
public readonly record struct DistancePoint(float Distance, float Value);

/// <summary>
/// An explosion (<c>area_of_effect</c> with a sphere shape) from a projectile's <c>detonate_action</c>.
/// <see cref="FallOff"/> lists the file's (distance, <c>intensity_value</c>) points in file order; how intensity scales
/// damage between points (steps or interpolation) is **not established**.
/// </summary>
/// <param name="LineOfSight">The file's <c>line_of_sight</c> (a target must be visible from the centre, assumed).</param>
/// <param name="IgnoreSelf">The file's <c>ignore_self</c>; false on every multiplayer projectile (the shooter can be hit, assumed).</param>
/// <param name="TargetFilter">e.g. "target_all" (friendly fire meaning not established).</param>
public sealed record SplashSpec(float Radius, DamageSpec Damage, IReadOnlyList<DistancePoint> FallOff, bool LineOfSight, bool IgnoreSelf, string TargetFilter)
{
    /// <summary>The blast's <c>lifetime</c> in seconds (0: applied once). With <see cref="Period"/> &gt; 0 it is a lingering area (the melta bomb's).</summary>
    public float Lifetime { get; init; }
    /// <summary>The file's <c>apply_every_this_many_seconds</c> (0: once).</summary>
    public float Period { get; init; }
    /// <summary>Whether this blast lingers: applied at the detonation and every Period seconds while younger than Lifetime.</summary>
    public bool Lingers => Lifetime > 0f && Period > 0f;
    /// <summary>
    /// The blast's <c>trigger_reaction/reaction_by_intensity</c> entries (multiplayer reactions only, M28): a target takes the
    /// entry for the blast's intensity at it (<see cref="Reaction.Pick"/>). Empty: no reaction.
    /// </summary>
    public IReadOnlyList<ReactionEntry> Reactions { get; init; } = [];
    /// <summary>The blast's <c>do_blindness/blindness_duration</c> in seconds (the blind grenade's, files: krak; 0: none).</summary>
    public float Blindness { get; init; }
    /// <summary>
    /// A blind grenade's blast (M37; the owner's knowledge of the original, 2026-10-28: the item the files name "krak" is the
    /// blind grenade, ammunition type <c>blind_grenade</c>, UI name "Blind Grenades"): it deals no damage, records no hit and
    /// starts no reaction or knockback; it blinds for <see cref="Blindness"/> every living player within <see cref="Radius"/>
    /// of the blast point whose eye it sees (always tested against the weapon collision), allies included, the owner too
    /// unless <see cref="IgnoreSelf"/>. <see cref="Damage"/>, <see cref="FallOff"/> and <see cref="Reactions"/> are not used.
    /// </summary>
    public bool BlindOnly { get; init; }
}

/// <summary>
/// A projectile blueprint (<c>ebps\projectiles\…</c>, <c>extensions/projectile_ext</c>). Units as in the file: speed in
/// distance units per second and lifetime in seconds (both assumed); angles in degrees (assumed).
/// </summary>
/// <param name="Blueprint">The blueprint path as the wargear names it.</param>
/// <param name="Lifetime">Seconds; 0 on the grenade launcher's grenade (assumed: no limit).</param>
/// <param name="CollisionType">"raycast" or "rigidbody" (the frag grenade).</param>
/// <param name="CollisionRadius"><c>raycast_shape/radius</c>; 0 when the blueprint has none (rigid bodies).</param>
/// <param name="Gravity"><c>gravity_factor_over_distance</c> (distance travelled, factor of gravity), file order.</param>
/// <param name="GroundLifetime">The file's <c>ground_lifetime</c> (seconds after landing, assumed: the frag grenade's fuse).</param>
/// <param name="ExtraAimAngle">The file's <c>extra_aim_angle</c> (degrees upward, assumed).</param>
/// <param name="MaxActive">The wargear's <c>max_active_projectiles</c> (0: no limit, assumed).</param>
/// <param name="DetonateDamage">A <c>do_damage</c> directly in <c>detonate_action</c> (the plasma cannon's charged shot), or null.</param>
public sealed record ProjectileSpec(string Blueprint, float Speed, float Lifetime, string CollisionType, float CollisionRadius,
    IReadOnlyList<DistancePoint> Gravity, bool DetonateOnCollision, bool DetonateWhenLifetimeOver, bool Sticky,
    int MaxBounces, float GroundLifetime, float ExtraAimAngle, int MaxActive, DamageSpec? DetonateDamage, SplashSpec? Splash)
{
    /// <summary>Contact rules of a rigid body (<see cref="CollisionType"/> "rigidbody"), else null (M12 bounce rules).</summary>
    public RigidSpec? Rigid { get; init; }
    /// <summary>The file's <c>stop_at_max_bounce</c>: a rigid body's contact after its last bounce stops it (ground) or drops it (wall).</summary>
    public bool StopAtMaxBounce { get; init; }
    /// <summary>Damaging blasts after <see cref="Splash"/> in the file (the melta bomb's second), in file order.</summary>
    public IReadOnlyList<SplashSpec> ExtraSplashes { get; init; } = [];

    /// <summary><see cref="Splash"/> (when present) then <see cref="ExtraSplashes"/>.</summary>
    public IEnumerable<SplashSpec> AllSplashes => Splash is { } s ? ExtraSplashes.Prepend(s) : ExtraSplashes;

    internal static void WritePoints(BinaryWriter w, IReadOnlyList<DistancePoint> points)
    {
        w.Write(points.Count); foreach (var f in points) { w.Write(f.Distance); w.Write(f.Value); }
    }

    internal static void WriteSplash(BinaryWriter w, SplashSpec? s)
    {
        w.Write(s is not null);
        if (s is null) return;
        w.Write(s.Radius); DamageSpec.Write(w, s.Damage);
        WritePoints(w, s.FallOff);
        w.Write(s.LineOfSight); w.Write(s.IgnoreSelf); w.Write(s.TargetFilter);
        w.Write(s.Lifetime); w.Write(s.Period);
        ReactionEntry.Write(w, s.Reactions); w.Write(s.Blindness); w.Write(s.BlindOnly);
    }

    internal static void Write(BinaryWriter w, ProjectileSpec? p)
    {
        w.Write(p is not null);
        if (p is null) return;
        w.Write(p.Blueprint); w.Write(p.Speed); w.Write(p.Lifetime); w.Write(p.CollisionType); w.Write(p.CollisionRadius);
        WritePoints(w, p.Gravity);
        w.Write(p.DetonateOnCollision); w.Write(p.DetonateWhenLifetimeOver); w.Write(p.Sticky); w.Write(p.MaxBounces);
        w.Write(p.GroundLifetime); w.Write(p.ExtraAimAngle); w.Write(p.MaxActive);
        DamageSpec.Write(w, p.DetonateDamage);
        WriteSplash(w, p.Splash);
        RigidSpec.Write(w, p.Rigid); w.Write(p.StopAtMaxBounce);
        w.Write(p.ExtraSplashes.Count); foreach (var s in p.ExtraSplashes) WriteSplash(w, s);
    }

    /// <summary>Writes a thrown item for a config hash.</summary>
    internal static void Write(BinaryWriter w, ThrownSpec t)
    {
        w.Write(t.Name); w.Write(t.Count); w.Write(t.InitialCount); w.Write(t.Cooldown); w.Write(t.MaxRange);
        Write(w, t.Projectile);
        if (t.Stim is { } stim) { w.Write("stim"); StimSpec.Write(w, stim); }   // perks/inert-2: only stims add bytes (other items hash as before)
        Variants.Write<ThrownSpec>(w, t.Variants, Write);
    }
}

/// <summary>
/// Charged fire (<c>ability_ranged_primary</c> or <c>_secondary</c> of type <c>charge_ranged</c>). Stages in file order
/// (<c>charge_states/&lt;n&gt;.charge_&lt;name&gt;</c>); on the install the first stage (at 0 s) has no shot and the last
/// has the charged shot, so releasing early fires nothing (**interpretation**).
/// </summary>
/// <param name="Ability">The ability key holding it, e.g. "ability_ranged_secondary" (input mapping not established).</param>
/// <param name="MaxChargeTime"><c>charge_upgrade_list/…/max_charge_time</c> default entry (1.5 on the lascannon, 0 elsewhere; meaning not established).</param>
/// <param name="MinTimeMultiplier"><c>min_charge_time_multiplier</c> default entry (perks lower it; 1 by default).</param>
/// <param name="ReloadCancels"><c>reload_button_cancels_charging</c>.</param>
/// <param name="AmmoToStart"><c>trigger_data/ammunition/…/required_to_start</c>.</param>
public sealed record ChargeSpec(string Ability, IReadOnlyList<ChargeStage> Stages, float MaxChargeTime, float MinTimeMultiplier, bool ReloadCancels, int AmmoToStart)
{
    /// <summary>
    /// sim/instant-fire (owner, 2026-10-31: the lascannon "should fire the instant the left mouse button is pressed"): a charge with a
    /// <c>max_charge_time</c> (install: the lascannon's 1.5 s only) is not held; the press fires the basic shot at once.
    /// </summary>
    public bool FiresOnPress => MaxChargeTime > 0f;
}

/// <summary>
/// One charge stage: reached after <see cref="MinTime"/> seconds held (× <see cref="ChargeSpec.MinTimeMultiplier"/>, assumed).
/// <see cref="HeatPerSecond"/> is the file's <c>heat_applied_per_second</c> (while charging in this stage, assumed);
/// <see cref="MoveSpeedMultiplier"/> a <c>speed_maximum</c> multiplication modifier of its <c>charging_action</c> (1 when none).
/// </summary>
public sealed record ChargeStage(string Name, float MinTime, float HeatPerSecond, float MoveSpeedMultiplier, ChargeShot? Shot);

/// <summary>
/// The shot a charge stage fires on release. Raycast: <see cref="Damage"/> per hit. Projectile: <see cref="Projectile"/>
/// (its <see cref="Damage"/> is the delivery's direct-hit damage, null when none). <see cref="HeatPerShot"/>: its
/// <c>fire_sim</c> <c>apply_heat</c>; <see cref="FireDelay"/>: a <c>delayed_action</c> delay in <c>fire_sim</c> (0 when none).
/// </summary>
public sealed record ChargeShot(DeliveryKind Delivery, DamageSpec? Damage, float MaxRange, float ShotCooldown, int AmmoCost,
    float HeatPerShot, float FireDelay, ProjectileSpec? Projectile)
{
    /// <summary>A hit-scan stage's penetration through props (M39; the lascannon's charged shot), or null.</summary>
    public PierceSpec? Pierce { get; init; }
}

/// <summary>
/// Lookups of a weapon's shots: which ability holds its charge, which projectile a shot launches, and its damage. A shot is
/// named by its stage: 0 = the basic shot, k ≥ 1 = the shot of charge stage k − 1 (<see cref="ChargeSpec.Stages"/>).
/// </summary>
public static class Shots
{
    public const string PrimaryAbility = "ability_ranged_primary", SecondaryAbility = "ability_ranged_secondary";

    /// <summary>
    /// Whether the weapon's charge is on its primary ability (the plasma pistol), not the secondary (the others). Data only since
    /// M31: Fire drives the charge either way.
    /// </summary>
    public static bool ChargeOnPrimary(WeaponSpec w) => w.Charge is { } c && c.Ability == PrimaryAbility;

    /// <summary>The charge shot of stage <paramref name="stage"/> (1-based), or null.</summary>
    public static ChargeShot? ChargeShotOf(WeaponSpec w, int stage) =>
        stage >= 1 && w.Charge is { } c && stage <= c.Stages.Count ? c.Stages[stage - 1].Shot : null;

    /// <summary>The projectile shot <paramref name="stage"/> launches, or null for a hit-scan shot or a stage without a shot.</summary>
    public static ProjectileSpec? ProjectileOf(WeaponSpec w, int stage) => stage == 0
        ? (w.Delivery == DeliveryKind.Projectile ? w.Projectile : null)
        : ChargeShotOf(w, stage) is { Delivery: DeliveryKind.Projectile } s ? s.Projectile : null;

    /// <summary>The direct-hit damage of shot <paramref name="stage"/> (null: none; a basic shot with damage 0 has none).</summary>
    public static DamageSpec? DirectDamageOf(WeaponSpec w, int stage) => stage == 0
        ? (w.Damage > 0f ? w.DirectDamage : null)
        : ChargeShotOf(w, stage)?.Damage;

    /// <summary>
    /// Whether the weapon's basic projectiles are detonated by the reload input (**interpretation**: the only weapon with
    /// a <see cref="ProjectileSpec.MaxActive"/> limit is the grenade launcher, whose <c>detonate_projectile</c> ability is
    /// bound to reload).
    /// </summary>
    public static bool RemoteDetonated(WeaponSpec? w) => w is { Delivery: DeliveryKind.Projectile, Projectile.MaxActive: > 0 };

    /// <summary>
    /// The projectile a world projectile or snapshot record names: with <paramref name="thrown"/>, item
    /// <paramref name="index"/> of <see cref="SimConfig.Thrown"/> (stage must be 0); else shot <paramref name="stage"/> of
    /// weapon <paramref name="index"/>. Null when the config has no such projectile.
    /// </summary>
    public static ProjectileSpec? ProjectileOf(SimConfig c, int index, int stage, bool thrown)
    {
        if (thrown) return stage == 0 && index >= 0 && index < c.Thrown.Count ? c.Thrown[index].Projectile : null;
        return index >= 0 && index < c.Weapons.Count ? ProjectileOf(c.Weapons[index], stage) : null;
    }
}

/// <summary>
/// A thrown equipment item (frag grenade, blind grenade (the files: <c>krak</c>), melta bomb): <c>ability_utility</c>'s projectile delivery.
/// Simulated by <c>Combat.StepPlayer</c> (the throw, on <see cref="InputButtons.Grenade"/>) and <c>World</c> (the projectile).
/// </summary>
/// <param name="Count"><c>capacity_clip</c> (default entry): most carried.</param>
/// <param name="InitialCount"><c>initial_clip_amount</c>: carried at spawn (-1: <see cref="Count"/>, assumed).</param>
/// <param name="Cooldown"><c>basic_weapon_tuning/…/shot_cooldown</c> (s): from one release to the next throw (interpretation).</param>
/// <param name="MaxRange"><c>delivery/max_range</c> (meaning for a throw not established; unused).</param>
public sealed record ThrownSpec(string Name, int Count, int InitialCount, float Cooldown, float MaxRange, ProjectileSpec Projectile)
{
    /// <summary>Grenades at spawn: InitialCount clamped to [0, Count]; -1 (or any negative) means Count.</summary>
    public int SpawnCount => InitialCount < 0 ? Math.Max(0, Count) : Math.Clamp(InitialCount, 0, Math.Max(0, Count));

    /// <summary>Whether this is a blind grenade (M37): its blasts only blind (<see cref="SplashSpec.BlindOnly"/>).</summary>
    public bool IsBlindGrenade => Projectile.AllSplashes.Any(s => s.BlindOnly);

    /// <summary>
    /// The name players see for the item when it differs from its file name: "blind grenade" for a blind grenade (the
    /// original's UI name, locale 104894 / 111304 "Blind Grenades"; the files call it <c>krak</c>), "combat stims" for stims; else null (use the file name).
    /// </summary>
    public string? PlayerName => IsBlindGrenade ? BlindGrenadeName : Stim is not null ? StimName : null;

    /// <summary>perks/inert-2: the player-facing name of combat stims (the original's UI name, locale 104400).</summary>
    public const string StimName = "combat stims";

    /// <summary>The player-facing name of a blind grenade.</summary>
    public const string BlindGrenadeName = "blind grenade";

    /// <summary>The item as perks change it (M43: Favor of the Armory's counts, Master-Crafted Wargear's projectile); see <see cref="WeaponSpec.Variants"/>.</summary>
    public IReadOnlyList<UpgradeVariant<ThrownSpec>> Variants { get; init; } = [];

    /// <summary>The item for a player with <paramref name="upgrades"/>.</summary>
    public ThrownSpec With(PerkUpgrades upgrades) => SmSim.Variants.Pick(this, Variants, upgrades);

    /// <summary>
    /// perks/inert-2: combat stims (<see cref="StimSpec"/>), or null for a thrown item. A stim launches nothing: <see cref="Projectile"/> is
    /// <see cref="NoProjectile"/> and the use applies the stim at its release.
    /// </summary>
    public StimSpec? Stim { get; init; }

    /// <summary>Seconds from the press to the release: the stim's own, else the throw's (<see cref="SimConfig.ThrowRelease"/>).</summary>
    public float ReleaseAt(SimConfig c) => Stim?.UseRelease ?? c.ThrowRelease;

    /// <summary>Seconds the use lasts: the stim's own, else the throw's (<see cref="SimConfig.ThrowDuration"/>).</summary>
    public float DurationOf(SimConfig c) => Stim?.UseDuration ?? c.ThrowDuration;

    /// <summary>The projectile of an item that launches none (a stim): never launched.</summary>
    public static readonly ProjectileSpec NoProjectile = new("", 0f, 0f, "", 0f, [], false, false, false, 0, 0f, 0f, 0, null, null);
}

/// <summary>
/// A weapon's heat, from its wargear <c>heat_info</c> (units as in the file; per second **assumed** for the rate).
/// Each shot adds <see cref="PerShot"/>; reaching <see cref="Overheat"/> locks the weapon until heat falls to
/// <see cref="Recovery"/>; heat falls at <see cref="CoolRate"/> per second once <see cref="CoolDelay"/> seconds have
/// passed since the last heat gain (<see cref="CoolDelayAfterOverheat"/> after an overheat).
/// </summary>
public sealed record HeatSpec(float PerShot, float Overheat, float HighHeat, float Recovery, float CoolRate, float CoolDelay, float CoolDelayAfterOverheat)
{
    /// <summary>
    /// The carrier's <c>speed_maximum</c> factor from the overheat's <c>apply_modifiers</c> (the plasma cannon: 0.5; the only MP weapon whose
    /// overheat applies modifiers), 1 without; lasts <see cref="OverheatSpeedTime"/> seconds (its <c>lifetime</c>: 1.5). <see cref="SmSim.Vent.OverheatSpeed"/>.
    /// </summary>
    public float OverheatSpeed { get; init; } = 1f;
    /// <summary>Seconds the overheat's speed factor lasts (0: none).</summary>
    public float OverheatSpeedTime { get; init; }
}

/// <summary>A melee weapon's combo, by its wargear name.</summary>
public sealed record MeleeCombo(string Wargear, IReadOnlyList<MeleeStep> Steps)
{
    /// <summary>The weapon's heavy strikes (Y1, Y2, …: the strike after that many light attacks; M28), or empty.</summary>
    public IReadOnlyList<MeleeStep> Heavy { get; init; } = [];
    /// <summary>The weapon's charge (its bull rush; M29), or null.</summary>
    public RushSpec? Rush { get; init; }
    /// <summary>The combo as perks change it (M43: Serrated Combat Blade, Sure Strike, Furious Charge, the heal-on-hit perks); see <see cref="WeaponSpec.Variants"/>.</summary>
    public IReadOnlyList<UpgradeVariant<MeleeCombo>> Variants { get; init; } = [];

    /// <summary>The combo for a player with <paramref name="upgrades"/>.</summary>
    public MeleeCombo With(PerkUpgrades upgrades) => SmSim.Variants.Pick(this, Variants, upgrades);

    /// <summary>Writes a list of steps for a config hash.</summary>
    internal static void WriteSteps(BinaryWriter w, IReadOnlyList<MeleeStep> steps)
    {
        w.Write(steps.Count);
        foreach (var m in steps)
        {
            foreach (float f in new[] { m.Duration, m.HitTime, m.ComboOpen, m.ComboClose, m.RangedAt, m.MoveAt, m.Damage, m.SecondaryDamage, m.Lunge, m.HealthMultiplier, m.ShieldMultiplier })
                w.Write(f);
            m.WriteExtras(w);
        }
    }

    /// <summary>Writes the whole combo (its variants included) for a config hash.</summary>
    internal static void Write(BinaryWriter w, MeleeCombo combo)
    {
        w.Write(combo.Wargear); WriteSteps(w, combo.Steps); WriteSteps(w, combo.Heavy); RushSpec.Write(w, combo.Rush);
        SmSim.Variants.Write(w, combo.Variants, Write);
    }
}

/// <summary>
/// A multiplayer class and the loadout the simulation gives it: one primary and one pistol from
/// <see cref="SimConfig.Weapons"/> (indices), and a melee combo. From the install by <c>SmContent.MpClasses</c>.
/// </summary>
/// <param name="Name">The class key in the game data, e.g. "sm - tactical".</param>
/// <param name="Primary">Index into <see cref="SimConfig.Weapons"/>, or -1 for none.</param>
/// <param name="Pistol">Index into <see cref="SimConfig.Weapons"/>, or -1 for none.</param>
/// <param name="Melee">The class's melee combo (empty: no melee attack).</param>
public sealed record ClassLoadout(string Name, int Primary, int Pistol, IReadOnlyList<MeleeStep> Melee)
{
    /// <summary>Every supported primary the class may choose (indices into <see cref="SimConfig.Weapons"/>), the default included.</summary>
    public IReadOnlyList<int> PrimaryChoices { get; init; } = [];
    /// <summary>Every supported pistol the class may choose.</summary>
    public IReadOnlyList<int> PistolChoices { get; init; } = [];
    /// <summary>Every supported melee combo the class may choose; the default (<see cref="Melee"/>) first when it has one.</summary>
    public IReadOnlyList<MeleeCombo> MeleeChoices { get; init; } = [];
    /// <summary>The default combo's heavy strikes (<see cref="MeleeCombo.Heavy"/>; M28), or empty.</summary>
    public IReadOnlyList<MeleeStep> MeleeHeavy { get; init; } = [];
    /// <summary>The default combo's charge (<see cref="MeleeCombo.Rush"/>; M29), or null.</summary>
    public RushSpec? MeleeRush { get; init; }
    /// <summary>Every supported thrown item the class may choose (indices into <see cref="SimConfig.Thrown"/>); the first is the default.</summary>
    public IReadOnlyList<int> EquipmentChoices { get; init; } = [];
    /// <summary>The class's jump pack (index into <see cref="SimConfig.JumpPacks"/>; base wargear, not a choice), or -1 for none.</summary>
    public int JumpPack { get; init; } = -1;
    /// <summary>
    /// The class's evade (index into <see cref="SimConfig.Evades"/>; base wargear <c>sm_mp_core_abil_evade</c> or
    /// <c>sm_mp_core_abil_heavy_evade</c>, not a choice), or -1 for none. A class with a <see cref="JumpPack"/> never evades:
    /// its Jump input is the pack's (M21; see <see cref="SmSim.Evade"/>).
    /// </summary>
    public int Evade { get; init; } = -1;
    /// <summary>
    /// The side the class plays for (from its <c>race</c> in the class table; protocol 10). <see cref="Team.None"/>: a class
    /// without a side (tests, the placeholder class): with <see cref="MatchRules"/>, such a player joins the smaller side.
    /// </summary>
    public Team Side { get; init; }
    /// <summary>The class's role (the data's <c>mp_class</c>: "tactical", "assault", "heavy"; "" unknown): a balanced join moves a player to the other side's class of the same role.</summary>
    public string Role { get; init; } = "";
    /// <summary>
    /// The perks the class may take (indices into <see cref="SimConfig.Perks"/>; the data's <c>class_perks</c> in its order). Either perk
    /// slot takes any of them (our default pending the owner; docs/customizer-architecture.md section 10, question 1).
    /// </summary>
    public IReadOnlyList<int> PerkChoices { get; init; } = [];
    /// <summary>The class's default perk (the data's <c>default_perk</c>; index into <see cref="SimConfig.Perks"/>), or -1 for none.</summary>
    public int DefaultPerk { get; init; } = -1;
    /// <summary>
    /// The class's own body (Ork O4; index into <see cref="SimConfig.Bodies"/>: speeds, health, shield, hit volumes; <see cref="SimConfig.For"/>),
    /// or -1 (every marine and Chaos class): the config's own fields.
    /// </summary>
    public int Body { get; init; } = -1;
    /// <summary>
    /// The class's own race (Ork O5; the look its players wear: <see cref="SmSim.Race.Orks"/> for the Ork Boy, who plays on either side), or
    /// <see cref="SmSim.Race.None"/> (every marine and Chaos class): the race of the player's side (<see cref="Races.Of"/>).
    /// </summary>
    public Race Race { get; init; } = Race.None;
}

/// <summary>
/// Lookups of a player's class, weapons and melee combo in a <see cref="SimConfig"/>; out-of-range indices mean "none".
/// <para>
/// Loadout choice (rules provisional; spec/behavior.md): a player's primary and pistol are weapon indices
/// (<see cref="PlayerState.Primary"/>, <see cref="PlayerState.Pistol"/>) and its melee an index into the class's
/// <see cref="ClassLoadout.MeleeChoices"/> (<see cref="PlayerState.MeleeChoice"/>). A value is valid for the class when it
/// is the class default or in the class's choice list (a melee choice: in range of the list, or -1 when the list is
/// empty); anything else means the class default. The effective choice list of a slot is the class's list, or just the
/// class default when the list is empty.
/// </para>
/// </summary>
public static class Loadouts
{
    /// <summary>The class at <paramref name="playerClass"/>, or null when the index is out of range.</summary>
    public static ClassLoadout? ClassOf(SimConfig c, int playerClass)
    {
        var classes = c.Classes;
        return playerClass >= 0 && playerClass < classes.Count ? classes[playerClass] : null;
    }

    /// <summary>Index into <see cref="SimConfig.Weapons"/> of the class's default weapon in <paramref name="slot"/>, or -1.</summary>
    public static int WeaponIndex(SimConfig c, int playerClass, WeaponSlot slot)
    {
        if (ClassOf(c, playerClass) is not { } loadout || slot == WeaponSlot.Second) return -1;   // the second primary has no class default (protocol 44)
        int index = slot == WeaponSlot.Pistol ? loadout.Pistol : loadout.Primary;
        return index >= 0 && index < c.Weapons.Count ? index : -1;
    }

    /// <summary>The class's default weapon in <paramref name="slot"/>, or null when it has none.</summary>
    public static WeaponSpec? Weapon(SimConfig c, int playerClass, WeaponSlot slot) =>
        WeaponIndex(c, playerClass, slot) is var i and >= 0 ? c.Weapons[i] : null;

    /// <summary>
    /// Index into <see cref="SimConfig.Weapons"/> of the player's weapon in <paramref name="slot"/> (its choice when valid
    /// for its class, else the class default), or -1.
    /// </summary>
    public static int WeaponIndex(SimConfig c, PlayerState s, WeaponSlot slot)
    {
        if (slot == WeaponSlot.Second) return SecondIndex(c, s);
        int chosen = slot == WeaponSlot.Pistol ? s.Pistol : s.Primary;
        return ValidWeapon(c, s.Class, slot, chosen) ? chosen : WeaponIndex(c, s.Class, slot);
    }

    /// <summary>The player's weapon in <paramref name="slot"/> as its perks change it (M43: <see cref="WeaponSpec.With"/>), or null.</summary>
    public static WeaponSpec? Weapon(SimConfig c, PlayerState s, WeaponSlot slot) =>
        WeaponIndex(c, s, slot) is var i and >= 0 ? (c.Weapons[i].Variants.Count == 0 ? c.Weapons[i] : c.Weapons[i].With(Upgrades(c, s))) : null;

    /// <summary>The weapon the player holds (its chosen weapon in <see cref="PlayerState.Slot"/>), or null.</summary>
    public static WeaponSpec? InHand(SimConfig c, PlayerState s) => Weapon(c, s, s.Slot);

    /// <summary>
    /// The weapon the player has holstered that the Switch toggle goes to, or null: the weapon of <see cref="ToggleSlot"/>.
    /// With two weapons it is the other one, as it always was.
    /// </summary>
    public static WeaponSpec? Holstered(SimConfig c, PlayerState s) => ToggleSlot(c, s) is { } slot ? Weapon(c, s, slot) : null;

    /// <summary>
    /// The slot the Switch toggle (and the mouse wheel) goes to (protocol 44): the next slot that has a weapon in the order pistol, primary, second primary,
    /// round to the pistol; null when there is none. With two weapons, the other one, as it always was; with three it cycles (an estimate: the owner named only keys 1, 2, 3).
    /// </summary>
    public static WeaponSlot? ToggleSlot(SimConfig c, PlayerState s)
    {
        ReadOnlySpan<WeaponSlot> order = [WeaponSlot.Pistol, WeaponSlot.Ranged, WeaponSlot.Second];
        int at = (int)s.Slot switch { 1 => 0, 0 => 1, _ => 2 };
        for (int k = 1; k < 3; k++)
        {
            var candidate = order[(at + k) % 3];
            if (WeaponIndex(c, s, candidate) >= 0) return candidate;
        }
        return null;
    }

    /// <summary>The perk file of Weapon Versatility (protocol 44).</summary>
    public const string SecondWeaponPerk = "mp_perk_secondary_weapon";
    /// <summary>The perk file of Rapid Deployment (a respawn of <see cref="RapidDeploymentSeconds"/>).</summary>
    public const string RapidDeploymentPerk = "mp_perk_respawn_timer";
    /// <summary>The perk file of Teleport Homer (allies respawn next to the living carrier).</summary>
    public const string TeleportHomerPerk = "mp_perk_friendly_spawn_target";
    /// <summary>The perk file of Tactical Readout.</summary>
    public const string TacticalReadoutPerk = "mp_perk_enemy_info";
    /// <summary>The perk file of Signum Link (the same as Tactical Readout for now).</summary>
    public const string SignumLinkPerk = "mp_perk_snap_to_increase";
    /// <summary>Respawn time of a player with Rapid Deployment (owner's recollection, 2026-10-30).</summary>
    public const float RapidDeploymentSeconds = 3f;

    /// <summary>Whether the class may carry the second primary: a Tactical (the data's role; owner's decision 2026-10-30).</summary>
    public static bool CanCarrySecond(SimConfig c, int playerClass) => ClassOf(c, playerClass) is { Role: "tactical" };

    /// <summary>
    /// The player's second primary (protocol 44): its choice when the player is a Tactical with Weapon Versatility and the choice is one of the
    /// class's primaries, else -1 (no third weapon). Server-side validation and the prediction use this one rule.
    /// </summary>
    public static int SecondIndex(SimConfig c, PlayerState s) =>
        CanCarrySecond(c, s.Class) && HasPerk(c, s, SecondWeaponPerk) && s.Second >= 0 && ValidWeapon(c, s.Class, WeaponSlot.Ranged, s.Second) ? s.Second : -1;

    /// <summary>Whether the player's perks show enemies' health and armour bars: Tactical Readout, or Signum Link (the same for now; owner's decision 2026-10-30).</summary>
    public static bool ShowsEnemyBars(SimConfig c, PlayerState s) => HasPerk(c, s, TacticalReadoutPerk) || HasPerk(c, s, SignumLinkPerk);

    /// <summary>The respawn wait of a player dying with its perks: <see cref="SimConfig.RespawnTime"/>, or <see cref="RapidDeploymentSeconds"/> with Rapid Deployment (never longer than the base).</summary>
    public static float RespawnTime(SimConfig c, PlayerState s) =>
        HasPerk(c, s, RapidDeploymentPerk) ? MathF.Min(c.RespawnTime, RapidDeploymentSeconds) : c.RespawnTime;

    /// <summary>The class's default melee combo (empty without a class or melee weapon).</summary>
    public static IReadOnlyList<MeleeStep> Melee(SimConfig c, int playerClass) => ClassOf(c, playerClass)?.Melee ?? [];

    /// <summary>The player's melee combo: its chosen one when valid, else the class default (M43: as its perks change it).</summary>
    public static IReadOnlyList<MeleeStep> Melee(SimConfig c, PlayerState s)
    {
        if (ClassOf(c, s.Class) is not { } loadout) return [];
        if (PerkCombo(c, s, loadout) is { } combo) return combo.Steps;
        return s.MeleeChoice >= 0 && s.MeleeChoice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[s.MeleeChoice].Steps : loadout.Melee;
    }

    /// <summary>The player's heavy strikes (its chosen combo's, else the class default's; M28), or empty.</summary>
    public static IReadOnlyList<MeleeStep> MeleeHeavy(SimConfig c, PlayerState s)
    {
        if (ClassOf(c, s.Class) is not { } loadout) return [];
        if (PerkCombo(c, s, loadout) is { } combo) return combo.Heavy;
        return s.MeleeChoice >= 0 && s.MeleeChoice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[s.MeleeChoice].Heavy : loadout.MeleeHeavy;
    }

    /// <summary>The player's charge (its chosen combo's, else the class default's; M29), or null.</summary>
    public static RushSpec? Rush(SimConfig c, PlayerState s)
    {
        if (ClassOf(c, s.Class) is not { } loadout) return null;
        if (PerkCombo(c, s, loadout) is { } combo) return combo.Rush;
        return s.MeleeChoice >= 0 && s.MeleeChoice < loadout.MeleeChoices.Count ? loadout.MeleeChoices[s.MeleeChoice].Rush : loadout.MeleeRush;
    }

    /// <summary>
    /// The player's combo as its perks change it (M43), or null when they change nothing: its chosen combo (else the class's first
    /// choice, which the content loader makes the default) with a variant for the player's upgrades.
    /// </summary>
    static MeleeCombo? PerkCombo(SimConfig c, PlayerState s, ClassLoadout loadout)
    {
        var choices = loadout.MeleeChoices;
        var combo = s.MeleeChoice >= 0 && s.MeleeChoice < choices.Count ? choices[s.MeleeChoice] : choices.Count > 0 ? choices[0] : null;
        if (combo is not { Variants.Count: > 0 }) return null;
        var chosen = combo.With(Upgrades(c, s));
        return ReferenceEquals(chosen, combo) ? null : chosen;
    }

    public static int MeleeChoiceOf(SimConfig c, PlayerState s)
    {
        int count = ClassOf(c, s.Class)?.MeleeChoices.Count ?? 0;
        return s.MeleeChoice >= 0 && s.MeleeChoice < count ? s.MeleeChoice : count > 0 ? 0 : -1;
    }

    /// <summary>The weapon indices a class may choose in <paramref name="slot"/>: its list, or its default alone when the list is empty.</summary>
    public static IReadOnlyList<int> Choices(SimConfig c, int playerClass, WeaponSlot slot)
    {
        if (ClassOf(c, playerClass) is not { } loadout) return [];
        if (slot == WeaponSlot.Second) return CanCarrySecond(c, playerClass) ? Choices(c, playerClass, WeaponSlot.Ranged) : [];
        var list = slot == WeaponSlot.Pistol ? loadout.PistolChoices : loadout.PrimaryChoices;
        if (list.Count > 0) return list;
        int fallback = WeaponIndex(c, playerClass, slot);
        return fallback >= 0 ? [fallback] : [];
    }

    /// <summary>Whether <paramref name="weapon"/> (a weapon index, -1 none) is the class default or one of its choices.</summary>
    public static bool ValidWeapon(SimConfig c, int playerClass, WeaponSlot slot, int weapon)
    {
        if (slot == WeaponSlot.Second) { var seconds = Choices(c, playerClass, slot); for (int i = 0; i < seconds.Count; i++) if (seconds[i] == weapon) return weapon >= 0; return false; }
        if (weapon == WeaponIndex(c, playerClass, slot)) return true;
        if (weapon < 0 || weapon >= c.Weapons.Count || ClassOf(c, playerClass) is not { } loadout) return false;
        var list = slot == WeaponSlot.Pistol ? loadout.PistolChoices : loadout.PrimaryChoices;
        for (int i = 0; i < list.Count; i++) if (list[i] == weapon) return true;
        return false;
    }

    /// <summary>
    /// The player with its loadout chosen from requests that index the class's choice lists (<see cref="Choices"/> for the
    /// weapons, <see cref="ClassLoadout.MeleeChoices"/> for melee). A negative or out-of-range request means the class default.
    /// </summary>
    /// <remarks>
    /// <paramref name="perk1Choice"/> and <paramref name="perk2Choice"/> index the class's <see cref="ClassLoadout.PerkChoices"/>: perk 1
    /// defaults to the class's <see cref="ClassLoadout.DefaultPerk"/>, perk 2 to none, and a perk 2 equal to perk 1 is dropped
    /// (<see cref="Resolved"/>, which also applies <see cref="PistolPerkRule"/>).
    /// </remarks>
    public static PlayerState Choose(SimConfig c, PlayerState s, int primaryChoice, int pistolChoice, int meleeChoice, int equipmentChoice = -1,
        int perk1Choice = -1, int perk2Choice = -1, int secondChoice = -1)
    {
        var primaries = Choices(c, s.Class, WeaponSlot.Ranged);
        var seconds = Choices(c, s.Class, WeaponSlot.Second);
        var pistols = Choices(c, s.Class, WeaponSlot.Pistol);
        int melees = ClassOf(c, s.Class)?.MeleeChoices.Count ?? 0;
        var equipment = EquipmentChoices(c, s.Class);
        var perks = PerkChoices(c, s.Class);
        return Resolved(c, s with
        {
            Primary = primaryChoice >= 0 && primaryChoice < primaries.Count ? primaries[primaryChoice] : WeaponIndex(c, s.Class, WeaponSlot.Ranged),
            Pistol = pistolChoice >= 0 && pistolChoice < pistols.Count ? pistols[pistolChoice] : WeaponIndex(c, s.Class, WeaponSlot.Pistol),
            MeleeChoice = meleeChoice >= 0 && meleeChoice < melees ? meleeChoice : -1,
            Equipment = equipmentChoice >= 0 && equipmentChoice < equipment.Count ? equipment[equipmentChoice] : -1,
            Perk1 = perk1Choice >= 0 && perk1Choice < perks.Count ? perks[perk1Choice] : -1,
            Perk2 = perk2Choice >= 0 && perk2Choice < perks.Count ? perks[perk2Choice] : -1,
            Second = secondChoice >= 0 && secondChoice < seconds.Count ? seconds[secondChoice] : -1,
        });
    }

    /// <summary>
    /// The player with every choice valid for its class (invalid ones replaced by the class default): weapons, melee and
    /// equipment as before; perk 1 a class perk, else the class default perk; perk 2 a class perk other than perk 1, else none;
    /// then a pistol the perks do not allow (<see cref="PistolPerkRule"/>) becomes the class default pistol. Applied at every
    /// (re)spawn (<see cref="Combat.Respawned"/>); the per-tick lookups (<see cref="WeaponIndex(SimConfig, PlayerState, WeaponSlot)"/>)
    /// do not look at perks, so a client predicting without them sees the same weapons.
    /// </summary>
    public static PlayerState Resolved(SimConfig c, PlayerState s)
    {
        s = s with
        {
            Primary = WeaponIndex(c, s, WeaponSlot.Ranged), Pistol = WeaponIndex(c, s, WeaponSlot.Pistol), MeleeChoice = MeleeChoiceOf(c, s),
            Equipment = EquipmentIndex(c, s),
        };
        int perk1 = PerkIndex(c, s, 1);
        s = s with { Perk1 = perk1, Perk2 = PerkIndex(c, s with { Perk1 = perk1 }, 2) };
        s = s with { Second = SecondIndex(c, s) };   // protocol 44: a Tactical with Weapon Versatility, else no third weapon
        if (!PistolPerkRule(c, s, s.Pistol)) s = s with { Pistol = WeaponIndex(c, s.Class, WeaponSlot.Pistol) };
        return s;
    }

    // ---- perks (interfaces for the perks milestone; no effects yet) ----

    /// <summary>The perks a class may take (indices into <see cref="SimConfig.Perks"/>; empty: none).</summary>
    public static IReadOnlyList<int> PerkChoices(SimConfig c, int playerClass) => ClassOf(c, playerClass)?.PerkChoices ?? [];

    /// <summary>Whether <paramref name="perk"/> (index into <see cref="SimConfig.Perks"/>) is one the class may take (its list or its default perk).</summary>
    public static bool ValidPerk(SimConfig c, int playerClass, int perk)
    {
        if (perk < 0 || perk >= c.Perks.Count || ClassOf(c, playerClass) is not { } loadout) return false;
        if (perk == loadout.DefaultPerk) return true;
        foreach (int i in loadout.PerkChoices) if (i == perk) return true;
        return false;
    }

    /// <summary>The class's default perk (in range of <see cref="SimConfig.Perks"/>), or -1.</summary>
    public static int DefaultPerk(SimConfig c, int playerClass) =>
        ClassOf(c, playerClass) is { DefaultPerk: var p } && p >= 0 && p < c.Perks.Count ? p : -1;

    /// <summary>
    /// The player's perk in <paramref name="slot"/> (1 or 2; index into <see cref="SimConfig.Perks"/>, -1 none): slot 1 its choice when
    /// valid, else the class default perk; slot 2 its choice when valid and not slot 1's perk, else none.
    /// </summary>
    public static int PerkIndex(SimConfig c, PlayerState s, int slot)
    {
        int first = ValidPerk(c, s.Class, s.Perk1) ? s.Perk1 : DefaultPerk(c, s.Class);
        if (slot == 1) return first;
        return slot == 2 && ValidPerk(c, s.Class, s.Perk2) && s.Perk2 != first ? s.Perk2 : -1;
    }

    /// <summary>Whether the player has the perk named <paramref name="name"/> in either slot.</summary>
    public static bool HasPerk(SimConfig c, PlayerState s, string name)
    {
        for (int slot = 1; slot <= 2; slot++)
            if (PerkIndex(c, s, slot) is var i and >= 0 && string.Equals(c.Perks[i].Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// The upgrade strings of the player's perks (slot 1 then slot 2; a perk without one is skipped): what the perks switch on
    /// in the tuning ("base entry plus entry that needs the upgrade"; docs/customizer-and-perks-data.md section 2). See <see cref="Upgrades"/>.
    /// </summary>
    public static IReadOnlyList<string> UpgradeStrings(SimConfig c, PlayerState s)
    {
        var list = new List<string>(2);
        for (int slot = 1; slot <= 2; slot++)
            if (PerkIndex(c, s, slot) is var i and >= 0 && c.Perks[i].UpgradeString is { Length: > 0 } u) list.Add(u);
        return list;
    }

    /// <summary>
    /// The upgrade strings of the player's perks without allocating (M43): what every perk variant is picked by
    /// (<see cref="Weapon(SimConfig, PlayerState, WeaponSlot)"/>, <see cref="Equipment(SimConfig, PlayerState)"/>,
    /// <see cref="JumpPack(SimConfig, PlayerState)"/>, the melee lookups, <see cref="Body"/>).
    /// </summary>
    public static PerkUpgrades Upgrades(SimConfig c, PlayerState s)
    {
        if (c.Perks.Count == 0) return default;
        string? Of(int slot) => PerkIndex(c, s, slot) is var i and >= 0 && c.Perks[i].UpgradeString is { Length: > 0 } u ? u : null;
        return new PerkUpgrades(Of(1), Of(2));
    }

    /// <summary>
    /// The player's health and armour shield as its perks leave them (M43; <see cref="PlayerUpgrade"/>): the config's values, an
    /// upgrade's <see cref="PlayerUpgrade.Shield"/> replacing the shield's, then every granted upgrade's additions (delays and rates
    /// not below 0) and multipliers.
    /// </summary>
    public static BodyStats Body(SimConfig c, PlayerState s)
    {
        c = c.For(s.Class);   // Ork O4: the class's own body
        var body = new BodyStats(c.HealthMax, c.HealthRegenDelay, c.HealthRegenRate, c.ShieldMax, c.ShieldRegenDelay, c.ShieldRegenRate, 1f, 1f, 1f);
        if (c.PlayerUpgrades.Count == 0) return body;
        var granted = Upgrades(c, s);
        if (granted.IsEmpty) return body;
        var upgrades = c.PlayerUpgrades;   // indexed loops: called per tick and per hit
        for (int i = 0; i < upgrades.Count; i++)
            if (granted.Has(upgrades[i].Upgrade) && upgrades[i].Shield is { } shield)
                body = body with { ShieldMax = shield.Max, ShieldRegenDelay = shield.RegenDelay, ShieldRegenRate = shield.RegenRate };
        for (int i = 0; i < upgrades.Count; i++)
        {
            var u = upgrades[i];
            if (!granted.Has(u.Upgrade)) continue;
            body = body with
            {
                HealthRegenRate = MathF.Max(0f, body.HealthRegenRate + u.HealthRegenRate), HealthRegenDelay = MathF.Max(0f, body.HealthRegenDelay + u.HealthRegenDelay),
                ShieldRegenRate = MathF.Max(0f, body.ShieldRegenRate + u.ShieldRegenRate), ShieldRegenDelay = MathF.Max(0f, body.ShieldRegenDelay + u.ShieldRegenDelay),
                ReceivedRanged = body.ReceivedRanged * u.ReceivedRanged, ReceivedMelee = body.ReceivedMelee * u.ReceivedMelee,
                ReceivedExplosive = body.ReceivedExplosive * u.ReceivedExplosive,
            };
        }
        return body;
    }

    /// <summary>The player's perk (slot 1, then 2; index into <see cref="SimConfig.Perks"/>) whose upgrade string is <paramref name="upgrade"/>, or -1.</summary>
    public static int PerkGranting(SimConfig c, PlayerState s, string upgrade)
    {
        for (int slot = 1; slot <= 2; slot++)
            if (PerkIndex(c, s, slot) is var i and >= 0 && string.Equals(c.Perks[i].UpgradeString, upgrade, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The player-blueprint upgrades the player's perks grant (M43; death and armour-break blasts read them).</summary>
    public static IEnumerable<PlayerUpgrade> PlayerUpgradesOf(SimConfig c, PlayerState s)
    {
        if (c.PlayerUpgrades.Count == 0) yield break;
        var granted = Upgrades(c, s);
        foreach (var u in c.PlayerUpgrades) if (granted.Has(u.Upgrade)) yield return u;
    }

    /// <summary>The perk the True Grit rule asks for: "True Grit", a bolter in place of the pistol.</summary>
    public const string TrueGritPerk = "mp_perk_bolter_in_place_of_pistol";

    /// <summary>
    /// <b>The True Grit rule</b> (our default pending the owner; docs/customizer-architecture.md section 10, question 2): with
    /// <see cref="SimConfig.PistolBoltersNeedTrueGrit"/>, a pistol-slot bolter (<see cref="IsPistolBolter"/>) is a valid pistol only
    /// for a player with the <see cref="TrueGritPerk"/> perk. Data-implied: the two pistol-slot bolters are the perk's content, but
    /// whether the original's front end hides them without it is not in the data. Switch it off with the config field.
    /// </summary>
    public static bool PistolPerkRule(SimConfig c, PlayerState s, int weapon) =>
        !c.PistolBoltersNeedTrueGrit || weapon < 0 || weapon >= c.Weapons.Count || !IsPistolBolter(c.Weapons[weapon]) || HasPerk(c, s, TrueGritPerk);

    /// <summary>A bolter in the pistol slot (<c>*_pistol_bolter</c>, <c>*_pistol_bolter_gold</c>: Bolter and Relic Bolter on the Assault list).</summary>
    public static bool IsPistolBolter(WeaponSpec w) => w.Slot == WeaponSlot.Pistol && IsPistolBolterName(w.Name);

    /// <summary>Whether a wargear file name is a pistol-slot bolter's (the one name rule; the customizer's catalogue uses it too).</summary>
    public static bool IsPistolBolterName(string name) =>
        name.EndsWith("_pistol_bolter", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_pistol_bolter_gold", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A loadout request resolved for class <paramref name="playerClass"/> (the caller has validated the class): every index kept
    /// when in range of the class's list, else -1 (default); perk 2 dropped when it names perk 1's effective perk; the pistol
    /// dropped when <see cref="PistolPerkRule"/> refuses it with the resolved perks. The class field is set to <paramref name="playerClass"/>.
    /// </summary>
    public static LoadoutChoice Resolve(SimConfig c, int playerClass, LoadoutChoice choice)
    {
        static int In(int i, int count) => i >= 0 && i < count ? i : -1;
        var perks = PerkChoices(c, playerClass);
        int perk1 = In(choice.Perk1, perks.Count), perk2 = In(choice.Perk2, perks.Count);
        int perk1Index = perk1 >= 0 ? perks[perk1] : DefaultPerk(c, playerClass);
        if (perk2 >= 0 && perks[perk2] == perk1Index) perk2 = -1;
        var pistols = Choices(c, playerClass, WeaponSlot.Pistol);
        int pistol = In(choice.Pistol, pistols.Count);
        var state = new PlayerState(0, default, default, 0f, true) { Class = playerClass, Perk1 = perk1Index, Perk2 = perk2 >= 0 ? perks[perk2] : -1 };
        if (pistol >= 0 && !PistolPerkRule(c, state, pistols[pistol])) pistol = -1;
        // Protocol 44: the second primary is kept only for a Tactical holding Weapon Versatility.
        int second = In(choice.Second, Choices(c, playerClass, WeaponSlot.Second).Count);
        if (second >= 0 && !HasPerk(c, state, SecondWeaponPerk)) second = -1;
        return new LoadoutChoice(playerClass, In(choice.Primary, Choices(c, playerClass, WeaponSlot.Ranged).Count), pistol,
            In(choice.Melee, ClassOf(c, playerClass)?.MeleeChoices.Count ?? 0), In(choice.Equipment, EquipmentChoices(c, playerClass).Count), perk1, perk2, second);
    }

    /// <summary>The thrown items a class may choose (indices into <see cref="SimConfig.Thrown"/>; empty: none).</summary>
    public static IReadOnlyList<int> EquipmentChoices(SimConfig c, int playerClass) => ClassOf(c, playerClass)?.EquipmentChoices ?? [];

    /// <summary>Whether <paramref name="item"/> (index into <see cref="SimConfig.Thrown"/>) is one of the class's equipment choices.</summary>
    public static bool ValidEquipment(SimConfig c, int playerClass, int item)
    {
        if (item < 0 || item >= c.Thrown.Count) return false;
        var list = EquipmentChoices(c, playerClass);
        for (int i = 0; i < list.Count; i++) if (list[i] == item) return true;
        return false;
    }

    /// <summary>The class's default thrown item (its first valid choice), or -1.</summary>
    public static int EquipmentIndex(SimConfig c, int playerClass)
    {
        foreach (int i in EquipmentChoices(c, playerClass)) if (i >= 0 && i < c.Thrown.Count) return i;
        return -1;
    }

    /// <summary>The player's thrown item: its choice when valid for its class, else the class default; -1 none.</summary>
    public static int EquipmentIndex(SimConfig c, PlayerState s) => ValidEquipment(c, s.Class, s.Equipment) ? s.Equipment : EquipmentIndex(c, s.Class);

    /// <summary>The player's thrown item as its perks change it (M43: <see cref="ThrownSpec.With"/>), or null.</summary>
    public static ThrownSpec? Equipment(SimConfig c, PlayerState s) =>
        EquipmentIndex(c, s) is var i and >= 0 ? (c.Thrown[i].Variants.Count == 0 ? c.Thrown[i] : c.Thrown[i].With(Upgrades(c, s))) : null;

    /// <summary>The class's jump pack (<see cref="ClassLoadout.JumpPack"/> when in range of <see cref="SimConfig.JumpPacks"/>), or null.</summary>
    public static JumpPackSpec? JumpPack(SimConfig c, int playerClass) =>
        ClassOf(c, playerClass) is { JumpPack: var i } && i >= 0 && i < c.JumpPacks.Count ? c.JumpPacks[i] : null;

    /// <summary>The player's jump pack (its class's) as its perks change it (M43: <see cref="JumpPackSpec.With"/>), or null.</summary>
    public static JumpPackSpec? JumpPack(SimConfig c, PlayerState s) =>
        JumpPack(c, s.Class) is { } pack ? (pack.Variants.Count == 0 ? pack : pack.With(Upgrades(c, s))) : null;

    /// <summary>
    /// The class's evade (<see cref="ClassLoadout.Evade"/> when in range of <see cref="SimConfig.Evades"/>), or null; for a class with
    /// a jump pack (<see cref="JumpPack(SimConfig, int)"/>) only a dash (<see cref="EvadeSpec.Dash"/>, M44), never a roll or hop.
    /// The Ork's is its side-only dodge hop (<see cref="EvadeSpec.SideOnly"/>, Ork O4).
    /// </summary>
    public static EvadeSpec? Evade(SimConfig c, int playerClass) =>
        ClassOf(c, playerClass) is { Evade: var i } && i >= 0 && i < c.Evades.Count && (c.Evades[i].Dash || JumpPack(c, playerClass) is null) ? c.Evades[i] : null;

    /// <summary>The player's evade (its class's), or null.</summary>
    public static EvadeSpec? Evade(SimConfig c, PlayerState s) => Evade(c, s.Class);

    /// <summary>One line naming the player's class, weapons and melee (server log).</summary>
    public static string Describe(SimConfig c, PlayerState s)
    {
        string Name(WeaponSpec? w) => w?.Name ?? "none";
        var loadout = ClassOf(c, s.Class);
        int melee = MeleeChoiceOf(c, s);
        string meleeName = melee >= 0 && loadout is not null ? loadout.MeleeChoices[melee].Wargear : Melee(c, s).Count > 0 ? "default" : "none";
        return $"{loadout?.Name ?? "?"}: primary {Name(Weapon(c, s, WeaponSlot.Ranged))}, pistol {Name(Weapon(c, s, WeaponSlot.Pistol))}" +
            (Weapon(c, s, WeaponSlot.Second) is { } second ? $", second primary {second.Name}" : "") + $", melee {meleeName}" +
            (Equipment(c, s) is { } item ? $", equipment {item.Name}" : "") + (JumpPack(c, s) is { } pack ? $", jump pack {pack.Name}" : "")
            + (Evade(c, s) is { } evade ? $", evade {evade.Name}" : "")
            + string.Concat(new[] { 1, 2 }.Select(slot => PerkIndex(c, s, slot)).Where(i => i >= 0).Select((i, k) => (k == 0 ? ", perks " : " + ") + c.Perks[i].Name));
    }

    /// <summary>
    /// The first holstered slot while <paramref name="slot"/> is in hand (protocol 44): the slots in the order pistol, primary, second primary without the one
    /// in hand. The player's <see cref="PlayerState.OtherClip"/> family holds its ammunition. With two weapons, the other one, as before.
    /// </summary>
    public static WeaponSlot Other(WeaponSlot slot) => slot == WeaponSlot.Pistol ? WeaponSlot.Ranged : WeaponSlot.Pistol;

    /// <summary>The second holstered slot (protocol 44; <see cref="PlayerState.ThirdClip"/> family): the second primary's slot unless it is in hand, then the primary's.</summary>
    public static WeaponSlot Third(WeaponSlot slot) => slot == WeaponSlot.Second ? WeaponSlot.Ranged : WeaponSlot.Second;
}
