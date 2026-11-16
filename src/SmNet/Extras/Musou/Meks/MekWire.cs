using System.Buffers.Binary;
using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>
/// The Scrap Meks' contraptions on the wire (expansion S4; Musou Mode only; envelope tag <see cref="Tag"/> = 17, after the Librarian's 16; no protocol bump, other profiles never hear it). One section with
/// every contraption of the battle, to every client: <c>[u8 count]</c> then per contraption <see cref="RecordBytes"/> bytes <c>[u8 id][u8 kind][u8 state][u8 flags: 1 a drop in flight][u16 Mek]
/// [i16 x][i16 y][i16 z]</c> (decimetres, no box needed) <c>[u8 health in 255ths of its maximum][u8 build progress in 255ths][u8 aim in 256ths of a turn][u8 Boyz queued or in flight]</c>, at most
/// <see cref="MaxContraptions"/> (a battle has 15: five Meks, three each).
/// </summary>
public static class MekWire
{
    public const byte Tag = 17;
    public const int RecordBytes = 16, MaxContraptions = 64;
    public const int MaxPayloadBytes = 1 + MaxContraptions * RecordBytes;
    const float Step = 0.1f;

    public static byte[] Write(IReadOnlyList<MusouContraption> contraptions)
    {
        int count = Math.Min(contraptions.Count, MaxContraptions);
        var bytes = new byte[1 + count * RecordBytes];
        bytes[0] = (byte)count;
        for (int k = 0; k < count; k++)
        {
            var c = contraptions[k];
            var s = bytes.AsSpan(1 + k * RecordBytes, RecordBytes);
            s[0] = c.Id; s[1] = (byte)c.Kind; s[2] = (byte)c.State; s[3] = (byte)(c.DropInFlight ? 1 : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], c.Mek);
            BinaryPrimitives.WriteInt16LittleEndian(s[6..], Quantise(c.Position.X));
            BinaryPrimitives.WriteInt16LittleEndian(s[8..], Quantise(c.Position.Y));
            BinaryPrimitives.WriteInt16LittleEndian(s[10..], Quantise(c.Position.Z));
            s[12] = Fraction(c.Health / MathF.Max(1f, c.MaxHealth));
            s[13] = Fraction(c.Progress);
            float turns = float.IsFinite(c.Aim) ? c.Aim / MathF.Tau : 0f;
            s[14] = (byte)((int)MathF.Round((turns - MathF.Floor(turns)) * 256f) & 255);
            s[15] = c.Queued;
        }
        return bytes;
    }

    /// <summary>The contraptions of a payload; null for a malformed one (wrong length, too many, an id of 0, an unknown kind, state or flag).</summary>
    public static IReadOnlyList<MusouContraption>? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 || payload[0] > MaxContraptions || payload.Length != 1 + payload[0] * RecordBytes) return null;
        var list = new List<MusouContraption>(payload[0]);
        for (int k = 0; k < payload[0]; k++)
        {
            var s = payload.Slice(1 + k * RecordBytes, RecordBytes);
            var kind = (MusouContraptionKind)s[1];
            var state = (MusouContraptionState)s[2];
            if (s[0] == 0 || !Enum.IsDefined(kind) || !Enum.IsDefined(state) || s[3] > 1) return null;
            var position = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s[6..]), BinaryPrimitives.ReadInt16LittleEndian(s[8..]), BinaryPrimitives.ReadInt16LittleEndian(s[10..])) * Step;
            list.Add(new MusouContraption(s[0], kind, state, BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), position, s[12] / 255f * MusouMekRules.Health(kind), s[13] / 255f,
                s[14] / 256f * MathF.Tau, s[3] == 1, s[15]));
        }
        return list;
    }

    static short Quantise(float metres) => float.IsFinite(metres) ? (short)Math.Clamp((int)MathF.Round(metres / Step), short.MinValue, short.MaxValue) : (short)0;

    static byte Fraction(float f) => float.IsFinite(f) ? (byte)Math.Clamp((int)MathF.Ceiling(f * 255f - 1e-3f), 0, 255) : (byte)0;
}
