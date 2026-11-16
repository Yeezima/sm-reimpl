using System.Text.Json;

namespace SmSim.Extras.Warfront.Campaign;

/// <summary>
/// Run save (plan section 3): <c>run.json</c> in a caller-supplied directory (the app passes the private warfront folder under the local app data), written atomically;
/// finished runs move to <c>records\&lt;utc&gt;-&lt;result&gt;.json</c> and are never read by a new run.
/// </summary>
public static class WarfrontSave
{
    public const string RunFile = "run.json";
    public const string RecordsDir = "records";

    public static WarfrontCampaign FromJson(string json, IWarfrontRollSource? rolls = null)
    {
        var s = JsonSerializer.Deserialize<WarfrontCampaign.Snapshot>(json, WarfrontCampaign.Json) ?? throw new InvalidDataException("empty warfront save");
        return WarfrontCampaign.FromSnapshot(s, rolls);
    }

    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, RunFile));

    /// <summary>Atomic write: a temp file in the same directory, then a replacing move.</summary>
    public static void Write(string dir, WarfrontCampaign c)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, RunFile);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, c.ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads <c>run.json</c> for Continue and applies the restart rule (a Battle returns to the ship; Travel stays); null when there is no save. Never looks at <c>records\</c>.</summary>
    public static WarfrontCampaign? Load(string dir, IWarfrontRollSource? rolls = null)
    {
        var path = Path.Combine(dir, RunFile);
        if (!File.Exists(path)) return null;
        var c = FromJson(File.ReadAllText(path), rolls);
        c.ResumeAfterRestart();
        return c;
    }

    /// <summary>A new run: a fresh, empty state. Reads nothing from disk.</summary>
    public static WarfrontCampaign NewRun(ulong runSeed, IWarfrontRollSource? rolls = null) => new(runSeed, rolls);

    /// <summary>Moves a finished run into <c>records\</c> and removes <c>run.json</c>. Returns the record path.</summary>
    public static string Archive(string dir, WarfrontCampaign c, DateTime utc)
    {
        var rec = Path.Combine(dir, RecordsDir);
        Directory.CreateDirectory(rec);
        var result = c.Phase == WarfrontPhase.Won ? "won" : c.Phase == WarfrontPhase.Lost ? "lost" : "abandoned";
        var stem = utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'") + "-" + result;
        var path = Path.Combine(rec, stem + ".json");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(rec, stem + "-" + n + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, c.ToJson());
        File.Move(tmp, path);
        var run = Path.Combine(dir, RunFile);
        if (File.Exists(run)) File.Delete(run);
        return path;
    }
}
