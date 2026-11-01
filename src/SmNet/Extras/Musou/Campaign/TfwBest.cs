using System.Globalization;
using System.Text.Json;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>A player's best run on this PC: the total, the UTC day, the outcome and the rules' version it was earned under.</summary>
public sealed record TfwBestRecord(long Total, string Date, string Outcome, uint Version);

/// <summary>
/// The local best (addendum to the pass 2 design): <c>musou-best.json</c> beside <c>frontend.json</c>, so the core's settings stay untouched. Only the current <see cref="TfwRules.Version"/> counts, since a tuning change makes older
/// scores incomparable; a damaged or older file reads as no best. It is a player's own record, not a verified one: a local server is under the player's control. No service: <see cref="ResultLine"/> is what players share.
/// </summary>
public static class TfwBest
{
    public const string FileName = "musou-best.json";
    /// <summary>A best file larger than this is not read (it is under 200 bytes).</summary>
    const long MaxFileBytes = 4096;

    /// <summary>The best of this version in <paramref name="directory"/>, or null.</summary>
    public static TfwBestRecord? Load(string directory)
    {
        try
        {
            var file = new FileInfo(Path.Combine(directory, FileName));
            if (!file.Exists || file.Length > MaxFileBytes) return null;
            var best = JsonSerializer.Deserialize<TfwBestRecord>(File.ReadAllText(file.FullName));
            return best is { Total: >= 0, Date: not null, Outcome: not null, Version: TfwRules.Version } ? best : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>A finished run: saved when it beats the best (a failed save loses nothing but the record); whether it is a new best.</summary>
    public static bool Note(string directory, long total, string outcome, DateTime utc)
    {
        if (Load(directory) is { } best && best.Total >= total) return false;
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileName), temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new TfwBestRecord(total, Day(utc), outcome, TfwRules.Version)));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return true;
    }

    static string Day(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The line COPY RESULT puts on the clipboard: the total, the outcome, the maps, the day and the rules' version.</summary>
    public static string ResultLine(long total, string outcome, int maps, DateTime utc) =>
        string.Create(CultureInfo.InvariantCulture, $"SM Reimpl Musou Mode: {total:N0} points, {outcome}, {maps} {(maps == 1 ? "map" : "maps")}, {Day(utc)}, rules v{TfwRules.Version}");

    /// <summary>A map's result in words.</summary>
    public static string ResultText(TfwMapResult result) => result switch
    {
        TfwMapResult.Won => "WON",
        TfwMapResult.Left => "LEFT",
        TfwMapResult.Lost => "LOST",
        TfwMapResult.Extracted => "EXTRACTED",
        _ => "?",
    };

    /// <summary>The humans ranked by total, high first, ties by id: the own player and the others tag 12 lists.</summary>
    public static IReadOnlyList<TfwScorePlayer> Ranked(ushort self, uint total, IReadOnlyList<TfwScorePlayer> others)
    {
        var all = new List<TfwScorePlayer>(others.Count + 1) { new(self, total) };
        foreach (var o in others) if (o.Player != self) all.Add(o);
        all.Sort(static (a, b) => a.RunTotal != b.RunTotal ? b.RunTotal.CompareTo(a.RunTotal) : a.Player.CompareTo(b.Player));
        return all;
    }
}
