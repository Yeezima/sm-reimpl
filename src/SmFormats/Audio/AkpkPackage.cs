using System.Buffers.Binary;
using System.Text;

namespace SmFormats.Audio;

/// <summary>One file inside an AKPK package: a SoundBank or a streamed <c>.wem</c>.</summary>
public sealed record AkpkEntry(uint Id, uint LanguageId, long Offset, long Size, bool IsBank);

/// <summary>
/// Read-only reader for Wwise file packages (<c>streamingaudio/*.pck</c>, magic "AKPK").
/// Layout (docs/discovery.md, "Audio (Wwise)"; verified on all 205 root packages of the install):
/// <code>
/// "AKPK" u32 headerSize (bytes after offset 8)  u32 version (1)
/// u32 languageMapSize  u32 bankLutSize  u32 streamLutSize  [u32 externalLutSize when the header has room]
/// language map: u32 count, count × {u32 nameOffset (from the map start), u32 languageId}, UTF-16LE names
/// each LUT: u32 count, count × entry; entry size = (lutSize - 4) / count:
///   24 bytes {u32 fileId, u32 blockSize, u64 fileSize, u32 startBlock, u32 languageId} (this game)
///   20 bytes {u32 fileId, u32 blockSize, u32 fileSize, u32 startBlock, u32 languageId} (older tools)
/// file offset = startBlock × blockSize
/// </code>
/// The package is opened with <see cref="FileAccess.Read"/> only; the install is never written.
/// </summary>
public sealed class AkpkPackage : IDisposable
{
    static readonly byte[] Magic = "AKPK"u8.ToArray();

    readonly Stream _stream;
    readonly bool _leaveOpen;
    readonly Dictionary<uint, string> _languages;
    readonly Dictionary<uint, List<AkpkEntry>> _streamsById = new();

    public uint Version { get; }
    public IReadOnlyDictionary<uint, string> Languages => _languages;
    public IReadOnlyList<AkpkEntry> Banks { get; }
    public IReadOnlyList<AkpkEntry> Streams { get; }
    public IReadOnlyList<AkpkEntry> Externals { get; }
    public long Length => _stream.Length;

    AkpkPackage(Stream stream, bool leaveOpen, uint version, Dictionary<uint, string> languages,
        List<AkpkEntry> banks, List<AkpkEntry> streams, List<AkpkEntry> externals)
    {
        _stream = stream; _leaveOpen = leaveOpen; Version = version; _languages = languages;
        Banks = banks; Streams = streams; Externals = externals;
        foreach (var e in streams)
        {
            if (!_streamsById.TryGetValue(e.Id, out var list)) _streamsById[e.Id] = list = new List<AkpkEntry>(1);
            list.Add(e);
        }
    }

    public static bool HasMagic(ReadOnlySpan<byte> head) => head.StartsWith(Magic);

    public static AkpkPackage Open(string path) =>
        Read(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), leaveOpen: false);

    public static AkpkPackage Read(Stream stream, bool leaveOpen = false)
    {
        try
        {
            Span<byte> head = stackalloc byte[8];
            stream.Position = 0;
            stream.ReadExactly(head);
            if (!HasMagic(head)) throw new InvalidDataException("Missing AKPK magic.");
            uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            if (headerSize < 16 || headerSize > stream.Length - 8) throw new InvalidDataException($"Bad AKPK header size {headerSize}.");
            var h = new byte[8 + headerSize];
            head.CopyTo(h);
            stream.ReadExactly(h.AsSpan(8));

            uint version = U32(h, 8), langSize = U32(h, 12), bankSize = U32(h, 16), streamSize = U32(h, 20);
            int pos = 24;
            uint externalSize = 0;
            if ((long)headerSize - 16 > (long)langSize + bankSize + streamSize)
            {
                externalSize = U32(h, 24);
                pos = 28;
            }
            if ((long)pos + langSize + bankSize + streamSize + externalSize > h.Length)
                throw new InvalidDataException("AKPK tables overrun the header.");

            var languages = ReadLanguages(h.AsSpan(pos, (int)langSize));
            pos += (int)langSize;
            var banks = ReadLut(h.AsSpan(pos, (int)bankSize), true, stream.Length);
            pos += (int)bankSize;
            var streams = ReadLut(h.AsSpan(pos, (int)streamSize), false, stream.Length);
            pos += (int)streamSize;
            var externals = ReadLut(h.AsSpan(pos, (int)externalSize), false, stream.Length);
            return new AkpkPackage(stream, leaveOpen, version, languages, banks, streams, externals);
        }
        catch
        {
            if (!leaveOpen) stream.Dispose();
            throw;
        }
    }

    static Dictionary<uint, string> ReadLanguages(ReadOnlySpan<byte> map)
    {
        var result = new Dictionary<uint, string>();
        if (map.Length < 4) return result;
        uint count = U32(map, 0);
        if (count > (map.Length - 4) / 8) throw new InvalidDataException("AKPK language map count out of range.");
        for (int i = 0; i < count; i++)
        {
            uint nameOffset = U32(map, 4 + 8 * i), id = U32(map, 8 + 8 * i);
            if (nameOffset >= map.Length) throw new InvalidDataException("AKPK language name offset out of range.");
            var s = map[(int)nameOffset..];
            int end = 0;
            while (end + 1 < s.Length && (s[end] != 0 || s[end + 1] != 0)) end += 2;
            result[id] = Encoding.Unicode.GetString(s[..end]);
        }
        return result;
    }

    static List<AkpkEntry> ReadLut(ReadOnlySpan<byte> lut, bool isBank, long fileLength)
    {
        var result = new List<AkpkEntry>();
        if (lut.Length < 4) return result;
        uint count = U32(lut, 0);
        if (count == 0) return result;
        if ((lut.Length - 4) % count != 0) throw new InvalidDataException("AKPK lookup table size is not a multiple of its count.");
        int size = (lut.Length - 4) / (int)count;
        if (size is not (20 or 24)) throw new InvalidDataException($"Unsupported AKPK lookup entry size {size}.");
        for (int i = 0; i < count; i++)
        {
            var e = lut.Slice(4 + size * i, size);
            uint id = U32(e, 0), block = U32(e, 4);
            ulong rawSize = size == 24 ? BinaryPrimitives.ReadUInt64LittleEndian(e[8..]) : U32(e, 8);
            uint start = U32(e, size - 8), lang = U32(e, size - 4);
            ulong rawOffset = (ulong)start * block;                         // < 2^64: no overflow for two u32s
            if (block == 0 || rawOffset > (ulong)fileLength || rawSize > (ulong)fileLength - rawOffset)
                throw new InvalidDataException($"AKPK entry {id} lies outside the file.");
            long offset = (long)rawOffset, fileSize = (long)rawSize;
            result.Add(new AkpkEntry(id, lang, offset, fileSize, isBank));
        }
        return result;
    }

    public string LanguageName(uint id) => _languages.TryGetValue(id, out var n) ? n : $"lang{id}";

    /// <summary>A streamed file by ID; with several languages, prefers <paramref name="languageId"/>, then "sfx" (0).</summary>
    public AkpkEntry? FindStream(uint id, uint? languageId = null)
    {
        if (!_streamsById.TryGetValue(id, out var list)) return null;
        if (languageId is uint l && list.Find(e => e.LanguageId == l) is { } exact) return exact;
        return list.Find(e => e.LanguageId == 0) ?? list[0];
    }

    public byte[] ReadEntry(AkpkEntry entry) => ReadRange(entry.Offset, entry.Size);

    /// <summary>
    /// Reads a bank entry. Without <paramref name="loadMedia"/> the DATA chunk stays on disk: the bank keeps only
    /// the media table, and <see cref="SoundBank.TryGetMediaRange"/> gives where to read each file from.
    /// </summary>
    public SoundBank ReadBank(AkpkEntry entry, bool loadMedia = true) =>
        loadMedia ? SoundBank.Read(ReadEntry(entry)) : SoundBank.ReadWithoutMedia(_stream, entry.Offset, entry.Size);

    public byte[] ReadRange(long offset, long size)
    {
        if (offset < 0 || size < 0 || offset + size > _stream.Length || size > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(offset), "Range lies outside the package.");
        var data = new byte[size];
        _stream.Position = offset;
        _stream.ReadExactly(data);
        return data;
    }

    public void Dispose()
    {
        if (!_leaveOpen) _stream.Dispose();
    }

    static uint U32(ReadOnlySpan<byte> d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d[o..]);
}
