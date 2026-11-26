using System.Buffers.Binary;

namespace SmFormats.Dds;

/// <summary>
/// A decoded DDS image's first mip level as RGBA8 (R, G, B, A per pixel, rows top to bottom), and a writer for uncompressed RGBA DDS files with a full mip
/// chain. Written from the public DDS layout (a 4-byte magic and a 124-byte header; <c>dwHeight</c> at 12, <c>dwWidth</c> at 16, the pixel format at 76:
/// flags at 80, four-character code at 84, bit count at 88, channel masks at 92..107). Read: BC1 (DXT1), BC2 (DXT3), BC3 (DXT5) and uncompressed 32-bit
/// formats with any channel masks. Used for the masks the client builds for armour pieces without a paint mask (armour-pieces-1125); nothing is written to disk.
/// </summary>
public sealed record DdsImage(int Width, int Height, byte[] Rgba)
{
    public const int HeaderBytes = 128;

    /// <summary>Decodes the first mip level, or null when the data is not a DDS of a supported format.</summary>
    public static DdsImage? TryDecode(ReadOnlySpan<byte> dds)
    {
        if (dds.Length < HeaderBytes || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ') return null;
        int height = BinaryPrimitives.ReadInt32LittleEndian(dds[12..]), width = BinaryPrimitives.ReadInt32LittleEndian(dds[16..]);
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192) return null;
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(dds[80..]);
        string fourCc = System.Text.Encoding.ASCII.GetString(dds.Slice(84, 4));
        var data = dds[HeaderBytes..];
        if ((flags & 4) != 0)
        {
            int bw = Math.Max(1, (width + 3) / 4), bh = Math.Max(1, (height + 3) / 4);
            int blockBytes = fourCc == "DXT1" ? 8 : fourCc is "DXT3" or "DXT5" ? 16 : 0;
            if (blockBytes == 0 || data.Length < bw * bh * blockBytes) return null;
            var full = new byte[bw * 4 * bh * 4 * 4];
            DecodeBlocks(data, bw, bh, fourCc, full);
            if (bw * 4 == width && bh * 4 == height) return new DdsImage(width, height, full);
            var cropped = new byte[width * height * 4];
            for (int y = 0; y < height; y++) full.AsSpan(y * bw * 16, width * 4).CopyTo(cropped.AsSpan(y * width * 4));
            return new DdsImage(width, height, cropped);
        }
        int bits = BinaryPrimitives.ReadInt32LittleEndian(dds[88..]);
        if (bits != 32 || data.Length < width * height * 4) return null;
        uint rm = BinaryPrimitives.ReadUInt32LittleEndian(dds[92..]), gm = BinaryPrimitives.ReadUInt32LittleEndian(dds[96..]),
             bm = BinaryPrimitives.ReadUInt32LittleEndian(dds[100..]), am = BinaryPrimitives.ReadUInt32LittleEndian(dds[104..]);
        bool hasAlpha = (flags & 1) != 0 && am != 0;
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            uint v = BinaryPrimitives.ReadUInt32LittleEndian(data[(i * 4)..]);
            rgba[i * 4] = Channel(v, rm); rgba[i * 4 + 1] = Channel(v, gm); rgba[i * 4 + 2] = Channel(v, bm);
            rgba[i * 4 + 3] = hasAlpha ? Channel(v, am) : (byte)255;
        }
        return new DdsImage(width, height, rgba);
    }

    static byte Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return (byte)(((v & mask) >> shift) * 255 / max);
    }

    static void DecodeBlocks(ReadOnlySpan<byte> data, int bw, int bh, string fourCc, byte[] rgba)
    {
        int width = bw * 4, blockBytes = fourCc == "DXT1" ? 8 : 16;
        Span<int> c = stackalloc int[16];
        Span<int> a = stackalloc int[8];
        for (int by = 0; by < bh; by++)
        for (int bx = 0; bx < bw; bx++)
        {
            var block = data.Slice((by * bw + bx) * blockBytes, blockBytes);
            var colour = fourCc == "DXT1" ? block : block[8..];
            int c0 = colour[0] | colour[1] << 8, c1 = colour[2] | colour[3] << 8;
            Palette(c0, c1, fourCc == "DXT1" && c0 <= c1, c);
            uint cBits = BinaryPrimitives.ReadUInt32LittleEndian(colour[4..]);
            ulong aBits = 0;
            if (fourCc == "DXT5")
            {
                a[0] = block[0]; a[1] = block[1];
                if (a[0] > a[1]) for (int i = 1; i <= 6; i++) a[1 + i] = ((7 - i) * a[0] + i * a[1]) / 7;
                else { for (int i = 1; i <= 4; i++) a[1 + i] = ((5 - i) * a[0] + i * a[1]) / 5; a[6] = 0; a[7] = 255; }
                for (int i = 0; i < 6; i++) aBits |= (ulong)block[2 + i] << (8 * i);
            }
            for (int p = 0; p < 16; p++)
            {
                int ci = (int)((cBits >> (2 * p)) & 3) * 4;
                int o = ((by * 4 + (p >> 2)) * width + bx * 4 + (p & 3)) * 4;
                rgba[o] = (byte)c[ci]; rgba[o + 1] = (byte)c[ci + 1]; rgba[o + 2] = (byte)c[ci + 2];
                rgba[o + 3] = fourCc switch
                {
                    "DXT5" => (byte)a[(int)((aBits >> (3 * p)) & 7)],
                    "DXT3" => (byte)(((block[p >> 1] >> ((p & 1) * 4)) & 15) * 17),
                    _ => (byte)c[ci + 3],
                };
            }
        }
    }

    static int E5(int v) => v << 3 | v >> 2;
    static int E6(int v) => v << 2 | v >> 4;

    static void Palette(int c0, int c1, bool threeColour, Span<int> p)
    {
        int r0 = E5(c0 >> 11), g0 = E6((c0 >> 5) & 63), b0 = E5(c0 & 31), r1 = E5(c1 >> 11), g1 = E6((c1 >> 5) & 63), b1 = E5(c1 & 31);
        p[0] = r0; p[1] = g0; p[2] = b0; p[3] = 255;
        p[4] = r1; p[5] = g1; p[6] = b1; p[7] = 255;
        if (threeColour)
        {
            p[8] = (r0 + r1) / 2; p[9] = (g0 + g1) / 2; p[10] = (b0 + b1) / 2; p[11] = 255;
            p[12] = p[13] = p[14] = p[15] = 0;
        }
        else
        {
            p[8] = (2 * r0 + r1) / 3; p[9] = (2 * g0 + g1) / 3; p[10] = (2 * b0 + b1) / 3; p[11] = 255;
            p[12] = (r0 + 2 * r1) / 3; p[13] = (g0 + 2 * g1) / 3; p[14] = (b0 + 2 * b1) / 3; p[15] = 255;
        }
    }

    /// <summary>
    /// An uncompressed 32-bit DDS (channel masks R 0xff, G 0xff00, B 0xff0000, A 0xff000000, so the bytes are RGBA in memory) with a full mip chain
    /// (2x2 box filter; an odd side keeps its last row or column out of the average). Deterministic.
    /// </summary>
    public static byte[] EncodeRgba8WithMips(int width, int height, ReadOnlySpan<byte> rgba)
    {
        if (width <= 0 || height <= 0 || rgba.Length != width * height * 4) throw new ArgumentException("RGBA data does not match the size.");
        var levels = new List<(int W, int H, byte[] Px)> { (width, height, rgba.ToArray()) };
        while (levels[^1].W > 1 || levels[^1].H > 1)
        {
            var (w, h, px) = levels[^1];
            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var next = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
            for (int k = 0; k < 4; k++)
            {
                int x0 = Math.Min(w - 1, x * 2), x1 = Math.Min(w - 1, x * 2 + 1), y0 = Math.Min(h - 1, y * 2), y1 = Math.Min(h - 1, y * 2 + 1);
                int sum = px[(y0 * w + x0) * 4 + k] + px[(y0 * w + x1) * 4 + k] + px[(y1 * w + x0) * 4 + k] + px[(y1 * w + x1) * 4 + k];
                next[(y * nw + x) * 4 + k] = (byte)((sum + 2) / 4);
            }
            levels.Add((nw, nh, next));
        }
        int total = HeaderBytes + levels.Sum(l => l.Px.Length);
        var b = new byte[total];
        "DDS "u8.CopyTo(b);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), 0x1 | 0x2 | 0x4 | 0x8 | 0x1000 | 0x20000);   // caps, height, width, pitch, pixel format, mip count
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), height);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(16), width);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(20), width * 4);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), levels.Count);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(76), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(80), 0x41);   // RGB + alpha pixels
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(88), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(92), 0x000000ff);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(96), 0x0000ff00);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(100), 0x00ff0000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(104), 0xff000000);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(108), 0x1000 | 0x8 | 0x400000);   // texture, complex, mipmap
        int at = HeaderBytes;
        foreach (var l in levels) { l.Px.CopyTo(b, at); at += l.Px.Length; }
        return b;
    }
}
