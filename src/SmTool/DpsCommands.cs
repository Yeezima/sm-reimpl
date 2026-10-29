using System.Globalization;
using SmContent;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool weapon-dps &lt;game-dir|auto&gt; [--range m] [--all] [--data]</c>: body-shot damage per second of the bolters and bolt pistols (every
/// perk variant): burst (trigger held, cooldowns in whole ticks) and sustained (clip and reload) on the health scale and against a full
/// marine (shield first), at a distance (default <see cref="OwnerBalance.MediumRange"/>); then the owner's overrides
/// (<see cref="OwnerBalance"/>: data damage, new damage, DPS before and after against the reference). <c>--all</c>: every hit-scan weapon; <c>--data</c>: the data's values (no overrides). Read-only.
/// </summary>
static class DpsCommands
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        float range = rest.SkipWhile(a => a != "--range").Skip(1).FirstOrDefault() is { } r && float.TryParse(r, NumberStyles.Float, Inv, out float v) ? v : OwnerBalance.MediumRange;
        bool all = rest.Contains("--all");
        bool data = rest.Contains("--data");
        var tuning = GameplayTuning.Load(gameDir, ownerBalance: !data);
        var c = tuning.Config;
        float tick = c.TickSeconds;
        Console.WriteLine(data ? "the data's values (--data: no owner's overrides)" : "with the owner's overrides (OwnerBalance; --data: the data's values)");
        Console.WriteLine($"config hash {c.Hash():X16}; tick {tick:0.#####} s; target: health {c.HealthMax} + shield {c.ShieldMax}; range {range} m");
        Console.WriteLine("DPS on the health scale (damage x fall-off x body multiplier) and against a full marine (shield first at the shield multiplier).");
        Header();
        foreach (var w in c.Weapons)
        {
            if (w.Delivery != DeliveryKind.Raycast) continue;
            if (!all && !(w.Name.Contains("bolter", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("bolt_pistol", StringComparison.OrdinalIgnoreCase))) continue;
            if (!all && (w.Name.Contains("heavy", StringComparison.OrdinalIgnoreCase) || w.Name.Contains("storm", StringComparison.OrdinalIgnoreCase))) continue;
            Line(OwnerBalance.Measure(w, range, tick, c.ShieldMax, c.HealthMax));
            foreach (var variant in w.Variants)
                Line(OwnerBalance.Measure(variant.Spec, range, tick, c.ShieldMax, c.HealthMax, string.Join("+", variant.Upgrades)));
        }
        var rows = tuning.Classes?.OwnerRows ?? [];
        Console.WriteLine();
        Console.WriteLine($"Owner's overrides (OwnerBalance; at {OwnerBalance.MediumRange} m): {rows.Count} rule(s) applied");
        Console.WriteLine($"{"weapon",-28} {"reference",-28} {"ratio",5} {"damage",13} {"health-scale burst before→after",34} {"sustained before→after",36}");
        foreach (var row in rows)
        {
            var before = OwnerBalance.Measure(row.Data, OwnerBalance.MediumRange, tick, c.ShieldMax, c.HealthMax);
            var after = OwnerBalance.Measure(row.Result, OwnerBalance.MediumRange, tick, c.ShieldMax, c.HealthMax);
            var reference = OwnerBalance.Measure(row.Reference, OwnerBalance.MediumRange, tick, c.ShieldMax, c.HealthMax);
            Console.WriteLine($"{row.Data.Name + (row.Variant == "default" ? "" : " + " + row.Variant.Replace("mp_perk_weap_", "")),-28} {row.Reference.Name,-28} {row.Ratio,5:0.00} {row.Data.Damage,6:0.#}→{row.Result.Damage,-6:0.#} " +
                $"{before.Burst,10:0.0}→{after.Burst,-7:0.0} ({row.Ratio * reference.Burst,6:0.0}) {after.Burst / reference.Burst,6:0.000}x " +
                $"{before.Sustained,10:0.0}→{after.Sustained,-7:0.0} ({row.Ratio * reference.Sustained,6:0.0}) {after.Sustained / reference.Sustained,6:0.000}x");
            Console.WriteLine($"{"",-28} target, against a full marine: burst {before.BurstVsMarine:0.0}→{after.BurstVsMarine:0.0} (target {row.Ratio * reference.BurstVsMarine:0.0}, {after.BurstVsMarine / reference.BurstVsMarine:0.000}x), " +
                $"sustained {before.SustainedVsMarine:0.0}→{after.SustainedVsMarine:0.0} (target {row.Ratio * reference.SustainedVsMarine:0.0}, {after.SustainedVsMarine / reference.SustainedVsMarine:0.000}x); {row.Decision}");
        }
        return 0;
    }

    static void Header() =>
        Console.WriteLine($"{"weapon",-34} {"variant",-36} {"dmg",6} {"cd s",6} {"rnd",3} {"clip",4} {"reload",6} {"fall",5} {"body",5} {"shield",6} {"burst",7} {"sust",7} {"burst/m",8} {"sust/m",7}");

    static void Line(WeaponDps d) =>
        Console.WriteLine($"{d.Weapon,-34} {d.Variant,-36} {d.Damage,6:0.##} {d.Cooldown,6:0.####} {d.Rounds,3} {d.Clip,4} {d.Reload,6:0.###} {d.FallOff,5:0.##} {d.BodyMultiplier,5:0.###} {d.ShieldBodyMultiplier,6:0.###} " +
            $"{d.Burst,7:0.0} {d.Sustained,7:0.0} {d.BurstVsMarine,8:0.0} {d.SustainedVsMarine,7:0.0}");
}
