namespace SmNet;

/// <summary>
/// Server to client (added content E3, ported by the loyalist barks without a protocol bump: extras wire 2; docs/added-content.md): the generic envelope that carries what added content sends, outside
/// the snapshots. Each section is a feature's own payload under a one-byte tag (gore: 1); the core never reads a payload. On the wire
/// <c>[16][u32 tick][u8 n]{[u8 tag][u16 len][payload]}</c>, at most <see cref="Protocol.MaxExtrasMessageBytes"/> bytes in all. A receiver passes on
/// every section (a feature skips the tags it does not know: the length is enough to step over them). A faithful server never sends this kind
/// (<c>GameServer.SendExtras</c> refuses without added content), and a client running no added content drops it.
/// </summary>
public sealed record ExtrasUpdate(uint ServerTick, (byte Tag, byte[] Payload)[] Sections) : Message
{
    /// <summary>The payloads of every section with <paramref name="tag"/>, in message order (a feature's view of the message).</summary>
    public IEnumerable<byte[]> PayloadsOf(byte tag) => Sections.Where(s => s.Tag == tag).Select(s => s.Payload);
}

/// <summary>One section of an <see cref="ExtrasUpdate"/> as the client queues it (<c>GameClient.TakeExtras</c>): the message's server tick, the tag, the payload.</summary>
public readonly record struct ExtrasSection(uint ServerTick, byte Tag, byte[] Payload);

public static partial class Protocol
{
    /// <summary>
    /// The largest extras message, header included (1200 bytes: the snapshot budget, one datagram without fragmentation; ours). A larger one is
    /// refused when written (ArgumentException) and when read (null).
    /// </summary>
    public const int MaxExtrasMessageBytes = SnapshotBudget;
    /// <summary>Bytes of the envelope's header: kind, server tick, section count.</summary>
    public const int ExtrasHeaderBytes = 1 + 4 + 1;
    /// <summary>Bytes in front of each section's payload: tag, length.</summary>
    public const int ExtrasSectionHeaderBytes = 1 + 2;
    /// <summary>Most sections in one message (the count is one byte).</summary>
    public const int MaxExtrasSections = byte.MaxValue;
    /// <summary>The largest payload of one section (one section alone in a message of <see cref="MaxExtrasMessageBytes"/>).</summary>
    public const int MaxExtrasPayloadBytes = MaxExtrasMessageBytes - ExtrasHeaderBytes - ExtrasSectionHeaderBytes;
    /// <summary>The reserved tag 0: never written (ArgumentException), a message carrying it is malformed (null). Features use 1..255.</summary>
    public const byte ReservedExtrasTag = 0;

    /// <summary>
    /// True when this build knows the message kind <paramref name="kind"/> (a packet's first byte). A demo recorded by a build with more kinds
    /// is played back without the ones this build does not know (<see cref="DemoTransport"/>).
    /// </summary>
    public static bool IsKnownKind(byte kind) => Enum.IsDefined((Kind)kind);

    /// <summary>Bytes an extras message takes on the wire (whether or not it is valid).</summary>
    public static int EnvelopeBytes(ExtrasUpdate u) => ExtrasHeaderBytes + (u.Sections ?? []).Sum(s => ExtrasSectionHeaderBytes + (s.Payload?.Length ?? 0));

    static void WriteExtras(Writer w, ExtrasUpdate u)
    {
        var sections = u.Sections ?? throw new ArgumentException("An extras message has a section list.");
        if (sections.Length > MaxExtrasSections) throw new ArgumentException($"At most {MaxExtrasSections} sections in an extras message.");
        foreach (var (tag, payload) in sections)
        {
            if (tag == ReservedExtrasTag) throw new ArgumentException($"Extras tag {ReservedExtrasTag} is reserved (features use 1..255).");
            if (payload is null || payload.Length > MaxExtrasPayloadBytes) throw new ArgumentException($"An extras section carries 0..{MaxExtrasPayloadBytes} bytes.");
        }
        int bytes = EnvelopeBytes(u);
        if (bytes > MaxExtrasMessageBytes) throw new ArgumentException($"An extras message of {bytes} bytes is over {MaxExtrasMessageBytes}: split it.");
        w.U8((byte)Kind.Extras); w.U32(u.ServerTick); w.U8((byte)sections.Length);
        foreach (var (tag, payload) in sections) { w.U8(tag); w.U16((ushort)payload.Length); w.Bytes(payload); }
    }

    /// <summary>Reads the envelope after the kind byte; a reserved tag is malformed, and a length past the packet throws (Read returns null).</summary>
    static ExtrasUpdate? ReadExtras(ref Reader r)
    {
        uint tick = r.U32();
        int n = r.U8();
        var sections = new (byte Tag, byte[] Payload)[n];
        for (int k = 0; k < n; k++)
        {
            byte tag = r.U8();
            int length = r.U16();
            if (tag == ReservedExtrasTag || length > MaxExtrasPayloadBytes) return null;
            sections[k] = (tag, r.Bytes(length).ToArray());   // a length beyond the packet throws: malformed
        }
        return new ExtrasUpdate(tick, sections);
    }
}
