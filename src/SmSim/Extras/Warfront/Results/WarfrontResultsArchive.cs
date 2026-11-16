using SmSim.Extras.Warfront.Campaign;

namespace SmSim.Extras.Warfront.Results;

/// <summary>
/// Files of the end of a run, under the caller's warfront directory (the app passes <c>%LOCALAPPDATA%\sm-reimpl\warfront</c>): the results record beside the archived run
/// (<c>records\&lt;stem&gt;.results.json</c>), and exports in <c>exports\</c>. Nothing here is read by a new run (<see cref="WarfrontSave.NewRun"/> reads no file).
/// </summary>
public static class WarfrontResultsArchive
{
    public const string ExportsDir = "exports";
    public const string RecordSuffix = ".results.json";

    /// <summary>Writes the results record next to <paramref name="archivedRunPath"/> (the path <see cref="WarfrontSave.Archive"/> returned): same stem, <c>.results.json</c>. Returns its path.</summary>
    public static string WriteRecord(string archivedRunPath, WarfrontRunRecord record)
    {
        var path = Path.Combine(Path.GetDirectoryName(archivedRunPath)!, Path.GetFileNameWithoutExtension(archivedRunPath) + RecordSuffix);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, record.ToJson());
        File.Move(tmp, path, overwrite: true);
        return path;
    }

    /// <summary>Archives the finished campaign (<see cref="WarfrontSave.Archive"/>) and appends its results record. Returns the campaign archive's path.</summary>
    public static string Archive(string dir, WarfrontCampaign c, WarfrontRunRecord record, DateTime utc)
    {
        var run = WarfrontSave.Archive(dir, c, utc);
        WriteRecord(run, record);
        return run;
    }

    /// <summary>Every readable results record in <c>records\</c>, oldest stem first; unreadable files are skipped.</summary>
    public static IReadOnlyList<(string Stem, WarfrontRunRecord Record)> LoadAll(string dir)
    {
        var rec = Path.Combine(dir, WarfrontSave.RecordsDir);
        var list = new List<(string, WarfrontRunRecord)>();
        if (!Directory.Exists(rec)) return list;
        foreach (var f in Directory.GetFiles(rec, "*" + RecordSuffix).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                var name = Path.GetFileName(f);
                list.Add((name[..^RecordSuffix.Length], WarfrontRunRecord.FromJson(File.ReadAllText(f))));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
        }
        return list;
    }

    public static WarfrontHonorRoll HonorRoll(string dir) => WarfrontHonorRoll.Build(LoadAll(dir));

    /// <summary>Writes an export (<paramref name="markdown"/>: .md with the Discord fence, else .txt plain) to <c>exports\&lt;utc&gt;.txt|md</c>; a taken name gets -2, -3. Returns the path.</summary>
    public static string Export(string dir, WarfrontRunRecord record, DateTime utc, bool markdown)
    {
        var ex = Path.Combine(dir, ExportsDir);
        Directory.CreateDirectory(ex);
        var stem = utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var ext = markdown ? ".md" : ".txt";
        var path = Path.Combine(ex, stem + ext);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(ex, stem + "-" + n + ext);
        File.WriteAllText(path, markdown ? WarfrontExport.Discord(record) : WarfrontExport.Text(record));
        return path;
    }
}
