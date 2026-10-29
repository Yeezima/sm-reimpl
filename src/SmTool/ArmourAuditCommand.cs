using SmContent;
using SmContent.Customization;

namespace SmTool;

/// <summary>
/// <c>smtool armour-audit &lt;game-dir|auto&gt; [--all] [--packages]</c> (fix/armour-pieces): every class's default look, every armour set worn whole and every
/// offered piece of both races, resolved and loaded as the client does (<see cref="ArmourAudit"/>); prints each case with a problem (--all: every case) and a
/// summary. --packages lists every package with its surface counts. Exit 0 always (it is a report); read-only.
/// </summary>
static class ArmourAuditCommand
{
    public static int Run(string gameDir, string[] rest)
    {
        if (gameDir == "auto") gameDir = GameInstall.Locate();
        var notes = new List<string>();
        var catalog = CustomizationCatalog.Load(gameDir, notes);
        foreach (var n in notes.Where(n => n.Contains("armour", StringComparison.OrdinalIgnoreCase))) Console.WriteLine("catalogue note: " + n);
        var audit = ArmourAudit.ForInstall(gameDir);
        var cases = audit.Run(catalog);
        bool all = rest.Contains("--all");
        foreach (var c in cases.Where(c => all || c.Problems.Count > 0))
        {
            Console.WriteLine($"{(c.Problems.Count == 0 ? "ok  " : "FAIL")} {c.ClassKey} | {c.Subject}");
            foreach (var p in c.Problems) Console.WriteLine("       " + p);
            if (all) foreach (var g in c.Gaps) Console.WriteLine("       gap: " + g);
        }
        if (rest.Contains("--packages"))
            foreach (var p in audit.Packages.Values.OrderBy(p => p.Package, StringComparer.Ordinal))
                Console.WriteLine($"package {p.Package}: {p.Drawn}/{p.Surfaces} surfaces drawn, {p.WithoutDiffuse} without diffuse{(p.Problem is null ? "" : " - " + p.Problem)}"
                    + (p.FreeHanging.Count > 0 ? $"; free-hanging: {string.Join(", ", p.FreeHanging)}" : ""));
        foreach (var race in new[] { CustomizationCatalog.SpaceMarine, CustomizationCatalog.Chaos })
        {
            var rc = cases.Where(c => c.Race == race).ToList();
            Console.WriteLine($"{race}: {rc.Count} cases, {rc.Count(c => c.Problems.Count > 0)} with problems, {rc.Count(c => c.Gaps.Count > 0)} with explained gaps");
        }
        var badPackages = audit.Packages.Values.Where(p => p.Problem is not null).ToList();
        Console.WriteLine($"packages: {audit.Packages.Count} checked, {badPackages.Count} failing, {audit.Packages.Values.Sum(p => p.WithoutDiffuse)} drawn surfaces without a diffuse");
        foreach (var p in badPackages) Console.WriteLine($"  {p.Package}: {p.Problem}");
        foreach (var p in audit.Packages.Values.Where(p => p.FreeHanging.Count > 0).OrderBy(p => p.Package, StringComparer.Ordinal))
            Console.WriteLine($"  free-hanging cloth drawn rigid: {p.Package}: {string.Join(", ", p.FreeHanging)}");
        foreach (var g in cases.SelectMany(c => c.Gaps).Select(g => g[(g.IndexOf(':') + 2)..]).Distinct().Order(StringComparer.Ordinal)) Console.WriteLine($"  explained gap: {g}");
        return 0;
    }
}
