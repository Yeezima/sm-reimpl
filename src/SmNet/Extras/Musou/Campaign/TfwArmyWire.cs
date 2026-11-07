using System.Buffers.Binary;
using System.Numerics;
using SmSim.Extras.Musou.Campaign;

namespace SmNet.Extras.Musou.Campaign;

/// <summary>
/// One army as the clients see it (tag 18): enough to draw its aggregate part as impostors in its slots (<see cref="TfwArmyLayout"/>): side, order, whether it has a leader, its count, its aggregate (<see cref="Far"/>
/// men in slots <c>[SlotBase, SlotBase + Far)</c>, <see cref="Elites"/> of them elites), its anchor and facing, its cohesion and its width.
/// </summary>
public readonly record struct TfwArmyView(byte Id, TfwArmySide Side, TfwArmyOrder Order, bool Led, ushort Count, ushort Far, ushort SlotBase, ushort Elites, Vector3 Anchor, float Yaw, float Cohesion, byte Width)
{
    /// <summary>The aggregate's elite share, as the server's promotions read it.</summary>
    public float EliteShare => Far > 0 ? Math.Clamp(Elites / (float)Far, 0f, 1f) : 0f;
}

/// <summary>
/// The battle flow's armies on the wire (expansion S6; Musou Mode only; envelope tag <see cref="Tag"/> = 18, boxless like tags 11 to 16; 17 is left to S4). One section for every army, to every client, twice a
/// second: <c>[u8 n]</c> then n records of <see cref="RecordBytes"/> bytes <c>[u8 id][u8 side | order &lt;&lt; 1 | led &lt;&lt; 4][u16 count][u16 far][u16 slot base][u16 elites][i16 x][i16 y][i16 z]</c> (decimetres)
/// <c>[u8 yaw in 256ths of a turn][u8 cohesion in 255ths][u8 width]</c>. 40 armies are 761 bytes.
/// </summary>
public static class TfwArmyWire
{
    public const byte Tag = 18;
    public const int RecordBytes = 19;
    public const int MaxArmies = TfwArmyRules.MaxArmies;
    const float Step = 0.1f;

    static short Decimetres(float v) => (short)Math.Clamp((int)MathF.Round(v / Step), short.MinValue, short.MaxValue);

    /// <summary>The views of the battle's armies (the first <see cref="MaxArmies"/>).</summary>
    public static List<TfwArmyView> Views(IReadOnlyList<TfwArmy> armies)
    {
        var list = new List<TfwArmyView>(Math.Min(armies.Count, MaxArmies));
        foreach (var a in armies)
        {
            if (list.Count >= MaxArmies) break;
            list.Add(new TfwArmyView(a.Id, a.Side, a.Order, a.Led, (ushort)Math.Clamp(a.Count, 0, ushort.MaxValue), (ushort)Math.Clamp(a.FarCount, 0, ushort.MaxValue),
                (ushort)Math.Clamp(a.SlotBase, 0, ushort.MaxValue), (ushort)Math.Clamp((int)MathF.Round(a.FarElites), 0, Math.Min(a.FarCount, ushort.MaxValue)), a.Anchor, a.Yaw, a.Cohesion, (byte)Math.Clamp(a.Width, 1, 255)));
        }
        return list;
    }

    public static byte[] Write(IReadOnlyList<TfwArmyView> armies)
    {
        int n = Math.Min(armies.Count, MaxArmies);
        var bytes = new byte[1 + n * RecordBytes];
        bytes[0] = (byte)n;
        for (int i = 0; i < n; i++)
        {
            var a = armies[i];
            var s = bytes.AsSpan(1 + i * RecordBytes, RecordBytes);
            s[0] = a.Id;
            s[1] = (byte)((int)a.Side | (int)a.Order << 1 | (a.Led ? 1 << 4 : 0));
            BinaryPrimitives.WriteUInt16LittleEndian(s[2..], a.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(s[4..], a.Far);
            BinaryPrimitives.WriteUInt16LittleEndian(s[6..], a.SlotBase);
            BinaryPrimitives.WriteUInt16LittleEndian(s[8..], a.Elites);
            BinaryPrimitives.WriteInt16LittleEndian(s[10..], Decimetres(a.Anchor.X));
            BinaryPrimitives.WriteInt16LittleEndian(s[12..], Decimetres(a.Anchor.Y));
            BinaryPrimitives.WriteInt16LittleEndian(s[14..], Decimetres(a.Anchor.Z));
            float turn = a.Yaw / MathF.Tau;
            s[16] = (byte)((int)MathF.Round((turn - MathF.Floor(turn)) * 256f) & 255);
            s[17] = (byte)Math.Clamp((int)MathF.Round(a.Cohesion * 255f), 0, 255);
            s[18] = a.Width;
        }
        return bytes;
    }

    /// <summary>The armies of a payload; null for a malformed one (wrong length, an unknown side or order, an aggregate over the count, a repeated id, no width).</summary>
    public static TfwArmyView[]? Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1) return null;
        int n = payload[0];
        if (n > MaxArmies || payload.Length != 1 + n * RecordBytes) return null;
        var list = new TfwArmyView[n];
        Span<bool> seen = stackalloc bool[256];
        for (int i = 0; i < n; i++)
        {
            var s = payload.Slice(1 + i * RecordBytes, RecordBytes);
            var side = (TfwArmySide)(s[1] & 1);
            var order = (TfwArmyOrder)((s[1] >> 1) & 7);
            if (!Enum.IsDefined(order) || (s[1] & 0xE0) != 0 || seen[s[0]] || s[18] == 0) return null;
            seen[s[0]] = true;
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(s[2..]), far = BinaryPrimitives.ReadUInt16LittleEndian(s[4..]), elites = BinaryPrimitives.ReadUInt16LittleEndian(s[8..]);
            if (far > count || elites > far) return null;
            var at = new Vector3(BinaryPrimitives.ReadInt16LittleEndian(s[10..]), BinaryPrimitives.ReadInt16LittleEndian(s[12..]), BinaryPrimitives.ReadInt16LittleEndian(s[14..])) * Step;
            list[i] = new TfwArmyView(s[0], side, order, (s[1] & 16) != 0, count, far, BinaryPrimitives.ReadUInt16LittleEndian(s[6..]), elites, at, s[16] / 256f * MathF.Tau, s[17] / 255f, s[18]);
        }
        return list;
    }
}
