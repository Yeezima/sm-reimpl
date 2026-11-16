using SmSim;

namespace SmTool;

/// <summary>
/// smtool cancel-config (cancel-editor, owner 2026-11-17): the owner's exported cancel tables. <c>diff FILE</c> lists in plain words what differs from the shipped default (or from <c>--against FILE2</c>);
/// <c>list</c> shows the exports folder; <c>apply FILE [--out config/cancel-table.json]</c> makes the export the release default (a validated copy; no code change, the next build embeds it).
/// No game install, no network.
/// </summary>
public static class CancelConfigCommand
{
    public static int Run(string[] args)
    {
        switch (args)
        {
            case ["list"]:
                Console.WriteLine($"exports folder: {CancelConfigStore.Folder()}");
                foreach (var f in CancelConfigStore.List()) Console.WriteLine("  " + Path.GetFileName(f));
                return 0;
            case ["diff", var file, .. var rest]:
            {
                string? against = rest is ["--against", var other] ? other : null;
                if (rest.Length > 0 && against is null) return Usage();
                if (Load(file) is not { } table) return 1;
                var reference = against is null ? CancelData.Shipped : Load(against);
                if (reference is null) return 1;
                var changes = table.DiffAgainst(reference);
                Console.WriteLine($"{Path.GetFileName(file)} against {(against is null ? "the shipped default" : Path.GetFileName(against))}: {changes.Count} change(s)");
                foreach (var ch in changes.OrderBy(c => c.Scope.ToString(), StringComparer.Ordinal).ThenBy(c => c.Row).ThenBy(c => c.Col))
                    Console.WriteLine("  " + (ch.Cell.State == CancelState.Inherit ? $"{CancelLayout.Number(ch.Row, ch.Col)} [{ch.Scope}]: the override is gone" : table.Describe(ch, reference)));
                return 0;
            }
            case ["apply", var file, .. var rest]:
            {
                string outPath = rest is ["--out", var o] ? o : Path.Combine("config", "cancel-table.json");
                if (rest.Length > 0 && rest is not ["--out", _]) return Usage();
                if (Load(file) is not { } table) return 1;
                File.WriteAllText(outPath, table.ToJson("default"), new System.Text.UTF8Encoding(false));
                Console.WriteLine($"wrote {outPath}: the release default now has {table.DiffFromShipped().Count} change(s) against the old one; rebuild to embed it");
                return 0;
            }
            default: return Usage();
        }
    }

    static CancelData? Load(string file)
    {
        var table = CancelConfigStore.Import(file, out string error);
        if (table is null) Console.Error.WriteLine($"cancel-config: {file}: {error}");
        return table;
    }

    static int Usage()
    {
        Console.Error.WriteLine("usage: smtool cancel-config list | diff FILE [--against FILE2] | apply FILE [--out PATH]");
        return 2;
    }
}
