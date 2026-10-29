using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmLauncher.Core;

/// <summary>
/// The launcher's install folder (default %LOCALAPPDATA%\SM Reimpl; the tester may choose another):
/// <code>
///   SM Reimpl Launcher.exe     the launcher itself (copied here on the first install)
///   current.json               the active version and the previous one (rollback), with what is known about each
///   update.json                only while an update runs: its phase (downloading, verifying, extracting, switching)
///   versions\&lt;tag&gt;\            one extracted playtest zip per version (the active one and the one before it)
///   versions\&lt;tag&gt;.staging\    a version being extracted and checked; renamed to versions\&lt;tag&gt;\ only once it is verified
///   downloads\&lt;zip&gt;.part        the download in progress (resumed with HTTP Range)
///   logs\launcher.log
/// </code>
/// Player data never lives here: profiles and settings are in %APPDATA%\sm-reimpl, caches in %LOCALAPPDATA%\sm-reimpl.
/// </summary>
public sealed class InstallLayout(string root)
{
    public const string LauncherFileName = "SM Reimpl Launcher.exe";

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string VersionsDir => Path.Combine(Root, "versions");
    public string DownloadsDir => Path.Combine(Root, "downloads");
    public string CurrentFile => Path.Combine(Root, "current.json");
    public string StateFile => Path.Combine(Root, "update.json");
    public string LockFile => Path.Combine(Root, "launcher.lock");
    public string LogFile => Path.Combine(Root, "logs", "launcher.log");
    public string SettingsFile => Path.Combine(Root, "launcher-settings.json");
    public string LauncherExe => Path.Combine(Root, LauncherFileName);

    public string VersionDir(string tag) => Path.Combine(VersionsDir, Names.CheckTag(tag));
    public string StagingDir(string tag) => VersionDir(tag) + ".staging";
    public string ReplacedDir(string tag) => VersionDir(tag) + ".replaced";
    public string PartFile(string assetName) =>
        Names.IsPlaytestAsset(assetName) ? Path.Combine(DownloadsDir, assetName + ".part") : throw new InvalidDataException($"'{assetName}' is not a playtest zip name.");

    /// <summary>
    /// A folder that already is a launcher install: it holds one of the launcher's own files (current.json, update.json while an update runs, or
    /// launcher.lock while a launcher has it open). A folder that merely has a "versions" subfolder is not one, so nothing in it is ever cleaned up.
    /// </summary>
    public static bool LooksLikeRoot(string folder) =>
        File.Exists(Path.Combine(folder, "current.json")) || File.Exists(Path.Combine(folder, "update.json")) || File.Exists(Path.Combine(folder, "launcher.lock"));

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SM Reimpl");
}

/// <summary>What the launcher knows about an installed version (kept in current.json, so it can be shown offline).</summary>
public sealed class InstalledVersion
{
    public string Tag { get; set; } = "";
    public string? Title { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? Built { get; set; }
    public string? HtmlUrl { get; set; }
    public string? ZipSha256 { get; set; }
    /// <summary>"download" or "adopted" (copied from a folder the tester had extracted).</summary>
    public string Source { get; set; } = "download";
    public DateTimeOffset InstalledAt { get; set; }
    public string? WhatsNew { get; set; }
}

/// <summary>current.json: the active version and the one before it. Written atomically (a temp file, flushed, then renamed over it).</summary>
public sealed class CurrentPointer
{
    public int Schema { get; set; } = 1;
    public string? Current { get; set; }
    public string? Previous { get; set; }
    public Dictionary<string, InstalledVersion> Versions { get; set; } = new(StringComparer.Ordinal);
}

public enum UpdatePhase { Downloading, Verifying, Extracting, Switching }

/// <summary>update.json: an update in progress, so a crash or a power cut is resumed or cleaned up on the next start.</summary>
public sealed class UpdateState
{
    public string Tag { get; set; } = "";
    public UpdatePhase Phase { get; set; }
    /// <summary>"download" or "adopt".</summary>
    public string Source { get; set; } = "download";
    public string? AssetName { get; set; }
    public string? Url { get; set; }
    public long Size { get; set; }
    public string? ApiSha256 { get; set; }
    public string? BodySha256 { get; set; }
    public string? Title { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? Built { get; set; }
    public string? HtmlUrl { get; set; }
    public string? WhatsNew { get; set; }
    public string? AdoptFrom { get; set; }
    public DateTimeOffset StartedAt { get; set; }

    public static UpdateState For(ReleaseInfo r) => new()
    {
        Tag = r.Tag, Phase = UpdatePhase.Downloading, AssetName = r.Asset.Name, Url = r.Asset.DownloadUrl, Size = r.Asset.Size,
        ApiSha256 = r.Asset.ApiSha256, BodySha256 = r.BodySha256, Title = r.Title, PublishedAt = r.PublishedAt, HtmlUrl = r.HtmlUrl,
        WhatsNew = r.WhatsNew, StartedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>The same release (tag, file, size and hashes): a .part of it may be resumed.</summary>
    public bool SameDownload(ReleaseInfo r) =>
        Source == "download" && Tag == r.Tag && AssetName == r.Asset.Name && Size == r.Asset.Size &&
        ApiSha256 == r.Asset.ApiSha256 && BodySha256 == r.BodySha256;

    public InstalledVersion ToInstalled() => new()
    {
        Tag = Tag, Title = Title, PublishedAt = PublishedAt, Built = Built, HtmlUrl = HtmlUrl, ZipSha256 = ApiSha256 ?? BodySha256,
        Source = Source == "adopt" ? "adopted" : "download", InstalledAt = DateTimeOffset.UtcNow, WhatsNew = WhatsNew,
    };
}

/// <summary>Small per-install preferences of the launcher (the last server address typed into Join).</summary>
public sealed class LauncherSettings
{
    public string? LastServer { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(CurrentPointer))]
[JsonSerializable(typeof(UpdateState))]
[JsonSerializable(typeof(LauncherSettings))]
internal sealed partial class LauncherJson : JsonSerializerContext;

/// <summary>Writes a file so that a crash or power cut leaves either the old or the new content: a temp file, flushed to disk, renamed over the target.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(text);
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        for (int attempt = 1; ; attempt++)
        {
            // MoveFileEx(REPLACE_EXISTING): a rename on the same volume. A reader holding the old file without delete sharing (an editor, a virus
            // scanner) makes it fail for a moment: retried briefly; the old content stays whole meanwhile.
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && attempt < 20) { Thread.Sleep(50); }
        }
    }

    /// <summary>Reads a small text file with every kind of sharing, so a reader never blocks <see cref="WriteAllText"/>'s rename.</summary>
    public static string ReadAllText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

public static class Store
{
    public static CurrentPointer? ReadPointer(InstallLayout l) => Read(l.CurrentFile, LauncherJson.Default.CurrentPointer);
    public static void WritePointer(InstallLayout l, CurrentPointer p) => AtomicFile.WriteAllText(l.CurrentFile, JsonSerializer.Serialize(p, LauncherJson.Default.CurrentPointer));
    public static UpdateState? ReadState(InstallLayout l) => Read(l.StateFile, LauncherJson.Default.UpdateState);
    public static void WriteState(InstallLayout l, UpdateState s) => AtomicFile.WriteAllText(l.StateFile, JsonSerializer.Serialize(s, LauncherJson.Default.UpdateState));
    public static void ClearState(InstallLayout l) { if (File.Exists(l.StateFile)) File.Delete(l.StateFile); }
    public static LauncherSettings ReadSettings(InstallLayout l) { try { return Read(l.SettingsFile, LauncherJson.Default.LauncherSettings) ?? new(); } catch (InvalidDataException) { return new(); } }
    public static void WriteSettings(InstallLayout l, LauncherSettings s) => AtomicFile.WriteAllText(l.SettingsFile, JsonSerializer.Serialize(s, LauncherJson.Default.LauncherSettings));

    /// <summary>Null when the file does not exist; <see cref="InvalidDataException"/> when it cannot be read as JSON.</summary>
    static T? Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(AtomicFile.ReadAllText(path), info); }
        catch (JsonException e) { throw new InvalidDataException($"{Path.GetFileName(path)} cannot be read.", e); }
        catch (FileNotFoundException) { return null; }
    }
}

/// <summary>One launcher at a time per install folder: an exclusively opened lock file (deleted when released).</summary>
public sealed class RootLock : IDisposable
{
    readonly FileStream _fs;
    RootLock(FileStream fs) => _fs = fs;

    public static RootLock? TryAcquire(InstallLayout l, TimeSpan wait)
    {
        Directory.CreateDirectory(l.Root);
        var until = DateTime.UtcNow + wait;
        while (true)
        {
            try { return new RootLock(new FileStream(l.LockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(200); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(200); }
        }
    }

    public void Dispose() => _fs.Dispose();
}
