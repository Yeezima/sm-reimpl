using System.IO.Compression;
using System.Security.Cryptography;

namespace SmLauncher.Core;

/// <summary>
/// Installing a build from parts (<see cref="PartsManifest"/>): the manifest is downloaded and checked against the hashes the release states (GitHub's
/// digest, the description's "Download check"), then only the parts that carry a file the installed versions do not have are downloaded (each checked
/// against the manifest's SHA-256 before it is opened), and the new version is assembled in versions\&lt;tag&gt;.staging: the downloaded parts' files are
/// written there (only paths the manifest assigns to that part; each file hashed), every other file is copied from the installed version that has the
/// same bytes (hashed while it is copied), and the result must match the build's own file list. Then it is switched to exactly like a zip install.
/// </summary>
public sealed partial class Updater
{
    /// <summary>
    /// Test and smoke-test aid (--install-parts): take the manifest and the part zips from this folder (or its parts\ subfolder) instead of downloading
    /// them. Every check still runs.
    /// </summary>
    public string? LocalPartsDir { get; init; }

    /// <summary>What the last parts update planned (the parts it downloaded and the ones it reused), for the window and the tests.</summary>
    public PartsPlan? LastPartsPlan { get; private set; }

    /// <summary>The download link of a part: the manifest's link with its release tag and file name replaced by the parts release and the part.</summary>
    public static Uri PartUrl(string manifestUrl, string partsTag, string asset)
    {
        var u = new Uri(manifestUrl);
        string path = u.AbsolutePath;
        int i = path.LastIndexOf("/releases/download/", StringComparison.Ordinal);
        if (i < 0) throw new InvalidDataException("The parts list's link is not a release download link.");
        var b = new UriBuilder(u) { Path = path[..i] + "/releases/download/" + Uri.EscapeDataString(Names.CheckTag(partsTag)) + "/" + Uri.EscapeDataString(asset), Query = "", Fragment = "" };
        return b.Uri;
    }

    async Task<string> RunPartsAsync(UpdateState state, IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        if (downloader is null && LocalPartsDir is null) throw new InvalidOperationException("No downloader.");
        if (!Names.IsPartsManifest(state.AssetName)) throw new InvalidDataException("The update's parts list has an unexpected name.");
        try { Hashes.CheckStated(state.ApiSha256, state.BodySha256, state.AssetName!); }
        catch (VerificationException) { Discard(state); throw; }
        Directory.CreateDirectory(layout.VersionsDir);
        Directory.CreateDirectory(layout.PartsDir);
        if (state.Phase == UpdatePhase.Switching)
        {
            // An interrupted switch: the staging folder is checked again before it is put in place (Recover normally finishes this itself).
            progress?.Report(new UpdateStatus(UpdatePhase.Switching, $"Switching to {state.Tag}", null));
            try { CompleteSwitch(state, verify: true, ct); FinishInstall(state); _log($"installed {state.Tag} (from parts)"); return state.Tag; }
            catch (VerificationException e) { _log("switch: " + e.Message + "; assembling again"); TryDeleteDir(layout.StagingDir(state.Tag)); }
        }
        state.Phase = UpdatePhase.Downloading;
        Store.WriteState(layout, state);

        // 1. The manifest, checked against the hashes the release states.
        string manifestFile = layout.PartFile(state.AssetName!);
        progress?.Report(new UpdateStatus(UpdatePhase.Downloading, "Downloading the build's parts list", null));
        if (!File.Exists(manifestFile) || new FileInfo(manifestFile).Length != state.Size)
            await FetchAsync(new Uri(state.Url!), state.AssetName!, manifestFile, state.Size, null, ct).ConfigureAwait(false);
        string manifestSha = Hashes.Sha256File(manifestFile);
        try { Hashes.CheckZip(manifestSha, state.ApiSha256, state.BodySha256, state.AssetName!); }
        catch (VerificationException e) { TryDelete(manifestFile); Store.ClearState(layout); _log("verify: " + e.Message); throw; }
        var manifest = PartsManifest.Parse(File.ReadAllText(manifestFile));
        if (manifest.Tag != state.Tag) throw new VerificationException($"The parts list belongs to {manifest.Tag}, not {state.Tag}. Nothing was installed.");

        // 2. Which parts to fetch: those carrying bytes the installed versions do not have.
        var p = ReadPointer();
        var installed = PartsPlan.InstalledHashes(CurrentDir(), IsUsable(p.Previous) ? layout.VersionDir(p.Previous!) : null);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var extra = new HashSet<string>(StringComparer.Ordinal);
        for (int attempt = 1; ; attempt++)
        {
            var have = new HashSet<string>(installed.Keys.Where(h => !excluded.Contains(h)), StringComparer.Ordinal);
            var plan = PartsPlan.Select(manifest, have);
            if (extra.Count > 0)
            {
                var download = manifest.Parts.Where(x => plan.Download.Contains(x) || extra.Contains(x.Name)).ToList();
                plan = new PartsPlan(download, manifest.Parts.Except(download).ToList());
            }
            LastPartsPlan = plan;
            _log($"parts {state.Tag}: {plan.Download.Count} of {manifest.Parts.Count} to download ({plan.DownloadBytes} bytes: {string.Join(", ", plan.Download.Select(x => x.Name))}); " +
                 $"{plan.Reuse.Count} taken from the installed build ({string.Join(", ", plan.Reuse.Select(x => x.Name))})");

            // 3. Download the parts (resumable; each checked before it is kept).
            state.Phase = UpdatePhase.Downloading;
            Store.WriteState(layout, state);
            long total = Math.Max(1, plan.DownloadBytes), before = 0;
            int n = 0;
            foreach (var part in plan.Download)
            {
                ct.ThrowIfCancellationRequested();
                n++;
                string zip = layout.PartZip(part.Asset);
                if (!(File.Exists(zip) && new FileInfo(zip).Length == part.ZipSize && Hashes.Sha256File(zip) == part.ZipSha256))
                {
                    TryDelete(zip);
                    string partial = zip + ".part";
                    string label = $"Downloading part {n} of {plan.Download.Count} ({part.Name})";
                    long done0 = before;
                    var dp = new ActionProgress<DownloadProgress>(d => progress?.Report(new UpdateStatus(UpdatePhase.Downloading, d.Note ?? label,
                        (double)(done0 + d.Done) / total, d with { Done = done0 + d.Done, Total = total })));
                    await FetchAsync(PartUrl(state.Url!, manifest.PartsTag, part.Asset), part.Asset, partial, part.ZipSize, dp, ct).ConfigureAwait(false);
                    progress?.Report(new UpdateStatus(UpdatePhase.Verifying, $"Checking part {n} ({part.Name})", (double)(before + part.ZipSize) / total));
                    string actual = await Hashes.Sha256FileAsync(partial, null, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, part.ZipSha256, StringComparison.Ordinal))
                    {
                        TryDelete(partial);
                        _log($"verify part {part.Name}: SHA-256 {actual} instead of {part.ZipSha256}");
                        throw new VerificationException($"The downloaded part '{part.Name}' is damaged. It was deleted; press Update to download it again.");
                    }
                    File.Move(partial, zip, overwrite: true);
                    _log($"part {part.Name}: {part.Asset} ({part.ZipSize} bytes) downloaded and checked");
                }
                before += part.ZipSize;
            }

            // 4. Assemble and check.
            state.Phase = UpdatePhase.Extracting;
            Store.WriteState(layout, state);
            var damaged = await Task.Run(() => Assemble(state, manifest, plan, installed, excluded, progress, ct), ct).ConfigureAwait(false);
            if (damaged.Count == 0) break;
            // An installed file did not have the bytes its list says: those parts are downloaded after all (once).
            if (attempt >= 2) throw new VerificationException("The installed build's files are damaged and the parts did not repair them. Nothing was changed.");
            foreach (var (hash, part) in damaged) { excluded.Add(hash); extra.Add(part); }
            _log($"parts {state.Tag}: {damaged.Count} installed file(s) did not match their list; downloading their parts too");
        }

        // 5. Switch, exactly as after a zip.
        state.Phase = UpdatePhase.Switching;
        Store.WriteState(layout, state);
        progress?.Report(new UpdateStatus(UpdatePhase.Switching, $"Switching to {state.Tag}", null));
        CompleteSwitch(state, verify: false, ct);
        FinishInstall(state);
        _log($"installed {state.Tag} (from parts)");
        return state.Tag;
    }

    /// <summary>
    /// Builds versions\&lt;tag&gt;.staging from the downloaded parts and the installed files. Returns the installed files (hash, part) that turned out damaged
    /// (the caller downloads their parts and assembles again); throws <see cref="VerificationException"/> when a part or the result does not check out.
    /// </summary>
    List<(string Hash, string Part)> Assemble(UpdateState state, PartsManifest manifest, PartsPlan plan, IReadOnlyDictionary<string, string> installed,
        IReadOnlySet<string> excluded, IProgress<UpdateStatus>? progress, CancellationToken ct)
    {
        string staging = layout.StagingDir(state.Tag);
        DeleteDir(staging);
        var damaged = new List<(string, string)>();
        try
        {
            DiskSpace.Require(staging, manifest.TotalBytes, "assemble the update", FreeBytes);
            Directory.CreateDirectory(staging);
            var byPath = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var written = new Dictionary<string, string>(StringComparer.Ordinal);       // sha256 -> a file in staging with those bytes
            long total = Math.Max(1, manifest.TotalBytes), done = 0;
            void Report(string what) => progress?.Report(new UpdateStatus(UpdatePhase.Extracting, what, (double)done / total));

            // The downloaded parts: only the paths the manifest gives to that part, each hashed while it is written.
            foreach (var part in plan.Download)
            {
                ct.ThrowIfCancellationRequested();
                using var zip = ZipFile.OpenRead(layout.PartZip(part.Asset));
                if (zip.Entries.Count > SafeZip.MaxEntries) throw new VerificationException($"The part '{part.Name}' holds too many files.");
                foreach (var e in zip.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string name = e.FullName.TrimEnd('/', '\\');
                    if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                    string? rel = PackageLayout.SafeRelative(name);
                    if (rel is null || !byPath.TryGetValue(rel.Replace('\\', '/'), out var f) || f.Part != part.Name)
                        throw new VerificationException($"The part '{part.Name}' holds a file the build's list does not give it ({Show(name)}). Nothing was changed.");
                    if (e.Length != f.Size) throw new VerificationException($"{Show(name)} in the part '{part.Name}' is not the size the list says. Nothing was changed.");
                    string target = PackageLayout.Inside(staging, rel) ?? throw new VerificationException($"The part '{part.Name}' holds an unsafe path. Nothing was changed.");
                    if (File.Exists(target)) throw new VerificationException($"The part '{part.Name}' holds {Show(name)} twice. Nothing was changed.");
                    Report($"Unpacking {part.Name}");
                    string sha;
                    using (var src = e.Open()) sha = WriteHashed(src, target, f.Size, $"{Show(name)} in the part '{part.Name}'");
                    if (sha != f.Sha256) throw new VerificationException($"{Show(name)} in the part '{part.Name}' does not match the build's list. Nothing was changed.");
                    written.TryAdd(sha, target);
                    done += f.Size;
                }
            }

            // Every other file: the same bytes from this build (a duplicate) or from an installed version.
            foreach (var f in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();
                string target = PackageLayout.Inside(staging, f.Path) ?? throw new VerificationException("The build's list holds an unsafe path. Nothing was changed.");
                if (File.Exists(target)) continue;
                string? source = written.GetValueOrDefault(f.Sha256);
                bool fromInstall = false;
                if (source is null && !excluded.Contains(f.Sha256) && installed.TryGetValue(f.Sha256, out var old)) { source = old; fromInstall = true; }
                if (source is null)
                    throw new VerificationException($"{Show(f.Path)} is in no downloaded part and not in the installed build. Nothing was changed.");
                Report(fromInstall ? "Taking the unchanged files from the installed build" : "Unpacking");
                string sha;
                try
                {
                    using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
                    sha = WriteHashed(src, target, f.Size, Show(f.Path));
                }
                catch (Exception e) when (fromInstall && e is IOException or UnauthorizedAccessException or VerificationException && !DiskSpace.IsDiskFull(e))
                {
                    TryDelete(target);
                    damaged.Add((f.Sha256, f.Part));
                    continue;
                }
                if (sha != f.Sha256)
                {
                    TryDelete(target);
                    if (!fromInstall) throw new VerificationException($"{Show(f.Path)} does not match the build's list. Nothing was changed.");
                    damaged.Add((f.Sha256, f.Part));
                    continue;
                }
                written.TryAdd(sha, target);
                done += f.Size;
            }
            if (damaged.Count > 0) { TryDeleteDir(staging); return damaged; }

            // The result against the build's own file list (and nothing else in the folder).
            var report = Manifest.Verify(staging, FilesProgress(progress, UpdatePhase.Extracting, "Checking the assembled files"), ct);
            if (!report.Ok) throw new VerificationException($"The assembled files do not match the build's own file list: {report.Describe()}. Nothing was changed.");
            int listed = report.Checked + 1;      // the list does not name itself
            if (listed != manifest.Files.Count) throw new VerificationException("The build's parts list and its file list disagree. Nothing was changed.");
            if (!IsUsableDir(staging)) throw new VerificationException($"The build has no {PackageLayout.GameExe}. Nothing was changed.");
            _log($"assembled {state.Tag}: {manifest.Files.Count} files from {plan.Download.Count} downloaded part(s) and the installed build");
            CarryOverFromCurrent(staging);
            return damaged;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            TryDeleteDir(staging);
            if (e is IOException io && DiskSpace.IsDiskFull(io)) throw DiskSpace.Wrap(io, "assemble the update");
            throw;
        }
        catch (OperationCanceledException) { TryDeleteDir(staging); throw; }
    }

    /// <summary>Writes <paramref name="src"/> to a new file, refusing more or fewer bytes than <paramref name="size"/>; returns the bytes' SHA-256.</summary>
    static string WriteHashed(Stream src, string target, long size, string what)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
        {
            var buffer = new byte[1 << 16];
            long written = 0;
            int n;
            while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += n;
                if (written > size) throw new VerificationException($"{what} is larger than the build's list says. Nothing was changed.");
                sha.AppendData(buffer, 0, n);
                dst.Write(buffer, 0, n);
            }
            if (written != size) throw new VerificationException($"{what} is shorter than the build's list says. Nothing was changed.");
            dst.Flush(flushToDisk: true);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Downloads (or, with <see cref="LocalPartsDir"/>, copies) one file of a parts update.</summary>
    async Task FetchAsync(Uri url, string name, string dest, long size, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (LocalPartsDir is { } dir)
        {
            string src = File.Exists(Path.Combine(dir, name)) ? Path.Combine(dir, name) : Path.Combine(dir, "parts", name);
            if (!File.Exists(src)) throw new DownloadFailedException($"{name} is not in {dir}.");
            if (new FileInfo(src).Length != size) throw new DownloadFailedException($"{name} is not the size the list says.");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(src, dest, overwrite: true);
            progress?.Report(new DownloadProgress(size, size, 0, TimeSpan.Zero, 0, null));
            return;
        }
        await downloader!.DownloadAsync(url, dest, size, progress, ct).ConfigureAwait(false);
    }

    void CleanPartsDir()
    {
        if (!Directory.Exists(layout.PartsDir)) return;
        // Only the launcher's own names: part zips and their .part files.
        foreach (var f in Directory.EnumerateFiles(layout.PartsDir))
        {
            string name = Path.GetFileName(f);
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) name = name[..^".part".Length];
            if (Names.IsPartAsset(name)) TryDelete(f);
        }
        try { if (!Directory.EnumerateFileSystemEntries(layout.PartsDir).Any()) Directory.Delete(layout.PartsDir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static string Show(string name) => name.Length > 80 ? name[..80] + "..." : name;
}
