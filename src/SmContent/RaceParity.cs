using System.Collections;
using System.Globalization;
using System.Reflection;
using SmSim;

namespace SmContent;

/// <summary>The kind of a wargear pair in <see cref="RaceParity.Pairs"/>.</summary>
public enum ParityKind { Ranged, Pistol, Exotic, Melee, Equipment, JumpPack }

/// <summary>A Chaos wargear file and its loyalist twin (wargear file names without folders).</summary>
public sealed record ParityPair(string Chaos, string Loyalist, ParityKind Kind);

/// <summary>A Chaos wargear file with no loyalist twin, and why that is harmless.</summary>
public sealed record NoTwin(string Chaos, string Why);

/// <summary>
/// <b>Owner's rule (2026-10-28, owner's knowledge): the Chaos weapons are identical to the loyalists' in stats and behaviour</b>; only the models
/// (and effects, sounds) differ ("weapons are identical in stats and behaviors between chaos and loyalists space marines. you only need to make
/// them have different models"). The game data give most Chaos files the loyalist numbers already; where they differ (damage, cooldowns, clips,
/// fire delays, jump pack and grenade values, perk variants) the Chaos spec takes the loyalist twin's value.
/// <para>
/// <see cref="Apply"/> copies, per pair of <see cref="Pairs"/>, everything the simulation reads from the loyalist spec onto the Chaos one (weapons,
/// thrown items, jump packs, melee combos with heavy strikes and charge, every perk variant, the delayed fire of <see cref="ChargeShot.FireDelay"/>
/// which the simulation fires after the delay: behaviour, not cosmetics), and keeps what makes the Chaos item look and sound Chaos: the spec's
/// <c>Name</c> / <c>Wargear</c> and every projectile <c>Blueprint</c> (the key of the models, effects and sounds, found by name). The Chaos classes' default
/// choices are the twins of the loyalist defaults of the same role. Kind: the owner's knowledge (rule), confidence high.
/// </para>
/// </summary>
public static class RaceParity
{
    static ParityPair P(string chaos, string loyalist, ParityKind kind) => new(chaos, loyalist, kind);

    /// <summary>
    /// Every Chaos wargear file the multiplayer classes name and its loyalist twin. The twin is the file with the race prefix stripped except for the
    /// two renamed melee weapons (<c>chain_axe</c> is the Chaos power axe, <c>2hand_power_mace</c> the Chaos thunder hammer; checked against the
    /// install with <c>smtool race-parity</c>: same ability layout, same slot and blueprint family). Verified-local.
    /// </summary>
    public static IReadOnlyList<ParityPair> Pairs { get; } =
    [
        P("csm_mp_pistol_bolt_pistol", "sm_mp_pistol_bolt_pistol", ParityKind.Pistol),
        P("csm_mp_pistol_bolt_pistol_devastator", "sm_mp_pistol_bolt_pistol_devastator", ParityKind.Pistol),
        P("csm_mp_pistol_plasma_pistol", "sm_mp_pistol_plasma_pistol", ParityKind.Pistol),
        P("csm_mp_pistol_bolter", "sm_mp_pistol_bolter", ParityKind.Pistol),
        P("csm_mp_pistol_bolter_gold", "sm_mp_pistol_bolter_gold", ParityKind.Pistol),
        P("csm_mp_ranged_bolter", "sm_mp_ranged_bolter", ParityKind.Ranged),
        P("csm_mp_ranged_bolter_gold", "sm_mp_ranged_bolter_gold", ParityKind.Ranged),
        P("csm_mp_ranged_plasma_gun", "sm_mp_ranged_plasma_gun", ParityKind.Ranged),
        P("csm_mp_ranged_sniper_rifle", "sm_mp_ranged_sniper_rifle", ParityKind.Ranged),
        P("csm_mp_ranged_melta", "sm_mp_ranged_melta", ParityKind.Ranged),
        P("csm_mp_ranged_storm_bolter", "sm_mp_ranged_storm_bolter", ParityKind.Ranged),
        P("csm_mp_ranged_grenade_launcher", "sm_mp_ranged_grenade_launcher", ParityKind.Ranged),
        P("csm_mp_exotic_heavy_bolter", "sm_mp_exotic_heavy_bolter", ParityKind.Exotic),
        P("csm_mp_exotic_las_cannon", "sm_mp_exotic_las_cannon", ParityKind.Exotic),
        P("csm_mp_exotic_plasma_cannon", "sm_mp_exotic_plasma_cannon", ParityKind.Exotic),
        P("csm_mp_melee_combat_knife", "sm_mp_melee_combat_knife", ParityKind.Melee),
        P("csm_mp_melee_chainsword", "sm_mp_melee_chainsword", ParityKind.Melee),
        P("csm_mp_melee_chainsword_gold", "sm_mp_melee_chainsword_gold", ParityKind.Melee),
        P("csm_mp_melee_powersword", "sm_mp_melee_powersword", ParityKind.Melee),
        P("csm_mp_melee_chain_axe", "sm_mp_melee_power_axe", ParityKind.Melee),
        P("csm_mp_melee_2hand_power_mace", "sm_mp_melee_2hand_thunder_hammer", ParityKind.Melee),
        P("csm_mp_eq_frag_grenade", "sm_mp_eq_frag_grenade", ParityKind.Equipment),
        P("csm_mp_eq_krak_grenade", "sm_mp_eq_krak_grenade", ParityKind.Equipment),
        P("csm_mp_abil_jumppack", "sm_mp_abil_jumppack", ParityKind.JumpPack),
    ];

    /// <summary>
    /// Chaos multiplayer wargear files with no loyalist twin. None is a class choice, so none changes a match; the data stay as read. The Chaos classes'
    /// other base wargear (sprint, both evades, stimpack, melta bomb) are the loyalist files themselves (<c>sm_mp_abil_sprint</c>,
    /// <c>sm_mp_core_abil_*</c>, <c>sm_mp_eq_*</c>) and so identical already.
    /// </summary>
    public static IReadOnlyList<NoTwin> NoTwins { get; } =
    [
        new("csm_mp_ranged_lasgun_ai", "an AI-only weapon (no class names it); no loyalist counterpart"),
        new("csm_mp_exotic_auto_cannon", "the Chaos Havoc's unused autocannon (no class names it); no loyalist counterpart"),
    ];

    static readonly Dictionary<string, string> LoyalistOf = Pairs.ToDictionary(p => p.Chaos, p => p.Loyalist, StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> ChaosOf = Pairs.ToDictionary(p => p.Loyalist, p => p.Chaos, StringComparer.OrdinalIgnoreCase);

    /// <summary>The loyalist twin of a Chaos wargear name, or null when it has none in <see cref="Pairs"/>.</summary>
    public static string? LoyalistTwin(string chaos) => LoyalistOf.GetValueOrDefault(chaos);

    /// <summary>The Chaos twin of a loyalist wargear name, or null.</summary>
    public static string? ChaosTwin(string loyalist) => ChaosOf.GetValueOrDefault(loyalist);

    /// <summary>The property names a spec keeps from the Chaos item: they key its models, effects and sounds.</summary>
    static readonly HashSet<string> KeptNames = new(StringComparer.Ordinal) { "Name", "Wargear", "Blueprint" };

    // ---- applying --------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The classes with every Chaos item in <see cref="Pairs"/> that the simulation reads taking its loyalist twin's numbers and behaviour (see the type's
    /// remarks), and the Chaos classes' default choices the twins of the loyalist defaults of the same role. Items without a twin, or whose twin was
    /// not read, stay as they are with a note. Indices into <see cref="MpClasses.Weapons"/>, <see cref="MpClasses.Thrown"/> and the rest are unchanged.
    /// </summary>
    public static MpClasses Apply(MpClasses classes, List<string> notes)
    {
        var weapons = classes.Weapons.ToList();
        int W(string name) => weapons.FindIndex(w => w.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        foreach (var p in Pairs.Where(p => p.Kind is ParityKind.Ranged or ParityKind.Pistol or ParityKind.Exotic))
        {
            int c = W(p.Chaos), l = W(p.Loyalist);
            if (c < 0) continue;
            if (l < 0) { notes.Add($"race parity: {p.Chaos} has no read twin {p.Loyalist}; its data stay"); continue; }
            weapons[c] = Adopt(weapons[l], weapons[c]);
        }

        var thrown = classes.Thrown.ToList();
        foreach (var p in Pairs.Where(p => p.Kind == ParityKind.Equipment))
        {
            int c = thrown.FindIndex(t => t.Name.Equals(p.Chaos, StringComparison.OrdinalIgnoreCase)), l = thrown.FindIndex(t => t.Name.Equals(p.Loyalist, StringComparison.OrdinalIgnoreCase));
            if (c < 0) continue;
            if (l < 0) { notes.Add($"race parity: {p.Chaos} has no read twin {p.Loyalist}; its data stay"); continue; }
            thrown[c] = Adopt(thrown[l], thrown[c]);
        }

        var packs = classes.JumpPacks.ToList();
        foreach (var p in Pairs.Where(p => p.Kind == ParityKind.JumpPack))
        {
            int c = packs.FindIndex(j => j.Name.Equals(p.Chaos, StringComparison.OrdinalIgnoreCase)), l = packs.FindIndex(j => j.Name.Equals(p.Loyalist, StringComparison.OrdinalIgnoreCase));
            if (c < 0) continue;
            if (l < 0) { notes.Add($"race parity: {p.Chaos} has no read twin {p.Loyalist}; its data stay"); continue; }
            packs[c] = Adopt(packs[l], packs[c]);
        }

        // Melee combos live in the loadouts' choices (by wargear name); one adopted combo per Chaos file.
        var combos = new Dictionary<string, MeleeCombo>(StringComparer.OrdinalIgnoreCase);
        foreach (var combo in classes.Loadouts.SelectMany(l => l.MeleeChoices)) combos.TryAdd(combo.Wargear, combo);
        var adoptedCombos = new Dictionary<string, MeleeCombo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pairs.Where(p => p.Kind == ParityKind.Melee))
        {
            if (!combos.TryGetValue(p.Chaos, out var chaos)) continue;
            if (!combos.TryGetValue(p.Loyalist, out var loyal)) { notes.Add($"race parity: {p.Chaos} has no read twin {p.Loyalist}; its data stay"); continue; }
            adoptedCombos[p.Chaos] = Adopt(loyal, chaos);
        }
        var animations = classes.MeleeAnimationsByWeapon.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var heavyAnimations = classes.MeleeHeavyAnimationsByWeapon.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var p in Pairs.Where(p => p.Kind == ParityKind.Melee && adoptedCombos.ContainsKey(p.Chaos)))
        {
            if (animations.TryGetValue(p.Loyalist, out var a)) animations[p.Chaos] = a;
            if (heavyAnimations.TryGetValue(p.Loyalist, out var h)) heavyAnimations[p.Chaos] = h;
        }

        // Defaults: a Chaos class's default primary, pistol, melee and equipment are the twins of the loyalist class's of the same role.
        // Primary and Pistol are weapon indices into the shared list (absolute, whatever the order of the choice lists); the melee and equipment
        // choices are positions in the class's list with the default first, so a reorder here moves the position of the other choices (Hello indices
        // are positional): it happens only when the data's own default is not the twin, which on the install it never is.
        var loadouts = classes.Loadouts.ToList();
        for (int i = 0; i < loadouts.Count; i++)
        {
            var l = loadouts[i];
            if (l.Side != Team.Chaos) continue;
            // A class without a role has no loyalist counterpart to follow (Teams.Equivalent falls back to a position then): its choices stay as built.
            var loyal = l.Role.Length == 0 ? null : loadouts.FirstOrDefault(x => x.Side == Team.SpaceMarines && x.Role == l.Role);
            var melee = l.MeleeChoices.Select(m => adoptedCombos.GetValueOrDefault(m.Wargear, m)).ToList();

            if (loyal is not null)
            {
                int primary = Twin(loyal.Primary, l.Primary, l.PrimaryChoices, notes, l.Name, "primary");
                int pistol = Twin(loyal.Pistol, l.Pistol, l.PistolChoices, notes, l.Name, "pistol");
                l = l with { Primary = primary, Pistol = pistol };
                melee = DefaultFirst(melee, loyal.MeleeChoices.FirstOrDefault()?.Wargear, notes, l.Name);
                l = l with { EquipmentChoices = DefaultFirstEquipment(l.EquipmentChoices, loyal.EquipmentChoices.Count > 0 ? classes.Thrown[loyal.EquipmentChoices[0]].Name : null) };
            }
            var first = melee.Count > 0 ? melee[0] : null;
            loadouts[i] = l with
            {
                MeleeChoices = melee, Melee = first?.Steps ?? l.Melee, MeleeHeavy = first?.Heavy ?? l.MeleeHeavy, MeleeRush = first is null ? l.MeleeRush : first.Rush,
            };
        }

        int Twin(int loyalIndex, int chaosIndex, IReadOnlyList<int> choices, List<string> log, string className, string slot)
        {
            if (loyalIndex < 0 || chaosIndex < 0) return chaosIndex;   // never grant a weapon to a Chaos class that has none (the Raptor has no primary)
            if (ChaosTwin(classes.Weapons[loyalIndex].Name) is not { } twin) return chaosIndex;
            int t = W(twin);
            if (t < 0) { log.Add($"race parity: {className}: the twin {twin} of the loyalist {slot} is not read; the default stays"); return chaosIndex; }
            if (choices.Count > 0 && !choices.Contains(t)) { log.Add($"race parity: {className}: the twin {twin} of the loyalist {slot} is not a choice of the class; the default stays"); return chaosIndex; }
            return t;
        }
        List<MeleeCombo> DefaultFirst(List<MeleeCombo> list, string? loyalDefault, List<string> log, string className)
        {
            if (loyalDefault is null || ChaosTwin(loyalDefault) is not { } twin) return list;
            int at = list.FindIndex(m => m.Wargear.Equals(twin, StringComparison.OrdinalIgnoreCase));
            if (at < 0) { log.Add($"race parity: {className}: the twin {twin} of the loyalist default melee weapon is not read; the default stays"); return list; }
            if (at == 0) return list;
            var moved = list[at];
            return [moved, .. list.Where((_, i) => i != at)];
        }
        IReadOnlyList<int> DefaultFirstEquipment(IReadOnlyList<int> choices, string? loyalDefault)
        {
            if (loyalDefault is null || ChaosTwin(loyalDefault) is not { } twin) return choices;
            int at = choices.ToList().FindIndex(i => thrown[i].Name.Equals(twin, StringComparison.OrdinalIgnoreCase));
            if (at <= 0) return choices;
            return [choices[at], .. choices.Where((_, i) => i != at)];
        }

        // The class-indexed melee animations follow the (possibly reordered) default.
        var perClass = classes.MeleeAnimations.ToList();
        for (int i = 0; i < loadouts.Count && i < perClass.Count; i++)
            if (loadouts[i].Side == Team.Chaos && loadouts[i].MeleeChoices.Count > 0 && animations.TryGetValue(loadouts[i].MeleeChoices[0].Wargear, out var anim)) perClass[i] = anim;

        // Chaos items the table has no twin for keep their data, said once.
        foreach (var name in weapons.Select(w => w.Name).Concat(thrown.Select(t => t.Name)).Concat(packs.Select(j => j.Name)).Concat(adoptedCombos.Keys.Concat(combos.Keys)).Distinct(StringComparer.OrdinalIgnoreCase))
            if (name.StartsWith("csm_", StringComparison.OrdinalIgnoreCase) && LoyalistTwin(name) is null && !NoTwins.Any(n => n.Chaos.Equals(name, StringComparison.OrdinalIgnoreCase)))
                notes.Add($"race parity: {name} has no loyalist twin in the table; its data stay");

        return classes with
        {
            Weapons = weapons, Thrown = thrown, JumpPacks = packs, Loadouts = loadouts, MeleeAnimations = perClass,
            MeleeAnimationsByWeapon = animations, MeleeHeavyAnimationsByWeapon = heavyAnimations,
        };
    }

    // ---- adopting one item ---------------------------------------------------------------------------------------------------------------

    /// <summary>The loyalist weapon with the Chaos item's name, projectile blueprints and (per matching upgrades) variants' names and blueprints.</summary>
    static WeaponSpec Adopt(WeaponSpec loyal, WeaponSpec chaos)
    {
        var stages = loyal.Charge?.Stages.Select((s, i) =>
        {
            var chaosProjectile = chaos.Charge is { } cc && i < cc.Stages.Count ? cc.Stages[i].Shot?.Projectile : null;
            return s.Shot is null ? s : s with { Shot = s.Shot with { Projectile = Keep(s.Shot.Projectile, chaosProjectile ?? chaos.Projectile) } };
        }).ToList();
        return loyal with
        {
            Name = chaos.Name,
            Projectile = Keep(loyal.Projectile, chaos.Projectile),
            Charge = loyal.Charge is { } charge ? charge with { Stages = stages! } : null,
            Variants = loyal.Variants.Select(v => v with { Spec = Adopt(v.Spec, chaos.Variants.FirstOrDefault(c => c.Upgrades.SequenceEqual(v.Upgrades))?.Spec ?? chaos with { Variants = [] }) }).ToList(),
        };
    }

    static ThrownSpec Adopt(ThrownSpec loyal, ThrownSpec chaos) => loyal with
    {
        Name = chaos.Name,
        Projectile = Keep(loyal.Projectile, chaos.Projectile)!,
        Variants = loyal.Variants.Select(v => v with { Spec = Adopt(v.Spec, chaos.Variants.FirstOrDefault(c => c.Upgrades.SequenceEqual(v.Upgrades))?.Spec ?? chaos with { Variants = [] }) }).ToList(),
    };

    static JumpPackSpec Adopt(JumpPackSpec loyal, JumpPackSpec chaos) => loyal with
    {
        Name = chaos.Name,
        Variants = loyal.Variants.Select(v => v with { Spec = Adopt(v.Spec, chaos.Variants.FirstOrDefault(c => c.Upgrades.SequenceEqual(v.Upgrades))?.Spec ?? chaos with { Variants = [] }) }).ToList(),
    };

    static MeleeCombo Adopt(MeleeCombo loyal, MeleeCombo chaos) => loyal with
    {
        Wargear = chaos.Wargear,
        Variants = loyal.Variants.Select(v => v with { Spec = Adopt(v.Spec, chaos.Variants.FirstOrDefault(c => c.Upgrades.SequenceEqual(v.Upgrades))?.Spec ?? chaos with { Variants = [] }) }).ToList(),
    };

    /// <summary>The loyalist projectile with the Chaos projectile's blueprint (the key of its trail, impact and sound effects).</summary>
    static ProjectileSpec? Keep(ProjectileSpec? loyal, ProjectileSpec? chaos) => loyal is null ? null : chaos is null ? loyal : loyal with { Blueprint = chaos.Blueprint };

    // ---- comparing -------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The items of a built class set by wargear name, the ones <see cref="Apply"/> touches: weapons, thrown items, jump packs and melee combos.
    /// </summary>
    public static IReadOnlyDictionary<string, object> Items(MpClasses classes)
    {
        var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in classes.Weapons) map.TryAdd(w.Name, w);
        foreach (var t in classes.Thrown) map.TryAdd(t.Name, t);
        foreach (var j in classes.JumpPacks) map.TryAdd(j.Name, j);
        foreach (var m in classes.Loadouts.SelectMany(l => l.MeleeChoices)) map.TryAdd(m.Wargear, m);
        return map;
    }

    /// <summary>
    /// The values that differ between two items of the same type, as <c>path: a | b</c>: every public property and field, records and lists
    /// recursively, perk variants included, the kept names (<c>Name</c>, <c>Wargear</c>, <c>Blueprint</c>) left out. Empty: the two behave alike.
    /// </summary>
    public static IReadOnlyList<string> Differences(object a, object b)
    {
        var x = Flatten(a); var y = Flatten(b);
        var list = new List<string>();
        foreach (var k in x.Keys.Union(y.Keys).Order(StringComparer.Ordinal))
        {
            x.TryGetValue(k, out var u); y.TryGetValue(k, out var v);
            if (u != v) list.Add($"{k}: {u ?? "-"} | {v ?? "-"}");
        }
        return list;
    }

    static Dictionary<string, string> Flatten(object spec)
    {
        var map = new Dictionary<string, string>();
        void Walk(object? o, string path, int depth)
        {
            if (o is null) { map[path] = "null"; return; }
            var type = o.GetType();
            if (o is string s) { map[path] = s; return; }
            if (type.IsPrimitive || type.IsEnum || o is decimal) { map[path] = Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""; return; }
            if (depth > 14) { map[path] = o.ToString() ?? ""; return; }
            if (o is IEnumerable list)
            {
                int i = 0;
                foreach (var item in list) Walk(item, $"{path}[{i++}]", depth + 1);
                if (i == 0) map[path] = "[]";
                return;
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name == "EqualityContract" || KeptNames.Contains(p.Name)) continue;
                object? value;
                try { value = p.GetValue(o); } catch (TargetInvocationException) { continue; }
                Walk(value, path.Length == 0 ? p.Name : path + "." + p.Name, depth + 1);
            }
            foreach (var f in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
                Walk(f.GetValue(o), path.Length == 0 ? f.Name : path + "." + f.Name, depth + 1);
        }
        Walk(spec, "", 0);
        return map;
    }
}
