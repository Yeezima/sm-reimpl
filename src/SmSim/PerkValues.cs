using System.Globalization;

namespace SmSim;

/// <summary>
/// perks/finish (owner's request 2026-10-29: "include accurate values to perk descriptions (if those can be determined). damage values, healing
/// values, % improvements of equipment, %spread reduction, etc."): the values line under a perk's locale description in the class creator,
/// generated from the very specs the simulation picks for a player with the perk (each item's <see cref="UpgradeVariant{T}"/> against its base,
/// the player-blueprint <see cref="PlayerUpgrade"/> against the config's body), so the text can never drift from the rules. Rules:
/// <list type="bullet">
/// <item>Only values the simulation uses are shown (e.g. the lascannon's unused <c>max_charge_time</c> is not); a perk with nothing to compare shows
/// no values line; an inert perk says <see cref="NotActive"/>.</item>
/// <item>Items: every weapon, thrown item, jump pack and melee combo a class that may take the perk carries, compared with its variant for exactly
/// the perk's upgrade string. Chaos and loyalist twins share neutral names (<see cref="ItemName"/>, the locale's challenge group names), so equal
/// lines appear once; items whose phrases are equal share one line ("Chainsword, Power Sword: …").</item>
/// <item>Numbers as stored (at most three decimals); percentages relative to the base, rounded; "→" between base and perk value.</item>
/// </list>
/// Engine-free; tested in <c>PerkValuesTests</c>.
/// </summary>
public static class PerkValues
{
    /// <summary>The values line of a perk that does nothing in the simulation.</summary>
    public const string NotActive = "Not active yet.";

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The perk's values, one line per item (or group of items with equal changes), in a stable order: player-blueprint changes first, then
    /// weapons, thrown items, the jump pack and melee. Empty when the perk is inert or nothing comparable was read.
    /// </summary>
    public static IReadOnlyList<string> Lines(SimConfig c, PerkSpec perk)
    {
        if (Perks.StatusOf(c, perk).Activity == PerkActivity.Inert) return [];
        int index = IndexOf(c, perk);
        var classes = Enumerable.Range(0, c.Classes.Count).Where(k => index < 0 || Loadouts.ValidPerk(c, k, index)).ToList();
        var groups = new List<(string Label, List<string> Phrases)>();
        void Add(string label, List<string> phrases)
        {
            if (phrases.Count == 0) return;
            if (groups.Any(g => g.Label == label && g.Phrases.SequenceEqual(phrases))) return;
            groups.Add((label, phrases));
        }

        // True Grit: its rule is the pistol choice; the values are the pistol-slot bolter's own.
        if (string.Equals(perk.Name, Loadouts.TrueGritPerk, StringComparison.OrdinalIgnoreCase))
            foreach (int k in classes)
                foreach (int i in Loadouts.Choices(c, k, WeaponSlot.Pistol))
                    if (i >= 0 && i < c.Weapons.Count && Loadouts.IsPistolBolter(c.Weapons[i]))
                        Add(ItemName(c.Weapons[i].Name), [$"offered, {N(c.Weapons[i].Damage)} damage per hit, clip {c.Weapons[i].ClipSize}"]);

        if (perk.UpgradeString is not { Length: > 0 } upgrade) return Join(groups);

        foreach (var u in c.PlayerUpgrades)
            if (string.Equals(u.Upgrade, upgrade, StringComparison.OrdinalIgnoreCase)) Add("", Player(c, u));

        var weapons = new List<int>();
        foreach (int k in classes)
            foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol })
                foreach (int i in Loadouts.Choices(c, k, slot)) if (i >= 0 && i < c.Weapons.Count && !weapons.Contains(i)) weapons.Add(i);
        foreach (int i in weapons)
            if (Single(c.Weapons[i].Variants, upgrade) is { } v) Add(ItemName(c.Weapons[i].Name), Weapon(c.Weapons[i], v));

        var thrown = new List<int>();
        foreach (int k in classes) foreach (int i in Loadouts.EquipmentChoices(c, k)) if (i >= 0 && i < c.Thrown.Count && !thrown.Contains(i)) thrown.Add(i);
        foreach (int i in thrown)
            if (Single(c.Thrown[i].Variants, upgrade) is { } v) Add(ItemName(c.Thrown[i].Name), Thrown(c.Thrown[i], v));

        var packs = new List<JumpPackSpec>();
        foreach (int k in classes) if (Loadouts.JumpPack(c, k) is { } p && !packs.Contains(p)) packs.Add(p);
        foreach (var p in packs)
            if (Single(p.Variants, upgrade) is { } v) Add(ItemName(p.Name), Pack(p, v));

        var combos = new List<MeleeCombo>();
        foreach (int k in classes) foreach (var m in c.Classes[k].MeleeChoices) if (!combos.Contains(m)) combos.Add(m);
        foreach (var m in combos)
            if (Single(m.Variants, upgrade) is { } v) Add(ItemName(m.Wargear), Melee(m, v));

        return Join(groups);
    }

    /// <summary>The values text of a perk: its <see cref="Lines"/> joined by new lines, <see cref="NotActive"/> when inert, "" when none was determined.</summary>
    public static string Text(SimConfig c, PerkSpec perk) =>
        Perks.StatusOf(c, perk).Activity == PerkActivity.Inert ? NotActive : string.Join("\n", Lines(c, perk));

    /// <summary>The class creator's description: the locale's text, then the values under it (either may be empty).</summary>
    public static string Describe(string localeText, string values) =>
        values.Length == 0 ? localeText : localeText.Length == 0 ? values : localeText.TrimEnd() + "\n" + values;

    // ---- items ----

    /// <summary>
    /// The neutral English name of an item by its wargear file name (both races' twins share it: the locale's challenge group names, e.g.
    /// 111736 "Storm Bolter / Twin Bolter"); the file name when unknown.
    /// </summary>
    public static string ItemName(string wargear)
    {
        string n = wargear.ToLowerInvariant();
        foreach (var prefix in new[] { "csm_", "sm_" }) if (n.StartsWith(prefix, StringComparison.Ordinal)) { n = n[prefix.Length..]; break; }
        if (n.EndsWith("_gold", StringComparison.Ordinal)) n = n[..^5];
        return n switch
        {
            "mp_pistol_bolt_pistol" or "mp_pistol_bolt_pistol_devastator" => "Bolt Pistol",
            "mp_pistol_plasma_pistol" => "Plasma Pistol",
            "mp_pistol_bolter" => "Bolter (pistol slot)",
            "mp_ranged_bolter" => "Bolter",
            "mp_ranged_storm_bolter" => "Storm Bolter / Twin Bolter",
            "mp_ranged_sniper_rifle" => "Stalker Bolter / Daemon Eye Bolter",
            "mp_ranged_grenade_launcher" => "Vengeance Launcher",
            "mp_ranged_plasma_gun" => "Plasma Gun",
            "mp_ranged_melta" => "Melta Gun",
            "mp_exotic_heavy_bolter" => "Heavy Bolter",
            "mp_exotic_plasma_cannon" => "Plasma Cannon",
            "mp_exotic_las_cannon" => "Lascannon",
            "mp_melee_combat_knife" => "Combat Knife",
            "mp_melee_chainsword" => "Chainsword",
            "mp_melee_power_axe" or "mp_melee_chain_axe" => "Power Axe / Chain Axe",
            "mp_melee_powersword" => "Power Sword",
            "mp_melee_2hand_thunder_hammer" or "mp_melee_2hand_power_mace" => "Thunder Hammer / Daemon Maul",
            "mp_core_abil_heavy_evade" => "Kick and stomp",
            "mp_abil_jumppack" => "Jump Pack",
            "mp_eq_frag_grenade" => "Frag Grenades",
            "mp_eq_krak_grenade" => "Blind Grenades",
            "mp_eq_stimpack" => "Combat Stims",
            _ => wargear,
        };
    }

    static List<string> Player(SimConfig c, PlayerUpgrade u)
    {
        var p = new List<string>();
        void Taken(string what, float m) { if (m != 1f) p.Add($"{what} damage taken {Pct(1f, m)} (x{N(m)})"); }
        Taken("ranged", u.ReceivedRanged); Taken("melee", u.ReceivedMelee); Taken("explosive", u.ReceivedExplosive);
        float shieldMax = u.Shield?.Max ?? c.ShieldMax, shieldDelay = u.Shield?.RegenDelay ?? c.ShieldRegenDelay, shieldRate = u.Shield?.RegenRate ?? c.ShieldRegenRate;
        if (shieldMax != c.ShieldMax) p.Add($"armour {Change(c.ShieldMax, shieldMax)}");
        if (u.HealthRegenRate != 0f) p.Add($"health regeneration {Change(c.HealthRegenRate, MathF.Max(0f, c.HealthRegenRate + u.HealthRegenRate), "/s")}");
        if (u.HealthRegenDelay != 0f) p.Add($"health regeneration starts after {Change(c.HealthRegenDelay, MathF.Max(0f, c.HealthRegenDelay + u.HealthRegenDelay), " s")}");
        if (u.ShieldRegenRate != 0f || shieldRate != c.ShieldRegenRate) p.Add($"armour regeneration {Change(c.ShieldRegenRate, MathF.Max(0f, shieldRate + u.ShieldRegenRate), "/s")}");
        if (u.ShieldRegenDelay != 0f || shieldDelay != c.ShieldRegenDelay)
            p.Add($"armour regeneration starts after {Change(c.ShieldRegenDelay, MathF.Max(0f, shieldDelay + u.ShieldRegenDelay), " s")}");
        foreach (var b in u.DeathBlasts.Where(Damaging)) p.Add($"{N(u.DeathBlastDelay)} s after death: {Blast(b)}");
        foreach (var b in u.ShieldBreakBlasts.Where(Damaging)) p.Add($"when your armour breaks: {Blast(b)}");
        return p;
    }

    static List<string> Weapon(WeaponSpec b, WeaponSpec v)
    {
        var p = new List<string>();
        if (v.Damage != b.Damage) p.Add($"damage {Change(b.Damage, v.Damage)} per hit");
        if (v.ShieldBodyMultiplier != b.ShieldBodyMultiplier) p.Add($"damage to armour x{N(b.ShieldBodyMultiplier)} → x{N(v.ShieldBodyMultiplier)} ({Pct(b.ShieldBodyMultiplier, v.ShieldBodyMultiplier)})");
        if (v.Bursts && !b.Bursts) p.Add($"fires {v.BurstLength}-round bursts, {N(v.BurstInterval)} s apart");
        if (v.ShotCooldown != b.ShotCooldown) p.Add($"{(v.Bursts ? "time between trigger pulls" : "time between shots")} {Change(b.ShotCooldown, v.ShotCooldown, " s")}");
        if (v.Pierce is { Points: > 0f } pierce && b.Pierce is null)
            p.Add($"passes through up to {N(pierce.Points)} {(pierce.BodyCost == 1f && pierce.PropCost == 1f ? "bodies or crates" : "targets")}");
        if (v.ClipSize != b.ClipSize) p.Add($"{(b.Heat is not null ? "ammunition" : "clip")} {Change(b.ClipSize, v.ClipSize)}");
        if (v.ReserveSize != b.ReserveSize) p.Add($"reserve {Change(b.ReserveSize, v.ReserveSize)}");
        if (v.Recharge is { } vr && b.Recharge is { } br && vr.RoundTime != br.RoundTime) p.Add($"a round recharges every {Change(br.RoundTime, vr.RoundTime, " s")}");
        else if (v.ReloadTime != b.ReloadTime) p.Add($"reload {Change(b.ReloadTime, v.ReloadTime, " s")}");
        if (v.Heat is { } vh && b.Heat is { } bh && vh.PerShot != bh.PerShot) p.Add($"heat per shot {Change(bh.PerShot, vh.PerShot)}");
        if (v.Brace is { } vb && b.Brace is { } bb)
        {
            if (vb.HeatPerShot != bb.HeatPerShot) p.Add($"braced heat per shot {Change(bb.HeatPerShot, vb.HeatPerShot)}");
            if (vb.EnterTime != bb.EnterTime) p.Add($"bracing {Change(bb.EnterTime, vb.EnterTime, " s")}");
            if (vb.ExitTime != bb.ExitTime) p.Add($"leaving the brace {Change(bb.ExitTime, vb.ExitTime, " s")}");
        }
        if (v.Vent is { } vv && b.Vent is { } bv && VentRate(vv) != VentRate(bv)) p.Add($"venting {Change(VentRate(bv), VentRate(vv), " heat/s")}");
        if (v.Charge is { } vc && b.Charge is { } bc && vc.MinTimeMultiplier != bc.MinTimeMultiplier)
        {
            float stage = ChargedAt(bc);
            p.Add(stage > 0f ? $"charged shot ready after {Change(stage * bc.MinTimeMultiplier, stage * vc.MinTimeMultiplier, " s")}"
                : $"charge time x{N(bc.MinTimeMultiplier)} → x{N(vc.MinTimeMultiplier)} ({Pct(bc.MinTimeMultiplier, vc.MinTimeMultiplier)})");
        }
        if (v.Projectile is { } vp && b.Projectile is { } bp && vp.MaxActive != bp.MaxActive) p.Add($"charges out at once {Change(bp.MaxActive, vp.MaxActive)}");
        p.AddRange(SpreadPhrases(b.Spread, v.Spread));
        return p;
    }

    static float VentRate(VentSpec v) => v.Rates.Count == 0 ? 0f : v.Rates.Max(r => r.Value);

    /// <summary>The hold time of the first charge stage that fires a shot (before the multiplier), 0 when none.</summary>
    static float ChargedAt(ChargeSpec c) => c.Stages.FirstOrDefault(s => s.Shot is not null && s.MinTime > 0f)?.MinTime ?? 0f;

    static IEnumerable<string> SpreadPhrases(SpreadSpec? b, SpreadSpec? v)
    {
        if (b is null || v is null) yield break;
        foreach (var (stance, word) in new[] { (SpreadStance.Standing, "hip"), (SpreadStance.Aiming, "aimed") })
        {
            var bs = b.Basic.SetFor(stance, SpreadMovement.Idle);
            var vs = v.Basic.SetFor(stance, SpreadMovement.Idle);
            if (bs is null || vs is null) continue;
            var (x, y) = (bs.Scatter, vs.Scatter);
            if (y.InitialHorizontal != x.InitialHorizontal) yield return $"{word} spread {Change(x.InitialHorizontal, y.InitialHorizontal, "°")}";
            if (y.IncreasePerShot != x.IncreasePerShot) yield return $"{word} spread growth per shot {Change(x.IncreasePerShot, y.IncreasePerShot, "°")}";
            if (y.MaxHorizontal != x.MaxHorizontal) yield return $"{word} maximum spread {Change(x.MaxHorizontal, y.MaxHorizontal, "°")}";
            if (y.DecreasePerSecond != x.DecreasePerSecond) yield return $"{word} spread recovery {Change(x.DecreasePerSecond, y.DecreasePerSecond, "°/s")}";
            var (h, g) = (bs.Handling, vs.Handling);
            if (g.TemporaryClimbPerShot != h.TemporaryClimbPerShot) yield return $"{word} recoil per shot {Change(h.TemporaryClimbPerShot, g.TemporaryClimbPerShot, "°")}";
            if (g.MaxTemporarySwayPerShot != h.MaxTemporarySwayPerShot) yield return $"{word} sway per shot {Change(h.MaxTemporarySwayPerShot, g.MaxTemporarySwayPerShot, "°")}";
        }
    }

    static List<string> Thrown(ThrownSpec b, ThrownSpec v)
    {
        var p = new List<string>();
        if (v.Count != b.Count) p.Add($"carried {Change(b.Count, v.Count)}");
        if (v.SpawnCount != b.SpawnCount && v.SpawnCount != v.Count) p.Add($"at spawn {Change(b.SpawnCount, v.SpawnCount)}");
        if (b.Stim is { } bs && v.Stim is { } vs)
        {
            if (vs.Lifetime != bs.Lifetime) p.Add($"lasts {Change(bs.Lifetime, vs.Lifetime, " s")}");
            void Taken(string what, float x, float y) { if (x != y) p.Add($"{what} damage to health x{N(x)} → x{N(y)}"); }
            Taken("ranged", bs.ReceivedRanged, vs.ReceivedRanged); Taken("melee", bs.ReceivedMelee, vs.ReceivedMelee); Taken("explosive", bs.ReceivedExplosive, vs.ReceivedExplosive);
            if (vs.MeleeDamage != bs.MeleeDamage) p.Add($"melee damage dealt x{N(bs.MeleeDamage)} → x{N(vs.MeleeDamage)}");
        }
        var bb = b.Projectile.AllSplashes.ToList();
        var vb = v.Projectile.AllSplashes.ToList();
        for (int i = 0; i < Math.Min(bb.Count, vb.Count); i++)
        {
            var (x, y) = (bb[i], vb[i]);
            string which = i == 0 ? "blast" : "outer blast";
            if (y.BlindOnly || x.BlindOnly)
            {
                if (y.Blindness != x.Blindness) p.Add($"{(i == 0 ? "blinds" : "outer blind")} {Change(x.Blindness, y.Blindness, " s")}");
            }
            else
            {
                if (y.Damage.Damage != x.Damage.Damage) p.Add($"{which} damage {Change(x.Damage.Damage, y.Damage.Damage)}");
                if (y.Damage.ShieldDamage != x.Damage.ShieldDamage) p.Add($"{which} armour damage {Change(x.Damage.ShieldDamage, y.Damage.ShieldDamage)}");
            }
            if (y.Radius != x.Radius) p.Add($"{which} radius {Change(x.Radius, y.Radius, " m")}");
            p.AddRange(ReactionChanges(which, x.Reactions, y.Reactions));
        }
        return p;
    }

    static List<string> Pack(JumpPackSpec b, JumpPackSpec v)
    {
        var p = new List<string>();
        if (v.RechargeRate != b.RechargeRate)
            p.Add($"power recharge {Change(b.RechargeRate, v.RechargeRate, "/s")}" + (b.MaxPower > 0f && b.RechargeRate > 0f && v.RechargeRate > 0f
                ? $", empty to full in {N(b.MaxPower / b.RechargeRate)} → {N(v.MaxPower / v.RechargeRate)} s" : ""));
        if (v.PoundDelay != b.PoundDelay) p.Add($"ground pound from {Change(b.PoundDelay, v.PoundDelay, " s")} after take-off");
        if (v.TakeOffBlasts.Count > 0 && b.TakeOffBlasts.Count == 0) foreach (var t in v.TakeOffBlasts.Where(Damaging)) p.Add($"take-off {Blast(t)}");
        if (v.Pound is { } vp && b.Pound is { } bp)
        {
            if (vp.Damage.Damage != bp.Damage.Damage) p.Add($"ground pound damage {Change(bp.Damage.Damage, vp.Damage.Damage)}");
            if (vp.Damage.ShieldDamage != bp.Damage.ShieldDamage) p.Add($"ground pound armour damage {Change(bp.Damage.ShieldDamage, vp.Damage.ShieldDamage)}");
            if (vp.Radius != bp.Radius) p.Add($"ground pound radius {Change(bp.Radius, vp.Radius, " m")}");
        }
        if (v.DashAttack is { } vd && b.DashAttack is { } bd) p.AddRange(RushPhrases("dash attack", bd.Charge, vd.Charge));
        if (v.Slash is { } vs && b.Slash is { } bs)
        {
            if (vs.Damage.Damage != bs.Damage.Damage) p.Add($"air slash damage {Change(bs.Damage.Damage, vs.Damage.Damage)}");
            if (vs.Heal != bs.Heal) p.Add(bs.Heal == 0f ? $"air slash heals {N(vs.Heal)} health per hit" : $"air slash heal {Change(bs.Heal, vs.Heal)}");
        }
        return p;
    }

    static List<string> Melee(MeleeCombo b, MeleeCombo v)
    {
        var p = new List<string>();
        p.AddRange(StepPhrases("attacks", b.Steps, v.Steps));
        p.AddRange(StepPhrases("heavy strikes", b.Heavy, v.Heavy));
        if (v.Rush is { } vr && b.Rush is { } br) p.AddRange(RushPhrases("charge", br, vr));
        return p;
    }

    static IEnumerable<string> StepPhrases(string what, IReadOnlyList<MeleeStep> b, IReadOnlyList<MeleeStep> v)
    {
        int n = Math.Min(b.Count, v.Count);
        if (n == 0) yield break;
        var idx = Enumerable.Range(0, n).ToList();
        // Damage and the reaction of the strikes that hit (the stomp blasts are listed on their own).
        var hits = idx.Where(i => b[i].Stomp is null || b[i].Damage > 0f).ToList();
        string List(Func<MeleeStep, float> f, IReadOnlyList<MeleeStep> s, List<int> at) => string.Join("/", at.Select(i => N(f(s[i]))).Distinct());
        var damaged = hits.Where(i => b[i].Damage != v[i].Damage).ToList();
        if (damaged.Count > 0)
        {
            var bv = damaged.Select(i => b[i].Damage).Distinct().ToList();
            var vv = damaged.Select(i => v[i].Damage).Distinct().ToList();
            yield return bv.Count == 1 && vv.Count == 1 ? $"{what} damage {Change(bv[0], vv[0])}" : $"{what} damage {List(s => s.Damage, b, damaged)} → {List(s => s.Damage, v, damaged)}";
        }
        var shield = hits.Where(i => b[i].ShieldDamage != v[i].ShieldDamage).ToList();
        if (shield.Count > 0) yield return $"{what} armour damage {List(s => s.ShieldDamage, b, shield)} → {List(s => s.ShieldDamage, v, shield)}";
        var second = hits.Where(i => b[i].SecondaryDamage != v[i].SecondaryDamage).ToList();
        if (second.Count > 0) yield return $"other targets damage {List(s => s.SecondaryDamage, b, second)} → {List(s => s.SecondaryDamage, v, second)}";
        var react = hits.Where(i => b[i].PrimaryReaction.Kind != v[i].PrimaryReaction.Kind).ToList();
        if (react.Count > 0)
            yield return $"{what}: {string.Join("/", react.Select(i => Word(b[i].PrimaryReaction.Kind)).Distinct())} → {string.Join("/", react.Select(i => Word(v[i].PrimaryReaction.Kind)).Distinct())}";
        var healed = idx.Where(i => b[i].Heal != v[i].Heal).ToList();
        if (healed.Count > 0 && healed.All(i => b[i].Heal == 0f))
            yield return $"heals {List(s => s.Heal, v, healed)} health per hit while hurt";
        var timed = idx.Where(i => b[i].Duration != v[i].Duration).ToList();
        foreach (int i in timed)
            yield return $"{(b[i].Stomp is not null ? "stomp" : what)} {Change(b[i].Duration, v[i].Duration, " s")}" + (b[i].HitTime != v[i].HitTime ? $", hit at {N(b[i].HitTime)} → {N(v[i].HitTime)} s" : "");
        // Stomps: equal changes once.
        var stomps = new List<string>();
        int blasts = idx.Count(i => b[i].Stomp is not null);
        foreach (int i in idx)
        {
            if (b[i].Stomp is not { } x || v[i].Stomp is not { } y) continue;
            string name = blasts == 1 ? "stomp" : $"{what.TrimEnd('s')} {i + 1} blast";
            var parts = new List<string>();
            if (y.Damage.Damage != x.Damage.Damage || y.Damage.ShieldDamage != x.Damage.ShieldDamage)
                parts.Add($"damage {N(x.Damage.Damage)} + {N(x.Damage.ShieldDamage)} armour → {N(y.Damage.Damage)} + {N(y.Damage.ShieldDamage)}");
            if (y.Radius != x.Radius) parts.Add($"radius {Change(x.Radius, y.Radius, " m")}");
            parts.AddRange(ReactionChanges("", x.Reactions, y.Reactions));
            if (parts.Count > 0 && $"{name} " + string.Join(", ", parts) is var line && !stomps.Contains(line)) stomps.Add(line);
        }
        foreach (var s in stomps) yield return s;
    }

    static IEnumerable<string> RushPhrases(string what, RushSpec b, RushSpec v)
    {
        if (v.Primary.Damage != b.Primary.Damage || v.Primary.ShieldDamage != b.Primary.ShieldDamage)
            yield return $"{what} damage {N(b.Primary.Damage)} + {N(b.Primary.ShieldDamage)} armour → {N(v.Primary.Damage)} + {N(v.Primary.ShieldDamage)} ({Pct(b.Primary.Damage + b.Primary.ShieldDamage, v.Primary.Damage + v.Primary.ShieldDamage)})";
        if (v.PrimaryReaction.Kind != b.PrimaryReaction.Kind) yield return $"{what}: {Word(b.PrimaryReaction.Kind)} → {Word(v.PrimaryReaction.Kind)}";
    }

    /// <summary>
    /// A blast's reaction entries that change kind, paired by position (a target takes the entry of the highest intensity it reaches,
    /// <see cref="Reaction.Pick"/>: the highest entry is the centre's): "close hits stagger → stun"; an entry the variant adds: "close hits stun".
    /// </summary>
    static IEnumerable<string> ReactionChanges(string which, IReadOnlyList<ReactionEntry> b, IReadOnlyList<ReactionEntry> v)
    {
        string prefix = which.Length > 0 ? which + ": " : "";
        // Where an entry applies: "" (the only entry), "close" (the highest intensity), "outer" (the lowest) or "middle".
        static string Where(IReadOnlyList<ReactionEntry> list, int i) =>
            list.Count == 1 ? "" : list[i].MinIntensity >= list.Max(e => e.MinIntensity) ? "close"
            : list[i].MinIntensity <= list.Min(e => e.MinIntensity) ? "outer" : "middle";
        static string Hits(IEnumerable<string> places) => string.Join("/", places.Where(p => p.Length > 0)) is { Length: > 0 } w ? w + " hits" : "hits";
        var changed = new List<(string Where, string Change)>();
        for (int i = 0; i < Math.Min(b.Count, v.Count); i++)
            if (b[i].Kind != v[i].Kind) changed.Add((Where(v, i), $"{Word(b[i].Kind)} → {Word(v[i].Kind)}"));
        for (int i = b.Count; i < v.Count; i++)
            if (v[i].Kind != ReactionKind.None) changed.Add((Where(v, i), $"now {Word(v[i].Kind)}"));
        // Equal changes at several places once: "close/middle hits stun → stagger".
        foreach (var g in changed.GroupBy(x => x.Change)) yield return $"{prefix}{Hits(g.Select(x => x.Where))} {g.Key}";
    }

    /// <summary>A reaction's name as the class creator says it.</summary>
    static string Word(ReactionKind k) => k switch
    {
        ReactionKind.Interrupt => "interrupt", ReactionKind.Stun => "stun", ReactionKind.Stagger => "stagger", _ => "no reaction",
    };

    static bool Damaging(SplashSpec s) => !s.BlindOnly && (s.Damage.Damage > 0f || s.Damage.ShieldDamage > 0f);

    static string Blast(SplashSpec s)
    {
        var kinds = s.Reactions.Where(r => r.Kind != ReactionKind.None).Select(r => Word(r.Kind)).Distinct().ToList();
        return $"blast of {N(s.Damage.Damage)} + {N(s.Damage.ShieldDamage)} armour damage within {N(s.Radius)} m" + (kinds.Count > 0 ? $" ({string.Join("/", kinds)})" : "");
    }

    // ---- helpers ----

    static int IndexOf(SimConfig c, PerkSpec perk)
    {
        for (int i = 0; i < c.Perks.Count; i++) if (ReferenceEquals(c.Perks[i], perk) || c.Perks[i].Name.Equals(perk.Name, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The variant read for exactly <paramref name="upgrade"/> (a single upgrade string), or null.</summary>
    static T? Single<T>(IReadOnlyList<UpgradeVariant<T>> variants, string upgrade) where T : class =>
        variants.FirstOrDefault(v => v.Upgrades.Count == 1 && string.Equals(v.Upgrades[0], upgrade, StringComparison.OrdinalIgnoreCase))?.Spec;

    static IReadOnlyList<string> Join(List<(string Label, List<string> Phrases)> groups)
    {
        // Items whose phrases are equal share a line ("Chainsword, Power Sword: charge …"); the player's own line has no label.
        var merged = new List<(List<string> Labels, string Text)>();
        foreach (var (label, phrases) in groups)
        {
            string text = string.Join(", ", phrases);
            int at = merged.FindIndex(m => m.Text == text);
            if (at >= 0) { if (label.Length > 0 && !merged[at].Labels.Contains(label)) merged[at].Labels.Add(label); }
            else merged.Add((label.Length > 0 ? [label] : [], text));
        }
        return merged.Select(m => m.Labels.Count == 0 ? Capital(m.Text) : $"{string.Join(", ", m.Labels)}: {m.Text}").ToList();
    }

    static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A number as stored, at most three decimals, no trailing zeros.</summary>
    public static string N(float v) => Math.Round(v, 3).ToString("0.###", Inv);

    /// <summary>"base → value (±p%)", with <paramref name="unit"/> after both numbers; no percentage from a base of 0.</summary>
    public static string Change(float from, float to, string unit = "") => $"{N(from)}{unit} → {N(to)}{unit}" + (from != 0f ? $" ({Pct(from, to)})" : "");

    /// <summary>The relative change from <paramref name="from"/> to <paramref name="to"/> as a signed whole percentage ("+60%", "−30%").</summary>
    public static string Pct(float from, float to)
    {
        double p = Math.Round((to - (double)from) / from * 100.0);
        return p > 0 ? $"+{p.ToString("0", Inv)}%" : p < 0 ? $"−{(-p).ToString("0", Inv)}%" : "±0%";
    }
}
