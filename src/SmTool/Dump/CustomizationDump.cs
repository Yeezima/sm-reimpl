using System.Text;
using SmContent;

namespace SmTool.Dump;

/// <summary>Prints a <see cref="CustomizationCatalog"/> as text (counts first; <c>full</c> adds every list). Console only; nothing is written.</summary>
public static class CustomizationDump
{
    public static string Format(CustomizationCatalog c, bool full)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== counts");
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            var r = c.Race(race)!;
            sb.AppendLine($"{race}: sets {r.CustomizableSets.Count()} customizable + {r.PredefinedSets.Count()} predefined; decals {r.Decals.Count}; presets {r.Presets.Count}; default set {r.DefaultSet?.Name ?? "-"}; default scheme {r.DefaultScheme}");
            sb.AppendLine("  pieces: " + string.Join(", ", CustomizationCatalog.ArmourSlots.Select(s => $"{s} {r.PiecesFor(s).Count}")));
        }
        sb.AppendLine($"palette: {c.Palette.Count} in {c.PaletteGroups.Count} groups ({string.Join(", ", c.PaletteGroups.Select(g => $"{g.Name} {g.Colours.Count}"))})");
        var u = c.Unlocks;
        sb.AppendLine($"unlocks: customizer {u.Customizer}, appearance {u.CustomAppearance}, second perk slot {u.SecondPerkSlot}, loadout slots [{string.Join(", ", u.LoadoutSlots)}], prestige {u.Prestige}, devastator {u.DevastatorClass}, assault {u.AssaultClass}");
        sb.AppendLine("cameras: " + string.Join("; ", c.Cameras.Select(k => $"{k.Tab} d={k.Distance} h={k.HorizontalOffset} v={k.VerticalOffset}")));
        sb.AppendLine($"perks: {c.Perks.All.Count} ({c.Perks.All.Count(p => p.Kind == SmSim.PerkKind.Class)} class, {c.Perks.All.Count(p => p.Kind == SmSim.PerkKind.Weapon)} weapon)");
        sb.AppendLine("== classes");
        foreach (var o in c.Perks.Classes)
        {
            var cc = c.Class(o.Key);
            sb.AppendLine($"{o.Key} \"{o.DisplayName}\" role {o.Role} level {cc?.Level}: perks {o.Perks.Count} (default {o.DefaultPerk?.Name ?? "-"}), default set {cc?.DefaultArmourSet}");
            foreach (var s in new[] { o.Primary, o.Pistol, o.Melee, o.Equipment })
                sb.AppendLine($"  {s.Slot}{(s.Selectable ? " (choice)" : " (fixed)")}: " + string.Join(", ", s.Items.Select(i => $"{i.DisplayName} [{i.Name} L{i.Level}{(i.Simulated ? "" : " NOT SIMULATED")}{(i.RequiredPerk is null ? "" : " needs " + i.RequiredPerk)}]")));
            foreach (var ov in cc?.Overrides ?? [])
                sb.AppendLine($"  override {ov.Piece.Slot}: {ov.Piece.Name} [{string.Join("+", ov.Piece.Packages)}]{(ov.RequiredWargear is null ? "" : " when " + ov.RequiredWargear)}");
        }
        if (!full) return sb.ToString();
        sb.AppendLine("== perks");
        foreach (var p in c.Perks.All)
            sb.AppendLine($"{p.Name} | {p.Kind} L{p.Level} | {p.DisplayName} | upgrade {p.UpgradeString ?? "-"} | {p.ChallengeText ?? "-"}");
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            var r = c.Race(race)!;
            sb.AppendLine($"== {race} sets");
            foreach (var s in r.Sets) sb.AppendLine($"{s.Name} | id '{s.SetId}' | {(s.Customizable ? "customizable" : "predefined")} | {s.DisplayName} | scheme {s.Scheme ?? "-"}");
            sb.AppendLine($"== {race} pieces");
            foreach (var slot in CustomizationCatalog.ArmourSlots)
                foreach (var p in r.PiecesFor(slot))
                    sb.AppendLine($"{slot} | {p.Name} | {p.DisplayName} | {string.Join("+", p.Packages)} | helmet {p.HelmetOverride} | sets {string.Join(",", p.SetNames)} | {p.ChallengeText ?? "-"}");
            sb.AppendLine($"== {race} presets");
            foreach (var p in r.Presets) sb.AppendLine($"{p.Name} | {p.DisplayName}");
            sb.AppendLine($"== {race} decals");
            foreach (var d in r.Decals) sb.AppendLine($"{d.Name} | {d.DisplayName} | family {d.Family} | variant {(d.Variant.Length == 0 ? "-" : d.Variant)}{(d.IsBlank ? " | blank" : "")}");
        }
        sb.AppendLine("== palette");
        foreach (var g in c.PaletteGroups)
            foreach (var p in g.Colours) sb.AppendLine($"{g.Name} | {p.Name} | {p.DisplayName} | {p.Diffuse[0]:0.###} {p.Diffuse[1]:0.###} {p.Diffuse[2]:0.###}");
        return sb.ToString();
    }
}
