using SmContent;

namespace SmTool;

/// <summary>
/// <c>smtool ork-blood [auto]</c> (gore-original, Musou Mode): what the original binds to an Ork's death, read from the install (read-only): the Ork blueprint's dismemberment
/// table (per type and zone: chance and point), each point's effects, and each multiplayer weapon's dismemberment types (shot, light strikes, heavy strikes, charge).
/// </summary>
static class OrkBloodCommand
{
    public static int Run(string gameDir)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate(null, RepoRoot());
        var data = OrkBlood.Load(gameDir);
        foreach (var n in data.Notes) Console.WriteLine($"note: {n}");
        Console.WriteLine($"table from {data.Source}: {data.Types.Count} types");
        foreach (var t in data.Types.Values)
            Console.WriteLine($"  {t.Name,-21} " + string.Join(" ", t.Zones.Select((z, i) => $"{OrkBlood.Zones[i]}={z.Chance:0}/{(z.Point.Length > 0 ? z.Point : "-")}")) + $" | nothing_left={t.NothingLeft}");
        var used = data.Types.Values.SelectMany(t => t.Zones.Select(z => z.Point).Append(t.NothingLeft)).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
        Console.WriteLine($"\npoints used by the table ({data.Points.Count} in {OrkBlood.PointPackage}):");
        foreach (var p in used)
            Console.WriteLine($"  {p,-20} " + string.Join(", ", data.PointEffects(p).Select(f => $"{f.Name} x{f.Scale:0.##} len {f.Length:0.##} @{f.Marker} {f.Direction}")));
        Console.WriteLine($"\nweapons ({data.Weapons.Count}): shot | melee abilities");
        foreach (var (name, w) in data.Weapons.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (w.Ranged.Length > 0 || w.Melee.Count > 0)
                Console.WriteLine($"  {name,-40} {Or(w.Ranged)} | " + string.Join(" ", w.Melee.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key.Replace("ability_", "")}={kv.Value}")));
        return data.Types.Count > 0 ? 0 : 1;
    }

    static string Or(string s) => s.Length > 0 ? s : "-";

    static string? RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SmReimpl.slnx"))) return d.FullName;
        return null;
    }
}
