using System.Diagnostics;

namespace SmFormats.Obpk;

/// <summary>
/// One full decompress of an archive, collecting the M1 consistency numbers and per-group byte signatures.
/// Holds no entry data beyond the first <see cref="SignatureLength"/> bytes of a few samples per group.
/// </summary>
public static class ObpkSurvey
{
    public const int SignatureLength = 16;

    public static ArchiveSurvey Run(ObpkArchive a, int samplesPerGroup = 3)
    {
        var groups = new Dictionary<string, GroupSurvey>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in a.Entries)
        {
            string g = a.GroupName(e);
            if (!groups.TryGetValue(g, out var s)) groups[g] = s = new GroupSurvey(g);
            s.Count++;
            s.Bytes += e.Size;
        }

        long read = 0;
        var sw = Stopwatch.StartNew();
        a.ReadEntries(_ => true, (e, data) =>
        {
            read += data.Length;
            var s = groups[a.GroupName(e)];
            if (data.Length > 0 && s.Samples.Count < samplesPerGroup)
                s.Samples.Add(new EntrySample(e.Path(a.GroupName(e)), e.Size, data[..Math.Min(SignatureLength, data.Length)].ToArray()));
        });
        sw.Stop();

        return new ArchiveSurvey(
            a.Entries.Count, a.Header.FileCount, a.Header.DecompressedSize, a.DataDecompressedSize,
            a.Entries.Sum(e => (long)e.Size), read, sw.Elapsed, a.Warnings, groups.Values.OrderBy(g => g.Name).ToList());
    }

    static readonly byte[] BodMagic = [0x42, 0x4F, 0x44, 0xFD];
    static readonly byte[] HavokMagic = [0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10];
    static readonly byte[] LuacMagic = [0x1B, 0x4C, 0x75, 0x61, 0x51];

    /// <summary>Common container magics. A match is a hint for the inner-type table, not a format claim.</summary>
    public static string? GuessMagic(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("DDS "u8)) return "DDS";
        if (head.StartsWith("<?xml"u8)) return "XML";
        if (head.StartsWith("RIFF"u8)) return "RIFF";
        if (head.StartsWith("AKPK"u8)) return "Wwise AKPK";
        if (head.StartsWith("BKHD"u8)) return "Wwise BKHD";
        if (head.StartsWith("CRID"u8)) return "CRI USM";
        if (head.StartsWith("OBPK\0"u8)) return "OBPK";
        if (head.StartsWith(BodMagic)) return "BOD";
        if (head.StartsWith("BAF "u8)) return "BAF";
        if (head.StartsWith(HavokMagic)) return "Havok packfile";
        if (head.StartsWith(LuacMagic)) return "Lua 5.1 bytecode";
        if (head.StartsWith("GFX"u8) || head.StartsWith("CFX"u8)) return "Scaleform GFx";
        if (head.Length >= 2 && head[0] == 0x78 && head[1] is 0x01 or 0x5E or 0x9C or 0xDA) return "zlib?";
        if (head.Length >= 2 && head[0] == 0xFF && head[1] == 0xFE) return "UTF-16LE text?";
        return null;
    }
}

/// <param name="HeaderDecompressedSize">Header field at offset 29.</param>
/// <param name="DataBlockSize">u32 at the start of the data block.</param>
/// <param name="DecompressedBytes">Bytes actually read from the zlib stream for all entries.</param>
public sealed record ArchiveSurvey(
    int Entries, uint HeaderFileCount, uint HeaderDecompressedSize, uint DataBlockSize,
    long EntrySizeSum, long DecompressedBytes, TimeSpan Elapsed, IReadOnlyList<string> Warnings,
    IReadOnlyList<GroupSurvey> Groups)
{
    /// M1 criterion 3: decompressed = header size = data-block size = sum of entry sizes, and no warnings.
    public bool SizesAgree =>
        DecompressedBytes == HeaderDecompressedSize && HeaderDecompressedSize == DataBlockSize && DataBlockSize == EntrySizeSum;

    public bool Pass => SizesAgree && Warnings.Count == 0 && Entries == HeaderFileCount;
}

public sealed class GroupSurvey(string name)
{
    public string Name { get; } = name;
    public int Count { get; set; }
    public long Bytes { get; set; }
    public List<EntrySample> Samples { get; } = [];
}

public sealed record EntrySample(string Path, uint Size, byte[] Head);
