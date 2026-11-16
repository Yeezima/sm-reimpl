using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool sound-config</c> (sound/mix-expand): the F7 mix editor's files, for the lead. <c>diff &lt;file&gt; [--base &lt;file&gt;]</c> lists in plain words what a sound-mix file changes against the
/// game's own mix (or against another file); <c>coverage [--game dir] [--map id] [--list]</c> counts the install's event names per editor group and lists the "Other" ones (read-only on the
/// install). Nothing is written.
/// </summary>
static class SoundConfigCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return Usage();
        string[] rest = args[1..];
        switch (args[0])
        {
            case "diff" or "show" when rest.Length >= 1 && !rest[0].StartsWith("--"): return Diff(rest);
            case "coverage": return Coverage(rest);
            default: return Usage();
        }
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: smtool sound-config diff <file> [--base <file>]   (what a sound-mix file changes, in plain words)\n       smtool sound-config coverage [--game <dir>] [--map <id>] [--list]   (event names per editor group)");
        return 2;
    }

    static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static bool Read(string path, out SoundMixConfig config, out SoundTuning tuning)
    {
        config = new SoundMixConfig();
        tuning = new SoundTuning();
        if (!File.Exists(path)) { Console.Error.WriteLine($"no such file: {path}"); return false; }
        if (!SoundMixFile.TryApply(File.ReadAllText(path), config, tuning)) { Console.Error.WriteLine($"not a sound-mix file: {path}"); return false; }
        return true;
    }

    static int Diff(string[] rest)
    {
        if (!Read(rest[0], out var after, out var afterTuning)) return 1;
        var before = new SoundMixConfig();
        var beforeTuning = new SoundTuning();
        string baseName = "the game's own mix";
        if (Opt(rest, "--base") is { } b)
        {
            if (!Read(b, out before, out beforeTuning)) return 1;
            baseName = Path.GetFileName(b);
        }
        var lines = SoundMixFile.Describe(before, beforeTuning, after, afterTuning);
        Console.WriteLine($"{Path.GetFileName(rest[0])} against {baseName}: {lines.Count} change{(lines.Count == 1 ? "" : "s")}");
        foreach (var l in lines) Console.WriteLine("  " + l);
        return 0;
    }

    static int Coverage(string[] rest)
    {
        List<string> names;
        try { names = AudioCommands.ClientEventNames(rest); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        { Console.Error.WriteLine("could not read the install: " + ex.Message); return 1; }
        var counts = SoundMixCatalog.Coverage(names);
        Console.WriteLine($"{names.Count} event names (the triggers', the match and menu events, the map's emitters and regions)");
        foreach (var g in SoundMixCatalog.Groups) Console.WriteLine($"  {SoundMixCatalog.GroupLabel(g),-22} {counts[g],4}");
        int total = counts.Values.Sum();
        Console.WriteLine($"  {"total",-22} {total,4}  ({(total == names.Count ? "none lost" : "MISMATCH: " + (names.Count - total) + " lost")}); Other holds {counts[SoundMixGroup.Other]} ({100.0 * counts[SoundMixGroup.Other] / Math.Max(1, names.Count):0.#} %)");
        foreach (var n in names.Where(n => SoundMixCatalog.Classify(n).Group == SoundMixGroup.Other)) Console.WriteLine("  Other: " + n);
        if (rest.Contains("--list"))
            foreach (var g in SoundMixCatalog.Groups)
                foreach (var n in names.Where(n => SoundMixCatalog.Classify(n).Group == g)) Console.WriteLine($"  [{SoundMixCatalog.GroupLabel(g)}] {SoundMixCatalog.Classify(n).Label}   <- {n}");
        return 0;
    }
}
