using System.Text.Json;
using SmSim.Extras.Warfront.Campaign;

namespace SmServer;

/// <summary>How a run load ended (<see cref="WarfrontRunLoader.Load"/>).</summary>
public enum WarfrontLoadState
{
    /// <summary>run.json read and the rewrite (the restart rule's phase) is on disk.</summary>
    Loaded,
    /// <summary>run.json read, but the rewrite failed: play goes on with the loaded campaign and the server retries the write (never a new run for a write failure).</summary>
    LoadedNotWritten,
    /// <summary>No run.json: a new run, written.</summary>
    New,
    /// <summary>run.json was damaged: it (and squads.json) were set aside under records\ and a new run began.</summary>
    NewAfterDamaged,
    /// <summary>A newer build's file: untouched, a lobby-only dummy campaign, nothing may be saved.</summary>
    Newer,
    /// <summary>The file could not be read (still locked after the retries): untouched, nothing may be saved.</summary>
    Locked,
    /// <summary>Damaged and neither moved nor copied aside: untouched, nothing may be saved.</summary>
    Refused,
}

/// <summary>The one line a player sees about a load that was not plain (<c>notice.json</c> in the run folder: <c>{kind, utc, text}</c>; kind is unreadable, newer or locked).</summary>
public sealed record WarfrontLoadNotice(string Kind, string Utc, string Text);

/// <summary>The result of <see cref="WarfrontRunLoader.Load"/>: the campaign to play, how it came about, the notice (null for a plain load) and a log line.</summary>
public sealed record WarfrontRunLoad(WarfrontCampaign Campaign, WarfrontLoadState State, WarfrontLoadNotice? Notice, string How, bool WriteFailed = false)
{
    /// <summary>The campaign on disk is not ours to write: nothing may save, archive or start a new run (Newer, Locked, Refused).</summary>
    public bool NoSave => State is WarfrontLoadState.Newer or WarfrontLoadState.Locked or WarfrontLoadState.Refused;
    /// <summary>The load rewrote nothing yet and the server must retry.</summary>
    public bool SavePending => State == WarfrontLoadState.LoadedNotWritten || WriteFailed;
}

/// <summary>The file operations of the loader (replaced by tests to fail one of them).</summary>
public sealed class WarfrontLoaderIo
{
    public Func<string, string> ReadAllText = File.ReadAllText;
    public Action<string, string> Move = (from, to) => File.Move(from, to);
    public Action<string, string> Copy = (from, to) => File.Copy(from, to);
    public Action<string, WarfrontCampaign> Write = WarfrontSave.Write;
    /// <summary>A new run when the file is absent or set aside (writes itself).</summary>
    public Func<string, WarfrontCampaign> NewRun = dir => WarfrontRunFiles.NewRun(dir);
    public Action<int> SleepMs = Thread.Sleep;
}

/// <summary>
/// Reads the run for a stage (owner decisions 2026-11-22, codebase fix 1): a load failure is told apart from a write failure, and a file this build cannot use is never replaced.
/// <list type="bullet">
/// <item>Good file, failed rewrite: <see cref="WarfrontLoadState.LoadedNotWritten"/>, the same campaign, the server retries.</item>
/// <item>Damaged file: moved (or copied) to <c>records\&lt;yyyyMMddTHHmmssZ&gt;-unreadable.json</c>, squads.json beside it, then a new run. Both fail: <see cref="WarfrontLoadState.Refused"/>. Nothing is deleted.</item>
/// <item>Newer build's file: <see cref="WarfrontLoadState.Newer"/>, untouched. Locked file (an IOException on three reads 200 ms apart): <see cref="WarfrontLoadState.Locked"/>, untouched.</item>
/// </list>
/// Every notice goes to <c>notice.json</c> beside run.json, which the Warfront menu reads and deletes.
/// </summary>
public static class WarfrontRunLoader
{
    public const string NoticeFile = "notice.json";
    public const string Unreadable = "unreadable", NewerKind = "newer", LockedKind = "locked";
    public const string NewerText = "this campaign needs a newer build";
    public const string LockedText = "the campaign file cannot be read";
    public const string DamagedText = "the campaign file was damaged; it was set aside and a new campaign began";
    public const string RefusedText = "the campaign file is damaged and could not be set aside";
    public const int ReadTries = 3, ReadGapMs = 200;

    static readonly JsonSerializerOptions NoticeJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = false };

    public static WarfrontRunLoad Load(string dir, IWarfrontRollSource? rolls, WarfrontLoaderIo? io = null, Func<DateTime>? now = null, Action<string>? log = null)
    {
        io ??= new WarfrontLoaderIo();
        var clock = now ?? (() => DateTime.UtcNow);
        log ??= _ => { };
        string path = Path.Combine(dir, WarfrontSave.RunFile);
        if (!File.Exists(path)) return NewRunLoad(dir, io, "a new run; none on disk", WarfrontLoadState.New, null);

        // read (a locked file is retried: three reads, 200 ms apart)
        string? text = null;
        Exception? lastRead = null;
        for (int i = 0; i < ReadTries && text is null; i++)
        {
            if (i > 0) io.SleepMs(ReadGapMs);
            try { text = io.ReadAllText(path); }
            catch (FileNotFoundException) { return NewRunLoad(dir, io, "a new run; none on disk", WarfrontLoadState.New, null); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { lastRead = e; }
        }
        if (text is null)
        {
            var n = Notice(LockedKind, LockedText, clock());
            log($"run.json cannot be read ({lastRead?.Message}); left untouched, nothing will be saved");
            WriteNotice(dir, n, log);
            return new WarfrontRunLoad(Dummy(), WarfrontLoadState.Locked, n, "the campaign file could not be read; a lobby-only campaign, nothing saved");
        }

        // parse
        WarfrontCampaign? campaign = null;
        string? why = null;
        try { campaign = WarfrontSave.FromJson(text, rolls); campaign.ResumeAfterRestart(); }
        catch (WarfrontSaveNewerException e)
        {
            var n = Notice(NewerKind, NewerText, clock());
            log($"run.json is a newer build's file (version {e.FoundVersion}); left untouched, nothing will be saved");
            WriteNotice(dir, n, log);
            return new WarfrontRunLoad(Dummy(), WarfrontLoadState.Newer, n, $"a newer build's campaign (version {e.FoundVersion}); a lobby-only campaign, nothing saved");
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or NotSupportedException)
        {
            why = e.Message;
        }
        if (campaign is not null)
        {
            // Chaos Warfront C1: run.json is the army's record; faction.json follows it (a crash between a new run's two writes left it a run behind, or the run predates the file).
            if (SmSim.Extras.Warfront.Faction.WarfrontFactionRotation.Reconcile(dir, campaign.Faction, campaign.RunSeed))
                log($"faction.json brought in line with run.json ({campaign.Faction}, seed {campaign.RunSeed:X})");
            try
            {
                io.Write(dir, campaign);
                return new WarfrontRunLoad(campaign, WarfrontLoadState.Loaded, null, $"run loaded: {campaign.Phase}, deployment {campaign.Deployments}, state {campaign.StateHash()[..8]}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log($"run.json was read but could not be rewritten ({e.Message}); playing on the loaded campaign, the write is retried");
                return new WarfrontRunLoad(campaign, WarfrontLoadState.LoadedNotWritten, null, $"run loaded (not yet rewritten): {campaign.Phase}, deployment {campaign.Deployments}, state {campaign.StateHash()[..8]}");
            }
        }

        // damaged: set aside, never delete
        log($"run.json is unreadable ({why}); setting it aside");
        if (!SetAsideDamaged(dir, io, clock, log, out string runTo, out string why2)) return Refuse(dir, clock(), log, why2);
        var notice = Notice(Unreadable, DamagedText, clock());
        WriteNotice(dir, notice, log);
        return NewRunLoad(dir, io, $"a new run after an unreadable save (kept as {Path.GetFileName(runTo)})", WarfrontLoadState.NewAfterDamaged, notice);
    }

    /// <summary>
    /// Sets a damaged run.json (and squads.json beside it) aside under <c>records\&lt;yyyyMMddTHHmmssZ&gt;-unreadable[-n].json</c> (squads: <c>...-squads.json</c>): moved, or copied when the move fails. Nothing is deleted
    /// except a squads file whose copy is safe. False (nothing set aside) when run.json could be neither moved nor copied. The same code serves the stage's load and the supervisor's <c>--warfront-new</c> path.
    /// </summary>
    public static bool SetAsideDamaged(string dir, WarfrontLoaderIo io, Func<DateTime> clock, Action<string> log, out string runTo, out string why)
    {
        why = "";
        runTo = "";
        string path = Path.Combine(dir, WarfrontSave.RunFile);
        var utc = clock().ToUniversalTime();
        string records = Path.Combine(dir, WarfrontSave.RecordsDir);
        string stamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'") + "-unreadable";
        string squadsTo;
        try
        {
            Directory.CreateDirectory(records);
            string suffix = "";
            for (int n = 2; File.Exists(Path.Combine(records, stamp + suffix + ".json")) || File.Exists(Path.Combine(records, stamp + suffix + "-squads.json")); n++) suffix = "-" + n;
            runTo = Path.Combine(records, stamp + suffix + ".json");
            squadsTo = Path.Combine(records, stamp + suffix + "-squads.json");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { why = e.Message; return false; }
        if (!SetAside(io, path, runTo, log, deleteOriginalAfterCopy: false)) { why = "neither moved nor copied"; return false; }
        string squads = Path.Combine(dir, "squads.json");
        if (File.Exists(squads) && !SetAside(io, squads, squadsTo, log, deleteOriginalAfterCopy: true)) log("squads.json could not be set aside; it stays");
        return true;
    }

    static WarfrontRunLoad Refuse(string dir, DateTime utc, Action<string> log, string why)
    {
        log($"run.json is damaged and could not be set aside ({why}); left untouched, nothing will be saved");
        var n = Notice(Unreadable, RefusedText, utc);
        WriteNotice(dir, n, log);
        return new WarfrontRunLoad(Dummy(), WarfrontLoadState.Refused, n, "the campaign file is damaged and could not be set aside; a lobby-only campaign, nothing saved");
    }

    /// <summary>Move; on a failure copy and keep the original. True when the content is safe under <paramref name="to"/>. A copied squads file is then removed (its content is kept) so the new run does not read it.</summary>
    static bool SetAside(WarfrontLoaderIo io, string from, string to, Action<string> log, bool deleteOriginalAfterCopy)
    {
        try { io.Move(from, to); return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"{Path.GetFileName(from)} could not be moved ({e.Message}); copying it");
        }
        try { io.Copy(from, to); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"{Path.GetFileName(from)} could not be copied ({e.Message})"); return false; }
        if (deleteOriginalAfterCopy) try { File.Delete(from); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"the copied {Path.GetFileName(from)} could not be removed: {e.Message}"); }
        return true;
    }

    static WarfrontRunLoad NewRunLoad(string dir, WarfrontLoaderIo io, string how, WarfrontLoadState state, WarfrontLoadNotice? notice)
    {
        try
        {
            var c = io.NewRun(dir);
            return new WarfrontRunLoad(c, state, notice, $"{how} (seed {c.RunSeed:X}, {c.Faction})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the folder cannot be written now: play on in memory, the server retries the write (the army as the setting and the rotation say; faction.json follows once run.json is written and loaded)
            var c = WarfrontRunFiles.BuildNewRun(null, SmSim.Extras.Warfront.Faction.WarfrontFactionRotation.Next(dir, WarfrontRunFiles.FactionSetting));
            return new WarfrontRunLoad(c, state, notice, $"{how} (seed {c.RunSeed:X}; not yet written: {e.Message})", WriteFailed: true);
        }
    }

    /// <summary>The dummy campaign of a NoSave load: fresh, in memory, never written.</summary>
    static WarfrontCampaign Dummy()
    {
        var c = WarfrontRunFiles.BuildNewRun();
        return c;
    }

    public static WarfrontLoadNotice Notice(string kind, string text, DateTime utc) => new(kind, utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), text);

    /// <summary>Writes <c>notice.json</c> (a failure is only logged: the notice is a courtesy).</summary>
    public static void WriteNotice(string dir, WarfrontLoadNotice notice, Action<string>? log = null)
    {
        try
        {
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, NoticeFile), tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(notice, NoticeJson));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Invoke($"notice.json could not be written: {e.Message}"); }
    }
}
