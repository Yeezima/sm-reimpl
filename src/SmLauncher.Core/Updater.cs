namespace SmLauncher.Core;

/// <summary>What an update or check is doing now, for the progress display.</summary>
public sealed record UpdateStatus(UpdatePhase? Phase, string Message, double? Fraction, DownloadProgress? Download = null);

/// <summary>What the start-up recovery found and did, and an update it left to resume (null when none).</summary>
public sealed record RecoveryReport(IReadOnlyList<string> Notes, UpdateState? Pending);

/// <summary>An IProgress that calls its delegate on the reporting thread (the UI wraps it in its own Progress&lt;T&gt;).</summary>
public sealed class ActionProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

/// <summary>
/// Installs, switches, rolls back and repairs versions in an <see cref="InstallLayout"/>. The rules that keep the previous version playable whatever
/// happens: a version is unpacked into versions\&lt;tag&gt;.staging, checked against the zip's hash and its own manifest, and only then renamed to
/// versions\&lt;tag&gt;; current.json is replaced atomically after that, and only versions that are neither current nor previous are ever deleted.
/// update.json records the phase, so <see cref="Recover"/> resumes or cleans up after a crash or a power cut. Callers hold the <see cref="RootLock"/>.
/// </summary>
public sealed class Updater(InstallLayout layout, Downloader? downloader, Action<string>? log = null)
{
    readonly Action<string> _log = log ?? (_ => { });

    /// <summary>Free bytes on a path's drive, checked before an unpack or a copy (tests replace it).</summary>
    public Func<string, long> FreeBytes { get; init; } = DiskSpace.DefaultFreeBytes;

    public InstallLayout Layout => layout;

    public CurrentPointer ReadPointer()
    {
        try { return Store.ReadPointer(layout) ?? new CurrentPointer(); }
        catch (InvalidDataException) { return new CurrentPointer(); }
    }

    public UpdateState? ReadState()
    {
        try { return Store.ReadState(layout) is { } s && Names.IsTag(s.Tag) ? s : null; }
        catch (InvalidDataException) { return null; }
    }

    /// <summary>The version folder holds the game (a cheap check; <see cref="CheckFiles"/> hashes every file).</summary>
    public bool IsUsable(string? tag) => tag is not null && Names.IsTag(tag) && IsUsableDir(layout.VersionDir(tag));
    static bool IsUsableDir(string dir) => File.Exists(Path.Combine(dir, PackageLayout.GameExe));

    /// <summary>The active version's folder, or null when nothing playable is installed.</summary>
    public string? CurrentDir() { var p = ReadPointer(); return IsUsable(p.Current) ? layout.VersionDir(p.Current!) : null; }

    // ---- start-up recovery ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Run first on every start. Finishes an interrupted switch (after checking the files again), cleans up a half-done extraction, keeps a
    /// partial download for resuming, removes leftovers no update refers to, and makes current.json point at a playable version: the previous one
    /// when the current one is broken, or the newest installed one when the file is missing or unreadable.
    /// </summary>
    public RecoveryReport Recover(CancellationToken ct = default)
    {
        var notes = new List<string>();
        Directory.CreateDirectory(layout.Root);
        foreach (var tmp in new[] { layout.CurrentFile + ".tmp", layout.StateFile + ".tmp", layout.SettingsFile + ".tmp" }) TryDelete(tmp);
        UpdateState? state = null;
        try { state = Store.ReadState(layout); }
        catch (InvalidDataException) { Store.ClearState(layout); notes.Add("An unreadable update record was removed."); }
        if (state is not null && !Names.IsTag(state.Tag)) { Store.ClearState(layout); state = null; }

        if (state is not null)
        {
            switch (state.Phase)
            {
                case UpdatePhase.Downloading:
                case UpdatePhase.Verifying:
                    TryDeleteDir(layout.StagingDir(state.Tag));
                    if (state.Source == "adopt") { Store.ClearState(layout); state = null; }
                    else notes.Add($"The update to {state.Tag} was interrupted; it continues where it stopped.");
                    break;
                case UpdatePhase.Extracting:
                    TryDeleteDir(layout.StagingDir(state.Tag));
                    if (state.Source == "adopt") { Store.ClearState(layout); notes.Add("An interrupted install from a folder was cleaned up; the previous version is unchanged."); state = null; }
                    else notes.Add($"The update to {state.Tag} was interrupted while unpacking; the half-unpacked files were removed and it unpacks again.");
                    break;
                case UpdatePhase.Switching:
                    var s = state;
                    try
                    {
                        CompleteSwitch(s, verify: true, ct);
                        FinishInstall(s);
                        notes.Add($"An interrupted switch to {s.Tag} was completed after checking its files.");
                        state = null;
                    }
                    catch (VerificationException e)
                    {
                        TryDeleteDir(layout.StagingDir(s.Tag));
                        if (s.Source == "adopt") { Store.ClearState(layout); state = null; }
                        else { s.Phase = UpdatePhase.Verifying; Store.WriteState(layout, s); }
                        notes.Add($"An interrupted switch to {s.Tag} was not completed ({e.Message}); the previous version stays active.");
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        notes.Add($"An interrupted switch to {s.Tag} has to wait: {e.Message}");      // kept as it is: the next start or Update finishes it
                    }
                    break;
            }
        }
        CleanStray(state, notes);
        RepairPointer(notes);
        CleanupOldVersions(state);
        foreach (var n in notes) _log("recover: " + n);
        return new RecoveryReport(notes, state);
    }

    void CleanStray(UpdateState? state, List<string> notes)
    {
        if (Directory.Exists(layout.VersionsDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(layout.VersionsDir))
            {
                string name = Path.GetFileName(dir);
                if (name.EndsWith(".staging", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Names.IsTag(name[..^".staging".Length])) continue;
                    if (state is null || !string.Equals(layout.StagingDir(state.Tag), dir, StringComparison.OrdinalIgnoreCase)) TryDeleteDir(dir);
                }
                else if (name.EndsWith(".replaced", StringComparison.OrdinalIgnoreCase))
                {
                    string tag = name[..^".replaced".Length];
                    string final = Path.Combine(layout.VersionsDir, tag);
                    if (Names.IsTag(tag) && !Directory.Exists(final))
                    {
                        try { Directory.Move(dir, final); notes.Add($"{tag} was put back after an interrupted reinstall."); }
                        catch (IOException) { }
                    }
                    else TryDeleteDir(dir);
                }
                else if (name.Contains(".trash-", StringComparison.OrdinalIgnoreCase) && Names.IsTag(name[..name.IndexOf(".trash-", StringComparison.OrdinalIgnoreCase)])) TryDeleteDir(dir);
            }
        }
        if (Directory.Exists(layout.DownloadsDir))
        {
            // Only the launcher's own files: <playtest zip name>.part. Anything else someone put in downloads\ stays.
            string? keep = state is { Source: "download", AssetName: { } a } && Names.IsPlaytestAsset(a) ? layout.PartFile(a) : null;
            foreach (var f in Directory.EnumerateFiles(layout.DownloadsDir, "*.part"))
            {
                string name = Path.GetFileName(f);
                if (Names.IsPlaytestAsset(name[..^".part".Length]) && !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            }
        }
    }

    void RepairPointer(List<string> notes)
    {
        CurrentPointer p;
        bool changed = false;
        try { p = Store.ReadPointer(layout) ?? new CurrentPointer(); }
        catch (InvalidDataException) { p = new CurrentPointer(); changed = true; notes.Add("current.json could not be read; it was rebuilt from the installed versions."); }
        if (p.Current is not null && !IsUsable(p.Current))
        {
            string bad = p.Current;
            if (p.Previous is not null && IsUsable(p.Previous)) { p.Current = p.Previous; p.Previous = null; notes.Add($"{bad} is incomplete, so the launcher went back to {p.Current}."); }
            else { p.Current = null; notes.Add($"{bad} is incomplete and there is no earlier version to go back to."); }
            changed = true;
        }
        if (p.Previous is not null && (!IsUsable(p.Previous) || p.Previous == p.Current)) { p.Previous = null; changed = true; }
        if (p.Current is null)
        {
            var installed = InstalledTags()
                .OrderByDescending(t => p.Versions.TryGetValue(t, out var v) ? v.InstalledAt : DateTimeOffset.MinValue)
                .ThenByDescending(t => Directory.GetLastWriteTimeUtc(layout.VersionDir(t)))
                .ToList();
            if (installed.Count > 0)
            {
                p.Current = installed[0];
                p.Previous = installed.Count > 1 ? installed[1] : null;
                changed = true;
                notes.Add($"The active version is now {p.Current} (found installed).");
            }
        }
        foreach (var t in p.Versions.Keys.ToList())
            if (!Names.IsTag(t) || !Directory.Exists(layout.VersionDir(t))) { p.Versions.Remove(t); changed = true; }
        if (changed) Store.WritePointer(layout, p);
    }

    /// <summary>Playable version folders under versions\ (no staging, replaced or trash folders).</summary>
    public IEnumerable<string> InstalledTags() =>
        Directory.Exists(layout.VersionsDir)
            ? Directory.EnumerateDirectories(layout.VersionsDir).Select(Path.GetFileName).Where(n => Names.IsTag(n) && IsUsable(n)).Select(n => n!)
            : [];

    /// <summary>Deletes every version but the current and the previous one (and the one an update is working on). A folder in use is skipped.</summary>
    public void CleanupOldVersions(UpdateState? state = null)
    {
        if (!Directory.Exists(layout.VersionsDir)) return;
        var p = ReadPointer();
        state ??= ReadState();
        var keep = new HashSet<string?>(StringComparer.OrdinalIgnoreCase) { p.Current, p.Previous, state?.Tag };
        foreach (var dir in Directory.EnumerateDirectories(layout.VersionsDir))
        {
            string name = Path.GetFileName(dir);
            if (!Names.IsTag(name) || keep.Contains(name)) continue;
            string trash = dir + ".trash-" + Guid.NewGuid().ToString("N")[..8];
            try { Directory.Move(dir, trash); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log($"cleanup: {name} is in use, kept for now"); continue; }
            TryDeleteDir(trash);
            _log($"cleanup: removed {name}");
        }
        bool changed = false;
        foreach (var t in p.Versions.Keys.ToList())
            if (!keep.Contains(t) && !Directory.Exists(Path.Combine(layout.VersionsDir, t))) { p.Versions.Remove(t); changed = true; }
        if (changed) Store.WritePointer(layout, p);
    }

    // ---- install from a release ----------------------------------------------------------------------------------------------

    /// <summary>
    /// Downloads (resuming a .part of the same release), verifies, unpacks, checks and switches to <paramref name="release"/>. A version that is installed
    /// already is checked and switched to without a download. Throws <see cref="DownloadFailedException"/>, <see cref="VerificationException"/> or
    /// <see cref="OperationCanceledException"/>; in every case the active version stays as it was.
    /// </summary>
    /// <remarks>With <paramref name="repair"/> the active version itself is installed again when its files are damaged (Check files).</remarks>
    public async Task<string> InstallAsync(ReleaseInfo release, IProgress<UpdateStatus>? progress, CancellationToken ct, bool repair = false)
    {
        Names.CheckTag(release.Tag);
        if (!Names.IsPlaytestAsset(release.Asset.Name)) throw new InvalidDataException("The release's file is not a playtest zip.");
        _ = new Uri(release.Asset.DownloadUrl);
        Directory.CreateDirectory(layout.VersionsDir);
        Directory.CreateDirectory(layout.DownloadsDir);
        var pointer = ReadPointer();
        if (pointer.Current == release.Tag && IsUsable(release.Tag) && !repair) return release.Tag;

        string final = layout.VersionDir(release.Tag);
        if (IsUsableDir(final))
        {
            progress?.Report(new UpdateStatus(UpdatePhase.Verifying, $"{release.Tag} is installed already: checking its files", null));
            var report = await Task.Run(() => Manifest.Verify(final, FilesProgress(progress, UpdatePhase.Verifying, "Checking the installed files"), ct), ct).ConfigureAwait(false);
            if (report.Ok)
            {
                var existing = ReadState();
                if (existing is not null && existing.Tag == release.Tag) Store.ClearState(layout);
                var meta = pointer.Versions.GetValueOrDefault(release.Tag) ?? UpdateState.For(release).ToInstalled();
                SwitchPointer(release.Tag, meta);
                CleanupOldVersions();
                _log($"switched to the installed {release.Tag}");
                return release.Tag;
            }
            _log($"the installed {release.Tag} is damaged ({report.Describe()}); installing it again");
        }

        var state = ReadState();
        if (state is not null && !state.SameDownload(release)) { Discard(state); state = null; }
        state ??= UpdateState.For(release);
        return await RunAsync(state, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Continues the update update.json records (after a restart, a cancel or a failure), from its phase. Null when there is none.</summary>
    public async Task<string?> ResumeAsync(IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        var state = ReadState();
        if (state is null || state.Source != "download" || state.AssetName is null || state.Url is null) return null;
        return await RunAsync(state, progress, ct).ConfigureAwait(false);
    }

    async Task<string> RunAsync(UpdateState state, IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        if (downloader is null) throw new InvalidOperationException("No downloader.");
        if (!Names.IsPlaytestAsset(state.AssetName)) throw new InvalidDataException("The update's file is not a playtest zip.");
        // Before a single byte is fetched: the release must state a hash, and its two hashes must agree.
        try { Hashes.CheckStated(state.ApiSha256, state.BodySha256, state.AssetName!); }
        catch (VerificationException) { Discard(state); throw; }
        var url = new Uri(state.Url!);
        Directory.CreateDirectory(layout.VersionsDir);
        Directory.CreateDirectory(layout.DownloadsDir);
        string part = layout.PartFile(state.AssetName!);
        bool verifiedNow = false, extractedNow = false;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            switch (state.Phase)
            {
                case UpdatePhase.Downloading:
                    Store.WriteState(layout, state);
                    long have = File.Exists(part) ? new FileInfo(part).Length : 0;
                    _log($"download {state.Tag}: {state.AssetName} ({state.Size} bytes){(have > 0 ? $", resuming at {have} bytes" : "")}");
                    await downloader.DownloadAsync(url, part, state.Size,
                        new ActionProgress<DownloadProgress>(d => progress?.Report(new UpdateStatus(UpdatePhase.Downloading, d.Note ?? "Downloading", d.Fraction, d))), ct).ConfigureAwait(false);
                    state.Phase = UpdatePhase.Verifying;
                    Store.WriteState(layout, state);
                    break;

                case UpdatePhase.Verifying:
                    if (!File.Exists(part) || new FileInfo(part).Length != state.Size) { state.Phase = UpdatePhase.Downloading; break; }
                    progress?.Report(new UpdateStatus(UpdatePhase.Verifying, "Checking the download (SHA-256)", 0));
                    string actual = await Hashes.Sha256FileAsync(part,
                        new ActionProgress<long>(b => progress?.Report(new UpdateStatus(UpdatePhase.Verifying, "Checking the download (SHA-256)", (double)b / Math.Max(1, state.Size)))), ct).ConfigureAwait(false);
                    try { Hashes.CheckZip(actual, state.ApiSha256, state.BodySha256, state.AssetName!); }
                    catch (VerificationException e) { TryDelete(part); Store.ClearState(layout); _log("verify: " + e.Message); throw; }
                    _log($"verify {state.Tag}: SHA-256 {actual} matches (api: {state.ApiSha256 is not null}, description: {state.BodySha256 is not null})");
                    verifiedNow = true;
                    state.Phase = UpdatePhase.Extracting;
                    Store.WriteState(layout, state);
                    break;

                case UpdatePhase.Extracting:
                    if (!verifiedNow) { state.Phase = UpdatePhase.Verifying; break; }       // after a restart the zip is checked again before it is opened
                    await Task.Run(() => ExtractAndCheck(state, part, progress, ct), ct).ConfigureAwait(false);
                    extractedNow = true;
                    state.Phase = UpdatePhase.Switching;
                    Store.WriteState(layout, state);
                    break;

                case UpdatePhase.Switching:
                    progress?.Report(new UpdateStatus(UpdatePhase.Switching, $"Switching to {state.Tag}", null));
                    try { CompleteSwitch(state, verify: !extractedNow, ct); }
                    catch (VerificationException e) when (!extractedNow)
                    {
                        _log("switch: " + e.Message + "; unpacking again");
                        TryDeleteDir(layout.StagingDir(state.Tag));
                        state.Phase = UpdatePhase.Verifying;
                        Store.WriteState(layout, state);
                        break;
                    }
                    FinishInstall(state);
                    _log($"installed {state.Tag}");
                    return state.Tag;
            }
        }
    }

    void ExtractAndCheck(UpdateState state, string zip, IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        string staging = layout.StagingDir(state.Tag);
        DeleteDir(staging);
        try
        {
            progress?.Report(new UpdateStatus(UpdatePhase.Extracting, "Unpacking", 0));
            int files = SafeZip.Extract(zip, staging,
                new ActionProgress<(long Done, long Total)>(x => progress?.Report(new UpdateStatus(UpdatePhase.Extracting, "Unpacking", (double)x.Done / Math.Max(1, x.Total)))), ct, FreeBytes);
            var report = Manifest.Verify(staging, FilesProgress(progress, UpdatePhase.Extracting, "Checking the unpacked files"), ct);
            if (report.HasManifest && !report.Ok)
                throw new VerificationException($"The unpacked files do not match the build's own file list: {report.Describe()}. Nothing was changed.");
            if (!IsUsableDir(staging)) throw new VerificationException($"The build has no {PackageLayout.GameExe}. Nothing was changed.");
            _log($"extract {state.Tag}: {files} files; manifest: {(report.HasManifest ? report.Describe() : "none in this build")}");
            CarryOverFromCurrent(staging);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TryDeleteDir(staging);
            if (e is VerificationException)
            {
                state.Phase = UpdatePhase.Verifying;      // the verified zip is kept: the next try unpacks it again without a download
                Store.WriteState(layout, state);
            }
            throw;
        }
        catch (OperationCanceledException) { TryDeleteDir(staging); throw; }
    }

    void CarryOverFromCurrent(string staging)
    {
        string? current = CurrentDir();
        if (current is null) return;
        foreach (var rel in CarryOver.Copy(current, staging, _log)) _log($"carried over {rel} from {Path.GetFileName(current)}");
    }

    /// <summary>Moves a checked staging folder into place (an existing copy of the same tag is set aside first). With <paramref name="verify"/> the files are hashed again.</summary>
    void CompleteSwitch(UpdateState state, bool verify, CancellationToken ct)
    {
        string final = layout.VersionDir(state.Tag), staging = layout.StagingDir(state.Tag), replaced = layout.ReplacedDir(state.Tag);
        if (Directory.Exists(staging))
        {
            if (verify) Require(staging, ct);
            try
            {
                if (Directory.Exists(final)) { DeleteDir(replaced); Directory.Move(final, replaced); }
                Directory.Move(staging, final);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"{state.Tag} could not be put in place ({e.Message}). Close the game and the server, then try again.", e);
            }
        }
        else
        {
            if (!Directory.Exists(final)) throw new VerificationException($"The unpacked files of {state.Tag} are gone.");
            if (verify) Require(final, ct);
        }
    }

    static void Require(string dir, CancellationToken ct)
    {
        var r = Manifest.Verify(dir, null, ct);
        bool ok = r.HasManifest ? r.Ok : IsUsableDir(dir);
        if (!ok) throw new VerificationException($"its files are not intact: {r.Describe()}");
    }

    void FinishInstall(UpdateState state)
    {
        SwitchPointer(state.Tag, state.ToInstalled());
        Store.ClearState(layout);
        if (state.Source == "download" && state.AssetName is { } a && Names.IsPlaytestAsset(a)) TryDelete(layout.PartFile(a));
        TryDeleteDir(layout.ReplacedDir(state.Tag));
        CleanupOldVersions();
    }

    void SwitchPointer(string tag, InstalledVersion meta)
    {
        var p = ReadPointer();
        if (p.Current != tag)
        {
            if (p.Current is not null && IsUsable(p.Current)) p.Previous = p.Current;
            p.Current = tag;
        }
        if (p.Previous == tag) p.Previous = null;
        meta.Tag = tag;
        if (p.Versions.TryGetValue(tag, out var old) && meta.Source == "download" && old.Source == "adopted" && meta.ZipSha256 is null) meta.Source = old.Source;
        p.Versions[tag] = meta;
        Store.WritePointer(layout, p);
    }

    /// <summary>Drops an update that will not be continued: its .part file and its staging folder.</summary>
    void Discard(UpdateState state)
    {
        if (state.AssetName is { } a && Names.IsPlaytestAsset(a)) TryDelete(layout.PartFile(a));
        if (Names.IsTag(state.Tag)) TryDeleteDir(layout.StagingDir(state.Tag));
        Store.ClearState(layout);
        _log($"dropped the unfinished update to {state.Tag}");
    }

    // ---- adopt a folder the tester extracted ---------------------------------------------------------------------------------

    /// <summary>
    /// Installs an already-extracted playtest folder (the one the launcher came in) as a version: copied into staging, checked against its own manifest
    /// (required here: there is no zip hash to rely on), then switched to like a download. The source folder is only read.
    /// </summary>
    public async Task<string> AdoptAsync(string folder, IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        if (IsInsideFolder(folder, layout.Root) || IsInsideFolder(layout.Root, folder)) throw new InvalidOperationException("That folder overlaps the launcher's install folder; choose another install folder.");
        var v = PackageLayout.ReadVersion(folder) ?? throw new InvalidDataException("That folder is not an unpacked playtest zip (notes\\VERSION.txt is missing).");
        string tag = v.Tag ?? throw new InvalidDataException($"The folder's version '{v.Version}' gives no usable tag.");
        if (!IsUsableDir(folder)) throw new InvalidDataException($"That folder has no {PackageLayout.GameExe}.");
        Directory.CreateDirectory(layout.VersionsDir);
        var meta = new InstalledVersion { Tag = tag, Title = $"sm-reimpl {v.Version}", Built = v.Built, Source = "adopted", InstalledAt = DateTimeOffset.UtcNow };
        if (ReadPointer().Current == tag && IsUsable(tag)) return tag;
        if (IsUsable(tag))
        {
            var r = await Task.Run(() => Manifest.Verify(layout.VersionDir(tag), FilesProgress(progress, UpdatePhase.Verifying, "Checking the installed files"), ct), ct).ConfigureAwait(false);
            if (r.Ok) { SwitchPointer(tag, ReadPointer().Versions.GetValueOrDefault(tag) ?? meta); CleanupOldVersions(); return tag; }
        }
        string manifestPath = Path.Combine(folder, PackageLayout.ManifestPath);
        if (!File.Exists(manifestPath))
            throw new VerificationException("The folder has no file list (notes\\CONTENTS-SHA256.txt), so it cannot be checked. Download the build instead.");
        var (entries, unsafeLines) = Manifest.Parse(File.ReadAllText(manifestPath));
        if (entries.Count == 0 || unsafeLines.Count > 0) throw new VerificationException("The folder's file list is empty or unreadable. Download the build instead.");
        var copy = entries.Select(e => e.Path).Append(PackageLayout.ManifestPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ReadState() is { } old) Discard(old);
        var state = new UpdateState
        {
            Tag = tag, Phase = UpdatePhase.Extracting, Source = "adopt", AdoptFrom = folder, Title = meta.Title, Built = v.Built, StartedAt = DateTimeOffset.UtcNow,
        };
        Store.WriteState(layout, state);
        string staging = layout.StagingDir(tag);
        try
        {
            await Task.Run(() =>
            {
                DeleteDir(staging);
                CopyListed(folder, staging, copy, FreeBytes, new ActionProgress<(long Done, long Total)>(x => progress?.Report(new UpdateStatus(UpdatePhase.Extracting, "Copying the build", (double)x.Done / Math.Max(1, x.Total)))), ct);
                var r = Manifest.Verify(staging, FilesProgress(progress, UpdatePhase.Verifying, "Checking the copied files"), ct);
                if (!r.Ok)
                    throw new VerificationException(r.HasManifest
                        ? $"The folder's files do not match its own file list: {r.Describe()}. Download the build instead."
                        : "The folder has no file list (notes\\CONTENTS-SHA256.txt), so it cannot be checked. Download the build instead.");
                CarryOverFromCurrent(staging);
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDir(staging);
            Store.ClearState(layout);
            throw;
        }
        state.Phase = UpdatePhase.Switching;
        Store.WriteState(layout, state);
        CompleteSwitch(state, verify: false, ct);
        FinishInstall(state);
        _log($"adopted {tag} from a folder");
        return tag;
    }

    /// <summary>
    /// Copies exactly the files the folder's own list names (and the list), nothing else the folder may hold: a stray program dropped into an unpacked
    /// zip never reaches the install. A missing listed file is left to the manifest check, which then refuses the folder.
    /// </summary>
    static void CopyListed(string from, string to, IReadOnlyList<string> relatives, Func<string, long> freeBytes, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        var files = new List<(string Src, string Rel, long Len)>();
        foreach (var rel in relatives)
        {
            string? src = PackageLayout.Inside(from, rel);
            if (src is null) throw new VerificationException($"The folder's file list holds an unsafe name ({rel}).");
            var fi = new FileInfo(src);
            if (!fi.Exists || (fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            files.Add((src, rel, fi.Length));
        }
        long total = files.Sum(f => f.Len), done = 0;
        DiskSpace.Require(to, total, "copy the build in", freeBytes);
        Directory.CreateDirectory(to);
        var buffer = new byte[1 << 20];
        foreach (var (src, rel, len) in files)
        {
            ct.ThrowIfCancellationRequested();
            string target = PackageLayout.Inside(to, rel) ?? throw new VerificationException($"The folder holds an unsafe name ({rel}).");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                using var s = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var t = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0) t.Write(buffer, 0, n);
                t.Flush(flushToDisk: true);
            }
            catch (IOException e) when (DiskSpace.IsDiskFull(e)) { throw DiskSpace.Wrap(e, "copy the build in"); }
            done += len;
            progress?.Report((done, total));
        }
    }

    // ---- rollback and checks -------------------------------------------------------------------------------------------------

    /// <summary>Makes the previous version the active one (and the active one the previous, so it can be undone). Files are not touched.</summary>
    public string Rollback()
    {
        var p = ReadPointer();
        if (p.Previous is null || !IsUsable(p.Previous)) throw new InvalidOperationException("There is no earlier version to go back to.");
        (p.Current, p.Previous) = (p.Previous, p.Current);
        Store.WritePointer(layout, p);
        _log($"rolled back to {p.Current}");
        return p.Current!;
    }

    /// <summary>Hashes every file of an installed version against its manifest.</summary>
    public ManifestReport CheckFiles(string tag, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default) =>
        Manifest.Verify(layout.VersionDir(tag), progress, ct);

    static IProgress<(int Done, int Total)>? FilesProgress(IProgress<UpdateStatus>? p, UpdatePhase phase, string message) =>
        p is null ? null : new ActionProgress<(int Done, int Total)>(x => p.Report(new UpdateStatus(phase, $"{message} ({x.Done} of {x.Total})", (double)x.Done / Math.Max(1, x.Total))));

    // ---- file helpers --------------------------------------------------------------------------------------------------------

    public static bool IsInsideFolder(string path, string folder)
    {
        string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        string f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static void TryDeleteDir(string path)
    {
        try { DeleteDir(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes a folder tree (read-only files too; links are removed, never followed). Retries briefly; throws when it cannot.</summary>
    public static void DeleteDir(string path)
    {
        if (!Directory.Exists(path)) return;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Directory.Delete(path); return; }     // a link: removed, its target never touched
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true }))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception) when (attempt < 4) { Thread.Sleep(150 * attempt); }
        }
    }
}

/// <summary>
/// Files a tester added inside the old version folder that the new one should keep: Godot's game\override.cfg (engine settings next to the game) and
/// small settings files (.cfg, .ini, .json up to 1 MB) that the old build's manifest does not list, so the tester put them there. The new build's own
/// files always win. Player data (%APPDATA%\sm-reimpl) and caches (%LOCALAPPDATA%\sm-reimpl) live outside the install and are never touched.
/// </summary>
public static class CarryOver
{
    public static readonly string[] Always = [@"game\override.cfg"];
    static readonly HashSet<string> SettingsExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cfg", ".ini", ".json" };
    public const long MaxBytes = 1 << 20;

    public static List<string> Plan(string oldDir, HashSet<string>? oldListed)
    {
        var result = new List<string>();
        if (!Directory.Exists(oldDir)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
        foreach (var f in Directory.EnumerateFiles(oldDir, "*", options))
        {
            string rel = Path.GetRelativePath(oldDir, f);
            if (rel.StartsWith(@"notes\", StringComparison.OrdinalIgnoreCase)) continue;
            bool always = Always.Contains(rel, StringComparer.OrdinalIgnoreCase);
            if (!always)
            {
                if (oldListed is null || oldListed.Contains(rel)) continue;
                if (!SettingsExtensions.Contains(Path.GetExtension(rel))) continue;
                if (new FileInfo(f).Length > MaxBytes) continue;
            }
            result.Add(rel);
        }
        return result;
    }

    public static List<string> Copy(string oldDir, string newDir, Action<string>? log = null)
    {
        var copied = new List<string>();
        foreach (var rel in Plan(oldDir, Manifest.ListedFiles(oldDir)))
        {
            string? target = PackageLayout.Inside(newDir, rel);
            if (target is null) continue;
            if (File.Exists(target)) { log?.Invoke($"carry-over: the new build has its own {rel}; kept it"); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(Path.Combine(oldDir, rel), target);
            copied.Add(rel);
        }
        return copied;
    }
}
