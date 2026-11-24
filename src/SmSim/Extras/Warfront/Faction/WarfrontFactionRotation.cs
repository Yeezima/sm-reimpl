using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmSim.Extras.Warfront.Faction;

/// <summary>
/// The rotation file (Chaos Warfront C1): <c>faction.json</c> in the run folder, <c>{"LastFaction":"Orks","LastRunSeed":123}</c>, the army and seed of the last run that was built. It sits beside <c>run.json</c> and outlives it
/// (an archive removes only <c>run.json</c>), so the Alternate setting knows what came last.
/// <para>Crash safety: a new run writes <c>run.json</c> first and this file second. A crash between the two leaves this file one run behind; whoever next reads <c>run.json</c> (the stage's loader, the supervisor)
/// calls <see cref="Reconcile"/>, which copies the run's own army and seed here. The army is therefore decided once, stored in <c>run.json</c>, and the rotation can neither repeat nor skip an army: it never
/// advances twice for one run. An unreadable file counts as no file (the next run is Orks under Alternate) until the next reconcile rewrites it.</para>
/// </summary>
public static class WarfrontFactionRotation
{
    public const string FileName = "faction.json";

    /// <summary>The file's content.</summary>
    public sealed record State(WarfrontFaction LastFaction, ulong LastRunSeed);

    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }, WriteIndented = false };

    /// <summary>The last run's army and seed; null when the file is missing or cannot be used (damaged, unknown army, unreadable).</summary>
    public static State? Read(string dir)
    {
        try
        {
            string path = Path.Combine(dir, FileName);
            if (!File.Exists(path)) return null;
            var s = JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json);
            return s is not null && WarfrontFactionPolicy.IsKnown((int)s.LastFaction) ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException) { return null; }
    }

    /// <summary>The army a new run built now in <paramref name="dir"/> would get (reads the file, writes nothing).</summary>
    public static WarfrontFaction Next(string dir, WarfrontFactionSetting setting) => WarfrontFactionPolicy.Choose(setting, Read(dir)?.LastFaction);

    /// <summary>Writes the file atomically (a temp file, then a replacing move). False when the folder cannot be written; the next <see cref="Reconcile"/> repairs it.</summary>
    public static bool Record(string dir, WarfrontFaction faction, ulong runSeed)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, FileName), tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new State(faction, runSeed), Json));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Brings the file in line with the run on disk (its army and seed, as read from <c>run.json</c>): nothing when they already match, else a rewrite (a crash came between the two writes of a new run, or the run
    /// predates the file). True when the file was rewritten.
    /// </summary>
    public static bool Reconcile(string dir, WarfrontFaction runFaction, ulong runSeed)
    {
        if (Read(dir) is { } s && s.LastFaction == runFaction && s.LastRunSeed == runSeed) return false;
        return Record(dir, runFaction, runSeed);
    }
}
