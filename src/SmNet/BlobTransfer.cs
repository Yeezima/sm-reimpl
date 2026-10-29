using SmSim;

namespace SmNet;

/// <summary>
/// Content-addressed blob transfer over unreliable datagrams (protocol 22; docs/customizer-architecture.md section 4): a blob is a
/// custom emblem, exactly <see cref="BlobBytes"/> bytes (128 x 128 BC3), named by its <see cref="EmblemHash"/>. The receiver pulls
/// it chunk by chunk (<see cref="BlobRequest"/>, answered by a <see cref="BlobChunk"/>; <see cref="BlobPull"/>) and verifies the
/// whole blob's hash before use. Shared by client and server, engine-free, with no I/O of its own: the caller sends the
/// messages these types produce and feeds back the ones it receives.
/// </summary>
public static class BlobTransfer
{
    /// <summary>Bytes of a blob (an emblem).</summary>
    public const int BlobBytes = EmblemHash.BlobBytes;
    /// <summary>Bytes of one chunk (a <see cref="BlobChunk"/> message is then 1045 bytes, under the 1200-byte packet).</summary>
    public const int ChunkBytes = 1024;
    /// <summary>Chunks of a blob (the count every <see cref="BlobChunk"/> of a blob carries; a 16-bit bitmap tracks them).</summary>
    public const int MaxChunks = BlobBytes / ChunkBytes;
    /// <summary>Most requests a pull keeps outstanding (ours).</summary>
    public const int Window = 4;
    /// <summary>Milliseconds before an unanswered chunk request is sent again (ours).</summary>
    public const long RetryMs = 250;
    /// <summary>
    /// Unanswered requests for one chunk after which a pull gives up (<see cref="BlobPullState.TimedOut"/>). Any chunk of the blob that
    /// arrives counts the outstanding chunks' tries afresh (their request in flight is then the first), so a pull fails only when its
    /// peer stops answering for about <see cref="MaxTries"/> x <see cref="RetryMs"/> ms, not because of scattered loss (interpretation
    /// of "3 tries per chunk").
    /// </summary>
    public const int MaxTries = 3;

    /// <summary>Whether <paramref name="blob"/> is a whole blob whose hash is <paramref name="hash"/>.</summary>
    public static bool Verify(EmblemHash hash, ReadOnlySpan<byte> blob) => !hash.IsNone && blob.Length == BlobBytes && EmblemHash.Of(blob) == hash;

    /// <summary>The chunk message for chunk <paramref name="index"/> of <paramref name="blob"/> (exactly <see cref="BlobBytes"/> bytes; the serving side).</summary>
    public static BlobChunk Chunk(EmblemHash hash, ReadOnlySpan<byte> blob, int index)
    {
        if (blob.Length != BlobBytes) throw new ArgumentException($"A blob is {BlobBytes} bytes, not {blob.Length}.", nameof(blob));
        if (index is < 0 or >= MaxChunks) throw new ArgumentOutOfRangeException(nameof(index));
        return new BlobChunk(hash, index, MaxChunks, blob.Slice(index * ChunkBytes, ChunkBytes).ToArray());
    }
}

/// <summary>Where a <see cref="BlobPull"/> is.</summary>
public enum BlobPullState : byte
{
    /// <summary>Chunks still missing; <see cref="BlobPull.Poll"/> says what to request.</summary>
    Pulling = 0,
    /// <summary>Every chunk arrived and the hash matched: <see cref="BlobPull.Data"/> is the blob.</summary>
    Complete = 1,
    /// <summary>A chunk went unanswered <see cref="BlobTransfer.MaxTries"/> times without progress (the chunks received are kept; <see cref="BlobPull.Retry"/>).</summary>
    TimedOut = 2,
    /// <summary>The assembled blob did not match its hash (a tampered or wrong chunk): every chunk was dropped (<see cref="BlobPull.Retry"/> starts over).</summary>
    Corrupt = 3,
}

/// <summary>What <see cref="BlobPull.Accept"/> did with a chunk.</summary>
public enum BlobChunkResult : byte
{
    /// <summary>Stored; chunks are still missing.</summary>
    Stored = 0,
    /// <summary>The last missing chunk: the blob verified (<see cref="BlobPullState.Complete"/>).</summary>
    Completed = 1,
    /// <summary>The last missing chunk, but the blob failed verification (<see cref="BlobPullState.Corrupt"/>).</summary>
    Corrupt = 2,
    /// <summary>A chunk already held (the first copy stays).</summary>
    Duplicate = 3,
    /// <summary>Another blob's chunk.</summary>
    OtherBlob = 4,
    /// <summary>Not a chunk of a blob of this size: a count other than <see cref="BlobTransfer.MaxChunks"/>, an index out of range or a length other than <see cref="BlobTransfer.ChunkBytes"/>.</summary>
    Malformed = 5,
    /// <summary>The pull is not pulling (complete, timed out or corrupt).</summary>
    Ignored = 6,
}

/// <summary>
/// The receiving side of one blob (<see cref="BlobTransfer"/>): a 16-bit bitmap of the chunks held, at most
/// <see cref="BlobTransfer.Window"/> requests outstanding, a request repeated after <see cref="BlobTransfer.RetryMs"/>, and
/// <see cref="BlobTransfer.MaxTries"/> unanswered tries per chunk. Times are the caller's clock in milliseconds. Not thread-safe.
/// </summary>
public sealed class BlobPull
{
    readonly byte[] _buffer = new byte[BlobTransfer.BlobBytes];
    readonly long[] _sentAt = new long[BlobTransfer.MaxChunks];
    readonly int[] _tries = new int[BlobTransfer.MaxChunks];
    ushort _requested;

    public BlobPull(EmblemHash hash)
    {
        if (hash.IsNone) throw new ArgumentException("A blob pull needs a hash.", nameof(hash));
        Hash = hash;
    }

    public EmblemHash Hash { get; }
    public BlobPullState State { get; private set; } = BlobPullState.Pulling;
    /// <summary>Bit i: chunk i is held.</summary>
    public ushort Received { get; private set; }
    /// <summary>Chunks held.</summary>
    public int ReceivedCount => System.Numerics.BitOperations.PopCount(Received);
    /// <summary>Requests sent in all (first sends and repeats).</summary>
    public int RequestsSent { get; private set; }
    /// <summary>A copy of the verified blob once <see cref="BlobPullState.Complete"/>, else null (the pull's own buffer is never handed out).</summary>
    public byte[]? Data => State == BlobPullState.Complete ? (byte[])_buffer.Clone() : null;

    const ushort All = (1 << BlobTransfer.MaxChunks) - 1;

    /// <summary>
    /// The chunks to request now (lowest index first; the caller sends a <see cref="BlobRequest"/> for each): unanswered requests older
    /// than <see cref="BlobTransfer.RetryMs"/> again, then new chunks up to <see cref="BlobTransfer.Window"/> outstanding. A chunk due
    /// again after <see cref="BlobTransfer.MaxTries"/> tries ends the pull (<see cref="BlobPullState.TimedOut"/>; nothing to send).
    /// </summary>
    public IReadOnlyList<int> Poll(long nowMs)
    {
        if (State != BlobPullState.Pulling) return [];
        var send = new List<int>();
        int outstanding = 0;
        for (int i = 0; i < BlobTransfer.MaxChunks; i++)
        {
            if (!Outstanding(i)) continue;
            outstanding++;
            if (nowMs - _sentAt[i] < BlobTransfer.RetryMs) continue;
            if (_tries[i] >= BlobTransfer.MaxTries) { State = BlobPullState.TimedOut; _requested = 0; return []; }
            send.Add(i);
        }
        for (int i = 0; i < BlobTransfer.MaxChunks && outstanding < BlobTransfer.Window; i++)
        {
            if (Held(i) || (_requested & (1 << i)) != 0) continue;
            send.Add(i);
            outstanding++;
        }
        send.Sort();
        foreach (int i in send)
        {
            _requested |= (ushort)(1 << i);
            _sentAt[i] = nowMs;
            _tries[i]++;
            RequestsSent++;
        }
        return send;
    }

    /// <summary>
    /// The requests <see cref="Poll"/> asks for as messages (<paramref name="session"/>: the client's session, 0 from the server).
    /// </summary>
    public IReadOnlyList<BlobRequest> Requests(long nowMs, uint session) => Poll(nowMs).Select(i => new BlobRequest(session, Hash, i)).ToArray();   // polled once, eagerly

    /// <summary>Takes a received chunk (see <see cref="BlobChunkResult"/>); the last missing one verifies the blob.</summary>
    public BlobChunkResult Accept(BlobChunk chunk)
    {
        if (chunk.Hash != Hash) return BlobChunkResult.OtherBlob;
        if (State != BlobPullState.Pulling) return BlobChunkResult.Ignored;
        if (chunk.Count != BlobTransfer.MaxChunks || chunk.Index is < 0 or >= BlobTransfer.MaxChunks || chunk.Data is not { Length: BlobTransfer.ChunkBytes })
            return BlobChunkResult.Malformed;
        if (Held(chunk.Index)) return BlobChunkResult.Duplicate;
        chunk.Data.CopyTo(_buffer, chunk.Index * BlobTransfer.ChunkBytes);
        Received |= (ushort)(1 << chunk.Index);
        // Progress: the peer answers, so the requests still in flight count as first tries again.
        for (int i = 0; i < BlobTransfer.MaxChunks; i++) if (Outstanding(i)) _tries[i] = 1;
        if (Received != All) return BlobChunkResult.Stored;
        _requested = 0;
        if (BlobTransfer.Verify(Hash, _buffer)) { State = BlobPullState.Complete; return BlobChunkResult.Completed; }
        Array.Clear(_buffer);
        Received = 0;
        State = BlobPullState.Corrupt;
        return BlobChunkResult.Corrupt;
    }

    /// <summary>
    /// Starts pulling again after <see cref="BlobPullState.TimedOut"/> (the chunks held are kept) or <see cref="BlobPullState.Corrupt"/>
    /// (from nothing); tries start afresh. Nothing for a pull that is pulling or complete. When to retry is the caller's choice.
    /// </summary>
    public void Retry()
    {
        if (State is not (BlobPullState.TimedOut or BlobPullState.Corrupt)) return;
        State = BlobPullState.Pulling;
        _requested = 0;
        Array.Clear(_tries);
    }

    bool Held(int i) => (Received & (1 << i)) != 0;
    bool Outstanding(int i) => (_requested & (1 << i)) != 0 && !Held(i);
}
