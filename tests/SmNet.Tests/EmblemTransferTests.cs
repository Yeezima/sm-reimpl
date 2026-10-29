using System.Security.Cryptography;
using SmServer;
using SmSim;
using Xunit.Abstractions;

namespace SmNet.Tests;

/// <summary>
/// Custom emblems over the network (T3b, protocol 22): uploaded by the client that shows it, verified, cached and served to the others over a
/// 150 ms / 10 % loss link; the fallback badge until then; a tampered upload refused and blacklisted; chunks only from the connection being
/// pulled; serving only verified, shown emblems within the per-peer budget; the per-peer new-emblem limit and the upload cap; custom emblems
/// off. Synthetic data only.
/// </summary>
public class EmblemTransferTests(ITestOutputHelper output)
{
    static readonly SimConfig Config = CustomFixture.Config;

    /// <summary>A look with the custom emblem on the right pad (fallback badge 2).</summary>
    static AppearanceCode WithEmblem(EmblemHash hash) => CustomFixture.Look(1).WithDecals(5, AppearanceCode.DecalEmblem, 2).WithEmblem(hash);

    static (byte[] Blob, EmblemHash Hash) NewBlob()
    {
        var blob = RandomNumberGenerator.GetBytes(EmblemHash.BlobBytes);
        return (blob, EmblemHash.Of(blob));
    }

    [Fact]
    public void An_emblem_uploads_verifies_and_reaches_another_client_over_a_lossy_link()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(CustomFixture.Lossy, seed: 21, clients: 0, Config, CustomFixture.Options);
        var a = h.AddClient(options: new ClientOptions
        {
            RequestedClass = CustomFixture.SmTac, SpaceMarineAppearance = WithEmblem(hash), EmblemSource = x => x == hash ? blob : null,
        }, nonce: 1);
        var b = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(h, () => a.Status == ClientStatus.Connected, TimeSpan.FromSeconds(3)));
        // Before the upload completes, the others see the fallback badge.
        var early = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.False(early.Code.UsesEmblem);
        Assert.Equal(2, early.Code.RightDecal);
        Assert.True(CustomFixture.RunUntil(h, () => b.Emblem(hash) is not null, TimeSpan.FromSeconds(20)));
        Assert.True(h.Server.HasEmblem(hash));
        var late = h.Server.AppearanceOf(a.PlayerId)!.Value;
        Assert.NotEqual(early.Version, late.Version);   // the arrival is a new version: clients ask again
        Assert.Equal(WithEmblem(hash), late.Code);
        Assert.Equal(blob, b.Emblem(hash));
        Assert.Equal(hash, b.AppearanceOf(a.PlayerId)!.Code.Emblem);
        Assert.Contains(hash, b.TakeEmblemArrivals());
        Assert.Equal(0, h.Server.EmblemUploads);
        output.WriteLine($"emblem reached the other client at {h.Clock.Now.TotalSeconds:F2} s; packets {h.Net.PacketsSent}, dropped {h.Net.PacketsDropped}");
    }

    [Fact]
    public void A_tampered_upload_is_refused_blacklisted_and_the_fallback_stays()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var log = new List<string>();
        h.Server.Log += log.Add;
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 3, CustomFixture.SmTac, sm: WithEmblem(hash)));
        var bad = (byte[])blob.Clone(); bad[5000] ^= 0x10;   // chunk 4 tampered
        for (int t = 0; t < 2000; t++)
        {
            h.Step();
            foreach (var r in CustomFixture.Drain(raw).OfType<BlobRequest>()) raw.Send(Protocol.Write(BlobTransfer.Chunk(r.Hash, bad, r.Chunk)));
        }
        Assert.False(h.Server.HasEmblem(hash));
        Assert.Contains(log, l => l.Contains("failed verification"));
        Assert.False(h.Server.AppearanceOf(id)!.Value.Code.UsesEmblem);
        // Named again in a Setup: refused at once, no new upload.
        raw.Send(Protocol.Write(new Setup(session, 1, LoadoutChoice.Default, WithEmblem(hash), AppearanceCode.Default)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        var ack = Assert.Single(CustomFixture.Drain(raw).OfType<SetupAck>());
        Assert.True((ack.Flags & SetupFlags.EmblemRejected) != 0);
        Assert.Equal(0, h.Server.EmblemUploads);
        // The uploader is penalised, not the hash: an honest client naming the same emblem uploads it.
        var honest = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac, ChaosAppearance = WithEmblem(hash), EmblemSource = x => x == hash ? blob : null }, nonce: 77);
        Assert.True(CustomFixture.RunUntil(h, () => h.Server.HasEmblem(hash), TimeSpan.FromSeconds(5)));
        Assert.True(h.Server.AppearanceOf(honest.PlayerId)!.Value.Code.UsesEmblem);
    }

    [Fact]
    public void Chunks_count_only_from_the_connection_being_pulled()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var silent = h.Net.Connect();
        CustomFixture.Join(h, silent, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: WithEmblem(hash)));   // names the emblem, never answers
        var other = h.Net.Connect();
        CustomFixture.Join(h, other, CustomFixture.HelloFor(h, 2, CustomFixture.CsmTac));
        Assert.Equal(1, h.Server.EmblemUploads);
        for (int i = 0; i < BlobTransfer.MaxChunks; i++) other.Send(Protocol.Write(BlobTransfer.Chunk(hash, blob, i)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(50));
        Assert.False(h.Server.HasEmblem(hash));
        // The silent uploader times out: retries (the chunks held kept), then the server gives up.
        CustomFixture.RunUntil(h, () => h.Server.EmblemUploads == 0, TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Server.EmblemUploads);
        Assert.False(h.Server.HasEmblem(hash));
    }

    [Fact]
    public void Only_verified_shown_emblems_are_served_within_the_chunk_budget()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var a = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmTac, SpaceMarineAppearance = WithEmblem(hash), EmblemSource = x => x == hash ? blob : null }, nonce: 1);
        Assert.True(CustomFixture.RunUntil(h, () => h.Server.HasEmblem(hash), TimeSpan.FromSeconds(3)));
        var raw = h.Net.Connect();
        var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 9, CustomFixture.CsmTac));
        CustomFixture.Drain(raw);
        for (int i = 0; i < 100; i++) raw.Send(Protocol.Write(new BlobRequest(session, hash, i % 16)));
        raw.Send(Protocol.Write(new BlobRequest(session, new EmblemHash(1, 2), 0)));   // not held: nothing
        raw.Send(Protocol.Write(new BlobRequest(session + 1, hash, 0)));               // no session: nothing
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        var chunks = CustomFixture.Drain(raw).OfType<BlobChunk>().ToList();
        Assert.Equal(64, chunks.Count);   // the burst of the 64-chunks-per-second budget
        Assert.All(chunks, c => Assert.Equal(hash, c.Hash));
        Assert.All(chunks, c => Assert.Equal(blob.AsSpan(c.Index * 1024, 1024).ToArray(), c.Data));
        // A blob nobody shows any more is not served.
        h.Server.World.Remove(a.PlayerId);
        a.Leave();
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(1100));
        CustomFixture.Drain(raw);
        raw.Send(Protocol.Write(new BlobRequest(session, hash, 0)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        Assert.Empty(CustomFixture.Drain(raw).OfType<BlobChunk>());
    }

    /// <summary>
    /// Steps the harness, every raw endpoint answering the server's chunk requests from its blob (by hash; <paramref name="serve"/> false for an endpoint
    /// that stays silent) and sending an empty input batch every 500 ms to stay connected; returns the (time, endpoint, chunk) of every request seen.
    /// </summary>
    static List<(double Ms, int Raw, BlobRequest Request)> Serve(Harness h, IReadOnlyList<(IClientTransport Raw, uint Session, Dictionary<EmblemHash, byte[]> Blobs)> raws,
        TimeSpan limit, Func<bool>? done = null, Func<int, bool>? serve = null, Func<int, BlobRequest, bool>? answer = null)
    {
        var seen = new List<(double, int, BlobRequest)>();
        int steps = (int)limit.TotalMilliseconds;
        for (int t = 0; t < steps && !(done?.Invoke() ?? false); t++)
        {
            h.Step();
            for (int i = 0; i < raws.Count; i++)
            {
                var (raw, session, blobs) = raws[i];
                if (t % 500 == 0) raw.Send(Protocol.Write(new InputBatch([], session)));
                foreach (var r in CustomFixture.Drain(raw).OfType<BlobRequest>())
                {
                    seen.Add((h.Clock.Now.TotalMilliseconds, i, r));
                    if ((serve?.Invoke(i) ?? true) && (answer?.Invoke(i, r) ?? true) && blobs.TryGetValue(r.Hash, out var blob))
                        raw.Send(Protocol.Write(BlobTransfer.Chunk(r.Hash, blob, r.Chunk)));
                }
            }
        }
        return seen;
    }

    [Fact]
    public void Uploads_are_one_per_peer_one_new_hash_per_interval_and_deferred_ones_start_later()
    {
        var (blob1, hash1) = NewBlob();
        var (blob2, hash2) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raw = h.Net.Connect();
        var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: WithEmblem(hash1)));
        var blobs = new Dictionary<EmblemHash, byte[]> { [hash1] = blob1, [hash2] = blob2 };
        var me = new[] { (raw, session, blobs) };
        Assert.Equal(1, h.Server.EmblemUploads);
        double t0 = h.Clock.Now.TotalMilliseconds;
        // A second hash while the first is still being pulled: queued (one upload per peer), not refused.
        raw.Send(Protocol.Write(new Setup(session, 1, LoadoutChoice.Default, WithEmblem(hash2), AppearanceCode.Default)));
        var ack = ReadAck(h, raw);
        var early = Serve(h, me, TimeSpan.FromMilliseconds(300), serve: _ => false);
        Assert.Equal(SetupFlags.Pending | SetupFlags.EmblemUploading, ack.Flags & ~SetupFlags.Defaulted);
        Assert.Equal(1, h.Server.EmblemUploads);
        Assert.DoesNotContain(early, e => e.Request.Hash == hash2);
        // Served: the first completes; the second waits for the one-new-hash-per-10-s limit, then the retry starts it.
        var seen = Serve(h, me, TimeSpan.FromSeconds(15), () => h.Server.HasEmblem(hash1) && h.Server.HasEmblem(hash2));
        Assert.True(h.Server.HasEmblem(hash1) && h.Server.HasEmblem(hash2));
        double second = seen.First(e => e.Request.Hash == hash2).Ms;
        Assert.True(second - t0 >= 10_000, $"the second hash was asked for {second - t0} ms after the first");
    }

    /// <summary>The SetupAck a raw endpoint received during the last steps (stepping a little more when none has arrived yet).</summary>
    static SetupAck ReadAck(Harness h, IClientTransport raw)
    {
        for (int i = 0; i < 50; i++)
        {
            if (CustomFixture.Drain(raw).OfType<SetupAck>().FirstOrDefault() is { } ack) return ack;
            h.Step();
        }
        throw new Xunit.Sdk.XunitException("no SetupAck");
    }

    [Fact]
    public void Joins_beyond_the_upload_cap_are_retried_until_every_emblem_is_held()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raws = new List<(IClientTransport, uint, Dictionary<EmblemHash, byte[]>)>();
        var hashes = new List<EmblemHash>();
        for (int i = 0; i < 6; i++)
        {
            var (blob, hash) = NewBlob();
            hashes.Add(hash);
            var raw = h.Net.Connect();
            var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, (uint)(10 + i), i % 2 == 0 ? (byte)CustomFixture.SmTac : (byte)CustomFixture.CsmTac,
                sm: WithEmblem(hash), chaos: WithEmblem(hash)));
            raws.Add((raw, session, new Dictionary<EmblemHash, byte[]> { [hash] = blob }));
        }
        Assert.True(h.Server.EmblemUploads <= CustomFixture.Options.MaxEmblemUploads);
        int most = 0;
        Serve(h, raws, TimeSpan.FromSeconds(20), () => { most = Math.Max(most, h.Server.EmblemUploads); return hashes.All(h.Server.HasEmblem); });
        Assert.All(hashes, x => Assert.True(h.Server.HasEmblem(x)));
        Assert.True(most <= 4, $"{most} uploads at once");
        output.WriteLine($"6 emblems held at {h.Clock.Now.TotalSeconds:F1} s");
    }

    [Fact]
    public void A_trickle_upload_ends_at_the_time_limit_and_the_peer_backs_off()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raw = h.Net.Connect();
        var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: WithEmblem(hash)));
        double start = h.Clock.Now.TotalMilliseconds;
        double lastAnswer = double.MinValue;
        bool Trickle(int _, BlobRequest r)   // one chunk every 600 ms: the pull never times out, but never finishes in 5 s either
        {
            double now = h.Clock.Now.TotalMilliseconds;
            if (now - lastAnswer < 600) return false;
            lastAnswer = now;
            return true;
        }
        var me = new[] { (raw, session, new Dictionary<EmblemHash, byte[]> { [hash] = blob }) };
        Serve(h, me, TimeSpan.FromSeconds(6), () => h.Server.EmblemUploads == 0, answer: Trickle);
        double dropped = h.Clock.Now.TotalMilliseconds;
        Assert.Equal(0, h.Server.EmblemUploads);      // dropped by the 5 s limit (or its retries running out first)
        Assert.True(dropped - start <= 5_100, $"dropped after {dropped - start} ms");
        Assert.False(h.Server.HasEmblem(hash));
        var after = Serve(h, me, TimeSpan.FromSeconds(8), serve: _ => false);
        Assert.Empty(after);                           // the 10 s back-off
        var retried = Serve(h, me, TimeSpan.FromSeconds(10), () => h.Server.HasEmblem(hash));
        Assert.NotEmpty(retried);
        Assert.True(retried[0].Ms - dropped >= 9_900, $"retried {retried[0].Ms - dropped} ms after the drop");
        Assert.True(h.Server.HasEmblem(hash));         // served properly the second time
    }

    [Fact]
    public void A_rejoin_from_the_same_address_drops_the_upload()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var raw = h.Net.Connect();
        CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: WithEmblem(hash)));
        Assert.Equal(1, h.Server.EmblemUploads);
        // A new client on the same address (another nonce) with the default look.
        var (_, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 2, CustomFixture.SmTac));
        var seen = Serve(h, new[] { (raw, session, new Dictionary<EmblemHash, byte[]>()) }, TimeSpan.FromSeconds(1));
        Assert.Equal(0, h.Server.EmblemUploads);
        Assert.DoesNotContain(seen, e => e.Request.Hash == hash);
        for (int i = 0; i < BlobTransfer.MaxChunks; i++) raw.Send(Protocol.Write(BlobTransfer.Chunk(hash, blob, i)));   // unasked: ignored
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        Assert.False(h.Server.HasEmblem(hash));
    }

    [Fact]
    public void Another_client_sees_more_than_four_new_emblems_at_once()
    {
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options);
        var hashes = new List<EmblemHash>();
        for (int i = 0; i < 6; i++)
        {
            var (blob, hash) = NewBlob();
            hashes.Add(hash);
            // Both races' looks carry the emblem: side balance moves some of them to Chaos.
            h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.SmTac, SpaceMarineAppearance = WithEmblem(hash), ChaosAppearance = WithEmblem(hash),
                EmblemSource = x => x == hash ? blob : null }, nonce: (uint)(1 + i));
        }
        var watcher = h.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac }, nonce: 50);
        Assert.True(CustomFixture.RunUntil(h, () => hashes.All(x => watcher.Emblem(x) is not null), TimeSpan.FromSeconds(40)));
        output.WriteLine($"6 emblems on the watcher at {h.Clock.Now.TotalSeconds:F1} s (4 pulls at once)");
    }

    [Fact]
    public void With_custom_emblems_off_nothing_is_pulled_or_served()
    {
        var (blob, hash) = NewBlob();
        var h = new Harness(LinkConditions.Perfect, seed: 1, clients: 0, Config, CustomFixture.Options with { CustomEmblems = false });
        var raw = h.Net.Connect();
        var (id, session) = CustomFixture.Join(h, raw, CustomFixture.HelloFor(h, 1, CustomFixture.SmTac, sm: WithEmblem(hash)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromSeconds(1));
        Assert.Empty(CustomFixture.Drain(raw).OfType<BlobRequest>());
        Assert.Equal(0, h.Server.EmblemUploads);
        var shown = h.Server.AppearanceOf(id)!.Value.Code;
        Assert.Equal((false, (byte)2), (shown.UsesEmblem, shown.RightDecal));   // the fallback badge
        raw.Send(Protocol.Write(new Setup(session, 1, LoadoutChoice.Default, WithEmblem(hash), AppearanceCode.Default)));
        CustomFixture.RunUntil(h, () => false, TimeSpan.FromMilliseconds(20));
        Assert.True((Assert.Single(CustomFixture.Drain(raw).OfType<SetupAck>()).Flags & SetupFlags.EmblemRejected) != 0);
        // A client with custom emblems off never pulls one either.
        var server = new Harness(LinkConditions.Perfect, seed: 2, clients: 0, Config, CustomFixture.Options);
        var a = server.AddClient(options: new ClientOptions { SpaceMarineAppearance = WithEmblem(hash), EmblemSource = x => x == hash ? blob : null }, nonce: 1);
        var b = server.AddClient(options: new ClientOptions { RequestedClass = CustomFixture.CsmTac, CustomEmblems = false }, nonce: 2);
        Assert.True(CustomFixture.RunUntil(server, () => b.AppearanceOf(a.PlayerId)?.Code.RightDecal == 2, TimeSpan.FromSeconds(5)));
        CustomFixture.RunUntil(server, () => false, TimeSpan.FromSeconds(2));
        Assert.Null(b.Emblem(hash));
        Assert.False(b.AppearanceOf(a.PlayerId)!.Code.UsesEmblem);
    }
}
