using SmSim.Extras.Warfront;

namespace SmNet.Extras.Warfront;

/// <summary>
/// The Guardsmen's vox section (Guardsmen G2; ours): envelope tag <see cref="WarfrontWire.VoxTag"/> (52), sent only by a Warfront lane battle whose line made a line, the tick it was made and then once a second for
/// <see cref="RepeatSeconds"/> s, to every identified client. Layout <c>[u16 sequence][u8 kind][u8 from][u8 to]</c>, 5 bytes, little endian. <see cref="TryDecode"/> never throws: a payload that is not 5 bytes,
/// a kind outside 1..5, a sequence of 0, or a point above 3 that is not 255 is refused. The client shows each sequence once.
/// </summary>
public static class WarfrontVoxWire
{
    public const int Length = 5;
    public const int RepeatSeconds = 4;

    /// <summary>A line as it travels (the tick stays on the server).</summary>
    public readonly record struct Line(ushort Sequence, WarfrontVoxKind Kind, byte From, byte To);

    public static byte[] Encode(WarfrontVoxLine line) => Encode(new Line(line.Sequence, line.Kind, line.From, line.To));

    public static byte[] Encode(Line line) => [(byte)line.Sequence, (byte)(line.Sequence >> 8), (byte)line.Kind, line.From, line.To];

    public static bool TryDecode(ReadOnlySpan<byte> payload, out Line line)
    {
        line = default;
        if (payload.Length != Length) return false;
        ushort seq = (ushort)(payload[0] | payload[1] << 8);
        byte kind = payload[2], from = payload[3], to = payload[4];
        if (seq == 0 || kind is < 1 or > 5 || !Point(from) || !Point(to)) return false;
        line = new Line(seq, (WarfrontVoxKind)kind, from, to);
        return true;
    }

    static bool Point(byte p) => p <= WarfrontVox.LandingZone || p == WarfrontVox.None;
}
