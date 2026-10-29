using System.Text;
using SmFormats.Obpk;

// smtool: read-only inspection of original game archives.
// Extraction only writes to the private cache (outside the repo); see docs/architecture.md.

return args switch
{
    ["list", var archive, ..] => List(archive, args.Contains("--csv")),
    ["verify", var archive] => Verify(archive),
    ["verify-all", var gameDir] => VerifyAll(gameDir),
    ["extract", var archive, var entry, var outDir] => Extract(archive, entry, outDir),
    ["m1-report", var gameDir, .. var rest] => M1Report.Run(gameDir, Opt(rest, "--discovery"), Opt(rest, "--out"), Opt(rest, "--extract")),
    ["synth-fixture", var output] => SynthFixture(output),
    ["graph-dump", .. var rest] => SmTool.GraphDump.Run(rest),
    ["weapon-parts", .. var rest] => SmTool.WeaponCommands.Parts(rest),
    ["nav-stats", var gameDir, .. var rest] => SmTool.NavCommands.Stats(gameDir, Opt(rest, "--map") ?? "mp_tdm_09", (Opt(rest, "--class") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), Opt(rest, "--cache"), int.TryParse(Opt(rest, "--top"), out var top) ? top : 0),
    ["refine-register", .. var rest] => SmTool.RefineRegister.Run(rest),
    ["scan-package", .. var rest] => SmTool.PackageScanCommand.Run(rest),
    ["make-tar", .. var rest] => SmTool.TarPackage.Run(rest),
    ["whats-new", .. var rest] => SmTool.WhatsNew.Run(rest),
    ["perks", var gameDir] => SmTool.PerkCommands.List(gameDir),
    ["ork-balance", var gameDir, .. var rest] => SmTool.OrkCommands.Balance(gameDir, rest),
    ["ork-actions", var gameDir] => SmTool.OrkCommands.Actions(gameDir),
    ["ork-sounds", var gameDir, .. var rest] => SmTool.OrkSoundCommands.Run(gameDir, rest),
    ["weapon-dps", var gameDir, .. var rest] => SmTool.DpsCommands.Run(gameDir, rest),
    ["hitbox-probe", var gameDir, .. var rest] => SmTool.HitboxCommands.Probe(gameDir, rest),
    ["race-parity", var gameDir, .. var rest] => SmTool.ParityCommands.Run(gameDir, rest),
    ["armour-audit", var gameDir, .. var rest] => SmTool.ArmourAuditCommand.Run(gameDir, rest),
    ["fx-smoke", var gameDir, .. var rest] => SmTool.FxSmokeCommands.Run(gameDir, rest),
    ["fx-dump", var gameDir, .. var rest] => SmTool.FxDumpCommands.Run(gameDir, rest),
    ["gfx-dump", var gameDir, .. var rest] => SmTool.GfxDumpCommand.Run(gameDir, rest),
    [var dump, .. var rest] when dump is "baf-dump" or "locale" or "customization-dump" && SmTool.DumpCommands.Run(dump, rest) is var dumpCode && dumpCode >= 0 => dumpCode,
    [var audio, .. var rest] when audio.StartsWith("audio-") && AudioCommands.Run(audio, rest) is var code && code >= 0 => code,
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        usage:
          smtool list <archive.oppc> [--csv]        list entries (path, size, offset)
          smtool verify <archive.oppc>              parse table and decompress everything, report consistency
          smtool verify-all <game-dir>              verify every OBPK archive under a folder, with timings
          smtool extract <archive.oppc> <entry-path|#index> <cache-dir>
                                                    copy one entry into the private cache (paths can repeat;
                                                    #index picks one exactly, see list --csv)
          smtool m1-report <game-dir> [--discovery <report.json>] [--out <file.md>] [--extract <cache-dir>]
                                                    run the M1 acceptance checks and write a Markdown summary
          smtool nav-stats <game-dir|auto> [--map mp_tdm_09] [--class assault,raptor] [--cache <dir>]
                                                    build or read the test bots' navigation graphs per side (and jump links); sizes and timings
          smtool perks <game-dir|auto>              the perks: upgrade strings, classes, simulation status, perk variants (M43)
          smtool armour-audit <game-dir|auto> [--all] [--packages]
                                                    every class's default look, every armour set worn whole and every offered piece of both races,
                                                    resolved and loaded as the client draws them: what fails, explained gaps, free-hanging cloth
          smtool scan-package [--against <dir>]... [--against-install <game-dir|auto>] [--secrets <config.json>]... [--secrets-env]
                              [--require-install] [--require-secrets] [--require-pack] [--profile binary] <zip|tar.gz|folder|file>...
                                                    the release content guard: fails (exit 1) when a package holds game data, private files, user paths or secrets;
                                                    exit 2 when an input could not be used (missing folder, unreadable secrets file, install required but not indexed)
          smtool make-tar <folder> <out.tar.gz> --prefix <top folder> [--exec a,b,...]
                                                    the Linux server package (tar.gz keeps the 0755 mode of the listed files)
          smtool ork-balance <game-dir|auto> [--mode parity|data|both]
                                                    the Ork Boy's balance (Ork O4): data, Tactical reference, rule, factor and result per stat,
                                                    the sustained-damage checks, hit-volume areas and the config hashes with and without the Ork
          smtool ork-actions <game-dir|auto>        the Ork's action clips and timings (Ork O7b / O7c): reactions, reload, recoil, toss release, death clips, choppa, charge, hop
          smtool ork-sounds <game-dir|auto> [--clips] [--all-tags]
                                                    the Ork Boy's sounds and barks (Ork O8): each sound and speech command of the boy's blueprint and
                                                    wargear resolved against the banks (voices per post, the speech bank's line for his voice), and the
                                                    behaviour tags of the boy rig's clips with what binds them
          smtool weapon-dps <game-dir|auto> [--range m] [--all] [--data]
                                                    body-shot DPS (burst, sustained; health scale and against a full marine) of the bolters and
                                                    bolt pistols with their perk variants, and the owner's overrides (OwnerBalance) before and after
          smtool race-parity <game-dir|auto> [--raw] [--fx-colours [word]]
                                                    each Chaos wargear and its loyalist twin: present in the install, the differences of the simulation's specs before and
                                                    after parity (after must be 0), the class defaults and the config hashes; --raw also lists the files' differing values;
                                                    --fx-colours [plasma] prints the hue of every effect those weapons play (weapons task A)
          smtool fx-dump <game-dir|auto> <name|word>... [--list] [--fields]
                                                    fx/impact-pass: the element tree of each named effect (--list: effect names containing each word)
          smtool fx-smoke <game-dir|auto> [--map mp_tdm_09] [--all] [--textures]
                                                    fx/no-smoke: every effect the multiplayer client plays with the smoke, dust, mist and haze sprites the client no
                                                    longer draws (SmokeRules) and what each keeps; --textures sums them per atlas texture
          smtool refine-register [--spec spec/behavior.md] [--changes docs/changes-from-original.md] [--out docs/refine-register.md] [--check]
                                                    write the human-playtest checklist of every estimate, guess and interpretation in the spec;
                                                    --check writes nothing and exits 1 on malformed rows, rows with no kind, confidence or source, empty cells in the
                                                    changes document, or a missing or stale register (--list-untagged prints the rows no rule tagged)
          smtool synth-fixture <out.oppc>           write a synthetic archive (no original data) for tooling tests
          smtool baf-dump <archive.oppc|folder> [--package <regex>] [--entry <regex>] [--list] [--grep <regex>] [--depth <n>] [--under <a/b>] [--locale] [--game <dir>]
                                                    print BAF attribute files of an archive as trees (--list: names only;
                                                    --grep: only entries whose name or string value matches, with their table path;
                                                    --locale annotates text ids with the English text; --under starts at a sub-table)
          smtool locale <id|lo-hi|regex> [...] [--language english] [--game <dir>]
                                                    look up localised text by id (numbers), an id range (e.g. 104200-104300) or search it (regex)

        audio (Wwise; reads streamingaudio/*.pck read-only; --game <dir> overrides install detection):
          smtool audio-list <file.pck> [--all] [--names <list.txt>]
                                                    banks, embedded/streamed wem counts, events with known names
          smtool audio-event <event-name|0xID> [--packages a,b] [--names <list.txt>]
                                                    resolve an event across the MP banks down to its .wem IDs
          smtool audio-decode <wem-id|event-name> [--out <dir>] [--no-wav] [--packages a,b]
                                                    decode to .ogg (+ .wav) in %LOCALAPPDATA%\sm-reimpl\cache\audio\
                                                    (first use downloads ww2ogg's codebook library, hash-checked)
          smtool audio-verify <streamingaudio-dir>  parse every package and bank, report HIRC decoding coverage
          smtool audio-coverage [--map <name>]       M26b: sound triggers of the MP files and the map, resolved against the banks
        """);
    return 2;
}

static int List(string path, bool csv)
{
    using var a = ObpkArchive.Open(path);
    if (csv) Console.WriteLine("index,path,group,size,offset,unknown,extra_hex");
    foreach (var e in a.Entries)
    {
        string p = e.Path(a.GroupName(e));
        Console.WriteLine(csv
            ? $"{e.Index},{Csv(p)},{Csv(a.GroupName(e))},{e.Size},{e.Offset},0x{e.Unknown:X8},{Convert.ToHexString(e.Extra)}"
            : $"{e.Size,12} {p}");
    }
    PrintWarnings(a);
    return 0;
}

static int Verify(string path)
{
    using var a = ObpkArchive.Open(path);
    long total = 0;
    var sigs = new Dictionary<string, string>();
    a.ReadEntries(_ => true, (e, data) =>
    {
        total += data.Length;
        sigs.TryAdd(a.GroupName(e), Signature(data));
    });
    Console.WriteLine($"version={a.Header.Version} entries={a.Entries.Count} groups={a.Groups.Count} decompressed={total} header_says={a.Header.DecompressedSize}");
    foreach (var (g, s) in sigs.OrderBy(kv => kv.Key)) Console.WriteLine($"  {g,-24} first bytes: {s}");
    PrintWarnings(a);
    return a.Warnings.Count == 0 && total == a.Header.DecompressedSize ? 0 : 1;
}

static int VerifyAll(string dir)
{
    int ok = 0, bad = 0;
    var head = new byte[5];
    foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            if (fs.Read(head) < 5 || !ObpkArchive.HasMagic(head)) continue;
        string rel = Path.GetRelativePath(dir, path).Replace('\\', '/');
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var a = ObpkArchive.Open(path);
            long total = 0;
            a.ReadEntries(_ => true, (_, d) => total += d.Length);
            bool pass = a.Warnings.Count == 0 && total == a.Header.DecompressedSize;
            Console.WriteLine($"{(pass ? "OK  " : "WARN")} {rel} entries={a.Entries.Count} decompressed={total} ms={sw.ElapsedMilliseconds}");
            foreach (var w in a.Warnings.Take(5)) Console.WriteLine($"     {w}");
            if (pass) ok++; else bad++;
        }
        catch (Exception ex) { Console.WriteLine($"FAIL {rel}: {ex.Message}"); bad++; }
    }
    Console.WriteLine($"archives ok={ok} not-ok={bad}");
    return bad == 0 && ok > 0 ? 0 : 1;
}

static int Extract(string path, string entryPath, string outDir)
{
    using var a = ObpkArchive.Open(path);
    ObpkEntry? entry;
    if (entryPath.StartsWith('#'))
    {
        if (!int.TryParse(entryPath.AsSpan(1), out int index)) { Console.Error.WriteLine($"Bad entry index: {entryPath}"); return 2; }
        entry = a.Entries.ElementAtOrDefault(index);
    }
    else entry = a.Entries.FirstOrDefault(e => string.Equals(e.Path(a.GroupName(e)), entryPath, StringComparison.OrdinalIgnoreCase));
    if (entry is null) { Console.Error.WriteLine($"Entry not found: {entryPath}"); return 1; }
    // The cache must be neither inside the game folder nor an ancestor of it.
    string? full = CacheWriter.Write(a, path, entry, outDir, CacheWriter.FindInstallRoot(path), out string? refusal);
    if (full is null) { Console.Error.WriteLine($"Refusing: {refusal}."); return 1; }
    Console.WriteLine($"wrote {entry.Size} bytes -> {full}");
    return 0;
}

static int SynthFixture(string output)
{
    var files = new SyntheticObpkWriter.File[]
    {
        new("dcm", "synthetic_mesh", Encoding.ASCII.GetBytes("SYNTHETIC-MESH")),
        new("dds", "synthetic_tex", Encoding.ASCII.GetBytes("DDS |synthetic"), "art/"),
        new("attr_pc", "synthetic_weapon", [0, 1, 2, 3]),
    };
    File.WriteAllBytes(output, SyntheticObpkWriter.Build(files));
    Console.WriteLine($"wrote synthetic fixture {output}");
    return 0;
}

static string? Opt(string[] rest, string name)
{
    int i = Array.IndexOf(rest, name);
    return i >= 0 && i + 1 < rest.Length ? rest[i + 1] : null;
}

static string Signature(ReadOnlySpan<byte> d)
{
    var head = d[..Math.Min(16, d.Length)];
    var ascii = new string(head.ToArray().Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());
    return $"{Convert.ToHexString(head)}  {ascii}";
}

static string Csv(string s) => s.IndexOfAny([',', '"', '\n']) < 0 ? s : "\"" + s.Replace("\"", "\"\"") + "\"";

static void PrintWarnings(ObpkArchive a)
{
    foreach (var w in a.Warnings.Take(20)) Console.Error.WriteLine($"warning: {w}");
    if (a.Warnings.Count > 20) Console.Error.WriteLine($"warning: ...{a.Warnings.Count - 20} more");
}
