using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using SmSim;
using SmSim.Extras.Musou.Ashfall;

namespace SmContent.Extras.Musou;

/// <summary>
/// Polish pass P2 (2026-11-10; Musou Mode, added content): Ashfall Citadel's generated layout kept in the private cache, so a join does not generate it again
/// (measured: 23 s of main-thread work at every join and at every server start, deterministic). The level and its dressing are written once to
/// <c>%LOCALAPPDATA%\sm-reimpl\cache\ashfall\&lt;layout&gt;-&lt;key&gt;-&lt;process&gt;.bin</c> (<see cref="GameInstall.DataRoot"/>; a derived file, nothing from the install in it, never in the
/// repository) and read back on the next join; the key is the generator's versions and the builds of the simulation assembly that generated it and of this reader, so a changed
/// generator or reader never serves a stale layout (the key changes with every build). The file ends with a checksum over its whole payload (P2 review): a damaged file is
/// refused and generated again, never predicted on. The level's own hash also goes into the map hash the server and client compare. <see cref="Disabled"/>
/// (<c>SM_NO_ASHFALL_CACHE=1</c>, the client's <c>--no-ashfall-cache</c>) generates as before. The client's and the server's keys differ (their simulation assemblies are
/// separate builds), so each process kind keeps its own newest files (<see cref="KeepPerLayout"/>) and never deletes the other's. P2 re-review: the checksum is checked before
/// anything of the file is parsed, any parse failure counts as unreadable (generated and overwritten, never thrown past the cache), and the pruning also removes the first
/// format's files and stale temporary files (<see cref="Prune"/>).
/// </summary>
public static class AshfallLayoutCache
{
    /// <summary>The environment variable that switches the cache off (server and client): the layout is generated at every start as before.</summary>
    public const string OffVariable = "SM_NO_ASHFALL_CACHE";

    /// <summary>Whether the cache is off (from <see cref="OffVariable"/>; the client's <c>--no-ashfall-cache</c> and the tests set it too).</summary>
    public static bool Disabled { get; set; } = Environment.GetEnvironmentVariable(OffVariable) == "1";

    /// <summary>The file format's version (in the file; a reader of another version generates instead). 2: the payload checksum (P2 review).</summary>
    public const uint FormatVersion = 2;
    const uint Magic = 0x46414D53;   // "SMAF"

    /// <summary>Files kept per layout and process kind: this build's and the one before (an update's rollback finds its file).</summary>
    public const int KeepPerLayout = 2;

    /// <summary>A temporary file this old is a crashed write's leftover and is pruned (a write takes well under a second, so a write in progress is never touched).</summary>
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    static readonly Dictionary<AshfallLayout, object> Gates = new();
    static readonly Dictionary<AshfallLayout, (AshfallLevel Level, AshfallDressing Dressing)> Memo = new();

    /// <summary>What the last build did (read from the cache, generated and written, cache off), for the caller's log.</summary>
    public static string? LastNote { get; private set; }

    /// <summary>The cache folder under the private data root.</summary>
    public static string Folder => Path.Combine(GameInstall.DataRoot, "cache", "ashfall");

    /// <summary>
    /// The key of a layout file: the level, graybox and dressing versions, the first 8 bytes of the simulation assembly's module version id (the generator) and of this
    /// assembly's (the reader): a new build of either, a new key.
    /// </summary>
    public static string Key
    {
        get
        {
            string sim = Convert.ToHexString(typeof(AshfallMap).Assembly.ManifestModule.ModuleVersionId.ToByteArray(), 0, 8).ToLowerInvariant();
            string reader = Convert.ToHexString(typeof(AshfallLayoutCache).Assembly.ManifestModule.ModuleVersionId.ToByteArray(), 0, 8).ToLowerInvariant();
            return string.Create(CultureInfo.InvariantCulture, $"v{AshfallMap.Version}-g{AshfallMap.GrayboxVersion}-d{AshfallMap.DressVersion}-{sim}-{reader}");
        }
    }

    /// <summary>The process kind in the file name (the server's and the client's files are kept apart): the executable's name, letters and digits only.</summary>
    public static string ProcessKind { get; } = Kind();

    static string Kind()
    {
        string name = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "") ?? "";
        var chars = name.ToLowerInvariant().Where(char.IsAsciiLetterOrDigit).Take(24).ToArray();
        return chars.Length > 0 ? new string(chars) : "process";
    }

    /// <summary>The file a layout is kept in.</summary>
    public static string PathOf(AshfallLayout layout) => PathIn(Folder, layout, ProcessKind);

    /// <summary>The file a layout is kept in inside <paramref name="folder"/> for the process kind <paramref name="kind"/> (the tests' own folders; the real cache is <see cref="Folder"/>).</summary>
    internal static string PathIn(string folder, AshfallLayout layout, string kind) => Path.Combine(folder, FileName(layout, Key, kind));

    static string FileName(AshfallLayout layout, string key, string kind) => $"{layout.ToString().ToLowerInvariant()}-{key}-{kind}.bin";

    /// <summary>Whether this layout is already at hand (in this process, or as a file): a join then loads it in well under a second.</summary>
    public static bool IsCached(AshfallLayout layout)
    {
        if (Disabled) return false;
        lock (Memo) if (Memo.ContainsKey(layout)) return true;   // Memo's own lock (P2 re-review): another layout's build writes it under that layout's gate
        try { return File.Exists(PathOf(layout)); } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    static object Gate(AshfallLayout layout)
    {
        lock (Gates) return Gates.TryGetValue(layout, out var g) ? g : Gates[layout] = new object();
    }

    /// <summary>The level of a layout (the server's need), through the cache.</summary>
    public static AshfallLevel Build(AshfallLayout layout, out string note) => BuildWithDressing(layout, out note).Level;

    /// <summary>The level and dressing of a layout: from this process's memory, else the cache file, else generated (and written).</summary>
    public static (AshfallLevel Level, AshfallDressing Dressing) BuildWithDressing(AshfallLayout layout, out string note)
    {
        lock (Gate(layout))
        {
            lock (Memo) if (Memo.TryGetValue(layout, out var memo)) { note = LastNote = $"{layout} layout: already built in this process"; return memo; }
            (AshfallLevel Level, AshfallDressing Dressing) built;
            if (Disabled)
            {
                var watch = Stopwatch.StartNew();
                built = AshfallMap.BuildWithDressing(layout);
                note = string.Create(CultureInfo.InvariantCulture, $"{layout} layout generated in {watch.ElapsedMilliseconds} ms (hash {built.Level.Hash:X16}); cache off");
            }
            else built = LoadOrGenerate(layout, Folder, ProcessKind, AshfallMap.BuildWithDressing, out note);
            lock (Memo) Memo[layout] = built;
            LastNote = note;
            return built;
        }
    }

    /// <summary>
    /// The disk side of a build, in <paramref name="folder"/> for the process kind <paramref name="kind"/>: the layout's file read back, else <paramref name="generate"/>d and
    /// written (a per-process temporary name, then moved over the file) and the folder pruned (<see cref="Prune"/>). A file that cannot be read (damaged, cut, another format, a
    /// failed parse) is generated again and overwritten, the reason in <paramref name="note"/>; a bad file or a disk error never throws from here. The caller holds the layout's
    /// gate (one writer per layout in a process). Neither this process's memory nor <see cref="Disabled"/> is consulted: the tests pass a temporary folder and a synthetic
    /// generator (the real cache stays off in the test processes).
    /// </summary>
    internal static (AshfallLevel Level, AshfallDressing Dressing) LoadOrGenerate(AshfallLayout layout, string folder, string kind, Func<AshfallLayout, (AshfallLevel Level, AshfallDressing Dressing)> generate, out string note)
    {
        var watch = Stopwatch.StartNew();
        string path = PathIn(folder, layout, kind), unreadable = "";
        try
        {
            if (File.Exists(path))
            {
                (AshfallLevel Level, AshfallDressing Dressing)? read;
                using (var stream = File.OpenRead(path)) read = Read(stream, layout);
                if (read is { } found)
                {
                    note = string.Create(CultureInfo.InvariantCulture, $"{layout} layout read from the cache in {watch.ElapsedMilliseconds} ms ({found.Level.Parts.Count} parts, {found.Dressing.Items.Count} items, hash {found.Level.Hash:X16}; {Path.GetFileName(path)})");
                    return found;
                }
                unreadable = "; the file there was another format, key or layout";
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            // A damaged or unreadable file (Read turns every parse failure into InvalidDataException): generated below and written over; the reason stays in the note.
            unreadable = $"; the cache file was unreadable ({e.GetType().Name}: {e.Message})";
        }
        var built = generate(layout);
        long generatedMs = watch.ElapsedMilliseconds;
        string written;
        try
        {
            Directory.CreateDirectory(folder);
            string temp = $"{path}.{Environment.ProcessId}.tmp";
            try
            {
                using (var stream = File.Create(temp)) Write(stream, layout, built.Level, built.Dressing);
                File.Move(temp, path, overwrite: true);
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
            written = $"; written to {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024} KB){Prune(folder, layout, kind, DateTime.UtcNow)}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            written = $"; not written ({e.GetType().Name}: {e.Message})";
        }
        note = string.Create(CultureInfo.InvariantCulture, $"{layout} layout generated in {generatedMs} ms (hash {built.Level.Hash:X16}){written}{unreadable}");
        return built;
    }

    /// <summary>A file name of this format ends in the reader's 8 bytes and the process kind: <c>...-&lt;16 hex&gt;-&lt;kind&gt;.bin</c>.</summary>
    static readonly Regex KindEnding = new(@"-[0-9a-f]{16}-(?<kind>[a-z0-9]{1,24})\.bin$", RegexOptions.CultureInvariant);

    /// <summary>A write's temporary file: <c>&lt;file&gt;.bin.&lt;pid&gt;.tmp</c> (format 2) or <c>&lt;file&gt;.bin.tmp</c> (the first format).</summary>
    static readonly Regex TempName = new(@"\.bin(\.[0-9]+)?\.tmp$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Tidies <paramref name="folder"/> after a write of <paramref name="layout"/> by the process kind <paramref name="kind"/>: deletes this layout's files of this kind beyond the
    /// newest <see cref="KeepPerLayout"/> (the file just written always stays), this layout's files without a process kind ending (the first format's
    /// <c>&lt;layout&gt;-v17-g5-d13-&lt;hex&gt;.bin</c>, anything else named <c>&lt;layout&gt;-*.bin</c>), and write temporaries older than <see cref="StaleTempAge"/> (a crash's
    /// leftovers, of any layout or kind; a write in progress is younger). The other process kind's files, other layouts' files, any other file and any subfolder stay. A file in
    /// use is left for the next write.
    /// </summary>
    internal static string Prune(string folder, AshfallLayout layout, string kind, DateTime nowUtc)
    {
        FileInfo[] files;
        try { files = new DirectoryInfo(folder).GetFiles(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return $"; older files not pruned ({e.GetType().Name})"; }
        string prefix = layout.ToString().ToLowerInvariant() + "-", current = FileName(layout, Key, kind);
        var mine = new List<FileInfo>();
        var remove = new List<FileInfo>();
        foreach (var f in files)
        {
            string name = f.Name;
            if (TempName.IsMatch(name)) { if (nowUtc - f.LastWriteTimeUtc > StaleTempAge) remove.Add(f); continue; }
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".bin", StringComparison.Ordinal)) continue;   // another layout's, or not a layout file
            var m = KindEnding.Match(name);
            if (!m.Success) remove.Add(f);                                                  // no process kind ending: the first format, or no file of this format
            else if (m.Groups["kind"].Value == kind && name != current) mine.Add(f);        // this kind's other builds (another kind's files are never touched)
        }
        remove.AddRange(mine.OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeepPerLayout - 1));
        int removed = 0, failed = 0;
        foreach (var f in remove)
        {
            try { f.Delete(); removed++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed++; }   // in use (an older build still reading it): tried again at the next write
        }
        return (removed > 0 ? $"; {removed} older or leftover file(s) removed" : "") + (failed > 0 ? $"; {failed} could not be removed (in use)" : "");
    }

    /// <summary>Forgets this process's copies (tests).</summary>
    public static void ForgetAll() { lock (Memo) Memo.Clear(); }

    /// <summary>Writes a layout's level and dressing (the format of <see cref="FormatVersion"/>): a header, the payload, a checksum over header and payload, an end mark.</summary>
    public static void Write(Stream stream, AshfallLayout layout, AshfallLevel level, AshfallDressing dressing)
    {
        using var buffer = new MemoryStream();
        using (var w = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic); w.Write(FormatVersion); w.Write(Key); w.Write((byte)layout);
            w.Write(level.Hash);
            w.Write(level.Parts.Count);
            foreach (var part in level.Parts)
            {
                w.Write((byte)part.Kind);
                w.Write(part.Corners.Length);
                foreach (var c in part.Corners) V(w, c);
            }
            w.Write(level.Points.Count);
            foreach (var p in level.Points) { w.Write(p.Name); V(w, p.Center); w.Write(p.Radius); w.Write(p.HalfHeight); }
            w.Write(level.Areas.Count);
            foreach (var a in level.Areas) { w.Write((byte)a.Side); V(w, a.Center); V(w, a.Size); w.Write(a.Yaw); w.Write((byte)a.Shape); }
            w.Write(level.Spawns.Count);
            foreach (var s in level.Spawns) V(w, s);
            w.Write(dressing.Version);
            w.Write(dressing.Items.Count);
            foreach (var i in dressing.Items) { w.Write(i.Asset); V(w, i.Position); w.Write(i.Yaw); V(w, i.Size); w.Write(i.SwapXZ); w.Write((byte)i.Role); w.Write(i.Part); }
            w.Write(dressing.HiddenParts.Count);
            foreach (int h in dressing.HiddenParts) w.Write(h);
            w.Write(dressing.Structures is not null);
            if (dressing.Structures is { } structures)
            {
                w.Write(structures.Count);
                foreach (var s in structures) { w.Write(s.Parts.Count); foreach (int p in s.Parts) w.Write(p); V(w, s.Min); V(w, s.Max); }
            }
            w.Write(dressing.ItemParts.Count);
            foreach (var (first, count) in dressing.ItemParts) { w.Write(first); w.Write(count); }
            w.Write(dressing.Flags.Count);
            foreach (var f in dressing.Flags) w.Write(f);
        }
        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        stream.Write(bytes);
        using var tail = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        tail.Write(Checksum(bytes));
        tail.Write(Magic);   // the end mark: a cut file is refused
    }

    /// <summary>FNV-1a over 64 bits of the header and payload (the same family as the level's own hash); a damaged file fails it.</summary>
    public static ulong Checksum(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in bytes) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>
    /// Reads a layout file; null when it is whole but of another format, key or layout (then the caller generates); throws <see cref="InvalidDataException"/> on a damaged file
    /// (too short, no end mark, the checksum, a count, a failed parse). P2 re-review: the end mark and the checksum are checked before anything else is parsed, and any parse
    /// failure (a damaged length prefix's <see cref="FormatException"/> among them) comes out as <see cref="InvalidDataException"/>.
    /// </summary>
    public static (AshfallLevel Level, AshfallDressing Dressing)? Read(Stream stream, AshfallLayout layout)
    {
        byte[] all;
        using (var whole = new MemoryStream()) { stream.CopyTo(whole); all = whole.ToArray(); }
        if (all.Length < 4 + 4 + 8 + 4) throw new InvalidDataException("the layout file is too short");
        // The tail first: the checksum over everything before it, then the end mark (little-endian, as BinaryWriter wrote them).
        int tailAt = all.Length - 12;
        ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(all.AsSpan(tailAt, 8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(all.AsSpan(tailAt + 8, 4)) != Magic) throw new InvalidDataException("the layout file has no end mark (cut short)");
        if (Checksum(all.AsSpan(0, tailAt)) != stored) throw new InvalidDataException("the layout file's checksum does not match its contents (damaged, or not this format)");
        try { return Parse(all, tailAt, layout); }
        catch (Exception e) when (e is not (InvalidDataException or OutOfMemoryException))
        {
            // Whole by its checksum yet not parsable (a bad length prefix, a value out of range): unreadable like a damaged file, never another kind of exception.
            throw new InvalidDataException($"the layout file could not be parsed ({e.GetType().Name}: {e.Message})", e);
        }
    }

    /// <summary>The header and payload of a file whose tail and checksum held (<see cref="Read"/>).</summary>
    static (AshfallLevel Level, AshfallDressing Dressing)? Parse(byte[] all, int tailAt, AshfallLayout layout)
    {
        using var r = new BinaryReader(new MemoryStream(all, 0, tailAt, writable: false), System.Text.Encoding.UTF8);
        if (r.ReadUInt32() != Magic || r.ReadUInt32() != FormatVersion || r.ReadString() != Key || r.ReadByte() != (byte)layout) return null;
        ulong hash = r.ReadUInt64();
        var parts = new AshfallPart[Count(r, 2_000_000)];
        for (int i = 0; i < parts.Length; i++)
        {
            var kind = (AshfallKind)r.ReadByte();
            var corners = new Vector3[Count(r, 1_000_000)];
            for (int c = 0; c < corners.Length; c++) corners[c] = V(r);
            parts[i] = new AshfallPart(kind, corners);
        }
        var points = new ControlPoint[Count(r, 1_000)];
        for (int i = 0; i < points.Length; i++) points[i] = new ControlPoint(r.ReadString(), V(r), r.ReadSingle(), r.ReadSingle());
        var areas = new StartArea[Count(r, 1_000)];
        for (int i = 0; i < areas.Length; i++) areas[i] = new StartArea((Team)r.ReadByte(), V(r), V(r), r.ReadSingle(), (AreaShape)r.ReadByte());
        var spawns = new Vector3[Count(r, 100_000)];
        for (int i = 0; i < spawns.Length; i++) spawns[i] = V(r);
        uint dressVersion = r.ReadUInt32();
        var items = new AshfallDressItem[Count(r, 1_000_000)];
        for (int i = 0; i < items.Length; i++) items[i] = new AshfallDressItem(r.ReadString(), V(r), r.ReadSingle(), V(r), r.ReadBoolean(), (AshfallDressRole)r.ReadByte(), r.ReadInt32());
        var hidden = new int[Count(r, 2_000_000)];
        for (int i = 0; i < hidden.Length; i++) hidden[i] = r.ReadInt32();
        AshfallStructure[]? structures = null;
        if (r.ReadBoolean())
        {
            structures = new AshfallStructure[Count(r, 1_000_000)];
            for (int i = 0; i < structures.Length; i++)
            {
                var sp = new int[Count(r, 1_000_000)];
                for (int k = 0; k < sp.Length; k++) sp[k] = r.ReadInt32();
                structures[i] = new AshfallStructure(sp, V(r), V(r));
            }
        }
        var itemParts = new (int First, int Count)[Count(r, 1_000_000)];
        for (int i = 0; i < itemParts.Length; i++) itemParts[i] = (r.ReadInt32(), r.ReadInt32());
        var flags = new string[Count(r, 1_000_000)];
        for (int i = 0; i < flags.Length; i++) flags[i] = r.ReadString();
        if (r.BaseStream.Position != tailAt) throw new InvalidDataException("the layout file holds more than its payload");
        var level = new AshfallLevel(parts, points, areas, spawns, hash) { Layout = layout };
        var dressing = new AshfallDressing(items, hidden, dressVersion, structures) { ItemParts = itemParts, Flags = flags };
        return (level, dressing);
    }

    static int Count(BinaryReader r, int most)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > most) throw new InvalidDataException($"a count of {n} in the layout file (at most {most})");
        return n;
    }

    static void V(BinaryWriter w, Vector3 v) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
    static Vector3 V(BinaryReader r) => new(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
}
