using System;
using System.IO;
using SmSim.Extras.Warfront.Progression;

namespace SmClient.Extras.Warfront.Stage;

/// <summary>How this PC's progress file read.</summary>
public enum WarfrontProgressLoad
{
    /// <summary>No readable file (never made, or both it and its .bak are unreadable): the host's copy, if any, becomes the first file.</summary>
    Absent,
    /// <summary>A good file for this key.</summary>
    Ok,
    /// <summary>A file of a newer build (version above 1): never read further, never touched, never overwritten ("needs a newer build").</summary>
    Newer,
    /// <summary>A good file, but for another key than this PC's client.json: treated as absent; it is kept as <see cref="WarfrontProgressStore.OtherKeyCopyPath"/> before the next write replaces it.</summary>
    KeyMismatch,
    /// <summary>The file exists but could not be read (locked, no access): it is never written over this session, nothing is sent to the host for it, the menu says so.</summary>
    Unreadable,
}

/// <summary>
/// This player's persistent Warfront progression on this PC (codebase fix 1; owner decisions 4 and 8): <c>progress.json</c> in the warfront folder (<see cref="WarfrontClientKey.Folder"/>, so <c>SM_WF_DIR</c> is honoured), or
/// <c>&lt;key file&gt;.progress.json</c> with <c>--warfront-key-file</c>. No Godot: tests link this file. Load order is the main file, then <c>.bak</c>, then absent. Writes are atomic (<c>File.Replace(tmp, path, path + ".bak")</c>).
/// A file of a newer build is never touched. The client writes only what <see cref="WarfrontProgressFile.ClientMayWrite"/> allows, so it never lowers its own file.
/// </summary>
public sealed class WarfrontProgressStore
{
    public string Path { get; }
    public ulong Key { get; }
    public string KeyHex => Key.ToString("x16");
    public WarfrontProgressLoad State { get; private set; } = WarfrontProgressLoad.Absent;
    /// <summary>The sanitized file as loaded or last written (null when absent, newer or of another key).</summary>
    public WarfrontProgressFile? Current { get; private set; }
    /// <summary>One-line log sink (the view sends it to the console log; never carries the key).</summary>
    public Action<string> Log { get; set; } = Console.WriteLine;
    /// <summary>The last write's problem, empty when it worked.</summary>
    public string LastProblem { get; private set; } = "";

    public WarfrontProgressStore(string path, ulong key) { Path = path; Key = key; }

    public string BakPath => Path + ".bak";
    string TmpPath => Path + ".tmp";
    /// <summary>The one safety copy a reset keeps (the next reset replaces it): <c>progress-before-reset.json</c>, or <c>&lt;key file&gt;.progress-before-reset.json</c>.</summary>
    public string BeforeResetPath => Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path[..^5] + "-before-reset.json" : Path + "-before-reset";
    public string OtherKeyCopyPath => Path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? Path[..^5] + "-other-key.json" : Path + "-other-key";

    /// <summary>The progress file's path for a key file named with <c>--warfront-key-file</c> (null or empty: the default folder's <c>progress.json</c>).</summary>
    public static string PathFor(string? keyFile) =>
        keyFile is { Length: > 0 } ? keyFile + ".progress.json" : System.IO.Path.Combine(WarfrontClientKey.Folder(), "progress.json");

    public bool Newer => State == WarfrontProgressLoad.Newer;
    /// <summary>The file could not be read (see <see cref="WarfrontProgressLoad.Unreadable"/>): treated like a newer file, never written.</summary>
    public bool Unreadable => State == WarfrontProgressLoad.Unreadable;

    /// <summary>Reads the file: main, then <c>.bak</c>, then absent.</summary>
    public WarfrontProgressLoad Load()
    {
        Current = null;
        _resetHeard = false;
        State = WarfrontProgressLoad.Absent;
        foreach (string p in new[] { Path, BakPath })
        {
            string text;
            try { if (!File.Exists(p)) continue; text = File.ReadAllText(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a good file may sit behind the lock: never take it for absent (a later Save would overwrite it), and never fall back to an older .bak either
                Log("warfront: the progress file could not be read: left alone this session");
                return State = WarfrontProgressLoad.Unreadable;
            }
            switch (WarfrontProgressFile.TryParse(text, out var file))
            {
                case WarfrontProgressParse.Newer:
                    Log("warfront: the progress file is of a newer build: left alone");
                    return State = WarfrontProgressLoad.Newer;
                case WarfrontProgressParse.Ok when file is not null:
                    if (!string.Equals(file.Key, KeyHex, StringComparison.OrdinalIgnoreCase))
                    {
                        Log("warfront: the progress file belongs to another key: treated as absent");
                        return State = WarfrontProgressLoad.KeyMismatch;
                    }
                    Current = WarfrontProgressFile.Sanitize(file);
                    Current.Key = KeyHex;
                    return State = WarfrontProgressLoad.Ok;
                default:
                    Log("warfront: the progress file is damaged" + (p == Path ? "; trying its backup" : ""));
                    break;
            }
        }
        return State;
    }

    /// <summary>Writes <paramref name="file"/> (key set to this player's) atomically. False (and nothing written) for a newer file on disk, or an IO problem (<see cref="LastProblem"/>).</summary>
    public bool Save(WarfrontProgressFile file)
    {
        LastProblem = "";
        if (Newer) { LastProblem = "the progress file is of a newer build"; return false; }
        if (Unreadable) { LastProblem = "the progress file could not be read"; return false; }
        try
        {
            if (State == WarfrontProgressLoad.KeyMismatch && File.Exists(Path)) File.Copy(Path, OtherKeyCopyPath, overwrite: true);   // kept, not lost
            var clean = WarfrontProgressFile.Sanitize(file);
            clean.Key = KeyHex;
            string? dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TmpPath, clean.ToJson());
            if (File.Exists(Path)) File.Replace(TmpPath, Path, BakPath);
            else File.Move(TmpPath, Path, overwrite: true);
            Current = clean;
            State = WarfrontProgressLoad.Ok;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastProblem = "the progress file could not be written";
            Log("warfront: " + LastProblem);
            try { if (File.Exists(TmpPath)) File.Delete(TmpPath); } catch (Exception) { /* a leftover tmp is harmless */ }
            return false;
        }
    }

    /// <summary>The host sent its copy: writes it only when <see cref="WarfrontProgressFile.ClientMayWrite"/> allows (never lowers this file, never touches a newer one). True when the file changed.</summary>
    public bool ApplyHostCopy(WarfrontProgressFile host)
    {
        if (Newer || Unreadable) return false;
        var h = WarfrontProgressFile.Sanitize(host);
        var own = State == WarfrontProgressLoad.Ok ? Current : null;
        if (own is not null)
        {
            // a reset the host has not applied yet: its copy (the old level, from before the reset) must not undo it. Only a level-1 copy passes, and that changes nothing.
            if (IsPendingReset(own) && !_resetHeard)
            {
                if (IsFresh(h)) _resetHeard = true;   // the host shows level 1: it has heard the reset, later copies with new XP are welcome
                else return false;
            }
            if (!WarfrontProgressFile.ClientMayWrite(h, own)) return false;
            if (SameBody(h, own)) return false;   // nothing new: no write
            h.ResetAt = own.ResetAt;
        }
        return Save(h);
    }

    bool _resetHeard;

    static bool SameBody(WarfrontProgressFile a, WarfrontProgressFile b)
    {
        if (a.Xp != b.Xp || a.PicksTaken != b.PicksTaken || a.Cards.Count != b.Cards.Count || a.ExtraRetainers.Count != b.ExtraRetainers.Count) return false;
        for (int i = 0; i < a.Cards.Count; i++) if (a.Cards[i] != b.Cards[i]) return false;
        for (int i = 0; i < a.ExtraRetainers.Count; i++)
            if (a.ExtraRetainers[i].ClassName != b.ExtraRetainers[i].ClassName || a.ExtraRetainers[i].Name != b.ExtraRetainers[i].Name) return false;
        return true;
    }

    /// <summary>
    /// RESET PROGRESSION: copies the current file to <see cref="BeforeResetPath"/> (replacing the previous copy), writes a level-1 file with <c>resetAt</c> and deletes the <c>.bak</c>. Refused (false) for a file of a newer build.
    /// </summary>
    public bool Reset(DateTime utcNow)
    {
        LastProblem = "";
        if (Newer) { LastProblem = "the progress file is of a newer build"; return false; }
        if (Unreadable) { LastProblem = "the progress file could not be read: nothing was reset"; return false; }
        try
        {
            // the safety copy is of the file Load would use (the main file, or the .bak when the main is damaged), never of a damaged main over a good copy
            string? source = GoodSource();
            if (source is not null) File.Copy(source, BeforeResetPath, overwrite: true);
            else if (File.Exists(Path) && !File.Exists(BeforeResetPath)) File.Copy(Path, BeforeResetPath, overwrite: false);   // nothing good: keep what there is, but never replace an earlier good copy
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LastProblem = "the safety copy could not be written: nothing was reset";
            Log("warfront: " + LastProblem);
            return false;
        }
        var fresh = WarfrontProgressFile.Fresh(KeyHex, utcNow.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        if (!Save(fresh)) return false;
        try { if (File.Exists(BakPath)) File.Delete(BakPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log("warfront: the old backup could not be deleted"); }
        return true;
    }

    /// <summary>The first of main and .bak that parses as a good file of this build (the one <see cref="Load"/> uses), or null.</summary>
    string? GoodSource()
    {
        foreach (string p in new[] { Path, BakPath })
        {
            try { if (File.Exists(p) && WarfrontProgressFile.TryParse(File.ReadAllText(p), out _) == WarfrontProgressParse.Ok) return p; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    /// <summary>A level-1 record with nothing earned: XP, picks, cards and extra retainers all empty.</summary>
    public static bool IsFresh(WarfrontProgressFile f) => f.Xp == 0 && f.PicksTaken == 0 && f.Cards.Count == 0 && f.ExtraRetainers.Count == 0;

    /// <summary>The state is a fresh level-1 one carrying a <c>resetAt</c>: a reset the host may not have heard yet.</summary>
    public static bool IsPendingReset(WarfrontProgressFile? f) =>
        f is { ResetAt: { Length: > 0 }, Xp: 0, PicksTaken: 0 } && f.Cards.Count == 0 && f.ExtraRetainers.Count == 0;
}
