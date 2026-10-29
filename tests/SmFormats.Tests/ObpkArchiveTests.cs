using System.Security.Cryptography;
using System.Text;
using SmFormats;
using SmFormats.Obpk;

namespace SmFormats.Tests;

public class ObpkArchiveTests
{
    static readonly SyntheticObpkWriter.File[] SampleFiles =
    [
        new("dcm", "marine_body", Encoding.ASCII.GetBytes("MESHDATA-1")),
        new("dcm", "bolter", new byte[5000]),
        new("dds", "marine_body_dif", Enumerable.Range(0, 70000).Select(i => (byte)i).ToArray(), @"art\textures\"),
        new("attr_pc", "bolter", [1, 2, 3]),
        new("empty", "zero", []),
    ];

    static ObpkArchive OpenSample(out byte[] bytes)
    {
        bytes = SyntheticObpkWriter.Build(SampleFiles);
        return ObpkArchive.Open(new MemoryStream(bytes, writable: false));
    }

    [Fact]
    public void Parses_header_tables_and_groups()
    {
        using var a = OpenSample(out _);
        Assert.Equal(10u, a.Header.Version);
        Assert.Equal((uint)SampleFiles.Length, a.Header.FileCount);
        Assert.Equal(["dcm", "dds", "attr_pc", "empty"], a.Groups.Select(g => g.Name));
        Assert.Equal(SampleFiles.Length, a.Entries.Count);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Builds_paths_from_folder_name_and_group()
    {
        using var a = OpenSample(out _);
        var paths = a.Entries.Select(e => e.Path(a.GroupName(e))).ToList();
        Assert.Contains("marine_body.dcm", paths);
        Assert.Contains("art/textures/marine_body_dif.dds", paths);
    }

    [Fact]
    public void Reads_every_entry_byte_exact()
    {
        using var a = OpenSample(out _);
        var byName = SampleFiles.ToDictionary(f => f.Group + "/" + f.Name, f => f.Data);
        int seen = 0;
        a.ReadEntries(_ => true, (e, data) =>
        {
            Assert.Equal(byName[a.GroupName(e) + "/" + e.FileName], data.ToArray());
            seen++;
        });
        Assert.Equal(SampleFiles.Length, seen);
    }

    [Fact]
    public void Reads_single_entry_in_the_middle()
    {
        using var a = OpenSample(out _);
        var tex = a.Entries.Single(e => e.FileName == "marine_body_dif");
        Assert.Equal(SampleFiles[2].Data, a.ReadEntry(tex));
    }

    [Fact]
    public void Never_writes_to_source_stream()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        var before = SHA256.HashData(bytes);
        using (var a = ObpkArchive.Open(new MemoryStream(bytes, writable: false)))
            a.ReadEntries(_ => true, (_, _) => { });
        Assert.Equal(before, SHA256.HashData(bytes));
    }

    [Fact]
    public void Rejects_missing_magic()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        bytes[0] = (byte)'X';
        Assert.Throws<InvalidDataException>(() => ObpkArchive.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Every_truncation_of_the_header_and_table_fails_cleanly()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        using var full = ObpkArchive.Open(new MemoryStream(bytes));
        for (int len = 0; len < full.DataOffset + 4; len++)
        {
            var cut = new MemoryStream(bytes, 0, len);
            Assert.Throws<InvalidDataException>(() => ObpkArchive.Open(cut));
        }
    }

    [Fact]
    public void Truncated_compressed_data_fails_on_read_not_silently()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        using var full = ObpkArchive.Open(new MemoryStream(bytes));
        var cut = bytes.AsSpan(0, (int)full.DataOffset + 20).ToArray();
        using var a = ObpkArchive.Open(new MemoryStream(cut));
        Assert.ThrowsAny<Exception>(() => a.ReadEntries(_ => true, (_, _) => { }));
    }

    [Fact]
    public void Implausible_string_length_is_rejected()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        using var full = ObpkArchive.Open(new MemoryStream(bytes));
        // First name length sits after the ID list block (count + len + one value = 12 bytes).
        int pos = (int)full.Header.TableOffset + 12 + 4;
        BitConverter.GetBytes(0x7FFFFFFFu).CopyTo(bytes, pos);
        Assert.Throws<InvalidDataException>(() => ObpkArchive.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void Header_size_mismatch_is_reported_as_warning()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        BitConverter.GetBytes(1u).CopyTo(bytes, 29); // DecompressedSize field (offset 29)
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Contains(a.Warnings, w => w.Contains("header says"));
    }

    [Fact]
    public void Name_table_is_a_string_pool_without_duplicates()
    {
        // Same layout as the real files: "a" repeats within dcm and across groups, "b" is new in bmat.
        var bytes = SyntheticObpkWriter.Build(
        [
            new("dcm", "a", [1]),
            new("dcm", "a", [2]),
            new("bmat", "a", [3]),
            new("bmat", "b", [4]),
        ]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Equal(["dcm", "a", "bmat", "b"], a.Names);
        Assert.Equal(["dcm", "bmat"], a.Groups.Select(g => g.Name));
        Assert.Equal(["a.dcm", "a.dcm", "a.bmat", "b.bmat"], a.Entries.Select(e => e.Path(a.GroupName(e))));
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Name_table_that_does_not_fit_the_pool_layout_is_reported()
    {
        var bytes = SyntheticObpkWriter.Build(SampleFiles);
        using var full = ObpkArchive.Open(new MemoryStream(bytes));
        // Rename the pooled "bolter" (first occurrence, before the entry records) to "bolteR".
        int pos = bytes.AsSpan((int)full.Header.TableOffset).IndexOf("bolter"u8) + (int)full.Header.TableOffset;
        bytes[pos + 5] = (byte)'R';
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Contains(a.Warnings, w => w.Contains("string-pool layout"));
    }

    [Fact]
    public void Paths_treat_backslash_root_and_separators_like_forward_slashes()
    {
        var e = new ObpkEntry(0, 0, "x", @"cp\maps\", 0, 0, 0, []);
        Assert.Equal("cp/maps/x.nav", e.Path("nav"));
        Assert.Equal("x.nav", (e with { Folder = @"\" }).Path("nav"));
        Assert.Equal("x.nav", (e with { Folder = "/" }).Path("nav"));
    }

    [Fact]
    public void Empty_group_in_the_middle_keeps_names_aligned()
    {
        var bytes = SyntheticObpkWriter.Build([new("dcm", "a", [1]), new("nav", "b", [2])], ["dcm", "lua", "nav"]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Equal(["dcm", "lua", "nav"], a.Groups.Select(g => g.Name));
        Assert.Equal(0u, a.Groups[1].FileCount);
        Assert.Empty(a.Warnings);
    }

    [Fact]
    public void Type_name_equal_to_its_first_file_name_is_named_with_a_warning()
    {
        // Pool holds "world" once, serving as both the type name and the file name.
        var bytes = SyntheticObpkWriter.Build([new("dcm", "a", [1]), new("world", "world", [2])]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Equal(["dcm", "world"], a.Groups.Select(g => g.Name));
        Assert.Contains(a.Warnings, w => w.Contains("Group 1") && w.Contains("assumed"));
        Assert.DoesNotContain(a.Warnings, w => w.Contains("string-pool layout"));
    }

    [Fact]
    public void Group_whose_strings_were_all_pooled_earlier_does_not_take_the_next_type_name()
    {
        // Group "x" has type and file names already in the pool, so its next pool string belongs to "nav".
        // The table cannot prove that, so neither group is named, and both are warned about.
        var bytes = SyntheticObpkWriter.Build([new("dcm", "x", [1]), new("x", "dcm", [2]), new("nav", "n", [3])]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.Null(a.Groups[1].Name);
        Assert.Null(a.Groups[2].Name);
        Assert.Contains(a.Warnings, w => w.StartsWith("Group 1:"));
        Assert.Contains(a.Warnings, w => w.StartsWith("Group 2:"));
    }

    [Fact]
    public void Correct_provisional_name_is_not_moved_to_the_next_group()
    {
        // Group "lua" has no new files and rightly takes "lua"; group "world" has type == first file name.
        var bytes = SyntheticObpkWriter.Build([new("dcm", "a", [1]), new("lua", "a", [2]), new("world", "world", [3])]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.NotEqual("lua", a.Groups[2].Name);
        Assert.Contains(a.Warnings, w => w.StartsWith("Group 2:"));
    }

    [Fact]
    public void Synthetic_archives_are_byte_reproducible()
    {
        Assert.Equal(SyntheticObpkWriter.Build(SampleFiles), SyntheticObpkWriter.Build(SampleFiles));
    }

    [Fact]
    public void HasMagic_detects_signature()
    {
        Assert.True(ObpkArchive.HasMagic("OBPK\0rest"u8));
        Assert.False(ObpkArchive.HasMagic("OBPK"u8));
    }
}

public class ObpkSurveyTests
{
    [Fact]
    public void Survey_agrees_on_sizes_and_samples_each_group()
    {
        var bytes = SyntheticObpkWriter.Build(
        [
            new("dcm", "a", Encoding.ASCII.GetBytes("MESH-A")),
            new("dcm", "b", Encoding.ASCII.GetBytes("MESH-B-LONGER")),
            new("dds", "t", Encoding.ASCII.GetBytes("DDS |0123456789abcdef"), @"art\"),
            new("empty", "z", []),
        ]);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        var s = ObpkSurvey.Run(a, samplesPerGroup: 1);

        Assert.True(s.Pass);
        Assert.Equal(s.EntrySizeSum, s.DecompressedBytes);
        var dcm = s.Groups.Single(g => g.Name == "dcm");
        Assert.Equal(2, dcm.Count);
        Assert.Single(dcm.Samples);
        var dds = s.Groups.Single(g => g.Name == "dds").Samples.Single();
        Assert.Equal("art/t.dds", dds.Path);
        Assert.Equal(ObpkSurvey.SignatureLength, dds.Head.Length);
        Assert.Equal("DDS", ObpkSurvey.GuessMagic(dds.Head));
        Assert.Empty(s.Groups.Single(g => g.Name == "empty").Samples);
    }

    [Theory]
    [InlineData(new byte[] { 0x42, 0x4F, 0x44, 0xFD, 2, 1 }, "BOD")]
    [InlineData(new byte[] { 0x57, 0xE0, 0xE0, 0x57, 0x10, 0xC0, 0xC0, 0x10, 0 }, "Havok packfile")]
    [InlineData(new byte[] { 0x1B, 0x4C, 0x75, 0x61, 0x51, 0 }, "Lua 5.1 bytecode")]
    [InlineData(new byte[] { 0x42, 0x4F, 0x44, 0x20 }, null)]
    public void GuessMagic_recognises_container_signatures(byte[] head, string? expected) =>
        Assert.Equal(expected, ObpkSurvey.GuessMagic(head));

    [Fact]
    public void Survey_fails_when_header_size_disagrees()
    {
        var bytes = SyntheticObpkWriter.Build([new("dcm", "a", [1, 2, 3])]);
        BitConverter.GetBytes(99u).CopyTo(bytes, 29);
        using var a = ObpkArchive.Open(new MemoryStream(bytes));
        Assert.False(ObpkSurvey.Run(a).Pass);
    }
}

public class InstallPathsTests
{
    [Theory]
    [InlineData(@"C:\games\sm\cache", @"C:\games\sm", true)]
    [InlineData(@"C:\games", @"C:\games\sm", true)]
    [InlineData(@"C:\games\SM\", @"c:\games\sm", true)]
    [InlineData(@"C:\games\smX\cache", @"C:\games\sm", false)]
    [InlineData(@"D:\cache", @"C:\games\sm", false)]
    public void Overlaps_is_separator_aware(string a, string b, bool expected)
    {
        if (!OperatingSystem.IsWindows()) return;   // drive-letter paths
        Assert.Equal(expected, InstallPaths.Overlaps(a, b));
    }

    [Fact]
    public void FindInstallRoot_walks_up_to_the_game_folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "sm-installpaths-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data", "chunks"));
            File.WriteAllText(Path.Combine(root, "steam_appid.txt"), "55150");
            string archive = Path.Combine(root, "data", "chunks", "x.oppc");
            Assert.Equal(Path.GetFullPath(root), InstallPaths.FindInstallRoot(archive));
            Assert.True(InstallPaths.Overlaps(Path.Combine(root, "cache"), InstallPaths.FindInstallRoot(archive)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
