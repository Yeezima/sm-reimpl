namespace SmSim;

/// <summary>
/// The upgrade strings a player's perks grant (M43): at most two, one per perk slot (<see cref="Loadouts.Upgrades"/>). A perk
/// applies its upgrade string to its owner (<c>apply_upgrade_string</c>); every tuning entry that requires the string then replaces
/// the default entry ("base entry plus entry that needs the upgrade"; docs/customizer-and-perks-data.md section 2).
/// </summary>
public readonly record struct PerkUpgrades(string? First, string? Second)
{
    /// <summary>No upgrade string (no perk, or perks without one).</summary>
    public bool IsEmpty => First is null && Second is null;

    /// <summary>Whether <paramref name="upgrade"/> is granted (ordinal, ignoring case like the rest of the name lookups).</summary>
    public bool Has(string upgrade) =>
        (First is not null && string.Equals(First, upgrade, StringComparison.OrdinalIgnoreCase))
        || (Second is not null && string.Equals(Second, upgrade, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether every string of <paramref name="required"/> is granted (an empty list: always).</summary>
    public bool Grants(IReadOnlyList<string> required)
    {
        for (int i = 0; i < required.Count; i++) if (!Has(required[i])) return false;
        return true;
    }
}

/// <summary>
/// A spec as it is with the upgrade strings <paramref name="Upgrades"/> granted (M43): read from the same file with the entries
/// that require them chosen over the defaults (<c>SmContent</c>'s readers). Only variants that differ from the base spec are kept.
/// </summary>
public sealed record UpgradeVariant<T>(IReadOnlyList<string> Upgrades, T Spec) where T : class;

/// <summary>Picking and hashing perk variants (M43).</summary>
public static class Variants
{
    /// <summary>Most upgrade strings one variant may need: a player has two perk slots.</summary>
    public const int MaxUpgrades = 2;

    /// <summary>
    /// The spec a player with <paramref name="granted"/> uses: the variant whose upgrades are all granted and which needs the
    /// most of them (ties: the first in the list), else <paramref name="spec"/> itself. Without variants or upgrades: the spec.
    /// The same rule as the readers' entry choice ("the entry whose required upgrades are all granted, preferring the one that
    /// requires the most"), so a player with two perks gets the pair's variant when one was read.
    /// </summary>
    public static T Pick<T>(T spec, IReadOnlyList<UpgradeVariant<T>> variants, PerkUpgrades granted) where T : class
    {
        if (variants.Count == 0 || granted.IsEmpty) return spec;
        UpgradeVariant<T>? best = null;
        foreach (var v in variants)
            if (v.Upgrades.Count > 0 && granted.Grants(v.Upgrades) && (best is null || v.Upgrades.Count > best.Upgrades.Count)) best = v;
        return best?.Spec ?? spec;
    }

    /// <summary>Writes a variant list for a config hash: count, then each variant's upgrades and spec.</summary>
    internal static void Write<T>(BinaryWriter w, IReadOnlyList<UpgradeVariant<T>> variants, Action<BinaryWriter, T> write) where T : class
    {
        w.Write(variants.Count);
        foreach (var v in variants)
        {
            w.Write(v.Upgrades.Count); foreach (var u in v.Upgrades) w.Write(u);
            write(w, v.Spec);
        }
    }

    /// <summary>The bytes <see cref="SimConfig.Hash"/> writes for a weapon (variants included): equal bytes, equal simulation.</summary>
    public static byte[] Bytes(WeaponSpec w) => Bytes(b => w.WriteHash(b));
    /// <summary>As <see cref="Bytes(WeaponSpec)"/> for a thrown item.</summary>
    public static byte[] Bytes(ThrownSpec t) => Bytes(b => ProjectileSpec.Write(b, t));
    /// <summary>As <see cref="Bytes(WeaponSpec)"/> for a jump pack.</summary>
    public static byte[] Bytes(JumpPackSpec j) => Bytes(b => j.Write(b));
    /// <summary>As <see cref="Bytes(WeaponSpec)"/> for a melee combo.</summary>
    public static byte[] Bytes(MeleeCombo m) => Bytes(b => MeleeCombo.Write(b, m));

    static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true)) write(w);
        return stream.ToArray();
    }
}

/// <summary>What kind of damage a <c>do_damage</c> deals (its <c>damagetype</c>): the player blueprint's received-damage modifiers key on it.</summary>
public enum DamageType : byte { Unknown = 0, Ranged = 1, Melee = 2, Explosive = 3 }

/// <summary>An armour shield's values (player blueprint <c>health_ext/armourshield_info</c>): the Iron Halo perk's entry replaces the default one.</summary>
public sealed record ShieldInfo(float Max, float RegenDelay, float RegenRate);

/// <summary>
/// What a perk's upgrade string does to the player blueprint (M43; <c>sm_space_marine_player</c> / <c>csm_chaos_marine_player</c>,
/// the entries that require <see cref="Upgrade"/>; read by <c>SmContent.PlayerUpgrades</c>). Modifiers from the lifetime action's
/// <c>apply_modifiers</c>: <c>received_*_damage</c> multiply the damage of that type the player takes, <c>health_regeneration</c>,
/// <c>health_regeneration_delay</c> and <c>armourshield_regeneration_delay</c> add to the blueprint's values (operation types as in
/// the file). <see cref="Shield"/>: an <c>armourshield_info</c> entry requiring it (Iron Halo). <see cref="DeathBlasts"/>: the
/// <c>death_action</c>'s delayed blasts (Final Vengeance), <see cref="DeathBlastDelay"/> seconds after the death.
/// <see cref="ShieldBreakBlasts"/>: the <c>armourshield_broken</c> blasts (Reactive Blast).
/// </summary>
public sealed record PlayerUpgrade(string Upgrade)
{
    public float ReceivedRanged { get; init; } = 1f;
    public float ReceivedMelee { get; init; } = 1f;
    public float ReceivedExplosive { get; init; } = 1f;
    /// <summary>Added to the health regeneration rate (health per second).</summary>
    public float HealthRegenRate { get; init; }
    /// <summary>Added to the health regeneration delay (seconds).</summary>
    public float HealthRegenDelay { get; init; }
    /// <summary>Added to the armour shield regeneration rate.</summary>
    public float ShieldRegenRate { get; init; }
    /// <summary>Added to the armour shield regeneration delay (seconds).</summary>
    public float ShieldRegenDelay { get; init; }
    /// <summary>Replaces the armour shield's values (before the additions above), or null.</summary>
    public ShieldInfo? Shield { get; init; }
    /// <summary>Blasts around the body <see cref="DeathBlastDelay"/> s after its death; empty: none.</summary>
    public IReadOnlyList<SplashSpec> DeathBlasts { get; init; } = [];
    public float DeathBlastDelay { get; init; }
    /// <summary>Blasts around the player when its armour shield breaks (falls from above 0 to 0); empty: none.</summary>
    public IReadOnlyList<SplashSpec> ShieldBreakBlasts { get; init; } = [];

    internal void Write(BinaryWriter w)
    {
        w.Write(Upgrade);
        foreach (float f in new[] { ReceivedRanged, ReceivedMelee, ReceivedExplosive, HealthRegenRate, HealthRegenDelay, ShieldRegenRate, ShieldRegenDelay, DeathBlastDelay })
            w.Write(f);
        w.Write(Shield is not null);
        if (Shield is { } s) { w.Write(s.Max); w.Write(s.RegenDelay); w.Write(s.RegenRate); }
        w.Write(DeathBlasts.Count); foreach (var b in DeathBlasts) ProjectileSpec.WriteSplash(w, b);
        w.Write(ShieldBreakBlasts.Count); foreach (var b in ShieldBreakBlasts) ProjectileSpec.WriteSplash(w, b);
    }
}

/// <summary>
/// A player's body as its perks leave it (M43; <see cref="Loadouts.Body"/>): health and armour shield values and the
/// received-damage multipliers. Without perks: the config's values and multipliers of 1.
/// </summary>
public readonly record struct BodyStats(float HealthMax, float HealthRegenDelay, float HealthRegenRate, float ShieldMax, float ShieldRegenDelay, float ShieldRegenRate,
    float ReceivedRanged, float ReceivedMelee, float ReceivedExplosive)
{
    /// <summary>The multiplier on damage of <paramref name="type"/> (1 for <see cref="DamageType.Unknown"/>).</summary>
    public float Received(DamageType type) => type switch
    {
        DamageType.Ranged => ReceivedRanged,
        DamageType.Melee => ReceivedMelee,
        DamageType.Explosive => ReceivedExplosive,
        _ => 1f,
    };
}

/// <summary>Whether a perk does anything in the simulation (M43; <see cref="Perks.StatusOf"/>).</summary>
public enum PerkActivity : byte
{
    /// <summary>Its effect is simulated (as far as the data gives one).</summary>
    Active = 0,
    /// <summary>Part of its effect is simulated; the rest needs a system we do not simulate (the note says which).</summary>
    Partial = 1,
    /// <summary>"NOT ACTIVE YET": nothing of it is simulated (no data consumer, or what it changes is not simulated).</summary>
    Inert = 2,
}

/// <summary>A perk's status for the class creator (M43): whether it works and, when not fully, why.</summary>
public readonly record struct PerkStatus(PerkActivity Activity, string Note)
{
    /// <summary>The label the class creator shows on a perk that does nothing yet.</summary>
    public const string NotActiveLabel = "NOT ACTIVE YET";

    /// <summary>
    /// perks/finish: the values line(s) the class creator shows under the perk's locale text (<see cref="PerkValues.Text"/>, from the specs the
    /// simulation uses); "" when none was determined or not filled in (<see cref="Perks.StatusOf"/> leaves it empty; the creator's statuses fill it).
    /// </summary>
    public string Values { get; init; } = "";
}

/// <summary>
/// Perk lookups for the class creator and the server log (M43). Whether a perk is active is read from the config: a perk is
/// active when a spec variant or a player-blueprint upgrade needs its upgrade string (or it is True Grit, whose rule is the
/// pistol choice). <see cref="Notes"/> holds what stays unsimulated (by perk file name; spec/behavior.md "Perks").
/// </summary>
public static class Perks
{
    /// <summary>
    /// Why a perk is (partly) inert, by its wargear file name: the part of its effect the simulation does not have. A perk not
    /// listed and with a consumer is fully active.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Notes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        // perks/finish: each checked again against the whole install (data/packages): what was found and what is missing.
        ["mp_perk_respawn_timer"] = "Rapid Deployment: shorter respawn; no file reads its upgrade string and no respawn time is in the data (the reduction is unknown; owner question)",
        ["mp_perk_friendly_spawn_target"] = "Teleport Homer: allies may respawn at the living carrier; no file reads its upgrade string (only the Dreadnought blueprints carry it); needs a spawn choice the game does not have yet (owner question)",
        ["mp_perk_enemy_info"] = "Tactical Readout: enemies' armour and health shown above them; no file reads its upgrade string; needs other players' health and armour on the wire (the snapshot budget is full) and a HUD marker (owner question)",
        ["mp_perk_secondary_weapon"] = "Weapon Versatility: an additional weapon; no upgrade string, only the front end's unlock_secondary_weapon; which weapon and slot is not in the data (owner question)",
        ["mp_perk_snap_to_increase"] = "Signum Link: no file reads its upgrade string; the only candidate data are aim-assist sets named __snap_upgrade (heavy bolter, plasma cannon: aim snap-to 0 to 12 degrees), and aim assist is not simulated (its PC use is unknown; owner question)",
        ["mp_perk_bolter_in_place_of_pistol"] = "True Grit: the pistol-slot Bolters are offered to Assault/Raptor only with it (data-implied rule); the bolter fires as in the data",
    };

    /// <summary>
    /// The perks' English names (the install's locale, docs/customizer-and-perks-data.md section 2; names only), by file name, for logs
    /// and the kill feed where the locale is not loaded (the class creator reads the locale itself).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> EnglishNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["mp_perk_improved_bullrush"] = "Furious Charge", ["mp_perk_ranged_damage_resistance"] = "Impenetrable",
        ["mp_perk_jump_pack_explodes_on_death"] = "Final Vengeance", ["mp_perk_jump_pack_take_off_damage"] = "Blast Off",
        ["mp_perk_jump_pack_ground_pound_damage"] = "Death From Above", ["mp_perk_jump_pack_reduced_cooldown"] = "Air-Cooled Thrusters",
        ["mp_perk_special_y_hold_attack"] = "Sure Strike", ["mp_perk_bolter_in_place_of_pistol"] = "True Grit",
        ["mp_perk_respawn_timer"] = "Rapid Deployment", ["mp_perk_shield_recharge"] = "Larraman's Blessing", ["mp_perk_enemy_info"] = "Tactical Readout",
        ["mp_perk_combat_knife_damage"] = "Serrated Combat Blade", ["mp_perk_secondary_weapon"] = "Weapon Versatility",
        ["mp_perk_equipment_double"] = "Favor of the Armory", ["mp_perk_friendly_spawn_target"] = "Teleport Homer",
        ["mp_perk_equipment_effectiveness"] = "Master-Crafted Wargear", ["mp_perk_melee_damage_resistance"] = "Feel No Pain",
        ["mp_perk_speed_increase"] = "Fast Stomp", ["mp_perk_better_stomp"] = "Furious Stomp", ["mp_perk_shield_break_damage"] = "Reactive Blast",
        ["mp_perk_snap_to_increase"] = "Signum Link", ["mp_perk_iron_halo"] = "Iron Halo", ["mp_perk_shield_regen_delay"] = "Artificer Armor",
        ["mp_perk_starting_ammo"] = "Ammunition Stores",
        ["mp_perk_weap_bolt_pistol_larger_mag"] = "Bolt Pistol Magazine", ["mp_perk_weap_plasma_pistol_less_heat"] = "Plasma Heat Sink",
        ["mp_perk_weap_chainsword_heal_on_hit"] = "Swordsman's Zeal", ["mp_perk_weap_power_axe_slow_on_hit"] = "Axeman's Zeal",
        ["mp_perk_weap_power_sword_heal_on_hit"] = "Power Swordsman's Zeal", ["mp_perk_weap_thunder_hammer_aoe_strike"] = "Killing Blow",
        ["mp_perk_weap_bolter_accuracy"] = "Bolter Targeter", ["mp_perk_weap_bolter_kraken_rounds"] = "Kraken Bolts",
        ["mp_perk_weap_storm_bolter_reload"] = "Rapid-Fire Reload", ["mp_perk_weap_storm_bolter_hip_fire"] = "Improved Twin-Link",
        ["mp_perk_weap_stalker_larger_mag"] = "Stalker Ammo Reserves", ["mp_perk_weap_stalker_zoom_modes"] = "Burst Fire",
        ["mp_perk_weap_gren_launcher_automatic"] = "Vengeance Projectiles", ["mp_perk_weap_gren_launcher_more_ammo"] = "Vengeance Payload",
        ["mp_perk_weap_melta_gun_range_increase"] = "Improved Melta Charge", ["mp_perk_weap_melta_gun_clip_size"] = "Additional Melta Fuel",
        ["mp_perk_weap_plasma_gun_less_heat"] = "Improved Plasma Charge", ["mp_perk_weap_plasma_gun_alt_fire_cost"] = "Enlarged Plasma Chamber",
        ["mp_perk_weap_heavy_bolter_braced_fire_time"] = "Heavy Bolter Expertise", ["mp_perk_weap_heavy_bolter_heat"] = "Heavy Bolter Coolant",
        ["mp_perk_weap_las_cannon_recharge"] = "Lascannon Capacitors", ["mp_perk_weap_las_cannon_zoom"] = "Lascannon Stabilizers",
        ["mp_perk_weap_plasma_cannon_less_heat"] = "Plasma Heat Sink", ["mp_perk_weap_plasma_cannon_alt_fire_charge_time"] = "Improved Plasma Charge",
    };

    /// <summary>A perk's English name (<see cref="EnglishNames"/>), else its file name.</summary>
    public static string NameOf(PerkSpec perk) => EnglishNames.GetValueOrDefault(perk.Name, perk.Name);

    /// <summary>
    /// The upgrade strings what class <paramref name="playerClass"/> carries reacts to (its weapon and equipment choices, jump pack
    /// and melee combos, and the player blueprint); every class's when <paramref name="playerClass"/> is negative.
    /// </summary>
    public static HashSet<string> Consumed(SimConfig c, int playerClass = -1)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add<T>(IReadOnlyList<UpgradeVariant<T>> list) where T : class { foreach (var v in list) set.UnionWith(v.Upgrades); }
        void Weapon(int i) { if (i >= 0 && i < c.Weapons.Count) { Add(c.Weapons[i].Variants); foreach (var p in c.Weapons[i].PerkPierces) set.Add(p.Upgrade); } }
        for (int k = 0; k < c.Classes.Count; k++)
        {
            if (playerClass >= 0 && k != playerClass) continue;
            foreach (var slot in new[] { WeaponSlot.Ranged, WeaponSlot.Pistol }) foreach (int i in Loadouts.Choices(c, k, slot)) Weapon(i);
            foreach (int i in Loadouts.EquipmentChoices(c, k)) if (i >= 0 && i < c.Thrown.Count) Add(c.Thrown[i].Variants);
            if (Loadouts.JumpPack(c, k) is { } pack) Add(pack.Variants);
            foreach (var m in c.Classes[k].MeleeChoices) Add(m.Variants);
        }
        foreach (var u in c.PlayerUpgrades) set.Add(u.Upgrade);
        return set;
    }

    /// <summary>
    /// A perk's status in this config: Inert when nothing a class that may take it carries reacts to its upgrade string (True Grit
    /// counts as active: its rule is the pistol choice); Partial when something does but <see cref="Notes"/> lists an unsimulated
    /// part; else Active.
    /// </summary>
    public static PerkStatus StatusOf(SimConfig c, PerkSpec perk)
    {
        int index = -1;
        for (int i = 0; i < c.Perks.Count; i++) if (ReferenceEquals(c.Perks[i], perk) || c.Perks[i].Name.Equals(perk.Name, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < c.Classes.Count; k++)
            if (index < 0 || Loadouts.ValidPerk(c, k, index)) consumed.UnionWith(Consumed(c, k));
        bool used = (perk.UpgradeString is { Length: > 0 } u && consumed.Contains(u)) || string.Equals(perk.Name, Loadouts.TrueGritPerk, StringComparison.OrdinalIgnoreCase);
        string note = Notes.GetValueOrDefault(perk.Name, "");
        if (!used) return new PerkStatus(PerkActivity.Inert, note.Length > 0 ? note : "no simulated entry needs its upgrade string");
        return new PerkStatus(note.Length > 0 && !string.Equals(perk.Name, Loadouts.TrueGritPerk, StringComparison.OrdinalIgnoreCase) ? PerkActivity.Partial : PerkActivity.Active, note);
    }
}
