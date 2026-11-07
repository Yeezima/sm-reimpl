using System.Buffers.Binary;
using System.Numerics;
using SmSim.Extras.Musou;

namespace SmNet.Extras.Musou;

/// <summary>One Librarian as the clients see him (tag 16): his stress and cooldowns for the HUD, his casts for the effects, and the links of this tick's lightning.</summary>
public readonly record struct LibrarianView(ushort Player, float Stress, bool Locked, bool Casting, float PullCooldown, float ShockwaveCooldown, byte PullCount, byte ShockwaveCount,
    IReadOnlyList<LibrarianLink> Links);

/// <summary>
/// The Librarian on the wire (Musou Mode only; envelope tag <see cref="Tag"/> = 16 with the war's tags 7 to 15; no protocol bump, other profiles never hear it). One section for every living Librarian, to every
/// client: <c>[u8 count]</c> then per Librarian <c>[u16 player][u16 stress in tenths][u8 flags: 1 locked, 2 casting][u8 pull cooldown in tenths][u8 shockwave cooldown in tenths][u8 pulls cast][u8 shockwaves cast]
/// [u8 links]</c> and, with at least one link, the chain: <c>[i16 x][i16 y][i16 z]</c> twice (the first link's from and to, decimetres), then per further link <c>[i8 dx][i8 dy][i8 dz]</c>, its end less the
/// one before in decimetres (MusouSim 14, the Librarian's second pass: 3 bytes a jump instead of 12, so all <see cref="LibrarianRules.MaxDrawnLinks"/> = 40 fit: 4 x (10 + 12 + 39 x 3) = 556 bytes, one section).
/// The chain is a line (each jump starts where the last one ended, <c>LibrarianPsyker.Lightning</c>), and a jump is at most <see cref="LibrarianRules.JumpRadius"/> across and a body's chest up or down, well
/// inside the 12.7 m a signed byte reaches; a link that does not continue the line or reaches further ends the drawing there (it is never sent wrong). The deltas are taken between the quantised points, so the
/// rounding never adds up along the chain. A client plays a Pull or a Shockwave when its count changes (a lost copy is repeated by the next section), and draws the links while they keep coming.
/// </summary>
public static class LibrarianWire
{
    public const byte Tag = 16;
    /// <summary>A Librarian's fixed bytes, the chain's first link (two full points) and each further jump (one delta).</summary>
    public const int RecordBytes = 10, FirstLinkBytes = 12, JumpBytes = 3;
    /// <summary>The most Librarians in one section (a Musou Mode party has four).</summary>
    public const int MaxLibrarians = 4;
    /// <summary>The largest payload: four Librarians with every link drawn (557 bytes with the count; one section takes up to <c>Protocol.MaxExtrasPayloadBytes</c>).</summary>
    public const int MaxPayloadBytes = 1 + MaxLibrarians * (RecordBytes + FirstLinkBytes + (LibrarianRules.MaxDrawnLinks - 1) * JumpBytes);
    const float Step = 0.1f;

    /// <summary>The bytes of a chain of <paramref name="links"/> links.</summary>
    public static int ChainBytes(int links) => links <= 0 ? 0 : FirstLinkBytes + (links - 1) * JumpBytes;

    /// <summary>How many of <paramref name="links"/> the wire carries: up to the cap, and up to the first link that does not continue the line or jumps further than a byte reaches.</summary>
    public static int Drawable(IReadOnlyList<LibrarianLink> links)
    {
        int n = Math.Min(links.Count, LibrarianRules.MaxDrawnLinks);
        if (n == 0) return 0;
        var last = Quantise(links[0].To);
        for (int k = 1; k < n; k++)
        {
            if (Quantise(links[k].From) != last) return k;
            var to = Quantise(links[k].To);
            if (!Fits(to.X - last.X) || !Fits(to.Y - last.Y) || !Fits(to.Z - last.Z)) return k;
            last = to;
        }
        return n;
    }

    public static byte[] Write(IReadOnlyList<LibrarianView> views)
    {
        int count = Math.Min(views.Count, MaxLibrarians);
        Span<int> drawn = stackalloc int[MaxLibrarians];
        int size = 1;
        for (int k = 0; k < count; k++) { drawn[k] = Drawable(views[k].Links); size += RecordBytes + ChainBytes(drawn[k]); }
        var bytes = new byte[size];
        bytes[0] = (byte)count;
        int at = 1;
        for (int k = 0; k < count; k++)
        {
            var v = views[k];
            int links = drawn[k];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at), v.Player);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 2), (ushort)Math.Clamp((int)MathF.Round(v.Stress * 10f), 0, 1000));
            bytes[at + 4] = (byte)((v.Locked ? 1 : 0) | (v.Casting ? 2 : 0));
            bytes[at + 5] = (byte)Math.Clamp((int)MathF.Ceiling(v.PullCooldown * 10f), 0, 255);
            bytes[at + 6] = (byte)Math.Clamp((int)MathF.Ceiling(v.ShockwaveCooldown * 10f), 0, 255);
            bytes[at + 7] = v.PullCount;
            bytes[at + 8] = v.ShockwaveCount;
            bytes[at + 9] = (byte)links;
            at += RecordBytes;
            if (links == 0) continue;
            WritePoint(bytes.AsSpan(at), Quantise(v.Links[0].From));
            var last = Quantise(v.Links[0].To);
            WritePoint(bytes.AsSpan(at + 6), last);
            at += FirstLinkBytes;
            for (int l = 1; l < links; l++)
            {
                var to = Quantise(v.Links[l].To);
                bytes[at] = (byte)(sbyte)(to.X - last.X);
                bytes[at + 1] = (byte)(sbyte)(to.Y - last.Y);
                bytes[at + 2] = (byte)(sbyte)(to.Z - last.Z);
                last = to;
                at += JumpBytes;
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
            if (stress > 1000 || flags > 3 || links > LibrarianRules.MaxDrawnLinks || s.Length < at + RecordBytes + ChainBytes(links)) return null;
            var drawn = new List<LibrarianLink>(links);
            int p = at + RecordBytes;
            if (links > 0)
            {
                var from = ReadPoint(s[p..]);
                var last = ReadPoint(s[(p + 6)..]);
                drawn.Add(new LibrarianLink(Metres(from), Metres(last)));
                p += FirstLinkBytes;
                for (int l = 1; l < links; l++, p += JumpBytes)
                {
                    var to = new Q(last.X + (sbyte)s[p], last.Y + (sbyte)s[p + 1], last.Z + (sbyte)s[p + 2]);
                    drawn.Add(new LibrarianLink(Metres(last), Metres(to)));
                    last = to;
                }
            }
            views.Add(new LibrarianView(BinaryPrimitives.ReadUInt16LittleEndian(s[at..]), stress / 10f, (flags & 1) != 0, (flags & 2) != 0, s[at + 5] / 10f, s[at + 6] / 10f, s[at + 7], s[at + 8], drawn));
            at = p;
        }
        return at == s.Length ? views : null;
    }

    /// <summary>A point in whole decimetres (the wire's grid; int so a delta never overflows while it is checked).</summary>
    readonly record struct Q(int X, int Y, int Z);

    static bool Fits(int delta) => delta is >= sbyte.MinValue and <= sbyte.MaxValue;

    static Q Quantise(Vector3 p) => new(Quantise(p.X), Quantise(p.Y), Quantise(p.Z));

    static int Quantise(float metres) => float.IsFinite(metres) ? Math.Clamp((int)MathF.Round(metres / Step), short.MinValue, short.MaxValue) : 0;

    static Vector3 Metres(Q q) => new(q.X * Step, q.Y * Step, q.Z * Step);

    static void WritePoint(Span<byte> to, Q p)
    {
        BinaryPrimitives.WriteInt16LittleEndian(to, (short)p.X);
        BinaryPrimitives.WriteInt16LittleEndian(to[2..], (short)p.Y);
        BinaryPrimitives.WriteInt16LittleEndian(to[4..], (short)p.Z);
    }

    static Q ReadPoint(ReadOnlySpan<byte> s) => new(BinaryPrimitives.ReadInt16LittleEndian(s), BinaryPrimitives.ReadInt16LittleEndian(s[2..]), BinaryPrimitives.ReadInt16LittleEndian(s[4..]));
}
