using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using SmContent;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>Hostile input, the codebook download and cache rules, media on demand and shared chains (M26a review).</summary>
public class AudioHardeningTests
{
    static readonly bool[] Blocks = [false, false, true, true, false, false, true, false];

    // ------------------------------------------------------------------ hostile input

    [Fact]
    public void Hirc_counts_that_cannot_fit_are_kept_raw()
    {
        var huge = new byte[] { 0xFF, 0xFF, 0xFF, 0x3F, 1, 0, 0, 0 };               // event claiming 2^30 actions
        var sw = Switch(0x20, new Node(0), 1, 2, [(2u, [0x30u])]).Payload;
        var swCut = sw[..^5];                                                       // a switch cut short inside its tables
        var bank = SoundBank.Read(Bank(1, null, [], [new Obj(HircType.Event, 0x10, huge), new Obj(HircType.Switch, 0x21, swCut)]));
        Assert.IsType<HircRaw>(bank.Find(0x10));
        Assert.NotNull(((HircRaw)bank.Find(0x21)!).ParseError);
        Assert.Equal(2, bank.ParseFailures);

        // An object whose size runs past the chunk spoils the whole HIRC: that is an InvalidDataException.
        var b = Bank(1, null, [], [Event(0x10, 1)]);
        int hirc = b.AsSpan().IndexOf("HIRC"u8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(hirc + 8 + 4 + 1), 0x7FFFFFF0);
        Assert.Throws<InvalidDataException>(() => SoundBank.Read(b));
    }

    [Fact]
    public void Akpk_entries_with_overflowing_offsets_or_sizes_are_rejected()
    {
        var good = Package([new Item(7, [1, 2, 3])], []);
        // Layout: 24-byte header, language map, bank LUT {count, entry {id, block, u64 size, start, lang}}.
        int lang = (int)BinaryPrimitives.ReadUInt32LittleEndian(good.AsSpan(12));
        int entry = 24 + lang + 4;
        var hugeSize = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(hugeSize.AsSpan(entry + 8), ulong.MaxValue - 1);
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(hugeSize)));
        var hugeOffset = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeOffset.AsSpan(entry + 4), uint.MaxValue);       // block size
        BinaryPrimitives.WriteUInt32LittleEndian(hugeOffset.AsSpan(entry + 16), uint.MaxValue);      // start block
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(hugeOffset)));
        var zeroBlock = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(zeroBlock.AsSpan(entry + 4), 0);
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(zeroBlock)));
        var hugeHeader = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeHeader.AsSpan(4), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(hugeHeader)));
        var hugeLanguages = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(hugeLanguages.AsSpan(24), 0x10000000);
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(hugeLanguages)));
    }

    [Fact]
    public void Wem_header_rejects_bad_block_sizes_channels_and_rates()
    {
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(Wem(1, 44100, Blocks, 10, true, blocksize0Exponent: 5)));
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(Wem(1, 44100, Blocks, 10, true, blocksize1Exponent: 14)));
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(Wem(1, 44100, Blocks, 10, true, 11, 8)));
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(Wem(0, 44100, Blocks, 10, true)));
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(Wem(1, 0, Blocks, 10, true)));
    }

    /// <summary>Random byte damage must end in InvalidDataException or NotSupportedException, never anything else or a hang.</summary>
    [Fact]
    public void Damaged_inputs_fail_cleanly()
    {
        var rng = new Random(26);
        var wem = Wem(2, 44100, Enumerable.Range(0, 40).Select(i => i % 3 == 1).ToArray(), 4000, inlineCodebook: false);
        var library = new VorbisCodebookLibrary(CodebookLibrary());
        var pck = Package([new Item(1, Bank(1, "B", [new Item(5, wem)], [Event(0x10, 0x11), Play(0x11, 0x12, 0, 1),
            Sound(0x12, 5, new Node(0) with { Rtpcs = 1, StateGroups = 1 })]))], [new Item(9, wem)]);
        for (int round = 0; round < 400; round++)
        {
            var w = (byte[])wem.Clone();
            var p = (byte[])pck.Clone();
            for (int k = 0; k < 1 + round % 4; k++)
            {
                w[rng.Next(w.Length)] = (byte)rng.Next(256);
                p[rng.Next(p.Length)] = (byte)rng.Next(256);
            }
            Expect(() => WwiseVorbis.Convert(w, library));
            Expect(() =>
            {
                using var lib = new WwiseLibrary();
                lib.AddPackage(AkpkPackage.Read(new MemoryStream(p)), "fuzz");
                lib.ResolveEvent(0x10).WemIds();
                lib.ReadMedia(5);
            });
        }

        static void Expect(Action a)
        {
            try { a(); }
            catch (InvalidDataException) { }
            catch (NotSupportedException) { }
            catch (EndOfStreamException) { }                                // a bank truncated inside the package
        }
    }

    // ------------------------------------------------------------------ granules

    [Fact]
    public void Trailing_empty_packets_share_the_clamped_end_and_granules_stay_monotonic()
    {
        var s = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 3500, inlineCodebook: true, emptyTailPackets: 2),
            options: new WwiseVorbisOptions(InlineCodebooks: true));
        Assert.Equal(Blocks.Length + 2, s.AudioPackets.Count);
        Assert.Equal(new long[] { 3500, 3500, 3500 }, s.Granules.TakeLast(3));
        for (int i = 1; i < s.Granules.Count; i++) Assert.True(s.Granules[i] >= s.Granules[i - 1]);
    }

    // ------------------------------------------------------------------ cache rules and the codebook download

    [Fact]
    public void Output_folders_inside_the_install_or_the_repo_are_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "smreimpl-refuse-" + Guid.NewGuid().ToString("N"));
        string game = Path.Combine(root, "game"), repo = Path.Combine(root, "repo");
        Assert.NotNull(AudioCache.RefuseOutputDir(Path.Combine(game, "streamingaudio", "out"), game, repo));
        Assert.NotNull(AudioCache.RefuseOutputDir(root, game, repo));                    // above the install
        Assert.NotNull(AudioCache.RefuseOutputDir(Path.Combine(repo, "tmp"), game, repo));
        Assert.Null(AudioCache.RefuseOutputDir(Path.Combine(root, "cache", "audio"), game, repo));
        Assert.Null(AudioCache.RefuseOutputDir(Path.Combine(root, "gamex"), game, null)); // sibling, not inside
    }

    sealed class FakeHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }

    static string TempDir() => Path.Combine(Path.GetTempPath(), "smreimpl-codebooks-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Codebook_download_is_kept_only_with_the_expected_hash()
    {
        byte[] file = CodebookLibrary();                                           // synthetic stand-in, not ww2ogg's file
        string sha = Convert.ToHexString(SHA256.HashData(file));
        Assert.True(AudioCache.HashMatches(file, sha.ToLowerInvariant()));
        Assert.False(AudioCache.HashMatches(file, AudioCache.CodebookSha256));

        string dir = TempDir();
        try
        {
            var good = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file) });
            string path = await AudioCache.EnsureCodebooksAsync(new HttpClient(good), dir, "https://example.invalid/cb.bin", sha);
            Assert.Equal(file, File.ReadAllBytes(path));
            Assert.True(AudioCache.HasCodebooks(dir, sha));
            await AudioCache.EnsureCodebooksAsync(new HttpClient(good), dir, "https://example.invalid/cb.bin", sha);
            Assert.Equal(1, good.Calls);                                            // cached: no second download
            Assert.Equal(1, AudioCache.EnsureCodebooks(new HttpClient(good), dir, "https://example.invalid/cb.bin", sha).Count);

            string dir2 = TempDir();
            var tampered = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([.. file, 0]) });
            await Assert.ThrowsAsync<InvalidDataException>(() => AudioCache.EnsureCodebooksAsync(new HttpClient(tampered), dir2, "https://example.invalid/cb.bin", sha));
            Assert.False(File.Exists(AudioCache.CodebookPath(dir2)));

            var offline = new FakeHandler(() => throw new HttpRequestException("no network"));
            var ex = await Assert.ThrowsAsync<IOException>(() => AudioCache.EnsureCodebooksAsync(new HttpClient(offline), dir2, "https://example.invalid/cb.bin", sha));
            Assert.Contains("Could not download", ex.Message);
            var missing = new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound));
            await Assert.ThrowsAsync<IOException>(() => AudioCache.EnsureCodebooksAsync(new HttpClient(missing), dir2, "https://example.invalid/cb.bin", sha));

            // A damaged cached copy is replaced.
            File.WriteAllBytes(AudioCache.CodebookPath(dir), [1, 2, 3]);
            Assert.False(AudioCache.HasCodebooks(dir, sha));
            await AudioCache.EnsureCodebooksAsync(new HttpClient(good), dir, "https://example.invalid/cb.bin", sha);
            Assert.Equal(file, File.ReadAllBytes(AudioCache.CodebookPath(dir)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Codebook_source_is_pinned()
    {
        Assert.Contains("hcs64/ww2ogg/4b4ca8ec4079a42488aae6344c3c52165e210eec/", AudioCache.CodebookUrl);
        Assert.Equal(64, AudioCache.CodebookSha256.Length);
        Assert.Throws<ArgumentNullException>(() => WwiseVorbis.Convert(Wem(1, 44100, Blocks, 10, inlineCodebook: false)));
    }

    // ------------------------------------------------------------------ media on demand, shared chains

    [Fact]
    public void Banks_read_without_media_find_the_same_bytes_in_the_package()
    {
        var media = new[] { new Item(1001, [1, 2, 3, 4]), new Item(1003, [5, 6, 7]) };
        var bankBytes = Bank(9, "B", media, [Sound(0x1, 1001, new Node(0))]);
        using var pck = AkpkPackage.Read(new MemoryStream(Package([new Item(9, bankBytes)], [])));
        var lazy = pck.ReadBank(pck.Banks[0], loadMedia: false);
        var full = pck.ReadBank(pck.Banks[0]);
        Assert.False(lazy.MediaInMemory);
        Assert.True(lazy.GetMedia(1003).IsEmpty);
        Assert.Equal(full.Objects.Count, lazy.Objects.Count);
        Assert.True(lazy.TryGetMediaRange(1003, out long off, out int size));
        Assert.Equal(new byte[] { 5, 6, 7 }, pck.ReadRange(pck.Banks[0].Offset + off, size));
        Assert.Equal(full.GetMedia(1003).ToArray(), pck.ReadRange(pck.Banks[0].Offset + off, size));
        Assert.False(lazy.TryGetMediaRange(4242, out _, out _));
    }

    [Fact]
    public void Shared_containers_resolve_once()
    {
        // 30 layers, each playing the next one twice: 2^30 paths, 31 distinct nodes.
        var objects = new List<Obj>();
        for (uint i = 0; i < 30; i++) objects.Add(Layer(0x100 + i, new Node(0), [0x101 + i, 0x101 + i]));
        objects.Add(Sound(0x100 + 30, 77, new Node(0)));
        objects.Add(Event(0xE, 0xA));
        objects.Add(Play(0xA, 0x100, 0, 1));
        using var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(1, Bank(1, null, [], objects))], []))), "dag");
        var chain = lib.ResolveEvent(0xE);
        Assert.Equal(new uint[] { 77 }, chain.WemIds());
        var top = chain.Actions[0].Target!;
        Assert.Same(top.Branches[0].Nodes[0], top.Branches[0].Nodes[1]);
    }

    [Fact]
    public void A_failing_package_leaves_the_library_unchanged()
    {
        var good = Package([new Item(1, Bank(1, "Good", [], [Event(0x10, 0x11)]))], []);
        var badBank = Bank(2, null, [], []);
        badBank[8] = 99;                                                              // version 99
        using var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(good)), "good");
        var stream = new MemoryStream(Package([new Item(2, badBank)], []));
        Assert.Throws<NotSupportedException>(() => lib.AddPackage(AkpkPackage.Read(stream), "bad"));
        Assert.Single(lib.Banks);
        Assert.False(stream.CanRead);                                                 // the failed package was disposed
    }
}
