using System.Buffers.Binary;
using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>One Librarian as the clients see him (tag 16): his stress and cooldowns for the HUD, his casts for the effects, and the links of this tick's lightning.</summary>
public readonly record struct LibrarianView(ushort Player, float Stress, bool Locked, bool Casting, float PullCooldown, float ShockwaveCooldown, byte PullCount, byte ShockwaveCount,
    IReadOnlyList<LibrarianLink> Links);

/// <summary>
/// The Librarian on the wire (Musou Mode only; envelope tag <see cref="Tag"/> = 16 with the war's tags 7 to 15, sent only under <c>World.Tfw</c>; no protocol bump, other profiles never hear it). One section for every
/// living Librarian, to every client: <c>[u8 count]</c> then per Librarian <c>[u16 player][u16 stress in tenths][u8 flags: 1 locked, 2 casting][u8 pull cooldown in tenths][u8 shockwave cooldown in tenths]
/// [u8 pulls cast][u8 shockwaves cast][u8 links]</c> then per link <c>[i16 x][i16 y][i16 z]</c> twice (from, to) in decimetres. A client plays a Pull or a Shockwave when its count changes (a lost copy is
/// repeated by the next section), and draws the links while they keep coming.
/// </summary>
public static class LibrarianWire
{
    public const byte Tag = 16;
    public const int RecordBytes = 10, LinkBytes = 12;
    /// <summary>The most Librarians in one section (a Musou Mode party has four; 4 x (10 + 16 x 12) stays inside one section).</summary>
    public const int MaxLibrarians = 4;
    const float Step = 0.1f;

    public static byte[] Write(IReadOnlyList<LibrarianView> views)
    {
        int count = Math.Min(views.Count, MaxLibrarians);
        int size = 1;
        for (int k = 0; k < count; k++) size += RecordBytes + LinkBytes * Math.Min(views[k].Links.Count, LibrarianRules.MaxDrawnLinks);
        var bytes = new byte[size];
        bytes[0] = (byte)count;
        int at = 1;
        for (int k = 0; k < count; k++)
        {
            var v = views[k];
            int links = Math.Min(v.Links.Count, LibrarianRules.MaxDrawnLinks);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), v.Player);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), (ushort)Math.Clamp((int)MathF.Round(v.Stress * 10f), 0, 1000));
            bytes[at + 4] = (byte)((v.Locked ? 1 : 0) | (v.Casting ? 2 : 0));
            bytes[at + 5] = (byte)Math.Clamp((int)MathF.Ceiling(v.PullCooldown * 10f), 0, 255);
            bytes[at + 6] = (byte)Math.Clamp((int)MathF.Ceiling(v.ShockwaveCooldown * 10f), 0, 255);
            bytes[at + 7] = v.PullCount;
            bytes[at + 8] = v.ShockwaveCount;
            bytes[at + 9] = (byte)links;
            at += RecordBytes;
            for (int l = 0; l < links; l++)
            {
                WritePoint(bytes.AsSpan(at), v.Links[l].From);
                WritePoint(bytes.AsSpan(at + 6), v.Links[l].To);
                at += LinkBytes;
            }
        }
        return bytes;
    }

    /// <summary>The views of a payload; null for a malformed one (wrong length, too many Librarians or links, a stress above 100).</summary>
    public static IReadOnlyList<LibrarianView>? Read(ReadOnlySpan<byte> s)
    {
        if (s.Length < 1 || s[0] > MaxLibrarians) return null;
        var views = new List<LibrarianView>(s[0]);
        int at = 1;
        for (int k = 0; k < s[0]; k++)
        {
            if (s.Length < at + RecordBytes) return null;
            ushort stress = BinaryPrimitives.ReadUInt16LittleEndian(s[(at + 2)..]);
            byte flags = s[at + 4], links = s[at + 9];
            if (stress > 1000 || flags > 3 || links > LibrarianRules.MaxDrawnLinks || s.Length < at + RecordBytes + links * LinkBytes) return null;
            var drawn = new List<LibrarianLink>(links);
            int p = at + RecordBytes;
            for (int l = 0; l < links; l++, p += LinkBytes) drawn.Add(new LibrarianLink(ReadPoint(s[p..]), ReadPoint(s[(p + 6)..])));
            views.Add(new LibrarianView(BinaryPrimitives.ReadUInt16LittleEndian(s[at..]), stress / 10f, (flags & 1) != 0, (flags & 2) != 0, s[at + 5] / 10f, s[at + 6] / 10f, s[at + 7], s[at + 8], drawn));
            at = p;
        }
        return at == s.Length ? views : null;
    }

    static void WritePoint(Span<byte> to, Vector3 p)
    {
        BinaryPrimitives.WriteInt16LittleEndian(to, Quantise(p.X));
        BinaryPrimitives.WriteInt16LittleEndian(to[2..], Quantise(p.Y));
        BinaryPrimitives.WriteInt16LittleEndian(to[4..], Quantise(p.Z));
    }

    static Vector3 ReadPoint(ReadOnlySpan<byte> s) => new(BinaryPrimitives.ReadInt16LittleEndian(s) * Step, BinaryPrimitives.ReadInt16LittleEndian(s[2..]) * Step, BinaryPrimitives.ReadInt16LittleEndian(s[4..]) * Step);

    static short Quantise(float metres) => float.IsFinite(metres) ? (short)Math.Clamp((int)MathF.Round(metres / Step), short.MinValue, short.MaxValue) : (short)0;
}
