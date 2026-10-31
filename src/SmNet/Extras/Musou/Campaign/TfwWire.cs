using System.Buffers.Binary;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>
/// The Three-Front War on the wire (design section H; no protocol bump, Musou's config hash keeps other profiles out). The server's sections are envelope tags (message kind 16):
/// 7 the war (about 62 bytes, 1 Hz and on a change), 8 the objectives (at most 180 bytes, every 10 ticks), 9 the Guardsmen (10-byte records), 10 the Guard events (15 bytes);
/// their layouts come with TF6. A client's commands are message kind 17 under <see cref="CommandTag"/>, with the payload <c>[u16 sequence][u8 command][u8 argument]</c>,
/// resent every 250 ms until tag 7 echoes the sequence. A faithful or extended server never sends 7 to 10 and drops 17.
/// </summary>
public static class TfwWire
{
    /// <summary>The envelope tags (gore 1, loyalist barks 2, Musou 3 to 6).</summary>
    public const byte WarTag = 7, ObjectivesTag = 8, GuardsTag = 9, GuardEventsTag = 10;
    /// <summary>The tag of the war's commands in message kind 17 (another namespace than the envelope's: client to server).</summary>
    public const byte CommandTag = 7;
    /// <summary>Bytes of a command's payload.</summary>
    public const int CommandBytes = 4;

    /// <summary>Whether an envelope tag is the war's (7 to 10).</summary>
    public static bool Owns(byte tag) => tag is WarTag or ObjectivesTag or GuardsTag or GuardEventsTag;

    public static byte[] WriteCommand(TfwCommandRecord c)
    {
        var bytes = new byte[CommandBytes];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, c.Sequence);
        bytes[2] = (byte)c.Command;
        bytes[3] = c.Argument;
        return bytes;
    }

    /// <summary>The command of a payload; null for a malformed one (wrong length, an unknown command).</summary>
    public static TfwCommandRecord? ReadCommand(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != CommandBytes || !Enum.IsDefined((TfwCommand)payload[2])) return null;
        return new TfwCommandRecord(BinaryPrimitives.ReadUInt16LittleEndian(payload), (TfwCommand)payload[2], payload[3]);
    }
}
