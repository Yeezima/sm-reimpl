using System.Globalization;
using SmSim.Extras.Warfront.Progression;

namespace SmTool;

/// <summary>
/// <c>smtool warfront-tuning [--levels N] [--humans H]</c> (Warfront W7): the per-level Ork scaling table (HP and damage multipliers) and, for a few representative Orks, the scaled health, damage per hit,
/// time to kill against the bolter and the unbraced heavy bolter (no cards, a balanced build, an all-damage build) and hits to down a 100 + 100 Marine. Weapon numbers are the spec's extracted rows and the Ork
/// numbers Musou's defaults: nothing is read from the install. Times are a lower bound (100 % accuracy, no reload).
/// </summary>
static class WarfrontTuningCommand
{
    public static int Run(string[] rest)
    {
        int levels = 20, humans = 1;
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == "--levels" && i + 1 < rest.Length) levels = int.Parse(rest[++i], CultureInfo.InvariantCulture);
            else if (rest[i] == "--humans" && i + 1 < rest.Length) humans = int.Parse(rest[++i], CultureInfo.InvariantCulture);
            else { Console.Error.WriteLine($"warfront-tuning: unknown argument \"{rest[i]}\" (usage: warfront-tuning [--levels N] [--humans H])"); return 2; }
        }
        var inv = CultureInfo.InvariantCulture;
        Console.WriteLine($"warfront-tuning: levels 1..{levels}, {humans} human(s); Ork HP x (1 + {WarfrontProgressionRules.OrkHealthPerLevel:0.##}(L-1)) x (1 + {WarfrontProgressionRules.OrkHealthPerExtraHuman:0.##}(humans-1)), damage x (1 + {WarfrontProgressionRules.OrkDamagePerLevel:0.##}(L-1)); estimates.");
        Console.WriteLine("Weapons: spec rows (bolter 22 / 0.133 s, heavy bolter 35 / 0.233 s); TTK in s, 100 % accuracy, no reload; balanced = damage, attack speed, max HP in turn; hits-to-down on 100 health + 100 armour.");
        Console.WriteLine();
        Console.WriteLine("Level  HP x (1 human)  HP x (4 humans)  Dmg x");
        var rows = WarfrontTuningTable.Build(levels, humans);
        var rows4 = WarfrontTuningTable.Build(levels, 4);
        for (int i = 0; i < rows.Count; i++) Console.WriteLine(string.Create(inv, $"{rows[i].Level,5}  {WarfrontTuningTable.Build(levels, 1)[i].HpMult,14:0.00}  {rows4[i].HpMult,15:0.00}  {rows[i].DmgMult,5:0.00}"));
        foreach (var ork in WarfrontTuningTable.Orks)
        {
            Console.WriteLine();
            Console.WriteLine($"{ork.Name}: level | HP | dmg/hit | bolter TTK none / balanced / all-damage | heavy TTK none | hits to down none / balanced || the same Ork with 4 humans (HP x1.6 on top)");
            foreach (var r in rows)
            {
                var o = r.Orks.First(x => x.Type == ork.Type);
                var o4 = rows4[r.Level - 1].Orks.First(x => x.Type == ork.Type);
                Console.WriteLine(string.Create(inv, $"  {r.Level,3} | {o.Hp,7:0.#} | {o.Hit,5:0.#} | {o.BolterTtkNone,5:0.00} / {o.BolterTtkBalanced,5:0.00} / {o.BolterTtkDamage,5:0.00} | {o.HeavyTtkNone,5:0.00} | {o.HitsToDownNone,3} / {o.HitsToDownBalanced,3} || 4 humans: HP {o4.Hp,7:0.#}, bolter TTK {o4.BolterTtkNone,5:0.00} / {o4.BolterTtkBalanced,5:0.00} / {o4.BolterTtkDamage,5:0.00}, heavy {o4.HeavyTtkNone,5:0.00}"));
            }
        }
        return 0;
    }
}
