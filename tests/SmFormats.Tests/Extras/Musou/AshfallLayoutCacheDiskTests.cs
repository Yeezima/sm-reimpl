using System.Buffers.Binary;
using SmContent.Extras.Musou;
using SmSim.Extras.Musou.Ashfall;

namespace SmFormats.Tests.Extras.Musou;

/// <summary>
/// Polish pass P2 re-review: the Ashfall layout cache on disk (a round trip through the temporary file, a damaged file refused and written over, a damaged length prefix, the
/// pruning), each test in a temporary folder of its own with a synthetic generator, through the cache's internal folder-and-kind entry points. The real cache stays off in this
/// process (TestProcessSetup) and is never read or written here.
/// </summary>
public sealed class AshfallLayoutCacheDiskTests : IDisposable
{
    const string Kind = "testkind", OtherKind = "smserver";
    readonly string dir = Path.Combine(Path.GetTempPath(), "sm-ashfall-cache-test-" + Guid.NewGuid().ToString("N"));
    int generated;

    public AshfallLayoutCacheDiskTests()
    {
        Assert.True(AshfallLayoutCache.Disabled);   // the test process's real cache stays off
        Assert.NotEqual(Path.GetFullPath(AshfallLayoutCache.Folder), Path.GetFullPath(dir));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    (AshfallLevel Level, AshfallDressing Dressing) Generate(AshfallLayout layout) { generated++; return AshfallLayoutCacheTests.Synthetic(); }

    (AshfallLevel Level, AshfallDressing Dressing) Load(out string note) => AshfallLayoutCache.LoadOrGenerate(AshfallLayout.City, dir, Kind, Generate, out note);

    string Current => AshfallLayoutCache.PathIn(dir, AshfallLayout.City, Kind);

    static string Hex(int n) => n.ToString("x16");

    /// <summary>A key of this format for another build (the generator's and the reader's 8 bytes made up).</summary>
    static string KeyLike(int build) => $"v{AshfallMap.Version}-g{AshfallMap.GrayboxVersion}-d{AshfallMap.DressVersion}-{Hex(build)}-{Hex(build + 1000)}";

    /// <summary>The first format's key: the versions and one build id, no reader id and no process kind after it.</summary>
    static string FirstFormatKey => $"v{AshfallMap.Version}-g{AshfallMap.GrayboxVersion}-d{AshfallMap.DressVersion}-{Hex(7)}";

    string Put(string name, TimeSpan age)
    {
        string path = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void A_layout_is_written_once_through_a_temporary_file_and_read_back()
    {
        var first = Load(out string wrote);
        Assert.Equal(1, generated);
        Assert.Contains("generated", wrote);
        Assert.Contains("written to " + Path.GetFileName(Current), wrote);
        Assert.True(File.Exists(Current));
        Assert.Single(Directory.GetFiles(dir));   // the temporary was moved over the file, nothing else left
        Assert.EndsWith($"-{Kind}.bin", Current);

        var second = Load(out string read);
        Assert.Equal(1, generated);   // read, not generated
        Assert.Contains("read from the cache", read);
        Assert.Equal(first.Level.Hash, second.Level.Hash);
        Assert.Equal(AshfallLayout.City, second.Level.Layout);
        Assert.Equal(first.Level.Parts.Count, second.Level.Parts.Count);
        Assert.Equal(first.Level.Points, second.Level.Points);
        Assert.Equal(first.Level.Spawns, second.Level.Spawns);
        Assert.Equal(first.Dressing.Items, second.Dressing.Items);
        Assert.Equal(first.Dressing.Flags, second.Dressing.Flags);
    }

    [Fact]
    public void A_damaged_file_is_refused_generated_again_and_written_over()
    {
        Load(out _);
        var bytes = File.ReadAllBytes(Current);
        bytes[bytes.Length / 2] ^= 0x55;
        File.WriteAllBytes(Current, bytes);

        Load(out string note);
        Assert.Equal(2, generated);
        Assert.Contains("unreadable", note);
        Assert.Contains("checksum", note);
        Assert.Contains("written to", note);
        using (var stream = File.OpenRead(Current)) Assert.NotNull(AshfallLayoutCache.Read(stream, AshfallLayout.City));   // the file written over is whole

        Load(out string again);
        Assert.Equal(2, generated);
        Assert.Contains("read from the cache", again);
    }

    /// <summary>A synthetic file whose key's length prefix (just after the magic and the format version) is no 7-bit encoded integer: ReadString's FormatException.</summary>
    static byte[] DamagedLengthPrefix(bool fixChecksum)
    {
        var (level, dressing) = AshfallLayoutCacheTests.Synthetic();
        using var stream = new MemoryStream();
        AshfallLayoutCache.Write(stream, AshfallLayout.City, level, dressing);
        var bytes = stream.ToArray();
        for (int i = 8; i < 13; i++) bytes[i] = 0xFF;   // five bytes with the high bit set: the length never ends
        if (fixChecksum)
        {
            int tailAt = bytes.Length - 12;
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(tailAt, 8), AshfallLayoutCache.Checksum(bytes.AsSpan(0, tailAt)));
        }
        return bytes;
    }

    [Fact]
    public void A_damaged_length_prefix_is_unreadable_not_a_crash_and_the_file_is_written_over()
    {
        // As damage: the checksum refuses the file before anything of it is parsed.
        var damaged = Assert.Throws<InvalidDataException>(() => AshfallLayoutCache.Read(new MemoryStream(DamagedLengthPrefix(fixChecksum: false)), AshfallLayout.City));
        Assert.Contains("checksum", damaged.Message);
        // Even under a checksum that holds, the parse failure comes out as InvalidDataException (the cache's "unreadable"), not as a FormatException.
        var parse = Assert.Throws<InvalidDataException>(() => AshfallLayoutCache.Read(new MemoryStream(DamagedLengthPrefix(fixChecksum: true)), AshfallLayout.City));
        Assert.IsType<FormatException>(parse.InnerException);

        // On disk: the load generates, says why and writes the file over.
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Current, DamagedLengthPrefix(fixChecksum: true));
        var built = Load(out string note);
        Assert.Equal(1, generated);
        Assert.Contains("unreadable", note);
        Assert.Contains("FormatException", note);
        using var stream = File.OpenRead(Current);
        Assert.Equal(built.Level.Hash, AshfallLayoutCache.Read(stream, AshfallLayout.City)!.Value.Level.Hash);
    }

    [Fact]
    public void A_write_keeps_this_kind_s_newest_two_and_removes_old_formats_and_stale_temporaries_only()
    {
        var h = TimeSpan.FromHours(1);
        // This kind's other builds: the newest stays (two with the file written now), the older go.
        string mineNewest = Put($"city-{KeyLike(3)}-{Kind}.bin", 1 * h), mineOld = Put($"city-{KeyLike(2)}-{Kind}.bin", 2 * h), mineOldest = Put($"city-{KeyLike(1)}-{Kind}.bin", 3 * h);
        // The other kind's files: all stay, however old or many.
        string[] other = [Put($"city-{KeyLike(1)}-{OtherKind}.bin", 5 * h), Put($"city-{KeyLike(2)}-{OtherKind}.bin", 4 * h), Put($"city-{KeyLike(3)}-{OtherKind}.bin", 3 * h)];
        // The first format (no process kind ending) and anything else named city-*.bin: removed.
        string firstFormat = Put($"city-{FirstFormatKey}.bin", 1 * h), oddName = Put("city-something.bin", 1 * h);
        // Write temporaries: stale ones go (both formats' names, any layout or kind); a fresh one (a write in progress) stays.
        string staleTemp = Put($"city-{KeyLike(2)}-{Kind}.bin.4242.tmp", 1 * h);
        string staleFirstFormatTemp = Put($"city-{FirstFormatKey}.bin.tmp", 1 * h);
        string staleOtherTemp = Put($"field-{KeyLike(2)}-{OtherKind}.bin.77.tmp", 1 * h);
        string freshTemp = Put($"city-{KeyLike(9)}-{OtherKind}.bin.555.tmp", TimeSpan.FromMinutes(1));
        // Other layouts' files, foreign files and a subfolder: never touched.
        string[] foreign =
        [
            Put($"field-{FirstFormatKey}.bin", 9 * h), Put($"field-{KeyLike(1)}-{Kind}.bin", 9 * h), Put("notes.txt", 9 * h), Put("foreign.tmp", 9 * h),
            Put("cityscape.bin", 9 * h), Put("city-notes.txt", 9 * h), Put(Path.Combine("sub", $"city-{FirstFormatKey}.bin"), 9 * h),
            Put(Path.Combine("sub", $"city-{KeyLike(1)}-{Kind}.bin.1.tmp"), 9 * h),
        ];

        Load(out string note);
        Assert.Equal(1, generated);
        Assert.Contains("7 older or leftover file(s) removed", note);
        Assert.True(File.Exists(Current));
        Assert.True(File.Exists(mineNewest));
        foreach (string gone in new[] { mineOld, mineOldest, firstFormat, oddName, staleTemp, staleFirstFormatTemp, staleOtherTemp })
            Assert.False(File.Exists(gone), Path.GetFileName(gone) + " should be removed");
        foreach (string stays in other.Concat(foreign).Append(freshTemp))
            Assert.True(File.Exists(stays), Path.GetFileName(stays) + " should stay");
        Assert.Equal(2, Directory.GetFiles(dir).Count(f => Path.GetFileName(f).StartsWith("city-", StringComparison.Ordinal) && f.EndsWith($"-{Kind}.bin", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_temporary_file_goes_only_once_it_is_stale()
    {
        var now = DateTime.UtcNow;
        string young = Put($"city-{KeyLike(1)}-{Kind}.bin.10.tmp", AshfallLayoutCache.StaleTempAge - TimeSpan.FromMinutes(1));
        string stale = Put($"city-{KeyLike(2)}-{Kind}.bin.11.tmp", AshfallLayoutCache.StaleTempAge + TimeSpan.FromMinutes(1));
        string note = AshfallLayoutCache.Prune(dir, AshfallLayout.City, Kind, now);
        Assert.True(File.Exists(young));   // a write in progress is never touched
        Assert.False(File.Exists(stale));
        Assert.Contains("1 older or leftover file(s) removed", note);
    }

    [Fact]
    public void A_file_in_use_is_left_for_the_next_write_without_an_error()
    {
        var h = TimeSpan.FromHours(1);
        Put($"city-{KeyLike(3)}-{Kind}.bin", 1 * h);
        string busy = Put($"city-{KeyLike(2)}-{Kind}.bin", 2 * h);
        string note;
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))   // an older build still reading it (no delete sharing)
            note = AshfallLayoutCache.Prune(dir, AshfallLayout.City, Kind, DateTime.UtcNow);
        Assert.True(File.Exists(busy));
        Assert.Contains("1 could not be removed (in use)", note);
        Assert.Contains("not pruned", AshfallLayoutCache.Prune(Path.Combine(dir, "missing"), AshfallLayout.City, Kind, DateTime.UtcNow));   // no folder: a note, no throw
    }
}
