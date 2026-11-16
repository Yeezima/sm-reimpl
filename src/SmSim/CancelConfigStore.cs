using System.Text;

namespace SmSim;

/// <summary>
/// cancel-editor: where exported cancel tables are kept and how they are read back. One folder in the owner's local application data (<c>%LOCALAPPDATA%\sm-reimpl\cancel-configs</c>; never the game install, never
/// the repository), a timestamped file per export. Used by the client panel (export and import) and by <c>smtool cancel-config</c>.
/// </summary>
public static class CancelConfigStore
{
    /// <summary>The export folder (not created until the first export).</summary>
    public static string Folder() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "cancel-configs");

    /// <summary>Writes <paramref name="table"/> as a new timestamped file in <paramref name="folder"/> (default: <see cref="Folder"/>) and returns its path. Never overwrites: a second export in the same second gets a letter.</summary>
    public static string Export(CancelData table, string? folder = null, DateTime? now = null)
    {
        folder ??= Folder();
        Directory.CreateDirectory(folder);
        var t = now ?? DateTime.Now;
        string stem = $"cancel-table-{t:yyyyMMdd-HHmmss}";
        string path = Path.Combine(folder, stem + ".json");
        for (char suffix = 'b'; File.Exists(path) && suffix <= 'z'; suffix++) path = Path.Combine(folder, $"{stem}{suffix}.json");
        File.WriteAllText(path, table.ToJson(Path.GetFileNameWithoutExtension(path), t.ToString("yyyy-MM-dd HH:mm:ss")), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Reads a table file. Null with <paramref name="error"/> when it is missing or is not a cancel table.</summary>
    public static CancelData? Import(string path, out string error)
    {
        error = "";
        try { return CancelData.Parse(File.ReadAllText(path), out error); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { error = e.Message; return null; }
    }

    /// <summary>The exports in a folder, newest first (names only sorted by the timestamp in them).</summary>
    public static IReadOnlyList<string> List(string? folder = null)
    {
        folder ??= Folder();
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "cancel-table-*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).ToList() : [];
    }
}
