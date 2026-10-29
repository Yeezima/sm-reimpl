using System.Numerics;
using SmSim;

namespace SmNet;

/// <summary>A pickup in the world as the pickup message carries it (protocol 26): id 1..65535, type (index into <see cref="PickupSpec.Types"/>, 0..15), place (1/64 m).</summary>
public readonly record struct PickupView(int Id, int Type, Vector3 Position);

/// <summary>
/// A pickup that left the world (protocol 26): <paramref name="Sequence"/> (0..65535, wrapping) tells repeats apart; collected by
/// <paramref name="Collector"/> (a player id, 1..65535) or expired (collector 0).
/// </summary>
public readonly record struct PickupEventView(int Sequence, int Id, int Type, PickupEnd End, int Collector, Vector3 Position);

/// <summary>
/// Server to client (protocol 26, fidelity gap 1), outside the snapshots (they have no room left): every pickup in the world
/// (<paramref name="Live"/>, at most <see cref="Protocol.MaxPickupRecords"/>; null: not in this message, the client keeps the list it has) and
/// the collections and expiries of about the last second (<paramref name="Events"/>, at most <see cref="Protocol.MaxPickupEvents"/>, oldest
/// first). The server sends the list whenever it changed and about once a second, the events every <c>GameServer.PickupInterval</c> ticks while
/// remembered, so a lost message costs little: the list is the whole state (a client shows exactly the newest one, minus the pickups that
/// events since then removed) and each event repeats in several messages (the client drops repeats by sequence). On the wire a live count of
/// <see cref="Protocol.NoPickupList"/> means no list. 7 + 12 per pickup + 16 per event bytes, at most 1031.
/// </summary>
public sealed record PickupUpdate(uint ServerTick, PickupView[]? Live, PickupEventView[] Events) : Message;

public static partial class Protocol
{
    /// <summary>Most pickups one message carries (= <see cref="PickupSpec.MaxLiveLimit"/>).</summary>
    public const int MaxPickupRecords = PickupSpec.MaxLiveLimit;
    /// <summary>The live count byte of a message without a live list (events only).</summary>
    public const byte NoPickupList = 255;
    /// <summary>Most events one message carries.</summary>
    public const int MaxPickupEvents = 16;
    /// <summary>Bytes of a pickup record: id u16, type u8, position 3 × i24.</summary>
    public const int PickupRecordBytes = 12;
    /// <summary>Bytes of an event record: sequence u16, id u16, type | end &lt;&lt; 4 u8, collector u16, position 3 × i24.</summary>
    public const int PickupEventBytes = 16;
    /// <summary>The largest pickup message (bytes): kind, tick, two counts and full lists.</summary>
    public const int MaxPickupMessageBytes = 1 + 4 + 1 + MaxPickupRecords * PickupRecordBytes + 1 + MaxPickupEvents * PickupEventBytes;

    /// <summary>A pickup's place after a trip through the wire (1/64 m, as projectiles).</summary>
    public static PickupView Quantize(PickupView p) => p with { Position = Quantize(p.Position) };
    /// <summary>An event's place after a trip through the wire.</summary>
    public static PickupEventView Quantize(PickupEventView e) => e with { Position = Quantize(e.Position) };

    static void WritePickups(Writer w, PickupUpdate p)
    {
        foreach (var q in p.Live ?? [])
            if (q.Id is < 1 or > ushort.MaxValue || q.Type is < 0 or >= PickupSpec.MaxTypes)
                throw new ArgumentException($"A pickup has an id 1..65535 and a type 0..{PickupSpec.MaxTypes - 1}.");
        foreach (var e in p.Events)
            if (e.Id is < 1 or > ushort.MaxValue || e.Type is < 0 or >= PickupSpec.MaxTypes || e.Sequence is < 0 or > ushort.MaxValue
                || (e.End == PickupEnd.Collected ? e.Collector is < 1 or > ushort.MaxValue : e.End != PickupEnd.Expired || e.Collector != 0))
                throw new ArgumentException("A pickup event has an id 1..65535, a type, a sequence 0..65535 and is collected by a player 1..65535 or expired (collector 0).");
        w.U8((byte)Kind.Pickups); w.U32(p.ServerTick);
        int live = p.Live is null ? 0 : Math.Min(p.Live.Length, MaxPickupRecords);
        w.U8(p.Live is null ? NoPickupList : (byte)live);
        for (int k = 0; k < live; k++)
        {
            var q = p.Live![k];
            w.U16((ushort)q.Id); w.U8((byte)q.Type);
            w.I24(Fixed(q.Position.X)); w.I24(Fixed(q.Position.Y)); w.I24(Fixed(q.Position.Z));
        }
        // The newest events when there are too many (older ones went out in earlier messages).
        int skip = Math.Max(0, p.Events.Length - MaxPickupEvents);
        w.U8((byte)(p.Events.Length - skip));
        for (int k = skip; k < p.Events.Length; k++)
        {
            var e = p.Events[k];
            w.U16((ushort)e.Sequence); w.U16((ushort)e.Id); w.U8((byte)(e.Type | ((int)e.End << 4))); w.U16((ushort)e.Collector);
            w.I24(Fixed(e.Position.X)); w.I24(Fixed(e.Position.Y)); w.I24(Fixed(e.Position.Z));
        }
    }

    static PickupUpdate? ReadPickups(ref Reader r)
    {
        uint tick = r.U32();
        int n = r.U8();
        bool list = n != NoPickupList;
        if (!list) n = 0;
        else if (n > MaxPickupRecords) return null;
        var live = new PickupView[n];
        for (int k = 0; k < n; k++)
        {
            int id = r.U16(), type = r.U8();
            var at = new Vector3(r.I24(), r.I24(), r.I24()) / PositionScale;
            if (id == 0 || type >= PickupSpec.MaxTypes) return null;
            for (int j = 0; j < k; j++) if (live[j].Id == id) return null;   // each pickup once
            live[k] = new PickupView(id, type, at);
        }
        int m = r.U8();
        if (m > MaxPickupEvents) return null;
        var events = new PickupEventView[m];
        for (int k = 0; k < m; k++)
        {
            int sequence = r.U16(), id = r.U16(), packed = r.U8(), collector = r.U16();
            var at = new Vector3(r.I24(), r.I24(), r.I24()) / PositionScale;
            int type = packed & 15, end = packed >> 4;
            if (id == 0 || end is not ((int)PickupEnd.Collected or (int)PickupEnd.Expired)) return null;
            if ((end == (int)PickupEnd.Collected) != (collector != 0)) return null;   // a collection names its collector, an expiry none
            for (int j = 0; j < k; j++) if (events[j].Sequence == sequence) return null;   // each event once
            events[k] = new PickupEventView(sequence, id, type, (PickupEnd)end, collector, at);
        }
        return new PickupUpdate(tick, list ? live : null, events);
    }
}
