using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SmSim.Extras.Musou.Campaign;
using SmSim.Extras.Warfront;
using SmSim.Extras.Warfront.Campaign;
using SmSim.Extras.Warfront.Maps;
using SmContent.Extras.Warfront;

namespace SmServer;

/// <summary>Where Warfront keeps its files and what the test aids ask for (slice W5a). <c>SM_WF_DIR</c> (test aid) points the run folder elsewhere so a scripted run never touches the owner's save.</summary>
static class WarfrontRunFiles
{
    public const string StageFile = "stage.json";

    /// <summary>Tests: the run folder, and the walking-graph cache folder (so a test arena never touches the private cache).</summary>
    internal static string? DirOverride, NavCacheOverride;

    /// <summary>Humans admitted (<c>--warfront-max-humans</c>), set by Program.cs before the server is built; one source for the stage child and the tests.</summary>
    internal static int MaxHumans = 4;

    /// <summary><c>--warfront-dedicated</c>, set by Program.cs before the server is built: the always-on shared campaign. Later dedicated-server slices (auto new run, default deploy, empty server) read it here.</summary>
    internal static bool Dedicated;

    /// <summary>Set by a dedicated stage child's SIGTERM or Ctrl+C handler: the stage saves and leaves with code 0 at its next tick.</summary>
    internal static volatile bool StopSignal;
    /// <summary>The stage child's SIGTERM registration, held for the process life (a local could be collected and unregister the handler).</summary>
    internal static IDisposable? TermRegistration;

    /// <summary>Written into the run folder by a dedicated supervisor that got SIGTERM: the stage child saves and leaves with code 0 at its next once-a-second check (same path as "the supervisor is gone").</summary>
    public const string StopFile = "stop.request";

    /// <summary>The environment variable the supervisor gives its child: its own process id. The child leaves (saved, exit code 0) when that process is gone.</summary>
    public const string SupervisorPidVariable = "SM_WF_SUPERVISOR_PID";

    /// <summary>The private run folder (<c>%LOCALAPPDATA%\sm-reimpl\warfront</c>), or <c>SM_WF_DIR</c>.</summary>
    public static string Dir
    {
        get
        {
            if (DirOverride is not null) return DirOverride;
            string? env = Environment.GetEnvironmentVariable("SM_WF_DIR");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl", "warfront");
        }
    }

    /// <summary>Forced rolls (<c>SM_WF_ROLLS=LWLW</c>: L = the lane is lost, W = it holds) first, then the hashed rolls.</summary>
    public static IWarfrontRollSource? Rolls()
    {
        string? text = Environment.GetEnvironmentVariable("SM_WF_ROLLS");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var list = new List<bool>();
        foreach (char c in text.Trim().ToUpperInvariant())
        {
            if (c == 'L') list.Add(true);
            else if (c == 'W') list.Add(false);
        }
        return new SequencedRolls(list);
    }

    /// <summary>A forced sequence, then the real (hashed) rolls: a test that rolls more than it planned goes on as a normal run.</summary>
    sealed class SequencedRolls(IEnumerable<bool> first) : IWarfrontRollSource
    {
        readonly Queue<bool> _q = new(first);
        public bool Lost(ulong runSeed, int deployment, int lane) => _q.Count > 0 ? _q.Dequeue() : HashedRolls.Instance.Lost(runSeed, deployment, lane);
    }

    /// <summary>The fronts a new run starts with from <c>SM_WF_START=a2,b1,c3</c> (test aid), or null.</summary>
    public static int[]? StartFronts()
    {
        string? text = Environment.GetEnvironmentVariable("SM_WF_START");
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fronts = new[] { 1, 1, 1 };
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (part.Length == 2 && char.ToLowerInvariant(part[0]) is >= 'a' and <= 'c' && part[1] is >= '0' and <= '4') fronts[char.ToLowerInvariant(part[0]) - 'a'] = part[1] - '0';
        return fronts;
    }

    /// <summary>A new run on the Ship, written to disk. Nothing is read from the records. The start fronts come from <c>SM_WF_START</c> when set.</summary>
    public static WarfrontCampaign NewRun(string dir, ulong? seed = null)
    {
        ulong runSeed = seed ?? (ulong.TryParse(Environment.GetEnvironmentVariable("SM_WF_SEED"), CultureInfo.InvariantCulture, out ulong s) ? s : BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)));
        var c = WarfrontSave.NewRun(runSeed, Rolls());
        if (StartFronts() is { } fronts)
        {
            // The campaign has no public setter for a front (a test aid of ours): go through its own save format.
            var node = JsonNode.Parse(c.ToJson())!.AsObject();
            node["Fronts"] = new JsonArray(fronts.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray());
            c = WarfrontSave.FromJson(node.ToJsonString(), Rolls());
        }
        c.Embark();
        WarfrontSave.Write(dir, c);
        try { File.Delete(Path.Combine(dir, StageFile)); } catch (IOException) { }
        return c;
    }

    /// <summary>The stage file: the map this stage (or the one it is leaving for) runs, the keys that must rejoin, and the last map that had a ship.</summary>
    public sealed record StageMemo(string Map, List<ulong> Expected, string? Ship = null);

    public static StageMemo? ReadStage(string dir)
    {
        try
        {
            string path = Path.Combine(dir, StageFile);
            return File.Exists(path) ? JsonSerializer.Deserialize<StageMemo>(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    public static void WriteStage(string dir, StageMemo memo)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, StageFile), tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(memo));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Which map a stage runs (plan section 3, "Stage change"; pure).</summary>
static class WarfrontStagePlan
{
    public const string FirstMap = "wf_b1";

    /// <summary>The map of a deployment target: a node's slot (the lane's current node), the HQ, the fortress.</summary>
    public static string MapOf(WarfrontCampaign c, WarfrontTarget t) => t.Kind switch
    {
        WarfrontTargetKind.Hq => SmContent.Extras.Warfront.WarfrontMaps.HqName,
        WarfrontTargetKind.Fortress => SmContent.Extras.Warfront.WarfrontMaps.FortressName,
        _ => WarfrontSlots.Of((TfwLane)t.Lane, Math.Clamp(c.Front(t.Lane), 1, 3)).Name,
    };

    /// <summary>A map that has a ship: a lane slot, the HQ or the fortress (each is composed with the hub).</summary>
    public static bool HasShip(string map) => WarfrontSlots.IsLaneMap(map) || map is SmContent.Extras.Warfront.WarfrontMaps.HqName or SmContent.Extras.Warfront.WarfrontMaps.FortressName;

    /// <summary>The next stage's map: the saved target's while the phase is Travel; otherwise the last lane map the previous stage ran (a ship), or <see cref="FirstMap"/>.</summary>
    public static string NextMap(WarfrontCampaign? c, string? lastShipMap)
    {
        if (c is { Phase: WarfrontPhase.Travel, CurrentTarget: { } t }) return MapOf(c, t);
        return lastShipMap is not null && HasShip(lastShipMap) ? lastShipMap : FirstMap;
    }
}

