using System.Buffers.Binary;
using SmFormats.Audio;
using static SmFormats.Audio.SyntheticWwiseWriter;

namespace SmFormats.Tests;

/// <summary>Wwise readers and the Wwise Vorbis converter, on synthetic fixtures only (no original bytes).</summary>
public class AudioTests
{
    /// <summary>A fact that is skipped unless the verified codebook library is in the private cache.</summary>
    public sealed class CodebookCacheFactAttribute : FactAttribute
    {
        public CodebookCacheFactAttribute()
        {
            if (!SmContent.AudioCache.HasCodebooks())
                Skip = $"ww2ogg's {SmContent.AudioCache.CodebookFileName} is not in {SmContent.AudioCache.CodebookDir} " +
                    "(downloaded on first use of smtool audio-decode; never committed)";
        }
    }

    // ------------------------------------------------------------------ FNV

    [Fact]
    public void Fnv1_matches_reference_vectors_and_ignores_case()
    {
        Assert.Equal(0x811c9dc5u, Fnv.Hash32(""));
        Assert.Equal(0x050c5d7eu, Fnv.Hash32("a"));
        Assert.Equal(0x31f0b262u, Fnv.Hash32("foobar"));
        Assert.Equal(Fnv.Hash32("play_sm_bolter_fire"), Fnv.Hash32("Play_SM_Bolter_Fire"));
        // IDs observed in the install for these names (an ID is a hash of a name, not game data).
        Assert.Equal(0xae144d10u, Fnv.Hash32("play_sm_bolter_fire"));
        Assert.Equal(0x2d2d0b60u, Fnv.Hash32("weapon_bolter"));
        Assert.Equal(0x41654531u, Fnv.Hash32("material_type"));
    }

    // ------------------------------------------------------------------ AKPK

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Akpk_lists_languages_banks_and_streams(bool wide)
    {
        byte[] bank = [1, 2, 3, 4, 5];
        byte[] s1 = [9, 9, 9], s2en = [7, 7], s2fr = [6];
        var bytes = Package([new Item(0xB0, bank)], [new Item(0x51, s1), new Item(0x52, s2en, 1), new Item(0x52, s2fr, 3)], wide,
            [(0, "sfx"), (1, "english(us)"), (3, "french(france)")]);
        using var p = AkpkPackage.Read(new MemoryStream(bytes));
        Assert.Equal(1u, p.Version);
        Assert.Equal("english(us)", p.Languages[1]);
        Assert.Equal("french(france)", p.LanguageName(3));
        Assert.Equal("lang9", p.LanguageName(9));
        var b = Assert.Single(p.Banks);
        Assert.Equal(0xB0u, b.Id);
        Assert.True(b.IsBank);
        Assert.Equal(0, b.Offset % 16);
        Assert.Equal(bank, p.ReadEntry(b));
        Assert.Equal(3, p.Streams.Count);
        Assert.Equal(s1, p.ReadEntry(p.FindStream(0x51)!));
        Assert.Equal(s2fr, p.ReadEntry(p.FindStream(0x52, 3)!));
        Assert.Equal(s2en, p.ReadEntry(p.FindStream(0x52)!));                // no sfx copy: first listed
        Assert.Null(p.FindStream(0x53));
    }

    [Fact]
    public void Akpk_rejects_bad_files()
    {
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream("NOPE0000000000000000000000000000"u8.ToArray())));
        var bytes = Package([new Item(1, [1, 2, 3])], []);
        var truncated = bytes[..^2];                                         // the bank now runs past the end
        Assert.Throws<InvalidDataException>(() => AkpkPackage.Read(new MemoryStream(truncated)));
    }

    // ------------------------------------------------------------------ SoundBank / HIRC

    const uint BankId = 0x2d2d0b60, Group = 0x0d500bea, Local = 0x0d500bea, Default = 0x6a6e4d03;

    static byte[] TestBank(bool feedback = false)
    {
        Node N(uint parent) => new(parent, Feedback: feedback);
        var objects = new List<Obj>
        {
            Event(0xE1, 0xA1, 0xA2, 0xA3),
            Play(0xA1, 0x5100, 0, BankId),
            Play(0xA2, 0x6100, 200, BankId, -10, 10),
            Stop(0xA3, 0x5100),
            Switch(0x5100, N(0) with { Volume = -3 }, Group, Default, [(Local, [0x5200u]), (Default, [0x5300u])]),
            Random(0x5200, N(0x5100) with { Pitch = -100, PitchMin = -50, PitchMax = 50, AttenuationId = 0xA77 }, [0x2001, 0x2002], avoidRepeat: 2),
            Sound(0x2001, 1001, N(0x5200), HircStreamType.Bank, BankId, 0, 4),
            Sound(0x2002, 1002, N(0x5200) with { FxIds = [0xF1, 0xF2], StateGroups = 2, Rtpcs = 1 }, HircStreamType.Streamed, 0x99),
            Sound(0x5300, 1003, N(0x5100) with { VolumeMin = -6, VolumeMax = 0 }, HircStreamType.Bank, BankId, 16, 3),
            Layer(0x6100, N(0), [0x6201, 0x6202]),
            Sound(0x6201, 1004, N(0x6100), HircStreamType.PrefetchStreamed, BankId, 32, 2),
            Sound(0x6202, 1001, N(0x6100), HircStreamType.Bank, BankId, 0, 4),
            new(HircType.Attenuation, 0xA77, [1, 2, 3]),
        };
        return Bank(BankId, "Weapon_Synthetic", [new Item(1001, [1, 2, 3, 4]), new Item(1003, [5, 6, 7]), new Item(1004, [8, 9])], objects, feedback);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bank_reads_header_media_name_and_typed_objects(bool feedback)
    {
        var bank = SoundBank.Read(TestBank(feedback));
        Assert.Equal(56u, bank.Version);
        Assert.Equal(BankId, bank.BankId);
        Assert.Equal(feedback, bank.FeedbackInBank);
        Assert.Equal("Weapon_Synthetic", bank.Name);
        Assert.Equal(0, bank.ParseFailures);
        Assert.Equal(3, bank.Media.Count);
        Assert.Equal(new byte[] { 5, 6, 7 }, bank.GetMedia(1003).ToArray());
        Assert.True(bank.GetMedia(424242).IsEmpty);

        var ev = Assert.IsType<HircEvent>(bank.Find(0xE1));
        Assert.Equal(new uint[] { 0xA1, 0xA2, 0xA3 }, ev.ActionIds);
        var play = Assert.IsType<HircAction>(bank.Find(0xA2));
        Assert.Equal(HircActionKind.Play, play.Kind);
        Assert.Equal((0x6100u, 200, -10, 10), (play.TargetId, play.Delay, play.DelayMin, play.DelayMax));
        Assert.Equal(BankId, play.PlayBankId);
        Assert.Equal(HircActionKind.Stop, Assert.IsType<HircAction>(bank.Find(0xA3)).Kind);

        var sw = Assert.IsType<HircSwitch>(bank.Find(0x5100));
        Assert.Equal((0u, Group, Default), (sw.GroupType, sw.GroupId, sw.DefaultSwitch));
        Assert.Equal(new uint[] { 0x5200 }, sw.NodesFor(Local));
        Assert.Equal(new uint[] { 0x5300 }, sw.NodesFor(12345));               // unknown value: default case
        Assert.Equal(new uint[] { 0x5300 }, sw.NodesFor(null));
        Assert.Equal(-3f, sw.Base.Volume.Base);

        var rnd = Assert.IsType<HircRandomSequence>(bank.Find(0x5200));
        Assert.True(rnd.IsRandom);
        Assert.Equal(2, rnd.AvoidRepeatCount);
        Assert.Equal(new uint[] { 0x2001, 0x2002 }, rnd.Children);
        Assert.Equal(new (uint, byte)[] { (0x2001, 50), (0x2002, 50) }, rnd.Playlist);
        Assert.Equal(new RangedValue(-100, -50, 50), rnd.Base.Pitch);
        Assert.True(rnd.Base.Is3D);
        Assert.Equal(0xA77u, rnd.Base.AttenuationId);
        Assert.Equal(0x5100u, rnd.Base.ParentId);

        var s1 = Assert.IsType<HircSound>(bank.Find(0x2001));
        Assert.True(s1.IsVorbis);
        Assert.Equal((1001u, HircStreamType.Bank, 0u, 4u), (s1.SourceId, s1.StreamType, s1.MediaOffset, s1.MediaSize));
        var s2 = Assert.IsType<HircSound>(bank.Find(0x2002));
        Assert.Equal(HircStreamType.Streamed, s2.StreamType);
        Assert.Equal(new uint[] { 0xF1, 0xF2 }, s2.Base.FxIds);
        Assert.Equal((2, 1), (s2.Base.StateGroupCount, s2.Base.RtpcCount));
        Assert.Equal(new RangedValue(0, -6, 0), Assert.IsType<HircSound>(bank.Find(0x5300)).Base.Volume);

        var layer = Assert.IsType<HircLayer>(bank.Find(0x6100));
        Assert.Equal(new uint[] { 0x6201, 0x6202 }, Assert.Single(layer.Layers).AssociatedChildIds);
        var raw = Assert.IsType<HircRaw>(bank.Find(0xA77));
        Assert.Null(raw.ParseError);                                          // attenuation is kept raw on purpose
    }

    [Fact]
    public void Bank_keeps_objects_that_do_not_parse_exactly_as_raw()
    {
        var good = Sound(0x10, 1, new Node(0));
        var tooLong = new Obj(HircType.Sound, 0x11, [.. good.Payload, 0xEE]);
        var tooShort = new Obj(HircType.Event, 0x12, [5, 0, 0, 0, 1, 0, 0, 0]);  // claims 5 actions, holds 1
        var bank = SoundBank.Read(Bank(1, null, [], [good, tooLong, tooShort]));
        Assert.IsType<HircSound>(bank.Find(0x10));
        Assert.NotNull(Assert.IsType<HircRaw>(bank.Find(0x11)).ParseError);
        Assert.NotNull(Assert.IsType<HircRaw>(bank.Find(0x12)).ParseError);
        Assert.Equal(2, bank.ParseFailures);
        Assert.Null(bank.Name);
    }

    [Fact]
    public void Bank_refuses_other_versions_and_bad_chunks()
    {
        Assert.Throws<NotSupportedException>(() => SoundBank.Read(Bank(1, null, [], [], version: 88)));
        Assert.Throws<InvalidDataException>(() => SoundBank.Read("DATA\x04\0\0\0abcd"u8.ToArray()));
        var b = Bank(1, null, [new Item(5, [1, 2])], []);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8 + 20 + 8 + 4), 999);  // DIDX entry offset past DATA
        Assert.Throws<InvalidDataException>(() => SoundBank.Read(b));
    }

    // ------------------------------------------------------------------ library and event chains

    static WwiseLibrary TestLibrary()
    {
        var events = Bank(0xE0, "Events", [], [Event(0xE1, 0xA1, 0xA2, 0xA3), Play(0xA1, 0x5100, 0, 0xE0), Play(0xA2, 0x6100, 200, 0xE0), Stop(0xA3, 0x5100)]);
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(0xE0, events)], []))), "events");
        // In TestBank, wem 1002 is streamed; the others are embedded.
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(BankId, TestBank())], [new Item(1002, [42, 42])]))), "weapon_synthetic");
        return lib;
    }

    [Fact]
    public void Library_resolves_an_event_through_switch_random_and_layer_to_wems()
    {
        using var lib = TestLibrary();
        Assert.Equal(2, lib.Banks.Count);
        var chain = lib.ResolveEvent(0xE1);
        Assert.Equal("Events", chain.BankName);
        Assert.Equal(3, chain.Actions.Count);
        Assert.Null(chain.Actions[2].Target);                                  // Stop is not followed
        var sw = chain.Actions[0].Target!;
        Assert.IsType<HircSwitch>(sw.Object);
        Assert.Equal("Weapon_Synthetic", sw.BankName);
        Assert.Equal(2, sw.Branches.Count);
        Assert.Equal((Local, false), (sw.Branches[0].SwitchValue!.Value, sw.Branches[0].IsDefault));
        Assert.True(sw.Branches[1].IsDefault);
        Assert.Equal(new uint[] { 1001, 1002, 1003, 1004 }, chain.WemIds());   // deduplicated, first-seen order

        Assert.False(lib.FindMedia(1001)!.Streamed);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, lib.ReadMedia(1001));
        var streamed = lib.FindMedia(1002)!;
        Assert.True(streamed.Streamed);
        Assert.Equal("weapon_synthetic", streamed.PackageName);
        Assert.Equal(new byte[] { 42, 42 }, lib.ReadMedia(1002));
        Assert.Null(lib.FindMedia(9999));
        Assert.Null(lib.ReadMedia(9999));

        var missing = lib.ResolveEvent("no_such_event");
        Assert.Null(missing.Event);
        Assert.Empty(missing.WemIds());
    }

    [Fact]
    public void Library_prefers_the_full_stream_over_a_prefetch_copy()
    {
        // A RIFF header that promises 100 bytes, embedded as only its first bytes, and the full file streamed.
        var full = new byte[108];
        "RIFF"u8.CopyTo(full);
        BinaryPrimitives.WriteUInt32LittleEndian(full.AsSpan(4), 100);
        var embedded = Bank(BankId, null, [new Item(1004, full[..12])], []);
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(BankId, embedded)], [new Item(1004, full)]))), "p");
        Assert.True(WwiseLibrary.IsTruncatedRiff(full[..12]));
        Assert.False(WwiseLibrary.IsTruncatedRiff(full));
        Assert.True(lib.FindMedia(1004)!.Streamed);
        Assert.Equal(108, lib.ReadMedia(1004)!.Length);
        lib.Dispose();
    }

    [Fact]
    public void Library_stops_on_cycles()
    {
        var a = Random(0x1, new Node(0), [0x2]);
        var b = Random(0x2, new Node(0x1), [0x1]);
        var lib = new WwiseLibrary();
        lib.AddPackage(AkpkPackage.Read(new MemoryStream(Package([new Item(1, Bank(1, null, [], [a, b]))], []))), "p");
        var root = lib.ResolveNode(0x1);
        var child = Assert.Single(Assert.Single(root.Branches).Nodes);
        Assert.True(Assert.Single(Assert.Single(child.Branches).Nodes).Truncated);
        lib.Dispose();
    }

    // ------------------------------------------------------------------ Wwise Vorbis

    static readonly bool[] Blocks = [false, false, true, true, false, false, true, false];

    [Fact]
    public void Wem_header_fields_are_read()
    {
        var info = WwiseVorbis.ReadInfo(Wem(2, 48000, Blocks, 3000, inlineCodebook: true));
        Assert.True(info.LittleEndian);
        Assert.Equal((2, 48000, 3000u), (info.Channels, info.SampleRate, info.SampleCount));
        Assert.Equal((8, 11), (info.Blocksize0Exponent, info.Blocksize1Exponent));
        Assert.True(info.NoGranule);
        Assert.True(info.ModPackets);
        Assert.False(info.HeaderTriad);
        Assert.Equal(-1, info.VorbSize);
        Assert.Equal(3u, info.ChannelLayout);
        Assert.Equal(3000 / 48000.0, info.Duration, 6);
    }

    [Fact]
    public void Setup_rebuild_expands_inline_and_external_codebooks_identically()
    {
        var inline = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 3000, inlineCodebook: true), options: new WwiseVorbisOptions(InlineCodebooks: true));
        var library = new VorbisCodebookLibrary(CodebookLibrary());
        Assert.Equal(1, library.Count);
        var external = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 3000, inlineCodebook: false), library);
        Assert.Equal(inline.Setup, external.Setup);
        Assert.Equal(new byte[] { 5, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s', 0, 0x42, 0x43, 0x56 }, external.Setup[..11]);
        Assert.Equal(1, external.Identification[0]);
        Assert.Equal(44100, BinaryPrimitives.ReadInt32LittleEndian(external.Identification.AsSpan(12)));
        Assert.Equal(3, external.Comment[0]);
        Assert.Contains("ww2ogg", System.Text.Encoding.ASCII.GetString(external.Comment));

        var empty = new VorbisCodebookLibrary([0, 0, 0, 0]);                  // offset table only: no codebooks
        Assert.Equal(0, empty.Count);
        var ex = Assert.Throws<InvalidDataException>(() => WwiseVorbis.Convert(Wem(1, 44100, Blocks, 3000, inlineCodebook: false), empty));
        Assert.Contains("invalid codebook id 0", ex.Message);
        Assert.Throws<InvalidDataException>(() => new VorbisCodebookLibrary([0xFF, 0xFF, 0xFF, 0xFF, 2, 0, 0, 0]));
    }

    [Fact]
    public void Modified_packets_get_type_and_window_bits_back_and_granules_follow_block_sizes()
    {
        // S S L L S S L S with blocksizes 256 / 2048: increments 128, 576, 1024, 576, 128, 576, 576.
        var s = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 3500, inlineCodebook: false), new VorbisCodebookLibrary(CodebookLibrary()));
        Assert.Equal(Blocks.Length, s.AudioPackets.Count);
        Assert.Equal(0x00, s.AudioPackets[0][0]);                             // type 0, mode 0
        Assert.Equal(0b1010, s.AudioPackets[2][0] & 0xF);                       // mode 1, prev short, next long
        Assert.Equal(0b0110, s.AudioPackets[3][0] & 0xF);                       // mode 1, prev long, next short
        Assert.Equal(0b0010, s.AudioPackets[6][0] & 0xF);                       // mode 1, prev short, next short
        Assert.Equal(new long[] { 0, 128, 704, 1728, 2304, 2432, 3008, 3500 }, s.Granules);   // last clamped from 3584
        Assert.Equal(3500, s.TotalSamples);

        var unclamped = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 9999, inlineCodebook: true), options: new WwiseVorbisOptions(InlineCodebooks: true));
        Assert.Equal(3584, unclamped.TotalSamples);
        var farOff = WwiseVorbis.Convert(Wem(1, 44100, Blocks, 100, inlineCodebook: true), options: new WwiseVorbisOptions(InlineCodebooks: true));
        Assert.Equal(3584, farOff.TotalSamples);                               // a cut beyond the last packet is not applied
    }

    [Fact]
    public void Ogg_pages_are_well_formed()
    {
        var blocks = Enumerable.Range(0, 3000).Select(i => i % 7 == 3).ToArray();   // enough packets for several pages
        var s = WwiseVorbis.Convert(Wem(2, 44100, blocks, 100000, inlineCodebook: true), options: new WwiseVorbisOptions(InlineCodebooks: true));
        using var ms = new MemoryStream();
        OggWriter.Write(s, ms);
        var ogg = ms.ToArray();
        var pages = new List<(int Flags, long Granule, uint Seq, int Packets)>();
        int pos = 0;
        while (pos < ogg.Length)
        {
            Assert.True(ogg.AsSpan(pos).StartsWith("OggS"u8));
            int segs = ogg[pos + 26], body = 0, ends = 0;
            for (int i = 0; i < segs; i++) { body += ogg[pos + 27 + i]; if (ogg[pos + 27 + i] < 255) ends++; }
            int len = 27 + segs + body;
            var page = ogg.AsSpan(pos, len).ToArray();
            uint crc = BinaryPrimitives.ReadUInt32LittleEndian(page.AsSpan(22));
            page.AsSpan(22, 4).Clear();
            Assert.Equal(crc, OggWriter.Crc(page));
            pages.Add((ogg[pos + 5], BinaryPrimitives.ReadInt64LittleEndian(ogg.AsSpan(pos + 6)), BinaryPrimitives.ReadUInt32LittleEndian(ogg.AsSpan(pos + 18)), ends));
            pos += len;
        }
        Assert.Equal(ogg.Length, pos);
        Assert.True(pages.Count > 3);
        Assert.Equal((2, 0L, 1), (pages[0].Flags, pages[0].Granule, pages[0].Packets)); // BOS, identification only
        Assert.Equal(2, pages[1].Packets);                                      // comment + setup
        Assert.Equal(4, pages[^1].Flags);                                       // EOS
        Assert.Equal(s.TotalSamples, pages[^1].Granule);
        Assert.Equal(Enumerable.Range(0, pages.Count).Select(i => (uint)i), pages.Select(p => p.Seq));
        // Poly 0x04C11DB7, init 0, no reflection, no final xor: the CRC-32/POSIX check value 0x765E7680 without its xor.
        Assert.Equal(0x765E7680u ^ 0xFFFFFFFFu, OggWriter.Crc("123456789"u8));
    }

    [Theory]
    [InlineData(1, 44100, true)]
    [InlineData(2, 48000, false)]
    public void Converted_stream_decodes_with_a_standard_vorbis_decoder(int channels, int rate, bool inlineCodebook)
    {
        var blocks = Enumerable.Range(0, 200).Select(i => i % 5 == 2).ToArray();
        var wem = Wem(channels, rate, blocks, 30000, inlineCodebook);
        var codebooks = inlineCodebook ? null : new VorbisCodebookLibrary(CodebookLibrary());
        var options = new WwiseVorbisOptions(InlineCodebooks: inlineCodebook);
        var ogg = WwiseVorbis.ToOgg(wem, codebooks, options);
        using var reader = new NVorbis.VorbisReader(new MemoryStream(ogg), true);
        Assert.Equal(channels, reader.Channels);
        Assert.Equal(rate, reader.SampleRate);
        var buffer = new float[4096];
        long samples = 0;
        int n;
        float peak = 0;
        while ((n = reader.ReadSamples(buffer, 0, buffer.Length)) > 0)
        {
            samples += n;
            for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Abs(buffer[i]));
        }
        long frames = samples / channels;
        long expected = WwiseVorbis.Convert(wem, codebooks, options).TotalSamples;
        // NVorbis 0.10.5 may keep up to (2048 - 256) / 4 extra samples at the end (see smtool's DecodeCheck).
        Assert.InRange(frames, expected, expected + 448);
        Assert.Equal(0f, peak);                                                // every floor is unused: silence
    }

    [Fact]
    public void Synthetic_codebook_library_is_read()
    {
        var lib = new VorbisCodebookLibrary(CodebookLibrary());
        var w = new BitWriter();
        Assert.True(lib.TryRebuild(0, w));
        Assert.Equal(new byte[] { 0x42, 0x43, 0x56 }, w.ToArray()[..3]);  // "BCV"
        Assert.False(lib.TryRebuild(1, new BitWriter()));
    }

    /// <summary>Runs only where ww2ogg's library has been downloaded into the private cache (it is not in the repo).</summary>
    [CodebookCacheFact]
    public void Cached_ww2ogg_codebook_library_is_complete()
    {
        var lib = new VorbisCodebookLibrary(File.ReadAllBytes(SmContent.AudioCache.CodebookPath()));
        Assert.True(lib.Count > 595, $"only {lib.Count} codebooks");         // the game's setups use IDs up to 595
        var w = new BitWriter();
        Assert.True(lib.TryRebuild(0, w));
        Assert.Equal(new byte[] { 0x42, 0x43, 0x56 }, w.ToArray()[..3]);
        Assert.False(lib.TryRebuild(lib.Count, new BitWriter()));
        Assert.False(lib.TryRebuild(-1, new BitWriter()));
    }

    [Fact]
    public void Bad_wems_are_rejected()
    {
        var good = Wem(1, 44100, Blocks, 3000, inlineCodebook: true);
        Assert.True(WwiseVorbis.IsWwiseVorbis(good));
        Assert.False(WwiseVorbis.IsWwiseVorbis("RIFX"u8));
        var pcm = (byte[])good.Clone();
        pcm[20] = 1; pcm[21] = 0;                                              // codec 1 (PCM)
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(pcm));
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(good[..^4]));   // RIFF truncated
        var noChunks = good[..12];
        BinaryPrimitives.WriteUInt32LittleEndian(noChunks.AsSpan(4), 4);
        Assert.Throws<InvalidDataException>(() => WwiseVorbis.ReadInfo(noChunks));  // no fmt or data chunk
    }

    [Fact]
    public void Bits_round_trip_least_significant_first()
    {
        var w = new BitWriter();
        w.Write(1, 1); w.Write(0b101, 3); w.Write(0xABCD, 16); w.Write(0xFFFFFFFF, 32);
        Assert.Equal(52, w.BitsWritten);
        var bytes = w.ToArray();
        Assert.Equal(0b1011, bytes[0] & 0xF);
        var r = new BitReader(bytes);
        Assert.Equal((1u, 5u, 0xABCDu, 0xFFFFFFFFu), (r.Read(1), r.Read(3), r.Read(16), r.Read(32)));
        Assert.Throws<InvalidDataException>(() => r.Read(8));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Write(4, 2));
    }
}
