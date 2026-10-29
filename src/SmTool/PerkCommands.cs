using System.Collections;
using System.Globalization;
using System.Reflection;
using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool perks &lt;game-dir|auto&gt;</c> (M43): every perk the classes offer with its upgrade string, the classes that may take
/// it and its simulation status (<see cref="Perks.StatusOf"/>), then every perk variant the content loader read (weapons, equipment,
/// jump packs, melee combos) as the fields that differ from the base spec, and the player-blueprint upgrades. Read-only.
/// </summary>
static class PerkCommands
{
    public static int List(string gameDir)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var tuning = GameplayTuning.Load(gameDir);
        var c = tuning.Config;
        foreach (var note in tuning.Notes.Where(n => n.Contains("perk", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("note: " + note);
        Console.WriteLine($"{c.Perks.Count} perks: {c.Perks.Count(p => Perks.StatusOf(c, p).Activity == PerkActivity.Active)} active, {c.Perks.Count(p => Perks.StatusOf(c, p).Activity == PerkActivity.Partial)} partly, {c.Perks.Count(p => Perks.StatusOf(c, p).Activity == PerkActivity.Inert)} inert");
        foreach (var p in c.Perks)
        {
            int index = c.Perks.ToList().IndexOf(p);
            var classes = Enumerable.Range(0, c.Classes.Count).Where(k => Loadouts.ValidPerk(c, k, index)).Select(k => c.Classes[k].Name);
            var status = Perks.StatusOf(c, p);
            Console.WriteLine($"  {p.Name} [{p.UpgradeString ?? "-"}] {p.Kind} lvl {p.Level} ({string.Join(", ", classes)}): {status.Activity}{(status.Note.Length > 0 ? " - " + status.Note : "")}");
            // perks/finish: the values line the class creator shows under the locale text (PerkValues, from the same specs the simulation picks).
            foreach (var line in PerkValues.Text(c, p).Split('\n', StringSplitOptions.RemoveEmptyEntries)) Console.WriteLine("      values: " + line);
        }
        void Show<T>(string what, T spec, IReadOnlyList<UpgradeVariant<T>> variants) where T : class
        {
            foreach (var v in variants)
            {
                var a = Flatten(spec); var b = Flatten(v.Spec);
                var diff = b.Where(kv => !a.TryGetValue(kv.Key, out var x) || x != kv.Value).Select(kv => $"{kv.Key}: {a.GetValueOrDefault(kv.Key, "-")} -> {kv.Value}")
                    .Concat(a.Keys.Where(k => !b.ContainsKey(k)).Select(k => $"{k}: {a[k]} -> -")).ToList();
                Console.WriteLine($"{what} + {string.Join(" + ", v.Upgrades)}: {diff.Count} field(s)");
                foreach (var d in diff.Take(40)) Console.WriteLine("    " + d);
                if (diff.Count > 40) Console.WriteLine($"    ... {diff.Count - 40} more");
            }
        }
        foreach (var w in c.Weapons) Show(w.Name, w, w.Variants);
        foreach (var t in c.Thrown) Show(t.Name, t, t.Variants);
        foreach (var j in c.JumpPacks) Show(j.Name, j, j.Variants);
        foreach (var combo in c.Classes.SelectMany(k => k.MeleeChoices).DistinctBy(m => m.Wargear)) Show(combo.Wargear, combo, combo.Variants);
        // perks/inert-2: the combat stims' base values (their perk variants are listed above with the other thrown items).
        foreach (var t in c.Thrown.Where(t => t.Stim is not null))
            Console.WriteLine($"stims {t.Name}: {t.Count} carried, {t.Cooldown} s apart; effect {t.Stim!.UseRelease:F3} s after the press for {t.Stim.Lifetime} s (use {t.Stim.UseDuration:F3} s); " +
                $"health-only damage taken ranged x{t.Stim.ReceivedRanged}, melee x{t.Stim.ReceivedMelee}, explosive x{t.Stim.ReceivedExplosive}, world x{t.Stim.ReceivedWorld}; melee dealt x{t.Stim.MeleeDamage}; reaction immunity {t.Stim.ReactionImmune}");
        foreach (var u in c.PlayerUpgrades)
            Console.WriteLine($"player + {u.Upgrade}: received ranged x{u.ReceivedRanged}, melee x{u.ReceivedMelee}, explosive x{u.ReceivedExplosive}; health regen +{u.HealthRegenRate}/s, delay {u.HealthRegenDelay:+0.##;-0.##;0} s; shield regen delay {u.ShieldRegenDelay:+0.##;-0.##;0} s"
                + (u.Shield is { } s ? $"; shield {s.Max} (delay {s.RegenDelay}, rate {s.RegenRate})" : "")
                + string.Concat(u.DeathBlasts.Select(b => $"; death blast after {u.DeathBlastDelay} s: r {b.Radius}, {b.Damage.Damage} + {b.Damage.ShieldDamage} {b.Damage.Type}, {b.Reactions.Count} reaction(s)"))
                + string.Concat(u.ShieldBreakBlasts.Select(b => $"; armour-break blast: r {b.Radius}, {b.Damage.Damage} + {b.Damage.ShieldDamage} {b.Damage.Type}, {b.Reactions.Count} reaction(s)")));
        return 0;
    }

    /// <summary>A spec's public values as path → text (records and lists recursively; variant lists left out).</summary>
    static Dictionary<string, string> Flatten(object spec)
    {
        var map = new Dictionary<string, string>();
        void Walk(object? o, string path, int depth)
        {
            if (o is null) { map[path] = "null"; return; }
            var type = o.GetType();
            if (o is string s) { map[path] = s; return; }
            if (type.IsPrimitive || type.IsEnum || o is decimal) { map[path] = Convert.ToString(o, CultureInfo.InvariantCulture) ?? ""; return; }
            if (depth > 8) { map[path] = o.ToString() ?? ""; return; }
            if (o is IEnumerable list)
            {
                int i = 0;
                foreach (var item in list) Walk(item, $"{path}[{i++}]", depth + 1);
                if (i == 0) map[path] = "[]";
                return;
            }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name is "Variants" or "EqualityContract" || p.PropertyType.IsGenericType && p.PropertyType.Name.StartsWith("IReadOnlyList") && p.PropertyType.GenericTypeArguments[0].Name.StartsWith("UpgradeVariant")) continue;
                object? value;
                try { value = p.GetValue(o); } catch { continue; }
                Walk(value, path.Length == 0 ? p.Name : path + "." + p.Name, depth + 1);
            }
        }
        Walk(spec, "", 0);
        return map;
    }
}
