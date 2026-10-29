using SmFormats.Baf;
using SmSim;

namespace SmContent;

/// <summary>
/// What perks' upgrade strings do to a multiplayer player blueprint (M43; <c>ebps</c> <c>sm_space_marine_player</c>,
/// <c>csm_chaos_marine_player</c>), as <see cref="PlayerUpgrade"/>s (**Verified-local** layout; docs/customizer-and-perks-data.md
/// section 2):
/// <list type="bullet">
/// <item><c>extensions/entity_actions_ext/lifetime_action/commands</c>: <c>actions_by_upgrade</c> entries with <c>apply_modifiers</c>
/// (<c>received_ranged_damage</c>, <c>received_melee_damage</c>, <c>received_explosive_damage</c> multiplications;
/// <c>health_regeneration</c>, <c>health_regeneration_delay</c>, <c>armourshield_regeneration_delay</c> additions). Other modifier
/// types are noted and not applied.</item>
/// <item><c>extensions/health_ext/death_action/commands</c>: an entry's <c>delayed_action</c> (its <c>delay</c> and blasts).</item>
/// <item><c>extensions/health_ext/armourshield_info</c>: an <c>armourshield_info</c> that requires a string replaces the shield's max,
/// regeneration delay and rate; the default one's <c>armourshield_broken</c> entries' blasts go off when the shield breaks.</item>
/// </list>
/// Entries are read only for <paramref name="perkStrings"/> (the classes' perks; e.g. not the coop strings) and only when they
/// require exactly that one string.
/// </summary>
public static class PlayerUpgrades
{
    public static List<PlayerUpgrade> Read(BafDocument blueprint, IReadOnlyCollection<string> perkStrings, List<string> notes)
    {
        var byUpgrade = new Dictionary<string, PlayerUpgrade>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        PlayerUpgrade Get(string u)
        {
            if (byUpgrade.TryGetValue(u, out var x)) return x;
            order.Add(u);
            return byUpgrade[u] = new PlayerUpgrade(u);
        }
        // The single perk string an entry requires, or null (no requirement, several, or not a perk's).
        string? Only(BafTable entry)
        {
            var list = entry.Table("required_upgrade_name/required_list")?.Entries.Where(e => e.Type == BafType.String).Select(e => e.AsString()).ToList() ?? [];
            return list.Count == 1 && perkStrings.Contains(list[0]) ? list[0] : null;
        }
        IEnumerable<(string Upgrade, BafTable Commands)> Entries(BafTable? commands)
        {
            foreach (var block in commands?.Entries.Where(e => e.Type == BafType.Table && e.Name == "actions_by_upgrade").Select(e => e.AsTable()) ?? [])
                foreach (var entry in block.Table("actions")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "entry").Select(e => e.AsTable()) ?? [])
                    if (Only(entry) is { } u && entry.Table("action/commands") is { } c) yield return (u, c);
        }

        // Modifiers of the lifetime action.
        foreach (var (u, commands) in Entries(blueprint.Root.Table("extensions/entity_actions_ext/lifetime_action/commands")))
        {
            foreach (var m in commands.Table("apply_modifiers/modifiers")?.Entries.Where(e => e.Type == BafType.Table && e.Name == "modifier").Select(e => e.AsTable()) ?? [])
            {
                string type = m.String("modifier_type") ?? "", op = m.String("operation_type") ?? "";
                if (m.Float("amount") is not float amount || !float.IsFinite(amount)) { notes.Add($"{u}: modifier {type} without an amount"); continue; }
                var p = Get(u);
                byUpgrade[u] = (type, op) switch
                {
                    ("received_ranged_damage", "multiplication") => p with { ReceivedRanged = p.ReceivedRanged * amount },
                    ("received_melee_damage", "multiplication") => p with { ReceivedMelee = p.ReceivedMelee * amount },
                    ("received_explosive_damage", "multiplication") => p with { ReceivedExplosive = p.ReceivedExplosive * amount },
                    ("health_regeneration", "addition") => p with { HealthRegenRate = p.HealthRegenRate + amount },
                    ("health_regeneration_delay", "addition") => p with { HealthRegenDelay = p.HealthRegenDelay + amount },
                    ("armourshield_regeneration", "addition") => p with { ShieldRegenRate = p.ShieldRegenRate + amount },
                    ("armourshield_regeneration_delay", "addition") => p with { ShieldRegenDelay = p.ShieldRegenDelay + amount },
                    _ => Noted(p, $"{u}: modifier {type} ({op} {amount}) not simulated"),
                };
            }
            foreach (var other in commands.Entries.Where(e => e.Type == BafType.Table && e.Name != "apply_modifiers"))
                notes.Add($"{u}: lifetime command {other.Name} not simulated");
        }

        // Delayed blasts at death.
        foreach (var (u, commands) in Entries(blueprint.Root.Table("extensions/health_ext/death_action/commands")))
        {
            if (commands.Table("delayed_action") is not { } delayed) { notes.Add($"{u}: death action without a delayed_action; not simulated"); continue; }
            var blasts = WeaponTuning.Blasts(delayed.Table("action/commands"), $"{u}: death blast", notes);
            if (blasts.Count == 0) { notes.Add($"{u}: death action without a blast"); continue; }
            float delay = delayed.Float("delay") is float d && float.IsFinite(d) && d > 0f ? d : 0f;
            var p = Get(u);
            byUpgrade[u] = p with { DeathBlasts = [.. p.DeathBlasts, .. blasts], DeathBlastDelay = delay };
        }

        // The armour shield: entries requiring a string replace it; the default one's broken actions blast.
        var shields = blueprint.Root.Table("extensions/health_ext/armourshield_info")?.Entries
            .Where(e => e.Type == BafType.Table && e.Name == "armourshield_info").Select(e => e.AsTable()).ToList() ?? [];
        foreach (var s in shields)
        {
            if (Only(s) is not { } u) continue;
            float F(string field) => s.Float(field) is float v && float.IsFinite(v) && v >= 0f ? v : float.NaN;
            float max = F("armourshield_max"), delay = F("armourshield_regen_delay"), rate = F("armourshield_regen_rate");
            if (!float.IsFinite(max) || !float.IsFinite(delay) || !float.IsFinite(rate)) { notes.Add($"{u}: armourshield_info without max, delay or rate; not simulated"); continue; }
            byUpgrade[u] = Get(u) with { Shield = new ShieldInfo(max, delay, rate) };
        }
        var defaultShield = WeaponTuning.Entry(blueprint.Root.Table("extensions/health_ext/armourshield_info"), "armourshield_info", []);
        foreach (var (u, commands) in Entries(defaultShield?.Table("armourshield_broken/commands")))
        {
            var blasts = WeaponTuning.Blasts(commands, $"{u}: armour-break blast", notes);
            if (blasts.Count == 0) continue;
            var p = Get(u);
            byUpgrade[u] = p with { ShieldBreakBlasts = [.. p.ShieldBreakBlasts, .. blasts] };
        }
        if (defaultShield?.Table("armourshield_broken_by_breaker/commands") is not null)
            notes.Add("armourshield_broken_by_breaker: read as the same armour break (its blasts equal armourshield_broken's on the install)");
        return order.Select(u => byUpgrade[u]).ToList();

        PlayerUpgrade Noted(PlayerUpgrade p, string note) { notes.Add(note); return p; }
    }
}
