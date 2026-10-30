using System.Security.Cryptography;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Custom classes, wire side (T3a, protocol 22; docs/customizer-architecture.md section 4): the changed Hello and the new Setup,
/// SetupAck, AppearanceQuery, Appearance, BlobRequest and BlobChunk messages round-trip, malformed ones are refused (null), every
/// message fits a 1200-byte packet (the loopback network throws on more), the Challenge never outgrows the Hello, snapshots are
/// unchanged, the appearance code and emblem hash, and the blob transfer under loss and against a tampered chunk.
/// Synthetic data only.
/// </summary>
public class SetupProtocolTests(ITestOutputHelper output)
{
    static readonly EmblemHash SomeHash = new(0x0123456789ABCDEFUL, 0xFEDCBA9876543210UL);

    /// <summary>A code with every field set (a predefined set, pieces, palette, patterns, the emblem on the right pad, a lens).</summary>
    static AppearanceCode Busy => AppearanceCode.Default.WithPredefinedSet(3).WithPiece(ArmourSlot.Head, 7).WithPiece(ArmourSlot.Backpack, 0)
        .WithPalette(ArmourSlot.Torso, 1, 2, 59).WithPattern(ArmourSlot.Torso, true).WithPattern(ArmourSlot.RightLeg, true)
        .WithDecals(12, AppearanceCode.DecalEmblem, 40).WithEmblem(SomeHash).WithLens((255, 16, 0));

    static T RoundTrip<T>(T message, int size) where T : Message
    {
        var bytes = Protocol.Write(message);
        Assert.Equal(size, bytes.Length);
        var back = Assert.IsType<T>(Protocol.Read(bytes));
        for (int n = 0; n < bytes.Length; n++) Assert.Null(Protocol.Read(bytes.AsSpan(0, n)));   // every truncation refused
        Assert.Null(Protocol.Read([.. bytes, 0]));                                                // and a trailing byte
        return back;
    }

    static byte[] Patch(byte[] bytes, int at, byte value) { var copy = (byte[])bytes.Clone(); copy[at] = value; return copy; }

    // ---------------------------------------------------------------- the messages

    [Fact]
    public void Protocol_22_bumps_the_version_and_keeps_snapshots_as_they_were()
    {
        Assert.Equal(46, Protocol.Version);
        Assert.Equal(1194, Protocol.MaxSnapshotBytes);   // protocol 21's worst case: no snapshot change
        Assert.Equal((47, 166), (Protocol.SlimRecordBytes, Protocol.FullRecordBytes));   // protocol 44: + 14 (the third weapon), 45: + 8 (the holstered reloads)   // protocol 23: + the perk word
        Assert.Equal((178, 140, 14, 68, 22, 21), (Protocol.HelloBytes, Protocol.SetupBytes, Protocol.SetupAckBytes, Protocol.AppearanceBytes,   // protocol 28: + the Ork look
            Protocol.BlobRequestBytes, Protocol.BlobChunkHeaderBytes));
    }

    [Fact]
    public void The_hello_carries_perks_and_both_appearances_and_stays_larger_than_any_challenge()
    {
        var hello = new Hello(Protocol.Version, 1, 2, 3, 4, 5, 6, 1, 2, 3, 4, 5, 2, RequestedPerk1: 6, RequestedPerk2: 7,
            SpaceMarineAppearance: Busy, ChaosAppearance: AppearanceCode.Default.WithPiece(ArmourSlot.LeftArm, 4));
        var back = RoundTrip(hello, Protocol.HelloBytes);
        Assert.Equal(hello, back);
        Assert.Equal(new LoadoutChoice(1, 2, 3, 4, 5, 6, 7), back.Loadout);
        Assert.Equal(Busy, back.AppearanceFor(Team.SpaceMarines));
        Assert.Equal(4, back.AppearanceFor(Team.Chaos).Piece(ArmourSlot.LeftArm));
        // Defaults: 255 bytes read as -1, the default codes.
        var plain = RoundTrip(new Hello(Protocol.Version, 1, 2, 3), Protocol.HelloBytes);
        Assert.Equal(LoadoutChoice.Default, plain.Loadout);
        Assert.True(plain.SpaceMarineAppearance.IsDefault && plain.ChaosAppearance.IsDefault);
        Assert.Equal(new Hello(Protocol.Version, 1, 2, 3), plain);
        // The largest challenge (longest map name) is not larger than the smallest hello: no amplification.
        var challenge = Protocol.Write(new Challenge(uint.MaxValue, ulong.MaxValue, uint.MaxValue, new string('m', Protocol.MaxMapName)));
        Assert.True(challenge.Length <= Protocol.HelloBytes, $"challenge {challenge.Length} > hello {Protocol.HelloBytes}");
    }

    [Fact]
    public void Setup_and_its_ack_round_trip_and_refuse_unknown_flags()
    {
        var setup = new Setup(0xDEADBEEF, 65535, new LoadoutChoice(2, -1, 1, 0, 1, 3, -1), Busy, AppearanceCode.Default);
        Assert.Equal(setup, RoundTrip(setup, Protocol.SetupBytes));
        // Out-of-range choices travel as the default.
        var wide = setup with { Loadout = new LoadoutChoice(255, 300, -7, 0, 0, 0, 254) };
        Assert.Equal(new LoadoutChoice(-1, -1, -1, 0, 0, 0, 254), Assert.IsType<Setup>(Protocol.Read(Protocol.Write(wide))).Loadout);
        Assert.Equal(new LoadoutChoice(-1, -1, -1, 0, 0, 0, 254), Protocol.Quantize(wide.Loadout));

        var ack = new SetupAck(9, new LoadoutChoice(1, 0, -1, -1, 0, 1, 0), SetupFlags.Pending | SetupFlags.EmblemUploading | SetupFlags.Defaulted, 513);
        Assert.Equal(ack, RoundTrip(ack, Protocol.SetupAckBytes));
        var bytes = Protocol.Write(ack);
        Assert.Null(Protocol.Read(Patch(bytes, 1 + 2 + 8, 16)));   // unknown flag
        Assert.Equal(SetupFlags.All, Assert.IsType<SetupAck>(Protocol.Read(Patch(bytes, 11, (byte)SetupFlags.All))).Flags);
        Assert.Equal(SetupFlags.Pending, Assert.IsType<SetupAck>(Protocol.Read(Protocol.Write(ack with { Flags = (SetupFlags)0xF1 }))).Flags);   // written masked
    }

    [Fact]
    public void Appearance_queries_and_answers_round_trip_and_refuse_misused_ids_and_races()
    {
        var empty = new AppearanceQuery(5, []);
        Assert.Empty(RoundTrip(empty, 6).PlayerIds);
        int[] ids = [.. Enumerable.Range(1, Protocol.MaxAppearanceQuery).Select(i => i * 4000)];
        var full = RoundTrip(new AppearanceQuery(7, ids), 6 + 2 * Protocol.MaxAppearanceQuery);
        Assert.Equal(7u, full.Session);
        Assert.Equal(ids, full.PlayerIds);
        var bytes = Protocol.Write(new AppearanceQuery(7, [3, 4]));
        Assert.Null(Protocol.Read(Patch(bytes, 5, 17)));   // a count above 16
        Assert.Null(Protocol.Read(Patch(bytes, 5, 3)));    // a count beyond the ids
        Assert.Null(Protocol.Read(Patch(Patch(bytes, 6, 0), 7, 0)));   // id 0
        Assert.Null(Protocol.Read(Patch(bytes, 8, 3)));    // the same id twice
        Assert.Throws<ArgumentException>(() => Protocol.Write(new AppearanceQuery(7, [.. ids, 1])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new AppearanceQuery(7, [0])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new AppearanceQuery(7, [70000])));
        Assert.Throws<ArgumentException>(() => Protocol.Write(new AppearanceQuery(7, [2, 2])));

        var answer = new Appearance(65535, 40000, Race.Chaos, Busy);
        Assert.Equal(answer, RoundTrip(answer, Protocol.AppearanceBytes));
        var a = Protocol.Write(answer with { Race = Race.SpaceMarines });
        Assert.Equal(Race.SpaceMarines, Assert.IsType<Appearance>(Protocol.Read(a)).Race);
        Assert.Null(Protocol.Read(Patch(a, 5, 0)));                 // race none
        Assert.Equal(Race.Orks, Assert.IsType<Appearance>(Protocol.Read(Patch(a, 5, 3))).Race);   // race 3: the Orks (protocol 28)
        Assert.Null(Protocol.Read(Patch(a, 5, 4)));                 // race 4
        Assert.Null(Protocol.Read(Patch(a, 5, 255)));
        Assert.Null(Protocol.Read(Patch(Patch(a, 1, 0), 2, 0)));    // player 0
        Assert.Throws<ArgumentException>(() => Protocol.Write(answer with { Race = Race.None }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(answer with { Race = (Race)4 }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(answer with { PlayerId = 0 }));
        // The code itself travels as is (the receiver sanitizes), even when inconsistent.
        var odd = AppearanceCode.FromBytes([.. Enumerable.Repeat((byte)0xEE, AppearanceCode.Size)]);
        Assert.Equal(odd, Assert.IsType<Appearance>(Protocol.Read(Protocol.Write(answer with { Code = odd }))).Code);
    }

    [Fact]
    public void Blob_requests_and_chunks_round_trip_and_refuse_bad_indices_counts_and_lengths()
    {
        var request = new BlobRequest(11, SomeHash, 15);
        Assert.Equal(request, RoundTrip(request, Protocol.BlobRequestBytes));
        var r = Protocol.Write(request);
        Assert.Null(Protocol.Read(Patch(r, 21, 16)));                                   // chunk 16
        Assert.Null(Protocol.Read(Protocol.Write(request).Take(5).Concat(new byte[16]).Append((byte)0).ToArray()));   // no hash
        Assert.Throws<ArgumentException>(() => Protocol.Write(request with { Chunk = 16 }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(request with { Hash = EmblemHash.None }));

        var data = RandomNumberGenerator.GetBytes(BlobTransfer.ChunkBytes);
        var chunk = new BlobChunk(SomeHash, 15, 16, data);
        var back = RoundTrip(chunk, Protocol.BlobChunkHeaderBytes + BlobTransfer.ChunkBytes);
        Assert.Equal((chunk.Hash, chunk.Index, chunk.Count), (back.Hash, back.Index, back.Count));
        Assert.Equal(data, back.Data);
        Assert.Equal(1045, Protocol.Write(chunk).Length);
        var small = RoundTrip(new BlobChunk(SomeHash, 0, 1, [42]), Protocol.BlobChunkHeaderBytes + 1);
        Assert.Equal(new byte[] { 42 }, small.Data);
        var c = Protocol.Write(chunk);
        Assert.Null(Protocol.Read(Patch(c, 17, 16)));    // index = count
        Assert.Null(Protocol.Read(Patch(c, 18, 0)));     // count 0
        Assert.Null(Protocol.Read(Patch(c, 18, 17)));    // count 17
        Assert.Null(Protocol.Read(Patch(Patch(c, 19, 0), 20, 0)));   // length 0
        Assert.Null(Protocol.Read(Patch(c, 19, 1)));     // length 1025 (and beyond the data)
        Assert.Null(Protocol.Read(Patch(Patch(c, 19, 0xFF), 20, 0x03)));   // length 1023: a byte left over
        Assert.Throws<ArgumentException>(() => Protocol.Write(chunk with { Data = new byte[BlobTransfer.ChunkBytes + 1] }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(chunk with { Data = [] }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(chunk with { Index = 16 }));
        Assert.Throws<ArgumentException>(() => Protocol.Write(chunk with { Count = 17, Index = 0 }));
    }

    [Fact]
    public void Scoreboard_rows_carry_the_appearance_version()
    {
        var board = new Scoreboard(9, true, 41, 600f, false, [new ScoreEntry(1, Team.SpaceMarines, 0, 1, 2, 65535), new ScoreEntry(2, Team.Chaos, 2, 0, 0)]);
        var bytes = Protocol.Write(board);
        Assert.Equal(13 + 2 * Protocol.ScoreRowBytes, bytes.Length);
        var back = Assert.IsType<Scoreboard>(Protocol.Read(bytes));
        Assert.Equal(board.Players, back.Players);
        Assert.Equal((ushort)65535, back.Players[0].AppearanceVersion);
        // A full board of 16 rows stays small.
        var full = board with { Players = [.. Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, Team.Chaos, 1, i, i, (ushort)i))] };
        Assert.Equal(13 + Protocol.MaxPlayers * Protocol.ScoreRowBytes, Protocol.Write(full).Length);   // protocol 40: 13-byte rows
    }

    [Fact]
    public void Every_new_message_at_its_largest_fits_a_packet_on_the_loopback_network()
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, LinkConditions.Perfect);
        var client = net.Connect();
        Message[] largest =
        [
            new Hello(Protocol.Version, 1, 2, 3, SpaceMarineAppearance: Busy, ChaosAppearance: Busy),
            new Setup(1, 2, LoadoutChoice.Default, Busy, Busy),
            new SetupAck(1, LoadoutChoice.Default, SetupFlags.All, 1),
            new AppearanceQuery(1, [.. Enumerable.Range(1, Protocol.MaxAppearanceQuery)]),
            new Appearance(1, 1, Race.Chaos, Busy),
            new BlobRequest(1, SomeHash, 0),
            new BlobChunk(SomeHash, 0, 16, new byte[BlobTransfer.ChunkBytes]),
            new Scoreboard(1, true, 1, 1f, true, [.. Enumerable.Range(1, Protocol.MaxPlayers).Select(i => new ScoreEntry(i, Team.Chaos, 1, 1, 1, 1))]),
        ];
        foreach (var m in largest)
        {
            var bytes = Protocol.Write(m);
            Assert.True(bytes.Length <= Protocol.SnapshotBudget, $"{m.GetType().Name}: {bytes.Length} bytes");
            client.Send(bytes);                       // to the server
            net.Server.Send(0, bytes);                // and back
        }
        Assert.Throws<InvalidOperationException>(() => client.Send(new byte[Protocol.SnapshotBudget + 1]));   // the guard is live
        int received = 0;
        while (net.Server.TryReceive(out _, out var p)) { Assert.NotNull(Protocol.Read(p)); received++; }
        Assert.Equal(largest.Length, received);
    }

    // ---------------------------------------------------------------- appearance code and emblem hash

    [Fact]
    public void The_appearance_code_has_the_documented_layout_and_defaults()
    {
        var d = AppearanceCode.Default.ToArray();
        Assert.Equal(62, d.Length);
        Assert.Equal(AppearanceCode.Default, default(AppearanceCode));
        Assert.True(default(AppearanceCode).IsDefault && default(AppearanceCode).IsWellFormed());
        Assert.Equal(new byte[] { 0, 255 }, d[..2]);
        Assert.All(d[2..38], b => Assert.Equal(255, b));                 // pieces and palette: the defaults
        Assert.Equal(new byte[] { 0, 0 }, d[38..40]);                             // no pattern bits
        Assert.Equal(new byte[] { 253, 253, 255 }, d[40..43]);             // the scheme's decals, the scheme's fallback
        Assert.All(d[43..62], b => Assert.Equal(0, b));                   // no lens, no emblem
        var b = Busy.ToArray();
        Assert.Equal(3, b[0]);                                            // lens and predefined set
        Assert.Equal(new byte[] { 3, 7, 0 }, new[] { b[1], b[2], b[10] });                     // set, head piece, backpack piece
        Assert.Equal(new byte[] { 1, 2, 59 }, b[14..17]);      // torso palette
        Assert.Equal(1 << 1 | 1 << 7, b[38] | b[39] << 8);                // torso and right-leg patterns
        Assert.Equal(new byte[] { 12, 254, 40 }, b[40..43]);
        Assert.Equal(new byte[] { 255, 16, 0 }, b[43..46]);
        Assert.Equal(SomeHash, EmblemHash.Read(b.AsSpan(46)));
        Assert.True(Busy.IsWellFormed());
        Assert.Equal((true, true, true), (Busy.UsesEmblem, Busy.PatternOn(ArmourSlot.RightLeg), Busy.HasLens));
        Assert.Equal(new byte[] { 1, 2, 59 }, new[] { Busy.Palette(ArmourSlot.Torso, 0), Busy.Palette(ArmourSlot.Torso, 1), Busy.Palette(ArmourSlot.Torso, 2) });
        Assert.Equal(Busy, AppearanceCode.FromBytes(b));
        Assert.Equal(Busy.GetHashCode(), AppearanceCode.FromBytes(b).GetHashCode());
        Assert.NotEqual(Busy, Busy.WithPiece(ArmourSlot.Head, 8));
        Assert.Throws<ArgumentException>(() => AppearanceCode.FromBytes(new byte[61]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Busy.Palette(ArmourSlot.Head, 3));
        // Builders undo: no set and no lens clear their flags.
        var plain = Busy.WithPredefinedSet(255).WithLens(null);
        Assert.Equal((AppearanceFlags.None, (byte)0), (plain.Flags, plain.Lens.R));
    }

    [Fact]
    public void Inconsistent_and_out_of_range_appearance_fields_default_one_by_one()
    {
        byte[] Bytes(AppearanceCode c) => c.ToArray();
        AppearanceCode With(Action<byte[]> edit) { var b = Bytes(AppearanceCode.Default); edit(b); return AppearanceCode.FromBytes(b); }
        // Each inconsistency alone is repaired and nothing else changes.
        (AppearanceCode Bad, AppearanceCode Fixed)[] cases =
        [
            (With(b => b[0] = 4), AppearanceCode.Default),                                      // unknown flag
            (With(b => b[0] = 2), AppearanceCode.Default),                                      // set flag without a set
            (With(b => b[1] = 5), AppearanceCode.Default),                                      // a set without its flag
            (With(b => b[44] = 9), AppearanceCode.Default),                                     // lens colour without the flag
            (With(b => b[39] = 0x80), AppearanceCode.Default),                                  // pattern bit 15
            (With(b => b[42] = 253), AppearanceCode.Default),                                   // fallback 253
            (With(b => b[40] = 254), AppearanceCode.Default),                                   // emblem without a hash
            (AppearanceCode.Default.WithEmblem(SomeHash), AppearanceCode.Default),              // a hash without an emblem pad
        ];
        foreach (var (bad, fix) in cases)
        {
            Assert.False(bad.IsWellFormed(), bad.ToString());
            Assert.Equal(fix, bad.Sanitized());
        }
        var mixed = Busy.WithPattern(ArmourSlot.Head, true);
        Assert.Equal(mixed, mixed.Sanitized());
        // Catalogue ranges: indices beyond the limits default, the rest stay.
        var limits = new AppearanceLimits(PredefinedSets: 3, PiecesPerSlot: [8, 1, 1, 1, 5, 1, 1, 1], Palette: 59, Badges: 13);   // no backpack count
        var within = Busy.Sanitized(limits);
        Assert.False(Busy.IsWithin(limits));
        Assert.Equal((AppearanceCode.DefaultIndex, AppearanceFlags.Lens), (within.PredefinedSet, within.Flags));   // set 3 of 3
        Assert.Equal(new byte[] { 7, 255 }, new[] { within.Piece(ArmourSlot.Head), within.Piece(ArmourSlot.Backpack) });                // backpack: no pieces
        Assert.Equal(new byte[] { 1, 2, 255 }, new[] { within.Palette(ArmourSlot.Torso, 0), within.Palette(ArmourSlot.Torso, 1), within.Palette(ArmourSlot.Torso, 2) });
        Assert.Equal(new byte[] { 12, AppearanceCode.DecalEmblem, AppearanceCode.FallbackSchemeDefault }, new[] { within.LeftDecal, within.RightDecal, within.FallbackBadge });
        Assert.True(within.IsWithin(limits));
        Assert.Equal(SomeHash, within.Emblem);
        Assert.True(AppearanceCode.Default.IsWithin(new AppearanceLimits(0, [], 0, 0)));
    }

    [Fact]
    public void The_emblem_hash_is_the_first_half_of_the_sha256_and_has_a_hex_name()
    {
        var blob = RandomNumberGenerator.GetBytes(EmblemHash.BlobBytes);
        Assert.Equal(16384, EmblemHash.BlobBytes);
        var hash = EmblemHash.Of(blob);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(blob)[..16]), hash.ToHex());
        Assert.True(EmblemHash.TryParseHex(hash.ToHex().ToUpperInvariant(), out var parsed));
        Assert.Equal(hash, parsed);
        Assert.False(EmblemHash.TryParseHex("xyz", out _));
        Assert.False(EmblemHash.TryParseHex(new string('g', 32), out _));
        Assert.True(EmblemHash.None.IsNone);
        Assert.True(BlobTransfer.Verify(hash, blob));
        Assert.False(BlobTransfer.Verify(hash, blob.AsSpan(1)));
        blob[100] ^= 1;
        Assert.False(BlobTransfer.Verify(hash, blob));
    }

    // ---------------------------------------------------------------- blob transfer

    /// <summary>
    /// Pulls a blob over a loopback link (both directions lossy): the client pulls, the server answers each request with the
    /// chunk (<paramref name="tamper"/>: the chunk with this index comes with one byte changed). Ticks of 10 ms; gives up after
    /// <paramref name="seconds"/>. Returns the pull, the time taken and the most requests ever outstanding.
    /// </summary>
    static (BlobPull Pull, double Ms, int MaxOutstanding) Transfer(byte[] blob, double loss, int seed, int tamper = -1, bool retry = false, double seconds = 10,
        BlobPull? pull = null)
    {
        var clock = new ManualClock();
        var net = new LoopbackNetwork(clock, new LinkConditions(TimeSpan.FromMilliseconds(40), TimeSpan.FromMilliseconds(10), loss), seed);
        var client = net.Connect();
        var hash = EmblemHash.Of(blob);
        pull ??= new BlobPull(hash);
        var asked = new HashSet<int>();
        int maxOutstanding = 0;
        while (clock.Now.TotalSeconds < seconds && pull.State != BlobPullState.Complete)
        {
            long now = (long)clock.Now.TotalMilliseconds;
            if (retry && pull.State is BlobPullState.TimedOut or BlobPullState.Corrupt) { pull.Retry(); tamper = -1; }
            foreach (var r in pull.Requests(now, 77)) { client.Send(Protocol.Write(r)); asked.Add(r.Chunk); }
            maxOutstanding = Math.Max(maxOutstanding, asked.Count(i => (pull.Received & (1 << i)) == 0));
            while (net.Server.TryReceive(out int connection, out var packet))
                if (Protocol.Read(packet) is BlobRequest q && q.Hash == hash && q.Session == 77)
                {
                    var chunk = BlobTransfer.Chunk(hash, blob, q.Chunk);
                    if (q.Chunk == tamper) chunk.Data[0] ^= 0x5A;
                    net.Server.Send(connection, Protocol.Write(chunk));
                }
            while (client.TryReceive(out var packet)) pull.Accept(Assert.IsType<BlobChunk>(Protocol.Read(packet)));
            if (!retry && pull.State is BlobPullState.TimedOut or BlobPullState.Corrupt) break;
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        return (pull, clock.Now.TotalMilliseconds, maxOutstanding);
    }

    [Fact]
    public void A_blob_arrives_whole_and_verified_on_a_clean_link_within_the_window()
    {
        var blob = RandomNumberGenerator.GetBytes(BlobTransfer.BlobBytes);
        var (pull, ms, outstanding) = Transfer(blob, 0, 1);
        Assert.Equal(BlobPullState.Complete, pull.State);
        Assert.Equal(blob, pull.Data);
        Assert.Equal(16, pull.ReceivedCount);
        Assert.Equal(16, pull.RequestsSent);   // no repeats without loss
        Assert.True(outstanding <= BlobTransfer.Window, $"{outstanding} outstanding");
        output.WriteLine($"clean link: {ms} ms");
        Assert.Empty(pull.Poll(1_000_000));   // nothing more to ask
    }

    [Fact]
    public void A_blob_arrives_under_ten_percent_loss_each_way()
    {
        var blob = RandomNumberGenerator.GetBytes(BlobTransfer.BlobBytes);
        var (one, ms, outstanding) = Transfer(blob, 0.10, seed: 7);
        Assert.Equal(BlobPullState.Complete, one.State);
        Assert.Equal(blob, one.Data);
        Assert.True(outstanding <= BlobTransfer.Window);
        output.WriteLine($"seed 7: {ms} ms, {one.RequestsSent} requests");
        // Over many links: nearly all complete without a retry, all with the caller's retry.
        int alone = 0, withRetry = 0;
        double slowest = 0;
        for (int seed = 100; seed < 300; seed++)
        {
            if (Transfer(blob, 0.10, seed).Pull.State == BlobPullState.Complete) alone++;
            var (p, t, _) = Transfer(blob, 0.10, seed, retry: true);
            if (p.State == BlobPullState.Complete && p.Data!.AsSpan().SequenceEqual(blob)) withRetry++;
            slowest = Math.Max(slowest, t);
        }
        output.WriteLine($"10 % loss, 200 links: {alone} complete without a retry, {withRetry} with; slowest {slowest} ms");
        Assert.True(alone >= 190, $"{alone} of 200");   // the tail: the last chunks have no other arrivals to reset their tries (BlobTransfer.MaxTries)
        Assert.Equal(200, withRetry);
    }

    [Fact]
    public void A_tampered_chunk_fails_verification_and_a_retry_starts_over()
    {
        var blob = RandomNumberGenerator.GetBytes(BlobTransfer.BlobBytes);
        var (pull, _, _) = Transfer(blob, 0, 1, tamper: 5);
        Assert.Equal(BlobPullState.Corrupt, pull.State);
        Assert.Null(pull.Data);
        Assert.Equal(0, pull.ReceivedCount);   // every chunk dropped
        Assert.Equal(BlobChunkResult.Ignored, pull.Accept(BlobTransfer.Chunk(pull.Hash, blob, 0)));
        // Retried against an honest peer it completes.
        var (again, _, _) = Transfer(blob, 0, 2, retry: true, pull: pull);
        Assert.Equal(BlobPullState.Complete, again.State);
        Assert.Equal(blob, again.Data);
    }

    [Fact]
    public void A_silent_peer_times_out_and_a_retry_keeps_the_chunks_held()
    {
        var blob = RandomNumberGenerator.GetBytes(BlobTransfer.BlobBytes);
        var hash = EmblemHash.Of(blob);
        var pull = new BlobPull(hash);
        Assert.Equal(new[] { 0, 1, 2, 3 }, pull.Poll(0));                       // the window
        Assert.Empty(pull.Poll(100));                                   // nothing new while they are outstanding
        Assert.Equal(BlobChunkResult.Stored, pull.Accept(BlobTransfer.Chunk(hash, blob, 2)));
        Assert.Equal(new[] { 4 }, pull.Poll(110));                              // one slot freed
        Assert.Equal(new[] { 0, 1, 3 }, pull.Poll(250));                        // repeats after 250 ms
        Assert.Equal(new[] { 4 }, pull.Poll(360));
        Assert.Equal(new[] { 0, 1, 3 }, pull.Poll(500));                        // third tries
        Assert.Equal(new[] { 4 }, pull.Poll(610));
        Assert.Empty(pull.Poll(749));
        Assert.Empty(pull.Poll(750));                                   // a fourth would be due: give up
        Assert.Equal(BlobPullState.TimedOut, pull.State);
        Assert.Equal(1, pull.ReceivedCount);
        pull.Retry();
        Assert.Equal(BlobPullState.Pulling, pull.State);
        Assert.Equal(new[] { 0, 1, 3, 4 }, pull.Poll(1000));                    // chunk 2 is kept
        for (int i = 0; i < 16; i++) if (i != 2) pull.Accept(BlobTransfer.Chunk(hash, blob, i));
        Assert.Equal(BlobPullState.Complete, pull.State);
        Assert.Equal(blob, pull.Data);
    }

    [Fact]
    public void Progress_counts_the_tries_afresh_and_odd_chunks_are_sorted_out()
    {
        var blob = RandomNumberGenerator.GetBytes(BlobTransfer.BlobBytes);
        var hash = EmblemHash.Of(blob);
        var pull = new BlobPull(hash);
        pull.Poll(0);
        pull.Poll(250);
        pull.Poll(500);                                                 // chunks 0..3 at their third try
        Assert.Equal(BlobChunkResult.Stored, pull.Accept(BlobTransfer.Chunk(hash, blob, 3)));   // progress
        Assert.NotEmpty(pull.Poll(750));                                // 0..2 may try again
        Assert.Equal(BlobPullState.Pulling, pull.State);
        Assert.Equal(BlobChunkResult.Duplicate, pull.Accept(BlobTransfer.Chunk(hash, blob, 3)));
        Assert.Equal(BlobChunkResult.OtherBlob, pull.Accept(BlobTransfer.Chunk(SomeHash, blob, 1)));
        Assert.Equal(BlobChunkResult.Malformed, pull.Accept(new BlobChunk(hash, 1, 8, new byte[BlobTransfer.ChunkBytes])));
        Assert.Equal(BlobChunkResult.Malformed, pull.Accept(new BlobChunk(hash, 1, 16, new byte[10])));
        Assert.Equal(1, pull.ReceivedCount);
        Assert.Throws<ArgumentException>(() => new BlobPull(EmblemHash.None));
        Assert.Throws<ArgumentException>(() => BlobTransfer.Chunk(hash, blob.AsSpan(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => BlobTransfer.Chunk(hash, blob, 16));
    }
}
