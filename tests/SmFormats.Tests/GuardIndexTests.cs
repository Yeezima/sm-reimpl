using System.IO.Compression;
using SmTool;

namespace SmFormats.Tests;

/// <summary>
/// The content guard's fingerprint index (smtool scan-package --index-cache): the guard's answer is the same with and without it, a stored hash is used
/// only for an unchanged file (path, size, write time), deleted files are dropped, and a broken index file is ignored and rebuilt. Synthetic files only.
/// </summary>
public sealed class GuardIndexTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "sm-guard-index-" + Guid.NewGuid().ToString("N"));
    readonly string _tree;
    readonly string _cache;

    public GuardIndexTests()
    {
        _tree = Path.Combine(_root, "tree");
        _cache = Path.Combine(_root, "guard");
        Directory.CreateDirectory(Path.Combine(_tree, "data"));
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    static byte[] Filler(int n, byte value) => Enumerable.Repeat(value, n).ToArray();

    string Zip(params (string Name, byte[] Data)[] entries)
    {
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            foreach (var (name, data) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(data);
            }
        return path;
    }

    string IndexFile => GuardIndex.FileFor(_cache, "private", _tree, skipSourceText: false);
    GuardIndex LoadIndex(string? buildId = null) => GuardIndex.Load(IndexFile, _tree, buildId, skipSourceText: false);

    /// <summary>One guard run over <paramref name="package"/>: with the index (loaded, then saved) or without it.</summary>
    (List<ScanFinding> Findings, int Hashed, GuardIndex? Index) Run(string package, bool withIndex, string? buildId = null)
    {
        var scanner = new PackageScanner();
        var index = withIndex ? LoadIndex(buildId) : null;
        scanner.AddForbiddenTree(_tree, skipSourceText: false, index);
        var findings = scanner.ScanPackage(package);
        if (withIndex) scanner.SaveIndexes();
        return (findings, scanner.ForbiddenHashesComputed, index);
    }

    static string Describe(List<ScanFinding> findings) => string.Join("\n", findings.Select(f => f.ToString()).Order(StringComparer.Ordinal));

    [Fact]
    public void AFreshIndexGivesTheSameAnswerAndTheNextRunReusesItsHashes()
    {
        File.WriteAllBytes(Path.Combine(_tree, "data", "a.dat"), Filler(10_000, 1));
        File.WriteAllBytes(Path.Combine(_tree, "data", "b.dat"), Filler(10_000, 2));
        File.WriteAllBytes(Path.Combine(_tree, "c.dat"), Filler(12_000, 3));
        // copy.txt copies b; x.txt matches nothing of its size, so both 10 000-byte files are hashed whatever the enumeration order.
        string package = Zip(("copy.txt", Filler(10_000, 2)), ("x.txt", Filler(10_000, 8)), ("other.txt", Filler(12_000, 9)));

        var plain = Run(package, withIndex: false);
        Assert.Contains(plain.Findings, f => f.Rule == "copy-of-private-file" && f.Entry == "copy.txt");
        Assert.Equal(3, plain.Hashed);

        var first = Run(package, withIndex: true);
        Assert.Equal("new", first.Index!.Status);
        Assert.Equal(0, first.Index.Reused);
        Assert.Equal(3, first.Hashed);
        Assert.Equal(Describe(plain.Findings), Describe(first.Findings));
        Assert.True(File.Exists(IndexFile));

        var stored = LoadIndex();
        Assert.Equal("loaded", stored.Status);
        Assert.Equal(3, stored.Entries.Count);
        Assert.All(stored.Entries.Values, e => Assert.NotNull(e.Hash));

        var second = Run(package, withIndex: true);
        Assert.Equal(3, second.Index!.Reused);
        Assert.Equal(0, second.Hashed);                                      // nothing of the tree read again
        Assert.Equal(Describe(plain.Findings), Describe(second.Findings));   // and the same answer
    }

    [Fact]
    public void AChangedFileIsHashedAgain()
    {
        string file = Path.Combine(_tree, "data", "a.dat");
        File.WriteAllBytes(file, Filler(10_000, 1));
        string package = Zip(("copy.txt", Filler(10_000, 7)));
        var first = Run(package, withIndex: true);
        Assert.DoesNotContain(first.Findings, f => f.Rule == "copy-of-private-file");

        // Same size, new content and a new write time: the stored hash must not be used, or the copy would be missed.
        File.WriteAllBytes(file, Filler(10_000, 7));
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddMinutes(5));
        var second = Run(package, withIndex: true);
        Assert.Equal(0, second.Index!.Reused);
        Assert.Equal(1, second.Hashed);
        Assert.Contains(second.Findings, f => f.Rule == "copy-of-private-file");
        Assert.Equal(Describe(Run(package, withIndex: false).Findings), Describe(second.Findings));

        // A different size alone is a change too (the stored entry is not looked at for another size).
        File.WriteAllBytes(file, Filler(11_000, 7));
        var third = LoadIndex();
        Assert.False(third.TryGetHash("data" + Path.DirectorySeparatorChar + "a.dat", 11_000, File.GetLastWriteTimeUtc(file).Ticks, out _));
    }

    [Fact]
    public void ADeletedFileIsDroppedFromTheIndex()
    {
        File.WriteAllBytes(Path.Combine(_tree, "data", "a.dat"), Filler(10_000, 1));
        File.WriteAllBytes(Path.Combine(_tree, "data", "gone.dat"), Filler(10_000, 2));
        string package = Zip(("copy.txt", Filler(10_000, 2)));
        Assert.Contains(Run(package, withIndex: true).Findings, f => f.Rule == "copy-of-private-file");
        Assert.Equal(2, LoadIndex().Entries.Count);

        File.Delete(Path.Combine(_tree, "data", "gone.dat"));
        var after = Run(package, withIndex: true);
        Assert.DoesNotContain(after.Findings, f => f.Rule == "copy-of-private-file");   // the stored hash of a deleted file never counts
        var stored = LoadIndex();
        Assert.Single(stored.Entries);
        Assert.False(stored.Entries.ContainsKey(Path.Combine("data", "gone.dat")));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"root\":%ROOT%,\"buildId\":null,\"skipSourceText\":false,\"files\":{\"a\":{\"s\":\"big\"}}}")]
    [InlineData("{\"version\":1,\"root\":%ROOT%,\"buildId\":null,\"skipSourceText\":false,\"files\":[]}")]
    [InlineData("{\"version\":2,\"root\":%ROOT%,\"buildId\":null,\"skipSourceText\":false,\"files\":{}}")]
    public void ACorruptIndexFileIsIgnoredAndRebuilt(string content)
    {
        File.WriteAllBytes(Path.Combine(_tree, "data", "a.dat"), Filler(10_000, 1));
        string package = Zip(("copy.txt", Filler(10_000, 1)));
        Directory.CreateDirectory(_cache);
        File.WriteAllText(IndexFile, content.Replace("%ROOT%", System.Text.Json.JsonSerializer.Serialize(_tree)));

        var run = Run(package, withIndex: true);
        Assert.NotEqual("loaded", run.Index!.Status);
        Assert.Equal(0, run.Index.Reused);
        Assert.Equal(Describe(Run(package, withIndex: false).Findings), Describe(run.Findings));

        var rebuilt = LoadIndex();
        Assert.Equal("loaded", rebuilt.Status);
        Assert.Single(rebuilt.Entries);
    }

    [Fact]
    public void AMalformedHashIsNeverTrusted()
    {
        string file = Path.Combine(_tree, "data", "a.dat");
        File.WriteAllBytes(file, Filler(10_000, 1));
        string package = Zip(("copy.txt", Filler(10_000, 1)));
        Run(package, withIndex: true);
        string text = File.ReadAllText(IndexFile);
        string hash = LoadIndex().Entries.Values.Single().Hash!;
        File.WriteAllText(IndexFile, text.Replace(hash, "not-a-hash"));
        var run = Run(package, withIndex: true);
        Assert.Equal(0, run.Index!.Reused);
        Assert.Contains(run.Findings, f => f.Rule == "copy-of-private-file");
    }

    [Fact]
    public void AnotherSteamBuildOrFolderDiscardsTheIndex()
    {
        File.WriteAllBytes(Path.Combine(_tree, "data", "a.dat"), Filler(10_000, 1));
        string package = Zip(("copy.txt", Filler(10_000, 1)));
        Run(package, withIndex: true, buildId: "7412325");
        Assert.Equal("loaded", LoadIndex("7412325").Status);
        Assert.Equal("other Steam build, rebuilt", LoadIndex("7412326").Status);
        Assert.Empty(LoadIndex("7412326").Entries);
        Assert.Equal("other folder, rebuilt", GuardIndex.Load(IndexFile, _root, "7412325", skipSourceText: false).Status);
        Assert.Throws<ArgumentException>(() => new PackageScanner().AddForbiddenTree(_tree, skipSourceText: true, LoadIndex("7412325")));
    }

    [Fact]
    public void TheBuildIdIsReadFromTheSteamManifestTwoFoldersUp()
    {
        string steamapps = Path.Combine(_root, "steamapps");
        string install = Path.Combine(steamapps, "common", "Game");
        Directory.CreateDirectory(install);
        Assert.Null(GuardIndex.SteamBuildId(install));
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_55150.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"55150\"\n\t\"buildid\"\t\t\"7412325\"\n}\n");
        Assert.Equal("7412325", GuardIndex.SteamBuildId(install));
    }

    [Fact]
    public void TheCommandKeepsTheIndexOutsideTheInstallAndGivesTheSameExitCode()
    {
        // A fake install that GameInstall.Validate accepts (data/ and the sanity archive), with one file a package copies.
        string install = Path.Combine(_root, "install");
        string sanity = Path.Combine(install, SmContent.GameInstall.SanityArchive.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(sanity)!);
        File.WriteAllBytes(sanity, Filler(9_000, 4));
        string leaky = Zip(("copy.txt", Filler(9_000, 4)));
        string clean = Zip(("fine.txt", Filler(9_000, 5)));

        Assert.Equal(1, PackageScanCommand.Run(["--against-install", install, "--require-install", leaky]));
        Assert.Equal(1, PackageScanCommand.Run(["--against-install", install, "--require-install", "--index-cache", _cache, leaky]));
        Assert.Equal(1, PackageScanCommand.Run(["--index-cache", _cache, "--against-install", install, "--require-install", leaky]));   // reused hashes: same answer
        Assert.Equal(0, PackageScanCommand.Run(["--against-install", install, "--require-install", "--index-cache", _cache, clean]));
        Assert.Single(Directory.GetFiles(_cache, "install-*.json"));

        // A cache folder inside the install is never used: nothing is written there.
        var before = Directory.GetFileSystemEntries(install, "*", SearchOption.AllDirectories).Order().ToList();
        Assert.Equal(1, PackageScanCommand.Run(["--against-install", install, "--require-install", "--index-cache", Path.Combine(install, "guard"), leaky]));
        Assert.Equal(before, Directory.GetFileSystemEntries(install, "*", SearchOption.AllDirectories).Order().ToList());
    }
}
