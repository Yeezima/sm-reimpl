using System.Text;
using System.Text.Json;
using SmFormats;
using SmFormats.Obpk;

// smtool m1-report: runs the M1 acceptance checks (docs/roadmap.md) in one pass and writes a Markdown summary.
// The report uses paths relative to the game folder so its tables can be pasted into docs/discovery.md.
static class M1Report
{
    // Name hints only (status: Reported or Hypothesis). Byte analysis in M2 decides.
    static readonly (string Needle, string Role)[] RoleHints =
    [
        ("dcm", "mesh (Reported)"), ("o3d", "object (Reported)"), ("bmat", "material (Reported)"),
        ("dds", "texture"), ("tga", "texture"), ("tex", "texture?"),
        ("attr", "attributes (BAF, Reported)"), ("skel", "skeleton?"), ("ske", "skeleton?"),
        ("anim", "animation?"), ("ani", "animation?"), ("event", "animation events?"),
        ("lvl", "level?"), ("level", "level?"), ("scn", "level/scene?"), ("ucs", "localized text (Reported)"),
    ];

    public static int Run(string gameDir, string? discoveryJson, string? outFile, string? extractDir)
    {
        gameDir = Path.GetFullPath(gameDir);
        var results = new List<(string Rel, string Full, long FileBytes, ArchiveSurvey? S, string? Error)>();
        var head = new byte[5];
        foreach (var path in Directory.EnumerateFiles(gameDir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
        {
            long len;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Read(head) < 5 || !ObpkArchive.HasMagic(head)) continue;
                len = fs.Length;
            }
            string rel = Path.GetRelativePath(gameDir, path).Replace('\\', '/');
            Console.Error.Write($"surveying {rel} ... ");
            try
            {
                using var a = ObpkArchive.Open(path);
                var s = ObpkSurvey.Run(a);
                results.Add((rel, path, len, s, null));
                Console.Error.WriteLine($"{(s.Pass ? "ok" : "WARN")} {s.Elapsed.TotalMilliseconds:F0} ms");
            }
            catch (Exception ex)
            {
                results.Add((rel, path, len, null, ex.Message));
                Console.Error.WriteLine($"FAIL {ex.Message}");
            }
        }

        var md = new StringBuilder();
        md.AppendLine("# M1 report").AppendLine();
        md.AppendLine($"Generated {DateTime.UtcNow:u} by `smtool m1-report`. Paths are relative to the game folder.").AppendLine();
        if (results.Count == 0)
        {
            md.AppendLine("**No OBPK archives found.** Check the game folder.");
            return Finish(md, outFile, 1);
        }

        var ok = results.Where(r => r.S is not null).ToList();
        var failed = results.Where(r => r.Error is not null).ToList();
        var sizeBad = ok.Where(r => !r.S!.SizesAgree).ToList();
        var warned = ok.Where(r => r.S!.Warnings.Count > 0 || r.S.Entries != r.S.HeaderFileCount).ToList();

        // Criterion 2: compare entry counts with discover.ps1's independent reader.
        string crossCheck = "not run (pass `--discovery <report.json>`)";
        bool crossOk = false;
        if (discoveryJson is not null)
        {
            var theirs = ReadDiscoveryCounts(discoveryJson);
            var mismatch = new List<string>();
            foreach (var r in results)
            {
                if (!theirs.TryGetValue(r.Rel, out var t)) { mismatch.Add($"{r.Rel}: missing from discover report"); continue; }
                if (t.Error is { Length: > 0 }) mismatch.Add($"{r.Rel}: discover error: {t.Error}");
                else if (r.S is not null && t.Entries != r.S.Entries) mismatch.Add($"{r.Rel}: smtool {r.S.Entries} vs discover {t.Entries}");
            }
            foreach (var extra in theirs.Keys.Except(results.Select(r => r.Rel), StringComparer.OrdinalIgnoreCase))
                mismatch.Add($"{extra}: only in discover report");
            crossOk = mismatch.Count == 0;
            crossCheck = crossOk ? $"identical for all {results.Count} archives" : $"{mismatch.Count} differences: " + string.Join("; ", mismatch.Take(10));
        }

        var largest = results.MaxBy(r => r.FileBytes);
        string timing = largest.S is { } ls
            ? $"`{largest.Rel}`: {largest.FileBytes:N0} bytes on disk, {ls.DecompressedBytes:N0} decompressed in {ls.Elapsed.TotalMilliseconds:F0} ms " +
              $"({ls.DecompressedBytes / 1e6 / Math.Max(ls.Elapsed.TotalSeconds, 1e-6):F0} MB/s)"
            : $"`{largest.Rel}` failed to parse";

        // Criterion 4: inner-type table merged across archives.
        var types = ok.SelectMany(r => r.S!.Groups.Select(g => (r.Rel, g)))
            .GroupBy(x => x.g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => (Name: x.Key, Count: x.Sum(y => y.g.Count), Bytes: x.Sum(y => y.g.Bytes), Archives: x.Count(),
                          Samples: x.SelectMany(y => y.g.Samples).Take(3).ToList()))
            .OrderByDescending(t => t.Bytes).ToList();

        string? extracted = null;
        if (extractDir is not null)
            extracted = ExtractSamples(ok.Select(r => (r.Rel, r.Full, r.S!)).ToList(), extractDir, gameDir);

        md.AppendLine("## Acceptance criteria").AppendLine();
        md.AppendLine("| # | Check | Result |").AppendLine("|---|---|---|");
        md.AppendLine($"| 1 | discover.ps1 report | {(discoveryJson is null ? "not supplied" : "see report.md in the same folder")} |");
        md.AppendLine($"| 2 | Zero parse errors in smtool | {(failed.Count == 0 ? $"PASS ({results.Count} archives)" : $"FAIL ({failed.Count} archives)")} |");
        md.AppendLine($"| 2 | Entry counts match discover.ps1 | {(discoveryJson is null ? "" : crossOk ? "PASS " : "FAIL ")}{crossCheck} |");
        md.AppendLine($"| 3 | decompressed = header = data block = sum of entries | {(sizeBad.Count == 0 && failed.Count == 0 ? "PASS" : $"FAIL ({sizeBad.Count} archives)")} |");
        md.AppendLine($"| 3 | No warnings | {(warned.Count == 0 ? "PASS" : $"{warned.Count} archives warn; explain each in docs/discovery.md")} |");
        md.AppendLine($"| 4 | Inner-type table | {types.Count} groups below; roles are name hints until M2 |");
        md.AppendLine($"| 5 | Mesh and texture sample extracted | {extracted ?? "not run (pass `--extract <cache-dir>`)"} |");
        md.AppendLine($"| 6 | Largest archive full decompress | {timing} |").AppendLine();

        md.AppendLine("## Archives").AppendLine();
        md.AppendLine("| Archive | Bytes | Entries | Header count | Header size | Data block | Entry sum | Decompressed | ms | Status |");
        md.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var r in results)
        {
            if (r.S is not { } s) { md.AppendLine($"| {r.Rel} | {r.FileBytes} | | | | | | | | FAIL: {Esc(r.Error!)} |"); continue; }
            string status = s.Pass ? "OK" : "WARN: " + Esc(string.Join("; ", s.Warnings.Take(3).DefaultIfEmpty("size mismatch")));
            md.AppendLine($"| {r.Rel} | {r.FileBytes} | {s.Entries} | {s.HeaderFileCount} | {s.HeaderDecompressedSize} | {s.DataBlockSize} | {s.EntrySizeSum} | {s.DecompressedBytes} | {s.Elapsed.TotalMilliseconds:F0} | {status} |");
        }
        md.AppendLine();

        md.AppendLine("## Inner types").AppendLine();
        md.AppendLine("| Group | Entries | Bytes | Archives | Magic guess | Name hint | First 16 bytes of up to 3 samples |");
        md.AppendLine("|---|---:|---:|---:|---|---|---|");
        foreach (var t in types)
        {
            string magic = string.Join(", ", t.Samples.Select(s => ObpkSurvey.GuessMagic(s.Head)).OfType<string>().Distinct());
            string sigs = string.Join("<br>", t.Samples.Select(s => $"`{Convert.ToHexString(s.Head)}` {Esc(Ascii(s.Head))}"));
            md.AppendLine($"| {Esc(t.Name)} | {t.Count} | {t.Bytes} | {t.Archives} | {magic} | {RoleFor(t.Name)} | {sigs} |");
        }

        int code = failed.Count == 0 && sizeBad.Count == 0 && (discoveryJson is null || crossOk) ? 0 : 1;
        return Finish(md, outFile, code);
    }

    static int Finish(StringBuilder md, string? outFile, int code)
    {
        if (outFile is null) Console.Write(md);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outFile))!);
            File.WriteAllText(outFile, md.ToString());
            Console.Error.WriteLine($"wrote {outFile}");
        }
        return code;
    }

    static Dictionary<string, (int Entries, string? Error)> ReadDiscoveryCounts(string jsonPath)
    {
        var map = new Dictionary<string, (int, string?)>(StringComparer.OrdinalIgnoreCase);
        // ReadAllText drops the UTF-8 BOM that Windows PowerShell 5.1 writes; JsonDocument rejects it in raw bytes.
        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        if (!doc.RootElement.TryGetProperty("archives", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
        foreach (var a in arr.EnumerateArray())
        {
            string path = a.GetProperty("Path").GetString()!.Replace('\\', '/');
            int n = a.TryGetProperty("Entries", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : -1;
            string? err = a.TryGetProperty("Error", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
            map[path] = (n, err);
        }
        return map;
    }

    // Picks a mid-sized .dcm entry and a texture entry (DDS magic first, then a texture-like group name).
    static string ExtractSamples(List<(string Rel, string Full, ArchiveSurvey S)> archives, string cacheDir, string gameDir)
    {
        if (CacheWriter.Overlaps(cacheDir, gameDir)) return "refused: cache dir overlaps the game folder";
        var notes = new List<string>();
        Pick(archives, "mesh", g => g.Name.Equals("dcm", StringComparison.OrdinalIgnoreCase), cacheDir, gameDir, notes);
        if (!Pick(archives, "texture", g => g.Samples.Any(s => ObpkSurvey.GuessMagic(s.Head) == "DDS"), cacheDir, gameDir, notes))
            Pick(archives, "texture", g => g.Name.ToLowerInvariant() is "dds" or "tga" or "tex" or "texture", cacheDir, gameDir, notes);
        return string.Join("; ", notes);
    }

    static bool Pick(List<(string Rel, string Full, ArchiveSurvey S)> archives, string label, Func<GroupSurvey, bool> match, string cacheDir, string gameDir, List<string> notes)
    {
        foreach (var (rel, full, s) in archives)
        {
            var g = s.Groups.FirstOrDefault(match);
            if (g is null) continue;
            using var a = ObpkArchive.Open(full);
            var candidates = a.Entries.Where(e => a.GroupName(e).Equals(g.Name, StringComparison.OrdinalIgnoreCase) && e.Size > 0).OrderBy(e => e.Size).ToList();
            if (candidates.Count == 0) continue;
            var entry = candidates[candidates.Count / 2];
            string entryPath = entry.Path(a.GroupName(entry));
            string? written = CacheWriter.Write(a, full, entry, cacheDir, gameDir, out string? refusal);
            if (written is null) { notes.Add($"{label}: refused, {refusal}"); return false; }
            notes.Add($"{label}: `{rel}` → `{entryPath}` ({entry.Size} bytes)");
            Console.Error.WriteLine($"extracted {label} -> {written}");
            return true;
        }
        notes.Add($"{label}: no matching group found");
        return false;
    }

    static string RoleFor(string group)
    {
        string g = group.ToLowerInvariant();
        foreach (var (needle, role) in RoleHints) if (g.Contains(needle)) return role;
        return "unknown";
    }

    static string Ascii(byte[] d) => new(d.Select(b => b is >= 32 and < 127 && b != '|' && b != '`' ? (char)b : '.').ToArray());
    static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}

static class CacheWriter
{
    /// Writes one entry under outDir/&lt;archive file name&gt;/&lt;entry path&gt; and returns the written path.
    /// Refuses (returns null with a reason) if the cache is inside, or an ancestor of, <paramref name="installDir"/>,
    /// or if the entry path would leave the cache.
    public static string? Write(ObpkArchive a, string archivePath, ObpkEntry entry, string outDir, string installDir, out string? refusal)
    {
        string cache = Path.GetFullPath(outDir);
        refusal = null;
        if (Overlaps(cache, installDir)) { refusal = "cache dir overlaps the game folder"; return null; }
        string full = Path.GetFullPath(Path.Combine(cache, Path.GetFileName(archivePath), entry.Path(a.GroupName(entry))));
        if (!IsWithin(full, cache)) { refusal = $"entry path '{entry.Path(a.GroupName(entry))}' would leave the cache dir"; return null; }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, a.ReadEntry(entry));
        return full;
    }

    public static bool Overlaps(string dirA, string dirB) => InstallPaths.Overlaps(dirA, dirB);
    static bool IsWithin(string path, string dir) => InstallPaths.IsWithin(path, dir);
    public static string FindInstallRoot(string archivePath) => InstallPaths.FindInstallRoot(archivePath);
}
