using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmContent;

/// <summary>
/// Gameplay tuning read from the player's install: the multiplayer player blueprints in <c>data/packages/ebps.oppc</c>
/// (<c>sm_space_marine_player</c>, <c>csm_chaos_marine_player</c>), extension <c>biped_locomotion_ext</c>.
/// Values the files do not hold keep the <see cref="SimConfig"/> placeholder and are listed in <see cref="Placeholders"/>.
/// </summary>
public sealed partial record GameplayTuning(SimConfig Config, string Source, IReadOnlyList<string> Placeholders, IReadOnlyList<string> Notes)
{
    public const string BlueprintPackage = "data/packages/ebps.oppc";
    public const string MarineBlueprint = "sm_space_marine_player";
    public const string ChaosBlueprint = "csm_chaos_marine_player";
    public const string WargearPackage = "data/packages/wargear.oppc";
    public const string Bolter = "sm_mp_ranged_bolter";

    /// <summary>Reads the Space Marine's locomotion, and checks the Chaos Marine's matches (PvP sides share one config).</summary>
    /// <param name="ork">Ork O4: add the Ork Boy as class 6 balanced in this mode (<see cref="OrkClass"/>; the config hash changes); null
    /// (the default): no Ork, the config exactly as before.</param>
    /// <param name="ownerBalance">The owner's weapon overrides (<see cref="OwnerBalance"/>, 2026-10-28; on by default and in the config hash);
    /// false: the data's weapon values (comparison aid).</param>
    /// <param name="ownerFeel">feel/buffer-speed (the owner's requests of 2026-10-29; on by default and in the config hash): the movement × 0.9
    /// (<see cref="MovementScale"/>) and input buffering (<see cref="SimConfig.InputBufferWindow"/> <see cref="OwnerInputBuffer"/> s); feel/locks-sprint-stop: the
    /// action lock scale (<see cref="OwnerLockScale"/>) and the sprint stop (<see cref="SimConfig.SprintStopTime"/>); false: the data's speeds, no buffering,
    /// the data's lock times and no sprint stop (comparison aid, tests).</param>
    public static GameplayTuning Load(string gameDir, OrkBalanceMode? ork = null, bool ownerBalance = true, bool ownerFeel = true)
    {
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using (var archive = ObpkArchive.Open(Path.Combine(gameDir, BlueprintPackage)))
            archive.ReadEntries(
                e => archive.GroupName(e) == "attr_pc" && e.FileName is MarineBlueprint or ChaosBlueprint,
                (e, data) => docs[e.FileName] = BafDocument.Read(data));
        if (!docs.TryGetValue(MarineBlueprint, out var marine))
            throw new FileNotFoundException($"'{MarineBlueprint}' not found in {BlueprintPackage}.");

        var notes = new List<string>();
        var config = FromBlueprint(marine, notes, out var fromBlueprint);
        var placeholders = fromBlueprint.ToList();   // one list the steps below add to and remove from
        var marineBlueprintOnly = config;
        BafDocument? bolter = null;
        string wargearPath = Path.Combine(gameDir, WargearPackage);
        if (File.Exists(wargearPath))
            using (var wargear = ObpkArchive.Open(wargearPath))
                wargear.ReadEntries(e => wargear.GroupName(e) == "attr_pc" && e.FileName == Bolter, (_, data) => bolter = BafDocument.Read(data));
        if (bolter is null) notes.Add($"'{Bolter}' not found in {WargearPackage}; weapon values are placeholders.");
        else config = WithWeapon(config, bolter, notes, placeholders);
        if (docs.TryGetValue(ChaosBlueprint, out var chaos))
        {
            var chaosConfig = FromBlueprint(chaos, [], out _);
            if (chaosConfig != marineBlueprintOnly) notes.Add("Chaos and Space Marine locomotion differ; the shared config uses the Space Marine's.");
        }
        else notes.Add($"'{ChaosBlueprint}' not found; not cross-checked.");
        config = WithMelee(config, gameDir, notes, placeholders, out var melee);
        config = WithClasses(config, gameDir, notes, placeholders, out var classes, ork, ownerBalance);
        if (ownerBalance) config = config with { Thrown = OwnerBalance.WithGrenadeBounce(config.Thrown, notes) };   // fix/grenades: the owner's shorter bounce (deliberate change)
        config = WithPlayerUpgrades(config, marine, chaos, notes);
        config = PickupTuning.Apply(config, gameDir, marine, chaos, notes, placeholders);   // fidelity gap 1: the deaths' drops
        config = WithHitVolumes(config, gameDir, notes, placeholders);
        config = MeleeLockTuning.Apply(config, gameDir, notes);   // feel/melee-lock: the global directional targeting values
        if (ownerFeel) config = MovementScale.Apply(config, MovementScale.OwnerFactor) with { InputBufferWindow = OwnerInputBuffer };   // feel/buffer-speed
        if (ownerFeel)   // feel/locks-sprint-stop: shorter move, jump and evade gates (an estimate) and the sprint stop over the stop clip's BRANCH_Sprint_Finished
            config = config with { ActionLockScale = OwnerLockScale, SprintStopTime = classes?.SprintStop?.FinishedAt ?? SprintStopTuning.FallbackTime };
        if (ownerFeel)   // feel/melee-charge-2: longer melee lunges and a faster weapon switch (both estimates, owner's requests)
            config = config with { MeleeLungeScale = OwnerLungeScale, WeaponSwitchTime = config.WeaponSwitchTime * OwnerSwitchScale };
        return new GameplayTuning(config, $"{BlueprintPackage}: {MarineBlueprint}", placeholders, notes) { Melee = melee, Classes = classes };
    }

    /// <summary>
    /// Ork O5: the Ork balance every networked server and client loads (<c>smserver</c>, the client's <c>--connect</c>): the Ork Boy plays, in
    /// <see cref="OrkBalanceMode.Parity"/> (the owner's decision, 2026-10-28); <paramref name="data"/> (<c>--ork-data</c> on both, a test aid):
    /// <see cref="OrkBalanceMode.Data"/>. The mode is in the config hash, so a server and a client that differ refuse each other.
    /// </summary>
    public static OrkBalanceMode MatchOrk(bool data = false) => data ? OrkBalanceMode.Data : OrkBalanceMode.Parity;

    /// <summary>
    /// The tuning a networked server or client plays with (<c>--content</c>, docs/added-content.md): <see cref="Load"/>, plus the Ork Boy in
    /// <see cref="MatchOrk"/> balance only when <paramref name="extras"/> has <see cref="ContentExtras.Orks"/> (the one place the Ork is gated), and
    /// <see cref="SimConfig.Extras"/> set with every other added content's tuning (<see cref="ExtrasLoad"/>). <see cref="ContentExtras.None"/>, the
    /// faithful profile, is <see cref="Load"/> without the Ork exactly.
    /// </summary>
    public static GameplayTuning LoadForMatch(string gameDir, ContentExtras extras, bool orkData = false)
    {
        var tuning = Load(gameDir, extras.HasFlag(ContentExtras.Orks) ? MatchOrk(orkData) : null);
        if (extras == ContentExtras.None) return tuning;
        var config = tuning.Config with { Extras = extras };
        var notes = tuning.Notes.ToList();
        ExtrasLoad(ref config, gameDir, notes);   // EXTRAS-HOOK
        return tuning with { Config = config, Notes = notes };
    }

    /// <summary>
    /// Hook for added content (docs/added-content.md): reads each extra's tuning from the install into <paramref name="config"/> (whose
    /// <see cref="SimConfig.Extras"/> is already set and not None) and notes what it could not read. Implemented in an <c>Extras/</c> folder; each
    /// implementation returns early unless its flag is on.
    /// </summary>
    static partial void ExtrasLoad(ref SimConfig config, string gameDir, List<string> notes);

    /// <summary>
    /// feel/buffer-speed (the owner's request of 2026-10-29: "when approaching the end of any animation the player should be able to buffer the next
    /// inputs"): the input buffer's press window in seconds (<see cref="SimConfig.InputBufferWindow"/>; ours: 0.3 s, 9 ticks at 30 Hz).
    /// </summary>
    public const float OwnerInputBuffer = 0.3f;

    /// <summary>
    /// feel/locks-sprint-stop (the owner's request of 2026-10-29: "i generally feel too locked out of movement.. reduce generally the animation timings so i
    /// can jump or hop out of things more easily. mark that as a estimation for later review"): <see cref="SimConfig.ActionLockScale"/>, the factor on the
    /// times that gate moving, jumping and evading out of an action, not the evade's cooldown (sim/evade-cooldowns). 0.7 is an **estimate (owner's request)**, to review in a playtest.
    /// </summary>
    public const float OwnerLockScale = 0.7f;

    /// <summary>
    /// feel/melee-charge-2 (the owner's request of 2026-10-29: "make all melee step forwards step forward more"): <see cref="SimConfig.MeleeLungeScale"/>, the factor
    /// on every melee attack's forward root motion. 1.625 (1.3 from 2026-10-29, then +25% on 2026-10-31) is an **estimate (owner's request)**, ours, to review in a playtest; the charge and the dash attack keep theirs.
    /// </summary>
    public const float OwnerLungeScale = 1.625f;

    /// <summary>
    /// feel/melee-charge-2 (the owner's request of 2026-10-29: "weapon switching is slightly faster ... make a note that this is an estimation and needs to be
    /// measured against the original"): the factor on <see cref="SimConfig.WeaponSwitchTime"/> (the 0.6 s placeholder × 0.8 = 0.48 s). An **estimate (owner's
    /// request)**, to measure against the original; the raise after a switch is unchanged.
    /// </summary>
    public const float OwnerSwitchScale = 0.8f;

    /// <summary>The melee weapon read by <see cref="MeleeTuning"/> (combat knife), or null when it could not be read.</summary>
    public MeleeTuning? Melee { get; init; }

    /// <summary>
    /// The multiplayer classes read by <see cref="MpClasses"/> (class keys, roles, races, melee animations), index for index
    /// matching <see cref="SimConfig.Classes"/>; null when they could not be read (the config then keeps its single default class).
    /// </summary>
    public MpClasses? Classes { get; init; }

    /// <summary>Most classes: <see cref="SimConfig.MaxClasses"/> (64; snapshots carry the class in 6 bits since protocol 10).</summary>
    public const int MaxClasses = SimConfig.MaxClasses;

    /// <summary>
    /// Fills <see cref="SimConfig.Weapons"/> and <see cref="SimConfig.Classes"/> from the install (<see cref="MpClasses.Load"/>).
    /// On any failure, or data the simulation cannot use (no classes, out-of-range weapon index, a non-finite or
    /// non-positive value), the single default class stays and the reason is noted. <see cref="SimConfig.WeaponSwitchTime"/>
    /// is always a placeholder.
    /// </summary>
    public static SimConfig WithClasses(SimConfig config, string gameDir, List<string> notes, List<string> placeholders, out MpClasses? classes, OrkBalanceMode? ork = null,
        bool ownerBalance = true)
    {
        placeholders.Add(nameof(SimConfig.WeaponSwitchTime));
        classes = null;
        MpClasses loaded;
        // The owner's bolter balance (2026-10-28; deliberate change from the original) against this config's marine, before the Ork.
        try { loaded = MpClasses.Load(gameDir, ork: ork, ownerBalance: ownerBalance ? (config.TickSeconds, config.ShieldMax, config.HealthMax) : null); }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            notes.Add($"classes: {e.GetType().Name}: {e.Message}; one default class with the single-weapon values.");
            placeholders.Add(nameof(SimConfig.Classes));
            return config;
        }
        foreach (var note in loaded.Notes) notes.Add($"classes: {note}");
        // Fidelity gap 7: the heavy bolter brace's entry and exit times from the rig's behaviour graphs.
        var braceNotes = new List<string>();
        loaded = loaded with { Weapons = BraceTuning.WithTiming(loaded.Weapons, gameDir, braceNotes) };
        foreach (var note in braceNotes) notes.Add($"classes: {note}");
        if (WithClasses(config, loaded.Weapons, loaded.Loadouts, loaded.Thrown, loaded.JumpPacks, loaded.Evades, loaded.Reactions, loaded.Perks, loaded.Bodies) is not { } result)
        {
            notes.Add("classes: the loadouts are unusable (none, too many, a bad weapon or equipment index, or a non-finite or non-positive value); one default class with the single-weapon values.");
            placeholders.Add(nameof(SimConfig.Classes));
            return config;
        }
        classes = loaded;
        if (loaded.Throw is { } t) result = result with { ThrowRelease = t.Release, ThrowDuration = t.Duration };
        else { placeholders.Add(nameof(SimConfig.ThrowRelease)); placeholders.Add(nameof(SimConfig.ThrowDuration)); }
        if (loaded.Reactions is null) placeholders.Add(nameof(SimConfig.Reactions));
        return result;
    }

    /// <summary>
    /// Most weapons, and most entries in a class's choice list: indices travel as one byte with 255 meaning none/default
    /// (<c>SmNet.Protocol.NoIndex</c>, <c>DefaultChoice</c>).
    /// </summary>
    public const int MaxIndexed = 255;

    /// <summary>
    /// The config with these weapons and classes, or null when the simulation cannot use them: a non-finite or non-positive
    /// value, ammunition beyond u16, more than <see cref="MaxIndexed"/> weapons or choices, a weapon index out of range,
    /// a melee step that is not finite or a combo of more than <see cref="SimConfig.MaxMeleeSteps"/> light or heavy steps, or heat with Overheat ≤ 0, Recovery outside [0, Overheat), CoolRate ≤ 0, or a
    /// negative or non-finite PerShot, HighHeat or delay; a projectile, splash or charge value that is negative or not
    /// finite, fall-off or gravity points whose distances decrease, a negative or non-finite detonation delay, a projectile speed or splash radius ≤ 0, or a delivery kind without its data (a raycast needs damage &gt; 0
    /// and a cooldown &gt; 0; a projectile weapon its <see cref="WeaponSpec.Projectile"/>).
    /// </summary>
    /// <param name="thrown">Thrown items (<see cref="SimConfig.Thrown"/>) the classes' <see cref="ClassLoadout.EquipmentChoices"/>
    /// index; null: none. Refused: more than <see cref="MaxIndexed"/>, a count outside 1..255 (it travels as one byte), a
    /// negative or non-finite cooldown, or an unusable projectile.</param>
    /// <param name="jumpPacks">Jump packs (<see cref="SimConfig.JumpPacks"/>) the classes' <see cref="ClassLoadout.JumpPack"/> index; null: none.
    /// Refused: more than <see cref="MaxIndexed"/>, no height point, points whose times decrease, a negative or non-finite value, a gain
    /// above 1, MaxPower above 1e6, or an unusable blast.</param>
    /// <param name="evades">Evades (<see cref="SimConfig.Evades"/>, M21) the classes' <see cref="ClassLoadout.Evade"/> index; null: none.
    /// Refused: more than <see cref="MaxIndexed"/>, a clip whose path has fewer than 2 or more than 4096 points or a non-finite
    /// point, a duration that is not positive or lasts more than <see cref="Evade.MaxTicks"/> − 1 ticks, a branch time outside
    /// [0, duration], a spacing (MinInterval) that is negative or longer than <see cref="Evade.MaxTicks"/> ticks, a wall-hit
    /// angle outside [0, 90], a non-finite turn rate or property time, or a hop height curve that is not empty or as long as
    /// the path, or has a value outside [0, 10] m.</param>
    /// <param name="perks">Perks (<see cref="SimConfig.Perks"/>) the classes' <see cref="ClassLoadout.PerkChoices"/> and
    /// <see cref="ClassLoadout.DefaultPerk"/> index; null: none. Refused: more than <see cref="SimConfig.MaxPerks"/> (127: the 7-bit wire field), a null name, an unknown kind or a negative level.</param>
    /// <param name="reactions">Reaction clips (<see cref="SimConfig.Reactions"/>, M28); null: the config's stay. Refused: a kind None or twice,
    /// a clip whose length is not positive, a branch time outside [0, its end], or an end later than <see cref="Reaction.MaxTicks"/> − 1 ticks.</param>
    /// <param name="bodies">Class bodies (<see cref="SimConfig.Bodies"/>, Ork O4) the classes' <see cref="ClassLoadout.Body"/> index; null or empty: none
    /// (the config's own stays). Refused: more than <see cref="MaxIndexed"/> or an unusable one (<see cref="BodySpec.IsValid"/>).</param>
    public static SimConfig? WithClasses(SimConfig config, IReadOnlyList<WeaponSpec> weapons, IReadOnlyList<ClassLoadout> loadouts, IReadOnlyList<ThrownSpec>? thrown = null,
        IReadOnlyList<JumpPackSpec>? jumpPacks = null, IReadOnlyList<EvadeSpec>? evades = null, IReadOnlyList<ReactionSpec>? reactions = null,
        IReadOnlyList<PerkSpec>? perks = null, IReadOnlyList<BodySpec>? bodies = null)
    {
        perks ??= [];
        bodies ??= [];
        thrown ??= [];
        jumpPacks ??= [];
        evades ??= [];
        static bool Positive(float v) => float.IsFinite(v) && v > 0f;
        static bool NonNegative(float v) => float.IsFinite(v) && v >= 0f;
        static bool HeatOk(HeatSpec? h) => h is null || (Positive(h.Overheat) && NonNegative(h.PerShot) && NonNegative(h.HighHeat)
            && NonNegative(h.Recovery) && h.Recovery < h.Overheat && Positive(h.CoolRate) && NonNegative(h.CoolDelay) && NonNegative(h.CoolDelayAfterOverheat));
        static bool ReactionOk(ReactionEntry e) => Enum.IsDefined(e.Kind) && new[] { e.MinIntensity, e.Backward, e.Right, e.Up, e.Duration }.All(float.IsFinite);
        static bool StepsOk(IReadOnlyList<MeleeStep>? steps) => steps is { Count: <= SimConfig.MaxMeleeSteps } && steps.All(m => Positive(m.Duration)
            && new[] { m.HitTime, m.ComboOpen, m.ComboClose, m.RangedAt, m.MoveAt, m.Damage, m.SecondaryDamage, m.Lunge, m.HealthMultiplier, m.ShieldMultiplier, m.NoReactFrom, m.NoReactTo }.All(float.IsFinite)
            && NonNegative(m.ShieldDamage) && NonNegative(m.SecondaryShieldDamage) && NonNegative(m.YComboOpen) && m.YComboOpen <= m.YComboClose && m.YComboClose <= m.Duration
            && ReactionOk(m.PrimaryReaction) && ReactionOk(m.SecondaryReaction) && (m.Stomp is null || SplashOk(m.Stomp)));
        // Weapons C: a light attack's heavy target and a heavy strike's Y,Y target lie inside the combo's heavy list (0: the old rule / none; -1: none).
        static bool ChainsOk(IReadOnlyList<MeleeStep> light, IReadOnlyList<MeleeStep> heavy) =>
            light.All(m => m.HeavyNext >= -1 && m.HeavyNext <= heavy.Count && m.YNext == 0) && heavy.All(m => m.YNext >= 0 && m.YNext <= heavy.Count && m.HeavyNext == 0);
        static bool DamageOk(DamageSpec? d) => d is null || new[] { d.Damage, d.ShieldDamage, d.HeadMultiplier, d.BodyMultiplier, d.ShieldHeadMultiplier, d.ShieldBodyMultiplier }.All(NonNegative);
        // Distances must not decrease (the simulation's interpolation and step lookup read them in order).
        static bool PointsOk(IReadOnlyList<DistancePoint>? points) => points is { Count: <= MaxIndexed } && points.All(p => NonNegative(p.Distance) && NonNegative(p.Value))
            && points.Zip(points.Skip(1)).All(pair => pair.Second.Distance >= pair.First.Distance);
        static bool Fraction(float v) => float.IsFinite(v) && v >= 0f && v <= 1f;
        static bool SplashOk(SplashSpec? s) => s is not null && Positive(s.Radius) && s.Damage is not null && DamageOk(s.Damage) && PointsOk(s.FallOff) && s.TargetFilter is not null
            && NonNegative(s.Lifetime) && NonNegative(s.Period) && NonNegative(s.Blindness) && s.Reactions is { Count: <= MaxIndexed } && s.Reactions.All(ReactionOk);
        static bool RigidOk(RigidSpec? r) => r is null || (float.IsFinite(r.GroundAngle) && r.GroundAngle is >= 0f and <= 180f
            && Fraction(r.GroundHorizontalReduction) && Fraction(r.GroundVerticalReduction) && Fraction(r.EntityHorizontalReduction) && Fraction(r.EntityVerticalReduction)
            && PointsOk(r.WallHorizontalReduction) && PointsOk(r.WallVerticalReduction) && r.WallHorizontalReduction.All(q => Fraction(q.Value)) && r.WallVerticalReduction.All(q => Fraction(q.Value))
            && NonNegative(r.DelayBeforeCollision) && NonNegative(r.DelayBetweenCollision));
        static bool ProjectileOk(ProjectileSpec? p) => p is null || (p.Blueprint is not null && p.CollisionType is not null && Positive(p.Speed)
            && NonNegative(p.Lifetime) && NonNegative(p.CollisionRadius) && PointsOk(p.Gravity) && p.MaxBounces >= 0 && NonNegative(p.GroundLifetime)
            && float.IsFinite(p.ExtraAimAngle) && p.MaxActive >= 0 && DamageOk(p.DetonateDamage)
            && (p.Splash is null || SplashOk(p.Splash)) && p.ExtraSplashes is { Count: <= MaxIndexed } && p.ExtraSplashes.All(SplashOk) && RigidOk(p.Rigid));
        static bool ConeOk(ConeSpec? c) => c is not null && Positive(c.Length) && float.IsFinite(c.Angle) && c.Angle is > 0f and < 180f && NonNegative(c.StartLength)
            && float.IsFinite(c.Offset) && c.Damage is not null && DamageOk(c.Damage) && PointsOk(c.FallOff) && PointsOk(c.CoverageFallOff);
        static bool RechargeOk(RechargeSpec? r) => r is null || (NonNegative(r.Delay) && Positive(r.PerSecond));
        static bool ThrownOk(ThrownSpec? t) => t is not null && t.Name is not null && t.Count is > 0 and <= byte.MaxValue && NonNegative(t.Cooldown)
            && float.IsFinite(t.MaxRange) && t.Projectile is not null
            && (t.Stim is { } stim ? stim.Valid && ReferenceEquals(t.Projectile, ThrownSpec.NoProjectile) : ProjectileOk(t.Projectile));   // perks/inert-2: a stim launches nothing
        static bool ChargeOk(ChargeSpec? c) => c is null || (c.Ability is not null && c.Stages is { Count: > 0 and <= MaxIndexed } && NonNegative(c.MaxChargeTime)
            && Positive(c.MinTimeMultiplier) && c.AmmoToStart >= 0 && c.Stages.All(s => s is not null && s.Name is not null && NonNegative(s.MinTime)
                && NonNegative(s.HeatPerSecond) && NonNegative(s.MoveSpeedMultiplier)
                && (s.Shot is not { } shot || (Enum.IsDefined(shot.Delivery) && DamageOk(shot.Damage) && NonNegative(shot.MaxRange) && NonNegative(shot.ShotCooldown)
                    && shot.AmmoCost >= 0 && NonNegative(shot.HeatPerShot) && NonNegative(shot.FireDelay) && ProjectileOk(shot.Projectile)
                    && (shot.Delivery == DeliveryKind.Raycast ? shot.Damage is not null : shot.Projectile is not null)))));
        // A projectile weapon may have no direct-hit damage and no cooldown (the grenade launcher: 0 s, paced by its clip of 1).
        // A cone needs its ConeSpec and a cooldown (it is not a projectile).
        static bool DeliveryOk(WeaponSpec w) => Enum.IsDefined(w.Delivery) && ProjectileOk(w.Projectile) && w.AmmoPerShot > 0 && ChargeOk(w.Charge)
            && w.DetonationDelays is { Count: <= MaxIndexed } && w.DetonationDelays.All(NonNegative) && RechargeOk(w.Recharge)
            && w.Delivery switch
            {
                DeliveryKind.Raycast => Positive(w.Damage) && Positive(w.ShotCooldown) && w.Projectile is null && w.Cone is null,
                DeliveryKind.Cone => ConeOk(w.Cone) && NonNegative(w.Damage) && Positive(w.ShotCooldown) && w.Projectile is null,
                _ => NonNegative(w.Damage) && NonNegative(w.ShotCooldown) && w.Projectile is not null && w.Cone is null,
            };
        bool WeaponOk(WeaponSpec w) => w is not null && Enum.IsDefined(w.Slot) && Enum.IsDefined(w.Trigger)
            && DeliveryOk(w) && w.ClipSize is > 0 and <= ushort.MaxValue && w.ReserveSize is >= 0 and <= ushort.MaxValue   // ammo travels as u16
            && Positive(w.ReloadTime) && Positive(w.MaxRange)
            && NonNegative(w.FallOffStart) && NonNegative(w.FallOffEnd) && NonNegative(w.FallOffEndMultiplier)
            && NonNegative(w.HeadMultiplier) && NonNegative(w.BodyMultiplier) && NonNegative(w.ShieldHeadMultiplier) && NonNegative(w.ShieldBodyMultiplier)
            && HeatOk(w.Heat) && NonNegative(w.RaiseDelay) && w.RaiseDelay / config.TickSeconds <= byte.MaxValue   // M31: a raise travels as ticks in a byte
            // Fidelity gap 7: a brace's transitions fit the own record's 6 bits; a vent's rates; an overheat's speed factor.
            && Brace.SpecOk(w.Brace, config.TickSeconds) && Vent.SpecOk(w.Vent)
            && (w.Heat is null || (NonNegative(w.Heat.OverheatSpeed) && NonNegative(w.Heat.OverheatSpeedTime)))
            && w.BurstLength is >= 0 and <= WeaponSpec.MaxBurstLength && NonNegative(w.BurstInterval)   // M43: a burst's rounds left travel in 2 bits
            // M43: a burst's last round leaves before its cooldown ends (the rounds are timed on the cooldown; a longer burst would lock the weapon).
            && (w.BurstLength <= 1 || (Positive(w.BurstInterval) && w.BurstInterval * (w.BurstLength - 1) < w.ShotCooldown));
        // M43: a perk variant passes the same checks, names the same weapon and slot, needs 1 or 2 upgrade strings and has no variants of its own.
        static bool UpgradesOk<T>(UpgradeVariant<T> v) where T : class => v is not null && v.Spec is not null && v.Upgrades is { Count: > 0 and <= Variants.MaxUpgrades } && v.Upgrades.All(u => !string.IsNullOrEmpty(u));
        bool weaponsOk = weapons.Count <= MaxIndexed && weapons.All(w => WeaponOk(w) && w.Variants.All(v => UpgradesOk(v) && WeaponOk(v.Spec) && v.Spec.Variants.Count == 0
            && v.Spec.Name == w.Name && v.Spec.Slot == w.Slot));
        bool thrownOk = thrown.Count <= MaxIndexed && thrown.All(t => ThrownOk(t) && t.Variants.All(v => UpgradesOk(v) && ThrownOk(v.Spec) && v.Spec.Variants.Count == 0));
        static bool JumpOk(JumpPackSpec? j) => j is not null && j.Name is not null && PointsOk(j.HeightMap) && j.HeightMap.Count > 0 && PointsOk(j.PowerCost)
            && new[] { j.MaxPower, j.RechargeRate, j.JumpingGravity, j.HangGravity, j.HangTime, j.FallingGravity, j.ForwardSpeed, j.MoveGain, j.DriftGain,
                j.PoundDelay, j.PoundGravity, j.PoundSpeed, j.PoundLimitAngle, j.TakeOffDelay, j.LandMoveLock, j.LandJumpLock, j.LandIdleMoveLock, j.LandIdleJumpLock, j.PoundRecovery, j.ReleaseCutDelay, j.ReleaseCutDecay }.All(NonNegative)
            && j.MoveGain <= 1f && j.DriftGain <= 1f && j.MaxPower <= 1e6f && j.PoundLimitAngle <= 180f
            && (j.Pound is null || SplashOk(j.Pound)) && j.PoundReactions is { Count: <= MaxIndexed } && j.PoundReactions.All(r => r.Reaction is not null && float.IsFinite(r.Intensity))
            && j.Landings is { Count: <= MaxIndexed } && j.Landings.All(l => l is not null && NonNegative(l.MinDrop) && SplashOk(l.Blast))
            && j.TakeOffBlasts is { Count: <= MaxIndexed } && j.TakeOffBlasts.All(SplashOk);
        bool jumpOk = jumpPacks.Count <= MaxIndexed && jumpPacks.All(j => JumpOk(j) && j.Variants.All(v => UpgradesOk(v) && JumpOk(v.Spec) && v.Spec.Variants.Count == 0));
        float tick = config.TickSeconds;
        bool Within(float v, float max) => float.IsFinite(v) && v >= 0f && v <= max;
        bool MoveOk(EvadeMove? m) => m is not null && m.Animation is not null && m.Path is { Count: >= 2 and <= 4096 }
            && m.Path.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y)) && Positive(m.Duration) && m.Duration / tick <= Evade.MaxTicks - 1
            && new[] { m.MoveAt, m.RangedAt, m.SwitchAt, m.MeleeAt, m.NextAt }.All(t => Within(t, m.Duration))
            && m.Height is not null && (m.Height.Count == 0 || m.Height.Count == m.Path.Count)
            // M44: a dash's flight follows the clip's signed root height (it falls below the take-off after the arc; up to 100 m here)
            && m.Height.All(h => m.Flight ? float.IsFinite(h) && h >= -100f && h <= 10f : Within(h, 10f))
            && Within(m.JumpAt, m.Duration) && (m.Landing is null || (m.Flight && m.Landing.Landing is null && !m.Landing.Flight && MoveOk(m.Landing)));
        // M44: a dash has flights with landings in every direction and a decision time; other evades have neither.
        bool DashOk(EvadeSpec e) => e.Dash
            ? new[] { e.Forward, e.Back, e.Left, e.Right }.All(m => m.Flight && m.Landing is not null) && e.ToJumpAt > 0f && e.ToJumpAt <= 1f   // M44: 0 would make every press a free jump
            : new[] { e.Forward, e.Back, e.Left, e.Right }.All(m => !m.Flight && m.Landing is null) && e.ToJumpAt == 0f;
        bool EvadeOk(EvadeSpec? e) => e is not null && e.Name is not null && MoveOk(e.Forward) && MoveOk(e.Back) && MoveOk(e.Left) && MoveOk(e.Right)
            && Within(e.MinInterval, Evade.MaxTicks * tick) && Within(e.WallHitAngle, 90f) && float.IsFinite(e.MaxTurnRate)
            && NonNegative(e.TargetableModeTime) && NonNegative(e.CollisionModeTime) && e.AnimationSet is not null && e.Camera is not null && DashOk(e);
        bool evadesOk = evades.Count <= MaxIndexed && evades.All(EvadeOk);
        bool Index(int i) => i >= -1 && i < weapons.Count;
        bool Choice(int i) => i >= 0 && i < weapons.Count;
        bool ClipOk(ReactionClip? k) => k is not null && k.Animation is not null && Positive(k.Duration) && Within(k.EndAt, k.Duration) && k.EndAt / tick <= Reaction.MaxTicks - 1
            && new[] { k.RunAt, k.EvadeAt, k.MeleeAt, k.ReactAt }.All(t => Within(t, k.EndAt));
        bool reactionsOk = reactions is null || (reactions.Count <= 3 && reactions.All(r => r is not null && r.Kind != ReactionKind.None && Enum.IsDefined(r.Kind)
            && ClipOk(r.Front) && ClipOk(r.Right) && ClipOk(r.Back) && ClipOk(r.Left)) && reactions.Select(r => r.Kind).Distinct().Count() == reactions.Count);
        bool RushMoveOk(RushMove? m) => m is not null && m.Animation is not null && m.Path is { Count: >= 2 and <= 4096 } && m.Path.All(p => float.IsFinite(p.X) && float.IsFinite(p.Y))
            && Positive(m.ClipLength) && Within(m.End, m.ClipLength) && m.End / tick <= Rush.MaxTicks - 1 && new[] { m.MoveAt, m.EvadeAt, m.MeleeAt }.All(t => Within(t, m.End));
        bool RushOk(RushSpec? r) => r is null || (r.Ability is not null && RushMoveOk(r.Rush) && RushMoveOk(r.Collision) && r.Primary is not null && DamageOk(r.Primary)
            && r.Secondary is not null && DamageOk(r.Secondary) && ReactionOk(r.PrimaryReaction) && ReactionOk(r.SecondaryReaction)
            && Positive(r.Range) && Within(r.MinInterval, Rush.MaxTicks * tick) && Within(r.ContactUntil, r.Rush.End) && Within(r.WallFrom, r.Rush.End)
            && Within(r.WallHitAngle, 90f) && Within(r.MinForwardStick, 1f) && Within(r.SideSteer, 90f) && NonNegative(r.ContactMargin) && Within(r.ContactHalfAngle, 180f)
            && Within(r.HyperArmourUntil, r.Rush.End));   // M32: the charger's hyper armour ends inside the rush
        bool loadoutsOk = loadouts.Count is > 0 and <= MaxClasses && loadouts.All(l => l is not null && Index(l.Primary) && Index(l.Pistol) && StepsOk(l.Melee) && StepsOk(l.MeleeHeavy) && ChainsOk(l.Melee, l.MeleeHeavy) && RushOk(l.MeleeRush)
            && l.PrimaryChoices is { Count: <= MaxIndexed } && l.PrimaryChoices.All(Choice)
            && l.PistolChoices is { Count: <= MaxIndexed } && l.PistolChoices.All(Choice)
            && l.MeleeChoices is { Count: <= MaxIndexed } && l.MeleeChoices.All(m => m is not null && m.Wargear is not null && StepsOk(m.Steps) && StepsOk(m.Heavy) && ChainsOk(m.Steps, m.Heavy) && RushOk(m.Rush)
                && m.Variants.All(v => UpgradesOk(v) && v.Spec.Wargear is not null && StepsOk(v.Spec.Steps) && StepsOk(v.Spec.Heavy) && ChainsOk(v.Spec.Steps, v.Spec.Heavy) && RushOk(v.Spec.Rush) && v.Spec.Variants.Count == 0
                    && v.Spec.Steps.Count == m.Steps.Count && v.Spec.Heavy.Count == m.Heavy.Count))
            && l.EquipmentChoices is { Count: <= MaxIndexed } && l.EquipmentChoices.All(i => i >= 0 && i < thrown.Count) && l.JumpPack >= -1 && l.JumpPack < jumpPacks.Count
            && l.Evade >= -1 && l.Evade < evades.Count
            && Enum.IsDefined(l.Side) && l.Role is not null
            && l.PerkChoices is { Count: <= MaxIndexed } && l.PerkChoices.All(i => i >= 0 && i < perks.Count) && l.DefaultPerk >= -1 && l.DefaultPerk < perks.Count
            && l.Body >= -1 && l.Body < bodies.Count
            && (l.Race == Race.None || (Races.IsKnown(l.Race) && l.Side == Team.None)));   // Ork O5: a race of its own only for a class without a side
        bool bodiesOk = bodies.Count <= MaxIndexed && bodies.All(b => b is not null && b.IsValid);
        bool perksOk = perks.Count <= SimConfig.MaxPerks && perks.All(p => p is not null && p.Name is not null && Enum.IsDefined(p.Kind) && p.Level >= 0);
        if (!perksOk || !weaponsOk || !loadoutsOk || !thrownOk || !jumpOk || !evadesOk || !reactionsOk || !bodiesOk) return null;
        // Copies, so later changes to the reader's lists cannot change a config (and its hash) after the handshake.
        return config with
        {
            Weapons = weapons.ToArray(),
            Thrown = thrown.ToArray(),
            Perks = perks.ToArray(),
            Bodies = bodies.Count == 0 ? config.Bodies : bodies.Select(b => b with { HitVolumes = b.HitVolumes?.ToArray() }).ToArray(),
            Reactions = reactions is null ? config.Reactions : reactions.ToArray(),
            JumpPacks = jumpPacks.Select(j => j with
            {
                HeightMap = j.HeightMap.ToArray(), PowerCost = j.PowerCost.ToArray(), PoundReactions = j.PoundReactions.ToArray(), Landings = j.Landings.ToArray(),
            }).ToArray(),
            Evades = evades.Select(e => e with
            {
                Forward = CopyMove(e.Forward), Back = CopyMove(e.Back), Left = CopyMove(e.Left), Right = CopyMove(e.Right),
            }).ToArray(),
            Classes = loadouts.Select(l => l with
            {
                Melee = Array.AsReadOnly(l.Melee.ToArray()),
                MeleeHeavy = Array.AsReadOnly(l.MeleeHeavy.ToArray()),
                PrimaryChoices = Array.AsReadOnly(l.PrimaryChoices.ToArray()),
                PistolChoices = Array.AsReadOnly(l.PistolChoices.ToArray()),
                MeleeChoices = Array.AsReadOnly(l.MeleeChoices.Select(m => m with { Steps = Array.AsReadOnly(m.Steps.ToArray()), Heavy = Array.AsReadOnly(m.Heavy.ToArray()) }).ToArray()),
                EquipmentChoices = Array.AsReadOnly(l.EquipmentChoices.ToArray()),
                PerkChoices = Array.AsReadOnly(l.PerkChoices.ToArray()),

            }).ToArray(),
        };
    }

    /// <summary>A copy of an evade move's lists (and its landing part's, M44).</summary>
    static EvadeMove CopyMove(EvadeMove m) => m with { Path = m.Path.ToArray(), Height = m.Height.ToArray(), Landing = m.Landing is null ? null : CopyMove(m.Landing) };

    /// <summary>
    /// Sets <see cref="SimConfig.PlayerUpgrades"/> (M43) from the Space Marine blueprint for the config's perks' upgrade strings
    /// (<see cref="PlayerUpgrades.Read"/>); the Chaos blueprint is read too and a difference noted (PvP sides share one config).
    /// Unusable values (a negative or non-finite multiplier, a blast that is not usable) leave the list empty with a note.
    /// </summary>
    public static SimConfig WithPlayerUpgrades(SimConfig config, BafDocument marine, BafDocument? chaos, List<string> notes)
    {
        var strings = config.Perks.Select(p => p.UpgradeString).OfType<string>().Where(u => u.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var upgrades = PlayerUpgrades.Read(marine, strings, notes);
        if (chaos is not null)
        {
            var other = PlayerUpgrades.Read(chaos, strings, []);
            static byte[] Bytes(SimConfig c) => BitConverter.GetBytes(c.Hash());
            if (!Bytes(new SimConfig { PlayerUpgrades = upgrades }).AsSpan().SequenceEqual(Bytes(new SimConfig { PlayerUpgrades = other })))
                notes.Add("perks: the Chaos blueprint's perk entries differ from the Space Marine's; the shared config uses the Space Marine's");
        }
        static bool Factor(float v) => float.IsFinite(v) && v >= 0f;
        static bool BlastOk(SplashSpec s) => float.IsFinite(s.Radius) && s.Radius > 0f && s.Damage is not null && Factor(s.Damage.Damage) && Factor(s.Damage.ShieldDamage)
            && s.FallOff.All(p => Factor(p.Distance) && Factor(p.Value)) && s.FallOff.Zip(s.FallOff.Skip(1)).All(q => q.Second.Distance >= q.First.Distance);
        bool ok = upgrades.All(u => Factor(u.ReceivedRanged) && Factor(u.ReceivedMelee) && Factor(u.ReceivedExplosive)
            && new[] { u.HealthRegenRate, u.HealthRegenDelay, u.ShieldRegenRate, u.ShieldRegenDelay }.All(float.IsFinite) && Factor(u.DeathBlastDelay)
            && (u.Shield is null || (Factor(u.Shield.Max) && Factor(u.Shield.RegenDelay) && Factor(u.Shield.RegenRate)))
            && u.DeathBlasts.All(BlastOk) && u.ShieldBreakBlasts.All(BlastOk));
        if (!ok) { notes.Add("perks: a player-blueprint perk value is unusable; no player-blueprint perk effects"); return config; }
        notes.Add($"perks: player blueprint upgrades for {string.Join(", ", upgrades.Select(u => u.Upgrade))}");
        return config with { PlayerUpgrades = upgrades.ToArray() };
    }

    /// <summary>
    /// Sets <see cref="SimConfig.HitVolumes"/> from the marine rig's ragdoll (<see cref="HitVolumeTuning"/>, M20) and drops
    /// "HitVolumes" from the placeholders; on any failure the placeholder volumes stay and the reason is noted.
    /// </summary>
    public static SimConfig WithHitVolumes(SimConfig config, string gameDir, List<string> notes, List<string> placeholders)
    {
        IReadOnlyList<HitVolume>? volumes;
        try { volumes = HitVolumeTuning.Load(gameDir, notes); }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            notes.Add($"hit volumes: {e.GetType().Name}: {e.Message}; placeholders");
            return config;
        }
        if (volumes is null) return config;
        placeholders.Remove("HitVolumes");
        config = config with { HitVolumes = volumes };
        // fix/hitboxes (protocol 37): the same volumes posed by the jump pack's flight clips, for players in flight; without them the rest pose stays.
        try
        {
            if (FlightHitVolumes.Load(gameDir, volumes, notes, config.TickSeconds) is { } flight)
            {
                notes.Add($"hit volumes in flight: {flight.Poses.Count} flight clips x {FlightHitVolumes.Steps} fractions, take-off {flight.TakeOffClip ?? "none"} x {flight.TakeOff.Count} ticks");
                config = config with { FlightHitVolumes = flight };
            }
        }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            notes.Add($"hit volumes in flight: {e.GetType().Name}: {e.Message}; the rest pose stays in flight");
        }
        return config;
    }

    /// <summary>
    /// Fills the melee combo and reach from the install (<see cref="MeleeTuning.Load"/>: the combat knife's behavior-graph
    /// timing and wargear damage). On any failure the placeholders stay and the reason is noted. The cone and vertical
    /// reach are always placeholders.
    /// </summary>
    public static SimConfig WithMelee(SimConfig config, string gameDir, List<string> notes, List<string> placeholders, out MeleeTuning? melee)
    {
        melee = null;
        placeholders.Add(nameof(SimConfig.MeleeConeDegrees));
        placeholders.Add(nameof(SimConfig.MeleeVerticalReach));
        try { melee = MeleeTuning.Load(gameDir); }
        catch (Exception e) when (e is not (StackOverflowException or AccessViolationException or OutOfMemoryException))
        {
            notes.Add($"melee: {e.GetType().Name}: {e.Message}; melee values are placeholders.");
        }
        if (melee is null || melee.Steps.Count == 0)
        {
            placeholders.Add(nameof(SimConfig.MeleeSteps));
            placeholders.Add(nameof(SimConfig.MeleeRange));
            return config;
        }
        foreach (var note in melee.Notes) notes.Add($"melee {melee.Weapon}: {note}");
        bool valid = melee.Steps.All(m => float.IsFinite(m.Duration) && m.Duration > 0 && new[] { m.HitTime, m.ComboOpen, m.ComboClose, m.RangedAt, m.MoveAt, m.Damage, m.SecondaryDamage, m.Lunge }.All(float.IsFinite));
        if (!valid)
        {
            notes.Add($"melee {melee.Weapon}: a step has a non-finite or non-positive value; melee values are placeholders.");
            placeholders.Add(nameof(SimConfig.MeleeSteps));
            placeholders.Add(nameof(SimConfig.MeleeRange));
            return config;
        }
        if (melee.Notes.Any(n => n.Contains("max_range"))) placeholders.Add(nameof(SimConfig.MeleeRange));
        return config with { MeleeSteps = melee.Steps.ToArray(), MeleeRange = melee.Range };
    }

    /// <summary>Maps one player blueprint's locomotion onto <see cref="SimConfig"/>.</summary>
    public static SimConfig FromBlueprint(BafDocument blueprint, List<string> notes, out IReadOnlyList<string> placeholders)
    {
        var defaults = new SimConfig();
        var missing = new List<string>();
        var locomotion = blueprint.Root.Table("extensions/biped_locomotion_ext");
        var standing = locomotion?.Table("speed_standing");
        var aiming = locomotion?.Table("speed_aiming");
        if (locomotion is null || standing is null) notes.Add("biped_locomotion_ext/speed_standing not found; all movement values are placeholders.");

        float Get(BafTable? table, string name, float fallback, string field, bool allowZero = false)
        {
            // Speeds, times and rates must be positive (a zero acceleration would freeze all players); delays may be 0.
            if (table?.Float(name) is { } v && float.IsFinite(v) && (v > 0 || (allowZero && v == 0))) return v;
            missing.Add(field);
            return fallback;
        }
        var health = blueprint.Root.Table("extensions/health_ext");
        var shield = health is { } h ? DefaultEntry(h.Table("armourshield_info"), "armourshield_info") : null;
        var config = defaults with
        {
            HealthMax = Get(health, "health_max", defaults.HealthMax, nameof(SimConfig.HealthMax)),
            HealthRegenDelay = Get(health, "health_regen_delay", defaults.HealthRegenDelay, nameof(SimConfig.HealthRegenDelay), allowZero: true),
            HealthRegenRate = Get(health, "health_regen_rate", defaults.HealthRegenRate, nameof(SimConfig.HealthRegenRate)),
            ShieldMax = Get(shield, "armourshield_max", defaults.ShieldMax, nameof(SimConfig.ShieldMax)),
            ShieldRegenDelay = Get(shield, "armourshield_regen_delay", defaults.ShieldRegenDelay, nameof(SimConfig.ShieldRegenDelay), allowZero: true),
            ShieldRegenRate = Get(shield, "armourshield_regen_rate", defaults.ShieldRegenRate, nameof(SimConfig.ShieldRegenRate)),
            RunSpeed = Get(standing, "run_speed", defaults.RunSpeed, nameof(SimConfig.RunSpeed)),
            MaxWalkableSlope = Get(locomotion, "max_walkable_slope", defaults.MaxWalkableSlope, nameof(SimConfig.MaxWalkableSlope)),
            StrafeSpeed = Get(standing, "strafe_speed", defaults.StrafeSpeed, nameof(SimConfig.StrafeSpeed)),
            BackSpeed = Get(standing, "back_speed", defaults.BackSpeed, nameof(SimConfig.BackSpeed)),
            SprintSpeed = Get(standing, "sprint_speed", defaults.SprintSpeed, nameof(SimConfig.SprintSpeed)),
            WalkSpeed = Get(standing, "walk_speed", defaults.WalkSpeed, nameof(SimConfig.WalkSpeed)),
            // Aim mode (protocol 12): the aiming table's run, strafe and back speeds (its sprint and walk speeds are not used).
            AimRunSpeed = Get(aiming, "run_speed", defaults.AimRunSpeed, nameof(SimConfig.AimRunSpeed)),
            AimStrafeSpeed = Get(aiming, "strafe_speed", defaults.AimStrafeSpeed, nameof(SimConfig.AimStrafeSpeed)),
            AimBackSpeed = Get(aiming, "back_speed", defaults.AimBackSpeed, nameof(SimConfig.AimBackSpeed)),
            GroundTimeToMaxSpeed = Get(locomotion, "ground_time_till_max_speed", defaults.GroundTimeToMaxSpeed, nameof(SimConfig.GroundTimeToMaxSpeed)),
            MaxAcceleration = Get(locomotion, "max_acceleration", defaults.MaxAcceleration, nameof(SimConfig.MaxAcceleration)),
        };
        missing.Add(nameof(SimConfig.TickRate));
        missing.Add(nameof(SimConfig.Gravity));
        missing.Add(nameof(SimConfig.RespawnTime));
        missing.Add(nameof(SimConfig.ShotConeDegrees));   // ours (protocol 12)
        missing.AddRange([nameof(SimConfig.EyeHeight), "HitVolumes", nameof(SimConfig.MaxRewindTicks), nameof(SimConfig.CapsuleRadius), nameof(SimConfig.CapsuleHeight), nameof(SimConfig.StepHeight)]);
        placeholders = missing;
        return config;
    }

    /// <summary>
    /// Maps the bolter's wargear file onto the weapon fields: the primary ability's default entries (those with no
    /// required upgrade), damage, health and shield multipliers for body and head, cooldown, range, fall-off,
    /// reload time, clip and reserve.
    /// </summary>
    public static SimConfig WithWeapon(SimConfig config, BafDocument weapon, List<string> notes, List<string> placeholders)
    {
        var primary = weapon.Root.Table("ability_data/ability_definitions/ability_ranged_primary");
        var delivery = primary?.Table("delivery");
        var damageEntry = DefaultEntry(delivery?.Table("hit_sim/actions_by_upgrade"), "entry") ?? DefaultEntry(delivery?.Table("hit_sim/actions"), "entry");
        var damage = damageEntry?.Table("action/commands/do_damage") ?? delivery?.Table("hit_sim/actions/do_damage");
        var health = damage?.Table("damage_material_multipliers");
        var shield = damage?.Table("armourshield_damage_material_multipliers");
        var tuning = DefaultEntry(delivery?.Table("basic_weapon_tuning"), "entry");
        var reload = weapon.Root.Table("ability_data/ability_definitions/ability_reload/reload_upgrade_info");
        var reloadInfo = reload is { } r ? DefaultEntry(r, "wargear\\reload_info") : null;
        var ammo = DefaultEntry(weapon.Root.Table("common_data/ammunition/entry/by_upgrade"), "ammunition_info");
        var fallOff = delivery?.Table("damage_fall_off")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "fall_off_entry")
            .Select(e => (Distance: e.AsTable().Float("distance") ?? -1f, Multiplier: e.AsTable().Float("damage_multiplier") ?? -1f))
            .Where(p => p.Distance >= 0 && p.Multiplier >= 0).OrderBy(p => p.Distance).ToList() ?? [];

        float Get(BafTable? table, string name, float fallback, string field, bool allowZero = false)
        {
            if (table?.Float(name) is { } v && float.IsFinite(v) && (v > 0 || (allowZero && v == 0))) return v;
            placeholders.Add(field);
            return fallback;
        }
        int GetInt(BafTable? table, string name, int fallback, string field)
        {
            if (table?[name] is { } e && (e.Type == BafType.Int ? e.Raw : (int)e.AsFloat()) is var v and > 0) return v;
            placeholders.Add(field);
            return fallback;
        }
        var c = config with
        {
            Damage = Get(damage, "damage_general", config.Damage, nameof(SimConfig.Damage)),
            BodyMultiplier = Get(health, "default_body", config.BodyMultiplier, nameof(SimConfig.BodyMultiplier), allowZero: true),
            HeadMultiplier = Get(health, "default_head", config.HeadMultiplier, nameof(SimConfig.HeadMultiplier), allowZero: true),
            ShieldBodyMultiplier = Get(shield, "default_body", config.ShieldBodyMultiplier, nameof(SimConfig.ShieldBodyMultiplier), allowZero: true),
            ShieldHeadMultiplier = Get(shield, "default_head", config.ShieldHeadMultiplier, nameof(SimConfig.ShieldHeadMultiplier), allowZero: true),
            ShotCooldown = Get(tuning, "shot_cooldown", config.ShotCooldown, nameof(SimConfig.ShotCooldown)),
            MaxRange = Get(delivery, "max_range", config.MaxRange, nameof(SimConfig.MaxRange)),
            ReloadTime = Get(reloadInfo, "triggered_reload_time", config.ReloadTime, nameof(SimConfig.ReloadTime)),
            ClipSize = GetInt(ammo, "capacity_clip", config.ClipSize, nameof(SimConfig.ClipSize)),
            ReserveSize = GetInt(ammo, "capacity_reserve", config.ReserveSize, nameof(SimConfig.ReserveSize)),
        };
        // The file's fall-off is a list of points; the simulation takes the last flat point and the final point.
        if (fallOff.Count >= 1 && fallOff.All(p => p.Multiplier >= 1f))
            c = c with { FallOffStart = c.MaxRange, FallOffEnd = c.MaxRange, FallOffEndMultiplier = 1f };   // no fall-off
        else if (fallOff.Count >= 2 && fallOff.FindLastIndex(p => p.Multiplier >= 1f) is var flatAt and >= 0 && flatAt < fallOff.Count - 1)
        {
            var (flat, end) = (fallOff[flatAt], fallOff[^1]);
            c = c with { FallOffStart = flat.Distance, FallOffEnd = end.Distance, FallOffEndMultiplier = end.Multiplier };
            bool ignored = fallOff.Skip(flatAt + 1).SkipLast(1).Any() || fallOff.Take(flatAt).Any(p => p.Multiplier != 1f);
            if (ignored)
                notes.Add($"{Bolter}: fall-off has {fallOff.Count} points; only the last x1 point and the final point are used.");
        }
        else placeholders.Add("FallOff");
        return c;
    }

    /// <summary>The child table named <paramref name="name"/> whose required-upgrade list is empty (the default variant).</summary>
    static BafTable? DefaultEntry(BafTable? parent, string name)
    {
        if (parent is not { } p) return null;
        var candidates = p.Entries.Where(e => e.Type == BafType.Table && e.Name == name).Select(e => e.AsTable()).ToList();
        foreach (var t in candidates)
            if (t.Table("required_upgrade_name/required_list") is not { } list || !list.Entries.Any()) return t;
        return null;   // every variant needs an upgrade: the caller keeps (and lists) the placeholder
    }
}
