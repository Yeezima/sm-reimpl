using System.Text.Json;
using System.Text.Json.Nodes;

namespace SmContent;

/// <summary>Where the game folder came from: the folder and a short human label ("Steam library D:\SteamLibrary", "SM_GAME_DIR", ...).</summary>
public sealed record GameLocation(string Dir, string Source);

/// <summary>
/// Finds the user's game folder without manual steps. Order: explicit path, SM_GAME_DIR, config/local.json "gameDir", the per-user
/// <c>&lt;DataRoot&gt;/settings.json</c> "gameDir" (written by <see cref="Remember"/>), the newest discovery run's game-dir.txt (written by m1.cmd /
/// discover.cmd under %LOCALAPPDATA%\sm-reimpl), then Steam (registry or the usual Steam folders, every library in <c>libraryfolders.vdf</c>, the
/// app 55150 manifest's <c>installdir</c>).
/// </summary>
/// <remarks>
/// <see cref="DataRoot"/> (the private data and cache root) is %LOCALAPPDATA%\sm-reimpl by default, or the folder named by the
/// environment variable <see cref="DataDirEnvVar"/> (SM_DATA_DIR) when it is set, e.g. a mounted volume on a Linux server.
/// The install is only ever read. The one file written here is settings.json, atomically and only under the data root.
/// </remarks>
public static class GameInstall
{
    public const string DataDirEnvVar = "SM_DATA_DIR";
    public const string SteamDirEnvVar = "SM_STEAM_DIR";
    public const string SettingsFileName = "settings.json";
    /// <summary>Steam's app id of Warhammer 40,000: Space Marine (2011).</summary>
    public const string SteamAppId = "55150";
    /// <summary>The install folder name Steam gives the game by default (the fallback when the app manifest is missing).</summary>
    public const string DefaultInstallDir = "Warhammer 40,000 Space Marine";
    /// <summary>A multiplayer archive the install must hold (the player blueprints, read by <c>GameplayTuning</c>): the light sanity check of automatic finds and of Browse.</summary>
    public const string SanityArchive = "data/packages/ebps.oppc";

    public static string DataRoot => DataRootFor(Environment.GetEnvironmentVariable);

    /// <summary>
    /// The data and cache root for an environment lookup: the full path of SM_DATA_DIR when it is set and not blank, else
    /// <c>&lt;LocalApplicationData&gt;/sm-reimpl</c> (%LOCALAPPDATA%\sm-reimpl on Windows, ~/.local/share/sm-reimpl on Linux).
    /// </summary>
    public static string DataRootFor(Func<string, string?> getEnv)
    {
        string? over = getEnv(DataDirEnvVar);
        if (!string.IsNullOrWhiteSpace(over)) return Path.GetFullPath(over.Trim());
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sm-reimpl");
    }

    public static string SettingsPath => Path.Combine(DataRoot, SettingsFileName);

    public static string Locate(string? explicitDir = null, string? repoRoot = null) =>
        TryLocate(explicitDir, repoRoot)?.Dir ?? throw new DirectoryNotFoundException(NotFoundMessage);

    /// <summary><see cref="Locate(string?, string?)"/> with the machine's parts injected (tests).</summary>
    public static string Locate(string? explicitDir, string? repoRoot, string dataRoot, Func<string, string?> getEnv, Func<IEnumerable<string>> steamRoots) =>
        TryLocate(explicitDir, repoRoot, dataRoot, getEnv, steamRoots)?.Dir ?? throw new DirectoryNotFoundException(NotFoundMessage);

    public const string NotFoundMessage =
        "Space Marine install not found. Install it on Steam, set SM_GAME_DIR, pass --game-dir, or put \"gameDir\" in config/local.json (or in the settings.json under the data folder).";

    /// <summary>The game folder and where it came from, or null when none of the places has a valid install.</summary>
    public static GameLocation? TryLocate(string? explicitDir = null, string? repoRoot = null) =>
        TryLocate(explicitDir, repoRoot, DataRoot, Environment.GetEnvironmentVariable, SteamRoots);

    /// <summary>The same with the machine's parts injected (tests): the data root, the environment and the Steam folders to look in.</summary>
    public static GameLocation? TryLocate(string? explicitDir, string? repoRoot, string dataRoot, Func<string, string?> getEnv, Func<IEnumerable<string>> steamRoots)
    {
        foreach (var (candidate, source, strict) in Candidates(explicitDir, repoRoot, dataRoot, getEnv, steamRoots))
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            bool ok;
            try { ok = strict ? Validate(candidate) is null : Directory.Exists(Path.Combine(candidate, "data")); }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { ok = false; }
            if (ok) return new GameLocation(Path.GetFullPath(candidate), source);
        }
        return null;
    }

    /// <summary>
    /// null when <paramref name="dir"/> looks like a Space Marine install (it has <c>data/</c> and <see cref="SanityArchive"/>), else a sentence saying
    /// what is missing. Reads directory entries only.
    /// </summary>
    public static string? Validate(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return "No folder chosen.";
        try
        {
            if (!Directory.Exists(dir)) return "That folder does not exist.";
            if (!Directory.Exists(Path.Combine(dir, "data"))) return "There is no \"data\" folder in it. Choose the folder that contains \"data\" (Steam: steamapps/common/" + DefaultInstallDir + ").";
            if (!File.Exists(Path.Combine(dir, SanityArchive.Replace('/', Path.DirectorySeparatorChar)))) return $"The \"data\" folder has no {SanityArchive}: this does not look like Space Marine (2011).";
            return null;
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "That folder cannot be read: " + e.Message;
        }
    }

    /// <summary>A folder the user picked: when it is the game's own <c>data</c> folder, its parent (a common slip); otherwise unchanged.</summary>
    public static string Normalize(string dir)
    {
        string trimmed = dir.TrimEnd('/', '\\');
        if (trimmed.Length > 0 && string.Equals(Path.GetFileName(trimmed), "data", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(trimmed) is { Length: > 0 } parent
            && Validate(trimmed) is not null && Validate(parent) is null) return parent;
        return dir;
    }

    static IEnumerable<(string? Dir, string Source, bool Strict)> Candidates(string? explicitDir, string? repoRoot, string dataRoot, Func<string, string?> getEnv, Func<IEnumerable<string>> steamRoots)
    {
        yield return (explicitDir, "path given on the command line", false);
        yield return (getEnv("SM_GAME_DIR"), "SM_GAME_DIR", false);
        if (repoRoot is not null) yield return (ReadGameDir(Path.Combine(repoRoot, "config", "local.json")), "config/local.json", false);
        yield return (ReadGameDir(Path.Combine(dataRoot, SettingsFileName)), "your saved choice", true);
        foreach (var dir in DiscoveredDirs(dataRoot)) yield return (dir, "discovery run", false);
        foreach (var (dir, library) in SteamInstalls(steamRoots())) yield return (dir, "Steam library " + library, true);
    }

    /// <summary>Largest settings, JSON, VDF or ACF file read: these are tiny, so anything bigger is not what it claims to be (and could be a slow or hostile read).</summary>
    public const long MaxTextFileBytes = 4 * 1024 * 1024;

    /// <summary>The text of a small file, or null when it is missing, too big or unreadable. ReadAllText drops the BOM PowerShell 5.1 writes.</summary>
    static string? SmallText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxTextFileBytes) return null;
            return File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The game folders the discovery runs recorded (newest first); a missing or unreadable discovery folder gives none.</summary>
    static List<string> DiscoveredDirs(string dataRoot)
    {
        var found = new List<string>();
        try
        {
            string discovery = Path.Combine(dataRoot, "discovery");
            if (!Directory.Exists(discovery)) return found;
            foreach (var run in Directory.GetDirectories(discovery).OrderByDescending(d => d, StringComparer.Ordinal))
                if (SmallText(Path.Combine(run, "game-dir.txt")) is { } text) found.Add(text.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        return found;
    }

    static string? ReadGameDir(string jsonFile)
    {
        if (SmallText(jsonFile) is not { } text) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("gameDir", out var g) && g.ValueKind == JsonValueKind.String) return g.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    // ---- Steam ----

    /// <summary>
    /// The Steam folders of this machine: on Windows the registry (<c>HKCU\Software\Valve\Steam</c> SteamPath, <c>HKLM\SOFTWARE\WOW6432Node\Valve\Steam</c> InstallPath)
    /// and the default Program Files folder; on Linux the usual places under the home folder (a Proton install has the same layout). Nothing is checked to exist here.
    /// </summary>
    public static IEnumerable<string> SteamRoots() => SteamRootsFor(Environment.GetEnvironmentVariable);

    /// <summary>
    /// <see cref="SteamRoots"/> for an environment lookup. When <see cref="SteamDirEnvVar"/> (SM_STEAM_DIR) is set, its folders (separated like PATH; empty means none)
    /// are the only Steam folders: for tests and unusual setups.
    /// </summary>
    public static IEnumerable<string> SteamRootsFor(Func<string, string?> getEnv)
    {
        if (getEnv(SteamDirEnvVar) is { } over)
            return over.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            AddRegistry(found, Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistry(found, Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            AddRegistry(found, Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
            if (Environment.GetEnvironmentVariable("ProgramFiles(x86)") is { Length: > 0 } pf) found.Add(Path.Combine(pf, "Steam"));
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length > 0)
            {
                found.Add(Path.Combine(home, ".steam", "steam"));
                found.Add(Path.Combine(home, ".local", "share", "Steam"));
                found.Add(Path.Combine(home, ".steam", "root"));
                found.Add(Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", ".local", "share", "Steam"));
            }
        }
        return found;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static void AddRegistry(List<string> into, Microsoft.Win32.RegistryKey hive, string subKey, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            if (key?.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value)) into.Add(value.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
    }

    /// <summary>Every library folder of the given Steam folders: the Steam folder itself and each path in <c>steamapps/libraryfolders.vdf</c> (new and old layout), without repeats.</summary>
    public static IEnumerable<string> SteamLibraries(IEnumerable<string> steamRoots)
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var root in steamRoots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string full;
            try { full = Path.GetFullPath(root); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!Directory.Exists(full)) continue;
            if (seen.Add(full)) yield return full;
            foreach (var lib in LibraryPaths(Path.Combine(full, "steamapps", "libraryfolders.vdf")))
            {
                string libFull;
                try { libFull = Path.GetFullPath(lib); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (Directory.Exists(libFull) && seen.Add(libFull)) yield return libFull;
            }
        }
    }

    static IEnumerable<string> LibraryPaths(string vdfFile)
    {
        VdfNode? root = null;
        if (SmallText(vdfFile) is { } text && VdfParser.TryParse(text, out var parsed)) root = parsed.Get("libraryfolders");
        if (root is null || !root.IsBlock) yield break;
        foreach (var entry in root.Children)
        {
            // New layout: "0" { "path" "D:\\SteamLibrary" ... }. Old layout: "1" "D:\\SteamLibrary" (numeric keys only; "TimeNextStatsReport" etc. are not paths).
            string? path = entry.Value.IsBlock ? entry.Value.GetString("path") : int.TryParse(entry.Key, out _) ? entry.Value.Value : null;
            if (!string.IsNullOrWhiteSpace(path)) yield return path;
        }
    }

    /// <summary>
    /// The game's folder in each library of the Steam folders (not yet validated): <c>steamapps/common/&lt;installdir&gt;</c> from <c>appmanifest_55150.acf</c>, else the
    /// default folder name when that exists. Each result comes with its library.
    /// </summary>
    public static IEnumerable<(string Dir, string Library)> SteamInstalls(IEnumerable<string> steamRoots)
    {
        foreach (var library in SteamLibraries(steamRoots))
        {
            string apps = Path.Combine(library, "steamapps");
            string common = Path.Combine(apps, "common");
            string? installDir = null;
            string manifest = Path.Combine(apps, $"appmanifest_{SteamAppId}.acf");
            if (SmallText(manifest) is { } acfText && VdfParser.TryParse(acfText, out var acf))
                installDir = acf.Get("AppState")?.GetString("installdir");
            // installdir is one folder name below steamapps/common; anything with a separator or ".." is not trusted.
            if (!string.IsNullOrWhiteSpace(installDir) && installDir.IndexOfAny(['/', '\\', ':']) < 0 && installDir != ".." && installDir != ".")
                yield return (Path.Combine(common, installDir), library);
            else
                yield return (Path.Combine(common, DefaultInstallDir), library);
        }
    }

    // ---- remembering ----

    /// <summary>
    /// The full path with every existing link (symbolic link or junction) on the way replaced by its target, so a data folder that points into the game folder is
    /// recognised as inside it. Parts that do not exist yet are kept as they are.
    /// </summary>
    public static string RealPath(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? "";
        string current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                if (info.Exists && info.LinkTarget is not null && info.ResolveLinkTarget(true) is { } target) current = Path.GetFullPath(target.FullName);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        }
        return current;
    }

    /// <summary>
    /// Saves the game folder in <c>&lt;DataRoot&gt;/settings.json</c> (atomically: a temp file next to it, then a move; other keys in the file are kept). Returns null on
    /// success, else a sentence saying why not. Refuses a folder that does not validate and never writes inside the game folder.
    /// </summary>
    public static string? Remember(string dir) => Remember(dir, DataRoot);

    public static string? Remember(string dir, string dataRoot)
    {
        if (Validate(dir) is { } problem) return problem;
        string game, realGame;
        string settings;
        try
        {
            game = Path.GetFullPath(dir);
            realGame = RealPath(dir);   // links resolved: a data folder that is a link into the install is inside it
            settings = Path.Combine(RealPath(dataRoot), SettingsFileName);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return e.Message; }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string gameWithSep = realGame.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (settings.StartsWith(gameWithSep, comparison)) return "The settings are never written inside the game folder; set SM_DATA_DIR to a folder outside it.";
        string temp = settings + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
            JsonObject obj = new();
            if (File.Exists(settings))
            {
                try { if (SmallText(settings) is { } old && JsonNode.Parse(old) is JsonObject existing) obj = existing; }
                catch (JsonException) { }   // a corrupt file is replaced
            }
            obj["gameDir"] = game;
            File.WriteAllText(temp, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, settings, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
            return "Could not save the choice: " + e.Message;
        }
    }
}
