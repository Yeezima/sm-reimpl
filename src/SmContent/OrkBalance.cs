using SmSim;

namespace SmContent;

/// <summary>
/// How the Ork Boy's values are set (Ork O4; docs/ork-faction-design.md section 2). <see cref="Parity"/> (the default, the owner's
/// decision 2026-10-28: "balanced to Tactical parity"): health, shield and weapon damage scaled from the AI-tuned data to the Tactical's
/// (a **deliberate change from the original**, docs/changes-from-original.md). <see cref="Data"/>: the data's own values (all factors 1;
/// a test and comparison aid).
/// </summary>
public enum OrkBalanceMode : byte { Parity = 0, Data = 1 }

/// <summary>How one balance row turns data into a result (<see cref="BalanceRow"/>).</summary>
public enum BalanceRule : byte
{
    /// <summary>The reference's value (factor reference / data).</summary>
    Scale,
    /// <summary>The reference's value where the data has none or 0 (no factor).</summary>
    Set,
    /// <summary>The data's value (factor 1; feel and animation timing).</summary>
    Kept,
    /// <summary>The reference weapon's field copied (range, fall-off, multipliers, reactions).</summary>
    Copied,
    /// <summary>The data's value when it has one, else the reference's (both modes).</summary>
    DataOrReference,
    /// <summary>A check, not a value: <see cref="BalanceRow.Result"/> against <see cref="BalanceRow.Reference"/> (<see cref="BalanceRow.Factor"/>: the difference as a fraction).</summary>
    Check,
}

/// <summary>
/// One stat of the Ork balance: its data value (NaN: none in the data), the Tactical reference, the rule, the factor (NaN: none) and
/// the value used in the mode it was built for. Units: as <see cref="Unit"/> (s, m, m/s, damage on the health scale, rounds).
/// </summary>
public sealed record BalanceRow(string Stat, string DataPath, float Data, float Reference, BalanceRule Rule, float Factor, float Result, string Unit = "")
{
    public string Note { get; init; } = "";
}

/// <summary>A gun's values as the Ork data hold them (raw fields the reader's fallbacks would hide; null: not in the data).</summary>
/// <param name="Spec">The weapon as <see cref="WeaponTuning.Read"/> gives it (its fallbacks where the data have none).</param>
/// <param name="RaiseDelay">The Ork rig's hip-fire raise delay for the gun's family (null: not read).</param>
public sealed record OrkGunData(WeaponSpec Spec, float? ShotCooldown, float? TimeBetweenShots, int? Clip, int? Reserve, bool ReserveInfinite, float? Reload,
    float? RaiseDelay = null);

/// <summary>The Ork blueprint's body values (<c>ork_coop_boy</c> <c>health_ext</c>, <c>biped_locomotion_ext</c>); null: not in the data.</summary>
public sealed record OrkBodyData(float? Health, float? HealthRegenDelay, float? HealthRegenRate, float? Shield, float? ShieldRegenDelay, float? ShieldRegenRate,
    float? Run, float? Strafe, float? Back, float? Sprint, float? Walk, float? AimRun, float? AimStrafe, float? AimBack);

/// <summary>Everything read for the Ork Boy (<see cref="OrkClass.From"/>); a missing part is null.</summary>
/// <param name="Choppa">The choppa's combo with its heavy strike and charge (<see cref="MeleeCombo.Rush"/>, null when not readable).</param>
/// <param name="ChoppaAnimations">The clips the choppa's light attacks play.</param>
public sealed record OrkData(OrkGunData? Shoota, OrkGunData? Slugga, MeleeCombo? Choppa, ThrownSpec? Stikk, OrkBodyData Body, EvadeSpec? Evade,
    IReadOnlyList<HitVolume>? HitVolumes)
{
    public IReadOnlyList<string> ChoppaAnimations { get; init; } = [];
    public IReadOnlyList<string> ChoppaHeavyAnimations { get; init; } = [];
}

/// <summary>The Tactical's gear and body the parity rows scale to (the class's defaults: bolter, bolt pistol, combat knife, frag grenade, roll).</summary>
public sealed record OrkReference(WeaponSpec Bolter, WeaponSpec BoltPistol, MeleeCombo Knife, ThrownSpec Frag, EvadeSpec? Evade, BodySpec Body);

/// <summary>The Ork Boy's simulation values in one mode, with the rows that explain them.</summary>
public sealed record OrkBuild(OrkBalanceMode Mode, WeaponSpec Shoota, WeaponSpec Slugga, MeleeCombo Choppa, ThrownSpec? Stikk, EvadeSpec? Evade, BodySpec Body,
    IReadOnlyList<BalanceRow> Rows);

/// <summary>
/// The Ork Boy's balance (Ork O4; design section 2): one row per stat (data, Tactical reference, rule, factor, result). Parity scales the
/// AI-tuned data to the Tactical: every movement speed the Tactical's (the owner's decision 2026-10-28); damage by sustained damage per second with cooldowns rounded up to whole ticks (both the bolter's
/// 0.133 s and the shoota's 0.11 s fire every 4th tick at 30 Hz), ammunition by total damage per life, health and shield to the
/// marine's; ranges, fall-off, multipliers and reactions copied from the reference weapon; animation timing, clip sizes, reload times
/// and the stikkbomb's flight kept (feel). Values that are ours or parity-scaled are listed in spec/behavior.md "Ork Boy".
/// </summary>
public static class OrkBalance
{
    /// <summary>Most a gun's sustained damage per second may differ from its reference's in parity before a note (±10 %; ours).</summary>
    public const float SustainedTolerance = 0.10f;

    /// <summary>Reserve of a gun whose data say "infinite" (Data mode; ours: the wire carries ammunition as u16).</summary>
    public const int Unlimited = ushort.MaxValue;

    /// <summary>A cooldown as the simulation fires it: rounded up to whole ticks, at least one (<paramref name="tickSeconds"/>).</summary>
    public static float EffectiveCooldown(float cooldown, float tickSeconds) =>
        !(tickSeconds > 0f) ? cooldown : MathF.Max(1f, MathF.Ceiling(cooldown / tickSeconds - 1e-3f)) * tickSeconds;

    /// <summary>Damage per second over whole clips, reloads included (body hits, no fall-off): clip × damage / (clip × cooldown + reload).</summary>
    public static float Sustained(WeaponSpec w, float tickSeconds)
    {
        float clip = w.ClipSize, time = clip * EffectiveCooldown(w.ShotCooldown, tickSeconds) + w.ReloadTime;
        return time > 0f ? clip * w.Damage / time : 0f;
    }

    /// <summary>Damage per second while the trigger is held (no reloads).</summary>
    public static float Burst(WeaponSpec w, float tickSeconds) => w.Damage / EffectiveCooldown(w.ShotCooldown, tickSeconds);

    /// <summary>The Ork's values in <paramref name="mode"/> (shoota, slugga and choppa must have been read).</summary>
    public static OrkBuild Apply(OrkData data, OrkReference reference, OrkBalanceMode mode, float tickSeconds)
    {
        if (data.Shoota is null || data.Slugga is null || data.Choppa is null) throw new ArgumentException("the Ork's shoota, slugga and choppa are needed");
        bool parity = mode == OrkBalanceMode.Parity;
        var rows = new List<BalanceRow>();

        // One row: the result in this mode (parity by the rule; Data: the data, else dataMissing, else the reference).
        float Row(string stat, string path, float? value, float target, BalanceRule rule, string unit = "", float? dataMissing = null, string note = "")
        {
            float d = value ?? float.NaN;
            float result;
            if (rule == BalanceRule.DataOrReference) result = value ?? target;
            else if (!parity) result = value ?? dataMissing ?? target;
            else result = rule switch
            {
                BalanceRule.Kept => value ?? dataMissing ?? target,
                _ => target,
            };
            float factor = !parity || rule == BalanceRule.Kept ? 1f
                : rule == BalanceRule.Scale && value is float v && v > 0f ? target / v
                : rule == BalanceRule.DataOrReference && value is not null ? 1f : float.NaN;
            rows.Add(new BalanceRow(stat, path, d, target, rule, factor, result, unit) { Note = note });
            return result;
        }

        // ---- body ----
        const string Same = "the Tactical's (owner's decision 2026-10-28)";
        var b = data.Body;
        var r = reference.Body;
        const string Health = "ork_coop_boy health_ext/", Shield = "ork_coop_boy health_ext/armourshield_info/", Speed = "ork_coop_boy biped_locomotion_ext/speed_standing/",
            Aim = "ork_coop_boy biped_locomotion_ext/speed_aiming/";
        var body = new BodySpec($"ork - boy ({(parity ? "parity" : "data")})",
            // The owner (2026-10-28): "the aim speed should be the same though, as well as movement speed in general (tactical marine)".
            Row("Run speed", Speed + "run_speed", b.Run, r.RunSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Strafe speed", Speed + "strafe_speed", b.Strafe, r.StrafeSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Back speed", Speed + "back_speed", b.Back, r.BackSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Sprint speed", Speed + "sprint_speed", b.Sprint, r.SprintSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Walk speed", Speed + "walk_speed", b.Walk, r.WalkSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Aim run speed", Aim + "run_speed", b.AimRun, r.AimRunSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Aim strafe speed", Aim + "strafe_speed", b.AimStrafe, r.AimStrafeSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Aim back speed", Aim + "back_speed", b.AimBack, r.AimBackSpeed, BalanceRule.Scale, "m/s", note: Same),
            Row("Health max", Health + "health_max", b.Health, r.HealthMax, BalanceRule.Scale, "health"),
            Row("Health regen delay", Health + "health_regen_delay", b.HealthRegenDelay, r.HealthRegenDelay, BalanceRule.Set, "s", dataMissing: 0f),
            Row("Health regen rate", Health + "health_regen_rate", b.HealthRegenRate, r.HealthRegenRate, BalanceRule.Set, "health/s", dataMissing: 0f, note: "none in the data: no regeneration"),
            Row("Shield max", Shield + "armourshield_max", b.Shield, r.ShieldMax, BalanceRule.Set, "shield", dataMissing: 0f, note: "none in the data"),
            Row("Shield regen delay", Shield + "armourshield_regen_delay", b.ShieldRegenDelay, r.ShieldRegenDelay, BalanceRule.Set, "s", dataMissing: 0f),
            Row("Shield regen rate", Shield + "armourshield_regen_rate", b.ShieldRegenRate, r.ShieldRegenRate, BalanceRule.Set, "shield/s", dataMissing: 0f))
        {
            HitVolumes = data.HitVolumes,
        };

        // ---- guns ----
        WeaponSpec Gun(string label, OrkGunData g, WeaponSpec refGun, WeaponSlot slot)
        {
            var s = g.Spec;
            string path = s.Name + " ";
            float cooldown = Row($"{label} cooldown", path + "basic_weapon_tuning/shot_cooldown (time_between_shots when 0)",
                g.ShotCooldown is > 0f ? g.ShotCooldown : g.TimeBetweenShots is > 0f ? g.TimeBetweenShots : null, refGun.ShotCooldown, BalanceRule.Kept, "s",
                dataMissing: s.ShotCooldown, note: $"fires every {EffectiveCooldown(g.ShotCooldown is > 0f ? g.ShotCooldown.Value : g.TimeBetweenShots ?? s.ShotCooldown, tickSeconds) / tickSeconds:F0} ticks");
            float refRate = refGun.Damage / EffectiveCooldown(refGun.ShotCooldown, tickSeconds);
            float targetDamage = refRate * EffectiveCooldown(cooldown, tickSeconds);
            // A gun without ammunition in the data (the slugga) gets the reference's damage per clip (ours: its reload copied too, the sustained rate stays close).
            int clip = (int)Row($"{label} clip", path + "ammunition_info/capacity_clip", g.Clip,
                g.Clip is null ? MathF.Max(1f, MathF.Round(refGun.ClipSize * refGun.Damage / MathF.Max(targetDamage, 1e-3f))) : refGun.ClipSize,
                g.Clip is null ? BalanceRule.Set : BalanceRule.Kept, "rounds", dataMissing: Unlimited,
                note: g.Clip is null ? $"no ammunition in the data: {refGun.Name}'s {refGun.ClipSize} × {refGun.Damage:0.##} damage per clip (unlimited in Data mode)" : "");
            float reload = Row($"{label} reload", path + "reload_info/triggered_reload_time", g.Reload, refGun.ReloadTime, g.Reload is null ? BalanceRule.Set : BalanceRule.Kept, "s",
                dataMissing: s.ReloadTime);
            float damage = Row($"{label} damage", path + "hit_sim/do_damage/damage_general", s.Damage, targetDamage, BalanceRule.Scale, "damage",
                note: $"{refGun.Name} {refGun.Damage:0.##} / {EffectiveCooldown(refGun.ShotCooldown, tickSeconds):0.####} s = {refRate:0.#} per s");
            float refTotal = (refGun.ClipSize + refGun.ReserveSize) * refGun.Damage;
            int reserve = (int)Row($"{label} reserve", path + "ammunition_info/capacity_reserve", g.ReserveInfinite ? null : g.Reserve,
                MathF.Max(0f, MathF.Round(refTotal / MathF.Max(damage, 1e-3f)) - clip), BalanceRule.Set, "rounds",
                dataMissing: g.ReserveInfinite ? Unlimited : 0f, note: $"{refGun.Name} ({refGun.ClipSize}+{refGun.ReserveSize}) × {refGun.Damage:0.##} = {refTotal:0} damage per life"
                    + (g.ReserveInfinite ? "; infinite in the data (unlimited in Data mode)" : ""));
            float range = Row($"{label} range, fall-off, multipliers", path + "delivery/max_range", s.MaxRange, refGun.MaxRange, BalanceRule.Copied, "m",
                note: $"{refGun.Name}'s fall-off {refGun.FallOffStart:0.#}-{refGun.FallOffEnd:0.#} m ×{refGun.FallOffEndMultiplier:0.##}, head ×{refGun.HeadMultiplier:0.##}");
            float raise = Row($"{label} raise delay", "ork rig hip fire", g.RaiseDelay, refGun.RaiseDelay, BalanceRule.DataOrReference, "s");
            var gun = s with
            {
                Slot = slot, ShotCooldown = cooldown, ClipSize = Math.Clamp(clip, 1, ushort.MaxValue), ReloadTime = reload, Damage = damage,
                ReserveSize = Math.Clamp(reserve, 0, ushort.MaxValue), MaxRange = range, RaiseDelay = raise,
                AlwaysReady = g.RaiseDelay is null ? refGun.AlwaysReady : s.AlwaysReady,
                Variants = [],
            };
            if (parity)
                gun = gun with
                {
                    FallOffStart = refGun.FallOffStart, FallOffEnd = refGun.FallOffEnd, FallOffEndMultiplier = refGun.FallOffEndMultiplier,
                    HeadMultiplier = refGun.HeadMultiplier, BodyMultiplier = refGun.BodyMultiplier,
                    ShieldHeadMultiplier = refGun.ShieldHeadMultiplier, ShieldBodyMultiplier = refGun.ShieldBodyMultiplier,
                };
            float mine = Sustained(gun, tickSeconds), theirs = Sustained(refGun, tickSeconds);
            rows.Add(new BalanceRow($"{label} sustained check", "clip × damage / (clip × cooldown + reload)", float.NaN, theirs, BalanceRule.Check,
                theirs > 0f ? mine / theirs - 1f : float.NaN, mine, "damage/s")
            {
                Note = !parity ? "Data mode" : theirs > 0f && MathF.Abs(mine / theirs - 1f) <= SustainedTolerance ? $"within ±{SustainedTolerance:P0}" : $"OUTSIDE ±{SustainedTolerance:P0}",
            });
            return gun;
        }
        var shoota = Gun("Shoota", data.Shoota, reference.Bolter, WeaponSlot.Ranged);
        var slugga = Gun("Slugga", data.Slugga, reference.BoltPistol, WeaponSlot.Pistol);

        // ---- choppa ----
        var choppa = data.Choppa;
        if (parity)
        {
            var knife = reference.Knife;
            for (int i = 0; i < choppa.Steps.Count && knife.Steps.Count > 0; i++)
            {
                var k = knife.Steps[Math.Min(i, knife.Steps.Count - 1)];
                Row($"Choppa light {i + 1} damage", $"{choppa.Wargear} ability_melee_light primary do_damage", choppa.Steps[i].Damage, k.Damage, BalanceRule.Scale, "damage");
                Row($"Choppa light {i + 1} secondary", $"{choppa.Wargear} ability_melee_light secondary do_damage", choppa.Steps[i].SecondaryDamage, k.SecondaryDamage,
                    choppa.Steps[i].SecondaryDamage > 0f ? BalanceRule.Scale : BalanceRule.Set, "damage");
            }
            for (int i = 0; i < choppa.Heavy.Count && knife.Heavy.Count > 0; i++)
                Row($"Choppa heavy {i + 1} damage", $"{choppa.Wargear} ability_melee_heavy primary do_damage", choppa.Heavy[i].Damage,
                    knife.Heavy[Math.Min(i, knife.Heavy.Count - 1)].Damage, BalanceRule.Scale, "damage", note: "with the knife's heavy strike's reactions (stun)");
            if (choppa.Rush is { } rush && knife.Rush is { } bull)
            {
                Row("Choppa charge damage", $"{choppa.Wargear} ability_melee_sprint primary do_damage", rush.Primary.Damage, bull.Primary.Damage, BalanceRule.Scale, "damage",
                    note: $"{bull.Ability}, with its stun");
                Row("Choppa charge shield part", $"{choppa.Wargear} ability_melee_sprint damage_armourshield", rush.Primary.ShieldDamage, bull.Primary.ShieldDamage,
                    rush.Primary.ShieldDamage > 0f ? BalanceRule.Scale : BalanceRule.Set, "shield");
            }
            choppa = ParityCombo(data.Choppa, knife) with
            {
                Variants = knife.Variants.Select(v => new UpgradeVariant<MeleeCombo>(v.Upgrades, ParityCombo(data.Choppa, v.Spec) with { Variants = [] })).ToArray(),
            };
        }
        else
        {
            for (int i = 0; i < choppa.Steps.Count; i++)
                Row($"Choppa light {i + 1} damage", $"{choppa.Wargear} ability_melee_light primary do_damage", choppa.Steps[i].Damage, reference.Knife.Steps.Count > 0 ? reference.Knife.Steps[0].Damage : float.NaN, BalanceRule.Scale, "damage");
            for (int i = 0; i < choppa.Heavy.Count; i++)
                Row($"Choppa heavy {i + 1} damage", $"{choppa.Wargear} ability_melee_heavy primary do_damage", choppa.Heavy[i].Damage, reference.Knife.Heavy.Count > 0 ? reference.Knife.Heavy[0].Damage : float.NaN, BalanceRule.Scale, "damage");
            if (choppa.Rush is { } rush)
                Row("Choppa charge damage", $"{choppa.Wargear} ability_melee_sprint primary do_damage", rush.Primary.Damage, reference.Knife.Rush?.Primary.Damage ?? float.NaN, BalanceRule.Scale, "damage");
            choppa = choppa with { Variants = [] };
        }
        if (choppa.Rush is null) rows.Add(new BalanceRow("Choppa charge", "Base_MELEE_Coop sprint attack", float.NaN, float.NaN, BalanceRule.Check, float.NaN, float.NaN)
        { Note = "not readable: no charge (F with W is the heavy strike)" });

        // ---- stikkbomb ----
        ThrownSpec? stikk = data.Stikk;
        if (stikk is not null)
        {
            var frag = reference.Frag;
            var splash = stikk.Projectile.Splash;
            Row("Stikk count", $"{stikk.Name} ammunition_info/capacity_clip", stikk.Count, frag.Count, BalanceRule.Set, "grenades");
            Row("Stikk initial", $"{stikk.Name} ammunition_info/initial_clip_amount", stikk.SpawnCount, frag.SpawnCount, BalanceRule.Set, "grenades");
            Row("Stikk cooldown", $"{stikk.Name} basic_weapon_tuning/shot_cooldown", stikk.Cooldown, frag.Cooldown, BalanceRule.Set, "s");
            Row("Stikk damage", $"{stikk.Projectile.Blueprint} area_of_effect do_damage/damage_general", splash?.Damage.Damage, frag.Projectile.Splash?.Damage.Damage ?? 0f, BalanceRule.Scale, "damage");
            Row("Stikk shield-only damage", $"{stikk.Projectile.Blueprint} do_damage/damage_armourshield", splash?.Damage.ShieldDamage, frag.Projectile.Splash?.Damage.ShieldDamage ?? 0f, BalanceRule.Scale, "shield");
            Row("Stikk radius", $"{stikk.Projectile.Blueprint} area_of_effect/shape/radius", splash?.Radius, frag.Projectile.Splash?.Radius ?? 0f, BalanceRule.Scale, "m",
                note: "fall-off bands, reactions copied from the frag");
            Row("Stikk speed, bounce, fuse", $"{stikk.Projectile.Blueprint} projectile_ext", stikk.Projectile.Speed, frag.Projectile.Speed, BalanceRule.Kept, "m/s",
                note: $"fuse {stikk.Projectile.GroundLifetime:0.##} s on the ground, {stikk.Projectile.MaxBounces} bounce(s) (open question 4)");
            if (parity)
                stikk = ParityStikk(data.Stikk!, frag) with
                {
                    Variants = frag.Variants.Select(v => new UpgradeVariant<ThrownSpec>(v.Upgrades, ParityStikk(data.Stikk!, v.Spec) with { Variants = [] })).ToArray(),
                };
            else stikk = stikk with { Variants = [] };
        }

        // ---- dodge hop ----
        var evade = data.Evade;
        if (evade is not null)
        {
            var tac = reference.Evade;
            if (evade.HyperArmourTime <= 0f && tac is not null)
                evade = evade with { TargetableModeTime = tac.TargetableModeTime, CollisionModeTime = tac.CollisionModeTime };
            Row("Hop left length", $"{evade.Left.Animation} (BRANCH_Evade_End)", evade.Left.Duration, tac?.Left.Duration ?? evade.Left.Duration, BalanceRule.Scale, "s",
                note: "the clip time-scaled to the Tactical roll's; branch times the roll's");
            Row("Hop right length", $"{evade.Right.Animation} (BRANCH_Evade_End)", evade.Right.Duration, tac?.Right.Duration ?? evade.Right.Duration, BalanceRule.Scale, "s");
            Row("Hop spacing", $"{evade.Name} special_nav_ability/min_time_between_activations", evade.MinInterval, tac?.MinInterval ?? evade.MinInterval, BalanceRule.Scale, "s");
            Row("Hop hyper armour", "the clip's TargetableMode / CollisionMode", data.Evade!.HyperArmourTime > 0f ? data.Evade.HyperArmourTime : null,
                tac?.HyperArmourTime ?? 0f, BalanceRule.DataOrReference, "s", note: "the Tactical's when the clip has none (ours)");
            if (parity && tac is not null)
            {
                var left = Retimed(evade.Left, tac.Left);
                var right = Retimed(evade.Right, tac.Right);
                evade = evade with { Left = left, Right = right, Forward = right, Back = left, MinInterval = tac.MinInterval };
            }
        }
        return new OrkBuild(mode, shoota, slugga, choppa, stikk, evade, body, rows);
    }

    /// <summary>The choppa's combo at parity with a reference combo (the knife, or one of its perk variants): each step's damage, secondary damage, multipliers, reactions, heals and type from the reference step at the same place (the last one past its end); timing from the data. The charge likewise with the bull rush's hits, reach and spacing.</summary>
    public static MeleeCombo ParityCombo(MeleeCombo data, MeleeCombo reference)
    {
        static IReadOnlyList<MeleeStep> Steps(IReadOnlyList<MeleeStep> mine, IReadOnlyList<MeleeStep> theirs) => theirs.Count == 0 ? mine
            : mine.Select((m, i) => theirs[Math.Min(i, theirs.Count - 1)] is var k
                ? m with
                {
                    Damage = k.Damage, SecondaryDamage = k.SecondaryDamage, HealthMultiplier = k.HealthMultiplier, ShieldMultiplier = k.ShieldMultiplier,
                    PrimaryReaction = k.PrimaryReaction, SecondaryReaction = k.SecondaryReaction, Heal = k.Heal, SecondaryHeal = k.SecondaryHeal, DamageType = k.DamageType,
                    Stomp = k.Stomp,
                }
                : m).ToArray();
        RushSpec? rush = data.Rush is { } r && reference.Rush is { } b
            ? r with
            {
                Primary = b.Primary, Secondary = b.Secondary, PrimaryReaction = b.PrimaryReaction, SecondaryReaction = b.SecondaryReaction, Range = b.Range,
                MinInterval = b.MinInterval, Ability = b.Ability,
            }
            : data.Rush;
        return data with { Steps = Steps(data.Steps, reference.Steps), Heavy = Steps(data.Heavy, reference.Heavy), Rush = rush };
    }

    /// <summary>The stikkbomb at parity with a frag grenade (or a perk variant of it): its count, spawn count, cooldown and blasts; the stikk's flight (speed, bounces, fuse) kept.</summary>
    public static ThrownSpec ParityStikk(ThrownSpec stikk, ThrownSpec frag) => stikk with
    {
        Count = frag.Count, InitialCount = frag.InitialCount, Cooldown = frag.Cooldown,
        Projectile = stikk.Projectile with { Splash = frag.Projectile.Splash, ExtraSplashes = frag.Projectile.ExtraSplashes, DetonateDamage = frag.Projectile.DetonateDamage },
    };

    /// <summary>A side hop retimed to a Tactical roll (parity, ours): the roll's length and branch times; the clip's path and height over it.</summary>
    public static EvadeMove Retimed(EvadeMove hop, EvadeMove roll) => hop with
    {
        Duration = roll.Duration, MoveAt = roll.MoveAt, RangedAt = roll.RangedAt, SwitchAt = roll.SwitchAt, MeleeAt = roll.MeleeAt, NextAt = roll.NextAt,
    };
}
