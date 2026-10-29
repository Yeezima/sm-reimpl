using System.Globalization;
using SmContent;
using SmFormats.Baf;
using SmFormats.Obpk;
using SmSim;

namespace SmTool;

/// <summary>
/// <c>smtool race-parity &lt;game-dir|auto&gt; [--raw]</c> (weapons task A): for each Chaos wargear and its loyalist twin
/// (<see cref="RaceParity.Pairs"/>) the pair exists in the install, then the differences of the simulation's specs before parity (the data as read) and
/// after (<see cref="RaceParity.Apply"/>, which must leave none but the kept names, blueprints), and the Chaos classes' default choices. <c>--raw</c>
/// also lists every value that differs between the two wargear files (numbers and strings, minus the race prefix). Read-only.
/// </summary>
static class ParityCommands
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        if (rest.Contains("--fx-colours")) return FxColours(gameDir, rest.SkipWhile(a => a != "--fx-colours").Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "plasma");
        bool raw = rest.Contains("--raw");
        var before = MpClasses.Load(gameDir, raceParity: false);
        var notes = new List<string>();
        var after = RaceParity.Apply(before, notes);

        // The pairs against the install's wargear files.
        var docs = ReadWargear(gameDir);
        int present = 0, missing = 0;
        Console.WriteLine($"Chaos wargear and their loyalist twins ({RaceParity.Pairs.Count} pairs, {RaceParity.NoTwins.Count} Chaos files with no twin)");
        foreach (var p in RaceParity.Pairs)
        {
            bool c = docs.ContainsKey(p.Chaos), l = docs.ContainsKey(p.Loyalist);
            if (c && l) present++; else missing++;
            Console.WriteLine($"  {p.Kind,-10} {p.Chaos,-38} <- {p.Loyalist,-38} {(c && l ? "both in the install" : "MISSING: " + (c ? "" : p.Chaos + " ") + (l ? "" : p.Loyalist))}");
            if (raw && c && l)
            {
                var diffs = RawDifferences(docs[p.Chaos], docs[p.Loyalist]);
                var cosmetic = diffs.Where(IsCosmetic).ToList();
                var other = diffs.Where(d => !IsCosmetic(d)).ToList();
                Console.WriteLine($"       raw: {diffs.Count} differing value(s): {cosmetic.Count} effect / sound / marker keys (kept Chaos), {other.Count} other");
                foreach (var d in other.Take(60)) Console.WriteLine("       raw other: " + d);
                if (other.Count > 60) Console.WriteLine($"       raw other: ... {other.Count - 60} more");
            }
        }
        foreach (var n in RaceParity.NoTwins)
            Console.WriteLine($"  no twin    {n.Chaos,-38} {(docs.ContainsKey(n.Chaos) ? "in the install" : "NOT in the install")}: {n.Why}");

        // Spec differences before and after, over the pairs the simulation reads.
        Console.WriteLine();
        Console.WriteLine("Spec differences (the simulation's numbers; names and blueprints are kept and not listed):");
        int totalBefore = 0, totalAfter = 0;
        var itemsBefore = RaceParity.Items(before); var itemsAfter = RaceParity.Items(after);
        foreach (var p in RaceParity.Pairs)
        {
            if (!itemsBefore.TryGetValue(p.Chaos, out var a) || !itemsBefore.TryGetValue(p.Loyalist, out var b) || !itemsAfter.TryGetValue(p.Chaos, out var c))
            { Console.WriteLine($"  {p.Kind,-10} {p.Chaos,-36} not read by the simulation (no class offers it, or the combo is not simulated)"); continue; }
            var d0 = RaceParity.Differences(a, b);
            var d1 = RaceParity.Differences(c, b);
            totalBefore += d0.Count; totalAfter += d1.Count;
            Console.WriteLine($"  {p.Kind,-10} {p.Chaos,-36} before {d0.Count,3} after {d1.Count,3}");
            foreach (var d in d0.Take(25)) Console.WriteLine("       " + d);
            if (d0.Count > 25) Console.WriteLine($"       ... {d0.Count - 25} more");
            foreach (var d in d1.Take(10)) Console.WriteLine("       AFTER: " + d);
        }
        Console.WriteLine($"total differences: {totalBefore} before, {totalAfter} after");
        foreach (var n in notes) Console.WriteLine("note: " + n);

        Console.WriteLine();
        Console.WriteLine("Class defaults (Chaos should be the twins of the loyalists'):");
        foreach (var l in after.Loadouts)
            Console.WriteLine($"  {l.Name,-20} primary {Name(after.Weapons, l.Primary),-30} pistol {Name(after.Weapons, l.Pistol),-34} melee {string.Join(",", l.MeleeChoices.Select(m => m.Wargear)),-60} equipment {string.Join(",", l.EquipmentChoices.Select(i => after.Thrown[i].Name))}");
        Console.WriteLine($"config hashes: before parity {Hash(before):X16}, after {Hash(after):X16}");
        Console.WriteLine($"pairs present {present}, missing {missing}");
        return missing == 0 && totalAfter == 0 ? 0 : 1;
    }

    /// <summary>
    /// <c>--fx-colours [word]</c>: for each wargear whose name has the word (default "plasma"), every effect it plays (its own commands, its
    /// projectiles' trails and detonations, the impact containers) with the colour keys of its elements (FrameColour, a streamer's Colour) as RGB and a hue name.
    /// </summary>
    static int FxColours(string gameDir, string word)
    {
        var catalogue = EffectCatalogue.Load(gameDir);
        using var fx = FxLibrary.Load(gameDir);
        foreach (var (name, weapon) in catalogue.Weapons.Where(kv => kv.Key.Contains(word, StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key.Replace("csm_", "sm_"), StringComparer.Ordinal).ThenBy(kv => kv.Key))
        {
            var plays = new List<(string Where, string Effect)>();
            foreach (var b in weapon.Bindings.Where(b => b.Play is not null)) plays.Add(($"{b.Ability}/{b.Event}{(b.IsDefault ? "" : " [" + string.Join("+", b.RequiredUpgrades) + "]")}", b.Play!.Name));
            foreach (var proj in weapon.Projectiles)
                foreach (var b in proj.Bindings.Where(b => b.Play is not null)) plays.Add(($"projectile {proj.Blueprint.Split('\\')[^1]}/{b.Event}", b.Play!.Name));
            var containers = weapon.Bindings.Concat(weapon.Projectiles.SelectMany(p => p.Bindings)).Where(b => b.Material is not null).Select(b => b.Material!).DistinctBy(m => m.Container).ToList();
            foreach (var trigger in containers)
                if (catalogue.Container(trigger) is { } table)
                    foreach (var (surface, classes) in table.Effects)
                        foreach (var (cls, list) in classes)
                            foreach (var play in list) plays.Add(($"impact {trigger.ContainerName}/{surface}/{cls}", play.Name));
            Console.WriteLine($"=== {name}");
            var systems = fx.Systems(plays.Select(p => p.Effect).Distinct());
            foreach (var group in plays.GroupBy(p => p.Effect).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                if (!systems.TryGetValue(group.Key, out var system)) { Console.WriteLine($"  {group.Key}: not in master_fx"); continue; }
                var summary = ColourSummary(system);
                var where = group.Select(g => g.Where).Distinct().ToList();
                Console.WriteLine($"  {group.Key,-44} {summary}   ({string.Join("; ", where.Take(3))}{(where.Count > 3 ? "; ..." : "")})");
            }
        }
        return 0;
    }

    /// <summary>Per colour-carrying element of the system the brightest key's RGB (with alpha above 0) and its hue name, summarised as counts per hue name.</summary>
    static string ColourSummary(SmFormats.Fx.FxSystem system)
    {
        var hues = new Dictionary<string, int>();
        var samples = new List<string>();
        foreach (var e in system.Elements)
        {
            var curve = e switch { SmFormats.Fx.FxRing r => r.Colour, SmFormats.Fx.FxBeam b => b.Colour, SmFormats.Fx.FxStreamer s => s.Colour, SmFormats.Fx.FxLight l => l.Colour, SmFormats.Fx.FxMesh m => m.Colour, _ => SmFormats.Fx.FxColourCurve.Empty };
            if (curve.IsEmpty) continue;
            var key = curve.Keys.Where(k => k.Colour.A > 0).OrderByDescending(k => Math.Max(k.Colour.R, Math.Max(k.Colour.G, k.Colour.B)) * (k.Colour.A + 1)).FirstOrDefault();
            if (key.Colour.A == 0) continue;
            string hue = HueName(key.Colour.R, key.Colour.G, key.Colour.B);
            hues[hue] = hues.GetValueOrDefault(hue) + 1;
            if (samples.Count < 4) samples.Add($"{e.Kind}({key.Colour.R},{key.Colour.G},{key.Colour.B}) {hue}");
        }
        return hues.Count == 0 ? "no colour keys" : string.Join(", ", hues.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}")) + "  e.g. " + string.Join(" ", samples);
    }

    /// <summary>A hue name for an RGB colour (0..255): black, white or grey when unsaturated, else red, orange, yellow, green, cyan, blue, purple or magenta by hue angle.</summary>
    internal static string HueName(int r, int g, int b)
    {
        float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        if (max < 40) return "black";
        if ((max - min) / max < 0.18f) return max > 200 ? "white" : "grey";
        float h = max == r ? 60f * (((g - b) / (max - min)) % 6f) : max == g ? 60f * (((b - r) / (max - min)) + 2f) : 60f * (((r - g) / (max - min)) + 4f);
        if (h < 0) h += 360f;
        return h switch { < 15 or >= 345 => "red", < 40 => "orange", < 70 => "yellow", < 165 => "green", < 200 => "cyan", < 255 => "blue", < 290 => "purple", _ => "magenta" };
    }

    static string Name(IReadOnlyList<WeaponSpec> w, int i) => i >= 0 && i < w.Count ? w[i].Name : "-";

    static ulong Hash(MpClasses m)
    {
        var config = new SimConfig { Weapons = m.Weapons, Classes = m.Loadouts, Thrown = m.Thrown, JumpPacks = m.JumpPacks, Evades = m.Evades };
        return config.Hash();
    }

    static Dictionary<string, BafDocument> ReadWargear(string gameDir)
    {
        var docs = new Dictionary<string, BafDocument>(StringComparer.OrdinalIgnoreCase);
        using var archive = ObpkArchive.Open(Path.Combine(gameDir, GameplayTuning.WargearPackage));
        var names = RaceParity.Pairs.SelectMany(p => new[] { p.Chaos, p.Loyalist }).Concat(RaceParity.NoTwins.Select(n => n.Chaos)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        archive.ReadEntries(e => archive.GroupName(e) == "attr_pc" && names.Contains(e.FileName), (e, d) => docs[e.FileName] = BafDocument.Read(d));
        return docs;
    }

    /// <summary>Every leaf of the file as path → text (siblings of one name numbered), the race prefix of strings removed.</summary>
    static Dictionary<string, string> Leaves(BafDocument doc)
    {
        var map = new Dictionary<string, string>();
        void Walk(BafTable t, string path, int depth)
        {
            if (depth > 40) return;
            var counts = new Dictionary<string, int>();
            foreach (var e in t.Entries)
            {
                int n = counts.GetValueOrDefault(e.Name); counts[e.Name] = n + 1;
                string p = $"{path}/{e.Name}{(n > 0 ? "#" + n : "")}";
                switch (e.Type)
                {
                    case BafType.Table: Walk(e.AsTable(), p, depth + 1); break;
                    case BafType.String: map[p] = e.AsString(); break;
                    case BafType.Float: map[p] = e.AsFloat().ToString("R", CultureInfo.InvariantCulture); break;
                    case BafType.Bool: map[p] = e.AsBool().ToString(); break;
                    default: map[p] = e.Raw.ToString(CultureInfo.InvariantCulture); break;
                }
            }
        }
        Walk(doc.Root, "", 0);
        return map;
    }

    /// <summary>A raw difference under an effect, sound or other presentation key (these stay Chaos).</summary>
    static bool IsCosmetic(string diff)
    {
        string path = diff[..Math.Max(0, diff.IndexOf(": ", StringComparison.Ordinal))];
        return CosmeticParts.Any(c => path.Contains(c, StringComparison.OrdinalIgnoreCase));
    }

    static readonly string[] CosmeticParts = ["post_sound_event", "play_fx", "_audio", "audio/", "fx_name", "fx_package", "/fx", "_fx/", "sound", "decal", "tracer", "eye", "marker", "ui_data", "display"];

    static List<string> RawDifferences(BafDocument chaos, BafDocument loyal)
    {
        var a = Leaves(chaos); var b = Leaves(loyal);
        static string Strip(string s) => s.Replace("csm_", "sm_", StringComparison.OrdinalIgnoreCase).Replace("race_chaos", "race_marine", StringComparison.OrdinalIgnoreCase).Replace("_chaos", "", StringComparison.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var k in a.Keys.Union(b.Keys).Order(StringComparer.Ordinal))
        {
            a.TryGetValue(k, out var x); b.TryGetValue(k, out var y);
            if (x is null) list.Add($"{k}: only in the loyalist file ({y})");
            else if (y is null) list.Add($"{k}: only in the Chaos file ({x})");
            else if (Strip(x) != Strip(y)) list.Add($"{k}: Chaos {x} | loyalist {y}");
        }
        return list;
    }
}
