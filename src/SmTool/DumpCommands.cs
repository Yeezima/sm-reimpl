using System.Text.RegularExpressions;
using SmContent;
using SmFormats.Baf;
using SmFormats.Locale;
using SmFormats.Obpk;
using SmTool.Dump;

namespace SmTool;

/// <summary>
/// Read-only dumps for data research (output to the console only; nothing is written into the install or the repo):
/// <c>baf-dump</c> prints BAF attribute entries of an archive as trees, <c>locale</c> looks up the localised text.
/// </summary>
static class DumpCommands
{
    public static int Run(string command, string[] args)
    {
        try
        {
            return command switch
            {
                "baf-dump" => BafDumpCommand(args),
                "locale" => LocaleCommand(args),
                "customization-dump" => CustomizationCommand(args),
                _ => -1,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or DirectoryNotFoundException or ArgumentException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    public const string Usage = """
        baf-dump <archive.oppc|folder> [--package <regex>] [--entry <regex>] [--list] [--grep <regex>] [--depth <n>] [--under <a/b>] [--locale] [--game <dir>]
                                                    print BAF attribute files of an archive as trees (--list: names only;
                                                    --grep: only entries whose name or string value matches, with their table path;
                                                    --locale annotates text ids with the English text; --under starts at a sub-table)
        customization-dump [--full] [--language english] [--game <dir>]
                                                    print the customizer / creator catalogue (counts and classes; --full adds pieces, sets, presets,
                                                    badges, palette and perks)
        locale <id|lo-hi|regex> [...] [--language english] [--game <dir>]
                                                    look up localised text by id (numbers), an id range (e.g. 104200-104300) or search it (regex)
        """;

    static int BafDumpCommand(string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--")) { Console.Error.WriteLine(Usage); return 2; }
        string gameDir = Locate(Opt(args, "--game"));
        string archivePath = Path.IsPathRooted(args[0]) || File.Exists(args[0]) ? args[0] : Path.Combine(gameDir, args[0]);
        var filter = Opt(args, "--entry") is { } pattern ? new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;
        bool list = args.Contains("--list");
        int depth = int.TryParse(Opt(args, "--depth"), out int d) ? d : 32;
        string? under = Opt(args, "--under");
        var grep = Opt(args, "--grep") is { } g ? new Regex(g, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;
        LocaleTable? locale = args.Contains("--locale") ? OpenLocale(gameDir, "english") : null;
        int shown = 0;
        // A folder (e.g. data/packages) scans every .oppc in it (--package <regex> narrows by file name); headers then name the archive.
        bool folder = Directory.Exists(archivePath);
        var packageFilter = Opt(args, "--package") is { } pk ? new Regex(pk, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) : null;
        var archives = folder
            ? Directory.GetFiles(archivePath, "*.oppc").Where(f => packageFilter is null || packageFilter.IsMatch(Path.GetFileNameWithoutExtension(f))).Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [archivePath];
        foreach (var path in archives)
        {
            string label = folder ? Path.GetFileNameWithoutExtension(path) + "/" : "";
            try
            {
                using var archive = ObpkArchive.Open(path);
                archive.ReadEntries(e => filter is null || filter.IsMatch(e.FileName), (e, data) =>
                {
                    if (!BafDocument.HasMagic(data)) return;
                    if (list) { Console.WriteLine(label + e.FileName); shown++; return; }
                    try
                    {
                        var doc = BafDocument.Read(data);
                        if (grep is not null)
                        {
                            var hits = BafDump.Grep(doc, grep).ToList();
                            if (hits.Count == 0) return;
                            Console.WriteLine($"=== {label}{e.FileName}");
                            foreach (var h in hits) Console.WriteLine("  " + h);
                            shown++;
                            return;
                        }
                        Console.WriteLine($"=== {label}{e.FileName}");
                        Console.Write(BafDump.Format(doc, locale, depth, under)); shown++;
                    }
                    catch (InvalidDataException ex) { Console.WriteLine($"(unreadable: {ex.Message})"); }
                });
            }
            catch (Exception ex) when (folder && ex is IOException or InvalidDataException or NotSupportedException)
            {
                Console.Error.WriteLine($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        Console.Error.WriteLine($"{shown} BAF entries");
        return 0;
    }

    static int CustomizationCommand(string[] args)
    {
        string gameDir = Locate(Opt(args, "--game"));
        var notes = new List<string>();
        var catalog = CustomizationCatalog.Load(gameDir, notes, language: Opt(args, "--language") ?? "english");
        Console.Write(CustomizationDump.Format(catalog, args.Contains("--full")));
        foreach (var n in notes.Distinct()) Console.Error.WriteLine("note: " + n);
        return 0;
    }

    static int LocaleCommand(string[] args)
    {
        var queries = args.Where((a, i) => !a.StartsWith("--") && (i == 0 || !(args[i - 1] is "--language" or "--game"))).ToList();
        if (queries.Count == 0) { Console.Error.WriteLine(Usage); return 2; }
        var locale = OpenLocale(Locate(Opt(args, "--game")), Opt(args, "--language") ?? "english");
        foreach (var q in queries)
        {
            if (int.TryParse(q, out int id)) Console.WriteLine($"{id}\t{locale.Get(id) ?? "(not found)"}");
            else if (Regex.Match(q, @"^(\d+)-(\d+)$") is { Success: true } range)
            {
                int lo = int.Parse(range.Groups[1].Value), hi = int.Parse(range.Groups[2].Value);
                foreach (var kv in locale.Between(lo, hi)) Console.WriteLine($"{kv.Key}\t{kv.Value}");
            }
            else
            {
                var re = new Regex(q, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                foreach (var kv in locale.Entries.Where(kv => re.IsMatch(kv.Value))) Console.WriteLine($"{kv.Key}\t{kv.Value}");
            }
        }
        return 0;
    }

    static LocaleTable OpenLocale(string gameDir, string language) => LocaleTable.Open(Path.Combine(gameDir, "data", "locale", language, "ucs.pc"));

    static string Locate(string? explicitDir) => GameInstall.Locate(explicitDir, FindRepoRoot());

    static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SmReimpl.slnx"))) return dir.FullName;
        return null;
    }

    static string? Opt(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
