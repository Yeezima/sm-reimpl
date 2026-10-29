using System.Globalization;
using System.Text;
using SmFormats.Baf;
using SmFormats.Locale;

namespace SmTool.Dump;

/// <summary>
/// Prints a BAF attribute document as an indented tree (<c>name = value</c>; tables as <c>name {</c>). Integer entries whose name
/// suggests a text id (name, description, text, title, label, caption) are annotated with the localised text when a table is given.
/// </summary>
public static class BafDump
{
    static readonly string[] TextHints = ["name", "desc", "text", "title", "label", "caption"];

    public static string Format(BafDocument doc, LocaleTable? locale = null, int maxDepth = 32, string? under = null)
    {
        var sb = new StringBuilder();
        var root = doc.Root;
        if (under is not null)
        {
            if (root.Table(under) is not { } t) return $"(no table '{under}')\n";
            root = t;
        }
        Write(sb, root, 0, maxDepth, locale);
        return sb.ToString();
    }

    /// <summary>
    /// The entries whose name, or string value, matches the pattern, each as <c>path/to/entry = value</c> (table names joined by '/').
    /// Used to find where an identifier (e.g. an upgrade name) is used across a package.
    /// </summary>
    public static IEnumerable<string> Grep(BafDocument doc, System.Text.RegularExpressions.Regex pattern)
    {
        var found = new List<string>();
        Walk(doc.Root, "", pattern, found, 0);
        return found;
    }

    static void Walk(BafTable table, string path, System.Text.RegularExpressions.Regex pattern, List<string> found, int depth)
    {
        if (depth > 40) return;
        foreach (var e in table.Entries)
        {
            string here = path.Length == 0 ? e.Name : path + "/" + e.Name;
            if (e.Type == BafType.Table) Walk(e.AsTable(), here, pattern, found, depth + 1);
            else if (pattern.IsMatch(e.Name) || (e.Type == BafType.String && pattern.IsMatch(e.AsString()))) found.Add($"{path} :: {e}");
        }
    }

    static void Write(StringBuilder sb, BafTable table, int depth, int maxDepth, LocaleTable? locale)
    {
        foreach (var e in table.Entries)
        {
            sb.Append(' ', depth * 2);
            switch (e.Type)
            {
                case BafType.Table:
                    var child = e.AsTable();
                    if (depth + 1 >= maxDepth) sb.Append(e.Name).Append(" { ").Append(child.Entries.Count()).Append(" entries }\n");
                    else { sb.Append(e.Name).Append(" {\n"); Write(sb, child, depth + 1, maxDepth, locale); sb.Append(' ', depth * 2).Append("}\n"); }
                    break;
                case BafType.Float:
                    sb.Append(e.Name).Append(" = ").Append(e.AsFloat().ToString("R", CultureInfo.InvariantCulture)).Append('\n');
                    break;
                case BafType.Int:
                    sb.Append(e.Name).Append(" = ").Append(e.Raw.ToString(CultureInfo.InvariantCulture));
                    if (locale is not null && TextHints.Any(h => e.Name.Contains(h, StringComparison.OrdinalIgnoreCase)) && locale.Get(e.Raw) is { } text)
                        sb.Append("   // ").Append(text.Length > 90 ? text[..90] + "..." : text);
                    sb.Append('\n');
                    break;
                default:
                    sb.Append(e.ToString()).Append('\n');
                    break;
            }
        }
    }
}
