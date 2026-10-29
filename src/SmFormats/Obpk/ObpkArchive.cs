using System.IO.Compression;
using System.Text;

namespace SmFormats.Obpk;

/// <summary>
/// Read-only reader for OBPK ".oppc" archives (Space Marine 2011, PC).
/// Layout per docs/discovery.md#oppc-obpk-archives. Field meanings marked "unknown"
/// there are preserved raw so later investigation can use them.
/// </summary>
public sealed class ObpkArchive : IDisposable
{
    public const int HeaderSize = 49;
    static readonly byte[] Magic = "OBPK\0"u8.ToArray();

    // Sanity caps; real archives are far below these. They exist so a corrupt table
    // fails fast instead of allocating gigabytes.
    const int MaxCount = 1_000_000;
    const int MaxNameLength = 4096;

    readonly Stream _stream;
    readonly bool _ownsStream;

    public ObpkHeader Header { get; }
    public IReadOnlyList<uint[]> IdLists { get; }
    public IReadOnlyList<string> Names { get; }
    public IReadOnlyList<ObpkGroup> Groups { get; }
    public IReadOnlyList<ObpkEntry> Entries { get; }
    /// <summary>Absolute offset of the u32 decompressed-size field preceding the zlib stream.</summary>
    public long DataOffset { get; }
    public uint DataDecompressedSize { get; }
    public IReadOnlyList<string> Warnings { get; }

    ObpkArchive(Stream stream, bool ownsStream)
    {
        _stream = stream;
        _ownsStream = ownsStream;
        var warnings = new List<string>();
        long length = stream.Length;
        var r = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        if (length < HeaderSize) throw new InvalidDataException($"File is {length} bytes; smaller than the {HeaderSize}-byte OBPK header.");
        stream.Position = 0;
        var magic = r.ReadBytes(5);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Missing OBPK magic.");

        var h = new ObpkHeader(
            Version: r.ReadUInt32(),
            Reserved0: r.ReadUInt32(),
            FileCount: r.ReadUInt32(),
            Unknown17: r.ReadUInt32(),
            TableOffset: r.ReadUInt32(),
            DataOffsetFromTable: r.ReadUInt32(),
            DecompressedSize: r.ReadUInt32(),
            Reserved1: r.ReadUInt32(),
            Reserved2: r.ReadUInt32(),
            Unknown41: r.ReadUInt32(),
            Unknown45: r.ReadUInt32());
        Header = h;
        if (h.Version != 10) warnings.Add($"Header version {h.Version}; only 10 has been documented.");
        if (h.TableOffset < HeaderSize || h.TableOffset >= length) throw new InvalidDataException($"Table offset {h.TableOffset} outside file (length {length}).");
        CheckCount(h.FileCount, "file count");

        stream.Position = h.TableOffset;
        uint idCount = ReadCount(r, "ID list count");
        var ids = new List<uint[]>((int)idCount);
        for (int i = 0; i < idCount; i++)
        {
            uint n = ReadCount(r, "ID list length");
            EnsureAvailable(n * 4L, "ID list");
            var list = new uint[n];
            for (int k = 0; k < n; k++) list[k] = r.ReadUInt32();
            ids.Add(list);
        }
        IdLists = ids;

        uint nameCount = ReadCount(r, "name count");
        var names = new List<string>((int)nameCount);
        for (int i = 0; i < nameCount; i++) names.Add(ReadString(r));
        Names = names;

        uint groupCount = ReadCount(r, "group count");
        var groupRecords = new List<(ulong Hash, uint Files)>((int)groupCount);
        long filesInGroups = 0;
        for (int i = 0; i < groupCount; i++)
        {
            EnsureAvailable(12, "group record");
            uint hashLo = r.ReadUInt32(), hashHi = r.ReadUInt32();
            uint files = r.ReadUInt32();
            CheckCount(files, "files in group");
            groupRecords.Add((((ulong)hashHi << 32) | hashLo, files));
            filesInGroups += files;
        }
        if (filesInGroups != h.FileCount) warnings.Add($"Groups list {filesInGroups} files; header says {h.FileCount}.");

        var entries = new List<ObpkEntry>((int)Math.Min(filesInGroups, MaxCount));
        long offset = 0;
        for (int gi = 0; gi < groupRecords.Count; gi++)
        {
            for (int k = 0; k < groupRecords[gi].Files; k++)
            {
                string fileName = ReadString(r);
                string folder = ReadString(r);
                EnsureAvailable(21, "entry record");
                uint size = r.ReadUInt32();
                uint unk = r.ReadUInt32();
                uint size2 = r.ReadUInt32();
                ulong reserved = r.ReadUInt64();
                byte extraLen = r.ReadByte();
                EnsureAvailable(extraLen, "entry extra bytes");
                byte[] extra = r.ReadBytes(extraLen);
                if (size != size2) warnings.Add($"Entry {entries.Count} ('{fileName}'): size fields differ ({size} vs {size2}).");
                if (reserved != 0) warnings.Add($"Entry {entries.Count} ('{fileName}'): reserved bytes non-zero.");
                entries.Add(new ObpkEntry(entries.Count, gi, fileName, folder, offset, size, unk, extra));
                offset += size;
            }
        }
        Entries = entries;
        Groups = NameGroups(groupRecords, names, entries, warnings);
        long tableEnd = stream.Position;

        DataOffset = (long)h.TableOffset + h.DataOffsetFromTable;
        if (DataOffset < tableEnd || DataOffset + 4 > length) throw new InvalidDataException($"Data offset {DataOffset} invalid (table ends {tableEnd}, file {length}).");
        long aligned = (tableEnd + 0xFFF) & ~0xFFFL;
        if (DataOffset != aligned) warnings.Add($"Data offset {DataOffset} is not the 0x1000-aligned table end ({aligned}).");
        stream.Position = DataOffset;
        DataDecompressedSize = r.ReadUInt32();
        if (DataDecompressedSize != h.DecompressedSize) warnings.Add($"Data block says {DataDecompressedSize} bytes decompressed; header says {h.DecompressedSize}.");
        if (offset != DataDecompressedSize) warnings.Add($"Entries sum to {offset} bytes; data block says {DataDecompressedSize}.");
        Warnings = warnings;

        uint ReadCount(BinaryReader br, string what)
        {
            EnsureAvailable(4, what);
            uint v = br.ReadUInt32();
            CheckCount(v, what);
            return v;
        }
        string ReadString(BinaryReader br)
        {
            EnsureAvailable(4, "string length");
            uint n = br.ReadUInt32();
            if (n > MaxNameLength) throw new InvalidDataException($"String length {n} at {stream.Position - 4} exceeds {MaxNameLength}.");
            EnsureAvailable(n, "string bytes");
            return Encoding.ASCII.GetString(br.ReadBytes((int)n));
        }
        void EnsureAvailable(long n, string what)
        {
            if (stream.Position + n > length) throw new InvalidDataException($"Truncated {what} at offset {stream.Position}: need {n} bytes, file length {length}.");
        }
        static void CheckCount(uint v, string what)
        {
            if (v > MaxCount) throw new InvalidDataException($"Implausible {what}: {v}.");
        }
    }

    /// <summary>
    /// The name table is a string pool without duplicates, in first-use order over the sequence
    /// "group type name, then that group's file names", group by group (Verified-local on all 2736
    /// archives, see docs/discovery.md). Walking it alongside the entries gives each group's type name.
    /// </summary>
    static List<ObpkGroup> NameGroups(List<(ulong Hash, uint Files)> records, List<string> pool, List<ObpkEntry> entries, List<string> warnings)
    {
        var names = new string?[records.Count];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int cursor = 0, first = 0, guessed = -1;
        bool inOrder = true;
        for (int gi = 0; gi < records.Count; gi++)
        {
            var files = entries.GetRange(first, (int)records[gi].Files);
            first += files.Count;
            string? firstNew = files.Select(e => e.FileName).FirstOrDefault(n => !seen.Contains(n));
            bool atCursor = cursor < pool.Count && !seen.Contains(pool[cursor]);
            // The type name precedes the group's new file names. None of these cases occur in the real install;
            // they are handled so a mismatch is attributed to the right group instead of shifting names silently.
            if (atCursor && pool[cursor] != firstNew)
            {
                // With no new file names, the next pool string may instead be the next group's type name
                // (if this group's was pooled earlier). Accept it provisionally; the next group confirms.
                if (firstNew is null) guessed = gi;
                names[gi] = pool[cursor++];
                seen.Add(names[gi]!);
            }
            else if (guessed == gi - 1 && gi > 0 && firstNew is not null && SameHashName(records, names, gi) is null)
            {
                // Either the previous group took this group's type name, or its guess was right and this group's
                // type name equals its first file name (or was pooled earlier). The table cannot tell these apart.
                names[gi - 1] = SameHashName(records, names, gi - 1);
                warnings.Add($"Group {gi - 1}: type name is ambiguous in the name table.");
                warnings.Add($"Group {gi}: type name is ambiguous in the name table.");
            }
            else if (atCursor && SameHashName(records, names, gi) is null)
            {
                // The type name equals the group's first new file name, so the pool holds it once.
                names[gi] = pool[cursor];
                warnings.Add($"Group {gi}: type name '{names[gi]}' assumed equal to its first file name.");
            }
            else
            {
                names[gi] = SameHashName(records, names, gi);
                if (names[gi] is null) warnings.Add($"Group {gi}: type name is not at its expected place in the name table.");
            }
            if (guessed != gi) guessed = -1;
            foreach (var e in files)
            {
                if (!seen.Add(e.FileName)) continue;
                if (cursor >= pool.Count || pool[cursor] != e.FileName) inOrder = false;
                cursor++;
            }
        }
        var groups = records.Select((r, gi) => new ObpkGroup(gi, r.Hash, r.Files, names[gi])).ToList();
        if (!inOrder || cursor != pool.Count)
            warnings.Add($"Name table ({pool.Count} strings) does not match the string-pool layout implied by groups and entries ({cursor}).");
        return groups;
    }

    /// The group hash is constant per type name across all real archives, so an earlier group with the same hash names this one.
    static string? SameHashName(List<(ulong Hash, uint Files)> records, string?[] names, int gi)
    {
        for (int k = 0; k < gi; k++)
            if (records[k].Hash == records[gi].Hash && names[k] is not null) return names[k];
        return null;
    }

    /// <summary>Opens a file read-only and shared, so the original installation is never locked or modified.</summary>
    public static ObpkArchive Open(string path) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), ownsStream: true);

    public static ObpkArchive Open(Stream stream, bool leaveOpen = false)
    {
        if (!stream.CanRead || !stream.CanSeek) throw new ArgumentException("Stream must be readable and seekable.", nameof(stream));
        return new(stream, ownsStream: !leaveOpen);
    }

    public static bool HasMagic(ReadOnlySpan<byte> firstBytes) => firstBytes.StartsWith(Magic);

    public string GroupName(ObpkEntry e) => Groups[e.GroupIndex].Name ?? $"group{e.GroupIndex}";

    /// <summary>
    /// Streams the single zlib block once and invokes <paramref name="visit"/> for each wanted entry,
    /// in archive order. Entries are contiguous, so random access costs a decompress up to that point.
    /// </summary>
    public void ReadEntries(Func<ObpkEntry, bool> want, Action<ObpkEntry, ReadOnlySpan<byte>> visit)
    {
        _stream.Position = DataOffset + 4;
        using var z = new ZLibStream(_stream, CompressionMode.Decompress, leaveOpen: true);
        long pos = 0;
        var skipBuf = new byte[81920];
        foreach (var e in Entries)
        {
            if (!want(e)) continue;
            Skip(z, e.Offset - pos, skipBuf);
            var data = new byte[e.Size];
            z.ReadExactly(data);
            pos = e.Offset + e.Size;
            visit(e, data);
        }
    }

    public byte[] ReadEntry(ObpkEntry entry)
    {
        byte[]? result = null;
        ReadEntries(e => e.Index == entry.Index, (_, d) => result = d.ToArray());
        return result ?? throw new InvalidOperationException("Entry not found.");
    }

    static void Skip(Stream s, long count, byte[] buf)
    {
        while (count > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n == 0) throw new EndOfStreamException("Compressed data ended before the entry table did.");
            count -= n;
        }
    }

    public void Dispose()
    {
        if (_ownsStream) _stream.Dispose();
    }
}

/// Field names with numbers carry their decimal byte offset.
/// <param name="Unknown17">Observed as 40 in one sample (GameExtractor). Meaning unknown.</param>
/// <param name="DataOffsetFromTable">Data block offset relative to <paramref name="TableOffset"/>.</param>
/// <param name="Unknown41">Observed 10000 (0x2710). Meaning unknown.</param>
/// <param name="Unknown45">Observed 50000 (0xC350). Meaning unknown.</param>
public sealed record ObpkHeader(
    uint Version, uint Reserved0, uint FileCount, uint Unknown17, uint TableOffset,
    uint DataOffsetFromTable, uint DecompressedSize, uint Reserved1, uint Reserved2,
    uint Unknown41, uint Unknown45);

/// <param name="Hash">64-bit value preceding each group. Identical for the same type name in every archive (Verified-local).</param>
/// <param name="Name">Type name from the name table, used as the file extension (e.g. "dds", "dcm"). Null if it could not be placed.</param>
public sealed record ObpkGroup(int Index, ulong Hash, uint FileCount, string? Name);

/// <param name="Folder">Backslash-separated with a trailing backslash; "\" is the root. Synthetic fixtures may use "/".</param>
/// <param name="Offset">Offset within the decompressed data block.</param>
/// <param name="Unknown">Index into <see cref="ObpkArchive.IdLists"/>, or 0xFFFFFFFF for none (Verified-local on every entry).
/// What the ID lists mean is unknown.</param>
/// <param name="Extra">Trailing bytes. Real files: 4 bytes = u32 copy of the size for most types; 8 bytes for dcm = the
/// entry's first 8 data bytes; 7 or 9 bytes for dds (meaning unknown, likely texture format metadata).</param>
public sealed record ObpkEntry(
    int Index, int GroupIndex, string FileName, string Folder, long Offset, uint Size, uint Unknown, byte[] Extra)
{
    /// <summary>Relative path with forward slashes. Not unique: real archives repeat folder+name+type (use <see cref="Index"/>).</summary>
    public string Path(string groupName)
    {
        string folder = Folder.Replace('\\', '/').Trim('/');
        return (folder.Length == 0 ? "" : folder + "/") + FileName + "." + groupName;
    }
}
